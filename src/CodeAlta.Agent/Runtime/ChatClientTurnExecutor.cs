using System.Text.Json;
using CodeAlta.Agent.Runtime.Tools;
using Microsoft.Extensions.AI;

namespace CodeAlta.Agent.Runtime;

/// <summary>
/// The last step of a provider on a request of <see cref="ChatClientTurnExecutor"/>: it returns the messages and the
/// options the client gets. The messages belong to the request and may be changed.
/// </summary>
/// <param name="request">The turn request.</param>
/// <param name="messages">The messages of the request.</param>
/// <param name="options">The options of the request.</param>
/// <returns>The messages and the options to send.</returns>
internal delegate (IReadOnlyList<ChatMessage> Messages, ChatOptions Options) ChatClientRequestPreparer(
    AgentTurnRequest request,
    IReadOnlyList<ChatMessage> messages,
    ChatOptions options);

/// <summary>
/// Shared turn executor for provider SDKs that expose <see cref="IChatClient"/>.
/// </summary>
internal sealed class ChatClientTurnExecutor : IModelProviderTurnExecutor, IModelProviderModelCatalog
{
    private readonly Func<ModelProviderRuntimeDescriptor, CancellationToken, ValueTask<IChatClient>> _chatClientFactory;
    private readonly Func<ModelProviderRuntimeDescriptor, CancellationToken, Task<IReadOnlyList<AgentModelInfo>>> _listModelsAsync;
    private readonly Func<AgentTurnRequest, AgentReasoningEffort, bool>? _supportsReasoningEffort;
    private readonly Action<AgentTurnRequest, ChatOptions>? _configureOptions;
    private readonly ChatClientRequestPreparer? _prepareRequest;
    private readonly Func<Exception, AgentFailureInfo?>? _classifyFailure;
    private readonly TimeSpan? _promptCacheLifetime;
    private readonly Func<IReadOnlyList<ChatResponseUpdate>, string?>? _describeContentFilter;
    private readonly Func<List<ChatResponseUpdate>, AgentTurnRequest, string?>? _reviewResponse;
    private readonly Func<Exception, AgentTurnRequest, string?>? _describeFailure;
    private readonly Func<AgentTurnRequest, bool>? _toolResultsCarryImages;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChatClientTurnExecutor"/> class.
    /// </summary>
    /// <param name="chatClientFactory">Factory that creates an <see cref="IChatClient"/> for a provider.</param>
    /// <param name="listModelsAsync">Delegate that lists models for a provider.</param>
    /// <param name="supportsReasoningEffort">
    /// Optional provider-specific fallback for reasoning-effort support when model metadata is unavailable.
    /// </param>
    /// <param name="configureOptions">Optional provider-specific chat options customizer.</param>
    /// <param name="prepareRequest">
    /// Optional provider-specific last step on the messages and the options of a request, before the client gets them.
    /// </param>
    /// <param name="classifyFailure">
    /// Optional provider-specific kind of the failure that ended a turn; the HTTP status of the exception otherwise.
    /// </param>
    /// <param name="promptCacheLifetime">
    /// How long the provider keeps the prompt cache a request read or wrote; null when it is not known, and the usage
    /// then gives no expiry.
    /// </param>
    /// <param name="describeContentFilter">
    /// Optional provider-specific sentence for a response the provider stopped by its content filter (a refusal), read
    /// from the updates of the response; a generic sentence otherwise.
    /// </param>
    /// <param name="reviewResponse">
    /// Optional provider-specific review of the updates of a response before they are read: it may remove what the
    /// provider says is not part of the answer, and returns the model that answered when the provider ran the request
    /// on another model than the one requested; null otherwise.
    /// </param>
    /// <param name="describeFailure">
    /// Optional provider-specific sentence for the failure that ended a turn, when the message of the exception would
    /// not tell the user what to do; null to keep that message.
    /// </param>
    /// <param name="toolResultsCarryImages">
    /// Optional provider-specific answer to whether the tool results of a request carry their images; when it is
    /// absent or false, the images follow the tool results in a user message.
    /// </param>
    public ChatClientTurnExecutor(
        Func<ModelProviderRuntimeDescriptor, CancellationToken, ValueTask<IChatClient>> chatClientFactory,
        Func<ModelProviderRuntimeDescriptor, CancellationToken, Task<IReadOnlyList<AgentModelInfo>>> listModelsAsync,
        Func<AgentTurnRequest, AgentReasoningEffort, bool>? supportsReasoningEffort = null,
        Action<AgentTurnRequest, ChatOptions>? configureOptions = null,
        ChatClientRequestPreparer? prepareRequest = null,
        Func<Exception, AgentFailureInfo?>? classifyFailure = null,
        TimeSpan? promptCacheLifetime = null,
        Func<IReadOnlyList<ChatResponseUpdate>, string?>? describeContentFilter = null,
        Func<List<ChatResponseUpdate>, AgentTurnRequest, string?>? reviewResponse = null,
        Func<Exception, AgentTurnRequest, string?>? describeFailure = null,
        Func<AgentTurnRequest, bool>? toolResultsCarryImages = null)
    {
        ArgumentNullException.ThrowIfNull(chatClientFactory);
        ArgumentNullException.ThrowIfNull(listModelsAsync);

        _chatClientFactory = chatClientFactory;
        _listModelsAsync = listModelsAsync;
        _supportsReasoningEffort = supportsReasoningEffort;
        _configureOptions = configureOptions;
        _prepareRequest = prepareRequest;
        _classifyFailure = classifyFailure;
        _promptCacheLifetime = promptCacheLifetime;
        _describeContentFilter = describeContentFilter;
        _reviewResponse = reviewResponse;
        _describeFailure = describeFailure;
        _toolResultsCarryImages = toolResultsCarryImages;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(
        ModelProviderRuntimeDescriptor provider,
        CancellationToken cancellationToken = default)
        => _listModelsAsync(provider, cancellationToken);

    /// <summary>Gets or sets how long a response that stopped in the middle waits before it is asked again.</summary>
    internal TimeSpan MidStreamRetryDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <inheritdoc />
    public Task<AgentTurnResponse> ExecuteTurnAsync(
        AgentTurnRequest request,
        Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate,
        CancellationToken cancellationToken = default)
        => ExecuteTurnAsync(request, onUpdate, static (_, _) => ValueTask.CompletedTask, cancellationToken);

    /// <inheritdoc />
    public async Task<AgentTurnResponse> ExecuteTurnAsync(
        AgentTurnRequest request,
        Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate,
        Func<AgentTurnSessionUpdate, CancellationToken, ValueTask> onSessionUpdate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(onUpdate);
        ArgumentNullException.ThrowIfNull(onSessionUpdate);

        try
        {
            var chatClient = await _chatClientFactory(request.Provider, cancellationToken).ConfigureAwait(false);
            try
            {
                // A tool result of these clients is text, unless the provider says it carries its images: the images
                // of tool results then follow in a user message.
                var inlineImages = _toolResultsCarryImages?.Invoke(request) == true;
                var conversation = AgentReasoningReplay.SanitizeForRequest(request.Conversation, request);
                IReadOnlyList<ChatMessage> messages = (inlineImages ? conversation : Images.AgentToolResultImages.MoveToUserMessages(conversation))
                    .Select(message => MapMessage(message, inlineImages))
                    .ToArray();
                var options = CreateOptions(request);
                if (_prepareRequest is not null)
                {
                    (messages, options) = _prepareRequest(request, messages, options);
                }

                List<ChatResponseUpdate> updates;
                for (var attempt = 1; ; attempt++)
                {
                    // The text of an attempt is a draft until the response is complete: a retry discards it.
                    var attemptId = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{request.RunId.Value}:{attempt}");
                    updates = [];
                    var emittedDelta = false;
                    try
                    {
                        await foreach (var update in chatClient
                                           .GetStreamingResponseAsync(messages, options, cancellationToken)
                                           .ConfigureAwait(false))
                        {
                            updates.Add(update);
                            foreach (var delta in ExtractStreamingDeltas(update, attemptId))
                            {
                                emittedDelta = true;
                                await onUpdate(delta, cancellationToken).ConfigureAwait(false);
                            }
                        }

                        break;
                    }
                    catch (Exception exception) when (attempt == 1 && ShouldAskAgain(exception, updates, cancellationToken))
                    {
                        // An outage in the middle of the stream, before any tool call: the provider's own retries
                        // only cover a request that has not started, so the request is sent once more.
                        await onSessionUpdate(new AgentTurnSessionUpdate
                        {
                            Kind = AgentSessionUpdateKind.Reconnecting,
                            Message = "The response stopped in the middle: the provider is unavailable. Asking again.",
                            Details = CreateRetryDetails(request, attemptId, emittedDelta, exception),
                        }, cancellationToken).ConfigureAwait(false);
                        await Task.Delay(MidStreamRetryDelay, cancellationToken).ConfigureAwait(false);
                    }
                }

                var reroutedTo = _reviewResponse?.Invoke(updates, request);
                var response = updates.ToChatResponse();
                if (!string.IsNullOrWhiteSpace(reroutedTo))
                {
                    response.ModelId = reroutedTo;
                    await onSessionUpdate(new AgentTurnSessionUpdate
                    {
                        Kind = AgentSessionUpdateKind.ModelChanged,
                        Message = $"The provider answered with {reroutedTo} instead of {request.ModelId}.",
                        Details = CreateRerouteDetails(request.ModelId, reroutedTo),
                    }, cancellationToken).ConfigureAwait(false);
                }

                // A response stopped by the content filter (a refusal of the model) fails the turn: what it wrote
                // before is not an answer, and is not kept.
                if (response.FinishReason == ChatFinishReason.ContentFilter)
                {
                    throw new AgentTurnExecutionException(new AgentTurnFailure(
                        _describeContentFilter?.Invoke(updates) ?? "The provider stopped the response: its content filter blocked it.",
                        IsContextOverflow: false,
                        new AgentFailureInfo(AgentFailureKind.Other)));
                }

                var (assistantMessage, assistantPartContentIds) = MapAssistantMessage(response, request);
                return new AgentTurnResponse
                {
                    AssistantMessage = assistantMessage,
                    AssistantPartContentIds = assistantPartContentIds,
                    UnrunnableToolCalls = FindUnrunnableToolCalls(response),
                    Usage = CreateUsage(request, response),
                    ProviderSessionId = response.ConversationId,
                    ProviderState = CreateProviderState(response),
                    Summary = ExtractSummary(assistantMessage),
                };
            }
            finally
            {
                chatClient.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AgentTurnExecutionException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw CreateTurnExecutionException(ex, _describeFailure?.Invoke(ex, request));
        }
    }

    private ChatOptions CreateOptions(AgentTurnRequest request)
    {
        var options = new ChatOptions
        {
            ModelId = request.ModelId,
            Instructions = ComposeInstructions(request),
            MaxOutputTokens = request.MaxOutputTokens,
            Tools = AgentToolBridge.CreateDeclarations(request.Tools).ToList(),
            ToolMode = request.Tools.Count > 0 ? ChatToolMode.Auto : ChatToolMode.None,
            AllowMultipleToolCalls = true,
            Reasoning = CreateReasoningOptions(request, _supportsReasoningEffort),
        };

        _configureOptions?.Invoke(request, options);

        return options;
    }

    private static ReasoningOptions? CreateReasoningOptions(
        AgentTurnRequest request,
        Func<AgentTurnRequest, AgentReasoningEffort, bool>? supportsReasoningEffort)
    {
        if (request.ReasoningEffort is not { } reasoningEffort ||
            !SupportsRequestedReasoningEffort(request, reasoningEffort, supportsReasoningEffort))
        {
            return null;
        }

        return new ReasoningOptions
        {
            Effort = reasoningEffort switch
            {
                AgentReasoningEffort.Minimal => ReasoningEffort.Low,
                AgentReasoningEffort.Low => ReasoningEffort.Low,
                AgentReasoningEffort.Medium => ReasoningEffort.Medium,
                AgentReasoningEffort.High => ReasoningEffort.High,
                // The highest effort a client names; a null effort would mean no thinking.
                AgentReasoningEffort.XHigh or AgentReasoningEffort.Max => ReasoningEffort.ExtraHigh,
                _ => null,
            },
            Output = ReasoningOutput.Full,
        };
    }

    private static bool SupportsRequestedReasoningEffort(
        AgentTurnRequest request,
        AgentReasoningEffort reasoningEffort,
        Func<AgentTurnRequest, AgentReasoningEffort, bool>? supportsReasoningEffort)
    {
        if (reasoningEffort == AgentReasoningEffort.None)
        {
            return false;
        }

        if (request.ModelInfo?.SupportedReasoningEfforts is { } supportedReasoningEfforts)
        {
            return supportedReasoningEfforts.Contains(reasoningEffort);
        }

        return supportsReasoningEffort?.Invoke(request, reasoningEffort) ?? true;
    }

    private static string? ComposeInstructions(AgentTurnRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SystemMessage))
        {
            return string.IsNullOrWhiteSpace(request.DeveloperInstructions)
                ? null
                : request.DeveloperInstructions.Trim();
        }

        if (string.IsNullOrWhiteSpace(request.DeveloperInstructions))
        {
            return request.SystemMessage.Trim();
        }

        return $"""
                {request.SystemMessage.Trim()}

                <developer_instructions>
                {request.DeveloperInstructions.Trim()}
                </developer_instructions>
                """;
    }

    private static ChatMessage MapMessage(AgentConversationMessage message, bool inlineImages)
        => new(MapRole(message.Role), message.Parts.Select(part => MapPart(part, inlineImages)).ToList());

    private static ChatRole MapRole(AgentConversationRole role)
        => role switch
        {
            AgentConversationRole.System => ChatRole.System,
            AgentConversationRole.User => ChatRole.User,
            AgentConversationRole.Assistant => ChatRole.Assistant,
            AgentConversationRole.Tool => ChatRole.Tool,
            _ => ChatRole.User,
        };

    private static AIContent MapPart(AgentMessagePart part, bool inlineImages)
        => part switch
        {
            AgentMessagePart.Text text => new TextContent(text.Value),
            AgentMessagePart.Reasoning reasoning => new TextReasoningContent(reasoning.Value ?? string.Empty)
            {
                ProtectedData = reasoning.ProtectedData,
                AdditionalProperties = reasoning.Redacted == true ? new() { [RedactedReasoningKey] = true } : null,
            },
            AgentMessagePart.ToolCall toolCall => new FunctionCallContent(
                toolCall.CallId,
                toolCall.Name,
                DeserializeArguments(toolCall.Arguments)),
            AgentMessagePart.ToolResult toolResult => new FunctionResultContent(
                toolResult.CallId,
                CreateFunctionResult(toolResult.Result, inlineImages)),
            AgentMessagePart.Uri uri => new UriContent(
                new Uri(uri.Value, UriKind.RelativeOrAbsolute),
                NormalizeMediaType(uri.MediaType, uri.Value)),
            AgentMessagePart.Data data => new DataContent(
                Convert.FromBase64String(data.Base64Data),
                NormalizeMediaType(data.MediaType, data.Name))
            {
                Name = data.Name,
            },
            _ => new TextContent(string.Empty),
        };

    private static string NormalizeMediaType(string? mediaType, string? nameOrPath)
    {
        if (!string.IsNullOrWhiteSpace(mediaType))
        {
            return mediaType.Trim();
        }

        return GuessMediaType(nameOrPath) ?? "application/octet-stream";
    }

    private static string? GuessMediaType(string? nameOrPath)
        => string.IsNullOrWhiteSpace(nameOrPath)
            ? null
            : Path.GetExtension(nameOrPath).ToLowerInvariant() switch
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

    private static object? CreateFunctionResult(AgentToolResult result, bool inlineImages)
    {
        if (result.Items.Count == 0)
        {
            return result.Error;
        }

        if (result.Items.Count == 1 && result.Items[0] is AgentToolResultItem.Text text)
        {
            return text.Value;
        }

        var contentItems = new List<AIContent>(result.Items.Count);
        foreach (var item in result.Items)
        {
            switch (item)
            {
                case AgentToolResultItem.Text textItem:
                    contentItems.Add(new TextContent(textItem.Value));
                    break;
                case AgentToolResultItem.ImageUrl imageUrl:
                    contentItems.Add(new UriContent(new Uri(imageUrl.Url, UriKind.Absolute), "image/*"));
                    break;
                case AgentToolResultItem.Image image when inlineImages:
                    contentItems.Add(new DataContent(Convert.FromBase64String(image.Base64Data), image.MediaType) { Name = image.DisplayName });
                    break;
                case AgentToolResultItem.Image or AgentToolResultItem.LocalImage:
                    contentItems.Add(new TextContent(Images.AgentToolResultImages.Describe(item)));
                    break;
            }
        }

        return contentItems;
    }

    private static IDictionary<string, object?>? DeserializeArguments(JsonElement arguments)
    {
        if (arguments.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return arguments.ValueKind == JsonValueKind.Object
            ? arguments.EnumerateObject().ToDictionary(
                static property => property.Name,
                static property => ConvertJsonValue(property.Value),
                StringComparer.Ordinal)
            : null;
    }

    private static IEnumerable<AgentTurnDelta> ExtractStreamingDeltas(ChatResponseUpdate update, string attemptId)
    {
        foreach (var content in update.Contents)
        {
            switch (content)
            {
                case TextContent textContent when !string.IsNullOrEmpty(textContent.Text):
                    yield return new AgentTurnDelta
                    {
                        Kind = AgentContentKind.Assistant,
                        ContentId = update.MessageId ?? update.ResponseId ?? $"assistant:{Guid.CreateVersion7()}",
                        Text = textContent.Text,
                        AttemptId = attemptId,
                    };
                    break;
                case TextReasoningContent reasoning when !string.IsNullOrEmpty(reasoning.Text):
                    yield return new AgentTurnDelta
                    {
                        Kind = AgentContentKind.Reasoning,
                        ContentId = update.MessageId ?? update.ResponseId ?? $"reasoning:{Guid.CreateVersion7()}",
                        Text = reasoning.Text,
                        AttemptId = attemptId,
                    };
                    break;
            }
        }
    }

    private static (AgentConversationMessage Message, IReadOnlyList<string?> PartContentIds) MapAssistantMessage(
        ChatResponse response,
        AgentTurnRequest request)
    {
        var assistantMessages = response.Messages
            .Where(static message => message.Role == ChatRole.Assistant)
            .ToArray();
        if (assistantMessages.Length == 0)
        {
            return (
                new AgentConversationMessage(
                    AgentConversationRole.Assistant,
                    []),
                Array.Empty<string?>());
        }

        var parts = new List<AgentMessagePart>();
        var partContentIds = new List<string?>();
        var reasoningProvenance = AgentReasoningReplay.CreateProvenance(request);
        foreach (var message in assistantMessages)
        {
            // The text and the reasoning are kept in the order they came, one part per block of reasoning: a block
            // ends with its signature, and a provider checks each block against what came before it.
            var assistantText = new System.Text.StringBuilder();
            var reasoningText = new System.Text.StringBuilder();
            string? reasoningProtectedData = null;
            var reasoningRedacted = false;
            var hasReasoning = false;

            void FlushReasoning()
            {
                if (hasReasoning && (reasoningText.Length > 0 || !string.IsNullOrWhiteSpace(reasoningProtectedData)))
                {
                    parts.Add(new AgentMessagePart.Reasoning(
                        reasoningText.Length == 0 ? string.Empty : reasoningText.ToString(),
                        string.IsNullOrEmpty(reasoningProtectedData) ? null : reasoningProtectedData,
                        reasoningProvenance)
                    {
                        Redacted = reasoningRedacted ? true : null,
                    });
                    partContentIds.Add(message.MessageId ?? response.ResponseId);
                }

                reasoningText.Clear();
                reasoningProtectedData = null;
                reasoningRedacted = false;
                hasReasoning = false;
            }

            void FlushText()
            {
                if (assistantText.Length > 0)
                {
                    parts.Add(new AgentMessagePart.Text(assistantText.ToString()));
                    partContentIds.Add(message.MessageId ?? response.ResponseId);
                    assistantText.Clear();
                }
            }

            void FlushBufferedContent()
            {
                FlushReasoning();
                FlushText();
            }

            foreach (var content in message.Contents)
            {
                switch (content)
                {
                    case TextContent text when !string.IsNullOrWhiteSpace(text.Text):
                        FlushReasoning();
                        assistantText.Append(text.Text);
                        break;
                    case TextReasoningContent reasoning:
                        FlushText();
                        var redacted = IsRedacted(reasoning);
                        // A block that already has its signature, or encrypted reasoning, ends before what follows.
                        if (hasReasoning && (!string.IsNullOrEmpty(reasoningProtectedData) || reasoningRedacted || redacted))
                        {
                            FlushReasoning();
                        }

                        hasReasoning = true;
                        if (reasoning.Text is { Length: > 0 } reasoningSegment)
                        {
                            reasoningText.Append(reasoningSegment);
                        }

                        if (!string.IsNullOrEmpty(reasoning.ProtectedData))
                        {
                            reasoningProtectedData = reasoning.ProtectedData;
                        }

                        reasoningRedacted |= redacted;
                        break;
                    default:
                        FlushBufferedContent();
                        if (TryMapAssistantPart(content, reasoningProvenance, out var part))
                        {
                            parts.Add(part);
                            partContentIds.Add(null);
                        }

                        break;
                }
            }

            FlushBufferedContent();
        }

        return (new AgentConversationMessage(AgentConversationRole.Assistant, parts), partContentIds);
    }

    // The Anthropic client marks the reasoning of a redacted_thinking block with this additional property, and sends
    // reasoning that carries it back as redacted_thinking (a thinking block with that payload as signature is refused).
    private const string RedactedReasoningKey = "anthropic:redacted_thinking";

    private static bool IsRedacted(AIContent content)
        => content.AdditionalProperties?.TryGetValue(RedactedReasoningKey, out var value) == true &&
            value is true or JsonElement { ValueKind: JsonValueKind.True };

    // A tool call whose arguments the client could not parse (FunctionCallContent.Exception) is recorded with no
    // arguments and must not run: running it with none would make the model chase a missing argument.
    private static Dictionary<string, string>? FindUnrunnableToolCalls(ChatResponse response)
    {
        Dictionary<string, string>? unrunnable = null;
        foreach (var functionCall in response.Messages.SelectMany(static message => message.Contents).OfType<FunctionCallContent>())
        {
            if (functionCall.Exception is null)
            {
                continue;
            }

            unrunnable ??= new Dictionary<string, string>(StringComparer.Ordinal);
            unrunnable[functionCall.CallId] = response.FinishReason == ChatFinishReason.Length
                ? $"The tool call '{functionCall.Name}' was cut by the output limit of the model (max_tokens) before its arguments were complete, so it did not run. Make the call again with less in it: split a large file or edit into several smaller calls."
                : $"The arguments of the tool call '{functionCall.Name}' are not valid JSON, so it did not run: {functionCall.Exception.Message}";
        }

        return unrunnable;
    }

    private static bool TryMapAssistantPart(
        AIContent content,
        AgentReasoningProvenance reasoningProvenance,
        out AgentMessagePart part)
    {
        switch (content)
        {
            case TextContent text when !string.IsNullOrWhiteSpace(text.Text):
                part = new AgentMessagePart.Text(text.Text);
                return true;
            case TextReasoningContent reasoning:
                if (string.IsNullOrWhiteSpace(reasoning.Text) && string.IsNullOrWhiteSpace(reasoning.ProtectedData))
                {
                    break;
                }

                part = new AgentMessagePart.Reasoning(reasoning.Text, reasoning.ProtectedData, reasoningProvenance);
                return true;
            case FunctionCallContent functionCall:
                part = new AgentMessagePart.ToolCall(
                    functionCall.CallId,
                    functionCall.Name,
                    SerializeArguments(functionCall.Arguments));
                return true;
        }

        part = default!;
        return false;
    }

    private static JsonElement SerializeArguments(IDictionary<string, object?>? arguments)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            if (arguments is not null)
            {
                foreach (var pair in arguments)
                {
                    writer.WritePropertyName(pair.Key);
                    WriteJsonValue(writer, pair.Value);
                }
            }

            writer.WriteEndObject();
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    private AgentSessionUsage? CreateUsage(AgentTurnRequest request, ChatResponse response)
    {
        if (response.Usage is null)
        {
            return null;
        }

        var cacheWriteTokens = GetCacheWriteTokens(response.Usage);
        var usage = AgentUsageFactory.CreateOperationUsage(
            modelId: response.ModelId ?? request.ModelId,
            modelInfo: request.ModelInfo,
            inputTokens: response.Usage.InputTokenCount,
            outputTokens: response.Usage.OutputTokenCount,
            totalTokens: response.Usage.TotalTokenCount,
            cachedInputTokens: response.Usage.CachedInputTokenCount,
            reasoningTokens: response.Usage.ReasoningTokenCount,
            updatedAt: response.CreatedAt ?? DateTimeOffset.UtcNow,
            cacheWriteTokens: cacheWriteTokens);

        // A request that read or wrote the cache keeps it for the lifetime of the provider from then: a read
        // refreshes it too.
        return _promptCacheLifetime is { } lifetime && (response.Usage.CachedInputTokenCount is > 0 || cacheWriteTokens is > 0)
            ? usage with { PromptCacheExpiresAt = usage.UpdatedAt + lifetime }
            : usage;
    }

    // The Anthropic client reports what a request wrote to the prompt cache as an additional count, under the name
    // of the usage property of its API.
    private static long? GetCacheWriteTokens(UsageDetails usage)
        => usage.AdditionalCounts is { } counts && counts.TryGetValue("CacheCreationInputTokens", out var tokens) && tokens > 0
            ? tokens
            : null;

    // A stream that started and then failed because the provider is unavailable (an overloaded_error or api_error in
    // the stream, a dropped connection), before a tool call that a second response could make again.
    private bool ShouldAskAgain(Exception exception, List<ChatResponseUpdate> updates, CancellationToken cancellationToken)
        => !cancellationToken.IsCancellationRequested &&
            exception is not (OperationCanceledException or AgentTurnExecutionException) &&
            updates.Count > 0 &&
            !updates.Any(static update => update.Contents.Any(static content => content is FunctionCallContent)) &&
            !IsContextOverflowMessage(exception.Message) &&
            (_classifyFailure?.Invoke(exception) ?? AgentFailureClassifier.Classify(exception))?.Kind == AgentFailureKind.Unavailable;

    // The shape the session reads to discard the draft of the attempt that failed (discardDraft, draftAttemptId).
    private static JsonElement CreateRetryDetails(AgentTurnRequest request, string attemptId, bool discardDraft, Exception exception)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("attempt", 1);
            writer.WriteNumber("nextAttempt", 2);
            writer.WriteNumber("maxRetries", 1);
            writer.WriteString("runId", request.RunId.Value);
            writer.WriteString("draftAttemptId", attemptId);
            writer.WriteBoolean("discardDraft", discardDraft);
            writer.WriteString("reason", exception.GetType().Name);
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    // The shape of a reroute the other executors report: no modelId, so the session keeps the model it selected.
    private static JsonElement CreateRerouteDetails(string? requestedModel, string effectiveModel)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            if (!string.IsNullOrWhiteSpace(requestedModel))
            {
                writer.WriteString("requestedModel", requestedModel);
            }

            writer.WriteString("effectiveModel", effectiveModel);
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    private static JsonElement? CreateProviderState(ChatResponse response)
    {
        if (string.IsNullOrWhiteSpace(response.ResponseId) &&
            string.IsNullOrWhiteSpace(response.ConversationId))
        {
            return null;
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            if (!string.IsNullOrWhiteSpace(response.ResponseId))
            {
                writer.WriteString("responseId", response.ResponseId);
            }

            if (!string.IsNullOrWhiteSpace(response.ConversationId))
            {
                writer.WriteString("conversationId", response.ConversationId);
            }

            writer.WriteEndObject();
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    private static string? ExtractSummary(AgentConversationMessage message)
        => message.Parts
            .OfType<AgentMessagePart.Text>()
            .Select(static part => part.Value)
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));

    private static object? ConvertJsonValue(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.Object => value.EnumerateObject().ToDictionary(
                static property => property.Name,
                static property => ConvertJsonValue(property.Value),
                StringComparer.Ordinal),
            JsonValueKind.Array => value.EnumerateArray().Select(ConvertJsonValue).ToArray(),
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number when value.TryGetInt64(out var int64Value) => int64Value,
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => value.GetRawText(),
        };

    private static void WriteJsonValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                return;
            case JsonElement element:
                element.WriteTo(writer);
                return;
            case string text:
                writer.WriteStringValue(text);
                return;
            case bool booleanValue:
                writer.WriteBooleanValue(booleanValue);
                return;
            case int intValue:
                writer.WriteNumberValue(intValue);
                return;
            case long longValue:
                writer.WriteNumberValue(longValue);
                return;
            case double doubleValue:
                writer.WriteNumberValue(doubleValue);
                return;
            case float floatValue:
                writer.WriteNumberValue(floatValue);
                return;
            case decimal decimalValue:
                writer.WriteNumberValue(decimalValue);
                return;
            case IDictionary<string, object?> dictionary:
                writer.WriteStartObject();
                foreach (var pair in dictionary)
                {
                    writer.WritePropertyName(pair.Key);
                    WriteJsonValue(writer, pair.Value);
                }

                writer.WriteEndObject();
                return;
            case IEnumerable<object?> sequence:
                writer.WriteStartArray();
                foreach (var item in sequence)
                {
                    WriteJsonValue(writer, item);
                }

                writer.WriteEndArray();
                return;
            default:
                writer.WriteStringValue(value.ToString());
                return;
        }
    }

    private AgentTurnExecutionException CreateTurnExecutionException(Exception ex, string? description)
    {
        var contextOverflow = IsContextOverflowMessage(ex.Message);
        return new(
            new AgentTurnFailure(
                description ?? ex.Message,
                contextOverflow,
                contextOverflow
                    ? new AgentFailureInfo(AgentFailureKind.Other)
                    : _classifyFailure?.Invoke(ex) ?? AgentFailureClassifier.Classify(ex)),
            ex);
    }

    private static bool IsContextOverflowMessage(string? message)
        => !string.IsNullOrWhiteSpace(message) &&
           (message.Contains("context length", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("maximum context length", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("too many tokens", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("prompt is too long", StringComparison.OrdinalIgnoreCase));
}
