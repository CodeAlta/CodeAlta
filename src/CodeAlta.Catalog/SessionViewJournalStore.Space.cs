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
    /// <exception cref="SessionViewChangeRefusedException">The session has a turn that did not end or a pending queued prompt.</exception>
    /// <exception cref="InvalidOperationException">The session is not a global or project session, changed, or requires recovery.</exception>
    /// <exception cref="IOException">Preparing or replacing the journal failed.</exception>
    /// <exception cref="OperationCanceledException">Canceled before the commit point.</exception>
    public Task MoveSessionToSpaceAsync(SessionViewDescriptor session, string spaceId, CancellationToken cancellationToken = default)
        => MoveSessionToSpaceAsync(session, spaceId, providerFamily: null, cancellationToken);

    /// <summary>
    /// Atomically gives an idle session another space, knowing the protocol family of the runtime of its provider: a
    /// summary that names the provider key as family, as CodeAlta 1.8 wrote it, does not make the session need
    /// recovery, and is written with that family.
    /// </summary>
    /// <param name="session">The session as the runtime knows it.</param>
    /// <param name="spaceId">The identifier of the space the session belongs to from now on.</param>
    /// <param name="providerFamily">The protocol family of the runtime of the session's provider; null when unknown.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes once the session has its new space.</returns>
    /// <inheritdoc cref="MoveSessionToSpaceAsync(SessionViewDescriptor, string, CancellationToken)" path="/exception"/>
    public async Task MoveSessionToSpaceAsync(SessionViewDescriptor session, string spaceId, string? providerFamily, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!SpaceDescriptor.IsValidId(spaceId)) throw new ArgumentException($"'{spaceId}' is not the identifier of a space.", nameof(spaceId));
        if (session.Kind is not (SessionViewKind.GlobalSession or SessionViewKind.ProjectSession))
            throw new InvalidOperationException("Only a chat or a project session belongs to a space.");
        var store = CreateSessionStore();
        var original = await store.ReadTransferSnapshotAsync(session.SessionId, requireState: false, providerFamily, cancellationToken).ConfigureAwait(false);
        // Replacement rewrites the existing first-line view header; never discard a history record from an
        // unsupported/headerless journal in order to manufacture one.
        if (await ReadHeaderAsync(session.SessionId, session.CreatedAt, cancellationToken).ConfigureAwait(false) is not { } header
            || header.SessionId != session.SessionId || header.Kind != session.Kind)
            throw new InvalidOperationException("The session requires a valid catalog header before it changes space.");
        var local = await ReadLatestStateAsync(session.SessionId, session.CreatedAt, cancellationToken).ConfigureAwait(false)
            ?? new SessionViewLocalState();
        ThrowIfWorkPending(original.History, local, "the session changes space");
        // The header keeps everything else it says: who created the session, when, where and on which provider.
        var moved = header.ToDescriptor();
        moved.SpaceId = spaceId;
        if (string.IsNullOrWhiteSpace(header.Title)) moved.Title = string.Empty;
        await store.CommitProviderSelectionAsync(original, original.Summary, original.State, CreateHeaderEvent(moved).ToJson(),
            [CreateStateEvent(moved, local).ToJson()], cancellationToken).ConfigureAwait(false);
        InvalidateLatestStateCache(GetPath(session.SessionId, session.CreatedAt));
    }
}
