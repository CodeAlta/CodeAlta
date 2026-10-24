using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeAlta.Acp;

internal interface IRawJsonValue<TSelf>
    where TSelf : struct, IRawJsonValue<TSelf>
{
    JsonElement Value { get; set; }

    static abstract TSelf FromJsonElement(JsonElement value);
}

internal sealed class RawJsonValueConverter<T> : JsonConverter<T>
    where T : struct, IRawJsonValue<T>
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return T.FromJsonElement(document.RootElement.Clone());
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        value.Value.WriteTo(writer);
    }
}

[JsonConverter(typeof(RawJsonValueConverter<AgentResponse>))]
public partial struct AgentResponse : IRawJsonValue<AgentResponse>
{
    public static AgentResponse FromJsonElement(JsonElement value) => new() { Value = value };
}

[JsonConverter(typeof(RawJsonValueConverter<AvailableCommandInput>))]
public partial record struct AvailableCommandInput : IRawJsonValue<AvailableCommandInput>
{
    public static AvailableCommandInput FromJsonElement(JsonElement value) => new() { Value = value };
}

[JsonConverter(typeof(RawJsonValueConverter<ClientResponse>))]
public partial struct ClientResponse : IRawJsonValue<ClientResponse>
{
    public static ClientResponse FromJsonElement(JsonElement value) => new() { Value = value };
}

[JsonConverter(typeof(RawJsonValueConverter<CompactionStatus>))]
public partial struct CompactionStatus : IRawJsonValue<CompactionStatus>
{
    public static CompactionStatus FromJsonElement(JsonElement value) => new() { Value = value };
}

[JsonConverter(typeof(RawJsonValueConverter<ContentBlock>))]
public partial struct ContentBlock : IRawJsonValue<ContentBlock>
{
    public static ContentBlock FromJsonElement(JsonElement value) => new() { Value = value };
}

[JsonConverter(typeof(RawJsonValueConverter<CreateElicitationRequest>))]
public partial struct CreateElicitationRequest : IRawJsonValue<CreateElicitationRequest>
{
    public static CreateElicitationRequest FromJsonElement(JsonElement value) => new() { Value = value };
}

[JsonConverter(typeof(RawJsonValueConverter<CreateElicitationResponse>))]
public partial struct CreateElicitationResponse : IRawJsonValue<CreateElicitationResponse>
{
    public static CreateElicitationResponse FromJsonElement(JsonElement value) => new() { Value = value };
}

[JsonConverter(typeof(RawJsonValueConverter<ElicitationContentValue>))]
public partial struct ElicitationContentValue : IRawJsonValue<ElicitationContentValue>
{
    public static ElicitationContentValue FromJsonElement(JsonElement value) => new() { Value = value };
}

[JsonConverter(typeof(RawJsonValueConverter<ElicitationPropertySchema>))]
public partial struct ElicitationPropertySchema : IRawJsonValue<ElicitationPropertySchema>
{
    public static ElicitationPropertySchema FromJsonElement(JsonElement value) => new() { Value = value };
}

[JsonConverter(typeof(RawJsonValueConverter<ErrorCode>))]
public partial struct ErrorCode : IRawJsonValue<ErrorCode>
{
    public static ErrorCode FromJsonElement(JsonElement value) => new() { Value = value };
}

[JsonConverter(typeof(RawJsonValueConverter<ExtNotification>))]
public partial record struct ExtNotification : IRawJsonValue<ExtNotification>
{
    public static ExtNotification FromJsonElement(JsonElement value) => new() { Value = value };
}

[JsonConverter(typeof(RawJsonValueConverter<ExtRequest>))]
public partial record struct ExtRequest : IRawJsonValue<ExtRequest>
{
    public static ExtRequest FromJsonElement(JsonElement value) => new() { Value = value };
}

[JsonConverter(typeof(RawJsonValueConverter<ExtResponse>))]
public partial record struct ExtResponse : IRawJsonValue<ExtResponse>
{
    public static ExtResponse FromJsonElement(JsonElement value) => new() { Value = value };
}

[JsonConverter(typeof(RawJsonValueConverter<IdleStateUpdate>))]
public partial struct IdleStateUpdate : IRawJsonValue<IdleStateUpdate>
{
    public static IdleStateUpdate FromJsonElement(JsonElement value) => new() { Value = value };
}

[JsonConverter(typeof(RawJsonValueConverter<LlmProtocol>))]
public partial struct LlmProtocol : IRawJsonValue<LlmProtocol>
{
    public static LlmProtocol FromJsonElement(JsonElement value) => new() { Value = value };
}

[JsonConverter(typeof(RawJsonValueConverter<MessageMcpResponse>))]
public partial struct MessageMcpResponse : IRawJsonValue<MessageMcpResponse>
{
    public static MessageMcpResponse FromJsonElement(JsonElement value) => new() { Value = value };
}

[JsonConverter(typeof(RawJsonValueConverter<NoticeSeverity>))]
public partial struct NoticeSeverity : IRawJsonValue<NoticeSeverity>
{
    public static NoticeSeverity FromJsonElement(JsonElement value) => new() { Value = value };
}

[JsonConverter(typeof(RawJsonValueConverter<PermissionOptionKind>))]
public partial struct PermissionOptionKind : IRawJsonValue<PermissionOptionKind>
{
    public static PermissionOptionKind FromJsonElement(JsonElement value) => new() { Value = value };
}

[JsonConverter(typeof(RawJsonValueConverter<PlanEntryPriority>))]
public partial struct PlanEntryPriority : IRawJsonValue<PlanEntryPriority>
{
    public static PlanEntryPriority FromJsonElement(JsonElement value) => new() { Value = value };
}

[JsonConverter(typeof(RawJsonValueConverter<PlanEntryStatus>))]
public partial struct PlanEntryStatus : IRawJsonValue<PlanEntryStatus>
{
    public static PlanEntryStatus FromJsonElement(JsonElement value) => new() { Value = value };
}

[JsonConverter(typeof(RawJsonValueConverter<PlanUpdateContent>))]
public partial struct PlanUpdateContent : IRawJsonValue<PlanUpdateContent>
{
    public static PlanUpdateContent FromJsonElement(JsonElement value) => new() { Value = value };
}

[JsonConverter(typeof(RawJsonValueConverter<ProtocolVersion>))]
public partial record struct ProtocolVersion : IRawJsonValue<ProtocolVersion>
{
    public static ProtocolVersion FromJsonElement(JsonElement value) => new() { Value = value };
}

[JsonConverter(typeof(RawJsonValueConverter<RequestId>))]
public partial struct RequestId : IRawJsonValue<RequestId>
{
    public static RequestId FromJsonElement(JsonElement value) => new() { Value = value };
}

[JsonConverter(typeof(RawJsonValueConverter<RequestPermissionOutcome>))]
public partial struct RequestPermissionOutcome : IRawJsonValue<RequestPermissionOutcome>
{
    public static RequestPermissionOutcome FromJsonElement(JsonElement value) => new() { Value = value };
}

[JsonConverter(typeof(RawJsonValueConverter<Role>))]
public partial struct Role : IRawJsonValue<Role>
{
    public static Role FromJsonElement(JsonElement value) => new() { Value = value };
}

[JsonConverter(typeof(RawJsonValueConverter<SessionConfigOption>))]
public partial struct SessionConfigOption : IRawJsonValue<SessionConfigOption>
{
    public static SessionConfigOption FromJsonElement(JsonElement value) => new() { Value = value };
}

[JsonConverter(typeof(RawJsonValueConverter<SessionConfigOptionCategory>))]
public partial struct SessionConfigOptionCategory : IRawJsonValue<SessionConfigOptionCategory>
{
    public static SessionConfigOptionCategory FromJsonElement(JsonElement value) => new() { Value = value };
}

[JsonConverter(typeof(RawJsonValueConverter<SessionConfigSelectOptions>))]
public partial struct SessionConfigSelectOptions : IRawJsonValue<SessionConfigSelectOptions>
{
    public static SessionConfigSelectOptions FromJsonElement(JsonElement value) => new() { Value = value };
}

[JsonConverter(typeof(RawJsonValueConverter<SessionUpdate>))]
public partial struct SessionUpdate : IRawJsonValue<SessionUpdate>
{
    public static SessionUpdate FromJsonElement(JsonElement value) => new() { Value = value };
}

[JsonConverter(typeof(RawJsonValueConverter<SetSessionConfigOptionRequest>))]
public partial struct SetSessionConfigOptionRequest : IRawJsonValue<SetSessionConfigOptionRequest>
{
    public static SetSessionConfigOptionRequest FromJsonElement(JsonElement value) => new() { Value = value };
}

[JsonConverter(typeof(RawJsonValueConverter<StateUpdate>))]
public partial struct StateUpdate : IRawJsonValue<StateUpdate>
{
    public static StateUpdate FromJsonElement(JsonElement value) => new() { Value = value };
}

[JsonConverter(typeof(RawJsonValueConverter<StopReason>))]
public partial struct StopReason : IRawJsonValue<StopReason>
{
    public static StopReason FromJsonElement(JsonElement value) => new() { Value = value };
}

[JsonConverter(typeof(RawJsonValueConverter<StringFormat>))]
public partial struct StringFormat : IRawJsonValue<StringFormat>
{
    public static StringFormat FromJsonElement(JsonElement value) => new() { Value = value };
}

[JsonConverter(typeof(RawJsonValueConverter<SystemPromptMode>))]
public partial struct SystemPromptMode : IRawJsonValue<SystemPromptMode>
{
    public static SystemPromptMode FromJsonElement(JsonElement value) => new() { Value = value };
}

[JsonConverter(typeof(RawJsonValueConverter<ToolCallContent>))]
public partial struct ToolCallContent : IRawJsonValue<ToolCallContent>
{
    public static ToolCallContent FromJsonElement(JsonElement value) => new() { Value = value };
}

[JsonConverter(typeof(RawJsonValueConverter<ToolCallStatus>))]
public partial struct ToolCallStatus : IRawJsonValue<ToolCallStatus>
{
    public static ToolCallStatus FromJsonElement(JsonElement value) => new() { Value = value };
}

[JsonConverter(typeof(RawJsonValueConverter<ToolKind>))]
public partial struct ToolKind : IRawJsonValue<ToolKind>
{
    public static ToolKind FromJsonElement(JsonElement value) => new() { Value = value };
}
