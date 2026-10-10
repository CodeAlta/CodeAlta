using System.Text;

namespace CodeAlta.Plugin.Statistics.Journal;

/// <summary>
/// A small cache of the strings the journal repeats (providers, models, tools, run identifiers), so that reading a long
/// session allocates a string once, not once per record. Direct-mapped: a collision replaces the older entry.
/// </summary>
internal sealed class Utf8StringCache
{
    private readonly Entry[] _entries;
    private readonly int _mask;

    /// <summary>Initializes a cache.</summary>
    /// <param name="capacity">The number of slots; rounded up to a power of two.</param>
    public Utf8StringCache(int capacity = 4096)
    {
        var size = 16;
        while (size < capacity)
        {
            size <<= 1;
        }

        _entries = new Entry[size];
        _mask = size - 1;
    }

    /// <summary>Gets the string of some UTF-8 bytes, from the cache when it holds it.</summary>
    /// <param name="utf8">The bytes, without escapes.</param>
    /// <returns>The string.</returns>
    public string Get(ReadOnlySpan<byte> utf8)
    {
        if (utf8.IsEmpty)
        {
            return string.Empty;
        }

        var hash = JournalHash.Compute(utf8);
        ref var entry = ref _entries[(int)(hash & (uint)_mask)];
        if (entry.Value is not null && entry.Hash == hash && utf8.SequenceEqual(entry.Bytes))
        {
            return entry.Value;
        }

        var value = Encoding.UTF8.GetString(utf8);
        entry = new Entry(hash, utf8.ToArray(), value);
        return value;
    }

    private struct Entry(ulong hash, byte[] bytes, string value)
    {
        public readonly ulong Hash = hash;
        public readonly byte[] Bytes = bytes;
        public readonly string Value = value;
    }
}

/// <summary>The 64-bit FNV-1a hash the reader uses for identifiers and for the fingerprint of a first line.</summary>
internal static class JournalHash
{
    private const ulong Offset = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;

    /// <summary>Hashes some bytes.</summary>
    /// <param name="bytes">The bytes.</param>
    /// <returns>The hash.</returns>
    public static ulong Compute(ReadOnlySpan<byte> bytes) => Append(Offset, bytes);

    /// <summary>Continues a hash with more bytes.</summary>
    /// <param name="hash">The hash so far; <see cref="Start"/> for none.</param>
    /// <param name="bytes">The bytes to add.</param>
    /// <returns>The new hash.</returns>
    public static ulong Append(ulong hash, ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            hash = (hash ^ value) * Prime;
        }

        return hash;
    }

    /// <summary>Gets the hash of no bytes.</summary>
    public static ulong Start => Offset;
}

/// <summary>Counts the characters and the words of a JSON string without decoding it.</summary>
internal static class JournalTextMetrics
{
    /// <summary>
    /// Counts the UTF-16 characters of the string a JSON token holds, as <see cref="string.Length"/> would.
    /// </summary>
    /// <param name="raw">The bytes of the token between its quotes, escapes included.</param>
    /// <param name="isEscaped">Whether the bytes hold an escape.</param>
    /// <returns>The number of characters.</returns>
    public static long CountChars(ReadOnlySpan<byte> raw, bool isEscaped)
    {
        if (!isEscaped)
        {
            return Encoding.UTF8.GetCharCount(raw);
        }

        long count = 0;
        var index = 0;
        while (index < raw.Length)
        {
            var slash = raw[index..].IndexOf((byte)'\\');
            if (slash < 0)
            {
                count += Encoding.UTF8.GetCharCount(raw[index..]);
                break;
            }

            count += Encoding.UTF8.GetCharCount(raw.Slice(index, slash));
            index += slash;
            // An escape is one character: \n \" \\ ..., or \uXXXX (a surrogate pair is two of them, as in a string).
            index += index + 1 < raw.Length && raw[index + 1] == (byte)'u' ? 6 : 2;
            count++;
        }

        return count;
    }

    /// <summary>
    /// Counts the characters and the words of the string a JSON token holds. A word is a run of characters that are
    /// not ASCII white space.
    /// </summary>
    /// <param name="raw">The bytes of the token between its quotes, escapes included.</param>
    /// <param name="isEscaped">Whether the bytes hold an escape.</param>
    /// <param name="words">The number of words.</param>
    /// <returns>The number of characters.</returns>
    public static long Measure(ReadOnlySpan<byte> raw, bool isEscaped, out long words)
    {
        long chars = 0;
        words = 0;
        var inWord = false;
        var index = 0;
        while (index < raw.Length)
        {
            var value = raw[index];
            if (value == (byte)'\\' && isEscaped)
            {
                var escape = index + 1 < raw.Length ? raw[index + 1] : (byte)0;
                var space = escape is (byte)'n' or (byte)'t' or (byte)'r' or (byte)'f';
                index += escape == (byte)'u' ? 6 : 2;
                chars++;
                if (space)
                {
                    inWord = false;
                }
                else if (!inWord)
                {
                    inWord = true;
                    words++;
                }

                continue;
            }

            index++;
            if (value is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r' or (byte)'\f' or (byte)'\v')
            {
                chars++;
                inWord = false;
                continue;
            }

            // A UTF-8 continuation byte belongs to the character that began before; a 4-byte sequence is two UTF-16 units.
            if ((value & 0xC0) != 0x80)
            {
                chars += (value & 0xF8) == 0xF0 ? 2 : 1;
            }

            if (!inWord)
            {
                inWord = true;
                words++;
            }
        }

        return chars;
    }
}

/// <summary>Reads the envelope every record ends with: <c>backendId</c>, <c>sessionId</c>, <c>timestamp</c> and <c>runId</c>.</summary>
internal static class JournalEnvelope
{
    /// <summary>The number of bytes at the end of a line that hold the envelope.</summary>
    public const int TailBytes = 512;

    private static ReadOnlySpan<byte> TimestampKey => ",\"timestamp\":\""u8;

    private static ReadOnlySpan<byte> RunIdKey => ",\"runId\":\""u8;

    private static ReadOnlySpan<byte> BackendKey => ",\"backendId\":\""u8;

    /// <summary>Reads the time and the run of a record from the end of its line.</summary>
    /// <param name="tail">The last bytes of the line, without its line end.</param>
    /// <param name="timestamp">The UTC time.</param>
    /// <param name="runId">The run, as a span of the tail; empty when the record has none.</param>
    /// <param name="provider">The provider key, as a span of the tail; empty when the tail does not hold it.</param>
    /// <returns><see langword="true"/> when a time was found.</returns>
    public static bool TryRead(ReadOnlySpan<byte> tail, out DateTimeOffset timestamp, out ReadOnlySpan<byte> runId, out ReadOnlySpan<byte> provider)
    {
        timestamp = default;
        runId = default;
        provider = default;

        var timestampIndex = tail.LastIndexOf(TimestampKey);
        if (timestampIndex < 0)
        {
            return false;
        }

        var valueStart = timestampIndex + TimestampKey.Length;
        var valueEnd = tail[valueStart..].IndexOf((byte)'"');
        if (valueEnd <= 0 || !TryParseTimestamp(tail.Slice(valueStart, valueEnd), out var parsed))
        {
            return false;
        }

        timestamp = parsed;
        var afterTimestamp = tail[(valueStart + valueEnd)..];
        var runIndex = afterTimestamp.IndexOf(RunIdKey);
        if (runIndex >= 0)
        {
            var runStart = runIndex + RunIdKey.Length;
            var runEnd = afterTimestamp[runStart..].IndexOf((byte)'"');
            if (runEnd > 0)
            {
                runId = afterTimestamp.Slice(runStart, runEnd);
            }
        }

        var backendIndex = tail[..timestampIndex].LastIndexOf(BackendKey);
        if (backendIndex >= 0)
        {
            var backendStart = backendIndex + BackendKey.Length;
            var backendEnd = tail[backendStart..timestampIndex].IndexOf((byte)'"');
            if (backendEnd > 0)
            {
                provider = tail.Slice(backendStart, backendEnd);
            }
        }

        return true;
    }
    /// <summary>Parses the ISO 8601 time the journal writes: <c>yyyy-MM-ddTHH:mm:ss[.fffffff]</c> followed by <c>Z</c> or an offset.</summary>
    /// <param name="text">The bytes of the time, without quotes.</param>
    /// <param name="value">The UTC time.</param>
    /// <returns><see langword="true"/> when the text is such a time.</returns>
    public static bool TryParseTimestamp(ReadOnlySpan<byte> text, out DateTimeOffset value)
    {
        value = default;
        if (text.Length < 20 || text[4] != (byte)'-' || text[7] != (byte)'-' || text[10] != (byte)'T' || text[13] != (byte)':' || text[16] != (byte)':')
        {
            return false;
        }

        if (!TryDigits(text, 0, 4, out var year) || !TryDigits(text, 5, 2, out var month) || !TryDigits(text, 8, 2, out var day)
            || !TryDigits(text, 11, 2, out var hour) || !TryDigits(text, 14, 2, out var minute) || !TryDigits(text, 17, 2, out var second))
        {
            return false;
        }

        var position = 19;
        long fractionTicks = 0;
        if (text[position] == (byte)'.')
        {
            position++;
            var scale = 1_000_000L;
            var digits = 0;
            while (position < text.Length && text[position] is >= (byte)'0' and <= (byte)'9')
            {
                if (digits < 7)
                {
                    fractionTicks += (text[position] - (byte)'0') * scale;
                    scale /= 10;
                }

                digits++;
                position++;
            }

            if (digits == 0)
            {
                return false;
            }
        }

        var offsetMinutes = 0;
        if (position >= text.Length)
        {
            return false;
        }

        if (text[position] == (byte)'Z')
        {
            position++;
        }
        else if (text[position] is (byte)'+' or (byte)'-')
        {
            var sign = text[position] == (byte)'-' ? -1 : 1;
            if (!TryDigits(text, position + 1, 2, out var offsetHours) || position + 3 >= text.Length || text[position + 3] != (byte)':'
                || !TryDigits(text, position + 4, 2, out var offsetRest))
            {
                return false;
            }

            offsetMinutes = sign * ((offsetHours * 60) + offsetRest);
            position += 6;
        }
        else
        {
            return false;
        }

        if (position != text.Length || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(Math.Max(year, 1), month) || hour > 23 || minute > 59 || second > 59)
        {
            return false;
        }

        var utc = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc).AddTicks(fractionTicks).AddMinutes(-offsetMinutes);
        value = new DateTimeOffset(utc, TimeSpan.Zero);
        return true;
    }

    private static bool TryDigits(ReadOnlySpan<byte> text, int start, int count, out int value)
    {
        value = 0;
        if (start + count > text.Length)
        {
            return false;
        }

        for (var index = start; index < start + count; index++)
        {
            var digit = text[index] - (byte)'0';
            if ((uint)digit > 9)
            {
                return false;
            }

            value = (value * 10) + digit;
        }

        return true;
    }
}
