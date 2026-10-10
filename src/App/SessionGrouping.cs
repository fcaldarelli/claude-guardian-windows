using System;
using System.Collections.Generic;
using System.Linq;

namespace ClaudeGuardian
{
    /// <summary>Sessions grouped by originating app and, within each app, by project.</summary>
    public sealed class AppGroup
    {
        public string Id;               // executable path, or "" if the app is not identified
        public string Name;
        public List<ProjectGroup> Projects;
        /// <summary>Most urgent state among the group's sessions (for sorting).</summary>
        public int Priority;
    }

    public sealed class ProjectGroup
    {
        public string Id;               // project root
        public string Name;
        public List<Session> Sessions;
        public int Priority;
    }

    public static class SessionGrouping
    {
        public static List<AppGroup> Group(IEnumerable<Session> sessions)
        {
            var apps = sessions
                .GroupBy(s => s.AppPath ?? "", StringComparer.OrdinalIgnoreCase)
                .Select(appSessions =>
                {
                    var projects = appSessions
                        .GroupBy(s => s.RootPath, StringComparer.OrdinalIgnoreCase)
                        .Select(projectSessions =>
                        {
                            var sorted = projectSessions.OrderBy(s => s.State.SortOrder()).ThenByDescending(s => s.UpdatedAt).ToList();
                            return new ProjectGroup
                            {
                                Id = projectSessions.Key,
                                Name = sorted[0].ProjectName,
                                Sessions = sorted,
                                Priority = sorted.Min(s => s.State.SortOrder()),
                            };
                        })
                        .OrderBy(p => p.Priority)
                        .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                        .ToList();

                    return new AppGroup
                    {
                        Id = appSessions.Key,
                        Name = appSessions.First().AppName ?? "Unknown App",
                        Projects = projects,
                        Priority = projects.Min(p => p.Priority),
                    };
                });

            // First those waiting for you, then those working, then idle ones; ties sorted alphabetically.
            return apps.OrderBy(a => a.Priority).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
    }
}
