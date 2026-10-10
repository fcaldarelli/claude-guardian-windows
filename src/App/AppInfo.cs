using System;
using System.Reflection;

namespace ClaudeGuardian
{
    /// <summary>
    /// App version. The single source is src\Directory.Build.props: from there it goes into the
    /// executable's attributes (Properties › Details in File Explorer) and is read here,
    /// so the version in the menu matches the one shown by Windows.
    /// </summary>
    public static class AppInfo
    {
        private static readonly Assembly Assembly = typeof(AppInfo).Assembly;

        /// <summary>"1.0.0"</summary>
        public static readonly string Version =
            Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

        /// <summary>Build number: the last component of FileVersion ("1.0.0.1" → "1").</summary>
        public static readonly string Build = BuildNumber();

        /// <summary>"1.0.0", or "1.0.0 (42)" if the build number is not "1".</summary>
        public static string VersionString => Build == "1" ? Version : Version + " (" + Build + ")";

        public static string ExecutablePath => Assembly.Location;

        private static string BuildNumber()
        {
            var fileVersion = Assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
            return System.Version.TryParse(fileVersion ?? "", out var parsed) && parsed.Revision >= 0
                ? parsed.Revision.ToString()
                : "1";
        }
    }
}
