using System.Globalization;
using System.Text;
using XenoAtom.Logging;

namespace CodeAlta.Desktop;

// Owned only by the desktop logger configuration. Never reads the rolling files.
internal sealed class DesktopLogCapture
{
    internal const int MaximumRows = 128;
    internal const int MaximumStoredBytes = 128 * 1024;
    internal const int MaximumTextChars = 2048;
    private readonly object _gate = new();
    private readonly Queue<(long Sequence, DesktopLogLine Line)> _rows = new();
    private readonly Queue<(string Token, long Boundary)> _grants = new();
    internal string CaptureId { get; } = Guid.NewGuid().ToString("D");
    private int _bytes;
    private long _sequence;
    private long _evictedThrough;
    private long _clearedThrough;

    internal void Append(string timestamp, string level, string logger, ReadOnlySpan<char> text)
    {
        var length = Math.Min(text.Length, MaximumTextChars);
        if (length > 0 && length < text.Length && char.IsHighSurrogate(text[length - 1])) length--;
        var line = new DesktopLogLine(Bound(timestamp, 48), Bound(level, 32), Bound(logger, 128), text[..length].ToString(), length < text.Length);
        var bytes = Size(line);
        lock (_gate)
        {
            while (_rows.Count >= MaximumRows || _bytes + bytes > MaximumStoredBytes)
            {
                var removed = _rows.Dequeue();
                _bytes -= Size(removed.Line);
                _evictedThrough = removed.Sequence;
            }
            _rows.Enqueue((++_sequence, line));
            _bytes += bytes;
        }
    }

    internal (DesktopLogLine[] Rows, long Omitted) Snapshot()
    {
        lock (_gate) return (_rows.Select(row => row.Line).ToArray(), Math.Max(0, _evictedThrough - _clearedThrough));
    }

    internal (DesktopLogLine[] Rows, long Omitted, string Boundary, string Grant) IssueSnapshot()
    {
        lock (_gate)
        {
            var token = Guid.NewGuid().ToString("D");
            if (_grants.Count == 32) _grants.Dequeue();
            _grants.Enqueue((token, _sequence));
            return (_rows.Select(row => row.Line).ToArray(), Math.Max(0, _evictedThrough - _clearedThrough),
                _sequence.ToString(CultureInfo.InvariantCulture), token);
        }
    }

    internal (string Status, int ClearedRows, long CoveredOmitted) Clear(string captureId, string boundary, string grant, string confirmation)
    {
        lock (_gate)
        {
            if (!string.Equals(captureId, CaptureId, StringComparison.Ordinal) || confirmation != "CLEAR CAPTURED LOGS"
                || !long.TryParse(boundary, NumberStyles.None, CultureInfo.InvariantCulture, out var end)
                || end <= 0 || boundary != end.ToString(CultureInfo.InvariantCulture)
                || !_grants.Any(item => item.Token == grant && item.Boundary == end)) return ("invalid_request", 0, 0);
            if (end <= _clearedThrough || end > _sequence) return ("stale_snapshot", 0, 0);
            var coveredOmitted = Math.Max(0, Math.Min(_evictedThrough, end) - _clearedThrough);
            var removed = 0;
            while (_rows.TryPeek(out var row) && row.Sequence <= end)
            {
                _rows.Dequeue();
                _bytes -= Size(row.Line);
                removed++;
            }
            _clearedThrough = end;
            return ("cleared", removed, coveredOmitted);
        }
    }

    private static int Size(DesktopLogLine line) => Encoding.UTF8.GetByteCount(line.Text) + Encoding.UTF8.GetByteCount(line.Logger)
        + Encoding.UTF8.GetByteCount(line.Timestamp) + Encoding.UTF8.GetByteCount(line.Level) + 64;
    private static string Bound(string text, int maximum)
    {
        var length = Math.Min(text.Length, maximum);
        if (length > 0 && length < text.Length && char.IsHighSurrogate(text[length - 1])) length--;
        return text[..length];
    }
}

internal sealed record DesktopLogLine(string Timestamp, string Level, string Logger, string Text, bool TextTruncated);

internal sealed class DesktopCaptureLogWriter(DesktopLogCapture capture) : LogWriter
{
    protected override void Log(LogMessage message) => capture.Append(message.Timestamp.ToString("O"),
        message.Level.ToString(), message.Logger.Name, message.Text);
}
