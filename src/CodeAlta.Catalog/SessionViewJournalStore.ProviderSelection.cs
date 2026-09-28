using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Catalog;

public sealed partial class SessionViewJournalStore
{
    /// <summary>Atomically saves an idle provider selection, cleared continuation, and local state in the existing journal.</summary>
    /// <remarks>No provider is executed. The flushed same-directory journal replacement is the commit point;
    /// before it failures preserve the original. Derived caches are refreshed by their changed file stamp.
    /// Existing history is retained. The target is attached lazily on the next send.
    /// The caller must hold idle-session mutation admission.</remarks>
    /// <exception cref="ArgumentNullException">The session or target is null.</exception>
    /// <exception cref="InvalidOperationException">The session changed, contains queued work, or requires recovery.</exception>
    /// <exception cref="IOException">Preparing or replacing the journal failed.</exception>
    /// <exception cref="OperationCanceledException">Canceled before the commit point.</exception>
    public async Task SelectProviderAsync(SessionViewDescriptor session, string expectedProvider, ModelProviderDescriptor target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(target);
        var store = CreateSessionStore();
        var original = await store.ReadTransferSnapshotAsync(session.SessionId, cancellationToken).ConfigureAwait(false);
        if (original.Summary.ProviderKey != expectedProvider) throw new InvalidOperationException("The provider selection changed.");
        // Replacement updates the existing first-line view header; never discard a history
        // record from an unsupported/headerless journal in order to manufacture one.
        if (await ReadHeaderAsync(session.SessionId, session.CreatedAt, cancellationToken).ConfigureAwait(false) is not { } header
            || header.SessionId != session.SessionId)
            throw new InvalidOperationException("The session requires a valid catalog header before provider selection.");
        if (original.History.OfType<AgentActivityEvent>().Where(activity => activity.Kind == AgentActivityKind.Turn)
            .GroupBy(activity => (activity.ProviderId, activity.RunId, activity.ActivityId))
            .Any(turn => turn.Last().Phase is not (AgentActivityPhase.Completed or AgentActivityPhase.Failed or AgentActivityPhase.Canceled)))
            throw new InvalidOperationException("An unfinished persisted turn must be resolved before provider selection.");
        var local = await ReadLatestStateAsync(session.SessionId, session.CreatedAt, cancellationToken).ConfigureAwait(false)
            ?? new SessionViewLocalState();
        if (local.QueuedPrompts.Any(prompt => prompt.State != "submitted"))
            throw new InvalidOperationException("Queued work must be resolved before switching providers.");
        var now = DateTimeOffset.UtcNow;
        var summary = original.Summary with { ProviderId = target.ProviderId, ProviderKey = target.ProviderId.Value,
            ProtocolFamily = target.ProviderType, ModelId = null, ReasoningEffort = null, UpdatedAt = now };
        var state = original.State with { ProviderKey = target.ProviderId.Value, ProtocolFamily = target.ProviderType,
            ProviderSessionId = null, ProviderState = null, UpdatedAt = now };
        local.ProviderKey = target.ProviderId.Value;
        local.ModelId = null;
        local.ReasoningEffort = null;
        var selected = SessionViewJournalHeader.FromDescriptor(session).ToDescriptor();
        selected.ProviderId = target.ProviderId.Value;
        selected.ProviderKey = target.ProviderId.Value;
        await store.CommitProviderSelectionAsync(original, summary, state, CreateHeaderEvent(selected).ToJson(),
            [CreateStateEvent(selected, local).ToJson()], cancellationToken).ConfigureAwait(false);
        InvalidateLatestStateCache(GetPath(session.SessionId, session.CreatedAt));
    }
}
