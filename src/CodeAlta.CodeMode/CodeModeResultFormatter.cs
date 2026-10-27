using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeAlta.CodeMode;

/// <summary>
/// Writes what a run did as the text the model reads: a header with the status, the time and the calls, then the
/// result, the output, and for a failure the source line and the calls already made, which are not undone.
/// </summary>
public static class CodeModeResultFormatter
{
    /// <summary>The output budget of a result, in tokens, when the script sets none.</summary>
    public const int DefaultMaxOutputTokens = 4_000;

    private const int CharactersPerToken = 4;

    /// <summary>Formats a finished run.</summary>
    /// <param name="result">The result of the run.</param>
    /// <param name="maxOutputTokens">The output budget, in tokens.</param>
    /// <returns>The text for the model.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> is null.</exception>
    public static string Format(CodeModeRunResult result, int maxOutputTokens = DefaultMaxOutputTokens)
    {
        ArgumentNullException.ThrowIfNull(result);
        var budget = Math.Max(1_000, maxOutputTokens * CharactersPerToken);
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

        var outputBudget = budget / 3;
        if (result.ValueJson is { } value)
        {
            builder.Append("Result:\n").Append(CodeModeJsonTruncation.Truncate(value, budget - Math.Min(outputBudget, OutputLength(result)))).Append('\n');
        }

        if (result.Output.Count > 0)
        {
            builder.Append("Output:\n");
            AppendOutput(builder, result, result.ValueJson is null ? budget : outputBudget);
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

        return builder.ToString().TrimEnd() + "\n";
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

        builder.Append(text).Append('\n');
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

        for (var round = 0; round < 64; round++)
        {
            var text = node.ToJsonString();
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

        var last = node.ToJsonString();
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
