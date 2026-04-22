using CodeAlta.App;
using CodeAlta.App.State;
using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Presentation.Sidebar;
using CodeAlta.Presentation.Threads;

namespace CodeAlta.Views;

internal static class SidebarUiStateHelpers
{
    public static string? GetExpandedProjectId(WorkThreadDescriptor? selectedThread)
        => SidebarSelectionResolver.ResolvePreferredExpandedProjectId(selectedThread?.ProjectRef);

    public static SidebarSelectionTarget ResolveCurrentTarget(ShellThreadStateCoordinator threadStateCoordinator)
    {
        ArgumentNullException.ThrowIfNull(threadStateCoordinator);
        return SidebarSelectionResolver.ResolveCurrentTarget(
            threadStateCoordinator.Selection.SelectedThreadId,
            threadStateCoordinator.Selection.SelectedProjectId,
            threadStateCoordinator.Selection.Target is WorkspaceTarget.Draft { IsGlobal: true });
    }

    public static void RefreshProjection(
        SidebarCoordinator sidebarCoordinator,
        ShellThreadStateCoordinator threadStateCoordinator,
        PromptDraftUiCoordinator promptDraftUiCoordinator,
        Func<string, OpenThreadState?> findOpenThread,
        Action verifyBindableAccess)
    {
        ArgumentNullException.ThrowIfNull(sidebarCoordinator);
        ArgumentNullException.ThrowIfNull(threadStateCoordinator);
        ArgumentNullException.ThrowIfNull(promptDraftUiCoordinator);
        ArgumentNullException.ThrowIfNull(findOpenThread);
        ArgumentNullException.ThrowIfNull(verifyBindableAccess);

        sidebarCoordinator.RefreshProjection(
            threadStateCoordinator.Projects,
            threadStateCoordinator.Threads,
            GetExpandedProjectId(threadStateCoordinator.GetSelectedThread()),
            ResolveCurrentTarget(threadStateCoordinator),
            threadStateCoordinator.NavigatorSettings,
            threadId => findOpenThread(threadId) is { } tab
                ? new ThreadVisualState(tab.StatusBusy, tab.HasPromptDraft)
                : new ThreadVisualState(false, promptDraftUiCoordinator.HasPersistedPromptDraft(threadId)),
            verifyBindableAccess);
    }
}
