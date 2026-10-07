using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using CodeAlta.Agent;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>
/// What the tool calls in flight have written so far. A tool reports its output while it runs as content deltas
/// that no journal keeps; this projection retains them per call until the call ends, so that a frontend that
/// looks at a running call sees what it wrote and what it writes next.
/// </summary>
/// <remarks>
/// A short gate makes commit and observation atomic; no I/O, callback or wait runs under it. At most
/// <see cref="MaxCalls"/> calls are retained, each with its newest <see cref="MaxCharactersPerCall"/> UTF-16 code
/// units. A call leaves when it ends, when its run ends, or when a newer call needs its place. Once it has left,
/// its output is the one of its persisted record.
/// </remarks>
public sealed class RuntimeToolOutputProjection
{
    /// <summary>Maximum running calls whose output is retained; the least recently written leaves first.</summary>
    public const int MaxCalls = 16;
    /// <summary>Maximum UTF-16 code units retained per call: the newest ones.</summary>
    public const int MaxCharactersPerCall = 512 * 1024;
    /// <summary>Maximum simultaneous observations, each with one payload-free wakeup slot.</summary>
    public const int MaxSubscribers = 32;

    private readonly object _gate = new();
    private readonly Dictionary<string, Call> _calls = new(StringComparer.Ordinal);
    private long _sequence;
    private int _subscribers;
    private bool _closed;

    internal RuntimeToolOutputProjection() { }

    internal int CallCount { get { lock (_gate) return _calls.Count; } }
    internal int SubscriberCount { get { lock (_gate) return _subscribers; } }

    /// <summary>
    /// Yields what a running call has written so far, then what it writes, until it ends. Updates are coalesced:
    /// each one holds everything written since the previous one.
    /// </summary>
    /// <param name="sessionId">The session of the call.</param>
    /// <param name="activityId">
    /// The activity identity of the call, as the session's events or its history rows give it (a long identity
    /// in its compact form, see <see cref="RuntimeDisplayProjection.CompactIdentifier"/>).
    /// </param>
    /// <param name="cancellationToken">Ends the observation.</param>
    /// <returns>
    /// The updates of the call, the last one with <see cref="RuntimeToolOutputUpdate.IsComplete"/>. A call that
    /// is not running yields that last update alone.
    /// </returns>
    /// <exception cref="ArgumentException">An identity is blank.</exception>
    /// <exception cref="InvalidOperationException">The subscriber limit has been reached.</exception>
    /// <exception cref="OperationCanceledException">Observation was canceled.</exception>
    public async IAsyncEnumerable<RuntimeToolOutputUpdate> ObserveAsync(string sessionId, string activityId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(activityId);
        cancellationToken.ThrowIfCancellationRequested();
        Call? call;
        Channel<bool>? signal = null;
        lock (_gate)
        {
            if (_calls.TryGetValue(Key(sessionId, activityId), out call))
            {
                if (_subscribers >= MaxSubscribers)
                    throw new InvalidOperationException("The tool output subscriber limit has been reached.");
                signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
                {
                    FullMode = BoundedChannelFullMode.DropWrite,
                    SingleReader = true,
                    SingleWriter = false,
                    AllowSynchronousContinuations = false,
                });
                call.Signals.Add(signal);
                _subscribers++;
            }
        }

        if (call is null || signal is null)
        {
            yield return new(string.Empty, 0, 0, true, true);
            yield break;
        }

        try
        {
            long position = -1;
            while (true)
            {
                RuntimeToolOutputUpdate update;
                lock (_gate) update = call.Read(ref position);
                if (update.IsReset || update.IsComplete || update.Text.Length > 0) yield return update;
                if (update.IsComplete) yield break;
                // A completed signal still lets the loop read once more: the final text and the end of the call.
                await signal.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false);
                signal.Reader.TryRead(out _);
            }
        }
        finally
        {
            lock (_gate)
            {
                if (call.Signals.Remove(signal)) _subscribers--;
            }
        }
    }

    internal void Commit(SessionRuntimeEvent? runtimeEvent)
    {
        switch (runtimeEvent)
        {
            case SessionAgentEvent { Event: AgentActivityEvent { ActivityId: { Length: > 0 } activityId } activity } when IsToolActivity(activity.Kind):
                lock (_gate)
                {
                    if (_closed) return;
                    var key = Key(runtimeEvent.SessionId, activityId);
                    if (activity.Phase is AgentActivityPhase.Requested or AgentActivityPhase.Started or AgentActivityPhase.Progressed or AgentActivityPhase.Selected) Open(key, runtimeEvent.SessionId);
                    else End(key);
                }
                break;
            case SessionAgentEvent { Event: AgentContentDeltaEvent delta } when IsToolOutput(delta.Kind):
                if (string.IsNullOrEmpty(delta.Delta)) return;
                lock (_gate)
                {
                    if (_closed || Resolve(runtimeEvent.SessionId, delta.ContentId, delta.ParentActivityId) is not { } key) return;
                    var call = Open(key, runtimeEvent.SessionId);
                    call.Append(delta.Delta);
                    call.Sequence = ++_sequence;
                    call.Wake();
                }
                break;
            case SessionAgentEvent { Event: AgentContentCompletedEvent completed } when IsToolOutput(completed.Kind):
                lock (_gate)
                {
                    if (!_closed && Resolve(runtimeEvent.SessionId, completed.ContentId, completed.ParentActivityId) is { } key) End(key);
                }
                break;
            case SessionLifecycleRuntimeEvent { Event.Kind: SessionLifecycleEventKind.RunCompleted or SessionLifecycleEventKind.RunFailed or SessionLifecycleEventKind.RunAborted }:
                // A run that ends leaves no call running, whatever its tools reported.
                lock (_gate)
                {
                    if (_closed) return;
                    foreach (var key in _calls.Where(pair => string.Equals(pair.Value.SessionId, runtimeEvent.SessionId, StringComparison.OrdinalIgnoreCase)).Select(static pair => pair.Key).ToArray())
                        End(key);
                }
                break;
        }
    }

    internal void Complete()
    {
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            foreach (var key in _calls.Keys.ToArray()) End(key);
        }
    }

    private static bool IsToolActivity(AgentActivityKind kind)
        => kind is AgentActivityKind.ToolCall or AgentActivityKind.CommandExecution or AgentActivityKind.McpToolCall
            or AgentActivityKind.DynamicToolCall or AgentActivityKind.CollabAgentToolCall or AgentActivityKind.Subagent
            or AgentActivityKind.Hook or AgentActivityKind.Skill or AgentActivityKind.WebSearch or AgentActivityKind.ImageGeneration;

    private static bool IsToolOutput(AgentContentKind kind)
        => kind is AgentContentKind.ToolOutput or AgentContentKind.CommandOutput or AgentContentKind.FileChangeOutput;

    private static string Key(string sessionId, string activityId)
        => string.Concat(sessionId.ToUpperInvariant(), "\n", RuntimeDisplayProjection.CompactIdentifier(activityId));

    // An output names its call by its own identity or by its parent activity: a call already known wins.
    private string? Resolve(string sessionId, string? contentId, string? parentActivityId)
    {
        if (!string.IsNullOrEmpty(contentId) && _calls.ContainsKey(Key(sessionId, contentId))) return Key(sessionId, contentId);
        if (!string.IsNullOrEmpty(parentActivityId)) return Key(sessionId, parentActivityId);
        return string.IsNullOrEmpty(contentId) ? null : Key(sessionId, contentId);
    }

    private Call Open(string key, string sessionId)
    {
        if (_calls.TryGetValue(key, out var call)) return call;
        if (_calls.Count >= MaxCalls) End(_calls.MinBy(static pair => pair.Value.Sequence).Key);
        call = new Call(sessionId) { Sequence = ++_sequence };
        _calls.Add(key, call);
        return call;
    }

    private void End(string key)
    {
        if (!_calls.Remove(key, out var call)) return;
        call.Completed = true;
        foreach (var signal in call.Signals)
        {
            signal.Writer.TryWrite(true);
            signal.Writer.TryComplete();
        }
    }

    private sealed class Call(string sessionId)
    {
        private readonly StringBuilder _text = new();
        private long _dropped;

        internal string SessionId { get; } = sessionId;
        internal List<Channel<bool>> Signals { get; } = [];
        internal long Sequence { get; set; }
        internal bool Completed { get; set; }

        internal void Append(string delta)
        {
            _text.Append(delta);
            // Trimmed when twice the bound is held, so that the copy is paid once per bound of new text.
            if (_text.Length <= 2 * MaxCharactersPerCall) return;
            var removed = _text.Length - MaxCharactersPerCall;
            // Never keep the second half of a surrogate pair as the first retained unit.
            if (char.IsLowSurrogate(_text[removed]) && char.IsHighSurrogate(_text[removed - 1])) removed++;
            _text.Remove(0, removed);
            _dropped += removed;
        }

        internal void Wake()
        {
            foreach (var signal in Signals) signal.Writer.TryWrite(true);
        }

        internal RuntimeToolOutputUpdate Read(ref long position)
        {
            var total = _dropped + _text.Length;
            // Nothing was sent yet, or what follows the last update is no longer retained: the retained text restarts the output.
            var reset = position < _dropped;
            var start = reset ? _dropped : position;
            var text = start >= total ? string.Empty : _text.ToString((int)(start - _dropped), (int)(total - start));
            position = total;
            return new(text, start, total, reset, Completed);
        }
    }
}

/// <summary>One update of the output of a running tool call.</summary>
/// <param name="Text">What the call wrote since the previous update, or since <paramref name="Start"/> for a reset.</param>
/// <param name="Start">Position of the first unit of <paramref name="Text"/> in everything the call wrote, in UTF-16 code units.</param>
/// <param name="TotalCharacters">Everything the call wrote so far, in UTF-16 code units.</param>
/// <param name="IsReset">
/// The update does not continue the previous one: it is the first one, or text in between was not retained. The
/// receiver replaces what it shows.
/// </param>
/// <param name="IsComplete">The call ended, or is not running: no update follows.</param>
public readonly record struct RuntimeToolOutputUpdate(string Text, long Start, long TotalCharacters, bool IsReset, bool IsComplete);
