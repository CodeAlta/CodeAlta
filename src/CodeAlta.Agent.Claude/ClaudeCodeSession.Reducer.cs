using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Agent.Claude;

// Reads what the CLI writes during a turn and cuts it into the assistant messages the session of CodeAlta asks for.
internal sealed partial class ClaudeCodeSession
{
    private abstract record TurnEvent;

    /// <summary>A message of the CLI, in the order it wrote it.</summary>
    private sealed record MessageEvent(string Type, JsonElement Message) : TurnEvent
    {
        /// <summary>For a result: the number of the turn the CLI started by itself that it ends, or zero.</summary>
        public int OwnTurn { get; init; }

        /// <summary>For a change of state: whether a user message was waiting for its answer when the CLI wrote it.</summary>
        public bool Unanswered { get; init; }
    }

    /// <summary>The CLI asked something about a tool call, or gave its result: the message that made the call is complete.</summary>
    private sealed record ToolActivityEvent : TurnEvent;

    /// <summary>The process ended.</summary>
    private sealed record ClosedEvent(Exception? Failure) : TurnEvent;

    /// <summary>One assistant message of the model, gathered from the lines the CLI writes for it.</summary>
    private sealed class Segment(string? messageId)
    {
        public string? MessageId { get; } = messageId;

        public List<AgentMessagePart> Parts { get; } = [];

        public List<string?> ContentIds { get; } = [];

        /// <summary>The content blocks received as assistant messages.</summary>
        public int AssistantBlocks { get; set; }

        /// <summary>The content blocks the stream announced.</summary>
        public int StreamBlocks { get; set; }

        /// <summary>The stream announced the message: it ends it too.</summary>
        public bool Streamed { get; set; }

        public bool StopSeen { get; set; }

        public bool HasToolCalls { get; set; }

        public JsonElement? Usage { get; set; }

        /// <summary>The output tokens of the whole message, which the stream gives when the message ends.</summary>
        public long? OutputTokens { get; set; }

        public string? Model { get; set; }
    }

    private readonly HashSet<string> _outstandingUserMessages = new(StringComparer.Ordinal);
    private Segment? _pending;
    private Segment? _held;
    private TurnEvent? _replay;
    private bool _assistantTextInRun;
    private bool _awaitingQueuedTurns;
    private (string Kind, string Text)? _apiError;
    private long? _contextWindow;
    private JsonElement? _lastUsage;
    private long? _lastOutputTokens;
    private string? _lastModel;
    private AgentRateLimitSummary? _rateLimits;

    private void ResetRunState()
    {
        lock (_gate)
        {
            _outstandingUserMessages.Clear();
            _unansweredUserMessages.Clear();
        }

        _pending = null;
        _held = null;
        _replay = null;
        _assistantTextInRun = false;
        _awaitingQueuedTurns = false;
        _apiError = null;
    }

    private async Task<AgentTurnResponse> ReadSegmentAsync(
        AgentTurnRequest request,
        Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate,
        Func<AgentTurnSessionUpdate, CancellationToken, ValueTask> onSessionUpdate,
        CancellationToken cancellationToken)
    {
        var reader = _eventReader ?? throw new InvalidOperationException("Claude Code is not running.");
        var connection = _connection!;
        while (true)
        {
            TurnEvent @event;
            if (_replay is { } replay)
            {
                _replay = null;
                @event = replay;
            }
            else
            {
                try
                {
                    @event = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (ChannelClosedException)
                {
                    throw new InvalidOperationException(connection.DescribeExit());
                }
            }

            switch (@event)
            {
                case ClosedEvent:
                    throw new InvalidOperationException(connection.DescribeExit());
                case ToolActivityEvent:
                    if (_pending is { HasToolCalls: true } && !PendingGoesOn && FinalizePending() is { } toolSegment)
                    {
                        return CreateResponse(toolSegment, requiresFollowUp: false);
                    }

                    break;
                case MessageEvent message:
                    if (await ReduceAsync(message, onUpdate, onSessionUpdate, cancellationToken).ConfigureAwait(false) is { } response)
                    {
                        return response;
                    }

                    break;
            }
        }
    }

    private async ValueTask<AgentTurnResponse?> ReduceAsync(
        MessageEvent @event,
        Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate,
        Func<AgentTurnSessionUpdate, CancellationToken, ValueTask> onSessionUpdate,
        CancellationToken cancellationToken)
    {
        var message = @event.Message;
        switch (@event.Type)
        {
            case "stream_event":
                return await ReduceStreamEventAsync(@event, onUpdate, cancellationToken).ConfigureAwait(false);
            case "assistant":
                return ReduceAssistant(@event);
            case "user":
                // The result of a tool: the message that called it has all its blocks, unless the model still writes it.
                return _pending is { HasToolCalls: true } && !PendingGoesOn && FinalizePending() is { } toolSegment
                    ? CreateResponse(toolSegment, requiresFollowUp: false)
                    : null;
            case "result":
                return ReduceResult(@event);
            case "system":
                return await ReduceSystemAsync(@event, onSessionUpdate, cancellationToken).ConfigureAwait(false);
            case "rate_limit_event":
                await ReduceRateLimitAsync(message, onSessionUpdate, cancellationToken).ConfigureAwait(false);
                return null;
            default:
                // A message of a kind this version does not know says nothing about the answer.
                return null;
        }
    }

    private async ValueTask<AgentTurnResponse?> ReduceStreamEventAsync(
        MessageEvent @event,
        Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate,
        CancellationToken cancellationToken)
    {
        if (!ClaudeCodeJson.TryGetObject(@event.Message, "event", out var stream))
        {
            return null;
        }

        switch (ClaudeCodeJson.GetString(stream, "type"))
        {
            case "message_start":
                var messageId = ClaudeCodeJson.TryGetObject(stream, "message", out var started) ? ClaudeCodeJson.GetString(started, "id") : null;
                if (BeginMessage(messageId, @event) is { } previous)
                {
                    return previous;
                }

                _pending!.Streamed = true;
                break;
            case "content_block_start":
                if (_pending is { } announced && ClaudeCodeJson.GetInt64(stream, "index") is { } startedIndex)
                {
                    announced.StreamBlocks = Math.Max(announced.StreamBlocks, (int)startedIndex + 1);
                }

                break;
            case "content_block_delta":
                if (_pending is { } streaming &&
                    ClaudeCodeJson.GetInt64(stream, "index") is { } index &&
                    ClaudeCodeJson.TryGetObject(stream, "delta", out var delta))
                {
                    var (kind, text) = ClaudeCodeJson.GetString(delta, "type") switch
                    {
                        "text_delta" => (AgentContentKind.Assistant, ClaudeCodeJson.GetString(delta, "text")),
                        "thinking_delta" => (AgentContentKind.Reasoning, ClaudeCodeJson.GetString(delta, "thinking")),
                        _ => (AgentContentKind.Assistant, null),
                    };
                    if (!string.IsNullOrEmpty(text))
                    {
                        await onUpdate(
                                new AgentTurnDelta { Kind = kind, ContentId = ContentId(streaming, (int)index), Text = text },
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                }

                break;
            case "message_delta":
                // A block of the message is written with the tokens generated so far: the end of the message has them all.
                if (_pending is { } ending &&
                    ClaudeCodeJson.TryGetObject(stream, "usage", out var endUsage) &&
                    ClaudeCodeJson.GetInt64(endUsage, "output_tokens") is { } outputTokens)
                {
                    ending.OutputTokens = outputTokens;
                }

                break;
            case "message_stop":
                if (_pending is { } stopped)
                {
                    stopped.StopSeen = true;
                    if (stopped.AssistantBlocks >= stopped.StreamBlocks && FinalizePending() is { } segment)
                    {
                        return CreateResponse(segment, requiresFollowUp: false);
                    }
                }

                break;
        }

        return null;
    }

    private AgentTurnResponse? ReduceAssistant(MessageEvent @event)
    {
        if (!ClaudeCodeJson.TryGetObject(@event.Message, "message", out var message))
        {
            return null;
        }

        if (BeginMessage(ClaudeCodeJson.GetString(message, "id"), @event) is { } previous)
        {
            return previous;
        }

        var segment = _pending!;
        var error = ClaudeCodeJson.GetString(@event.Message, "error");
        if (message.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            segment.Usage = usage;
        }

        if (ClaudeCodeJson.GetString(message, "model") is { Length: > 0 } model && model[0] != '<')
        {
            segment.Model = model;
        }

        if (ClaudeCodeJson.TryGetArray(message, "content", out var content))
        {
            foreach (var block in content.EnumerateArray())
            {
                var index = segment.AssistantBlocks++;
                switch (ClaudeCodeJson.GetString(block, "type"))
                {
                    case "text" when ClaudeCodeJson.GetString(block, "text") is { } text && !string.IsNullOrWhiteSpace(text):
                        if (error is not null)
                        {
                            // The CLI writes a failed request as an assistant text: it is an error, not an answer.
                            _apiError = (error, text);
                            break;
                        }

                        segment.Parts.Add(new AgentMessagePart.Text(text));
                        segment.ContentIds.Add(ContentId(segment, index));
                        _assistantTextInRun = true;
                        break;
                    case "thinking" when ClaudeCodeJson.GetString(block, "thinking") is { } thinking && !string.IsNullOrWhiteSpace(thinking):
                        // The signature of the block belongs to the conversation of the CLI: it is not kept.
                        segment.Parts.Add(new AgentMessagePart.Reasoning(thinking));
                        segment.ContentIds.Add(ContentId(segment, index));
                        break;
                    case "tool_use" when ClaudeCodeJson.GetString(block, "id") is { Length: > 0 } toolUseId:
                        var toolCall = GetToolCall(toolUseId);
                        if (toolCall is null)
                        {
                            break;
                        }

                        segment.Parts.Add(new AgentMessagePart.ToolCall(toolUseId, toolCall.Name, toolCall.Arguments));
                        segment.ContentIds.Add(null);
                        segment.HasToolCalls = true;
                        break;
                }
            }
        }

        if (error is not null && _apiError is null)
        {
            _apiError = (error, string.Empty);
        }

        return segment.StopSeen && segment.AssistantBlocks >= segment.StreamBlocks && FinalizePending() is { } complete
            ? CreateResponse(complete, requiresFollowUp: false)
            : null;
    }

    // Whether the model still writes the message being gathered. The CLI runs a tool call as soon as the model
    // wrote it, and can give its result before the message ends: the calls of one message stay together, so that
    // those the CLI runs at the same time are shown together.
    private bool PendingGoesOn => _pending is { Streamed: true, StopSeen: false };

    // Makes the message with that identifier the one being gathered. Returns a response when a previous message
    // has to be returned first: the event is then read again by the next call.
    private AgentTurnResponse? BeginMessage(string? messageId, TurnEvent @event)
    {
        if (_pending is { } pending && !string.Equals(pending.MessageId, messageId, StringComparison.Ordinal) && FinalizePending() is { } previous)
        {
            _replay = @event;
            return CreateResponse(previous, requiresFollowUp: false);
        }

        if (_pending is null && _held is { } held)
        {
            if (string.Equals(held.MessageId, messageId, StringComparison.Ordinal))
            {
                // More blocks of a message that looked complete.
                _pending = held;
                _held = null;
            }
            else
            {
                // The model goes on after an answer without tool calls (a hook or a queued message asked it to).
                _held = null;
                _replay = @event;
                return CreateResponse(held, requiresFollowUp: true);
            }
        }

        _pending ??= new Segment(messageId);
        return null;
    }

    // Closes the message being gathered. A message with tool calls is returned at once; one without is held
    // until the CLI says whether the turn is over.
    private Segment? FinalizePending()
    {
        var segment = _pending;
        _pending = null;
        if (segment is null || segment.Parts.Count == 0)
        {
            if (segment?.Usage is { } emptyUsage)
            {
                _lastUsage = emptyUsage;
            }

            return null;
        }

        if (_held is { } held)
        {
            _held = null;
            segment.Parts.InsertRange(0, held.Parts);
            segment.ContentIds.InsertRange(0, held.ContentIds);
        }

        if (segment.HasToolCalls)
        {
            return segment;
        }

        _held = segment;
        return null;
    }

    private AgentTurnResponse? ReduceResult(MessageEvent @event)
    {
        if (_pending is not null && FinalizePending() is { } toolSegment)
        {
            _replay = @event;
            return CreateResponse(toolSegment, requiresFollowUp: false);
        }

        var result = @event.Message;
        ReadResultUsage(result);

        bool more;
        lock (_gate)
        {
            if (ClaudeCodeJson.TryGetArray(result, "user_message_uuids", out var uuids))
            {
                foreach (var uuid in uuids.EnumerateArray())
                {
                    if (uuid.ValueKind == JsonValueKind.String)
                    {
                        _outstandingUserMessages.Remove(uuid.GetString()!);
                    }
                }
            }
            else if (@event.OwnTurn == 0 && !NamesItsOrigin(result))
            {
                // A CLI that does not name the messages of a turn runs them in one turn.
                _outstandingUserMessages.Clear();
            }

            // A turn the CLI started by itself answers no message: one sent meanwhile has its own turn.
            _ownTurnsRead = Math.Max(_ownTurnsRead, @event.OwnTurn);
            more = _outstandingUserMessages.Count > 0;
        }

        var subtype = ClaudeCodeJson.GetString(result, "subtype");
        if (ClaudeCodeJson.GetBoolean(result, "is_error") || (subtype is not null && subtype.StartsWith("error", StringComparison.Ordinal)))
        {
            if (_held is { } answered)
            {
                // What the model answered before the turn failed (a turn or budget limit) is recorded first: the
                // failure is reported by the next call.
                _held = null;
                _replay = @event;
                return CreateResponse(answered, requiresFollowUp: true);
            }

            if (@event.OwnTurn == 0 || !more)
            {
                throw CreateFailure(result, subtype);
            }

            // The turn that failed is one the CLI started by itself: the run goes on with the turn of its message.
            _apiError = null;
        }

        if (more)
        {
            // A message sent while the turn ran has its own turn: the answer is held until it starts, or until the
            // CLI says it is idle.
            _awaitingQueuedTurns = true;
            return null;
        }

        return CompleteRun(ClaudeCodeJson.GetString(result, "result"));
    }

    private AgentTurnResponse CompleteRun(string? resultText)
    {
        var segment = _held;
        _held = null;
        _awaitingQueuedTurns = false;
        if (segment is null && !_assistantTextInRun && !string.IsNullOrWhiteSpace(resultText))
        {
            // The output of a command of the CLI (`/context`, `/cost`, ...) is only in the result.
            segment = new Segment(null);
            segment.Parts.Add(new AgentMessagePart.Text(resultText));
            segment.ContentIds.Add(null);
        }

        return CreateResponse(segment ?? new Segment(null), requiresFollowUp: false);
    }

    private async ValueTask<AgentTurnResponse?> ReduceSystemAsync(
        MessageEvent @event,
        Func<AgentTurnSessionUpdate, CancellationToken, ValueTask> onSessionUpdate,
        CancellationToken cancellationToken)
    {
        var message = @event.Message;
        switch (ClaudeCodeJson.GetString(message, "subtype"))
        {
            case "session_state_changed":
                // The CLI is idle although a message waits for its turn: it will not have one. An idle state the
                // CLI wrote before the message was sent (after a turn of its own) says nothing of it.
                if (_awaitingQueuedTurns &&
                    @event.Unanswered &&
                    string.Equals(ClaudeCodeJson.GetString(message, "state"), "idle", StringComparison.Ordinal))
                {
                    lock (_gate)
                    {
                        _outstandingUserMessages.Clear();
                    }

                    return CompleteRun(null);
                }

                break;
            case "status":
                if (string.Equals(ClaudeCodeJson.GetString(message, "status"), "compacting", StringComparison.Ordinal))
                {
                    await onSessionUpdate(
                            new AgentTurnSessionUpdate { Kind = AgentSessionUpdateKind.Info, Message = "Claude Code is compacting its context." },
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                break;
            case "compact_boundary":
                await onSessionUpdate(
                        new AgentTurnSessionUpdate { Kind = AgentSessionUpdateKind.Info, Message = DescribeCompaction(message) },
                        cancellationToken)
                    .ConfigureAwait(false);
                break;
            case "api_retry":
                var attempt = ClaudeCodeJson.GetInt64(message, "attempt");
                var maxRetries = ClaudeCodeJson.GetInt64(message, "max_retries");
                var delaySeconds = (ClaudeCodeJson.GetInt64(message, "retry_delay_ms") ?? 0) / 1000d;
                var reason = ClaudeCodeJson.GetString(message, "error") ?? "request failed";
                await onSessionUpdate(
                        new AgentTurnSessionUpdate
                        {
                            Kind = AgentSessionUpdateKind.Reconnecting,
                            Message = string.Create(CultureInfo.InvariantCulture, $"Claude API {reason}; retry {attempt}/{maxRetries} in {delaySeconds:0.#} s."),
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
                break;
        }

        return null;
    }

    private async ValueTask ReduceRateLimitAsync(
        JsonElement message,
        Func<AgentTurnSessionUpdate, CancellationToken, ValueTask> onSessionUpdate,
        CancellationToken cancellationToken)
    {
        if (!ClaudeCodeJson.TryGetObject(message, "rate_limit_info", out var info))
        {
            return;
        }

        var status = ClaudeCodeJson.GetString(info, "status");
        var type = ClaudeCodeJson.GetString(info, "rateLimitType") ?? ClaudeCodeJson.GetString(info, "rate_limit_type");
        var utilization = ClaudeCodeJson.GetDouble(info, "utilization");
        var resetsAt = ClaudeCodeJson.GetInt64(info, "resetsAt") ?? ClaudeCodeJson.GetInt64(info, "resets_at");
        // The window that limits now, and how long it is when the CLI names it.
        var limiting = new AgentRateLimitWindow(
            UsedPercent: utilization is { } used ? (int)Math.Round(Math.Clamp(used, 0d, 1d) * 100d) : null,
            ResetsAt: resetsAt is > 0 ? DateTimeOffset.FromUnixTimeSeconds(resetsAt.Value) : null,
            WindowDurationMinutes: type switch
            {
                ClaudeCodeAccountUsage.FiveHour => 5 * 60,
                { } week when week.StartsWith(ClaudeCodeAccountUsage.SevenDay, StringComparison.Ordinal) => 7 * 24 * 60,
                _ => null,
            });
        // A newer CLI also sends the two windows of the plan together: the five hours first, then the week.
        ClaudeCodeJson.TryGetObject(info, "unifiedWindows", out var windows);
        var fiveHour = ReadUnifiedWindow(windows, ClaudeCodeAccountUsage.FiveHour, 5 * 60);
        var sevenDay = ReadUnifiedWindow(windows, ClaudeCodeAccountUsage.SevenDay, 7 * 24 * 60);
        _rateLimits = new AgentRateLimitSummary(
            Name: type,
            Primary: fiveHour ?? (sevenDay is null ? limiting : null),
            Secondary: sevenDay,
            Label: type is null ? "Claude usage limit" : $"Claude usage limit ({type.Replace('_', ' ')})");
        if (status is "allowed_warning" or "rejected")
        {
            var text = status == "rejected" ? "reached" : "nearly reached";
            var until = limiting.ResetsAt is { } reset
                ? string.Create(CultureInfo.InvariantCulture, $" It resets at {reset.ToLocalTime():HH:mm}.")
                : string.Empty;
            await onSessionUpdate(
                    new AgentTurnSessionUpdate { Kind = AgentSessionUpdateKind.Warning, Message = $"{_rateLimits.Label} {text}.{until}" },
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static AgentRateLimitWindow? ReadUnifiedWindow(JsonElement windows, string name, long minutes)
        => ClaudeCodeJson.TryGetObject(windows, name, out var window) && ClaudeCodeJson.GetDouble(window, "utilization") is { } used
            ? new AgentRateLimitWindow(
                UsedPercent: (int)Math.Round(Math.Clamp(used, 0d, 1d) * 100d),
                ResetsAt: ClaudeCodeJson.GetInt64(window, "resetsAt") is > 0 and var at ? DateTimeOffset.FromUnixTimeSeconds(at) : null,
                WindowDurationMinutes: minutes)
            : null;

    private void ReadResultUsage(JsonElement result)
    {
        // A result names every model the conversation used so far, a subagent's included: which window is the one
        // of the context is known with the model that answered.
        if (result.TryGetProperty("modelUsage", out var modelUsage) && modelUsage.ValueKind == JsonValueKind.Object)
        {
            _modelContextWindows.Clear();
            foreach (var model in modelUsage.EnumerateObject())
            {
                if (ClaudeCodeJson.GetInt64(model.Value, "contextWindow") is > 0 and var contextWindow)
                {
                    _modelContextWindows[model.Name] = contextWindow;
                }
            }
        }

        // `total_cost_usd` is the cost of the conversation so far, also in a process that resumed it: the cost of
        // the turn is what was added since the result before. A total smaller than the one before is a count that
        // started again, and is then the cost of the turn.
        _resultCost = null;
        if (ClaudeCodeJson.GetDouble(result, "total_cost_usd") is { } total)
        {
            if (_costTotal is { } before)
            {
                _resultCost = total >= before ? total - before : total;
            }

            _costTotal = total;
        }

        _resultDurationMs = ClaudeCodeJson.GetDouble(result, "duration_ms");
    }

    private long? ResolveContextWindow()
    {
        if (_lastModel is { } answered)
        {
            foreach (var (model, contextWindow) in _modelContextWindows)
            {
                if (model.StartsWith(answered, StringComparison.OrdinalIgnoreCase) || answered.StartsWith(model, StringComparison.OrdinalIgnoreCase))
                {
                    return contextWindow;
                }
            }
        }

        // What the CLI said of its context when it started, then the only model there is.
        return _contextWindow ?? (_modelContextWindows.Count == 1 ? _modelContextWindows.Values.First() : null);
    }

    private readonly Dictionary<string, long> _modelContextWindows = new(StringComparer.OrdinalIgnoreCase);

    // The cost of the conversation at its last result; null when it is not known (a session saved without it).
    private double? _costTotal;
    private double? _resultCost;
    private double? _resultDurationMs;
    private int _conversationCount;

    private AgentTurnResponse CreateResponse(Segment segment, bool requiresFollowUp)
    {
        if (segment.Usage is { } usage)
        {
            _lastUsage = usage;
            _lastOutputTokens = segment.OutputTokens;
        }

        if (segment.Model is { } model)
        {
            _lastModel = model;
        }

        var durable = segment.Parts.Count > 0 || !requiresFollowUp;
        if (durable)
        {
            _syncedAssistants++;
        }

        var hasContentIds = segment.ContentIds.Exists(static id => id is not null);
        var usageSnapshot = CreateUsage(_conversationCount + (durable ? 1 : 0));

        // The cost and the duration the CLI gives with a result are those of the turn that ended: they are
        // reported once, with the answer that follows the result, not with the requests of a later turn.
        _resultCost = null;
        _resultDurationMs = null;
        return new AgentTurnResponse
        {
            AssistantMessage = new AgentConversationMessage(AgentConversationRole.Assistant, [.. segment.Parts]),
            AssistantPartContentIds = hasContentIds ? [.. segment.ContentIds] : null,
            RequiresProviderFollowUp = requiresFollowUp,
            Usage = usageSnapshot,
            ProviderSessionId = _claudeSessionId,
            ProviderState = _claudeSessionId is { } sessionId
                ? new ClaudeCodeProviderState(sessionId, _syncedUsers, _syncedAssistants, _instructionsHash, _costTotal).ToJson()
                : null,
        };
    }

    // The usage of the last request of the model: what it read is what the context of the session holds.
    private AgentSessionUsage? CreateUsage(int messageCount)
    {
        if (_lastUsage is not { } usage)
        {
            return _rateLimits is null
                ? null
                : new AgentSessionUsage(RateLimits: _rateLimits, Scope: AgentUsageScope.RateLimitOnly, Source: AgentUsageSource.ProviderUsage, UpdatedAt: DateTimeOffset.UtcNow);
        }

        // `input_tokens` is what was not read from or written to the prompt cache: the request read all three, and
        // the input of an operation is the three together.
        var uncached = ClaudeCodeJson.GetInt64(usage, "input_tokens") ?? 0;
        var cacheRead = ClaudeCodeJson.GetInt64(usage, "cache_read_input_tokens") ?? 0;
        var cacheWrite = ClaudeCodeJson.GetInt64(usage, "cache_creation_input_tokens") ?? 0;
        var input = uncached + cacheRead + cacheWrite;
        var output = _lastOutputTokens ?? ClaudeCodeJson.GetInt64(usage, "output_tokens") ?? 0;
        var current = input + output;
        var contextWindow = ResolveContextWindow();
        return new AgentSessionUsage(
            Window: new AgentWindowUsageSnapshot(
                CurrentTokens: current,
                TokenLimit: contextWindow,
                MessageCount: messageCount,
                Label: "Active context window",
                TotalContextEnvelope: contextWindow),
            LastOperation: new AgentOperationUsageSnapshot(
                Model: _lastModel,
                InputTokens: input,
                OutputTokens: output,
                CacheReadTokens: cacheRead,
                CacheWriteTokens: cacheWrite,
                CachedInputTokens: cacheRead,
                Cost: _resultCost,
                DurationMs: _resultDurationMs),
            RateLimits: _rateLimits,
            Scope: AgentUsageScope.CurrentWindow,
            Source: AgentUsageSource.ProviderUsage,
            UpdatedAt: DateTimeOffset.UtcNow);
    }

    private InvalidOperationException CreateFailure(JsonElement result, string? subtype)
    {
        var text = ClaudeCodeJson.GetString(result, "result");
        if (string.IsNullOrWhiteSpace(text) && ClaudeCodeJson.TryGetArray(result, "errors", out var errors))
        {
            text = string.Join(" ", errors.EnumerateArray().Where(static error => error.ValueKind == JsonValueKind.String).Select(static error => error.GetString()));
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            text = _apiError?.Text;
        }

        var apiError = _apiError?.Kind;
        _apiError = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            text = subtype switch
            {
                "error_max_turns" => "The turn limit of Claude Code was reached.",
                "error_max_budget_usd" => "The budget set for Claude Code was reached.",
                _ => "Claude Code could not complete the turn.",
            };
        }

        if (string.Equals(apiError, "authentication_failed", StringComparison.Ordinal))
        {
            return new InvalidOperationException(
                "Claude Code is not signed in. Run `claude` in a terminal and sign in with /login, then send the message again. " +
                $"CodeAlta does not handle the credentials of Claude Code. ({text.Trim()})");
        }

        return new InvalidOperationException($"Claude Code: {text.Trim()}");
    }

    private async Task<AgentCompactionOutcome> ReadCompactionAsync(CancellationToken cancellationToken)
    {
        var reader = _eventReader ?? throw new InvalidOperationException("Claude Code is not running.");
        var connection = _connection!;
        JsonElement? boundary = null;
        while (true)
        {
            TurnEvent @event;
            try
            {
                @event = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                throw new InvalidOperationException(connection.DescribeExit());
            }

            if (@event is ClosedEvent)
            {
                throw new InvalidOperationException(connection.DescribeExit());
            }

            if (@event is not MessageEvent message)
            {
                continue;
            }

            if (message.Type == "system" && ClaudeCodeJson.GetString(message.Message, "subtype") == "compact_boundary")
            {
                boundary = message.Message;
                continue;
            }

            if (message.Type != "result")
            {
                continue;
            }

            if (message.OwnTurn > 0)
            {
                // The result of a turn the CLI started by itself, which no run read: the command has its own.
                lock (_gate)
                {
                    _ownTurnsRead = Math.Max(_ownTurnsRead, message.OwnTurn);
                }

                continue;
            }

            lock (_gate)
            {
                _outstandingUserMessages.Clear();
            }

            var text = ClaudeCodeJson.GetString(message.Message, "result");
            if (boundary is not { } compacted)
            {
                // The command answers without a boundary when there was nothing to compact, or when it failed.
                var failed = ClaudeCodeJson.GetBoolean(message.Message, "is_error");
                return new AgentCompactionOutcome(!failed, string.IsNullOrWhiteSpace(text) ? "Claude Code did not compact its context." : $"Claude Code: {text.Trim()}");
            }

            ClaudeCodeJson.TryGetObject(compacted, "compact_metadata", out var metadata);
            var before = ClaudeCodeJson.GetInt64(metadata, "pre_tokens");
            var after = ClaudeCodeJson.GetInt64(metadata, "post_tokens");
            return new AgentCompactionOutcome(
                true,
                DescribeCompaction(compacted),
                TokensRemoved: before is not null && after is not null ? Math.Max(0, before.Value - after.Value) : null,
                PreCompactionTokens: before,
                PostCompactionTokens: after);
        }
    }

    private static string DescribeCompaction(JsonElement boundary)
    {
        ClaudeCodeJson.TryGetObject(boundary, "compact_metadata", out var metadata);
        var before = ClaudeCodeJson.GetInt64(metadata, "pre_tokens");
        var after = ClaudeCodeJson.GetInt64(metadata, "post_tokens");
        return (before, after) switch
        {
            ({ } pre, { } post) => string.Create(CultureInfo.InvariantCulture, $"Claude Code compacted its context from {pre:#,0} to {post:#,0} tokens."),
            ({ } pre, null) => string.Create(CultureInfo.InvariantCulture, $"Claude Code compacted its context of {pre:#,0} tokens."),
            _ => "Claude Code compacted its context.",
        };
    }

    private static string ContentId(Segment segment, int index)
        => $"claude:{segment.MessageId ?? "message"}:{index.ToString(CultureInfo.InvariantCulture)}";
}
