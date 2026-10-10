using System.Text;
using System.Text.Json;
using CodeAlta.Agent;

namespace CodeAlta.Plugin.Statistics.Journal;

/// <summary>
/// Reads the payload of the records statistics need with a forward-only JSON reader, keeping only the fields it uses.
/// The envelope of a record (<c>backendId</c>, <c>sessionId</c>, <c>timestamp</c>, <c>runId</c>) is serialized last and is
/// read from the end of the line by <see cref="JournalEnvelope"/>: the payload loop stops where the envelope begins.
/// </summary>
internal sealed class JournalPayloadParser
{
    private static ReadOnlySpan<byte> PromptIdSignature => "{\"prompt_id\":\""u8;

    private static ReadOnlySpan<byte> QueuedPromptsKey => ",\"queued_prompts\":"u8;

    private readonly Utf8StringCache _strings;

    /// <summary>Initializes a parser.</summary>
    /// <param name="strings">The cache of the strings the journal repeats.</param>
    public JournalPayloadParser(Utf8StringCache strings)
    {
        ArgumentNullException.ThrowIfNull(strings);
        _strings = strings;
    }

    /// <summary>Parses the payload of a record.</summary>
    /// <param name="kind">The kind the start of the line gave.</param>
    /// <param name="line">The bytes of the line, or its first bytes when <paramref name="isFinalBlock"/> is false.</param>
    /// <param name="isFinalBlock">Whether <paramref name="line"/> is the whole line.</param>
    /// <param name="isPromptSeen">Tells whether a prompt provenance entry was already taken; null takes them all.</param>
    /// <returns>The record, without its envelope; <see langword="null"/> when the line is not what its start announced.</returns>
    /// <exception cref="JsonException">The line is not valid JSON.</exception>
    public JournalRecord? Parse(JournalRecordKind kind, ReadOnlySpan<byte> line, bool isFinalBlock, Func<ulong, bool>? isPromptSeen)
    {
        var reader = new Utf8JsonReader(line, isFinalBlock, default);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            return null;
        }

        return kind switch
        {
            JournalRecordKind.Tool => ParseTool(ref reader),
            JournalRecordKind.UserContent => ParseUserContent(ref reader),
            JournalRecordKind.Content => ParseContent(ref reader),
            JournalRecordKind.Usage => ParseUsage(ref reader),
            JournalRecordKind.ModelChanged => ParseModelChanged(ref reader),
            JournalRecordKind.Compaction => ParseCompaction(ref reader),
            JournalRecordKind.SystemPrompt => ParseSystemPrompt(ref reader),
            JournalRecordKind.Header => ParseHeader(ref reader),
            JournalRecordKind.State => ParseState(ref reader, line, isFinalBlock, isPromptSeen),
            _ => null,
        };
    }

    // ----- tool calls -----

    private ToolRecord? ParseTool(ref Utf8JsonReader reader)
    {
        var record = new ToolRecord();
        var hasPhase = false;
        while (NextTopLevelProperty(ref reader))
        {
            if (reader.ValueTextEquals("kind"u8))
            {
                if (!reader.Read())
                {
                    break;
                }

                record.ActivityKind = ParseActivityKind(reader.ValueSpan);
            }
            else if (reader.ValueTextEquals("phase"u8))
            {
                if (!reader.Read())
                {
                    break;
                }

                hasPhase = TryParsePhase(reader.ValueSpan, out var phase);
                record.Phase = phase;
            }
            else if (reader.ValueTextEquals("activityId"u8))
            {
                if (!reader.Read())
                {
                    break;
                }

                record.ActivityId = ReadString(ref reader);
            }
            else if (reader.ValueTextEquals("name"u8))
            {
                if (!reader.Read())
                {
                    break;
                }

                record.Name = ReadString(ref reader) ?? record.Name;
            }
            else if (reader.ValueTextEquals("details"u8))
            {
                if (!reader.Read() || !ParseToolDetails(ref reader, record))
                {
                    break;
                }
            }
            else if (!SkipValue(ref reader))
            {
                break;
            }
        }

        return hasPhase ? record : null;
    }

    private bool ParseToolDetails(ref Utf8JsonReader reader, ToolRecord record)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            return SkipCurrent(ref reader);
        }

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("toolName"u8))
            {
                if (!reader.Read())
                {
                    return false;
                }

                record.Name ??= ReadString(ref reader);
            }
            else if (reader.ValueTextEquals("arguments"u8))
            {
                if (!reader.Read())
                {
                    return false;
                }

                var start = reader.TokenStartIndex;
                if (!ParseArguments(ref reader, record))
                {
                    return false;
                }

                record.ArgumentBytes = reader.BytesConsumed - start;
            }
            else if (reader.ValueTextEquals("readFiles"u8))
            {
                if (!reader.Read())
                {
                    return false;
                }

                if (reader.TokenType == JsonTokenType.StartArray)
                {
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    {
                        record.FilesRead++;
                        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray && !reader.TrySkip())
                        {
                            return false;
                        }
                    }
                }
                else if (!SkipCurrent(ref reader))
                {
                    return false;
                }
            }
            else if (reader.ValueTextEquals("modifiedFiles"u8))
            {
                if (!reader.Read())
                {
                    return false;
                }

                if (reader.TokenType == JsonTokenType.StartArray)
                {
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    {
                        if (reader.TokenType == JsonTokenType.String)
                        {
                            (record.ModifiedExtensions ??= []).Add(ExtensionOf(reader.ValueSpan));
                        }
                        else if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray && !reader.TrySkip())
                        {
                            return false;
                        }
                    }
                }
                else if (!SkipCurrent(ref reader))
                {
                    return false;
                }
            }
            else if (reader.ValueTextEquals("diff"u8))
            {
                if (!reader.Read())
                {
                    return false;
                }

                if (reader.TokenType == JsonTokenType.String)
                {
                    JournalDiffCounter.Count(reader.ValueSpan, out var added, out var removed);
                    record.LinesAdded = added;
                    record.LinesRemoved = removed;
                }
                else if (!SkipCurrent(ref reader))
                {
                    return false;
                }
            }
            else if (reader.ValueTextEquals("result"u8))
            {
                if (!reader.Read())
                {
                    return false;
                }

                var start = reader.TokenStartIndex;
                if (!ParseResult(ref reader, record))
                {
                    return false;
                }

                record.ResultBytes = reader.BytesConsumed - start;
            }
            else if (reader.ValueTextEquals("server"u8) || reader.ValueTextEquals("mcpServerName"u8))
            {
                if (!reader.Read())
                {
                    return false;
                }

                record.McpServer = ReadString(ref reader);
            }
            else if (reader.ValueTextEquals("mcpToolName"u8) || reader.ValueTextEquals("tool"u8))
            {
                if (!reader.Read())
                {
                    return false;
                }

                record.McpTool = ReadString(ref reader);
            }
            else if (!SkipValue(ref reader))
            {
                return false;
            }
        }

        return reader.TokenType == JsonTokenType.EndObject;
    }

    // The arguments of a call: an object, or for a few tools an array of words. Only the fields that name what ran are kept.
    private bool ParseArguments(ref Utf8JsonReader reader, ToolRecord record)
    {
        if (reader.TokenType == JsonTokenType.StartArray)
        {
            return ParseWords(ref reader, record);
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            return true;
        }

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("command"u8))
            {
                if (!reader.Read())
                {
                    return false;
                }

                if (reader.TokenType == JsonTokenType.String)
                {
                    record.ShellProgram = ShellProgramOf(reader.ValueSpan, reader.ValueIsEscaped);
                }
                else if (!SkipCurrent(ref reader))
                {
                    return false;
                }
            }
            else if (reader.ValueTextEquals("args"u8))
            {
                if (!reader.Read())
                {
                    return false;
                }

                if (reader.TokenType == JsonTokenType.StartArray)
                {
                    if (!ParseWords(ref reader, record))
                    {
                        return false;
                    }
                }
                else if (!SkipCurrent(ref reader))
                {
                    return false;
                }
            }
            else if (reader.ValueTextEquals("skillName"u8))
            {
                if (!reader.Read())
                {
                    return false;
                }

                record.SkillName = ReadString(ref reader);
            }
            else if (!SkipValue(ref reader))
            {
                return false;
            }
        }

        return reader.TokenType == JsonTokenType.EndObject;
    }

    // The first two words of an argument list that are not options: "session create" for an `alta session create --project x` call.
    private bool ParseWords(ref Utf8JsonReader reader, ToolRecord record)
    {
        var words = 0;
        string? first = null;
        string? second = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                if (words < 2)
                {
                    var value = reader.ValueSpan;
                    if (value.IsEmpty || reader.ValueIsEscaped || value.Length > 32 || !IsCommandWord(value))
                    {
                        // An option, or something that is not a command word (a sentence, a path, a value), ends the command words.
                        words = 2;
                    }
                    else if (words == 0)
                    {
                        first = _strings.Get(value);
                        words = 1;
                    }
                    else
                    {
                        second = _strings.Get(value);
                        words = 2;
                    }
                }
            }
            else if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray && !reader.TrySkip())
            {
                return false;
            }
        }

        if (first is not null)
        {
            record.AltaCommand = second is null ? first : first + " " + second;
        }

        return reader.TokenType == JsonTokenType.EndArray;
    }

    // A word of a command is written as the commands of alta are: a lower-case letter, then lower-case letters, digits, '-' or '_'.
    private static bool IsCommandWord(ReadOnlySpan<byte> value)
    {
        if (value[0] is not (>= (byte)'a' and <= (byte)'z'))
        {
            return false;
        }

        foreach (var letter in value)
        {
            if (letter is not (>= (byte)'a' and <= (byte)'z' or >= (byte)'0' and <= (byte)'9' or (byte)'-' or (byte)'_'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ParseResult(ref Utf8JsonReader reader, ToolRecord record)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            return SkipCurrent(ref reader);
        }

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("success"u8))
            {
                if (!reader.Read())
                {
                    return false;
                }

                if (reader.TokenType is JsonTokenType.True or JsonTokenType.False)
                {
                    record.Success = reader.GetBoolean();
                }
                else if (!SkipCurrent(ref reader))
                {
                    return false;
                }
            }
            else if (!SkipValue(ref reader))
            {
                return false;
            }
        }

        return reader.TokenType == JsonTokenType.EndObject;
    }

    // ----- content -----

    private UserContentRecord? ParseUserContent(ref Utf8JsonReader reader)
    {
        var record = new UserContentRecord();
        while (NextTopLevelProperty(ref reader))
        {
            if (reader.ValueTextEquals("content"u8))
            {
                if (!reader.Read())
                {
                    break;
                }

                if (reader.TokenType == JsonTokenType.String)
                {
                    record.Chars = JournalTextMetrics.Measure(reader.ValueSpan, reader.ValueIsEscaped, out var words);
                    record.Words = words;
                }
                else if (!SkipCurrent(ref reader))
                {
                    break;
                }
            }
            else if (reader.ValueTextEquals("details"u8))
            {
                if (!reader.Read() || !ParseItems(ref reader, record))
                {
                    break;
                }
            }
            else if (reader.ValueTextEquals("ask_id"u8))
            {
                if (!reader.Read())
                {
                    break;
                }

                record.IsAnswer = reader.TokenType == JsonTokenType.String;
            }
            else if (reader.ValueTextEquals("source_session_id"u8))
            {
                if (!reader.Read())
                {
                    break;
                }

                record.SourceSessionId = ReadString(ref reader);
            }
            else if (!SkipValue(ref reader))
            {
                break;
            }
        }

        return record;
    }

    private static bool ParseItems(ref Utf8JsonReader reader, UserContentRecord record)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            return SkipCurrent(ref reader);
        }

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (!reader.ValueTextEquals("items"u8))
            {
                if (!SkipValue(ref reader))
                {
                    return false;
                }

                continue;
            }

            if (!reader.Read())
            {
                return false;
            }

            if (reader.TokenType != JsonTokenType.StartArray)
            {
                if (!SkipCurrent(ref reader))
                {
                    return false;
                }

                continue;
            }

            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType != JsonTokenType.StartObject)
                {
                    if (reader.TokenType == JsonTokenType.StartArray && !reader.TrySkip())
                    {
                        return false;
                    }

                    continue;
                }

                // The type of an item is its first property; the rest (a path, a text) is passed over.
                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    if (reader.ValueTextEquals("$type"u8))
                    {
                        if (!reader.Read())
                        {
                            return false;
                        }

                        var type = reader.ValueSpan;
                        if (type.SequenceEqual("file"u8))
                        {
                            record.Files++;
                        }
                        else if (type.SequenceEqual("directory"u8))
                        {
                            record.Directories++;
                        }
                        else if (type.SequenceEqual("localImage"u8) || type.SequenceEqual("image"u8) || type.SequenceEqual("remoteImage"u8))
                        {
                            record.Images++;
                        }
                        else if (type.SequenceEqual("skill"u8))
                        {
                            record.Skills++;
                        }
                    }
                    else if (!SkipValue(ref reader))
                    {
                        return false;
                    }
                }
            }
        }

        return reader.TokenType == JsonTokenType.EndObject;
    }

    private ContentRecord? ParseContent(ref Utf8JsonReader reader)
    {
        var record = new ContentRecord();
        var channelKnown = false;
        while (NextTopLevelProperty(ref reader))
        {
            if (reader.ValueTextEquals("kind"u8))
            {
                if (!reader.Read())
                {
                    break;
                }

                var kind = reader.ValueSpan;
                channelKnown = true;
                if (kind.SequenceEqual("Assistant"u8))
                {
                    record.Channel = ContentChannel.Assistant;
                }
                else if (kind.SequenceEqual("Reasoning"u8))
                {
                    record.Channel = ContentChannel.Reasoning;
                }
                else if (kind.SequenceEqual("ReasoningSummary"u8))
                {
                    record.Channel = ContentChannel.ReasoningSummary;
                }
                else
                {
                    channelKnown = false;
                }
            }
            else if (reader.ValueTextEquals("content"u8))
            {
                if (!reader.Read())
                {
                    break;
                }

                if (reader.TokenType == JsonTokenType.String)
                {
                    // Words are shown for the answers only: the reasoning is counted in characters.
                    if (record.Channel == ContentChannel.Assistant)
                    {
                        record.Chars = JournalTextMetrics.Measure(reader.ValueSpan, reader.ValueIsEscaped, out var words);
                        record.Words = words;
                    }
                    else
                    {
                        record.Chars = JournalTextMetrics.CountChars(reader.ValueSpan, reader.ValueIsEscaped);
                    }
                }
                else if (!SkipCurrent(ref reader))
                {
                    break;
                }
            }
            else if (!SkipValue(ref reader))
            {
                break;
            }
        }

        return channelKnown ? record : null;
    }

    // ----- session updates -----

    private UsageRecord? ParseUsage(ref Utf8JsonReader reader)
    {
        var record = new UsageRecord();
        while (NextTopLevelProperty(ref reader))
        {
            if (reader.ValueTextEquals("usage"u8))
            {
                if (!reader.Read() || !ParseUsageObject(ref reader, record))
                {
                    break;
                }
            }
            else if (!SkipValue(ref reader))
            {
                break;
            }
        }

        return record;
    }

    private bool ParseUsageObject(ref Utf8JsonReader reader, UsageRecord record)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            return SkipCurrent(ref reader);
        }

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("window"u8))
            {
                if (!reader.Read() || !ParseWindow(ref reader, record))
                {
                    return false;
                }
            }
            else if (reader.ValueTextEquals("lastOperation"u8))
            {
                if (!reader.Read() || !ParseOperation(ref reader, record))
                {
                    return false;
                }
            }
            else if (!SkipValue(ref reader))
            {
                return false;
            }
        }

        return reader.TokenType == JsonTokenType.EndObject;
    }

    private static bool ParseWindow(ref Utf8JsonReader reader, UsageRecord record)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            return SkipCurrent(ref reader);
        }

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("currentTokens"u8))
            {
                if (!reader.Read())
                {
                    return false;
                }

                record.WindowTokens = ReadLong(ref reader);
            }
            else if (reader.ValueTextEquals("tokenLimit"u8))
            {
                if (!reader.Read())
                {
                    return false;
                }

                record.WindowLimit = ReadLong(ref reader);
            }
            else if (!SkipValue(ref reader))
            {
                return false;
            }
        }

        return reader.TokenType == JsonTokenType.EndObject;
    }

    private bool ParseOperation(ref Utf8JsonReader reader, UsageRecord record)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            return SkipCurrent(ref reader);
        }

        string? model = null;
        string? initiator = null;
        string? parentToolCallId = null;
        string? effort = null;
        string? costUnit = null;
        long? input = null, output = null, cacheRead = null, cacheWrite = null, cached = null, reasoning = null;
        double? cost = null, duration = null;
        var any = false;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            any = true;
            if (reader.ValueTextEquals("model"u8))
            {
                if (!reader.Read())
                {
                    return false;
                }

                model = ReadString(ref reader);
            }
            else if (reader.ValueTextEquals("inputTokens"u8))
            {
                if (!reader.Read())
                {
                    return false;
                }

                input = ReadLong(ref reader);
            }
            else if (reader.ValueTextEquals("outputTokens"u8))
            {
                if (!reader.Read())
                {
                    return false;
                }

                output = ReadLong(ref reader);
            }
            else if (reader.ValueTextEquals("cacheReadTokens"u8))
            {
                if (!reader.Read())
                {
                    return false;
                }

                cacheRead = ReadLong(ref reader);
            }
            else if (reader.ValueTextEquals("cacheWriteTokens"u8))
            {
                if (!reader.Read())
                {
                    return false;
                }

                cacheWrite = ReadLong(ref reader);
            }
            else if (reader.ValueTextEquals("cachedInputTokens"u8))
            {
                if (!reader.Read())
                {
                    return false;
                }

                cached = ReadLong(ref reader);
            }
            else if (reader.ValueTextEquals("reasoningTokens"u8))
            {
                if (!reader.Read())
                {
                    return false;
                }

                reasoning = ReadLong(ref reader);
            }
            else if (reader.ValueTextEquals("cost"u8))
            {
                if (!reader.Read())
                {
                    return false;
                }

                cost = ReadDouble(ref reader);
            }
            else if (reader.ValueTextEquals("durationMs"u8))
            {
                if (!reader.Read())
                {
                    return false;
                }

                duration = ReadDouble(ref reader);
            }
            else if (reader.ValueTextEquals("initiator"u8))
            {
                if (!reader.Read())
                {
                    return false;
                }

                initiator = ReadString(ref reader);
            }
            else if (reader.ValueTextEquals("parentToolCallId"u8))
            {
                if (!reader.Read())
                {
                    return false;
                }

                parentToolCallId = ReadString(ref reader);
            }
            else if (reader.ValueTextEquals("reasoningEffort"u8))
            {
                if (!reader.Read())
                {
                    return false;
                }

                effort = ReadString(ref reader);
            }
            else if (reader.ValueTextEquals("costUnit"u8))
            {
                if (!reader.Read())
                {
                    return false;
                }

                costUnit = ReadString(ref reader);
            }
            else if (!SkipValue(ref reader))
            {
                return false;
            }
        }

        if (any)
        {
            record.Operation = new AgentOperationUsageSnapshot(
                Model: model,
                InputTokens: input,
                OutputTokens: output,
                CacheReadTokens: cacheRead,
                CacheWriteTokens: cacheWrite,
                CachedInputTokens: cached,
                ReasoningTokens: reasoning,
                Cost: cost,
                DurationMs: duration,
                Initiator: initiator,
                ParentToolCallId: parentToolCallId,
                ReasoningEffort: effort,
                CostUnit: costUnit);
        }

        return reader.TokenType == JsonTokenType.EndObject;
    }

    private ModelChangedRecord? ParseModelChanged(ref Utf8JsonReader reader)
    {
        var record = new ModelChangedRecord();
        while (NextTopLevelProperty(ref reader))
        {
            if (!reader.ValueTextEquals("details"u8))
            {
                if (!SkipValue(ref reader))
                {
                    break;
                }

                continue;
            }

            if (!reader.Read())
            {
                break;
            }

            if (reader.TokenType != JsonTokenType.StartObject)
            {
                if (!SkipCurrent(ref reader))
                {
                    break;
                }

                continue;
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("providerKey"u8))
                {
                    if (!reader.Read())
                    {
                        return record;
                    }

                    record.ProviderKey = ReadString(ref reader);
                }
                else if (reader.ValueTextEquals("modelId"u8))
                {
                    if (!reader.Read())
                    {
                        return record;
                    }

                    record.ModelId = ReadString(ref reader);
                }
                else if (reader.ValueTextEquals("reasoningEffort"u8))
                {
                    if (!reader.Read())
                    {
                        return record;
                    }

                    record.ReasoningEffort = ReadString(ref reader);
                }
                else if (reader.ValueTextEquals("agentPromptId"u8))
                {
                    if (!reader.Read())
                    {
                        return record;
                    }

                    record.AgentPromptId = ReadString(ref reader);
                }
                else if (!SkipValue(ref reader))
                {
                    return record;
                }
            }
        }

        return record;
    }

    private CompactionRecord? ParseCompaction(ref Utf8JsonReader reader)
    {
        var record = new CompactionRecord();
        while (NextTopLevelProperty(ref reader))
        {
            if (!reader.ValueTextEquals("details"u8))
            {
                if (!SkipValue(ref reader))
                {
                    break;
                }

                continue;
            }

            if (!reader.Read())
            {
                break;
            }

            if (reader.TokenType != JsonTokenType.StartObject)
            {
                if (!SkipCurrent(ref reader))
                {
                    break;
                }

                continue;
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("trigger"u8))
                {
                    if (!reader.Read())
                    {
                        return record;
                    }

                    record.Trigger = ReadString(ref reader);
                }
                else if (reader.ValueTextEquals("tokensBefore"u8))
                {
                    if (!reader.Read())
                    {
                        return record;
                    }

                    record.TokensBefore = ReadLong(ref reader);
                }
                else if (reader.ValueTextEquals("tokensAfter"u8))
                {
                    if (!reader.Read())
                    {
                        return record;
                    }

                    record.TokensAfter = ReadLong(ref reader);
                }
                else if (!SkipValue(ref reader))
                {
                    return record;
                }
            }
        }

        return record;
    }

    private SystemPromptRecord? ParseSystemPrompt(ref Utf8JsonReader reader)
    {
        var record = new SystemPromptRecord();
        while (NextTopLevelProperty(ref reader))
        {
            if (reader.ValueTextEquals("reason"u8))
            {
                if (!reader.Read())
                {
                    break;
                }

                record.Reason = ReadString(ref reader);
            }
            else if (reader.ValueTextEquals("agentPromptId"u8))
            {
                if (!reader.Read())
                {
                    break;
                }

                record.AgentPromptId = ReadString(ref reader);
            }
            else if (reader.ValueTextEquals("systemMessage"u8) || reader.ValueTextEquals("developerInstructions"u8))
            {
                var isSystem = reader.ValueTextEquals("systemMessage"u8);
                if (!reader.Read())
                {
                    break;
                }

                if (reader.TokenType == JsonTokenType.String)
                {
                    var chars = JournalTextMetrics.CountChars(reader.ValueSpan, reader.ValueIsEscaped);
                    if (isSystem)
                    {
                        record.SystemChars = chars;
                    }
                    else
                    {
                        record.DeveloperChars = chars;
                    }
                }
                else if (!SkipCurrent(ref reader))
                {
                    break;
                }
            }
            else if (reader.ValueTextEquals("statistics"u8))
            {
                if (!reader.Read())
                {
                    break;
                }

                if (reader.TokenType != JsonTokenType.StartObject)
                {
                    if (!SkipCurrent(ref reader))
                    {
                        break;
                    }

                    continue;
                }

                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    if (reader.ValueTextEquals("totalApproxTokens"u8))
                    {
                        if (!reader.Read())
                        {
                            return record;
                        }

                        record.ApproxTokens = ReadLong(ref reader) ?? 0;
                    }
                    else if (!SkipValue(ref reader))
                    {
                        return record;
                    }
                }
            }
            else if (!SkipValue(ref reader))
            {
                break;
            }
        }

        return record;
    }

    // ----- header and state -----

    private HeaderRecord? ParseHeader(ref Utf8JsonReader reader)
    {
        var record = new HeaderRecord();
        while (NextTopLevelProperty(ref reader))
        {
            if (reader.ValueTextEquals("backendEventType"u8))
            {
                if (!reader.Read())
                {
                    break;
                }

                record.IsLegacy = reader.ValueTextEquals("codealta.threadHeader"u8);
            }
            else if (reader.ValueTextEquals("raw"u8))
            {
                if (!reader.Read())
                {
                    break;
                }

                if (reader.TokenType != JsonTokenType.StartObject)
                {
                    if (!SkipCurrent(ref reader))
                    {
                        break;
                    }

                    continue;
                }

                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    if (reader.ValueTextEquals("kind"u8))
                    {
                        if (!reader.Read())
                        {
                            return record;
                        }

                        record.SessionKind = ReadString(ref reader);
                    }
                    else if (reader.ValueTextEquals("project_ref"u8))
                    {
                        if (!reader.Read())
                        {
                            return record;
                        }

                        record.ProjectRef = ReadString(ref reader);
                    }
                    else if (reader.ValueTextEquals("parent_session_id"u8))
                    {
                        if (!reader.Read())
                        {
                            return record;
                        }

                        record.ParentSessionId = ReadString(ref reader);
                    }
                    else if (reader.ValueTextEquals("created_by"u8))
                    {
                        if (!reader.Read())
                        {
                            return record;
                        }

                        record.CreatedBy = ParseActor(ref reader);
                    }
                    else if (reader.ValueTextEquals("title"u8))
                    {
                        if (!reader.Read())
                        {
                            return record;
                        }

                        record.Title = ReadString(ref reader);
                    }
                    else if (reader.ValueTextEquals("provider_key"u8))
                    {
                        if (!reader.Read())
                        {
                            return record;
                        }

                        record.ProviderKey = ReadString(ref reader);
                    }
                    else if (reader.ValueTextEquals("created_at"u8))
                    {
                        if (!reader.Read())
                        {
                            return record;
                        }

                        record.CreatedAt = reader.TokenType == JsonTokenType.String && reader.TryGetDateTimeOffset(out var created)
                            ? created.ToUniversalTime()
                            : null;
                    }
                    else if (!SkipValue(ref reader))
                    {
                        return record;
                    }
                }
            }
            else if (!SkipValue(ref reader))
            {
                break;
            }
        }

        return record;
    }

    private StateRecord? ParseState(ref Utf8JsonReader reader, ReadOnlySpan<byte> line, bool isFinalBlock, Func<ulong, bool>? isPromptSeen)
    {
        var record = new StateRecord();
        while (NextTopLevelProperty(ref reader))
        {
            if (!reader.ValueTextEquals("raw"u8))
            {
                if (!SkipValue(ref reader))
                {
                    break;
                }

                continue;
            }

            if (!reader.Read())
            {
                break;
            }

            if (reader.TokenType != JsonTokenType.StartObject)
            {
                if (!SkipCurrent(ref reader))
                {
                    break;
                }

                continue;
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("provider_key"u8))
                {
                    if (!reader.Read())
                    {
                        return record;
                    }

                    record.ProviderKey = ReadString(ref reader);
                }
                else if (reader.ValueTextEquals("model_id"u8))
                {
                    if (!reader.Read())
                    {
                        return record;
                    }

                    record.ModelId = ReadString(ref reader);
                }
                else if (reader.ValueTextEquals("reasoning_effort"u8))
                {
                    if (!reader.Read())
                    {
                        return record;
                    }

                    record.ReasoningEffort = ReadString(ref reader);
                }
                else if (reader.ValueTextEquals("permission_mode"u8))
                {
                    if (!reader.Read())
                    {
                        return record;
                    }

                    record.PermissionMode = ReadString(ref reader);
                }
                else if (reader.ValueTextEquals("agent_prompt_id"u8))
                {
                    if (!reader.Read())
                    {
                        return record;
                    }

                    record.AgentPromptId = ReadString(ref reader);
                }
                else if (reader.ValueTextEquals("parent_session_id"u8))
                {
                    if (!reader.Read())
                    {
                        return record;
                    }

                    record.ParentSessionId = ReadString(ref reader);
                }
                else if (reader.ValueTextEquals("created_by"u8))
                {
                    if (!reader.Read())
                    {
                        return record;
                    }

                    record.CreatedBy = ParseActor(ref reader);
                }
                else if (reader.ValueTextEquals("prompt_provenance"u8))
                {
                    if (!reader.Read())
                    {
                        return record;
                    }

                    if (reader.TokenType == JsonTokenType.StartArray)
                    {
                        // The list repeats every prompt of the session in each state record, up to 1.6 MB: it is not
                        // walked token by token. Its entries start with their identifier, which is looked at first.
                        ScanProvenance(line, (int)reader.TokenStartIndex, isPromptSeen, record);
                    }

                    // Nothing statistics read follows the list in the state, and the envelope is read from the end of the line.
                    return record;
                }
                else if (!SkipValue(ref reader))
                {
                    return record;
                }
            }

            break;
        }

        return record;
    }

    private void ScanProvenance(ReadOnlySpan<byte> line, int arrayStart, Func<ulong, bool>? isPromptSeen, StateRecord record)
    {
        var region = line[arrayStart..];
        var end = region.IndexOf(QueuedPromptsKey);
        if (end >= 0)
        {
            region = region[..end];
        }

        var position = 0;
        while (position < region.Length)
        {
            var found = region[position..].IndexOf(PromptIdSignature);
            if (found < 0)
            {
                break;
            }

            var entryStart = position + found;
            var idStart = entryStart + PromptIdSignature.Length;
            var idLength = region[idStart..].IndexOf((byte)'"');
            if (idLength < 0)
            {
                break;
            }

            var hash = JournalHash.Compute(region.Slice(idStart, idLength));
            position = idStart + idLength;
            if (isPromptSeen is not null && isPromptSeen(hash))
            {
                record.ProvenancePassedOver++;
                continue;
            }

            var entry = ParseProvenanceEntry(region[entryStart..]);
            if (entry is null)
            {
                continue;
            }

            entry.IdHash = hash;
            record.Provenance.Add(entry);
        }
    }

    private PromptProvenanceEntry? ParseProvenanceEntry(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json, isFinalBlock: false, default);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            return null;
        }

        var entry = new PromptProvenanceEntry();
        try
        {
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("kind"u8))
                {
                    if (!reader.Read())
                    {
                        return null;
                    }

                    entry.DispatchKind = ReadString(ref reader);
                }
                else if (reader.ValueTextEquals("run_id"u8))
                {
                    if (!reader.Read())
                    {
                        return null;
                    }

                    entry.RunId = ReadString(ref reader);
                }
                else if (reader.ValueTextEquals("queued"u8))
                {
                    if (!reader.Read())
                    {
                        return null;
                    }

                    entry.Queued = reader.TokenType == JsonTokenType.True;
                }
                else if (reader.ValueTextEquals("submitted_by"u8))
                {
                    if (!reader.Read())
                    {
                        return null;
                    }

                    entry.SubmittedBy = ParseActor(ref reader);
                }
                else if (reader.ValueTextEquals("created_at"u8))
                {
                    if (!reader.Read())
                    {
                        return null;
                    }

                    entry.CreatedAt = reader.TokenType == JsonTokenType.String && reader.TryGetDateTimeOffset(out var created)
                        ? created.ToUniversalTime()
                        : null;
                }
                else if (!SkipValue(ref reader))
                {
                    return null;
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }

        // A reader that stopped at the end of the data before the closing brace read an entry that was cut.
        return reader.TokenType == JsonTokenType.EndObject ? entry : null;
    }

    private JournalActor? ParseActor(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            SkipCurrent(ref reader);
            return null;
        }

        var actor = new JournalActor();
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("Kind"u8) || reader.ValueTextEquals("kind"u8))
            {
                if (!reader.Read())
                {
                    return actor;
                }

                actor.Kind = ReadString(ref reader);
            }
            else if (reader.ValueTextEquals("SourceSessionId"u8) || reader.ValueTextEquals("sourceSessionId"u8))
            {
                if (!reader.Read())
                {
                    return actor;
                }

                actor.SourceSessionId = ReadString(ref reader);
            }
            else if (reader.ValueTextEquals("AutomationId"u8) || reader.ValueTextEquals("automationId"u8))
            {
                if (!reader.Read())
                {
                    return actor;
                }

                actor.AutomationId = ReadString(ref reader);
            }
            else if (!SkipValue(ref reader))
            {
                return actor;
            }
        }

        return actor;
    }

    // ----- helpers -----

    // Moves to the next property of the record that is part of its payload; the envelope that follows it is read elsewhere.
    private static bool NextTopLevelProperty(ref Utf8JsonReader reader)
        => reader.Read() && reader.TokenType == JsonTokenType.PropertyName && !reader.ValueTextEquals("backendId"u8);

    // After a property name: reads its value and, for an object or an array, passes over all of it.
    private static bool SkipValue(ref Utf8JsonReader reader)
        => reader.Read() && SkipCurrent(ref reader);

    private static bool SkipCurrent(ref Utf8JsonReader reader)
        => reader.TokenType is not (JsonTokenType.StartObject or JsonTokenType.StartArray) || reader.TrySkip();

    private string? ReadString(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            return null;
        }

        return reader.ValueIsEscaped ? reader.GetString() : _strings.Get(reader.ValueSpan);
    }

    private static long? ReadLong(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.Number)
        {
            return null;
        }

        if (reader.TryGetInt64(out var value))
        {
            return value;
        }

        return reader.TryGetDouble(out var real) && double.IsFinite(real) ? (long)real : null;
    }

    private static double? ReadDouble(ref Utf8JsonReader reader)
        => reader.TokenType == JsonTokenType.Number && reader.TryGetDouble(out var value) && double.IsFinite(value) ? value : null;

    private static bool TryParsePhase(ReadOnlySpan<byte> value, out ToolPhase phase)
    {
        phase = ToolPhase.Started;
        if (value.SequenceEqual("Started"u8))
        {
            return true;
        }

        if (value.SequenceEqual("Completed"u8))
        {
            phase = ToolPhase.Completed;
            return true;
        }

        if (value.SequenceEqual("Failed"u8))
        {
            phase = ToolPhase.Failed;
            return true;
        }

        if (value.SequenceEqual("Canceled"u8))
        {
            phase = ToolPhase.Canceled;
            return true;
        }

        return false;
    }

    private static AgentActivityKind ParseActivityKind(ReadOnlySpan<byte> value)
    {
        if (value.SequenceEqual("ToolCall"u8))
        {
            return AgentActivityKind.ToolCall;
        }

        if (value.SequenceEqual("CommandExecution"u8))
        {
            return AgentActivityKind.CommandExecution;
        }

        if (value.SequenceEqual("FileChange"u8))
        {
            return AgentActivityKind.FileChange;
        }

        if (value.SequenceEqual("McpToolCall"u8))
        {
            return AgentActivityKind.McpToolCall;
        }

        if (value.SequenceEqual("DynamicToolCall"u8))
        {
            return AgentActivityKind.DynamicToolCall;
        }

        if (value.SequenceEqual("CollabAgentToolCall"u8))
        {
            return AgentActivityKind.CollabAgentToolCall;
        }

        if (value.SequenceEqual("WebSearch"u8))
        {
            return AgentActivityKind.WebSearch;
        }

        if (value.SequenceEqual("ImageGeneration"u8))
        {
            return AgentActivityKind.ImageGeneration;
        }

        if (value.SequenceEqual("Skill"u8))
        {
            return AgentActivityKind.Skill;
        }

        return AgentActivityKind.ToolCall;
    }

    // The extension of a path as it is written in a JSON string (separators may be escaped), lower case, without the dot.
    private string ExtensionOf(ReadOnlySpan<byte> rawPath)
    {
        var lastSeparator = Math.Max(rawPath.LastIndexOf((byte)'/'), rawPath.LastIndexOf((byte)'\\'));
        var name = rawPath[(lastSeparator + 1)..];
        var dot = name.LastIndexOf((byte)'.');
        if (dot <= 0 || dot == name.Length - 1 || name.Length - dot - 1 > 12)
        {
            return string.Empty;
        }

        Span<byte> extension = stackalloc byte[12];
        var tail = name[(dot + 1)..];
        for (var index = 0; index < tail.Length; index++)
        {
            var value = tail[index];
            if (value is >= (byte)'A' and <= (byte)'Z')
            {
                value = (byte)(value + 32);
            }
            else if (!(value is >= (byte)'a' and <= (byte)'z' || value is >= (byte)'0' and <= (byte)'9' || value is (byte)'_' or (byte)'-' or (byte)'+'))
            {
                return string.Empty;
            }

            extension[index] = value;
        }

        return _strings.Get(extension[..tail.Length]);
    }

    // The program a shell command runs: its first word, without quotes, without its directory or its .exe, in lower case.
    private string? ShellProgramOf(ReadOnlySpan<byte> raw, bool isEscaped)
    {
        // Only the start of the command is read, as the shell reads it: the escapes of JSON are undone first. The journals write a
        // quote, an ampersand and every character that is not ASCII as \uXXXX, and a backslash of the command as two: a backslash
        // that stays is one of the command (a path of Windows, or an escape of the shell).
        Span<byte> start = stackalloc byte[160];
        var length = 0;
        var read = 0;
        for (; read < raw.Length && length < start.Length; read++)
        {
            var value = raw[read];
            if (isEscaped && value == (byte)'\\' && read + 1 < raw.Length)
            {
                var next = raw[++read];
                if (next == (byte)'u')
                {
                    if (!TryReadHex4(raw, read + 1, out var code))
                    {
                        return null;
                    }

                    read += 4;
                    if (code >= 0x80)
                    {
                        // Half of a pair is no character by itself: it is kept as one that no program name holds.
                        if (code is >= 0xD800 and <= 0xDFFF)
                        {
                            value = (byte)'?';
                        }
                        else if (new Rune(code).TryEncodeToUtf8(start[length..], out var written))
                        {
                            length += written;
                            continue;
                        }
                        else
                        {
                            // The character does not fit in what is read: the command is cut before it.
                            read -= 5;
                            break;
                        }
                    }
                    else
                    {
                        value = (byte)code;
                    }
                }
                else
                {
                    value = next switch
                    {
                        (byte)'n' or (byte)'t' or (byte)'r' or (byte)'b' or (byte)'f' => (byte)' ',
                        _ => next,
                    };
                }
            }

            // A line end or a tab separates words as a space does.
            start[length++] = value < (byte)' ' ? (byte)' ' : value;
        }

        var text = start[..length];

        // Whether the command goes on beyond what was read: a word or a value that reaches the end is then not known to be whole.
        var cut = read < raw.Length || length == start.Length;
        var begin = 0;
        scoped ReadOnlySpan<byte> word;
        while (true)
        {
            while (begin < text.Length && text[begin] is (byte)' ' or (byte)'(' or (byte)'&' or (byte)';')
            {
                begin++;
            }

            // A quoted program ends at the closing quote (its path may hold spaces); any other word ends at a separator.
            var wordEnd = begin;
            if (begin < text.Length && text[begin] is (byte)'"' or (byte)'\'')
            {
                var quote = text[begin++];
                wordEnd = begin;
                while (wordEnd < text.Length && text[wordEnd] != quote)
                {
                    wordEnd++;
                }

                if (wordEnd == text.Length)
                {
                    return null;
                }

                word = text[begin..wordEnd];
                break;
            }

            while (wordEnd < text.Length && text[wordEnd] is not ((byte)' ' or (byte)';' or (byte)'|' or (byte)')' or (byte)'>' or (byte)'<' or (byte)'"' or (byte)'\'' or (byte)'='))
            {
                wordEnd++;
            }

            if (wordEnd == text.Length && cut)
            {
                return null;
            }

            if (wordEnd == text.Length || text[wordEnd] != (byte)'=')
            {
                word = text[begin..wordEnd];
                break;
            }

            // NAME=value before the program (`PGPASSWORD=... psql`, `$env:TOKEN='...'; gh`) sets a variable for it: the value may be
            // a secret, so it is stepped over and never kept. A value whose end cannot be told leaves the command without a program.
            if (!TrySkipAssignedValue(text, wordEnd + 1, cut, out begin))
            {
                return null;
            }
        }

        var slash = Math.Max(word.LastIndexOf((byte)'/'), word.LastIndexOf((byte)'\\'));
        if (slash >= 0)
        {
            word = word[(slash + 1)..];
        }

        if (word.Length > 4 && word[^4..].SequenceEqual(".exe"u8))
        {
            word = word[..^4];
        }

        if (word.IsEmpty || word.Length > 40)
        {
            return null;
        }

        Span<byte> lower = stackalloc byte[40];
        for (var index = 0; index < word.Length; index++)
        {
            var value = word[index];
            if (!IsProgramNameByte(value))
            {
                // An expression, a variable, a piece of text: not the name of a program.
                return null;
            }

            lower[index] = value is >= (byte)'A' and <= (byte)'Z' ? (byte)(value + 32) : value;
        }

        return _strings.Get(lower[..word.Length]);
    }

    // Steps over the value of a NAME=value word: a quoted text, or a plain word. False when the value has no end that can be told:
    // a quote that does not close, an escape (the backslash of a POSIX shell, the backtick of PowerShell, a doubled quote: the quote
    // or the space after it is part of the value), a command substitution, a text that goes on after the closing quote, another
    // operator of the shell, a value that goes beyond what was read. An empty value followed by a space is one too: PowerShell
    // allows the space, and the next word is then the value. What follows a value that was not stepped over whole may be a word of
    // it, so the command is then left without a program.
    private static bool TrySkipAssignedValue(ReadOnlySpan<byte> text, int valueStart, bool cut, out int next)
    {
        next = valueStart;
        if (next < text.Length && text[next] is (byte)'"' or (byte)'\'')
        {
            var quote = text[next++];
            while (next < text.Length && text[next] != quote)
            {
                if (quote == (byte)'"' && (text[next] is (byte)'\\' or (byte)'`' || (text[next] == (byte)'$' && next + 1 < text.Length && text[next + 1] == (byte)'(')))
                {
                    return false;
                }

                next++;
            }

            if (next == text.Length)
            {
                return false;
            }

            next++;
            return next == text.Length ? !cut : text[next] is (byte)' ' or (byte)';';
        }

        while (next < text.Length && text[next] is not ((byte)' ' or (byte)';'))
        {
            if (text[next] is (byte)'$' or (byte)'`' or (byte)'(' or (byte)')' or (byte)'{' or (byte)'"' or (byte)'\'' or (byte)'\\' or (byte)'|' or (byte)'&' or (byte)'<' or (byte)'>')
            {
                return false;
            }

            next++;
        }

        if (next == text.Length)
        {
            return !cut;
        }

        return next > valueStart || text[next] != (byte)' ';
    }

    private static bool TryReadHex4(ReadOnlySpan<byte> raw, int start, out int code)
    {
        code = 0;
        if (start + 4 > raw.Length)
        {
            return false;
        }

        for (var index = start; index < start + 4; index++)
        {
            var value = raw[index];
            var digit = value is >= (byte)'0' and <= (byte)'9' ? value - '0'
                : value is >= (byte)'a' and <= (byte)'f' ? value - 'a' + 10
                : value is >= (byte)'A' and <= (byte)'F' ? value - 'A' + 10
                : -1;
            if (digit < 0)
            {
                return false;
            }

            code = (code << 4) | digit;
        }

        return true;
    }

    private static bool IsProgramNameByte(byte value)
        => value is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9'
            or (byte)'.' or (byte)'_' or (byte)'-' or (byte)'+' or >= 0x80;
}
