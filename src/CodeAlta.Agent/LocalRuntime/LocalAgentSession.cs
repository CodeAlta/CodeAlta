using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    private const string SkillActivationEventType = "local.skillActivation";
    private const string CompactionSnapshotEventType = "local.compactionSnapshot";
    private const string CompactionCheckpointEventType = "local.compactionCheckpoint";
    private static readonly Regex SkillContentTagRegex = new(
        "<skill_content\\b(?<attributes>[^>]*)>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex XmlAttributeRegex = new(
        "(?<name>[A-Za-z_:][-A-Za-z0-9_:.]*)=\"(?<value>[^\"]*)\"",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly string _protocolFamily;
    private readonly string _providerKey;
    private readonly ILocalAgentSessionStore _store;
    private readonly ILocalAgentTurnExecutor _turnExecutor;
    private readonly LocalAgentCompactionSummarizer _compactionSummarizer;
    private readonly AgentSessionCreateOptions _options;
    private readonly Channel<AgentEvent> _eventChannel;
    private readonly ConcurrentDictionary<Guid, Action<AgentEvent>> _subscribers = new();
    private readonly SemaphoreSlim _stateGate = new(initialCount: 1, maxCount: 1);
    private readonly List<AgentEvent> _history;
    private readonly List<LocalAgentConversationMessage> _conversation;
    private readonly Queue<AgentInput> _pendingSteerInputs = new();
    private AgentModelInfo? _resolvedModelInfo;
    private bool _resolvedModelInfoLoaded;
    private LocalAgentSessionSummary _summary;
    private LocalAgentSessionState _state;
    private AgentRunId? _activeRunId;
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
        _compactionSummarizer = new LocalAgentCompactionSummarizer(new LocalAgentTurnExecutorCompactionSummaryExecutor(turnExecutor));
        _options = options;
        _summary = summary;
        _state = RebuildLoadedSkillsState(state, history);
        _history = [.. history];
        _conversation = ReplayConversation(history, _state);
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
            if (_activeRunId is not null)
            {
                throw new InvalidOperationException($"Local raw-API session '{SessionId}' already has an active run.");
            }

            _activeRunId = runId;
            _activeRunCancellation = linkedCts;
            _pendingSteerInputs.Clear();
        }
        finally
        {
            _stateGate.Release();
        }

        try
        {
            var fileChangeTracker = new LocalAgentTurnFileChangeTracker(_summary.WorkingDirectory);
            await AppendUserMessageAsync(options.Input, runId, linkedCts.Token).ConfigureAwait(false);

            var instructionBundle = LocalAgentInstructionComposer.Compose(_options, _state.LoadedSkills);
            _state = _state with
            {
                InstructionHash = instructionBundle.InstructionHash,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            await _store.UpsertStateAsync(_state, linkedCts.Token).ConfigureAwait(false);

            var allTools = BuildAvailableTools();
            var modelInfo = await ResolveModelInfoAsync(linkedCts.Token).ConfigureAwait(false);
            var toolMap = LocalAgentToolBridge.CreateDefinitionMap(allTools);
            var requestDeveloperInstructions = CombineDeveloperInstructions(
                instructionBundle.DeveloperInstructions,
                instructionBundle.RuntimeContext);

            while (true)
            {
                _ = await AppendPendingSteerInputsAsync(runId, linkedCts.Token).ConfigureAwait(false);
                await RefreshEstimatedUsageAsync(
                        runId,
                        instructionBundle.SystemMessage,
                        instructionBundle.DeveloperInstructions,
                        modelInfo,
                        linkedCts.Token)
                    .ConfigureAwait(false);

                await MaybeCompactForThresholdAsync(
                        runId,
                        instructionBundle.SystemMessage,
                        instructionBundle.DeveloperInstructions,
                        modelInfo,
                        LocalAgentCompactionTrigger.Threshold,
                        linkedCts.Token)
                    .ConfigureAwait(false);

                var turnRequest = CreateTurnRequest(
                    runId,
                    instructionBundle.SystemMessage,
                    requestDeveloperInstructions,
                    modelInfo,
                    allTools);
                var response = await ExecuteTurnWithOverflowRecoveryAsync(
                        turnRequest,
                        runId,
                        instructionBundle.SystemMessage,
                        instructionBundle.DeveloperInstructions,
                        modelInfo,
                        linkedCts.Token)
                    .ConfigureAwait(false);

                await AppendAssistantMessageAsync(
                        response,
                        runId,
                        instructionBundle.SystemMessage,
                        instructionBundle.DeveloperInstructions,
                        modelInfo,
                        linkedCts.Token)
                    .ConfigureAwait(false);

                var toolCalls = response.AssistantMessage.Parts.OfType<LocalAgentMessagePart.ToolCall>().ToArray();
                if (toolCalls.Length == 0)
                {
                    if (await AppendPendingSteerInputsAsync(runId, linkedCts.Token).ConfigureAwait(false))
                    {
                        continue;
                    }

                    await AppendTurnDiffUpdatedAsync(fileChangeTracker, runId, linkedCts.Token).ConfigureAwait(false);
                    await CompleteActiveRunAsync(runId, CancellationToken.None).ConfigureAwait(false);
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

                    var activityKind = IsSkillActivationTool(toolCall.Name)
                        ? AgentActivityKind.Skill
                        : AgentActivityKind.ToolCall;

                    var started = new AgentActivityEvent(
                        BackendId,
                        SessionId,
                        DateTimeOffset.UtcNow,
                        runId,
                        activityKind,
                        AgentActivityPhase.Started,
                        toolCall.CallId,
                        null,
                        toolCall.Name,
                        null,
                        CreateToolCallDetails(toolCall, _summary.WorkingDirectory));
                    await AppendEventsAsync([started], linkedCts.Token).ConfigureAwait(false);

                    using var progressGate = new SemaphoreSlim(1, 1);
                    var toolOutputContentId = $"{toolCall.CallId}:output";
                    var trackedModifiedFiles = GetTrackedFileMutationPaths(toolCall, _summary.WorkingDirectory);
                    LocalAgentTurnFileChangeTracker? toolFileChangeTracker = null;
                    if (trackedModifiedFiles.Count > 0)
                    {
                        toolFileChangeTracker = new LocalAgentTurnFileChangeTracker(_summary.WorkingDirectory);
                        await toolFileChangeTracker.CaptureBeforeAsync(trackedModifiedFiles, linkedCts.Token).ConfigureAwait(false);
                        await fileChangeTracker.CaptureBeforeAsync(trackedModifiedFiles, linkedCts.Token).ConfigureAwait(false);
                    }

                    AgentToolResult result;
                    try
                    {
                        result = await toolDefinition.Handler(
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
                    }
                    catch (OperationCanceledException ex) when (!linkedCts.IsCancellationRequested)
                    {
                        result = CreateToolExecutionFailureResult(toolCall.Name, ex);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        result = CreateToolExecutionFailureResult(toolCall.Name, ex);
                    }

                    if (trackedModifiedFiles.Count > 0)
                    {
                        if (toolFileChangeTracker is not null)
                        {
                            await toolFileChangeTracker.CaptureAfterAsync(trackedModifiedFiles, linkedCts.Token).ConfigureAwait(false);
                        }

                        await fileChangeTracker.CaptureAfterAsync(trackedModifiedFiles, linkedCts.Token).ConfigureAwait(false);
                    }

                    var toolDiff = toolFileChangeTracker?.CreateUnifiedDiff();

                    var toolMessage = new LocalAgentConversationMessage(
                        LocalAgentConversationRole.Tool,
                        [new LocalAgentMessagePart.ToolResult(toolCall.CallId, result)]);
                    _conversation.Add(toolMessage);

                    var completed = new AgentActivityEvent(
                        BackendId,
                        SessionId,
                        DateTimeOffset.UtcNow,
                        runId,
                        activityKind,
                        result.Success ? AgentActivityPhase.Completed : AgentActivityPhase.Failed,
                        toolCall.CallId,
                        null,
                        toolCall.Name,
                        result.Error,
                        CreateToolResultDetails(toolCall, result, _summary.WorkingDirectory, toolDiff));
                    var rawToolEvent = new AgentRawEvent(
                        BackendId,
                        SessionId,
                        DateTimeOffset.UtcNow,
                        ToolMessageEventType,
                        SerializeLocalMessage(toolMessage),
                        runId);
                    AgentRawEvent? rawSkillActivationEvent = null;
                    if (result.Success && IsSkillActivationTool(toolCall.Name))
                    {
                        var activatedSkill = TryCreateLoadedSkillState(toolCall, result);
                        if (activatedSkill is not null)
                        {
                            _state = _state with
                            {
                                LoadedSkills = MergeLoadedSkill(_state.LoadedSkills, activatedSkill),
                                UpdatedAt = DateTimeOffset.UtcNow,
                            };
                            rawSkillActivationEvent = new AgentRawEvent(
                                BackendId,
                                SessionId,
                                DateTimeOffset.UtcNow,
                                SkillActivationEventType,
                                JsonSerializer.SerializeToElement(activatedSkill, AgentJsonSerializerContext.Default.LocalAgentLoadedSkillState),
                                runId);
                        }
                    }

                    var toolOutputText = new AgentContentCompletedEvent(
                        BackendId,
                        SessionId,
                        DateTimeOffset.UtcNow,
                        runId,
                        AgentContentKind.ToolOutput,
                        toolOutputContentId,
                        toolCall.CallId,
                        RenderToolResult(result),
                        CreateToolResultDetails(toolCall, result, _summary.WorkingDirectory, toolDiff));
                    var events = rawSkillActivationEvent is null
                        ? new AgentEvent[] { rawToolEvent, completed, toolOutputText }
                        : [rawToolEvent, rawSkillActivationEvent, completed, toolOutputText];
                    await AppendEventsAsync(events, linkedCts.Token).ConfigureAwait(false);
                    if (rawSkillActivationEvent is not null)
                    {
                        await _store.UpsertStateAsync(_state, linkedCts.Token).ConfigureAwait(false);
                    }
                }
            }
        }
        finally
        {
            await CompleteActiveRunAsync(runId, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<AgentRunId> SteerAsync(AgentSteerOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(_disposed, this);

        cancellationToken.ThrowIfCancellationRequested();

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_activeRunId is not { } activeRunId)
            {
                throw new InvalidOperationException("Local raw-API steering requires an active run.");
            }

            if (options.ExpectedRunId is { } expectedRunId && expectedRunId != activeRunId)
            {
                throw new InvalidOperationException(
                    $"Local raw-API steering expected run '{expectedRunId.Value}', but active run is '{activeRunId.Value}'.");
            }

            _pendingSteerInputs.Enqueue(options.Input);
            return activeRunId;
        }
        finally
        {
            _stateGate.Release();
        }
    }

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
            var instructionBundle = LocalAgentInstructionComposer.Compose(_options, _state.LoadedSkills);
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
        _pendingSteerInputs.Clear();
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
                Provider = Provider,
            });
        return _options.Tools is { Count: > 0 }
            ? [.. builtIns, .. _options.Tools]
            : builtIns;
    }

    private static string CombineDeveloperInstructions(string? developerInstructions, string runtimeContext)
        => string.IsNullOrWhiteSpace(developerInstructions)
            ? runtimeContext
            : $"""
               {developerInstructions.Trim()}

               {runtimeContext}
               """;

    private LocalAgentTurnRequest CreateTurnRequest(
        AgentRunId runId,
        string? systemMessage,
        string? developerInstructions,
        AgentModelInfo? modelInfo,
        IReadOnlyList<AgentToolDefinition> tools,
        IReadOnlyList<LocalAgentConversationMessage>? conversation = null)
    {
        ArgumentNullException.ThrowIfNull(tools);

        return new LocalAgentTurnRequest
        {
            Provider = Provider,
            BackendId = BackendId,
            SessionId = SessionId,
            RunId = runId,
            ModelId = _summary.ModelId ?? _options.Model,
            ModelInfo = modelInfo,
            WorkingDirectory = _summary.WorkingDirectory,
            SystemMessage = systemMessage,
            DeveloperInstructions = developerInstructions,
            ReasoningEffort = _options.ReasoningEffort,
            Conversation = conversation?.ToArray() ?? _conversation.ToArray(),
            Tools = tools,
            State = _state,
        };
    }

    private static AgentSessionUsage? CreateConversationUsageSnapshot(
        string? systemMessage,
        string? developerInstructions,
        AgentModelInfo? modelInfo,
        IReadOnlyList<LocalAgentConversationMessage> conversation,
        AgentSessionUsage? usage)
    {
        if (usage is null)
        {
            return null;
        }

        var estimate = LocalAgentTokenEstimator.EstimatePromptTokens(
            systemMessage,
            developerInstructions,
            conversation,
            usage);
        var label = estimate.IsEstimated
            ? "Estimated active context"
            : "Active context window";
        var usageWithWindow = LocalAgentUsageFactory.AttachWindowEstimate(
            usage,
            modelInfo,
            estimate.Tokens,
            conversation.Count,
            DateTimeOffset.UtcNow,
            label);
        return LocalAgentUsageFactory.AttachMessageCount(usageWithWindow, conversation.Count);
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
        var events = new List<AgentEvent> { rawEvent, userContent };
        if (TryCreateUserActivatedSkillState(input, out var activatedSkill))
        {
            _state = _state with
            {
                LoadedSkills = MergeLoadedSkill(_state.LoadedSkills, activatedSkill),
                UpdatedAt = DateTimeOffset.UtcNow,
            };

            events.Add(new AgentRawEvent(
                BackendId,
                SessionId,
                DateTimeOffset.UtcNow,
                SkillActivationEventType,
                JsonSerializer.SerializeToElement(activatedSkill, AgentJsonSerializerContext.Default.LocalAgentLoadedSkillState),
                runId));
            events.Add(new AgentActivityEvent(
                BackendId,
                SessionId,
                DateTimeOffset.UtcNow,
                runId,
                AgentActivityKind.Skill,
                AgentActivityPhase.Completed,
                activatedSkill.ActivationId,
                null,
                "codealta.skills.activate",
                $"Activated skill '{activatedSkill.Name}' from the UI.",
                JsonSerializer.SerializeToElement(activatedSkill, AgentJsonSerializerContext.Default.LocalAgentLoadedSkillState)));
        }

        await AppendEventsAsync(events, cancellationToken).ConfigureAwait(false);
        if (activatedSkill is not null)
        {
            await _store.UpsertStateAsync(_state, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> AppendPendingSteerInputsAsync(AgentRunId runId, CancellationToken cancellationToken)
    {
        List<AgentInput>? pendingInputs = null;
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_activeRunId != runId || _pendingSteerInputs.Count == 0)
            {
                return false;
            }

            pendingInputs = [.. _pendingSteerInputs];
            _pendingSteerInputs.Clear();
        }
        finally
        {
            _stateGate.Release();
        }

        foreach (var input in pendingInputs)
        {
            await AppendUserMessageAsync(input, runId, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    private async Task CompleteActiveRunAsync(AgentRunId runId, CancellationToken cancellationToken)
    {
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_activeRunId != runId)
            {
                return;
            }

            _activeRunId = null;
            _activeRunCancellation = null;
            _pendingSteerInputs.Clear();
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private async Task RefreshEstimatedUsageAsync(
        AgentRunId runId,
        string? systemMessage,
        string? developerInstructions,
        AgentModelInfo? modelInfo,
        CancellationToken cancellationToken)
    {
        if (_state.Usage is null)
        {
            return;
        }

        var estimate = LocalAgentTokenEstimator.EstimatePromptTokens(
            systemMessage,
            developerInstructions,
            _conversation,
            _state.Usage);
        var label = estimate.IsEstimated
            ? "Estimated active context"
            : "Active context window";
        var refreshedUsage = LocalAgentUsageFactory.AttachWindowEstimate(
            _state.Usage,
            modelInfo,
            estimate.Tokens,
            _conversation.Count,
            DateTimeOffset.UtcNow,
            label);
        if (refreshedUsage is null || Equals(refreshedUsage, _state.Usage))
        {
            return;
        }

        _state = _state with
        {
            Usage = refreshedUsage,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        _summary = _summary with
        {
            Usage = refreshedUsage,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        var usageEvent = new AgentSessionUpdateEvent(
            BackendId,
            SessionId,
            DateTimeOffset.UtcNow,
            runId,
            AgentSessionUpdateKind.UsageUpdated,
            "Usage updated.",
            Usage: refreshedUsage);
        await AppendEventsAsync([usageEvent], LocalAgentEventPersistenceMode.TransientOnly, cancellationToken).ConfigureAwait(false);
        await _store.UpsertStateAsync(_state, cancellationToken).ConfigureAwait(false);
        await _store.UpsertSessionAsync(_summary, cancellationToken).ConfigureAwait(false);
    }

    private async Task AppendTurnDiffUpdatedAsync(
        LocalAgentTurnFileChangeTracker fileChangeTracker,
        AgentRunId runId,
        CancellationToken cancellationToken)
    {
        var diff = fileChangeTracker.CreateUnifiedDiff();
        if (string.IsNullOrWhiteSpace(diff))
        {
            return;
        }

        var diffUpdated = new AgentSessionUpdateEvent(
            BackendId,
            SessionId,
            DateTimeOffset.UtcNow,
            runId,
            AgentSessionUpdateKind.DiffUpdated,
            "Turn diff updated.",
            CreateDiffDetails(diff));
        await AppendEventsAsync([diffUpdated], cancellationToken).ConfigureAwait(false);
    }

    private async Task AppendAssistantMessageAsync(
        LocalAgentTurnResponse response,
        AgentRunId runId,
        string? systemMessage,
        string? developerInstructions,
        AgentModelInfo? modelInfo,
        CancellationToken cancellationToken)
    {
        _conversation.Add(response.AssistantMessage);
        var effectiveUsage = CreateConversationUsageSnapshot(
            systemMessage,
            developerInstructions,
            modelInfo,
            _conversation,
            response.Usage);

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
                        CreateReasoningDetails(
                            reasoning,
                            redactProtectedData: string.Equals(
                                _summary.ProtocolFamily,
                                "openai-codex-subscription",
                                StringComparison.OrdinalIgnoreCase))));
                    break;
                case LocalAgentMessagePart.ToolCall toolCall:
                    events.Add(new AgentActivityEvent(
                        BackendId,
                        SessionId,
                        DateTimeOffset.UtcNow,
                        runId,
                        IsSkillActivationTool(toolCall.Name) ? AgentActivityKind.Skill : AgentActivityKind.ToolCall,
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
            AgentCompactionOutcome? compacted;
            try
            {
                compacted = await CompactCoreAsync(
                        LocalAgentCompactionTrigger.Overflow,
                        runId,
                        systemMessage,
                        developerInstructions,
                        modelInfo,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception compactionEx) when (compactionEx is not OperationCanceledException)
            {
                throw new InvalidOperationException(
                    "The provider reported a context-overflow error, but automatic compaction recovery could not complete.",
                    compactionEx);
            }

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

        var now = DateTimeOffset.UtcNow;
        var latestUserRequest = GetLatestUserRequest(_conversation);
        var currentPromptEstimate = LocalAgentTokenEstimator.EstimatePromptTokens(
            systemMessage,
            developerInstructions,
            _conversation,
            _state.Usage);
        var currentPromptTokens = currentPromptEstimate.Tokens;
        var planningUsage = LocalAgentUsageFactory.AttachWindowEstimate(
            _state.Usage,
            modelInfo,
            currentPromptTokens,
            _conversation.Count,
            now,
            currentPromptEstimate.IsEstimated ? "Estimated active context" : "Active context window");
        var checkpointContentId = $"compaction:{Guid.CreateVersion7()}";
        var summaryOutputTokens = GetCompactionSummaryOutputTokens(settings, budget);
        LocalAgentCompactionPreparation? preparation = null;
        LocalAgentCompactionResult? result = null;
        LocalAgentCompactionCheckpoint? checkpoint = null;
        LocalAgentConversationMessage? checkpointMessage = null;
        IReadOnlyList<LocalAgentConversationMessage>? retainedConversation = null;
        long? checkpointTokenEstimate = null;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            long? promptBudgetOverride = attempt == 0 ? null : budget.UsablePromptBudget ?? currentPromptTokens;
            var keepAnchorOnly = attempt == 2;
            var plannerSettings = attempt < 2
                ? settings with { AllowOversizedAnchorReduction = false }
                : settings;
            try
            {
                preparation = LocalAgentCompactionPlanner.Prepare(
                    trigger,
                    systemMessage,
                    developerInstructions,
                    _conversation,
                    planningUsage,
                    budget,
                    plannerSettings,
                    FindLatestUserContentId(),
                    checkpointTokenEstimate,
                    promptBudgetOverride,
                    keepAnchorOnly);
            }
            catch (InvalidOperationException) when (attempt < 2)
            {
                preparation = null;
                continue;
            }

            if (preparation is null)
            {
                return null;
            }

            var summaryResult = await _compactionSummarizer.SummarizeAsync(
                    BackendId,
                    Provider,
                    SessionId,
                    _summary.ModelId ?? _options.Model,
                    modelInfo,
                    _summary.WorkingDirectory,
                    _state,
                    preparation,
                    _history,
                    latestUserRequest,
                    summaryOutputTokens,
                    cancellationToken)
                .ConfigureAwait(false);

            checkpointTokenEstimate = LocalAgentTokenEstimator.EstimateCheckpointTokens(summaryResult.Summary);
            retainedConversation = [.. preparation.TurnPrefixMessages, .. preparation.MessagesToKeep];
            checkpoint = new LocalAgentCompactionCheckpoint
            {
                Version = 2,
                ContentId = checkpointContentId,
                Trigger = trigger.ToString().ToLowerInvariant(),
                Summary = summaryResult.Summary,
                FirstKeptEventOffset = TryResolveFirstKeptEventOffset(retainedConversation),
                AnchorContentId = preparation.AnchorContentId,
                IsSplitTurn = summaryResult.IsSplitTurn,
                TokensBefore = summaryResult.TokensBefore,
                SummarizedMessageCount = summaryResult.MessagesSummarized,
                KeptMessageCount = retainedConversation.Count,
                SummaryPromptInputTokens = summaryResult.SummaryPromptInputTokens,
                SummaryPromptIncludedMessageCount = summaryResult.SummaryPromptIncludedMessages,
                SummaryPromptTotalMessageCount = summaryResult.SummaryPromptTotalMessages,
                SummaryCallCount = summaryResult.SummaryCallCount,
                SummaryMaxOutputTokens = summaryResult.SummaryMaxOutputTokens,
                CompressionRatio = summaryResult.CompressionRatio,
                ReadFiles = summaryResult.ReadFiles,
                ModifiedFiles = summaryResult.ModifiedFiles,
                OmittedToolResultCount = summaryResult.SerializerStatistics.OmittedToolResultCount,
                OmittedReasoningCount = summaryResult.SerializerStatistics.OmittedReasoningCount,
                OmittedAttachmentCount = summaryResult.SerializerStatistics.OmittedAttachmentCount,
                DroppedMessageCount = summaryResult.SerializerStatistics.DroppedMessageCount,
                SerializedToolResultCharacters = summaryResult.SerializerStatistics.SerializedToolResultCharacters,
                SerializedReasoningCharacters = summaryResult.SerializerStatistics.SerializedReasoningCharacters,
                TotalToolCallCount = summaryResult.SerializerStatistics.TotalToolCallCount,
                SerializedToolCallCount = summaryResult.SerializerStatistics.SerializedToolCallCount,
                CollapsedToolCallCount = summaryResult.SerializerStatistics.CollapsedToolCallCount,
                TotalToolResultCount = summaryResult.SerializerStatistics.TotalToolResultCount,
                SerializedToolResultCount = summaryResult.SerializerStatistics.SerializedToolResultCount,
                SerializedToolResultExcerptCount = summaryResult.SerializerStatistics.SerializedToolResultExcerptCount,
                TotalReasoningCount = summaryResult.SerializerStatistics.TotalReasoningCount,
                SerializedReasoningCount = summaryResult.SerializerStatistics.SerializedReasoningCount,
                TotalAttachmentCount = summaryResult.SerializerStatistics.TotalAttachmentCount,
                SerializedAttachmentCount = summaryResult.SerializerStatistics.SerializedAttachmentCount,
                ChunkCount = summaryResult.ChunkCount,
                OversizedAnchorReduced = summaryResult.OversizedAnchorReduced,
                KeptMessages = retainedConversation,
            };
            checkpointMessage = checkpoint.CreateMessage();

            var candidateConversation = new List<LocalAgentConversationMessage>(retainedConversation.Count + 1)
            {
                checkpointMessage,
            };
            candidateConversation.AddRange(retainedConversation);

            var tokensAfter = LocalAgentTokenEstimator.EstimatePromptTokens(
                systemMessage,
                developerInstructions,
                candidateConversation,
                usage: null).Tokens;
            var compressionRatio = summaryResult.TokensBefore > 0
                ? (double)tokensAfter / summaryResult.TokensBefore
                : (double?)null;
            if (FitsResolvedPromptBudget(tokensAfter, budget) &&
                (trigger is not LocalAgentCompactionTrigger.Threshold ||
                 compressionRatio is null ||
                 compressionRatio <= settings.TargetContextRatioMax ||
                 attempt >= 2))
            {
                result = summaryResult with
                {
                    TokensAfter = tokensAfter,
                    CompressionRatio = compressionRatio,
                };
                checkpoint = checkpoint with
                {
                    TokensAfter = tokensAfter,
                    CompressionRatio = compressionRatio,
                };
                break;
            }
        }

        if (preparation is null || result is null || checkpoint is null || checkpointMessage is null || retainedConversation is null)
        {
            throw new InvalidOperationException("Compaction summarization could not produce a prompt that fits the resolved limits after bounded replanning.");
        }

        var started = new AgentSessionUpdateEvent(
            BackendId,
            SessionId,
            now,
            runId,
            AgentSessionUpdateKind.CompactionStarted,
            $"{trigger} local compaction started.");

        _conversation.Clear();
        _conversation.Add(checkpointMessage);
        _conversation.AddRange(retainedConversation);

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
        var completionMessage = $"{trigger} local compaction summarized {result.MessagesSummarized} messages.";
        if (result.CompressionRatio is { } realizedCompressionRatio &&
            realizedCompressionRatio > settings.TargetContextRatioMax)
        {
            completionMessage += $" Post-compaction ratio {realizedCompressionRatio:P1} exceeded target {settings.TargetContextRatioMax:P1} because retained context remained expensive.";
        }

        var completed = new AgentSessionUpdateEvent(
            BackendId,
            SessionId,
            now,
            runId,
            AgentSessionUpdateKind.CompactionCompleted,
            completionMessage,
            Details: CreateCompactionDetailsElement(checkpoint),
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

    private static int GetCompactionSummaryOutputTokens(
        LocalAgentCompactionSettings settings,
        LocalAgentTokenBudget budget)
    {
        var desired = settings.SummaryOutputTokens > 0
            ? settings.SummaryOutputTokens
            : LocalAgentCompactionSettings.Default.SummaryOutputTokens;
        var reservedCap = settings.ReservedOutputTokens > 0
            ? Math.Min(desired, settings.ReservedOutputTokens)
            : desired;
        var providerCap = budget.OutputTokenLimit is > 0
            ? Math.Min(reservedCap, (int)budget.OutputTokenLimit.Value)
            : reservedCap;
        return Math.Max(providerCap, 1);
    }

    private static JsonElement CreateCompactionDetailsElement(LocalAgentCompactionCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", "codealta.localCompaction.v1");
            writer.WriteString("contentId", checkpoint.ContentId);
            writer.WriteString("trigger", checkpoint.Trigger);
            writer.WriteString("summaryMarkdown", checkpoint.Summary);
            writer.WriteString("anchorContentId", checkpoint.AnchorContentId);
            writer.WriteNumber("tokensBefore", checkpoint.TokensBefore);
            if (checkpoint.TokensAfter is { } tokensAfter)
            {
                writer.WriteNumber("tokensAfter", tokensAfter);
                writer.WriteNumber("tokensRemoved", Math.Max(0, checkpoint.TokensBefore - tokensAfter));
            }
            else
            {
                writer.WriteNull("tokensAfter");
                writer.WriteNull("tokensRemoved");
            }

            if (checkpoint.CompressionRatio is { } compressionRatio)
            {
                writer.WriteNumber("compressionRatio", compressionRatio);
            }
            else
            {
                writer.WriteNull("compressionRatio");
            }

            writer.WriteNumber("summarizedMessageCount", checkpoint.SummarizedMessageCount);
            writer.WriteNumber("keptMessageCount", checkpoint.KeptMessageCount);
            writer.WriteNumber("messagesAfter", checkpoint.KeptMessageCount + 1);
            writer.WriteNumber("summaryPromptInputTokens", checkpoint.SummaryPromptInputTokens);
            writer.WriteNumber("summaryPromptIncludedMessageCount", checkpoint.SummaryPromptIncludedMessageCount);
            writer.WriteNumber("summaryPromptTotalMessageCount", checkpoint.SummaryPromptTotalMessageCount);
            writer.WriteNumber("summaryCallCount", checkpoint.SummaryCallCount);
            writer.WriteNumber("summaryMaxOutputTokens", checkpoint.SummaryMaxOutputTokens);
            writer.WriteNumber("chunkCount", checkpoint.ChunkCount);
            writer.WriteBoolean("isSplitTurn", checkpoint.IsSplitTurn);
            writer.WriteBoolean("oversizedAnchorReduced", checkpoint.OversizedAnchorReduced);
            writer.WriteNumber("omittedToolResultCount", checkpoint.OmittedToolResultCount);
            writer.WriteNumber("omittedReasoningCount", checkpoint.OmittedReasoningCount);
            writer.WriteNumber("omittedAttachmentCount", checkpoint.OmittedAttachmentCount);
            writer.WriteNumber("droppedMessageCount", checkpoint.DroppedMessageCount);
            writer.WriteNumber("serializedToolResultCharacters", checkpoint.SerializedToolResultCharacters);
            writer.WriteNumber("serializedReasoningCharacters", checkpoint.SerializedReasoningCharacters);
            writer.WriteNumber("totalToolCallCount", checkpoint.TotalToolCallCount);
            writer.WriteNumber("serializedToolCallCount", checkpoint.SerializedToolCallCount);
            writer.WriteNumber("collapsedToolCallCount", checkpoint.CollapsedToolCallCount);
            writer.WriteNumber("totalToolResultCount", checkpoint.TotalToolResultCount);
            writer.WriteNumber("serializedToolResultCount", checkpoint.SerializedToolResultCount);
            writer.WriteNumber("serializedToolResultExcerptCount", checkpoint.SerializedToolResultExcerptCount);
            writer.WriteNumber("totalReasoningCount", checkpoint.TotalReasoningCount);
            writer.WriteNumber("serializedReasoningCount", checkpoint.SerializedReasoningCount);
            writer.WriteNumber("totalAttachmentCount", checkpoint.TotalAttachmentCount);
            writer.WriteNumber("serializedAttachmentCount", checkpoint.SerializedAttachmentCount);
            WriteStringArray(writer, "readFiles", checkpoint.ReadFiles);
            WriteStringArray(writer, "modifiedFiles", checkpoint.ModifiedFiles);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private static void WriteStringArray(Utf8JsonWriter writer, string propertyName, IReadOnlyList<string> values)
    {
        writer.WritePropertyName(propertyName);
        writer.WriteStartArray();
        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }

    private static bool FitsResolvedPromptBudget(long promptTokens, LocalAgentTokenBudget budget)
    {
        if (budget.ContextWindow is not null &&
            promptTokens + budget.ReservedOutputTokens + budget.ReservedOverheadTokens > budget.ContextWindow.Value)
        {
            return false;
        }

        if (budget.InputTokenLimit is not null && promptTokens > budget.InputTokenLimit.Value)
        {
            return false;
        }

        if (budget.OutputTokenLimit is not null && budget.ReservedOutputTokens > budget.OutputTokenLimit.Value)
        {
            return false;
        }

        return true;
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
                        localImage.MediaType ?? GuessMediaType(localImage.Path) ?? "application/octet-stream",
                        string.IsNullOrWhiteSpace(localImage.DisplayName) ? Path.GetFileName(localImage.Path) : localImage.DisplayName));
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
                AgentInputItem.LocalImage localImage => string.IsNullOrWhiteSpace(localImage.DisplayName)
                    ? $"Local image: {localImage.Path}"
                    : $"Local image ({localImage.DisplayName}): {localImage.Path}",
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

    private static AgentToolResult CreateToolExecutionFailureResult(string toolName, Exception exception)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentNullException.ThrowIfNull(exception);

        var detail = string.IsNullOrWhiteSpace(exception.Message)
            ? exception.GetType().Name
            : exception.Message;
        var message = $"Tool '{toolName}' failed: {detail}";
        return new AgentToolResult(false, [new AgentToolResultItem.Text(message)], message);
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
            ".bmp" => "image/bmp",
            ".tif" or ".tiff" => "image/tiff",
            ".pdf" => "application/pdf",
            ".txt" => "text/plain",
            _ => null,
        };

    private static JsonElement SerializeAgentInput(AgentInput input)
        => JsonSerializer.SerializeToElement(input, AgentJsonSerializerContext.Default.AgentInput);

    private static JsonElement SerializeLocalMessage(LocalAgentConversationMessage message)
        => JsonSerializer.SerializeToElement(message, AgentJsonSerializerContext.Default.LocalAgentConversationMessage);

    private static JsonElement? CreateReasoningDetails(
        LocalAgentMessagePart.Reasoning reasoning,
        bool redactProtectedData = false)
    {
        if (string.IsNullOrWhiteSpace(reasoning.ProtectedData))
        {
            return null;
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            if (redactProtectedData)
            {
                writer.WriteBoolean("protectedDataRedacted", true);
            }
            else
            {
                writer.WriteString("protectedData", reasoning.ProtectedData);
            }

            writer.WriteEndObject();
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    private static JsonElement CreateDiffDetails(string diff)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("diff", diff);
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

    private static JsonElement CreateToolResultDetails(LocalAgentMessagePart.ToolCall toolCall, AgentToolResult result, string? workingDirectory, string? diff = null)
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
            if (!string.IsNullOrWhiteSpace(diff))
            {
                writer.WriteString("diff", diff);
            }

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

        var readFiles = new List<string>();
        var modifiedFiles = new List<string>();
        var seenReadFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenModifiedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddReadFile(string path)
        {
            if (seenReadFiles.Add(path))
            {
                readFiles.Add(path);
            }
        }

        void AddModifiedFile(string path)
        {
            if (seenModifiedFiles.Add(path))
            {
                modifiedFiles.Add(path);
            }
        }

        switch (toolCall.Name)
        {
            case "read_file":
                if (GetPath(toolCall.Arguments, "path") is { Length: > 0 } readPath)
                {
                    AddReadFile(Resolve(readPath));
                }

                break;
            case "grep":
                if (GetPath(toolCall.Arguments, "path") is { Length: > 0 } grepPath)
                {
                    var resolvedGrepPath = Resolve(grepPath);
                    if (File.Exists(resolvedGrepPath))
                    {
                        AddReadFile(resolvedGrepPath);
                    }
                }

                break;
            case "write_file":
            case "replace_in_file":
            case "delete_file_or_dir":
                if (GetPath(toolCall.Arguments, "path") is { Length: > 0 } modifiedPath)
                {
                    AddModifiedFile(Resolve(modifiedPath));
                }

                break;
            case "rename_file_or_dir":
                if (GetPath(toolCall.Arguments, "old_path") is { Length: > 0 } oldPath)
                {
                    AddModifiedFile(Resolve(oldPath));
                }

                if (GetPath(toolCall.Arguments, "new_path") is { Length: > 0 } newPath)
                {
                    AddModifiedFile(Resolve(newPath));
                }

                break;
            case "apply_patch":
                if (toolCall.Arguments.TryGetProperty("input", out var patchInput) &&
                    patchInput.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(patchInput.GetString()))
                {
                    try
                    {
                        foreach (var path in LocalAgentApplyPatch.GetTouchedPaths(patchInput.GetString()!, workingDirectory ?? Environment.CurrentDirectory))
                        {
                            AddModifiedFile(path);
                        }
                    }
                    catch (InvalidOperationException)
                    {
                    }
                }

                break;
        }

        return new ToolFileActivity(readFiles, modifiedFiles);
    }

    private static IReadOnlyList<string> GetTrackedFileMutationPaths(LocalAgentMessagePart.ToolCall toolCall, string? workingDirectory)
    {
        if (!IsTrackedFileMutationTool(toolCall.Name))
        {
            return [];
        }

        return GetToolFileActivity(toolCall, workingDirectory).ModifiedFiles;
    }

    private static bool IsTrackedFileMutationTool(string toolName)
        => toolName is "write_file" or
            "replace_in_file" or
            "delete_file_or_dir" or
            "rename_file_or_dir" or
            "apply_patch";

    private static bool IsSkillActivationTool(string toolName)
        => string.Equals(toolName, "codealta.skills.activate", StringComparison.Ordinal) ||
           string.Equals(toolName, "codealta_skills_activate", StringComparison.Ordinal);

    private static LocalAgentLoadedSkillState? TryCreateLoadedSkillState(
        LocalAgentMessagePart.ToolCall toolCall,
        AgentToolResult result)
    {
        var payload = result.Items.OfType<AgentToolResultItem.Text>()
            .Select(static item => item.Value)
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        var skillName = GetArgumentString(toolCall.Arguments, "skillName")
            ?? GetArgumentString(toolCall.Arguments, "skill_name");
        return TryCreateLoadedSkillState(skillName, null, payload, "model", toolCall.CallId);
    }

    private static bool TryCreateUserActivatedSkillState(
        AgentInput input,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out LocalAgentLoadedSkillState? activatedSkill)
    {
        ArgumentNullException.ThrowIfNull(input);

        var skill = input.Items.OfType<AgentInputItem.Skill>().FirstOrDefault();
        var payload = input.Items.OfType<AgentInputItem.Text>()
            .Select(static item => item.Value)
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value) &&
                                            value.Contains("<skill_content", StringComparison.OrdinalIgnoreCase));
        activatedSkill = payload is null
            ? null
            : TryCreateLoadedSkillState(
                skill?.Name,
                skill?.Path,
                payload,
                "user",
                $"user-skill:{Guid.CreateVersion7()}");
        return activatedSkill is not null;
    }

    private static LocalAgentLoadedSkillState? TryCreateLoadedSkillState(
        string? skillName,
        string? skillPath,
        string payload,
        string activationMode,
        string activationId)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        var attributes = TryParseSkillPayloadAttributes(payload);
        skillName = string.IsNullOrWhiteSpace(skillName)
            ? GetAttribute(attributes, "name") ?? GetAttribute(attributes, "skill_name")
            : skillName.Trim();
        if (string.IsNullOrWhiteSpace(skillName))
        {
            return null;
        }

        var skillFilePath = GetAttribute(attributes, "path") ?? skillPath ?? string.Empty;
        var skillRootPath = GetAttribute(attributes, "root")
            ?? (string.IsNullOrWhiteSpace(skillFilePath) ? string.Empty : Path.GetDirectoryName(skillFilePath) ?? string.Empty);
        var sourceKind = GetAttribute(attributes, "source_kind") ?? GetAttribute(attributes, "source");
        var sourceId = GetAttribute(attributes, "source_id");
        var baseDirectoryUri = GetAttribute(attributes, "base_directory");
        var activation = new LocalAgentLoadedSkillState
        {
            Name = skillName,
            SkillFilePath = skillFilePath,
            SkillRootPath = skillRootPath,
            SourceKind = sourceKind,
            SourceId = sourceId,
            ActivatedAt = DateTimeOffset.UtcNow,
            ActivationMode = activationMode,
            ActivationId = activationId,
            Payload = payload,
            BaseDirectoryUri = baseDirectoryUri,
            RestoredFromHistory = false,
        };
        return EnsureSkillAvailability(activation);
    }

    private static LocalAgentSessionState RebuildLoadedSkillsState(
        LocalAgentSessionState state,
        IReadOnlyList<AgentEvent> history)
    {
        var loadedSkills = new Dictionary<string, LocalAgentLoadedSkillState>(StringComparer.OrdinalIgnoreCase);
        foreach (var skill in state.LoadedSkills ?? [])
        {
            if (!string.IsNullOrWhiteSpace(skill.Name))
            {
                loadedSkills[skill.Name] = EnsureSkillAvailability(skill);
            }
        }

        foreach (var rawEvent in history.OfType<AgentRawEvent>())
        {
            if (!string.Equals(rawEvent.BackendEventType, SkillActivationEventType, StringComparison.Ordinal))
            {
                continue;
            }

            LocalAgentLoadedSkillState? loadedSkill;
            try
            {
                loadedSkill = rawEvent.Raw.Deserialize(
                    AgentJsonSerializerContext.Default.LocalAgentLoadedSkillState);
            }
            catch (JsonException)
            {
                continue;
            }

            if (loadedSkill is null || string.IsNullOrWhiteSpace(loadedSkill.Name))
            {
                continue;
            }

            loadedSkills[loadedSkill.Name] = EnsureSkillAvailability(
                loadedSkill with
                {
                    RestoredFromHistory = true,
                });
        }

        return state with
        {
            LoadedSkills = loadedSkills.Values
                .OrderBy(static skill => skill.ActivatedAt)
                .ThenBy(static skill => skill.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
        };
    }

    private static IReadOnlyList<LocalAgentLoadedSkillState> MergeLoadedSkill(
        IReadOnlyList<LocalAgentLoadedSkillState> existing,
        LocalAgentLoadedSkillState loadedSkill)
    {
        var merged = existing
            .Where(skill => !string.Equals(skill.Name, loadedSkill.Name, StringComparison.OrdinalIgnoreCase))
            .Append(EnsureSkillAvailability(loadedSkill))
            .OrderBy(static skill => skill.ActivatedAt)
            .ThenBy(static skill => skill.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return merged;
    }

    private static Dictionary<string, string> TryParseSkillPayloadAttributes(string payload)
    {
        var match = SkillContentTagRegex.Match(payload);
        if (!match.Success)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match attributeMatch in XmlAttributeRegex.Matches(match.Groups["attributes"].Value))
        {
            attributes[attributeMatch.Groups["name"].Value] = WebUtility.HtmlDecode(attributeMatch.Groups["value"].Value);
        }

        return attributes;
    }

    private static string? GetAttribute(IReadOnlyDictionary<string, string> attributes, string name)
        => attributes.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    private static string? GetArgumentString(JsonElement arguments, string propertyName)
        => arguments.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static LocalAgentLoadedSkillState EnsureSkillAvailability(LocalAgentLoadedSkillState skill)
    {
        if (string.IsNullOrWhiteSpace(skill.SkillFilePath))
        {
            return skill with
            {
                IsAvailable = false,
                MissingReason = "The activated skill could not be resolved to a SKILL.md path.",
            };
        }

        if (File.Exists(skill.SkillFilePath))
        {
            return skill with
            {
                IsAvailable = true,
                MissingReason = null,
            };
        }

        return skill with
        {
            IsAvailable = false,
            MissingReason = $"The activated skill file '{skill.SkillFilePath}' is no longer available on disk.",
        };
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
