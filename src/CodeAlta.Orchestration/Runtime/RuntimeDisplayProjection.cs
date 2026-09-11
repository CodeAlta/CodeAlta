using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using CodeAlta.Agent;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>
/// Runtime-owned committed live status/text projection. A short single-writer gate makes commit and
/// snapshot/subscription atomic across sessions. No I/O, callbacks, asynchronous work or observer waits run under it.
/// Limits bound retained payload/counts, not total process heap or consumer-retained snapshots.
/// </summary>
public sealed class RuntimeDisplayProjection
{
    /// <summary>Maximum retained session windows; least recently published is evicted on admission.</summary>
    public const int MaxSessions = 128;
    /// <summary>Maximum retained text items per session.</summary>
    public const int MaxTextItemsPerSession = 8;
    /// <summary>Maximum UTF-16 code units per text item.</summary>
    public const int MaxTextCharacters = 4096;
    /// <summary>Maximum UTF-16 code units per identity; oversized text/session identities are omitted, never aliased by truncation.</summary>
    public const int MaxIdentifierCharacters = 256;
    /// <summary>Maximum UTF-16 code units per status/configuration/lifecycle label.</summary>
    public const int MaxMetadataCharacters = 512;
    /// <summary>Maximum simultaneous observations, each with one payload-free wakeup slot and no replay buffer.</summary>
    public const int MaxSubscribers = 32;

    private readonly object _gate = new();
    private readonly Guid _epoch = Guid.NewGuid();
    private readonly Dictionary<string, RuntimeDisplaySession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Channel<bool>> _subscribers = [];
    private long _revision, _evictedSessions, _omittedSessionEvents;
    private bool _closed;

    internal RuntimeDisplayProjection() { }

    internal int SubscriberCount { get { lock (_gate) return _subscribers.Count; } }

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
            if (!_closed && _subscribers.Count >= MaxSubscribers)
                throw new InvalidOperationException("The runtime display subscriber limit has been reached.");
            signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });
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

    private void Release(Channel<bool> signal)
    {
        lock (_gate)
        {
            _subscribers.Remove(signal);
            signal.Writer.TryComplete();
        }
    }

    private RuntimeDisplaySnapshot Snapshot() => new(_epoch, _revision, _closed,
        _sessions.Values.OrderBy(s => s.Revision).ToImmutableArray(), _evictedSessions, _omittedSessionEvents);

    internal void Commit(SessionRuntimeEvent? runtimeEvent)
    {
        lock (_gate)
        {
            if (_closed) return;
            var revision = _revision + 1;
            if (runtimeEvent is null || !ValidIdentity(runtimeEvent.SessionId)) _omittedSessionEvents++;
            else
            {
                var isNew = !_sessions.TryGetValue(runtimeEvent.SessionId, out var session);
                if (isNew)
                    session = new(runtimeEvent.SessionId, revision, null, null, null, null, null, [], false, 0, 0);

                // Prepare all display values before changing the committed revision, evicting or notifying.
                var candidate = Project(session, runtimeEvent) with { Revision = revision };
                if (isNew && _sessions.Count == MaxSessions)
                {
                    _sessions.Remove(_sessions.Values.MinBy(s => s.Revision).SessionId);
                    _evictedSessions++;
                }
                _sessions[session.SessionId] = candidate;
            }
            _revision = revision;
            foreach (var signal in _subscribers) signal.Writer.TryWrite(true);
        }
    }

    internal void Complete()
    {
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            _revision++;
            foreach (var signal in _subscribers)
            {
                signal.Writer.TryWrite(true);
                signal.Writer.TryComplete();
            }
            _subscribers.Clear();
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
                return ProjectText(session, delta.RunId?.Value, delta.ContentId, delta.Kind, delta.Delta, complete: false);
            case SessionAgentEvent { Event: AgentContentCompletedEvent completed }:
                return ProjectText(session, completed.RunId?.Value, completed.ContentId, completed.Kind, completed.Content, complete: true);
            default:
                session = session with { UnsupportedEvents = session.UnsupportedEvents + 1 };
                break;
        }
        return session with { MetadataTruncated = truncated };
    }

    private static RuntimeDisplaySession ProjectText(RuntimeDisplaySession session, string? runId, string contentId,
        AgentContentKind kind, string? text, bool complete)
    {
        // Do not retain tools, arbitrary Details, provider objects or invalid/aliased stable identities.
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
            text.Length > remaining || (!complete && index >= 0 && previous.IsTruncated), !complete);
        if (index >= 0) items = items.RemoveAt(index);
        else if (items.Length == MaxTextItemsPerSession)
        {
            items = items.RemoveAt(0);
            session = session with { EvictedTextItems = session.EvictedTextItems + 1 };
        }
        return session with { Text = items.Add(value) };
    }

    private static bool ValidIdentity(string? value) => value is not null && value.Length <= MaxIdentifierCharacters && !string.IsNullOrWhiteSpace(value);

    private static string Prefix(string value, int limit)
    {
        if (value.Length <= limit) return value;
        // Do not split a well-formed surrogate pair at the retained prefix boundary.
        if (limit > 0 && char.IsHighSurrogate(value[limit - 1]) && char.IsLowSurrogate(value[limit])) limit--;
        return value[..limit];
    }
}
