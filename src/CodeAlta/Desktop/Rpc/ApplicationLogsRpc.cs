using System.Text.Json;
using System.Globalization;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

[NeoRpcService("applicationLogs", Version = 1)]
internal sealed class ApplicationLogsService(DesktopLogCapture? capture)
{
    internal const int MaximumResponseBytes = 48 * 1024;
    internal const int MaximumResponseRows = 64;

    [NeoRpcMethod("read")]
    public ApplicationLogsResponse Read(ApplicationLogsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (capture is null) return new("unavailable", [], "0", 0);
        try
        {
            var (snapshot, omitted) = capture.Snapshot();
            var start = Math.Max(0, snapshot.Length - MaximumResponseRows);
            var rows = snapshot[start..].ToList();
            ApplicationLogsResponse response;
            do
            {
                response = new("ok", rows, omitted.ToString(CultureInfo.InvariantCulture), snapshot.Length - rows.Count);
                if (JsonSerializer.SerializeToUtf8Bytes(response, DesktopJsonContext.Default.ApplicationLogsResponse).Length <= MaximumResponseBytes)
                    return response;
                rows.RemoveAt(0); // Keep the newest rows; report the omitted prefix.
            } while (rows.Count > 0);
            return new("ok", [], omitted.ToString(CultureInfo.InvariantCulture), snapshot.Length);
        }
        catch (Exception) { return new("read_failed", [], "0", 0); }
    }
}

internal sealed record ApplicationLogsRequest;
internal sealed record ApplicationLogsResponse(string Status, IReadOnlyList<DesktopLogLine> Rows, string CaptureOmitted, int ReadOmitted);
