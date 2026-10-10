namespace CodeAlta.Plugin.Statistics.Journal;

/// <summary>
/// Classifies a journal line from its first bytes. The events of a session are serialized with their discriminators
/// first (<c>$type</c>, then <c>kind</c> and <c>phase</c>, or <c>backendEventType</c> for a raw event), and the shared fields
/// (<c>backendId</c>, <c>sessionId</c>, <c>timestamp</c>, <c>runId</c>) last, so the kind of a record is known long before the
/// line ends.
/// </summary>
internal static class JournalPrefixClassifier
{
    /// <summary>The number of bytes at the start of a line the classifier needs at most.</summary>
    public const int PrefixBytes = 160;

    /// <summary>Classifies a line from its start.</summary>
    /// <param name="prefix">The first bytes of the line (all of it when it is shorter than <see cref="PrefixBytes"/>).</param>
    /// <param name="runEnd">How the run ended, for a <see cref="JournalRecordKind.RunEnd"/>.</param>
    /// <returns>
    /// The kind of record; <see cref="JournalRecordKind.Touch"/> for a record statistics do not read, and
    /// <see cref="JournalRecordKind.Unknown"/> when the line does not start the way the serializer writes it.
    /// </returns>
    public static JournalRecordKind Classify(ReadOnlySpan<byte> prefix, out RunEndKind runEnd)
    {
        runEnd = RunEndKind.Idle;
        var position = 0;
        if (!Expect(prefix, ref position, "{\"$type\":\""u8) || !ReadValue(prefix, ref position, out var type))
        {
            return JournalRecordKind.Unknown;
        }

        if (type.SequenceEqual("raw"u8))
        {
            if (!Expect(prefix, ref position, ",\"backendEventType\":\""u8) || !ReadValue(prefix, ref position, out var eventType))
            {
                return JournalRecordKind.Unknown;
            }

            if (eventType.SequenceEqual("codealta.sessionHeader"u8) || eventType.SequenceEqual("codealta.threadHeader"u8))
            {
                return JournalRecordKind.Header;
            }

            return eventType.SequenceEqual("codealta.sessionState"u8) || eventType.SequenceEqual("codealta.threadState"u8)
                ? JournalRecordKind.State
                : JournalRecordKind.Touch;
        }

        if (type.SequenceEqual("error"u8))
        {
            runEnd = RunEndKind.Error;
            return JournalRecordKind.RunEnd;
        }

        if (type.SequenceEqual("system_prompt"u8))
        {
            return JournalRecordKind.SystemPrompt;
        }

        if (type.SequenceEqual("activity"u8))
        {
            return ClassifyActivity(prefix, position);
        }

        if (type.SequenceEqual("contentCompleted"u8))
        {
            if (!Expect(prefix, ref position, ",\"kind\":\""u8) || !ReadValue(prefix, ref position, out var contentKind))
            {
                return JournalRecordKind.Unknown;
            }

            if (contentKind.SequenceEqual("User"u8))
            {
                return JournalRecordKind.UserContent;
            }

            return contentKind.SequenceEqual("Assistant"u8)
                || contentKind.SequenceEqual("Reasoning"u8)
                || contentKind.SequenceEqual("ReasoningSummary"u8)
                    ? JournalRecordKind.Content
                    : JournalRecordKind.Touch;
        }

        if (type.SequenceEqual("sessionUpdate"u8))
        {
            if (!Expect(prefix, ref position, ",\"kind\":\""u8) || !ReadValue(prefix, ref position, out var updateKind))
            {
                return JournalRecordKind.Unknown;
            }

            if (updateKind.SequenceEqual("UsageUpdated"u8))
            {
                return JournalRecordKind.Usage;
            }

            if (updateKind.SequenceEqual("ModelChanged"u8))
            {
                return JournalRecordKind.ModelChanged;
            }

            if (updateKind.SequenceEqual("Idle"u8))
            {
                return JournalRecordKind.RunEnd;
            }

            if (updateKind.SequenceEqual("CompactionCompleted"u8))
            {
                return JournalRecordKind.Compaction;
            }

            return JournalRecordKind.Touch;
        }

        // contentDelta, notes, planSnapshot, interaction, permission requests, user input requests, background tasks.
        return IsKnownTouchType(type) ? JournalRecordKind.Touch : JournalRecordKind.Unknown;
    }

    private static JournalRecordKind ClassifyActivity(ReadOnlySpan<byte> prefix, int position)
    {
        if (!Expect(prefix, ref position, ",\"kind\":\""u8) || !ReadValue(prefix, ref position, out var kind)
            || !Expect(prefix, ref position, ",\"phase\":\""u8) || !ReadValue(prefix, ref position, out var phase))
        {
            return JournalRecordKind.Unknown;
        }

        // Requested and progress records repeat what Started and Completed say.
        if (!(phase.SequenceEqual("Started"u8) || phase.SequenceEqual("Completed"u8) || phase.SequenceEqual("Failed"u8) || phase.SequenceEqual("Canceled"u8)))
        {
            return JournalRecordKind.Touch;
        }

        // The lifecycle of a turn, of a compaction, of a hook or of a sub-agent is not a tool call.
        return kind.SequenceEqual("Turn"u8) || kind.SequenceEqual("Compaction"u8) || kind.SequenceEqual("Hook"u8) || kind.SequenceEqual("Subagent"u8)
            ? JournalRecordKind.Touch
            : JournalRecordKind.Tool;
    }

    private static bool IsKnownTouchType(ReadOnlySpan<byte> type)
        => type.SequenceEqual("contentDelta"u8)
            || type.SequenceEqual("notes"u8)
            || type.SequenceEqual("planSnapshot"u8)
            || type.SequenceEqual("interaction"u8)
            || type.SequenceEqual("permissionGeneric"u8)
            || type.SequenceEqual("permissionCommand"u8)
            || type.SequenceEqual("permissionFileChange"u8)
            || type.SequenceEqual("userInputRequest"u8)
            || type.SequenceEqual("backgroundTasks"u8);

    private static bool Expect(ReadOnlySpan<byte> prefix, ref int position, ReadOnlySpan<byte> literal)
    {
        if (prefix.Length - position < literal.Length || !prefix.Slice(position, literal.Length).SequenceEqual(literal))
        {
            return false;
        }

        position += literal.Length;
        return true;
    }

    // Reads up to the closing quote; a value with an escape or a missing quote is not one a discriminator has.
    private static bool ReadValue(ReadOnlySpan<byte> prefix, ref int position, out ReadOnlySpan<byte> value)
    {
        var rest = prefix[position..];
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
