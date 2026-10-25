using System.Collections.Frozen;
using System.Text;
using System.Text.Json;
using CodeAlta.Acp;
using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;
using static CodeAlta.Cli.CodeAltaAcpExtensions;

namespace CodeAlta.Cli;

/// <summary>Receives what <see cref="AcpEventProjector"/> makes of the events of a run.</summary>
internal interface IAcpEventSink
{
    /// <summary>Takes a <c>session/update</c> of a session: the root of the run, or a session it started.</summary>
    ValueTask SessionUpdateAsync(string sessionId, SessionUpdate update, CancellationToken cancellationToken);

    /// <summary>Takes an extension notification (<c>_codealta/...</c>) for what ACP has no update for.</summary>
    ValueTask ExtensionAsync(string method, JsonElement parameters, CancellationToken cancellationToken);
}

/// <summary>
/// Projects the runtime events of a headless run (<see cref="HeadlessSessionEvent"/>) onto the Agent Client Protocol: the
/// one projection that <c>altacli run --format jsonl</c> writes as lines and <c>altacli acp</c> sends to its client.
/// </summary>
/// <remarks>
/// <para>
/// Every update is built with the builders of <c>CodeAlta.Acp</c> (<see cref="AcpSessionUpdates"/>) and carries the
/// session it belongs to, a session the root started included:
/// </para>
/// <list type="bullet">
/// <item><c>agent_message_chunk</c> and <c>agent_thought_chunk</c> for the deltas of the answer and of the reasoning, for
/// the whole text when the provider streamed none, and for what the stream of events dropped once the text is complete
/// (with an error <c>events_dropped</c> when a part in the middle was lost);</item>
/// <item><c>tool_call</c> when a tool call starts (kind, title, locations, raw input, and in <c>_meta.codealta</c> the name
/// of the CodeAlta tool and the run), <c>tool_call_update</c> when it ends (status) and when its output is known (a bounded
/// text, a diff);</item>
/// <item><c>plan</c> for the plan snapshots, <c>usage_update</c> for the use of the context window when it is known.</item>
/// </list>
/// <para>
/// What ACP has no update for is a custom notification of <see cref="CodeAltaAcpExtensions"/>, its parameters always naming
/// <c>sessionId</c>: a session the run started (<see cref="CodeAltaAcpExtensions.SessionStartedMethod"/>), a compaction, a
/// retry, a fallback to another model, an error.
/// </para>
/// <para>Events are projected one at a time, in order, as the one reader of the driver gives them.</para>
/// </remarks>
internal sealed class AcpEventProjector
{
    /// <summary>The most characters of the output of a tool an update carries.</summary>
    public const int MaximumToolOutput = 8 * 1024;

    private readonly IAcpEventSink _sink;
    private readonly Func<string, CancellationToken, ValueTask<SessionViewDescriptor?>>? _findSession;
    // The sessions seen, and the tool calls announced, by session and call.
    private readonly HashSet<string> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<(string Session, string Call)> _tools = [];
    // The texts streamed and not completed yet, by session and content, with their run, and the content each channel (answer
    // or thought) of a session streamed last: a completed text is compared with what was streamed of it, and only what the
    // client misses is sent (all of it when nothing was streamed). They are forgotten when the run of the session ends.
    private readonly Dictionary<(string Session, string Content), StreamedText> _streamed = [];
    private readonly Dictionary<(string Session, bool Thought), string> _lastStreamed = [];

    /// <summary>Creates a projection to a sink.</summary>
    /// <param name="sink">What takes the updates and the notifications.</param>
    /// <param name="findSession">Finds a session the run started, for the parent and the title it is announced with; null announces it without them.</param>
    public AcpEventProjector(IAcpEventSink sink, Func<string, CancellationToken, ValueTask<SessionViewDescriptor?>>? findSession = null)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _sink = sink;
        _findSession = findSession;
    }

    /// <summary>Projects one event.</summary>
    public async ValueTask ProjectAsync(HeadlessSessionEvent item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        var sessionId = item.Event.SessionId;
        if (string.IsNullOrEmpty(sessionId)) return;
        if (_sessions.Add(sessionId) && !string.Equals(sessionId, item.RootSessionId, StringComparison.OrdinalIgnoreCase))
        {
            await AnnounceSessionAsync(item, cancellationToken).ConfigureAwait(false);
        }

        switch (item.Event)
        {
            case SessionAgentEvent { Event: var agentEvent }:
                await ProjectAgentEventAsync(sessionId, agentEvent, cancellationToken).ConfigureAwait(false);
                break;
            case SessionHostEvent { Kind: AgentSessionUpdateKind.ProviderFallback } host:
                await ExtensionAsync(FallbackMethod, sessionId, writer => writer.WriteString("message", host.Message), cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>
    /// Replays the stored history of a session as the updates of a conversation, as <c>session/load</c> sends them: the
    /// messages of the user (<c>user_message_chunk</c>), the answers and the reasoning of the agent, each whole, and the
    /// tool calls with their output and their final status, and the plans. What is not conversation (usage, compaction,
    /// retries, errors) is not replayed.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="history">Its stored events, in order (<see cref="SessionRuntimeService.TryReadStoredHistoryAsync(SessionViewDescriptor, CancellationToken)"/>).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async ValueTask ReplayAsync(string sessionId, IEnumerable<AgentEvent> history, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(history);
        foreach (var agentEvent in history)
        {
            switch (agentEvent)
            {
                case AgentContentCompletedEvent { Kind: AgentContentKind.User, Content.Length: > 0 } user:
                    await UpdateAsync(sessionId, AcpSessionUpdates.UserMessageChunk(user.Content), cancellationToken).ConfigureAwait(false);
                    break;
                case AgentContentCompletedEvent { Kind: AgentContentKind.Assistant, Content.Length: > 0 } answer:
                    await UpdateAsync(sessionId, AcpSessionUpdates.AgentMessageChunk(answer.Content), cancellationToken).ConfigureAwait(false);
                    break;
                case AgentContentCompletedEvent { Kind: AgentContentKind.Reasoning or AgentContentKind.ReasoningSummary, Content.Length: > 0 } thought:
                    await UpdateAsync(sessionId, AcpSessionUpdates.AgentThoughtChunk(thought.Content), cancellationToken).ConfigureAwait(false);
                    break;
                case AgentContentDeltaEvent or AgentSessionUpdateEvent or AgentErrorEvent
                    or AgentContentCompletedEvent { Kind: AgentContentKind.User or AgentContentKind.Assistant or AgentContentKind.Reasoning or AgentContentKind.ReasoningSummary }:
                    break;
                default:
                    await ProjectAgentEventAsync(sessionId, agentEvent, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
    }

    private async ValueTask AnnounceSessionAsync(HeadlessSessionEvent item, CancellationToken cancellationToken)
    {
        var sessionId = item.Event.SessionId;
        var descriptor = item.Event is SessionCatalogRuntimeEvent catalog ? catalog.Session : null;
        if (descriptor is null && _findSession is not null)
        {
            try
            {
                descriptor = await _findSession(sessionId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                // Announced without its parent and its title.
            }
        }

        await ExtensionAsync(SessionStartedMethod, sessionId, writer =>
        {
            WriteStringOrNull(writer, "parentSessionId", descriptor?.ParentSessionId ?? item.RootSessionId);
            WriteStringOrNull(writer, "title", string.IsNullOrWhiteSpace(descriptor?.Title) ? null : descriptor.Title);
        }, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask ProjectAgentEventAsync(string sessionId, AgentEvent agentEvent, CancellationToken cancellationToken)
    {
        switch (agentEvent)
        {
            case AgentContentDeltaEvent { Kind: AgentContentKind.Assistant, Delta.Length: > 0 } delta:
                Streamed(sessionId, thought: false, delta);
                await UpdateAsync(sessionId, AcpSessionUpdates.AgentMessageChunk(delta.Delta), cancellationToken).ConfigureAwait(false);
                break;
            case AgentContentDeltaEvent { Kind: AgentContentKind.Reasoning or AgentContentKind.ReasoningSummary, Delta.Length: > 0 } delta:
                Streamed(sessionId, thought: true, delta);
                await UpdateAsync(sessionId, AcpSessionUpdates.AgentThoughtChunk(delta.Delta), cancellationToken).ConfigureAwait(false);
                break;
            case AgentContentCompletedEvent { Kind: AgentContentKind.Assistant } answer:
                await CompleteAsync(sessionId, thought: false, answer, cancellationToken).ConfigureAwait(false);
                break;
            case AgentContentCompletedEvent { Kind: AgentContentKind.Reasoning or AgentContentKind.ReasoningSummary } thought:
                await CompleteAsync(sessionId, thought: true, thought, cancellationToken).ConfigureAwait(false);
                break;
            case AgentSessionUpdateEvent { Kind: AgentSessionUpdateKind.Idle }:
                ForgetStreamed(sessionId);
                break;
            case AgentActivityEvent activity when IsToolActivity(activity.Kind) && !string.IsNullOrEmpty(activity.ActivityId):
                await ProjectToolActivityAsync(sessionId, activity, cancellationToken).ConfigureAwait(false);
                break;
            case AgentContentCompletedEvent { Kind: AgentContentKind.ToolOutput, ParentActivityId: { Length: > 0 } callId } output
                when _tools.Contains((sessionId, callId)):
                await UpdateAsync(sessionId, AcpSessionUpdates.ToolCallUpdate(new ToolCallUpdate
                {
                    ToolCallId = callId,
                    Content = ToolOutputContent(output),
                }), cancellationToken).ConfigureAwait(false);
                break;
            case AgentPlanSnapshotEvent { Snapshot.Steps: { } steps }:
                await UpdateAsync(sessionId, AcpSessionUpdates.Plan(steps.Select(static step => new PlanEntry
                {
                    Content = step.Text,
                    Priority = PlanEntryPriority.Medium,
                    Status = step.Status switch
                    {
                        AgentPlanStepStatus.Completed => PlanEntryStatus.Completed,
                        AgentPlanStepStatus.InProgress => PlanEntryStatus.InProgress,
                        _ => PlanEntryStatus.Pending,
                    },
                })), cancellationToken).ConfigureAwait(false);
                break;
            case AgentSessionUpdateEvent update:
                await ProjectSessionUpdateAsync(sessionId, update, cancellationToken).ConfigureAwait(false);
                break;
            case AgentErrorEvent error:
                // The error of a run ends it, without completing what it streamed.
                ForgetStreamed(sessionId);
                await ExtensionAsync(ErrorMethod, sessionId, writer =>
                {
                    writer.WriteString("kind", FailureKindName(error.Failure?.Kind ?? AgentFailureKind.Other));
                    writer.WriteString("message", error.Message);
                }, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private async ValueTask ProjectSessionUpdateAsync(string sessionId, AgentSessionUpdateEvent update, CancellationToken cancellationToken)
    {
        switch (update.Kind)
        {
            case AgentSessionUpdateKind.UsageUpdated when update.Usage is { CurrentTokens: { } used, TokenLimit: { } size } && used >= 0 && size > 0:
                await UpdateAsync(sessionId, AcpSessionUpdates.Usage((ulong)used, (ulong)size), cancellationToken).ConfigureAwait(false);
                break;
            case AgentSessionUpdateKind.CompactionStarted or AgentSessionUpdateKind.CompactionCompleted:
                await ExtensionAsync(CompactionMethod, sessionId, writer =>
                {
                    writer.WriteString("phase", update.Kind == AgentSessionUpdateKind.CompactionStarted ? "started" : "completed");
                    WriteStringOrNull(writer, "message", update.Message);
                }, cancellationToken).ConfigureAwait(false);
                break;
            case AgentSessionUpdateKind.Reconnecting:
                await ExtensionAsync(RetryMethod, sessionId, writer => WriteStringOrNull(writer, "message", update.Message), cancellationToken).ConfigureAwait(false);
                break;
            case AgentSessionUpdateKind.ProviderFallback:
                await ExtensionAsync(FallbackMethod, sessionId, writer =>
                {
                    var details = update.Details ?? default;
                    WriteStringOrNull(writer, "from", AcpJson.GetString(details, "from"));
                    WriteStringOrNull(writer, "to", AcpJson.GetString(details, "to"));
                    WriteStringOrNull(writer, "reason", AcpJson.GetString(details, "reason"));
                    WriteStringOrNull(writer, "message", update.Message);
                }, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private async ValueTask ProjectToolActivityAsync(string sessionId, AgentActivityEvent activity, CancellationToken cancellationToken)
    {
        var callId = activity.ActivityId;
        var details = activity.Details ?? default;
        var name = activity.Name ?? AcpJson.GetString(details, "toolName") ?? "tool";
        var announced = _tools.Contains((sessionId, callId));
        switch (activity.Phase)
        {
            case AgentActivityPhase.Requested or AgentActivityPhase.Started when !announced:
                _tools.Add((sessionId, callId));
                await UpdateAsync(sessionId, AcpSessionUpdates.ToolCall(new ToolCall
                {
                    ToolCallId = callId,
                    Title = Title(name, details),
                    Kind = KindOf(name, activity.Kind),
                    Status = activity.Phase == AgentActivityPhase.Started ? ToolCallStatus.InProgress : ToolCallStatus.Pending,
                    Locations = Locations(details),
                    RawInput = details.ValueKind == JsonValueKind.Object && details.TryGetProperty("arguments", out var arguments) ? arguments.Clone() : null,
                    Meta = ToolMeta(name, activity.RunId),
                }), cancellationToken).ConfigureAwait(false);
                break;
            case AgentActivityPhase.Started:
                await UpdateAsync(sessionId, AcpSessionUpdates.ToolCallUpdate(new ToolCallUpdate { ToolCallId = callId, Status = ToolCallStatus.InProgress }),
                    cancellationToken).ConfigureAwait(false);
                break;
            case AgentActivityPhase.Completed or AgentActivityPhase.Failed or AgentActivityPhase.Canceled:
                var failed = activity.Phase != AgentActivityPhase.Completed;
                if (!announced)
                {
                    // A call whose start was not seen (a provider that runs its own loop reports it once it ended).
                    _tools.Add((sessionId, callId));
                    await UpdateAsync(sessionId, AcpSessionUpdates.ToolCall(new ToolCall
                    {
                        ToolCallId = callId,
                        Title = Title(name, details),
                        Kind = KindOf(name, activity.Kind),
                        Status = failed ? ToolCallStatus.Failed : ToolCallStatus.Completed,
                        Locations = Locations(details),
                        Content = failed && !string.IsNullOrWhiteSpace(activity.Message) ? [ToolCallContent.FromText(Bound(activity.Message))] : null,
                        Meta = ToolMeta(name, activity.RunId),
                    }), cancellationToken).ConfigureAwait(false);
                    break;
                }

                await UpdateAsync(sessionId, AcpSessionUpdates.ToolCallUpdate(new ToolCallUpdate
                {
                    ToolCallId = callId,
                    Status = failed ? ToolCallStatus.Failed : ToolCallStatus.Completed,
                    Locations = Locations(details),
                    Content = failed && !string.IsNullOrWhiteSpace(activity.Message) ? [ToolCallContent.FromText(Bound(activity.Message))] : null,
                }), cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private void Streamed(string sessionId, bool thought, AgentContentDeltaEvent delta)
    {
        if (!_streamed.TryGetValue((sessionId, delta.ContentId), out var text) || text.Run != delta.RunId)
        {
            // Bounded: the contents of a provider that never completes them are dropped at last.
            if (_streamed.Count >= 1024)
            {
                _streamed.Clear();
                _lastStreamed.Clear();
            }

            _streamed[(sessionId, delta.ContentId)] = text = new StreamedText(delta.RunId);
        }

        text.Text.Append(delta.Delta);
        _lastStreamed[(sessionId, thought)] = delta.ContentId;
    }

    // Sends what the client misses of a completed text: all of it when nothing was streamed; the end the stream dropped;
    // or, when it dropped a part in the middle, the text after what the client has right, with an error that says so.
    private async ValueTask CompleteAsync(string sessionId, bool thought, AgentContentCompletedEvent completed, CancellationToken cancellationToken)
    {
        var text = completed.Content;
        var streamed = TakeStreamed(sessionId, thought, completed);
        var common = streamed is null ? 0 : CommonPrefixLength(streamed, text);
        if (common < text.Length)
        {
            var rest = text[common..];
            await UpdateAsync(sessionId, thought ? AcpSessionUpdates.AgentThoughtChunk(rest) : AcpSessionUpdates.AgentMessageChunk(rest), cancellationToken).ConfigureAwait(false);
        }

        if (streamed is not null && common < streamed.Length && common < text.Length)
        {
            await ExtensionAsync(ErrorMethod, sessionId, writer =>
            {
                writer.WriteString("kind", "events_dropped");
                writer.WriteString("message", $"Updates of the {(thought ? "reasoning" : "answer")} were lost: the text streamed is incomplete. The text after the last part it has right was sent again.");
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    // What was streamed of a completed text: its own deltas, or else the deltas the channel streamed last in the same run (a
    // provider that names its completed texts otherwise); null when nothing was streamed of it.
    private string? TakeStreamed(string sessionId, bool thought, AgentContentCompletedEvent completed)
    {
        if (_streamed.Remove((sessionId, completed.ContentId), out var own))
        {
            if (_lastStreamed.TryGetValue((sessionId, thought), out var last) && last == completed.ContentId) _lastStreamed.Remove((sessionId, thought));
            return own.Run == completed.RunId ? own.Text.ToString() : null;
        }

        return _lastStreamed.Remove((sessionId, thought), out var lastId) && _streamed.Remove((sessionId, lastId), out var other) && other.Run == completed.RunId
            ? other.Text.ToString()
            : null;
    }

    private void ForgetStreamed(string sessionId)
    {
        foreach (var key in _streamed.Keys.Where(key => string.Equals(key.Session, sessionId, StringComparison.OrdinalIgnoreCase)).ToArray()) _streamed.Remove(key);
        _lastStreamed.Remove((sessionId, false));
        _lastStreamed.Remove((sessionId, true));
    }

    private static int CommonPrefixLength(string left, string right) => left.AsSpan().CommonPrefixLength(right);

    private static bool IsToolActivity(AgentActivityKind kind)
        => kind is AgentActivityKind.ToolCall or AgentActivityKind.CommandExecution or AgentActivityKind.FileChange or AgentActivityKind.McpToolCall
            or AgentActivityKind.DynamicToolCall or AgentActivityKind.CollabAgentToolCall or AgentActivityKind.Skill or AgentActivityKind.WebSearch
            or AgentActivityKind.ImageGeneration;

    // The kind of a tool for a client: the tools of CodeAlta and of the harnesses of providers by name, the others by the
    // kind of their activity.
    internal static ToolKind KindOf(string name, AgentActivityKind kind)
        => ToolKindsByName.TryGetValue(name, out var known) ? known
            : kind switch
            {
                AgentActivityKind.CommandExecution => ToolKind.Execute,
                AgentActivityKind.FileChange => ToolKind.Edit,
                AgentActivityKind.WebSearch => ToolKind.Fetch,
                _ => ToolKind.Other,
            };

    // The tools by name, without case: those of CodeAlta; of Claude Code (Read, Edit, Bash...); of Codex (shell,
    // exec_command, apply_patch...); of Copilot and Gemini CLI (view, str_replace_editor, run_shell_command...); and the
    // names the ACP plugin gives the calls of an ACP agent by their kind (shell, read, edit, search, fetch, think).
    private static readonly FrozenDictionary<string, ToolKind> ToolKindsByName = new (string[] Names, ToolKind Kind)[]
    {
        (["read_file", "view_image", "list_dir", "Read", "NotebookRead", "LS", "view", "read_many_files", "list_directory"], ToolKind.Read),
        (["grep", "Glob", "ToolSearch", "search", "grep_files", "search_files", "file_search", "codebase_search", "search_file_content"], ToolKind.Search),
        (["webget", AgentToolPatterns.WebSearchToolName, AgentToolPatterns.WebFetchToolName, "WebFetch", "WebSearch", "fetch", "google_web_search"], ToolKind.Fetch),
        (["shell_command", "Bash", "PowerShell", "BashOutput", "KillShell", "KillBash", "shell", "exec_command", "local_shell", "write_stdin",
            "run_shell_command", "read_bash", "write_bash", "stop_bash", "read_powershell", "write_powershell", "stop_powershell"], ToolKind.Execute),
        (["write_file", "replace_in_file", "apply_patch", "Write", "Edit", "MultiEdit", "NotebookEdit", "replace", "str_replace",
            "str_replace_editor", "str_replace_based_edit_tool", "create", "insert"], ToolKind.Edit),
        (["delete_file_or_dir"], ToolKind.Delete),
        (["rename_file_or_dir"], ToolKind.Move),
        (["think", "TodoWrite", "update_plan"], ToolKind.Think),
        (["switch_mode", "EnterPlanMode", "ExitPlanMode"], ToolKind.SwitchMode),
    }.SelectMany(static group => group.Names.Select(name => KeyValuePair.Create(name, group.Kind))).ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    // The name of the tool and what it works on: a path, a command, a pattern, an address, or the arguments of alta.
    internal static string Title(string name, JsonElement details)
    {
        if (details.ValueKind != JsonValueKind.Object || !details.TryGetProperty("arguments", out var arguments) || arguments.ValueKind != JsonValueKind.Object)
            return name;
        foreach (var key in new[] { "path", "file_path", "notebook_path", "command", "pattern", "url", "query", "source" })
        {
            if (AcpJson.GetString(arguments, key) is { Length: > 0 } value) return name + " " + Shorten(value);
        }

        if (AcpJson.TryGetArray(arguments, "args", out var args))
        {
            var words = args.EnumerateArray().Where(static word => word.ValueKind == JsonValueKind.String).Select(static word => word.GetString()!).Take(4);
            return name + " " + Shorten(string.Join(' ', words));
        }

        return name;
    }

    // The extras of a tool call, in its _meta: the name of the CodeAlta tool and the run that called it.
    private static Dictionary<string, JsonElement> ToolMeta(string name, AgentRunId? runId)
        => new(StringComparer.Ordinal)
        {
            [MetaKey] = AcpJson.Build(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("tool", name);
                if (runId is { } run) writer.WriteString("runId", run.Value);
                writer.WriteEndObject();
            }),
        };

    private static List<ToolCallLocation>? Locations(JsonElement details)
    {
        var paths = new List<ToolCallLocation>();
        foreach (var key in new[] { "modifiedFiles", "readFiles" })
        {
            if (!AcpJson.TryGetArray(details, key, out var files)) continue;
            foreach (var file in files.EnumerateArray())
            {
                if (file.ValueKind == JsonValueKind.String && file.GetString() is { Length: > 0 } path && paths.All(location => location.Path != path))
                    paths.Add(new ToolCallLocation { Path = path });
            }
        }

        return paths.Count == 0 ? null : paths;
    }

    // The output of a tool as a client shows it: the change of a file, the unified diff of the call, and the bounded text of
    // its result. An ACP diff carries whole contents, which only a write_file that created its file knows (the content it
    // wrote, from nothing); the change of another call is its unified diff.
    private static List<ToolCallContent> ToolOutputContent(AgentContentCompletedEvent output)
    {
        var content = new List<ToolCallContent>();
        var details = output.Details ?? default;
        var diff = AcpJson.GetString(details, "diff");
        if (AcpJson.GetString(details, "toolName") == "write_file"
            && diff is not null && diff.ReplaceLineEndings("\n").Split('\n').Contains("new file mode 100644")
            && AcpJson.TryGetObject(details, "arguments", out var arguments) && AcpJson.GetString(arguments, "content") is { } newText
            && AcpJson.TryGetArray(details, "modifiedFiles", out var modified) && modified.GetArrayLength() == 1
            && modified[0].GetString() is { } path && Path.IsPathFullyQualified(path))
        {
            content.Add(ToolCallContent.FromDiff(path, null, newText));
        }
        else if (diff is { Length: > 0 })
        {
            content.Add(ToolCallContent.FromText(Bound("```diff\n" + diff.TrimEnd() + "\n```")));
        }

        if (!string.IsNullOrEmpty(output.Content)) content.Add(ToolCallContent.FromText(Bound(output.Content)));
        return content;
    }

    internal static string FailureKindName(AgentFailureKind kind)
        => kind switch
        {
            AgentFailureKind.QuotaExhausted => "quota_exhausted",
            AgentFailureKind.RateLimited => "rate_limited",
            AgentFailureKind.Unavailable => "unavailable",
            AgentFailureKind.Auth => "auth",
            AgentFailureKind.ModelUnavailable => "model_unavailable",
            _ => "other",
        };

    private static string Bound(string text)
        => text.Length <= MaximumToolOutput ? text : string.Concat(text.AsSpan(0, MaximumToolOutput), "\n[truncated]");

    private static string Shorten(string value)
    {
        var line = value.ReplaceLineEndings(" ").Trim();
        return line.Length <= 80 ? line : string.Concat(line.AsSpan(0, 79), "…");
    }

    private ValueTask UpdateAsync(string sessionId, SessionUpdate update, CancellationToken cancellationToken)
        => _sink.SessionUpdateAsync(sessionId, update, cancellationToken);

    private ValueTask ExtensionAsync(string method, string sessionId, Action<Utf8JsonWriter> writeFields, CancellationToken cancellationToken)
        => _sink.ExtensionAsync(method, AcpJson.Build(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("sessionId", sessionId);
            writeFields(writer);
            writer.WriteEndObject();
        }), cancellationToken);

    internal static void WriteStringOrNull(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null) writer.WriteNull(name);
        else writer.WriteString(name, value);
    }

    // The text streamed of a content, and the run that streamed it.
    private sealed class StreamedText(AgentRunId? run)
    {
        public AgentRunId? Run { get; } = run;

        public StringBuilder Text { get; } = new();
    }
}
