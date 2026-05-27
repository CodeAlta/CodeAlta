#pragma warning disable OPENAI001
#pragma warning disable SCME0001

using System.ClientModel.Primitives;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using CodeAlta.Agent.LocalRuntime;
using CodeAlta.Agent.LocalRuntime.Tools;
using OpenAI.Chat;

namespace CodeAlta.Agent.OpenAI;

internal sealed class OpenAIChatTurnExecutor(OpenAIProviderOptions provider) : IModelProviderTurnExecutor, IModelProviderModelCatalog
{
    public Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(
        ModelProviderRuntimeDescriptor providerDescriptor,
        CancellationToken cancellationToken = default)
        => OpenAIProviderSdkFactory.ListModelsAsync(provider, providerDescriptor, cancellationToken);

    public async Task<LocalAgentTurnResponse> ExecuteTurnAsync(
        LocalAgentTurnRequest request,
        Func<LocalAgentTurnDelta, CancellationToken, ValueTask> onUpdate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(onUpdate);

        try
        {
            var protocolTrace = OpenAIProtocolTraceLogger.Create(provider.ProtocolTracing, request);
            var client = OpenAIProviderSdkFactory.CreateChatClient(provider, request.ModelId, protocolTrace);
            var modelRequest = OpenAIModelRequestOverrides.Find(provider.ModelRequestOverrides, request.ModelId);
            using var headerScope = provider.RequestHeaderContext?.Push(
                OpenAIModelRequestOverrides.MergeHeaders(provider.ExtraHeaders, modelRequest));
            var messages = CreateMessages(request);
            var options = CreateOptions(request);
            var streamedToolCalls = new Dictionary<int, StreamingToolCallState>();
            var streamedAssistantContent = new StringBuilder();
            var streamedReasoning = new StringBuilder();
            var assistantContentId = $"assistant:{Guid.CreateVersion7()}";
            var reasoningContentId = $"reasoning:{Guid.CreateVersion7()}";
            ChatTokenUsage? usage = null;
            string? completionId = null;
            string? modelId = null;

            await foreach (var update in client.CompleteChatStreamingAsync(messages, options, cancellationToken).ConfigureAwait(false))
            {
                protocolTrace?.WriteLine("<<< stream update (sdk model JSON):");
                protocolTrace?.WriteLine(SerializeModel(update));
                completionId ??= update.CompletionId;
                modelId ??= update.Model;
                usage = update.Usage ?? usage;

                foreach (var contentPart in update.ContentUpdate)
                {
                    if (contentPart.Kind == ChatMessageContentPartKind.Text && !string.IsNullOrEmpty(contentPart.Text))
                    {
                        streamedAssistantContent.Append(contentPart.Text);
                        await onUpdate(
                            new LocalAgentTurnDelta
                            {
                                Kind = AgentContentKind.Assistant,
                                ContentId = assistantContentId,
                                Text = contentPart.Text,
                            },
                            cancellationToken).ConfigureAwait(false);
                    }
                }

                if (!string.IsNullOrEmpty(update.RefusalUpdate))
                {
                    streamedAssistantContent.Append(update.RefusalUpdate);
                    await onUpdate(
                        new LocalAgentTurnDelta
                        {
                            Kind = AgentContentKind.Assistant,
                            ContentId = assistantContentId,
                            Text = update.RefusalUpdate,
                        },
                        cancellationToken).ConfigureAwait(false);
                }

                if (TryGetReasoningDelta(
                    update,
                    request.Provider.Profile?.ReasoningFieldNames,
                    streamedReasoning,
                    out var reasoningDelta))
                {
                    streamedReasoning.Append(reasoningDelta);
                    await onUpdate(
                        new LocalAgentTurnDelta
                        {
                            Kind = AgentContentKind.Reasoning,
                            ContentId = reasoningContentId,
                            Text = reasoningDelta,
                        },
                        cancellationToken).ConfigureAwait(false);
                }

                foreach (var toolUpdate in update.ToolCallUpdates)
                {
                    if (!streamedToolCalls.TryGetValue(toolUpdate.Index, out var toolState))
                    {
                        toolState = new StreamingToolCallState();
                        streamedToolCalls.Add(toolUpdate.Index, toolState);
                    }

                    if (!string.IsNullOrWhiteSpace(toolUpdate.ToolCallId))
                    {
                        toolState.CallId = toolUpdate.ToolCallId;
                    }

                    if (!string.IsNullOrWhiteSpace(toolUpdate.FunctionName))
                    {
                        toolState.Name = toolUpdate.FunctionName;
                    }

                    var argumentDelta = toolUpdate.FunctionArgumentsUpdate?.ToString();
                    if (!string.IsNullOrEmpty(argumentDelta))
                    {
                        toolState.Arguments.Append(argumentDelta);
                    }
                }
            }

            var parts = new List<LocalAgentMessagePart>();
            var assistantPartContentIds = new List<string?>();
            if (streamedAssistantContent.Length > 0)
            {
                parts.Add(new LocalAgentMessagePart.Text(streamedAssistantContent.ToString()));
                assistantPartContentIds.Add(assistantContentId);
            }

            if (streamedReasoning.Length > 0)
            {
                parts.Add(new LocalAgentMessagePart.Reasoning(
                    streamedReasoning.ToString(),
                    ProtectedData: null,
                    LocalAgentReasoningReplay.CreateProvenance(request)));
                assistantPartContentIds.Add(reasoningContentId);
            }

            foreach (var toolState in streamedToolCalls.OrderBy(static pair => pair.Key).Select(static pair => pair.Value))
            {
                parts.Add(new LocalAgentMessagePart.ToolCall(
                    toolState.CallId ?? $"tool:{Guid.CreateVersion7()}",
                    toolState.Name ?? string.Empty,
                    DeserializeToolArguments(toolState.Arguments.ToString())));
                assistantPartContentIds.Add(null);
            }

            var assistantMessage = new LocalAgentConversationMessage(LocalAgentConversationRole.Assistant, parts);
            protocolTrace?.WriteLine(
                $"### turn end provider={request.Provider.ProviderKey} session={request.SessionId} run={request.RunId.Value} completion={completionId ?? "<none>"} model={modelId ?? "<none>"}");
            return new LocalAgentTurnResponse
            {
                AssistantMessage = assistantMessage,
                AssistantPartContentIds = assistantPartContentIds,
                Usage = CreateUsage(request, modelId, usage),
                ProviderSessionId = string.IsNullOrWhiteSpace(completionId) ? null : completionId,
                ProviderState = CreateProviderState(completionId),
                Summary = ExtractSummary(assistantMessage),
            };
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

    private static IReadOnlyList<ChatMessage> CreateMessages(LocalAgentTurnRequest request)
    {
        var supportsDeveloperRole = request.Provider.Profile?.SupportsDeveloperRole ?? true;
        var messages = new List<ChatMessage>();

        if (!string.IsNullOrWhiteSpace(request.SystemMessage))
        {
            messages.Add(ChatMessage.CreateSystemMessage(request.SystemMessage.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(request.DeveloperInstructions))
        {
            var developerInstructions = request.DeveloperInstructions.Trim();
            if (supportsDeveloperRole)
            {
                messages.Add(ChatMessage.CreateDeveloperMessage(developerInstructions));
            }
            else if (messages.LastOrDefault() is SystemChatMessage systemMessage)
            {
                messages[^1] = ChatMessage.CreateSystemMessage(
                    MergeSystemAndDeveloperInstructions(
                        string.Concat(systemMessage.Content.Select(static part => part.Text)),
                        developerInstructions));
            }
            else
            {
                messages.Add(ChatMessage.CreateSystemMessage(
                    MergeSystemAndDeveloperInstructions(null, developerInstructions)));
            }
        }

        foreach (var message in LocalAgentReasoningReplay.SanitizeForRequest(request.Conversation, request))
        {
            messages.Add(MapMessage(message, request.Provider.Profile));
        }

        return messages;
    }

    private ChatCompletionOptions CreateOptions(LocalAgentTurnRequest request)
    {
        var options = new ChatCompletionOptions
        {
            ToolChoice = request.Tools.Count > 0 ? ChatToolChoice.CreateAutoChoice() : null,
            StoredOutputEnabled = request.Provider.Profile?.SupportsStore == false ? null : false,
            AllowParallelToolCalls = request.Provider.Profile?.SupportsParallelToolCalls == false
                ? null
                : request.Tools.Count > 0,
        };

        ApplyMaxOutputTokens(request, options);

        if ((request.Provider.Profile?.SupportsReasoningEffort ?? true) &&
            request.ReasoningEffort is { } reasoningEffort &&
            SupportsRequestedReasoningEffort(request, reasoningEffort) &&
            TryMapReasoningEffort(reasoningEffort, out var reasoningEffortLevel))
        {
            options.ReasoningEffortLevel = reasoningEffortLevel;
        }

        var modelRequest = OpenAIModelRequestOverrides.Find(provider.ModelRequestOverrides, request.ModelId);
        OpenAIExtraBodyPatchHelper.Apply(
            ref options.Patch,
            OpenAIModelRequestOverrides.MergeExtraBody(provider.ExtraBody, modelRequest));

        foreach (var tool in request.Tools)
        {
            options.Tools.Add(
                ChatTool.CreateFunctionTool(
                    LocalAgentToolBridge.GetRegisteredToolName(tool.Spec.Name),
                    tool.Spec.Description,
                    BinaryData.FromString(LocalAgentToolBridge.CreateOpenAIStrictInputSchema(tool.Spec.InputSchema).GetRawText()),
                    functionSchemaIsStrict: true));
        }

        return options;
    }

    private static void ApplyMaxOutputTokens(LocalAgentTurnRequest request, ChatCompletionOptions options)
    {
        if (request.MaxOutputTokens is not { } maxOutputTokens)
        {
            return;
        }

        var fieldName = NormalizeMaxOutputTokensFieldName(request.Provider.Profile?.MaxTokensFieldName);
        if (string.Equals(fieldName, "max_completion_tokens", StringComparison.Ordinal))
        {
            options.MaxOutputTokenCount = maxOutputTokens;
            return;
        }

        OpenAIExtraBodyPatchHelper.Apply(
            ref options.Patch,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [fieldName] = maxOutputTokens,
            });
    }

    private static string NormalizeMaxOutputTokensFieldName(string? fieldName)
        => string.IsNullOrWhiteSpace(fieldName)
            ? "max_completion_tokens"
            : fieldName.Trim();

    private static bool SupportsRequestedReasoningEffort(LocalAgentTurnRequest request, AgentReasoningEffort reasoningEffort)
    {
        if (reasoningEffort == AgentReasoningEffort.None)
        {
            return false;
        }

        return request.ModelInfo?.SupportedReasoningEfforts is not { } supportedReasoningEfforts ||
            supportedReasoningEfforts.Contains(reasoningEffort);
    }

    private static bool TryMapReasoningEffort(AgentReasoningEffort reasoningEffort, out ChatReasoningEffortLevel reasoningEffortLevel)
    {
        switch (reasoningEffort)
        {
            case AgentReasoningEffort.Minimal:
            case AgentReasoningEffort.Low:
                reasoningEffortLevel = ChatReasoningEffortLevel.Low;
                return true;
            case AgentReasoningEffort.Medium:
                reasoningEffortLevel = ChatReasoningEffortLevel.Medium;
                return true;
            case AgentReasoningEffort.High:
            case AgentReasoningEffort.XHigh:
                reasoningEffortLevel = ChatReasoningEffortLevel.High;
                return true;
            default:
                reasoningEffortLevel = default;
                return false;
        }
    }

    private static ChatMessage MapMessage(
        LocalAgentConversationMessage message,
        LocalAgentProviderProfile? profile)
    {
        return message.Role switch
        {
            LocalAgentConversationRole.System => ChatMessage.CreateSystemMessage(CreateContentParts(message.Parts)),
            LocalAgentConversationRole.User => ChatMessage.CreateUserMessage(CreateContentParts(message.Parts)),
            LocalAgentConversationRole.Assistant => CreateAssistantMessage(message.Parts, profile),
            LocalAgentConversationRole.Tool => CreateToolMessage(message.Parts),
            _ => ChatMessage.CreateUserMessage(string.Empty),
        };
    }

    private static ChatMessageContentPart[] CreateContentParts(IReadOnlyList<LocalAgentMessagePart> parts)
    {
        var contentParts = new List<ChatMessageContentPart>(parts.Count);
        foreach (var part in parts)
        {
            switch (part)
            {
                case LocalAgentMessagePart.Text text:
                    contentParts.Add(ChatMessageContentPart.CreateTextPart(text.Value));
                    break;
                case LocalAgentMessagePart.Uri uri when Uri.TryCreate(uri.Value, UriKind.Absolute, out var absoluteUri):
                    contentParts.Add(
                        IsImageMediaType(uri.MediaType)
                            ? ChatMessageContentPart.CreateImagePart(absoluteUri)
                            : ChatMessageContentPart.CreateTextPart(uri.Value));
                    break;
                case LocalAgentMessagePart.Data data:
                    var bytes = BinaryData.FromBytes(Convert.FromBase64String(data.Base64Data));
                    contentParts.Add(
                        IsImageMediaType(data.MediaType)
                            ? ChatMessageContentPart.CreateImagePart(bytes, data.MediaType ?? "image/*")
                            : ChatMessageContentPart.CreateFilePart(bytes, data.MediaType ?? "application/octet-stream", data.Name ?? "attachment"));
                    break;
                case LocalAgentMessagePart.Reasoning reasoning when !string.IsNullOrWhiteSpace(reasoning.Value):
                    contentParts.Add(ChatMessageContentPart.CreateTextPart($"<assistant_reasoning>{reasoning.Value}</assistant_reasoning>"));
                    break;
            }
        }

        return contentParts.Count == 0
            ? [ChatMessageContentPart.CreateTextPart(string.Empty)]
            : [.. contentParts];
    }

    private static AssistantChatMessage CreateAssistantMessage(
        IReadOnlyList<LocalAgentMessagePart> parts,
        LocalAgentProviderProfile? profile)
    {
        var contentParts = new List<ChatMessageContentPart>();
        var toolCalls = new List<ChatToolCall>();
        var reasoningInputFieldName = NormalizeReasoningInputFieldName(profile?.ReasoningInputFieldName);
        StringBuilder? reasoningInput = null;

        foreach (var part in parts)
        {
            switch (part)
            {
                case LocalAgentMessagePart.Text text:
                    contentParts.Add(ChatMessageContentPart.CreateTextPart(text.Value));
                    break;
                case LocalAgentMessagePart.Uri uri:
                    contentParts.Add(ChatMessageContentPart.CreateTextPart(uri.Value));
                    break;
                case LocalAgentMessagePart.Data data:
                    contentParts.Add(ChatMessageContentPart.CreateTextPart(data.Name ?? data.MediaType ?? "attachment"));
                    break;
                case LocalAgentMessagePart.Reasoning reasoning when !string.IsNullOrWhiteSpace(reasoning.Value) &&
                    reasoningInputFieldName is not null:
                    reasoningInput ??= new StringBuilder();
                    if (reasoningInput.Length > 0)
                    {
                        reasoningInput.AppendLine();
                    }

                    reasoningInput.Append(reasoning.Value);
                    break;
                case LocalAgentMessagePart.Reasoning reasoning when !string.IsNullOrWhiteSpace(reasoning.Value):
                    contentParts.Add(ChatMessageContentPart.CreateTextPart($"<assistant_reasoning>{reasoning.Value}</assistant_reasoning>"));
                    break;
                case LocalAgentMessagePart.ToolCall toolCall:
                    toolCalls.Add(
                        ChatToolCall.CreateFunctionToolCall(
                            toolCall.CallId,
                            LocalAgentToolBridge.GetRegisteredToolName(toolCall.Name),
                            BinaryData.FromString(toolCall.Arguments.GetRawText())));
                    break;
            }
        }

        var assistantMessage = contentParts.Count == 0
            ? new AssistantChatMessage(string.Empty)
            : new AssistantChatMessage(contentParts);
        foreach (var toolCall in toolCalls)
        {
            assistantMessage.ToolCalls.Add(toolCall);
        }

        if (reasoningInputFieldName is not null && (reasoningInput is not null || toolCalls.Count > 0))
        {
            assistantMessage.Patch.Set(
                Encoding.UTF8.GetBytes($"$.{reasoningInputFieldName}"),
                CreateJsonStringValue(reasoningInput?.ToString() ?? string.Empty));
        }

        return assistantMessage;
    }

    private static BinaryData CreateJsonStringValue(string value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStringValue(value);
        }

        return BinaryData.FromBytes(stream.ToArray());
    }

    private static string? NormalizeReasoningInputFieldName(string? reasoningInputFieldName)
    {
        if (string.IsNullOrWhiteSpace(reasoningInputFieldName))
        {
            return null;
        }

        var normalized = reasoningInputFieldName.Trim();
        return normalized.StartsWith("$.", StringComparison.Ordinal)
            ? normalized[2..]
            : normalized;
    }

    private static ToolChatMessage CreateToolMessage(IReadOnlyList<LocalAgentMessagePart> parts)
    {
        var toolResult = parts.OfType<LocalAgentMessagePart.ToolResult>().FirstOrDefault();
        if (toolResult is null)
        {
            return new ToolChatMessage($"tool:{Guid.CreateVersion7()}", string.Empty);
        }

        return new ToolChatMessage(toolResult.CallId, RenderToolResult(toolResult.Result));
    }

    private static JsonElement DeserializeToolArguments(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return CreateObjectElement(static writer => { });
        }

        try
        {
            using var document = JsonDocument.Parse(arguments);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return CreateObjectElement(writer => writer.WriteString("raw", arguments));
        }
    }

    private static AgentSessionUsage? CreateUsage(LocalAgentTurnRequest request, string? modelId, ChatTokenUsage? usage)
    {
        if (usage is null)
        {
            return null;
        }

        return LocalAgentUsageFactory.CreateOperationUsage(
            modelId: modelId ?? request.ModelId,
            modelInfo: request.ModelInfo,
            inputTokens: usage.InputTokenCount,
            outputTokens: usage.OutputTokenCount,
            totalTokens: usage.TotalTokenCount,
            cachedInputTokens: usage.InputTokenDetails?.CachedTokenCount,
            reasoningTokens: usage.OutputTokenDetails?.ReasoningTokenCount,
            updatedAt: DateTimeOffset.UtcNow);
    }

    private static JsonElement? CreateProviderState(string? completionId)
    {
        if (string.IsNullOrWhiteSpace(completionId))
        {
            return null;
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("completionId", completionId);
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    private static JsonElement CreateObjectElement(Action<Utf8JsonWriter> writeProperties)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writeProperties(writer);
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    private static string? ExtractSummary(LocalAgentConversationMessage message)
        => message.Parts
            .OfType<LocalAgentMessagePart.Text>()
            .Select(static part => part.Value)
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));

    private static bool TryGetReasoningDelta(
        StreamingChatCompletionUpdate update,
        IReadOnlyList<string>? reasoningFieldNames,
        StringBuilder accumulatedReasoning,
        [NotNullWhen(true)] out string? reasoningText)
    {
        ArgumentNullException.ThrowIfNull(accumulatedReasoning);

        foreach (var reasoningFieldName in EnumerateReasoningFieldNames(reasoningFieldNames))
        {
            if (!TryGetReasoningValue(update, reasoningFieldName, out var reasoningValue))
            {
                continue;
            }

            reasoningText = ComputeReasoningDelta(accumulatedReasoning, reasoningValue);
            if (!string.IsNullOrWhiteSpace(reasoningText))
            {
                return true;
            }
        }

        reasoningText = null;
        return false;
    }

    private static IEnumerable<string> EnumerateReasoningFieldNames(IReadOnlyList<string>? reasoningFieldNames)
        => reasoningFieldNames is { Count: > 0 }
            ? reasoningFieldNames.Where(static value => !string.IsNullOrWhiteSpace(value))
            : ["reasoning_content", "reasoning"];

    private static bool TryGetReasoningValue(
        StreamingChatCompletionUpdate update,
        string reasoningFieldName,
        [NotNullWhen(true)] out string? reasoningValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reasoningFieldName);

        var normalizedFieldName = reasoningFieldName.Trim();
        var jsonPath = normalizedFieldName.StartsWith("$", StringComparison.Ordinal)
            ? normalizedFieldName
            : $"$.choices[0].delta.{normalizedFieldName}";

        return update.Patch.TryGetValue(Encoding.UTF8.GetBytes(jsonPath), out reasoningValue) &&
            !string.IsNullOrWhiteSpace(reasoningValue);
    }

    private static string? ComputeReasoningDelta(StringBuilder accumulatedReasoning, string reasoningValue)
    {
        ArgumentNullException.ThrowIfNull(accumulatedReasoning);
        ArgumentException.ThrowIfNullOrWhiteSpace(reasoningValue);

        if (accumulatedReasoning.Length == 0)
        {
            return reasoningValue;
        }

        var currentReasoning = accumulatedReasoning.ToString();
        return reasoningValue.StartsWith(currentReasoning, StringComparison.Ordinal)
            ? reasoningValue[currentReasoning.Length..]
            : reasoningValue;
    }

    private static string MergeSystemAndDeveloperInstructions(string? systemMessage, string developerInstructions)
        => string.IsNullOrWhiteSpace(systemMessage)
            ? developerInstructions
            : $"""
               {systemMessage.Trim()}

               <developer_instructions>
               {developerInstructions}
               </developer_instructions>
               """;

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

    private static string SerializeModel<T>(T model)
        where T : notnull
        => model is IPersistableModel<T> persistable
            ? persistable.Write(new ModelReaderWriterOptions("J")).ToString()
            : model.ToString() ?? string.Empty;

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
            message.Contains("too many tokens", StringComparison.OrdinalIgnoreCase));

    private sealed class StreamingToolCallState
    {
        public StringBuilder Arguments { get; } = new();

        public string? CallId { get; set; }

        public string? Name { get; set; }
    }
}
