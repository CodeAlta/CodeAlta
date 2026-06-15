using CodeAlta.Catalog;
using CodeAlta.Presentation.Tabs;

namespace CodeAlta.Presentation.Shell;

internal static class ShellTextFormatter
{
    public static string BuildDraftPromptMessage(bool globalScopeSelected)
    {
        return globalScopeSelected
            ? SR.T("Send the first prompt to start a global session.")
            : SR.T("Send the first prompt to start a session for the selected project.");
    }

    public static string BuildDraftTabTitle(
        ProjectDescriptor? selectedProject,
        bool globalScopeSelected)
    {
        if (globalScopeSelected)
        {
            return SR.T("Global draft");
        }

        return selectedProject is null
            ? SR.T("Project draft")
            : SR.T("{0} draft", SessionTabVisualFactory.CompactTitle(selectedProject.DisplayName));
    }

    public static string BuildDraftTabBodyText(
        ProjectDescriptor? selectedProject,
        bool globalScopeSelected)
    {
        if (globalScopeSelected)
        {
            return SR.T("Draft scope selected. Send a prompt to start a global session.");
        }

        return selectedProject is null
            ? SR.T("Draft scope selected. Choose a project or send a prompt to start a session.")
            : SR.T("Draft scope selected for '{0}'. Send a prompt to start a session.", selectedProject.DisplayName);
    }

    public static string BuildWelcomeSubtitle(ProjectDescriptor? selectedProject, bool globalScopeSelected)
    {
        if (globalScopeSelected)
        {
            return SR.T("Global workspace ready for a new session.");
        }

        return selectedProject is null
            ? SR.T("Project draft selected. Choose a project or start typing below.")
            : SR.T("Next session will start in {0}.", FormatProjectLaunchScope(selectedProject));
    }

    public static IReadOnlyList<string> BuildWelcomeGuidanceLines(
        ProjectDescriptor? selectedProject,
        bool globalScopeSelected)
    {
        if (globalScopeSelected)
        {
            return
            [
                SR.T("Use the prompt below to start a new global session."),
                SR.T("Pick a project in the sidebar before sending if you want repository context."),
                SR.T("Reopen any session tab to continue previous work."),
            ];
        }

        if (selectedProject is null)
        {
            return
            [
                SR.T("Choose a project in the sidebar or keep typing below to prepare the next session."),
                SR.T("Your first prompt will create the draft once a scope is selected."),
                SR.T("Reopen any session tab to continue previous work."),
            ];
        }

        return
        [
            SR.T("Use the prompt below to start a new session for {0}.", selectedProject.DisplayName),
            SR.T("Switch projects in the sidebar before sending if you want a different scope."),
            SR.T("Reopen any session tab to continue previous work."),
        ];
    }

    public static string BuildReadyStatusText(
        SessionViewDescriptor? session,
        ProjectDescriptor? selectedProject,
        bool globalScopeSelected)
    {
        _ = session;
        _ = selectedProject;
        _ = globalScopeSelected;
        return SR.T("Prompt ready");
    }

    private static string FormatProjectLaunchScope(ProjectDescriptor project)
    {
        if (string.IsNullOrWhiteSpace(project.ProjectPath))
        {
            return project.DisplayName;
        }

        return SR.T("{0} from folder {1}", project.DisplayName, project.ProjectPath);
    }
}
