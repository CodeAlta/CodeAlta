using System.ComponentModel;
using CodeAlta.Agent;
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
    private readonly Action _refreshHeaderAndThreadWorkspace;
    private readonly CodexInstallProgressReporter? _codexInstallProgress;

    public ChatBackendInitializationCoordinator(
        AgentHub agentHub,
        IReadOnlyList<AgentBackendDescriptor> backendDescriptors,
        Dictionary<string, ChatBackendState> chatBackendStates,
        Action<Action> dispatchToUi,
        Action refreshHeaderAndThreadWorkspace,
        CodexInstallProgressReporter? codexInstallProgress = null)
    {
        ArgumentNullException.ThrowIfNull(agentHub);
        ArgumentNullException.ThrowIfNull(backendDescriptors);
        ArgumentNullException.ThrowIfNull(chatBackendStates);
        ArgumentNullException.ThrowIfNull(dispatchToUi);
        ArgumentNullException.ThrowIfNull(refreshHeaderAndThreadWorkspace);

        _agentHub = agentHub;
        _backendDescriptors = backendDescriptors;
        _chatBackendStates = chatBackendStates;
        _dispatchToUi = dispatchToUi;
        _refreshHeaderAndThreadWorkspace = refreshHeaderAndThreadWorkspace;
        _codexInstallProgress = codexInstallProgress;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await Task.WhenAll(_backendDescriptors.Select(descriptor => RefreshAsync(descriptor.BackendId, cancellationToken)))
            .ConfigureAwait(false);
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

    private async Task RefreshAsync(AgentBackendId backendId, CancellationToken cancellationToken)
    {
        var state = _chatBackendStates[backendId.Value];
        if (CanReuseLoadedBackendState(backendId, state))
        {
            LogInfo(
                $"Skipping chat backend refresh for loaded process-backed backend backend={backendId.Value} displayName={state.DisplayName} models={state.Models.Count}");
            return;
        }

        LogInfo($"Refreshing chat backend backend={backendId.Value} displayName={state.DisplayName}");
        _dispatchToUi(
            () =>
            {
                state.Availability = ChatBackendAvailability.Connecting;
                state.StatusMessage = "Detecting backend...";
                _refreshHeaderAndThreadWorkspace();
            });

        using var codexProgressSubscription = SubscribeCodexProgressIfNeeded(backendId, state);
        try
        {
            // Backend discovery is explicit background I/O. Any state mutation after this point
            // must go back through the UI dispatcher.
            var models = await _agentHub.ListModelsAsync(backendId, cancellationToken).ConfigureAwait(false);
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
                    _refreshHeaderAndThreadWorkspace();
                });
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
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
                    _refreshHeaderAndThreadWorkspace();
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
                    _refreshHeaderAndThreadWorkspace();
                }));
    }

    private static bool IsProcessBackedProviderBackend(AgentBackendId backendId)
        => string.Equals(backendId.Value, AgentBackendIds.Codex.Value, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(backendId.Value, AgentBackendIds.Copilot.Value, StringComparison.OrdinalIgnoreCase);

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
