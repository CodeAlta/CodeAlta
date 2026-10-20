using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Catalog;

public sealed partial class SessionViewJournalStore
{
    /// <summary>Gets the folder of the attachments of a session whose journal is not written yet.</summary>
    /// <param name="session">The new session, with its identifier and its creation time.</param>
    /// <returns>The folder; it may not exist.</returns>
    /// <exception cref="ArgumentException">The session has no identifier.</exception>
    internal string GetNewSessionAttachmentDirectory(SessionViewDescriptor session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return CreateSessionStore().GetNewSessionAttachmentDirectory(session.SessionId, session.CreatedAt);
    }

    /// <summary>
    /// Writes the journal of a new session that starts with the turns of a conversation imported from another program,
    /// with the header and the local state of the session, and lists it.
    /// </summary>
    /// <param name="target">The new session, with its identifier, its creation time, its project and its folder.</param>
    /// <param name="summary">Its summary.</param>
    /// <param name="state">Its state after the turns.</param>
    /// <param name="events">The records of the turns, in order.</param>
    /// <param name="local">Its local state: the provider, the model and the modes it continues with.</param>
    /// <param name="cancellationToken">Cancels before the session exists.</param>
    /// <exception cref="ArgumentException">The session has no identifier or no creation time.</exception>
    /// <exception cref="InvalidOperationException">The session exists.</exception>
    /// <exception cref="IOException">Writing fails.</exception>
    /// <exception cref="OperationCanceledException">Canceled before the journal exists.</exception>
    internal async Task ImportAsync(SessionViewDescriptor target, AgentSessionSummary summary, AgentSessionState state,
        IReadOnlyList<AgentEvent> events, SessionViewLocalState local, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(local);
        if (string.IsNullOrWhiteSpace(target.SessionId) || target.CreatedAt == default
            || !string.Equals(target.SessionId, summary.SessionId, StringComparison.Ordinal))
            throw new ArgumentException("An imported session needs an identifier and a creation time of its own.", nameof(target));
        var store = CreateSessionStore();
        await store.CommitImportAsync(summary, state, CreateHeaderEvent(target).ToJson(), events, [CreateStateEvent(target, local).ToJson()],
            cancellationToken).ConfigureAwait(false);
        var path = GetPath(target.SessionId, target.CreatedAt);
        await _sessionCache.UpsertSessionViewHeaderAsync(target.SessionId, path, SessionViewJournalHeader.FromDescriptor(target), CancellationToken.None)
            .ConfigureAwait(false);
        await _sessionCache.UpsertSessionViewStateAsync(target.SessionId, path, local, CancellationToken.None).ConfigureAwait(false);
    }
}
