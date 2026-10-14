using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Jobs;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>What deleting a session with all its descendants did.</summary>
/// <param name="Status"><c>ok</c>, or why nothing (or not everything) was deleted: <c>busy</c>, <c>closed</c>,
/// <c>session_missing</c>, <c>descendants_changed</c>, <c>session_in_use</c>, <c>delete_partial</c> or
/// <c>delete_unconfirmed</c>.</param>
/// <param name="Deleted">The sessions that were deleted, the descendants first and the session last.</param>
/// <param name="BlockingSessionId">The session that stopped the deletion: the one at work for <c>session_in_use</c>,
/// the one that could not be deleted for <c>delete_partial</c>; null otherwise.</param>
public sealed record OwnedSessionTreeDeletion(string Status, IReadOnlyList<string> Deleted, string? BlockingSessionId)
{
    internal static OwnedSessionTreeDeletion Refused(string status, string? blockingSessionId = null) => new(status, [], blockingSessionId);
}

public sealed partial class SessionRuntimeService
{
    // The command owner excludes new owned sends and queues while this runs; each session of the tree is claimed, as
    // the sessions of a deleted project are, before the first one is deleted.
    internal Task<OwnedSessionTreeDeletion> DeleteOwnedCatalogSessionTreeAsync(string sessionId, string? projectId, string workspacePath,
        string confirmedTitle, IReadOnlyCollection<string> expectedDescendants, Func<string, bool> isBusy)
        => AdmitAsync(() => DeleteOwnedCatalogSessionTreeBodyAsync(sessionId, projectId, workspacePath, confirmedTitle, expectedDescendants, isBusy),
            CancellationToken.None);

    // The sessions among these that something goes on in, as a deletion would find them; nothing is claimed.
    internal Task<IReadOnlySet<string>> ReadSessionsAtWorkAsync(IReadOnlyCollection<string> sessionIds, Func<string, bool> isBusy)
        => AdmitAsync<IReadOnlySet<string>>(async () =>
        {
            var atWork = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in sessionIds)
            {
                if (isBusy(id) || Jobs.List(id).Any(static job => job.State == SessionJobState.Running)
                    || await IsAtWorkAsync(id).ConfigureAwait(false))
                    atWork.Add(id);
            }

            return atWork;
        }, CancellationToken.None);

    private async Task<OwnedSessionTreeDeletion> DeleteOwnedCatalogSessionTreeBodyAsync(string sessionId, string? projectId, string workspacePath,
        string confirmedTitle, IReadOnlyCollection<string> expectedDescendants, Func<string, bool> isBusy)
    {
        var store = _sessionViewCatalog.JournalStore.CreateSessionStore();
        var all = new List<AgentSessionMetadata>();
        await foreach (var metadata in store.ListSessionsForDeletionAsync(CancellationToken.None).ConfigureAwait(false))
            all.Add(metadata);
        var root = all.FirstOrDefault(metadata => metadata.SessionId == sessionId);
        if (root is null || await ValidateListedSessionAsync(root, projectId, workspacePath, confirmedTitle, CancellationToken.None).ConfigureAwait(false) != "ok")
            return OwnedSessionTreeDeletion.Refused("session_missing");

        // What is deleted is what the user saw: a child that came or went since then is shown first.
        var descendants = SessionLineage.Descendants(root.SessionId, all);
        var ids = descendants.Select(static descendant => descendant.Session.SessionId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!ids.SetEquals(expectedDescendants)) return OwnedSessionTreeDeletion.Refused("descendants_changed");
        if (descendants.FirstOrDefault(static descendant => FolderOf(descendant.Session) is null) is { } unplaced)
            return OwnedSessionTreeDeletion.Refused("session_missing", unplaced.Session.SessionId);

        var claims = new List<IDisposable>();
        try
        {
            // Nothing is deleted while one of them is at work: a run is never stopped to delete it.
            foreach (var id in ids.Prepend(root.SessionId))
                if (!await ClaimIdleForDeletionAsync(id, isBusy, claims).ConfigureAwait(false))
                    return OwnedSessionTreeDeletion.Refused("session_in_use", id);

            // From the bottom of the tree: a session is deleted after the sessions below it, so what a failure leaves
            // keeps its parents.
            var deleted = new List<string>();
            for (var index = descendants.Count - 1; index >= 0; index--)
            {
                var member = descendants[index].Session;
                var result = await DeleteOwnedCatalogSessionBodyAsync(member.SessionId, null, FolderOf(member)!, confirmedTitle, ids).ConfigureAwait(false);
                if (result != "ok") return new(deleted.Count == 0 ? result : "delete_partial", deleted, member.SessionId);
                deleted.Add(member.SessionId);
            }

            // The session itself as Delete deletes it: its title and scope are checked again, and a child that came
            // in the meantime keeps it.
            var last = await DeleteOwnedCatalogSessionBodyAsync(root.SessionId, projectId, workspacePath, confirmedTitle).ConfigureAwait(false);
            if (last != "ok") return new(deleted.Count == 0 ? last : "delete_partial", deleted, root.SessionId);
            deleted.Add(root.SessionId);
            return new("ok", deleted, null);
        }
        finally
        {
            foreach (var claim in claims) claim.Dispose();
        }
    }

    private static string? FolderOf(AgentSessionMetadata metadata)
        => (metadata.Context?.Cwd ?? metadata.WorkspacePath) is var path && !string.IsNullOrWhiteSpace(path) ? path : null;

    // The session as the list shows it: in that scope, with that title. For a session that was never named, the title
    // is the first line of its summary.
    private async Task<string> ValidateListedSessionAsync(AgentSessionMetadata metadata, string? projectId, string workspacePath, string confirmedTitle,
        CancellationToken cancellationToken)
    {
        if (metadata.WorkspacePath != workspacePath) return "session_missing";
        var projects = await _projectCatalog.LoadAsync(cancellationToken).ConfigureAwait(false);
        var session = TryCreateRecoverableSession(metadata, projects);
        if (session is null || session.SessionId != metadata.SessionId || session.ProjectRef != projectId
            || session.Kind != (projectId is null ? SessionViewKind.GlobalSession : SessionViewKind.ProjectSession))
            return "session_missing";
        return ListedTitle(metadata, session.Kind, ProjectOf(session, projects)) == confirmedTitle ? "ok" : "session_missing";
    }
}
