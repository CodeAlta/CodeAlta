using System.ComponentModel;
using CodeAlta.Agent;
using CodeAlta.App.Events;
using CodeAlta.CodexSdk;
using CodeAlta.Models;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Presentation.Chat;
using XenoAtom.Logging;

namespace CodeAlta.App;

internal sealed class ChatBackendInitializationCoordinator
{
    private static readonly Logger Logger = LogManager.GetLogger("CodeAlta.App.ChatBackendInitialization");
    private readonly AgentHub _agentHub;
    private readonly IReadOnlyList<AgentBackendDescriptor> _backendDescriptors;
    private readonly Dictionary<string, ChatBackendState> _chatBackendStates;
    private readonly Action<Action> _dispatchToUi;
    private readonly FrontendEventPublisher _frontendEvents;
    private readonly CodexInstallProgressReporter? _codexInstallProgress;
    private readonly Action<string?>? _setProviderInitializationStatus;
    private readonly Action<AgentBackendId, bool>? _setBackendSessionLoadingEnabled;
    private long _providerInitializationStatusVersion;

    public ChatBackendInitializationCoordinator(
        AgentHub agentHub,
        IReadOnlyList<AgentBackendDescriptor> backendDescriptors,
        Dictionary<string, ChatBackendState> chatBackendStates,
        Action<Action> dispatchToUi,
        FrontendEventPublisher frontendEvents,
        CodexInstallProgressReporter? codexInstallProgress = null,
        Action<string?>? setProviderInitializationStatus = null,
        Action<AgentBackendId, bool>? setBackendSessionLoadingEnabled = null)
    {
        ArgumentNullException.ThrowIfNull(agentHub);
        ArgumentNullException.ThrowIfNull(backendDescriptors);
        ArgumentNullException.ThrowIfNull(chatBackendStates);
        ArgumentNullException.ThrowIfNull(dispatchToUi);
        ArgumentNullException.ThrowIfNull(frontendEvents);

        _agentHub = agentHub;
        _backendDescriptors = backendDescriptors;
        _chatBackendStates = chatBackendStates;
        _dispatchToUi = dispatchToUi;
        _frontendEvents = frontendEvents;
        _codexInstallProgress = codexInstallProgress;
        _setProviderInitializationStatus = setProviderInitializationStatus;
        _setBackendSessionLoadingEnabled = setBackendSessionLoadingEnabled;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var progress = new ProviderInitializationProgress(_backendDescriptors);
        ReportProviderInitializationProgress(progress.Snapshot(null));
        try
        {
            await Task.WhenAll(_backendDescriptors.Select(descriptor => RefreshAndReportAsync(descriptor, progress, cancellationToken)))
                .ConfigureAwait(false);
        }
        finally
        {
            ReportProviderInitializationProgress(null);
        }
    }

    public Task RefreshBackendAsync(AgentBackendId backendId, CancellationToken cancellationToken = default)
    {
        return RefreshAsync(backendId, cancellationToken);
    }

    public Task RefreshBackendsAsync(IEnumerable<AgentBackendId> backendIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(backendIds);
        return Task.WhenAll(backendIds.Distinct().Select(backendId => RefreshAsync(backendId, cancellationToken)));
    }

    internal static (ChatBackendAvailability Availability, string StatusMessage) ClassifyFailure(
        ChatBackendState state,
        Exception exception)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(exception);

        var root = exception.GetBaseException();
        if (root is FileNotFoundException or DirectoryNotFoundException)
        {
            return (ChatBackendAvailability.Unsupported, ChatBackendPresentation.BuildUnsupportedBackendMessage(state, root.Message));
        }

        if (root is Win32Exception win32Exception && win32Exception.NativeErrorCode == 2)
        {
            return (ChatBackendAvailability.Unsupported, ChatBackendPresentation.BuildUnsupportedBackendMessage(state, root.Message));
        }

        var message = root.Message.Trim();
        if (message.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("No such file", StringComparison.OrdinalIgnoreCase))
        {
            return (ChatBackendAvailability.Unsupported, ChatBackendPresentation.BuildUnsupportedBackendMessage(state, message));
        }

        return (ChatBackendAvailability.Failed, ChatBackendPresentation.BuildFailedBackendMessage(state, message));
    }

    internal static bool CanReuseLoadedBackendState(AgentBackendId backendId, ChatBackendState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        return IsProcessBackedProviderBackend(backendId) &&
               state.Availability == ChatBackendAvailability.Ready;
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

    private async Task RefreshAndReportAsync(
        AgentBackendDescriptor descriptor,
        ProviderInitializationProgress progress,
        CancellationToken cancellationToken)
    {
        try
        {
            await RefreshAsync(descriptor.BackendId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ReportProviderInitializationProgress(progress.Snapshot(descriptor));
        }
    }

    private async Task RefreshAsync(AgentBackendId backendId, CancellationToken cancellationToken)
    {
        var state = _chatBackendStates[backendId.Value];
        if (CanReuseLoadedBackendState(backendId, state))
        {
            _setBackendSessionLoadingEnabled?.Invoke(backendId, true);
            LogInfo(
                $"Skipping chat backend refresh for loaded process-backed backend backend={backendId.Value} displayName={state.DisplayName} models={state.Models.Count}");
            return;
        }

        _setBackendSessionLoadingEnabled?.Invoke(backendId, false);
        LogInfo($"Refreshing chat backend backend={backendId.Value} displayName={state.DisplayName}");
        _dispatchToUi(
            () =>
            {
                state.Availability = ChatBackendAvailability.Connecting;
                state.StatusMessage = "Detecting backend...";
                PublishProviderStateChanged(backendId);
            });

        using var codexProgressSubscription = SubscribeCodexProgressIfNeeded(backendId, state);
        try
        {
            // Backend discovery is explicit background I/O. Any state mutation after this point
            // must go back through the UI dispatcher.
            var models = await _agentHub.ListModelsAsync(backendId, cancellationToken).ConfigureAwait(false);
            _setBackendSessionLoadingEnabled?.Invoke(backendId, true);
            _dispatchToUi(
                () =>
                {
                    state.Models.Clear();
                    state.Models.AddRange(models);
                    state.SelectedModelId = ChatBackendPresentation.ResolvePreferredModelId(models, state.SelectedModelId);
                    state.SelectedReasoningEffort = ChatBackendPresentation.ResolvePreferredReasoningEffort(
                        ChatBackendPreferenceCoordinator.FindModel(models, state.SelectedModelId),
                        state.SelectedReasoningEffort);
                    state.Availability = ChatBackendAvailability.Ready;
                    state.StatusMessage = ChatBackendPresentation.BuildReadyStatusMessage(state);
                    LogInfo(
                        $"Chat backend ready backend={backendId.Value} displayName={state.DisplayName} models={models.Count} status={state.StatusMessage}");
                    PublishProviderStateChanged(backendId);
                });
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _setBackendSessionLoadingEnabled?.Invoke(backendId, false);
            var (availability, statusMessage) = ClassifyFailure(state, ex);
            LogWarn(
                ex,
                $"Chat backend initialization failed backend={backendId.Value} displayName={state.DisplayName} classifiedAvailability={availability} status={statusMessage}");
            _dispatchToUi(
                () =>
                {
                    state.Models.Clear();
                    state.SelectedModelId = null;
                    state.SelectedReasoningEffort = null;
                    state.DraftScopeKey = null;
                    state.Availability = availability;
                    state.StatusMessage = statusMessage;
                    PublishProviderStateChanged(backendId);
                });
        }
    }

    private IDisposable? SubscribeCodexProgressIfNeeded(AgentBackendId backendId, ChatBackendState state)
    {
        if (_codexInstallProgress is null || backendId != AgentBackendIds.Codex)
        {
            return null;
        }

        return _codexInstallProgress.Subscribe(progress =>
            _dispatchToUi(
                () =>
                {
                    state.Availability = ChatBackendAvailability.Connecting;
                    state.StatusMessage = progress.Message;
                    PublishProviderStateChanged(backendId);
                }));
    }

    private void PublishProviderStateChanged(AgentBackendId backendId)
    {
        _frontendEvents.Publish(new ModelProviderStateChangedEvent(backendId.Value));
        _frontendEvents.Publish(new HeaderChangedEvent());
    }

    private static bool IsProcessBackedProviderBackend(AgentBackendId backendId)
        => string.Equals(backendId.Value, AgentBackendIds.Codex.Value, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(backendId.Value, AgentBackendIds.Copilot.Value, StringComparison.OrdinalIgnoreCase);

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

        public ProviderInitializationProgress(IReadOnlyList<AgentBackendDescriptor> descriptors)
        {
            ArgumentNullException.ThrowIfNull(descriptors);

            TotalProviderCount = descriptors.Count;
            _initializingProviderDisplayNames = descriptors.Select(static descriptor => descriptor.DisplayName).ToList();
        }

        public int TotalProviderCount { get; }

        public ProviderInitializationProgressSnapshot Snapshot(AgentBackendDescriptor? completedDescriptor)
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
        if (LogManager.IsInitialized && Logger.IsEnabled(LogLevel.Info))
        {
            Logger.Info(message);
        }
    }

    private static void LogWarn(Exception exception, string message)
    {
        if (LogManager.IsInitialized && Logger.IsEnabled(LogLevel.Warn))
        {
            Logger.Warn(exception, message);
        }
    }
}
