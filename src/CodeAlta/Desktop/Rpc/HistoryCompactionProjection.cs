using System.Text;
using System.Text.Json;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// Shapes the details of a completed local compaction for a history row. The stored details hold the whole
/// checkpoint summary and the lists of files, which do not fit a row's details budget; cut there, they would
/// no longer be JSON. The row gets the figures only (always small and valid) and the summary as text.
/// </summary>
internal static class HistoryCompactionProjection
{
    /// <summary>The schema of the details a local compaction records.</summary>
    internal const string Schema = "codealta.localCompaction.v1";

    /// <summary>Heading that separates the checkpoint summary from the message in the row's text.</summary>
    internal const string SummaryHeading = "**Checkpoint summary**";

    private const int MaximumStringLength = 256;

    /// <summary>
    /// Splits local compaction details into their figures and their checkpoint summary.
    /// </summary>
    /// <param name="details">The details of a compaction-completed update.</param>
    /// <param name="figures">
    /// A JSON object with the schema, every number, flag and short string of the details, and
    /// <c>{name}Count</c> for each list.
    /// </param>
    /// <param name="summary">The checkpoint summary, or null when the details hold none.</param>
    /// <returns>False when the details are not those of a local compaction.</returns>
    internal static bool TryProject(JsonElement? details, out string figures, out string? summary)
    {
        figures = string.Empty;
        summary = null;
        if (details is not { ValueKind: JsonValueKind.Object } value
            || !value.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.String
            || !string.Equals(schema.GetString(), Schema, StringComparison.Ordinal)) return false;
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject())
            {
                switch (property.Value.ValueKind)
                {
                    case JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False:
                        property.WriteTo(writer);
                        break;
                    case JsonValueKind.String when property.NameEquals("summaryMarkdown"):
                        summary = property.Value.GetString();
                        break;
                    case JsonValueKind.String when property.Value.GetString() is { Length: <= MaximumStringLength }:
                        property.WriteTo(writer);
                        break;
                    case JsonValueKind.Array:
                        writer.WriteNumber(property.Name + "Count", property.Value.GetArrayLength());
                        break;
                }
            }

            writer.WriteEndObject();
        }

        figures = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
        if (string.IsNullOrWhiteSpace(summary)) summary = null;
        return true;
    }
}
