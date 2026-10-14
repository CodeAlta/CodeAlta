using CodeAlta.Agent;

namespace CodeAlta.Catalog;

public sealed partial class SessionViewJournalStore
{
    /// <summary>Atomically gives an idle session another space, in its existing journal.</summary>
    /// <remarks>Only the first-line header changes, with the local state written again after it: the session keeps its
    /// project, its folder, its history and its provider continuation. The flushed same-directory journal replacement
    /// is the commit point; before it failures preserve the original. Derived caches are refreshed by their changed
    /// file stamp, and a reader that follows the journal by its first line reads it again. The caller must hold
    /// idle-session mutation admission and validate the space.</remarks>
    /// <param name="session">The session as the runtime knows it.</param>
    /// <param name="spaceId">The identifier of the space the session belongs to from now on.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentNullException">The session is null.</exception>
    /// <exception cref="ArgumentException">The space is not the identifier of a space.</exception>
    /// <exception cref="InvalidOperationException">The session is not a global or project session, changed, contains queued
    /// work, or requires recovery.</exception>
    /// <exception cref="IOException">Preparing or replacing the journal failed.</exception>
    /// <exception cref="OperationCanceledException">Canceled before the commit point.</exception>
    public async Task MoveSessionToSpaceAsync(SessionViewDescriptor session, string spaceId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!SpaceDescriptor.IsValidId(spaceId)) throw new ArgumentException($"'{spaceId}' is not the identifier of a space.", nameof(spaceId));
        if (session.Kind is not (SessionViewKind.GlobalSession or SessionViewKind.ProjectSession))
            throw new InvalidOperationException("Only a chat or a project session belongs to a space.");
        var store = CreateSessionStore();
        var original = await store.ReadTransferSnapshotAsync(session.SessionId, requireState: false, cancellationToken).ConfigureAwait(false);
        // Replacement rewrites the existing first-line view header; never discard a history record from an
        // unsupported/headerless journal in order to manufacture one.
        if (await ReadHeaderAsync(session.SessionId, session.CreatedAt, cancellationToken).ConfigureAwait(false) is not { } header
            || header.SessionId != session.SessionId || header.Kind != session.Kind)
            throw new InvalidOperationException("The session requires a valid catalog header before it changes space.");
        if (original.History.OfType<AgentActivityEvent>().Where(activity => activity.Kind == AgentActivityKind.Turn)
            .GroupBy(activity => (activity.ProviderId, activity.RunId, activity.ActivityId))
            .Any(turn => turn.Last().Phase is not (AgentActivityPhase.Completed or AgentActivityPhase.Failed or AgentActivityPhase.Canceled)))
            throw new InvalidOperationException("An unfinished persisted turn must be resolved before the session changes space.");
        var local = await ReadLatestStateAsync(session.SessionId, session.CreatedAt, cancellationToken).ConfigureAwait(false)
            ?? new SessionViewLocalState();
        if (local.QueuedPrompts.Any(prompt => prompt.State != "submitted"))
            throw new InvalidOperationException("Queued work must be resolved before the session changes space.");
        // The header keeps everything else it says: who created the session, when, where and on which provider.
        var moved = header.ToDescriptor();
        moved.SpaceId = spaceId;
        if (string.IsNullOrWhiteSpace(header.Title)) moved.Title = string.Empty;
        await store.CommitProviderSelectionAsync(original, original.Summary, original.State, CreateHeaderEvent(moved).ToJson(),
            [CreateStateEvent(moved, local).ToJson()], cancellationToken).ConfigureAwait(false);
        InvalidateLatestStateCache(GetPath(session.SessionId, session.CreatedAt));
    }
}
