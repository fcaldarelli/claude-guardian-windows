using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace ClaudeGuardian.Hook
{
    /// <summary>
    /// Registers (or removes) this executable in the hooks of %USERPROFILE%\.claude\settings.json.
    /// Can be re-run without duplicating entries; backs up settings.json and keeps the other hooks.
    /// </summary>
    internal static class Installer
    {
        private const string Marker = "claude-activity-hook";

        private static readonly string[] Events =
        {
            "SessionStart", "UserPromptSubmit", "PreToolUse", "PostToolUse", "Notification", "Stop", "SessionEnd",
        };

        private static string SettingsPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");

        public static int Install()
        {
            // "exec" form (command + args): Claude Code launches the executable directly, without
            // going through Git Bash or PowerShell. Forward slashes still work with
            // Claude Code versions that pass the command to bash.
            var exe = Assembly.GetExecutingAssembly().Location.Replace('\\', '/');

            return Update(hooks =>
            {
                foreach (var name in Events)
                {
                    var groups = WithoutOurEntries(hooks, name);
                    var group = new Dictionary<string, object>();
                    if (name == "PreToolUse" || name == "PostToolUse") group["matcher"] = "*";
                    group["hooks"] = new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["type"] = "command",
                            ["command"] = exe,
                            ["args"] = new List<object>(),
                            ["timeout"] = 5L,
                        },
                    };
                    groups.Add(group);
                    hooks[name] = groups;
                }
                Console.WriteLine("✓ Hooks registered in " + SettingsPath + " (backup next to the file)");
                Console.WriteLine("  Command: " + exe);
            });
        }

        public static int Uninstall()
        {
            return Update(hooks =>
            {
                foreach (var name in hooks.Keys.ToList())
                {
                    var groups = WithoutOurEntries(hooks, name);
                    if (groups.Count == 0) hooks.Remove(name);
                    else hooks[name] = groups;
                }
                Console.WriteLine("✓ Hooks removed from " + SettingsPath + " (backup next to the file)");
            });
        }

        private static int Update(Action<Dictionary<string, object>> change)
        {
            try
            {
                var path = SettingsPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));

                Dictionary<string, object> settings;
                if (File.Exists(path))
                {
                    var text = File.ReadAllText(path, Encoding.UTF8);
                    settings = string.IsNullOrWhiteSpace(text) ? new Dictionary<string, object>() : Json.Parse(text) as Dictionary<string, object>;
                    if (settings == null)
                    {
                        Console.WriteLine("✗ " + path + " does not contain a JSON object: leaving it unchanged.");
                        return 1;
                    }
                    File.Copy(path, path + ".bak." + DateTime.Now.ToString("yyyyMMddHHmmss"), true);
                }
                else
                {
                    settings = new Dictionary<string, object>();
                }

                if (!(settings.TryGetValue("hooks", out var hooksValue) && hooksValue is Dictionary<string, object> hooks))
                {
                    hooks = new Dictionary<string, object>();
                    settings["hooks"] = hooks;
                }

                change(hooks);
                if (hooks.Count == 0) settings.Remove("hooks");

                File.WriteAllText(path, Json.Serialize(settings, indented: true) + "\n", new UTF8Encoding(false));
                return 0;
            }
            catch (FormatException e)
            {
                Console.WriteLine("✗ settings.json is not valid JSON (" + e.Message + "): leaving it unchanged.");
                return 1;
            }
            catch (Exception e)
            {
                Console.WriteLine("✗ " + e.Message);
                return 1;
            }
        }

        /// <summary>The event's groups, except those that contain this hook.</summary>
        private static List<object> WithoutOurEntries(Dictionary<string, object> hooks, string eventName)
        {
            var groups = hooks.TryGetValue(eventName, out var value) && value is List<object> list ? list : new List<object>();
            return groups.Where(group =>
            {
                var entries = (group as Dictionary<string, object>)?.TryGetValue("hooks", out var h) == true ? h as List<object> : null;
                return entries == null || entries.All(entry =>
                    ((entry as Dictionary<string, object>).GetString("command") ?? "").IndexOf(Marker, StringComparison.OrdinalIgnoreCase) < 0);
            }).ToList();
        }
    }
}
