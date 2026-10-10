using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CodeAlta.Plugin.Statistics.Tests;

/// <summary>
/// Writes synthetic session journals in the shape the application writes them: the discriminators first, the payload,
/// then the envelope (<c>backendId</c>, <c>sessionId</c>, <c>timestamp</c>, <c>runId</c>). Every text is a placeholder.
/// </summary>
internal sealed class JournalBuilder
{
    private readonly List<string> _lines = [];

    public JournalBuilder(string sessionId = "11111111-1111-1111-1111-111111111111", string provider = "codex")
    {
        SessionId = sessionId;
        Provider = provider;
    }

    public string SessionId { get; }

    public string Provider { get; set; }

    public IReadOnlyList<string> Lines => _lines;

    public static DateTimeOffset Time(int minute, double seconds = 0)
        => new DateTimeOffset(2026, 10, 9, 10, 0, 0, TimeSpan.Zero).AddMinutes(minute).AddSeconds(seconds);

    public static string Text(string value) => JsonSerializer.Serialize(value);

    public static string Json(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    public string Envelope(DateTimeOffset time, string? run, string? provider = null)
        => $"\"backendId\":\"{provider ?? Provider}\",\"sessionId\":\"{SessionId}\",\"timestamp\":\"{Json(time)}\""
           + (run is null ? string.Empty : $",\"runId\":\"{run}\"") + "}";

    public JournalBuilder Add(string line)
    {
        _lines.Add(line);
        return this;
    }

    public JournalBuilder AddRaw(string eventType, string rawJson, DateTimeOffset time, string? run = null)
        => Add($"{{\"$type\":\"raw\",\"backendEventType\":\"{eventType}\",\"raw\":{rawJson},{Envelope(time, run)}");

    public JournalBuilder Header(
        DateTimeOffset time,
        string kind = "ProjectSession",
        string? projectRef = "22222222-2222-2222-2222-222222222222",
        string? parent = null,
        string? createdByKind = null,
        string? createdBySession = null,
        string? automation = null,
        string title = "A title",
        bool legacy = false)
    {
        var raw = new StringBuilder("{\"schema_version\":1,");
        raw.Append(legacy ? $"\"thread_id\":\"{SessionId}\"," : $"\"session_id\":\"{SessionId}\",");
        raw.Append($"\"kind\":\"{kind}\",\"backend_id\":\"{Provider}\",\"provider_key\":\"{Provider}\"");
        if (projectRef is not null)
        {
            raw.Append($",\"project_ref\":\"{projectRef}\"");
        }

        if (parent is not null)
        {
            raw.Append($",\"parent_session_id\":\"{parent}\"");
        }

        if (createdByKind is not null)
        {
            raw.Append($",\"created_by\":{{\"Kind\":\"{createdByKind}\"");
            if (createdBySession is not null)
            {
                raw.Append($",\"SourceSessionId\":\"{createdBySession}\"");
            }

            if (automation is not null)
            {
                raw.Append($",\"AutomationId\":\"{automation}\"");
            }

            raw.Append($",\"CorrelationId\":\"c\",\"CreatedAt\":\"{Json(time)}\"}}");
        }

        raw.Append($",\"working_directory\":\"C:\\\\placeholder\",\"title\":{Text(title)},\"created_at\":\"{Json(time)}\"}}");
        return AddRaw(legacy ? "codealta.threadHeader" : "codealta.sessionHeader", raw.ToString(), time);
    }

    public JournalBuilder State(
        DateTimeOffset time,
        string? model = "gpt-6.1-sol",
        string? effort = "Low",
        string? permission = null,
        string? parent = null,
        string? createdByKind = null,
        string[]? provenance = null,
        int queued = 0,
        bool legacy = false)
    {
        var raw = new StringBuilder($"{{\"provider_key\":\"{Provider}\"");
        if (model is not null)
        {
            raw.Append($",\"model_id\":\"{model}\"");
        }

        if (effort is not null)
        {
            raw.Append($",\"reasoning_effort\":\"{effort}\"");
        }

        if (permission is not null)
        {
            raw.Append($",\"permission_mode\":\"{permission}\"");
        }

        raw.Append(",\"agent_prompt_id\":\"default\",\"archived\":false");
        if (parent is not null)
        {
            raw.Append($",\"parent_session_id\":\"{parent}\"");
        }

        if (createdByKind is not null)
        {
            raw.Append($",\"created_by\":{{\"Kind\":\"{createdByKind}\",\"CreatedAt\":\"{Json(time)}\"}}");
        }

        raw.Append(",\"prompt_provenance\":[").Append(string.Join(',', provenance ?? [])).Append("],\"queued_prompts\":[");
        for (var index = 0; index < queued; index++)
        {
            raw.Append(index > 0 ? "," : string.Empty).Append($"{{\"queue_item_id\":\"q{index}\",\"kind\":\"send\",\"prompt\":\"…\",\"state\":\"queued\",\"created_at\":\"{Json(time)}\"}}");
        }

        raw.Append("]}");
        return AddRaw(legacy ? "codealta.threadState" : "codealta.sessionState", raw.ToString(), time);
    }

    public static string Provenance(
        string promptId,
        string dispatchKind,
        DateTimeOffset createdAt,
        string? runId = null,
        bool queued = false,
        string? submittedByKind = null,
        string? sourceSession = null)
    {
        var builder = new StringBuilder($"{{\"prompt_id\":\"{promptId}\",\"kind\":\"{dispatchKind}\"");
        if (runId is not null)
        {
            builder.Append($",\"run_id\":\"{runId}\"");
        }

        builder.Append($",\"queued\":{(queued ? "true" : "false")},\"prompt_preview\":\"…\"");
        if (submittedByKind is not null)
        {
            builder.Append($",\"submitted_by\":{{\"Kind\":\"{submittedByKind}\"");
            if (sourceSession is not null)
            {
                builder.Append($",\"SourceSessionId\":\"{sourceSession}\"");
            }

            builder.Append($",\"CreatedAt\":\"{Json(createdAt)}\"}}");
        }

        return builder.Append($",\"created_at\":\"{Json(createdAt)}\"}}").ToString();
    }

    public JournalBuilder LocalSnapshot(DateTimeOffset time, int padding = 0)
        => AddRaw("local.sessionState", $"{{\"sessionId\":\"{SessionId}\",\"protocolFamily\":\"codex\",\"providerKey\":\"{Provider}\",\"updatedAt\":\"{Json(time)}\",\"loadedSkills\":[],\"padding\":\"{new string('x', padding)}\"}}", time);

    public JournalBuilder ModelChanged(DateTimeOffset time, string run, string provider, string model, string effort = "High", string agentPrompt = "default")
    {
        Provider = provider;
        return Add($"{{\"$type\":\"sessionUpdate\",\"kind\":\"ModelChanged\",\"details\":{{\"providerKey\":\"{provider}\",\"agentPromptId\":\"{agentPrompt}\",\"promptId\":\"{agentPrompt}\",\"modelId\":\"{model}\",\"reasoningEffort\":\"{effort}\"}},{Envelope(time, run)}");
    }

    public JournalBuilder User(
        DateTimeOffset time,
        string run,
        string text = "hello world",
        string[]? itemTypes = null,
        bool answer = false,
        string? sourceSession = null)
    {
        var items = new StringBuilder($"{{\"$type\":\"text\",\"value\":{Text(text)}}}");
        foreach (var type in itemTypes ?? [])
        {
            items.Append(type == "skill"
                ? ",{\"$type\":\"skill\",\"name\":\"s\",\"path\":\"p\"}"
                : $",{{\"$type\":\"{type}\",\"path\":\"C:\\\\x\",\"displayName\":\"x\"{(type == "localImage" ? ",\"mediaType\":\"image/png\"" : string.Empty)}}}");
        }

        return Add($"{{\"$type\":\"contentCompleted\",\"kind\":\"User\",\"contentId\":\"u-{_lines.Count}\",\"content\":{Text(text)},\"details\":{{\"items\":[{items}]}}"
                   + (answer ? ",\"ask_id\":\"ask-1\"" : string.Empty)
                   + (sourceSession is not null ? $",\"source_session_id\":\"{sourceSession}\"" : string.Empty)
                   + $",{Envelope(time, run)}");
    }

    public JournalBuilder Assistant(DateTimeOffset time, string run, string text = "an answer")
        => Add($"{{\"$type\":\"contentCompleted\",\"kind\":\"Assistant\",\"contentId\":\"a-{_lines.Count}\",\"content\":{Text(text)},{Envelope(time, run)}");

    public JournalBuilder Reasoning(DateTimeOffset time, string run, string text = "thinking")
        => Add($"{{\"$type\":\"contentCompleted\",\"kind\":\"Reasoning\",\"contentId\":\"r-{_lines.Count}\",\"content\":{Text(text)},{Envelope(time, run)}");

    public JournalBuilder Usage(
        DateTimeOffset time,
        string run,
        string model = "gpt-6.1-sol",
        long? input = 1000,
        long? output = 100,
        long? cacheRead = null,
        long? cacheWrite = null,
        long? cached = null,
        long? reasoning = null,
        double? cost = null,
        string? costUnit = null,
        double? duration = null,
        string? initiator = null,
        long window = 5000,
        long limit = 100000,
        string kind = "UsageUpdated",
        string? provider = null)
    {
        var op = new StringBuilder($"{{\"model\":\"{model}\"");
        void Number(string name, long? value)
        {
            if (value is { } v)
            {
                op.Append($",\"{name}\":{v}");
            }
        }

        Number("inputTokens", input);
        Number("outputTokens", output);
        Number("cacheReadTokens", cacheRead);
        Number("cacheWriteTokens", cacheWrite);
        Number("cachedInputTokens", cached);
        Number("reasoningTokens", reasoning);
        if (cost is { } c)
        {
            op.Append($",\"cost\":{c.ToString("R", CultureInfo.InvariantCulture)}");
        }

        if (duration is { } d)
        {
            op.Append($",\"durationMs\":{d.ToString("R", CultureInfo.InvariantCulture)}");
        }

        if (initiator is not null)
        {
            op.Append($",\"initiator\":\"{initiator}\"");
        }

        if (costUnit is not null)
        {
            op.Append($",\"costUnit\":\"{costUnit}\"");
        }

        op.Append('}');
        return Add($"{{\"$type\":\"sessionUpdate\",\"kind\":\"{kind}\",\"message\":\"usage\",\"usage\":{{\"window\":{{\"currentTokens\":{window},\"tokenLimit\":{limit},\"messageCount\":2,\"label\":\"w\"}},\"lastOperation\":{op},\"scope\":\"CurrentWindow\",\"source\":\"LocalProviderUsage\",\"updatedAt\":\"{Json(time)}\",\"currentTokens\":{window},\"tokenLimit\":{limit}}},{Envelope(time, run, provider)}");
    }

    public JournalBuilder Idle(DateTimeOffset time, string run, long input = 1000, long output = 100)
        => Add($"{{\"$type\":\"sessionUpdate\",\"kind\":\"Idle\",\"usage\":{{\"window\":{{\"currentTokens\":5000,\"tokenLimit\":100000}},\"lastOperation\":{{\"model\":\"gpt-6.1-sol\",\"inputTokens\":{input},\"outputTokens\":{output},\"cachedInputTokens\":0}},\"scope\":\"CurrentWindow\"}},{Envelope(time, run)}");

    public JournalBuilder Error(DateTimeOffset time, string run)
        => Add($"{{\"$type\":\"error\",\"message\":\"…\",\"exceptionInfo\":{{\"type\":\"T\",\"message\":\"…\",\"stackTrace\":\"…\",\"hResult\":-1}},{Envelope(time, run)}");

    public JournalBuilder Requested(DateTimeOffset time, string run, string id, string name, string argsJson = "{}")
        => Add($"{{\"$type\":\"activity\",\"kind\":\"ToolCall\",\"phase\":\"Requested\",\"activityId\":\"{id}\",\"name\":\"{name}\",\"details\":{{\"toolCallId\":\"{id}\",\"toolName\":\"{name}\",\"arguments\":{argsJson},\"readFiles\":[],\"modifiedFiles\":[]}},{Envelope(time, run)}");

    public JournalBuilder ToolStarted(DateTimeOffset time, string run, string id, string name, string argsJson = "{}", string[]? readFiles = null, string[]? modifiedFiles = null)
        => Add($"{{\"$type\":\"activity\",\"kind\":\"ToolCall\",\"phase\":\"Started\",\"activityId\":\"{id}\",\"name\":\"{name}\",\"details\":{{\"toolCallId\":\"{id}\",\"toolName\":\"{name}\",\"arguments\":{argsJson},\"readFiles\":{PathList(readFiles)},\"modifiedFiles\":{PathList(modifiedFiles)}}},{Envelope(time, run)}");

    public JournalBuilder ToolDone(
        DateTimeOffset time,
        string run,
        string id,
        string name,
        string phase = "Completed",
        string argsJson = "{}",
        string[]? readFiles = null,
        string[]? modifiedFiles = null,
        string? diff = null,
        string resultText = "ok",
        string kind = "ToolCall")
        => Add($"{{\"$type\":\"activity\",\"kind\":\"{kind}\",\"phase\":\"{phase}\",\"activityId\":\"{id}\",\"name\":\"{name}\""
               + (phase == "Failed" ? ",\"message\":\"…\"" : string.Empty)
               + $",\"details\":{{\"toolCallId\":\"{id}\",\"toolName\":\"{name}\",\"arguments\":{argsJson},\"readFiles\":{PathList(readFiles)},\"modifiedFiles\":{PathList(modifiedFiles)}"
               + (diff is null ? string.Empty : $",\"diff\":{Text(diff)}")
               + $",\"result\":{{\"success\":{(phase == "Failed" ? "false" : "true")},\"items\":[{{\"$type\":\"text\",\"value\":{Text(resultText)}}}]"
               + (phase == "Failed" ? ",\"error\":\"…\"" : string.Empty)
               + $"}}}},{Envelope(time, run)}");

    public JournalBuilder ToolOutput(DateTimeOffset time, string run, string id, string text = "output")
        => Add($"{{\"$type\":\"contentCompleted\",\"kind\":\"ToolOutput\",\"contentId\":\"o-{id}\",\"parentActivityId\":\"{id}\",\"content\":{Text(text)},\"details\":{{\"toolCallId\":\"{id}\"}},{Envelope(time, run)}");

    public JournalBuilder DiffUpdated(DateTimeOffset time, string run)
        => Add($"{{\"$type\":\"sessionUpdate\",\"kind\":\"DiffUpdated\",\"message\":\"diff\",\"details\":{{\"diff\":\"+x\\n-y\"}},{Envelope(time, run)}");

    public JournalBuilder Compaction(DateTimeOffset time, string run, string trigger = "threshold", long before = 200000, long after = 20000)
        => Add($"{{\"$type\":\"sessionUpdate\",\"kind\":\"CompactionCompleted\",\"message\":\"compacted\",\"details\":{{\"schema\":\"s\",\"contentId\":\"c\",\"trigger\":\"{trigger}\",\"summaryMarkdown\":\"…\",\"tokensBefore\":{before},\"tokensAfter\":{after},\"tokensRemoved\":{before - after}}},\"usage\":{{\"scope\":\"Compaction\"}},{Envelope(time, run)}");

    public JournalBuilder SystemPrompt(DateTimeOffset time, string run, int systemChars = 5000, int developerChars = 20000, string reason = "session_start")
        => Add($"{{\"$type\":\"system_prompt\",\"reason\":\"{reason}\",\"effectivePromptHash\":\"h\",\"agentPromptId\":\"default\",\"systemMessage\":\"{new string('s', systemChars)}\",\"developerInstructions\":\"{new string('d', developerChars)}\",\"providerPayloadSummary\":{{\"channelMapping\":\"m\",\"appliedToProvider\":true,\"lossy\":false}},\"statistics\":{{\"systemApproxTokens\":1,\"developerApproxTokens\":2,\"totalApproxTokens\":{(systemChars + developerChars) / 4},\"systemChars\":{systemChars},\"developerChars\":{developerChars}}},\"change\":{{\"kind\":\"initial\",\"addedParts\":[],\"removedParts\":[],\"changedParts\":[]}},{Envelope(time, run)}");

    public JournalBuilder LocalMessage(DateTimeOffset time, string run, string type = "local.assistantMessage")
        => AddRaw(type, "{\"role\":\"Assistant\",\"parts\":[{\"$type\":\"reasoning\",\"value\":\"…\"}]}", time, run);

    public byte[] ToBytes(bool trailingNewline = true)
    {
        var text = string.Join('\n', _lines) + (trailingNewline && _lines.Count > 0 ? "\n" : string.Empty);
        return new UTF8Encoding(false).GetBytes(text);
    }

    public MemoryStream ToStream(bool trailingNewline = true) => new(ToBytes(trailingNewline));

    private static string PathList(string[]? paths)
        => "[" + string.Join(',', (paths ?? []).Select(Text)) + "]";
}
