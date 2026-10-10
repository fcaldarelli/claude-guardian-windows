using System;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

namespace ClaudeGuardian
{
    /// <summary>
    /// Notification area icons with the sessions menu.
    ///
    /// Like the "⚙ 3  ✋ 1  ☾ 2" label on macOS, there are three icons side by side: working,
    /// waiting, idle, each with its own color and count. With no sessions only the app icon
    /// remains. Built with TrayIcons=single (see src\Directory.Build.props), there is a single icon
    /// instead, showing the most urgent state.
    ///
    /// Click (left or right) on an icon: the menu, rebuilt every time it opens.
    /// </summary>
    public sealed class TrayApp : ApplicationContext
    {
        /// <summary>States shown by the three icons, in macOS order.</summary>
        private static readonly SessionState[] IconStates = { SessionState.Working, SessionState.Waiting, SessionState.Idle };

        /// <summary>One icon instead of three: chosen at build time with the TrayIcons property.</summary>
#if SINGLE_TRAY_ICON
        private static readonly bool SingleIcon = true;
#else
        private static readonly bool SingleIcon = false;
#endif

        private readonly NotifyIcon[] _notifyIcons = new NotifyIcon[SingleIcon ? 1 : IconStates.Length];
        private readonly Icon[] _statusIcons = new Icon[IconStates.Length];
        private readonly ContextMenuStrip _menu;
        private readonly ActivityMonitor _monitor;
        private readonly Icon _appIcon;
        private string _iconKey;

        public TrayApp()
        {
            // The menu must be created first: it is the first control, and it installs the UI
            // synchronization context used by the monitor.
            _menu = new ContextMenuStrip
            {
                ShowItemToolTips = true,
                Renderer = new MenuRenderer(),
            };
            _menu.Opening += (s, e) => { e.Cancel = false; BuildMenu(); };

            // The embedded icon has every size: take the exact notification area one,
            // instead of shrinking (and blurring) the 32 px one associated with the executable.
            using (var stream = typeof(TrayApp).Assembly.GetManifestResourceStream("AppIcon.ico"))
                _appIcon = stream != null ? new Icon(stream, SystemInformation.SmallIconSize) : SystemIcons.Application;

            // All three icons are always created, in the same order: Windows remembers for each one
            // whether you made it always visible, and recognizes it by creation order.
            for (var i = 0; i < _notifyIcons.Length; i++)
            {
                var icon = new NotifyIcon
                {
                    Icon = _appIcon,
                    Text = "Claude Guardian",
                    ContextMenuStrip = _menu,
                };
                icon.MouseUp += (s, e) =>
                {
                    // By default the menu opens only on right click; NotifyIcon has an internal method
                    // that opens it in the correct position, on left click too.
                    if (e.Button == MouseButtons.Left)
                        typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(icon, null);
                };
                _notifyIcons[i] = icon;
            }

            _monitor = new ActivityMonitor();
            _monitor.Changed += (s, e) => UpdateIcon();
            UpdateIcon();
            BuildMenu();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (var icon in _notifyIcons)
                {
                    icon.Visible = false;
                    icon.Dispose();
                }
                _monitor.Dispose();
                _menu.Dispose();
                foreach (var icon in _statusIcons) TrayIconRenderer.Destroy(icon);
            }
            base.Dispose(disposing);
        }

        // MARK: - Icon

        private string Summary =>
            "Working: " + _monitor.WorkingCount + " · Waiting for you: " + _monitor.WaitingCount + " · Idle: " + _monitor.IdleCount;

        private int CountOf(SessionState state)
        {
            switch (state)
            {
                case SessionState.Working: return _monitor.WorkingCount;
                case SessionState.Waiting: return _monitor.WaitingCount;
                default: return _monitor.IdleCount;
            }
        }

        private void UpdateIcon()
        {
            var hasSessions = _monitor.Sessions.Count > 0;
            var size = SystemInformation.SmallIconSize.Width;
            var key = !hasSessions ? "none"
                : _monitor.WorkingCount + "/" + _monitor.WaitingCount + "/" + _monitor.IdleCount + "@" + size;
            if (key == _iconKey) return;
            _iconKey = key;

            var previous = (Icon[])_statusIcons.Clone();
            Array.Clear(_statusIcons, 0, _statusIcons.Length);

            if (!hasSessions)
            {
                // With no open sessions, only the app icon.
                _notifyIcons[0].Icon = _appIcon;
            }
            else if (SingleIcon)
            {
                _statusIcons[0] = TrayIconRenderer.Status(_monitor.WorkingCount, _monitor.WaitingCount, _monitor.IdleCount, size);
                _notifyIcons[0].Icon = _statusIcons[0];
            }
            else
            {
                for (var i = 0; i < IconStates.Length; i++)
                {
                    _statusIcons[i] = TrayIconRenderer.Count(IconStates[i], CountOf(IconStates[i]), size);
                    _notifyIcons[i].Icon = _statusIcons[i];
                }
            }

            var multi = hasSessions && !SingleIcon;
            for (var i = 0; i < _notifyIcons.Length; i++)
            {
                // The notification area tooltip is limited to 63 characters.
                var tip = "Claude Guardian\n" + (hasSessions ? Summary : "No active Claude Code sessions");
                _notifyIcons[i].Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
                _notifyIcons[i].Visible = i == 0 || multi;
            }

            foreach (var icon in previous) TrayIconRenderer.Destroy(icon);
        }

        // MARK: - Menu

        /// <summary>
        /// Indent of sessions under the project: em spaces (U+2003), which unlike regular spaces
        /// are not collapsed. The icons stay in the left column.
        /// </summary>
        private const string SessionIndent = "  ";

        private void BuildMenu()
        {
            _monitor.Reload();
            UpdateIcon();

            var imageSize = _menu.LogicalToDeviceUnits(16);
            _menu.ImageScalingSize = new Size(imageSize, imageSize);

            _menu.SuspendLayout();
            var items = _menu.Items;
            var previous = new ToolStripItem[items.Count];
            items.CopyTo(previous, 0);
            items.Clear();
            foreach (var item in previous) item.Dispose();

            items.Add(Header(Summary, bold: false));

            if (_monitor.Sessions.Count == 0)
            {
                items.Add(new ToolStripSeparator());
                items.Add(Header("No active Claude Code sessions", bold: false));
            }
            else
            {
                // App → project → sessions. App headers are non-clickable text;
                // projects and sessions are clickable.
                foreach (var app in SessionGrouping.Group(_monitor.Sessions))
                {
                    items.Add(new ToolStripSeparator());
                    items.Add(Header(app.Name, bold: true));

                    foreach (var project in app.Projects)
                    {
                        // Sessions of the same project and app share the window: use the most urgent one.
                        var first = project.Sessions[0];
                        var projectItem = new ToolStripMenuItem(project.Name.Replace("&", "&&"), TrayIconRenderer.Folder(imageSize), (s, e) => Open(first))
                        {
                            ToolTipText = project.Id,
                        };
                        items.Add(projectItem);

                        foreach (var session in project.Sessions)
                        {
                            var target = session;
                            items.Add(new ToolStripMenuItem(SessionLine(session), TrayIconRenderer.StateDot(session.State, imageSize), (s, e) => Open(target))
                            {
                                ToolTipText = session.Cwd + "\nClick: open · Ctrl+click: folder · Shift+click: copy diagnostics",
                            });
                        }
                    }
                }
            }

            items.Add(new ToolStripSeparator());

            var login = new ToolStripMenuItem("Open at Login") { Checked = LoginItem.IsEnabled, CheckOnClick = true };
            login.CheckedChanged += (s, e) =>
            {
                try
                {
                    LoginItem.SetEnabled(login.Checked);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine("ClaudeGuardian: could not update login item: " + ex.Message);
                }
            };
            items.Add(login);

            items.Add(new ToolStripMenuItem("Open State Folder", null, (s, e) =>
            {
                try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + _monitor.Directory + "\"") { UseShellExecute = true }); }
                catch (Exception ex) { Trace.WriteLine("ClaudeGuardian: " + ex.Message); }
            }));
            items.Add(new ToolStripMenuItem("Refresh", null, (s, e) => _monitor.Reload()));

            items.Add(new ToolStripSeparator());
            items.Add(Header("Claude Guardian " + AppInfo.VersionString, bold: false));
            items.Add(new ToolStripMenuItem("Quit Claude Guardian", null, (s, e) => ExitThread()));

            _menu.ResumeLayout();
        }

        /// <summary>Non-clickable row (app header, summary, version).</summary>
        private ToolStripMenuItem Header(string text, bool bold)
        {
            var item = new ToolStripMenuItem(text) { Enabled = false, Tag = MenuRenderer.HeaderTag };
            if (bold) item.Font = new Font(_menu.Font, FontStyle.Bold);
            return item;
        }

        /// <summary>
        /// "Session title · Working · 2 min. ago"
        /// If the session is in a subfolder: "Title · backend\ · Working · 2 min. ago"
        /// </summary>
        private static string SessionLine(Session session)
        {
            var parts = new System.Collections.Generic.List<string> { session.Title ?? "Untitled session" };
            if (session.SubPath != null) parts.Add(session.SubPath + "\\");
            parts.Add(session.State.Label());
            parts.Add(RelativeTime(session.UpdatedDate));
            // "&" in Windows menus marks the access key letter: it must be doubled.
            return SessionIndent + string.Join(" · ", parts).Replace("&", "&&");
        }

        private static string RelativeTime(DateTime date)
        {
            var elapsed = DateTime.Now - date;
            if (elapsed.TotalSeconds < 60) return "now";
            if (elapsed.TotalMinutes < 60) return (int)elapsed.TotalMinutes + " min. ago";
            if (elapsed.TotalHours < 24) return (int)elapsed.TotalHours + " hr. ago";
            var days = (int)elapsed.TotalDays;
            return days == 1 ? "1 day ago" : days + " days ago";
        }

        /// <summary>
        /// Click: go back to the session's app. Ctrl+click: folder in File Explorer.
        /// Shift+click: copy the diagnostics to the clipboard.
        /// </summary>
        private static void Open(Session session)
        {
            var modifiers = Control.ModifierKeys;
            try
            {
                if ((modifiers & Keys.Shift) != 0)
                    Clipboard.SetText(SessionFocuser.Diagnostics(session));
                else if ((modifiers & Keys.Control) != 0)
                    SessionFocuser.RevealInExplorer(session);
                else
                    SessionFocuser.Focus(session);
            }
            catch (Exception ex)
            {
                Trace.WriteLine("ClaudeGuardian: " + ex);
            }
        }

        /// <summary>Headers are disabled items, but drawn with the normal text color.</summary>
        private sealed class MenuRenderer : ToolStripProfessionalRenderer
        {
            public const string HeaderTag = "header";

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                if (Equals(e.Item.Tag, HeaderTag)) e.TextColor = SystemColors.MenuText;
                base.OnRenderItemText(e);
            }
        }
    }
}
