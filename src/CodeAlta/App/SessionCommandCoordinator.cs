using CodeAlta.App.State;
using CodeAlta.Threading;
using CodeAlta.Agent;
using CodeAlta.App.Context;
using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Presentation.Chat;
using CodeAlta.Presentation.Shell;
using CodeAlta.Presentation.Prompting;
using CodeAlta.ViewModels;
using CodeAlta.Views;
using XenoAtom.Logging;
using XenoAtom.Terminal.UI.Controls;

namespace CodeAlta.App;

internal sealed class SessionCommandCoordinator
{
    private readonly SessionRuntimeService _runtimeService;
    private readonly IReadOnlyList<ModelProviderDescriptor> _providerDescriptors;
    private readonly Dictionary<string, ModelProviderState> _modelProviderStates;
    private readonly SessionSelectionContext _sessionSelection;
    private readonly ModelProviderSelectorStateStore _selectorState;
    private readonly ShellSessionCommandContext _commandContext;
    private readonly SessionPromptQueueCoordinator _queueCoordinator;
    private readonly PromptComposerViewModel _promptComposerViewModel;
    private readonly Func<bool> _getAlwaysEnqueue;
    private readonly SessionExecutionOptionsFactory _executionOptionsFactory;
    private readonly SessionPromptDispatchCoordinator _promptDispatchCoordinator;
    private readonly PluginHostBridge? _pluginHostBridge;

    public SessionCommandCoordinator(
        SessionRuntimeService runtimeService,
        CatalogOptions catalogOptions,
        Dictionary<string, ModelProviderState> modelProviderStates,
        SessionSelectionContext sessionSelection,
        ModelProviderSelectorStateStore selectorState,
        ShellSessionCommandContext commandContext,
        SessionPromptQueueCoordinator queueCoordinator,
        PromptComposerViewModel promptComposerViewModel,
        IUiDispatcher uiDispatcher,
        IProjectFileSearchService? projectFileSearchService = null,
        PluginHostBridge? pluginHostBridge = null,
        IServiceProvider? altaServices = null,
        Func<bool>? getAlwaysEnqueue = null,
        Func<string?>? getPreferredAgentPromptId = null)
        : this(
            runtimeService,
            catalogOptions,
            ModelProviderPresentation.CreateProviderStates().Values
                .Select(static state => new ModelProviderDescriptor(state.ProviderId, state.DisplayName))
                .ToArray(),
            modelProviderStates,
            sessionSelection,
            selectorState,
            commandContext,
            queueCoordinator,
            promptComposerViewModel,
            uiDispatcher,
            projectFileSearchService,
            pluginHostBridge,
            altaServices,
            getAlwaysEnqueue,
            getPreferredAgentPromptId)
    {
    }

    public SessionCommandCoordinator(
        SessionRuntimeService runtimeService,
        CatalogOptions catalogOptions,
        IReadOnlyList<ModelProviderDescriptor> providerDescriptors,
        Dictionary<string, ModelProviderState> modelProviderStates,
        SessionSelectionContext sessionSelection,
        ModelProviderSelectorStateStore selectorState,
        ShellSessionCommandContext commandContext,
        SessionPromptQueueCoordinator queueCoordinator,
        PromptComposerViewModel promptComposerViewModel,
        IUiDispatcher uiDispatcher,
        IProjectFileSearchService? projectFileSearchService = null,
        PluginHostBridge? pluginHostBridge = null,
        IServiceProvider? altaServices = null,
        Func<bool>? getAlwaysEnqueue = null,
        Func<string?>? getPreferredAgentPromptId = null)
    {
        ArgumentNullException.ThrowIfNull(runtimeService);
        ArgumentNullException.ThrowIfNull(providerDescriptors);
        ArgumentNullException.ThrowIfNull(modelProviderStates);
        ArgumentNullException.ThrowIfNull(sessionSelection);
        ArgumentNullException.ThrowIfNull(selectorState);
        ArgumentNullException.ThrowIfNull(commandContext);
        ArgumentNullException.ThrowIfNull(queueCoordinator);
        ArgumentNullException.ThrowIfNull(promptComposerViewModel);

        _runtimeService = runtimeService;
        _providerDescriptors = providerDescriptors;
        _modelProviderStates = modelProviderStates;
        _sessionSelection = sessionSelection;
        _selectorState = selectorState;
        _commandContext = commandContext;
        _queueCoordinator = queueCoordinator;
        _promptComposerViewModel = promptComposerViewModel;
        _getAlwaysEnqueue = getAlwaysEnqueue ?? (() => _promptComposerViewModel.AlwaysEnqueue);
        _pluginHostBridge = pluginHostBridge;
        var permissionRequests = new SessionPermissionRequestCoordinator(sessionSelection, commandContext, uiDispatcher);
        var userInputRequests = new SessionUserInputRequestCoordinator(sessionSelection, commandContext);
        _executionOptionsFactory = new SessionExecutionOptionsFactory(catalogOptions, modelProviderStates, sessionSelection, permissionRequests, userInputRequests, getPreferredAgentPromptId, altaServices);
        _promptDispatchCoordinator = new SessionPromptDispatchCoordinator(
            runtimeService,
            _executionOptionsFactory,
            queueCoordinator,
            commandContext,
            catalogOptions,
            projectFileSearchService ?? NullProjectFileSearchService.Instance,
            pluginHostBridge,
            new RuntimeSessionOrchestratorAdapter(runtimeService, sessionSelection.FindSession));
    }

    public async Task SendPromptAsync(
        string? promptText,
        bool steer,
        CancellationToken cancellationToken = default)
    {
        var session = _sessionSelection.GetSelectedSession();
        var hadExistingSession = session is not null;
        var prompt = _commandContext.CaptureSessionInput(promptText);
        if (prompt.Images.Count > 0 && session is null && !CurrentPromptModelSupportsImages(session, null))
        {
            _commandContext.SetShellStatus(SR.T("The selected model does not support image input; remove the prompt images or choose a vision-capable model."), false, StatusTone.Warning);
            return;
        }

        if (session is null)
        {
            if (steer)
            {
                _commandContext.SetShellStatus(SR.T("Start the session before steering it."), false, StatusTone.Warning);
                return;
            }

            if (!prompt.HasContent)
            {
                return;
            }

            if (_commandContext.TrySetPromptUnavailableStatus())
            {
                return;
            }

            var initialSessionTitle = SessionPromptDispatchCoordinator.CreateInitialSessionTitle(prompt);
            session = _sessionSelection.Selection.Target is WorkspaceTarget.Draft { IsGlobal: true }
                ? await _commandContext.CreateGlobalSessionAsync(initialSessionTitle)
                : await _commandContext.CreateProjectSessionAsync(initialSessionTitle);
            if (session is null)
            {
                return;
            }

            _commandContext.ClearDraftInput();
        }
        var tab = _sessionSelection.EnsureSessionTab(session);
        if (!IsModelProviderReady(tab.ProviderId))
        {
            _commandContext.SetReadyStatusForCurrentSelection();
            return;
        }

        if (!prompt.HasContent)
        {
            if (steer && session is not null)
            {
                await _queueCoordinator.ConvertNextQueuedPromptToSteerAsync(tab, cancellationToken);
            }

            return;
        }

        if (prompt.Images.Count > 0 && !CurrentPromptModelSupportsImages(session, tab))
        {
            _commandContext.SetShellStatus(SR.T("The selected model does not support image input; remove the prompt images or choose a vision-capable model."), false, StatusTone.Warning);
            return;
        }

        await _sessionSelection.EnsureSessionHistoryLoadedAsync(session, cancellationToken);
        tab.Timeline.ReplaceTruncatedHistoryLoadButton();

        var alwaysEnqueue = hadExistingSession &&
            UiDispatch.Invoke(_selectorState.GetUiDispatcher(), _getAlwaysEnqueue);
        if (!steer && (tab.StatusBusy || alwaysEnqueue))
        {
            _queueCoordinator.EnqueuePrompt(tab, prompt);
            _commandContext.ClearSessionInput();
            return;
        }

        _commandContext.ClearSessionInput();
        await _promptDispatchCoordinator.DispatchPromptAsync(session, tab, prompt, steer, cancellationToken);
    }

    public bool IsCurrentPromptEmpty()
        => _commandContext.IsSessionInputEmpty();

    public async Task SendAskResponseAsync(
        SessionViewDescriptor session,
        OpenSessionState tab,
        string markdown,
        string askId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentException.ThrowIfNullOrWhiteSpace(markdown);
        ArgumentException.ThrowIfNullOrWhiteSpace(askId);

        await _sessionSelection.EnsureSessionHistoryLoadedAsync(session, cancellationToken);
        tab.Timeline.ReplaceTruncatedHistoryLoadButton();
        await _promptDispatchCoordinator.DispatchPromptAsync(
            session,
            tab,
            PromptSubmission.TextOnly(markdown).WithAskId(askId),
            steer: false,
            cancellationToken);
    }

    public async Task AbortSelectedSessionAsync()
    {
        var session = _sessionSelection.GetSelectedSession();
        if (session is null)
        {
            return;
        }

        try
        {
            await _runtimeService.AbortAsync(session.SessionId);
            var tab = _sessionSelection.EnsureSessionTab(session);
            _commandContext.SetSessionStatus(tab, SR.T("Stopped · {0}", session.Title), false, StatusTone.Warning);
        }
        catch (Exception ex)
        {
            var tab = _sessionSelection.EnsureSessionTab(session);
            _commandContext.SetSessionStatus(tab, SR.T("Failed to abort '{0}': {1}", session.Title, ex.Message), false, StatusTone.Error);
        }
    }

    public async Task CompactSelectedSessionAsync()
    {
        var session = _sessionSelection.GetSelectedSession();
        if (session is null)
        {
            _commandContext.SetShellStatus(SR.T("Open a session before compacting it."), false, StatusTone.Warning);
            return;
        }

        if (!IsModelProviderReady(new ModelProviderId(session.ProviderId)))
        {
            _commandContext.SetReadyStatusForCurrentSelection();
            return;
        }

        var tab = _sessionSelection.EnsureSessionTab(session);
        if (tab.StatusBusy)
        {
            _commandContext.SetShellStatus(SR.T("Wait for '{0}' to become idle before compacting it.", session.Title), false, StatusTone.Warning);
            return;
        }

        if (session.StartedAt is null)
        {
            _commandContext.SetSessionStatus(tab, SR.T("Compaction is available after the session has completed at least one run."), false, StatusTone.Warning);
            return;
        }

        try
        {
            tab.PendingManualCompaction = true;
            _commandContext.SetSessionStatus(tab, SR.T("Compacting '{0}'...", session.Title), true, StatusTone.Info);
            var options = BuildExecutionOptions(session, tab);
            var augmentation = _pluginHostBridge is null
                ? new PluginCompactionAugmentation()
                : await _pluginHostBridge.BeforeCompactionAsync(session, tab, CancellationToken.None);
            if (!string.IsNullOrWhiteSpace(augmentation.CancelReason))
            {
                tab.PendingManualCompaction = false;
                _commandContext.SetSessionStatus(tab, SR.T("Compaction cancelled by plugin: {0}", augmentation.CancelReason), false, StatusTone.Warning);
                return;
            }

            if (!string.IsNullOrWhiteSpace(augmentation.AdditionalDeveloperInstructions))
            {
                options = _promptDispatchCoordinator.AppendAdditionalDeveloperInstructions(options, augmentation.AdditionalDeveloperInstructions);
            }

            await _runtimeService.CompactAsync(session, options, CancellationToken.None);
            if (_pluginHostBridge is not null)
            {
                await _pluginHostBridge.AfterCompactionAsync(session, tab, succeeded: true, summary: null, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            tab.PendingManualCompaction = false;
            if (_pluginHostBridge is not null)
            {
                await _pluginHostBridge.AfterCompactionAsync(session, tab, succeeded: false, summary: ex.Message, CancellationToken.None);
            }

            CodeAltaApp.UiLogger.Error(ex, $"Failed to compact session {session.SessionId}");

            _commandContext.SetSessionStatus(tab, SR.T("Failed to compact '{0}': {1}", session.Title, ex.Message), false, StatusTone.Error);
        }
    }

    public Task ClearSelectedSessionQueueAsync()
    {
        _queueCoordinator.ClearSelectedSessionQueue();
        return Task.CompletedTask;
    }

    public async Task ConvertSelectedSessionQueuedPromptToSteerAsync(string queuedPromptId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queuedPromptId);
        await _queueCoordinator.ConvertSelectedSessionQueuedPromptToSteerAsync(queuedPromptId, cancellationToken);
    }

    public void DeleteSelectedSessionQueuedPrompt(string queuedPromptId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queuedPromptId);
        _queueCoordinator.DeleteSelectedSessionQueuedPrompt(queuedPromptId);
    }

    public void DeleteSelectedSessionPendingSteer(string pendingSteerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pendingSteerId);
        _queueCoordinator.DeleteSelectedSessionPendingSteer(pendingSteerId);
    }

    public void UpdateSelectedSessionQueuedPromptCount(string queuedPromptId, int remainingCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queuedPromptId);
        _queueCoordinator.UpdateSelectedSessionQueuedPromptCount(queuedPromptId, remainingCount);
    }

    public void UpdateSelectedSessionQueuedPromptText(string queuedPromptId, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queuedPromptId);
        ArgumentNullException.ThrowIfNull(text);
        _queueCoordinator.UpdateSelectedSessionQueuedPromptText(queuedPromptId, text);
    }

    public Task DrainQueuedPromptAsync(OpenSessionState tab, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tab);
        return _queueCoordinator.DrainNextQueuedPromptAsync(tab, cancellationToken);
    }

    public Task DispatchQueuedPromptAsync(
        OpenSessionState tab,
        PromptSubmission prompt,
        bool steer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(prompt);
        if (!prompt.HasContent)
        {
            throw new ArgumentException("Prompt text or image attachments are required.", nameof(prompt));
        }

        if (prompt.Images.Count > 0 && !CurrentPromptModelSupportsImages(tab.SessionView, tab))
        {
            _commandContext.SetSessionStatus(tab, SR.T("The selected model does not support image input; the queued prompt was left in the queue."), false, StatusTone.Warning);
            throw new InvalidOperationException(SR.T("The selected model does not support image input."));
        }

        return _promptDispatchCoordinator.DispatchPromptAsync(tab.SessionView, tab, prompt, steer, cancellationToken);
    }

    public SessionExecutionOptions BuildPreferredExecutionOptions(
        ModelProviderId providerId,
        string workingDirectory,
        IReadOnlyList<string> projectRoots,
        Func<string?>? sourceSessionIdProvider = null)
        => _promptDispatchCoordinator.BuildPreferredExecutionOptions(providerId, workingDirectory, projectRoots, sourceSessionIdProvider);

    public SessionExecutionOptions BuildExecutionOptions(SessionViewDescriptor session, OpenSessionState tab)
        => _promptDispatchCoordinator.BuildExecutionOptions(session, tab);

    public async Task ActivateSelectedSkillAsync(string skillName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillName);

        var session = _sessionSelection.GetSelectedSession();
        if (session is null)
        {
            _commandContext.SetShellStatus(SR.T("Open a CodeAlta-managed session before activating a CodeAlta-managed skill."), false, StatusTone.Warning);
            return;
        }

        var providerId = new ModelProviderId(session.ResolvedProviderKey);
        if (!IsModelProviderReady(providerId))
        {
            _commandContext.SetReadyStatusForCurrentSelection();
            return;
        }

        var tab = _sessionSelection.EnsureSessionTab(session);
        if (tab.StatusBusy)
        {
            _commandContext.SetShellStatus(SR.T("Wait for '{0}' to become idle before activating a skill.", session.Title), false, StatusTone.Warning);
            return;
        }

        try
        {
            await _sessionSelection.EnsureSessionHistoryLoadedAsync(session, cancellationToken);
            tab.Timeline.ReplaceTruncatedHistoryLoadButton();
            _commandContext.SetSessionStatus(tab, SR.T("Activating skill '{0}'...", skillName), true, StatusTone.Info);
            _ = await _runtimeService.ActivateSkillAsync(
                    session,
                    BuildExecutionOptions(session, tab),
                    skillName,
                    cancellationToken)
                ;
            _commandContext.SetSessionStatus(tab, SR.T("Activated skill '{0}'.", skillName), false, StatusTone.Ready);
        }
        catch (Exception ex)
        {
            CodeAltaApp.UiLogger.Error(ex, $"Failed to activate skill {skillName}.");
            _commandContext.SetSessionStatus(tab, SR.T("Failed to activate skill '{0}': {1}", skillName, ex.Message), false, StatusTone.Error);
        }
    }

    private bool IsModelProviderReady(ModelProviderId providerId)
    {
        return _modelProviderStates.TryGetValue(providerId.Value, out var state) &&
               state.Availability == ModelProviderAvailability.Ready;
    }

    private bool CurrentPromptModelSupportsImages(SessionViewDescriptor? session, OpenSessionState? tab)
    {
        var providerId = tab is not null
            ? new ModelProviderId(tab.ProviderId.Value)
            : session is not null
                ? new ModelProviderId(session.ResolvedProviderKey)
                : ResolveSelectedProviderId();
        if (!_modelProviderStates.TryGetValue(providerId.Value, out var providerState))
        {
            return false;
        }

        var modelId = tab?.ModelId ?? providerState.SelectedModelId;
        var model = !string.IsNullOrWhiteSpace(modelId)
            ? providerState.Models.FirstOrDefault(candidate => string.Equals(candidate.Id, modelId, StringComparison.Ordinal))
            : null;
        model ??= ModelProviderPresentation.GetSelectedModel(providerState) ??
            (providerState.Models.Count == 1 ? providerState.Models[0] : null);
        return AgentModelCapabilityHelper.SupportsImageInput(providerId, model);
    }

    private ModelProviderId ResolveSelectedProviderId()
    {
        var providerIndex = UiDispatch.Invoke(_selectorState.GetUiDispatcher(), () => _selectorState.GetSelectedModelProviderIndex());
        var providerOptions = ModelProviderPresentation.BuildProviderOptions(_providerDescriptors);
        if (providerIndex is { } index && (uint)index < (uint)providerOptions.Count)
        {
            return providerOptions[index].ProviderId;
        }

        return _modelProviderStates.Values.FirstOrDefault(static state => state.Availability == ModelProviderAvailability.Ready) is { } readyState
            ? readyState.ProviderId
            : ModelProviderIds.Codex;
    }

}
