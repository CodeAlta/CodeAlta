using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeAlta.CodeMode;

/// <summary>How the value a script returned reads.</summary>
public enum CodeModeResultKind
{
    /// <summary>A text, read as Markdown: what a script returns for a person to read.</summary>
    Markdown,

    /// <summary>An object or an array, written as JSON.</summary>
    Json,

    /// <summary>A number, a boolean or null, written as JSON.</summary>
    Scalar,
}

/// <summary>A long text of lines inside a JSON result, such as the <c>stdout</c> of a command, kept in a file of its own.</summary>
/// <param name="Path">Where it is in the result: the names of the properties and the indexes of the items, from the root.</param>
/// <param name="Text">The text.</param>
public sealed record CodeModeResultTextPart(IReadOnlyList<string> Path, string Text)
{
    /// <summary>Gets how the result header names it: <c>stdout</c>, <c>runs[2].stdout</c>.</summary>
    public string DisplayName => string.Concat(Path.Select(static (segment, index) =>
        segment.Length > 0 && segment.All(char.IsAsciiDigit) ? "[" + segment + "]" : (index == 0 ? segment : "." + segment)));
}

/// <summary>The value a script returned as it is read: its text and how it reads.</summary>
/// <param name="Text">The text.</param>
/// <param name="Kind">How it reads.</param>
public sealed record CodeModeResultText(string Text, CodeModeResultKind Kind)
{
    /// <summary>Gets the extension of a file that keeps the whole of it: <c>.md</c> or <c>.json</c>.</summary>
    public string FileExtension => Kind == CodeModeResultKind.Markdown ? ".md" : ".json";
}

/// <summary>
/// Writes what a run did as the text the model reads, which the user is shown too: a header with the status, the time
/// and the calls; for a failure the source line and the calls already made, which are not undone; the output; and the
/// result last. A text the script returns is written as it is, Markdown; a longer result than the budget is a preview of
/// its start and its end, and names the file beside the session that keeps the whole of it.
/// </summary>
public static class CodeModeResultFormatter
{
    /// <summary>The output budget of a result, in tokens, when the script sets none.</summary>
    public const int DefaultMaxOutputTokens = 10_000;

    /// <summary>The line that names the file that keeps the whole of a result, before the result: <c>Full result: &lt;path&gt; (…)</c>.</summary>
    public const string FullResultPrefix = "Full result: ";

    /// <summary>
    /// The characters of the preview of a result kept whole in a file: about 2,000 of its start and 1,000 of its end, at
    /// whole lines.
    /// </summary>
    public const int PreviewCharacters = 3_000;

    private const int CharactersPerToken = 4;

    private static readonly JsonSerializerOptions s_relaxedJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly JsonSerializerOptions s_indentedJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = true };

    /// <summary>Reads the JSON of the value a script returned as the text the result shows.</summary>
    /// <param name="valueJson">The JSON of the value.</param>
    /// <returns>
    /// A string as it is; an array of texts of several lines, such as the answers of sub-agents, as Markdown sections
    /// separated by rules; any other value as its JSON.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="valueJson"/> is null.</exception>
    public static CodeModeResultText Present(string valueJson)
    {
        ArgumentNullException.ThrowIfNull(valueJson);
        try
        {
            using var document = JsonDocument.Parse(valueJson);
            var root = document.RootElement;
            switch (root.ValueKind)
            {
                case JsonValueKind.String:
                    return new CodeModeResultText(root.GetString()!, CodeModeResultKind.Markdown);
                case JsonValueKind.Array when root.GetArrayLength() > 0
                    && root.EnumerateArray().All(static item => item.ValueKind == JsonValueKind.String)
                    && root.EnumerateArray().Any(static item => item.GetString()!.Contains('\n')):
                    return new CodeModeResultText(string.Join("\n\n---\n\n", root.EnumerateArray().Select(static item => item.GetString()!.Trim())), CodeModeResultKind.Markdown);
                case JsonValueKind.Object or JsonValueKind.Array:
                    // A backtick or an accent reads as itself, not as an escape of six characters.
                    return new CodeModeResultText(valueJson.Contains("\\u", StringComparison.Ordinal)
                        ? JsonNode.Parse(valueJson)!.ToJsonString(s_relaxedJson) : valueJson, CodeModeResultKind.Json);
                default:
                    return new CodeModeResultText(valueJson, CodeModeResultKind.Scalar);
            }
        }
        catch (JsonException)
        {
            return new CodeModeResultText(valueJson, CodeModeResultKind.Scalar);
        }
    }

    /// <summary>Gives the result of a run to keep whole in a file, when it is longer than what its text gives.</summary>
    /// <param name="result">The result of the run.</param>
    /// <param name="maxOutputTokens">The output budget, in tokens.</param>
    /// <returns>The result, or null when the text gives all of it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> is null.</exception>
    public static CodeModeResultText? ResultToKeep(CodeModeRunResult result, int maxOutputTokens)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.ValueJson is not { } value)
        {
            return null;
        }

        var text = Present(value);
        return Shown(text).Length > ResultBudget(result, Budget(maxOutputTokens)) ? Kept(text) : null;
    }

    // The text of the file that keeps a whole result: JSON indented, so that it reads by line (read_file, grep) as the
    // header of the result counts it.
    private static CodeModeResultText Kept(CodeModeResultText text)
    {
        if (text.Kind != CodeModeResultKind.Json)
        {
            return text;
        }

        try
        {
            return text with { Text = JsonNode.Parse(text.Text)!.ToJsonString(s_indentedJson) };
        }
        catch (JsonException)
        {
            return text;
        }
    }

    private static int LineCount(string text) => text.Length == 0 ? 0 : 1 + text.AsSpan(0, text[^1] == '\n' ? text.Length - 1 : text.Length).Count('\n');

    /// <summary>
    /// Gives the long texts of lines a JSON result holds, such as the <c>stdout</c> of a command: a JSON file keeps each on
    /// one line, so each is also kept in a text file of its own (<see cref="TextPartPath"/>), which reads by line.
    /// </summary>
    /// <param name="kept">The result kept whole (<see cref="ResultToKeep"/>).</param>
    /// <returns>The texts, the longest first, at most three; none for a result that is not JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="kept"/> is null.</exception>
    public static IReadOnlyList<CodeModeResultTextPart> TextParts(CodeModeResultText kept)
    {
        ArgumentNullException.ThrowIfNull(kept);
        if (kept.Kind != CodeModeResultKind.Json)
        {
            return [];
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(kept.Text);
        }
        catch (JsonException)
        {
            return [];
        }

        var parts = new List<CodeModeResultTextPart>();
        Visit(root, []);
        return parts.OrderByDescending(static part => part.Text.Length).Take(MaxTextParts).ToList();

        void Visit(JsonNode? node, List<string> path)
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach (var (key, child) in obj)
                    {
                        Visit(child, [.. path, key]);
                    }

                    break;
                case JsonArray array:
                    for (var index = 0; index < array.Count; index++)
                    {
                        Visit(array[index], [.. path, index.ToString(CultureInfo.InvariantCulture)]);
                    }

                    break;
                case JsonValue value when value.TryGetValue<string>(out var text) && text.Length >= TextPartMinCharacters && LineCount(text) >= TextPartMinLines:
                    parts.Add(new CodeModeResultTextPart(path.ToArray(), text));
                    break;
            }
        }
    }

    /// <summary>Gives the file that keeps a long text of a JSON result, beside the file of the whole result.</summary>
    /// <param name="fullResultPath">The file of the whole result.</param>
    /// <param name="part">The text.</param>
    /// <returns>The path: the one of the result, with <c>.&lt;name&gt;.txt</c> in the place of its extension.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fullResultPath"/> or <paramref name="part"/> is null.</exception>
    public static string TextPartPath(string fullResultPath, CodeModeResultTextPart part)
    {
        ArgumentNullException.ThrowIfNull(fullResultPath);
        ArgumentNullException.ThrowIfNull(part);
        var name = string.Concat(string.Join('.', part.Path).Select(static c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_'));
        if (name.Length > 60)
        {
            name = name[..60];
        }

        return Path.ChangeExtension(fullResultPath, null) + "." + (name.Length == 0 ? "text" : name) + ".txt";
    }

    private const int MaxTextParts = 3;

    private const int TextPartMinCharacters = 2_000;

    private const int TextPartMinLines = 20;

    /// <summary>Formats a finished run.</summary>
    /// <param name="result">The result of the run.</param>
    /// <param name="maxOutputTokens">The output budget, in tokens.</param>
    /// <returns>The text for the model.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> is null.</exception>
    public static string Format(CodeModeRunResult result, int maxOutputTokens = DefaultMaxOutputTokens)
        => Format(result, maxOutputTokens, null);

    /// <summary>Formats a finished run whose whole result may be kept in a file (<see cref="ResultToKeep"/>).</summary>
    /// <param name="result">The result of the run.</param>
    /// <param name="maxOutputTokens">The output budget, in tokens.</param>
    /// <param name="fullResultPath">The file that keeps the whole result, or null when none does.</param>
    /// <returns>The text for the model.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> is null.</exception>
    public static string Format(CodeModeRunResult result, int maxOutputTokens, string? fullResultPath)
        => Format(result, maxOutputTokens, fullResultPath, []);

    /// <summary>Formats a finished run, with lines of the host before its result, such as the pictures it attached.</summary>
    /// <param name="result">The result of the run.</param>
    /// <param name="maxOutputTokens">The output budget, in tokens.</param>
    /// <param name="fullResultPath">The file that keeps the whole result, or null when none does.</param>
    /// <param name="notes">The lines of the host, in their order; each is written on a line of its own.</param>
    /// <returns>The text for the model.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> or <paramref name="notes"/> is null.</exception>
    public static string Format(CodeModeRunResult result, int maxOutputTokens, string? fullResultPath, IReadOnlyList<string> notes)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(notes);
        var budget = Budget(maxOutputTokens);
        var builder = new StringBuilder();
        var status = result.Status switch
        {
            CodeModeRunStatus.Completed => "Script completed",
            CodeModeRunStatus.Cancelled => "Script stopped",
            _ when result.Error?.Location is { } location => $"Script failed at line {location.Line}:{location.Column}",
            _ => "Script failed",
        };
        builder.Append(status).Append(" · ").Append(Duration(result.Duration)).Append(" · ").Append(CallsSummary(result)).Append('\n');

        if (result.Error is { } error && result.Status != CodeModeRunStatus.Completed)
        {
            builder.Append(error.Name).Append(": ").Append(error.Message).Append('\n');
            if (error.Location is { } location && result.Source?.GetLine(location.Line) is { } line)
            {
                builder.Append(location.Line.ToString(CultureInfo.InvariantCulture).PadLeft(4)).Append(" | ").Append(line.TrimEnd()).Append('\n');
            }

            var made = result.Calls.Where(static call => call.Status is CodeModeCallStatus.Succeeded or CodeModeCallStatus.Failed).ToList();
            if (made.Count > 0)
            {
                builder.Append("Calls made before the failure (not undone): ").Append(GroupCalls(made)).Append('\n');
            }
        }

        if (result.Output.Count > 0)
        {
            builder.Append("Output:\n");
            AppendOutput(builder, result, result.ValueJson is null ? budget : budget / 3);
        }

        var notAwaited = result.Calls.Where(static call => call.Status == CodeModeCallStatus.Cancelled && call.ErrorMessage?.StartsWith("Not awaited", StringComparison.Ordinal) == true).ToList();
        if (notAwaited.Count > 0)
        {
            builder.Append("Not awaited, cancelled when the script ended: ").Append(GroupCalls(notAwaited)).Append(". Await every call whose result matters.\n");
        }

        var refused = result.Calls.Count(static call => call.Status == CodeModeCallStatus.Refused);
        if (refused > 0)
        {
            builder.Append(refused.ToString(CultureInfo.InvariantCulture)).Append(" calls were refused: the run reached its limit of calls.\n");
        }

        if (result.Calls.Count > 0 && result.Duration >= TimeSpan.FromSeconds(1))
        {
            builder.Append(Profile(result)).Append('\n');
        }

        foreach (var note in notes)
        {
            builder.Append(note).Append('\n');
        }

        // The result comes last: everything after its line is the value, which the window shows as Markdown.
        if (result.ValueJson is { } value)
        {
            AppendResult(builder, Present(value), ResultBudget(result, budget), fullResultPath);
        }

        return builder.ToString().TrimEnd() + "\n";
    }

    private static int Budget(int maxOutputTokens) => Math.Max(1_000, maxOutputTokens * CharactersPerToken);

    // The result has the budget the output leaves it: the output takes at most a third.
    private static int ResultBudget(CodeModeRunResult result, int budget)
        => result.Output.Count > 0 ? budget - Math.Min(budget / 3, OutputLength(result)) : budget;

    // The text of a value as the result writes it whole: an object or an array in a block of JSON.
    private static string Shown(CodeModeResultText text) => text.Kind == CodeModeResultKind.Json ? "```json\n" + text.Text + "\n```" : text.Text;

    private static void AppendResult(StringBuilder builder, CodeModeResultText text, int budget, string? fullResultPath)
    {
        var shown = Shown(text);
        if (shown.Length <= budget)
        {
            builder.Append("Result:\n").Append(shown).Append('\n');
            return;
        }

        // A result kept whole in a file gives a preview of its start and its end, as Claude Code does with a long output, and
        // says which lines of the file it shows: the session reads only the others. Without a file, the excerpt has the
        // whole budget.
        if (fullResultPath is not null)
        {
            budget = Math.Min(budget, PreviewCharacters);
        }

        if (text.Kind == CodeModeResultKind.Markdown)
        {
            var excerpt = CodeModeMarkdownExcerpt.Cut(text.Text, budget, fullResultPath is null ? null : "they are in the full result", out var lines);
            if (fullResultPath is not null)
            {
                builder.Append(FullResultPrefix).Append(fullResultPath).Append(" (")
                    .Append(Count(lines.TotalLines, "line")).Append(", ").Append(Count(text.Text.Length, "character"))
                    .Append("; shown below: ").Append(ShownLines(lines)).Append(")\n");
            }

            builder.Append("Result:\n").Append(excerpt).Append('\n');
        }
        else
        {
            // JSON keeps its shape: the longest values lose their end, and the model still reads valid JSON.
            var json = CodeModeJsonTruncation.Truncate(text.Text, Math.Max(200, budget - 12));
            if (fullResultPath is not null)
            {
                var kept = Kept(text);
                builder.Append(FullResultPrefix).Append(fullResultPath).Append(" (")
                    .Append(Count(LineCount(kept.Text), "line")).Append(" of indented JSON, ").Append(Count(kept.Text.Length, "character"))
                    .Append("; the JSON below is cut inside: read_file or grep the file for the rest");
                // A long text of lines is on one line of the JSON: its own file reads by line, from the first line not shown.
                foreach (var part in TextParts(kept))
                {
                    builder.Append("; `").Append(part.DisplayName).Append("` (").Append(Count(LineCount(part.Text), "line"));
                    if (ShownLinesOf(json, part.Path) is { } shownLines)
                    {
                        builder.Append(shownLines == 0 ? "; none shown" : "; lines 1–" + shownLines.ToString("N0", CultureInfo.InvariantCulture) + " shown");
                    }

                    builder.Append(") is in ").Append(TextPartPath(fullResultPath, part));
                }

                builder.Append(")\n");
            }

            builder.Append("Result:\n").Append(text.Kind == CodeModeResultKind.Json ? "```json\n" + json + "\n```" : json).Append('\n');
        }
    }

    // The whole lines of a text that the cut JSON still shows, or null when the cut JSON no longer has it.
    private static int? ShownLinesOf(string json, IReadOnlyList<string> path)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        foreach (var segment in path)
        {
            node = node switch
            {
                JsonObject obj => obj[segment],
                JsonArray array when int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index < array.Count => array[index],
                _ => null,
            };
        }

        if (node is not JsonValue value || !value.TryGetValue<string>(out var text))
        {
            return null;
        }

        var marker = text.LastIndexOf("…[", StringComparison.Ordinal);
        var shown = marker >= 0 && text.EndsWith(" characters cut]", StringComparison.Ordinal) ? text[..marker] : text;
        // A line cut in its middle is not shown whole.
        return shown.Length == text.Length ? LineCount(text) : shown.AsSpan().Count('\n');
    }

    private static string Count(int count, string unit)
        => count.ToString("N0", CultureInfo.InvariantCulture) + " " + unit + (count == 1 ? string.Empty : "s");

    private static string ShownLines(CodeModeExcerptLines lines)
    {
        var head = lines.HeadLastLine > 0 ? "lines 1–" + lines.HeadLastLine.ToString("N0", CultureInfo.InvariantCulture) : null;
        var tail = lines.TailFirstLine <= lines.TotalLines
            ? "lines " + lines.TailFirstLine.ToString("N0", CultureInfo.InvariantCulture) + "–" + lines.TotalLines.ToString("N0", CultureInfo.InvariantCulture)
            : null;
        return (head, tail) switch
        {
            ({ } h, { } t) => h + " and " + t[6..],
            ({ } h, null) => h,
            (null, { } t) => t,
            _ => "none",
        };
    }

    /// <summary>Formats a run that goes on in the background after <c>exec</c> stopped waiting for it.</summary>
    /// <param name="runId">The id of the run.</param>
    /// <param name="elapsed">How long it has run.</param>
    /// <param name="calls">The calls it made so far.</param>
    /// <param name="recentOutput">The output it wrote so far, or null.</param>
    /// <returns>The text for the model.</returns>
    public static string FormatRunning(string runId, TimeSpan elapsed, int calls, string? recentOutput)
    {
        var builder = new StringBuilder();
        builder.Append("Script running as ").Append(runId).Append(" · ").Append(Duration(elapsed)).Append(" · ")
            .Append(calls.ToString(CultureInfo.InvariantCulture)).Append(calls == 1 ? " call so far\n" : " calls so far\n");
        if (!string.IsNullOrWhiteSpace(recentOutput))
        {
            builder.Append("Output:\n").Append(recentOutput.TrimEnd()).Append('\n');
        }

        builder.Append("It goes on in the background: call `wait` with this run to wait for it, or end your turn; its result comes back to this session as a prompt.\n");
        return builder.ToString();
    }

    private static string CallsSummary(CodeModeRunResult result)
    {
        var count = result.Calls.Count;
        if (count == 0)
        {
            return "no calls";
        }

        var text = count == 1 ? "1 call" : count.ToString(CultureInfo.InvariantCulture) + " calls";
        return result.MaxCallsAtOnce > 1 ? text + ", " + result.MaxCallsAtOnce.ToString(CultureInfo.InvariantCulture) + " at once" : text;
    }

    // Consecutive calls of one function with one outcome read as one entry: `alta.session.result ✓ ×2`.
    private static string GroupCalls(IReadOnlyList<CodeModeCallRecord> calls)
    {
        var groups = new List<(string Path, CodeModeCallStatus Status, int Count)>();
        foreach (var call in calls)
        {
            var index = groups.FindIndex(group => group.Path == call.Path && group.Status == call.Status);
            if (index >= 0)
            {
                groups[index] = (call.Path, call.Status, groups[index].Count + 1);
            }
            else
            {
                groups.Add((call.Path, call.Status, 1));
            }
        }

        const int shown = 12;
        var text = string.Join(", ", groups.Take(shown).Select(static group =>
            group.Path + " " + Mark(group.Status) + (group.Count > 1 ? " ×" + group.Count.ToString(CultureInfo.InvariantCulture) : string.Empty)));
        return groups.Count > shown ? text + $", … {groups.Count - shown} more" : text;
    }

    private static string Mark(CodeModeCallStatus status) => status switch
    {
        CodeModeCallStatus.Succeeded => "✓",
        CodeModeCallStatus.Failed => "✗",
        CodeModeCallStatus.Cancelled => "⊘",
        _ => "∅",
    };

    private static int OutputLength(CodeModeRunResult result) => result.Output.Sum(static entry => entry.Text.Length + 1);

    // The output keeps its head and its tail when it is longer than its budget.
    private static void AppendOutput(StringBuilder builder, CodeModeRunResult result, int budget)
    {
        var lines = new List<string>(result.Output.Count);
        foreach (var entry in result.Output)
        {
            lines.Add(entry.Kind switch
            {
                CodeModeOutputKind.Warning => "warn: " + entry.Text,
                CodeModeOutputKind.Error => "error: " + entry.Text,
                _ => entry.Text,
            });
        }

        var text = string.Join('\n', lines);
        if (text.Length > budget)
        {
            var head = budget * 2 / 3;
            var tail = budget - head;
            text = text[..head] + $"\n… {text.Length - budget:N0} characters of output left out …\n" + text[^tail..];
        }

        // In a block, so that the window shows it as written and the result after it is not taken for a part of it.
        var fence = CodeModeMarkdownExcerpt.FenceFor(text);
        builder.Append(fence).Append('\n').Append(text).Append('\n').Append(fence).Append('\n');
        if (result.DroppedOutputCharacters > 0)
        {
            builder.Append("… ").Append(result.DroppedOutputCharacters.ToString("N0", CultureInfo.InvariantCulture)).Append(" more characters of output were not kept: the log of a run is bounded.\n");
        }
    }

    // Where the time went: the slowest steps and functions, and the script's own time.
    private static string Profile(CodeModeRunResult result)
    {
        var parts = new List<string>();
        foreach (var step in result.Steps.OrderByDescending(static step => step.Duration).Take(3))
        {
            parts.Add($"step {step.StepId} {Duration(step.Duration)}");
        }

        foreach (var group in result.Calls.GroupBy(static call => call.Path).Select(static group => (Path: group.Key, Total: TimeSpan.FromTicks(group.Sum(static call => call.Duration.Ticks)), Count: group.Count()))
            .OrderByDescending(static group => group.Total).Take(3))
        {
            parts.Add($"{group.Path} {Duration(group.Total)}{(group.Count > 1 ? " ×" + group.Count.ToString(CultureInfo.InvariantCulture) : string.Empty)}");
        }

        var queued = TimeSpan.FromTicks(result.Calls.Sum(static call => call.Queued.Ticks));
        if (queued >= TimeSpan.FromMilliseconds(100))
        {
            parts.Add($"queued {Duration(queued)}");
        }

        parts.Add($"script {Duration(result.ScriptTime)}");
        return "Profile: " + string.Join(" · ", parts);
    }

    /// <summary>Writes a duration the way the header does: <c>18 ms</c>, <c>1.8 s</c>, <c>4m12s</c>.</summary>
    /// <param name="duration">The duration.</param>
    /// <returns>The text.</returns>
    public static string Duration(TimeSpan duration) => duration.TotalSeconds switch
    {
        < 1 => duration.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) + " ms",
        < 60 => duration.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s",
        < 3600 => $"{(int)duration.TotalMinutes}m{duration.Seconds:00}s",
        _ => $"{(int)duration.TotalHours}h{duration.Minutes:00}m",
    };
}

/// <summary>
/// Cuts JSON by its structure: the result keeps its shape, the longest values are cut first, and a marker says what
/// was left out. The model reads valid JSON, never a value cut in the middle of a key.
/// </summary>
public static class CodeModeJsonTruncation
{
    private static readonly JsonSerializerOptions s_relaxed = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Cuts a JSON text to at most about <paramref name="maxCharacters"/> characters.</summary>
    /// <param name="json">The JSON text.</param>
    /// <param name="maxCharacters">The budget.</param>
    /// <returns>The JSON, cut when needed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is null.</exception>
    public static string Truncate(string json, int maxCharacters)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length <= maxCharacters)
        {
            return json;
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return CutText(json, maxCharacters);
        }

        if (node is null)
        {
            return json;
        }

        // Written back as the script wrote it: a backtick or an accent stays one character, not an escape of six.
        for (var round = 0; round < 64; round++)
        {
            var text = node.ToJsonString(s_relaxed);
            if (text.Length <= maxCharacters)
            {
                return text;
            }

            var excess = text.Length - maxCharacters;
            if (!CutLargest(node, excess))
            {
                return CutText(text, maxCharacters);
            }
        }

        var last = node.ToJsonString(s_relaxed);
        return last.Length <= maxCharacters ? last : CutText(last, maxCharacters);
    }

    // Cuts the largest piece by what is over the budget, no more: a long string loses its tail, a long array its last
    // items. A JSON just over the budget keeps about the budget, not half of it.
    private static bool CutLargest(JsonNode root, int excess)
    {
        JsonNode? largest = null;
        var largestSize = 0;
        Visit(root);
        if (largest is null)
        {
            return false;
        }

        switch (largest)
        {
            case JsonValue value when value.TryGetValue<string>(out var text):
                // A character is at least one character of the JSON: cutting the excess and the marker's room is enough.
                var keep = Math.Max(32, text.Length - excess - 40);
                if (keep < text.Length && char.IsHighSurrogate(text[keep - 1]))
                {
                    keep--;
                }

                if (keep >= text.Length)
                {
                    return false;
                }

                Replace(value, JsonValue.Create(text[..keep] + $"…[{text.Length - keep:N0} characters cut]"));
                return true;
            case JsonArray array:
                // The marker of an earlier round is replaced by one that counts every item cut.
                var dropped = 0;
                var removed = 0;
                if (array[^1] is JsonValue marker && marker.TryGetValue<string>(out var markerText)
                    && markerText.StartsWith("…[", StringComparison.Ordinal) && markerText.EndsWith(" more items cut]", StringComparison.Ordinal)
                    && int.TryParse(markerText.AsSpan(2, markerText.Length - 2 - " more items cut]".Length), NumberStyles.None, CultureInfo.InvariantCulture, out var before))
                {
                    removed = markerText.Length + 3;
                    array.RemoveAt(array.Count - 1);
                    dropped = before;
                }

                var cut = 0;
                while (array.Count > 1 && (cut == 0 || removed < excess + 40))
                {
                    removed += (array[^1]?.ToJsonString(s_relaxed).Length ?? 4) + 1;
                    array.RemoveAt(array.Count - 1);
                    cut++;
                }

                array.Add((JsonNode?)JsonValue.Create($"…[{dropped + cut} more items cut]"));
                return cut > 0;
            default:
                return false;
        }

        void Visit(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach (var (_, child) in obj)
                    {
                        Visit(child);
                    }

                    break;
                case JsonArray array:
                    if (array.Count > 4)
                    {
                        var size = array.ToJsonString().Length;
                        if (size > largestSize)
                        {
                            largest = array;
                            largestSize = size;
                        }
                    }

                    foreach (var child in array)
                    {
                        Visit(child);
                    }

                    break;
                case JsonValue value when value.TryGetValue<string>(out var text) && text.Length > 64:
                    if (text.Length > largestSize)
                    {
                        largest = value;
                        largestSize = text.Length;
                    }

                    break;
            }
        }
    }

    private static void Replace(JsonNode node, JsonNode replacement)
    {
        switch (node.Parent)
        {
            case JsonObject parent:
                parent[node.GetPropertyName()] = replacement;
                break;
            case JsonArray parent:
                parent[node.GetElementIndex()] = replacement;
                break;
        }
    }

    private static string CutText(string text, int maxCharacters)
    {
        if (text.Length <= maxCharacters)
        {
            return text;
        }

        var head = Math.Max(0, maxCharacters * 2 / 3);
        var tail = Math.Max(0, maxCharacters - head);
        return text[..head] + $"\n… {text.Length - maxCharacters:N0} characters left out …\n" + text[^tail..];
    }
}
