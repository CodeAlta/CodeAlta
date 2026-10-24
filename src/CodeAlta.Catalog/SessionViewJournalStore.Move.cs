using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Catalog;

/// <summary>Where a project session goes when it is moved to another project.</summary>
/// <param name="ProjectRef">The identifier of the target project.</param>
/// <param name="WorkingDirectory">The folder of the target project.</param>
/// <param name="WorktreeDirectory">The git worktree the session keeps working in, or <see langword="null"/>.</param>
/// <param name="Title">The new saved title, or <see langword="null"/> to keep the saved one.</param>
/// <param name="Summary">The new summary, or <see langword="null"/> to keep the saved one.</param>
public sealed record SessionViewMove(string ProjectRef, string WorkingDirectory, string? WorktreeDirectory, string? Title, string? Summary)
{
    /// <summary>
    /// Gets the protocol family of the runtime of the session's provider, or <see langword="null"/> when it is not
    /// known. A summary that names the provider key as family, as CodeAlta 1.8 wrote it, is moved with this family.
    /// </summary>
    public string? ProviderFamily { get; init; }
}

public sealed partial class SessionViewJournalStore
{
    /// <summary>Atomically moves an idle project session to another project in its existing journal.</summary>
    /// <remarks>The first-line header, the summary and the local state change together: the header is what the
    /// lists check the scope of a session against, and the summary is what its project is derived from, so the
    /// session is never seen half moved. The flushed same-directory journal replacement is the commit point; before
    /// it failures preserve the original. History and the provider continuation are kept. Derived caches are
    /// refreshed by their changed file stamp. The caller must hold idle-session mutation admission.</remarks>
    /// <param name="session">The session as the runtime knows it.</param>
    /// <param name="move">Where the session goes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentNullException">The session or the move is null.</exception>
    /// <exception cref="ArgumentException">The project or folder is empty.</exception>
    /// <exception cref="SessionViewChangeRefusedException">The session has a turn that did not end or a pending queued prompt.</exception>
    /// <exception cref="InvalidOperationException">The session is not a project session, changed, or requires recovery.</exception>
    /// <exception cref="IOException">Preparing or replacing the journal failed.</exception>
    /// <exception cref="OperationCanceledException">Canceled before the commit point.</exception>
    public async Task MoveSessionAsync(SessionViewDescriptor session, SessionViewMove move, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(move);
        ArgumentException.ThrowIfNullOrWhiteSpace(move.ProjectRef, nameof(move));
        ArgumentException.ThrowIfNullOrWhiteSpace(move.WorkingDirectory, nameof(move));
        if (session.Kind != SessionViewKind.ProjectSession)
            throw new InvalidOperationException("Only a project session can be moved to another project.");
        var store = CreateSessionStore();
        var original = await store.ReadTransferSnapshotAsync(session.SessionId, requireState: false, move.ProviderFamily, cancellationToken).ConfigureAwait(false);
        // Replacement rewrites the existing first-line view header; never discard a history record from an
        // unsupported/headerless journal in order to manufacture one.
        if (await ReadHeaderAsync(session.SessionId, session.CreatedAt, cancellationToken).ConfigureAwait(false) is not { } header
            || header.SessionId != session.SessionId || header.Kind != SessionViewKind.ProjectSession)
            throw new InvalidOperationException("The session requires a valid catalog header before it is moved.");
        var local = await ReadLatestStateAsync(session.SessionId, session.CreatedAt, cancellationToken).ConfigureAwait(false)
            ?? new SessionViewLocalState();
        ThrowIfWorkPending(original.History, local, "the session is moved");
        var now = DateTimeOffset.UtcNow;
        var worktree = string.IsNullOrWhiteSpace(move.WorktreeDirectory) ? null : move.WorktreeDirectory.Trim();
        var newTitle = string.IsNullOrWhiteSpace(move.Title) ? null : move.Title.Trim();
        var summary = original.Summary with { WorkingDirectory = move.WorkingDirectory, WorktreeDirectory = worktree,
            Title = newTitle ?? original.Summary.Title, Summary = move.Summary ?? original.Summary.Summary, UpdatedAt = now };
        var state = original.State with { UpdatedAt = now };
        // The header keeps who created the session, when, and on which provider: only where it belongs changes.
        var moved = header.ToDescriptor();
        moved.ProjectRef = move.ProjectRef.Trim();
        moved.WorkingDirectory = move.WorkingDirectory;
        if (newTitle is not null) moved.Title = newTitle;
        else if (string.IsNullOrWhiteSpace(header.Title)) moved.Title = string.Empty;
        await store.CommitProviderSelectionAsync(original, summary, state, CreateHeaderEvent(moved).ToJson(),
            [CreateStateEvent(moved, local).ToJson()], cancellationToken).ConfigureAwait(false);
    }
}
