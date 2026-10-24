using System.Collections.Concurrent;
using CodeAlta.Catalog;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>What archiving sessions, or taking them out of the archive, did.</summary>
/// <param name="Status"><c>ok</c>, or why nothing (or not everything) was done: <c>busy</c>, <c>closed</c>,
/// <c>session_missing</c>, <c>session_in_use</c>, <c>queue_pending</c>, <c>interaction_pending</c>, <c>io_failed</c>,
/// <c>archive_partial</c> or <c>archive_unconfirmed</c>.</param>
/// <param name="Changed">The sessions whose archive state changed: the session first, then the sessions below it.</param>
/// <param name="BlockingSessionId">The session that is at work, or the one that could not be changed.</param>
public sealed record SessionArchiveResult(string Status, IReadOnlyList<string> Changed, string? BlockingSessionId)
{
    internal static SessionArchiveResult Refused(string status, string? blocking = null) => new(status, [], blocking);
}

public sealed partial class SessionRuntimeService
{
    // When each session was taken out of the archive because a prompt was about to run in it.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _restoredForRun = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>When a prompt that was about to run in a session took it out of the archive, the last time; null otherwise.</summary>
    /// <param name="sessionId">The session.</param>
    /// <returns>The instant of that restore, or <see langword="null"/>.</returns>
    public DateTimeOffset? GetRestoredForRunAt(string sessionId)
        => _restoredForRun.TryGetValue(sessionId, out var at) ? at : null;

    // A prompt for an archived session (from an agent, a reminder, an automation, a queue) takes it out of the
    // archive before it runs, by the path of a restore: nothing runs in a session nobody sees. Not on the actor of
    // the session, which the restore queries.
    private async Task RestoreArchivedForRunAsync(string sessionId, DateTimeOffset createdAt)
    {
        if (createdAt == default) return;
        SessionViewLocalState? local;
        try
        {
            local = await _sessionViewCatalog.JournalStore.ReadLatestStateAsync(sessionId, createdAt, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException)
        {
            return;
        }

        if (local?.Archived != true) return;
        var changed = new List<string>();
        try
        {
            await SetOneSessionArchivedAsync(sessionId, archived: false, static _ => false, changed).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            // The prompt runs all the same; the lists say the session is archived until a restore succeeds.
        }

        if (changed.Count != 0) _restoredForRun[sessionId] = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Archives a session, or takes it out of the archive. An archived session keeps its history, its project and its
    /// space; the lists leave it out until it is restored. A session that works, or one below it, cannot be archived:
    /// what it does would report to a session nobody sees.
    /// </summary>
    /// <param name="sessionId">Exact catalog session identity.</param>
    /// <param name="archived">Whether the session is archived (true) or restored (false).</param>
    /// <param name="withChildren">Whether the sessions below it are archived or restored with it.</param>
    /// <param name="cancellationToken">Cancels waiting for admission only.</param>
    /// <returns>What changed, and the status (see <see cref="SessionArchiveResult.Status"/>).</returns>
    /// <exception cref="ArgumentException">The session is blank.</exception>
    public Task<SessionArchiveResult> SetSessionArchivedAsync(string sessionId, bool archived, bool withChildren, CancellationToken cancellationToken = default)
        => SetOwnedSessionArchivedAsync(sessionId, archived, withChildren, static _ => false, cancellationToken);

    // The command owner adds the sends and the questions it has under way for a session (`ownerBusy`).
    internal Task<SessionArchiveResult> SetOwnedSessionArchivedAsync(string sessionId, bool archived, bool withChildren, Func<string, bool> ownerBusy,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(ownerBusy);
        return AdmitAsync(() => SetSessionArchivedBodyAsync(sessionId.Trim(), archived, withChildren, ownerBusy), cancellationToken).WaitAsync(cancellationToken);
    }

    private async Task<SessionArchiveResult> SetSessionArchivedBodyAsync(string sessionId, bool archived, bool withChildren, Func<string, bool> ownerBusy)
    {
        var family = SessionFamily(sessionId, await ListStoredSessionsAsync().ConfigureAwait(false));
        if (family.Count == 0) return SessionArchiveResult.Refused("session_missing");
        if (archived)
        {
            // The sessions below one that is archived alone report to it: none of them may be at work either.
            foreach (var member in family)
            {
                if (ownerBusy(member)) return SessionArchiveResult.Refused("session_in_use", member);
                var refusal = await GetActorForWork(member).QueryAsync(token => new ValueTask<string?>(ArchiveRefusalAsync(member, token)), CancellationToken.None)
                    .ConfigureAwait(false);
                if (refusal is not null) return SessionArchiveResult.Refused(refusal, member);
            }
        }

        var changed = new List<string>();
        foreach (var member in withChildren ? family : [family[0]])
        {
            string status;
            try
            {
                status = await SetOneSessionArchivedAsync(member, archived, ownerBusy, changed).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The journal may have been written before what failed: the lists say what happened.
                status = changed.Contains(member, StringComparer.OrdinalIgnoreCase) ? "ok" : exception is IOException ? "io_failed" : "archive_unconfirmed";
            }

            if (status != "ok") return new(changed.Count == 0 ? status : "archive_partial", changed, member);
        }

        return new("ok", changed, null);
    }

    // Why a session cannot be archived now, or null. Runs on the actor of the session.
    private async Task<string?> ArchiveRefusalAsync(string sessionId, CancellationToken token)
    {
        if (_transitions.ContainsKey(sessionId)) return "session_in_use";
        if (_entries.TryGetValue(sessionId, out var entry) && (entry.HasActiveRun || entry.QueueDrainInProgress || entry.OwnedQueue is not null
            || entry.Attachment.IsRetiring || entry.PendingAgentPromptId is not null
            // The tasks of its provider report to it.
            || entry.BackgroundTasks.Any(static task => task.Outcome is null))) return "session_in_use";
        if ((await Permissions.ListAsync().ConfigureAwait(false)).Any(permission => permission.Handle.SessionId == sessionId)
            || (await Permissions.ListOwnedUserInputsAsync(sessionId, token).ConfigureAwait(false)).Entries.Count != 0) return "interaction_pending";
        if (await ResolveOwnedSessionBodyAsync(sessionId, token).ConfigureAwait(false) is not { } session) return "session_missing";
        var local = await _sessionViewCatalog.JournalStore.ReadLatestStateAsync(sessionId, session.CreatedAt, token).ConfigureAwait(false);
        return local?.HasPendingQueuedPrompts() == true ? "queue_pending" : null;
    }

    private async Task<string> SetOneSessionArchivedAsync(string sessionId, bool archived, Func<string, bool> ownerBusy, List<string> changed)
    {
        if (archived && ownerBusy(sessionId)) return "session_in_use";
        var actor = GetActorForWork(sessionId);
        Task? retirement = null;
        SessionViewDescriptor? written = null;
        var status = await actor.QueryAsync(async token =>
        {
            if (archived && await ArchiveRefusalAsync(sessionId, token).ConfigureAwait(false) is { } refusal) return refusal;
            if (await ResolveOwnedSessionBodyAsync(sessionId, token).ConfigureAwait(false) is not { } session) return "session_missing";
            // Read as it is, never as a blank state: what the session chose (its mode, its queue) is written back with it.
            var local = await _sessionViewCatalog.JournalStore.ReadLatestStateAsync(sessionId, session.CreatedAt, token).ConfigureAwait(false)
                ?? new SessionViewLocalState();
            if (local.Archived == archived) return "ok";
            local.Archived = archived;
            local.ArchivedAt = archived ? DateTimeOffset.UtcNow : null;
            await _sessionViewCatalog.JournalStore.AppendStateAsync(session, local, token).ConfigureAwait(false);
            changed.Add(sessionId);
            written = session;
            if (archived && _entries.TryGetValue(sessionId, out var entry) && HasOwnedCommandDefaults(entry))
            {
                // An archived session keeps no provider at work: it is attached again if it is used again. An
                // attachment another front end made (the terminal UI) is its own to end.
                retirement = _forwarding.RetireAsync(entry.Attachment);
                _entries.TryRemove(sessionId, out _);
                _transitions[sessionId] = retirement;
            }

            return "ok";
        }, CancellationToken.None).ConfigureAwait(false);
        if (status != "ok" || written is null) return status;

        // The archive state is written: what follows never undoes it, and the transition of the retired attachment is
        // always removed, or the session could never be attached again.
        try
        {
            try
            {
                PublishSessionLifecycleEvent(sessionId);
                if (await ResolveOwnedSessionBodyAsync(sessionId, CancellationToken.None).ConfigureAwait(false) is { } session)
                    PublishSessionCatalogEvent(session);
                await _agentSessionCatalog.NotifySessionUpdatedAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The lists read the journal again on their next refresh.
            }

            if (retirement is not null)
            {
                try
                {
                    await retirement.ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // The provider of an archived session did not stop cleanly; the session is archived all the same.
                }
            }
        }
        finally
        {
            if (retirement is not null)
            {
                await actor.QueryAsync(_ =>
                {
                    if (_transitions.TryGetValue(sessionId, out var current) && ReferenceEquals(current, retirement))
                        _transitions.TryRemove(sessionId, out var removed);
                    return ValueTask.FromResult(true);
                }, CancellationToken.None).ConfigureAwait(false);
                PublishSessionLifecycleEvent(sessionId);
            }
        }

        return "ok";
    }
}
