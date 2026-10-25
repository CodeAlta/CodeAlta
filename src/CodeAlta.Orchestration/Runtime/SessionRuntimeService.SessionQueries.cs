using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;

namespace CodeAlta.Orchestration.Runtime;

public sealed partial class SessionRuntimeService
{
    // How many times a read of the last turn starts again when the journal grew while it was read.
    private const int LastTurnReadAttempts = 3;

    /// <summary>
    /// Gets one recoverable user-facing session by its identifier, as <see cref="ListRecoverableSessionsAsync(CancellationToken)"/>
    /// lists it, without listing the other sessions.
    /// </summary>
    /// <param name="sessionId">The identifier of the session.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The session; <see langword="null"/> when the store has no such session or the listing would leave it out (no
    /// provider, no folder, or a folder that is no project of the catalog).
    /// </returns>
    /// <remarks>
    /// The session is read from the store, while the listing reads the snapshot the session catalog keeps of it: the two
    /// differ only for a change the catalog was not told of yet. The store finds an identifier that differs in case;
    /// a caller that matches identifiers exactly compares <see cref="SessionViewDescriptor.SessionId"/>.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is blank.</exception>
    /// <exception cref="IOException">The journal or the catalog of the projects cannot be read.</exception>
    /// <exception cref="OperationCanceledException">The read is canceled.</exception>
    public async Task<SessionViewDescriptor?> TryGetRecoverableSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var metadata = await _sessionViewCatalog.JournalStore.CreateSessionStore()
            .GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (metadata is null)
        {
            return null;
        }

        // What ListRecoverableSessionsAsync does with each session it lists.
        var projects = await _projectCatalog.LoadAsync(cancellationToken).ConfigureAwait(false);
        var session = TryCreateRecoverableSession(metadata, projects);
        if (session is null)
        {
            return null;
        }

        await ApplySessionLocalStateAsync(session, metadata, cancellationToken).ConfigureAwait(false);
        return session;
    }

    /// <summary>
    /// Gets one session by its identifier as <see cref="TryGetRecoverableSessionAsync"/> does, and also a session whose
    /// folder is no project of the catalog: that folder is then given a transient project
    /// (<see cref="ProjectCatalog.GetOrAddTransientAsync"/>), which this runtime knows until it ends and which is never
    /// written, so that the session can be resumed and sent to. A headless process resumes sessions this way.
    /// </summary>
    /// <param name="sessionId">The identifier of the session.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The session; <see langword="null"/> when the store has no such session, or it names no provider or no folder.</returns>
    /// <remarks>
    /// The listings of the sessions (<see cref="ListRecoverableSessionsAsync(CancellationToken)"/>) leave out the sessions of a
    /// folder outside the catalog until a project of this runtime names it, as before: this lookup is what binds one.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is blank.</exception>
    /// <exception cref="IOException">The journal or the catalog of the projects cannot be read.</exception>
    /// <exception cref="OperationCanceledException">The read is canceled.</exception>
    public async Task<SessionViewDescriptor?> TryGetResumableSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var metadata = await _sessionViewCatalog.JournalStore.CreateSessionStore()
            .GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (metadata is null)
        {
            return null;
        }

        var projects = await _projectCatalog.LoadAsync(cancellationToken).ConfigureAwait(false);
        var session = TryCreateRecoverableSession(metadata, projects);
        if (session is null && !string.IsNullOrWhiteSpace(metadata.ProviderKey)
            && (metadata.Context?.Cwd ?? metadata.WorkspacePath) is { Length: > 0 } folder && Path.IsPathFullyQualified(folder))
        {
            await _projectCatalog.GetOrAddTransientAsync(folder, cancellationToken).ConfigureAwait(false);
            projects = await _projectCatalog.LoadAsync(cancellationToken).ConfigureAwait(false);
            session = TryCreateRecoverableSession(metadata, projects);
        }

        if (session is null)
        {
            return null;
        }

        await ApplySessionLocalStateAsync(session, metadata, cancellationToken).ConfigureAwait(false);
        return session;
    }

    private async Task ApplySessionLocalStateAsync(SessionViewDescriptor session, AgentSessionMetadata metadata, CancellationToken cancellationToken)
    {
        if (metadata.ViewState is not null)
        {
            ApplyCachedSessionLocalState(session, metadata.ViewState);
        }
        else
        {
            await ApplyPersistedSessionLocalStateAsync(session, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads the persisted history of a session as far back as its last answered turn goes: the events from the first
    /// record of the run of its last assistant message that has a run to the end of the journal. The events of that
    /// run, and the last assistant message, are the same as in <see cref="TryReadStoredHistoryAsync(SessionViewDescriptor, Action{Exception}?, CancellationToken)"/>.
    /// </summary>
    /// <param name="session">The session descriptor.</param>
    /// <param name="onUnavailable">Optional callback invoked when a local history file exists but cannot be read.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The end of the stored history, or the whole of it; <see langword="null"/> when it is not available, as
    /// <see cref="TryReadStoredHistoryAsync(SessionViewDescriptor, Action{Exception}?, CancellationToken)"/> returns it.
    /// </returns>
    /// <remarks>
    /// The turns of the journal come from the session index of the store (<see cref="FileSystemAgentSessionStore.ReadTurnsAsync"/>),
    /// and only the records from the start of that turn are read. The whole history is read instead, as
    /// <see cref="TryReadStoredHistoryAsync(SessionViewDescriptor, Action{Exception}?, CancellationToken)"/> reads it, when no
    /// turn has an assistant message, when the run of the last one is not a turn of the journal (a run of the provider, or
    /// no run), and when the journal cannot be read that way (a format the bounded readers do not take, a record they
    /// cannot parse, or a journal that keeps growing).
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">The read is canceled.</exception>
    public async Task<IReadOnlyList<AgentEvent>?> TryReadStoredLastTurnHistoryAsync(
        SessionViewDescriptor session,
        Action<Exception>? onUnavailable,
        CancellationToken cancellationToken = default)
        => await AdmitAsync(() => TryReadStoredLastTurnHistoryOwnedBodyAsync(session, onUnavailable, cancellationToken), cancellationToken)
            .WaitAsync(cancellationToken).ConfigureAwait(false);

    private async Task<IReadOnlyList<AgentEvent>?> TryReadStoredLastTurnHistoryOwnedBodyAsync(SessionViewDescriptor session,
        Action<Exception>? onUnavailable, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (string.IsNullOrWhiteSpace(session.SessionId))
        {
            return null;
        }

        var store = _sessionViewCatalog.JournalStore.CreateSessionStore();
        for (var attempt = 0; attempt < LastTurnReadAttempts; attempt++)
        {
            IReadOnlyList<AgentEvent>? tail;
            try
            {
                var turns = await store.ReadTurnsAsync(session.SessionId, cancellationToken).ConfigureAwait(false);
                tail = await ReadLastTurnTailAsync(store, session.SessionId, turns, cancellationToken).ConfigureAwait(false);
            }
            catch (AgentSessionHistoryException error) when (error.Code == "history_changed")
            {
                // The session wrote meanwhile: its turns are read again.
                continue;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // Whatever the bounded readers do not take, the reader of the whole history reads, or reports.
                break;
            }

            if (tail is not null)
            {
                return tail;
            }

            break;
        }

        return await TryReadStoredHistoryOwnedBodyAsync(session, onUnavailable, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads a window of the persisted history of a session, numbered as in the whole history (the first event is 1), by
    /// reading only the turns it spans: the events after <paramref name="since"/> that <paramref name="include"/> keeps,
    /// the first <paramref name="limit"/> of them, or the last ones for <paramref name="fromTail"/>.
    /// </summary>
    /// <param name="session">The session descriptor.</param>
    /// <param name="since">The number of the last event already seen; null for none.</param>
    /// <param name="limit">The most events to return.</param>
    /// <param name="fromTail">True for the last events, false for the first ones.</param>
    /// <param name="include">Which events count.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The events with their numbers, in journal order, and whether more events than <paramref name="limit"/> count;
    /// null when the window cannot be read this way (no stored history, a journal the bounded readers do not take, or
    /// one that keeps changing): the whole history is then read, as <see cref="TryReadStoredHistoryAsync(SessionViewDescriptor, Action{Exception}?, CancellationToken)"/> reads it.
    /// </returns>
    /// <remarks>
    /// The turns come from the session index of the store, with the number of events of the history before each of
    /// them; the journal is read from the start of a turn, a turn at a time, until the window is complete.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> or <paramref name="include"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="limit"/> is not positive.</exception>
    /// <exception cref="OperationCanceledException">The read is canceled.</exception>
    public async Task<StoredHistoryWindow?> TryReadStoredHistoryWindowAsync(SessionViewDescriptor session, long? since, int limit, bool fromTail,
        Func<AgentEvent, bool> include, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(include);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        return await AdmitAsync(() => TryReadStoredHistoryWindowOwnedBodyAsync(session, since ?? 0, limit, fromTail, include, cancellationToken), cancellationToken)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<StoredHistoryWindow?> TryReadStoredHistoryWindowOwnedBodyAsync(SessionViewDescriptor session, long since, int limit, bool fromTail,
        Func<AgentEvent, bool> include, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(session.SessionId))
        {
            return null;
        }

        var store = _sessionViewCatalog.JournalStore.CreateSessionStore();
        for (var attempt = 0; attempt < LastTurnReadAttempts; attempt++)
        {
            try
            {
                var turns = await store.ReadTurnsAsync(session.SessionId, cancellationToken).ConfigureAwait(false);
                if (turns.HistoryCount == 0)
                {
                    // Nothing stored: the caller reads what the provider keeps, as for the whole history.
                    return null;
                }

                return await ReadHistoryWindowAsync(store, turns, since, limit, fromTail, include, cancellationToken).ConfigureAwait(false);
            }
            catch (AgentSessionHistoryException error) when (error.Code == "history_changed")
            {
                // The journal was replaced meanwhile: its turns are read again.
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                break;
            }
        }

        return null;
    }

    // The window, read a segment at a time: what precedes the first turn, then each turn, each with the number of events
    // before it; null when a segment does not hold the number of events its numbers say.
    private static async Task<StoredHistoryWindow?> ReadHistoryWindowAsync(FileSystemAgentSessionStore store, AgentSessionTurns turns,
        long since, int limit, bool fromTail, Func<AgentEvent, bool> include, CancellationToken cancellationToken)
    {
        var segments = new List<(long Start, long End, long First, long Count)>(turns.Turns.Count + 1);
        var start = 0L;
        var first = 0L;
        foreach (var turn in turns.Turns)
        {
            segments.Add((start, turn.StartOffset, first, turn.HistoryIndex - first));
            (start, first) = (turn.StartOffset, turn.HistoryIndex);
        }

        segments.Add((start, turns.EndOffset, first, turns.HistoryCount - first));
        var wanted = limit + 1;
        var found = new List<StoredHistoryEvent>();
        var order = fromTail ? Enumerable.Range(0, segments.Count).Reverse() : Enumerable.Range(0, segments.Count);
        foreach (var index in order)
        {
            var segment = segments[index];
            // A segment whose events are all at or before the last one seen holds none of the window.
            if (segment.Count == 0 || segment.First + segment.Count <= since)
            {
                if (fromTail && segment.First + segment.Count <= since) break;
                continue;
            }

            var events = await store.ReadHistoryRangeAsync(turns.Revision, segment.Start, segment.End, cancellationToken).ConfigureAwait(false);
            if (events.Count != segment.Count)
            {
                return null;
            }

            var kept = new List<StoredHistoryEvent>();
            for (var position = 0; position < events.Count; position++)
            {
                var sequence = segment.First + position + 1;
                if (sequence > since && include(events[position]))
                {
                    kept.Add(new(events[position], sequence));
                }
            }

            if (fromTail)
            {
                found.InsertRange(0, kept);
            }
            else
            {
                found.AddRange(kept);
            }

            if (found.Count >= wanted)
            {
                break;
            }
        }

        var truncated = found.Count > limit;
        var window = !truncated ? found : fromTail ? found.GetRange(found.Count - limit, limit) : found.GetRange(0, limit);
        return new StoredHistoryWindow(window, truncated);
    }

    // The events from the start of the turn of the last assistant message to the end of the journal, read from the
    // revision the turns were read from; null when the whole history is to be read instead.
    private static async Task<IReadOnlyList<AgentEvent>?> ReadLastTurnTailAsync(FileSystemAgentSessionStore store, string sessionId,
        AgentSessionTurns turns, CancellationToken cancellationToken)
    {
        var revision = turns.Revision;
        // The last assistant message is in the turn that starts last among those that have one, or after it.
        var start = -1L;
        foreach (var turn in turns.Turns)
        {
            if (turn.AssistantMessageCount > 0 && turn.StartOffset > start)
            {
                start = turn.StartOffset;
            }
        }

        if (start < 0)
        {
            return null;
        }

        // The pages come from the end, the newest first.
        var pages = new List<AgentSessionHistoryPage>();
        AgentSessionHistoryCursor? cursor = null;
        var readFrom = revision.Length;
        while (true)
        {
            while (readFrom > start)
            {
                var page = await store.ReadTimelinePageAsync(sessionId, cursor, cancellationToken).ConfigureAwait(false);
                if (page.Revision is not { } read || read.Length != revision.Length || read.LastWriteUtcTicks != revision.LastWriteUtcTicks)
                {
                    throw new AgentSessionHistoryException("history_changed");
                }

                pages.Add(page);
                cursor = page.Next;
                readFrom = cursor?.Offset ?? 0;
            }

            var events = new List<AgentEvent>();
            for (var index = pages.Count - 1; index >= 0; index--)
            {
                foreach (var entry in pages[index].Entries)
                {
                    if (entry.Offset >= start)
                    {
                        events.Add(entry.Event);
                    }
                }
            }

            // The run of the last assistant message that has one: all its records are after the first one the turns name.
            string? runId = null;
            for (var index = events.Count - 1; index >= 0 && runId is null; index--)
            {
                if (events[index] is AgentContentCompletedEvent { Kind: AgentContentKind.Assistant, RunId: { } assistantRunId })
                {
                    runId = assistantRunId.Value;
                }
            }

            if (string.IsNullOrWhiteSpace(runId))
            {
                return null;
            }

            AgentSessionTurn? lastTurn = null;
            foreach (var turn in turns.Turns)
            {
                if (string.Equals(turn.RunId, runId, StringComparison.Ordinal))
                {
                    lastTurn = turn;
                    break;
                }
            }

            if (lastTurn is null)
            {
                return null;
            }

            if (lastTurn.StartOffset >= start)
            {
                return events;
            }

            // A run that started before the turn that starts last: its first records are further back.
            start = lastTurn.StartOffset;
        }
    }
}

/// <summary>A window of the persisted history of a session.</summary>
/// <param name="Events">The events of the window, in journal order.</param>
/// <param name="Truncated">Whether more events than the window holds count.</param>
public sealed record StoredHistoryWindow(IReadOnlyList<StoredHistoryEvent> Events, bool Truncated);

/// <summary>An event of the persisted history of a session, with its number in the whole history.</summary>
/// <param name="Event">The event.</param>
/// <param name="Sequence">Its number: 1 for the first event of the history.</param>
public sealed record StoredHistoryEvent(AgentEvent Event, long Sequence);
