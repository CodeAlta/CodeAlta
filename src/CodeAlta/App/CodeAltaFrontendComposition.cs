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
    public required UserPromptPreferenceCoordinator UserPromptPreferences { get; init; }
    public required CodeAltaShellController ShellController { get; init; }
    public required RuntimeEventPump RuntimeEventPump { get; init; }
    public required TerminalLoopCoordinator TerminalLoopCoordinator { get; init; }
    public required ModelProviderInitializationCoordinator ModelProviderInitializationCoordinator { get; init; }
    public required ShellSessionStateCoordinator SessionStateCoordinator { get; init; }
    public required DraftTabReplacementPort DraftTabReplacement { get; init; }
    public required ShellWorkspaceCoordinator WorkspaceCoordinator { get; init; }
    public required SessionRuntimeEventCoordinator SessionRuntimeEventCoordinator { get; init; }
    public required SessionPromptQueueCoordinator SessionPromptQueueCoordinator { get; init; }
    public required SessionCommandCoordinator SessionCommandCoordinator { get; init; }
    public required SessionCreationCoordinator SessionCreationCoordinator { get; init; }
    public required PromptDraftUiCoordinator PromptDraftUiCoordinator { get; init; }
    public required CodeAltaShellViewModel ShellViewModel { get; init; }
    public required SidebarViewModel SidebarViewModel { get; init; }
    public required SessionWorkspaceViewModel SessionWorkspaceViewModel { get; init; }
    public required PromptComposerViewModel PromptComposerViewModel { get; init; }
    public required SessionUsageViewModel SessionUsageViewModel { get; init; }
    public required Dictionary<string, ModelProviderState> ModelProviderStates { get; init; }
    public required SidebarCoordinator SidebarCoordinator { get; init; }
    public required NavigatorActionCoordinator NavigatorActionCoordinator { get; init; }
    public required ModelProviderSelectorCoordinator ModelProviderSelectorCoordinator { get; init; }
    public required UserPromptSelectorCoordinator UserPromptSelectorCoordinator { get; init; }
    public required ModelCatalogCoordinator ModelCatalogCoordinator { get; init; }
    public required IModelProviderPreferencePort ModelProviderPreferencePort { get; init; }
    public required ModelProviderSelectorStateStore ModelProviderSelectorStateStore { get; init; }
    public required ShellStateStore ShellStateStore { get; init; }
    public required FrontendEventPublisher FrontendEvents { get; init; }
    public required ShellWorkspaceContext ShellWorkspaceContext { get; init; }
    public required SessionSelectionContext SessionSelectionContext { get; init; }
    public required WorkspaceRefreshContext WorkspaceRefreshContext { get; init; }
    public required ReminderUiCoordinator ReminderUiCoordinator { get; init; }
    public required AskModeCoordinator AskModeCoordinator { get; init; }

    public static CodeAltaFrontendComposition Create(
        IReadOnlyList<ModelProviderDescriptor> providerDescriptors,
        ProjectCatalog projectCatalog,
        SessionViewCatalog sessionCatalog,
        SessionRuntimeService runtimeService,
        CatalogOptions catalogOptions,
        AgentHub agentHub,
        IProjectFileSearchService projectFileSearchService,
        ICodeAltaShell shell,
        KnownProjectImporter knownProjectImporter,
        CodeAltaApp frontend,
        ProjectDescriptor? currentProject = null,
        PluginHostBridge? pluginHostBridge = null,
        IModelProviderRegistry? modelProviderRegistry = null,
        IModelProviderInitializationService? modelProviderInitializationService = null)
    {
        ArgumentNullException.ThrowIfNull(projectCatalog);
        ArgumentNullException.ThrowIfNull(sessionCatalog);
        ArgumentNullException.ThrowIfNull(runtimeService);
        ArgumentNullException.ThrowIfNull(catalogOptions);
        ArgumentNullException.ThrowIfNull(agentHub);
        ArgumentNullException.ThrowIfNull(providerDescriptors);
        ArgumentNullException.ThrowIfNull(projectFileSearchService);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(knownProjectImporter);
        ArgumentNullException.ThrowIfNull(frontend);

        var shellViewModel = new CodeAltaShellViewModel();
        var sidebarViewModel = new SidebarViewModel();
        var sessionWorkspaceViewModel = new SessionWorkspaceViewModel();
        var promptComposerViewModel = new PromptComposerViewModel();
        var sessionUsageViewModel = new SessionUsageViewModel();
        var modelProviderStates = ModelProviderPresentation.CreateProviderStates(providerDescriptors);
        modelProviderInitializationService ??= modelProviderRegistry is not null
            ? new ModelProviderInitializationService(modelProviderRegistry)
            : new EmptyModelProviderInitializationService();
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
        var configStore = new CodeAltaConfigStore(catalogOptions);
        var modelProviderPreferences = new ModelProviderPreferenceCoordinator(configStore, CodeAlta.Views.CodeAltaApp.UiLogger);
        var userPromptPreferences = new UserPromptPreferenceCoordinator();
        var altaToolProviderIds = ResolveAltaToolProviderIds(configStore);
        var askService = new AltaAskService();
        askService.QueueChanged += (_, args) => frontendEvents.Publish(new AskQueueChangedEvent(args.SessionId));
        var notesService = new AltaNotesService();
        var altaServices = new AltaServiceCollection()
            .Add(catalogOptions)
            .Add(projectCatalog)
            .Add(sessionCatalog)
            .Add(runtimeService)
            .Add(runtimeService.SkillCatalog)
            .Add(agentHub)
            .Add(modelProviderInitializationService)
            .Add(projectFileSearchService)
            .Add<IAltaAskService>(askService)
            .Add<IAltaNotesService>(notesService)
            .Add<IReadOnlyList<ModelProviderDescriptor>>(providerDescriptors)
            .Add<IAltaSessionToolProviderPolicy>(new AltaSessionToolProviderPolicy(altaToolProviderIds));
        if (modelProviderRegistry is not null)
        {
            altaServices.Add(modelProviderRegistry);
        }
        if (pluginHostBridge?.Runtime is { } pluginRuntime)
        {
            altaServices.Add<IAltaPluginCatalog>(new RuntimeAltaPluginCatalog(pluginRuntime));
            altaServices.AddPluginRuntimeHooks(pluginRuntime);
        }

        var altaRegistry = new AltaCommandRegistry();
        var altaDispatcher = new AltaCommandDispatcher(altaRegistry, altaServices);
        var reminderService = new AltaReminderService(altaServices);
        pluginHostBridge?.Alta?.SetDispatcher(altaDispatcher);
        altaServices
            .Add(altaRegistry)
            .Add(altaDispatcher)
            .Add(reminderService);
        _ = CoordinatorAgentsBootstrapper.Ensure(
            catalogOptions.GlobalRoot,
            AltaHelpText.RenderRootHelp(altaRegistry, altaServices));

        var sessionPromptDraftService = new SessionPromptDraftService(frontend.LoadPromptDraft, frontend.DeletePromptDraft);
        var sessionModelProviderPreferenceService = new SessionModelProviderPreferenceService(frontend.ApplySessionPreference, frontend.RememberSessionPreference);
        var shellController = new CodeAltaShellController(
            shell,
            knownProjectImporter,
            new ProjectCatalogStore(projectCatalog),
            new RecoverableSessionSource(runtimeService),
            new SessionDeleter(runtimeService),
            providerDescriptors);
        var runtimeEventPump = new RuntimeEventPump(runtimeService, shellController);
        var terminalLoopCoordinator = new TerminalLoopCoordinator(
            shellController,
            runtimeEventPump,
            uiDispatcher,
            frontend.ApplyPendingSidebarSelection);
        var sessionStateCoordinator = new ShellSessionStateCoordinator(
            projectCatalog,
            sessionCatalog,
            uiDispatcher,
            shellStateStore,
            new SessionTimelineSurface(() => frontend.SessionPaneLayout?.GetAbsoluteBounds()),
            sessionPromptDraftService,
            sessionModelProviderPreferenceService,
            new SessionModelProviderReadinessService(session => frontend.IsModelProviderReady(new ModelProviderId(session.ProviderId))),
            new SessionHistoryLoaderService(frontend.EnsureSessionHistoryLoadedAsync),
            new SessionStateTabLifecycleService(
                () => frontend.GetShellTabs()
                    .Where(static tab => tab.Kind == ShellTabKind.Session)
                    .Select(static tab => tab.TabId.Value)
                    .ToArray(),
                frontend.ResetPendingSessionTabSelection,
                draftTabReplacement.ReplaceDraftTabWithSession,
                frontend.RemoveSessionTabPage),
            frontendEvents,
            currentProject);
        var sessionSelectionContext = new SessionSelectionContext(
            sessionStateCoordinator,
            frontend.EnsureSessionHistoryLoadedAsync,
            frontend.IsSelectedSession);
        var promptDraftUiCoordinator = new PromptDraftUiCoordinator(
            new PromptDraftCoordinator(),
            catalogOptions,
            () => sessionStateCoordinator.Selection,
            frontendEvents,
            frontend.UpdatePromptImageAttachmentsUi);
        var modelProviderSelectorStateContext = new ModelProviderSelectorStateStore(sessionWorkspaceViewModel, uiDispatcher);
        var modelProviderPreferencePort = new FrontendModelProviderPreferencePort(
            frontend.ApplyDraftModelProviderPreference,
            frontend.ApplySessionPreference,
            frontend.RememberGlobalModelProviderPreference,
            frontend.RememberSessionPreference);
        var workspaceRefreshContext = new WorkspaceRefreshContext(request =>
        {
            switch (request.Reason)
            {
                case WorkspaceRefreshReason.SelectedSessionUsageInvalidated:
                    frontend.ApplySessionUsageProjection();
                    break;
                case WorkspaceRefreshReason.HeaderAndSessionWorkspace:
                    frontendEvents.Publish(new HeaderChangedEvent());
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(request), request.Reason, "Unknown workspace refresh reason.");
            }
        });
        var resolveProviderDisplayName = CreateProviderDisplayNameResolver(providerDescriptors);
        var (navigatorActionCoordinator, sidebarCoordinator) = SidebarServicesFactory.Create(
            sidebarViewModel,
            catalogOptions,
            shellController,
            sessionStateCoordinator,
            notesService,
            resolveProviderDisplayName,
            () => frontend.SessionInput,
            () => frontendEvents.Publish(new CatalogChangedEvent()),
            frontend.SetStatus,
            frontend.SetReadyStatusForCurrentSelection);
        notesService.Changed += (_, args) => UiDispatch.Post(uiDispatcher, () => sidebarCoordinator.View.SetNotesMarkdown(args.Markdown));
        var reminderUiCoordinator = new ReminderUiCoordinator(
            reminderService,
            new ReminderUiCoordinatorPort
            {
                GetSelectedSession = sessionStateCoordinator.GetSelectedSession,
                GetDialogBounds = () => DialogBoundsResolver.ResolveAppBounds(frontend.SessionInput is { } input ? input : sidebarCoordinator.View.Tree),
                GetFocusTarget = () => frontend.SessionInput is { } input ? input : sidebarCoordinator.View.Tree,
                SetStatus = (message, tone) => frontend.SetStatus(message, tone: tone),
                DispatchToUi = frontend.DispatchToUi,
                RefreshProjection = frontend.RefreshSidebarProjection,
            });
        var sessionProviderSwitchCoordinator = new SessionProviderSwitchCoordinator(
            configStore,
            modelProviderStates,
            tab =>
            {
                frontend.ApplySessionPreference(tab);
                return Task.CompletedTask;
            },
            sessionId => runtimeService.DetachRuntimeSessionAsync(sessionId),
            sessionStateCoordinator.UpsertRuntimeSession,
            frontend.PersistViewStateAsync);
        var modelProviderSelectorCoordinator = new ModelProviderSelectorCoordinator(
            providerDescriptors,
            sessionWorkspaceViewModel,
            promptComposerViewModel,
            modelProviderStates,
            modelProviderSelectorStateContext,
            sessionSelectionContext,
            modelProviderPreferencePort,
            workspaceRefreshContext,
            configStore.GetEffectiveDefaultProvider,
            frontend.SyncModelProviderSelectorItems,
            sessionProviderSwitchCoordinator.CanSelectSessionProvider,
            (session, tab, targetProviderId) => sessionProviderSwitchCoordinator.SwitchSessionProviderAsync(session, tab, targetProviderId),
            () => frontendEvents.Publish(new SelectionChangedEvent()),
            () => configStore.LoadGlobalProviderDefinitions(includeDisabled: true)
                .Where(static definition => definition.Enabled != false)
                .Select(static definition => definition.ProviderKey)
                .ToArray(),
            () => modelProviderPreferences.GetDraftModelProviderPreference(
                sessionStateCoordinator.ViewState,
                sessionStateCoordinator.Selection.Target is WorkspaceTarget.Draft { IsGlobal: true }
                    ? null
                    : sessionStateCoordinator.GetSelectedProject()?.Id),
            pluginHostBridge is null ? null : pluginHostBridge.GetPromptPlaceholderContributions);
        var userPromptSelectorCoordinator = new UserPromptSelectorCoordinator(
            sessionWorkspaceViewModel,
            catalogOptions,
            sessionSelectionContext,
            userPromptPreferences,
            workspaceRefreshContext,
            () => sessionStateCoordinator.ViewState,
            () => _ = frontend.PersistViewStateAsync(),
            session => _ = sessionStateCoordinator.PersistSessionLocalStateAsync(session),
            frontend.SyncUserPromptSelectorItems,
            frontend.SetStatus);
        var modelCatalogCoordinator = new ModelCatalogCoordinator(
            modelProviderStates,
            modelProviderSelectorCoordinator,
            sessionStateCoordinator.GetSelectedSession,
            sessionStateCoordinator.FindOpenSession,
            modelProviderSelectorCoordinator.GetPreferredModelProviderId,
            () => DialogBoundsResolver.ResolveAppBounds(frontend.SessionInput),
            () => frontend.SessionInput,
            frontend.FocusPromptEditor,
            frontend.FocusReasoningSelector,
            (message, tone) => frontend.SetStatus(message, tone: tone));

        SessionPromptQueueCoordinator? sessionPromptQueueCoordinator = null;
        SessionCommandCoordinator? sessionCommandCoordinator = null;

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
                () => frontend.SessionPaneLayout?.GetAbsoluteBounds(),
                () => frontend.SessionInput,
                _ => { },
                frontend.FocusPromptTarget,
                _ => frontendEvents.Publish(new CatalogChangedEvent()),
                _ => frontend.RefreshSidebarProjection()),
            new DelegatingShellWorkspaceProjectionPort(
                frontend.EnsureSelectionDefaults,
                frontend.RefreshSidebarProjection,
                frontend.SyncSidebarSelectionToCurrentState,
                () => sessionPromptQueueCoordinator!.RefreshSelectedSessionQueueUi(),
                () => frontend.RefreshModelProviderSelectorsForDraftScope(),
                frontend.RefreshModelProviderSelectorsForSession,
                frontend.SyncPromptText,
                frontend.ApplyPromptAvailabilityProjection,
                frontend.SyncActivePromptPanelProjection,
                frontend.SyncSessionTabControl),
            uiDispatcher);
        var workspaceCoordinator = new ShellWorkspaceCoordinator(
            shellViewModel,
            sessionWorkspaceViewModel,
            sessionUsageViewModel,
            modelProviderStates,
            sessionSelectionContext,
            shellWorkspaceContext);
        sessionPromptQueueCoordinator = new SessionPromptQueueCoordinator(
            sessionWorkspaceViewModel,
            sessionSelectionContext,
            frontend.DispatchToUi,
            frontend.VerifyBindableAccess,
            (tab, prompt, cancellationToken) => sessionCommandCoordinator!.DispatchQueuedPromptAsync(tab, prompt, steer: false, cancellationToken),
            (tab, prompt, cancellationToken) => sessionCommandCoordinator!.DispatchQueuedPromptAsync(tab, prompt, steer: true, cancellationToken));
        var modelProviderInitializationCoordinator = new ModelProviderInitializationCoordinator(
            modelProviderInitializationService,
            providerDescriptors,
            modelProviderStates,
            frontend.DispatchToUi,
            frontendEvents,
            frontend.SetProviderSessionLoadStatus);
        var shellStatusPort = new ShellStatusPort(
            uiDispatcher,
            frontend.SetStatus,
            (session, message, showSpinner, tone) => frontend.SetSessionStatus(session, message, showSpinner, tone),
            frontend.ClearSessionStatus,
            frontend.SetProviderSessionLoadStatus);
        var sessionRuntimeEventCoordinator = new SessionRuntimeEventCoordinator(
            shellStateStore,
            sessionId => sessionStateCoordinator.FindOpenSession(sessionId),
            static () => true,
            frontend.IsSelectedSession,
            shellStatusPort,
            (tab, cancellationToken) => sessionCommandCoordinator!.DrainQueuedPromptAsync(tab, cancellationToken),
            projectFileSearchService,
            sessionStateCoordinator.UpsertRuntimeSession,
            new PluginAgentEventObserver(pluginHostBridge),
            frontendEvents);
        var sessionCreationCoordinator = new SessionCreationCoordinator(
            runtimeService,
            catalogOptions,
            modelProviderSelectorCoordinator.GetPreferredModelProviderId,
            sessionSelectionContext.GetSelectedProject,
            () => sessionSelectionContext.Selection,
            static () => null,
            project => projectCatalog.EnsurePersistedAsync(project, CancellationToken.None),
            frontend.UpsertProject,
            (providerId, workingDirectory, projectRoots, sourceSessionIdProvider) => sessionCommandCoordinator!.BuildPreferredExecutionOptions(providerId, workingDirectory, projectRoots, sourceSessionIdProvider),
            frontend.RememberSessionPreference,
            frontend.RegisterCreatedSessionAsync,
            static () => { },
            frontend.SetStatus);
        sessionCommandCoordinator = new SessionCommandCoordinator(
            runtimeService,
            catalogOptions,
            providerDescriptors,
            modelProviderStates,
            sessionSelectionContext,
            modelProviderSelectorStateContext,
            new ShellSessionCommandContext(
                new DelegatingSessionLifecycleCommandPort(
                    title => sessionCreationCoordinator.CreateGlobalSessionAsync(title),
                    title => sessionCreationCoordinator.CreateProjectSessionAsync(title),
                    frontend.PersistViewStateAsync,
                    frontend.RekeySessionIdentity),
                new SessionCommandUiPort(
                    uiDispatcher,
                    frontend.TrySetPromptUnavailableStatus,
                    static () => true,
                    frontend.ClearDraftPromptText,
                    frontend.SetReadyStatusForCurrentSelection,
                    () => frontendEvents.Publish(new HeaderChangedEvent()),
                    () => frontendEvents.Publish(new CatalogChangedEvent()),
                    sessionRuntimeEventCoordinator.TryRenderInteraction),
                promptSessionPort,
                () => legacyPromptSessionId,
                shellStatusPort),
            sessionPromptQueueCoordinator,
            promptComposerViewModel,
            projectFileSearchService,
            pluginHostBridge,
            altaServices,
            altaToolProviderIds,
            frontend.GetAlwaysEnqueue,
            userPromptSelectorCoordinator.GetPreferredUserPromptName);
        var askModeCoordinator = new AskModeCoordinator(
            askService,
            sessionStateCoordinator,
            sessionCommandCoordinator,
            frontendEvents,
            sessionWorkspaceViewModel,
            frontend.SetStatus);

        return new CodeAltaFrontendComposition
        {
            ModelProviderPreferences = modelProviderPreferences,
            UserPromptPreferences = userPromptPreferences,
            ShellController = shellController,
            RuntimeEventPump = runtimeEventPump,
            TerminalLoopCoordinator = terminalLoopCoordinator,
            ModelProviderInitializationCoordinator = modelProviderInitializationCoordinator,
            SessionStateCoordinator = sessionStateCoordinator,
            DraftTabReplacement = draftTabReplacement,
            WorkspaceCoordinator = workspaceCoordinator,
            SessionRuntimeEventCoordinator = sessionRuntimeEventCoordinator,
            SessionPromptQueueCoordinator = sessionPromptQueueCoordinator,
            SessionCommandCoordinator = sessionCommandCoordinator,
            SessionCreationCoordinator = sessionCreationCoordinator,
            PromptDraftUiCoordinator = promptDraftUiCoordinator,
            ShellViewModel = shellViewModel,
            SidebarViewModel = sidebarViewModel,
            SessionWorkspaceViewModel = sessionWorkspaceViewModel,
            PromptComposerViewModel = promptComposerViewModel,
            SessionUsageViewModel = sessionUsageViewModel,
            ModelProviderStates = modelProviderStates,
            SidebarCoordinator = sidebarCoordinator,
            NavigatorActionCoordinator = navigatorActionCoordinator,
            ModelProviderSelectorCoordinator = modelProviderSelectorCoordinator,
            UserPromptSelectorCoordinator = userPromptSelectorCoordinator,
            ModelCatalogCoordinator = modelCatalogCoordinator,
            ModelProviderPreferencePort = modelProviderPreferencePort,
            ModelProviderSelectorStateStore = modelProviderSelectorStateContext,
            ShellStateStore = shellStateStore,
            FrontendEvents = frontendEvents,
            ShellWorkspaceContext = shellWorkspaceContext,
            SessionSelectionContext = sessionSelectionContext,
            WorkspaceRefreshContext = workspaceRefreshContext,
            ReminderUiCoordinator = reminderUiCoordinator,
            AskModeCoordinator = askModeCoordinator,
        };

    }

    private sealed class EmptyModelProviderInitializationService : IModelProviderInitializationService
    {
        public IReadOnlyList<ModelProviderStateSnapshot> CurrentStates => [];

        public async IAsyncEnumerable<ModelProviderStateChanged> StreamStateChangesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task InitializeAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RefreshProviderAsync(ModelProviderId providerId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<AgentModelInfo>> GetModelsAsync(ModelProviderId providerId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AgentModelInfo>>([]);
    }

    private static IReadOnlySet<string> ResolveAltaToolProviderIds(CodeAltaConfigStore configStore)
    {
        ArgumentNullException.ThrowIfNull(configStore);

        var ProviderIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ModelProviderIds.OpenAIChat.Value,
            ModelProviderIds.OpenAIResponses.Value,
        };
        foreach (var provider in configStore.LoadGlobalProviderDefinitions())
        {
            if (SupportsHostInjectedTools(provider.ProviderType) && !string.IsNullOrWhiteSpace(provider.ProviderKey))
            {
                ProviderIds.Add(provider.ProviderKey);
            }
        }

        return ProviderIds;
    }

    private static bool SupportsHostInjectedTools(string? providerType)
        => string.Equals(providerType, "openai-chat", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(providerType, "openai-responses", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(providerType, "azure-openai", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(providerType, "codex", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(providerType, "copilot", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(providerType, "xai", StringComparison.OrdinalIgnoreCase);

    private static Func<string?, string> CreateProviderDisplayNameResolver(
        IReadOnlyList<ModelProviderDescriptor> providerDescriptors)
    {
        ArgumentNullException.ThrowIfNull(providerDescriptors);

        var displayNames = providerDescriptors.ToDictionary(
            static descriptor => descriptor.ProviderId.Value,
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

            return Presentation.Sidebar.SidebarSessionPresentation.ResolveProviderDisplayName(providerKey);
        };
    }
}
