#pragma warning disable OPENAI001
#pragma warning disable SCME0001

using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeAlta.Agent.LocalRuntime;
using CodeAlta.Agent.LocalRuntime.Tools;
using CodeAlta.Agent.OpenAI.CodexSubscription;
using OpenAI.Responses;
using XenoAtom.Logging;

namespace CodeAlta.Agent.OpenAI;

internal sealed class OpenAIResponsesTurnExecutor(
    OpenAIProviderOptions provider,
    CodexSubscriptionConcurrencyLimiter? codexSubscriptionConcurrencyLimiter = null) : ILocalAgentTurnExecutor, ILocalAgentProviderSessionCleanup, IAsyncDisposable
{
    private static readonly Logger Logger = LogManager.GetLogger("CodeAlta.Agent.OpenAI");
    private static readonly ResponseReasoningEffortLevel XHighReasoningEffortLevel = new("xhigh");
    private static readonly TimeSpan CodexBaseRetryDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan DefaultWebSocketIdleTimeout = TimeSpan.FromMinutes(5);
    private static readonly Random SharedRandom = Random.Shared;
    private const string WebSocketErrorCodeDataKey = "OpenAI.WebSocketErrorCode";
    private const string WebSocketErrorPayloadDataKey = "OpenAI.WebSocketErrorPayload";
    private const string WebSocketErrorTypeDataKey = "OpenAI.WebSocketErrorType";
    private const string WebSocketWrappedErrorDataKey = "OpenAI.WebSocketWrappedError";
    private const string WebSocketConnectionLimitReachedCode = "websocket_connection_limit_reached";

    private readonly CodexSubscriptionConcurrencyLimiter _codexSubscriptionConcurrencyLimiter = codexSubscriptionConcurrencyLimiter ?? new CodexSubscriptionConcurrencyLimiter();
    private readonly ConcurrentDictionary<string, OpenAIResponsesLiveContinuation> _liveContinuations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, OpenAIResponsesWebSocketSessionEntry> _webSocketSessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _webSocketHttpFallbackSessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<OpenAIResponsesTurnStateKey, CodexTurnState> _codexTurnStates = new();

    public Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(
        LocalAgentProviderDescriptor providerDescriptor,
        CancellationToken cancellationToken = default)
        => OpenAIProviderSdkFactory.ListModelsAsync(provider, providerDescriptor, cancellationToken);

    public ValueTask DisposeAsync()
    {
        foreach (var session in _webSocketSessions.Values)
        {
            session.Dispose();
        }

        _webSocketSessions.Clear();
        _liveContinuations.Clear();
        _webSocketHttpFallbackSessions.Clear();
        _codexTurnStates.Clear();
        return ValueTask.CompletedTask;
    }

    ValueTask ILocalAgentProviderSessionCleanup.DisposeProviderSessionAsync(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ResetWebSocketSession(sessionId);
        ClearLiveContinuation(sessionId);
        _webSocketHttpFallbackSessions.TryRemove(sessionId, out _);
        ClearCodexTurnStates(sessionId);
        return ValueTask.CompletedTask;
    }

    public Task<LocalAgentTurnResponse> ExecuteTurnAsync(
        LocalAgentTurnRequest request,
        Func<LocalAgentTurnDelta, CancellationToken, ValueTask> onUpdate,
        CancellationToken cancellationToken = default)
        => ExecuteTurnAsync(
            request,
            onUpdate,
            static (_, _) => ValueTask.CompletedTask,
            cancellationToken);

    public async Task<LocalAgentTurnResponse> ExecuteTurnAsync(
        LocalAgentTurnRequest request,
        Func<LocalAgentTurnDelta, CancellationToken, ValueTask> onUpdate,
        Func<LocalAgentTurnSessionUpdate, CancellationToken, ValueTask> onSessionUpdate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(onUpdate);
        ArgumentNullException.ThrowIfNull(onSessionUpdate);

        try
        {
            // Codex CLI treats dropped response streams as retryable turn-level failures and
            // defaults to five reconnect attempts (six total attempts including the original).
            var retryBudget = provider.CodexSubscription is null ? 1 : 6;
            var refreshedCredentialAfterUnauthorized = false;
            for (var attempt = 1; ; attempt++)
            {
                var emittedUpdate = false;
                try
                {
                    var turnState = GetCodexTurnState(request);
                    await using var concurrencyLease = await CreateCodexConcurrencyLeaseAsync(
                        request,
                        onSessionUpdate,
                        cancellationToken).ConfigureAwait(false);
                    var client = OpenAIProviderSdkFactory.CreateResponsesClient(
                        provider,
                        new OpenAIResponsesClientFactoryContext(
                            request.ModelId,
                            request.SessionId,
                            request.RunId,
                            request.Provider,
                            turnState));
                    var fullOptions = await CreateRequestPayloadAsync(request, cancellationToken).ConfigureAwait(false);
                    LogCodexDiagnostic("request", request, attempt);
                    WriteCodexConsoleDiagnostic(
                        provider,
                        $"request attempt={attempt} session={request.SessionId} run={request.RunId.Value} transport={ResolveInitialTransport(request)} fullPayload={FormatCodexConsolePayload(SerializeModel(fullOptions))}");
                    ResponseResult? completedResponse = null;
                    ResponseResult? latestResponse = null;
                    var streamedOutputItems = new SortedDictionary<int, ResponseItem>();
                    var sideChannelEvents = new List<OpenAIResponsesWebSocketSideChannelEvent>();

                    async Task ProcessStreamAsync(OpenAIResponsesTransport transport)
                    {
                        await foreach (var update in CreateResponseStreamingAsync(
                                client,
                                request,
                                fullOptions,
                                transport,
                                sideChannelEvents,
                                cancellationToken).ConfigureAwait(false))
                        {
                            WriteCodexConsoleDiagnostic(
                                provider,
                                $"stream transport={transport} update={update.GetType().Name} payload={FormatCodexConsolePayload(SerializeModel(update))}");
                            switch (update)
                            {
                                case StreamingResponseCreatedUpdate created:
                                    latestResponse = created.Response;
                                    break;
                                case StreamingResponseInProgressUpdate inProgress:
                                    latestResponse = inProgress.Response;
                                    break;
                                case StreamingResponseOutputTextDeltaUpdate outputTextDelta when !string.IsNullOrEmpty(outputTextDelta.Delta):
                                    emittedUpdate = true;
                                    await onUpdate(
                                        new LocalAgentTurnDelta
                                        {
                                            Kind = AgentContentKind.Assistant,
                                            ContentId = outputTextDelta.ItemId,
                                            Text = outputTextDelta.Delta,
                                        },
                                        cancellationToken).ConfigureAwait(false);
                                    break;
                                case StreamingResponseRefusalDeltaUpdate refusalDelta when !string.IsNullOrEmpty(refusalDelta.Delta):
                                    emittedUpdate = true;
                                    await onUpdate(
                                        new LocalAgentTurnDelta
                                        {
                                            Kind = AgentContentKind.Assistant,
                                            ContentId = refusalDelta.ItemId,
                                            Text = refusalDelta.Delta,
                                        },
                                        cancellationToken).ConfigureAwait(false);
                                    break;
                                case StreamingResponseReasoningSummaryTextDeltaUpdate reasoningSummaryDelta when !string.IsNullOrEmpty(reasoningSummaryDelta.Delta):
                                    emittedUpdate = true;
                                    await onUpdate(
                                        new LocalAgentTurnDelta
                                        {
                                            Kind = AgentContentKind.Reasoning,
                                            ContentId = reasoningSummaryDelta.ItemId,
                                            Text = reasoningSummaryDelta.Delta,
                                        },
                                        cancellationToken).ConfigureAwait(false);
                                    break;
                                case StreamingResponseReasoningTextDeltaUpdate reasoningTextDelta when !string.IsNullOrEmpty(reasoningTextDelta.Delta):
                                    emittedUpdate = true;
                                    await onUpdate(
                                        new LocalAgentTurnDelta
                                        {
                                            Kind = AgentContentKind.Reasoning,
                                            ContentId = reasoningTextDelta.ItemId,
                                            Text = reasoningTextDelta.Delta,
                                        },
                                        cancellationToken).ConfigureAwait(false);
                                    break;
                                case StreamingResponseOutputItemDoneUpdate outputItemDone when outputItemDone.Item is not null:
                                    streamedOutputItems[outputItemDone.OutputIndex] = outputItemDone.Item;
                                    break;
                                case StreamingResponseIncompleteUpdate incomplete:
                                    latestResponse = incomplete.Response;
                                    completedResponse = incomplete.Response;
                                    break;
                                case StreamingResponseFailedUpdate failed:
                                    throw CreateResponseFailureException(failed.Response, "failed");
                                case StreamingResponseErrorUpdate error:
                                    throw CreateTurnExecutionException(CreateStreamErrorException(error));
                                case StreamingResponseCompletedUpdate completed:
                                    latestResponse = completed.Response;
                                    completedResponse = completed.Response;
                                    break;
                            }
                        }
                    }

                    var initialTransport = ResolveInitialTransport(request);
                    try
                    {
                        await ProcessStreamAsync(initialTransport).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ShouldFallbackFromWebSocket(provider, initialTransport, emittedUpdate, ex))
                    {
                        MarkWebSocketHttpFallback(request.SessionId);
                        ResetWebSocketSession(request.SessionId);
                        WriteCodexConsoleDiagnostic(
                            provider,
                            $"websocket fallback session={request.SessionId} run={request.RunId.Value} error={ex.GetType().Name}");
                        completedResponse = null;
                        latestResponse = null;
                        streamedOutputItems.Clear();
                        sideChannelEvents.Clear();
                        emittedUpdate = false;
                        await ProcessStreamAsync(OpenAIResponsesTransport.Http).ConfigureAwait(false);
                    }

                    WriteCodexConsoleDiagnostic(
                        provider,
                        $"stream end latestOutputItems={latestResponse?.OutputItems.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "(none)"} streamedOutputItems={streamedOutputItems.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)} terminal={(completedResponse is null ? "(none)" : completedResponse.Status.ToString())}");
                    completedResponse = CreateResponseFromTerminalOrStreamedItems(
                        request,
                        completedResponse,
                        latestResponse,
                        streamedOutputItems);

                    if (completedResponse is null)
                    {
                        throw new InvalidOperationException("The OpenAI Responses stream completed without a terminal response payload or reconstructable output.");
                    }

                    if (completedResponse.Status is ResponseStatus.Failed)
                    {
                        throw CreateResponseFailureException(completedResponse, "failed");
                    }

                    if (completedResponse.Status is ResponseStatus.Incomplete &&
                        completedResponse.OutputItems.Count == 0)
                    {
                        throw CreateTurnExecutionException(CreateResponseFailureException(completedResponse, "incomplete"));
                    }

                    var (assistantMessage, assistantPartContentIds) = MapAssistantMessage(completedResponse);
                    UpdateLiveContinuation(request, fullOptions, completedResponse, assistantMessage);
                    WriteCodexConsoleDiagnostic(
                        provider,
                        $"mapped assistantParts={assistantMessage.Parts.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)} textParts={assistantMessage.Parts.OfType<LocalAgentMessagePart.Text>().Count().ToString(System.Globalization.CultureInfo.InvariantCulture)} reasoningParts={assistantMessage.Parts.OfType<LocalAgentMessagePart.Reasoning>().Count().ToString(System.Globalization.CultureInfo.InvariantCulture)} toolCalls={assistantMessage.Parts.OfType<LocalAgentMessagePart.ToolCall>().Count().ToString(System.Globalization.CultureInfo.InvariantCulture)} response={completedResponse.Id ?? "(none)"}");
                    LogCodexDiagnostic("response", request, attempt, completedResponse.Id);
                    return new LocalAgentTurnResponse
                    {
                        AssistantMessage = assistantMessage,
                        AssistantPartContentIds = assistantPartContentIds,
                        Usage = CreateUsage(request, completedResponse),
                        ProviderSessionId = string.IsNullOrWhiteSpace(completedResponse.Id) ? null : completedResponse.Id,
                        ProviderState = CreateProviderState(completedResponse, sideChannelEvents),
                        Summary = ExtractSummary(assistantMessage),
                    };
                }
                catch (Exception ex) when (ShouldRefreshCodexSubscriptionCredential(
                    provider,
                    ex,
                    emittedUpdate,
                    refreshedCredentialAfterUnauthorized))
                {
                    ClearLiveContinuation(request.SessionId);
                    refreshedCredentialAfterUnauthorized = true;
                    LogCodexDiagnostic(
                        "credential-refresh",
                        request,
                        attempt,
                        httpStatus: System.Net.HttpStatusCode.Unauthorized,
                        errorType: ex.GetType().Name);
                    try
                    {
                        await OpenAIProviderSdkFactory.ForceRefreshCodexSubscriptionCredentialAsync(
                                provider,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (Exception refreshException) when (refreshException is not OperationCanceledException)
                    {
                        throw new InvalidOperationException(
                            "ChatGPT/Codex authentication failed; re-authentication is required.",
                            refreshException);
                    }
                }
                catch (Exception ex) when (ShouldRetryCodexSubscriptionRequest(
                    provider,
                    ex,
                    emittedUpdate,
                    attempt,
                    retryBudget,
                    out var delay))
                {
                    ClearLiveContinuation(request.SessionId);
                    LogCodexDiagnostic(
                        "retry",
                        request,
                        attempt,
                        httpStatus: GetHttpStatusCode(ex),
                        errorType: ex.GetType().Name);
                    await onSessionUpdate(
                            CreateCodexReconnectSessionUpdate(attempt, retryBudget),
                            cancellationToken)
                        .ConfigureAwait(false);
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException ex) when (IsTransportAbortedOperationCanceled(ex))
        {
            ClearLiveContinuation(request.SessionId);
            LogCodexDiagnostic(
                "error",
                request,
                attempt: 1,
                httpStatus: GetHttpStatusCode(ex),
                errorType: ex.GetType().Name);
            throw CreateTurnExecutionException(TranslateCodexSubscriptionException(provider, ex));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (LocalAgentTurnExecutionException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ClearLiveContinuation(request.SessionId);
            LogCodexDiagnostic(
                "error",
                request,
                attempt: 1,
                httpStatus: GetHttpStatusCode(ex),
                errorType: ex.GetType().Name);
            throw CreateTurnExecutionException(TranslateCodexSubscriptionException(provider, ex));
        }
    }

    private OpenAIResponsesTransport ResolveInitialTransport(LocalAgentTurnRequest request)
    {
        var transport = ResolveConfiguredInitialTransport(provider);
        return transport == OpenAIResponsesTransport.WebSocket &&
               _webSocketHttpFallbackSessions.ContainsKey(request.SessionId)
            ? OpenAIResponsesTransport.Http
            : transport;
    }

    private static OpenAIResponsesTransport ResolveConfiguredInitialTransport(OpenAIProviderOptions provider)
    {
        if (provider.CodexSubscription is not { } codexOptions)
        {
            return OpenAIResponsesTransport.Http;
        }

        return string.Equals(codexOptions.ResponseTransport, "http", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(codexOptions.ResponseTransport, "sse", StringComparison.OrdinalIgnoreCase)
            ? OpenAIResponsesTransport.Http
            : OpenAIResponsesTransport.WebSocket;
    }

    private static bool ShouldFallbackFromWebSocket(
        OpenAIProviderOptions provider,
        OpenAIResponsesTransport transport,
        bool emittedUpdate,
        Exception exception)
    {
        if (provider.CodexSubscription is null ||
            transport != OpenAIResponsesTransport.WebSocket ||
            emittedUpdate ||
            exception is OperationCanceledException && !IsTransportAbortedOperationCanceled(exception))
        {
            return false;
        }

        if (IsWebSocketConnectionLimitReached(exception))
        {
            return false;
        }

        if (IsWrappedWebSocketError(exception))
        {
            return GetHttpStatusCode(exception) == HttpStatusCode.UpgradeRequired;
        }

        if (GetHttpStatusCode(exception) is { } statusCode)
        {
            return statusCode == HttpStatusCode.UpgradeRequired;
        }

        return true;
    }

    private async IAsyncEnumerable<StreamingResponseUpdate> CreateResponseStreamingAsync(
        ResponsesClient client,
        LocalAgentTurnRequest request,
        CreateResponseOptions fullOptions,
        OpenAIResponsesTransport transport,
        List<OpenAIResponsesWebSocketSideChannelEvent> sideChannelEvents,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (transport == OpenAIResponsesTransport.WebSocket)
        {
            var entry = await GetOrCreateWebSocketSessionAsync(request, cancellationToken).ConfigureAwait(false);
            var options = entry.Session.HasOpenConnection
                ? CreateWebSocketContinuationRequestOptions(request, fullOptions)
                : fullOptions;
            var previousSideChannelReceived = entry.Session.SideChannelReceived;
            try
            {
                entry.Session.SideChannelReceived = sideChannelEvent =>
                {
                    sideChannelEvents.Add(sideChannelEvent);
                    previousSideChannelReceived?.Invoke(sideChannelEvent);
                };
                await foreach (var update in entry.Session
                    .CreateResponseStreamingAsync(options, fullOptions, cancellationToken)
                    .ConfigureAwait(false))
                {
                    yield return update;
                }
            }
            finally
            {
                entry.Session.SideChannelReceived = previousSideChannelReceived;
                ArmWebSocketSessionIdle(request.SessionId, entry);
            }

            yield break;
        }

        await foreach (var update in client.CreateResponseStreamingAsync(fullOptions, cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }
    }

    private async ValueTask<OpenAIResponsesWebSocketSessionEntry> GetOrCreateWebSocketSessionAsync(
        LocalAgentTurnRequest request,
        CancellationToken cancellationToken)
    {
        while (_webSocketSessions.TryGetValue(request.SessionId, out var existing))
        {
            if (existing.TryBeginUse())
            {
                return existing;
            }

            _webSocketSessions.TryRemove(new KeyValuePair<string, OpenAIResponsesWebSocketSessionEntry>(request.SessionId, existing));
        }

        var created = provider.ResponsesWebSocketSessionFactory is not null
            ? await provider.ResponsesWebSocketSessionFactory(
                    new OpenAIResponsesWebSocketSessionFactoryContext(
                        request.ModelId,
                        request.SessionId,
                        request.RunId,
                        request.Provider,
                        GetCodexTurnState(request) ?? new CodexTurnState()))
                .ConfigureAwait(false)
            : await CreateDefaultWebSocketSessionAsync(request, cancellationToken).ConfigureAwait(false);
        var createdEntry = new OpenAIResponsesWebSocketSessionEntry(created);

        if (_webSocketSessions.TryAdd(request.SessionId, createdEntry))
        {
            if (createdEntry.TryBeginUse())
            {
                return createdEntry;
            }

            ResetWebSocketSession(request.SessionId, createdEntry);
        }

        createdEntry.Dispose();
        return await GetOrCreateWebSocketSessionAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private ValueTask<IOpenAIResponsesWebSocketSession> CreateDefaultWebSocketSessionAsync(
        LocalAgentTurnRequest request,
        CancellationToken cancellationToken)
    {
        if (provider.CodexSubscription is not { } codexOptions)
        {
            throw new InvalidOperationException("OpenAI Responses WebSocket transport is only configured for Codex subscription providers.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var authManager = OpenAIProviderSdkFactory.CreateCodexSubscriptionAuthManager(
            provider,
            codexOptions,
            OpenAIProviderSdkFactory.ResolveStateRootPath(provider));
        IOpenAIResponsesWebSocketSession session = new OpenAICodexSubscriptionWebSocketSession(
            provider.BaseUri,
            codexOptions,
            authManager,
            request.SessionId,
            OpenAIProviderSdkFactory.CreateCodeAltaUserAgentApplicationId(),
            GetCodexTurnState(request) ?? new CodexTurnState(),
            provider.ResponsesWebSocketIdleTimeout ?? DefaultWebSocketIdleTimeout);
        return ValueTask.FromResult(session);
    }

    private void ResetWebSocketSession(string sessionId)
        => ResetWebSocketSession(sessionId, expectedEntry: null);

    private void ResetWebSocketSession(string sessionId, OpenAIResponsesWebSocketSessionEntry? expectedEntry)
    {
        OpenAIResponsesWebSocketSessionEntry? removed = null;
        if (expectedEntry is null)
        {
            _webSocketSessions.TryRemove(sessionId, out removed);
        }
        else if (_webSocketSessions.TryRemove(new KeyValuePair<string, OpenAIResponsesWebSocketSessionEntry>(sessionId, expectedEntry)))
        {
            removed = expectedEntry;
        }

        removed?.Dispose();
    }

    private void MarkWebSocketHttpFallback(string sessionId)
        => _webSocketHttpFallbackSessions[sessionId] = 0;

    private void ArmWebSocketSessionIdle(string sessionId, OpenAIResponsesWebSocketSessionEntry entry)
    {
        if (!_webSocketSessions.TryGetValue(sessionId, out var current) ||
            !ReferenceEquals(current, entry))
        {
            entry.EndUse();
            return;
        }

        var timeout = provider.ResponsesWebSocketIdleTimeout ?? DefaultWebSocketIdleTimeout;
        entry.EndUseAndArmIdle(timeout, generation => ExpireIdleWebSocketSession(sessionId, entry, generation));
    }

    private void ExpireIdleWebSocketSession(
        string sessionId,
        OpenAIResponsesWebSocketSessionEntry entry,
        long generation)
    {
        if (!entry.TryExpireIdle(generation))
        {
            return;
        }

        _webSocketSessions.TryRemove(new KeyValuePair<string, OpenAIResponsesWebSocketSessionEntry>(sessionId, entry));
    }

    private void ClearLiveContinuation(string sessionId)
        => _liveContinuations.TryRemove(sessionId, out _);

    private CodexTurnState? GetCodexTurnState(LocalAgentTurnRequest request)
    {
        if (provider.CodexSubscription is null)
        {
            return null;
        }

        var currentKey = new OpenAIResponsesTurnStateKey(request.SessionId, request.RunId.Value);
        foreach (var key in _codexTurnStates.Keys)
        {
            if (string.Equals(key.SessionId, request.SessionId, StringComparison.Ordinal) &&
                !string.Equals(key.RunId, request.RunId.Value, StringComparison.Ordinal))
            {
                if (_codexTurnStates.TryRemove(key, out var oldState))
                {
                    oldState.Clear();
                }
            }
        }

        return _codexTurnStates.GetOrAdd(currentKey, static _ => new CodexTurnState());
    }

    private void ClearCodexTurnStates(string sessionId)
    {
        foreach (var key in _codexTurnStates.Keys)
        {
            if (string.Equals(key.SessionId, sessionId, StringComparison.Ordinal))
            {
                if (_codexTurnStates.TryRemove(key, out var oldState))
                {
                    oldState.Clear();
                }
            }
        }
    }

    private CreateResponseOptions CreateWebSocketContinuationRequestOptions(
        LocalAgentTurnRequest request,
        CreateResponseOptions fullOptions)
    {
        if (provider.CodexSubscription is null)
        {
            return fullOptions;
        }

        if (!request.CanUseProviderContinuation)
        {
            ClearLiveContinuation(request.SessionId);
            return fullOptions;
        }

        if (!_liveContinuations.TryGetValue(request.SessionId, out var continuation) ||
            !string.IsNullOrWhiteSpace(fullOptions.PreviousResponseId))
        {
            return fullOptions;
        }

        if (!TryCreateContinuationOptions(fullOptions, continuation, out var continuationOptions))
        {
            ClearLiveContinuation(request.SessionId);
            return fullOptions;
        }

        return continuationOptions;
    }

    private static bool TryCreateContinuationOptions(
        CreateResponseOptions fullOptions,
        OpenAIResponsesLiveContinuation continuation,
        out CreateResponseOptions continuationOptions)
    {
        continuationOptions = null!;

        if (!string.Equals(
                GetRequestWithoutInputJson(fullOptions),
                continuation.RequestWithoutInputJson,
                StringComparison.Ordinal))
        {
            return false;
        }

        var currentInputItemsJson = GetInputItemsJson(fullOptions);
        var baselineCount = continuation.InputItemsJson.Count + continuation.OutputItemsJson.Count;
        if (currentInputItemsJson.Count < baselineCount)
        {
            return false;
        }

        for (var index = 0; index < continuation.InputItemsJson.Count; index++)
        {
            if (!string.Equals(currentInputItemsJson[index], continuation.InputItemsJson[index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        for (var index = 0; index < continuation.OutputItemsJson.Count; index++)
        {
            if (!string.Equals(
                    currentInputItemsJson[continuation.InputItemsJson.Count + index],
                    continuation.OutputItemsJson[index],
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        continuationOptions = CloneResponseOptions(fullOptions);
        continuationOptions.PreviousResponseId = continuation.ResponseId;
        continuationOptions.InputItems.Clear();
        foreach (var inputItem in fullOptions.InputItems.Skip(baselineCount))
        {
            continuationOptions.InputItems.Add(inputItem);
        }

        return true;
    }

    private void UpdateLiveContinuation(
        LocalAgentTurnRequest request,
        CreateResponseOptions fullOptions,
        ResponseResult response,
        LocalAgentConversationMessage assistantMessage)
    {
        if (!request.CanUseProviderContinuation ||
            provider.CodexSubscription is null ||
            string.IsNullOrWhiteSpace(response.Id))
        {
            ClearLiveContinuation(request.SessionId);
            return;
        }

        var replayOutputItems = CreateAssistantItems(assistantMessage.Parts).ToArray();
        if (replayOutputItems.Length != response.OutputItems.Count)
        {
            ClearLiveContinuation(request.SessionId);
            return;
        }

        _liveContinuations[request.SessionId] = new OpenAIResponsesLiveContinuation(
            GetRequestWithoutInputJson(fullOptions),
            GetInputItemsJson(fullOptions),
            replayOutputItems.Select(CreateResponseItemJson).ToArray(),
            response.Id);
    }

    private static CreateResponseOptions CloneResponseOptions(CreateResponseOptions options)
    {
        var clone = new CreateResponseOptions
        {
            BackgroundModeEnabled = options.BackgroundModeEnabled,
            ConversationOptions = options.ConversationOptions,
            EndUserId = options.EndUserId,
            Instructions = options.Instructions,
            MaxOutputTokenCount = options.MaxOutputTokenCount,
            MaxToolCallCount = options.MaxToolCallCount,
            Model = options.Model,
            ParallelToolCallsEnabled = options.ParallelToolCallsEnabled,
            PreviousResponseId = options.PreviousResponseId,
            ReasoningOptions = options.ReasoningOptions,
            SafetyIdentifier = options.SafetyIdentifier,
            ServiceTier = options.ServiceTier,
            StoredOutputEnabled = options.StoredOutputEnabled,
            StreamingEnabled = options.StreamingEnabled,
            Temperature = options.Temperature,
            TextOptions = options.TextOptions,
            ToolChoice = options.ToolChoice,
            TopLogProbabilityCount = options.TopLogProbabilityCount,
            TopP = options.TopP,
            TruncationMode = options.TruncationMode,
        };

        foreach (var includedProperty in options.IncludedProperties)
        {
            clone.IncludedProperties.Add(includedProperty);
        }

        foreach (var inputItem in options.InputItems)
        {
            clone.InputItems.Add(inputItem);
        }

        foreach (var metadata in options.Metadata)
        {
            clone.Metadata[metadata.Key] = metadata.Value;
        }

        foreach (var tool in options.Tools)
        {
            clone.Tools.Add(tool);
        }

        return clone;
    }

    private static string GetRequestWithoutInputJson(CreateResponseOptions options)
    {
        using var document = JsonDocument.Parse(SerializeModel(options));
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!property.NameEquals("input"u8) && !property.NameEquals("previous_response_id"u8))
                {
                    property.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }

        using var normalized = JsonDocument.Parse(stream.ToArray());
        return normalized.RootElement.GetRawText();
    }

    private static IReadOnlyList<string> GetInputItemsJson(CreateResponseOptions options)
        => options.InputItems.Select(CreateResponseItemJson).ToArray();

    private static string CreateResponseItemJson(ResponseItem item)
    {
        using var document = JsonDocument.Parse(SerializeModel(item));
        return document.RootElement.GetRawText();
    }

    private void LogCodexDiagnostic(
        string eventName,
        LocalAgentTurnRequest request,
        int attempt,
        string? responseId = null,
        System.Net.HttpStatusCode? httpStatus = null,
        string? errorType = null)
    {
        if (provider.CodexSubscription is null ||
            !LogManager.IsInitialized ||
            !Logger.IsEnabled(LogLevel.Debug))
        {
            return;
        }

        Logger.Debug(OpenAICodexSubscriptionDiagnostics.CreateRequestShape(
            provider,
            request,
            Math.Max(attempt - 1, 0),
            eventName,
            responseId,
            httpStatus,
            errorType));
    }

    private static void WriteCodexConsoleDiagnostic(OpenAIProviderOptions provider, string message)
    {
        if (provider.CodexSubscription is null ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CODEALTA_CODEX_SUB_DEBUG")))
        {
            return;
        }

        Console.WriteLine($"[codex-sub-debug] {message}");
    }

    private static string SerializeModel<T>(T model)
        where T : notnull
        => model is IPersistableModel<T> persistable
            ? persistable.Write(new ModelReaderWriterOptions("J")).ToString()
            : model.ToString() ?? string.Empty;

    private static string TruncateForConsole(string text, int maxLength)
        => text.Length <= maxLength ? text : text[..maxLength] + "…";

    private static string FormatCodexConsolePayload(string payload)
        => TruncateForConsole(
            Regex.Replace(
                OpenAICodexSubscriptionSecretRedactor.Redact(payload),
                "\"encrypted_content\"\\s*:\\s*\"[^\"]*\"",
                "\"encrypted_content\":\"(redacted)\"",
                RegexOptions.CultureInvariant),
            2048);

    private static System.Net.HttpStatusCode? GetHttpStatusCode(Exception exception)
    {
        if (exception is HttpRequestException { StatusCode: { } statusCode })
        {
            return statusCode;
        }

        if (exception is ClientResultException { Status: >= 100 and <= 599 } clientResultException)
        {
            return (System.Net.HttpStatusCode)clientResultException.Status;
        }

        return exception.InnerException is null ? null : GetHttpStatusCode(exception.InnerException);
    }

    private ValueTask<IAsyncDisposable?> CreateCodexConcurrencyLeaseAsync(
        LocalAgentTurnRequest request,
        Func<LocalAgentTurnSessionUpdate, CancellationToken, ValueTask> onSessionUpdate,
        CancellationToken cancellationToken)
    {
        if (provider.CodexSubscription is not { } codexOptions)
        {
            return ValueTask.FromResult<IAsyncDisposable?>(null);
        }

        return AcquireCodexConcurrencyLeaseAsync(request, codexOptions, onSessionUpdate, cancellationToken);
    }

    private async ValueTask<IAsyncDisposable?> AcquireCodexConcurrencyLeaseAsync(
        LocalAgentTurnRequest request,
        OpenAICodexSubscriptionOptions codexOptions,
        Func<LocalAgentTurnSessionUpdate, CancellationToken, ValueTask> onSessionUpdate,
        CancellationToken cancellationToken)
        => await _codexSubscriptionConcurrencyLimiter.AcquireAsync(
            provider.ProviderKey,
            request.SessionId,
            codexOptions.AccountId,
            codexOptions.MaxConcurrentRequests,
            async (waitInfo, waitCancellationToken) =>
                await onSessionUpdate(
                        CreateCodexConcurrencyLimitWaitSessionUpdate(provider.ProviderKey, waitInfo.MaxConcurrentRequests),
                        waitCancellationToken)
                    .ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);

    private static ResponseResult? CreateResponseFromTerminalOrStreamedItems(
        LocalAgentTurnRequest request,
        ResponseResult? completedResponse,
        ResponseResult? latestResponse,
        IReadOnlyDictionary<int, ResponseItem> streamedOutputItems)
    {
        if (completedResponse is not null)
        {
            return completedResponse.OutputItems.Count > 0 || streamedOutputItems.Count == 0
                ? completedResponse
                : CreateResponseWithStreamedOutputItems(request, completedResponse, streamedOutputItems);
        }

        return TryCreateResponseWithoutTerminalPayload(request, latestResponse, streamedOutputItems);
    }

    private static ResponseResult? TryCreateResponseWithoutTerminalPayload(
        LocalAgentTurnRequest request,
        ResponseResult? latestResponse,
        IReadOnlyDictionary<int, ResponseItem> streamedOutputItems)
    {
        if (latestResponse?.OutputItems.Count > 0)
        {
            return latestResponse;
        }

        if (streamedOutputItems.Count == 0)
        {
            return null;
        }

        return CreateResponseWithStreamedOutputItems(request, latestResponse, streamedOutputItems);
    }

    private static ResponseResult CreateResponseWithStreamedOutputItems(
        LocalAgentTurnRequest request,
        ResponseResult? sourceResponse,
        IReadOnlyDictionary<int, ResponseItem> streamedOutputItems)
    {
        var response = new ResponseResult
        {
            Id = sourceResponse?.Id,
            Model = sourceResponse?.Model ?? request.ModelId,
            CreatedAt = sourceResponse?.CreatedAt ?? DateTimeOffset.UtcNow,
            Status = sourceResponse?.Status ?? ResponseStatus.Completed,
            Error = sourceResponse?.Error,
            Usage = sourceResponse?.Usage,
            IncompleteStatusDetails = sourceResponse?.IncompleteStatusDetails,
        };

        foreach (var item in streamedOutputItems.OrderBy(static pair => pair.Key).Select(static pair => pair.Value))
        {
            response.OutputItems.Add(item);
        }

        return response;
    }

    private async ValueTask<CreateResponseOptions> CreateRequestPayloadAsync(
        LocalAgentTurnRequest request,
        CancellationToken cancellationToken)
    {
        var toolDefinitions = request.Tools.Select(CreateFunctionTool).Cast<ResponseTool>().ToArray();
        var options = new CreateResponseOptions
        {
            Model = request.ModelId,
            Instructions = ComposeInstructions(request),
            ParallelToolCallsEnabled = toolDefinitions.Length > 0,
            StoredOutputEnabled = request.Provider.Profile?.SupportsStore == false ? null : false,
            PreviousResponseId = null,
            StreamingEnabled = true,
            MaxOutputTokenCount = request.MaxOutputTokens,
        };

        foreach (var inputItem in CreateConversationItems(request.Conversation))
        {
            options.InputItems.Add(inputItem);
        }

        foreach (var tool in toolDefinitions)
        {
            options.Tools.Add(tool);
        }

        if (toolDefinitions.Length > 0)
        {
            options.ToolChoice = ResponseToolChoice.CreateAutoChoice();
        }

        if ((request.Provider.Profile?.SupportsReasoningEffort ?? true) &&
            request.ReasoningEffort is { } reasoningEffort)
        {
            options.ReasoningOptions = new ResponseReasoningOptions
            {
                ReasoningSummaryVerbosity = ResponseReasoningSummaryVerbosity.Detailed,
                ReasoningEffortLevel = reasoningEffort switch
                {
                    AgentReasoningEffort.None => null,
                    AgentReasoningEffort.Minimal => ResponseReasoningEffortLevel.Minimal,
                    AgentReasoningEffort.Low => ResponseReasoningEffortLevel.Low,
                    AgentReasoningEffort.Medium => ResponseReasoningEffortLevel.Medium,
                    AgentReasoningEffort.High => ResponseReasoningEffortLevel.High,
                    AgentReasoningEffort.XHigh => XHighReasoningEffortLevel,
                    _ => null,
                },
            };
        }

        OpenAIExtraBodyPatchHelper.Apply(ref options.Patch, provider.ExtraBody);
        provider.ResponsesRequestCustomizer?.Invoke(new OpenAIResponsesRequestCustomizationContext(request, options));

        if (provider.CodexSubscription is not null)
        {
            await ApplyCodexSubscriptionRequestCustomizationAsync(
                request,
                options,
                provider.CodexSubscription,
                cancellationToken).ConfigureAwait(false);
        }

        return options;
    }

    private async ValueTask ApplyCodexSubscriptionRequestCustomizationAsync(
        LocalAgentTurnRequest request,
        CreateResponseOptions options,
        OpenAICodexSubscriptionOptions codexOptions,
        CancellationToken cancellationToken)
    {
        options.StoredOutputEnabled = false;
        options.StreamingEnabled = true;
        options.PreviousResponseId = null;
        // The Codex subscription responses endpoint currently rejects max_output_tokens.
        options.MaxOutputTokenCount = null;
        options.ParallelToolCallsEnabled = true;
        options.ToolChoice ??= ResponseToolChoice.CreateAutoChoice();

        if (codexOptions.IncludeEncryptedReasoning &&
            !options.IncludedProperties.Contains(IncludedResponseProperty.ReasoningEncryptedContent))
        {
            options.IncludedProperties.Add(IncludedResponseProperty.ReasoningEncryptedContent);
        }

        options.Patch.Set("$.prompt_cache_key"u8, request.SessionId);
        options.Patch.Set("$.text.verbosity"u8, codexOptions.TextVerbosity);

        var stateRootPath = string.IsNullOrWhiteSpace(provider.StateRootPath)
            ? Path.Combine(AppContext.BaseDirectory, ".codealta-state")
            : provider.StateRootPath;
        var installationIdProvider = new CodexSubscriptionInstallationIdProvider(
            stateRootPath,
            CodexAuthFileReader.ResolveCodexHome());
        var installationId = await installationIdProvider.ResolveAsync(
            codexOptions.SendInstallationId,
            codexOptions.InstallationIdSource,
            cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(installationId))
        {
            options.Patch.Set("$.client_metadata.x-codex-installation-id"u8, installationId);
        }
    }

    private static string? ComposeInstructions(LocalAgentTurnRequest request)
    {
        var systemMessage = string.IsNullOrWhiteSpace(request.SystemMessage)
            ? null
            : request.SystemMessage.Trim();
        var developerInstructions = string.IsNullOrWhiteSpace(request.DeveloperInstructions)
            ? null
            : request.DeveloperInstructions.Trim();

        if (string.IsNullOrWhiteSpace(developerInstructions))
        {
            return systemMessage;
        }

        return string.IsNullOrWhiteSpace(systemMessage)
            ? developerInstructions
            : $"""
               {systemMessage}

               <developer_instructions>
               {developerInstructions}
               </developer_instructions>
               """;
    }

    private static IReadOnlyList<ResponseItem> CreateConversationItems(IReadOnlyList<LocalAgentConversationMessage> messages)
    {
        var items = new List<ResponseItem>();
        foreach (var message in messages)
        {
            switch (message.Role)
            {
                case LocalAgentConversationRole.System:
                    if (TryCreateTextualMessage(message.Parts, static parts => ResponseItem.CreateSystemMessageItem(parts), out var systemMessage))
                    {
                        items.Add(systemMessage);
                    }

                    break;
                case LocalAgentConversationRole.User:
                    if (TryCreateTextualMessage(message.Parts, static parts => ResponseItem.CreateUserMessageItem(parts), out var userMessage))
                    {
                        items.Add(userMessage);
                    }

                    break;
                case LocalAgentConversationRole.Assistant:
                    items.AddRange(CreateAssistantItems(message.Parts));
                    break;
                case LocalAgentConversationRole.Tool:
                    items.AddRange(CreateToolResultItems(message.Parts));
                    break;
            }
        }

        return items;
    }

    private static IEnumerable<ResponseItem> CreateAssistantItems(IReadOnlyList<LocalAgentMessagePart> parts)
    {
        var contentParts = new List<ResponseContentPart>();
        var reasoningItems = new List<ResponseItem>();
        var toolCalls = new List<ResponseItem>();

        foreach (var part in parts)
        {
            switch (part)
            {
                case LocalAgentMessagePart.Text text:
                    contentParts.Add(ResponseContentPart.CreateOutputTextPart(text.Value, []));
                    break;
                case LocalAgentMessagePart.Uri uri:
                    contentParts.Add(ResponseContentPart.CreateOutputTextPart(uri.Value, []));
                    break;
                case LocalAgentMessagePart.Data data:
                    contentParts.Add(ResponseContentPart.CreateOutputTextPart(data.Name ?? data.MediaType ?? "attachment", []));
                    break;
                case LocalAgentMessagePart.Reasoning reasoning:
                    var reasoningItem = ResponseItem.CreateReasoningItem(reasoning.Value ?? string.Empty);
                    if (!string.IsNullOrWhiteSpace(reasoning.ProtectedData))
                    {
                        reasoningItem.EncryptedContent = reasoning.ProtectedData;
                    }

                    reasoningItems.Add(reasoningItem);
                    break;
                case LocalAgentMessagePart.ToolCall toolCall:
                    toolCalls.Add(
                        ResponseItem.CreateFunctionCallItem(
                            toolCall.CallId,
                            LocalAgentToolBridge.GetRegisteredToolName(toolCall.Name),
                            BinaryData.FromString(toolCall.Arguments.GetRawText())));
                    break;
            }
        }

        if (contentParts.Count > 0)
        {
            yield return ResponseItem.CreateAssistantMessageItem(contentParts.ToArray());
        }

        foreach (var reasoningItem in reasoningItems)
        {
            yield return reasoningItem;
        }

        foreach (var toolCall in toolCalls)
        {
            yield return toolCall;
        }
    }

    private static IEnumerable<ResponseItem> CreateToolResultItems(IReadOnlyList<LocalAgentMessagePart> parts)
    {
        foreach (var part in parts.OfType<LocalAgentMessagePart.ToolResult>())
        {
            yield return ResponseItem.CreateFunctionCallOutputItem(
                part.CallId,
                RenderToolResult(part.Result));
        }
    }

    private static bool TryCreateTextualMessage(
        IReadOnlyList<LocalAgentMessagePart> parts,
        Func<ResponseContentPart[], MessageResponseItem> factory,
        out MessageResponseItem message)
    {
        var contentParts = new List<ResponseContentPart>(parts.Count);
        foreach (var part in parts)
        {
            switch (part)
            {
                case LocalAgentMessagePart.Text text:
                    contentParts.Add(ResponseContentPart.CreateInputTextPart(text.Value));
                    break;
                case LocalAgentMessagePart.Uri uri when TryCreateInputContentPart(uri, out var uriPart):
                    contentParts.Add(uriPart);
                    break;
                case LocalAgentMessagePart.Data data when TryCreateInputContentPart(data, out var dataPart):
                    contentParts.Add(dataPart);
                    break;
            }
        }

        if (contentParts.Count == 0)
        {
            message = null!;
            return false;
        }

        message = factory(contentParts.ToArray());
        return true;
    }

    private static bool TryCreateInputContentPart(LocalAgentMessagePart.Uri part, out ResponseContentPart contentPart)
    {
        if (Uri.TryCreate(part.Value, UriKind.Absolute, out var uri))
        {
            contentPart = IsImageMediaType(part.MediaType)
                ? ResponseContentPart.CreateInputImagePart(uri)
                : ResponseContentPart.CreateInputFilePart(uri.AbsoluteUri);
            return true;
        }

        contentPart = null!;
        return false;
    }

    private static bool TryCreateInputContentPart(LocalAgentMessagePart.Data part, out ResponseContentPart contentPart)
    {
        var mediaType = string.IsNullOrWhiteSpace(part.MediaType)
            ? "application/octet-stream"
            : part.MediaType;
        var data = BinaryData.FromBytes(Convert.FromBase64String(part.Base64Data), mediaType);
        contentPart = IsImageMediaType(mediaType)
            ? ResponseContentPart.CreateInputImagePart(data)
            : ResponseContentPart.CreateInputFilePart(data, mediaType, part.Name ?? "attachment");
        return true;
    }

    private static FunctionTool CreateFunctionTool(AgentToolDefinition tool)
        => ResponseTool.CreateFunctionTool(
            LocalAgentToolBridge.GetRegisteredToolName(tool.Spec.Name),
            BinaryData.FromString(LocalAgentToolBridge.CreateOpenAIStrictInputSchema(tool.Spec.InputSchema).GetRawText()),
            strictModeEnabled: true,
            functionDescription: tool.Spec.Description);

    private static (LocalAgentConversationMessage Message, IReadOnlyList<string?> PartContentIds) MapAssistantMessage(ResponseResult response)
    {
        var parts = new List<LocalAgentMessagePart>();
        var partContentIds = new List<string?>();
        foreach (var item in response.OutputItems)
        {
            switch (item)
            {
                case MessageResponseItem message when message.Role == MessageRole.Assistant:
                    var textBuilder = new System.Text.StringBuilder();
                    foreach (var content in message.Content)
                    {
                        switch (content.Kind)
                        {
                            case ResponseContentPartKind.OutputText when !string.IsNullOrWhiteSpace(content.Text):
                                textBuilder.Append(content.Text);
                                break;
                            case ResponseContentPartKind.Refusal when !string.IsNullOrWhiteSpace(content.Refusal):
                                textBuilder.Append(content.Refusal);
                                break;
                        }
                    }

                    if (textBuilder.Length > 0)
                    {
                        parts.Add(new LocalAgentMessagePart.Text(textBuilder.ToString()));
                        partContentIds.Add(string.IsNullOrWhiteSpace(message.Id) ? response.Id : message.Id);
                    }

                    break;
                case ReasoningResponseItem reasoning:
                    if (!string.IsNullOrWhiteSpace(reasoning.GetSummaryText()) || !string.IsNullOrWhiteSpace(reasoning.EncryptedContent))
                    {
                        parts.Add(new LocalAgentMessagePart.Reasoning(
                            reasoning.GetSummaryText() ?? string.Empty,
                            string.IsNullOrWhiteSpace(reasoning.EncryptedContent) ? null : reasoning.EncryptedContent));
                        partContentIds.Add(string.IsNullOrWhiteSpace(reasoning.Id) ? response.Id : reasoning.Id);
                    }

                    break;
                case FunctionCallResponseItem toolCall:
                    parts.Add(new LocalAgentMessagePart.ToolCall(
                        toolCall.CallId,
                        toolCall.FunctionName,
                        DeserializeJson(toolCall.FunctionArguments)));
                    partContentIds.Add(null);
                    break;
            }
        }

        return (new LocalAgentConversationMessage(LocalAgentConversationRole.Assistant, parts), partContentIds);
    }

    private static AgentSessionUsage? CreateUsage(LocalAgentTurnRequest request, ResponseResult response)
    {
        if (response.Usage is null)
        {
            return null;
        }

        return LocalAgentUsageFactory.CreateOperationUsage(
            modelId: response.Model,
            modelInfo: request.ModelInfo,
            inputTokens: response.Usage.InputTokenCount,
            outputTokens: response.Usage.OutputTokenCount,
            totalTokens: response.Usage.TotalTokenCount,
            cachedInputTokens: response.Usage.InputTokenDetails?.CachedTokenCount,
            reasoningTokens: response.Usage.OutputTokenDetails?.ReasoningTokenCount,
            updatedAt: response.CreatedAt == default ? DateTimeOffset.UtcNow : response.CreatedAt);
    }

    private static JsonElement? CreateProviderState(
        ResponseResult response,
        IReadOnlyList<OpenAIResponsesWebSocketSideChannelEvent> sideChannelEvents)
    {
        if (string.IsNullOrWhiteSpace(response.Id) && sideChannelEvents.Count == 0)
        {
            return null;
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            if (!string.IsNullOrWhiteSpace(response.Id))
            {
                writer.WriteString("responseId", response.Id);
            }

            if (sideChannelEvents.Count > 0)
            {
                writer.WritePropertyName("codexWebSocketSideChannels"u8);
                writer.WriteStartArray();
                foreach (var sideChannelEvent in sideChannelEvents.TakeLast(8))
                {
                    WriteSideChannelSummary(writer, sideChannelEvent);
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    private static void WriteSideChannelSummary(
        Utf8JsonWriter writer,
        OpenAIResponsesWebSocketSideChannelEvent sideChannelEvent)
    {
        writer.WriteStartObject();
        writer.WriteString("type"u8, TruncateForConsole(
            OpenAICodexSubscriptionSecretRedactor.Redact(sideChannelEvent.Type),
            128));

        try
        {
            using var document = JsonDocument.Parse(sideChannelEvent.Payload);
            var root = document.RootElement;
            if (string.Equals(sideChannelEvent.Type, "codex.rate_limits", StringComparison.Ordinal))
            {
                WriteRateLimitSummary(writer, root);
            }
            else
            {
                var metadata = SelectSideChannelMetadata(root);
                if (!TryWriteOptionalStringProperty(writer, "model", metadata, "model", "server_model", "serverModel") &&
                    !TryWriteOptionalStringProperty(writer, "model", root, "model", "server_model", "serverModel"))
                {
                    TryWriteHeaderStringProperty(writer, "model", root, "OpenAI-Model", "openai-model", "x-openai-model");
                }

                WriteOptionalStringProperty(writer, "status", metadata, "status");
                if (!TryWriteOptionalStringProperty(writer, "modelsEtag", metadata, "models_etag", "modelsEtag", "etag") &&
                    !TryWriteOptionalStringProperty(writer, "modelsEtag", root, "models_etag", "modelsEtag", "etag"))
                {
                    TryWriteHeaderStringProperty(writer, "modelsEtag", root, "x-models-etag", "OpenAI-Models-Etag");
                }

                if (!TryWriteOptionalStringProperty(
                        writer,
                        "serverReasoning",
                        metadata,
                        "server_reasoning",
                        "serverReasoning",
                        "reasoning_included",
                        "reasoningIncluded"))
                {
                    TryWriteHeaderStringProperty(writer, "serverReasoning", root, "x-reasoning-included", "X-Reasoning-Included");
                }
                if (!TryWriteOptionalStringProperty(writer, "message", metadata, "message", "detail"))
                {
                    WriteOptionalStringProperty(writer, "message", root, "message", "detail");
                }

                WriteStringArrayProperty(writer, "verifications", metadata, "verifications", "openai_verification_recommendation");
            }
        }
        catch (JsonException)
        {
            // The event type is still useful if a future side-channel frame changes shape.
        }

        writer.WriteEndObject();
    }

    private static void WriteRateLimitSummary(Utf8JsonWriter writer, JsonElement root)
    {
        var rateLimitDetails = TryGetProperty(root, out var nested, "rate_limits", "rateLimits") ? nested : root;
        if (!TryWriteOptionalStringProperty(
                writer,
                "limitId",
                root,
                "metered_limit_name",
                "limit_id",
                "limitId",
                "limit_name"))
        {
            WriteOptionalStringProperty(
                writer,
                "limitId",
                rateLimitDetails,
                "metered_limit_name",
                "limit_id",
                "limitId",
                "limit_name");
        }

        if (!TryWriteOptionalStringProperty(writer, "limitName", root, "limit_name", "limitName"))
        {
            WriteOptionalStringProperty(writer, "limitName", rateLimitDetails, "limit_name", "limitName");
        }

        if (!TryWriteOptionalStringProperty(writer, "planType", root, "plan_type", "planType"))
        {
            WriteOptionalStringProperty(writer, "planType", rateLimitDetails, "plan_type", "planType");
        }

        if (!TryWriteOptionalStringProperty(
                writer,
                "rateLimitReachedType",
                root,
                "rate_limit_reached_type",
                "rateLimitReachedType"))
        {
            WriteOptionalStringProperty(
                writer,
                "rateLimitReachedType",
                rateLimitDetails,
                "rate_limit_reached_type",
                "rateLimitReachedType");
        }

        WriteRateLimitWindowSummary(writer, "primary", rateLimitDetails, "primary");
        WriteRateLimitWindowSummary(writer, "secondary", rateLimitDetails, "secondary");
        WriteCreditsSummary(writer, root);
        if (!TryGetProperty(root, out _, "credits"))
        {
            WriteCreditsSummary(writer, rateLimitDetails);
        }
    }

    private static JsonElement SelectSideChannelMetadata(JsonElement root)
    {
        if (TryGetProperty(root, out var parameters, "params") && parameters.ValueKind == JsonValueKind.Object)
        {
            return parameters;
        }

        if (TryGetProperty(root, out var response, "response") && response.ValueKind == JsonValueKind.Object)
        {
            if (TryGetProperty(response, out var responseMetadata, "metadata") &&
                responseMetadata.ValueKind == JsonValueKind.Object)
            {
                return responseMetadata;
            }

            return response;
        }

        if (TryGetProperty(root, out var metadata, "metadata") && metadata.ValueKind == JsonValueKind.Object)
        {
            return metadata;
        }

        return root;
    }

    private static void WriteRateLimitWindowSummary(
        Utf8JsonWriter writer,
        string outputName,
        JsonElement element,
        string propertyName)
    {
        if (!TryGetProperty(element, out var window, propertyName) ||
            window.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        writer.WritePropertyName(outputName);
        writer.WriteStartObject();
        WriteOptionalNumberProperty(writer, "usedPercent", window, "used_percent", "usedPercent");
        WriteOptionalNumberProperty(writer, "resetsAt", window, "reset_at", "resets_at", "resetsAt");
        WriteOptionalNumberProperty(
            writer,
            "windowDurationMins",
            window,
            "window_minutes",
            "window_duration_mins",
            "windowDurationMins",
            "windowMinutes");
        writer.WriteEndObject();
    }

    private static void WriteCreditsSummary(Utf8JsonWriter writer, JsonElement rateLimits)
    {
        if (!TryGetProperty(rateLimits, out var credits, "credits") || credits.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        writer.WritePropertyName("credits"u8);
        writer.WriteStartObject();
        WriteOptionalBooleanProperty(writer, "hasCredits", credits, "has_credits", "hasCredits");
        WriteOptionalBooleanProperty(writer, "unlimited", credits, "unlimited");
        WriteOptionalStringProperty(
            writer,
            "balance",
            credits,
            "balance",
            "remaining",
            "remaining_credits",
            "remainingCredits");
        writer.WriteEndObject();
    }

    private static void WriteOptionalStringProperty(
        Utf8JsonWriter writer,
        string outputName,
        JsonElement element,
        params string[] propertyNames)
        => TryWriteOptionalStringProperty(writer, outputName, element, propertyNames);

    private static bool TryWriteOptionalStringProperty(
        Utf8JsonWriter writer,
        string outputName,
        JsonElement element,
        params string[] propertyNames)
    {
        if (!TryGetProperty(element, out var property, propertyNames) ||
            !TryGetStringValue(property, out var value))
        {
            return false;
        }

        writer.WriteString(outputName, TruncateForConsole(
            OpenAICodexSubscriptionSecretRedactor.Redact(value),
            512));
        return true;
    }

    private static bool TryWriteHeaderStringProperty(
        Utf8JsonWriter writer,
        string outputName,
        JsonElement root,
        params string[] headerNames)
    {
        if (!TryGetHeadersElement(root, out var headers))
        {
            return false;
        }

        return TryWriteOptionalStringProperty(writer, outputName, headers, headerNames);
    }

    private static void WriteStringArrayProperty(
        Utf8JsonWriter writer,
        string outputName,
        JsonElement element,
        params string[] propertyNames)
    {
        if (!TryGetProperty(element, out var array, propertyNames) ||
            array.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        writer.WritePropertyName(outputName);
        writer.WriteStartArray();
        foreach (var item in array.EnumerateArray().Take(16))
        {
            if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
            {
                writer.WriteStringValue(TruncateForConsole(
                    OpenAICodexSubscriptionSecretRedactor.Redact(item.GetString()),
                    128));
            }
        }

        writer.WriteEndArray();
    }

    private static void WriteOptionalBooleanProperty(
        Utf8JsonWriter writer,
        string outputName,
        JsonElement element,
        params string[] propertyNames)
    {
        if (!TryGetProperty(element, out var property, propertyNames) ||
            property.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            return;
        }

        writer.WriteBoolean(outputName, property.GetBoolean());
    }

    private static void WriteOptionalNumberProperty(
        Utf8JsonWriter writer,
        string outputName,
        JsonElement element,
        params string[] propertyNames)
    {
        if (!TryGetProperty(element, out var property, propertyNames) ||
            property.ValueKind != JsonValueKind.Number)
        {
            return;
        }

        if (property.TryGetInt64(out var longValue))
        {
            writer.WriteNumber(outputName, longValue);
            return;
        }

        if (property.TryGetDouble(out var doubleValue))
        {
            writer.WriteNumber(outputName, doubleValue);
        }
    }

    private static bool TryGetProperty(
        JsonElement element,
        out JsonElement property,
        params string[] propertyNames)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var propertyName in propertyNames)
            {
                if (element.TryGetProperty(propertyName, out property))
                {
                    return true;
                }
            }

            foreach (var candidate in element.EnumerateObject())
            {
                foreach (var propertyName in propertyNames)
                {
                    if (string.Equals(candidate.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                    {
                        property = candidate.Value;
                        return true;
                    }
                }
            }
        }

        property = default;
        return false;
    }

    private static bool TryGetHeadersElement(JsonElement root, out JsonElement headers)
    {
        if (TryGetProperty(root, out headers, "headers") && headers.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        if (TryGetProperty(root, out var response, "response") &&
            response.ValueKind == JsonValueKind.Object &&
            TryGetProperty(response, out headers, "headers") &&
            headers.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        headers = default;
        return false;
    }

    private static bool TryGetStringValue(JsonElement element, out string value)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                value = element.GetString() ?? string.Empty;
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (TryGetStringValue(item, out value))
                    {
                        return true;
                    }
                }

                value = string.Empty;
                return false;
            default:
                value = string.Empty;
                return false;
        }

        return !string.IsNullOrWhiteSpace(value);
    }

    private static JsonElement DeserializeJson(BinaryData data)
    {
        using var document = JsonDocument.Parse(data);
        return document.RootElement.Clone();
    }

    private static string? ExtractSummary(LocalAgentConversationMessage message)
        => message.Parts
            .OfType<LocalAgentMessagePart.Text>()
            .Select(static part => part.Value)
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));

    private static bool IsImageMediaType(string? mediaType)
        => mediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true;

    private static string RenderToolResult(AgentToolResult result)
    {
        if (result.Items.Count == 0)
        {
            return result.Error ?? string.Empty;
        }

        return string.Join(
            Environment.NewLine,
            result.Items.Select(static item => item switch
            {
                AgentToolResultItem.Text text => text.Value,
                AgentToolResultItem.ImageUrl imageUrl => imageUrl.Url,
                _ => string.Empty,
            }).Where(static value => !string.IsNullOrWhiteSpace(value)));
    }

    private static Exception CreateResponseFailureException(ResponseResult response, string fallbackStatus)
    {
        ArgumentNullException.ThrowIfNull(response);

        var message = response.Error?.Message;
        if (string.IsNullOrWhiteSpace(message) &&
            response.IncompleteStatusDetails?.Reason is { } incompleteReason)
        {
            message = $"The OpenAI Responses request ended {fallbackStatus} ({incompleteReason}).";
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            message = $"The OpenAI Responses request ended {fallbackStatus}.";
        }

        if (response.Error is { Code: { } errorCode } &&
            !string.IsNullOrWhiteSpace(errorCode.ToString()) &&
            !message.Contains(errorCode.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            message = $"{errorCode}: {message}";
        }

        return new InvalidOperationException(message);
    }

    private static Exception CreateStreamErrorException(StreamingResponseErrorUpdate error)
    {
        ArgumentNullException.ThrowIfNull(error);

        var message = string.IsNullOrWhiteSpace(error.Message)
            ? "The OpenAI Responses stream reported an unknown error."
            : error.Message;
        if (!string.IsNullOrWhiteSpace(error.Code) &&
            !message.Contains(error.Code, StringComparison.OrdinalIgnoreCase))
        {
            message = $"{error.Code}: {message}";
        }

        if (!string.IsNullOrWhiteSpace(error.Param))
        {
            message = $"{message} (param: {error.Param})";
        }

        return new InvalidOperationException(message);
    }

    private static LocalAgentTurnExecutionException CreateTurnExecutionException(Exception ex)
        => new(
            new LocalAgentTurnFailure(
                ex.Message,
                IsContextOverflowMessage(ex.Message)),
            ex);

    private static Exception TranslateCodexSubscriptionException(OpenAIProviderOptions provider, Exception exception)
    {
        if (provider.CodexSubscription is null)
        {
            return exception;
        }

        if (GetHttpStatusCode(exception) is { } statusCode)
        {
            var message = statusCode switch
            {
                System.Net.HttpStatusCode.Unauthorized => "ChatGPT/Codex authentication failed; re-authentication is required.",
                System.Net.HttpStatusCode.Forbidden => "ChatGPT/Codex account, workspace, plan, or policy does not allow this request.",
                System.Net.HttpStatusCode.TooManyRequests => "ChatGPT/Codex rate limit or quota was reached. Retry later or after the service-provided Retry-After time.",
                >= System.Net.HttpStatusCode.InternalServerError => "ChatGPT/Codex backend is temporarily unavailable.",
                System.Net.HttpStatusCode.BadRequest => "ChatGPT/Codex rejected the request shape.",
                _ => $"ChatGPT/Codex request failed with HTTP {(int)statusCode}.",
            };
            var detail = GetCodexSubscriptionErrorDetail(exception);
            if (!string.IsNullOrWhiteSpace(detail) &&
                !detail.StartsWith("Response status code", StringComparison.OrdinalIgnoreCase) &&
                !detail.StartsWith("Service request failed", StringComparison.OrdinalIgnoreCase))
            {
                message = $"{message} {detail}";
            }

            return new InvalidOperationException(message, exception);
        }

        if (IsPrematureResponseEnded(exception))
        {
            return new InvalidOperationException(
                "ChatGPT/Codex response stream ended prematurely before a terminal response was received. This is usually a transient network or service hiccup; retry the prompt if no answer was recorded.",
                exception);
        }

        if (exception is JsonException ||
            exception is InvalidOperationException invalidOperationException &&
            invalidOperationException.Message.Contains(
                "stream completed without a terminal response payload",
                StringComparison.OrdinalIgnoreCase))
        {
            return new InvalidOperationException(
                "ChatGPT/Codex response stream did not match the expected protocol; no terminal response payload was received.",
                exception);
        }

        return exception;
    }

    private static bool ShouldRefreshCodexSubscriptionCredential(
        OpenAIProviderOptions provider,
        Exception exception,
        bool emittedUpdate,
        bool alreadyRefreshed)
        => provider.CodexSubscription is not null &&
           !emittedUpdate &&
           !alreadyRefreshed &&
           GetHttpStatusCode(exception) == System.Net.HttpStatusCode.Unauthorized;

    private static bool ShouldRetryCodexSubscriptionRequest(
        OpenAIProviderOptions provider,
        Exception exception,
        bool emittedUpdate,
        int attempt,
        int retryBudget,
        out TimeSpan delay)
    {
        delay = TimeSpan.Zero;
        if (provider.CodexSubscription is null ||
            emittedUpdate ||
            attempt >= retryBudget ||
            exception is LocalAgentTurnExecutionException ||
            exception is OperationCanceledException && !IsTransportAbortedOperationCanceled(exception))
        {
            return false;
        }

        if (IsRetryableCodexSubscriptionException(exception))
        {
            delay = GetRetryAfterDelay(exception) ?? GetExponentialBackoffDelay(attempt);
            return true;
        }

        return false;
    }

    private static LocalAgentTurnSessionUpdate CreateCodexReconnectSessionUpdate(int retryAttempt, int retryBudget)
    {
        var maxRetries = Math.Max(retryBudget - 1, 1);
        var retryText = retryAttempt.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var maxRetriesText = maxRetries.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new LocalAgentTurnSessionUpdate
        {
            Kind = AgentSessionUpdateKind.Reconnecting,
            Message = $"Reconnecting to ChatGPT/Codex... {retryText}/{maxRetriesText}",
        };
    }

    private static LocalAgentTurnSessionUpdate CreateCodexConcurrencyLimitWaitSessionUpdate(
        string providerKey,
        int maxConcurrentRequests)
    {
        var maxConcurrentRequestsText = maxConcurrentRequests.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new LocalAgentTurnSessionUpdate
        {
            Kind = AgentSessionUpdateKind.Warning,
            Message = $"Waiting for CodeAlta's local ChatGPT/Codex concurrency guard: {maxConcurrentRequestsText} active request(s) for this ChatGPT account are already running. Codex CLI and pi-mono do not impose an equivalent account-wide limiter. To allow more parallel sessions, set max_concurrent_requests to a higher value under [providers.{providerKey}] in config.toml.",
        };
    }

    private static bool IsPrematureResponseEnded(Exception exception)
    {
        if (IsTransportAbortedOperationCanceled(exception))
        {
            return true;
        }

        if (exception is HttpIOException { HttpRequestError: HttpRequestError.ResponseEnded })
        {
            return true;
        }

        if (exception is WebSocketException &&
            (exception.Message.Contains("close handshake", StringComparison.OrdinalIgnoreCase) ||
             exception.Message.Contains("closed", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (exception.Message.Contains("response ended prematurely", StringComparison.OrdinalIgnoreCase) &&
            exception.Message.Contains("ResponseEnded", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (exception.Message.Contains("stream closed before a terminal response event", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return exception.InnerException is not null && IsPrematureResponseEnded(exception.InnerException);
    }

    private static bool IsTransportAbortedOperationCanceled(Exception exception)
    {
        if (exception is not OperationCanceledException)
        {
            return false;
        }

        return ContainsAbortedTransportSignal(exception.InnerException);
    }

    private static bool ContainsAbortedTransportSignal(Exception? exception)
    {
        while (exception is not null)
        {
            if (exception is System.Net.Sockets.SocketException socketException &&
                socketException.SocketErrorCode == System.Net.Sockets.SocketError.OperationAborted)
            {
                return true;
            }

            if (exception is IOException &&
                exception.Message.Contains("operation has been aborted", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            exception = exception.InnerException;
        }

        return false;
    }

    private static bool IsRetryableCodexSubscriptionException(Exception exception)
    {
        if (IsWebSocketConnectionLimitReached(exception))
        {
            return true;
        }

        if (IsPrematureResponseEnded(exception))
        {
            return true;
        }

        if (IsWebSocketReceiveIdleTimeout(exception))
        {
            return true;
        }

        if (IsNonRetryableCodexSubscriptionLimit(exception))
        {
            return false;
        }

        var statusCode = GetHttpStatusCode(exception);
        var retryableResponseFailure = ContainsRetryableCodexSubscriptionFailureSignal(
            GetCodexSubscriptionErrorDetail(exception));

        return retryableResponseFailure ||
            statusCode is null && exception is HttpRequestException ||
            statusCode is System.Net.HttpStatusCode.TooManyRequests ||
            statusCode >= System.Net.HttpStatusCode.InternalServerError;
    }

    private static bool IsWebSocketReceiveIdleTimeout(Exception exception)
    {
        if (exception is TimeoutException &&
            exception.Message.Contains("Codex subscription WebSocket did not receive", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return exception.InnerException is not null && IsWebSocketReceiveIdleTimeout(exception.InnerException);
    }

    private static bool IsNonRetryableCodexSubscriptionLimit(Exception exception)
    {
        if (IsWebSocketConnectionLimitReached(exception))
        {
            return false;
        }

        var detail = GetCodexSubscriptionErrorDetail(exception);
        return ContainsNonRetryableCodexSubscriptionLimitSignal(detail);
    }

    private static bool ContainsNonRetryableCodexSubscriptionLimitSignal(string detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return false;
        }

        var normalized = detail.ToLowerInvariant();
        return normalized.Contains("usage_limit_reached", StringComparison.Ordinal) ||
               normalized.Contains("usage_not_included", StringComparison.Ordinal) ||
               normalized.Contains("insufficient_quota", StringComparison.Ordinal) ||
               normalized.Contains("quota", StringComparison.Ordinal) ||
               normalized.Contains("usage limit", StringComparison.Ordinal) ||
               normalized.Contains("usage has been reached", StringComparison.Ordinal) ||
               normalized.Contains("not included in your plan", StringComparison.Ordinal) ||
               normalized.Contains("upgrade your plan", StringComparison.Ordinal) ||
               normalized.Contains("plan limit", StringComparison.Ordinal) ||
               normalized.Contains("retry limit", StringComparison.Ordinal) ||
               normalized.Contains("billing limit", StringComparison.Ordinal) ||
               (normalized.Contains("billing", StringComparison.Ordinal) && normalized.Contains("limit", StringComparison.Ordinal)) ||
               (normalized.Contains("plan", StringComparison.Ordinal) && normalized.Contains("limit", StringComparison.Ordinal));
    }

    private static bool ContainsRetryableCodexSubscriptionFailureSignal(string detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return false;
        }

        var normalized = detail.ToLowerInvariant();
        return normalized.Contains("server_overloaded", StringComparison.Ordinal) ||
               normalized.Contains("server overloaded", StringComparison.Ordinal) ||
               normalized.Contains("overloaded", StringComparison.Ordinal) ||
               normalized.Contains("service unavailable", StringComparison.Ordinal) ||
               normalized.Contains("temporarily unavailable", StringComparison.Ordinal) ||
               normalized.Contains("upstream", StringComparison.Ordinal);
    }

    private static string GetCodexSubscriptionErrorDetail(Exception exception)
    {
        var details = new List<string>();
        AppendCodexSubscriptionErrorDetails(exception, details);
        if (details.Count == 0)
        {
            return string.Empty;
        }

        var combined = string.Join(
            ' ',
            details
                .Where(static detail => !string.IsNullOrWhiteSpace(detail))
                .Select(static detail => detail.Trim())
                .Distinct(StringComparer.Ordinal));
        return TruncateForConsole(OpenAICodexSubscriptionSecretRedactor.Redact(combined), 2048);
    }

    private static void AppendCodexSubscriptionErrorDetails(Exception exception, List<string> details)
    {
        var clientResultException = exception as ClientResultException;
        if (clientResultException is not null)
        {
            AppendClientResultErrorDetails(clientResultException, details);
        }

        if (!string.IsNullOrWhiteSpace(exception.Message) &&
            (clientResultException is null || !IsGenericClientResultExceptionMessage(exception.Message)))
        {
            details.Add(exception.Message);
        }

        foreach (var key in exception.Data.Keys)
        {
            if (key is null || exception.Data[key] is not { } value)
            {
                continue;
            }

            var valueText = value switch
            {
                string text => text,
                TimeSpan timeSpan => timeSpan.ToString(),
                DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                IFormattable formattable => formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
                _ => value.ToString(),
            };
            if (string.IsNullOrWhiteSpace(valueText))
            {
                continue;
            }

            details.Add($"{key}: {valueText}");
            AppendJsonErrorDetails(valueText, details);
        }

        if (exception.InnerException is not null)
        {
            AppendCodexSubscriptionErrorDetails(exception.InnerException, details);
        }
    }

    private static bool IsGenericClientResultExceptionMessage(string message)
        => message.StartsWith("Service request failed", StringComparison.OrdinalIgnoreCase) ||
           message.StartsWith("Response status code", StringComparison.OrdinalIgnoreCase);

    private static void AppendClientResultErrorDetails(ClientResultException exception, List<string> details)
    {
        var response = exception.GetRawResponse();
        if (response is null)
        {
            return;
        }

        foreach (var header in response.Headers)
        {
            if (!string.IsNullOrWhiteSpace(header.Value))
            {
                details.Add($"{header.Key}: {header.Value}");
            }
        }

        string? content;
        try
        {
            content = response.Content.ToString();
        }
        catch
        {
            content = null;
        }

        if (!string.IsNullOrWhiteSpace(content))
        {
            AppendJsonErrorDetails(content, details);
            details.Add(content);
        }
    }

    private static void AppendJsonErrorDetails(string? content, List<string> details)
    {
        if (string.IsNullOrWhiteSpace(content) ||
            !content.TrimStart().StartsWith("{", StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            AppendJsonErrorDetails(document.RootElement, details);
        }
        catch (JsonException)
        {
        }
    }

    private static void AppendJsonErrorDetails(JsonElement element, List<string> details)
    {
        AppendJsonStringProperty(element, "code", details);
        AppendJsonStringProperty(element, "type", details);
        AppendJsonStringProperty(element, "message", details);
        AppendJsonStringProperty(element, "detail", details);

        if (element.TryGetProperty("error"u8, out var errorElement) &&
            errorElement.ValueKind == JsonValueKind.Object)
        {
            AppendJsonErrorDetails(errorElement, details);
        }
    }

    private static void AppendJsonStringProperty(JsonElement element, string propertyName, List<string> details)
    {
        if (element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(property.GetString()))
        {
            details.Add(property.GetString()!);
        }
    }

    private static bool IsWebSocketConnectionLimitReached(Exception exception)
    {
        if (exception.Data.Contains(WebSocketErrorCodeDataKey) &&
            exception.Data[WebSocketErrorCodeDataKey] is string code &&
            string.Equals(code, WebSocketConnectionLimitReachedCode, StringComparison.Ordinal))
        {
            return true;
        }

        return exception.InnerException is not null && IsWebSocketConnectionLimitReached(exception.InnerException);
    }

    private static bool IsWrappedWebSocketError(Exception exception)
    {
        if (exception.Data.Contains(WebSocketWrappedErrorDataKey) &&
            exception.Data[WebSocketWrappedErrorDataKey] is bool wrappedError && wrappedError)
        {
            return true;
        }

        return exception.InnerException is not null && IsWrappedWebSocketError(exception.InnerException);
    }

    private static TimeSpan? GetRetryAfterDelay(Exception exception)
    {
        foreach (var key in new[] { "Retry-After", "retry-after", "RetryAfter" })
        {
            if (!exception.Data.Contains(key))
            {
                continue;
            }

            var value = exception.Data[key];
            switch (value)
            {
                case TimeSpan timeSpan when timeSpan >= TimeSpan.Zero:
                    return timeSpan;
                case int seconds when seconds >= 0:
                    return TimeSpan.FromSeconds(seconds);
                case long seconds when seconds >= 0:
                    return TimeSpan.FromSeconds(seconds);
                case DateTimeOffset retryAt:
                    return retryAt <= DateTimeOffset.UtcNow
                        ? TimeSpan.Zero
                        : retryAt - DateTimeOffset.UtcNow;
                case string text when TryParseRetryAfter(text, out var delay):
                    return delay;
            }
        }

        if (exception is ClientResultException clientResultException &&
            clientResultException.GetRawResponse() is { } response)
        {
            foreach (var key in new[] { "Retry-After", "retry-after", "RetryAfter" })
            {
                if (response.Headers.TryGetValue(key, out var retryAfter) &&
                    retryAfter is not null &&
                    TryParseRetryAfter(retryAfter, out var delay))
                {
                    return delay;
                }
            }
        }

        if (exception.InnerException is not null)
        {
            return GetRetryAfterDelay(exception.InnerException);
        }

        return null;
    }

    private static bool TryParseRetryAfter(string text, out TimeSpan delay)
    {
        if (int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var seconds) &&
            seconds >= 0)
        {
            delay = TimeSpan.FromSeconds(seconds);
            return true;
        }

        if (DateTimeOffset.TryParse(
                text,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal,
                out var retryAt))
        {
            delay = retryAt <= DateTimeOffset.UtcNow
                ? TimeSpan.Zero
                : retryAt - DateTimeOffset.UtcNow;
            return true;
        }

        delay = TimeSpan.Zero;
        return false;
    }

    private static TimeSpan GetExponentialBackoffDelay(int attempt)
    {
        var exponent = Math.Max(attempt - 1, 0);
        var baseDelay = CodexBaseRetryDelay.TotalMilliseconds * Math.Pow(2, exponent);
        var jitter = SharedRandom.Next(0, 125);
        return TimeSpan.FromMilliseconds(Math.Min(baseDelay + jitter, 2_000));
    }

    private static bool IsContextOverflowMessage(string? message)
        => !string.IsNullOrWhiteSpace(message) &&
           (message.Contains("context length", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("maximum context length", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("too many tokens", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("prompt is too long", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("context window", StringComparison.OrdinalIgnoreCase));

    private enum OpenAIResponsesTransport
    {
        Http,
        WebSocket,
    }

    private sealed class OpenAIResponsesWebSocketSessionEntry(IOpenAIResponsesWebSocketSession session) : IDisposable
    {
        private readonly object _gate = new();
        private Timer? _idleTimer;
        private bool _active;
        private bool _disposed;
        private int _sessionDisposed;
        private long _idleGeneration;

        public IOpenAIResponsesWebSocketSession Session { get; } = session;

        public bool TryBeginUse()
        {
            Timer? timer;
            lock (_gate)
            {
                if (_disposed)
                {
                    return false;
                }

                _active = true;
                _idleGeneration++;
                timer = _idleTimer;
                _idleTimer = null;
            }

            timer?.Dispose();
            return true;
        }

        public void EndUse()
        {
            lock (_gate)
            {
                _active = false;
            }
        }

        public void EndUseAndArmIdle(TimeSpan timeout, Action<long> expire)
        {
            ArgumentNullException.ThrowIfNull(expire);

            Timer? previousTimer;
            long generation;
            lock (_gate)
            {
                _active = false;
                if (_disposed || timeout == Timeout.InfiniteTimeSpan)
                {
                    return;
                }

                previousTimer = _idleTimer;
                _idleTimer = null;
                generation = ++_idleGeneration;
            }

            previousTimer?.Dispose();
            if (timeout <= TimeSpan.Zero)
            {
                expire(generation);
                return;
            }

            var timer = new Timer(
                static state =>
                {
                    var callback = (IdleExpirationCallback)state!;
                    callback.Expire(callback.Generation);
                },
                new IdleExpirationCallback(expire, generation),
                timeout,
                Timeout.InfiniteTimeSpan);

            lock (_gate)
            {
                if (_disposed || _active || _idleGeneration != generation)
                {
                    timer.Dispose();
                    return;
                }

                _idleTimer = timer;
            }
        }

        public bool TryExpireIdle(long generation)
        {
            Timer? timer;
            lock (_gate)
            {
                if (_disposed || _active || _idleGeneration != generation)
                {
                    return false;
                }

                _disposed = true;
                timer = _idleTimer;
                _idleTimer = null;
            }

            timer?.Dispose();
            DisposeSession();
            return true;
        }

        public void Dispose()
        {
            Timer? timer;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _active = false;
                timer = _idleTimer;
                _idleTimer = null;
            }

            timer?.Dispose();
            DisposeSession();
        }

        private void DisposeSession()
        {
            if (Interlocked.Exchange(ref _sessionDisposed, 1) == 0)
            {
                Session.Dispose();
            }
        }

        private sealed record IdleExpirationCallback(Action<long> Expire, long Generation);
    }

    private sealed record OpenAIResponsesLiveContinuation(
        string RequestWithoutInputJson,
        IReadOnlyList<string> InputItemsJson,
        IReadOnlyList<string> OutputItemsJson,
        string ResponseId);

    private sealed record OpenAIResponsesTurnStateKey(string SessionId, string RunId);
}
