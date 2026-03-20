using CodeAlta.Catalog;
using CodeAlta.Presentation.Tabs;

namespace CodeAlta.Presentation.Shell;

internal static class ShellTextFormatter
{
    public static string BuildHeaderText(
        WorkThreadDescriptor? thread,
        ProjectDescriptor? selectedProject,
        string globalRoot,
        string preferredBackendId,
        bool globalScopeSelected)
    {
        _ = globalRoot;

        if (thread is null)
        {
            if (globalScopeSelected)
            {
                return $"CodeAlta | {preferredBackendId} | global draft";
            }

            if (selectedProject is not null)
            {
                return $"CodeAlta | {preferredBackendId} | {selectedProject.Slug} draft";
            }

            return "CodeAlta | no thread selected";
        }

        return thread.Kind switch
        {
            WorkThreadKind.GlobalThread => $"CodeAlta | {thread.BackendId} | {ThreadTabVisualFactory.CompactTitle(thread.Title)} | global",
            WorkThreadKind.ProjectThread => $"CodeAlta | {thread.BackendId} | {selectedProject?.Slug ?? "?"} | {ThreadTabVisualFactory.CompactTitle(thread.Title)}",
            WorkThreadKind.InternalThread => $"CodeAlta | {thread.BackendId} | internal | {ThreadTabVisualFactory.CompactTitle(thread.Title)}",
            _ => $"CodeAlta | thread={thread.Title}",
        };
    }

    public static string BuildDraftPromptMessage(bool globalScopeSelected)
    {
        return globalScopeSelected
            ? "Send the first prompt to start a global thread."
            : "Send the first prompt to start a thread for the selected project.";
    }

    public static string BuildDraftTabTitle(
        ProjectDescriptor? selectedProject,
        bool globalScopeSelected)
    {
        if (globalScopeSelected)
        {
            return "Global draft";
        }

        return selectedProject is null
            ? "Project draft"
            : $"{ThreadTabVisualFactory.CompactTitle(selectedProject.DisplayName)} draft";
    }

    public static string BuildDraftTabBodyText(
        ProjectDescriptor? selectedProject,
        bool globalScopeSelected)
    {
        if (globalScopeSelected)
        {
            return "Draft scope selected. Send a prompt to start a global thread.";
        }

        return selectedProject is null
            ? "Draft scope selected. Choose a project or send a prompt to start a thread."
            : $"Draft scope selected for '{selectedProject.DisplayName}'. Send a prompt to start a thread.";
    }

    public static string BuildWelcomeSubtitle(ProjectDescriptor? selectedProject, bool globalScopeSelected)
    {
        if (globalScopeSelected)
        {
            return "Global workspace ready for a new thread.";
        }

        return selectedProject is null
            ? "Project draft selected. Choose a project or start typing below."
            : $"Next thread will start in {selectedProject.DisplayName}.";
    }

    public static IReadOnlyList<string> BuildWelcomeGuidanceLines(
        ProjectDescriptor? selectedProject,
        bool globalScopeSelected)
    {
        if (globalScopeSelected)
        {
            return
            [
                "Use the prompt below to start a new global thread.",
                "Pick a project in the sidebar before sending if you want repository context.",
                "Reopen any thread tab to continue previous work.",
            ];
        }

        if (selectedProject is null)
        {
            return
            [
                "Choose a project in the sidebar or keep typing below to prepare the next thread.",
                "Your first prompt will create the draft once a scope is selected.",
                "Reopen any thread tab to continue previous work.",
            ];
        }

        return
        [
            $"Use the prompt below to start a new thread for {selectedProject.DisplayName}.",
            "Switch projects in the sidebar before sending if you want a different scope.",
            "Reopen any thread tab to continue previous work.",
        ];
    }

    public static string BuildReadyStatusText(
        WorkThreadDescriptor? thread,
        ProjectDescriptor? selectedProject,
        bool globalScopeSelected)
    {
        _ = thread;
        _ = selectedProject;
        _ = globalScopeSelected;
        return "Prompt ready";
    }
}