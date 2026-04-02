using CodeAlta.Agent;
using CodeAlta.App.Context;
using CodeAlta.App.State;
using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Presentation.Chat;
using CodeAlta.Presentation.Workspace;
using CodeAlta.ViewModels;
using XenoAtom.Terminal.UI;

namespace CodeAlta.App;

internal sealed class CodeAltaFrontendComposition
{
    public required ChatBackendPreferenceCoordinator BackendPreferences { get; init; }
    public required CodeAltaShellController ShellController { get; init; }
    public required RuntimeEventPump RuntimeEventPump { get; init; }
    public required TerminalLoopCoordinator TerminalLoopCoordinator { get; init; }
    public required ChatBackendInitializationCoordinator ChatBackendInitializationCoordinator { get; init; }
    public required ShellThreadStateCoordinator ThreadStateCoordinator { get; init; }
    public required ShellWorkspaceCoordinator WorkspaceCoordinator { get; init; }
    public required ThreadRuntimeEventCoordinator ThreadRuntimeEventCoordinator { get; init; }
    public required ThreadPromptQueueCoordinator ThreadPromptQueueCoordinator { get; init; }
    public required ThreadCommandCoordinator ThreadCommandCoordinator { get; init; }
    public required ThreadCreationCoordinator ThreadCreationCoordinator { get; init; }
    public required PromptDraftUiCoordinator PromptDraftUiCoordinator { get; init; }
    public required CodeAltaShellViewModel ShellViewModel { get; init; }
    public required SidebarViewModel SidebarViewModel { get; init; }
    public required ThreadWorkspaceViewModel ThreadWorkspaceViewModel { get; init; }
    public required PromptComposerViewModel PromptComposerViewModel { get; init; }
    public required SessionUsageViewModel SessionUsageViewModel { get; init; }
    public required Dictionary<string, ChatBackendState> ChatBackendStates { get; init; }
    public required SidebarCoordinator SidebarCoordinator { get; init; }
    public required NavigatorActionCoordinator NavigatorActionCoordinator { get; init; }
    public required ChatSelectorCoordinator ChatSelectorCoordinator { get; init; }
    public required ChatPreferenceContext ChatPreferenceContext { get; init; }
    public required ChatSelectorStateContext ChatSelectorStateContext { get; init; }
    public required ShellWorkspaceContext ShellWorkspaceContext { get; init; }
    public required ThreadSelectionContext ThreadSelectionContext { get; init; }
    public required WorkspaceRefreshContext WorkspaceRefreshContext { get; init; }

    public static CodeAltaFrontendComposition Create(
        ProjectCatalog projectCatalog,
        WorkThreadCatalog threadCatalog,
        WorkThreadRuntimeService runtimeService,
        CatalogOptions catalogOptions,
        AgentHub agentHub,
        ICodeAltaShell shell,
        KnownProjectImporter knownProjectImporter,
        State<float> welcomePhase01,
        CodeAltaFrontendCallbacks callbacks)
    {
        ArgumentNullException.ThrowIfNull(projectCatalog);
        ArgumentNullException.ThrowIfNull(threadCatalog);
        ArgumentNullException.ThrowIfNull(runtimeService);
        ArgumentNullException.ThrowIfNull(catalogOptions);
        ArgumentNullException.ThrowIfNull(agentHub);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(knownProjectImporter);
        ArgumentNullException.ThrowIfNull(welcomePhase01);
        ArgumentNullException.ThrowIfNull(callbacks);

        var shellViewModel = new CodeAltaShellViewModel();
        var sidebarViewModel = new SidebarViewModel();
        var threadWorkspaceViewModel = new ThreadWorkspaceViewModel();
        var promptComposerViewModel = new PromptComposerViewModel();
        var sessionUsageViewModel = new SessionUsageViewModel();
        var chatBackendStates = ChatBackendPresentation.CreateBackendStates();
        var backendPreferences = new ChatBackendPreferenceCoordinator(new CodeAltaConfigStore(catalogOptions), CodeAlta.Views.CodeAltaApp.UiLogger);
        var shellController = new CodeAltaShellController(
            shell,
            knownProjectImporter,
            new ProjectCatalogStore(projectCatalog),
            new RecoverableThreadSource(runtimeService),
            new WorkThreadDeleter(runtimeService));
        var runtimeEventPump = new RuntimeEventPump(runtimeService, shellController);
        var terminalLoopCoordinator = new TerminalLoopCoordinator(
            shellController,
            runtimeEventPump,
            callbacks.AssignUiDispatcher,
            callbacks.ApplyPendingSidebarSelection);
        var threadStateCoordinator = new ShellThreadStateCoordinator(
            projectCatalog,
            threadCatalog,
            callbacks.GetUiDispatcher,
            callbacks.GetThreadPaneBounds,
            thread => callbacks.IsChatBackendReady(new AgentBackendId(thread.BackendId)),
            callbacks.LoadPromptDraft,
            callbacks.DeletePromptDraft,
            callbacks.ApplyThreadPreference,
            callbacks.RememberThreadPreference,
            callbacks.EnsureThreadHistoryLoadedAsync,
            callbacks.RefreshSelectionAndThreadWorkspace,
            callbacks.RefreshCatalogAndThreadWorkspace,
            callbacks.ResetPendingThreadTabSelection,
            callbacks.RemoveThreadTabPage,
            callbacks.SetStatus);
        var threadSelectionContext = new ThreadSelectionContext(
            threadStateCoordinator,
            callbacks.EnsureThreadHistoryLoadedAsync,
            callbacks.IsSelectedThread);
        var promptDraftUiCoordinator = new PromptDraftUiCoordinator(
            new PromptDraftCoordinator(),
            catalogOptions,
            () => threadStateCoordinator.Selection,
            callbacks.RefreshCatalogAndThreadWorkspace);
        var chatSelectorStateContext = new ChatSelectorStateContext(
            threadWorkspaceViewModel,
            callbacks.GetUiDispatcher,
            callbacks.VerifyBindableAccess);
        var chatPreferenceContext = new ChatPreferenceContext(
            callbacks.ApplyDraftBackendPreference,
            callbacks.ApplyThreadPreference,
            callbacks.RememberGlobalBackendPreference,
            callbacks.RememberThreadPreference);
        var workspaceRefreshContext = new WorkspaceRefreshContext(
            callbacks.InvalidateSelectedSessionUsage,
            callbacks.RefreshHeaderAndThreadWorkspace);
        var (navigatorActionCoordinator, sidebarCoordinator) = SidebarServicesFactory.Create(
            sidebarViewModel,
            catalogOptions,
            shellController,
            threadStateCoordinator,
            callbacks.RefreshCatalogAndThreadWorkspace,
            callbacks.SetStatus,
            callbacks.SetReadyStatusForCurrentSelection);
        var chatSelectorCoordinator = new ChatSelectorCoordinator(
            threadWorkspaceViewModel,
            promptComposerViewModel,
            chatBackendStates,
            chatSelectorStateContext,
            threadSelectionContext,
            chatPreferenceContext,
            workspaceRefreshContext);

        ThreadPromptQueueCoordinator? threadPromptQueueCoordinator = null;
        ThreadCommandCoordinator? threadCommandCoordinator = null;

        var shellWorkspaceContext = new ShellWorkspaceContext(
            chatSelectorCoordinator.GetPreferredBackendId,
            () =>
            {
                var hasStatus = chatSelectorCoordinator.TryGetPromptUnavailableStatus(out var message, out var tone);
                return (hasStatus, message, tone);
            },
            callbacks.HasWorkspaceSurface,
            callbacks.SetThreadPaneContent,
            callbacks.EnsureSelectionDefaults,
            callbacks.RefreshSidebarProjection,
            callbacks.SyncSidebarSelectionToCurrentState,
            () => threadPromptQueueCoordinator!.RefreshSelectedThreadQueueUi(),
            callbacks.RefreshChatSelectorsForDraftScope,
            callbacks.RefreshChatSelectorsForThread,
            callbacks.SyncPromptText,
            callbacks.UpdatePromptAvailabilityUi,
            callbacks.SyncThreadTabControl,
            callbacks.DispatchToUi,
            callbacks.DispatchToUiDeferred,
            callbacks.VerifyBindableAccess);
        var workspaceCoordinator = new ShellWorkspaceCoordinator(
            shellViewModel,
            threadWorkspaceViewModel,
            sessionUsageViewModel,
            chatBackendStates,
            threadSelectionContext,
            shellWorkspaceContext,
            welcomePhase01);
        threadPromptQueueCoordinator = new ThreadPromptQueueCoordinator(
            threadWorkspaceViewModel,
            threadSelectionContext,
            callbacks.UpdatePromptAvailabilityUi,
            callbacks.DispatchToUi,
            callbacks.VerifyBindableAccess,
            (tab, prompt, cancellationToken) => threadCommandCoordinator!.DispatchQueuedPromptAsync(tab, prompt, steer: false, cancellationToken),
            (tab, prompt, cancellationToken) => threadCommandCoordinator!.DispatchQueuedPromptAsync(tab, prompt, steer: true, cancellationToken));
        var chatBackendInitializationCoordinator = new ChatBackendInitializationCoordinator(
            agentHub,
            chatBackendStates,
            callbacks.DispatchToUi,
            callbacks.RefreshHeaderAndThreadWorkspace);
        var threadRuntimeEventCoordinator = new ThreadRuntimeEventCoordinator(
            threadId => threadStateCoordinator.FindThread(threadId),
            threadId => threadStateCoordinator.FindOpenThread(threadId),
            callbacks.GetAutoApproveEnabled,
            callbacks.IsSelectedThread,
            callbacks.InvalidateSelectedSessionUsage,
            callbacks.RefreshShellChrome,
            callbacks.SetStatus,
            callbacks.SetThreadStatus,
            callbacks.ClearThreadStatus,
            () => threadPromptQueueCoordinator!.RefreshSelectedThreadQueueUi(),
            (tab, cancellationToken) => threadCommandCoordinator!.DrainQueuedPromptAsync(tab, cancellationToken));
        var threadCreationCoordinator = new ThreadCreationCoordinator(
            runtimeService,
            catalogOptions,
            chatSelectorCoordinator.GetPreferredBackendId,
            threadSelectionContext.GetSelectedProject,
            () => threadSelectionContext.Selection,
            static () => null,
            (backendId, workingDirectory, projectRoots) => threadCommandCoordinator!.BuildPreferredExecutionOptions(backendId, workingDirectory, projectRoots),
            callbacks.RememberThreadPreference,
            callbacks.RegisterCreatedThreadAsync,
            static () => { },
            callbacks.SetStatus);
        threadCommandCoordinator = new ThreadCommandCoordinator(
            runtimeService,
            catalogOptions,
            chatBackendStates,
            threadSelectionContext,
            chatSelectorStateContext,
            chatPreferenceContext,
            new ThreadCommandContext(
                callbacks.GetUiDispatcher(),
                callbacks.TrySetPromptUnavailableStatus,
                title => threadCreationCoordinator.CreateGlobalThreadAsync(title),
                title => threadCreationCoordinator.CreateProjectThreadAsync(title),
                callbacks.PersistViewStateAsync,
                callbacks.GetAutoApproveEnabled,
                callbacks.ClearDraftPromptText,
                callbacks.SetReadyStatusForCurrentSelection,
                callbacks.ClearPromptText,
                callbacks.IsPromptTextEmpty,
                callbacks.RestorePromptText,
                callbacks.RefreshHeaderAndThreadWorkspace,
                callbacks.RefreshCatalogAndThreadWorkspace,
                callbacks.SetStatus,
                callbacks.SetThreadStatus,
                threadRuntimeEventCoordinator.TryRenderInteraction),
            threadPromptQueueCoordinator,
            promptComposerViewModel);

        return new CodeAltaFrontendComposition
        {
            BackendPreferences = backendPreferences,
            ShellController = shellController,
            RuntimeEventPump = runtimeEventPump,
            TerminalLoopCoordinator = terminalLoopCoordinator,
            ChatBackendInitializationCoordinator = chatBackendInitializationCoordinator,
            ThreadStateCoordinator = threadStateCoordinator,
            WorkspaceCoordinator = workspaceCoordinator,
            ThreadRuntimeEventCoordinator = threadRuntimeEventCoordinator,
            ThreadPromptQueueCoordinator = threadPromptQueueCoordinator,
            ThreadCommandCoordinator = threadCommandCoordinator,
            ThreadCreationCoordinator = threadCreationCoordinator,
            PromptDraftUiCoordinator = promptDraftUiCoordinator,
            ShellViewModel = shellViewModel,
            SidebarViewModel = sidebarViewModel,
            ThreadWorkspaceViewModel = threadWorkspaceViewModel,
            PromptComposerViewModel = promptComposerViewModel,
            SessionUsageViewModel = sessionUsageViewModel,
            ChatBackendStates = chatBackendStates,
            SidebarCoordinator = sidebarCoordinator,
            NavigatorActionCoordinator = navigatorActionCoordinator,
            ChatSelectorCoordinator = chatSelectorCoordinator,
            ChatPreferenceContext = chatPreferenceContext,
            ChatSelectorStateContext = chatSelectorStateContext,
            ShellWorkspaceContext = shellWorkspaceContext,
            ThreadSelectionContext = threadSelectionContext,
            WorkspaceRefreshContext = workspaceRefreshContext,
        };
    }
}
