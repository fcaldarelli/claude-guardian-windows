using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace ClaudeGuardian
{
    /// <summary>State of a Claude Code session, as written by the hook.</summary>
    public enum SessionState
    {
        Working,
        Waiting,
        Stalled,
        Idle,
        Unknown,
    }

    public static class SessionStateExtensions
    {
        public static SessionState Parse(string raw)
        {
            switch (raw)
            {
                case "working": return SessionState.Working;
                case "waiting": return SessionState.Waiting;
                case "stalled": return SessionState.Stalled;
                case "idle": return SessionState.Idle;
                default: return SessionState.Unknown;
            }
        }

        public static string RawValue(this SessionState state) => state.ToString().ToLowerInvariant();

        public static string Label(this SessionState state)
        {
            switch (state)
            {
                case SessionState.Working: return "Working";
                case SessionState.Waiting: return "Waiting for you";
                case SessionState.Stalled: return "No activity";
                case SessionState.Idle: return "Idle";
                default: return "Unknown";
            }
        }

        /// <summary>Menu order: first those waiting for you, then those working, then idle ones.</summary>
        public static int SortOrder(this SessionState state)
        {
            switch (state)
            {
                case SessionState.Waiting: return 0;
                case SessionState.Working: return 1;
                case SessionState.Stalled: return 2;
                case SessionState.Idle: return 3;
                default: return 4;
            }
        }
    }

    public sealed class Session
    {
        public string SessionId;
        /// <summary>State written by the hook; the monitor may correct it (interruptions, inactivity).</summary>
        public SessionState State;
        public string Event;
        public string Cwd;
        public string ProjectRoot;
        public string TranscriptPath;
        public int Pid;
        /// <summary>Start time of the Claude Code process, to detect a reused PID.</summary>
        public long PidStarted;
        public string AppPath;
        public int AppPid;
        /// <summary>Claude Code's console window (classic console or Windows Terminal).</summary>
        public long ConsoleWindow;
        public long UpdatedAt;

        /// <summary>Title read from the transcript (not present in the state file).</summary>
        public string Title;

        public string Id => SessionId;

        public static Session FromJson(Dictionary<string, object> obj)
        {
            var id = obj.GetString("session_id");
            if (string.IsNullOrEmpty(id)) return null;
            return new Session
            {
                SessionId = id,
                State = SessionStateExtensions.Parse(obj.GetString("state")),
                Event = obj.GetString("event"),
                Cwd = obj.GetString("cwd") ?? "",
                ProjectRoot = obj.GetString("project_root"),
                TranscriptPath = obj.GetString("transcript_path"),
                Pid = (int)obj.GetLong("pid"),
                PidStarted = obj.GetLong("pid_started"),
                AppPath = obj.GetString("app_path"),
                AppPid = (int)obj.GetLong("app_pid"),
                ConsoleWindow = obj.GetLong("console_hwnd"),
                UpdatedAt = obj.GetLong("updated_at"),
            };
        }

        /// <summary>Project root (folder with .idea, .vscode or .git), or else the session folder.</summary>
        public string RootPath => string.IsNullOrEmpty(ProjectRoot) ? Cwd : ProjectRoot;

        /// <summary>Subfolder relative to the root, if Claude was launched deeper down.</summary>
        public string SubPath
        {
            get
            {
                var root = RootPath.TrimEnd('\\', '/');
                if (string.Equals(root, Cwd.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) return null;
                if (!Cwd.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)
                    && !Cwd.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)) return null;
                return Cwd.Substring(root.Length + 1).TrimEnd('\\', '/');
            }
        }

        public bool HasApp => !string.IsNullOrEmpty(AppPath);

        /// <summary>Name of the app that launched the session (Windows Terminal, VS Code, IntelliJ...).</summary>
        public string AppName
        {
            get
            {
                if (!HasApp) return ConsoleWindow != 0 ? "Console" : null;
                return AppNames.Friendly(AppPath);
            }
        }

        public string ProjectName
        {
            get
            {
                var name = Path.GetFileName(RootPath.TrimEnd('\\', '/'));
                return string.IsNullOrEmpty(name) ? (string.IsNullOrEmpty(RootPath) ? "Session" : RootPath) : name;
            }
        }

        /// <summary>Name shown in the menu: "project" or "project\subfolder".</summary>
        public string DisplayName => SubPath == null ? ProjectName : ProjectName + "\\" + SubPath;

        public DateTime UpdatedDate => DateTimeOffset.FromUnixTimeSeconds(UpdatedAt).LocalDateTime;
    }

    /// <summary>Human-readable app names, derived from the executable path.</summary>
    public static class AppNames
    {
        private static readonly Dictionary<string, string> Known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["WindowsTerminal.exe"] = "Windows Terminal",
            ["Code.exe"] = "Visual Studio Code",
            ["Code - Insiders.exe"] = "Visual Studio Code - Insiders",
            ["VSCodium.exe"] = "VSCodium",
            ["Cursor.exe"] = "Cursor",
            ["Windsurf.exe"] = "Windsurf",
            ["Zed.exe"] = "Zed",
            ["idea64.exe"] = "IntelliJ IDEA",
            ["pycharm64.exe"] = "PyCharm",
            ["webstorm64.exe"] = "WebStorm",
            ["phpstorm64.exe"] = "PhpStorm",
            ["rider64.exe"] = "Rider",
            ["goland64.exe"] = "GoLand",
            ["clion64.exe"] = "CLion",
            ["rubymine64.exe"] = "RubyMine",
            ["datagrip64.exe"] = "DataGrip",
            ["rustrover64.exe"] = "RustRover",
            ["studio64.exe"] = "Android Studio",
            ["devenv.exe"] = "Visual Studio",
            ["mintty.exe"] = "Git Bash",
            ["wezterm-gui.exe"] = "WezTerm",
            ["alacritty.exe"] = "Alacritty",
            ["Hyper.exe"] = "Hyper",
            ["Tabby.exe"] = "Tabby",
            ["ConEmu64.exe"] = "ConEmu",
            ["ConEmu.exe"] = "ConEmu",
            ["Warp.exe"] = "Warp",
            ["claude.exe"] = "Claude",
        };

        private static readonly Dictionary<string, string> Cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static string Friendly(string exePath)
        {
            if (Cache.TryGetValue(exePath, out var cached)) return cached;
            var file = Path.GetFileName(exePath);
            string name;
            if (!Known.TryGetValue(file, out name))
            {
                try { name = FileVersionInfo.GetVersionInfo(exePath).FileDescription; }
                catch { name = null; }
                if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileNameWithoutExtension(exePath);
            }
            Cache[exePath] = name;
            return name;
        }
    }
}
