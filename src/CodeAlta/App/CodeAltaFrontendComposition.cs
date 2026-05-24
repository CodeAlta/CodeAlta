using CodeAlta.Agent;
using CodeAlta.App.Context;
using CodeAlta.App.Events;
using CodeAlta.App.State;
using CodeAlta.Catalog;
using CodeAlta.LiveTool;
using CodeAlta.Models;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Presentation.Chat;
using CodeAlta.Presentation.Workspace;
using CodeAlta.Threading;
using CodeAlta.ViewModels;
using CodeAlta.Views;

namespace CodeAlta.App;

internal sealed class CodeAltaFrontendComposition
{
    public required ModelProviderPreferenceCoordinator ModelProviderPreferences { get; init; }
    public required CodeAltaShellController ShellController { get; init; }
    public required RuntimeEventPump RuntimeEventPump { get; init; }
    public required TerminalLoopCoordinator TerminalLoopCoordinator { get; init; }
    public required ChatBackendInitializationCoordinator ChatBackendInitializationCoordinator { get; init; }
    public required ShellThreadStateCoordinator ThreadStateCoordinator { get; init; }
    public required DraftTabReplacementPort DraftTabReplacement { get; init; }
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
    public required ModelProviderSelectorCoordinator ModelProviderSelectorCoordinator { get; init; }
    public required ModelCatalogCoordinator ModelCatalogCoordinator { get; init; }
    public required IModelProviderPreferencePort ModelProviderPreferencePort { get; init; }
    public required ModelProviderSelectorStateStore ModelProviderSelectorStateStore { get; init; }
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
        CodeAltaApp frontend,
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
        var draftTabReplacement = new DraftTabReplacementPort();
        var legacyPromptSessionId = new PromptSessionId("legacy-selected-prompt");
        var promptSessionPort = new LegacyPromptSessionPort(
            uiDispatcher,
            frontend.IsPromptTextEmpty,
            frontend.ClearPromptText,
            frontend.RestorePromptText,
            frontend.SnapshotPromptImages,
            frontend.RestorePromptImages,
            frontend.GetPromptText,
            () => frontendEvents.Publish(new PromptAvailabilityChangedEvent()),
            frontend.UpdatePromptImageAttachmentsUi);
        var sessionLoadableBackendIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sessionLoadableBackendIdsGate = new object();
        knownProjectImporter.ShouldLoadProviderSessions = ShouldLoadProviderSessions;
        var configStore = new CodeAltaConfigStore(catalogOptions);
        var modelProviderPreferences = new ModelProviderPreferenceCoordinator(configStore, CodeAlta.Views.CodeAltaApp.UiLogger);
        var altaToolBackendIds = ResolveAltaToolBackendIds(configStore);
        var altaServices = new AltaServiceCollection()
            .Add(catalogOptions)
            .Add(projectCatalog)
            .Add(threadCatalog)
            .Add(runtimeService)
            .Add(runtimeService.SkillCatalog)
            .Add(agentHub)
            .Add(projectFileSearchService)
            .Add<IReadOnlyList<AgentBackendDescriptor>>(backendDescriptors)
            .Add<IAltaSessionToolBackendPolicy>(new AltaSessionToolBackendPolicy(altaToolBackendIds));
        if (pluginHostBridge?.Runtime is { } pluginRuntime)
        {
            altaServices.Add<IAltaPluginCatalog>(new RuntimeAltaPluginCatalog(pluginRuntime));
        }

        var altaRegistry = new AltaCommandRegistry();
        var altaDispatcher = new AltaCommandDispatcher(altaRegistry, altaServices);
        pluginHostBridge?.Alta?.SetDispatcher(altaDispatcher);
        altaServices
            .Add(altaRegistry)
            .Add(altaDispatcher);
        _ = CoordinatorAgentsBootstrapper.Ensure(
            catalogOptions.GlobalRoot,
            AltaHelpText.RenderRootHelp(altaRegistry, altaServices));

        var threadPromptDraftService = new ThreadPromptDraftService(frontend.LoadPromptDraft, frontend.DeletePromptDraft);
        var threadModelProviderPreferenceService = new ThreadModelProviderPreferenceService(frontend.ApplyThreadPreference, frontend.RememberThreadPreference);
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
            new ThreadTimelineSurface(() => frontend.ThreadPaneLayout?.GetAbsoluteBounds()),
            threadPromptDraftService,
            threadModelProviderPreferenceService,
            new ThreadModelProviderReadinessService(thread => frontend.IsModelProviderReady(new AgentBackendId(thread.BackendId))),
            new ThreadHistoryLoaderService(frontend.EnsureThreadHistoryLoadedAsync),
            new ThreadStateTabLifecycleService(
                () => frontend.GetShellTabs()
                    .Where(static tab => tab.Kind == ShellTabKind.Thread)
                    .Select(static tab => tab.TabId.Value)
                    .ToArray(),
                frontend.ResetPendingThreadTabSelection,
                draftTabReplacement.ReplaceDraftTabWithThread,
                frontend.RemoveThreadTabPage),
            frontendEvents);
        var threadSelectionContext = new ThreadSelectionContext(
            threadStateCoordinator,
            frontend.EnsureThreadHistoryLoadedAsync,
            frontend.IsSelectedThread);
        var promptDraftUiCoordinator = new PromptDraftUiCoordinator(
            new PromptDraftCoordinator(),
            catalogOptions,
            () => threadStateCoordinator.Selection,
            frontendEvents,
            frontend.UpdatePromptImageAttachmentsUi);
        var modelProviderSelectorStateContext = new ModelProviderSelectorStateStore(threadWorkspaceViewModel, uiDispatcher);
        var modelProviderPreferencePort = new FrontendModelProviderPreferencePort(
            frontend.ApplyDraftModelProviderPreference,
            frontend.ApplyThreadPreference,
            frontend.RememberGlobalModelProviderPreference,
            frontend.RememberThreadPreference);
        var workspaceRefreshContext = new WorkspaceRefreshContext(request =>
        {
            switch (request.Reason)
            {
                case WorkspaceRefreshReason.SelectedSessionUsageInvalidated:
                    frontend.ApplySessionUsageProjection();
                    break;
                case WorkspaceRefreshReason.HeaderAndThreadWorkspace:
                    frontendEvents.Publish(new HeaderChangedEvent());
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
            () => frontend.ThreadInput,
            () => frontendEvents.Publish(new CatalogChangedEvent()),
            frontend.SetStatus,
            frontend.SetReadyStatusForCurrentSelection);
        var threadProviderSwitchCoordinator = new ThreadProviderSwitchCoordinator(
            configStore,
            chatBackendStates,
            tab =>
            {
                frontend.ApplyThreadPreference(tab);
                return Task.CompletedTask;
            },
            threadId => runtimeService.DetachThreadSessionAsync(threadId),
            threadStateCoordinator.UpsertRuntimeThread,
            frontend.PersistViewStateAsync);
        var modelProviderSelectorCoordinator = new ModelProviderSelectorCoordinator(
            backendDescriptors,
            threadWorkspaceViewModel,
            promptComposerViewModel,
            chatBackendStates,
            modelProviderSelectorStateContext,
            threadSelectionContext,
            modelProviderPreferencePort,
            workspaceRefreshContext,
            configStore.GetEffectiveDefaultProvider,
            frontend.SyncModelProviderSelectorItems,
            threadProviderSwitchCoordinator.CanSelectThreadProvider,
            (thread, tab, targetBackendId) => threadProviderSwitchCoordinator.SwitchThreadProviderAsync(thread, tab, targetBackendId),
            () => frontendEvents.Publish(new SelectionChangedEvent()),
            () => configStore.LoadGlobalProviderDefinitions(includeDisabled: true)
                .Where(static definition => definition.Enabled != false)
                .Select(static definition => definition.ProviderKey)
                .ToArray(),
            () => modelProviderPreferences.GetDraftModelProviderPreference(
                threadStateCoordinator.ViewState,
                threadStateCoordinator.Selection.Target is WorkspaceTarget.Draft { IsGlobal: true }
                    ? null
                    : threadStateCoordinator.GetSelectedProject()?.Id));
        var modelCatalogCoordinator = new ModelCatalogCoordinator(
            chatBackendStates,
            modelProviderSelectorCoordinator,
            threadStateCoordinator.GetSelectedThread,
            threadStateCoordinator.FindOpenThread,
            modelProviderSelectorCoordinator.GetPreferredModelProviderId,
            () => DialogBoundsResolver.ResolveAppBounds(frontend.ThreadInput),
            () => frontend.ThreadInput,
            frontend.FocusPromptEditor,
            frontend.FocusReasoningSelector,
            (message, tone) => frontend.SetStatus(message, tone: tone));

        ThreadPromptQueueCoordinator? threadPromptQueueCoordinator = null;
        ThreadCommandCoordinator? threadCommandCoordinator = null;

        var shellWorkspaceContext = new ShellWorkspaceContext(
            new DelegatingShellPromptAvailabilityPort(
                modelProviderSelectorCoordinator.GetPreferredModelProviderId,
                () =>
                {
                    var hasStatus = modelProviderSelectorCoordinator.TryGetPromptUnavailableStatus(out var message, out var tone);
                    return (hasStatus, message, tone);
                },
                frontend.HasCurrentPromptDraft),
            new ShellWorkspaceSurfacePort(
                frontend.HasWorkspaceSurface,
                () => frontend.ThreadPaneLayout?.GetAbsoluteBounds(),
                () => frontend.ThreadInput,
                _ => { },
                frontend.FocusPromptTarget,
                _ => frontendEvents.Publish(new CatalogChangedEvent()),
                _ => frontend.RefreshSidebarProjection()),
            new DelegatingShellWorkspaceProjectionPort(
                frontend.EnsureSelectionDefaults,
                frontend.RefreshSidebarProjection,
                frontend.SyncSidebarSelectionToCurrentState,
                () => threadPromptQueueCoordinator!.RefreshSelectedThreadQueueUi(),
                () => frontend.RefreshModelProviderSelectorsForDraftScope(),
                frontend.RefreshModelProviderSelectorsForThread,
                frontend.SyncPromptText,
                frontend.ApplyPromptAvailabilityProjection,
                frontend.SyncActivePromptPanelProjection,
                frontend.SyncThreadTabControl),
            uiDispatcher);
        var workspaceCoordinator = new ShellWorkspaceCoordinator(
            shellViewModel,
            threadWorkspaceViewModel,
            sessionUsageViewModel,
            chatBackendStates,
            threadSelectionContext,
            shellWorkspaceContext);
        threadPromptQueueCoordinator = new ThreadPromptQueueCoordinator(
            threadWorkspaceViewModel,
            threadSelectionContext,
            frontend.DispatchToUi,
            frontend.VerifyBindableAccess,
            (tab, prompt, cancellationToken) => threadCommandCoordinator!.DispatchQueuedPromptAsync(tab, prompt, steer: false, cancellationToken),
            (tab, prompt, cancellationToken) => threadCommandCoordinator!.DispatchQueuedPromptAsync(tab, prompt, steer: true, cancellationToken));
        var chatBackendInitializationCoordinator = new ChatBackendInitializationCoordinator(
            agentHub,
            backendDescriptors,
            chatBackendStates,
            frontend.DispatchToUi,
            frontendEvents,
            frontend.SetProviderSessionLoadStatus,
            SetBackendSessionLoadingEnabled);
        var shellStatusPort = new ShellStatusPort(
            uiDispatcher,
            frontend.SetStatus,
            (thread, message, showSpinner, tone) => frontend.SetThreadStatus(thread, message, showSpinner, tone),
            frontend.ClearThreadStatus,
            frontend.SetProviderSessionLoadStatus);
        var threadRuntimeEventCoordinator = new ThreadRuntimeEventCoordinator(
            shellStateStore,
            threadId => threadStateCoordinator.FindOpenThread(threadId),
            static () => true,
            frontend.IsSelectedThread,
            shellStatusPort,
            (tab, cancellationToken) => threadCommandCoordinator!.DrainQueuedPromptAsync(tab, cancellationToken),
            projectFileSearchService,
            threadStateCoordinator.UpsertRuntimeThread,
            new PluginAgentEventObserver(pluginHostBridge),
            frontendEvents);
        var threadCreationCoordinator = new ThreadCreationCoordinator(
            runtimeService,
            catalogOptions,
            modelProviderSelectorCoordinator.GetPreferredModelProviderId,
            threadSelectionContext.GetSelectedProject,
            () => threadSelectionContext.Selection,
            static () => null,
            (backendId, workingDirectory, projectRoots, sourceThreadIdProvider) => threadCommandCoordinator!.BuildPreferredExecutionOptions(backendId, workingDirectory, projectRoots, sourceThreadIdProvider),
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
            modelProviderSelectorStateContext,
            new ThreadCommandContext(
                new DelegatingThreadLifecycleCommandPort(
                    title => threadCreationCoordinator.CreateGlobalThreadAsync(title),
                    title => threadCreationCoordinator.CreateProjectThreadAsync(title),
                    frontend.PersistViewStateAsync,
                    frontend.RekeyThreadIdentity),
                new ThreadCommandUiPort(
                    uiDispatcher,
                    frontend.TrySetPromptUnavailableStatus,
                    static () => true,
                    frontend.ClearDraftPromptText,
                    frontend.SetReadyStatusForCurrentSelection,
                    () => frontendEvents.Publish(new HeaderChangedEvent()),
                    () => frontendEvents.Publish(new CatalogChangedEvent()),
                    threadRuntimeEventCoordinator.TryRenderInteraction),
                promptSessionPort,
                () => legacyPromptSessionId,
                shellStatusPort),
            threadPromptQueueCoordinator,
            promptComposerViewModel,
            projectFileSearchService,
            pluginHostBridge,
            altaServices,
            altaToolBackendIds,
            frontend.GetAlwaysEnqueue);

        return new CodeAltaFrontendComposition
        {
            ModelProviderPreferences = modelProviderPreferences,
            ShellController = shellController,
            RuntimeEventPump = runtimeEventPump,
            TerminalLoopCoordinator = terminalLoopCoordinator,
            ChatBackendInitializationCoordinator = chatBackendInitializationCoordinator,
            ThreadStateCoordinator = threadStateCoordinator,
            DraftTabReplacement = draftTabReplacement,
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
            ModelProviderSelectorCoordinator = modelProviderSelectorCoordinator,
            ModelCatalogCoordinator = modelCatalogCoordinator,
            ModelProviderPreferencePort = modelProviderPreferencePort,
            ModelProviderSelectorStateStore = modelProviderSelectorStateContext,
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

    private static IReadOnlySet<string> ResolveAltaToolBackendIds(CodeAltaConfigStore configStore)
    {
        ArgumentNullException.ThrowIfNull(configStore);

        var backendIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            AgentBackendIds.OpenAIChat.Value,
            AgentBackendIds.OpenAIResponses.Value,
        };
        foreach (var provider in configStore.LoadGlobalProviderDefinitions())
        {
            if (SupportsHostInjectedTools(provider.ProviderType) && !string.IsNullOrWhiteSpace(provider.ProviderKey))
            {
                backendIds.Add(provider.ProviderKey);
            }
        }

        return backendIds;
    }

    private static bool SupportsHostInjectedTools(string? providerType)
        => string.Equals(providerType, "openai-chat", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(providerType, "openai-responses", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(providerType, "azure-openai", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(providerType, "codex", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(providerType, "copilot", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(providerType, "xai", StringComparison.OrdinalIgnoreCase);

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
