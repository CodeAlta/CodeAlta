using System.Text.Json;
using System.Globalization;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

[NeoRpcService("applicationLogs", Version = 1)]
internal sealed class ApplicationLogsService(DesktopLogCapture? capture)
{
    internal const int MaximumResponseBytes = 256 * 1024;
    internal const int MaximumResponseRows = 400;

    [NeoRpcMethod("read")]
    public ApplicationLogsResponse Read(ApplicationLogsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (capture is null) return new("unavailable", [], "0", 0);
        try
        {
            var (snapshot, omitted, boundary, grant) = capture.IssueSnapshot();
            var start = Math.Max(0, snapshot.Length - MaximumResponseRows);
            var rows = snapshot[start..].ToList();
            ApplicationLogsResponse response;
            do
            {
                response = new("ok", rows, omitted.ToString(CultureInfo.InvariantCulture), snapshot.Length - rows.Count)
                { CaptureId = capture.CaptureId, Boundary = boundary, Grant = grant };
                if (JsonSerializer.SerializeToUtf8Bytes(response, DesktopJsonContext.Default.ApplicationLogsResponse).Length <= MaximumResponseBytes)
                    return response;
                rows.RemoveRange(0, Math.Max(1, rows.Count / 8)); // Keep the newest rows; report the omitted prefix.
            } while (rows.Count > 0);
            return new("ok", [], omitted.ToString(CultureInfo.InvariantCulture), snapshot.Length)
            { CaptureId = capture.CaptureId, Boundary = boundary, Grant = grant };
        }
        catch (Exception) { return new("read_failed", [], "0", 0); }
    }

    [NeoRpcMethod("clear")]
    public ApplicationLogsClearResponse Clear(ApplicationLogsClearRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (capture is null) return new("unavailable", null, "", 0, "0");
        try
        {
            var (status, removed, covered) = capture.Clear(request.CaptureId, request.Boundary, request.Grant, request.Confirmation);
            return status == "cleared"
                ? new(status, capture.CaptureId, request.Boundary, removed, covered.ToString(CultureInfo.InvariantCulture))
                : new(status, null, "", 0, "0");
        }
        catch (Exception) { return new("clear_failed", null, "", 0, "0"); }
    }
}

internal sealed record ApplicationLogsRequest;
internal sealed record ApplicationLogsResponse(string Status, IReadOnlyList<DesktopLogLine> Rows, string CaptureOmitted, int ReadOmitted)
{
    public string? CaptureId { get; init; }
    public string Boundary { get; init; } = "0";
    public string Grant { get; init; } = "";
}
internal sealed record ApplicationLogsClearRequest(string CaptureId, string Boundary, string Grant, string Confirmation);
internal sealed record ApplicationLogsClearResponse(string Status, string? CaptureId, string Boundary, int ClearedRows, string CoveredOmitted);
