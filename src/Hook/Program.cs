using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ClaudeGuardian.Hook
{
    /// <summary>
    /// claude-activity-hook.exe
    ///
    /// Claude Code hook: records the state of each session in
    /// %USERPROFILE%\.claude\activity\&lt;session_id&gt;.json, so the app in the
    /// notification area can count how many sessions are working.
    ///
    /// Register it (without arguments) on these events:
    ///   SessionStart, UserPromptSubmit, PreToolUse, PostToolUse,
    ///   Notification, Stop, SessionEnd
    ///
    /// It is an executable rather than a PowerShell script because it runs on every tool
    /// call: PowerShell takes half a second just to start.
    /// It is built as a "Windows" application (no console) so that no windows pop up.
    ///
    /// IMPORTANT: for UserPromptSubmit and SessionStart Claude Code adds the command's
    /// output to the context, so in hook mode nothing is written to stdout.
    ///
    /// Other modes (used by install-hooks.ps1):
    ///   --install     registers this executable in %USERPROFILE%\.claude\settings.json
    ///   --uninstall   removes this hook's entries from settings.json
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length > 0 && (args[0] == "--install" || args[0] == "--uninstall"))
            {
                // UTF-8 messages for install-hooks.ps1, which reads them from the pipe.
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
                return args[0] == "--install" ? Installer.Install() : Installer.Uninstall();
            }

            try
            {
                RunHook();
            }
            catch
            {
                // A hook error must never disturb Claude Code.
            }
            return 0;
        }

        private static string ActivityDirectory()
        {
            var custom = Environment.GetEnvironmentVariable("CLAUDE_ACTIVITY_DIR");
            if (!string.IsNullOrEmpty(custom)) return custom;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "activity");
        }

        private static void RunHook()
        {
            string raw;
            using (var stdin = Console.OpenStandardInput())
            using (var reader = new StreamReader(stdin, new UTF8Encoding(false)))
            {
                raw = reader.ReadToEnd();
            }
            var input = Json.TryParseObject(raw);
            if (input == null) return;

            var sid = new string((input.GetString("session_id") ?? "")
                .Where(c => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '-')
                .ToArray());
            if (sid.Length == 0) return;

            var dir = ActivityDirectory();
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, sid + ".json");

            var hookEvent = input.GetString("hook_event_name") ?? "";
            string state;
            switch (hookEvent)
            {
                case "SessionStart":
                case "Stop":
                    state = "idle";
                    break;
                case "UserPromptSubmit":
                case "PreToolUse":
                case "PostToolUse":
                    state = "working";
                    break;
                case "Notification":
                    state = NotificationState(input.GetString("notification_type"), input.GetString("message"));
                    if (state == null) return;
                    break;
                case "SessionEnd":
                    TryDelete(file);
                    return;
                default:
                    return;
            }

            var cwd = input.GetString("cwd") ?? "";
            var tree = ProcessTree.Inspect();

            var record = new Dictionary<string, object>
            {
                ["session_id"] = sid,
                ["state"] = state,
                ["event"] = hookEvent,
                ["cwd"] = cwd,
                ["project_root"] = FindProjectRoot(cwd),
                ["transcript_path"] = input.GetString("transcript_path") ?? "",
                ["pid"] = (long)tree.ClaudePid,
                ["pid_started"] = tree.ClaudeStarted,
                ["app_path"] = tree.AppPath ?? "",
                ["app_pid"] = (long)tree.AppPid,
                ["console_hwnd"] = tree.ConsoleWindow,
                ["updated_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };

            // Atomic write: temporary file then replace, so the app never reads a half-written file.
            var tmp = Path.Combine(dir, "." + sid + "." + System.Diagnostics.Process.GetCurrentProcess().Id + ".tmp");
            File.WriteAllText(tmp, Json.Serialize(record) + "\n", new UTF8Encoding(false));
            ReplaceFile(tmp, file);
        }

        /// <summary>
        /// "idle" if Claude has finished and awaits a new prompt, "waiting" if it awaits a permission
        /// or an answer, null for purely informational notifications (the state does not change).
        /// </summary>
        private static string NotificationState(string type, string message)
        {
            if (type == "idle_prompt" || (message ?? "").Contains("waiting for your input")) return "idle";
            switch (type)
            {
                case "auth_success":
                case "elicitation_complete":
                case "elicitation_response":
                case "agent_completed":
                    return null;
            }
            if (type != null && type.StartsWith("quota_", StringComparison.Ordinal)) return null;
            return "waiting";
        }

        /// <summary>
        /// Walks up from a folder to the project root (.idea, .vscode or .git).
        /// Needed when Claude was started in a subfolder: the editor recognizes
        /// the project as "already open", not the subfolder.
        /// </summary>
        private static string FindProjectRoot(string cwd)
        {
            if (string.IsNullOrEmpty(cwd)) return cwd;
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\');
            try
            {
                var d = new DirectoryInfo(cwd);
                while (d != null && d.Parent != null && !string.Equals(d.FullName.TrimEnd('\\'), home, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var marker in new[] { ".idea", ".vscode", ".git" })
                    {
                        var path = Path.Combine(d.FullName, marker);
                        if (Directory.Exists(path) || File.Exists(path)) return d.FullName;
                    }
                    d = d.Parent;
                }
            }
            catch
            {
                // Invalid or inaccessible path: use the session folder.
            }
            return cwd;
        }

        private static void ReplaceFile(string source, string destination)
        {
            // The app opens the files with full sharing, but an antivirus or the indexer
            // may hold them open for an instant: retry a few times.
            for (var attempt = 0; attempt < 10; attempt++)
            {
                if (MoveFileEx(source, destination, MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) return;
                Thread.Sleep(20);
            }
            TryDelete(source);
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch { }
        }

        private const uint MOVEFILE_REPLACE_EXISTING = 0x1;
        private const uint MOVEFILE_WRITE_THROUGH = 0x8;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "MoveFileExW")]
        private static extern bool MoveFileEx(string existing, string replacement, uint flags);
    }
}
