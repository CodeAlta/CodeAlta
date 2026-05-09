using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeAlta.LiveTool;

/// <summary>
/// Writes compact JSONL records used by the <c>alta</c> command surface.
/// </summary>
public static class AltaJsonlWriter
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Writes a JSON object followed by a newline.</summary>
    /// <param name="writer">The target writer.</param>
    /// <param name="properties">The record properties.</param>
    public static void WriteRecord(TextWriter writer, IReadOnlyDictionary<string, object?> properties)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(properties);
        writer.Write(JsonSerializer.Serialize(properties, Options));
        writer.WriteLine();
    }

    /// <summary>Writes a JSON object followed by a newline.</summary>
    /// <param name="writer">The target writer.</param>
    /// <param name="record">The JSON-serializable record.</param>
    public static void WriteRecord(TextWriter writer, object record)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(record);
        writer.Write(Serialize(record));
        writer.WriteLine();
    }

    /// <summary>Serializes a JSONL record object with the alta JSON options.</summary>
    /// <param name="record">The JSON-serializable record.</param>
    /// <returns>Compact JSON text.</returns>
    public static string Serialize(object record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return JsonSerializer.Serialize(record, Options);
    }

    /// <summary>Creates a standard result header record.</summary>
    public static Dictionary<string, object?> CreateResultRecord(
        string correlationId,
        int exitCode,
        bool truncated,
        int recordCount,
        int diagnosticCount)
        => new(StringComparer.Ordinal)
        {
            ["type"] = "alta.result",
            ["version"] = 1,
            ["exitCode"] = exitCode,
            ["correlationId"] = correlationId,
            ["truncated"] = truncated,
            ["recordCount"] = recordCount,
            ["diagnosticCount"] = diagnosticCount,
        };

    /// <summary>Writes an <c>alta.error</c> diagnostic record.</summary>
    public static void WriteError(
        TextWriter writer,
        string correlationId,
        string code,
        int exitCode,
        string message,
        string? commandPath = null,
        string? usageHint = null,
        IReadOnlyList<string>? suggestions = null)
        => WriteRecord(writer, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "alta.error",
            ["version"] = 1,
            ["correlationId"] = correlationId,
            ["code"] = code,
            ["exitCode"] = exitCode,
            ["commandPath"] = commandPath,
            ["message"] = message,
            ["usageHint"] = usageHint,
            ["suggestions"] = suggestions is { Count: > 0 } ? suggestions : null,
        });

    /// <summary>Writes an <c>alta.warning</c> diagnostic record.</summary>
    public static void WriteWarning(
        TextWriter writer,
        string correlationId,
        string code,
        string message,
        string? commandPath = null)
        => WriteRecord(writer, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "alta.warning",
            ["version"] = 1,
            ["correlationId"] = correlationId,
            ["code"] = code,
            ["commandPath"] = commandPath,
            ["message"] = message,
        });
}
