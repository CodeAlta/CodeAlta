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
    private readonly Queue<DesktopLogLine> _rows = new();
    private int _bytes;
    private long _omitted;

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
                _bytes -= Size(removed);
                _omitted++;
            }
            _rows.Enqueue(line);
            _bytes += bytes;
        }
    }

    internal (DesktopLogLine[] Rows, long Omitted) Snapshot()
    {
        lock (_gate) return (_rows.ToArray(), _omitted);
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
