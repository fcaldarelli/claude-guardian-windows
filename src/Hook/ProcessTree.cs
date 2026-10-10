using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace ClaudeGuardian.Hook
{
    /// <summary>
    /// Walks up the process tree starting from the hook and determines:
    ///  ClaudePid      the Claude Code process (to discard dead sessions: crash, closed terminal)
    ///  AppPath        the executable of the app that launched it (Windows Terminal, VS Code, IntelliJ, ...)
    ///  ConsoleWindow  Claude's console window, to bring exactly that one to the foreground
    /// </summary>
    internal sealed class ProcessTree
    {
        public int ClaudePid;
        public long ClaudeStarted;
        public string AppPath;
        public int AppPid;
        public long ConsoleWindow;

        /// <summary>
        /// "Pass-through" processes between Claude and the app: shells, console hosts, runtimes.
        /// The app is the first ancestor that is not one of these.
        /// </summary>
        private static readonly HashSet<string> Intermediates = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "claude.exe", "node.exe", "bun.exe",
            "cmd.exe", "powershell.exe", "pwsh.exe", "bash.exe", "sh.exe", "zsh.exe", "fish.exe", "nu.exe",
            "conhost.exe", "openconsole.exe", "winpty-agent.exe", "wsl.exe", "wslhost.exe",
            "git.exe", "git-bash.exe", "npx.exe", "npm.exe", "tmux.exe", "starship.exe", "clink_x64.exe",
        };

        /// <summary>System processes: if we get here the app cannot be identified (e.g. classic console).</summary>
        private static readonly HashSet<string> Roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "explorer.exe", "services.exe", "svchost.exe", "wininit.exe", "winlogon.exe", "userinit.exe",
            "sihost.exe", "runtimebroker.exe", "taskhostw.exe", "system", "[system process]",
        };

        public static ProcessTree Inspect()
        {
            var result = new ProcessTree();
            var table = ProcessTable.Snapshot();
            var self = System.Diagnostics.Process.GetCurrentProcess().Id;

            // Chain of ancestors, from the hook's parent upwards.
            var chain = new List<ProcessEntry>();
            var child = self;
            for (var i = 0; i < 25; i++)
            {
                if (!table.TryGetValue(child, out var entry)) break;
                var parent = entry.ParentPid;
                if (parent <= 4 || !table.ContainsKey(parent) || !ProcessTable.IsGenuineParent(parent, child)) break;
                chain.Add(table[parent]);
                child = parent;
            }

            // Claude Code: the first ancestor named claude*.exe (or node.exe for older installations).
            var claudeIndex = chain.FindIndex(p => p.ExeName.IndexOf("claude", StringComparison.OrdinalIgnoreCase) >= 0);
            if (claudeIndex < 0) claudeIndex = chain.FindIndex(p => string.Equals(p.ExeName, "node.exe", StringComparison.OrdinalIgnoreCase));
            if (claudeIndex < 0 && chain.Count > 0) claudeIndex = 0;
            if (claudeIndex < 0) return result;

            result.ClaudePid = chain[claudeIndex].Pid;
            result.ClaudeStarted = ProcessTable.StartTime(result.ClaudePid);

            // App: the first ancestor of Claude that is not a shell or a console host.
            for (var i = claudeIndex + 1; i < chain.Count; i++)
            {
                var name = chain[i].ExeName;
                var path = ProcessTable.ImagePath(chain[i].Pid);
                if (Intermediates.Contains(name) && !IsElectronApp(path)) continue;
                if (Roots.Contains(name)) break;
                if (string.IsNullOrEmpty(path)) break;
                result.AppPath = path;
                result.AppPid = chain[i].Pid;

                // Electron and Chromium apps (VS Code, Cursor...) have child processes with the same
                // executable: walk up to the main process, which owns the windows.
                for (var j = i + 1; j < chain.Count; j++)
                {
                    if (!string.Equals(ProcessTable.ImagePath(chain[j].Pid), path, StringComparison.OrdinalIgnoreCase)) break;
                    result.AppPid = chain[j].Pid;
                }
                break;
            }

            result.ConsoleWindow = ConsoleWindowOf(result.ClaudePid);
            return result;
        }

        /// <summary>
        /// The Claude desktop app is named claude.exe like the CLI: it is recognized because,
        /// like all Electron apps, it has the resources\app.asar archive next to it.
        /// </summary>
        private static bool IsElectronApp(string exePath)
        {
            if (string.IsNullOrEmpty(exePath)) return false;
            try
            {
                return File.Exists(Path.Combine(Path.GetDirectoryName(exePath), "resources", "app.asar"));
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// The console window Claude Code is attached to. With the classic console it is
        /// the terminal window itself; with Windows Terminal it is a hidden window
        /// "owned" by the terminal window, which the app resolves at click time.
        /// </summary>
        private static long ConsoleWindowOf(int pid)
        {
            if (pid <= 4) return 0;
            try
            {
                FreeConsole();
                if (!AttachConsole((uint)pid)) return 0;
                var hwnd = GetConsoleWindow();
                FreeConsole();
                return hwnd.ToInt64();
            }
            catch
            {
                return 0;
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeConsole();

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();
    }
}
