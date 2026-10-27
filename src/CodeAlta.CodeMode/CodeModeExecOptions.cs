using System.Text.Json;

namespace CodeAlta.CodeMode;

/// <summary>
/// The options a script sets on its first line, as Codex's <c>exec</c> takes them:
/// <c>// @exec: {"title": "…", "yield_ms": 30000, "max_output_tokens": 4000, "timeout_ms": 600000}</c>.
/// </summary>
public sealed record CodeModeExecOptions
{
    /// <summary>Gets the options of a script without a first-line pragma.</summary>
    public static CodeModeExecOptions None { get; } = new();

    /// <summary>Gets the title of the run, shown in the timeline.</summary>
    public string? Title { get; init; }

    /// <summary>Gets how long <c>exec</c> waits for the run before it goes on in the background, in milliseconds.</summary>
    public int? YieldMilliseconds { get; init; }

    /// <summary>Gets the output budget of the result, in tokens.</summary>
    public int? MaxOutputTokens { get; init; }

    /// <summary>Gets the wall time of the run, in milliseconds.</summary>
    public int? TimeoutMilliseconds { get; init; }

    /// <summary>Gets the tokens the sessions the run starts may spend in all; once spent, no session starts.</summary>
    public int? BudgetTokens { get; init; }

    /// <summary>Gets the run this one resumes or reruns over its recorded steps (<c>"resume": "run-7f3c2a"</c>), or null.</summary>
    public string? Resume { get; init; }

    /// <summary>Gets which recorded steps replay: <c>resume</c> (the unchanged prefix, the default) or <c>all</c>.</summary>
    public string? Replay { get; init; }

    /// <summary>Gets the step from which a rerun runs live (<c>"from": "merge#1"</c>), or null.</summary>
    public string? From { get; init; }

    /// <summary>Gets whether a resume may run again a step during which a call that is not idempotent was in flight.</summary>
    public bool RerunInFlight { get; init; }

    /// <summary>The names of the options, for the message of an unknown one.</summary>
    private const string KnownOptions = "title, yield_ms, max_output_tokens, timeout_ms, budget_tokens, resume, replay, from, rerun_in_flight";

    /// <summary>
    /// Parses the JSON object of a pragma. Unknown keys and values of the wrong type are refused, so that a typo
    /// is reported instead of being ignored.
    /// </summary>
    /// <param name="json">The JSON object after <c>// @exec:</c>.</param>
    /// <param name="error">The reason it was refused.</param>
    /// <returns>The options, or null when <paramref name="error"/> is set.</returns>
    public static CodeModeExecOptions? TryParse(string json, out string? error)
    {
        error = null;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            error = $"The options of `// @exec:` are not a JSON object: {ex.Message}";
            return null;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "The options of `// @exec:` must be a JSON object.";
                return null;
            }

            var options = new CodeModeExecOptions();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "title" when property.Value.ValueKind == JsonValueKind.String:
                        options = options with { Title = property.Value.GetString() };
                        break;
                    case "yield_ms" or "yield_time_ms" when TryGetPositive(property.Value, out var yieldMs):
                        options = options with { YieldMilliseconds = yieldMs };
                        break;
                    case "max_output_tokens" or "max_tokens" when TryGetPositive(property.Value, out var tokens):
                        options = options with { MaxOutputTokens = tokens };
                        break;
                    case "timeout_ms" when TryGetPositive(property.Value, out var timeoutMs):
                        options = options with { TimeoutMilliseconds = timeoutMs };
                        break;
                    case "budget_tokens" when TryGetPositive(property.Value, out var budget):
                        options = options with { BudgetTokens = budget };
                        break;
                    case "resume" when property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is { Length: > 0 } run:
                        options = options with { Resume = run };
                        break;
                    case "replay" when property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is "resume" or "all":
                        options = options with { Replay = property.Value.GetString() };
                        break;
                    case "from" when property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is { Length: > 0 } from:
                        options = options with { From = from };
                        break;
                    case "rerun_in_flight" when property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                        options = options with { RerunInFlight = property.Value.GetBoolean() };
                        break;
                    case "replay" when property.Value.ValueKind == JsonValueKind.String:
                        error = "The option `replay` of `// @exec:` is \"resume\" or \"all\".";
                        return null;
                    case "title" or "yield_ms" or "yield_time_ms" or "max_output_tokens" or "max_tokens" or "timeout_ms" or "budget_tokens" or "resume" or "replay" or "from" or "rerun_in_flight":
                        error = $"The option `{property.Name}` of `// @exec:` has a value of the wrong type.";
                        return null;
                    default:
                        error = $"Unknown option `{property.Name}` in `// @exec:`. Known options: {KnownOptions}.";
                        return null;
                }
            }

            if (options.Resume is null && (options.Replay is not null || options.From is not null || options.RerunInFlight))
            {
                error = "The options `replay`, `from` and `rerun_in_flight` of `// @exec:` go with `resume`, which names the run.";
                return null;
            }

            if (options.From is not null && options.Replay == "all")
            {
                error = "The option `from` of `// @exec:` reruns the steps before it: it does not go with `\"replay\": \"all\"`.";
                return null;
            }

            return options;
        }
    }

    private static bool TryGetPositive(JsonElement value, out int number)
    {
        number = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out number) && number > 0;
    }
}
