using CodeAlta.Agent;
using CodeAlta.App.Context;
using CodeAlta.App.Events;
using CodeAlta.App.State;
using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Presentation.Chat;
using CodeAlta.Presentation.Workspace;
using CodeAlta.Threading;
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
    public required IModelProviderPreferencePort ModelProviderPreferencePort { get; init; }
    public required ChatSelectorStateStore ChatSelectorStateStore { get; init; }
    public required ShellStateStore ShellStateStore { get; init; }
    public required FrontendEventPublisher FrontendEvents { get; init; }
    public required ShellWorkspaceContext ShellWorkspaceContext { get; init; }
    public required ThreadSelectionContext ThreadSelectionContext { get; init; }
    public required WorkspaceRefreshContext WorkspaceRefreshContext { get; init; }

    public static CodeAltaFrontendComposition Create(
        IReadOnlyList<AgentBackendDescriptor> backendDescriptors,
        ProjectCatalog projectCatalog,
        WorkThreadCatalog threadCatalog,
        WorkThreadRuntimeService runtimeService,
        CatalogOptions catalogOptions,
        AgentHub agentHub,
        IProjectFileSearchService projectFileSearchService,
        ICodeAltaShell shell,
        KnownProjectImporter knownProjectImporter,
        State<float> welcomePhase01,
        ICodeAltaFrontendServices frontend,
        CodexInstallProgressReporter? codexInstallProgress = null,
        PluginHostBridge? pluginHostBridge = null)
    {
        ArgumentNullException.ThrowIfNull(projectCatalog);
        ArgumentNullException.ThrowIfNull(threadCatalog);
        ArgumentNullException.ThrowIfNull(runtimeService);
        ArgumentNullException.ThrowIfNull(catalogOptions);
        ArgumentNullException.ThrowIfNull(agentHub);
        ArgumentNullException.ThrowIfNull(backendDescriptors);
        ArgumentNullException.ThrowIfNull(projectFileSearchService);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(knownProjectImporter);
        ArgumentNullException.ThrowIfNull(welcomePhase01);
        ArgumentNullException.ThrowIfNull(frontend);

        var shellViewModel = new CodeAltaShellViewModel();
        var sidebarViewModel = new SidebarViewModel();
        var threadWorkspaceViewModel = new ThreadWorkspaceViewModel();
        var promptComposerViewModel = new PromptComposerViewModel();
        var sessionUsageViewModel = new SessionUsageViewModel();
        var chatBackendStates = ChatBackendPresentation.CreateBackendStates(backendDescriptors);
        var uiDispatcher = frontend.GetUiDispatcher();
        var shellStateStore = new ShellStateStore(uiDispatcher);
        var frontendEvents = new FrontendEventPublisher(uiDispatcher);
        var legacyPromptSessionId = new PromptSessionId("legacy-selected-prompt");
        var promptSessionPort = new LegacyPromptSessionPort(
            uiDispatcher,
            frontend.IsPromptTextEmpty,
            frontend.ClearPromptText,
            frontend.RestorePromptText,
            frontend.SnapshotPromptImages,
            frontend.RestorePromptImages,
            frontend.UpdatePromptAvailabilityUi,
            frontend.UpdatePromptImageAttachmentsUi);
        var sessionLoadableBackendIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sessionLoadableBackendIdsGate = new object();
        knownProjectImporter.ShouldLoadProviderSessions = ShouldLoadProviderSessions;
        var configStore = new CodeAltaConfigStore(catalogOptions);
        var backendPreferences = new ChatBackendPreferenceCoordinator(configStore, CodeAlta.Views.CodeAltaApp.UiLogger);
        var shellController = new CodeAltaShellController(
            shell,
            knownProjectImporter,
            new ProjectCatalogStore(projectCatalog),
            new RecoverableThreadSource(runtimeService) { ShouldListBackendSessions = ShouldLoadProviderSessions },
            new WorkThreadDeleter(runtimeService),
            backendDescriptors);
        var runtimeEventPump = new RuntimeEventPump(runtimeService, shellController);
        var terminalLoopCoordinator = new TerminalLoopCoordinator(
            shellController,
            runtimeEventPump,
            uiDispatcher,
            frontend.ApplyPendingSidebarSelection);
        var threadStateCoordinator = new ShellThreadStateCoordinator(
            projectCatalog,
            threadCatalog,
            uiDispatcher,
            shellStateStore,
            frontend.GetThreadPaneBounds,
            thread => frontend.IsChatBackendReady(new AgentBackendId(thread.BackendId)),
            frontend.LoadPromptDraft,
            frontend.DeletePromptDraft,
            frontend.ApplyThreadPreference,
            frontend.RememberThreadPreference,
            frontend.EnsureThreadHistoryLoadedAsync,
            frontend.ResetPendingThreadTabSelection,
            frontend.RemoveThreadTabPage,
            frontend.SetStatus,
            frontendEvents);
        var threadSelectionContext = new ThreadSelectionContext(
            threadStateCoordinator,
            frontend.EnsureThreadHistoryLoadedAsync,
            frontend.IsSelectedThread);
        var promptDraftUiCoordinator = new PromptDraftUiCoordinator(
            new PromptDraftCoordinator(),
            catalogOptions,
            () => threadStateCoordinator.Selection,
            frontend.RefreshCatalogAndThreadWorkspace,
            frontend.UpdatePromptImageAttachmentsUi);
        var chatSelectorStateContext = new ChatSelectorStateStore(threadWorkspaceViewModel, uiDispatcher);
        var modelProviderPreferencePort = new FrontendModelProviderPreferencePort(
            frontend.ApplyDraftBackendPreference,
            frontend.ApplyThreadPreference,
            frontend.RememberGlobalBackendPreference,
            frontend.RememberThreadPreference);
        var workspaceRefreshContext = new WorkspaceRefreshContext(request =>
        {
            switch (request.Reason)
            {
                case WorkspaceRefreshReason.SelectedSessionUsageInvalidated:
                    frontend.InvalidateSelectedSessionUsage();
                    break;
                case WorkspaceRefreshReason.HeaderAndThreadWorkspace:
                    frontend.RefreshHeaderAndThreadWorkspace();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(request), request.Reason, "Unknown workspace refresh reason.");
            }
        });
        var resolveProviderDisplayName = CreateProviderDisplayNameResolver(backendDescriptors);
        var (navigatorActionCoordinator, sidebarCoordinator) = SidebarServicesFactory.Create(
            sidebarViewModel,
            catalogOptions,
            shellController,
            threadStateCoordinator,
            resolveProviderDisplayName,
            frontend.GetPromptFocusTarget,
            frontend.RefreshCatalogAndThreadWorkspace,
            frontend.SetStatus,
            frontend.SetReadyStatusForCurrentSelection);
        var threadProviderSwitchCoordinator = new ThreadProviderSwitchCoordinator(
            catalogOptions,
            threadCatalog,
            configStore,
            chatBackendStates,
            tab =>
            {
                frontend.ApplyThreadPreference(tab);
                return Task.CompletedTask;
            },
            threadId => runtimeService.DetachThreadSessionAsync(threadId),
            threadStateCoordinator.RekeyThreadIdentity,
            frontend.PersistViewStateAsync,
            runtimeService.GetHistoryAsync);
        var chatSelectorCoordinator = new ChatSelectorCoordinator(
            backendDescriptors,
            threadWorkspaceViewModel,
            promptComposerViewModel,
            chatBackendStates,
            chatSelectorStateContext,
            threadSelectionContext,
            modelProviderPreferencePort,
            workspaceRefreshContext,
            configStore.GetEffectiveDefaultProvider,
            frontend.SyncChatSelectorItems,
            threadProviderSwitchCoordinator.CanSelectThreadProvider,
            (thread, tab, targetBackendId) => threadProviderSwitchCoordinator.SwitchThreadProviderAsync(thread, tab, targetBackendId),
            frontend.RefreshSelectionAndThreadWorkspace,
            () => configStore.LoadGlobalProviderDefinitions(includeDisabled: true)
                .Where(static definition => definition.Enabled != false)
                .Select(static definition => definition.ProviderKey)
                .ToArray());

        ThreadPromptQueueCoordinator? threadPromptQueueCoordinator = null;
        ThreadCommandCoordinator? threadCommandCoordinator = null;

        var shellWorkspaceContext = new ShellWorkspaceContext(
            new DelegatingShellPromptAvailabilityPort(
                chatSelectorCoordinator.GetPreferredBackendId,
                () =>
                {
                    var hasStatus = chatSelectorCoordinator.TryGetPromptUnavailableStatus(out var message, out var tone);
                    return (hasStatus, message, tone);
                }),
            new ShellWorkspaceSurfacePort(
                frontend.HasWorkspaceSurface,
                frontend.GetThreadPaneBounds,
                frontend.GetPromptFocusTarget,
                _ => { },
                frontend.FocusPromptTarget,
                _ => frontend.RefreshCatalogAndThreadWorkspace(),
                _ => frontend.RefreshSidebarProjection()),
            new DelegatingShellWorkspaceProjectionPort(
                frontend.EnsureSelectionDefaults,
                frontend.RefreshSidebarProjection,
                frontend.SyncSidebarSelectionToCurrentState,
                () => threadPromptQueueCoordinator!.RefreshSelectedThreadQueueUi(),
                frontend.RefreshChatSelectorsForDraftScope,
                frontend.RefreshChatSelectorsForThread,
                frontend.SyncPromptText,
                frontend.UpdatePromptAvailabilityUi,
                frontend.SyncThreadTabControl),
            uiDispatcher);
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
            frontend.UpdatePromptAvailabilityUi,
            frontend.DispatchToUi,
            frontend.VerifyBindableAccess,
            (tab, prompt, cancellationToken) => threadCommandCoordinator!.DispatchQueuedPromptAsync(tab, prompt, steer: false, cancellationToken),
            (tab, prompt, cancellationToken) => threadCommandCoordinator!.DispatchQueuedPromptAsync(tab, prompt, steer: true, cancellationToken));
        var chatBackendInitializationCoordinator = new ChatBackendInitializationCoordinator(
            agentHub,
            backendDescriptors,
            chatBackendStates,
            frontend.DispatchToUi,
            frontend.RefreshHeaderAndThreadWorkspace,
            codexInstallProgress,
            frontend.SetProviderSessionLoadStatus,
            SetBackendSessionLoadingEnabled);
        var threadRuntimeEventCoordinator = new ThreadRuntimeEventCoordinator(
            threadId => threadStateCoordinator.FindThread(threadId),
            threadId => threadStateCoordinator.FindOpenThread(threadId),
            frontend.GetAutoApproveEnabled,
            frontend.IsSelectedThread,
            frontend.InvalidateSelectedSessionUsage,
            frontend.RefreshShellChrome,
            frontend.SetStatus,
            frontend.SetThreadStatus,
            frontend.ClearThreadStatus,
            () => threadPromptQueueCoordinator!.RefreshSelectedThreadQueueUi(),
            (tab, cancellationToken) => threadCommandCoordinator!.DrainQueuedPromptAsync(tab, cancellationToken),
            projectFileSearchService,
            pluginHostBridge);
        var threadCreationCoordinator = new ThreadCreationCoordinator(
            runtimeService,
            catalogOptions,
            chatSelectorCoordinator.GetPreferredBackendId,
            threadSelectionContext.GetSelectedProject,
            () => threadSelectionContext.Selection,
            static () => null,
            (backendId, workingDirectory, projectRoots) => threadCommandCoordinator!.BuildPreferredExecutionOptions(backendId, workingDirectory, projectRoots),
            frontend.RememberThreadPreference,
            frontend.RegisterCreatedThreadAsync,
            static () => { },
            frontend.SetStatus);
        threadCommandCoordinator = new ThreadCommandCoordinator(
            runtimeService,
            catalogOptions,
            backendDescriptors,
            chatBackendStates,
            threadSelectionContext,
            chatSelectorStateContext,
            new ThreadCommandContext(
                new DelegatingThreadLifecycleCommandPort(
                    title => threadCreationCoordinator.CreateGlobalThreadAsync(title),
                    title => threadCreationCoordinator.CreateProjectThreadAsync(title),
                    frontend.PersistViewStateAsync,
                    frontend.RekeyThreadIdentity),
                new ThreadCommandUiPort(
                    uiDispatcher,
                    frontend.TrySetPromptUnavailableStatus,
                    frontend.GetAutoApproveEnabled,
                    frontend.ClearDraftPromptText,
                    frontend.SetReadyStatusForCurrentSelection,
                    frontend.RefreshHeaderAndThreadWorkspace,
                    frontend.RefreshCatalogAndThreadWorkspace,
                    threadRuntimeEventCoordinator.TryRenderInteraction),
                promptSessionPort,
                () => legacyPromptSessionId,
                new ShellStatusPort(
                    uiDispatcher,
                    frontend.SetStatus,
                    frontend.SetThreadStatus,
                    frontend.ClearThreadStatus,
                    frontend.SetProviderSessionLoadStatus)),
            threadPromptQueueCoordinator,
            promptComposerViewModel,
            projectFileSearchService,
            pluginHostBridge);

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
            ModelProviderPreferencePort = modelProviderPreferencePort,
            ChatSelectorStateStore = chatSelectorStateContext,
            ShellStateStore = shellStateStore,
            FrontendEvents = frontendEvents,
            ShellWorkspaceContext = shellWorkspaceContext,
            ThreadSelectionContext = threadSelectionContext,
            WorkspaceRefreshContext = workspaceRefreshContext,
        };

        bool ShouldLoadProviderSessions(AgentBackendId backendId)
        {
            lock (sessionLoadableBackendIdsGate)
            {
                if (sessionLoadableBackendIds.Contains(backendId.Value))
                {
                    return true;
                }
            }

            return chatBackendStates.TryGetValue(backendId.Value, out var state) &&
                   state.Availability == ChatBackendAvailability.Ready;
        }

        void SetBackendSessionLoadingEnabled(AgentBackendId backendId, bool enabled)
        {
            lock (sessionLoadableBackendIdsGate)
            {
                if (enabled)
                {
                    sessionLoadableBackendIds.Add(backendId.Value);
                }
                else
                {
                    sessionLoadableBackendIds.Remove(backendId.Value);
                }
            }
        }
    }

    private static Func<string?, string> CreateProviderDisplayNameResolver(
        IReadOnlyList<AgentBackendDescriptor> backendDescriptors)
    {
        ArgumentNullException.ThrowIfNull(backendDescriptors);

        var displayNames = backendDescriptors.ToDictionary(
            static descriptor => descriptor.BackendId.Value,
            static descriptor => descriptor.DisplayName,
            StringComparer.OrdinalIgnoreCase);
        return providerKey =>
        {
            if (!string.IsNullOrWhiteSpace(providerKey) &&
                displayNames.TryGetValue(providerKey, out var displayName) &&
                !string.IsNullOrWhiteSpace(displayName))
            {
                return displayName;
            }

            return Presentation.Sidebar.SidebarThreadPresentation.ResolveProviderDisplayName(providerKey);
        };
    }
}
