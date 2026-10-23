using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using CodeAlta.Agent;

namespace CodeAlta.Plugin.Mobile;

/// <summary>
/// What the phone shows of a session while it runs: a state for the dot and one short line, the latest thing the
/// session said or did. It is fed by the agent events the plugin observes and pushed to the phones that listen.
/// </summary>
internal sealed class MobileLive
{
    // States of the dot: idle (gray), running (green), tools (yellow), thinking (blue).
    internal const string Idle = "idle", Running = "running", Tools = "tools", Thinking = "thinking";

    private const int LineLength = 160;
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Channel<string>, byte> _listeners = new();

    internal int ListenerCount => _listeners.Count;

    /// <summary>
    /// Whether a session can be steered now. alta steers a session when its runtime entry has an active run
    /// (SessionRuntimeService.GetActiveRuntimeSessionForSteeringAsync: entry not terminated, HasActiveRun), the same
    /// HasActiveRunAsync that <c>alta session status</c> reports as <c>isRunning</c>; the plugin asks it.
    /// </summary>
    internal Func<string, Task<bool>>? SteerProbe { get; init; }

    /// <summary>The live state of a session, when one was seen since the plugin started.</summary>
    internal JsonObject? Describe(string sessionId) => _sessions.TryGetValue(sessionId, out var session) ? session.ToJson(sessionId) : null;

    internal void Observe(AgentEvent agentEvent)
    {
        if (string.IsNullOrEmpty(agentEvent.SessionId)) return;
        var session = _sessions.GetOrAdd(agentEvent.SessionId, static _ => new Session());
        lock (session)
        {
            switch (agentEvent)
            {
                case AgentActivityEvent { Kind: AgentActivityKind.Turn } turn:
                    if (turn.Phase is AgentActivityPhase.Started or AgentActivityPhase.Progressed) session.Set(Running, session.Kind, session.Line);
                    else if (turn.Phase is AgentActivityPhase.Completed or AgentActivityPhase.Failed or AgentActivityPhase.Canceled)
                    {
                        session.Tools.Clear();
                        session.Activity.Ended();
                        session.Set(Idle, turn.Phase == AgentActivityPhase.Failed ? "error" : session.Kind, session.Line);
                    }

                    break;
                case AgentActivityEvent activity:
                    // A tool call is a step of the activity, whose line the card shows as the desktop folds it.
                    if (activity.Phase is AgentActivityPhase.Requested or AgentActivityPhase.Started or AgentActivityPhase.Progressed)
                    {
                        session.Tools.Add(activity.ActivityId);
                        session.Activity.ToolStarted(activity.ActivityId, activity.Name ?? activity.Kind.ToString(), activity.Message ?? Detail(activity.Details), agentEvent.Timestamp);
                    }
                    else
                    {
                        session.Tools.Remove(activity.ActivityId);
                        session.Activity.ToolEnded(activity.ActivityId, activity.Phase == AgentActivityPhase.Failed, agentEvent.Timestamp);
                    }

                    session.SetActivity(session.Tools.Count > 0 ? Tools : Running);
                    break;
                case AgentContentDeltaEvent delta:
                    // Deltas can reach plugins out of order, so streamed text is not shown: a thought streams under the
                    // activity line (its headline comes when it is complete), an answer says "Writing…" until it is.
                    if (delta.Kind is AgentContentKind.Reasoning or AgentContentKind.ReasoningSummary) session.SetActivity(Thinking);
                    else if (delta.Kind == AgentContentKind.Assistant && session.Kind != "writing") session.Set(session.Tools.Count > 0 ? Tools : Running, "writing", "Writing…");
                    break;
                case AgentContentCompletedEvent completed:
                    switch (completed.Kind)
                    {
                        case AgentContentKind.Assistant:
                            session.Activity.Reset();
                            session.Set(session.State == Idle ? Idle : Running, "assistant", Concise(completed.Content));
                            break;
                        case AgentContentKind.Reasoning or AgentContentKind.ReasoningSummary:
                            session.Activity.Thought(completed.Content, agentEvent.Timestamp);
                            session.SetActivity(session.State == Idle ? Idle : session.Tools.Count > 0 ? Tools : Running);
                            break;
                        case AgentContentKind.User:
                            session.Activity.Reset();
                            session.Set(Running, "user", Concise(completed.Content));
                            break;
                    }

                    break;
                case AgentSessionUpdateEvent { Kind: AgentSessionUpdateKind.Idle }:
                    session.Tools.Clear();
                    session.Activity.Ended();
                    if (session.Kind == "activity") session.SetActivity(Idle);
                    else session.Set(Idle, session.Kind, session.Line);
                    break;
                case AgentErrorEvent:
                    session.Set(session.State, "error", "Error");
                    break;
                default:
                    return;
            }
        }
    }

    /// <summary>Pushes the sessions that changed since the last flush; called a few times a second.</summary>
    internal void Flush()
    {
        foreach (var (id, session) in _sessions)
        {
            Probe(id, session);
            string? payload = null;
            lock (session)
            {
                if (!session.Dirty) continue;
                session.Dirty = false;
                payload = session.ToJson(id).ToJsonString();
            }

            Broadcast("session", payload);
        }
    }

    // While a session is busy its steerability is asked again every 3 s, and once when it stops.
    private void Probe(string id, Session session)
    {
        bool busy;
        lock (session)
        {
            busy = session.State != Idle;
            if (!busy) { if (session.CanSteer) { session.CanSteer = false; session.Dirty = true; } return; }
            if (SteerProbe is null || session.Probing || DateTimeOffset.UtcNow - session.ProbedAt < TimeSpan.FromSeconds(3)) return;
            session.Probing = true;
        }

        _ = Task.Run(async () =>
        {
            bool can;
            try { can = await SteerProbe(id).ConfigureAwait(false); } catch { can = false; }
            lock (session)
            {
                session.Probing = false;
                session.ProbedAt = DateTimeOffset.UtcNow;
                if (session.State == Idle) can = false;
                if (session.CanSteer != can) { session.CanSteer = can; session.Dirty = true; }
                // alta has no run for a session still shown busy (a stopped turn whose tool never reported its end):
                // after two such answers in a row the session is shown idle, as alta has it.
                session.NotRunning = can || session.State == Idle ? 0 : session.NotRunning + 1;
                if (session.NotRunning >= 2)
                {
                    session.NotRunning = 0;
                    session.Tools.Clear();
                    if (session.Kind == "activity") session.SetActivity(Idle); else session.Set(Idle, session.Kind, session.Line);
                }
            }
        });
    }

    internal void Broadcast(string eventName, string json)
    {
        var frame = $"event: {eventName}\ndata: {json}\n\n";
        foreach (var listener in _listeners.Keys) listener.Writer.TryWrite(frame);
    }

    /// <summary>A listener gets the state of every session seen so far, then each change.</summary>
    internal Channel<string> Subscribe()
    {
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        foreach (var (id, session) in _sessions)
            lock (session) channel.Writer.TryWrite($"event: session\ndata: {session.ToJson(id).ToJsonString()}\n\n");
        _listeners[channel] = 0;
        return channel;
    }

    internal void Unsubscribe(Channel<string> channel)
    {
        _listeners.TryRemove(channel, out _);
        channel.Writer.TryComplete();
    }

    /// <summary>The first line of a text, without markup noise, cut to the length of a line on a phone.</summary>
    internal static string Concise(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var line = text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(static l => l.Trim('#', '*', '-', '>', ' ', '`').Length > 0) ?? "";
        line = string.Join(' ', line.Trim('#', '*', '>', ' ').Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries));
        return line.Length <= LineLength ? line : line[..(LineLength - 1)] + "…";
    }

    // What a tool works on, when its details say: the command it runs, the file or the query.
    private static string? Detail(JsonElement? details)
    {
        if (details is not { ValueKind: JsonValueKind.Object } value) return null;
        foreach (var name in (string[])["command", "path", "file", "filePath", "query", "url", "pattern"])
        {
            if (value.TryGetProperty(name, out var found) && found.ValueKind == JsonValueKind.String) return found.GetString();
            if (value.TryGetProperty("arguments", out var arguments) && arguments.ValueKind == JsonValueKind.Object
                && arguments.TryGetProperty(name, out found) && found.ValueKind == JsonValueKind.String) return found.GetString();
        }

        return null;
    }

    private sealed class Session
    {
        internal string State = Idle;
        internal string? Kind;
        internal string? Line;
        internal DateTimeOffset At;
        internal bool Dirty;
        internal readonly HashSet<string> Tools = new(StringComparer.Ordinal);
        internal readonly MobileActivity Activity = new();
        internal bool CanSteer, Probing;
        internal int NotRunning;
        internal DateTimeOffset ProbedAt;

        internal void SetActivity(string state)
        {
            var summary = Activity.ToJson(state != Idle);
            Set(state, "activity", (string?)summary["headline"]);
        }

        internal void Set(string state, string? kind, string? line)
        {
            State = state;
            Kind = kind;
            Line = line;
            At = DateTimeOffset.UtcNow;
            Dirty = true;
        }

        internal JsonObject ToJson(string id) => new()
        {
            ["id"] = id, ["live"] = State, ["kind"] = Kind, ["line"] = Line, ["at"] = At == default ? null : At.ToString("O"),
            ["activity"] = Kind == "activity" ? Activity.ToJson(State != Idle) : null,
            ["canSteer"] = CanSteer,
        };
    }
}
