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

        if (metadata.ViewState is not null)
        {
            ApplyCachedSessionLocalState(session, metadata.ViewState);
        }
        else
        {
            await ApplyPersistedSessionLocalStateAsync(session, cancellationToken).ConfigureAwait(false);
        }

        return session;
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
    /// The turns of the journal are found by the two ends of its records (<see cref="FileSystemAgentSessionStore.ReadTurnsAsync"/>),
    /// and only the records from the start of that turn are parsed. The whole history is read instead, as
    /// <see cref="TryReadStoredHistoryAsync(SessionViewDescriptor, Action{Exception}?, CancellationToken)"/> reads it, when the
    /// turn is more than half of the journal, when no turn has an assistant message, when the run of the last one is
    /// not a turn of the journal (a run of the provider, or no run), and when the journal cannot be read that way (a
    /// format the bounded readers do not take, a record they cannot parse, or a journal that keeps growing).
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

        if (start < 0 || !IsSmallPart(revision, start))
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
            if (!IsSmallPart(revision, start))
            {
                return null;
            }
        }
    }

    // Pages are worth reading for at most half of the journal: beyond it, one pass over the whole journal costs less.
    private static bool IsSmallPart(AgentHistoryRevision revision, long start)
        => start >= 0 && (revision.Length - start) * 2 <= revision.Length;
}
