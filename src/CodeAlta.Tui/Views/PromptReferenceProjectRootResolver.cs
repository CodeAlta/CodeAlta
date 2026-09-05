using CodeAlta.Catalog;

namespace CodeAlta.Tui.Views;

internal static class PromptReferenceProjectRootResolver
{
    public static string? Resolve(
        SessionViewDescriptor? selectedSession,
        Func<string?, ProjectDescriptor?> getProjectById,
        Func<ProjectDescriptor?> getSelectedProject,
        string? globalRoot = null)
    {
        ArgumentNullException.ThrowIfNull(getProjectById);
        ArgumentNullException.ThrowIfNull(getSelectedProject);

        if (selectedSession is null)
        {
            return getSelectedProject()?.ProjectPath ?? NormalizeOptionalRoot(globalRoot);
        }

        if (selectedSession.Kind == SessionViewKind.GlobalSession)
        {
            return NormalizeOptionalRoot(selectedSession.WorkingDirectory) ?? NormalizeOptionalRoot(globalRoot);
        }

        return getProjectById(selectedSession.ProjectRef)?.ProjectPath ?? selectedSession.WorkingDirectory;
    }

    private static string? NormalizeOptionalRoot(string? path)
        => string.IsNullOrWhiteSpace(path) ? null : path.Trim();
}
