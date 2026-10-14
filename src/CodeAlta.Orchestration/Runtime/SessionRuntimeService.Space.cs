using CodeAlta.Agent;
using CodeAlta.Catalog;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>What giving sessions another space did.</summary>
/// <param name="Status"><c>ok</c>, or why nothing (or not everything) moved: <c>busy</c>, <c>closed</c>,
/// <c>session_in_use</c>, <c>interaction_pending</c>, <c>session_missing</c>, <c>requires_recovery</c>,
/// <c>move_partial</c> or <c>move_unconfirmed</c>.</param>
/// <param name="Moved">The sessions whose space changed.</param>
/// <param name="Skipped">The sessions that were left where they were because they were at work (a bulk move only).</param>
/// <param name="Warnings">What went wrong without stopping the move, in words.</param>
public sealed record OwnedSessionSpaceMoveResult(string Status, IReadOnlyList<string> Moved, IReadOnlyList<string> Skipped, IReadOnlyList<string> Warnings)
{
    internal static OwnedSessionSpaceMoveResult Refused(string status) => new(status, [], [], []);
}

public sealed partial class SessionRuntimeService
{
    // The command owner excludes new owned sends and queues while this runs. Each session is checked again by its
    // actor where its journal is rewritten and its attachment retired.
    internal Task<OwnedSessionSpaceMoveResult> MoveOwnedSessionToSpaceAsync(string sessionId, string spaceId, Func<string, bool> ownerBusy)
        => AdmitAsync(() => MoveOwnedSessionToSpaceBodyAsync(sessionId, spaceId, ownerBusy), CancellationToken.None);

    // The chats of a space that is gone go to another space, the default one: those at work are left, and read as in
    // the default space all the same, since their space no longer exists.
    internal Task<OwnedSessionSpaceMoveResult> MoveOwnedSpaceChatsAsync(string fromSpaceId, string toSpaceId, Func<string, bool> ownerBusy)
        => AdmitAsync(() => MoveOwnedSpaceChatsBodyAsync(fromSpaceId, toSpaceId, ownerBusy), CancellationToken.None);

    private async Task<OwnedSessionSpaceMoveResult> MoveOwnedSessionToSpaceBodyAsync(string sessionId, string spaceId, Func<string, bool> ownerBusy)
    {
        var all = await ListStoredSessionsAsync().ConfigureAwait(false);
        var members = SessionFamily(sessionId, all);
        if (members.Count == 0) return OwnedSessionSpaceMoveResult.Refused("session_missing");
        foreach (var member in members)
        {
            if (ownerBusy(member)) return OwnedSessionSpaceMoveResult.Refused("busy");
            var refusal = await GetActorForWork(member).QueryAsync(token => new ValueTask<string?>(MoveRefusalAsync(member, token)), CancellationToken.None)
                .ConfigureAwait(false);
            if (refusal is not null) return OwnedSessionSpaceMoveResult.Refused(refusal);
        }

        // The sessions it started first and the session last, as a move to another project.
        var moved = new List<string>();
        var warnings = new List<string>();
        for (var index = members.Count - 1; index >= 0; index--)
        {
            var status = await MoveOneSessionToSpaceGuardedAsync(members[index], spaceId, ownerBusy, moved, warnings).ConfigureAwait(false);
            if (status != "ok") return new(moved.Count == 0 ? status : "move_partial", moved, [], warnings);
        }

        return new("ok", moved, [], warnings);
    }

    private async Task<OwnedSessionSpaceMoveResult> MoveOwnedSpaceChatsBodyAsync(string fromSpaceId, string toSpaceId, Func<string, bool> ownerBusy)
    {
        var chats = (await ListStoredSessionsAsync().ConfigureAwait(false))
            .Where(session => session.Kind == SessionViewKind.GlobalSession && string.Equals(session.SpaceId, fromSpaceId, StringComparison.Ordinal))
            .Select(static session => session.SessionId).ToArray();
        var moved = new List<string>();
        var skipped = new List<string>();
        var warnings = new List<string>();
        foreach (var chat in chats)
        {
            var status = await MoveOneSessionToSpaceGuardedAsync(chat, toSpaceId, ownerBusy, moved, warnings).ConfigureAwait(false);
            if (status != "ok") skipped.Add(chat);
        }

        return new("ok", moved, skipped, warnings);
    }

    // The stored sessions as the lists read them, with what their journal says of their space.
    private async Task<IReadOnlyList<SessionViewDescriptor>> ListStoredSessionsAsync()
    {
        var projects = await _projectCatalog.LoadAsync(CancellationToken.None).ConfigureAwait(false);
        var store = _sessionViewCatalog.JournalStore.CreateSessionStore();
        var sessions = new List<SessionViewDescriptor>();
        await foreach (var metadata in store.ListSessionsAsync(filter: null, cancellationToken: CancellationToken.None).ConfigureAwait(false))
        {
            if (TryCreateRecoverableSession(metadata, projects) is { } session)
            {
                session.ParentSessionId ??= NormalizeOptionalText(metadata.ViewState?.ParentSessionId) ?? NormalizeOptionalText(metadata.CreatedBySessionId);
                sessions.Add(session);
            }
        }

        return sessions;
    }

    // The session and, below it, the sessions it started, the session first: they work together, in one space.
    private static IReadOnlyList<string> SessionFamily(string sessionId, IReadOnlyList<SessionViewDescriptor> all)
    {
        var root = all.FirstOrDefault(session => string.Equals(session.SessionId, sessionId, StringComparison.OrdinalIgnoreCase));
        if (root is null || root.Kind is not (SessionViewKind.GlobalSession or SessionViewKind.ProjectSession)) return [];
        var members = new List<string> { root.SessionId };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root.SessionId };
        for (var index = 0; index < members.Count; index++)
        {
            foreach (var candidate in all)
            {
                if (seen.Contains(candidate.SessionId) || !string.Equals(candidate.ParentSessionId, members[index], StringComparison.OrdinalIgnoreCase)) continue;
                seen.Add(candidate.SessionId);
                members.Add(candidate.SessionId);
            }
        }

        return members;
    }

    private async Task<string> MoveOneSessionToSpaceGuardedAsync(string sessionId, string spaceId, Func<string, bool> ownerBusy, List<string> moved, List<string> warnings)
    {
        try
        {
            return await MoveOneSessionToSpaceAsync(sessionId, spaceId, ownerBusy, moved, warnings).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (moved.Contains(sessionId, StringComparer.OrdinalIgnoreCase))
            {
                // Its journal was rewritten before what failed: it moved.
                warnings.Add($"The session {sessionId} changed space, but: {exception.Message}");
                return "ok";
            }

            warnings.Add($"The session {sessionId} did not change space: {exception.Message}");
            return exception is InvalidOperationException ? "requires_recovery" : "move_unconfirmed";
        }
    }

    private async Task<string> MoveOneSessionToSpaceAsync(string sessionId, string spaceId, Func<string, bool> ownerBusy, List<string> moved, List<string> warnings)
    {
        if (ownerBusy(sessionId)) return "busy";
        var actor = GetActorForWork(sessionId);
        Task? retirement = null;
        var changed = false;
        var status = await actor.QueryAsync(async token =>
        {
            if (await MoveRefusalAsync(sessionId, token).ConfigureAwait(false) is { } refusal) return refusal;
            if (await ResolveOwnedSessionBodyAsync(sessionId, token).ConfigureAwait(false) is not { } session) return "session_missing";
            if (string.Equals(session.SpaceId, spaceId, StringComparison.Ordinal)) return "ok";
            var family = await RuntimeFamilyOfKeyWrittenAsFamilyAsync(
                await _sessionViewCatalog.JournalStore.CreateSessionStore().GetSessionSummaryAsync(sessionId, token).ConfigureAwait(false), token).ConfigureAwait(false);
            await _sessionViewCatalog.JournalStore.MoveSessionToSpaceAsync(session, spaceId, family, token).ConfigureAwait(false);
            changed = true;
            if (_entries.TryGetValue(sessionId, out var entry))
            {
                // The attachment has the instructions, the tools and the resources of the old space: the next send
                // attaches the session again, in its new space.
                retirement = _forwarding.RetireAsync(entry.Attachment);
                _entries.TryRemove(sessionId, out _);
                _transitions[sessionId] = retirement;
            }

            return "ok";
        }, CancellationToken.None).ConfigureAwait(false);
        if (status != "ok" || !changed) return status;
        moved.Add(sessionId);

        // The space is changed: what follows never undoes it, and the transition of the retired attachment is always
        // removed, or the session could never be attached again.
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
                warnings.Add($"The session {sessionId} changed space, but the lists may show it in its old space until they are read again: {exception.Message}");
            }

            if (retirement is not null)
            {
                try
                {
                    await retirement.ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    warnings.Add($"The session {sessionId} changed space, but its provider did not stop cleanly: {exception.Message}");
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
