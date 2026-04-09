using System.Text.Json;
using CodeAlta.Agent.LocalRuntime.Tools;
using Microsoft.Extensions.AI;

namespace CodeAlta.Agent.LocalRuntime;

/// <summary>
/// Shared turn executor for provider SDKs that expose <see cref="IChatClient"/>.
/// </summary>
internal sealed class LocalAgentChatClientTurnExecutor : ILocalAgentTurnExecutor
{
    private readonly Func<LocalAgentProviderDescriptor, CancellationToken, ValueTask<IChatClient>> _chatClientFactory;
    private readonly Func<LocalAgentProviderDescriptor, CancellationToken, Task<IReadOnlyList<AgentModelInfo>>> _listModelsAsync;

    /// <summary>
    /// Initializes a new instance of the <see cref="LocalAgentChatClientTurnExecutor"/> class.
    /// </summary>
    /// <param name="chatClientFactory">Factory that creates an <see cref="IChatClient"/> for a provider.</param>
    /// <param name="listModelsAsync">Delegate that lists models for a provider.</param>
    public LocalAgentChatClientTurnExecutor(
        Func<LocalAgentProviderDescriptor, CancellationToken, ValueTask<IChatClient>> chatClientFactory,
        Func<LocalAgentProviderDescriptor, CancellationToken, Task<IReadOnlyList<AgentModelInfo>>> listModelsAsync)
    {
        ArgumentNullException.ThrowIfNull(chatClientFactory);
        ArgumentNullException.ThrowIfNull(listModelsAsync);

        _chatClientFactory = chatClientFactory;
        _listModelsAsync = listModelsAsync;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(
        LocalAgentProviderDescriptor provider,
        CancellationToken cancellationToken = default)
        => _listModelsAsync(provider, cancellationToken);

    /// <inheritdoc />
    public async Task<LocalAgentTurnResponse> ExecuteTurnAsync(
        LocalAgentTurnRequest request,
        Func<LocalAgentTurnDelta, CancellationToken, ValueTask> onUpdate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(onUpdate);

        try
        {
            var chatClient = await _chatClientFactory(request.Provider, cancellationToken).ConfigureAwait(false);
            try
            {
                var messages = request.Conversation.Select(MapMessage).ToArray();
                var updates = new List<ChatResponseUpdate>();
                await foreach (var update in chatClient
                                   .GetStreamingResponseAsync(messages, CreateOptions(request), cancellationToken)
                                   .ConfigureAwait(false))
                {
                    updates.Add(update);
                    foreach (var delta in ExtractStreamingDeltas(update))
                    {
                        await onUpdate(delta, cancellationToken).ConfigureAwait(false);
                    }
                }

                var response = updates.ToChatResponse();
                var (assistantMessage, assistantPartContentIds) = MapAssistantMessage(response);
                return new LocalAgentTurnResponse
                {
                    AssistantMessage = assistantMessage,
                    AssistantPartContentIds = assistantPartContentIds,
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
        catch (LocalAgentTurnExecutionException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw CreateTurnExecutionException(ex);
        }
    }

    private static ChatOptions CreateOptions(LocalAgentTurnRequest request)
    {
        var options = new ChatOptions
        {
            ModelId = request.ModelId,
            Instructions = ComposeInstructions(request),
            Tools = LocalAgentToolBridge.CreateDeclarations(request.Tools).ToList(),
            ToolMode = request.Tools.Count > 0 ? ChatToolMode.Auto : ChatToolMode.None,
            AllowMultipleToolCalls = true,
            Reasoning = CreateReasoningOptions(request),
        };

        return options;
    }

    private static ReasoningOptions? CreateReasoningOptions(LocalAgentTurnRequest request)
    {
        if (request.ReasoningEffort is null)
        {
            return null;
        }

        return new ReasoningOptions
        {
            Effort = request.ReasoningEffort.Value switch
            {
                AgentReasoningEffort.None => ReasoningEffort.None,
                AgentReasoningEffort.Minimal => ReasoningEffort.Low,
                AgentReasoningEffort.Low => ReasoningEffort.Low,
                AgentReasoningEffort.Medium => ReasoningEffort.Medium,
                AgentReasoningEffort.High => ReasoningEffort.High,
                AgentReasoningEffort.XHigh => ReasoningEffort.High,
                _ => null,
            },
            Output = ReasoningOutput.Full,
        };
    }

    private static string? ComposeInstructions(LocalAgentTurnRequest request)
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

    private static ChatMessage MapMessage(LocalAgentConversationMessage message)
        => new(MapRole(message.Role), message.Parts.Select(MapPart).ToList());

    private static ChatRole MapRole(LocalAgentConversationRole role)
        => role switch
        {
            LocalAgentConversationRole.System => ChatRole.System,
            LocalAgentConversationRole.User => ChatRole.User,
            LocalAgentConversationRole.Assistant => ChatRole.Assistant,
            LocalAgentConversationRole.Tool => ChatRole.Tool,
            _ => ChatRole.User,
        };

    private static AIContent MapPart(LocalAgentMessagePart part)
        => part switch
        {
            LocalAgentMessagePart.Text text => new TextContent(text.Value),
            LocalAgentMessagePart.Reasoning reasoning => new TextReasoningContent(reasoning.Value ?? string.Empty)
            {
                ProtectedData = reasoning.ProtectedData,
            },
            LocalAgentMessagePart.ToolCall toolCall => new FunctionCallContent(
                toolCall.CallId,
                toolCall.Name,
                DeserializeArguments(toolCall.Arguments)),
            LocalAgentMessagePart.ToolResult toolResult => new FunctionResultContent(
                toolResult.CallId,
                CreateFunctionResult(toolResult.Result)),
            LocalAgentMessagePart.Uri uri => new UriContent(
                new Uri(uri.Value, UriKind.RelativeOrAbsolute),
                uri.MediaType ?? "application/octet-stream"),
            LocalAgentMessagePart.Data data => new DataContent(
                Convert.FromBase64String(data.Base64Data),
                data.MediaType)
            {
                Name = data.Name,
            },
            _ => new TextContent(string.Empty),
        };

    private static object? CreateFunctionResult(AgentToolResult result)
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

    private static IEnumerable<LocalAgentTurnDelta> ExtractStreamingDeltas(ChatResponseUpdate update)
    {
        foreach (var content in update.Contents)
        {
            switch (content)
            {
                case TextContent textContent when !string.IsNullOrEmpty(textContent.Text):
                    yield return new LocalAgentTurnDelta
                    {
                        Kind = AgentContentKind.Assistant,
                        ContentId = update.MessageId ?? update.ResponseId ?? $"assistant:{Guid.CreateVersion7()}",
                        Text = textContent.Text,
                    };
                    break;
                case TextReasoningContent reasoning when !string.IsNullOrEmpty(reasoning.Text):
                    yield return new LocalAgentTurnDelta
                    {
                        Kind = AgentContentKind.Reasoning,
                        ContentId = update.MessageId ?? update.ResponseId ?? $"reasoning:{Guid.CreateVersion7()}",
                        Text = reasoning.Text,
                    };
                    break;
            }
        }
    }

    private static (LocalAgentConversationMessage Message, IReadOnlyList<string?> PartContentIds) MapAssistantMessage(ChatResponse response)
    {
        var assistantMessages = response.Messages
            .Where(static message => message.Role == ChatRole.Assistant)
            .ToArray();
        if (assistantMessages.Length == 0)
        {
            return (
                new LocalAgentConversationMessage(
                    LocalAgentConversationRole.Assistant,
                    []),
                Array.Empty<string?>());
        }

        var parts = new List<LocalAgentMessagePart>();
        var partContentIds = new List<string?>();
        foreach (var message in assistantMessages)
        {
            var assistantText = new System.Text.StringBuilder();
            var reasoningText = new System.Text.StringBuilder();
            string? reasoningProtectedData = null;

            void FlushBufferedContent()
            {
                if (reasoningText.Length > 0 || !string.IsNullOrWhiteSpace(reasoningProtectedData))
                {
                    parts.Add(new LocalAgentMessagePart.Reasoning(
                        reasoningText.Length == 0 ? string.Empty : reasoningText.ToString(),
                        reasoningProtectedData));
                    partContentIds.Add(message.MessageId ?? response.ResponseId);
                    reasoningText.Clear();
                    reasoningProtectedData = null;
                }

                if (assistantText.Length > 0)
                {
                    parts.Add(new LocalAgentMessagePart.Text(assistantText.ToString()));
                    partContentIds.Add(message.MessageId ?? response.ResponseId);
                    assistantText.Clear();
                }
            }

            foreach (var content in message.Contents)
            {
                switch (content)
                {
                    case TextContent text when !string.IsNullOrWhiteSpace(text.Text):
                        assistantText.Append(text.Text);
                        break;
                    case TextReasoningContent reasoning:
                        if (reasoning.Text is { Length: > 0 } reasoningSegment)
                        {
                            reasoningText.Append(reasoningSegment);
                        }

                        reasoningProtectedData ??= reasoning.ProtectedData;
                        break;
                    default:
                        FlushBufferedContent();
                        if (TryMapAssistantPart(content, out var part))
                        {
                            parts.Add(part);
                            partContentIds.Add(null);
                        }

                        break;
                }
            }

            FlushBufferedContent();
        }

        return (new LocalAgentConversationMessage(LocalAgentConversationRole.Assistant, parts), partContentIds);
    }

    private static bool TryMapAssistantPart(AIContent content, out LocalAgentMessagePart part)
    {
        switch (content)
        {
            case TextContent text when !string.IsNullOrWhiteSpace(text.Text):
                part = new LocalAgentMessagePart.Text(text.Text);
                return true;
            case TextReasoningContent reasoning:
                if (string.IsNullOrWhiteSpace(reasoning.Text) && string.IsNullOrWhiteSpace(reasoning.ProtectedData))
                {
                    break;
                }

                part = new LocalAgentMessagePart.Reasoning(reasoning.Text, reasoning.ProtectedData);
                return true;
            case FunctionCallContent functionCall:
                part = new LocalAgentMessagePart.ToolCall(
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

    private static AgentSessionUsage? CreateUsage(LocalAgentTurnRequest request, ChatResponse response)
    {
        if (response.Usage is null)
        {
            return null;
        }

        return LocalAgentUsageFactory.CreateOperationUsage(
            modelId: response.ModelId ?? request.ModelId,
            modelInfo: request.ModelInfo,
            inputTokens: response.Usage.InputTokenCount,
            outputTokens: response.Usage.OutputTokenCount,
            totalTokens: response.Usage.TotalTokenCount,
            cachedInputTokens: response.Usage.CachedInputTokenCount,
            reasoningTokens: response.Usage.ReasoningTokenCount,
            updatedAt: response.CreatedAt ?? DateTimeOffset.UtcNow);
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

    private static string? ExtractSummary(LocalAgentConversationMessage message)
        => message.Parts
            .OfType<LocalAgentMessagePart.Text>()
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

    private static LocalAgentTurnExecutionException CreateTurnExecutionException(Exception ex)
        => new(
            new LocalAgentTurnFailure(
                ex.Message,
                IsContextOverflowMessage(ex.Message)),
            ex);

    private static bool IsContextOverflowMessage(string? message)
        => !string.IsNullOrWhiteSpace(message) &&
           (message.Contains("context length", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("maximum context length", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("too many tokens", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("prompt is too long", StringComparison.OrdinalIgnoreCase));
}
