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
        var primary = command ?? Text(details, "arguments", "path") ?? Text(details, "path")
            ?? Text(details, "arguments", "query") ?? Text(details, "arguments", "pattern") ?? Text(details, "query") ?? Text(details, "arguments");
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
        var preview = Preview(output, 160);
        cost = 512 + 6 * (preview?.Length ?? 0);
        if (cost > budget) { cost = 0; return null; }
        var (lines, bytes) = MeasureOutput(output);
        return new(null, false, preview, lines, [], bytes);
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

    private static string? Text(JsonElement? value, params string[] path)
        => Value(value, path) is { ValueKind: JsonValueKind.String } text ? text.GetString() : null;

    private static JsonElement? Value(JsonElement? value, params string[] path)
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
internal sealed record HistoryToolSummary(string? Primary, bool IsCommand, string? Output, int OutputLines, HistoryToolField[] Fields, int? OutputBytes,
    int? Added = null, int? Removed = null);
internal sealed record HistoryToolField(string Path, string Text, bool Truncated);
