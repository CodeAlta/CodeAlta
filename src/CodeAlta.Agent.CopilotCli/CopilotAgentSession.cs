using System.Collections.Concurrent;
using System.Threading.Channels;
using GitHub.Copilot.SDK;
using XenoAtom.Logging;

namespace CodeAlta.Agent.CopilotCli;

/// <summary>
/// Copilot session-backed implementation of <see cref="IAgentSession"/>.
/// </summary>
public sealed class CopilotAgentSession : ICopilotAgentSession, IAgentCompactionOutcomeProvider
{
    private static readonly Logger Logger = LogManager.GetLogger("CodeAlta.Agent.Copilot.Session");
    private static readonly TimeSpan QuotaRefreshDebounceInterval = TimeSpan.FromMilliseconds(250);
    private readonly CopilotAgentBackend _backend;
    private readonly Channel<AgentEvent> _eventChannel;
    private readonly ConcurrentDictionary<Guid, Action<AgentEvent>> _subscribers = new();
    private readonly IDisposable _subscription;
    private readonly CopilotInteractionTracker _interactionTracker = new();
    private readonly CancellationTokenSource _quotaRefreshCancellation = new();
    private readonly SemaphoreSlim _quotaRefreshLock = new(1, 1);
    private DateTimeOffset _lastQuotaRefreshAt = DateTimeOffset.MinValue;
    private bool _disposed;

    internal CopilotAgentSession(CopilotAgentBackend backend, CopilotSession session)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(session);

        _backend = backend;
        Session = session;
        _eventChannel = Channel.CreateUnbounded<AgentEvent>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

        _subscription = Session.On(OnSessionEvent);
        TriggerQuotaRefresh(force: true);
    }

    /// <inheritdoc />
    public AgentBackendId BackendId => AgentBackendIds.Copilot;

    /// <inheritdoc />
    public string SessionId => Session.SessionId;

    /// <inheritdoc />
    public string? WorkspacePath => Session.WorkspacePath;

    /// <inheritdoc />
    public CopilotSession Session { get; }

    /// <inheritdoc />
    public async IAsyncEnumerable<AgentEvent> StreamEventsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in _eventChannel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return item;
        }
    }

    /// <inheritdoc />
    public IDisposable Subscribe(Action<AgentEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var key = Guid.CreateVersion7();
        _subscribers.TryAdd(key, handler);
        return new Unsubscriber(() => _subscribers.TryRemove(key, out _));
    }

    /// <inheritdoc />
    public async Task<AgentRunId> SendAsync(AgentSendOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var messageOptions = CopilotAgentMapper.ToSendMessageOptions(options);
        var messageId = await Session.SendAsync(messageOptions, cancellationToken).ConfigureAwait(false);
        return new AgentRunId(messageId);
    }

    /// <inheritdoc />
    public async Task<AgentRunId> SteerAsync(AgentSteerOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var messageOptions = CopilotAgentMapper.ToSteerMessageOptions(options);
        var messageId = await Session.SendAsync(messageOptions, cancellationToken).ConfigureAwait(false);
        return new AgentRunId(messageId);
    }

    /// <inheritdoc />
    public Task AbortAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Session.AbortAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task CompactAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _ = await CompactWithOutcomeAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<AgentCompactionOutcome?> CompactWithOutcomeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var result = await Session.Rpc.History.CompactAsync(cancellationToken).ConfigureAwait(false);
        var tokensRemoved = checked((long)result.TokensRemoved);
        var messagesRemoved = checked((int)result.MessagesRemoved);
        var message = result.Success
            ? $"Manual compaction completed ({tokensRemoved:0} tokens and {messagesRemoved:0} messages removed)."
            : "Manual compaction failed.";
        return new AgentCompactionOutcome(
            result.Success,
            message,
            MessagesRemoved: messagesRemoved,
            TokensRemoved: tokensRemoved);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AgentEvent>> GetHistoryAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var messages = await Session.GetMessagesAsync(cancellationToken).ConfigureAwait(false);
        return CopilotAgentMapper.ToHistoryEvents(SessionId, messages);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        _subscription.Dispose();
        _quotaRefreshCancellation.Cancel();
        CompleteEventStream();
        _quotaRefreshCancellation.Dispose();
        _quotaRefreshLock.Dispose();
        await Session.DisposeAsync().ConfigureAwait(false);
        _backend.RemoveSession(SessionId, this);
    }

    internal void Publish(AgentEvent eventData)
    {
        if (!_eventChannel.Writer.TryWrite(eventData))
            return;

        foreach (var subscriber in _subscribers.Values)
        {
            try
            {
                subscriber(eventData);
            }
            catch
            {
            }
        }
    }

    private void OnSessionEvent(SessionEvent sessionEvent)
    {
        try
        {
            LogDebug($"Raw Copilot session event session={SessionId} type={sessionEvent.Type} payload={SafeToJson(sessionEvent)}");
            var projectedEvents = ProjectSessionEvents(SessionId, sessionEvent, _interactionTracker);
            foreach (var eventData in projectedEvents)
            {
                LogDebug($"Mapped Copilot agent event session={SessionId} type={eventData.GetType().Name} payload={eventData.ToJson()}");
                Publish(eventData);
            }

            if (ShouldRefreshQuotaForEvent(sessionEvent))
            {
                TriggerQuotaRefresh(force: false);
            }
        }
        catch (Exception ex)
        {
            LogError($"Failed to map Copilot session event session={SessionId} type={sessionEvent.Type}", ex);
            Publish(
                new AgentErrorEvent(
                    AgentBackendIds.Copilot,
                    SessionId,
                    DateTimeOffset.UtcNow,
                    ex.Message,
                    ex));
        }
    }

    internal static bool ShouldRefreshQuotaForEvent(SessionEvent sessionEvent)
    {
        ArgumentNullException.ThrowIfNull(sessionEvent);

        return sessionEvent switch
        {
            AssistantTurnStartEvent => true,
            AssistantTurnEndEvent => true,
            SessionIdleEvent => true,
            SessionShutdownEvent => true,
            _ => false,
        };
    }

    internal static IReadOnlyList<AgentEvent> ProjectSessionEvents(
        string sessionId,
        SessionEvent sessionEvent,
        CopilotInteractionTracker tracker)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(sessionEvent);
        ArgumentNullException.ThrowIfNull(tracker);

        var mappedEvents = new List<AgentEvent>(capacity: 2);
        if (sessionEvent is AssistantMessageEvent assistantMessage &&
            tracker.ShouldEmitEmbeddedReasoning(assistantMessage) &&
            CopilotAgentMapper.TryCreateEmbeddedReasoningEvent(sessionId, assistantMessage) is { } embeddedReasoningEvent)
        {
            mappedEvents.Add(embeddedReasoningEvent);
        }

        var primaryEvent = CopilotAgentMapper.ToAgentEvent(sessionId, sessionEvent);
        mappedEvents.Add(primaryEvent);
        var projection = tracker.Project(sessionId, sessionEvent, primaryEvent);
        if (projection.SyntheticEvent is null)
        {
            return projection.PublishMappedEvent ? mappedEvents : [];
        }

        if (!projection.PublishMappedEvent)
        {
            return [projection.SyntheticEvent];
        }

        var result = new AgentEvent[mappedEvents.Count + 1];
        for (var index = 0; index < mappedEvents.Count; index++)
        {
            result[index] = mappedEvents[index];
        }

        result[^1] = projection.SyntheticEvent;
        return result;
    }

    private void CompleteEventStream()
    {
        _eventChannel.Writer.TryComplete();
    }

    private void TriggerQuotaRefresh(bool force)
    {
        if (_disposed)
        {
            return;
        }

        _ = RefreshQuotaAsync(force, _quotaRefreshCancellation.Token);
    }

    private async Task RefreshQuotaAsync(bool force, CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return;
        }

        if (!force && DateTimeOffset.UtcNow - _lastQuotaRefreshAt < QuotaRefreshDebounceInterval)
        {
            return;
        }

        if (!await _quotaRefreshLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            if (!force && DateTimeOffset.UtcNow - _lastQuotaRefreshAt < QuotaRefreshDebounceInterval)
            {
                return;
            }

            _lastQuotaRefreshAt = DateTimeOffset.UtcNow;
            var quota = await _backend.Client.Rpc.Account.GetQuotaAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            if (CopilotAgentMapper.CreateCopilotQuotaUsage(DateTimeOffset.UtcNow, quota) is not { } usage)
            {
                return;
            }

            Publish(new AgentSessionUpdateEvent(
                AgentBackendIds.Copilot,
                SessionId,
                usage.UpdatedAt,
                null,
                AgentSessionUpdateKind.UsageUpdated,
                null,
                Usage: usage));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            LogDebug($"Failed to refresh Copilot quota session={SessionId}: {ex.Message}");
        }
        finally
        {
            _quotaRefreshLock.Release();
        }
    }

    private static void LogDebug(string message)
    {
        Logger.Debug(message);
    }

    private static void LogError(string message, Exception exception)
    {
        Logger.Error(exception, message);
    }

    private static string SafeToJson(SessionEvent sessionEvent)
    {
        try
        {
            return sessionEvent.ToJson();
        }
        catch (Exception ex)
        {
            return $"<failed to serialize raw session event: {ex.Message}>";
        }
    }

    private sealed class Unsubscriber(Action unsubscribe) : IDisposable
    {
        private Action? _unsubscribe = unsubscribe;

        public void Dispose()
        {
            Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
        }
    }

    internal sealed class CopilotInteractionTracker
    {
        private readonly Dictionary<string, InteractionState> _states = new(StringComparer.Ordinal);
        private string? _activeInteractionId;

        public bool ShouldEmitEmbeddedReasoning(AssistantMessageEvent assistantMessage)
        {
            ArgumentNullException.ThrowIfNull(assistantMessage);

            if (string.IsNullOrWhiteSpace(assistantMessage.Data.ReasoningText) ||
                string.IsNullOrWhiteSpace(assistantMessage.Data.InteractionId))
            {
                return false;
            }

            return !_states.TryGetValue(assistantMessage.Data.InteractionId!, out var state) ||
                   !state.HasExplicitReasoning;
        }

        public SessionEventProjection Project(string sessionId, SessionEvent sessionEvent, AgentEvent mappedEvent)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
            ArgumentNullException.ThrowIfNull(sessionEvent);
            ArgumentNullException.ThrowIfNull(mappedEvent);

            switch (sessionEvent)
            {
                case UserMessageEvent userMessage when !string.IsNullOrWhiteSpace(userMessage.Data.InteractionId):
                    GetOrCreate(userMessage.Data.InteractionId!).HasUserMessage = true;
                    _activeInteractionId = userMessage.Data.InteractionId;
                    return SessionEventProjection.PublishMapped;

                case AssistantTurnStartEvent turnStart when !string.IsNullOrWhiteSpace(turnStart.Data.InteractionId):
                    GetOrCreate(turnStart.Data.InteractionId!).HasAssistantTurn = true;
                    _activeInteractionId = turnStart.Data.InteractionId;
                    return SessionEventProjection.PublishMapped;

                case AssistantReasoningEvent:
                case AssistantReasoningDeltaEvent:
                    if (_activeInteractionId is not null)
                    {
                        GetOrCreate(_activeInteractionId).HasExplicitReasoning = true;
                    }

                    return SessionEventProjection.PublishMapped;

                case AssistantMessageEvent assistantMessage
                    when !string.IsNullOrWhiteSpace(assistantMessage.Data.InteractionId)
                         && CopilotAgentMapper.GetAssistantMessageContentKind(assistantMessage.Data) == AgentContentKind.Assistant:
                    GetOrCreate(assistantMessage.Data.InteractionId!).FinalAnswerSeen = true;
                    _activeInteractionId = assistantMessage.Data.InteractionId;
                    return SessionEventProjection.PublishMapped;

                case AssistantTurnEndEvent turnEnd:
                    if (_activeInteractionId is not null &&
                        _states.TryGetValue(_activeInteractionId, out var state) &&
                        state.FinalAnswerSeen)
                    {
                        _states.Remove(_activeInteractionId);
                        _activeInteractionId = null;
                        return new SessionEventProjection(
                            PublishMappedEvent: true,
                            SyntheticEvent: new AgentSessionUpdateEvent(
                                AgentBackendIds.Copilot,
                                sessionId,
                                turnEnd.Timestamp,
                                null,
                                AgentSessionUpdateKind.Idle,
                                null));
                    }

                    return SessionEventProjection.PublishMapped;

                case SessionIdleEvent:
                    return new SessionEventProjection(PublishMappedEvent: !HasPendingInteraction(), SyntheticEvent: null);

                case AbortEvent:
                case SessionErrorEvent:
                case SessionShutdownEvent:
                    _states.Clear();
                    _activeInteractionId = null;
                    return SessionEventProjection.PublishMapped;

                default:
                    return SessionEventProjection.PublishMapped;
            }
        }

        private bool HasPendingInteraction()
            => _states.Count > 0;

        private InteractionState GetOrCreate(string interactionId)
        {
            if (!_states.TryGetValue(interactionId, out var state))
            {
                state = new InteractionState();
                _states[interactionId] = state;
            }

            return state;
        }

        private sealed class InteractionState
        {
            public bool HasUserMessage { get; set; }

            public bool HasAssistantTurn { get; set; }

            public bool HasExplicitReasoning { get; set; }

            public bool FinalAnswerSeen { get; set; }
        }
    }

    internal readonly record struct SessionEventProjection(bool PublishMappedEvent, AgentEvent? SyntheticEvent)
    {
        public static SessionEventProjection PublishMapped => new(PublishMappedEvent: true, SyntheticEvent: null);
    }
}
