using System.ComponentModel;
using CodeAlta.Agent;
using CodeAlta.App.Events;
using CodeAlta.Models;
using CodeAlta.Presentation.Chat;
using XenoAtom.Logging;

namespace CodeAlta.App;

internal sealed class ChatBackendInitializationCoordinator
{
    private static readonly Logger Logger = LogManager.GetLogger("CodeAlta.App.ChatBackendInitialization");
    private readonly IModelProviderInitializationService _providerInitializationService;
    private readonly IReadOnlyList<ModelProviderDescriptor> _backendDescriptors;
    private readonly Dictionary<string, ModelProviderState> _modelProviderStates;
    private readonly Action<Action> _dispatchToUi;
    private readonly FrontendEventPublisher _frontendEvents;
    private readonly Action<string?>? _setProviderInitializationStatus;
    private long _providerInitializationStatusVersion;

    public ChatBackendInitializationCoordinator(
        IModelProviderInitializationService providerInitializationService,
        IReadOnlyList<ModelProviderDescriptor> backendDescriptors,
        Dictionary<string, ModelProviderState> modelProviderStates,
        Action<Action> dispatchToUi,
        FrontendEventPublisher frontendEvents,
        Action<string?>? setProviderInitializationStatus = null)
    {
        ArgumentNullException.ThrowIfNull(providerInitializationService);
        ArgumentNullException.ThrowIfNull(backendDescriptors);
        ArgumentNullException.ThrowIfNull(modelProviderStates);
        ArgumentNullException.ThrowIfNull(dispatchToUi);
        ArgumentNullException.ThrowIfNull(frontendEvents);

        _providerInitializationService = providerInitializationService;
        _backendDescriptors = backendDescriptors;
        _modelProviderStates = modelProviderStates;
        _dispatchToUi = dispatchToUi;
        _frontendEvents = frontendEvents;
        _setProviderInitializationStatus = setProviderInitializationStatus;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var progress = new ProviderInitializationProgress(_backendDescriptors);
        var descriptorsByProviderId = _backendDescriptors.ToDictionary(
            static descriptor => ModelProviderId.NormalizeValue(descriptor.ProviderId.Value),
            StringComparer.OrdinalIgnoreCase);
        var completedProviderIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ReportProviderInitializationProgress(progress.Snapshot(null));
        using var stateReaderCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var stateReaderTask = ApplyProviderStateChangesAsync(
            descriptorsByProviderId,
            completedProviderIds,
            progress,
            stateReaderCts.Token);
        try
        {
            await _providerInitializationService.InitializeAllAsync(cancellationToken).ConfigureAwait(false);

            foreach (var providerState in _providerInitializationService.CurrentStates)
            {
                await ApplyProviderStateChangeAsync(
                        providerState,
                        descriptorsByProviderId,
                        completedProviderIds,
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            stateReaderCts.Cancel();
            try
            {
                await stateReaderTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stateReaderCts.IsCancellationRequested)
            {
            }

            ReportProviderInitializationProgress(null);
        }
    }

    public Task RefreshBackendAsync(AgentBackendId backendId, CancellationToken cancellationToken = default)
    {
        return RefreshAsync(new ModelProviderId(backendId.Value), cancellationToken);
    }

    public Task RefreshBackendsAsync(IEnumerable<AgentBackendId> backendIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(backendIds);
        return Task.WhenAll(backendIds.Distinct().Select(backendId => RefreshAsync(new ModelProviderId(backendId.Value), cancellationToken)));
    }

    internal static (ModelProviderAvailability Availability, string StatusMessage) ClassifyFailure(
        ModelProviderState state,
        Exception exception)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(exception);

        var root = exception.GetBaseException();
        if (root is FileNotFoundException or DirectoryNotFoundException)
        {
            return (ModelProviderAvailability.Unsupported, ChatBackendPresentation.BuildUnsupportedBackendMessage(state, root.Message));
        }

        if (root is Win32Exception win32Exception && win32Exception.NativeErrorCode == 2)
        {
            return (ModelProviderAvailability.Unsupported, ChatBackendPresentation.BuildUnsupportedBackendMessage(state, root.Message));
        }

        var message = root.Message.Trim();
        if (message.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("No such file", StringComparison.OrdinalIgnoreCase))
        {
            return (ModelProviderAvailability.Unsupported, ChatBackendPresentation.BuildUnsupportedBackendMessage(state, message));
        }

        return (ModelProviderAvailability.Failed, ChatBackendPresentation.BuildFailedBackendMessage(state, message));
    }

    internal static bool CanReuseLoadedBackendState(ModelProviderId providerId, ModelProviderState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        return IsProcessBackedProviderBackend(providerId) &&
               state.Availability == ModelProviderAvailability.Ready;
    }

    internal static string? FormatProviderInitializationStatus(
        int completedProviderCount,
        int totalProviderCount,
        IReadOnlyList<string> initializingProviderDisplayNames)
    {
        ArgumentNullException.ThrowIfNull(initializingProviderDisplayNames);

        if (totalProviderCount <= 0 || completedProviderCount >= totalProviderCount)
        {
            return null;
        }

        var completed = Math.Clamp(completedProviderCount, 0, totalProviderCount);
        var initializingNames = initializingProviderDisplayNames
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Take(2)
            .ToArray();
        var initializingText = initializingNames.Length == 0
            ? "providers"
            : string.Join(", ", initializingNames) + (initializingProviderDisplayNames.Count > initializingNames.Length ? ", …" : string.Empty);

        return $"Initializing {initializingText} {BuildProgressBar(completed, totalProviderCount)} {completed}/{totalProviderCount}";
    }

    private async Task RefreshAsync(ModelProviderId providerId, CancellationToken cancellationToken)
        => await RefreshAsync(providerId, displayName: null, cancellationToken).ConfigureAwait(false);

    private async Task ApplyProviderStateChangesAsync(
        IReadOnlyDictionary<string, ModelProviderDescriptor> descriptorsByProviderId,
        HashSet<string> completedProviderIds,
        ProviderInitializationProgress progress,
        CancellationToken cancellationToken)
    {
        await foreach (var change in _providerInitializationService.StreamStateChangesAsync(cancellationToken).ConfigureAwait(false))
        {
            await ApplyProviderStateChangeAsync(
                    change.State,
                    descriptorsByProviderId,
                    completedProviderIds,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task ApplyProviderStateChangeAsync(
        ModelProviderStateSnapshot providerState,
        IReadOnlyDictionary<string, ModelProviderDescriptor> descriptorsByProviderId,
        HashSet<string> completedProviderIds,
        ProviderInitializationProgress progress,
        CancellationToken cancellationToken)
    {
        var key = ModelProviderId.NormalizeValue(providerState.ProviderId.Value);
        if (!descriptorsByProviderId.TryGetValue(key, out var descriptor))
        {
            return;
        }

        var isTerminal = IsTerminalAvailability(providerState.Availability);
        lock (completedProviderIds)
        {
            if (!isTerminal && completedProviderIds.Contains(key))
            {
                return;
            }
        }

        var state = await EnsureBackendStateAsync(providerState.ProviderId, descriptor.DisplayName, cancellationToken).ConfigureAwait(false);
        _dispatchToUi(
            () =>
            {
                ApplyProviderState(providerState, state);
                LogInfo(
                    $"Chat backend state updated provider={providerState.ProviderId.Value} displayName={state.DisplayName} availability={state.Availability} models={state.Models.Count} status={state.StatusMessage}");
                PublishProviderStateChanged(providerState.ProviderId);
            });

        if (isTerminal)
        {
            lock (completedProviderIds)
            {
                if (!completedProviderIds.Add(key))
                {
                    return;
                }
            }

            ReportProviderInitializationProgress(progress.Snapshot(descriptor));
        }
    }

    private async Task RefreshAsync(
        ModelProviderId providerId,
        string? displayName,
        CancellationToken cancellationToken)
    {
        var state = await EnsureBackendStateAsync(providerId, displayName, cancellationToken).ConfigureAwait(false);
        if (CanReuseLoadedBackendState(providerId, state))
        {
            LogInfo(
                $"Skipping chat backend refresh for loaded process-backed backend provider={providerId.Value} displayName={state.DisplayName} models={state.Models.Count}");
            return;
        }

        LogInfo($"Refreshing chat backend provider={providerId.Value} displayName={state.DisplayName}");
        _dispatchToUi(
            () =>
            {
                state.Availability = ModelProviderAvailability.Probing;
                state.StatusMessage = "Detecting provider...";
                PublishProviderStateChanged(providerId);
            });

        try
        {
            // Provider discovery is explicit background I/O. Any state mutation after this point
            // must go back through the UI dispatcher.
            await _providerInitializationService.RefreshProviderAsync(providerId, cancellationToken).ConfigureAwait(false);
            var providerState = _providerInitializationService.CurrentStates.FirstOrDefault(
                snapshot => snapshot.ProviderId == providerId);
            if (providerState is null)
            {
                throw new InvalidOperationException($"Model provider '{providerId.Value}' did not publish an initialization state.");
            }

            _dispatchToUi(
                () =>
                {
                    ApplyProviderState(providerState, state);
                    LogInfo(
                        $"Chat backend state updated provider={providerId.Value} displayName={state.DisplayName} availability={state.Availability} models={state.Models.Count} status={state.StatusMessage}");
                    PublishProviderStateChanged(providerId);
                });
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            var (availability, statusMessage) = ClassifyFailure(state, ex);
            LogWarn(
                ex,
                $"Chat backend initialization failed provider={providerId.Value} displayName={state.DisplayName} classifiedAvailability={availability} status={statusMessage}");
            _dispatchToUi(
                () =>
                {
                    state.Models.Clear();
                    state.SelectedModelId = null;
                    state.SelectedReasoningEffort = null;
                    state.DraftScopeKey = null;
                    state.Availability = availability;
                    state.StatusMessage = statusMessage;
                    PublishProviderStateChanged(providerId);
                });
        }
    }

    private static void ApplyProviderState(ModelProviderStateSnapshot providerState, ModelProviderState state)
    {
        state.DisplayName = providerState.Descriptor.DisplayName;
        state.Models.Clear();
        state.Models.AddRange(providerState.Models);
        state.Availability = providerState.Availability;
        state.StatusMessage = providerState.Availability == ModelProviderAvailability.Ready
            ? ChatBackendPresentation.BuildReadyStatusMessage(state)
            : providerState.StatusMessage ?? $"{state.DisplayName} is {providerState.Availability.ToString().ToLowerInvariant()}.";

        if (providerState.Availability == ModelProviderAvailability.Ready)
        {
            state.SelectedModelId = ResolveSelectedModelIdAfterDiscovery(providerState.Models, state.SelectedModelId ?? providerState.SelectedModelId);
            state.SelectedReasoningEffort = ChatBackendPresentation.ResolvePreferredReasoningEffort(
                ModelProviderPreferenceCoordinator.FindModel(providerState.Models, state.SelectedModelId),
                state.SelectedReasoningEffort ?? providerState.SelectedReasoningEffort);
            return;
        }

        state.SelectedModelId = providerState.SelectedModelId;
        state.SelectedReasoningEffort = providerState.SelectedReasoningEffort;
        state.DraftScopeKey = null;
    }

    private static string? ResolveSelectedModelIdAfterDiscovery(
        IReadOnlyList<AgentModelInfo> models,
        string? selectedModelId)
    {
        ArgumentNullException.ThrowIfNull(models);

        return string.IsNullOrWhiteSpace(selectedModelId)
            ? ChatBackendPresentation.ResolvePreferredModelId(models, selectedModelId)
            : selectedModelId.Trim();
    }

    private async Task<ModelProviderState> EnsureBackendStateAsync(
        ModelProviderId providerId,
        string? displayName,
        CancellationToken cancellationToken)
    {
        if (_modelProviderStates.TryGetValue(providerId.Value, out var state))
        {
            return state;
        }

        var completion = new TaskCompletionSource<ModelProviderState>(TaskCreationOptions.RunContinuationsAsynchronously);
        _dispatchToUi(
            () =>
            {
                try
                {
                    if (!_modelProviderStates.TryGetValue(providerId.Value, out var state))
                    {
                        state = new ModelProviderState(
                            providerId,
                            string.IsNullOrWhiteSpace(displayName) ? providerId.Value : displayName.Trim());
                        _modelProviderStates[providerId.Value] = state;
                    }

                    completion.SetResult(state);
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
            });

        return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private void PublishProviderStateChanged(ModelProviderId providerId)
    {
        _frontendEvents.Publish(new ModelProviderStateChangedEvent(providerId.Value));
        _frontendEvents.Publish(new HeaderChangedEvent());
    }

    private static bool IsProcessBackedProviderBackend(ModelProviderId providerId)
        => false;

    private static bool IsTerminalAvailability(ModelProviderAvailability availability)
        => availability is ModelProviderAvailability.Ready or
            ModelProviderAvailability.Failed or
            ModelProviderAvailability.Unsupported or
            ModelProviderAvailability.Disabled;

    private void ReportProviderInitializationProgress(ProviderInitializationProgressSnapshot? progress)
    {
        if (_setProviderInitializationStatus is null)
        {
            return;
        }

        var status = progress is null
            ? null
            : FormatProviderInitializationStatus(
                progress.CompletedProviderCount,
                progress.TotalProviderCount,
                progress.InitializingProviderDisplayNames);
        var version = Interlocked.Increment(ref _providerInitializationStatusVersion);
        _dispatchToUi(
            () =>
            {
                if (version == Volatile.Read(ref _providerInitializationStatusVersion))
                {
                    _setProviderInitializationStatus(status);
                }
            });
    }

    private static string BuildProgressBar(int completed, int total)
    {
        const int width = 8;
        if (total <= 0)
        {
            return "[□□□□□□□□]";
        }

        var filled = (int)Math.Round(Math.Clamp(completed, 0, total) / (double)total * width, MidpointRounding.AwayFromZero);
        filled = Math.Clamp(filled, 0, width);
        return "[" + new string('■', filled) + new string('□', width - filled) + "]";
    }

    private sealed class ProviderInitializationProgress
    {
        private readonly object _gate = new();
        private readonly List<string> _initializingProviderDisplayNames;
        private int _completedProviderCount;

        public ProviderInitializationProgress(IReadOnlyList<ModelProviderDescriptor> descriptors)
        {
            ArgumentNullException.ThrowIfNull(descriptors);

            TotalProviderCount = descriptors.Count;
            _initializingProviderDisplayNames = descriptors.Select(static descriptor => descriptor.DisplayName).ToList();
        }

        public int TotalProviderCount { get; }

        public ProviderInitializationProgressSnapshot Snapshot(ModelProviderDescriptor? completedDescriptor)
        {
            lock (_gate)
            {
                if (completedDescriptor is not null)
                {
                    _completedProviderCount++;
                    _initializingProviderDisplayNames.RemoveAll(
                        name => string.Equals(name, completedDescriptor.DisplayName, StringComparison.Ordinal));
                }

                return new ProviderInitializationProgressSnapshot(
                    _completedProviderCount,
                    TotalProviderCount,
                    _initializingProviderDisplayNames.ToArray());
            }
        }
    }

    private sealed record ProviderInitializationProgressSnapshot(
        int CompletedProviderCount,
        int TotalProviderCount,
        IReadOnlyList<string> InitializingProviderDisplayNames);

    private static void LogInfo(string message)
    {
        Logger.Info(message);
    }

    private static void LogWarn(Exception exception, string message)
    {
        Logger.Warn(exception, message);
    }
}
