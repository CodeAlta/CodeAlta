using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using CodeAlta.Agent.LocalRuntime.Compaction;
using CodeAlta.Agent.ModelCatalog;
using CodeAlta.Agent.LocalRuntime.Tools;

namespace CodeAlta.Agent.LocalRuntime;

/// <summary>
/// Shared session implementation for provider-backed local raw-API agents.
/// </summary>
public sealed class LocalAgentSession : IAgentSession, IAgentCompactionOutcomeProvider
{
    private const string UserMessageEventType = "local.userMessage";
    private const string AssistantMessageEventType = "local.assistantMessage";
    private const string ToolMessageEventType = "local.toolMessage";
    private const string CompactionSnapshotEventType = "local.compactionSnapshot";
    private const string CompactionCheckpointEventType = "local.compactionCheckpoint";

    private readonly string _protocolFamily;
    private readonly string _providerKey;
    private readonly ILocalAgentSessionStore _store;
    private readonly ILocalAgentTurnExecutor _turnExecutor;
    private readonly AgentSessionCreateOptions _options;
    private readonly Channel<AgentEvent> _eventChannel;
    private readonly ConcurrentDictionary<Guid, Action<AgentEvent>> _subscribers = new();
    private readonly SemaphoreSlim _stateGate = new(initialCount: 1, maxCount: 1);
    private readonly List<AgentEvent> _history;
    private readonly List<LocalAgentConversationMessage> _conversation;
    private AgentModelInfo? _resolvedModelInfo;
    private bool _resolvedModelInfoLoaded;
    private LocalAgentSessionSummary _summary;
    private LocalAgentSessionState _state;
    private CancellationTokenSource? _activeRunCancellation;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="LocalAgentSession"/> class.
    /// </summary>
    /// <param name="backendId">The backend identifier.</param>
    /// <param name="provider">The configured provider descriptor.</param>
    /// <param name="summary">The persisted session summary.</param>
    /// <param name="state">The persisted session state.</param>
    /// <param name="history">The persisted session history.</param>
    /// <param name="store">The local session store.</param>
    /// <param name="turnExecutor">The provider turn executor.</param>
    /// <param name="options">The session options.</param>
    public LocalAgentSession(
        AgentBackendId backendId,
        LocalAgentProviderDescriptor provider,
        LocalAgentSessionSummary summary,
        LocalAgentSessionState state,
        IReadOnlyList<AgentEvent> history,
        ILocalAgentSessionStore store,
        ILocalAgentTurnExecutor turnExecutor,
        AgentSessionCreateOptions options)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(turnExecutor);
        ArgumentNullException.ThrowIfNull(options);

        BackendId = backendId;
        _protocolFamily = provider.ProtocolFamily;
        _providerKey = provider.ProviderKey;
        _store = store;
        _turnExecutor = turnExecutor;
        _options = options;
        _summary = summary;
        _state = state;
        _history = [.. history];
        _conversation = ReplayConversation(history, state);
        _eventChannel = Channel.CreateUnbounded<AgentEvent>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
        Provider = provider;
    }

    /// <summary>
    /// Gets the configured provider descriptor.
    /// </summary>
    public LocalAgentProviderDescriptor Provider { get; }

    /// <inheritdoc />
    public AgentBackendId BackendId { get; }

    /// <inheritdoc />
    public string SessionId => _summary.SessionId;

    /// <inheritdoc />
    public string? WorkspacePath => _summary.WorkingDirectory;

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
        _subscribers[key] = handler;
        return new LocalUnsubscriber(() => _subscribers.TryRemove(key, out _));
    }

    /// <inheritdoc />
    public async Task<AgentRunId> SendAsync(AgentSendOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var runId = new AgentRunId(Guid.CreateVersion7().ToString());
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _activeRunCancellation = linkedCts;
        }
        finally
        {
            _stateGate.Release();
        }

        try
        {
            await AppendUserMessageAsync(options.Input, runId, linkedCts.Token).ConfigureAwait(false);

            var instructionBundle = LocalAgentInstructionComposer.Compose(_options);
            _state = _state with
            {
                InstructionHash = instructionBundle.InstructionHash,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            await _store.UpsertStateAsync(_state, linkedCts.Token).ConfigureAwait(false);

            var allTools = BuildAvailableTools();
            var toolMap = LocalAgentToolBridge.CreateDefinitionMap(allTools);
            var modelInfo = await ResolveModelInfoAsync(linkedCts.Token).ConfigureAwait(false);

            while (true)
            {
                await MaybeCompactForThresholdAsync(
                        runId,
                        instructionBundle.SystemMessage,
                        instructionBundle.DeveloperInstructions,
                        modelInfo,
                        LocalAgentCompactionTrigger.Threshold,
                        linkedCts.Token)
                    .ConfigureAwait(false);

                var turnRequest = new LocalAgentTurnRequest
                {
                    Provider = Provider,
                    BackendId = BackendId,
                    SessionId = SessionId,
                    RunId = runId,
                    ModelId = _summary.ModelId ?? _options.Model,
                    ModelInfo = modelInfo,
                    WorkingDirectory = _summary.WorkingDirectory,
                    SystemMessage = instructionBundle.SystemMessage,
                    DeveloperInstructions = instructionBundle.DeveloperInstructions,
                    ReasoningEffort = _options.ReasoningEffort,
                    Conversation = _conversation.ToArray(),
                    Tools = allTools,
                    State = _state,
                };
                var response = await ExecuteTurnWithOverflowRecoveryAsync(
                        turnRequest,
                        runId,
                        instructionBundle.SystemMessage,
                        instructionBundle.DeveloperInstructions,
                        modelInfo,
                        linkedCts.Token)
                    .ConfigureAwait(false);

                await AppendAssistantMessageAsync(response, runId, linkedCts.Token).ConfigureAwait(false);

                var toolCalls = response.AssistantMessage.Parts.OfType<LocalAgentMessagePart.ToolCall>().ToArray();
                if (toolCalls.Length == 0)
                {
                    await MaybeCompactForThresholdAsync(
                            runId,
                            instructionBundle.SystemMessage,
                            instructionBundle.DeveloperInstructions,
                            modelInfo,
                            LocalAgentCompactionTrigger.Threshold,
                            linkedCts.Token)
                        .ConfigureAwait(false);

                    var idleEvent = new AgentSessionUpdateEvent(
                        BackendId,
                        SessionId,
                        DateTimeOffset.UtcNow,
                        runId,
                        AgentSessionUpdateKind.Idle,
                        null,
                        Usage: response.Usage);
                    await AppendEventsAsync([idleEvent], linkedCts.Token).ConfigureAwait(false);
                    return runId;
                }

                foreach (var toolCall in toolCalls)
                {
                    if (!toolMap.TryGetValue(toolCall.Name, out var toolDefinition))
                    {
                        throw new InvalidOperationException($"Tool '{toolCall.Name}' was not registered for session '{SessionId}'.");
                    }

                    var started = new AgentActivityEvent(
                        BackendId,
                        SessionId,
                        DateTimeOffset.UtcNow,
                        runId,
                        AgentActivityKind.ToolCall,
                        AgentActivityPhase.Started,
                        toolCall.CallId,
                        null,
                        toolCall.Name,
                        null,
                        CreateToolCallDetails(toolCall, _summary.WorkingDirectory));
                    await AppendEventsAsync([started], linkedCts.Token).ConfigureAwait(false);

                    using var progressGate = new SemaphoreSlim(1, 1);
                    var toolOutputContentId = $"{toolCall.CallId}:output";
                    var result = await toolDefinition.Handler(
                            new AgentToolInvocation(
                                BackendId,
                                SessionId,
                                toolCall.CallId,
                                toolDefinition.Spec.Name,
                                toolCall.Arguments,
                                async (update, cancellationToken) =>
                                {
                                    if (string.IsNullOrEmpty(update.Delta))
                                    {
                                        return;
                                    }

                                    await progressGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                                    try
                                    {
                                        var deltaEvent = new AgentContentDeltaEvent(
                                            BackendId,
                                            SessionId,
                                            DateTimeOffset.UtcNow,
                                            runId,
                                            AgentContentKind.ToolOutput,
                                            toolOutputContentId,
                                            toolCall.CallId,
                                            update.Delta,
                                            update.Details);
                                        await AppendEventsAsync([deltaEvent], LocalAgentEventPersistenceMode.TransientOnly, cancellationToken).ConfigureAwait(false);
                                    }
                                    finally
                                    {
                                        progressGate.Release();
                                    }
                                }),
                            linkedCts.Token)
                        .ConfigureAwait(false);
                    var toolMessage = new LocalAgentConversationMessage(
                        LocalAgentConversationRole.Tool,
                        [new LocalAgentMessagePart.ToolResult(toolCall.CallId, result)]);
                    _conversation.Add(toolMessage);

                    var completed = new AgentActivityEvent(
                        BackendId,
                        SessionId,
                        DateTimeOffset.UtcNow,
                        runId,
                        AgentActivityKind.ToolCall,
                        result.Success ? AgentActivityPhase.Completed : AgentActivityPhase.Failed,
                        toolCall.CallId,
                        null,
                        toolCall.Name,
                        result.Error,
                        CreateToolResultDetails(toolCall, result, _summary.WorkingDirectory));
                    var rawToolEvent = new AgentRawEvent(
                        BackendId,
                        SessionId,
                        DateTimeOffset.UtcNow,
                        ToolMessageEventType,
                        SerializeLocalMessage(toolMessage),
                        runId);
                    var toolOutputText = new AgentContentCompletedEvent(
                        BackendId,
                        SessionId,
                        DateTimeOffset.UtcNow,
                        runId,
                        AgentContentKind.ToolOutput,
                        toolOutputContentId,
                        toolCall.CallId,
                        RenderToolResult(result),
                        CreateToolResultDetails(toolCall, result, _summary.WorkingDirectory));
                    await AppendEventsAsync([rawToolEvent, completed, toolOutputText], linkedCts.Token).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            await _stateGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                _activeRunCancellation = null;
            }
            finally
            {
                _stateGate.Release();
            }
        }
    }

    /// <inheritdoc />
    public Task<AgentRunId> SteerAsync(AgentSteerOptions options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Local raw-API sessions do not support steering while a turn is executing.");

    /// <inheritdoc />
    public Task AbortAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _activeRunCancellation?.Cancel();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task CompactAsync(CancellationToken cancellationToken = default)
        => CompactWithOutcomeAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<AgentCompactionOutcome?> CompactWithOutcomeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var instructionBundle = LocalAgentInstructionComposer.Compose(_options);
            var modelInfo = await ResolveModelInfoAsync(cancellationToken).ConfigureAwait(false);
            var outcome = await CompactCoreAsync(
                    trigger: LocalAgentCompactionTrigger.Manual,
                    runId: null,
                    systemMessage: instructionBundle.SystemMessage,
                    developerInstructions: instructionBundle.DeveloperInstructions,
                    modelInfo: modelInfo,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (outcome is null)
            {
                return new AgentCompactionOutcome(true, "Nothing to compact.");
            }

            return new AgentCompactionOutcome(
                Success: true,
                Message: outcome.Message,
                MessagesRemoved: outcome.MessagesRemoved,
                TokensRemoved: outcome.TokensRemoved,
                PreCompactionTokens: outcome.PreCompactionTokens,
                PostCompactionTokens: outcome.PostCompactionTokens);
        }
        finally
        {
            _stateGate.Release();
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<AgentEvent>> GetHistoryAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<AgentEvent>>([.. _history]);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _activeRunCancellation?.Cancel();
        _activeRunCancellation?.Dispose();
        _stateGate.Dispose();
        _eventChannel.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    private IReadOnlyList<AgentToolDefinition> BuildAvailableTools()
    {
        var builtIns = LocalAgentBuiltInToolFactory.CreateDefaultTools(
            new LocalAgentBuiltInToolOptions
            {
                BackendId = BackendId,
                SessionId = SessionId,
                WorkingDirectory = _summary.WorkingDirectory,
                OnPermissionRequest = _options.OnPermissionRequest,
                OnUserInputRequest = _options.OnUserInputRequest,
            });
        return _options.Tools is { Count: > 0 }
            ? [.. builtIns, .. _options.Tools]
            : builtIns;
    }

    private async Task AppendUserMessageAsync(AgentInput input, AgentRunId runId, CancellationToken cancellationToken)
    {
        var message = new LocalAgentConversationMessage(
            LocalAgentConversationRole.User,
            MapInputItems(input.Items));
        _conversation.Add(message);

        var rawEvent = new AgentRawEvent(
            BackendId,
            SessionId,
            DateTimeOffset.UtcNow,
            UserMessageEventType,
            SerializeAgentInput(input),
            runId);
        var userContent = new AgentContentCompletedEvent(
            BackendId,
            SessionId,
            DateTimeOffset.UtcNow,
            runId,
            AgentContentKind.User,
            $"user:{Guid.CreateVersion7()}",
            null,
            RenderUserInput(input.Items),
            SerializeAgentInput(input));
        await AppendEventsAsync([rawEvent, userContent], cancellationToken).ConfigureAwait(false);
    }

    private async Task AppendAssistantMessageAsync(
        LocalAgentTurnResponse response,
        AgentRunId runId,
        CancellationToken cancellationToken)
    {
        _conversation.Add(response.AssistantMessage);
        var effectiveUsage = LocalAgentUsageFactory.AttachMessageCount(response.Usage, _conversation.Count);

        _state = _state with
        {
            ProviderSessionId = response.ProviderSessionId ?? _state.ProviderSessionId,
            ProviderState = response.ProviderState ?? _state.ProviderState,
            Usage = effectiveUsage ?? _state.Usage,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        _summary = _summary with
        {
            ModelId = _summary.ModelId ?? _options.Model,
            Title = response.Title ?? _summary.Title,
            Summary = response.Summary ?? ExtractAssistantSummary(response.AssistantMessage) ?? _summary.Summary,
            Usage = effectiveUsage ?? _summary.Usage,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        var events = new List<AgentEvent>
        {
            new AgentRawEvent(
                BackendId,
                SessionId,
                DateTimeOffset.UtcNow,
                AssistantMessageEventType,
                SerializeLocalMessage(response.AssistantMessage),
                runId),
        };

        var assistantPartContentIds = response.AssistantPartContentIds;
        for (var partIndex = 0; partIndex < response.AssistantMessage.Parts.Count; partIndex++)
        {
            var part = response.AssistantMessage.Parts[partIndex];
            var partContentId = assistantPartContentIds is not null &&
                                partIndex < assistantPartContentIds.Count &&
                                !string.IsNullOrWhiteSpace(assistantPartContentIds[partIndex])
                ? assistantPartContentIds[partIndex]
                : null;
            switch (part)
            {
                case LocalAgentMessagePart.Text text:
                    events.Add(new AgentContentCompletedEvent(
                        BackendId,
                        SessionId,
                        DateTimeOffset.UtcNow,
                        runId,
                        AgentContentKind.Assistant,
                        partContentId ?? $"assistant:{Guid.CreateVersion7()}",
                        null,
                        text.Value));
                    break;
                case LocalAgentMessagePart.Reasoning reasoning:
                    events.Add(new AgentContentCompletedEvent(
                        BackendId,
                        SessionId,
                        DateTimeOffset.UtcNow,
                        runId,
                        AgentContentKind.Reasoning,
                        partContentId ?? $"reasoning:{Guid.CreateVersion7()}",
                        null,
                        reasoning.Value ?? string.Empty,
                        CreateReasoningDetails(reasoning)));
                    break;
                case LocalAgentMessagePart.ToolCall toolCall:
                    events.Add(new AgentActivityEvent(
                        BackendId,
                        SessionId,
                        DateTimeOffset.UtcNow,
                        runId,
                        AgentActivityKind.ToolCall,
                        AgentActivityPhase.Requested,
                        toolCall.CallId,
                        null,
                        toolCall.Name,
                        null,
                        CreateToolCallDetails(toolCall, _summary.WorkingDirectory)));
                    break;
            }
        }

        if (effectiveUsage is not null)
        {
            events.Add(new AgentSessionUpdateEvent(
                BackendId,
                SessionId,
                DateTimeOffset.UtcNow,
                runId,
                AgentSessionUpdateKind.UsageUpdated,
                "Usage updated.",
                Usage: effectiveUsage));
        }

        await AppendEventsAsync(events, cancellationToken).ConfigureAwait(false);
        await _store.UpsertStateAsync(_state, cancellationToken).ConfigureAwait(false);
        await _store.UpsertSessionAsync(_summary, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask OnStreamingDeltaAsync(
        AgentRunId runId,
        LocalAgentTurnDelta delta,
        CancellationToken cancellationToken)
    {
        var @event = new AgentContentDeltaEvent(
            BackendId,
            SessionId,
            DateTimeOffset.UtcNow,
            runId,
            delta.Kind,
            delta.ContentId,
            null,
            delta.Text);
        await AppendEventsAsync([@event], LocalAgentEventPersistenceMode.TransientOnly, cancellationToken).ConfigureAwait(false);
    }

    private async Task AppendEventsAsync(
        IReadOnlyList<AgentEvent> events,
        CancellationToken cancellationToken)
        => await AppendEventsAsync(events, LocalAgentEventPersistenceMode.DurableCanonical, cancellationToken).ConfigureAwait(false);

    private async Task AppendEventsAsync(
        IReadOnlyList<AgentEvent> events,
        LocalAgentEventPersistenceMode persistenceMode,
        CancellationToken cancellationToken)
    {
        if (events.Count == 0)
        {
            return;
        }

        _history.AddRange(events);
        if (persistenceMode is LocalAgentEventPersistenceMode.DurableCanonical)
        {
            await _store.AppendEventsAsync(_protocolFamily, _providerKey, SessionId, events, cancellationToken).ConfigureAwait(false);
        }

        foreach (var @event in events)
        {
            if (_eventChannel.Writer.TryWrite(@event))
            {
                foreach (var subscriber in _subscribers.Values)
                {
                    try
                    {
                        subscriber(@event);
                    }
                    catch
                    {
                    }
                }
            }
        }
    }

    private async Task<LocalAgentTurnResponse> ExecuteTurnWithOverflowRecoveryAsync(
        LocalAgentTurnRequest request,
        AgentRunId runId,
        string? systemMessage,
        string? developerInstructions,
        AgentModelInfo? modelInfo,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _turnExecutor.ExecuteTurnAsync(
                    request,
                    (delta, ct) => OnStreamingDeltaAsync(runId, delta, ct),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (LocalAgentTurnExecutionException ex) when (ex.Failure.IsContextOverflow)
        {
            var compacted = await CompactCoreAsync(
                    LocalAgentCompactionTrigger.Overflow,
                    runId,
                    systemMessage,
                    developerInstructions,
                    modelInfo,
                    cancellationToken)
                .ConfigureAwait(false);
            if (compacted is null)
            {
                throw;
            }

            try
            {
                return await _turnExecutor.ExecuteTurnAsync(
                        request with
                        {
                            Conversation = _conversation.ToArray(),
                            State = _state,
                        },
                        (delta, ct) => OnStreamingDeltaAsync(runId, delta, ct),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (LocalAgentTurnExecutionException retryEx) when (retryEx.Failure.IsContextOverflow)
            {
                throw;
            }
        }
    }

    private async Task MaybeCompactForThresholdAsync(
        AgentRunId? runId,
        string? systemMessage,
        string? developerInstructions,
        AgentModelInfo? modelInfo,
        LocalAgentCompactionTrigger trigger,
        CancellationToken cancellationToken)
    {
        var settings = Provider.Compaction ?? LocalAgentCompactionSettings.Default;
        if (!settings.Enabled)
        {
            return;
        }

        var budget = LocalAgentTokenBudgetResolver.Resolve(modelInfo, settings);
        if (budget.UsablePromptBudget is null)
        {
            return;
        }

        if (budget.UsablePromptBudget <= 0)
        {
            throw new InvalidOperationException("The resolved prompt budget is not usable after reserved output and overhead tokens.");
        }

        if (budget.OutputTokenLimit is not null && settings.ReservedOutputTokens > budget.OutputTokenLimit.Value)
        {
            throw new InvalidOperationException("The reserved output token budget exceeds the model's output token limit.");
        }

        var estimate = LocalAgentTokenEstimator.EstimatePromptTokens(
            systemMessage,
            developerInstructions,
            _conversation,
            _state.Usage);
        var thresholdTokens = Math.Max((long)Math.Floor(budget.UsablePromptBudget.Value * settings.TriggerThreshold), 1);
        if (estimate.Tokens < thresholdTokens)
        {
            return;
        }

        _ = await CompactCoreAsync(
                trigger,
                runId,
                systemMessage,
                developerInstructions,
                modelInfo,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<AgentCompactionOutcome?> CompactCoreAsync(
        LocalAgentCompactionTrigger trigger,
        AgentRunId? runId,
        string? systemMessage,
        string? developerInstructions,
        AgentModelInfo? modelInfo,
        CancellationToken cancellationToken)
    {
        var settings = Provider.Compaction ?? LocalAgentCompactionSettings.Default;
        if (trigger is not LocalAgentCompactionTrigger.Manual && !settings.Enabled)
        {
            return null;
        }

        var budget = LocalAgentTokenBudgetResolver.Resolve(modelInfo, settings);
        if (budget.UsablePromptBudget is <= 0)
        {
            throw new InvalidOperationException("The resolved prompt budget is not usable after reserved output and overhead tokens.");
        }

        if (budget.OutputTokenLimit is not null && settings.ReservedOutputTokens > budget.OutputTokenLimit.Value)
        {
            throw new InvalidOperationException("The reserved output token budget exceeds the model's output token limit.");
        }

        var preparation = LocalAgentCompactionPlanner.Prepare(
            trigger,
            systemMessage,
            developerInstructions,
            _conversation,
            _state.Usage,
            budget,
            settings,
            FindLatestUserContentId());
        if (preparation is null)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var started = new AgentSessionUpdateEvent(
            BackendId,
            SessionId,
            now,
            runId,
            AgentSessionUpdateKind.CompactionStarted,
            $"{trigger} local compaction started.");

        var firstKeptEventOffset = TryResolveFirstKeptEventOffset(preparation.MessagesToKeep);
        var checkpointContentId = $"compaction:{Guid.CreateVersion7()}";
        var postCompactionConversation = new List<LocalAgentConversationMessage>(preparation.MessagesToKeep.Count + 1);
        var latestUserRequest = GetLatestUserRequest(_conversation);
        var tentativeSummary = LocalAgentCompactionSummarizer.Summarize(
            preparation,
            _history,
            latestUserRequest,
            tokensAfter: null);
        var checkpoint = new LocalAgentCompactionCheckpoint
        {
            Version = 1,
            ContentId = checkpointContentId,
            Trigger = trigger.ToString().ToLowerInvariant(),
            Summary = tentativeSummary.Summary,
            FirstKeptEventOffset = firstKeptEventOffset,
            AnchorContentId = preparation.AnchorContentId,
            TokensBefore = tentativeSummary.TokensBefore,
            SummarizedMessageCount = tentativeSummary.MessagesSummarized,
            ReadFiles = tentativeSummary.ReadFiles,
            ModifiedFiles = tentativeSummary.ModifiedFiles,
            KeptMessages = preparation.MessagesToKeep,
        };
        var checkpointMessage = checkpoint.CreateMessage();
        postCompactionConversation.Add(checkpointMessage);
        postCompactionConversation.AddRange(preparation.MessagesToKeep);
        var tokensAfter = LocalAgentTokenEstimator.EstimatePromptTokens(
            systemMessage,
            developerInstructions,
            postCompactionConversation,
            usage: null).Tokens;
        var result = LocalAgentCompactionSummarizer.Summarize(
            preparation,
            _history,
            latestUserRequest,
            tokensAfter);
        checkpoint = checkpoint with
        {
            Summary = result.Summary,
            TokensAfter = result.TokensAfter,
            ReadFiles = result.ReadFiles,
            ModifiedFiles = result.ModifiedFiles,
        };
        checkpointMessage = checkpoint.CreateMessage();

        _conversation.Clear();
        _conversation.Add(checkpointMessage);
        _conversation.AddRange(preparation.MessagesToKeep);

        var usage = CreateCompactionUsage(result, budget, _conversation.Count, _state.Usage);
        _state = _state with
        {
            CompactionEventOffset = CountDurableEvents() + 2,
            CompactionSummaryContentId = checkpoint.ContentId,
            CompactionCheckpointEventId = checkpoint.ContentId,
            LastCompactedAt = now,
            LastCompactionTrigger = checkpoint.Trigger,
            LastCompactionTokensBefore = result.TokensBefore,
            LastCompactionTokensAfter = result.TokensAfter,
            Usage = usage,
            UpdatedAt = now,
        };
        _summary = _summary with
        {
            Usage = usage,
            UpdatedAt = now,
        };

        var rawCheckpoint = new AgentRawEvent(
            BackendId,
            SessionId,
            now,
            CompactionCheckpointEventType,
            JsonSerializer.SerializeToElement(checkpoint, AgentJsonSerializerContext.Default.LocalAgentCompactionCheckpoint),
            runId);
        var completed = new AgentSessionUpdateEvent(
            BackendId,
            SessionId,
            now,
            runId,
            AgentSessionUpdateKind.CompactionCompleted,
            $"{trigger} local compaction summarized {result.MessagesSummarized} messages.",
            Usage: usage);
        await AppendEventsAsync([started, rawCheckpoint, completed], cancellationToken).ConfigureAwait(false);
        await _store.UpsertStateAsync(_state, cancellationToken).ConfigureAwait(false);
        await _store.UpsertSessionAsync(_summary, cancellationToken).ConfigureAwait(false);

        long? tokensRemoved = result.TokensAfter is null ? null : Math.Max(0, result.TokensBefore - result.TokensAfter.Value);
        return new AgentCompactionOutcome(
            Success: true,
            Message: completed.Message,
            MessagesRemoved: result.MessagesSummarized,
            TokensRemoved: tokensRemoved,
            PreCompactionTokens: result.TokensBefore,
            PostCompactionTokens: result.TokensAfter);
    }

    private static AgentSessionUsage? CreateCompactionUsage(
        LocalAgentCompactionResult result,
        LocalAgentTokenBudget budget,
        int messageCount,
        AgentSessionUsage? previousUsage)
    {
        return new AgentSessionUsage(
            Window: new AgentWindowUsageSnapshot(
                CurrentTokens: result.TokensAfter,
                TokenLimit: budget.ContextWindow,
                MessageCount: messageCount,
                Label: "Post-compaction window"),
            LastOperation: previousUsage?.LastOperation,
            RateLimits: previousUsage?.RateLimits,
            Scope: AgentUsageScope.Compaction,
            Source: AgentUsageSource.RecoveredHistory,
            UpdatedAt: DateTimeOffset.UtcNow,
            Details: previousUsage?.Details);
    }

    private long CountDurableEvents()
        => _history.Count(static @event => @event is not AgentContentDeltaEvent);

    private string? FindLatestUserContentId()
        => _history
            .OfType<AgentContentCompletedEvent>()
            .LastOrDefault(static @event => @event.Kind is AgentContentKind.User)
            ?.ContentId;

    private static string? GetLatestUserRequest(IReadOnlyList<LocalAgentConversationMessage> conversation)
        => conversation
            .Reverse()
            .Where(static message => message.Role is LocalAgentConversationRole.User)
            .SelectMany(static message => message.Parts.OfType<LocalAgentMessagePart.Text>())
            .Select(static part => part.Value)
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));

    private long? TryResolveFirstKeptEventOffset(IReadOnlyList<LocalAgentConversationMessage> keptMessages)
    {
        if (keptMessages.Count == 0)
        {
            return null;
        }

        var conversationStartIndex = _conversation.Count > 0 &&
                                     LocalAgentCompactionCheckpoint.TryExtractSummary(_conversation[0]) is not null
            ? 1
            : 0;
        var firstKeptConversationIndex = -1;
        for (var index = conversationStartIndex; index < _conversation.Count; index++)
        {
            if (ReferenceEquals(_conversation[index], keptMessages[0]))
            {
                firstKeptConversationIndex = index - conversationStartIndex;
                break;
            }
        }

        if (firstKeptConversationIndex < 0)
        {
            return null;
        }

        var replayableOffsets = GetReplayableRawEventOffsets();
        return firstKeptConversationIndex < replayableOffsets.Count
            ? replayableOffsets[firstKeptConversationIndex].Offset
            : null;
    }

    private IReadOnlyList<(long Offset, string BackendEventType)> GetReplayableRawEventOffsets()
    {
        var results = new List<(long Offset, string BackendEventType)>();
        long offset = 0;
        foreach (var @event in _history)
        {
            if (@event is AgentContentDeltaEvent)
            {
                continue;
            }

            offset++;
            if (@event is not AgentRawEvent rawEvent)
            {
                continue;
            }

            if (rawEvent.BackendEventType == UserMessageEventType)
            {
                var input = rawEvent.Raw.Deserialize(AgentJsonSerializerContext.Default.AgentInput);
                if (input is not null)
                {
                    results.Add((offset, rawEvent.BackendEventType));
                }

                continue;
            }

            if (rawEvent.BackendEventType is AssistantMessageEventType or ToolMessageEventType)
            {
                var message = rawEvent.Raw.Deserialize(AgentJsonSerializerContext.Default.LocalAgentConversationMessage);
                if (message is not null)
                {
                    results.Add((offset, rawEvent.BackendEventType));
                }
            }
        }

        return results;
    }

    private static IReadOnlyList<LocalAgentMessagePart> MapInputItems(IReadOnlyList<AgentInputItem> items)
    {
        var parts = new List<LocalAgentMessagePart>(items.Count);
        foreach (var item in items)
        {
            switch (item)
            {
                case AgentInputItem.Text text:
                    parts.Add(new LocalAgentMessagePart.Text(text.Value));
                    break;
                case AgentInputItem.ImageUrl imageUrl:
                    parts.Add(new LocalAgentMessagePart.Uri(imageUrl.Url, GuessMediaType(imageUrl.Url)));
                    break;
                case AgentInputItem.LocalImage localImage:
                    parts.Add(new LocalAgentMessagePart.Data(
                        Convert.ToBase64String(File.ReadAllBytes(localImage.Path)),
                        GuessMediaType(localImage.Path) ?? "application/octet-stream",
                        Path.GetFileName(localImage.Path)));
                    break;
                case AgentInputItem.File file:
                    parts.Add(new LocalAgentMessagePart.Text(RenderFileInput(file)));
                    break;
                case AgentInputItem.Directory directory:
                    parts.Add(new LocalAgentMessagePart.Text(
                        string.IsNullOrWhiteSpace(directory.DisplayName)
                            ? $"Directory: {directory.Path}"
                            : $"Directory ({directory.DisplayName}): {directory.Path}"));
                    break;
                case AgentInputItem.Selection selection:
                    parts.Add(new LocalAgentMessagePart.Text(
                        $"""
                        Selection: {selection.DisplayName}
                        File: {selection.FilePath}
                        Text:
                        {selection.SelectedText}
                        """));
                    break;
                case AgentInputItem.Skill skill:
                    parts.Add(new LocalAgentMessagePart.Text($"Skill: {skill.Name} ({skill.Path})"));
                    break;
                case AgentInputItem.Mention mention:
                    parts.Add(new LocalAgentMessagePart.Text($"Mention: {mention.Name} ({mention.Path})"));
                    break;
            }
        }

        return parts;
    }

    private static string RenderUserInput(IReadOnlyList<AgentInputItem> items)
        => string.Join(
            Environment.NewLine,
            items.Select(static item => item switch
            {
                AgentInputItem.Text text => text.Value,
                AgentInputItem.ImageUrl imageUrl => $"Image URL: {imageUrl.Url}",
                AgentInputItem.LocalImage localImage => $"Local image: {localImage.Path}",
                AgentInputItem.File file => RenderFileInput(file),
                AgentInputItem.Directory directory => $"Directory: {directory.Path}",
                AgentInputItem.Selection selection => $"Selection: {selection.FilePath}",
                AgentInputItem.Skill skill => $"Skill: {skill.Name}",
                AgentInputItem.Mention mention => $"Mention: {mention.Name}",
                _ => string.Empty,
            }));

    private static string RenderFileInput(AgentInputItem.File file)
        => file.LineRange is null
            ? $"File: {file.Path}"
            : $"File: {file.Path} ({file.LineRange.StartLine}-{file.LineRange.EndLine})";

    private static string RenderToolResult(AgentToolResult result)
    {
        var segments = result.Items.Select(static item => item switch
        {
            AgentToolResultItem.Text text => text.Value,
            AgentToolResultItem.ImageUrl imageUrl => imageUrl.Url,
            _ => string.Empty,
        });
        var rendered = string.Join(Environment.NewLine, segments.Where(static segment => !string.IsNullOrWhiteSpace(segment)));
        return string.IsNullOrWhiteSpace(rendered)
            ? (result.Error ?? "(no output)")
            : rendered;
    }

    private static string? ExtractAssistantSummary(LocalAgentConversationMessage message)
        => message.Parts.OfType<LocalAgentMessagePart.Text>().Select(static part => part.Value).FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));

    private static string? GuessMediaType(string pathOrUri)
        => Path.GetExtension(pathOrUri).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".pdf" => "application/pdf",
            ".txt" => "text/plain",
            _ => null,
        };

    private static JsonElement SerializeAgentInput(AgentInput input)
        => JsonSerializer.SerializeToElement(input, AgentJsonSerializerContext.Default.AgentInput);

    private static JsonElement SerializeLocalMessage(LocalAgentConversationMessage message)
        => JsonSerializer.SerializeToElement(message, AgentJsonSerializerContext.Default.LocalAgentConversationMessage);

    private static JsonElement? CreateReasoningDetails(LocalAgentMessagePart.Reasoning reasoning)
    {
        if (string.IsNullOrWhiteSpace(reasoning.ProtectedData))
        {
            return null;
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("protectedData", reasoning.ProtectedData);
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    private static JsonElement CreateToolCallDetails(LocalAgentMessagePart.ToolCall toolCall, string? workingDirectory)
    {
        var fileActivity = GetToolFileActivity(toolCall, workingDirectory);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("toolCallId", toolCall.CallId);
            writer.WriteString("toolName", toolCall.Name);
            writer.WritePropertyName("arguments");
            toolCall.Arguments.WriteTo(writer);
            WritePaths(writer, "readFiles", fileActivity.ReadFiles);
            WritePaths(writer, "modifiedFiles", fileActivity.ModifiedFiles);
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    private static JsonElement CreateToolResultDetails(LocalAgentMessagePart.ToolCall toolCall, AgentToolResult result, string? workingDirectory)
    {
        var fileActivity = GetToolFileActivity(toolCall, workingDirectory);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("toolCallId", toolCall.CallId);
            writer.WriteString("toolName", toolCall.Name);
            writer.WritePropertyName("arguments");
            toolCall.Arguments.WriteTo(writer);
            WritePaths(writer, "readFiles", fileActivity.ReadFiles);
            WritePaths(writer, "modifiedFiles", fileActivity.ModifiedFiles);
            writer.WritePropertyName("result");
            JsonSerializer.Serialize(writer, result, AgentJsonSerializerContext.Default.AgentToolResult);
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    private static void WritePaths(Utf8JsonWriter writer, string propertyName, IReadOnlyList<string> paths)
    {
        writer.WritePropertyName(propertyName);
        writer.WriteStartArray();
        foreach (var path in paths)
        {
            writer.WriteStringValue(path);
        }

        writer.WriteEndArray();
    }

    private static ToolFileActivity GetToolFileActivity(LocalAgentMessagePart.ToolCall toolCall, string? workingDirectory)
    {
        static string? GetPath(JsonElement arguments, string propertyName)
            => arguments.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        string Resolve(string path)
            => Path.GetFullPath(Path.IsPathRooted(path)
                ? path
                : Path.Combine(workingDirectory ?? Environment.CurrentDirectory, path));

        var readFiles = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var modifiedFiles = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        switch (toolCall.Name)
        {
            case "read_file":
            case "view_image":
                if (GetPath(toolCall.Arguments, "path") is { Length: > 0 } readPath)
                {
                    readFiles.Add(Resolve(readPath));
                }

                break;
            case "apply_patch":
                if (toolCall.Arguments.TryGetProperty("input", out var patchInput) &&
                    patchInput.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(patchInput.GetString()))
                {
                    foreach (var path in LocalAgentApplyPatch.GetTouchedPaths(patchInput.GetString()!, workingDirectory ?? Environment.CurrentDirectory))
                    {
                        modifiedFiles.Add(path);
                    }
                }

                break;
        }

        return new ToolFileActivity([.. readFiles], [.. modifiedFiles]);
    }

    private static List<LocalAgentConversationMessage> ReplayConversation(
        IReadOnlyList<AgentEvent> history,
        LocalAgentSessionState state)
    {
        var conversation = new List<LocalAgentConversationMessage>();
        LocalAgentCompactionCheckpoint? latestCheckpoint = null;
        long durableOffset = 0;
        foreach (var @event in history)
        {
            if (@event is AgentContentDeltaEvent)
            {
                continue;
            }

            durableOffset++;
            if (@event is not AgentRawEvent rawEvent)
            {
                continue;
            }

            if (rawEvent.BackendEventType == CompactionCheckpointEventType)
            {
                var checkpoint = rawEvent.Raw.Deserialize(AgentJsonSerializerContext.Default.LocalAgentCompactionCheckpoint);
                if (checkpoint is not null &&
                    (string.IsNullOrWhiteSpace(state.CompactionCheckpointEventId) ||
                     string.Equals(checkpoint.ContentId, state.CompactionCheckpointEventId, StringComparison.Ordinal)))
                {
                    latestCheckpoint = checkpoint;
                }
            }
        }

        if (latestCheckpoint is not null)
        {
            conversation.Add(latestCheckpoint.CreateMessage());
            conversation.AddRange(latestCheckpoint.KeptMessages);
        }

        durableOffset = 0;
        var includeAfterCheckpoint = latestCheckpoint is null;
        foreach (var @event in history)
        {
            if (@event is AgentContentDeltaEvent)
            {
                continue;
            }

            durableOffset++;
            if (@event is not AgentRawEvent rawEvent)
            {
                continue;
            }

            if (latestCheckpoint is not null && rawEvent.BackendEventType == CompactionCheckpointEventType)
            {
                var checkpoint = rawEvent.Raw.Deserialize(AgentJsonSerializerContext.Default.LocalAgentCompactionCheckpoint);
                includeAfterCheckpoint = checkpoint is not null &&
                                         string.Equals(checkpoint.ContentId, latestCheckpoint.ContentId, StringComparison.Ordinal);
                continue;
            }

            if (!includeAfterCheckpoint)
            {
                continue;
            }

            if (rawEvent.BackendEventType == UserMessageEventType)
            {
                var input = rawEvent.Raw.Deserialize(AgentJsonSerializerContext.Default.AgentInput);
                if (input is not null)
                {
                    conversation.Add(new LocalAgentConversationMessage(
                        LocalAgentConversationRole.User,
                        MapInputItems(input.Items)));
                }

                continue;
            }

            if (rawEvent.BackendEventType is AssistantMessageEventType or ToolMessageEventType)
            {
                var message = rawEvent.Raw.Deserialize(AgentJsonSerializerContext.Default.LocalAgentConversationMessage);
                if (message is not null)
                {
                    conversation.Add(message);
                }

                continue;
            }

            if (latestCheckpoint is null && rawEvent.BackendEventType == CompactionSnapshotEventType)
            {
                var snapshot = rawEvent.Raw.Deserialize(AgentJsonSerializerContext.Default.LocalAgentCompactionSnapshot);
                conversation.Clear();
                if (snapshot is not null)
                {
                    conversation.Add(snapshot.SummaryMessage);
                }
            }
        }

        return conversation;
    }

    private static LocalAgentCompactionSnapshot CreateCompactionSnapshot(
        int includedEventCount,
        IReadOnlyList<LocalAgentConversationMessage> conversation)
    {
        var builder = new StringBuilder();
        builder.AppendLine("<compacted_history>");
        builder.AppendLine("This is a local summary of earlier conversation state. Treat it as authoritative context.");
        builder.AppendLine();

        foreach (var message in conversation)
        {
            var line = RenderCompactionLine(message);
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (builder.Length + line.Length + Environment.NewLine.Length > 12_000)
            {
                builder.AppendLine("- ... earlier messages omitted for brevity ...");
                break;
            }

            builder.Append("- ");
            builder.AppendLine(line);
        }

        builder.AppendLine("</compacted_history>");
        return new LocalAgentCompactionSnapshot
        {
            IncludedEventCount = includedEventCount,
            SummarizedMessageCount = conversation.Count,
            SummaryMessage = new LocalAgentConversationMessage(
                LocalAgentConversationRole.System,
                [new LocalAgentMessagePart.Text(builder.ToString().Trim())]),
        };
    }

    private static string RenderCompactionLine(LocalAgentConversationMessage message)
    {
        var segments = new List<string>(message.Parts.Count);
        foreach (var part in message.Parts)
        {
            switch (part)
            {
                case LocalAgentMessagePart.Text text when !string.IsNullOrWhiteSpace(text.Value):
                    segments.Add(CompactText(text.Value));
                    break;
                case LocalAgentMessagePart.Reasoning reasoning when !string.IsNullOrWhiteSpace(reasoning.Value):
                    segments.Add($"reasoning: {CompactText(reasoning.Value)}");
                    break;
                case LocalAgentMessagePart.ToolCall toolCall:
                    segments.Add($"tool call {toolCall.Name}#{toolCall.CallId}");
                    break;
                case LocalAgentMessagePart.ToolResult toolResult:
                    segments.Add($"tool result {toolResult.CallId}: {CompactText(RenderToolResult(toolResult.Result))}");
                    break;
                case LocalAgentMessagePart.Uri uri:
                    segments.Add($"uri: {uri.Value}");
                    break;
                case LocalAgentMessagePart.Data data:
                    segments.Add($"data: {data.Name ?? data.MediaType}");
                    break;
            }
        }

        if (segments.Count == 0)
        {
            return string.Empty;
        }

        var role = message.Role switch
        {
            LocalAgentConversationRole.User => "User",
            LocalAgentConversationRole.Assistant => "Assistant",
            LocalAgentConversationRole.Tool => "Tool",
            LocalAgentConversationRole.System => "System",
            _ => "Message",
        };
        return $"{role}: {string.Join(" | ", segments)}";
    }

    private static string CompactText(string text)
    {
        var condensed = string.Join(
            " ",
            text.Split(['\r', '\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries));
        return condensed.Length <= 240
            ? condensed
            : condensed[..237] + "...";
    }

    private sealed record ToolFileActivity(IReadOnlyList<string> ReadFiles, IReadOnlyList<string> ModifiedFiles);

    private sealed class LocalUnsubscriber(Action dispose) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            dispose();
        }
    }

    private async Task<AgentModelInfo?> ResolveModelInfoAsync(CancellationToken cancellationToken)
    {
        if (_resolvedModelInfoLoaded)
        {
            return _resolvedModelInfo;
        }

        _resolvedModelInfoLoaded = true;
        var modelId = _summary.ModelId ?? _options.Model;
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return null;
        }

        try
        {
            var models = await _turnExecutor.ListModelsAsync(Provider, cancellationToken).ConfigureAwait(false);
            _resolvedModelInfo = AgentModelIdentity.FindBestMatch(models, modelId);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            _resolvedModelInfo = null;
        }

        return _resolvedModelInfo;
    }
}
