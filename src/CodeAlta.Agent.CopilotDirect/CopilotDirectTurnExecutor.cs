using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.LocalRuntime;

namespace CodeAlta.Agent.CopilotDirect;

internal sealed class CopilotDirectTurnExecutor : ILocalAgentTurnExecutor
{
    private readonly CopilotDirectProviderOptions _provider;
    private readonly HttpClient _httpClient;
    private readonly CopilotDirectAuthManager _authManager;
    private readonly CopilotModelDiscoveryClient _modelDiscovery;

    public CopilotDirectTurnExecutor(CopilotDirectProviderOptions provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _provider = provider;
        _httpClient = provider.HttpClient ?? new HttpClient();
        _authManager = new CopilotDirectAuthManager(provider, _httpClient);
        _modelDiscovery = new CopilotModelDiscoveryClient(provider, _authManager, _httpClient);
    }

    public Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(
        LocalAgentProviderDescriptor provider,
        CancellationToken cancellationToken = default)
        => _modelDiscovery.ListModelsAsync(provider, cancellationToken);

    public async Task<LocalAgentTurnResponse> ExecuteTurnAsync(
        LocalAgentTurnRequest request,
        Func<LocalAgentTurnDelta, CancellationToken, ValueTask> onUpdate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(onUpdate);

        var endpointKind = CopilotEndpointDispatcher.Resolve(request.ModelInfo);
        try
        {
            return await ExecuteTurnCoreAsync(request, endpointKind, onUpdate, cancellationToken).ConfigureAwait(false);
        }
        catch (CopilotDirectUnauthorizedException)
        {
            await _authManager.ForceRefreshAsync(cancellationToken).ConfigureAwait(false);
            return await ExecuteTurnCoreAsync(request, endpointKind, onUpdate, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<LocalAgentTurnResponse> ExecuteTurnCoreAsync(
        LocalAgentTurnRequest request,
        CopilotEndpointKind endpointKind,
        Func<LocalAgentTurnDelta, CancellationToken, ValueTask> onUpdate,
        CancellationToken cancellationToken)
    {
        var credential = await _authManager.GetCredentialAsync(cancellationToken).ConfigureAwait(false);
        var path = endpointKind switch
        {
            CopilotEndpointKind.Responses => "/responses",
            CopilotEndpointKind.AnthropicMessages => "/v1/messages",
            _ => "/chat/completions",
        };
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, new Uri(credential.BaseUri, path));
        CopilotDirectHeaders.ApplyTurnHeaders(
            httpRequest,
            credential.Token,
            IsAgentInitiated(request),
            HasVisionInput(request),
            endpointKind == CopilotEndpointKind.AnthropicMessages);
        var body = endpointKind switch
        {
            CopilotEndpointKind.Responses => CreateResponsesBody(request),
            CopilotEndpointKind.AnthropicMessages => CreateAnthropicMessagesBody(request),
            _ => CreateChatCompletionsBody(request),
        };
        var bodyBytes = JsonSerializer.SerializeToUtf8Bytes(body, CopilotDirectJsonContext.Default.DictionaryStringObject);
        httpRequest.Content = new ByteArrayContent(bodyBytes);
        httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        await TraceRequestAsync(httpRequest, bodyBytes, cancellationToken).ConfigureAwait(false);

        using var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await TraceResponseAsync(response, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            throw new CopilotDirectUnauthorizedException();
        }

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var retryAfter = response.Headers.RetryAfter is null ? string.Empty : $" Retry-After={response.Headers.RetryAfter}";
            throw new InvalidOperationException($"GitHub Copilot direct request failed with HTTP {(int)response.StatusCode} {response.ReasonPhrase}.{retryAfter} {TrimError(error)}");
        }

        var turn = endpointKind switch
        {
            CopilotEndpointKind.Responses => await ReadResponsesStreamAsync(response, onUpdate, cancellationToken).ConfigureAwait(false),
            CopilotEndpointKind.AnthropicMessages => await ReadAnthropicStreamAsync(response, onUpdate, cancellationToken).ConfigureAwait(false),
            _ => await ReadChatCompletionsStreamAsync(response, onUpdate, cancellationToken).ConfigureAwait(false),
        };

        return CreateResponse(request, turn);
    }

    private static Dictionary<string, object?> CreateChatCompletionsBody(LocalAgentTurnRequest request)
    {
        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = request.ModelId,
            ["stream"] = true,
            ["messages"] = CreateOpenAIMessages(request),
        };
        if (request.MaxOutputTokens is { } maxTokens && !IsGptModel(request.ModelId))
        {
            body["max_completion_tokens"] = maxTokens;
        }

        if (request.ReasoningEffort is { } effort && effort is not AgentReasoningEffort.None)
        {
            body["reasoning_effort"] = ToReasoningEffort(effort);
        }

        if (request.Tools.Count > 0)
        {
            body["tools"] = request.Tools.Select(static tool => new Dictionary<string, object?>
            {
                ["type"] = "function",
                ["function"] = new Dictionary<string, object?>
                {
                    ["name"] = tool.Spec.Name,
                    ["description"] = tool.Spec.Description,
                    ["parameters"] = tool.Spec.InputSchema.Clone(),
                },
            }).ToArray();
            body["tool_choice"] = "auto";
            body["parallel_tool_calls"] = true;
        }

        return body;
    }

    private static Dictionary<string, object?> CreateResponsesBody(LocalAgentTurnRequest request)
    {
        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = request.ModelId,
            ["stream"] = true,
            ["input"] = CreateResponsesInput(request),
        };
        if (request.MaxOutputTokens is { } maxTokens)
        {
            body["max_output_tokens"] = maxTokens;
        }

        if (request.ReasoningEffort is { } effort && effort is not AgentReasoningEffort.None)
        {
            body["reasoning"] = new Dictionary<string, object?> { ["effort"] = ToReasoningEffort(effort) };
        }

        if (request.Tools.Count > 0)
        {
            body["tools"] = request.Tools.Select(static tool => new Dictionary<string, object?>
            {
                ["type"] = "function",
                ["name"] = tool.Spec.Name,
                ["description"] = tool.Spec.Description,
                ["parameters"] = tool.Spec.InputSchema.Clone(),
            }).ToArray();
            body["tool_choice"] = "auto";
        }

        return body;
    }

    private static Dictionary<string, object?> CreateAnthropicMessagesBody(LocalAgentTurnRequest request)
    {
        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = request.ModelId,
            ["stream"] = true,
            ["messages"] = CreateAnthropicMessages(request),
            ["max_tokens"] = request.MaxOutputTokens ?? 4096,
        };
        var system = ComposeInstructions(request);
        if (!string.IsNullOrWhiteSpace(system))
        {
            body["system"] = system;
        }

        if (request.Tools.Count > 0)
        {
            body["tools"] = request.Tools.Select(static tool => new Dictionary<string, object?>
            {
                ["name"] = tool.Spec.Name,
                ["description"] = tool.Spec.Description,
                ["input_schema"] = tool.Spec.InputSchema.Clone(),
            }).ToArray();
        }

        return body;
    }

    private static List<Dictionary<string, object?>> CreateOpenAIMessages(LocalAgentTurnRequest request)
    {
        var messages = new List<Dictionary<string, object?>>();
        if (!string.IsNullOrWhiteSpace(request.SystemMessage))
        {
            messages.Add(new Dictionary<string, object?> { ["role"] = "system", ["content"] = request.SystemMessage.Trim() });
        }

        if (!string.IsNullOrWhiteSpace(request.DeveloperInstructions))
        {
            messages.Add(new Dictionary<string, object?> { ["role"] = "developer", ["content"] = request.DeveloperInstructions.Trim() });
        }

        foreach (var message in request.Conversation)
        {
            messages.Add(MapOpenAIMessage(message));
        }

        return messages;
    }

    private static List<Dictionary<string, object?>> CreateResponsesInput(LocalAgentTurnRequest request)
    {
        var input = new List<Dictionary<string, object?>>();
        if (!string.IsNullOrWhiteSpace(request.SystemMessage))
        {
            input.Add(new Dictionary<string, object?> { ["role"] = "system", ["content"] = request.SystemMessage.Trim() });
        }

        if (!string.IsNullOrWhiteSpace(request.DeveloperInstructions))
        {
            input.Add(new Dictionary<string, object?> { ["role"] = "developer", ["content"] = request.DeveloperInstructions.Trim() });
        }

        foreach (var message in request.Conversation)
        {
            input.Add(MapOpenAIMessage(message));
        }

        return input;
    }

    private static List<Dictionary<string, object?>> CreateAnthropicMessages(LocalAgentTurnRequest request)
    {
        var messages = new List<Dictionary<string, object?>>();
        foreach (var message in request.Conversation.Where(static message => message.Role is not LocalAgentConversationRole.System))
        {
            messages.Add(new Dictionary<string, object?>
            {
                ["role"] = message.Role == LocalAgentConversationRole.Assistant ? "assistant" : "user",
                ["content"] = MapAnthropicContent(message.Parts),
            });
        }

        return messages;
    }

    private static Dictionary<string, object?> MapOpenAIMessage(LocalAgentConversationMessage message)
    {
        if (message.Role == LocalAgentConversationRole.Tool)
        {
            var toolResult = message.Parts.OfType<LocalAgentMessagePart.ToolResult>().FirstOrDefault();
            return new Dictionary<string, object?>
            {
                ["role"] = "tool",
                ["tool_call_id"] = toolResult?.CallId ?? $"tool:{Guid.CreateVersion7()}",
                ["content"] = toolResult is null ? string.Empty : RenderToolResult(toolResult.Result),
            };
        }

        var result = new Dictionary<string, object?>
        {
            ["role"] = message.Role switch
            {
                LocalAgentConversationRole.System => "system",
                LocalAgentConversationRole.Assistant => "assistant",
                _ => "user",
            },
            ["content"] = MapOpenAIContent(message.Parts),
        };

        var toolCalls = message.Parts.OfType<LocalAgentMessagePart.ToolCall>().Select(static toolCall => new Dictionary<string, object?>
        {
            ["id"] = toolCall.CallId,
            ["type"] = "function",
            ["function"] = new Dictionary<string, object?>
            {
                ["name"] = toolCall.Name,
                ["arguments"] = toolCall.Arguments.GetRawText(),
            },
        }).ToArray();
        if (toolCalls.Length > 0)
        {
            result["tool_calls"] = toolCalls;
        }

        return result;
    }

    private static object MapOpenAIContent(IReadOnlyList<LocalAgentMessagePart> parts)
    {
        var content = new List<Dictionary<string, object?>>();
        foreach (var part in parts)
        {
            switch (part)
            {
                case LocalAgentMessagePart.Text text:
                    content.Add(new Dictionary<string, object?> { ["type"] = "text", ["text"] = text.Value });
                    break;
                case LocalAgentMessagePart.Reasoning reasoning when !string.IsNullOrWhiteSpace(reasoning.Value):
                    content.Add(new Dictionary<string, object?> { ["type"] = "text", ["text"] = $"<assistant_reasoning>{reasoning.Value}</assistant_reasoning>" });
                    break;
                case LocalAgentMessagePart.Uri uri when IsImageMediaType(uri.MediaType):
                    content.Add(new Dictionary<string, object?> { ["type"] = "image_url", ["image_url"] = new Dictionary<string, object?> { ["url"] = uri.Value } });
                    break;
                case LocalAgentMessagePart.Uri uri:
                    content.Add(new Dictionary<string, object?> { ["type"] = "text", ["text"] = uri.Value });
                    break;
                case LocalAgentMessagePart.Data data when IsImageMediaType(data.MediaType):
                    content.Add(new Dictionary<string, object?> { ["type"] = "image_url", ["image_url"] = new Dictionary<string, object?> { ["url"] = $"data:{data.MediaType};base64,{data.Base64Data}" } });
                    break;
                case LocalAgentMessagePart.Data data:
                    content.Add(new Dictionary<string, object?> { ["type"] = "text", ["text"] = data.Name ?? data.MediaType });
                    break;
            }
        }

        return content.Count == 1 && content[0].TryGetValue("type", out var type) && string.Equals(type?.ToString(), "text", StringComparison.Ordinal)
            ? content[0]["text"] ?? string.Empty
            : content;
    }

    private static object[] MapAnthropicContent(IReadOnlyList<LocalAgentMessagePart> parts)
    {
        var content = new List<Dictionary<string, object?>>();
        foreach (var part in parts)
        {
            switch (part)
            {
                case LocalAgentMessagePart.Text text:
                    content.Add(new Dictionary<string, object?> { ["type"] = "text", ["text"] = text.Value });
                    break;
                case LocalAgentMessagePart.Reasoning reasoning when !string.IsNullOrWhiteSpace(reasoning.Value):
                    content.Add(new Dictionary<string, object?> { ["type"] = "thinking", ["thinking"] = reasoning.Value });
                    break;
                case LocalAgentMessagePart.ToolCall toolCall:
                    content.Add(new Dictionary<string, object?> { ["type"] = "tool_use", ["id"] = toolCall.CallId, ["name"] = toolCall.Name, ["input"] = toolCall.Arguments.Clone() });
                    break;
                case LocalAgentMessagePart.ToolResult toolResult:
                    content.Add(new Dictionary<string, object?> { ["type"] = "tool_result", ["tool_use_id"] = toolResult.CallId, ["content"] = RenderToolResult(toolResult.Result) });
                    break;
                case LocalAgentMessagePart.Uri uri when IsImageMediaType(uri.MediaType):
                    content.Add(new Dictionary<string, object?> { ["type"] = "image", ["source"] = new Dictionary<string, object?> { ["type"] = "url", ["url"] = uri.Value } });
                    break;
            }
        }

        return content.Count == 0 ? [new Dictionary<string, object?> { ["type"] = "text", ["text"] = string.Empty }] : [.. content];
    }

    private static async Task<CopilotTurnResult> ReadChatCompletionsStreamAsync(HttpResponseMessage response, Func<LocalAgentTurnDelta, CancellationToken, ValueTask> onUpdate, CancellationToken cancellationToken)
    {
        var result = new CopilotTurnResult();
        var assistantContentId = $"assistant:{Guid.CreateVersion7()}";
        var reasoningContentId = $"reasoning:{Guid.CreateVersion7()}";
        var toolCalls = new Dictionary<int, StreamingToolCall>();
        await foreach (var json in ReadSseDataAsync(response, cancellationToken).ConfigureAwait(false))
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            result.ProviderId ??= root.TryGetProperty("id", out var id) ? id.GetString() : null;
            result.ModelId ??= root.TryGetProperty("model", out var model) ? model.GetString() : null;
            if (root.TryGetProperty("usage", out var usage))
            {
                result.Usage = usage.Clone();
            }

            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            {
                continue;
            }

            var delta = choices[0].TryGetProperty("delta", out var deltaElement) ? deltaElement : default;
            if (delta.ValueKind == JsonValueKind.Undefined)
            {
                continue;
            }

            if (TryGetString(delta, "content", out var content) && !string.IsNullOrEmpty(content))
            {
                result.Assistant.Append(content);
                await onUpdate(new LocalAgentTurnDelta { Kind = AgentContentKind.Assistant, ContentId = assistantContentId, Text = content }, cancellationToken).ConfigureAwait(false);
            }

            if ((TryGetString(delta, "reasoning_text", out var reasoning) || TryGetString(delta, "reasoning_content", out reasoning)) && !string.IsNullOrEmpty(reasoning))
            {
                result.Reasoning.Append(reasoning);
                await onUpdate(new LocalAgentTurnDelta { Kind = AgentContentKind.Reasoning, ContentId = reasoningContentId, Text = reasoning }, cancellationToken).ConfigureAwait(false);
            }

            if (delta.TryGetProperty("tool_calls", out var toolCallDeltas))
            {
                foreach (var toolCallDelta in toolCallDeltas.EnumerateArray())
                {
                    var index = toolCallDelta.TryGetProperty("index", out var indexElement) && indexElement.TryGetInt32(out var parsedIndex) ? parsedIndex : toolCalls.Count;
                    if (!toolCalls.TryGetValue(index, out var state))
                    {
                        state = new StreamingToolCall();
                        toolCalls[index] = state;
                    }

                    if (TryGetString(toolCallDelta, "id", out var toolCallId))
                    {
                        state.Id = toolCallId;
                    }

                    if (toolCallDelta.TryGetProperty("function", out var function))
                    {
                        if (TryGetString(function, "name", out var functionName))
                        {
                            state.Name = functionName;
                        }

                        if (TryGetString(function, "arguments", out var arguments))
                        {
                            state.Arguments.Append(arguments);
                        }
                    }
                }
            }
        }

        result.ToolCalls.AddRange(toolCalls.OrderBy(static pair => pair.Key).Select(static pair => pair.Value));
        result.AssistantContentId = assistantContentId;
        result.ReasoningContentId = reasoningContentId;
        return result;
    }

    private static async Task<CopilotTurnResult> ReadResponsesStreamAsync(HttpResponseMessage response, Func<LocalAgentTurnDelta, CancellationToken, ValueTask> onUpdate, CancellationToken cancellationToken)
    {
        var result = new CopilotTurnResult { AssistantContentId = $"assistant:{Guid.CreateVersion7()}", ReasoningContentId = $"reasoning:{Guid.CreateVersion7()}" };
        await foreach (var json in ReadSseDataAsync(response, cancellationToken).ConfigureAwait(false))
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (TryGetString(root, "response_id", out var responseId) || TryGetString(root, "id", out responseId))
            {
                result.ProviderId ??= responseId;
            }

            if (TryGetString(root, "delta", out var delta) && !string.IsNullOrEmpty(delta))
            {
                result.Assistant.Append(delta);
                await onUpdate(new LocalAgentTurnDelta { Kind = AgentContentKind.Assistant, ContentId = result.AssistantContentId, Text = delta }, cancellationToken).ConfigureAwait(false);
            }

            if (root.TryGetProperty("response", out var responseObject))
            {
                if (TryGetString(responseObject, "id", out var id))
                {
                    result.ProviderId ??= id;
                }

                if (responseObject.TryGetProperty("usage", out var usage))
                {
                    result.Usage = usage.Clone();
                }
            }
        }

        return result;
    }

    private static async Task<CopilotTurnResult> ReadAnthropicStreamAsync(HttpResponseMessage response, Func<LocalAgentTurnDelta, CancellationToken, ValueTask> onUpdate, CancellationToken cancellationToken)
    {
        var result = new CopilotTurnResult { AssistantContentId = $"assistant:{Guid.CreateVersion7()}", ReasoningContentId = $"reasoning:{Guid.CreateVersion7()}" };
        await foreach (var json in ReadSseDataAsync(response, cancellationToken).ConfigureAwait(false))
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (TryGetString(root, "message", out var _))
            {
                continue;
            }

            if (TryGetString(root, "id", out var id))
            {
                result.ProviderId ??= id;
            }

            if (root.TryGetProperty("delta", out var deltaElement))
            {
                if (TryGetString(deltaElement, "text", out var text) && !string.IsNullOrEmpty(text))
                {
                    result.Assistant.Append(text);
                    await onUpdate(new LocalAgentTurnDelta { Kind = AgentContentKind.Assistant, ContentId = result.AssistantContentId, Text = text }, cancellationToken).ConfigureAwait(false);
                }

                if ((TryGetString(deltaElement, "thinking", out var thinking) || TryGetString(deltaElement, "partial_json", out thinking)) && !string.IsNullOrEmpty(thinking))
                {
                    result.Reasoning.Append(thinking);
                    await onUpdate(new LocalAgentTurnDelta { Kind = AgentContentKind.Reasoning, ContentId = result.ReasoningContentId, Text = thinking }, cancellationToken).ConfigureAwait(false);
                }
            }

            if (root.TryGetProperty("usage", out var usage))
            {
                result.Usage = usage.Clone();
            }
        }

        return result;
    }

    private static async IAsyncEnumerable<string> ReadSseDataAsync(HttpResponseMessage response, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        var builder = new StringBuilder();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (line.Length == 0)
            {
                if (builder.Length > 0)
                {
                    var data = builder.ToString().Trim();
                    builder.Clear();
                    if (data != "[DONE]" && data.Length > 0)
                    {
                        yield return data;
                    }
                }

                continue;
            }

            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                if (builder.Length > 0)
                {
                    builder.AppendLine();
                }

                builder.Append(line[5..].TrimStart());
            }
        }
    }

    private static LocalAgentTurnResponse CreateResponse(LocalAgentTurnRequest request, CopilotTurnResult result)
    {
        var parts = new List<LocalAgentMessagePart>();
        var contentIds = new List<string?>();
        if (result.Assistant.Length > 0)
        {
            parts.Add(new LocalAgentMessagePart.Text(result.Assistant.ToString()));
            contentIds.Add(result.AssistantContentId);
        }

        if (result.Reasoning.Length > 0)
        {
            parts.Add(new LocalAgentMessagePart.Reasoning(result.Reasoning.ToString()));
            contentIds.Add(result.ReasoningContentId);
        }

        foreach (var toolCall in result.ToolCalls)
        {
            parts.Add(new LocalAgentMessagePart.ToolCall(
                string.IsNullOrWhiteSpace(toolCall.Id) ? $"tool:{Guid.CreateVersion7()}" : toolCall.Id,
                toolCall.Name ?? string.Empty,
                DeserializeArguments(toolCall.Arguments.ToString())));
            contentIds.Add(null);
        }

        var assistant = new LocalAgentConversationMessage(LocalAgentConversationRole.Assistant, parts);
        return new LocalAgentTurnResponse
        {
            AssistantMessage = assistant,
            AssistantPartContentIds = contentIds,
            Usage = CreateUsage(request, result),
            ProviderSessionId = result.ProviderId,
            Summary = parts.OfType<LocalAgentMessagePart.Text>().Select(static text => text.Value).FirstOrDefault(static text => !string.IsNullOrWhiteSpace(text)),
        };
    }

    private static AgentSessionUsage? CreateUsage(LocalAgentTurnRequest request, CopilotTurnResult result)
    {
        if (result.Usage is null)
        {
            return null;
        }

        var usage = result.Usage.Value;
        var inputTokens = TryReadLong(usage, "input_tokens") ?? TryReadLong(usage, "prompt_tokens");
        var outputTokens = TryReadLong(usage, "output_tokens") ?? TryReadLong(usage, "completion_tokens");
        var totalTokens = TryReadLong(usage, "total_tokens");
        return new AgentSessionUsage(
            LastOperation: new AgentOperationUsageSnapshot(
                Model: result.ModelId ?? request.ModelId,
                InputTokens: inputTokens,
                OutputTokens: outputTokens,
                Label: $"{result.ModelId ?? request.ModelId}: {inputTokens ?? 0}/{outputTokens ?? 0} tokens"),
            Scope: AgentUsageScope.LastOperation,
            Source: AgentUsageSource.LocalProviderUsage,
            UpdatedAt: DateTimeOffset.UtcNow);
    }

    private static string? ComposeInstructions(LocalAgentTurnRequest request)
        => string.IsNullOrWhiteSpace(request.SystemMessage)
            ? Normalize(request.DeveloperInstructions)
            : string.IsNullOrWhiteSpace(request.DeveloperInstructions)
                ? request.SystemMessage.Trim()
                : $"{request.SystemMessage.Trim()}\n\n<developer_instructions>\n{request.DeveloperInstructions.Trim()}\n</developer_instructions>";

    private static bool IsAgentInitiated(LocalAgentTurnRequest request)
        => request.Conversation.LastOrDefault()?.Role != LocalAgentConversationRole.User;

    private static bool HasVisionInput(LocalAgentTurnRequest request)
        => request.Conversation.SelectMany(static message => message.Parts).Any(static part => part switch
        {
            LocalAgentMessagePart.Uri uri => IsImageMediaType(uri.MediaType),
            LocalAgentMessagePart.Data data => IsImageMediaType(data.MediaType),
            _ => false,
        });

    private static bool IsImageMediaType(string? mediaType)
        => mediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsGptModel(string? modelId)
        => modelId?.Contains("gpt", StringComparison.OrdinalIgnoreCase) == true;

    private static string ToReasoningEffort(AgentReasoningEffort effort)
        => effort switch
        {
            AgentReasoningEffort.Minimal => "low",
            AgentReasoningEffort.Low => "low",
            AgentReasoningEffort.Medium => "medium",
            AgentReasoningEffort.High => "high",
            AgentReasoningEffort.XHigh => "high",
            _ => "medium",
        };

    private static string RenderToolResult(AgentToolResult result)
        => result.Items.Count == 0
            ? result.Error ?? string.Empty
            : string.Join(Environment.NewLine, result.Items.Select(static item => item switch
            {
                AgentToolResultItem.Text text => text.Value,
                AgentToolResultItem.ImageUrl image => image.Url,
                _ => string.Empty,
            }).Where(static text => !string.IsNullOrWhiteSpace(text)));

    private static JsonElement DeserializeArguments(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return CreateObjectElement();
        }

        try
        {
            using var document = JsonDocument.Parse(arguments);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            using var document = JsonDocument.Parse("{}");
            return document.RootElement.Clone();
        }
    }

    private static JsonElement CreateObjectElement()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    private static bool TryGetString(JsonElement element, string name, out string? value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString();
            return true;
        }

        value = null;
        return false;
    }

    private static long? TryReadLong(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var property) && property.TryGetInt64(out var value)
            ? value
            : null;

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string TrimError(string error)
        => string.IsNullOrWhiteSpace(error) ? string.Empty : (error.Length <= 512 ? error : error[..512]);

    private async ValueTask TraceRequestAsync(HttpRequestMessage request, byte[] bodyBytes, CancellationToken cancellationToken)
    {
        if (!_provider.ProtocolTraceEnabled)
        {
            return;
        }

        var builder = new StringBuilder();
        builder.AppendLine($"{DateTimeOffset.UtcNow:O} >>> {request.Method} {request.RequestUri}");
        foreach (var header in request.Headers)
        {
            builder.AppendLine($"{header.Key}: {RedactHeader(header.Key, string.Join(",", header.Value))}");
        }

        if (request.Content is not null)
        {
            foreach (var header in request.Content.Headers)
            {
                builder.AppendLine($"{header.Key}: {RedactHeader(header.Key, string.Join(",", header.Value))}");
            }
        }

        builder.AppendLine(System.Text.Encoding.UTF8.GetString(bodyBytes));
        await AppendTraceAsync(builder.ToString(), cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask TraceResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!_provider.ProtocolTraceEnabled)
        {
            return;
        }

        var builder = new StringBuilder();
        builder.AppendLine($"{DateTimeOffset.UtcNow:O} <<< {(int)response.StatusCode} {response.ReasonPhrase}");
        foreach (var header in response.Headers.Concat(response.Content.Headers))
        {
            builder.AppendLine($"{header.Key}: {RedactHeader(header.Key, string.Join(",", header.Value))}");
        }

        await AppendTraceAsync(builder.ToString(), cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask AppendTraceAsync(string text, CancellationToken cancellationToken)
    {
        var root = string.IsNullOrWhiteSpace(_provider.StateRootPath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodeAlta")
            : _provider.StateRootPath.Trim();
        var directory = Path.Combine(root, "protocol-trace", CopilotDirectAgentBackend.ProtocolFamily);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, SanitizeFileName(_provider.ProviderKey) + ".log");
        await File.AppendAllTextAsync(path, text + Environment.NewLine, cancellationToken).ConfigureAwait(false);
    }

    private static string RedactHeader(string name, string value)
        => name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("x-api-key", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
            ? "<redacted>"
            : value;

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(value.Select(ch => invalid.Contains(ch) ? '_' : ch));
    }

    private sealed class CopilotTurnResult
    {
        public StringBuilder Assistant { get; } = new();
        public StringBuilder Reasoning { get; } = new();
        public List<StreamingToolCall> ToolCalls { get; } = [];
        public string? AssistantContentId { get; set; }
        public string? ReasoningContentId { get; set; }
        public string? ProviderId { get; set; }
        public string? ModelId { get; set; }
        public JsonElement? Usage { get; set; }
    }

    private sealed class StreamingToolCall
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public StringBuilder Arguments { get; } = new();
    }
}

internal sealed class CopilotDirectUnauthorizedException : Exception;

internal static class CopilotDirectSerialization
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}
