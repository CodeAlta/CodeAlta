using CodeAlta.Catalog;

namespace CodeAlta.Presentation.Sessions;

internal static class SessionScopePresentation
{
    public static string BuildScopeSummary(
        SessionViewDescriptor session,
        IReadOnlyList<ProjectDescriptor> projects,
        string globalRoot)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(projects);

        return session.Kind switch
        {
            SessionViewKind.GlobalSession => SR.T("Global session · {0}", globalRoot),
            SessionViewKind.ProjectSession when projects.FirstOrDefault(project => string.Equals(project.Id, session.ProjectRef, StringComparison.OrdinalIgnoreCase)) is { } project
                => $"{project.DisplayName} · {project.ProjectPath}",
            SessionViewKind.InternalSession when projects.FirstOrDefault(project => string.Equals(project.Id, session.ProjectRef, StringComparison.OrdinalIgnoreCase)) is { } internalProject
                => SR.T("Internal · {0}", internalProject.DisplayName),
            SessionViewKind.InternalSession => SR.T("Internal session"),
            _ => session.WorkingDirectory,
        };
    }

    public static IReadOnlyList<SessionViewDescriptor> FilterSessionsForProject(
        IReadOnlyList<SessionViewDescriptor> sessions,
        string? projectId,
        bool includeInternal)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        return sessions
            .Where(session => string.Equals(session.ProjectRef, projectId, StringComparison.OrdinalIgnoreCase))
            .Where(session => includeInternal || session.Kind == SessionViewKind.ProjectSession)
            .OrderByDescending(static session => session.LastActiveAt)
            .ToArray();
    }
}
