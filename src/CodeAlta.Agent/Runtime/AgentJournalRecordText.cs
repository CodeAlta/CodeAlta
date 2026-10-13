using System.Buffers.Text;
using System.Text;

namespace CodeAlta.Agent.Runtime;

/// <summary>What a journal record is, as far as its first bytes say.</summary>
internal enum AgentJournalRecordKind
{
    /// <summary>A line that does not start the way the serializer writes a record.</summary>
    Unknown,

    /// <summary>A record of no interest to a reader of the two ends of a line.</summary>
    Other,

    /// <summary>The first-line view header (<c>codealta.sessionHeader</c>).</summary>
    Header,

    /// <summary>A view-state snapshot (<c>codealta.sessionState</c>).</summary>
    ViewState,

    /// <summary>A session summary snapshot (<c>local.sessionSummary</c>).</summary>
    Summary,

    /// <summary>A session state snapshot (<c>local.sessionState</c>).</summary>
    State,

    /// <summary>A completed user message.</summary>
    UserContent,

    /// <summary>A completed assistant message.</summary>
    AssistantContent,

    /// <summary>The start of a tool call of any kind.</summary>
    ToolStarted,

    /// <summary>The session became idle: the run ended.</summary>
    Idle,

    /// <summary>An error record.</summary>
    Error,

    /// <summary>A compaction completed.</summary>
    CompactionCompleted,

    /// <summary>The usage of a request to the model.</summary>
    UsageUpdated,

    /// <summary>The system prompt applied to a turn (<c>system_prompt</c>).</summary>
    SystemPrompt,
}

/// <summary>
/// Reads what a journal record says about itself from the two ends of its line, without parsing it. The serializer
/// writes the discriminators first (<c>$type</c>, then <c>kind</c> and <c>phase</c>, or <c>backendEventType</c> for a raw
/// event) and the fields every record shares last (<c>backendId</c>, <c>sessionId</c>, <c>timestamp</c>, <c>runId</c>),
/// so neither end depends on the size of what lies between them: a record can be hundreds of megabytes.
/// </summary>
internal static class AgentJournalRecordText
{
    /// <summary>The number of bytes at the start of a line that <see cref="Classify"/> needs at most.</summary>
    internal const int HeadBytes = 160;

    /// <summary>The number of bytes at the end of a line that hold the shared fields.</summary>
    internal const int TailBytes = 512;

    private static ReadOnlySpan<byte> SessionIdKey => ",\"sessionId\":\""u8;

    private static ReadOnlySpan<byte> TimestampKey => ",\"timestamp\":\""u8;

    private static ReadOnlySpan<byte> RunIdKey => ",\"runId\":\""u8;

    /// <summary>Classifies a record from the start of its line.</summary>
    /// <param name="head">The first bytes of the line: all of it when it is shorter than <see cref="HeadBytes"/>.</param>
    /// <returns>The kind of the record.</returns>
    internal static AgentJournalRecordKind Classify(ReadOnlySpan<byte> head)
    {
        var position = 0;
        if (!Expect(head, ref position, "{\"$type\":\""u8) || !ReadValue(head, ref position, out var type))
        {
            return AgentJournalRecordKind.Unknown;
        }

        if (type.SequenceEqual("raw"u8))
        {
            if (!Expect(head, ref position, ",\"backendEventType\":\""u8) || !ReadValue(head, ref position, out var eventType))
            {
                return AgentJournalRecordKind.Unknown;
            }

            // The "thread" names are those of journals written before sessions were called sessions.
            if (eventType.SequenceEqual("codealta.sessionHeader"u8) || eventType.SequenceEqual("codealta.threadHeader"u8))
            {
                return AgentJournalRecordKind.Header;
            }

            if (eventType.SequenceEqual("codealta.sessionState"u8) || eventType.SequenceEqual("codealta.threadState"u8))
            {
                return AgentJournalRecordKind.ViewState;
            }

            if (eventType.SequenceEqual("local.sessionSummary"u8))
            {
                return AgentJournalRecordKind.Summary;
            }

            return eventType.SequenceEqual("local.sessionState"u8) ? AgentJournalRecordKind.State : AgentJournalRecordKind.Other;
        }

        if (type.SequenceEqual("contentCompleted"u8))
        {
            if (!Expect(head, ref position, ",\"kind\":\""u8) || !ReadValue(head, ref position, out var contentKind))
            {
                return AgentJournalRecordKind.Unknown;
            }

            if (contentKind.SequenceEqual("User"u8))
            {
                return AgentJournalRecordKind.UserContent;
            }

            return contentKind.SequenceEqual("Assistant"u8) ? AgentJournalRecordKind.AssistantContent : AgentJournalRecordKind.Other;
        }

        if (type.SequenceEqual("activity"u8))
        {
            if (!Expect(head, ref position, ",\"kind\":\""u8) || !ReadValue(head, ref position, out var activityKind)
                || !Expect(head, ref position, ",\"phase\":\""u8) || !ReadValue(head, ref position, out var phase))
            {
                return AgentJournalRecordKind.Unknown;
            }

            // The lifecycle of a turn, of a compaction, of a hook or of a sub-agent is not a tool call.
            return phase.SequenceEqual("Started"u8)
                && !(activityKind.SequenceEqual("Turn"u8) || activityKind.SequenceEqual("Compaction"u8)
                    || activityKind.SequenceEqual("Hook"u8) || activityKind.SequenceEqual("Subagent"u8))
                    ? AgentJournalRecordKind.ToolStarted
                    : AgentJournalRecordKind.Other;
        }

        if (type.SequenceEqual("sessionUpdate"u8))
        {
            if (!Expect(head, ref position, ",\"kind\":\""u8) || !ReadValue(head, ref position, out var updateKind))
            {
                return AgentJournalRecordKind.Unknown;
            }

            if (updateKind.SequenceEqual("Idle"u8))
            {
                return AgentJournalRecordKind.Idle;
            }

            if (updateKind.SequenceEqual("UsageUpdated"u8))
            {
                return AgentJournalRecordKind.UsageUpdated;
            }

            return updateKind.SequenceEqual("CompactionCompleted"u8) ? AgentJournalRecordKind.CompactionCompleted : AgentJournalRecordKind.Other;
        }

        if (type.SequenceEqual("system_prompt"u8))
        {
            return AgentJournalRecordKind.SystemPrompt;
        }

        return type.SequenceEqual("error"u8) ? AgentJournalRecordKind.Error : AgentJournalRecordKind.Other;
    }

    /// <summary>Whether a record is one of the four metadata snapshots, of which only the last counts.</summary>
    /// <param name="kind">The kind of the record.</param>
    /// <returns><see langword="true"/> for a header, a view state, a summary or a state.</returns>
    internal static bool IsMetadataSnapshot(AgentJournalRecordKind kind)
        => kind is AgentJournalRecordKind.Header or AgentJournalRecordKind.ViewState or AgentJournalRecordKind.Summary or AgentJournalRecordKind.State;

    /// <summary>Finds the session identifier among the shared fields at the end of a line.</summary>
    /// <param name="tail">The last bytes of the line, without its line end.</param>
    /// <param name="value">Where the value is in <paramref name="tail"/>, between its quotes.</param>
    /// <returns><see langword="true"/> when the line ends the way the serializer writes a record.</returns>
    /// <remarks>
    /// The value is the one immediately followed by the timestamp, which only the shared fields are: a <c>sessionId</c>
    /// inside the payload of a record is never taken for it.
    /// </remarks>
    internal static bool TryFindSessionId(ReadOnlySpan<byte> tail, out Range value)
    {
        value = default;
        var timestampIndex = tail.LastIndexOf(TimestampKey);
        if (timestampIndex < 0)
        {
            return false;
        }

        // The value ends with the quote before the timestamp key.
        var keyIndex = tail[..timestampIndex].LastIndexOf(SessionIdKey);
        if (keyIndex < 0)
        {
            return false;
        }

        var start = keyIndex + SessionIdKey.Length;
        var end = timestampIndex - 1;
        if (end < start || tail[end] != (byte)'"' || tail[start..end].IndexOfAny((byte)'"', (byte)'\\') >= 0)
        {
            return false;
        }

        value = start..end;
        return true;
    }

    /// <summary>Reads the time and the run of a record from the end of its line.</summary>
    /// <param name="tail">The last bytes of the line, without its line end.</param>
    /// <param name="timestamp">The time of the record.</param>
    /// <param name="runId">The run, as a span of <paramref name="tail"/>; empty when the record has none.</param>
    /// <returns><see langword="true"/> when a time was found.</returns>
    internal static bool TryReadEnvelope(ReadOnlySpan<byte> tail, out DateTimeOffset timestamp, out ReadOnlySpan<byte> runId)
    {
        timestamp = default;
        runId = default;
        var timestampIndex = tail.LastIndexOf(TimestampKey);
        if (timestampIndex < 0)
        {
            return false;
        }

        var valueStart = timestampIndex + TimestampKey.Length;
        var valueLength = tail[valueStart..].IndexOf((byte)'"');
        if (valueLength <= 0 || !TryParseTimestamp(tail.Slice(valueStart, valueLength), out timestamp))
        {
            return false;
        }

        var rest = tail[(valueStart + valueLength)..];
        var runIndex = rest.IndexOf(RunIdKey);
        if (runIndex >= 0)
        {
            var runStart = runIndex + RunIdKey.Length;
            var runLength = rest[runStart..].IndexOfAny((byte)'"', (byte)'\\');
            if (runLength > 0 && rest[runStart + runLength] == (byte)'"')
            {
                runId = rest.Slice(runStart, runLength);
            }
        }

        return true;
    }

    /// <summary>Reads a string field that the serializer writes among the last fields of a record.</summary>
    /// <param name="tail">The last bytes of the line, without its line end.</param>
    /// <param name="key">The field with its punctuation, as in <c>,"ask_id":"</c>.</param>
    /// <returns>The value, or null when the field is absent or holds an escape.</returns>
    internal static string? ReadTailString(ReadOnlySpan<byte> tail, ReadOnlySpan<byte> key)
    {
        var index = tail.LastIndexOf(key);
        if (index < 0)
        {
            return null;
        }

        var rest = tail[(index + key.Length)..];
        var length = rest.IndexOfAny((byte)'"', (byte)'\\');
        return length > 0 && rest[length] == (byte)'"' ? Encoding.UTF8.GetString(rest[..length]) : null;
    }

    /// <summary>Reads the start of a JSON string value, decoding its escapes, up to a number of characters.</summary>
    /// <param name="json">The bytes that follow the opening quote; they may end before the closing quote.</param>
    /// <param name="maxChars">The most characters to return.</param>
    /// <param name="complete">Whether the closing quote was reached within <paramref name="maxChars"/>.</param>
    /// <returns>The decoded text.</returns>
    internal static string ReadStringStart(ReadOnlySpan<byte> json, int maxChars, out bool complete)
    {
        complete = false;
        var builder = new StringBuilder(Math.Min(maxChars, 256));
        var position = 0;
        while (position < json.Length && builder.Length < maxChars)
        {
            var value = json[position];
            if (value == (byte)'"')
            {
                complete = true;
                break;
            }

            if (value != (byte)'\\')
            {
                // A run of plain bytes, cut where a multi-byte character is whole.
                var run = json[position..].IndexOfAny((byte)'"', (byte)'\\');
                run = run < 0 ? json.Length - position : run;
                var chars = Encoding.UTF8.GetCharCount(json.Slice(position, run));
                if (chars > maxChars - builder.Length)
                {
                    run = Utf8PrefixLength(json.Slice(position, run), maxChars - builder.Length);
                }
                else if (position + run == json.Length)
                {
                    run = CompleteUtf8Length(json.Slice(position, run));
                }

                // Nothing whole is left: a character that the limit or the buffer cuts.
                if (run == 0)
                {
                    break;
                }

                builder.Append(Encoding.UTF8.GetString(json.Slice(position, run)));
                position += run;
                continue;
            }

            if (position + 1 >= json.Length)
            {
                break;
            }

            var escape = json[position + 1];
            if (escape != (byte)'u')
            {
                builder.Append(escape switch
                {
                    (byte)'n' => '\n',
                    (byte)'r' => '\r',
                    (byte)'t' => '\t',
                    (byte)'b' => '\b',
                    (byte)'f' => '\f',
                    _ => (char)escape,
                });
                position += 2;
                continue;
            }

            if (position + 6 > json.Length || !Utf8Parser.TryParse(json.Slice(position + 2, 4), out ushort unit, out var consumed, 'X') || consumed != 4)
            {
                break;
            }

            builder.Append((char)unit);
            position += 6;
        }

        // Half of a surrogate pair is not text.
        if (builder.Length > 0 && char.IsHighSurrogate(builder[^1]))
        {
            builder.Length--;
            complete = false;
        }

        return builder.ToString();
    }

    private static bool TryParseTimestamp(ReadOnlySpan<byte> value, out DateTimeOffset timestamp)
    {
        if (Utf8Parser.TryParse(value, out timestamp, out var consumed, 'O') && consumed == value.Length)
        {
            return true;
        }

        // The serializer writes fewer fraction digits than the round-trip format when they are zero.
        Span<char> text = stackalloc char[64];
        return value.Length <= text.Length
            && Encoding.UTF8.TryGetChars(value, text, out var written)
            && DateTimeOffset.TryParse(text[..written], System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out timestamp);
    }

    // The length of the longest prefix that decodes to at most maxChars characters.
    private static int Utf8PrefixLength(ReadOnlySpan<byte> bytes, int maxChars)
    {
        var chars = 0;
        var position = 0;
        while (position < bytes.Length)
        {
            var lead = bytes[position];
            var size = lead < 0x80 ? 1 : lead < 0xe0 ? 2 : lead < 0xf0 ? 3 : 4;
            var units = size == 4 ? 2 : 1;
            if (position + size > bytes.Length || chars + units > maxChars)
            {
                break;
            }

            chars += units;
            position += size;
        }

        return position;
    }

    // The length without a multi-byte character that the buffer cuts.
    private static int CompleteUtf8Length(ReadOnlySpan<byte> bytes)
    {
        var index = bytes.Length - 1;
        var back = 0;
        while (index >= 0 && back < 3 && (bytes[index] & 0xc0) == 0x80)
        {
            index--;
            back++;
        }

        if (index < 0)
        {
            return bytes.Length;
        }

        var lead = bytes[index];
        var size = lead < 0x80 ? 1 : lead < 0xe0 ? 2 : lead < 0xf0 ? 3 : 4;
        return index + size > bytes.Length ? index : bytes.Length;
    }

    private static bool Expect(ReadOnlySpan<byte> head, ref int position, ReadOnlySpan<byte> literal)
    {
        if (head.Length - position < literal.Length || !head.Slice(position, literal.Length).SequenceEqual(literal))
        {
            return false;
        }

        position += literal.Length;
        return true;
    }

    // Reads up to the closing quote; a value with an escape or a missing quote is not one a discriminator has.
    private static bool ReadValue(ReadOnlySpan<byte> head, ref int position, out ReadOnlySpan<byte> value)
    {
        var rest = head[position..];
        var end = rest.IndexOfAny((byte)'"', (byte)'\\');
        if (end < 0 || rest[end] != (byte)'"')
        {
            value = default;
            return false;
        }

        value = rest[..end];
        position += end + 1;
        return true;
    }
}
