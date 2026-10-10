namespace CodeAlta.Plugin.Statistics.Journal;

/// <summary>Counts the lines a unified diff adds and removes without decoding it.</summary>
internal static class JournalDiffCounter
{
    /// <summary>Counts the added and removed lines of a diff held in a JSON string.</summary>
    /// <param name="raw">The bytes of the string between its quotes, escapes included (a <c>+</c> is written <c>+</c>).</param>
    /// <param name="added">The lines that start with <c>+</c>, the file headers apart.</param>
    /// <param name="removed">The lines that start with <c>-</c>, the file headers apart.</param>
    public static void Count(ReadOnlySpan<byte> raw, out long added, out long removed)
    {
        // The rule of the terminal and the timeline: a line that starts with +++ or --- is a file header.
        added = 0;
        removed = 0;
        var index = 0;
        var atLineStart = true;
        while (index < raw.Length)
        {
            if (atLineStart)
            {
                atLineStart = false;
                var first = Decode(raw, index, out var firstLength);
                if (first is '+' or '-')
                {
                    var second = Decode(raw, index + firstLength, out var secondLength);
                    var third = Decode(raw, index + firstLength + secondLength, out _);
                    if (!(second == first && third == first))
                    {
                        if (first == '+')
                        {
                            added++;
                        }
                        else
                        {
                            removed++;
                        }
                    }
                }
            }

            var slash = raw[index..].IndexOf((byte)'\\');
            if (slash < 0 || index + slash + 1 >= raw.Length)
            {
                break;
            }

            index += slash;
            var escape = raw[index + 1];
            if (escape == (byte)'n')
            {
                index += 2;
                atLineStart = true;
            }
            else
            {
                index += escape == (byte)'u' ? 6 : 2;
            }
        }
    }

    // The character at a position of a JSON string as far as the diff rules look at it: ASCII, with its escapes.
    private static char Decode(ReadOnlySpan<byte> raw, int index, out int length)
    {
        length = 0;
        if (index >= raw.Length)
        {
            return '\0';
        }

        var value = raw[index];
        if (value != (byte)'\\' || index + 1 >= raw.Length)
        {
            length = 1;
            return value < 0x80 ? (char)value : '￿';
        }

        var escape = raw[index + 1];
        if (escape == (byte)'u' && index + 6 <= raw.Length)
        {
            length = 6;
            var code = 0;
            for (var offset = 2; offset < 6; offset++)
            {
                var digit = raw[index + offset];
                var nibble = digit switch
                {
                    >= (byte)'0' and <= (byte)'9' => digit - (byte)'0',
                    >= (byte)'a' and <= (byte)'f' => digit - (byte)'a' + 10,
                    >= (byte)'A' and <= (byte)'F' => digit - (byte)'A' + 10,
                    _ => -1,
                };
                if (nibble < 0)
                {
                    return '￿';
                }

                code = (code << 4) | nibble;
            }

            return (char)code;
        }

        length = 2;
        return escape switch
        {
            (byte)'n' => '\n',
            (byte)'r' => '\r',
            (byte)'t' => '\t',
            _ => (char)escape,
        };
    }
}
