using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Agent.Telemetry;

/// <summary>What the spans of the runs of a session say of it.</summary>
/// <param name="SessionId">The session (<c>gen_ai.conversation.id</c>).</param>
/// <param name="ProviderName">The provider (<c>gen_ai.provider.name</c>).</param>
/// <param name="ProviderKey">The key of the configured provider.</param>
/// <param name="Model">The model requested, when the session names one.</param>
/// <param name="ReasoningLevel">The reasoning effort requested, when the session names one.</param>
/// <param name="AgentPromptId">The agent prompt of the session, when it has one.</param>
/// <param name="CaptureContent">
/// Whether the spans record the content of the run: its prompts and its answer, the arguments and the results of its tool
/// calls (<see cref="OpenTelemetryEnvironment.CapturesMessageContent"/>).
/// </param>
internal sealed record AgentRunTraceInfo(
    string SessionId,
    string ProviderName,
    string ProviderKey,
    string? Model,
    string? ReasoningLevel,
    string? AgentPromptId,
    bool CaptureContent = false);

/// <summary>
/// The spans of one run of a session, made from the normalized events of the run, the same for every provider: the
/// <c>invoke_agent</c> span of the run, a <c>chat</c> span for each usage the provider reports for a request to its model,
/// and an <c>execute_tool</c> span for each tool call, the tool calls of a script under the call that runs it.
/// </summary>
/// <remarks>
/// The events of a run reach <see cref="Observe"/> one at a time, and the run starts and ends it from its own thread: a
/// lock orders them. The spans never become <see cref="Activity.Current"/>, so that they are not the parent of what the
/// thread that reports an event does next. A request starts when the run asks its provider for an answer, or when the
/// last tool call before it ended: a provider that runs its own loop (an agent CLI) reports several requests in one.
/// The content is recorded only when <see cref="AgentRunTraceInfo.CaptureContent"/> asks for it: on the span of the run,
/// its prompts (<c>gen_ai.input.messages</c>) and its last answer (<c>gen_ai.output.messages</c>); on the span of a tool
/// call, its arguments and its result, when the provider reports them. Each text is cut at <see cref="MaxContentLength"/>
/// characters, so that a collector does not refuse the span. The requests record no content: the whole conversation of
/// each one would be large, and an agent that runs its own loop does not report it.
/// </remarks>
internal sealed class AgentRunTrace
{
    private readonly Lock _gate = new();
    private readonly AgentRunTraceInfo _info;
    private readonly AgentRunId _runId;
    private readonly Activity _run;
    private readonly Dictionary<string, Activity> _tools = new(StringComparer.Ordinal);
    private readonly List<string> _prompts = [];
    private string? _answer;
    private DateTimeOffset _requestStart;
    private long _inputTokens, _outputTokens, _cacheReadTokens, _cacheWriteTokens, _reasoningTokens;
    private bool _usageReported, _ended;

    private AgentRunTrace(AgentRunTraceInfo info, AgentRunId runId, Activity run, DateTimeOffset start)
    {
        _info = info;
        _runId = runId;
        _run = run;
        _requestStart = start;
    }

    /// <summary>The length at which a text of the content is cut, in characters.</summary>
    internal const int MaxContentLength = 32 * 1024;

    /// <summary>Starts the span of a run, when something listens to the spans of CodeAlta.</summary>
    /// <param name="info">The session.</param>
    /// <param name="runId">The run.</param>
    /// <returns>The trace of the run, or <see langword="null"/> when nothing records it.</returns>
    public static AgentRunTrace? Start(AgentRunTraceInfo info, AgentRunId runId)
    {
        if (!AgentTelemetry.Source.HasListeners())
        {
            return null;
        }

        var start = DateTimeOffset.UtcNow;
        List<KeyValuePair<string, object?>> tags =
        [
            new("gen_ai.operation.name", "invoke_agent"),
            new("gen_ai.provider.name", info.ProviderName),
            new("gen_ai.agent.name", AgentTelemetry.AgentName),
            new("gen_ai.conversation.id", info.SessionId),
            new("codealta.provider.key", info.ProviderKey),
            new("codealta.run.id", runId.Value),
        ];
        AddOptional(tags, "gen_ai.request.model", info.Model);
        AddOptional(tags, "gen_ai.request.reasoning.level", info.ReasoningLevel);
        AddOptional(tags, "codealta.agent_prompt.id", info.AgentPromptId);
        // The run is a child of what runs it in this process, when that is traced.
        var run = StartSpan($"invoke_agent {AgentTelemetry.AgentName}", default, tags, start);
        return run is null ? null : new AgentRunTrace(info, runId, run, start);
    }

    /// <summary>Notes that the run asks its provider for an answer: the next request starts now.</summary>
    public void RequestStarting()
    {
        lock (_gate)
        {
            _requestStart = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>Records what an event of the session says of the run: a tool call that starts or ends, the usage of a request.</summary>
    /// <param name="event">The event; those of other runs, and those outside the runs, are ignored.</param>
    public void Observe(AgentEvent @event)
    {
        // The deltas, most of the events of a run, are left at once.
        if (@event is not (AgentActivityEvent or AgentSessionUpdateEvent or AgentContentCompletedEvent) || @event.RunId != _runId)
        {
            return;
        }

        lock (_gate)
        {
            if (_ended)
            {
                return;
            }

            switch (@event)
            {
                case AgentActivityEvent { Phase: AgentActivityPhase.Started } activity when IsToolCall(activity.Kind):
                    StartTool(activity);
                    break;
                case AgentActivityEvent { Phase: AgentActivityPhase.Completed or AgentActivityPhase.Failed or AgentActivityPhase.Canceled } activity
                    when IsToolCall(activity.Kind):
                    EndTool(activity);
                    break;
                case AgentSessionUpdateEvent { Kind: AgentSessionUpdateKind.UsageUpdated, Usage.LastOperation: { } operation } usage:
                    RecordRequest(operation, usage.Timestamp);
                    break;
                case AgentContentCompletedEvent { Kind: AgentContentKind.User } prompt when _info.CaptureContent:
                    _prompts.Add(prompt.Content);
                    break;
                case AgentContentCompletedEvent { Kind: AgentContentKind.Assistant } answer when _info.CaptureContent:
                    _answer = answer.Content;
                    break;
            }
        }
    }

    /// <summary>Ends the span of the run, and those of its tool calls still open.</summary>
    /// <param name="failure">What failed the run, or <see langword="null"/> when it ended well.</param>
    public void End(Exception? failure)
    {
        lock (_gate)
        {
            if (_ended)
            {
                return;
            }

            _ended = true;
            var now = DateTimeOffset.UtcNow;
            foreach (var tool in _tools.Values)
            {
                Fail(tool, failure is null ? "interrupted" : ErrorType(failure));
                Stop(tool, now);
            }

            _tools.Clear();
            if (_usageReported)
            {
                SetUsage(_run, _inputTokens, _outputTokens, _cacheReadTokens, _cacheWriteTokens, _reasoningTokens);
            }

            if (_info.CaptureContent)
            {
                if (_prompts.Count > 0) _run.SetTag("gen_ai.input.messages", Messages("user", _prompts));
                if (_answer is not null) _run.SetTag("gen_ai.output.messages", Messages("assistant", [_answer]));
            }

            if (failure is not null)
            {
                Fail(_run, ErrorType(failure));
            }

            Stop(_run, now);
        }
    }

    private void StartTool(AgentActivityEvent activity)
    {
        if (_tools.ContainsKey(activity.ActivityId))
        {
            return;
        }

        var name = string.IsNullOrWhiteSpace(activity.Name) ? "tool" : activity.Name;
        // A call of a script is a child of the call that runs the script.
        var parent = activity.ParentActivityId is { } parentId && _tools.TryGetValue(parentId, out var parentTool) ? parentTool.Context : _run.Context;
        List<KeyValuePair<string, object?>> tags =
        [
            new("gen_ai.operation.name", "execute_tool"),
            new("gen_ai.tool.name", name),
            new("gen_ai.tool.call.id", activity.ActivityId),
            new("gen_ai.agent.name", AgentTelemetry.AgentName),
            new("gen_ai.conversation.id", _info.SessionId),
        ];
        if (_info.CaptureContent && Property(activity.Details, "arguments", "input") is { } arguments)
        {
            tags.Add(new("gen_ai.tool.call.arguments", arguments));
        }

        if (StartSpan($"execute_tool {name}", parent, tags, activity.Timestamp) is { } tool)
        {
            _tools[activity.ActivityId] = tool;
        }
    }

    private void EndTool(AgentActivityEvent activity)
    {
        if (!_tools.Remove(activity.ActivityId, out var tool))
        {
            return;
        }

        if (_info.CaptureContent && (Property(activity.Details, "result", "output") ?? Cut(activity.Message)) is { } result)
        {
            tool.SetTag("gen_ai.tool.call.result", result);
        }

        // The message of a failure is not in the status: it is made of the arguments and the output of the tool.
        if (activity.Phase is AgentActivityPhase.Failed)
        {
            Fail(tool, "tool_error");
        }
        else if (activity.Phase is AgentActivityPhase.Canceled)
        {
            Fail(tool, "cancelled");
        }

        Stop(tool, activity.Timestamp);
        // The next request of the run starts once its tool calls end.
        if (activity.ParentActivityId is null && activity.Timestamp > _requestStart)
        {
            _requestStart = activity.Timestamp;
        }
    }

    private void RecordRequest(AgentOperationUsageSnapshot operation, DateTimeOffset end)
    {
        var model = _info.Model ?? operation.Model;
        List<KeyValuePair<string, object?>> tags =
        [
            new("gen_ai.operation.name", "chat"),
            new("gen_ai.provider.name", _info.ProviderName),
            new("gen_ai.conversation.id", _info.SessionId),
        ];
        AddOptional(tags, "gen_ai.request.model", model);
        AddOptional(tags, "gen_ai.request.reasoning.level", operation.ReasoningEffort ?? _info.ReasoningLevel);
        var start = _requestStart <= end ? _requestStart : end;
        if (StartSpan(model is null ? "chat" : $"chat {model}", _run.Context, tags, start, ActivityKind.Client) is { } request)
        {
            if (operation.Model is { Length: > 0 } responseModel) request.SetTag("gen_ai.response.model", responseModel);
            var input = AgentInputTokenUsage.From(operation);
            SetUsage(request, input?.Total, operation.OutputTokens, input?.CacheRead, input?.CacheWrite, operation.ReasoningTokens);
            Stop(request, end);
        }

        var counted = AgentInputTokenUsage.From(operation);
        _inputTokens += counted?.Total ?? 0;
        _cacheReadTokens += counted?.CacheRead ?? 0;
        _cacheWriteTokens += counted?.CacheWrite ?? 0;
        _outputTokens += operation.OutputTokens ?? 0;
        _reasoningTokens += operation.ReasoningTokens ?? 0;
        _usageReported = true;
        _requestStart = end;
    }

    // The input holds what the cache read and wrote, as the conventions count it; a count of zero for the cache or the
    // reasoning is left out, as they ask.
    private static void SetUsage(Activity activity, long? input, long? output, long? cacheRead, long? cacheWrite, long? reasoning)
    {
        if (input is { } inputTokens) activity.SetTag("gen_ai.usage.input_tokens", inputTokens);
        if (output is { } outputTokens) activity.SetTag("gen_ai.usage.output_tokens", outputTokens);
        if (cacheRead is > 0) activity.SetTag("gen_ai.usage.cache_read.input_tokens", cacheRead.Value);
        if (cacheWrite is > 0) activity.SetTag("gen_ai.usage.cache_write.input_tokens", cacheWrite.Value);
        if (reasoning is > 0) activity.SetTag("gen_ai.usage.reasoning.output_tokens", reasoning.Value);
    }

    private static Activity? StartSpan(string name, ActivityContext parent, List<KeyValuePair<string, object?>> tags, DateTimeOffset start,
        ActivityKind kind = ActivityKind.Internal)
    {
        var current = Activity.Current;
        var activity = AgentTelemetry.Source.StartActivity(name, kind, parent, tags, links: null, start);
        // A started activity becomes the current one of this thread: the spans of CodeAlta only ever have explicit parents.
        Activity.Current = current;
        return activity;
    }

    private static void Stop(Activity activity, DateTimeOffset end)
    {
        activity.SetEndTime(end.UtcDateTime);
        activity.Stop();
    }

    private static void Fail(Activity activity, string errorType)
    {
        activity.SetTag("error.type", errorType);
        activity.SetStatus(ActivityStatusCode.Error);
    }

    // The kind of a failure of the provider (RateLimited, Auth...), else the type of the exception; never its message,
    // which can hold what the run was about.
    private static string ErrorType(Exception failure)
        => AgentFailureClassifier.Classify(failure) is { Kind: not AgentFailureKind.Other } classified
            ? classified.Kind.ToString()
            : failure.GetType().FullName ?? failure.GetType().Name;

    // The messages of one role, in the JSON of the conventions: [{"role":"user","parts":[{"type":"text","content":"..."}]}].
    private static string Messages(string role, IReadOnlyList<string> texts)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var text in texts)
            {
                writer.WriteStartObject();
                writer.WriteString("role", role);
                writer.WriteStartArray("parts");
                writer.WriteStartObject();
                writer.WriteString("type", "text");
                writer.WriteString("content", Cut(text));
                writer.WriteEndObject();
                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    // The first of two properties of the details of a tool call, as the text of its JSON (a string as its value).
    private static string? Property(JsonElement? details, string name, string otherName)
    {
        if (details is not { ValueKind: JsonValueKind.Object } value
            || !(value.TryGetProperty(name, out var property) || value.TryGetProperty(otherName, out property))
            || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return Cut(property.ValueKind == JsonValueKind.String ? property.GetString() : property.GetRawText());
    }

    private static string? Cut(string? text)
        => text is null || text.Length <= MaxContentLength ? text : string.Concat(text.AsSpan(0, MaxContentLength), "…");

    private static void AddOptional(List<KeyValuePair<string, object?>> tags, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            tags.Add(new(name, value));
        }
    }

    private static bool IsToolCall(AgentActivityKind kind)
        => kind is AgentActivityKind.ToolCall or AgentActivityKind.CommandExecution or AgentActivityKind.FileChange or AgentActivityKind.McpToolCall
            or AgentActivityKind.DynamicToolCall or AgentActivityKind.CollabAgentToolCall or AgentActivityKind.Skill;
}
