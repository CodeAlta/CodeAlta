using System.Text;
using System.Text.Json;
using CodeAlta.Agent;

namespace CodeAlta.Desktop.Rpc;

internal static class HistoryToolProjection
{
    internal static HistoryToolSummary Project(AgentActivityEvent activity)
        => Project(activity, 64 * 1024, out _)!;

    internal static HistoryToolSummary? Project(AgentActivityEvent activity, int budget, out int cost)
    {
        var details = activity.Details;
        var command = Text(details, "command") ?? Text(details, "arguments", "command") ?? Text(details, "input", "command");
        command ??= activity.Kind == AgentActivityKind.CommandExecution ? activity.Name : null;
        // The `alta` tool takes its command line as an array: it is shown as the command it is.
        command ??= AltaCommand(activity.Name, details);
        var primary = command ?? Text(details, "arguments", "path") ?? Text(details, "path")
            ?? Text(details, "arguments", "query") ?? Text(details, "arguments", "pattern") ?? Text(details, "query")
            ?? Text(details, "arguments", "url") ?? Text(details, "arguments") ?? Files(details) ?? Arguments(Value(details, "arguments"));
        var output = Text(details, "aggregatedOutput") ?? Text(details, "result", "content")
            ?? Text(details, "error", "message") ?? Text(details, "output", "body") ?? Text(details, "result", "detailedContent")
            ?? Text(details, "output") ?? Text(details, "result");
        output ??= activity.Phase is AgentActivityPhase.Completed or AgentActivityPhase.Failed ? activity.Message : null;
        var (lines, bytes) = MeasureOutput(output);
        primary = Preview(primary, 256);
        var preview = Preview(output, 160);
        cost = 512 + 6 * ((primary?.Length ?? 0) + (preview?.Length ?? 0));
        if (cost > budget) { cost = 0; return null; }
        var fields = new List<HistoryToolField>();
        // The diff an edit left behind (write_file, apply_patch, ...): its size is counted whole, like the TUI does,
        // and its text is shown first, within a budget of its own.
        var diff = Text(details, "diff") ?? Text(details, "result", "diff") ?? Text(details, "output", "diff");
        var (added, removed) = HistoryFileProjection.CountLines(diff);
        if (diff is not null && added is not null)
        {
            var limit = Math.Max(0, Math.Min(MaximumDiffLength, (budget - cost - 256) / 6));
            var length = Math.Min(limit, diff.Length);
            // Cut at the end of a line, so that no half line is colored as an addition or a removal.
            if (length < diff.Length) length = diff.LastIndexOf('\n', Math.Max(0, length - 1)) + 1;
            if (length > 0)
            {
                fields.Add(new("diff", diff[..length], length < diff.Length));
                cost += 256 + 6 * length;
            }
        }

        var characters = 8192;
        foreach (var path in new[] { "arguments", "input", "command", "aggregatedOutput", "result.content", "result.detailedContent", "output.body", "error.message", "output", "result" })
        {
            var value = Value(details, path.Split('.'));
            if (value is null || value.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                || (path is "output" or "result") && value.Value.ValueKind != JsonValueKind.String) continue;
            var text = value.Value.ValueKind == JsonValueKind.String ? value.Value.GetString()! : value.Value.GetRawText();
            if (string.IsNullOrEmpty(text)) continue;
            var limit = Math.Max(0, Math.Min(characters, (budget - cost - 256) / 6));
            if (limit == 0) break;
            var length = Math.Min(limit, text.Length);
            if (length < text.Length && char.IsHighSurrogate(text[length - 1])) length--;
            fields.Add(new(path, text[..length], length < text.Length));
            characters -= length;
            cost += 256 + 6 * length;
        }
        return new(primary, command is not null, preview, lines, fields.ToArray(), bytes, added, removed);
    }

    /// <summary>Longest part of an edit's diff sent with its tool call, in UTF-16 units.</summary>
    internal const int MaximumDiffLength = 16 * 1024;

    internal static HistoryToolSummary? ProjectOutput(string output, int budget, out int cost)
    {
        // The result of a shell command is previewed and measured by what the command wrote, not by its header.
        var shell = ShellCommandResult.Parse(output);
        var written = shell is null ? output : shell.Stdout.Length == 0 ? shell.Stderr : shell.Stderr.Length == 0 ? shell.Stdout : $"{shell.Stdout}\n{shell.Stderr}";
        var preview = Preview(written, 160);
        cost = 512 + 6 * (preview?.Length ?? 0);
        if (cost > budget) { cost = 0; return null; }
        var (lines, bytes) = MeasureOutput(written);
        return new(null, false, preview, lines, [], bytes, ExitCode: shell?.ExitCode);
    }

    private static (int Lines, int? Bytes) MeasureOutput(string? output)
    {
        if (output is null) return (0, null);
        // Match the TUI output buffer: CRLF/CR become LF; trailing blank lines do
        // not increase the line count, but all normalized bytes are accounted for.
        var normalized = output.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var body = normalized.AsSpan().TrimEnd('\n');
        var lines = body.IsEmpty ? 0 : body.Count('\n') + 1;
        return (lines, Encoding.UTF8.GetByteCount(normalized));
    }

    private static string? Preview(string? value, int limit)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        foreach (var line in value.AsSpan().EnumerateLines())
        {
            var text = line.Trim();
            if (text.IsEmpty) continue;
            if (text.Length <= limit) return text.ToString();
            return string.Concat(text[..(char.IsHighSurrogate(text[limit - 1]) ? limit - 1 : limit)], "…");
        }
        return null;
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

    internal static string? Text(JsonElement? value, params string[] path)
        => Value(value, path) is { ValueKind: JsonValueKind.String } text ? text.GetString() : null;

    internal static JsonElement? Value(JsonElement? value, params string[] path)
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
}

/// <param name="Added">Lines the call added to files, when its record has a diff.</param>
/// <param name="Removed">Lines the call removed from files, when its record has a diff.</param>
/// <param name="ExitCode">The exit code of a shell command, when its result states one.</param>
internal sealed record HistoryToolSummary(string? Primary, bool IsCommand, string? Output, int OutputLines, HistoryToolField[] Fields, int? OutputBytes,
    int? Added = null, int? Removed = null, int? ExitCode = null);
internal sealed record HistoryToolField(string Path, string Text, bool Truncated);

/// <summary>
/// The result of the <c>shell_command</c> tool as its text states it: <c>exit_code</c>, <c>working_directory</c>,
/// then what the command wrote to its standard output and to its standard error.
/// </summary>
/// <param name="ExitCode">The exit code of the command.</param>
/// <param name="WorkingDirectory">The folder the command ran in.</param>
/// <param name="Stdout">What the command wrote to its standard output; empty when it wrote nothing.</param>
/// <param name="Stderr">What the command wrote to its standard error; empty when it wrote nothing.</param>
internal sealed record ShellCommandResult(int ExitCode, string WorkingDirectory, string Stdout, string Stderr)
{
    private const string Empty = "(empty)";

    /// <summary>Reads the text of a result, or returns null when it does not have that form.</summary>
    internal static ShellCommandResult? Parse(string? text)
    {
        if (text is null || !text.StartsWith("exit_code: ", StringComparison.Ordinal)) return null;
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        var first = normalized.IndexOf('\n');
        if (first < 0 || !int.TryParse(normalized.AsSpan(11, first - 11), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var exitCode)) return null;
        var second = normalized.IndexOf('\n', first + 1);
        const string directory = "working_directory: ", standardOutput = "stdout:\n", standardError = "\nstderr:\n";
        if (second < 0 || string.CompareOrdinal(normalized, first + 1, directory, 0, directory.Length) != 0
            || string.CompareOrdinal(normalized, second + 1, standardOutput, 0, standardOutput.Length) != 0) return null;
        var body = second + 1 + standardOutput.Length;
        // The last such line starts the standard error: a command that prints this line itself is not told apart.
        var split = normalized.LastIndexOf(standardError, StringComparison.Ordinal);
        if (split < body - 1) return null;
        var output = normalized[body..Math.Max(body, split)];
        var error = normalized[(split + standardError.Length)..];
        return new(exitCode, normalized[(first + 1 + directory.Length)..second], output == Empty ? "" : output, error == Empty ? "" : error);
    }
}
