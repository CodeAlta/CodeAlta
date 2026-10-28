using System.Buffers;
using System.Text.Json;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Plugin.CodexCli;

/// <summary>A turn of a Codex thread, and how much of the conversation of CodeAlta the thread held once it ended.</summary>
/// <param name="Id">The identifier of the turn.</param>
/// <param name="Users">The user messages of the conversation the thread held.</param>
/// <param name="Assistants">The assistant messages of the conversation it held.</param>
internal sealed record CodexCliTurnMark(string Id, int Users, int Assistants);

/// <summary>
/// What a session records in its journal to find its Codex thread again: the thread, how much of the conversation of
/// CodeAlta it holds, the instructions it was given, and its last turns, which a rewind forks from.
/// </summary>
/// <param name="ThreadId">The Codex thread.</param>
/// <param name="Users">The user messages of the conversation the thread was sent.</param>
/// <param name="Assistants">The assistant messages of the conversation it wrote.</param>
/// <param name="Instructions">The hash of the instructions of CodeAlta it was given last, or <see langword="null"/>.</param>
/// <param name="Turns">Its last turns, the oldest first.</param>
internal sealed record CodexCliProviderState(string ThreadId, int Users, int Assistants, string? Instructions, IReadOnlyList<CodexCliTurnMark> Turns)
{
    /// <summary>How many turns are kept: a rewind further back starts a thread told the conversation.</summary>
    public const int MaxTurns = 100;

    private const string Kind = "codex-cli";

    public static CodexCliProviderState? Read(AgentSessionState state) => Read(state.ProviderState);

    public static CodexCliProviderState? Read(JsonElement? providerState)
    {
        if (providerState is not { ValueKind: JsonValueKind.Object } element ||
            !element.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String || !string.Equals(kind.GetString(), Kind, StringComparison.Ordinal) ||
            !element.TryGetProperty("threadId", out var thread) || thread.ValueKind != JsonValueKind.String || thread.GetString() is not { Length: > 0 } threadId)
        {
            return null;
        }

        var turns = new List<CodexCliTurnMark>();
        if (element.TryGetProperty("turns", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var turn in array.EnumerateArray())
            {
                if (String(turn, "id") is { Length: > 0 } id)
                {
                    turns.Add(new CodexCliTurnMark(id, Int(turn, "users"), Int(turn, "assistants")));
                }
            }
        }

        return new CodexCliProviderState(threadId, Int(element, "users"), Int(element, "assistants"), String(element, "instructions"), turns);
    }

    /// <summary>Gets the last turn that ended with the thread holding exactly that much of the conversation, or <see langword="null"/>.</summary>
    public CodexCliTurnMark? FindTurn(int users, int assistants)
        => Turns.LastOrDefault(turn => turn.Users == users && turn.Assistants == assistants);

    public JsonElement ToJson()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("kind", Kind);
            writer.WriteString("threadId", ThreadId);
            writer.WriteNumber("users", Users);
            writer.WriteNumber("assistants", Assistants);
            if (Instructions is not null)
            {
                writer.WriteString("instructions", Instructions);
            }

            writer.WriteStartArray("turns");
            foreach (var turn in Turns.Skip(Math.Max(0, Turns.Count - MaxTurns)))
            {
                writer.WriteStartObject();
                writer.WriteString("id", turn.Id);
                writer.WriteNumber("users", turn.Users);
                writer.WriteNumber("assistants", turn.Assistants);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private static string? String(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int Int(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : 0;
}
