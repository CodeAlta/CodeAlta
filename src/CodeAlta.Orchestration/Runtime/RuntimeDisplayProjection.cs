using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using CodeAlta.Agent;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>
/// Runtime-owned committed partial live display projection. A short single-writer gate makes commit and
/// snapshot/subscription atomic across sessions. No I/O, callbacks, asynchronous work or observer waits run under it.
/// Limits bound retained payload/counts, not total process heap or consumer-retained snapshots.
/// </summary>
public sealed class RuntimeDisplayProjection
{
    /// <summary>Maximum retained session windows; least recently published is evicted on admission.</summary>
    public const int MaxSessions = 128;
    /// <summary>Maximum retained text items per session.</summary>
    public const int MaxTextItemsPerSession = 8;
    /// <summary>Maximum reported plain ToolCall identities per session, ordered by latest update.</summary>
    public const int MaxToolActivitiesPerSession = 2;
    /// <summary>Maximum UTF-16 code units in a retained well-formed tool name prefix.</summary>
    public const int MaxToolNameCharacters = 128;
    /// <summary>Maximum UTF-16 code units per text item.</summary>
    public const int MaxTextCharacters = 4096;
    /// <summary>
    /// Maximum UTF-16 code units per identity; oversized session identities are omitted, never aliased by truncation.
    /// Oversized text and tool identities are compacted with <see cref="CompactIdentifier" />.
    /// </summary>
    public const int MaxIdentifierCharacters = 256;
    /// <summary>Maximum UTF-16 code units per status/configuration/lifecycle label.</summary>
    public const int MaxMetadataCharacters = 512;
    /// <summary>Maximum simultaneous observations, each with one payload-free wakeup slot and no replay buffer.</summary>
    public const int MaxSubscribers = 32;

    private readonly object _gate = new();
    private readonly Guid _epoch = Guid.NewGuid();
    private readonly Dictionary<string, RuntimeDisplaySession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Channel<bool>> _subscribers = [];
    // The observations of one session: woken by what changes that session, and by what changes every session's
    // replacement (an eviction, an omitted event, the closure), not by the commits of the other sessions.
    private readonly Dictionary<string, List<Channel<bool>>> _sessionSubscribers = new(StringComparer.OrdinalIgnoreCase);
    private int _sessionSubscriberCount;
    private long _revision, _evictedSessions, _omittedSessionEvents;
    private bool _closed;

    internal RuntimeDisplayProjection() { }

    internal int SubscriberCount { get { lock (_gate) return _subscribers.Count + _sessionSubscriberCount; } }

    /// <summary>Returns detached immutable retained state at one atomic revision, including after closure.</summary>
    public RuntimeDisplaySnapshot GetSnapshot()
    {
        lock (_gate) return Snapshot();
    }

    /// <summary>
    /// Atomically registers and yields an initial snapshot on first enumeration, then bounded/coalesced replacements.
    /// Cancellation releases admission even while the consumer is suspended at a yield. Dispose the enumerator on detach.
    /// Closing yields the final closed replacement and ends; subscribing after close yields one closed baseline.
    /// </summary>
    /// <exception cref="InvalidOperationException">The live subscriber limit has been reached.</exception>
    /// <exception cref="OperationCanceledException">Observation was canceled.</exception>
    public async IAsyncEnumerable<RuntimeDisplayReplacement> ObserveAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Channel<bool> signal;
        RuntimeDisplaySnapshot snapshot;
        lock (_gate)
        {
            if (!_closed && _subscribers.Count + _sessionSubscriberCount >= MaxSubscribers)
                throw new InvalidOperationException("The runtime display subscriber limit has been reached.");
            signal = CreateSignal();
            snapshot = Snapshot();
            if (!_closed) _subscribers.Add(signal);
        }

        using var registration = cancellationToken.Register(() => Release(signal));
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new(snapshot, true, null, false);
            while (!snapshot.IsClosed)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!await signal.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield break;
                }
                RuntimeDisplaySnapshot next;
                lock (_gate)
                {
                    signal.Reader.TryRead(out _);
                    next = Snapshot();
                }
                if (next.Revision == snapshot.Revision) continue;
                var previous = snapshot.Revision;
                snapshot = next;
                yield return new(snapshot, false, previous, snapshot.Revision > previous + 1);
            }
        }
        finally { Release(signal); }
    }

    /// <summary>
    /// Like <see cref="ObserveAsync"/>, for one session: each replacement holds that session alone (none while it is not
    /// retained) with the counters of the whole projection, and a commit of another session does not wake the observation.
    /// An eviction, an omitted event and the closure, which change the counters, wake it.
    /// </summary>
    /// <param name="sessionId">The session to observe.</param>
    /// <param name="cancellationToken">Ends the observation.</param>
    /// <returns>The initial replacement, then the bounded/coalesced replacements of that session.</returns>
    /// <exception cref="ArgumentException">The session identity is blank.</exception>
    /// <exception cref="InvalidOperationException">The live subscriber limit has been reached.</exception>
    /// <exception cref="OperationCanceledException">Observation was canceled.</exception>
    public async IAsyncEnumerable<RuntimeDisplayReplacement> ObserveSessionAsync(string sessionId, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        cancellationToken.ThrowIfCancellationRequested();
        Channel<bool> signal;
        RuntimeDisplaySnapshot snapshot;
        lock (_gate)
        {
            if (!_closed && _subscribers.Count + _sessionSubscriberCount >= MaxSubscribers)
                throw new InvalidOperationException("The runtime display subscriber limit has been reached.");
            signal = CreateSignal();
            snapshot = SnapshotOf(sessionId);
            if (!_closed)
            {
                if (!_sessionSubscribers.TryGetValue(sessionId, out var signals)) _sessionSubscribers[sessionId] = signals = [];
                signals.Add(signal);
                _sessionSubscriberCount++;
            }
        }

        using var registration = cancellationToken.Register(() => ReleaseSession(sessionId, signal));
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new(snapshot, true, null, false);
            while (!snapshot.IsClosed)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!await signal.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield break;
                }
                RuntimeDisplaySnapshot next;
                lock (_gate)
                {
                    signal.Reader.TryRead(out _);
                    next = SnapshotOf(sessionId);
                }
                if (next.Revision == snapshot.Revision) continue;
                var previous = snapshot.Revision;
                snapshot = next;
                yield return new(snapshot, false, previous, snapshot.Revision > previous + 1);
            }
        }
        finally { ReleaseSession(sessionId, signal); }
    }

    private static Channel<bool> CreateSignal()
        => Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });

    private void Release(Channel<bool> signal)
    {
        lock (_gate)
        {
            _subscribers.Remove(signal);
            signal.Writer.TryComplete();
        }
    }

    private void ReleaseSession(string sessionId, Channel<bool> signal)
    {
        lock (_gate)
        {
            if (_sessionSubscribers.TryGetValue(sessionId, out var signals) && signals.Remove(signal))
            {
                _sessionSubscriberCount--;
                if (signals.Count == 0) _sessionSubscribers.Remove(sessionId);
            }
            signal.Writer.TryComplete();
        }
    }

    private RuntimeDisplaySnapshot Snapshot() => new(_epoch, _revision, _closed,
        _sessions.Values.OrderBy(s => s.Revision).ToImmutableArray(), _evictedSessions, _omittedSessionEvents);

    private RuntimeDisplaySnapshot SnapshotOf(string sessionId) => new(_epoch, _revision, _closed,
        _sessions.TryGetValue(sessionId, out var session) ? [session] : [], _evictedSessions, _omittedSessionEvents);

    // Wakes the observations of every session and those of the session that changed; all of them when the counters changed.
    private void Wake(string? sessionId, bool countersChanged)
    {
        foreach (var signal in _subscribers) signal.Writer.TryWrite(true);
        if (countersChanged)
        {
            foreach (var signals in _sessionSubscribers.Values)
                foreach (var signal in signals) signal.Writer.TryWrite(true);
        }
        else if (sessionId is not null && _sessionSubscribers.TryGetValue(sessionId, out var signals))
        {
            foreach (var signal in signals) signal.Writer.TryWrite(true);
        }
    }

    internal void Commit(SessionRuntimeEvent? runtimeEvent)
    {
        lock (_gate)
        {
            if (_closed) return;
            var revision = _revision + 1;
            var countersChanged = false;
            if (runtimeEvent is null || !ValidIdentity(runtimeEvent.SessionId))
            {
                _omittedSessionEvents++;
                countersChanged = true;
            }
            else
            {
                var isNew = !_sessions.TryGetValue(runtimeEvent.SessionId, out var session);
                if (isNew)
                    session = new(runtimeEvent.SessionId, revision, null, null, null, null, null, [], false, 0, 0);

                // Prepare all display values before changing the committed revision, evicting or notifying.
                var candidate = Project(session with { Revision = revision }, runtimeEvent);
                if (isNew && _sessions.Count == MaxSessions)
                {
                    _sessions.Remove(_sessions.Values.MinBy(s => s.Revision).SessionId);
                    _evictedSessions++;
                    countersChanged = true;
                }
                _sessions[session.SessionId] = candidate;
            }
            _revision = revision;
            Wake(runtimeEvent?.SessionId, countersChanged);
        }
    }

    /// <summary>
    /// Removes what the retained window of a session holds of its conversation: its texts and its tool activities.
    /// For a session that was rewound: the runs they belong to are no longer in its journal, and a view that shows a
    /// live text until the journal holds it would show them again, after the turns that were kept.
    /// </summary>
    /// <param name="sessionId">The session; one that has no retained window is left alone.</param>
    internal void ForgetConversation(string sessionId)
    {
        lock (_gate)
        {
            if (_closed || !ValidIdentity(sessionId) || !_sessions.TryGetValue(sessionId, out var session)) return;
            if (session.Text.IsEmpty && session.ToolActivities.IsEmpty) return;
            var revision = _revision + 1;
            _sessions[session.SessionId] = session with { Revision = revision, Text = [], ToolActivities = [] };
            _revision = revision;
            Wake(session.SessionId, countersChanged: false);
        }
    }

    internal void Complete()
    {
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            _revision++;
            foreach (var signal in _subscribers.Concat(_sessionSubscribers.Values.SelectMany(static signals => signals)))
            {
                signal.Writer.TryWrite(true);
                signal.Writer.TryComplete();
            }
            _subscribers.Clear();
            _sessionSubscribers.Clear();
            _sessionSubscriberCount = 0;
        }
    }

    private static RuntimeDisplaySession Project(RuntimeDisplaySession session, SessionRuntimeEvent runtimeEvent)
    {
        var truncated = session.MetadataTruncated;
        string? Label(string? value)
        {
            if (value is null) return null;
            truncated |= value.Length > MaxMetadataCharacters;
            return Prefix(value, MaxMetadataCharacters);
        }
        RuntimeDisplayConfiguration Configuration(string? provider, string? key, string? model, AgentReasoningEffort? effort, string? prompt)
            => new(Label(provider), Label(key), Label(model), effort, Label(prompt));

        switch (runtimeEvent)
        {
            case SessionLifecycleRuntimeEvent { Event: not null } lifecycle:
                session = session with { Lifecycle = new(lifecycle.Event.Kind, Label(lifecycle.Event.RunId), Label(lifecycle.Event.Message)) };
                break;
            case SessionQueueRuntimeEvent queue:
                session = session with { QueuedPromptCount = queue.QueuedPromptCount };
                break;
            case SessionAgentConfigurationRuntimeEvent configuration:
                session = session with { Configuration = Configuration(configuration.ProviderId, configuration.ProviderKey,
                    configuration.ModelId, configuration.ReasoningEffort, configuration.AgentPromptId) };
                break;
            case SessionCatalogRuntimeEvent { Session: not null } catalog:
                var descriptor = catalog.Session;
                session = session with { Configuration = Configuration(descriptor.ProviderId, descriptor.ProviderKey,
                    descriptor.ModelId, descriptor.ReasoningEffort, descriptor.AgentPromptId) };
                break;
            case SessionHostEvent host:
                session = session with { StatusKind = host.Kind, StatusMessage = Label(host.Message) };
                break;
            case SessionAgentEvent { Event: AgentContentDeltaEvent delta }:
                return ProjectText(session, delta.RunId?.Value, delta.ContentId, delta.Kind, delta.Delta, delta.Timestamp, complete: false);
            case SessionAgentEvent { Event: AgentContentCompletedEvent completed }:
                // A user message is shown without the lines that name its image and attached files, like its persisted record.
                return ProjectText(session, completed.RunId?.Value, completed.ContentId, completed.Kind,
                    completed is { Kind: AgentContentKind.User, Content: { } typed }
                        ? PromptImageHistory.RemoveAttachmentLines(typed, completed.Details) : completed.Content,
                    completed.Timestamp, complete: true,
                    completed.Kind == AgentContentKind.User ? PromptImageHistory.ReadClientRequestId(completed.Details) : null);
            case SessionAgentEvent { Event: AgentActivityEvent activity }:
                return ProjectToolActivity(session, activity);
            default:
                session = session with { UnsupportedEvents = session.UnsupportedEvents + 1 };
                break;
        }
        return session with { MetadataTruncated = truncated };
    }

    private static RuntimeDisplaySession ProjectToolActivity(RuntimeDisplaySession session, AgentActivityEvent activity)
    {
        // Only these scalar fields are inspected. In particular, never touch Details, Message or parent/provider graphs.
        var activityId = activity.ActivityId is null ? null : CompactIdentifier(activity.ActivityId);
        if (activity.Kind != AgentActivityKind.ToolCall ||
            activity.Phase is not (AgentActivityPhase.Requested or AgentActivityPhase.Started or AgentActivityPhase.Progressed or
                AgentActivityPhase.Completed or AgentActivityPhase.Failed or AgentActivityPhase.Canceled) ||
            !ValidToolIdentity(activity.ProviderId.Value) || !ValidToolIdentity(activityId) ||
            (activity.RunId is { } suppliedRun && !ValidToolIdentity(suppliedRun.Value)) ||
            (activity.Name is { } name && !WellFormedToolString(name)))
            return session with { UnsupportedEvents = session.UnsupportedEvents + 1 };

        var runId = activity.RunId?.Value;
        var items = session.ToolActivities;
        var index = -1;
        for (var i = 0; i < items.Length; i++)
            if (items[i].ProviderId == activity.ProviderId.Value && items[i].RunId == runId && items[i].ActivityId == activityId)
            { index = i; break; }
        var value = new RuntimeDisplayToolActivity(activity.ProviderId.Value, runId, activityId, activity.Phase,
            activity.Name is null ? null : Prefix(activity.Name, MaxToolNameCharacters), activity.Name?.Length > MaxToolNameCharacters)
        {
            Timestamp = index >= 0 ? items[index].Timestamp : activity.Timestamp,
            Sequence = index >= 0 ? items[index].Sequence : session.Revision,
        };
        if (index >= 0) items = items.RemoveAt(index); // Latest report wins, even Completed -> Started.
        else if (items.Length == MaxToolActivitiesPerSession)
        {
            items = items.RemoveAt(0);
            session = session with { EvictedToolActivities = session.EvictedToolActivities + 1 };
        }
        return session with { ToolActivities = items.Add(value) };
    }

    /// <summary>
    /// Maps a provider-opaque content or activity identity longer than <see cref="MaxIdentifierCharacters" /> to a
    /// stable, collision-resistant SHA-256 form, so live and persisted displays correlate the same item.
    /// </summary>
    /// <param name="value">The identity reported by the provider, such as a Responses API item identity.</param>
    /// <returns>
    /// <paramref name="value" /> unchanged when it fits or contains an unpaired surrogate (left for validation to reject);
    /// otherwise <c>~sha256:</c> followed by the lowercase hexadecimal hash of its UTF-8 bytes.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="value" /> is null.</exception>
    public static string CompactIdentifier(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length <= MaxIdentifierCharacters || !WellFormedToolString(value)) return value;
        return "~sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static bool ValidToolIdentity([NotNullWhen(true)] string? value)
        => value is { Length: > 0 and <= MaxIdentifierCharacters } && !string.IsNullOrWhiteSpace(value) && WellFormedToolString(value);

    private static bool WellFormedToolString(string value)
    {
        // Local policy only: malformed names reject the entire report, including malformed suffixes beyond the prefix.
        // Existing text and status/configuration label handling is deliberately unchanged.
        for (var i = 0; i < value.Length; i++)
        {
            if (!char.IsSurrogate(value[i])) continue;
            if (!char.IsHighSurrogate(value[i]) || ++i == value.Length || !char.IsLowSurrogate(value[i])) return false;
        }
        return true;
    }

    private static RuntimeDisplaySession ProjectText(RuntimeDisplaySession session, string? runId, string contentId,
        AgentContentKind kind, string? text, DateTimeOffset timestamp, bool complete, string? clientRequestId = null)
    {
        // Do not retain tools, arbitrary Details, provider objects or invalid/aliased stable identities.
        if (contentId is not null) contentId = CompactIdentifier(contentId);
        if (text is null || !ValidIdentity(contentId) || (runId is not null && !ValidIdentity(runId)) ||
            kind is not (AgentContentKind.User or AgentContentKind.Assistant or AgentContentKind.Reasoning or AgentContentKind.ReasoningSummary or AgentContentKind.Plan or AgentContentKind.Notice))
            return session with { UnsupportedEvents = session.UnsupportedEvents + 1 };

        var items = session.Text;
        var index = -1;
        for (var i = 0; i < items.Length; i++)
            if (items[i].RunId == runId && items[i].ContentId == contentId && items[i].Kind == kind) { index = i; break; }
        var previous = index < 0 ? default : items[index];
        // A late delta must not unfinalize or append to finalized content.
        if (!complete && index >= 0 && previous.IsComplete)
            return session with { UnsupportedEvents = session.UnsupportedEvents + 1 };
        // Once the retained prefix has a hole, later deltas cannot fill it (including a surrogate-boundary cut).
        if (!complete && index >= 0 && previous.IsTruncated)
            return session with { Text = items.RemoveAt(index).Add(previous) };
        var prefix = complete || index < 0 ? string.Empty : previous.Text;
        var remaining = MaxTextCharacters - prefix.Length;
        var value = new RuntimeDisplayText(runId, contentId, kind, prefix + Prefix(text, remaining), complete,
            text.Length > remaining || (!complete && index >= 0 && previous.IsTruncated), !complete)
        {
            Timestamp = index >= 0 ? previous.Timestamp : timestamp,
            Sequence = index >= 0 ? previous.Sequence : session.Revision,
            ClientRequestId = clientRequestId ?? previous.ClientRequestId,
        };
        if (index >= 0) items = items.RemoveAt(index);
        else if (items.Length == MaxTextItemsPerSession)
        {
            items = items.RemoveAt(0);
            session = session with { EvictedTextItems = session.EvictedTextItems + 1 };
        }
        return session with { Text = items.Add(value) };
    }

    private static bool ValidIdentity([NotNullWhen(true)] string? value)
    {
        if (value is null || value.Length > MaxIdentifierCharacters || string.IsNullOrWhiteSpace(value)) return false;
        // JSON replacement of unpaired surrogates can alias distinct stable identities at a renderer boundary.
        for (var i = 0; i < value.Length; i++)
        {
            if (!char.IsSurrogate(value[i])) continue;
            if (!char.IsHighSurrogate(value[i]) || ++i == value.Length || !char.IsLowSurrogate(value[i])) return false;
        }
        return true;
    }

    private static string Prefix(string value, int limit)
    {
        if (value.Length <= limit) return value;
        // Do not split a well-formed surrogate pair at the retained prefix boundary.
        if (limit > 0 && char.IsHighSurrogate(value[limit - 1]) && char.IsLowSurrogate(value[limit])) limit--;
        return value[..limit];
    }
}
