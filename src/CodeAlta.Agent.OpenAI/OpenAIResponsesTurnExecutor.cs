#pragma warning disable OPENAI001

using System.Text.Json;
using CodeAlta.Agent.LocalRuntime;
using CodeAlta.Agent.LocalRuntime.Tools;
using OpenAI.Responses;

namespace CodeAlta.Agent.OpenAI;

internal sealed class OpenAIResponsesTurnExecutor(OpenAIProviderOptions provider) : ILocalAgentTurnExecutor
{
    public Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(
        LocalAgentProviderDescriptor providerDescriptor,
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
            var client = OpenAIProviderSdkFactory.CreateResponsesClient(provider, request.ModelId);
            var options = CreateRequestPayload(request);
            ResponseResult? completedResponse = null;

            await foreach (var update in client.CreateResponseStreamingAsync(options, cancellationToken).ConfigureAwait(false))
            {
                switch (update)
                {
                    case StreamingResponseOutputTextDeltaUpdate outputTextDelta when !string.IsNullOrEmpty(outputTextDelta.Delta):
                        await onUpdate(
                            new LocalAgentTurnDelta
                            {
                                Kind = AgentContentKind.Assistant,
                                ContentId = outputTextDelta.ItemId,
                                Text = outputTextDelta.Delta,
                            },
                            cancellationToken).ConfigureAwait(false);
                        break;
                    case StreamingResponseReasoningSummaryTextDeltaUpdate reasoningSummaryDelta when !string.IsNullOrEmpty(reasoningSummaryDelta.Delta):
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
                        await onUpdate(
                            new LocalAgentTurnDelta
                            {
                                Kind = AgentContentKind.Reasoning,
                                ContentId = reasoningTextDelta.ItemId,
                                Text = reasoningTextDelta.Delta,
                            },
                            cancellationToken).ConfigureAwait(false);
                        break;
                    case StreamingResponseCompletedUpdate completed:
                        completedResponse = completed.Response;
                        break;
                }
            }

            if (completedResponse is null)
            {
                throw new InvalidOperationException("The OpenAI Responses stream completed without a final response payload.");
            }

            var (assistantMessage, assistantPartContentIds) = MapAssistantMessage(completedResponse);
            return new LocalAgentTurnResponse
            {
                AssistantMessage = assistantMessage,
                AssistantPartContentIds = assistantPartContentIds,
                Usage = CreateUsage(request, completedResponse),
                ProviderSessionId = string.IsNullOrWhiteSpace(completedResponse.Id) ? null : completedResponse.Id,
                ProviderState = CreateProviderState(completedResponse),
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

    private static CreateResponseOptions CreateRequestPayload(LocalAgentTurnRequest request)
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
                    AgentReasoningEffort.XHigh => ResponseReasoningEffortLevel.High,
                    _ => null,
                },
            };
        }

        return options;
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
        var data = BinaryData.FromBytes(Convert.FromBase64String(part.Base64Data));
        contentPart = IsImageMediaType(part.MediaType)
            ? ResponseContentPart.CreateInputImagePart(data, part.MediaType ?? "image/*")
            : ResponseContentPart.CreateInputFilePart(data, part.MediaType ?? "application/octet-stream", part.Name ?? "attachment");
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

    private static JsonElement? CreateProviderState(ResponseResult response)
    {
        if (string.IsNullOrWhiteSpace(response.Id))
        {
            return null;
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("responseId", response.Id);
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
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
}
