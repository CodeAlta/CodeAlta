using System.Collections.Immutable;
using System.Text.Json;
using CodeAlta.Orchestration.Hosting;

namespace CodeAlta.Cli;

/// <summary>
/// The structured final answer of <c>--output-schema</c> (<see cref="StructuredAnswer"/>): the schema is read from a file,
/// and refused when it uses what the check does not cover.
/// </summary>
internal static class CliStructuredOutput
{
    /// <summary>The keywords the check covers, in the order the help lists them.</summary>
    internal static ImmutableArray<string> CheckedKeywords => StructuredAnswer.CheckedKeywords;

    /// <summary>Reads the schema of a file and checks that it uses only what the check covers.</summary>
    /// <returns>The schema, or a message when the file cannot be read, is not a JSON Schema, or uses what is not checked.</returns>
    public static (JsonElement Schema, string? Error) Load(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var root = document.RootElement;
            if (root.ValueKind is not (JsonValueKind.Object or JsonValueKind.True or JsonValueKind.False))
                return (default, $"The schema '{path}' is not a JSON Schema: a schema is an object.");
            var problems = StructuredAnswer.Inspect(root);
            return problems.Count == 0
                ? (root.Clone(), null)
                : (default, $"The schema '{path}' uses what --output-schema does not check: {string.Join("; ", problems)}. "
                    + $"It checks {string.Join(", ", CheckedKeywords)}.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return (default, $"The schema '{path}' cannot be read: {exception.Message}");
        }
        catch (JsonException exception)
        {
            return (default, $"The schema '{path}' is not JSON: {exception.Message}");
        }
    }

    /// <summary>The instruction appended to the prompt.</summary>
    public static string Instruction(JsonElement schema) => StructuredAnswer.Instruction(schema);

    /// <summary>Reads the JSON value of a final answer and checks it against the schema.</summary>
    /// <returns>The JSON text of the value, or the reasons it is not the value asked for.</returns>
    public static (string? Json, IReadOnlyList<string> Errors) Check(string? finalMessage, JsonElement schema) => StructuredAnswer.Check(finalMessage, schema);
}
