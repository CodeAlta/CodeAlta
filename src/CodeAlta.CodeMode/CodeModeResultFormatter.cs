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
    public const int DefaultMaxOutputTokens = 4_000;

    /// <summary>The line that names the file that keeps the whole of a result, before the result: <c>Full result: &lt;path&gt; (…)</c>.</summary>
    public const string FullResultPrefix = "Full result: ";

    /// <summary>
    /// The characters of the preview of a result kept whole in a file: about 2,000 of its start and 1,000 of its end, at
    /// whole lines.
    /// </summary>
    public const int PreviewCharacters = 3_000;

    private const int CharactersPerToken = 4;

    private static readonly JsonSerializerOptions s_relaxedJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

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
        return Shown(text).Length > ResultBudget(result, Budget(maxOutputTokens)) ? text : null;
    }

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

        // A result kept whole in a file gives a preview of its start and its end, as Claude Code does with a long output: the
        // session reads the file for the rest. Without a file, the excerpt has the whole budget.
        if (fullResultPath is not null)
        {
            budget = Math.Min(budget, PreviewCharacters);
            builder.Append(FullResultPrefix).Append(fullResultPath).Append(" (")
                .Append(text.Text.Length.ToString("N0", CultureInfo.InvariantCulture))
                .Append(" characters; a preview of its start and its end is below, read the file for the rest)\n");
        }

        builder.Append("Result:\n");
        if (text.Kind == CodeModeResultKind.Markdown)
        {
            builder.Append(CodeModeMarkdownExcerpt.Cut(text.Text, budget, fullResultPath is null ? null : "they are in the full result")).Append('\n');
        }
        else
        {
            // JSON keeps its shape: the longest values lose their end, and the model still reads valid JSON.
            var json = CodeModeJsonTruncation.Truncate(text.Text, Math.Max(200, budget - 12));
            builder.Append(text.Kind == CodeModeResultKind.Json ? "```json\n" + json + "\n```" : json).Append('\n');
        }
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

    // Cuts the largest piece: a long string loses its tail, a long array its last items.
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
                var keep = Math.Max(32, Math.Min(text.Length / 2, text.Length - excess - 40));
                if (keep >= text.Length)
                {
                    return false;
                }

                Replace(value, JsonValue.Create(text[..keep] + $"…[{text.Length - keep:N0} characters cut]"));
                return true;
            case JsonArray array:
                var remove = Math.Max(1, array.Count / 2);
                var dropped = 0;
                while (remove-- > 0 && array.Count > 1)
                {
                    array.RemoveAt(array.Count - 1);
                    dropped++;
                }

                if (array.Count > 0 && array[^1] is JsonValue marker && marker.TryGetValue<string>(out var markerText) && markerText.StartsWith("…[", StringComparison.Ordinal))
                {
                    array.RemoveAt(array.Count - 1);
                    dropped++;
                }

                array.Add((JsonNode?)JsonValue.Create($"…[{dropped} more items cut]"));
                return dropped > 0;
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
