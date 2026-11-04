using System.Text;
using System.Text.Json;

namespace CodeAlta.Agent;

/// <summary>What a tool call's records say, whole: the texts are not cut here.</summary>
/// <param name="Name">The name of the tool, when the record has one.</param>
/// <param name="Message">The message of the record, when it has one.</param>
/// <param name="Command">The command line, for a call that runs one.</param>
/// <param name="WorkingDirectory">The folder the call asked to run in, when its arguments name one.</param>
/// <param name="ExitCode">The exit code, when the record has one as a number.</param>
/// <param name="Error">The error the call reported, when it failed with one.</param>
/// <param name="Arguments">The arguments, as their JSON text or as the string the provider gave.</param>
/// <param name="Output">The text of the result.</param>
/// <param name="Diff">The unified diff of the edits the call made.</param>
/// <param name="ReadFiles">The files the call read, at most <see cref="AgentToolCallProjection.MaximumPaths"/>.</param>
/// <param name="ModifiedFiles">The files the call modified, at most <see cref="AgentToolCallProjection.MaximumPaths"/>.</param>
public sealed record AgentToolCallParts(string? Name, string? Message, string? Command, string? WorkingDirectory, int? ExitCode, string? Error,
    string? Arguments, string? Output, string? Diff, string[] ReadFiles, string[] ModifiedFiles);

/// <summary>Reads a tool call from the records providers write, with the shapes the terminal UI reads.</summary>
public static class AgentToolCallProjection
{
    /// <summary>Most paths listed for the files a call read or modified.</summary>
    public const int MaximumPaths = 64;

    /// <summary>Projects the activity record of a call and, when the timeline attached one, its output record.</summary>
    /// <param name="activity">The newest activity record of the call.</param>
    /// <param name="output">The completed output record of the call, when there is one.</param>
    /// <returns>What the records say.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="activity"/> is null.</exception>
    public static AgentToolCallParts Project(AgentActivityEvent activity, AgentContentCompletedEvent? output)
    {
        ArgumentNullException.ThrowIfNull(activity);
        var details = activity.Details;
        var name = activity.Name is { Length: > 0 } given ? given
            : Text(details, "toolName") ?? Text(details, "mcpToolName") ?? Text(details, "tool") ?? Text(details, "name");
        var command = Text(details, "command") ?? Text(details, "arguments", "command") ?? Text(details, "input", "command")
            ?? (activity.Kind == AgentActivityKind.CommandExecution ? activity.Name : null);
        var directory = Text(details, "cwd") ?? Text(details, "arguments", "workdir") ?? Text(details, "arguments", "cwd") ?? Text(details, "input", "cwd");
        var ended = activity.Phase is AgentActivityPhase.Completed or AgentActivityPhase.Failed or AgentActivityPhase.Canceled;
        var text = output?.Content ?? Text(details, "aggregatedOutput") ?? ResultItems(details) ?? Text(details, "result", "content")
            ?? Text(details, "error", "message") ?? Text(details, "output", "body") ?? Text(details, "result", "detailedContent")
            ?? Text(details, "output") ?? Text(details, "result") ?? (ended ? activity.Message : null);
        var diff = Text(details, "diff") ?? Text(details, "result", "diff") ?? Text(details, "output", "diff")
            ?? Text(output?.Details, "diff");
        var error = Text(details, "result", "error") ?? Text(details, "error", "message")
            ?? (activity.Phase == AgentActivityPhase.Failed ? activity.Message : null);
        return new(name, activity.Message, command, directory,
            Number(details, "exitCode") ?? Number(details, "exit_code") ?? Number(details, "result", "exitCode"), error,
            Json(details, "arguments") ?? Json(details, "input"), text, diff, Paths(details, "readFiles"), Paths(details, "modifiedFiles"));
    }

    /// <summary>The command line of a call that runs one, as a person reads it; null for any other call.</summary>
    /// <param name="activity">An activity record of the call.</param>
    /// <returns>The command line, or null.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="activity"/> is null.</exception>
    public static string? CommandLine(AgentActivityEvent activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        var details = activity.Details;
        var command = Text(details, "command") ?? Text(details, "arguments", "command") ?? Text(details, "input", "command");
        command ??= activity.Kind == AgentActivityKind.CommandExecution ? activity.Name : null;
        // The `alta` tool takes its command line as an array: it is shown as the command it is.
        return command ?? AltaCommand(activity.Name, details);
    }

    /// <summary>What a call works on, as its row shows it: its command, the file or the query it names, or its arguments on one line.</summary>
    /// <param name="activity">An activity record of the call.</param>
    /// <returns>The text, not cut; null when the record names nothing.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="activity"/> is null.</exception>
    public static string? Subject(AgentActivityEvent activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        var details = activity.Details;
        return CommandLine(activity) ?? Text(details, "arguments", "path") ?? Text(details, "path")
            ?? Text(details, "arguments", "query") ?? Text(details, "arguments", "pattern") ?? Text(details, "query")
            ?? Text(details, "arguments", "url") ?? Text(details, "arguments") ?? Files(details) ?? Arguments(Value(details, "arguments"));
    }

    /// <summary>The string at a path of a JSON value, which may hold its objects as JSON text.</summary>
    /// <param name="value">The value.</param>
    /// <param name="path">The names of the properties to follow.</param>
    /// <returns>The string, or null when the path leads to no string.</returns>
    public static string? Text(JsonElement? value, params string[] path)
        => Value(value, path) is { ValueKind: JsonValueKind.String } text ? text.GetString() : null;

    /// <summary>The value at a path of a JSON value, which may hold its objects as JSON text.</summary>
    /// <param name="value">The value.</param>
    /// <param name="path">The names of the properties to follow.</param>
    /// <returns>The value, or null when the path leads nowhere.</returns>
    public static JsonElement? Value(JsonElement? value, params string[] path)
    {
        if (value is not { } current) return null;
        foreach (var part in path)
        {
            if (current.ValueKind == JsonValueKind.String && current.GetString() is { Length: <= 32768 } json)
            {
                try { using var parsed = JsonDocument.Parse(json); current = parsed.RootElement.Clone(); }
                catch (JsonException) { return null; }
            }
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out current)) return null;
        }
        return current;
    }

    // `alta session list --json` for the arguments {"args": ["session", "list", "--json"]}.
    private static string? AltaCommand(string? name, JsonElement? details)
    {
        if (!string.Equals(name, "alta", StringComparison.Ordinal) || Value(details, "arguments", "args") is not { ValueKind: JsonValueKind.Array } args) return null;
        var line = new StringBuilder("alta");
        foreach (var argument in args.EnumerateArray())
        {
            if (argument.ValueKind != JsonValueKind.String || line.Length > 512) return null;
            var text = argument.GetString()!;
            line.Append(' ').Append(text.Length > 0 && !text.Any(static character => char.IsWhiteSpace(character) || character is '"' or '\'')
                ? text : string.Concat("\"", text.Replace("\"", "\\\"", StringComparison.Ordinal), "\""));
        }
        return line.ToString();
    }

    // The files a call changed or read, when it names them: the path of a single one, else the name of the
    // first one and how many follow.
    private static string? Files(JsonElement? details)
    {
        foreach (var property in new[] { "modifiedFiles", "readFiles" })
        {
            if (Value(details, property) is not { ValueKind: JsonValueKind.Array } files || files.GetArrayLength() == 0) continue;
            if (files[0].ValueKind != JsonValueKind.String || files[0].GetString() is not { Length: > 0 } first) continue;
            if (files.GetArrayLength() == 1) return first;
            var name = first[(first.LastIndexOfAny(['/', '\\']) + 1)..];
            return $"{(name.Length == 0 ? first : name)} (+{files.GetArrayLength() - 1})";
        }
        return null;
    }

    // The arguments of a call that names no command, path or query, on one line: `uid: 1_25, dblClick: false`.
    private static string? Arguments(JsonElement? arguments)
    {
        if (arguments is not { ValueKind: JsonValueKind.Object } value) return null;
        var line = new StringBuilder();
        foreach (var property in value.EnumerateObject())
        {
            var text = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => property.Value.GetRawText(),
                _ => null,
            };
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (line.Length > 0) line.Append(", ");
            line.Append(property.Name).Append(": ").Append(text.Length > 96 ? string.Concat(text.AsSpan(0, char.IsHighSurrogate(text[95]) ? 95 : 96), "…") : text);
            if (line.Length > 256) break;
        }
        return line.Length == 0 ? null : line.ToString();
    }

    // The result of a tool of the CodeAlta runtime: its text items, in order.
    private static string? ResultItems(JsonElement? details)
    {
        if (Value(details, "result", "items") is not { ValueKind: JsonValueKind.Array } items) return null;
        StringBuilder? text = null;
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.String) continue;
            if (text is null) text = new();
            else text.Append('\n');
            text.Append(value.GetString());
        }
        return text?.ToString();
    }

    private static string? Json(JsonElement? details, string property)
        => Value(details, property) is { } value && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText() : null;

    private static int? Number(JsonElement? details, params string[] path)
        => Value(details, path) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt32(out var number) ? number : null;

    private static string[] Paths(JsonElement? details, string property)
    {
        if (Value(details, property) is not { ValueKind: JsonValueKind.Array } values) return [];
        var paths = new List<string>();
        foreach (var value in values.EnumerateArray())
        {
            if (paths.Count == MaximumPaths) break;
            if (value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 and <= 1024 } path) paths.Add(path);
        }
        return paths.ToArray();
    }
}
