using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace ClaudeGuardian
{
    /// <summary>
    /// Brings back to the foreground the app and window a Claude Code session was launched from.
    ///
    /// Strategy, from the most precise to the most generic:
    ///  1. Console: Claude's console window (classic console, or the Windows Terminal
    ///     window hosting it), recorded by the hook.
    ///  2. Any app: a window whose title contains the project name.
    ///  3. Editor: opening the project root, which brings forward the already open window.
    ///  4. Otherwise: activating the app.
    /// </summary>
    public static class SessionFocuser
    {
        private static readonly HashSet<string> Editors = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Code.exe", "Code - Insiders.exe", "VSCodium.exe", "Cursor.exe", "Windsurf.exe", "Zed.exe",
            "idea64.exe", "pycharm64.exe", "webstorm64.exe", "phpstorm64.exe", "rider64.exe", "goland64.exe",
            "clion64.exe", "rubymine64.exe", "datagrip64.exe", "rustrover64.exe", "studio64.exe",
        };

        private static bool IsEditor(string appPath) =>
            Editors.Contains(Path.GetFileName(appPath))
            || appPath.IndexOf("\\JetBrains\\", StringComparison.OrdinalIgnoreCase) >= 0;

        public static void Focus(Session session)
        {
            // 1. Console window: precise even with many windows of the same terminal.
            var host = ConsoleHost(session);
            if (host != IntPtr.Zero)
            {
                WindowTools.BringToFront(host);
                return;
            }

            if (!session.HasApp)
            {
                // App not identified (e.g. session inside tmux or WSL): show the folder.
                RevealInExplorer(session);
                return;
            }

            // 2. Window by title.
            var windows = AppWindows(session);
            var match = BestMatch(windows, session);
            if (match != null)
            {
                WindowTools.BringToFront(match.Handle);
                return;
            }

            // 3. Editor: like "code C:\path" or "idea64 C:\path" from the command line. The path
            //    is passed to the already open instance, which brings that project's window to the foreground.
            if (IsEditor(session.AppPath) && File.Exists(session.AppPath))
            {
                WindowTools.AllowAnyForeground();
                if (Start(session.AppPath, Quote(session.RootPath))) return;
            }

            // 4. The app: its topmost window, or launch it.
            Activate(session, windows);
        }

        public static void RevealInExplorer(Session session)
        {
            var folder = Directory.Exists(session.Cwd) ? session.Cwd : session.RootPath;
            Start("explorer.exe", Quote(folder));
        }

        // MARK: - Console

        /// <summary>
        /// The visible window hosting Claude's console: the console itself (conhost)
        /// or, for Windows Terminal, the window that "owns" the pseudo-console.
        /// </summary>
        private static IntPtr ConsoleHost(Session session)
        {
            if (session.ConsoleWindow == 0) return IntPtr.Zero;
            var console = new IntPtr(session.ConsoleWindow);
            if (!WindowTools.IsValid(console)) return IntPtr.Zero;
            var root = WindowTools.RootOwner(console);
            if (WindowTools.IsVisible(root)) return root;
            return WindowTools.IsVisible(console) ? console : IntPtr.Zero;
        }

        // MARK: - App

        /// <summary>
        /// Windows of the app that launched the session, in their stacking order.
        /// The executable is compared, not just the recorded PID, because several copies of the same app
        /// (e.g. IntelliJ Ultimate and Community) have different executables, while the windows of
        /// apps like VS Code can belong to a process other than the recorded one.
        /// </summary>
        private static List<TopWindow> AppWindows(Session session)
        {
            var paths = new Dictionary<int, string>();
            return WindowTools.TopLevelWindows().Where(w =>
            {
                if (w.Pid == session.AppPid) return true;
                if (!paths.TryGetValue(w.Pid, out var path))
                {
                    path = ProcessTable.ImagePath(w.Pid);
                    paths[w.Pid] = path;
                }
                return string.Equals(path, session.AppPath, StringComparison.OrdinalIgnoreCase);
            }).ToList();
        }

        private static void Activate(Session session, List<TopWindow> windows)
        {
            var window = windows.FirstOrDefault(w => w.Pid == session.AppPid) ?? windows.FirstOrDefault();
            if (window != null)
            {
                WindowTools.BringToFront(window.Handle);
                return;
            }
            if (File.Exists(session.AppPath))
            {
                WindowTools.AllowAnyForeground();
                Start(session.AppPath, "");
            }
            else
            {
                RevealInExplorer(session);
            }
        }

        private static bool Start(string file, string arguments)
        {
            try
            {
                Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = true })?.Dispose();
                return true;
            }
            catch (Exception e)
            {
                Trace.WriteLine("ClaudeGuardian: could not start " + file + ": " + e.Message);
                return false;
            }
        }

        private static string Quote(string path) => "\"" + (path ?? "").TrimEnd('\\') + "\"";

        // MARK: - Window by title

        /// <summary>Names under which the project can appear in the window title.</summary>
        private static List<string> CandidateNames(Session session)
        {
            var names = new List<string> { session.ProjectName, Path.GetFileName(session.Cwd.TrimEnd('\\', '/')) };
            // IntelliJ uses the project name saved in .idea\.name, which can differ from the folder.
            try
            {
                var ideaName = Path.Combine(session.RootPath, ".idea", ".name");
                if (File.Exists(ideaName)) names.Add(File.ReadAllText(ideaName).Trim());
            }
            catch { }
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return names.Where(n => !string.IsNullOrEmpty(n) && seen.Add(n)).ToList();
        }

        /// <summary>
        /// Splits a title into its parts. Dashes split only when surrounded by spaces,
        /// so a project like "shop-backend" stays whole.
        /// </summary>
        private static List<string> TitleParts(string title)
        {
            var text = title;
            foreach (var separator in new[] { " — ", " – ", " - ", " | ", "[", "]", "(", ")" })
                text = text.Replace(separator, "\u001F");
            return text.Split('\u001F').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        }

        /// <summary>
        /// Picks the most likely window. First exact matches on a "part" of the title
        /// (editors use titles like "project – File.java" or "file.ts - project - Visual Studio Code"),
        /// then the full path, then the session title (Claude Code uses it as the terminal
        /// title), then the name contained in the title, only if unique.
        /// </summary>
        private static TopWindow BestMatch(List<TopWindow> windows, Session session)
        {
            var names = CandidateNames(session);

            // 1. A part of the title matches the project name.
            var exact = windows.Where(w =>
                TitleParts(w.Title).Any(part => names.Any(n => string.Equals(n, part, StringComparison.OrdinalIgnoreCase)))).ToList();
            if (exact.Count == 1) return exact[0];

            // 2. The title contains the session path (some terminals show the folder).
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var paths = new List<string>();
            if (session.Cwd.Length > 3) paths.Add(session.Cwd);
            if (session.Cwd.StartsWith(home + "\\", StringComparison.OrdinalIgnoreCase))
            {
                var relative = session.Cwd.Substring(home.Length);
                paths.Add("~" + relative);
                paths.Add("~" + relative.Replace('\\', '/'));    // Git Bash and WSL show ~/project
            }
            var byPath = windows.Where(w => paths.Any(p => w.Title.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
            if (byPath.Count == 1) return byPath[0];
            if (exact.Count > 0) return exact[0];

            // 3. The session title appears in the window title.
            if (!string.IsNullOrEmpty(session.Title) && session.Title.Length >= 6)
            {
                var byTitle = windows.Where(w => w.Title.IndexOf(session.Title.TrimEnd('…'), StringComparison.CurrentCultureIgnoreCase) >= 0).ToList();
                if (byTitle.Count == 1) return byTitle[0];
            }

            // 4. The name appears in the title: accepted only if a single window matches,
            //    to avoid opening the wrong one (e.g. "app" inside "my-app").
            var loose = windows.Where(w => names.Any(n => w.Title.IndexOf(n, StringComparison.CurrentCultureIgnoreCase) >= 0)).ToList();
            return loose.Count == 1 ? loose[0] : null;
        }

        // MARK: - Diagnostics

        /// <summary>Text with what the app sees for this session, to paste when reporting problems.</summary>
        public static string Diagnostics(Session session)
        {
            var lines = new List<string>
            {
                "Claude Guardian " + AppInfo.VersionString + " – diagnostics",
                "Windows: " + Environment.OSVersion.VersionString + (Environment.Is64BitOperatingSystem ? " (64-bit)" : ""),
                "Session: " + session.SessionId,
                "Displayed state: " + session.State.RawValue() + " · last hook event: " + (session.Event ?? "-") + " " + session.UpdatedDate,
                "Transcript: " + (string.IsNullOrEmpty(session.TranscriptPath) ? "- (not yet recorded by the hook)" : session.TranscriptPath),
                "Folder: " + session.Cwd,
                "Project root: " + session.RootPath,
                "Names searched: " + string.Join(" | ", CandidateNames(session)),
                "Claude Code pid: " + session.Pid,
                "App: " + (session.HasApp ? session.AppPath : "-") + " (recorded pid: " + session.AppPid + ")",
            };

            var console = new IntPtr(session.ConsoleWindow);
            if (session.ConsoleWindow == 0)
            {
                lines.Add("Console window: - (not recorded)");
            }
            else
            {
                var root = WindowTools.RootOwner(console);
                lines.Add("Console window: 0x" + session.ConsoleWindow.ToString("X")
                    + (WindowTools.IsValid(console) ? (WindowTools.IsVisible(console) ? " visible" : " hidden") : " GONE")
                    + " · root owner: " + (root == IntPtr.Zero ? "-" : "0x" + root.ToInt64().ToString("X")
                        + (WindowTools.IsVisible(root) ? " visible" : " hidden")
                        + " \"" + WindowTools.TitleOf(root) + "\""
                        + " (" + Path.GetFileName(ProcessTable.ImagePath(WindowTools.ProcessOf(root)) ?? "?") + ")"));
                var host = ConsoleHost(session);
                if (host != IntPtr.Zero)
                {
                    lines.Add("→ Using the console window (step 1).");
                    return string.Join(Environment.NewLine, lines);
                }
            }

            if (!session.HasApp)
            {
                lines.Add("App not identified: opening the folder.");
                return string.Join(Environment.NewLine, lines);
            }
            lines.Add("Editor: " + (IsEditor(session.AppPath) ? "yes" : "no"));

            var windows = AppWindows(session);
            var chosen = BestMatch(windows, session);
            lines.Add("App windows found: " + windows.Count);
            foreach (var w in windows)
            {
                var mark = chosen != null && chosen.Handle == w.Handle ? "→" : " ";
                lines.Add(mark + " " + w.Title + "   [pid " + w.Pid + " · parts: " + string.Join(" | ", TitleParts(w.Title)) + "]");
            }
            if (chosen == null)
            {
                lines.Add(IsEditor(session.AppPath)
                    ? "No window selected: using the fallback (opening the project)."
                    : "No window selected: activating the app.");
            }
            return string.Join(Environment.NewLine, lines);
        }
    }
}
