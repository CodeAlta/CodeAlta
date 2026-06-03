using CodeAlta.App;
using CodeAlta.App.State;
using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Presentation.Sidebar;
using CodeAlta.Presentation.Sessions;

namespace CodeAlta.Views;

internal static class SidebarUiStateHelpers
{
    public static string? GetExpandedProjectId(SessionViewDescriptor? selectedSession)
        => SidebarSelectionResolver.ResolvePreferredExpandedProjectId(selectedSession?.ProjectRef);

    public static void ToggleNavigator(SidebarView sidebarView, Action focusPromptTarget)
    {
        ArgumentNullException.ThrowIfNull(sidebarView);
        ArgumentNullException.ThrowIfNull(focusPromptTarget);

        var collapse = !sidebarView.IsCollapsed;
        var hadSidebarFocus = sidebarView.Tree.HasFocusWithin;
        sidebarView.SetCollapsed(collapse);
        if (collapse && hadSidebarFocus)
        {
            focusPromptTarget();
        }
    }

    public static SidebarSelectionTarget ResolveCurrentTarget(ShellSessionStateCoordinator sessionStateCoordinator)
    {
        ArgumentNullException.ThrowIfNull(sessionStateCoordinator);
        return SidebarSelectionResolver.ResolveCurrentTarget(
            sessionStateCoordinator.Selection.SelectedSessionId,
            sessionStateCoordinator.Selection.SelectedProjectId,
            sessionStateCoordinator.Selection.Target is WorkspaceTarget.Draft { IsGlobal: true });
    }

    public static void RefreshProjection(
        SidebarCoordinator sidebarCoordinator,
        ShellSessionStateCoordinator sessionStateCoordinator,
        PromptDraftUiCoordinator promptDraftUiCoordinator,
        Func<string, OpenSessionState?> findOpenSession,
        Func<string, bool> isRuntimeSessionRunning,
        Func<string, bool> hasActiveReminder,
        Action verifyBindableAccess)
    {
        ArgumentNullException.ThrowIfNull(sidebarCoordinator);
        ArgumentNullException.ThrowIfNull(sessionStateCoordinator);
        ArgumentNullException.ThrowIfNull(promptDraftUiCoordinator);
        ArgumentNullException.ThrowIfNull(findOpenSession);
        ArgumentNullException.ThrowIfNull(isRuntimeSessionRunning);
        ArgumentNullException.ThrowIfNull(hasActiveReminder);
        ArgumentNullException.ThrowIfNull(verifyBindableAccess);

        sidebarCoordinator.RefreshProjection(
            sessionStateCoordinator.Projects,
            sessionStateCoordinator.Sessions,
            GetExpandedProjectId(sessionStateCoordinator.GetSelectedSession()),
            ResolveCurrentTarget(sessionStateCoordinator),
            sessionStateCoordinator.NavigatorSettings,
            sessionId => findOpenSession(sessionId) is { } tab
                ? new SessionVisualState(tab.StatusBusy || isRuntimeSessionRunning(sessionId), tab.HasPromptDraft, hasActiveReminder(sessionId))
                : new SessionVisualState(isRuntimeSessionRunning(sessionId), promptDraftUiCoordinator.HasPersistedPromptDraft(sessionId), hasActiveReminder(sessionId)),
            promptDraftUiCoordinator.HasDraftPrompt,
            verifyBindableAccess);
        var markdown = sessionStateCoordinator.GetSelectedSession() is { } session && findOpenSession(session.SessionId) is { } tab
            ? tab.NotesMarkdown
            : string.Empty;
        sidebarCoordinator.View.SetNotesMarkdown(markdown);
    }
}
