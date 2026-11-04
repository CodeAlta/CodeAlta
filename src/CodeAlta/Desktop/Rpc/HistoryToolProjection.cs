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
        var command = AgentToolCallProjection.CommandLine(activity);
        var primary = AgentToolCallProjection.Subject(activity);
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
        return new(primary, command is not null, preview, lines, fields.ToArray(), bytes, added, removed, App: AppServer(details));
    }

    // The server of the view of an MCP tool call (MCP Apps), when its result has one: the row offers to show it.
    private static string? AppServer(JsonElement? details)
        => Text(details, "result", "app", "server") is { Length: > 0 and <= 256 } server
           && Text(details, "result", "app", "resourceUri") is { } uri && uri.StartsWith("ui://", StringComparison.OrdinalIgnoreCase)
            ? server : null;

    /// <summary>Longest part of an edit's diff sent with its tool call, in UTF-16 units.</summary>
    internal const int MaximumDiffLength = 16 * 1024;

    internal static HistoryToolSummary? ProjectOutput(string output, int budget, out int cost)
    {
        // The result of a shell command is previewed and measured by what the command wrote, not by its header.
        var shell = ShellCommandResult.Parse(output);
        var written = shell is null ? output : shell.Stdout.Length == 0 ? shell.Stderr : shell.Stderr.Length == 0 ? shell.Stdout : $"{shell.Stdout}\n{shell.Stderr}";
        var preview = Preview(written, 160);
        cost = 512 + 6 * ((preview?.Length ?? 0) + (shell?.SandboxNote?.Length ?? 0));
        if (cost > budget) { cost = 0; return null; }
        var (lines, bytes) = MeasureOutput(written);
        return new(null, false, preview, lines, [], bytes, ExitCode: shell?.ExitCode, Sandbox: shell?.SandboxNote);
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

    internal static string? Text(JsonElement? value, params string[] path) => AgentToolCallProjection.Text(value, path);

    internal static JsonElement? Value(JsonElement? value, params string[] path) => AgentToolCallProjection.Value(value, path);
}

/// <param name="Added">Lines the call added to files, when its record has a diff.</param>
/// <param name="Removed">Lines the call removed from files, when its record has a diff.</param>
/// <param name="ExitCode">The exit code of a shell command, when its result states one.</param>
/// <param name="App">The MCP server whose view (an MCP App) the call has, read whole with <c>mcpApps.open</c>; null for none.</param>
/// <param name="Sandbox">The note the sandbox of the session added after a shell command (<c>[sandbox: profile] …</c>); null for none.</param>
internal sealed record HistoryToolSummary(string? Primary, bool IsCommand, string? Output, int OutputLines, HistoryToolField[] Fields, int? OutputBytes,
    int? Added = null, int? Removed = null, int? ExitCode = null, string? App = null, string? Sandbox = null);
internal sealed record HistoryToolField(string Path, string Text, bool Truncated);

/// <summary>
/// The result of the <c>shell_command</c> tool as its text states it: <c>exit_code</c>, <c>working_directory</c>,
/// then what the command wrote to its standard output and to its standard error.
/// </summary>
/// <param name="ExitCode">The exit code of the command.</param>
/// <param name="WorkingDirectory">The folder the command ran in.</param>
/// <param name="Stdout">What the command wrote to its standard output; empty when it wrote nothing.</param>
/// <param name="Stderr">What the command wrote to its standard error; empty when it wrote nothing.</param>
/// <param name="SandboxNote">The line the sandbox of the session added after the result (<c>[sandbox: profile] …</c>); null for none.</param>
internal sealed record ShellCommandResult(int ExitCode, string WorkingDirectory, string Stdout, string Stderr, string? SandboxNote = null)
{
    private const string Empty = "(empty)";
    private const string SandboxPrefix = "\n[sandbox: ";

    /// <summary>Reads the text of a result, or returns null when it does not have that form.</summary>
    internal static ShellCommandResult? Parse(string? text)
    {
        if (text is null || !text.StartsWith("exit_code: ", StringComparison.Ordinal)) return null;
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        // The note of a sandbox is the last line, after the standard error: it is not something the command wrote.
        string? note = null;
        var end = normalized.TrimEnd('\n');
        var last = end.LastIndexOf('\n');
        if (last >= 0 && string.CompareOrdinal(end, last, SandboxPrefix, 0, SandboxPrefix.Length) == 0 && end.IndexOf(']', last) > last)
        {
            note = end[(last + 1)..];
            normalized = end[..last];
        }

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
        return new(exitCode, normalized[(first + 1 + directory.Length)..second], output == Empty ? "" : output, error == Empty ? "" : error, note);
    }
}
