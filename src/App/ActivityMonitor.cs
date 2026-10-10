using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace ClaudeGuardian
{
    /// <summary>
    /// Reads the state files written by the hook in %USERPROFILE%\.claude\activity and
    /// keeps the session list up to date. Must be created and used on the UI thread.
    /// </summary>
    public sealed class ActivityMonitor : IDisposable
    {
        public IReadOnlyList<Session> Sessions { get; private set; } = new List<Session>();

        /// <summary>Fires (on the UI thread) on every reload.</summary>
        public event EventHandler Changed;

        public int WorkingCount => Sessions.Count(s => s.State == SessionState.Working);
        public int WaitingCount => Sessions.Count(s => s.State == SessionState.Waiting);
        /// <summary>Idle: turn finished, or "working" with no activity for too long.</summary>
        public int IdleCount => Sessions.Count(s => s.State == SessionState.Idle || s.State == SessionState.Stalled);

        public string Directory { get; }

        /// <summary>Past this time without updates, a session without a valid PID is ignored.</summary>
        private static readonly TimeSpan StaleAfter = TimeSpan.FromHours(12);

        /// <summary>
        /// How long without any activity before a "working" session is considered stalled.
        /// A very long command (e.g. a 15-minute build) can exceed it: in that case it shows
        /// as "No activity" until it finishes.
        /// </summary>
        private static readonly TimeSpan StalledAfter = TimeSpan.FromMinutes(10);

        private readonly TranscriptTitleReader _titleReader = new TranscriptTitleReader();
        private readonly SynchronizationContext _ui;
        private readonly System.Windows.Forms.Timer _timer;
        private FileSystemWatcher _watcher;
        private int _reloadQueued;

        public ActivityMonitor()
        {
            _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

            var custom = Environment.GetEnvironmentVariable("CLAUDE_ACTIVITY_DIR");
            Directory = !string.IsNullOrEmpty(custom)
                ? custom
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "activity");

            try { System.IO.Directory.CreateDirectory(Directory); } catch { }
            Reload();
            StartWatching();

            // Periodic check: discards sessions whose process died
            // without the SessionEnd hook firing (crash, terminal closed).
            _timer = new System.Windows.Forms.Timer { Interval = 3000 };
            _timer.Tick += (s, e) => Reload();
            _timer.Start();
        }

        public void Dispose()
        {
            _timer.Dispose();
            _watcher?.Dispose();
        }

        public void Reload()
        {
            var now = DateTimeOffset.UtcNow;
            var result = new List<Session>();

            string[] files;
            try { files = System.IO.Directory.GetFiles(Directory, "*.json"); }
            catch { files = new string[0]; }

            foreach (var file in files)
            {
                if (Path.GetFileName(file).StartsWith(".", StringComparison.Ordinal)) continue;
                var session = Read(file);
                if (session == null) continue;

                if (!IsAlive(session, now))
                {
                    try { File.Delete(file); } catch { }
                    continue;
                }

                var lastActivity = DateTimeOffset.FromUnixTimeSeconds(session.UpdatedAt);
                if (!string.IsNullOrEmpty(session.TranscriptPath))
                {
                    var info = _titleReader.InfoFor(session.TranscriptPath);
                    session.Title = info.Title;

                    // Interruption (Esc) or API error: Claude Code does not call the Stop hook,
                    // so the recorded state would stay "working".
                    if (info.Interrupted && (session.State == SessionState.Working || session.State == SessionState.Waiting))
                        session.State = SessionState.Idle;

                    if (info.LastModified.HasValue)
                    {
                        var modified = new DateTimeOffset(info.LastModified.Value);
                        if (modified > lastActivity) lastActivity = modified;
                    }
                }

                // Safety net: "working" but no events and no transcript writes
                // for too long. It is no longer counted among the working sessions.
                if (session.State == SessionState.Working && now - lastActivity > StalledAfter)
                    session.State = SessionState.Stalled;

                result.Add(session);
            }

            _titleReader.Retain(new HashSet<string>(
                result.Where(s => !string.IsNullOrEmpty(s.TranscriptPath)).Select(s => s.TranscriptPath),
                StringComparer.OrdinalIgnoreCase));

            Sessions = result
                .OrderBy(s => s.State.SortOrder())
                .ThenByDescending(s => s.UpdatedAt)
                .ToList();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        private static Session Read(string file)
        {
            try
            {
                // Full sharing: the hook must be able to replace the file while we read it.
                using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    var obj = Json.TryParseObject(reader.ReadToEnd());
                    return obj == null ? null : Session.FromJson(obj);
                }
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        private static bool IsAlive(Session session, DateTimeOffset now)
        {
            if (session.Pid > 4) return ProcessTable.IsAlive(session.Pid, session.PidStarted);
            return now - DateTimeOffset.FromUnixTimeSeconds(session.UpdatedAt) < StaleAfter;
        }

        /// <summary>Watches the folder: every hook write updates the count immediately.</summary>
        private void StartWatching()
        {
            try
            {
                _watcher = new FileSystemWatcher(Directory, "*.json")
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                };
                FileSystemEventHandler handler = (s, e) => QueueReload();
                _watcher.Changed += handler;
                _watcher.Created += handler;
                _watcher.Deleted += handler;
                _watcher.Renamed += (s, e) => QueueReload();
                _watcher.EnableRaisingEvents = true;
            }
            catch
            {
                // Folder cannot be watched: the periodic check remains.
            }
        }

        /// <summary>
        /// Events arrive on a system thread, often in bursts (replacing a file
        /// generates several): they are coalesced into a single reload on the UI thread.
        /// </summary>
        private void QueueReload()
        {
            if (Interlocked.Exchange(ref _reloadQueued, 1) == 1) return;
            _ui.Post(_ =>
            {
                Interlocked.Exchange(ref _reloadQueued, 0);
                Reload();
            }, null);
        }
    }
}
