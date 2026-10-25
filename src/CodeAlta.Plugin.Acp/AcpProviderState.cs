using System.Text.Json;
using CodeAlta.Acp;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Plugin.Acp;

/// <summary>
/// What a session records in its journal to find the conversation of the agent again: the session of the agent, and
/// how much of the conversation of CodeAlta it holds.
/// </summary>
/// <param name="SessionId">The session of the agent.</param>
/// <param name="Users">The user messages of the conversation the agent was sent.</param>
/// <param name="Assistants">The assistant messages of the conversation the agent wrote.</param>
/// <param name="Instructions">The hash of the instructions of CodeAlta the agent was given last, or <see langword="null"/>.</param>
/// <param name="Delivery">How the instructions were given: <c>acp-system-prompt</c> or <c>first-prompt</c>.</param>
internal sealed record AcpProviderState(string SessionId, int Users, int Assistants, string? Instructions, string? Delivery)
{
    private const string Kind = "acp";

    public static AcpProviderState? Read(AgentSessionState state) => Read(state.ProviderState);

    public static AcpProviderState? Read(JsonElement? providerState)
    {
        if (providerState is not { ValueKind: JsonValueKind.Object } element ||
            !string.Equals(AcpJson.GetString(element, "kind"), Kind, StringComparison.Ordinal) ||
            AcpJson.GetString(element, "sessionId") is not { Length: > 0 } sessionId)
        {
            return null;
        }

        return new AcpProviderState(
            sessionId,
            (int)(AcpJson.GetInt64(element, "users") ?? 0),
            (int)(AcpJson.GetInt64(element, "assistants") ?? 0),
            AcpJson.GetString(element, "instructions"),
            AcpJson.GetString(element, "delivery"));
    }

    public JsonElement ToJson()
        => AcpJson.Build(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("kind", Kind);
            writer.WriteString("sessionId", SessionId);
            writer.WriteNumber("users", Users);
            writer.WriteNumber("assistants", Assistants);
            if (Instructions is not null)
            {
                writer.WriteString("instructions", Instructions);
            }

            if (Delivery is not null)
            {
                writer.WriteString("delivery", Delivery);
            }

            writer.WriteEndObject();
        });
}
