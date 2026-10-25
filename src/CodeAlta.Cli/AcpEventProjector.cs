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
/// <item><c>agent_message_chunk</c> and <c>agent_thought_chunk</c> for the deltas of the answer and of the reasoning;</item>
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
                await UpdateAsync(sessionId, AcpSessionUpdates.AgentMessageChunk(delta.Delta), cancellationToken).ConfigureAwait(false);
                break;
            case AgentContentDeltaEvent { Kind: AgentContentKind.Reasoning or AgentContentKind.ReasoningSummary, Delta.Length: > 0 } delta:
                await UpdateAsync(sessionId, AcpSessionUpdates.AgentThoughtChunk(delta.Delta), cancellationToken).ConfigureAwait(false);
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

    private static bool IsToolActivity(AgentActivityKind kind)
        => kind is AgentActivityKind.ToolCall or AgentActivityKind.CommandExecution or AgentActivityKind.FileChange or AgentActivityKind.McpToolCall
            or AgentActivityKind.DynamicToolCall or AgentActivityKind.CollabAgentToolCall or AgentActivityKind.Skill or AgentActivityKind.WebSearch
            or AgentActivityKind.ImageGeneration;

    // The kind of a tool for a client: the built-in tools by name, the others by the kind of their activity.
    internal static ToolKind KindOf(string name, AgentActivityKind kind)
        => name switch
        {
            "read_file" or "view_image" or "list_dir" => ToolKind.Read,
            "grep" => ToolKind.Search,
            "webget" or AgentToolPatterns.WebSearchToolName or AgentToolPatterns.WebFetchToolName => ToolKind.Fetch,
            "shell_command" => ToolKind.Execute,
            "write_file" or "replace_in_file" or "apply_patch" => ToolKind.Edit,
            "delete_file_or_dir" => ToolKind.Delete,
            "rename_file_or_dir" => ToolKind.Move,
            _ => kind switch
            {
                AgentActivityKind.CommandExecution => ToolKind.Execute,
                AgentActivityKind.FileChange => ToolKind.Edit,
                AgentActivityKind.WebSearch => ToolKind.Fetch,
                _ => ToolKind.Other,
            },
        };

    // The name of the tool and what it works on: a path, a command, a pattern, an address, or the arguments of alta.
    internal static string Title(string name, JsonElement details)
    {
        if (details.ValueKind != JsonValueKind.Object || !details.TryGetProperty("arguments", out var arguments) || arguments.ValueKind != JsonValueKind.Object)
            return name;
        foreach (var key in new[] { "path", "command", "pattern", "url", "query", "source" })
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

    // The output of a tool as a client shows it: the change of a file a replacement made, the unified diff of the call, and
    // the bounded text of its result.
    private static List<ToolCallContent> ToolOutputContent(AgentContentCompletedEvent output)
    {
        var content = new List<ToolCallContent>();
        var details = output.Details ?? default;
        if (AcpJson.GetString(details, "toolName") == "replace_in_file"
            && AcpJson.TryGetObject(details, "arguments", out var arguments)
            && AcpJson.GetString(arguments, "old_string") is { } oldText && AcpJson.GetString(arguments, "new_string") is { } newText
            && AcpJson.TryGetArray(details, "modifiedFiles", out var modified) && modified.GetArrayLength() == 1
            && modified[0].GetString() is { } path && Path.IsPathFullyQualified(path))
        {
            content.Add(ToolCallContent.FromDiff(path, oldText, newText));
        }
        else if (AcpJson.GetString(details, "diff") is { Length: > 0 } diff)
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
}
