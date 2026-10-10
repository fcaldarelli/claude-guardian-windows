using System;
using Microsoft.Win32;

namespace ClaudeGuardian
{
    /// <summary>"Open at Login": an entry in HKCU\...\Run, as per-user installed apps do.</summary>
    public static class LoginItem
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "Claude Guardian";

        private static string Command => "\"" + AppInfo.ExecutablePath + "\"";

        /// <summary>Enabled only if the entry points to this executable (not to a copy moved elsewhere).</summary>
        public static bool IsEnabled
        {
            get
            {
                try
                {
                    using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
                        return string.Equals(key?.GetValue(ValueName) as string, Command, StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    return false;
                }
            }
        }

        public static void SetEnabled(bool enabled)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enabled) key.SetValue(ValueName, Command);
                else key.DeleteValue(ValueName, false);
            }
        }
    }
}
