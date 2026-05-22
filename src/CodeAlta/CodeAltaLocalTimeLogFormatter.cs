using System.Globalization;
using XenoAtom.Logging;

namespace CodeAlta;

internal sealed record CodeAltaLocalTimeLogFormatter : LogFormatter
{
    internal static CodeAltaLocalTimeLogFormatter Instance { get; } = new(TimeZoneInfo.Local);

    private readonly TimeZoneInfo _timeZoneInfo;

    internal CodeAltaLocalTimeLogFormatter(TimeZoneInfo timeZoneInfo)
    {
        ArgumentNullException.ThrowIfNull(timeZoneInfo);
        _timeZoneInfo = timeZoneInfo;
    }

    public override bool TryFormat(LogMessage logMessage, Span<char> destination, out int charsWritten, ref LogMessageFormatSegments segments)
    {
        charsWritten = 0;
        var position = 0;
        var timestampFormat = TimestampFormat;
        var provider = logMessage.FormatProvider;
        var timestamp = TimeZoneInfo.ConvertTime(new DateTimeOffset(logMessage.Timestamp, TimeSpan.Zero), _timeZoneInfo);

        var start = position;
        if (!TryWriteTimestamp(timestamp, timestampFormat, provider, destination, ref position)) return InsufficientBuffer(out charsWritten);
        segments.Add(start, position, LogMessageFormatSegmentKind.Timestamp);

        if (!TryAppend(' ', destination, ref position)) return InsufficientBuffer(out charsWritten);

        start = position;
        if (!TryWriteLevel(logMessage.Level, LevelFormat, destination, ref position)) return InsufficientBuffer(out charsWritten);
        segments.Add(start, position, LogMessageFormatSegmentKind.Level);

        if (!TryAppend(' ', destination, ref position)) return InsufficientBuffer(out charsWritten);

        start = position;
        if (!TryAppend(logMessage.Logger.Name.AsSpan(), destination, ref position)) return InsufficientBuffer(out charsWritten);
        segments.Add(start, position, LogMessageFormatSegmentKind.LoggerName);

        if (!logMessage.EventId.IsEmpty)
        {
            if (!TryAppend(" [".AsSpan(), destination, ref position)) return InsufficientBuffer(out charsWritten);

            start = position;
            if (!TryWriteEventId(logMessage.EventId, destination, ref position)) return InsufficientBuffer(out charsWritten);
            segments.Add(start, position, LogMessageFormatSegmentKind.EventId);

            if (!TryAppend(']', destination, ref position)) return InsufficientBuffer(out charsWritten);
        }

        if (!TryAppend(' ', destination, ref position)) return InsufficientBuffer(out charsWritten);

        start = position;
        if (!TryAppend(logMessage.Text, destination, ref position)) return InsufficientBuffer(out charsWritten);
        segments.Add(start, position, LogMessageFormatSegmentKind.Text);

        if (logMessage.Exception is { } exception)
        {
            if (!TryAppend(" | ".AsSpan(), destination, ref position)) return InsufficientBuffer(out charsWritten);

            start = position;
            if (!TryAppend(exception.ToString().AsSpan(), destination, ref position)) return InsufficientBuffer(out charsWritten);
            segments.Add(start, position, LogMessageFormatSegmentKind.Exception);
        }

        charsWritten = position;
        return true;
    }

    private static bool TryWriteTimestamp(DateTimeOffset value, string format, IFormatProvider provider, Span<char> destination, ref int position)
    {
        if (!value.TryFormat(destination[position..], out var charsWritten, format.AsSpan(), provider))
        {
            return false;
        }

        position += charsWritten;
        return true;
    }

    private static bool TryWriteLevel(LogLevel level, LogLevelFormat levelFormat, Span<char> destination, ref int position)
    {
        return levelFormat switch
        {
            LogLevelFormat.Short => TryAppend(level.ToShortString().AsSpan(), destination, ref position),
            LogLevelFormat.Long => TryAppend(level.ToLongString().AsSpan(), destination, ref position),
            LogLevelFormat.Tri => TryAppend(level.ToTriString().AsSpan(), destination, ref position),
            LogLevelFormat.Char => TryAppend(level.ToCharString().AsSpan(), destination, ref position),
            _ => TryAppend(level.ToShortString().AsSpan(), destination, ref position)
        };
    }

    private static bool TryWriteEventId(LogEventId eventId, Span<char> destination, ref int position)
    {
        if (!eventId.Id.TryFormat(destination[position..], out var charsWritten, provider: CultureInfo.InvariantCulture))
        {
            return false;
        }

        position += charsWritten;
        if (string.IsNullOrWhiteSpace(eventId.Name))
        {
            return true;
        }

        return TryAppend(':', destination, ref position) && TryAppend(eventId.Name.AsSpan(), destination, ref position);
    }

    private static bool TryAppend(char value, Span<char> destination, ref int position)
    {
        if ((uint)position >= (uint)destination.Length)
        {
            return false;
        }

        destination[position++] = value;
        return true;
    }

    private static bool TryAppend(ReadOnlySpan<char> value, Span<char> destination, ref int position)
    {
        if (destination.Length - position < value.Length)
        {
            return false;
        }

        value.CopyTo(destination[position..]);
        position += value.Length;
        return true;
    }

    private static bool InsufficientBuffer(out int charsWritten)
    {
        charsWritten = 0;
        return false;
    }
}
