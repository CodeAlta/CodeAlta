using System.Collections;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using CodeAlta.Agent.Runtime;
using CodeAlta.Agent.Runtime.Compaction;

namespace CodeAlta.Agent;

internal sealed class ModelProviderIdJsonConverter : JsonConverter<ModelProviderId>
{
    public override ModelProviderId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(reader.GetString() ?? string.Empty);

    public override void Write(Utf8JsonWriter writer, ModelProviderId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}

internal sealed class AgentRunIdJsonConverter : JsonConverter<AgentRunId>
{
    public override AgentRunId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(reader.GetString() ?? string.Empty);

    public override void Write(Utf8JsonWriter writer, AgentRunId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}

internal sealed class AgentObjectDictionaryJsonConverter : JsonConverter<IReadOnlyDictionary<string, object?>>
{
    public override IReadOnlyDictionary<string, object?> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal);
        }

        using var document = JsonDocument.ParseValue(ref reader);
        return document.RootElement.ValueKind == JsonValueKind.Object
            ? ReadObject(document.RootElement)
            : throw new JsonException("Expected a JSON object.");
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyDictionary<string, object?> value, JsonSerializerOptions options)
        => WriteObject(writer, value);

    private static Dictionary<string, object?> ReadObject(JsonElement element)
    {
        var dictionary = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            dictionary[property.Name] = ReadValue(property.Value);
        }

        return dictionary;
    }

    private static object? ReadValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => ReadObject(element),
            JsonValueKind.Array => element.EnumerateArray().Select(ReadValue).ToArray(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out var int64) => int64,
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => element.Clone(),
        };
    }

    private static void WriteObject(Utf8JsonWriter writer, IEnumerable<KeyValuePair<string, object?>> entries)
    {
        writer.WriteStartObject();
        foreach (var entry in entries)
        {
            writer.WritePropertyName(entry.Key);
            WriteValue(writer, entry.Value);
        }

        writer.WriteEndObject();
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                return;
            case JsonElement element:
                element.WriteTo(writer);
                return;
            case string stringValue:
                writer.WriteStringValue(stringValue);
                return;
            case char charValue:
                writer.WriteStringValue(charValue.ToString());
                return;
            case bool boolValue:
                writer.WriteBooleanValue(boolValue);
                return;
            case byte byteValue:
                writer.WriteNumberValue(byteValue);
                return;
            case sbyte sbyteValue:
                writer.WriteNumberValue(sbyteValue);
                return;
            case short shortValue:
                writer.WriteNumberValue(shortValue);
                return;
            case ushort ushortValue:
                writer.WriteNumberValue(ushortValue);
                return;
            case int intValue:
                writer.WriteNumberValue(intValue);
                return;
            case uint uintValue:
                writer.WriteNumberValue(uintValue);
                return;
            case long longValue:
                writer.WriteNumberValue(longValue);
                return;
            case ulong ulongValue:
                writer.WriteNumberValue(ulongValue);
                return;
            case float floatValue:
                writer.WriteNumberValue(floatValue);
                return;
            case double doubleValue:
                writer.WriteNumberValue(doubleValue);
                return;
            case decimal decimalValue:
                writer.WriteNumberValue(decimalValue);
                return;
            case DateTimeOffset dateTimeOffsetValue:
                writer.WriteStringValue(dateTimeOffsetValue);
                return;
            case DateTime dateTimeValue:
                writer.WriteStringValue(dateTimeValue);
                return;
            case Guid guidValue:
                writer.WriteStringValue(guidValue);
                return;
            case ModelProviderId ProviderId:
                writer.WriteStringValue(ProviderId.Value);
                return;
            case AgentRunId runId:
                writer.WriteStringValue(runId.Value);
                return;
            case Enum enumValue:
                writer.WriteStringValue(enumValue.ToString());
                return;
            case IReadOnlyDictionary<string, object?> readOnlyDictionary:
                WriteObject(writer, readOnlyDictionary);
                return;
            case IDictionary<string, object?> dictionary:
                WriteObject(writer, dictionary);
                return;
            case IEnumerable enumerable:
                writer.WriteStartArray();
                foreach (var item in enumerable)
                {
                    WriteValue(writer, item);
                }

                writer.WriteEndArray();
                return;
            default:
                throw new NotSupportedException(
                    $"Unsupported capability value type '{value.GetType().FullName}'.");
        }
    }
}

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    WriteIndented = false)]
[JsonSerializable(typeof(ModelProviderId))]
[JsonSerializable(typeof(AgentRunId))]
[JsonSerializable(typeof(AgentEvent))]
[JsonSerializable(typeof(AgentPermissionRequest))]
[JsonSerializable(typeof(AgentInput))]
[JsonSerializable(typeof(AgentSendOptions))]
[JsonSerializable(typeof(AgentSteerOptions))]
[JsonSerializable(typeof(AgentMcpServerConfig))]
[JsonSerializable(typeof(AgentLocalMcpServerConfig))]
[JsonSerializable(typeof(AgentRemoteMcpServerConfig))]
[JsonSerializable(typeof(AgentMcpRemoteTransport))]
[JsonSerializable(typeof(AgentInputItem))]
[JsonSerializable(typeof(AgentInputItem.Text), TypeInfoPropertyName = "AgentInputItemText")]
[JsonSerializable(typeof(AgentInputItem.ImageUrl), TypeInfoPropertyName = "AgentInputItemImageUrl")]
[JsonSerializable(typeof(AgentInputItem.LocalImage), TypeInfoPropertyName = "AgentInputItemLocalImage")]
[JsonSerializable(typeof(AgentInputItem.File), TypeInfoPropertyName = "AgentInputItemFile")]
[JsonSerializable(typeof(AgentInputItem.Directory), TypeInfoPropertyName = "AgentInputItemDirectory")]
[JsonSerializable(typeof(AgentInputItem.Selection), TypeInfoPropertyName = "AgentInputItemSelection")]
[JsonSerializable(typeof(AgentInputItem.Skill), TypeInfoPropertyName = "AgentInputItemSkill")]
[JsonSerializable(typeof(AgentInputItem.Mention), TypeInfoPropertyName = "AgentInputItemMention")]
[JsonSerializable(typeof(AgentLineRange))]
[JsonSerializable(typeof(AgentPosition))]
[JsonSerializable(typeof(AgentSelectionRange))]
[JsonSerializable(typeof(AgentToolInvocation))]
[JsonSerializable(typeof(AgentToolResult))]
[JsonSerializable(typeof(AgentToolResultItem))]
[JsonSerializable(typeof(AgentToolResultItem.Text), TypeInfoPropertyName = "AgentToolResultItemText")]
[JsonSerializable(typeof(AgentToolResultItem.ImageUrl), TypeInfoPropertyName = "AgentToolResultItemImageUrl")]
[JsonSerializable(typeof(AgentToolSpec))]
[JsonSerializable(typeof(AgentPermissionDecision))]
[JsonSerializable(typeof(AgentCommandPreviewAction))]
[JsonSerializable(typeof(AgentNetworkAccessRequest))]
[JsonSerializable(typeof(AgentNetworkPolicyAmendment))]
[JsonSerializable(typeof(AgentExceptionInfo))]
[JsonSerializable(typeof(AgentModelInfo))]
[JsonSerializable(typeof(AgentSystemPromptEvent))]
[JsonSerializable(typeof(AgentSystemPromptProviderPayloadSummary))]
[JsonSerializable(typeof(AgentSystemPromptStatistics))]
[JsonSerializable(typeof(AgentSystemPromptChangeSummary))]
[JsonSerializable(typeof(AgentSessionContext))]
[JsonSerializable(typeof(AgentSessionListFilter))]
[JsonSerializable(typeof(AgentSessionMetadata))]
[JsonSerializable(typeof(AgentSessionMetadataDetails))]
[JsonSerializable(typeof(CodexSessionMetadataDetails))]
[JsonSerializable(typeof(CopilotSessionMetadataDetails))]
[JsonSerializable(typeof(RawApiSessionMetadataDetails))]
[JsonSerializable(typeof(AgentTransportKind))]
[JsonSerializable(typeof(AgentCompactionSettings))]
[JsonSerializable(typeof(AgentProviderProfile))]
[JsonSerializable(typeof(ModelProviderRuntimeDescriptor))]
[JsonSerializable(typeof(AgentCompactionSnapshot))]
[JsonSerializable(typeof(AgentCompactionCheckpoint))]
[JsonSerializable(typeof(AgentConversationMessage))]
[JsonSerializable(typeof(AgentConversationRole))]
[JsonSerializable(typeof(AgentReasoningProvenance))]
[JsonSerializable(typeof(AgentMessagePart))]
[JsonSerializable(typeof(AgentMessagePart.Text), TypeInfoPropertyName = "AgentMessagePartText")]
[JsonSerializable(typeof(AgentMessagePart.Reasoning), TypeInfoPropertyName = "AgentMessagePartReasoning")]
[JsonSerializable(typeof(AgentMessagePart.ToolCall), TypeInfoPropertyName = "AgentMessagePartToolCall")]
[JsonSerializable(typeof(AgentMessagePart.ToolResult), TypeInfoPropertyName = "AgentMessagePartToolResult")]
[JsonSerializable(typeof(AgentMessagePart.Uri), TypeInfoPropertyName = "AgentMessagePartUri")]
[JsonSerializable(typeof(AgentMessagePart.Data), TypeInfoPropertyName = "AgentMessagePartData")]
[JsonSerializable(typeof(AgentLoadedSkillState))]
[JsonSerializable(typeof(AgentSessionSummary))]
[JsonSerializable(typeof(AgentSessionState))]
[JsonSerializable(typeof(AgentSessionUsage))]
[JsonSerializable(typeof(AgentWindowUsageSnapshot))]
[JsonSerializable(typeof(AgentOperationUsageSnapshot))]
[JsonSerializable(typeof(AgentRateLimitSummary))]
[JsonSerializable(typeof(AgentRateLimitWindow))]
[JsonSerializable(typeof(AgentUsageScope))]
[JsonSerializable(typeof(AgentUsageSource))]
[JsonSerializable(typeof(AgentSessionUsageDetails))]
[JsonSerializable(typeof(CodexSessionUsageDetails))]
[JsonSerializable(typeof(CodexTokenUsage))]
[JsonSerializable(typeof(CodexRateLimitSnapshot))]
[JsonSerializable(typeof(CodexRateLimitWindow))]
[JsonSerializable(typeof(CopilotSessionUsageDetails))]
[JsonSerializable(typeof(CopilotAssistantUsage))]
[JsonSerializable(typeof(CopilotTokenDetail))]
[JsonSerializable(typeof(CopilotCompactionUsage))]
[JsonSerializable(typeof(CopilotCompactionTokenUsage))]
[JsonSerializable(typeof(CopilotQuotaSnapshot))]
[JsonSerializable(typeof(CopilotQuotaDetails))]
[JsonSerializable(typeof(CopilotRequestQuotaDetails))]
[JsonSerializable(typeof(CopilotOpaqueQuotaDetails))]
[JsonSerializable(typeof(AgentPlanSnapshot))]
[JsonSerializable(typeof(AgentPlanStep))]
[JsonSerializable(typeof(AgentUserInputForm))]
[JsonSerializable(typeof(AgentUserInputPrompt))]
[JsonSerializable(typeof(AgentUserInputOption))]
[JsonSerializable(typeof(AgentUserInputRequest))]
[JsonSerializable(typeof(AgentUserInputResponse))]
internal partial class AgentJsonSerializerContext : JsonSerializerContext;

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    WriteIndented = true)]
[JsonSerializable(typeof(ModelProviderId))]
[JsonSerializable(typeof(AgentRunId))]
[JsonSerializable(typeof(AgentEvent))]
[JsonSerializable(typeof(AgentPermissionRequest))]
[JsonSerializable(typeof(AgentInput))]
[JsonSerializable(typeof(AgentSendOptions))]
[JsonSerializable(typeof(AgentSteerOptions))]
[JsonSerializable(typeof(AgentMcpServerConfig))]
[JsonSerializable(typeof(AgentLocalMcpServerConfig))]
[JsonSerializable(typeof(AgentRemoteMcpServerConfig))]
[JsonSerializable(typeof(AgentMcpRemoteTransport))]
[JsonSerializable(typeof(AgentInputItem))]
[JsonSerializable(typeof(AgentInputItem.Text), TypeInfoPropertyName = "IndentedAgentInputItemText")]
[JsonSerializable(typeof(AgentInputItem.ImageUrl), TypeInfoPropertyName = "IndentedAgentInputItemImageUrl")]
[JsonSerializable(typeof(AgentInputItem.LocalImage), TypeInfoPropertyName = "IndentedAgentInputItemLocalImage")]
[JsonSerializable(typeof(AgentInputItem.File), TypeInfoPropertyName = "IndentedAgentInputItemFile")]
[JsonSerializable(typeof(AgentInputItem.Directory), TypeInfoPropertyName = "IndentedAgentInputItemDirectory")]
[JsonSerializable(typeof(AgentInputItem.Selection), TypeInfoPropertyName = "IndentedAgentInputItemSelection")]
[JsonSerializable(typeof(AgentInputItem.Skill), TypeInfoPropertyName = "IndentedAgentInputItemSkill")]
[JsonSerializable(typeof(AgentInputItem.Mention), TypeInfoPropertyName = "IndentedAgentInputItemMention")]
[JsonSerializable(typeof(AgentLineRange))]
[JsonSerializable(typeof(AgentPosition))]
[JsonSerializable(typeof(AgentSelectionRange))]
[JsonSerializable(typeof(AgentToolInvocation))]
[JsonSerializable(typeof(AgentToolResult))]
[JsonSerializable(typeof(AgentToolResultItem))]
[JsonSerializable(typeof(AgentToolResultItem.Text), TypeInfoPropertyName = "IndentedAgentToolResultItemText")]
[JsonSerializable(typeof(AgentToolResultItem.ImageUrl), TypeInfoPropertyName = "IndentedAgentToolResultItemImageUrl")]
[JsonSerializable(typeof(AgentToolSpec))]
[JsonSerializable(typeof(AgentPermissionDecision))]
[JsonSerializable(typeof(AgentCommandPreviewAction))]
[JsonSerializable(typeof(AgentNetworkAccessRequest))]
[JsonSerializable(typeof(AgentNetworkPolicyAmendment))]
[JsonSerializable(typeof(AgentExceptionInfo))]
[JsonSerializable(typeof(AgentModelInfo))]
[JsonSerializable(typeof(AgentSystemPromptEvent))]
[JsonSerializable(typeof(AgentSystemPromptProviderPayloadSummary))]
[JsonSerializable(typeof(AgentSystemPromptStatistics))]
[JsonSerializable(typeof(AgentSystemPromptChangeSummary))]
[JsonSerializable(typeof(AgentSessionContext))]
[JsonSerializable(typeof(AgentSessionListFilter))]
[JsonSerializable(typeof(AgentSessionMetadata))]
[JsonSerializable(typeof(AgentSessionMetadataDetails))]
[JsonSerializable(typeof(CodexSessionMetadataDetails))]
[JsonSerializable(typeof(CopilotSessionMetadataDetails))]
[JsonSerializable(typeof(RawApiSessionMetadataDetails))]
[JsonSerializable(typeof(AgentTransportKind))]
[JsonSerializable(typeof(AgentCompactionSettings))]
[JsonSerializable(typeof(AgentProviderProfile))]
[JsonSerializable(typeof(ModelProviderRuntimeDescriptor))]
[JsonSerializable(typeof(AgentCompactionSnapshot))]
[JsonSerializable(typeof(AgentCompactionCheckpoint))]
[JsonSerializable(typeof(AgentConversationMessage))]
[JsonSerializable(typeof(AgentConversationRole))]
[JsonSerializable(typeof(AgentReasoningProvenance))]
[JsonSerializable(typeof(AgentMessagePart))]
[JsonSerializable(typeof(AgentMessagePart.Text), TypeInfoPropertyName = "IndentedAgentMessagePartText")]
[JsonSerializable(typeof(AgentMessagePart.Reasoning), TypeInfoPropertyName = "IndentedAgentMessagePartReasoning")]
[JsonSerializable(typeof(AgentMessagePart.ToolCall), TypeInfoPropertyName = "IndentedAgentMessagePartToolCall")]
[JsonSerializable(typeof(AgentMessagePart.ToolResult), TypeInfoPropertyName = "IndentedAgentMessagePartToolResult")]
[JsonSerializable(typeof(AgentMessagePart.Uri), TypeInfoPropertyName = "IndentedAgentMessagePartUri")]
[JsonSerializable(typeof(AgentMessagePart.Data), TypeInfoPropertyName = "IndentedAgentMessagePartData")]
[JsonSerializable(typeof(AgentLoadedSkillState))]
[JsonSerializable(typeof(AgentSessionSummary))]
[JsonSerializable(typeof(AgentSessionState))]
[JsonSerializable(typeof(AgentSessionUsage))]
[JsonSerializable(typeof(AgentWindowUsageSnapshot))]
[JsonSerializable(typeof(AgentOperationUsageSnapshot))]
[JsonSerializable(typeof(AgentRateLimitSummary))]
[JsonSerializable(typeof(AgentRateLimitWindow))]
[JsonSerializable(typeof(AgentUsageScope))]
[JsonSerializable(typeof(AgentUsageSource))]
[JsonSerializable(typeof(AgentSessionUsageDetails))]
[JsonSerializable(typeof(CodexSessionUsageDetails))]
[JsonSerializable(typeof(CodexTokenUsage))]
[JsonSerializable(typeof(CodexRateLimitSnapshot))]
[JsonSerializable(typeof(CodexRateLimitWindow))]
[JsonSerializable(typeof(CopilotSessionUsageDetails))]
[JsonSerializable(typeof(CopilotAssistantUsage))]
[JsonSerializable(typeof(CopilotTokenDetail))]
[JsonSerializable(typeof(CopilotCompactionUsage))]
[JsonSerializable(typeof(CopilotCompactionTokenUsage))]
[JsonSerializable(typeof(CopilotQuotaSnapshot))]
[JsonSerializable(typeof(CopilotQuotaDetails))]
[JsonSerializable(typeof(CopilotRequestQuotaDetails))]
[JsonSerializable(typeof(CopilotOpaqueQuotaDetails))]
[JsonSerializable(typeof(AgentPlanSnapshot))]
[JsonSerializable(typeof(AgentPlanStep))]
[JsonSerializable(typeof(AgentUserInputForm))]
[JsonSerializable(typeof(AgentUserInputPrompt))]
[JsonSerializable(typeof(AgentUserInputOption))]
[JsonSerializable(typeof(AgentUserInputRequest))]
[JsonSerializable(typeof(AgentUserInputResponse))]
internal partial class AgentIndentedJsonSerializerContext : JsonSerializerContext;

/// <summary>
/// JSON serialization helpers for CodeAlta agent DTOs.
/// </summary>
public static class AgentJsonExtensions
{
    /// <summary>
    /// Serializes the event to JSON.
    /// </summary>
    /// <param name="value">The value to serialize.</param>
    /// <param name="indented">Whether to use indented formatting.</param>
    /// <returns>The JSON representation.</returns>
    public static string ToJson(this AgentEvent value, bool indented = false)
        => Serialize(value, AgentJsonSerializerContext.Default.AgentEvent, AgentIndentedJsonSerializerContext.Default.AgentEvent, indented);

    /// <summary>
    /// Serializes the permission request to JSON.
    /// </summary>
    /// <param name="value">The value to serialize.</param>
    /// <param name="indented">Whether to use indented formatting.</param>
    /// <returns>The JSON representation.</returns>
    public static string ToJson(this AgentPermissionRequest value, bool indented = false)
        => Serialize(value, AgentJsonSerializerContext.Default.AgentPermissionRequest, AgentIndentedJsonSerializerContext.Default.AgentPermissionRequest, indented);

    /// <summary>
    /// Serializes the input payload to JSON.
    /// </summary>
    /// <param name="value">The value to serialize.</param>
    /// <param name="indented">Whether to use indented formatting.</param>
    /// <returns>The JSON representation.</returns>
    public static string ToJson(this AgentInput value, bool indented = false)
        => Serialize(value, AgentJsonSerializerContext.Default.AgentInput, AgentIndentedJsonSerializerContext.Default.AgentInput, indented);

    /// <summary>
    /// Serializes the agent-runtime conversation message to JSON.
    /// </summary>
    /// <param name="value">The value to serialize.</param>
    /// <param name="indented">Whether to use indented formatting.</param>
    /// <returns>The JSON representation.</returns>
    public static string ToJson(this AgentConversationMessage value, bool indented = false)
        => Serialize(value, AgentJsonSerializerContext.Default.AgentConversationMessage, AgentIndentedJsonSerializerContext.Default.AgentConversationMessage, indented);

    /// <summary>
    /// Serializes the send options to JSON.
    /// </summary>
    /// <param name="value">The value to serialize.</param>
    /// <param name="indented">Whether to use indented formatting.</param>
    /// <returns>The JSON representation.</returns>
    public static string ToJson(this AgentSendOptions value, bool indented = false)
        => Serialize(value, AgentJsonSerializerContext.Default.AgentSendOptions, AgentIndentedJsonSerializerContext.Default.AgentSendOptions, indented);

    /// <summary>
    /// Serializes the steer options to JSON.
    /// </summary>
    /// <param name="value">The value to serialize.</param>
    /// <param name="indented">Whether to use indented formatting.</param>
    /// <returns>The JSON representation.</returns>
    public static string ToJson(this AgentSteerOptions value, bool indented = false)
        => Serialize(value, AgentJsonSerializerContext.Default.AgentSteerOptions, AgentIndentedJsonSerializerContext.Default.AgentSteerOptions, indented);

    /// <summary>
    /// Serializes the MCP server configuration to JSON.
    /// </summary>
    /// <param name="value">The value to serialize.</param>
    /// <param name="indented">Whether to use indented formatting.</param>
    /// <returns>The JSON representation.</returns>
    public static string ToJson(this AgentMcpServerConfig value, bool indented = false)
        => Serialize(value, AgentJsonSerializerContext.Default.AgentMcpServerConfig, AgentIndentedJsonSerializerContext.Default.AgentMcpServerConfig, indented);

    /// <summary>
    /// Serializes the session metadata to JSON.
    /// </summary>
    /// <param name="value">The value to serialize.</param>
    /// <param name="indented">Whether to use indented formatting.</param>
    /// <returns>The JSON representation.</returns>
    public static string ToJson(this AgentSessionMetadata value, bool indented = false)
        => Serialize(value, AgentJsonSerializerContext.Default.AgentSessionMetadata, AgentIndentedJsonSerializerContext.Default.AgentSessionMetadata, indented);

    /// <summary>
    /// Serializes the local provider descriptor to JSON.
    /// </summary>
    /// <param name="value">The value to serialize.</param>
    /// <param name="indented">Whether to use indented formatting.</param>
    /// <returns>The JSON representation.</returns>
    public static string ToJson(this ModelProviderRuntimeDescriptor value, bool indented = false)
        => Serialize(value, AgentJsonSerializerContext.Default.ModelProviderRuntimeDescriptor, AgentIndentedJsonSerializerContext.Default.ModelProviderRuntimeDescriptor, indented);

    /// <summary>
    /// Serializes the local session summary to JSON.
    /// </summary>
    /// <param name="value">The value to serialize.</param>
    /// <param name="indented">Whether to use indented formatting.</param>
    /// <returns>The JSON representation.</returns>
    public static string ToJson(this AgentSessionSummary value, bool indented = false)
        => Serialize(value, AgentJsonSerializerContext.Default.AgentSessionSummary, AgentIndentedJsonSerializerContext.Default.AgentSessionSummary, indented);

    /// <summary>
    /// Serializes the local session state to JSON.
    /// </summary>
    /// <param name="value">The value to serialize.</param>
    /// <param name="indented">Whether to use indented formatting.</param>
    /// <returns>The JSON representation.</returns>
    public static string ToJson(this AgentSessionState value, bool indented = false)
        => Serialize(value, AgentJsonSerializerContext.Default.AgentSessionState, AgentIndentedJsonSerializerContext.Default.AgentSessionState, indented);

    /// <summary>
    /// Serializes the model info to JSON.
    /// </summary>
    /// <param name="value">The value to serialize.</param>
    /// <param name="indented">Whether to use indented formatting.</param>
    /// <returns>The JSON representation.</returns>
    public static string ToJson(this AgentModelInfo value, bool indented = false)
        => Serialize(value, AgentJsonSerializerContext.Default.AgentModelInfo, AgentIndentedJsonSerializerContext.Default.AgentModelInfo, indented);

    /// <summary>
    /// Serializes the permission decision to JSON.
    /// </summary>
    /// <param name="value">The value to serialize.</param>
    /// <param name="indented">Whether to use indented formatting.</param>
    /// <returns>The JSON representation.</returns>
    public static string ToJson(this AgentPermissionDecision value, bool indented = false)
        => Serialize(value, AgentJsonSerializerContext.Default.AgentPermissionDecision, AgentIndentedJsonSerializerContext.Default.AgentPermissionDecision, indented);

    /// <summary>
    /// Serializes the tool invocation to JSON.
    /// </summary>
    /// <param name="value">The value to serialize.</param>
    /// <param name="indented">Whether to use indented formatting.</param>
    /// <returns>The JSON representation.</returns>
    public static string ToJson(this AgentToolInvocation value, bool indented = false)
        => Serialize(value, AgentJsonSerializerContext.Default.AgentToolInvocation, AgentIndentedJsonSerializerContext.Default.AgentToolInvocation, indented);

    /// <summary>
    /// Serializes the tool result to JSON.
    /// </summary>
    /// <param name="value">The value to serialize.</param>
    /// <param name="indented">Whether to use indented formatting.</param>
    /// <returns>The JSON representation.</returns>
    public static string ToJson(this AgentToolResult value, bool indented = false)
        => Serialize(value, AgentJsonSerializerContext.Default.AgentToolResult, AgentIndentedJsonSerializerContext.Default.AgentToolResult, indented);

    /// <summary>
    /// Serializes the user input request to JSON.
    /// </summary>
    /// <param name="value">The value to serialize.</param>
    /// <param name="indented">Whether to use indented formatting.</param>
    /// <returns>The JSON representation.</returns>
    public static string ToJson(this AgentUserInputRequest value, bool indented = false)
        => Serialize(value, AgentJsonSerializerContext.Default.AgentUserInputRequest, AgentIndentedJsonSerializerContext.Default.AgentUserInputRequest, indented);

    /// <summary>
    /// Serializes the user input response to JSON.
    /// </summary>
    /// <param name="value">The value to serialize.</param>
    /// <param name="indented">Whether to use indented formatting.</param>
    /// <returns>The JSON representation.</returns>
    public static string ToJson(this AgentUserInputResponse value, bool indented = false)
        => Serialize(value, AgentJsonSerializerContext.Default.AgentUserInputResponse, AgentIndentedJsonSerializerContext.Default.AgentUserInputResponse, indented);

    /// <summary>
    /// Deserializes a loaded-skill state payload from a JSON element.
    /// </summary>
    /// <param name="value">The serialized JSON payload.</param>
    /// <returns>The loaded-skill state when the payload is valid; otherwise <see langword="null"/>.</returns>
    public static AgentLoadedSkillState? ToAgentLoadedSkillState(this JsonElement value)
        => value.Deserialize(AgentJsonSerializerContext.Default.AgentLoadedSkillState);

    /// <summary>
    /// Serializes the value to indented JSON.
    /// </summary>
    /// <param name="value">The value to serialize.</param>
    /// <returns>The indented JSON representation.</returns>
    public static string ToJsonIndented(this AgentEvent value) => value.ToJson(indented: true);

    private static string Serialize<T>(T value, JsonTypeInfo<T> compactTypeInfo, JsonTypeInfo<T> indentedTypeInfo, bool indented)
    {
        return JsonSerializer.Serialize(value, indented ? indentedTypeInfo : compactTypeInfo);
    }
}
