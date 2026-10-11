using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeAlta.Agent;

/// <summary>Validates a current journal event's typed fields without accepting unknown CLR properties.</summary>
/// <remarks>This is shape validation, not retirement authorization. Dynamic details/raw payloads require their owning subsystem's validation.</remarks>
public static class AgentJournalValidation
{
    /// <summary>Rejects unsupported event shapes and incorrectly typed fields instead of silently discarding them.</summary>
    /// <param name="value">One parsed journal event.</param>
    /// <exception cref="JsonException">The event is null, unknown or malformed.</exception>
    /// <exception cref="NotSupportedException">The event uses an unsupported polymorphic shape.</exception>
    public static void ValidateEvent(JsonElement value)
    {
        if (value.Deserialize(StrictAgentJournalJsonContext.Default.AgentEvent) is null)
        {
            throw new JsonException("The journal event is null.");
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(AgentEvent))]
internal sealed partial class StrictAgentJournalJsonContext : JsonSerializerContext;
