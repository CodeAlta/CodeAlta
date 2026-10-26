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
                    case "title" or "yield_ms" or "yield_time_ms" or "max_output_tokens" or "max_tokens" or "timeout_ms":
                        error = $"The option `{property.Name}` of `// @exec:` has a value of the wrong type.";
                        return null;
                    default:
                        error = $"Unknown option `{property.Name}` in `// @exec:`. Known options: title, yield_ms, max_output_tokens, timeout_ms.";
                        return null;
                }
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
