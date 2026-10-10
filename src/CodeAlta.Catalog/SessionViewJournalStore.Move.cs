using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Catalog;

public sealed partial class SessionViewJournalStore
{
    /// <summary>Atomically moves an idle project session to another project in its existing journal.</summary>
    /// <remarks>The first-line header, the summary and the local state change together: the header is what the
    /// lists check the scope of a session against, and the summary is what its project is derived from, so the
    /// session is never seen half moved. The flushed same-directory journal replacement is the commit point; before
    /// it failures preserve the original. History and the provider continuation are kept. Derived caches are
    /// refreshed by their changed file stamp. The caller must hold idle-session mutation admission.</remarks>
    /// <param name="session">The session as the runtime knows it.</param>
    /// <param name="projectRef">The identifier of the target project.</param>
    /// <param name="workingDirectory">The folder of the target project.</param>
    /// <param name="worktreeDirectory">The git worktree the session keeps working in, or <see langword="null"/>.</param>
    /// <param name="title">The new title, or <see langword="null"/> to keep the saved one.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentNullException">The session is null.</exception>
    /// <exception cref="ArgumentException">The project or folder is empty.</exception>
    /// <exception cref="InvalidOperationException">The session is not a project session, changed, contains queued
    /// work, or requires recovery.</exception>
    /// <exception cref="IOException">Preparing or replacing the journal failed.</exception>
    /// <exception cref="OperationCanceledException">Canceled before the commit point.</exception>
    public async Task MoveSessionAsync(SessionViewDescriptor session, string projectRef, string workingDirectory,
        string? worktreeDirectory, string? title, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        if (session.Kind != SessionViewKind.ProjectSession)
            throw new InvalidOperationException("Only a project session can be moved to another project.");
        var store = CreateSessionStore();
        var original = await store.ReadTransferSnapshotAsync(session.SessionId, cancellationToken).ConfigureAwait(false);
        // Replacement rewrites the existing first-line view header; never discard a history record from an
        // unsupported/headerless journal in order to manufacture one.
        if (await ReadHeaderAsync(session.SessionId, session.CreatedAt, cancellationToken).ConfigureAwait(false) is not { } header
            || header.SessionId != session.SessionId || header.Kind != SessionViewKind.ProjectSession)
            throw new InvalidOperationException("The session requires a valid catalog header before it is moved.");
        if (original.History.OfType<AgentActivityEvent>().Where(activity => activity.Kind == AgentActivityKind.Turn)
            .GroupBy(activity => (activity.ProviderId, activity.RunId, activity.ActivityId))
            .Any(turn => turn.Last().Phase is not (AgentActivityPhase.Completed or AgentActivityPhase.Failed or AgentActivityPhase.Canceled)))
            throw new InvalidOperationException("An unfinished persisted turn must be resolved before the session is moved.");
        var local = await ReadLatestStateAsync(session.SessionId, session.CreatedAt, cancellationToken).ConfigureAwait(false)
            ?? new SessionViewLocalState();
        if (local.QueuedPrompts.Any(prompt => prompt.State != "submitted"))
            throw new InvalidOperationException("Queued work must be resolved before the session is moved.");
        var now = DateTimeOffset.UtcNow;
        var worktree = string.IsNullOrWhiteSpace(worktreeDirectory) ? null : worktreeDirectory.Trim();
        var newTitle = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        var summary = original.Summary with { WorkingDirectory = workingDirectory, WorktreeDirectory = worktree,
            Title = newTitle ?? original.Summary.Title, UpdatedAt = now };
        var state = original.State with { UpdatedAt = now };
        // The header keeps who created the session, when, and on which provider: only where it belongs changes.
        var moved = header.ToDescriptor();
        moved.ProjectRef = projectRef.Trim();
        moved.WorkingDirectory = workingDirectory;
        if (newTitle is not null) moved.Title = newTitle;
        else if (string.IsNullOrWhiteSpace(header.Title)) moved.Title = string.Empty;
        await store.CommitProviderSelectionAsync(original, summary, state, CreateHeaderEvent(moved).ToJson(),
            [CreateStateEvent(moved, local).ToJson()], cancellationToken).ConfigureAwait(false);
        InvalidateLatestStateCache(GetPath(session.SessionId, session.CreatedAt));
    }
}
