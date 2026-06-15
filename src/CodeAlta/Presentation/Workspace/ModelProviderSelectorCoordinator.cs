using CodeAlta.App.State;
using CodeAlta.Threading;
using CodeAlta.Agent;
using CodeAlta.App;
using CodeAlta.App.Context;
using CodeAlta.App.Events;
using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Presentation.Chat;
using CodeAlta.Presentation.Prompting;
using CodeAlta.ViewModels;
using XenoAtom.Terminal.UI.Controls;

namespace CodeAlta.Presentation.Workspace;

internal sealed class ModelProviderSelectorCoordinator : IPromptAvailabilityProjectionController
{
    private readonly IReadOnlyList<ModelProviderDescriptor> _providerDescriptors;
    private readonly SessionWorkspaceViewModel _workspaceViewModel;
    private readonly PromptComposerViewModel _promptComposerViewModel;
    private readonly Dictionary<string, ModelProviderState> _modelProviderStates;
    private readonly ModelProviderSelectorStateStore _selectorState;
    private readonly SessionSelectionContext _sessionSelection;
    private readonly IModelProviderPreferencePort _preferences;
    private readonly WorkspaceRefreshContext _workspaceRefresh;
    private readonly Func<string?, string?> _getEffectiveDefaultProviderKey;
    private readonly Action _syncModelProviderSelectorItems;
    private readonly Func<SessionViewDescriptor, OpenSessionState, bool> _canSelectSessionProvider;
    private readonly Func<SessionViewDescriptor, OpenSessionState, ModelProviderId, Task<bool>> _trySwitchSessionProviderAsync;
    private readonly Action _refreshSelectionAndSessionWorkspace;
    private readonly Func<IReadOnlyList<string>>? _getConfiguredProviderKeys;
    private readonly Func<ModelProviderPreference?> _getDraftModelProviderPreference;
    private readonly Func<IReadOnlyList<string>> _getPromptPlaceholderContributions;
    private readonly Dictionary<string, ModelProviderId> _draftProviderIdsByScope = new(StringComparer.OrdinalIgnoreCase);
    private ModelProviderId? _draftProviderId;
    private bool _selectorsRefreshing;

    public ModelProviderSelectorCoordinator(
        SessionWorkspaceViewModel workspaceViewModel,
        PromptComposerViewModel promptComposerViewModel,
        Dictionary<string, ModelProviderState> modelProviderStates,
        ModelProviderSelectorStateStore selectorState,
        SessionSelectionContext sessionSelection,
        IModelProviderPreferencePort preferences,
        WorkspaceRefreshContext workspaceRefresh,
        Func<string?, string?> getEffectiveDefaultProviderKey,
        Action syncModelProviderSelectorItems,
        Func<SessionViewDescriptor, OpenSessionState, bool>? canSelectSessionProvider = null,
        Func<SessionViewDescriptor, OpenSessionState, ModelProviderId, Task<bool>>? trySwitchSessionProviderAsync = null,
        Action? refreshSelectionAndSessionWorkspace = null,
        Func<IReadOnlyList<string>>? getConfiguredProviderKeys = null,
        Func<ModelProviderPreference?>? getDraftModelProviderPreference = null,
        Func<IReadOnlyList<string>>? getPromptPlaceholderContributions = null)
        : this(
            ModelProviderPresentation.CreateProviderStates().Values
                .Select(static state => new ModelProviderDescriptor(state.ProviderId, state.DisplayName))
                .ToArray(),
            workspaceViewModel,
            promptComposerViewModel,
            modelProviderStates,
            selectorState,
            sessionSelection,
            preferences,
            workspaceRefresh,
            getEffectiveDefaultProviderKey,
            syncModelProviderSelectorItems,
            canSelectSessionProvider,
            trySwitchSessionProviderAsync,
            refreshSelectionAndSessionWorkspace,
            getConfiguredProviderKeys,
            getDraftModelProviderPreference,
            getPromptPlaceholderContributions)
    {
    }

    public ModelProviderSelectorCoordinator(
        IReadOnlyList<ModelProviderDescriptor> providerDescriptors,
        SessionWorkspaceViewModel workspaceViewModel,
        PromptComposerViewModel promptComposerViewModel,
        Dictionary<string, ModelProviderState> modelProviderStates,
        ModelProviderSelectorStateStore selectorState,
        SessionSelectionContext sessionSelection,
        IModelProviderPreferencePort preferences,
        WorkspaceRefreshContext workspaceRefresh,
        Func<string?, string?> getEffectiveDefaultProviderKey,
        Action syncModelProviderSelectorItems,
        Func<SessionViewDescriptor, OpenSessionState, bool>? canSelectSessionProvider = null,
        Func<SessionViewDescriptor, OpenSessionState, ModelProviderId, Task<bool>>? trySwitchSessionProviderAsync = null,
        Action? refreshSelectionAndSessionWorkspace = null,
        Func<IReadOnlyList<string>>? getConfiguredProviderKeys = null,
        Func<ModelProviderPreference?>? getDraftModelProviderPreference = null,
        Func<IReadOnlyList<string>>? getPromptPlaceholderContributions = null)
    {
        ArgumentNullException.ThrowIfNull(providerDescriptors);
        ArgumentNullException.ThrowIfNull(workspaceViewModel);
        ArgumentNullException.ThrowIfNull(promptComposerViewModel);
        ArgumentNullException.ThrowIfNull(modelProviderStates);
        ArgumentNullException.ThrowIfNull(selectorState);
        ArgumentNullException.ThrowIfNull(sessionSelection);
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(workspaceRefresh);
        ArgumentNullException.ThrowIfNull(getEffectiveDefaultProviderKey);
        ArgumentNullException.ThrowIfNull(syncModelProviderSelectorItems);

        _providerDescriptors = providerDescriptors;
        _workspaceViewModel = workspaceViewModel;
        _promptComposerViewModel = promptComposerViewModel;
        _modelProviderStates = modelProviderStates;
        _selectorState = selectorState;
        _sessionSelection = sessionSelection;
        _preferences = preferences;
        _workspaceRefresh = workspaceRefresh;
        _getEffectiveDefaultProviderKey = getEffectiveDefaultProviderKey;
        _syncModelProviderSelectorItems = syncModelProviderSelectorItems;
        _canSelectSessionProvider = canSelectSessionProvider ?? ((_, _) => false);
        _trySwitchSessionProviderAsync = trySwitchSessionProviderAsync ?? ((_, _, _) => Task.FromResult(false));
        _refreshSelectionAndSessionWorkspace = refreshSelectionAndSessionWorkspace ?? (() => { });
        _getConfiguredProviderKeys = getConfiguredProviderKeys;
        _getDraftModelProviderPreference = getDraftModelProviderPreference ?? (static () => null);
        _getPromptPlaceholderContributions = getPromptPlaceholderContributions ?? (static () => []);
    }

    public void RefreshForDraftScope(ModelProviderId? preferredProviderId = null)
    {
        _selectorState.VerifyBindableAccess();
        _selectorsRefreshing = true;
        try
        {
            var providerOptions = ModelProviderPresentation.BuildProviderOptions(_providerDescriptors);
            _workspaceViewModel.ProviderSummaryMarkup = ModelProviderPresentation.BuildProviderSummaryMarkup(
                _modelProviderStates.Values,
                isInitializing: false,
                configuredProviderKeys: GetConfiguredProviderKeys());
            if (providerOptions.Count == 0)
            {
                _selectorState.SetModelProviderSelection([], -1);
                _selectorState.SetModelSelection([], -1);
                _selectorState.SetReasoningSelection([], -1);
                _workspaceViewModel.CanSelectModelProvider = false;
                _workspaceViewModel.CanSelectModel = false;
                _workspaceViewModel.CanSelectReasoning = false;
                _syncModelProviderSelectorItems();
                return;
            }

            var providerId = preferredProviderId ?? GetPreferredDraftProviderId(providerOptions);
            var providerIndex = Math.Max(0, providerOptions.FindIndex(option => string.Equals(option.ProviderId.Value, providerId.Value, StringComparison.OrdinalIgnoreCase)));
            _draftProviderId = providerOptions[providerIndex].ProviderId;
            if (preferredProviderId is not null)
            {
                RememberDraftProviderForCurrentScope(_draftProviderId.Value);
            }

            _selectorState.SetModelProviderSelection(providerOptions, providerIndex);

            var providerState = _modelProviderStates[providerOptions[providerIndex].ProviderId.Value];
            _preferences.ApplyDraftModelProviderState(providerState);
            var modelOptions = ModelProviderPresentation.BuildModelOptions(providerState);
            _selectorState.SetModelSelection(
                modelOptions,
                Math.Clamp(
                modelOptions.FindIndex(option => string.Equals(option.ModelId, providerState.SelectedModelId, StringComparison.Ordinal)),
                0,
                Math.Max(0, modelOptions.Count - 1)));

            var selectedModel = providerState.Models.FirstOrDefault(model => string.Equals(model.Id, providerState.SelectedModelId, StringComparison.Ordinal))
                                ?? ModelProviderPresentation.GetSelectedModel(providerState);
            var reasoningOptions = ModelProviderPresentation.BuildReasoningOptions(selectedModel);
            _selectorState.SetReasoningSelection(
                reasoningOptions,
                Math.Clamp(
                reasoningOptions.FindIndex(option => option.Effort == providerState.SelectedReasoningEffort),
                0,
                Math.Max(0, reasoningOptions.Count - 1)));
            _workspaceViewModel.CanSelectModelProvider = true;
            _workspaceViewModel.CanSelectModel = providerState.Availability == ModelProviderAvailability.Ready;
            _workspaceViewModel.CanSelectReasoning = providerState.Availability == ModelProviderAvailability.Ready;
            _syncModelProviderSelectorItems();
        }
        finally
        {
            _selectorsRefreshing = false;
        }
    }

    public void RefreshForSession(OpenSessionState tab)
    {
        ArgumentNullException.ThrowIfNull(tab);

        _selectorState.VerifyBindableAccess();
        _selectorsRefreshing = true;
        try
        {
            var configuredProviderOptions = ModelProviderPresentation.BuildProviderOptions(_providerDescriptors);
            var providerOptions = BuildSessionProviderOptions(tab, configuredProviderOptions);
            _workspaceViewModel.ProviderSummaryMarkup = ModelProviderPresentation.BuildProviderSummaryMarkup(
                _modelProviderStates.Values,
                isInitializing: false,
                configuredProviderKeys: GetConfiguredProviderKeys());
            if (providerOptions.Count == 0)
            {
                _selectorState.SetModelProviderSelection([], -1);
                _selectorState.SetModelSelection([], -1);
                _selectorState.SetReasoningSelection([], -1);
                _workspaceViewModel.CanSelectModelProvider = false;
                _workspaceViewModel.CanSelectModel = false;
                _workspaceViewModel.CanSelectReasoning = false;
                _syncModelProviderSelectorItems();
                return;
            }

            _selectorState.SetModelProviderSelection(
                providerOptions,
                Math.Clamp(
                providerOptions.FindIndex(option => string.Equals(option.ProviderId.Value, tab.ProviderId.Value, StringComparison.OrdinalIgnoreCase)),
                0,
                Math.Max(0, providerOptions.Count - 1)));

            var providerState = GetSessionProviderState(tab, out var providerStateIsRegistered);
            _preferences.ApplySessionModelProviderState(tab);

            var modelOptions = ModelProviderPresentation.BuildModelOptions(providerState, tab.ModelId);
            _selectorState.SetModelSelection(
                modelOptions,
                Math.Clamp(
                modelOptions.FindIndex(option => string.Equals(option.ModelId, tab.ModelId ?? providerState.SelectedModelId, StringComparison.Ordinal)),
                0,
                Math.Max(0, modelOptions.Count - 1)));

            var selectedModel = providerState.Models.FirstOrDefault(model =>
                                    string.Equals(model.Id, tab.ModelId, StringComparison.Ordinal))
                                ?? ModelProviderPresentation.GetSelectedModel(providerState);
            var reasoningOptions = ModelProviderPresentation.BuildReasoningOptions(selectedModel);
            _selectorState.SetReasoningSelection(
                reasoningOptions,
                Math.Clamp(
                reasoningOptions.FindIndex(option => option.Effort == tab.ReasoningEffort),
                0,
                Math.Max(0, reasoningOptions.Count - 1)));
            _workspaceViewModel.CanSelectModelProvider = HasRegisteredProviderOption(providerOptions) && _canSelectSessionProvider(tab.SessionView, tab);
            _workspaceViewModel.CanSelectModel = providerStateIsRegistered && providerState.Availability == ModelProviderAvailability.Ready;
            _workspaceViewModel.CanSelectReasoning = providerStateIsRegistered && providerState.Availability == ModelProviderAvailability.Ready;
            _syncModelProviderSelectorItems();
        }
        finally
        {
            _selectorsRefreshing = false;
        }
    }

    public async Task OnModelProviderSelectionChangedAsync(int newIndex)
    {
        if (_selectorsRefreshing)
        {
            return;
        }

        var options = GetCurrentModelProviderOptions();
        if (options.Count == 0)
        {
            return;
        }

        if ((uint)newIndex >= (uint)options.Count)
        {
            return;
        }

        _selectorState.SetSelectedModelProviderIndex(newIndex);

        var session = _sessionSelection.GetSelectedSession();
        if (session is null)
        {
            var selectedProviderId = options[newIndex].ProviderId;
            if (!_modelProviderStates.TryGetValue(selectedProviderId.Value, out var draftProviderState))
            {
                RefreshForDraftScope();
                return;
            }

            _draftProviderId = selectedProviderId;
            RememberDraftProviderForCurrentScope(selectedProviderId);
            RefreshForDraftScope(selectedProviderId);
            _preferences.RememberGlobalPreference(CreatePreference(
                selectedProviderId,
                draftProviderState.SelectedModelId,
                draftProviderState.SelectedReasoningEffort));
            _workspaceRefresh.ApplySessionUsageProjection();
            return;
        }

        var tab = _sessionSelection.EnsureSessionTab(session);
        var targetProviderId = options[newIndex].ProviderId;
        if (string.Equals(tab.ProviderId.Value, targetProviderId.Value, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!_modelProviderStates.TryGetValue(targetProviderId.Value, out var targetProviderState))
        {
            return;
        }

        if (!_canSelectSessionProvider(session, tab))
        {
            RefreshForSession(tab);
            return;
        }

        if (targetProviderState.Availability != ModelProviderAvailability.Ready)
        {
            return;
        }

        if (await _trySwitchSessionProviderAsync(session, tab, targetProviderId))
        {
            _refreshSelectionAndSessionWorkspace();
            return;
        }

        RefreshForSession(tab);
    }

    public void OnModelSelectionChanged(int newIndex)
    {
        if (_selectorsRefreshing)
        {
            return;
        }

        var session = _sessionSelection.GetSelectedSession();
        if (session is null)
        {
            var ProviderId = GetPreferredModelProviderId();
            var draftProviderState = _modelProviderStates[ProviderId.Value];
            var draftOptions = ModelProviderPresentation.BuildModelOptions(draftProviderState);
            if ((uint)newIndex >= (uint)draftOptions.Count)
            {
                return;
            }

            draftProviderState.SelectedModelId = draftOptions[newIndex].ModelId;
            var preferredModel = ModelProviderPreferenceCoordinator.FindModel(draftProviderState.Models, draftProviderState.SelectedModelId);
            draftProviderState.SelectedReasoningEffort = ModelProviderPresentation.ResolvePreferredReasoningEffort(preferredModel, preferredReasoningEffort: null);
            UpdateModelSelectorState(draftOptions, newIndex, preferredModel, draftProviderState.SelectedReasoningEffort);
            _preferences.RememberGlobalPreference(CreatePreference(ProviderId, draftProviderState.SelectedModelId, draftProviderState.SelectedReasoningEffort));
            _workspaceRefresh.ApplySessionUsageProjection();
            return;
        }

        var tab = _sessionSelection.EnsureSessionTab(session);
        if (!_modelProviderStates.TryGetValue(tab.ProviderId.Value, out var providerState))
        {
            RefreshForSession(tab);
            return;
        }

        var options = ModelProviderPresentation.BuildModelOptions(providerState, tab.ModelId);
        if ((uint)newIndex >= (uint)options.Count)
        {
            return;
        }

        tab.ModelId = options[newIndex].ModelId;
        var selectedModel = ModelProviderPreferenceCoordinator.FindModel(providerState.Models, tab.ModelId);
        tab.ReasoningEffort = ModelProviderPresentation.ResolvePreferredReasoningEffort(selectedModel, preferredReasoningEffort: null);
        UpdateModelSelectorState(options, newIndex, selectedModel, tab.ReasoningEffort);
        _preferences.RememberSessionPreference(tab.SessionView.SessionId, CreatePreference(new ModelProviderId(tab.ProviderId.Value), tab.ModelId, tab.ReasoningEffort), true);
        _preferences.RememberGlobalPreference(CreatePreference(new ModelProviderId(tab.ProviderId.Value), tab.ModelId, tab.ReasoningEffort));
        _workspaceRefresh.ApplyHeaderProjection();
    }

    public void OnReasoningSelectionChanged(int newIndex)
    {
        if (_selectorsRefreshing)
        {
            return;
        }

        var session = _sessionSelection.GetSelectedSession();
        if (session is null)
        {
            var ProviderId = GetPreferredModelProviderId();
            var draftProviderState = _modelProviderStates[ProviderId.Value];
            var draftSelectedModel = draftProviderState.Models.FirstOrDefault(model => string.Equals(model.Id, draftProviderState.SelectedModelId, StringComparison.Ordinal));
            var draftOptions = ModelProviderPresentation.BuildReasoningOptions(draftSelectedModel);
            if ((uint)newIndex >= (uint)draftOptions.Count)
            {
                return;
            }

            draftProviderState.SelectedReasoningEffort = draftOptions[newIndex].Effort;
            UpdateReasoningSelectorState(draftOptions, newIndex);
            _preferences.RememberGlobalPreference(CreatePreference(ProviderId, draftProviderState.SelectedModelId, draftProviderState.SelectedReasoningEffort));
            _workspaceRefresh.ApplySessionUsageProjection();
            return;
        }

        var tab = _sessionSelection.EnsureSessionTab(session);
        if (!_modelProviderStates.TryGetValue(tab.ProviderId.Value, out var providerState))
        {
            RefreshForSession(tab);
            return;
        }

        var selectedModel = providerState.Models.FirstOrDefault(model => string.Equals(model.Id, tab.ModelId, StringComparison.Ordinal));
        var options = ModelProviderPresentation.BuildReasoningOptions(selectedModel);
        if ((uint)newIndex >= (uint)options.Count)
        {
            return;
        }

        tab.ReasoningEffort = options[newIndex].Effort;
        UpdateReasoningSelectorState(options, newIndex);
        _preferences.RememberSessionPreference(tab.SessionView.SessionId, CreatePreference(new ModelProviderId(tab.ProviderId.Value), tab.ModelId, tab.ReasoningEffort), true);
        _preferences.RememberGlobalPreference(CreatePreference(new ModelProviderId(tab.ProviderId.Value), tab.ModelId, tab.ReasoningEffort));
    }

    public async Task<bool> SelectProviderModelAsync(ModelProviderId providerId, string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        if (!_modelProviderStates.TryGetValue(providerId.Value, out var providerState) ||
            providerState.Models.All(model => !string.Equals(model.Id, modelId, StringComparison.Ordinal)))
        {
            return false;
        }

        var session = _sessionSelection.GetSelectedSession();
        if (session is null)
        {
            SelectDraftProviderModel(providerId, providerState, modelId);
            return true;
        }

        var tab = _sessionSelection.EnsureSessionTab(session);
        if (!string.Equals(tab.ProviderId.Value, providerId.Value, StringComparison.OrdinalIgnoreCase))
        {
            if (!_canSelectSessionProvider(session, tab) ||
                !await _trySwitchSessionProviderAsync(session, tab, providerId))
            {
                RefreshForSession(tab);
                return false;
            }

            _refreshSelectionAndSessionWorkspace();
        }

        if (!_modelProviderStates.TryGetValue(tab.ProviderId.Value, out providerState))
        {
            return false;
        }

        SelectSessionProviderModel(tab, providerState, modelId);
        return true;
    }

    private void SelectDraftProviderModel(ModelProviderId providerId, ModelProviderState providerState, string modelId)
    {
        _draftProviderId = providerId;
        RememberDraftProviderForCurrentScope(providerId);
        providerState.SelectedModelId = modelId.Trim();
        var selectedModel = ModelProviderPreferenceCoordinator.FindModel(providerState.Models, providerState.SelectedModelId);
        providerState.SelectedReasoningEffort = ModelProviderPresentation.ResolvePreferredReasoningEffort(selectedModel, preferredReasoningEffort: null);
        _preferences.RememberGlobalPreference(CreatePreference(providerId, providerState.SelectedModelId, providerState.SelectedReasoningEffort));
        RefreshForDraftScope(providerId);
        _workspaceRefresh.ApplySessionUsageProjection();
    }

    private void SelectSessionProviderModel(OpenSessionState tab, ModelProviderState providerState, string modelId)
    {
        tab.ModelId = modelId.Trim();
        var selectedModel = ModelProviderPreferenceCoordinator.FindModel(providerState.Models, tab.ModelId);
        tab.ReasoningEffort = ModelProviderPresentation.ResolvePreferredReasoningEffort(selectedModel, preferredReasoningEffort: null);
        _preferences.RememberSessionPreference(tab.SessionView.SessionId, CreatePreference(new ModelProviderId(tab.ProviderId.Value), tab.ModelId, tab.ReasoningEffort), true);
        _preferences.RememberGlobalPreference(CreatePreference(new ModelProviderId(tab.ProviderId.Value), tab.ModelId, tab.ReasoningEffort));
        RefreshForSession(tab);
        _workspaceRefresh.ApplyHeaderProjection();
    }

    public ModelProviderId GetPreferredModelProviderId()
    {
        return UiDispatch.Invoke(
            _selectorState.GetUiDispatcher(),
            () =>
            {
                var options = ModelProviderPresentation.BuildProviderOptions(_providerDescriptors);
                if (options.Count == 0)
                {
                    return GetDefaultProviderId();
                }

                if (_selectorState.GetSelectedModelProviderIndex() is { } providerIndex &&
                    (uint)providerIndex < (uint)options.Count)
                {
                    return options[providerIndex].ProviderId;
                }

                if (ResolveConfiguredDefaultProviderId(options) is { } configuredDefaultProviderId)
                {
                    return configuredDefaultProviderId;
                }

                var readyProvider = options.FirstOrDefault(option => IsModelProviderReady(option.ProviderId));
                if (readyProvider is not null)
                {
                    return readyProvider.ProviderId;
                }

                return GetDefaultProviderId();
            });
    }

    public bool IsModelProviderReady(ModelProviderId providerId)
    {
        return _modelProviderStates.TryGetValue(providerId.Value, out var state) &&
               state.Availability == ModelProviderAvailability.Ready;
    }

    public bool TryGetPromptUnavailableStatus(out string message, out StatusTone tone)
    {
        var projection = BuildPromptComposerProjection();
        if (!projection.HasUnavailableStatus)
        {
            message = string.Empty;
            tone = StatusTone.Ready;
            return false;
        }

        message = projection.UnavailableStatusMessage!;
        tone = projection.UnavailableStatusTone;
        return true;
    }

    public void ApplyPromptAvailabilityProjection()
    {
        _selectorState.VerifyBindableAccess();
        var projection = BuildPromptComposerProjection();
        _promptComposerViewModel.Placeholder = projection.Placeholder;
        _promptComposerViewModel.IsEnabled = projection.IsEnabled;
        _promptComposerViewModel.CanSend = projection.CanSend;
        _promptComposerViewModel.CanSteer = projection.CanSteer;
        _promptComposerViewModel.CanAbort = projection.CanAbort;
        _promptComposerViewModel.CanCompact = projection.CanCompact;
        _promptComposerViewModel.CanCloseTab = projection.CanCloseTab;
        _promptComposerViewModel.CanClearQueue = projection.CanClearQueue;
        _promptComposerViewModel.CanAlwaysEnqueue = projection.CanAlwaysEnqueue;
        UpdateSessionModelProviderSelectionAvailability();
    }

    private void UpdateSessionModelProviderSelectionAvailability()
    {
        if (_sessionSelection.Selection.Target is not WorkspaceTarget.Session)
        {
            return;
        }

        var selectedSession = _sessionSelection.GetSelectedSession();
        if (selectedSession is null ||
            _sessionSelection.FindOpenSession(selectedSession.SessionId) is not { } selectedTab)
        {
            return;
        }

        _workspaceViewModel.CanSelectModelProvider = HasRegisteredProviderOption(_workspaceViewModel.ModelProviderOptions) &&
            _canSelectSessionProvider(selectedSession, selectedTab);
    }

    private IReadOnlyList<ModelProviderOption> GetCurrentModelProviderOptions()
        => _workspaceViewModel.ModelProviderOptions.Count > 0
            ? _workspaceViewModel.ModelProviderOptions
            : ModelProviderPresentation.BuildProviderOptions(_providerDescriptors);

    private List<ModelProviderOption> BuildSessionProviderOptions(
        OpenSessionState tab,
        IReadOnlyList<ModelProviderOption> configuredProviderOptions)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(configuredProviderOptions);

        var options = configuredProviderOptions.ToList();
        if (options.Any(option => string.Equals(option.ProviderId.Value, tab.ProviderId.Value, StringComparison.OrdinalIgnoreCase)))
        {
            return options;
        }

        options.Insert(0, new ModelProviderOption(new ModelProviderId(tab.ProviderId.Value), BuildUnavailableSessionProviderLabel(tab.SessionView, new ModelProviderId(tab.ProviderId.Value))));
        return options;
    }

    private ModelProviderState GetSessionProviderState(OpenSessionState tab, out bool isRegistered)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (_modelProviderStates.TryGetValue(tab.ProviderId.Value, out var providerState))
        {
            isRegistered = true;
            return providerState;
        }

        isRegistered = false;
        return new ModelProviderState(new ModelProviderId(tab.ProviderId.Value), BuildUnavailableSessionProviderLabel(tab.SessionView, new ModelProviderId(tab.ProviderId.Value)))
        {
            Availability = ModelProviderAvailability.Unsupported,
            SelectedModelId = tab.ModelId,
            SelectedReasoningEffort = tab.ReasoningEffort,
        };
    }

    private bool HasRegisteredProviderOption(IReadOnlyList<ModelProviderOption> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Any(option => _modelProviderStates.ContainsKey(option.ProviderId.Value));
    }

    private ModelProviderId GetPreferredDraftProviderId(IReadOnlyList<ModelProviderOption> providerOptions)
    {
        if (_draftProviderIdsByScope.TryGetValue(GetCurrentDraftScopeKey(), out var scopedDraftProviderId) &&
            providerOptions.FirstOrDefault(option => string.Equals(option.ProviderId.Value, scopedDraftProviderId.Value, StringComparison.OrdinalIgnoreCase)) is { } scopedDraftProvider &&
            IsModelProviderReady(scopedDraftProvider.ProviderId))
        {
            return scopedDraftProvider.ProviderId;
        }

        if (_getDraftModelProviderPreference() is { } draftPreference &&
            providerOptions.FirstOrDefault(option =>
                string.Equals(option.ProviderId.Value, draftPreference.ModelProviderId.Value, StringComparison.OrdinalIgnoreCase)) is { } persistedDraftProvider)
        {
            return persistedDraftProvider.ProviderId;
        }

        if (_draftProviderId is { } draftProviderId &&
            providerOptions.FirstOrDefault(option => string.Equals(option.ProviderId.Value, draftProviderId.Value, StringComparison.OrdinalIgnoreCase)) is { } draftProvider)
        {
            if (IsModelProviderReady(draftProvider.ProviderId))
            {
                return draftProvider.ProviderId;
            }
        }

        if (ResolveConfiguredDefaultProviderId(providerOptions) is { } configuredDefaultProviderId)
        {
            return configuredDefaultProviderId;
        }

        var readyProvider = providerOptions.FirstOrDefault(option => IsModelProviderReady(option.ProviderId));
        if (readyProvider is not null)
        {
            return readyProvider.ProviderId;
        }

        return providerOptions.FirstOrDefault()?.ProviderId ?? GetDefaultProviderId();
    }

    private bool HasAnyReadyModelProvider()
    {
        return _modelProviderStates.Values.Any(static state => state.Availability == ModelProviderAvailability.Ready);
    }

    private void UpdateModelSelectorState(
        IReadOnlyList<ChatModelOption> modelOptions,
        int selectedModelIndex,
        AgentModelInfo? selectedModel,
        AgentReasoningEffort? selectedReasoningEffort)
    {
        ArgumentNullException.ThrowIfNull(modelOptions);

        var reasoningOptions = ModelProviderPresentation.BuildReasoningOptions(selectedModel);
        var selectedReasoningIndex = Math.Clamp(
            reasoningOptions.FindIndex(option => option.Effort == selectedReasoningEffort),
            0,
            Math.Max(0, reasoningOptions.Count - 1));

        var wasRefreshing = _selectorsRefreshing;
        _selectorsRefreshing = true;
        try
        {
            _selectorState.SetModelSelection(modelOptions, selectedModelIndex);
            _selectorState.SetReasoningSelection(reasoningOptions, selectedReasoningIndex);
            _syncModelProviderSelectorItems();
        }
        finally
        {
            _selectorsRefreshing = wasRefreshing;
        }
    }

    private void UpdateReasoningSelectorState(
        IReadOnlyList<ChatReasoningOption> reasoningOptions,
        int selectedReasoningIndex)
    {
        ArgumentNullException.ThrowIfNull(reasoningOptions);

        var wasRefreshing = _selectorsRefreshing;
        _selectorsRefreshing = true;
        try
        {
            _selectorState.SetReasoningSelection(reasoningOptions, selectedReasoningIndex);
            _syncModelProviderSelectorItems();
        }
        finally
        {
            _selectorsRefreshing = wasRefreshing;
        }
    }

    private PromptComposerProjection BuildPromptComposerProjection()
    {
        var selection = _sessionSelection.Selection;
        var selectedSession = selection.Target is WorkspaceTarget.Session ? _sessionSelection.GetSelectedSession() : null;
        var selectedTab = selectedSession is null ? null : _sessionSelection.FindOpenSession(selectedSession.SessionId);
        var providerId = selectedTab?.ProviderId ??
            (selectedSession is not null ? new ModelProviderId(selectedSession.ProviderId) : GetPreferredModelProviderId());
        if (!_modelProviderStates.TryGetValue(providerId.Value, out var providerState) && selectedSession is not null)
        {
            providerState = new ModelProviderState(providerId, BuildUnavailableSessionProviderLabel(selectedSession, providerId))
            {
                Availability = ModelProviderAvailability.Unsupported,
            };
        }

        if (providerState is null || string.IsNullOrWhiteSpace(providerState.DisplayName))
        {
            providerState = _modelProviderStates.Values.FirstOrDefault()
                ?? new ModelProviderState(GetDefaultProviderId(), GetDefaultProviderId().Value);
        }

        return PromptComposerProjectionBuilder.Build(
            selectedSession,
            _sessionSelection.GetSelectedProject(),
            selection.Target is WorkspaceTarget.Draft { IsGlobal: true },
            providerState.DisplayName,
            providerState.Availability,
            HasAnyReadyModelProvider(),
            _sessionSelection.HasOpenDraftTab(),
            _sessionSelection.OpenSessionIds.Count + (_sessionSelection.HasOpenDraftTab() ? 1 : 0),
            selectedSession?.SessionId,
            selectedTab is not null &&
            selectedTab.QueuedPrompts.Count > 0,
            selectedSession is not null,
            selectedSession is not null &&
            selectedTab is { } selectedSessionTab &&
            selectedSession.StartedAt is not null &&
            !selectedSessionTab.StatusBusy,
            selectedSession is not null &&
            selectedTab is { } selectedAbortTab &&
            selectedAbortTab.StatusBusy,
            _getPromptPlaceholderContributions());
    }

    private ModelProviderId GetDefaultProviderId()
    {
        return _providerDescriptors.FirstOrDefault()?.ProviderId ?? ModelProviderIds.Codex;
    }

    private static ModelProviderPreference CreatePreference(
        ModelProviderId modelProviderId,
        string? modelId,
        AgentReasoningEffort? reasoningEffort)
        => new(modelProviderId, modelId, reasoningEffort);

    private static string BuildUnavailableSessionProviderLabel(SessionViewDescriptor session, ModelProviderId providerId)
    {
        ArgumentNullException.ThrowIfNull(session);
        var providerKey = string.IsNullOrWhiteSpace(session.ResolvedProviderKey)
            ? providerId.Value
            : session.ResolvedProviderKey.Trim();
        return SR.T("{0} (not configured)", providerKey);
    }

    private IReadOnlyList<string>? GetConfiguredProviderKeys()
        => _getConfiguredProviderKeys?.Invoke();

    private ModelProviderId? ResolveConfiguredDefaultProviderId(IReadOnlyList<ModelProviderOption> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var projectRoot = _sessionSelection.GetSelectedProject()?.ProjectPath;
        var configuredProviderKey = _getEffectiveDefaultProviderKey(projectRoot);
        if (string.IsNullOrWhiteSpace(configuredProviderKey))
        {
            return null;
        }

        return options.FirstOrDefault(option =>
                string.Equals(option.ProviderId.Value, configuredProviderKey, StringComparison.OrdinalIgnoreCase))
            ?.ProviderId;
    }

    private void RememberDraftProviderForCurrentScope(ModelProviderId providerId)
        => _draftProviderIdsByScope[GetCurrentDraftScopeKey()] = providerId;

    private string GetCurrentDraftScopeKey()
        => _sessionSelection.GetSelectedProject()?.ProjectPath ?? "__global__";
}
