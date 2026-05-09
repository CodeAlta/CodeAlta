using System.Text;

namespace CodeAlta.LiveTool;

/// <summary>
/// Creates compact flat live-tool transcripts from captured command output.
/// </summary>
public static class AltaTranscriptFormatter
{
    /// <summary>Returns the help text or a flat JSONL transcript headed by <c>alta.result</c>.</summary>
    public static AltaCommandResult FlattenForLiveTool(AltaCommandResult commandResult)
    {
        ArgumentNullException.ThrowIfNull(commandResult);
        if (commandResult.IsHelp)
        {
            return commandResult;
        }

        var stdoutRecords = SplitRecords(commandResult.Stdout);
        var stderrRecords = SplitRecords(commandResult.Stderr);
        var truncated = commandResult.Truncated;
        var maxRecords = commandResult.MaxOutputRecords;
        var maxBytes = commandResult.MaxOutputBytes;
        var emittedRecords = new List<string>(stdoutRecords.Count + stderrRecords.Count);
        emittedRecords.AddRange(stdoutRecords);
        emittedRecords.AddRange(stderrRecords);

        if (maxRecords is > 0 && emittedRecords.Count > maxRecords.Value)
        {
            emittedRecords = emittedRecords.Take(maxRecords.Value).ToList();
            truncated = true;
        }

        var normalCount = emittedRecords.Count(record => !record.Contains("\"type\":\"alta.error\"", StringComparison.Ordinal) &&
                                                        !record.Contains("\"type\":\"alta.warning\"", StringComparison.Ordinal));
        var diagnosticCount = emittedRecords.Count - normalCount;
        var header = AltaJsonlWriter.Serialize(new
        {
            type = "alta.result",
            version = 1,
            exitCode = commandResult.ExitCode,
            correlationId = commandResult.CorrelationId,
            truncated,
            recordCount = normalCount,
            diagnosticCount,
        });

        var builder = new StringBuilder(header.Length + commandResult.Stdout.Length + commandResult.Stderr.Length + 8);
        builder.Append(header).Append('\n');
        foreach (var record in emittedRecords)
        {
            if (maxBytes is > 0)
            {
                var projectedBytes = Encoding.UTF8.GetByteCount(builder.ToString()) + Encoding.UTF8.GetByteCount(record) + 1;
                if (projectedBytes > maxBytes.Value)
                {
                    truncated = true;
                    break;
                }
            }

            builder.Append(record).Append('\n');
        }

        if (truncated && !header.Contains("\"truncated\":true", StringComparison.Ordinal))
        {
            builder.Remove(0, header.Length);
            builder.Insert(0, AltaJsonlWriter.Serialize(new
            {
                type = "alta.result",
                version = 1,
                exitCode = commandResult.ExitCode,
                correlationId = commandResult.CorrelationId,
                truncated = true,
                recordCount = normalCount,
                diagnosticCount,
            }));
        }

        return commandResult with
        {
            Stdout = builder.ToString(),
            Stderr = string.Empty,
            Truncated = truncated,
        };
    }

    private static IReadOnlyList<string> SplitRecords(string text)
        => text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
