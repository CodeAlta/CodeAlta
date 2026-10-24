using System.Text.Json;
using System.Text.Json.Nodes;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugin.Mobile;

/// <summary>What one <c>alta</c> command gave: its exit code, its error and its records, the result header left out.</summary>
/// <param name="ExitCode">The exit code.</param>
/// <param name="Error">The error, when the command failed.</param>
/// <param name="Records">The records.</param>
/// <param name="Truncated">Whether the host cut the output: records are missing at its end.</param>
internal sealed record MobileAltaResult(int ExitCode, string? Error, IReadOnlyList<JsonObject> Records, bool Truncated = false)
{
    /// <summary>Gets the records of one type.</summary>
    internal IEnumerable<JsonObject> OfType(string type) => Records.Where(record => (string?)record["type"] == type);
}

/// <summary>Runs the <c>alta</c> commands of the mobile view and reads their records.</summary>
internal static class MobileAlta
{
    /// <summary>Runs one command.</summary>
    internal static async Task<MobileAltaResult> RunAsync(IPluginAltaService alta, IReadOnlyList<string> args, string? stdin, CancellationToken cancellationToken)
    {
        var result = await alta.InvokeAsync(args, stdin, cancellationToken: cancellationToken).ConfigureAwait(false);
        var records = new List<JsonObject>();
        string? error = null;
        foreach (var line in result.TranscriptJsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            JsonNode? node;
            try { node = JsonNode.Parse(line); }
            catch (JsonException) { continue; }
            if (node is not JsonObject record) continue;
            var type = (string?)record["type"];
            if (type == "alta.error") error ??= Text(record, "message") ?? Text(record, "code");
            else if (type != "alta.result") records.Add(record);
        }

        return new(result.ExitCode, result.ExitCode == 0 ? null : error ?? result.Error ?? "The command failed.", records, result.Truncated);
    }

    /// <summary>A string member of a record, null when it has none or an empty one.</summary>
    internal static string? Text(JsonObject? record, string name)
        => record?[name] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;

    /// <summary>A boolean member of a record, false when it has none.</summary>
    internal static bool Flag(JsonObject? record, string name)
        => record?[name] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;
}
