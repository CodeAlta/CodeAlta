using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using CodeAlta.Agent.Runtime.Compaction;
using CodeAlta.Agent.Runtime.Images;
using CodeAlta.Agent.ModelCatalog;
using CodeAlta.Agent.Runtime.Tools;

namespace CodeAlta.Agent.Runtime;

/// <summary>
/// Shared session implementation for provider-backed local raw-API agents.
/// </summary>
public sealed class AgentSession : IAgentSession, IAgentCompactionOutcomeProvider, IAgentIdleCompactionProvider, IAgentTargetedAbortProvider, IAgentProviderInitiatedRuns
{
    private const string UserMessageEventType = "local.userMessage";
    private const string AssistantMessageEventType = "local.assistantMessage";
    private const string ToolMessageEventType = "local.toolMessage";
    private const string SkillActivationEventType = "local.skillActivation";
    private const string CompactionSnapshotEventType = "local.compactionSnapshot";
    private const string CompactionCheckpointEventType = "local.compactionCheckpoint";
    private const int FallbackModelVisibleToolResultCharacterLimit = 120_000;
    private const int ToolResultTruncationFooterReserve = 512;
    private static readonly Regex SkillContentTagRegex = new(
        "<skill_content\\b(?<attributes>[^>]*)>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex XmlAttributeRegex = new(
        "(?<name>[A-Za-z_:][-A-Za-z0-9_:.]*)=\"(?<value>[^\"]*)\"",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly string _protocolFamily;
    private readonly string _providerKey;
    private readonly IAgentSessionJournalStore _store;
    private readonly IModelProviderTurnExecutor _turnExecutor;
    private IReadOnlyList<AgentModelInfo> _cachedModels;
    private readonly AgentCompactionSummarizer _compactionSummarizer;
    private readonly AgentSessionCreateOptions _options;
    private readonly bool _allowProviderContinuation;
    private readonly Channel<AgentEvent> _eventChannel;
    private readonly ConcurrentDictionary<Guid, Action<AgentEvent>> _subscribers = new();
    private readonly SemaphoreSlim _stateGate = new(initialCount: 1, maxCount: 1);
    private readonly List<AgentEvent> _history;
    private readonly List<AgentConversationMessage> _conversation;
    private readonly Queue<AgentInput> _pendingSteerInputs = new();
    private readonly AgentSessionToolImages _toolImages;
    private AgentModelInfo? _resolvedModelInfo;
    private bool _resolvedModelInfoLoaded;
    private AgentSessionSummary _summary;
    private AgentSessionState _state;
    private AgentRunId? _activeRunId;
    private int? _activeRunConversationStartIndex;
    private ActiveRun? _activeRun;
    private readonly object _lifetimeGate = new();
    private int _operationUses;
    private int _activeOperationUses;
    private readonly List<OperationUse> _retainedOperations = [];
    private TaskCompletionSource? _operationsSettled;
    private Task? _disposeTask;
    private volatile bool _disposed;

    /// <summary>Gets the optional borrowed client for this session's built-in tools.</summary>
    /// <remarks>Null preserves factory fallback acquisition. The supplying owner must retain the client
    /// through all original tool invocations and session cleanup; this session never disposes it.</remarks>
    internal HttpClient? BuiltInToolHttpClient { get; init; }

    /// <summary>
    /// Initializes a new instance of the <see cref="AgentSession"/> class.
    /// </summary>
    /// <param name="ProviderId">The model provider identifier.</param>
    /// <param name="provider">The configured provider descriptor.</param>
    /// <param name="summary">The persisted session summary.</param>
    /// <param name="state">The persisted session state.</param>
    /// <param name="history">The persisted session history.</param>
    /// <param name="store">The local session store.</param>
    /// <param name="turnExecutor">The provider turn executor.</param>
    /// <param name="options">The session options.</param>
    /// <param name="allowProviderContinuation">Whether provider-native live continuation may be reused for this session.</param>
    /// <param name="cachedModels">Cached provider model metadata available at session creation/resume time.</param>
    public AgentSession(
        ModelProviderId providerId,
        ModelProviderRuntimeDescriptor provider,
        AgentSessionSummary summary,
        AgentSessionState state,
        IReadOnlyList<AgentEvent> history,
        IAgentSessionJournalStore store,
        IModelProviderTurnExecutor turnExecutor,
        AgentSessionCreateOptions options,
        bool allowProviderContinuation = false,
        IReadOnlyList<AgentModelInfo>? cachedModels = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(turnExecutor);
        ArgumentNullException.ThrowIfNull(options);

        ProviderId = providerId;
        _protocolFamily = provider.ProtocolFamily;
        _providerKey = provider.ProviderKey;
        _store = store;
        _toolImages = new AgentSessionToolImages(store as IAgentSessionAttachmentStore, summary.SessionId);
        _turnExecutor = turnExecutor;
        _cachedModels = cachedModels ?? LoadConstructorModelCache(provider, turnExecutor);
        _compactionSummarizer = new AgentCompactionSummarizer(new AgentTurnExecutorCompactionSummaryExecutor(turnExecutor));
        _options = options;
        _allowProviderContinuation = allowProviderContinuation;
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
    public ModelProviderRuntimeDescriptor Provider { get; }

    /// <inheritdoc />
    public ModelProviderId ProviderId { get; }

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
        using var operation = EnterOperation();
        ArgumentNullException.ThrowIfNull(handler);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var key = Guid.CreateVersion7();
        _subscribers[key] = handler;
        return new SubscriberLease(() => _subscribers.TryRemove(key, out _));
    }

    /// <inheritdoc />
    IDisposable IAgentProviderInitiatedRuns.OnProviderInitiatedRun(Action handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _turnExecutor is IAgentProviderInitiatedTurns provider
            ? provider.OnProviderTurn(SessionId, handler)
            : new SubscriberLease(static () => { });
    }

    /// <inheritdoc />
    AgentSendOptions? IAgentProviderInitiatedRuns.TakeProviderInitiatedRun()
        => !_disposed &&
           _turnExecutor is IAgentProviderInitiatedTurns provider &&
           provider.GetPendingProviderTurn(SessionId) is { Length: > 0 } message
            ? new AgentSendOptions { Input = AgentInput.Text(message), IsProviderInitiated = true }
            : null;

    /// <inheritdoc />
    public async Task<AgentRunId> SendAsync(AgentSendOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        using var operation = EnterOperation();
        var run = new ActiveRun(new AgentRunId(Guid.CreateVersion7().ToString()));
        var admitted = false;
        CancellationTokenRegistration callerRegistration = default;
        Exception? failure = null;
        try
        {
            await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                cancellationToken.ThrowIfCancellationRequested();
                if (_activeRun is not null)
                    throw new InvalidOperationException($"Local raw-API session '{SessionId}' already has an active run.");
                _activeRunId = run.Id;
                _activeRun = run;
                _activeRunConversationStartIndex = _conversation.Count;
                _pendingSteerInputs.Clear();
                admitted = true;
            }
            finally { _stateGate.Release(); }
            // The forwarding callback only signals the one retained worker. It never synchronously
            // traverses provider callbacks, and its own disposal is joined before source release.
            callerRegistration = cancellationToken.UnsafeRegister(static state => ((ActiveRun)state!).SignalCancellation(), run);
            run.Body = ExecuteRunAsync(options, run.Id, run.Cancellation, run);
            return await run.Body.ConfigureAwait(false);
        }
        catch (Exception ex) { failure = ex; throw; }
        finally
        {
            run.Finish = FinishRunAsync(run, admitted, options.RunLifecycle, callerRegistration, failure);
            try { await run.Finish.ConfigureAwait(false); }
            catch (Exception cleanup)
            {
                if (AgentDependencyRetentionException.Contains(cleanup)) operation.Retain(cleanup, run);
                throw;
            }
        }
    }

    private async Task<AgentRunId> ExecuteRunAsync(AgentSendOptions options, AgentRunId runId, CancellationTokenSource linkedCts, ActiveRun run)
    {
        try
        {
            if (options.RunLifecycle is { } lifecycle)
            {
                await run.Start.RunAsync(lifecycle, runId, linkedCts.Token).ConfigureAwait(false);
            }
            linkedCts.Token.ThrowIfCancellationRequested();
            // A provider that starts a process in a folder that is gone reports it as a failure of its executable.
            if (!string.IsNullOrWhiteSpace(_summary.WorktreeDirectory) && !Directory.Exists(_summary.WorktreeDirectory))
                throw new InvalidOperationException($"The git worktree this session works in, '{_summary.WorktreeDirectory}', no longer exists.");
            var fileChangeTracker = new AgentTurnFileChangeTracker(_summary.ExecutionDirectory);
            var instructionBundle = AgentInstructionComposer.Compose(_options, GetPromptIntegratedLoadedSkills());
            var requestDeveloperInstructions = CombineDeveloperInstructions(
                instructionBundle.DeveloperInstructions,
                instructionBundle.RuntimeContext);
            await AppendModelSelectionEventAsync(runId, linkedCts.Token).ConfigureAwait(false);
            await AppendSystemPromptEventIfChangedAsync(
                    runId,
                    instructionBundle.SystemMessage,
                    requestDeveloperInstructions,
                    instructionBundle.InstructionHash,
                    linkedCts.Token)
                .ConfigureAwait(false);
            await AppendUserMessageAsync(options.Input, runId, options.AskId, linkedCts.Token, options.SourceSessionId).ConfigureAwait(false);

            _state = _state with
            {
                InstructionHash = instructionBundle.InstructionHash,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            await _store.UpsertStateAsync(_state, linkedCts.Token).ConfigureAwait(false);

            var permissionRequestHandler = options.OnPermissionRequest ?? _options.OnPermissionRequest;
            var userInputRequestHandler = options.OnUserInputRequest ?? _options.OnUserInputRequest;
            var allTools = AppendSendTools(BuildAvailableTools(permissionRequestHandler,
                userInputRequestHandler, options.EnableUserInputTool), options.AdditionalTools);
            // A provider that runs tools itself answers its own permission prompts and questions through the run.
            var providerToolHost = _turnExecutor as IAgentProviderToolHost;
            providerToolHost?.AttachRun(new AgentProviderRunContext(SessionId, runId, permissionRequestHandler,
                options.EnableUserInputTool ? userInputRequestHandler : null, options.IsProviderInitiated));
            var modelInfo = await ResolveModelInfoAsync(linkedCts.Token).ConfigureAwait(false);
            var toolMap = AgentToolBridge.CreateDefinitionMap(allTools);
            var runTools = new AgentRunTools(toolMap.Keys);
            var toolRun = new ToolCallRun(runId, fileChangeTracker, instructionBundle, modelInfo, runTools);

            while (true)
            {
                // A tool call of the previous step may have registered tools (an MCP server it activated):
                // this request offers them, so the model goes on in the same turn.
                if (runTools.Take() is { Count: > 0 } addedTools)
                {
                    // A tool registered again under a name of the run (a plugin that was built again) takes its place.
                    var names = addedTools.Select(static tool => AgentToolBridge.GetRegisteredToolName(tool.Spec.Name)).ToHashSet(StringComparer.Ordinal);
                    allTools = [.. allTools.Where(tool => !names.Contains(AgentToolBridge.GetRegisteredToolName(tool.Spec.Name))), .. addedTools];
                    toolMap = AgentToolBridge.CreateDefinitionMap(allTools);
                }

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
                        AgentCompactionTrigger.Threshold,
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

                var toolCalls = response.AssistantMessage.Parts.OfType<AgentMessagePart.ToolCall>().ToArray();
                if (toolCalls.Length == 0)
                {
                    if (response.RequiresProviderFollowUp)
                    {
                        continue;
                    }

                    if (await AppendPendingSteerInputsAsync(runId, linkedCts.Token).ConfigureAwait(false))
                    {
                        continue;
                    }

                    await AppendTurnDiffUpdatedAsync(fileChangeTracker, runId, linkedCts.Token).ConfigureAwait(false);
                    await CompleteActiveTurnAsync(runId, CancellationToken.None).ConfigureAwait(false);
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
                            AgentCompactionTrigger.Threshold,
                            linkedCts.Token)
                        .ConfigureAwait(false);

                    var idleEvent = new AgentSessionUpdateEvent(
                        ProviderId,
                        SessionId,
                        DateTimeOffset.UtcNow,
                        runId,
                        AgentSessionUpdateKind.Idle,
                        null,
                        Usage: _state.Usage);
                    await AppendEventsAsync([idleEvent], linkedCts.Token).ConfigureAwait(false);
                    return runId;
                }

                await RunToolCallsAsync(toolRun, toolCalls, toolMap, providerToolHost, linkedCts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            var originalBodyFailure = run.Start.PublicationFailure ?? ex;
            run.OriginalBodyFailure = originalBodyFailure;
            run.ErrorPublication = AppendRunErrorAsync(runId, originalBodyFailure);
            try { await run.ErrorPublication.ConfigureAwait(false); }
            catch (Exception publicationFailure) { throw new AggregateException(ex, publicationFailure); }
            throw;
        }
    }

    /// <summary>What the tool calls of a run share.</summary>
    private sealed record ToolCallRun(
        AgentRunId RunId,
        AgentTurnFileChangeTracker FileChanges,
        AgentInstructionBundle Instructions,
        AgentModelInfo? ModelInfo,
        AgentRunTools RunTools);

    // Runs the tool calls of an assistant message. The session runs its tools one after the other, in their order.
    // A provider that runs tools itself may run several of them at the same time: such a call is shown from when
    // the provider says it starts, and at the latest when the calls before it ended.
    private async Task RunToolCallsAsync(
        ToolCallRun run,
        IReadOnlyList<AgentMessagePart.ToolCall> toolCalls,
        IReadOnlyDictionary<string, AgentToolDefinition> toolMap,
        IAgentProviderToolHost? providerToolHost,
        CancellationToken cancellationToken)
    {
        // What the calls record (events, conversation, file changes) is written by one of them at a time.
        using var gate = new SemaphoreSlim(1, 1);
        using var batch = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var running = new List<Task>();
        try
        {
            foreach (var toolCall in toolCalls)
            {
                toolMap.TryGetValue(toolCall.Name, out var toolDefinition);
                if (providerToolHost is not null)
                {
                    toolDefinition = providerToolHost.ResolveTool(SessionId, toolCall, toolDefinition) ?? toolDefinition;
                }

                if (toolDefinition is null)
                {
                    throw new InvalidOperationException($"Tool '{toolCall.Name}' was not registered for session '{SessionId}'.");
                }

                var before = running.Count == 0 ? Task.CompletedTask : Task.WhenAll(running);
                if (providerToolHost?.WhenToolStarts(SessionId, toolCall) is { } started)
                {
                    running.Add(RunStartedToolCallAsync(run, toolCall, toolDefinition, started, before, gate, batch));
                    continue;
                }

                await before.ConfigureAwait(false);
                await RunToolCallAsync(run, toolCall, toolDefinition, gate, batch.Token).ConfigureAwait(false);
            }

            await Task.WhenAll(running).ConfigureAwait(false);
        }
        catch
        {
            // A call failed, or the run is cancelled: the others are not left running, and end before this returns.
            await batch.CancelAsync().ConfigureAwait(false);
            try
            {
                await Task.WhenAll(running).ConfigureAwait(false);
            }
            catch
            {
                // The first failure is the one reported.
            }

            throw;
        }
    }

    private async Task RunStartedToolCallAsync(
        ToolCallRun run,
        AgentMessagePart.ToolCall toolCall,
        AgentToolDefinition toolDefinition,
        Task started,
        Task before,
        SemaphoreSlim gate,
        CancellationTokenSource batch)
    {
        try
        {
            await Task.WhenAny(started, before).WaitAsync(batch.Token).ConfigureAwait(false);
            batch.Token.ThrowIfCancellationRequested();
            await RunToolCallAsync(run, toolCall, toolDefinition, gate, batch.Token).ConfigureAwait(false);
        }
        catch
        {
            await batch.CancelAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task RunToolCallAsync(
        ToolCallRun run,
        AgentMessagePart.ToolCall toolCall,
        AgentToolDefinition toolDefinition,
        SemaphoreSlim gate,
        CancellationToken cancellationToken)
    {
        var activityKind = IsSkillActivationTool(toolCall)
            ? AgentActivityKind.Skill
            : AgentActivityKind.ToolCall;

        var started = new AgentActivityEvent(
            ProviderId,
            SessionId,
            DateTimeOffset.UtcNow,
            run.RunId,
            activityKind,
            AgentActivityPhase.Started,
            toolCall.CallId,
            null,
            toolCall.Name,
            null,
            CreateToolCallDetails(toolCall, _summary.ExecutionDirectory));
        var toolOutputContentId = $"{toolCall.CallId}:output";
        var trackedModifiedFiles = GetTrackedFileMutationPaths(toolCall, _summary.ExecutionDirectory);
        AgentTurnFileChangeTracker? toolFileChangeTracker = null;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await AppendEventsAsync([started], cancellationToken).ConfigureAwait(false);
            if (trackedModifiedFiles.Count > 0)
            {
                toolFileChangeTracker = new AgentTurnFileChangeTracker(_summary.ExecutionDirectory);
                await toolFileChangeTracker.CaptureBeforeAsync(trackedModifiedFiles, cancellationToken).ConfigureAwait(false);
                await run.FileChanges.CaptureBeforeAsync(trackedModifiedFiles, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            gate.Release();
        }

        AgentToolResult result;
        try
        {
            result = await toolDefinition.Handler(
                    new AgentToolInvocation(
                        ProviderId,
                        SessionId,
                        toolCall.CallId,
                        toolDefinition.Spec.Name,
                        toolCall.Arguments,
                        async (update, progressToken) =>
                        {
                            if (string.IsNullOrEmpty(update.Delta))
                            {
                                return;
                            }

                            await gate.WaitAsync(progressToken).ConfigureAwait(false);
                            try
                            {
                                var deltaEvent = new AgentContentDeltaEvent(
                                    ProviderId,
                                    SessionId,
                                    DateTimeOffset.UtcNow,
                                    run.RunId,
                                    AgentContentKind.ToolOutput,
                                    toolOutputContentId,
                                    toolCall.CallId,
                                    update.Delta,
                                    update.Details);
                                await AppendEventsAsync([deltaEvent], AgentEventPersistenceMode.TransientOnly, progressToken).ConfigureAwait(false);
                            }
                            finally
                            {
                                gate.Release();
                            }
                        }) { RunTools = run.RunTools },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            result = CreateToolExecutionFailureResult(toolCall.Name, ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result = CreateToolExecutionFailureResult(toolCall.Name, ex);
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (trackedModifiedFiles.Count > 0)
            {
                if (toolFileChangeTracker is not null)
                {
                    await toolFileChangeTracker.CaptureAfterAsync(trackedModifiedFiles, cancellationToken).ConfigureAwait(false);
                }

                await run.FileChanges.CaptureAfterAsync(trackedModifiedFiles, cancellationToken).ConfigureAwait(false);
            }

            var toolDiff = toolFileChangeTracker?.CreateUnifiedDiff();

            // An image the tool returned is checked, resized when needed and saved beside the journal.
            result = await _toolImages.PrepareAsync(result, run.ModelInfo, cancellationToken).ConfigureAwait(false);
            var modelVisibleResult = CreateModelVisibleToolResult(
                toolCall,
                result,
                run.Instructions.SystemMessage,
                run.Instructions.DeveloperInstructions,
                run.ModelInfo);
            var toolMessage = new AgentConversationMessage(
                AgentConversationRole.Tool,
                [new AgentMessagePart.ToolResult(toolCall.CallId, modelVisibleResult)]);
            _conversation.Add(toolMessage);

            var completed = new AgentActivityEvent(
                ProviderId,
                SessionId,
                DateTimeOffset.UtcNow,
                run.RunId,
                activityKind,
                result.Success ? AgentActivityPhase.Completed : AgentActivityPhase.Failed,
                toolCall.CallId,
                null,
                toolCall.Name,
                result.Error,
                CreateToolResultDetails(toolCall, result, _summary.ExecutionDirectory, toolDiff));
            var rawToolEvent = new AgentRawEvent(
                ProviderId,
                SessionId,
                DateTimeOffset.UtcNow,
                ToolMessageEventType,
                SerializeLocalMessage(toolMessage),
                run.RunId);
            AgentRawEvent? rawSkillActivationEvent = null;
            if (result.Success && IsSkillActivationTool(toolCall))
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
                        ProviderId,
                        SessionId,
                        DateTimeOffset.UtcNow,
                        SkillActivationEventType,
                        JsonSerializer.SerializeToElement(activatedSkill, AgentJsonSerializerContext.Default.AgentLoadedSkillState),
                        run.RunId);
                }
            }

            var toolOutputText = new AgentContentCompletedEvent(
                ProviderId,
                SessionId,
                DateTimeOffset.UtcNow,
                run.RunId,
                AgentContentKind.ToolOutput,
                toolOutputContentId,
                toolCall.CallId,
                RenderToolResult(result),
                CreateToolResultDetails(toolCall, result, _summary.ExecutionDirectory, toolDiff));
            var events = rawSkillActivationEvent is null
                ? new AgentEvent[] { rawToolEvent, completed, toolOutputText }
                : [rawToolEvent, rawSkillActivationEvent, completed, toolOutputText];
            await AppendEventsAsync(events, cancellationToken).ConfigureAwait(false);
            if (rawSkillActivationEvent is not null)
            {
                await _store.UpsertStateAsync(_state, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<AgentRunId> SteerAsync(AgentSteerOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        using var operation = EnterOperation();
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
    public async Task AbortAsync(CancellationToken cancellationToken = default)
    {
        using var operation = EnterOperation(allowRetained: true);
        ActiveRun? run;
        await _stateGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            run = _activeRun;
            if (run is { CancellationSealed: false }) run.CancellationReserved = true;
        }
        finally { _stateGate.Release(); }
        // Preserve untargeted trusted policy (including its historically ignored caller token).
        if (run is null) return;
        if (run.CancellationReserved) run.SignalCancellation();
        await run.CancellationWork.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<AgentTargetedAbortOutcome> AbortRunAsync(AgentRunId expectedRunId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedRunId.Value);
        using var operation = EnterOperation(allowRetained: true);
        ActiveRun run;
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            // Exact cancellation remains available during postprocessing, but closes before Closing.
            // The source lifetime remains owned after that point for trusted/disposal cancellation.
            if (_activeRun is not { Closing: false } current || current.Id != expectedRunId)
                return AgentTargetedAbortOutcome.TargetNotActive;
            run = current;
            // Work already exists behind an unreleased signal. Teardown sees this reservation and
            // must join that original task even if the controlling caller has not launched it yet.
            run.CancellationReserved = true;
        }
        finally { _stateGate.Release(); }
        run.SignalCancellation();
        await run.CancellationWork.ConfigureAwait(false);
        return AgentTargetedAbortOutcome.CancellationSignalled;
    }

    /// <inheritdoc />
    public Task CompactAsync(CancellationToken cancellationToken = default)
        => CompactWithOutcomeAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<AgentCompactionOutcome?> CompactWithOutcomeAsync(CancellationToken cancellationToken = default)
    {
        using var operation = EnterOperation();
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await CompactGateHeldAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _stateGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<AgentCompactionOutcome?> TryCompactWhenIdleAsync(CancellationToken cancellationToken = default)
    {
        using var operation = EnterOperation();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!await _stateGate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return null;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_activeRun is not null) return null;
            return await CompactGateHeldAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _stateGate.Release(); }
    }

    private async Task<AgentCompactionOutcome> CompactGateHeldAsync(CancellationToken cancellationToken)
    {
        var instructionBundle = AgentInstructionComposer.Compose(_options, _state.LoadedSkills);
        var modelInfo = await ResolveModelInfoAsync(cancellationToken).ConfigureAwait(false);
        if (_turnExecutor is IAgentProviderCompaction providerCompaction)
        {
            return await CompactProviderContextAsync(providerCompaction, instructionBundle, modelInfo, cancellationToken).ConfigureAwait(false);
        }

        var outcome = await CompactCoreAsync(
                trigger: AgentCompactionTrigger.Manual,
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

    // The provider keeps the context of the session: it compacts it, and the journal keeps every message.
    private async Task<AgentCompactionOutcome> CompactProviderContextAsync(
        IAgentProviderCompaction providerCompaction,
        AgentInstructionBundle instructionBundle,
        AgentModelInfo? modelInfo,
        CancellationToken cancellationToken)
    {
        var started = new AgentSessionUpdateEvent(
            ProviderId,
            SessionId,
            DateTimeOffset.UtcNow,
            null,
            AgentSessionUpdateKind.CompactionStarted,
            "Manual provider compaction started.");
        await AppendEventsAsync([started], cancellationToken).ConfigureAwait(false);

        AgentCompactionOutcome outcome;
        try
        {
            var request = CreateTurnRequest(
                new AgentRunId($"provider-compaction:{Guid.CreateVersion7()}"),
                instructionBundle.SystemMessage,
                CombineDeveloperInstructions(instructionBundle.DeveloperInstructions, instructionBundle.RuntimeContext),
                modelInfo,
                tools: []);
            outcome = await providerCompaction.CompactAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            outcome = new AgentCompactionOutcome(false, ex.Message);
        }

        var usage = _state.Usage;
        if (outcome is { Success: true, PostCompactionTokens: { } postCompactionTokens })
        {
            usage = AgentUsageFactory.AttachWindowEstimate(
                usage,
                modelInfo,
                postCompactionTokens,
                _conversation.Count,
                DateTimeOffset.UtcNow,
                "Active context window");
            _state = _state with { Usage = usage, UpdatedAt = DateTimeOffset.UtcNow };
            _summary = _summary with { Usage = usage, UpdatedAt = DateTimeOffset.UtcNow };
            await _store.UpsertStateAsync(_state, cancellationToken).ConfigureAwait(false);
            await _store.UpsertSessionAsync(_summary, cancellationToken).ConfigureAwait(false);
        }

        var completed = new AgentSessionUpdateEvent(
            ProviderId,
            SessionId,
            DateTimeOffset.UtcNow,
            null,
            outcome.Success ? AgentSessionUpdateKind.CompactionCompleted : AgentSessionUpdateKind.Warning,
            outcome.Message ?? (outcome.Success ? "Manual provider compaction completed." : "Manual provider compaction failed."),
            Usage: outcome.Success ? usage : null);
        await AppendEventsAsync([completed], cancellationToken).ConfigureAwait(false);
        return outcome;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<AgentEvent>> GetHistoryAsync(CancellationToken cancellationToken = default)
    {
        using var operation = EnterOperation(allowRetained: true);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<AgentEvent>>([.. _history]);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource launch;
        Task disposal;
        lock (_lifetimeGate)
        {
            if (_disposeTask is not null) return new(_disposeTask);
            _disposed = true;
            launch = new(TaskCreationOptions.RunContinuationsAsynchronously);
            disposal = _disposeTask = DisposeCoreAsync(launch.Task, _operationsSettled?.Task ?? Task.CompletedTask);
        }
        launch.TrySetResult();
        return new(disposal);
    }

    private async Task DisposeCoreAsync(Task launch, Task operationsSettled)
    {
        await launch.ConfigureAwait(false);
        ActiveRun? run;
        await _stateGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            run = _activeRun;
            if (run is { CancellationSealed: false }) run.CancellationReserved = true;
        }
        finally { _stateGate.Release(); }
        run?.SignalCancellation();
        List<Exception> failures = [];
        // Start cancellation before joining operation scopes, which include actual hooks and all
        // registration/traversal disposal. No new operation can enter after _disposed was published.
        try { await Task.WhenAll(operationsSettled, run?.CancellationWork ?? Task.CompletedTask).ConfigureAwait(false); }
        catch (Exception ex) { failures.Add(ex); }
        lock (_lifetimeGate)
        {
            if (_retainedOperations.Count != 0)
            {
                failures.AddRange(_retainedOperations.Select(static operation => operation.Failure!));
                throw new AgentDependencyRetentionException(SessionId, "session disposal", failures, this);
            }
        }
        try
        {
            if (_turnExecutor is IAgentProviderSessionCleanup providerSessionCleanup)
            {
                await providerSessionCleanup.DisposeProviderSessionAsync(SessionId).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            failures.Add(ex);
            throw new AgentDependencyRetentionException(SessionId, "provider session cleanup", failures, this);
        }
        _pendingSteerInputs.Clear();
        _stateGate.Dispose();
        _eventChannel.Writer.TryComplete();
        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException(failures);
    }

    // These are session-local scopes, not detached wait wrappers. Release occurs only after the
    // original operation has joined its work; disposal cannot free the gate/provider underneath it.
    private OperationUse EnterOperation(bool allowRetained = false)
    {
        lock (_lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!allowRetained && _retainedOperations.Count != 0)
                throw new AgentDependencyRetentionException(SessionId, "run admission",
                    _retainedOperations.Select(static operation => operation.Failure!), this);
            _operationUses++;
            if (_activeOperationUses++ == 0) _operationsSettled = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return new OperationUse(this);
        }
    }

    private sealed class OperationUse(AgentSession owner) : IDisposable
    {
        private bool _settled;
        internal Exception? Failure { get; private set; }
        internal object? Dependencies { get; private set; }
        internal void Retain(Exception failure, object dependencies)
        {
            lock (owner._lifetimeGate)
            {
                if (_settled) return;
                Failure = failure;
                Dependencies = dependencies;
                _settled = true;
                owner._retainedOperations.Add(this);
                // Settlement is not release: the unreleased-use count remains unchanged.
                if (--owner._activeOperationUses == 0) owner._operationsSettled!.TrySetResult();
            }
        }
        public void Dispose()
        {
            lock (owner._lifetimeGate)
            {
                if (_settled) return;
                _settled = true;
                owner._operationUses--;
                if (--owner._activeOperationUses == 0) owner._operationsSettled!.TrySetResult();
            }
        }
    }

    private sealed class ActiveRun
    {
        private readonly TaskCompletionSource<bool> _cancel = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ActiveRun(AgentRunId id) { Id = id; CancellationWork = CancelAsync(); }
        internal AgentRunId Id { get; }
        internal CancellationTokenSource Cancellation { get; } = new();
        internal Task CancellationWork { get; }
        internal Task? Traversal { get; private set; }
        internal Task<AgentRunId>? Body { get; set; }
        internal RunStartInvocation Start { get; } = new();
        internal Task? Started => Start.Original;
        internal Task? ErrorPublication { get; set; }
        internal Exception? OriginalBodyFailure { get; set; }
        internal Task? Finish { get; set; }
        internal RunReleaseDecision? Release { get; set; }
        // State gate owns admission flags. Only the one worker can ever cancel this source.
        internal bool CancellationReserved { get; set; }
        internal bool CancellationSealed { get; set; }
        internal bool Closing { get; set; }
        internal void SignalCancellation() => _cancel.TrySetResult(true);
        internal void CompleteWithoutCancellation() => _cancel.TrySetResult(false);
        private async Task CancelAsync()
        {
            if (!await _cancel.Task.ConfigureAwait(false)) return;
            Traversal = Cancellation.CancelAsync();
            await Traversal.ConfigureAwait(false);
        }
    }

    private async Task FinishRunAsync(ActiveRun run, bool admitted, AgentRunLifecycle? lifecycle,
        CancellationTokenRegistration callerRegistration, Exception? bodyFailure)
    {
        if (admitted)
        {
            await _stateGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try { run.Closing = true; }
            finally { _stateGate.Release(); }
        }
        run.Release = new RunReleaseDecision(run);
        await run.Release.FinishAsync(
            () => admitted && lifecycle is not null ? lifecycle.ClosingAsync(run.Id) : Task.CompletedTask,
            callerRegistration.DisposeAsync,
            async retain =>
            {
                await _stateGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    run.CancellationSealed = true;
                    if (retain) run.CancellationReserved = true;
                    if (run.CancellationReserved) run.SignalCancellation();
                    else run.CompleteWithoutCancellation();
                }
                finally { _stateGate.Release(); }
                try { await run.CancellationWork.ConfigureAwait(false); }
                catch (Exception failure) when (run.CancellationReserved && run.Traversal is null)
                {
                    throw new AgentDependencyRetentionException(run.Id.Value, "cancellation launch", [failure], run);
                }
            },
            run.Cancellation.Dispose,
            async () =>
            {
            if (admitted)
            {
                await CompleteActiveTurnAsync(run.Id, CancellationToken.None).ConfigureAwait(false);
                await _stateGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    // Logical turn completion may already have cleared _activeRunId. Release only
                    // this exact source lifetime, after all original work and source disposal.
                    if (ReferenceEquals(_activeRun, run)) _activeRun = null;
                }
                finally { _stateGate.Release(); }
            }
            }, bodyFailure).ConfigureAwait(false);
    }

    // Shared by the actual Agent finish path and inert fixtures, not by provider/store emulation.
    internal sealed class RunReleaseDecision(object dependencies)
    {
        internal RunReleaseStage? Closing { get; private set; }
        internal RunReleaseStage? Registration { get; private set; }
        internal RunReleaseStage? Cancellation { get; private set; }
        internal RunReleaseStage? Slot { get; private set; }
        internal bool Retained { get; private set; }
        internal bool SourceReleased { get; private set; }
        internal bool SlotReleased { get; private set; }
        internal Exception? SourceFailure { get; private set; }
        internal async Task FinishAsync(Func<Task> close, Func<ValueTask> unregister,
            Func<bool, Task> cancel, Action releaseSource, Func<Task> releaseSlot, Exception? bodyFailure)
        {
            Closing = new RunReleaseStage(close);
            Registration = new RunReleaseStage(() => unregister().AsTask());
            // Both records and observers exist before either callback can execute.
            Closing.Launch();
            Registration.Launch();
            var closingFailure = await Closing.Outcome.ConfigureAwait(false);
            var registrationFailure = await Registration.Outcome.ConfigureAwait(false);
            var failures = new List<Exception>();
            if (bodyFailure is not null) failures.Add(bodyFailure);
            if (closingFailure is not null) failures.Add(Closing.Failure!);
            if (registrationFailure is not null) failures.Add(Registration.Failure!);
            Retained = Closing.Original is null || registrationFailure is not null || failures.Any(AgentDependencyRetentionException.Contains)
                || Closing.OriginalFaults is { } closingFaults && AgentDependencyRetentionException.Contains(closingFaults);
            Cancellation = new RunReleaseStage(() => cancel(Retained));
            Cancellation.Launch();
            if (await Cancellation.Outcome.ConfigureAwait(false) is { } cancellationFailure)
            {
                failures.Add(Cancellation.Failure!);
                Retained |= Cancellation.Original is null || AgentDependencyRetentionException.Contains(Cancellation.Failure!);
            }
            if (!Retained)
            {
                try { releaseSource(); SourceReleased = true; }
                catch (Exception failure) { SourceFailure = failure; failures.Add(failure); Retained = true; }
                if (!Retained)
                {
                    Slot = new RunReleaseStage(releaseSlot);
                    Slot.Launch();
                    if (await Slot.Outcome.ConfigureAwait(false) is { } slotFailure)
                    {
                        failures.Add(Slot.Failure!);
                        Retained = true;
                    }
                    else SlotReleased = true;
                }
            }
            if (Retained) throw new AgentDependencyRetentionException("agent run", "required release", failures, new { Owner = dependencies, Decision = this });
            // SendAsync already propagates its body failure; this method must not double-report it.
            if (bodyFailure is not null && failures.Count == 1) return;
            if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
            if (failures.Count > 1) throw new AggregateException(failures);
        }
    }

    internal sealed class RunReleaseStage
    {
        private readonly Func<Task> _invoke;
        private readonly TaskCompletionSource<Task> _launched = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal RunReleaseStage(Func<Task> invoke)
        {
            _invoke = invoke;
            Outcome = ObserveAsync();
        }
        internal Task? Original { get; private set; }
        internal AggregateException? OriginalFaults { get; private set; }
        internal Exception? Failure { get; private set; }
        internal Task<Exception?> Outcome { get; }

        internal void Launch()
        {
            try { Original = _invoke(); _launched.TrySetResult(Original); }
            catch (Exception failure) { _launched.TrySetException(failure); }
        }
        private async Task<Exception?> ObserveAsync()
        {
            try
            {
                var original = await _launched.Task.ConfigureAwait(false);
                await original.ConfigureAwait(false);
                return null;
            }
            catch (Exception failure)
            {
                OriginalFaults = Original?.Exception;
                Failure = OriginalFaults is { InnerExceptions.Count: > 1 } ? OriginalFaults : failure;
                return failure;
            }
        }
    }

    // Implemented only by lifecycle composition owners. Error publication uses the originally
    // selected start error, while cleanup and the caller receive the complete escaping evidence.
    internal interface IRunStartEvidence
    {
        RunStartInvocation? FailedStart { get; }
    }

    internal sealed class RunStartInvocation
    {
        private readonly Func<Exception, bool> _containsRetention;
        private int _started;

        internal RunStartInvocation() : this(AgentDependencyRetentionException.Contains) { }
        internal RunStartInvocation(Func<Exception, bool> containsRetention)
        {
            ArgumentNullException.ThrowIfNull(containsRetention);
            _containsRetention = containsRetention;
        }

        internal AgentRunLifecycle? Invocation { get; private set; }
        internal Task? Original { get; private set; }
        internal Task? Completion { get; private set; }
        internal Exception? AwaitedFailure { get; private set; }
        internal AggregateException? OriginalFaults { get; private set; }
        internal Exception? PublicationFailure { get; private set; }
        internal Exception? Failure { get; private set; }

        internal Task RunAsync(AgentRunLifecycle lifecycle, AgentRunId runId, CancellationToken executionToken)
        {
            ArgumentNullException.ThrowIfNull(lifecycle);
            if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("Lifecycle start already invoked.");
            Invocation = lifecycle;
            Completion = InvokeAsync(lifecycle, runId, executionToken);
            return Completion;
        }

        private async Task InvokeAsync(AgentRunLifecycle lifecycle, AgentRunId runId, CancellationToken executionToken)
        {
            try
            {
                Original = lifecycle.StartedAsync(runId, executionToken) ?? throw new InvalidOperationException("Lifecycle start returned no original.");
                await Original.ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                AwaitedFailure = failure;
                OriginalFaults = Original?.Exception;
                PublicationFailure = (lifecycle as IRunStartEvidence)?.FailedStart?.PublicationFailure ?? failure;
                var evidence = OriginalFaults is { InnerExceptions.Count: > 1 } ? OriginalFaults : failure;
                // Orchestration can recognize additional explicit marker families without making
                // Agent depend on Plugins. Translate only that retention contract, not ordinary errors.
                Failure = !AgentDependencyRetentionException.Contains(evidence) && _containsRetention(evidence)
                    ? new AgentDependencyRetentionException("agent lifecycle", "start", [evidence], this) : evidence;
                ExceptionDispatchInfo.Throw(Failure);
                throw;
            }
        }
    }

    private IReadOnlyList<AgentToolDefinition> BuildAvailableTools(AgentPermissionRequestHandler permissionRequestHandler,
        AgentUserInputRequestHandler? userInputRequestHandler, bool enableUserInputTool)
    {
        var builtIns = AgentBuiltInToolFactory.CreateDefaultTools(
            new AgentBuiltInToolOptions
            {
                ProviderId = ProviderId,
                SessionId = SessionId,
                WorkingDirectory = _summary.ExecutionDirectory,
                OnPermissionRequest = permissionRequestHandler,
                OnUserInputRequest = userInputRequestHandler,
                EnableUserInputTool = enableUserInputTool,
                Provider = Provider,
                HttpClient = BuiltInToolHttpClient,
            });
        return _options.Tools is { Count: > 0 }
            ? [.. builtIns, .. _options.Tools]
            : builtIns;
    }

    // Registration uses normalized/truncated names and suffixes duplicate session tools. Preserve that
    // legacy behavior, but never let an additional per-send tool silently acquire another alias.
    internal static IReadOnlyList<AgentToolDefinition> AppendSendTools(
        IReadOnlyList<AgentToolDefinition> original, IReadOnlyList<AgentToolDefinition>? additional)
    {
        if (additional is not { Count: > 0 }) return original;
        var used = new HashSet<string>(AgentToolBridge.CreateDefinitionMap(original).Keys, StringComparer.Ordinal);
        var combined = new List<AgentToolDefinition>(original.Count + additional.Count);
        combined.AddRange(original);
        foreach (var tool in additional)
        {
            ArgumentNullException.ThrowIfNull(tool);
            var alias = AgentToolBridge.GetRegisteredToolName(tool.Spec.Name);
            if (!used.Add(alias)) throw new ArgumentException("Additional send tool collides with a registered tool alias.", nameof(additional));
            combined.Add(tool);
        }
        return combined.AsReadOnly();
    }

    private static string CombineDeveloperInstructions(string? developerInstructions, string runtimeContext)
    {
        if (string.IsNullOrWhiteSpace(developerInstructions))
        {
            return runtimeContext;
        }

        if (string.IsNullOrWhiteSpace(runtimeContext))
        {
            return developerInstructions.Trim();
        }

        return $"""
               {developerInstructions.Trim()}

               {runtimeContext}
               """;
    }

    private IReadOnlyList<AgentLoadedSkillState> GetPromptIntegratedLoadedSkills()
    {
        if (_state.LoadedSkills.Count == 0 || _state.LastCompactedAt is not { } lastCompactedAt)
        {
            return [];
        }

        return _state.LoadedSkills
            .Where(skill => skill.ActivatedAt <= lastCompactedAt)
            .ToArray();
    }

    private AgentTurnRequest CreateTurnRequest(
        AgentRunId runId,
        string? systemMessage,
        string? developerInstructions,
        AgentModelInfo? modelInfo,
        IReadOnlyList<AgentToolDefinition> tools,
        IReadOnlyList<AgentConversationMessage>? conversation = null)
    {
        ArgumentNullException.ThrowIfNull(tools);

        return new AgentTurnRequest
        {
            Provider = Provider,
            ProviderId = ProviderId,
            SessionId = SessionId,
            RunId = runId,
            ModelId = _summary.ModelId ?? _options.Model,
            ModelInfo = modelInfo,
            WorkingDirectory = _summary.ExecutionDirectory,
            SystemMessage = systemMessage,
            DeveloperInstructions = developerInstructions,
            ReasoningEffort = _options.ReasoningEffort,
            Conversation = _toolImages.Resolve(conversation ?? CreateProviderConversation().Messages, modelInfo).ToArray(),
            Tools = tools,
            CanUseProviderContinuation = _allowProviderContinuation,
            State = _state,
        };
    }

    private AgentInlineMediaPruneResult CreateProviderConversation()
    {
        var pruned = AgentMediaCompaction.PruneInlineImages(_conversation, ShouldPreserveInlineMediaForActiveRun);
        var recoverMissingToolResultsBeforeIndex = _activeRunConversationStartIndex ?? pruned.Messages.Count;
        var normalized = AgentConversationToolCallRecovery.NormalizeForReplay(
            pruned.Messages,
            recoverMissingToolResultsBeforeIndex);
        return ReferenceEquals(normalized, pruned.Messages)
            ? pruned
            : pruned with { Messages = normalized };
    }

    // The usage of the last request says nothing of a conversation images were just taken out of, unless the
    // provider keeps the context: it still has them, and its count holds a prompt this session knows nothing of.
    private AgentSessionUsage? SelectUsageForPromptEstimate(AgentInlineMediaPruneResult providerConversation)
        => providerConversation.PrunedImageCount > 0 && _turnExecutor is not IAgentProviderCompaction ? null : _state.Usage;

    private static AgentSessionUsage? CreateConversationUsageSnapshot(
        string? systemMessage,
        string? developerInstructions,
        AgentModelInfo? modelInfo,
        IReadOnlyList<AgentConversationMessage> conversation,
        AgentSessionUsage? previousUsage,
        AgentSessionUsage? usage,
        bool providerKeepsContext)
    {
        if (usage is null)
        {
            return null;
        }

        // A provider that keeps the context counts all of it with each request, and trims it by itself (it clears
        // old tool results, it compacts): a count smaller than the one before is what it holds now.
        var usageForEstimate = providerKeepsContext ? usage : SelectUsageForConversationEstimate(previousUsage, usage, conversation.Count);
        var estimate = AgentTokenEstimator.EstimatePromptTokens(
            systemMessage,
            developerInstructions,
            conversation,
            usageForEstimate);
        var label = estimate.IsEstimated
            ? "Estimated active context"
            : "Active context window";
        var usageWithWindow = AgentUsageFactory.AttachWindowEstimate(
            usage,
            modelInfo,
            estimate.Tokens,
            conversation.Count,
            DateTimeOffset.UtcNow,
            label);
        return AgentUsageFactory.AttachMessageCount(usageWithWindow, conversation.Count);
    }

    private static AgentSessionUsage SelectUsageForConversationEstimate(
        AgentSessionUsage? previousUsage,
        AgentSessionUsage usage,
        int conversationMessageCount)
    {
        if (previousUsage?.Window is not { CurrentTokens: > 0, MessageCount: >= 0 } previousWindow ||
            previousWindow.MessageCount.Value > conversationMessageCount ||
            usage.Window?.CurrentTokens is not { } currentTokens ||
            currentTokens >= previousWindow.CurrentTokens.Value)
        {
            return usage;
        }

        // Provider-native continuation can report usage for only the incremental request tail.
        // Keep the provider operation breakdown from the latest response, but base the active
        // window estimate on the last non-smaller window snapshot plus locally-added messages.
        return previousUsage with
        {
            LastOperation = usage.LastOperation ?? previousUsage.LastOperation,
            RateLimits = usage.RateLimits ?? previousUsage.RateLimits,
            Details = usage.Details ?? previousUsage.Details,
            UpdatedAt = usage.UpdatedAt == default ? previousUsage.UpdatedAt : usage.UpdatedAt,
        };
    }

    private async Task AppendUserMessageAsync(AgentInput input, AgentRunId runId, string? askId, CancellationToken cancellationToken, string? sourceSessionId = null)
    {
        var message = new AgentConversationMessage(
            AgentConversationRole.User,
            MapInputItems(input.Items));
        _conversation.Add(message);

        var rawEvent = new AgentRawEvent(
            ProviderId,
            SessionId,
            DateTimeOffset.UtcNow,
            UserMessageEventType,
            SerializeAgentInput(input),
            runId);
        var userContent = new AgentContentCompletedEvent(
            ProviderId,
            SessionId,
            DateTimeOffset.UtcNow,
            runId,
            AgentContentKind.User,
            $"user:{Guid.CreateVersion7()}",
            null,
            RenderUserInput(input.Items),
            SerializeAgentInput(input),
            askId,
            string.IsNullOrWhiteSpace(sourceSessionId) ? null : sourceSessionId.Trim());
        var events = new List<AgentEvent> { rawEvent, userContent };
        if (TryCreateUserActivatedSkillState(input, out var activatedSkill))
        {
            _state = _state with
            {
                LoadedSkills = MergeLoadedSkill(_state.LoadedSkills, activatedSkill),
                UpdatedAt = DateTimeOffset.UtcNow,
            };

            events.Add(new AgentRawEvent(
                ProviderId,
                SessionId,
                DateTimeOffset.UtcNow,
                SkillActivationEventType,
                JsonSerializer.SerializeToElement(activatedSkill, AgentJsonSerializerContext.Default.AgentLoadedSkillState),
                runId));
            events.Add(new AgentActivityEvent(
                ProviderId,
                SessionId,
                DateTimeOffset.UtcNow,
                runId,
                AgentActivityKind.Skill,
                AgentActivityPhase.Completed,
                activatedSkill.ActivationId,
                null,
                "alta skill activate",
                $"Activated skill '{activatedSkill.Name}' from the UI.",
                JsonSerializer.SerializeToElement(activatedSkill, AgentJsonSerializerContext.Default.AgentLoadedSkillState)));
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
            await AppendUserMessageAsync(input, runId, askId: null, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    // Clear steering/replay/media bookkeeping before post-turn usage refresh and compaction, but
    // retain source lifetime authority until FinishRunAsync has joined and disposed the original work.
    private async Task CompleteActiveTurnAsync(AgentRunId runId, CancellationToken cancellationToken)
    {
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_activeRunId != runId)
            {
                return;
            }

            _activeRunId = null;
            _activeRunConversationStartIndex = null;
            _pendingSteerInputs.Clear();
            _toolImages.ReleaseRun();
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
        var providerConversation = CreateProviderConversation();
        var estimate = AgentTokenEstimator.EstimatePromptTokens(
            systemMessage,
            developerInstructions,
            providerConversation.Messages,
            SelectUsageForPromptEstimate(providerConversation));
        var label = estimate.IsEstimated
            ? "Estimated active context"
            : "Active context window";
        var refreshedUsage = AgentUsageFactory.AttachWindowEstimate(
            _state.Usage,
            modelInfo,
            estimate.Tokens,
            providerConversation.Messages.Count,
            DateTimeOffset.UtcNow,
            label);
        if (refreshedUsage is null || Equals(refreshedUsage, _state.Usage))
        {
            return;
        }

        var shouldEmitPredictiveUpdate = providerConversation.Messages.Any(static message => message.Role is AgentConversationRole.Tool);

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

        if (shouldEmitPredictiveUpdate)
        {
            var usageEvent = new AgentSessionUpdateEvent(
                ProviderId,
                SessionId,
                DateTimeOffset.UtcNow,
                runId,
                AgentSessionUpdateKind.UsageUpdated,
                "Usage updated.",
                Usage: refreshedUsage with { LastOperation = null });
            await AppendEventsAsync([usageEvent], AgentEventPersistenceMode.TransientOnly, cancellationToken).ConfigureAwait(false);
        }

        await _store.UpsertStateAsync(_state, cancellationToken).ConfigureAwait(false);
        await _store.UpsertSessionAsync(_summary, cancellationToken).ConfigureAwait(false);
    }

    private async Task AppendTurnDiffUpdatedAsync(
        AgentTurnFileChangeTracker fileChangeTracker,
        AgentRunId runId,
        CancellationToken cancellationToken)
    {
        var diff = fileChangeTracker.CreateUnifiedDiff();
        if (string.IsNullOrWhiteSpace(diff))
        {
            return;
        }

        var diffUpdated = new AgentSessionUpdateEvent(
            ProviderId,
            SessionId,
            DateTimeOffset.UtcNow,
            runId,
            AgentSessionUpdateKind.DiffUpdated,
            "Turn diff updated.",
            CreateDiffDetails(diff));
        await AppendEventsAsync([diffUpdated], cancellationToken).ConfigureAwait(false);
    }

    private async Task AppendAssistantMessageAsync(
        AgentTurnResponse response,
        AgentRunId runId,
        string? systemMessage,
        string? developerInstructions,
        AgentModelInfo? modelInfo,
        CancellationToken cancellationToken)
    {
        var hasDurableAssistantOutput = response.AssistantMessage.Parts.Count > 0 || !response.RequiresProviderFollowUp;
        if (hasDurableAssistantOutput)
        {
            _conversation.Add(response.AssistantMessage);
        }
        var providerConversation = CreateProviderConversation();
        var effectiveUsage = CreateConversationUsageSnapshot(
            systemMessage,
            developerInstructions,
            modelInfo,
            providerConversation.Messages,
            _state.Usage,
            response.Usage,
            providerKeepsContext: _turnExecutor is IAgentProviderCompaction);

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
            ReasoningEffort = _options.ReasoningEffort ?? _summary.ReasoningEffort,
            AgentPromptId = NormalizeOptionalText(_options.AgentPromptId) ?? _summary.AgentPromptId,
            Title = response.Title ?? _summary.Title,
            Summary = response.Summary ?? ExtractAssistantSummary(response.AssistantMessage) ?? _summary.Summary,
            Usage = effectiveUsage ?? _summary.Usage,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        var events = new List<AgentEvent>();
        if (hasDurableAssistantOutput)
        {
            events.Add(new AgentRawEvent(
                ProviderId,
                SessionId,
                DateTimeOffset.UtcNow,
                AssistantMessageEventType,
                SerializeLocalMessage(response.AssistantMessage),
                runId));
        }

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
                case AgentMessagePart.Text text:
                    events.Add(new AgentContentCompletedEvent(
                        ProviderId,
                        SessionId,
                        DateTimeOffset.UtcNow,
                        runId,
                        AgentContentKind.Assistant,
                        partContentId ?? $"assistant:{Guid.CreateVersion7()}",
                        null,
                        text.Value));
                    break;
                case AgentMessagePart.Reasoning reasoning:
                    events.Add(new AgentContentCompletedEvent(
                        ProviderId,
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
                                "codex",
                                StringComparison.OrdinalIgnoreCase))));
                    break;
                case AgentMessagePart.ToolCall toolCall:
                    events.Add(new AgentActivityEvent(
                        ProviderId,
                        SessionId,
                        DateTimeOffset.UtcNow,
                        runId,
                        IsSkillActivationTool(toolCall) ? AgentActivityKind.Skill : AgentActivityKind.ToolCall,
                        AgentActivityPhase.Requested,
                        toolCall.CallId,
                        null,
                        toolCall.Name,
                        null,
                        CreateToolCallDetails(toolCall, _summary.ExecutionDirectory)));
                    break;
            }
        }

        if (effectiveUsage is not null)
        {
            events.Add(new AgentSessionUpdateEvent(
                ProviderId,
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
        AgentTurnDelta delta,
        CancellationToken cancellationToken)
    {
        var details = CreateStreamingDeltaDetails(delta);
        var @event = new AgentContentDeltaEvent(
            ProviderId,
            SessionId,
            DateTimeOffset.UtcNow,
            runId,
            delta.Kind,
            delta.ContentId,
            null,
            delta.Text,
            details);
        await AppendEventsAsync([@event], AgentEventPersistenceMode.TransientOnly, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask OnSessionUpdateAsync(
        AgentRunId runId,
        AgentTurnSessionUpdate update,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);

        if (TryGetDiscardDraftAttemptId(update.Details, out var discardedAttemptId))
        {
            RemoveTransientDraftDeltas(discardedAttemptId);
        }

        var @event = new AgentSessionUpdateEvent(
            ProviderId,
            SessionId,
            DateTimeOffset.UtcNow,
            runId,
            update.Kind,
            update.Message,
            update.Details,
            Usage: update.Usage);
        await AppendEventsAsync([@event], AgentEventPersistenceMode.TransientOnly, cancellationToken).ConfigureAwait(false);
    }

    private static JsonElement? CreateStreamingDeltaDetails(AgentTurnDelta delta)
    {
        if (delta.Details is not null &&
            string.IsNullOrWhiteSpace(delta.AttemptId) &&
            delta.IsDraft)
        {
            return delta.Details;
        }

        if (delta.Details is null &&
            string.IsNullOrWhiteSpace(delta.AttemptId) &&
            delta.IsDraft)
        {
            return null;
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            if (delta.Details is { ValueKind: JsonValueKind.Object } details)
            {
                foreach (var property in details.EnumerateObject())
                {
                    property.WriteTo(writer);
                }
            }

            if (!string.IsNullOrWhiteSpace(delta.AttemptId))
            {
                writer.WriteString("attemptId", delta.AttemptId);
            }

            writer.WriteBoolean("draft", delta.IsDraft);
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    private void RemoveTransientDraftDeltas(string attemptId)
    {
        for (var index = _history.Count - 1; index >= 0; index--)
        {
            if (_history[index] is AgentContentDeltaEvent delta &&
                TryGetDraftAttemptId(delta.Details, out var deltaAttemptId) &&
                string.Equals(deltaAttemptId, attemptId, StringComparison.Ordinal))
            {
                _history.RemoveAt(index);
            }
        }
    }

    private static bool TryGetDiscardDraftAttemptId(JsonElement? details, out string attemptId)
    {
        attemptId = string.Empty;
        if (details is not { ValueKind: JsonValueKind.Object } root ||
            !TryGetBooleanProperty(root, "discardDraft", out var discardDraft) ||
            !discardDraft)
        {
            return false;
        }

        return TryGetStringProperty(root, "draftAttemptId", out attemptId) &&
               !string.IsNullOrWhiteSpace(attemptId);
    }

    private static bool TryGetDraftAttemptId(JsonElement? details, out string attemptId)
    {
        attemptId = string.Empty;
        if (details is not { ValueKind: JsonValueKind.Object } root ||
            TryGetBooleanProperty(root, "draft", out var isDraft) && !isDraft)
        {
            return false;
        }

        return TryGetStringProperty(root, "attemptId", out attemptId) &&
               !string.IsNullOrWhiteSpace(attemptId);
    }

    private static bool TryGetStringProperty(JsonElement element, string propertyName, out string value)
    {
        if (element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(property.GetString()))
        {
            value = property.GetString()!;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool TryGetBooleanProperty(JsonElement element, string propertyName, out bool value)
    {
        if (element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            value = property.GetBoolean();
            return true;
        }

        value = false;
        return false;
    }

    private async Task AppendModelSelectionEventAsync(AgentRunId runId, CancellationToken cancellationToken)
    {
        var modelId = NormalizeOptionalText(_options.Model) ?? NormalizeOptionalText(_summary.ModelId);
        var update = new AgentSessionUpdateEvent(
            ProviderId,
            SessionId,
            DateTimeOffset.UtcNow,
            runId,
            AgentSessionUpdateKind.ModelChanged,
            null,
            CreateModelSelectionDetails(modelId, _options.ReasoningEffort, NormalizeOptionalText(_options.AgentPromptId)));
        await AppendEventsAsync([update], cancellationToken).ConfigureAwait(false);
    }

    private JsonElement CreateModelSelectionDetails(string? modelId, AgentReasoningEffort? reasoningEffort, string? agentPromptId)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("providerKey", _providerKey);
            writer.WriteString("agentPromptId", string.IsNullOrWhiteSpace(agentPromptId) ? "default" : agentPromptId);
            writer.WriteString("promptId", string.IsNullOrWhiteSpace(agentPromptId) ? "default" : agentPromptId);
            if (string.IsNullOrWhiteSpace(modelId))
            {
                writer.WriteNull("modelId");
            }
            else
            {
                writer.WriteString("modelId", modelId);
            }

            if (reasoningEffort is { } effort)
            {
                writer.WriteString("reasoningEffort", effort.ToString());
            }

            writer.WriteEndObject();
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    private static string? NormalizeOptionalText(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async Task AppendSystemPromptEventIfChangedAsync(
        AgentRunId runId,
        string? systemMessage,
        string? developerInstructions,
        string effectivePromptHash,
        CancellationToken cancellationToken)
    {
        var previousHash = string.IsNullOrWhiteSpace(_state.InstructionHash) ? null : _state.InstructionHash;
        if (string.Equals(previousHash, effectivePromptHash, StringComparison.Ordinal))
        {
            return;
        }

        var statistics = CreateSystemPromptStatistics(systemMessage, developerInstructions);
        var agentPromptUsage = ResolveAgentPromptUsage(NormalizeOptionalText(_options.AgentPromptId));
        var promptEvent = new AgentSystemPromptEvent(
            ProviderId,
            SessionId,
            DateTimeOffset.UtcNow,
            runId,
            previousHash is null ? "session_start" : "changed",
            effectivePromptHash,
            NormalizeOptionalText(_options.AgentPromptId),
            systemMessage,
            developerInstructions,
            new AgentSystemPromptProviderPayloadSummary("native-system-and-developer", AppliedToProvider: true, Lossy: false),
            CreateSystemPromptManifest(effectivePromptHash, agentPromptUsage, statistics, _options.InstructionTransformations),
            statistics,
            new AgentSystemPromptChangeSummary(
                previousHash is null ? "initial" : "changed",
                previousHash is null ? ["system", "developer"] : [],
                [],
                previousHash is null ? [] : ["effective_prompt"]),
            agentPromptUsage);
        await AppendEventsAsync([promptEvent], cancellationToken).ConfigureAwait(false);
    }

    private AgentPromptUsageInfo? ResolveAgentPromptUsage(string? agentPromptId)
    {
        if (_options.AgentPromptUsage is { } usage)
        {
            return usage;
        }

        return string.IsNullOrWhiteSpace(agentPromptId)
            ? null
            : new AgentPromptUsageInfo(agentPromptId, null, null);
    }

    private static AgentSystemPromptStatistics CreateSystemPromptStatistics(string? systemMessage, string? developerInstructions)
    {
        var systemChars = systemMessage?.Length ?? 0;
        var developerChars = developerInstructions?.Length ?? 0;
        var systemTokens = EstimatePromptTokens(systemMessage);
        var developerTokens = EstimatePromptTokens(developerInstructions);
        return new AgentSystemPromptStatistics(systemTokens, developerTokens, systemTokens + developerTokens, systemChars, developerChars);
    }

    private static int EstimatePromptTokens(string? text)
        => checked((int)TokenEstimator.Estimate(text));

    private static JsonElement CreateSystemPromptManifest(string effectivePromptHash, AgentPromptUsageInfo? agentPromptUsage, AgentSystemPromptStatistics statistics, IReadOnlyList<AgentInstructionTransformationInfo> transformations)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", 1);
            writer.WriteString("effectivePromptHash", effectivePromptHash);
            if (agentPromptUsage is null || string.IsNullOrWhiteSpace(agentPromptUsage.PromptName))
            {
                writer.WriteNull("agentPromptId");
            }
            else
            {
                writer.WriteString("agentPromptId", agentPromptUsage.PromptName);
                writer.WriteStartObject("agentPrompt");
                writer.WriteString("promptName", agentPromptUsage.PromptName);
                if (!string.IsNullOrWhiteSpace(agentPromptUsage.DisplayName))
                {
                    writer.WriteString("displayName", agentPromptUsage.DisplayName);
                }

                if (!string.IsNullOrWhiteSpace(agentPromptUsage.SourcePath))
                {
                    writer.WriteString("sourcePath", agentPromptUsage.SourcePath);
                }

                writer.WriteEndObject();
            }

            writer.WriteStartObject("provider");
            writer.WriteString("channelMapping", "native-system-and-developer");
            writer.WriteBoolean("appliedToProvider", true);
            writer.WriteBoolean("lossy", false);
            writer.WriteEndObject();
            writer.WriteStartObject("statistics");
            writer.WriteNumber("systemApproxTokens", statistics.SystemApproxTokens);
            writer.WriteNumber("developerApproxTokens", statistics.DeveloperApproxTokens);
            writer.WriteNumber("totalApproxTokens", statistics.TotalApproxTokens);
            writer.WriteNumber("systemChars", statistics.SystemChars);
            writer.WriteNumber("developerChars", statistics.DeveloperChars);
            writer.WriteEndObject();
            if (transformations.Count > 0)
            {
                writer.WriteStartArray("instructionTransformations");
                foreach (var transformation in transformations)
                {
                    writer.WriteStartObject();
                    writer.WriteString("pluginRuntimeKey", transformation.PluginRuntimeKey);
                    writer.WriteString("runtimeContributionKey", transformation.RuntimeContributionKey);
                    if (!string.IsNullOrWhiteSpace(transformation.NaturalName))
                    {
                        writer.WriteString("naturalName", transformation.NaturalName);
                    }

                    writer.WriteNumber("order", transformation.Order);
                    writer.WriteString("stage", transformation.Stage);
                    writer.WriteString("disposition", transformation.Disposition);
                    writer.WriteStartArray("changedChannels");
                    foreach (var channel in transformation.ChangedChannels)
                    {
                        writer.WriteStringValue(channel);
                    }

                    writer.WriteEndArray();
                    if (!string.IsNullOrWhiteSpace(transformation.ChangeSummary))
                    {
                        writer.WriteString("changeSummary", transformation.ChangeSummary);
                    }

                    if (!string.IsNullOrWhiteSpace(transformation.ResultInstructionHash))
                    {
                        writer.WriteString("resultInstructionHash", transformation.ResultInstructionHash);
                    }

                    if (transformation.Metadata.Count > 0)
                    {
                        writer.WriteStartObject("metadata");
                        foreach (var pair in transformation.Metadata)
                        {
                            writer.WriteString(pair.Key, pair.Value);
                        }

                        writer.WriteEndObject();
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    private async Task AppendEventsAsync(
        IReadOnlyList<AgentEvent> events,
        CancellationToken cancellationToken)
        => await AppendEventsAsync(events, AgentEventPersistenceMode.DurableCanonical, cancellationToken).ConfigureAwait(false);

    private async Task AppendRunErrorAsync(AgentRunId runId, Exception exception)
    {
        var message = exception is OperationCanceledException
            ? "Run cancelled before the assistant response completed."
            : exception.Message;
        var errorEvent = new AgentErrorEvent(
            ProviderId,
            SessionId,
            DateTimeOffset.UtcNow,
            message,
            exception,
            runId);

        try
        {
            await AppendEventsAsync([errorEvent], CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception appendException) when (appendException is IOException or UnauthorizedAccessException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    private async Task AppendEventsAsync(
        IReadOnlyList<AgentEvent> events,
        AgentEventPersistenceMode persistenceMode,
        CancellationToken cancellationToken)
    {
        if (events.Count == 0)
        {
            return;
        }

        _history.AddRange(events);
        if (persistenceMode is AgentEventPersistenceMode.DurableCanonical)
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

    private async Task<AgentTurnResponse> ExecuteTurnWithOverflowRecoveryAsync(
        AgentTurnRequest request,
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
                    (update, ct) => OnSessionUpdateAsync(runId, update, ct),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AgentTurnExecutionException ex) when (ex.Failure.IsContextOverflow)
        {
            AgentCompactionOutcome? compacted;
            try
            {
                compacted = await CompactCoreAsync(
                        AgentCompactionTrigger.Overflow,
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
                            Conversation = _toolImages.Resolve(_conversation, request.ModelInfo).ToArray(),
                            State = _state,
                        },
                        (delta, ct) => OnStreamingDeltaAsync(runId, delta, ct),
                        (update, ct) => OnSessionUpdateAsync(runId, update, ct),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (AgentTurnExecutionException retryEx) when (retryEx.Failure.IsContextOverflow)
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
        AgentCompactionTrigger trigger,
        CancellationToken cancellationToken)
    {
        var settings = Provider.Compaction ?? AgentCompactionSettings.Default;
        if (!settings.Enabled)
        {
            return;
        }

        var budget = AgentTokenBudgetResolver.Resolve(modelInfo, settings);
        if (budget.InputContextLimit is null)
        {
            return;
        }

        if (budget.InputContextLimit <= 0)
        {
            throw new InvalidOperationException("The resolved input-context limit is not usable.");
        }

        var estimate = AgentTokenEstimator.EstimatePromptTokens(
            systemMessage,
            developerInstructions,
            _conversation,
            _state.Usage);
        var thresholdTokens = Math.Max((long)Math.Floor(budget.InputContextLimit.Value * settings.Ratio), 1);
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
        AgentCompactionTrigger trigger,
        AgentRunId? runId,
        string? systemMessage,
        string? developerInstructions,
        AgentModelInfo? modelInfo,
        CancellationToken cancellationToken)
    {
        var settings = Provider.Compaction ?? AgentCompactionSettings.Default;
        if (trigger is not AgentCompactionTrigger.Manual && !settings.Enabled)
        {
            return null;
        }

        var budget = AgentTokenBudgetResolver.Resolve(modelInfo, settings);
        if (budget.InputContextLimit is <= 0)
        {
            throw new InvalidOperationException("The resolved input-context limit is not usable.");
        }

        var started = new AgentSessionUpdateEvent(
            ProviderId,
            SessionId,
            DateTimeOffset.UtcNow,
            runId,
            AgentSessionUpdateKind.CompactionStarted,
            $"{trigger} local compaction started.");
        await AppendEventsAsync([started], cancellationToken).ConfigureAwait(false);
        await Task.Yield();

        var now = DateTimeOffset.UtcNow;
        var latestUserRequest = GetLatestUserRequest(_conversation);
        var currentPromptEstimate = AgentTokenEstimator.EstimatePromptTokens(
            systemMessage,
            developerInstructions,
            _conversation,
            _state.Usage);
        var currentPromptTokens = currentPromptEstimate.Tokens;
        var planningUsage = AgentUsageFactory.AttachWindowEstimate(
            _state.Usage,
            modelInfo,
            currentPromptTokens,
            _conversation.Count,
            now,
            currentPromptEstimate.IsEstimated ? "Estimated active context" : "Active context window");
        var checkpointContentId = $"compaction:{Guid.CreateVersion7()}";
        var postCompactionTargetRatio = ResolvePostCompactionTargetRatio(settings);
        var postCompactionTargetTokens = ResolvePostCompactionTargetTokens(settings, budget);
        var checkpointTargetTokens = ResolveCheckpointTargetTokens(settings, postCompactionTargetTokens);
        var summarizerMaxOutputTokens = GetCompactionSummarizerMaxOutputTokens(settings, budget, postCompactionTargetTokens);
        var fixedPromptTokens = AgentTokenEstimator.EstimatePromptTokens(
            systemMessage,
            developerInstructions,
            [],
            usage: null).Tokens;
        AgentCompactionPreparation? preparation = null;
        AgentCompactionResult? result = null;
        AgentCompactionCheckpoint? checkpoint = null;
        AgentConversationMessage? checkpointMessage = null;
        IReadOnlyList<AgentConversationMessage>? retainedConversation = null;
        long? checkpointTokenEstimate = null;
        var planningAttemptCount = 0;
        var shrinkAttempted = false;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            planningAttemptCount = attempt + 1;
            long? promptBudgetOverride = attempt == 2
                ? budget.InputContextLimit ?? currentPromptTokens
                : postCompactionTargetTokens;
            var keepAnchorOnly = attempt == 2;
            try
            {
                preparation = AgentCompactionPlanner.Prepare(
                    trigger,
                    systemMessage,
                    developerInstructions,
                    _conversation,
                    planningUsage,
                    budget,
                    settings,
                    FindLatestUserContentId(),
                    checkpointTokenEstimate,
                    promptBudgetOverride,
                    keepAnchorOnly,
                    allowOversizedAnchorReduction: attempt == 2);
            }
            catch (InvalidOperationException) when (attempt < 2)
            {
                preparation = null;
                continue;
            }

            if (preparation?.OversizedAnchorMessage is { } oversizedAnchorMessage &&
                AgentMediaCompaction.ContainsPrunableInlineImages(
                    [oversizedAnchorMessage],
                    ShouldPreserveInlineMediaInCompactedMessage))
            {
                preparation = TryCreateInlineMediaCompactionPreparation(
                    trigger,
                    systemMessage,
                    developerInstructions,
                    planningUsage) ?? preparation;
            }

            if (preparation is null)
            {
                preparation = TryCreateInlineMediaCompactionPreparation(
                    trigger,
                    systemMessage,
                    developerInstructions,
                    planningUsage);
                if (preparation is null)
                {
                    return null;
                }
            }

            var summaryResult = await _compactionSummarizer.SummarizeAsync(
                    ProviderId,
                    Provider,
                    SessionId,
                    _summary.ModelId ?? _options.Model,
                    modelInfo,
                    _summary.ExecutionDirectory,
                    _state,
                    preparation,
                    _history,
                    latestUserRequest,
                    summarizerMaxOutputTokens,
                    cancellationToken)
                .ConfigureAwait(false);

            checkpointTokenEstimate = AgentTokenEstimator.EstimateCheckpointTokens(summaryResult.Summary);
            var unprunedRetainedConversation = new List<AgentConversationMessage>(
                preparation.TurnPrefixMessages.Count + preparation.MessagesToKeep.Count);
            unprunedRetainedConversation.AddRange(preparation.TurnPrefixMessages);
            unprunedRetainedConversation.AddRange(preparation.MessagesToKeep);
            var firstKeptEventOffset = TryResolveFirstKeptEventOffset(unprunedRetainedConversation);
            var retainedMediaPruneResult = AgentMediaCompaction.PruneInlineImages(
                unprunedRetainedConversation,
                ShouldPreserveInlineMediaInCompactedMessage);
            retainedConversation = retainedMediaPruneResult.Messages;
            checkpoint = new AgentCompactionCheckpoint
            {
                Version = 2,
                ContentId = checkpointContentId,
                Trigger = trigger.ToString().ToLowerInvariant(),
                Summary = summaryResult.Summary,
                FirstKeptEventOffset = firstKeptEventOffset,
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
                TargetRatio = postCompactionTargetRatio,
                TargetTokens = postCompactionTargetTokens,
                TargetMet = null,
                TargetMissReason = null,
                PlanningAttemptCount = planningAttemptCount,
                CheckpointTokens = checkpointTokenEstimate,
                FixedPromptTokens = fixedPromptTokens,
                RetainedMessageTokens = retainedConversation.Sum(AgentTokenEstimator.EstimateMessage),
                ModelVisibleReadFileCount = summaryResult.ModelVisibleReadFileCount,
                ModelVisibleModifiedFileCount = summaryResult.ModelVisibleModifiedFileCount,
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

            var candidateConversation = new List<AgentConversationMessage>(retainedConversation.Count + 1)
            {
                checkpointMessage,
            };
            candidateConversation.AddRange(retainedConversation);

            var tokensAfter = AgentTokenEstimator.EstimatePromptTokens(
                systemMessage,
                developerInstructions,
                candidateConversation,
                usage: null).Tokens;
            var compressionRatio = summaryResult.TokensBefore > 0
                ? (double)tokensAfter / summaryResult.TokensBefore
                : (double?)null;
            var targetMet = tokensAfter <= postCompactionTargetTokens;
            if (!targetMet &&
                !shrinkAttempted &&
                attempt < 2 &&
                ShouldShrinkCompactionSummary(postCompactionTargetTokens, checkpointTokenEstimate.Value, checkpointTargetTokens))
            {
                shrinkAttempted = true;
                summaryResult = await _compactionSummarizer.ShrinkSummaryAsync(
                        ProviderId,
                        Provider,
                        SessionId,
                        _summary.ModelId ?? _options.Model,
                        modelInfo,
                        _summary.ExecutionDirectory,
                        _state,
                        summaryResult,
                        latestUserRequest,
                        checkpointTargetTokens,
                        summarizerMaxOutputTokens,
                        cancellationToken)
                    .ConfigureAwait(false);

                checkpointTokenEstimate = AgentTokenEstimator.EstimateCheckpointTokens(summaryResult.Summary);
                checkpoint = checkpoint with
                {
                    Summary = summaryResult.Summary,
                    SummaryPromptInputTokens = summaryResult.SummaryPromptInputTokens,
                    SummaryCallCount = summaryResult.SummaryCallCount,
                    SummaryMaxOutputTokens = summaryResult.SummaryMaxOutputTokens,
                    CheckpointTokens = checkpointTokenEstimate,
                    ModelVisibleReadFileCount = summaryResult.ModelVisibleReadFileCount,
                    ModelVisibleModifiedFileCount = summaryResult.ModelVisibleModifiedFileCount,
                };
                checkpointMessage = checkpoint.CreateMessage();
                candidateConversation = new List<AgentConversationMessage>(retainedConversation.Count + 1)
                {
                    checkpointMessage,
                };
                candidateConversation.AddRange(retainedConversation);
                tokensAfter = AgentTokenEstimator.EstimatePromptTokens(
                    systemMessage,
                    developerInstructions,
                    candidateConversation,
                    usage: null).Tokens;
                compressionRatio = summaryResult.TokensBefore > 0
                    ? (double)tokensAfter / summaryResult.TokensBefore
                    : (double?)null;
                targetMet = tokensAfter <= postCompactionTargetTokens;
            }

            if (FitsResolvedPromptBudget(tokensAfter, budget))
            {
                if (!targetMet && attempt < 2)
                {
                    continue;
                }

                var retainedMessageTokens = retainedConversation.Sum(AgentTokenEstimator.EstimateMessage);
                var postCompactionInputRatio = budget.InputContextLimit is > 0
                    ? (double)tokensAfter / budget.InputContextLimit.Value
                    : (double?)null;
                var targetMissReason = DetermineTargetMissReason(
                    preparation,
                    summaryResult,
                    targetMet,
                    postCompactionTargetTokens,
                    fixedPromptTokens,
                    checkpointTokenEstimate.Value,
                    retainedMessageTokens);
                result = summaryResult with
                {
                    TokensAfter = tokensAfter,
                    CompressionRatio = compressionRatio,
                    TargetRatio = postCompactionTargetRatio,
                    TargetTokens = postCompactionTargetTokens,
                    TargetMet = targetMet,
                    TargetMissReason = targetMissReason,
                    PlanningAttemptCount = planningAttemptCount,
                    PostCompactionInputRatio = postCompactionInputRatio,
                    CheckpointTokens = checkpointTokenEstimate,
                    FixedPromptTokens = fixedPromptTokens,
                    RetainedMessageTokens = retainedMessageTokens,
                };
                checkpoint = checkpoint with
                {
                    TokensAfter = tokensAfter,
                    CompressionRatio = compressionRatio,
                    TargetRatio = postCompactionTargetRatio,
                    TargetTokens = postCompactionTargetTokens,
                    TargetMet = targetMet,
                    TargetMissReason = targetMissReason,
                    PlanningAttemptCount = planningAttemptCount,
                    PostCompactionInputRatio = postCompactionInputRatio,
                    CheckpointTokens = checkpointTokenEstimate,
                    FixedPromptTokens = fixedPromptTokens,
                    RetainedMessageTokens = retainedMessageTokens,
                };
                break;
            }
        }

        if (preparation is null || result is null || checkpoint is null || checkpointMessage is null || retainedConversation is null)
        {
            throw new InvalidOperationException("Compaction summarization could not produce a prompt that fits the resolved limits after bounded replanning.");
        }

        _conversation.Clear();
        _conversation.Add(checkpointMessage);
        _conversation.AddRange(retainedConversation);

        var completedAt = DateTimeOffset.UtcNow;
        var usage = CreateCompactionUsage(result, budget, _conversation.Count, _state.Usage);
        _state = _state with
        {
            CompactionEventOffset = CountDurableEvents() + 1,
            CompactionSummaryContentId = checkpoint.ContentId,
            CompactionCheckpointEventId = checkpoint.ContentId,
            LastCompactedAt = completedAt,
            LastCompactionTrigger = checkpoint.Trigger,
            LastCompactionTokensBefore = result.TokensBefore,
            LastCompactionTokensAfter = result.TokensAfter,
            Usage = usage,
            UpdatedAt = completedAt,
        };
        _summary = _summary with
        {
            Usage = usage,
            UpdatedAt = completedAt,
        };

        var rawCheckpoint = new AgentRawEvent(
            ProviderId,
            SessionId,
            completedAt,
            CompactionCheckpointEventType,
            JsonSerializer.SerializeToElement(checkpoint, AgentJsonSerializerContext.Default.AgentCompactionCheckpoint),
            runId);
        var completionMessage = result.MessagesSummarized == 0 && result.SerializerStatistics.OmittedAttachmentCount > 0
            ? $"{trigger} local compaction compacted inline media attachments in retained context."
            : $"{trigger} local compaction summarized {result.MessagesSummarized} messages.";
        var completed = new AgentSessionUpdateEvent(
            ProviderId,
            SessionId,
            completedAt,
            runId,
            AgentSessionUpdateKind.CompactionCompleted,
            completionMessage,
            Details: CreateCompactionDetailsElement(checkpoint),
            Usage: usage);
        await AppendEventsAsync([rawCheckpoint, completed], cancellationToken).ConfigureAwait(false);
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

    private static int GetCompactionSummarizerMaxOutputTokens(
        AgentCompactionSettings settings,
        AgentTokenBudget budget,
        long postCompactionTargetTokens)
    {
        var ratio = settings.SummaryOutputRatio > 0
            ? Math.Min(settings.SummaryOutputRatio, AgentCompactionSettings.MaxSummaryOutputRatio)
            : AgentCompactionSettings.DefaultSummaryOutputRatio;
        var hardCap = budget.InputContextLimit is > 0
            ? (long)Math.Ceiling(budget.InputContextLimit.Value * ratio)
            : 1L;
        var summaryShare = settings.SummaryShareOfTarget > 0
            ? Math.Min(settings.SummaryShareOfTarget, AgentCompactionSettings.MaxSummaryShareOfTarget)
            : AgentCompactionSettings.DefaultSummaryShareOfTarget;
        var desired = Math.Max((long)Math.Floor(postCompactionTargetTokens * summaryShare), 1L);
        desired = Math.Min(desired, hardCap);

        if (budget.MaxOutputTokens is > 0)
        {
            desired = Math.Min(desired, budget.MaxOutputTokens.Value);
        }

        return (int)Math.Clamp(desired, 1L, int.MaxValue);
    }

    private static double ResolvePostCompactionTargetRatio(AgentCompactionSettings settings)
        => settings.PostCompactionTargetRatio > 0
            ? Math.Min(settings.PostCompactionTargetRatio, AgentCompactionSettings.MaxPostCompactionTargetRatio)
            : AgentCompactionSettings.DefaultPostCompactionTargetRatio;

    private static long ResolvePostCompactionTargetTokens(AgentCompactionSettings settings, AgentTokenBudget budget)
    {
        var inputContextLimit = budget.InputContextLimit is > 0
            ? budget.InputContextLimit.Value
            : 1L;
        return Math.Max((long)Math.Floor(inputContextLimit * ResolvePostCompactionTargetRatio(settings)), 1L);
    }

    private static long ResolveCheckpointTargetTokens(AgentCompactionSettings settings, long postCompactionTargetTokens)
    {
        var summaryShare = settings.SummaryShareOfTarget > 0
            ? Math.Min(settings.SummaryShareOfTarget, AgentCompactionSettings.MaxSummaryShareOfTarget)
            : AgentCompactionSettings.DefaultSummaryShareOfTarget;
        return Math.Max((long)Math.Floor(postCompactionTargetTokens * summaryShare), 1L);
    }

    private static bool ShouldShrinkCompactionSummary(
        long postCompactionTargetTokens,
        long checkpointTokens,
        long checkpointTargetTokens)
    {
        var relativeSummarySizeThreshold = Math.Max(checkpointTargetTokens, postCompactionTargetTokens / 2);
        return checkpointTokens > checkpointTargetTokens || checkpointTokens >= relativeSummarySizeThreshold;
    }

    private static string DetermineTargetMissReason(
        AgentCompactionPreparation preparation,
        AgentCompactionResult result,
        bool targetMet,
        long postCompactionTargetTokens,
        long fixedPromptTokens,
        long checkpointTokens,
        long retainedMessageTokens)
    {
        if (targetMet)
        {
            return "none";
        }

        if (fixedPromptTokens >= postCompactionTargetTokens)
        {
            return "fixed_prompt";
        }

        if (result.OversizedAnchorReduced || preparation.OversizedAnchorMessage is not null)
        {
            return result.OversizedAnchorReduced ? "oversized_anchor_reduced" : "latest_user_anchor";
        }

        var anchorTokens = preparation.TurnPrefixMessages
            .Where(static message => message.Role is AgentConversationRole.User)
            .Sum(AgentTokenEstimator.EstimateMessage);
        if (anchorTokens > 0 && fixedPromptTokens + checkpointTokens + anchorTokens > postCompactionTargetTokens)
        {
            return "latest_user_anchor";
        }

        if (fixedPromptTokens + checkpointTokens > postCompactionTargetTokens || checkpointTokens >= postCompactionTargetTokens / 2)
        {
            return "summary_size";
        }

        if (retainedMessageTokens > 0)
        {
            return "retained_suffix";
        }

        return "input_fit_only";
    }

    private static JsonElement CreateCompactionDetailsElement(AgentCompactionCheckpoint checkpoint)
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

            if (checkpoint.TargetRatio is { } targetRatio)
            {
                writer.WriteNumber("targetRatio", targetRatio);
            }
            else
            {
                writer.WriteNull("targetRatio");
            }

            if (checkpoint.TargetTokens is { } targetTokens)
            {
                writer.WriteNumber("targetTokens", targetTokens);
            }
            else
            {
                writer.WriteNull("targetTokens");
            }

            if (checkpoint.TargetMet is { } targetMet)
            {
                writer.WriteBoolean("targetMet", targetMet);
            }
            else
            {
                writer.WriteNull("targetMet");
            }

            writer.WriteString("targetMissReason", checkpoint.TargetMissReason);
            if (checkpoint.PlanningAttemptCount is { } planningAttemptCount)
            {
                writer.WriteNumber("planningAttemptCount", planningAttemptCount);
            }
            else
            {
                writer.WriteNull("planningAttemptCount");
            }

            if (checkpoint.PostCompactionInputRatio is { } postCompactionInputRatio)
            {
                writer.WriteNumber("postCompactionInputRatio", postCompactionInputRatio);
            }
            else
            {
                writer.WriteNull("postCompactionInputRatio");
            }

            if (checkpoint.CheckpointTokens is { } checkpointTokens)
            {
                writer.WriteNumber("checkpointTokens", checkpointTokens);
            }
            else
            {
                writer.WriteNull("checkpointTokens");
            }

            if (checkpoint.FixedPromptTokens is { } fixedPromptTokens)
            {
                writer.WriteNumber("fixedPromptTokens", fixedPromptTokens);
            }
            else
            {
                writer.WriteNull("fixedPromptTokens");
            }

            if (checkpoint.RetainedMessageTokens is { } retainedMessageTokens)
            {
                writer.WriteNumber("retainedMessageTokens", retainedMessageTokens);
            }
            else
            {
                writer.WriteNull("retainedMessageTokens");
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
            if (checkpoint.ModelVisibleReadFileCount is { } modelVisibleReadFileCount)
            {
                writer.WriteNumber("modelVisibleReadFileCount", modelVisibleReadFileCount);
            }
            else
            {
                writer.WriteNull("modelVisibleReadFileCount");
            }

            if (checkpoint.ModelVisibleModifiedFileCount is { } modelVisibleModifiedFileCount)
            {
                writer.WriteNumber("modelVisibleModifiedFileCount", modelVisibleModifiedFileCount);
            }
            else
            {
                writer.WriteNull("modelVisibleModifiedFileCount");
            }

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

    private static bool FitsResolvedPromptBudget(long promptTokens, AgentTokenBudget budget)
    {
        return budget.InputContextLimit is null || promptTokens <= budget.InputContextLimit.Value;
    }

    private static AgentSessionUsage? CreateCompactionUsage(
        AgentCompactionResult result,
        AgentTokenBudget budget,
        int messageCount,
        AgentSessionUsage? previousUsage)
    {
        return new AgentSessionUsage(
            Window: new AgentWindowUsageSnapshot(
                CurrentTokens: result.TokensAfter,
                TokenLimit: budget.InputContextLimit,
                MessageCount: messageCount,
                Label: "Post-compaction window",
                TotalContextEnvelope: budget.TotalContextEnvelope,
                MaxOutputTokens: budget.MaxOutputTokens),
            LastOperation: previousUsage?.LastOperation,
            RateLimits: previousUsage?.RateLimits,
            Scope: AgentUsageScope.Compaction,
            Source: AgentUsageSource.RecoveredHistory,
            UpdatedAt: DateTimeOffset.UtcNow,
            Details: previousUsage?.Details);
    }

    private long CountDurableEvents()
        => _history.Count(static @event => @event is not AgentContentDeltaEvent);

    private AgentCompactionPreparation? TryCreateInlineMediaCompactionPreparation(
        AgentCompactionTrigger trigger,
        string? systemMessage,
        string? developerInstructions,
        AgentSessionUsage? planningUsage)
    {
        if (_conversation.Count == 0)
        {
            return null;
        }

        var previousSummary = AgentCompactionCheckpoint.TryExtractSummary(_conversation[0]);
        var conversationStartIndex = previousSummary is null ? 0 : 1;
        var messagesToKeep = _conversation.Skip(conversationStartIndex).ToArray();
        if (messagesToKeep.Length == 0 ||
            !AgentMediaCompaction.ContainsPrunableInlineImages(messagesToKeep, ShouldPreserveInlineMediaInCompactedMessage))
        {
            return null;
        }

        var tokensBefore = AgentTokenEstimator.EstimatePromptTokens(
            systemMessage,
            developerInstructions,
            _conversation,
            planningUsage);

        return new AgentCompactionPreparation(
            trigger,
            MessagesToSummarize: [],
            TurnPrefixMessages: [],
            MessagesToKeep: messagesToKeep,
            AnchorContentId: FindLatestUserContentId(),
            IsSplitTurn: false,
            TokensBefore: tokensBefore,
            PreviousSummary: previousSummary);
    }

    private bool ShouldPreserveInlineMediaInCompactedMessage(AgentConversationMessage message)
        => _conversation.Count > 0 &&
           _conversation[^1].Role is AgentConversationRole.User &&
           ReferenceEquals(message, _conversation[^1]);

    private bool ShouldPreserveInlineMediaForActiveRun(AgentConversationMessage message)
    {
        if (_activeRunConversationStartIndex is not { } startIndex)
        {
            return false;
        }

        for (var index = Math.Max(startIndex, 0); index < _conversation.Count; index++)
        {
            if (ReferenceEquals(_conversation[index], message))
            {
                return true;
            }
        }

        return false;
    }

    private string? FindLatestUserContentId()
        => _history
            .OfType<AgentContentCompletedEvent>()
            .LastOrDefault(static @event => @event.Kind is AgentContentKind.User)
            ?.ContentId;

    private static string? GetLatestUserRequest(IReadOnlyList<AgentConversationMessage> conversation)
        => conversation
            .Reverse()
            .Where(static message => message.Role is AgentConversationRole.User)
            .SelectMany(static message => message.Parts.OfType<AgentMessagePart.Text>())
            .Select(static part => part.Value)
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));

    private long? TryResolveFirstKeptEventOffset(IReadOnlyList<AgentConversationMessage> keptMessages)
    {
        if (keptMessages.Count == 0)
        {
            return null;
        }

        var conversationStartIndex = _conversation.Count > 0 &&
                                     AgentCompactionCheckpoint.TryExtractSummary(_conversation[0]) is not null
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
                var message = rawEvent.Raw.Deserialize(AgentJsonSerializerContext.Default.AgentConversationMessage);
                if (message is not null)
                {
                    results.Add((offset, rawEvent.BackendEventType));
                }
            }
        }

        return results;
    }

    private static IReadOnlyList<AgentMessagePart> MapInputItems(IReadOnlyList<AgentInputItem> items)
    {
        var parts = new List<AgentMessagePart>(items.Count);
        foreach (var item in items)
        {
            switch (item)
            {
                case AgentInputItem.Text text:
                    parts.Add(new AgentMessagePart.Text(text.Value));
                    break;
                case AgentInputItem.ImageUrl imageUrl:
                    if (AgentDataUri.TryParseBase64(imageUrl.Url, out var mediaType, out var base64Data))
                    {
                        parts.Add(new AgentMessagePart.Data(base64Data, mediaType, "image"));
                    }
                    else
                    {
                        parts.Add(new AgentMessagePart.Uri(imageUrl.Url, GuessMediaType(imageUrl.Url)));
                    }

                    break;
                case AgentInputItem.LocalImage localImage:
                    parts.Add(new AgentMessagePart.Data(
                        Convert.ToBase64String(File.ReadAllBytes(localImage.Path)),
                        ResolveMediaType(localImage.MediaType, localImage.Path),
                        string.IsNullOrWhiteSpace(localImage.DisplayName) ? Path.GetFileName(localImage.Path) : localImage.DisplayName));
                    break;
                case AgentInputItem.File file:
                    parts.Add(new AgentMessagePart.Text(RenderFileInput(file)));
                    break;
                case AgentInputItem.Directory directory:
                    parts.Add(new AgentMessagePart.Text(
                        string.IsNullOrWhiteSpace(directory.DisplayName)
                            ? $"Directory: {directory.Path}"
                            : $"Directory ({directory.DisplayName}): {directory.Path}"));
                    break;
                case AgentInputItem.Selection selection:
                    parts.Add(new AgentMessagePart.Text(
                        $"""
                        Selection: {selection.DisplayName}
                        File: {selection.FilePath}
                        Text:
                        {selection.SelectedText}
                        """));
                    break;
                case AgentInputItem.Skill skill:
                    parts.Add(new AgentMessagePart.Text($"Skill: {skill.Name} ({skill.Path})"));
                    break;
                case AgentInputItem.Mention mention:
                    parts.Add(new AgentMessagePart.Text($"Mention: {mention.Name} ({mention.Path})"));
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
        var rendered = result.Items.Count == 0 ? string.Empty : AgentToolResultImages.RenderText(result);
        return string.IsNullOrWhiteSpace(rendered)
            ? (result.Error ?? "(no output)")
            : rendered;
    }

    private AgentToolResult CreateModelVisibleToolResult(
        AgentMessagePart.ToolCall toolCall,
        AgentToolResult result,
        string? systemMessage,
        string? developerInstructions,
        AgentModelInfo? modelInfo)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        ArgumentNullException.ThrowIfNull(result);

        if (IsSkillActivationTool(toolCall))
        {
            return result;
        }

        // The budget is for text: an image of the result is kept beside the excerpt.
        var images = result.Items.Where(AgentToolResultImages.IsImage).ToArray();
        var rendered = images.Length == 0
            ? RenderToolResult(result)
            : RenderToolResult(result with { Items = [.. result.Items.Where(static item => !AgentToolResultImages.IsImage(item))] });
        var characterLimit = ResolveModelVisibleToolResultCharacterLimit(systemMessage, developerInstructions, modelInfo);
        if (characterLimit <= 0 || rendered.Length <= characterLimit)
        {
            return result;
        }

        var excerpt = CreateModelVisibleToolResultExcerpt(toolCall.Name, rendered, characterLimit);
        return result with
        {
            Items = [new AgentToolResultItem.Text(excerpt), .. images],
        };
    }

    private int ResolveModelVisibleToolResultCharacterLimit(
        string? systemMessage,
        string? developerInstructions,
        AgentModelInfo? modelInfo)
    {
        var settings = Provider.Compaction ?? AgentCompactionSettings.Default;
        var budget = AgentTokenBudgetResolver.Resolve(modelInfo, settings);
        var inputLimit = budget.InputContextLimit ?? _state.Usage?.TokenLimit;
        if (inputLimit is not > 0)
        {
            return FallbackModelVisibleToolResultCharacterLimit;
        }

        var thresholdTokens = Math.Max((long)Math.Floor(inputLimit.Value * settings.Ratio), 1L);
        var perToolTokenLimit = Math.Max((long)Math.Floor(inputLimit.Value * AgentCompactionSettings.DefaultPostCompactionTargetRatio), 1L);
        var providerConversation = CreateProviderConversation();
        var currentEstimate = AgentTokenEstimator.EstimatePromptTokens(
            systemMessage,
            developerInstructions,
            providerConversation.Messages,
            SelectUsageForPromptEstimate(providerConversation));
        var remainingTokens = Math.Max(thresholdTokens - currentEstimate.Tokens, 0L);
        var allowedTokens = Math.Min(perToolTokenLimit, remainingTokens);
        if (allowedTokens <= 0)
        {
            return ToolResultTruncationFooterReserve;
        }

        var allowedCharacters = allowedTokens * 4L;
        return (int)Math.Min(Math.Max(allowedCharacters, ToolResultTruncationFooterReserve), int.MaxValue);
    }

    private static string CreateModelVisibleToolResultExcerpt(string toolName, string output, int characterLimit)
    {
        var footer = Environment.NewLine + Environment.NewLine +
            $"[CodeAlta: tool '{toolName}' output was truncated from {output.Length:#,0} characters to keep the active context within the model input limit. Re-run the tool with narrower arguments if more detail is needed.]";
        if (characterLimit <= footer.Length + 32)
        {
            return footer.Trim();
        }

        var contentBudget = characterLimit - footer.Length;
        var headLength = Math.Max((int)Math.Floor(contentBudget * 0.70d), 0);
        var tailLength = Math.Max(contentBudget - headLength, 0);
        if (headLength + tailLength >= output.Length)
        {
            return output;
        }

        var head = output[..headLength].TrimEnd();
        var tail = tailLength == 0 ? string.Empty : output[^tailLength..].TrimStart();
        return string.IsNullOrEmpty(tail)
            ? head + footer
            : head + footer + Environment.NewLine + Environment.NewLine + tail;
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

    private static string? ExtractAssistantSummary(AgentConversationMessage message)
        => message.Parts.OfType<AgentMessagePart.Text>().Select(static part => part.Value).FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));

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

    private static string ResolveMediaType(string? mediaType, string pathOrUri)
        => string.IsNullOrWhiteSpace(mediaType)
            ? GuessMediaType(pathOrUri) ?? "application/octet-stream"
            : mediaType.Trim();

    private static JsonElement SerializeAgentInput(AgentInput input)
        => JsonSerializer.SerializeToElement(input, AgentJsonSerializerContext.Default.AgentInput);

    private static JsonElement SerializeLocalMessage(AgentConversationMessage message)
        => JsonSerializer.SerializeToElement(message, AgentJsonSerializerContext.Default.AgentConversationMessage);

    private static JsonElement? CreateReasoningDetails(
        AgentMessagePart.Reasoning reasoning,
        bool redactProtectedData = false)
    {
        if (string.IsNullOrWhiteSpace(reasoning.ProtectedData) && reasoning.SummaryParts is null)
        {
            return null;
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            if (reasoning.SummaryParts is { } summaryParts)
            {
                writer.WritePropertyName("summaryParts");
                writer.WriteStartArray();
                foreach (var summaryPart in summaryParts)
                {
                    writer.WriteStringValue(summaryPart);
                }

                writer.WriteEndArray();
            }

            if (!string.IsNullOrWhiteSpace(reasoning.ProtectedData) && redactProtectedData)
            {
                writer.WriteBoolean("protectedDataRedacted", true);
            }
            else if (!string.IsNullOrWhiteSpace(reasoning.ProtectedData))
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

    private static JsonElement CreateToolCallDetails(AgentMessagePart.ToolCall toolCall, string? workingDirectory)
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

    private static JsonElement CreateToolResultDetails(AgentMessagePart.ToolCall toolCall, AgentToolResult result, string? workingDirectory, string? diff = null)
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
            // The details name an image; its bytes, when the result holds them, stay in the tool message.
            var recorded = result.Items.Any(static item => item is AgentToolResultItem.Image)
                ? result with { Items = [.. result.Items.Select(static item => item is AgentToolResultItem.Image ? new AgentToolResultItem.Text(AgentToolResultImages.Describe(item)) : item)] }
                : result;
            JsonSerializer.Serialize(writer, recorded, AgentJsonSerializerContext.Default.AgentToolResult);
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

    private static ToolFileActivity GetToolFileActivity(AgentMessagePart.ToolCall toolCall, string? workingDirectory)
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
            case "view_image":
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
            // The file tools of an agent CLI that runs them itself (Claude Code): they name the file `file_path`.
            case "Read":
                if (GetPath(toolCall.Arguments, "file_path") is { Length: > 0 } providerReadPath)
                {
                    AddReadFile(Resolve(providerReadPath));
                }

                break;
            case "Edit":
            case "MultiEdit":
            case "Write":
                if (GetPath(toolCall.Arguments, "file_path") is { Length: > 0 } providerModifiedPath)
                {
                    AddModifiedFile(Resolve(providerModifiedPath));
                }

                break;
            case "NotebookEdit":
                if (GetPath(toolCall.Arguments, "notebook_path") is { Length: > 0 } providerNotebookPath)
                {
                    AddModifiedFile(Resolve(providerNotebookPath));
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
                        foreach (var path in AgentApplyPatch.GetTouchedPaths(patchInput.GetString()!, workingDirectory ?? Environment.CurrentDirectory))
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

    private static IReadOnlyList<string> GetTrackedFileMutationPaths(AgentMessagePart.ToolCall toolCall, string? workingDirectory)
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
            "apply_patch" or
            "Edit" or
            "MultiEdit" or
            "Write" or
            "NotebookEdit";

    private static bool IsSkillActivationTool(AgentMessagePart.ToolCall toolCall)
        => string.Equals(toolCall.Name, "codealta_skills_activate", StringComparison.Ordinal) ||
           IsAltaSkillActivationCommand(toolCall);

    private static bool IsAltaSkillActivationCommand(AgentMessagePart.ToolCall toolCall)
    {
        if (!string.Equals(toolCall.Name, "alta", StringComparison.Ordinal) ||
            !toolCall.Arguments.TryGetProperty("args", out var argsElement) ||
            argsElement.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var args = argsElement.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString())
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Select(static item => item!)
            .ToArray();
        return args is ["skills_activate", ..] ||
               args is ["skill", "activate", ..] ||
               args is ["skills", "activate", ..];
    }

    private static AgentLoadedSkillState? TryCreateLoadedSkillState(
        AgentMessagePart.ToolCall toolCall,
        AgentToolResult result)
    {
        var renderedResult = result.Items.OfType<AgentToolResultItem.Text>()
            .Select(static item => item.Value)
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
        if (string.IsNullOrWhiteSpace(renderedResult))
        {
            return null;
        }

        var payload = renderedResult;
        var skillName = GetArgumentString(toolCall.Arguments, "skillName")
            ?? GetArgumentString(toolCall.Arguments, "skill_name");
        if (IsAltaSkillActivationCommand(toolCall))
        {
            var activationRecord = TryReadAltaSkillActivationRecord(renderedResult);
            if (activationRecord is null)
            {
                return null;
            }

            payload = activationRecord.Payload;
            skillName = activationRecord.SkillName ?? skillName;
        }

        return TryCreateLoadedSkillState(skillName, null, payload, "model", toolCall.CallId);
    }

    private static AltaSkillActivationRecord? TryReadAltaSkillActivationRecord(string transcript)
    {
        foreach (var line in transcript.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            JsonDocument? document = null;
            try
            {
                document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!root.TryGetProperty("type", out var type) ||
                    type.ValueKind != JsonValueKind.String ||
                    !string.Equals(type.GetString(), "alta.skill.activated", StringComparison.Ordinal))
                {
                    continue;
                }

                var payload = root.TryGetProperty("payload", out var payloadElement) && payloadElement.ValueKind == JsonValueKind.String
                    ? payloadElement.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(payload))
                {
                    return null;
                }

                var skillName = root.TryGetProperty("skillName", out var skillNameElement) && skillNameElement.ValueKind == JsonValueKind.String
                    ? skillNameElement.GetString()
                    : null;
                return new AltaSkillActivationRecord(skillName, payload);
            }
            catch (JsonException)
            {
            }
            finally
            {
                document?.Dispose();
            }
        }

        return null;
    }

    private static bool TryCreateUserActivatedSkillState(
        AgentInput input,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out AgentLoadedSkillState? activatedSkill)
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

    private static AgentLoadedSkillState? TryCreateLoadedSkillState(
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
        var activation = new AgentLoadedSkillState
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

    private static AgentSessionState RebuildLoadedSkillsState(
        AgentSessionState state,
        IReadOnlyList<AgentEvent> history)
    {
        var loadedSkills = new Dictionary<string, AgentLoadedSkillState>(StringComparer.OrdinalIgnoreCase);
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

            AgentLoadedSkillState? loadedSkill;
            try
            {
                loadedSkill = rawEvent.Raw.Deserialize(
                    AgentJsonSerializerContext.Default.AgentLoadedSkillState);
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

    private static IReadOnlyList<AgentLoadedSkillState> MergeLoadedSkill(
        IReadOnlyList<AgentLoadedSkillState> existing,
        AgentLoadedSkillState loadedSkill)
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

    private sealed record AltaSkillActivationRecord(string? SkillName, string Payload);

    private static AgentLoadedSkillState EnsureSkillAvailability(AgentLoadedSkillState skill)
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

    private static List<AgentConversationMessage> ReplayConversation(
        IReadOnlyList<AgentEvent> history,
        AgentSessionState state)
    {
        var conversation = new List<AgentConversationMessage>();
        AgentCompactionCheckpoint? latestCheckpoint = null;
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
                var checkpoint = rawEvent.Raw.Deserialize(AgentJsonSerializerContext.Default.AgentCompactionCheckpoint);
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
                var checkpoint = rawEvent.Raw.Deserialize(AgentJsonSerializerContext.Default.AgentCompactionCheckpoint);
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
                    conversation.Add(new AgentConversationMessage(
                        AgentConversationRole.User,
                        MapInputItems(input.Items)));
                }

                continue;
            }

            if (rawEvent.BackendEventType is AssistantMessageEventType or ToolMessageEventType)
            {
                var message = rawEvent.Raw.Deserialize(AgentJsonSerializerContext.Default.AgentConversationMessage);
                if (message is not null)
                {
                    conversation.Add(message);
                }

                continue;
            }

            if (latestCheckpoint is null && rawEvent.BackendEventType == CompactionSnapshotEventType)
            {
                var snapshot = rawEvent.Raw.Deserialize(AgentJsonSerializerContext.Default.AgentCompactionSnapshot);
                conversation.Clear();
                if (snapshot is not null)
                {
                    conversation.Add(snapshot.SummaryMessage);
                }
            }
        }

        var normalized = AgentConversationToolCallRecovery.NormalizeForReplay(conversation);
        return ReferenceEquals(normalized, conversation) ? conversation : [.. normalized];
    }

    private static AgentCompactionSnapshot CreateCompactionSnapshot(
        int includedEventCount,
        IReadOnlyList<AgentConversationMessage> conversation)
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
        return new AgentCompactionSnapshot
        {
            IncludedEventCount = includedEventCount,
            SummarizedMessageCount = conversation.Count,
            SummaryMessage = new AgentConversationMessage(
                AgentConversationRole.System,
                [new AgentMessagePart.Text(builder.ToString().Trim())]),
        };
    }

    private static string RenderCompactionLine(AgentConversationMessage message)
    {
        var segments = new List<string>(message.Parts.Count);
        foreach (var part in message.Parts)
        {
            switch (part)
            {
                case AgentMessagePart.Text text when !string.IsNullOrWhiteSpace(text.Value):
                    segments.Add(CompactText(text.Value));
                    break;
                case AgentMessagePart.Reasoning reasoning when !string.IsNullOrWhiteSpace(reasoning.Value):
                    segments.Add($"reasoning: {CompactText(reasoning.Value)}");
                    break;
                case AgentMessagePart.ToolCall toolCall:
                    segments.Add($"tool call {toolCall.Name}#{toolCall.CallId}");
                    break;
                case AgentMessagePart.ToolResult toolResult:
                    segments.Add($"tool result {toolResult.CallId}: {CompactText(RenderToolResult(toolResult.Result))}");
                    break;
                case AgentMessagePart.Uri uri:
                    segments.Add($"uri: {uri.Value}");
                    break;
                case AgentMessagePart.Data data:
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
            AgentConversationRole.User => "User",
            AgentConversationRole.Assistant => "Assistant",
            AgentConversationRole.Tool => "Tool",
            AgentConversationRole.System => "System",
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

    private static IReadOnlyList<AgentModelInfo> LoadConstructorModelCache(
        ModelProviderRuntimeDescriptor provider,
        IModelProviderTurnExecutor turnExecutor)
    {
        if (turnExecutor is not IModelProviderModelCatalog modelCatalog)
        {
            return [];
        }

        try
        {
            return modelCatalog.ListModelsAsync(provider, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch
        {
            return [];
        }
    }

    private sealed record ToolFileActivity(IReadOnlyList<string> ReadFiles, IReadOnlyList<string> ModifiedFiles);

    private sealed class SubscriberLease(Action dispose) : IDisposable
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
            cancellationToken.ThrowIfCancellationRequested();
            if (_cachedModels.Count == 0 && _turnExecutor is IModelProviderModelCatalog modelCatalog)
            {
                _cachedModels = await modelCatalog.ListModelsAsync(Provider, cancellationToken).ConfigureAwait(false);
            }

            _resolvedModelInfo = AgentModelIdentity.FindBestMatch(_cachedModels, modelId);
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
