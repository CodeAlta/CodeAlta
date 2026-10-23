using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Catalog;

/// <summary>What a session takes on the provider it switches to, instead of the provider's configured defaults.</summary>
/// <param name="ModelId">The model, or <see langword="null"/> for the provider's own.</param>
/// <param name="ReasoningEffort">The reasoning effort, or <see langword="null"/> for the model's own.</param>
/// <param name="PermissionMode">The permission mode, or <see langword="null"/> for the provider's configured one.</param>
public sealed record SessionViewProviderCarryOver(string? ModelId, AgentReasoningEffort? ReasoningEffort, string? PermissionMode);

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
    public Task SelectProviderAsync(SessionViewDescriptor session, string expectedProvider, ModelProviderDescriptor target,
        CancellationToken cancellationToken = default)
        => SelectProviderAsync(session, expectedProvider, target, carryOver: null, cancellationToken);

    /// <summary>
    /// Atomically saves an idle provider selection with the model, the reasoning effort and the permission mode the
    /// session takes on the new provider, as a fallback chooses them: the session never runs in a mode wider than
    /// the one it had.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <param name="expectedProvider">The provider the session is expected to be on.</param>
    /// <param name="target">The provider the session goes to.</param>
    /// <param name="carryOver">What the session takes on the new provider; null for its configured defaults.</param>
    /// <param name="cancellationToken">Cancels before the commit point.</param>
    /// <returns>A task that completes once the selection is saved.</returns>
    /// <inheritdoc cref="SelectProviderAsync(SessionViewDescriptor, string, ModelProviderDescriptor, CancellationToken)" path="/exception"/>
    public Task SelectProviderAsync(SessionViewDescriptor session, string expectedProvider, ModelProviderDescriptor target,
        SessionViewProviderCarryOver? carryOver, CancellationToken cancellationToken = default)
        => SelectProviderAsync(session, expectedProvider, target, carryOver, providerFamily: null, cancellationToken);

    /// <summary>
    /// Atomically saves an idle provider selection, knowing the protocol family of the runtime of the provider the
    /// session is on: a summary that names that provider's key as family, as CodeAlta 1.8 wrote it, does not make
    /// the session need recovery.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <param name="expectedProvider">The provider the session is expected to be on.</param>
    /// <param name="target">The provider the session goes to.</param>
    /// <param name="carryOver">What the session takes on the new provider; null for its configured defaults.</param>
    /// <param name="providerFamily">The protocol family of the runtime of <paramref name="expectedProvider"/>; null when unknown.</param>
    /// <param name="cancellationToken">Cancels before the commit point.</param>
    /// <returns>A task that completes once the selection is saved.</returns>
    /// <inheritdoc cref="SelectProviderAsync(SessionViewDescriptor, string, ModelProviderDescriptor, CancellationToken)" path="/exception"/>
    public async Task SelectProviderAsync(SessionViewDescriptor session, string expectedProvider, ModelProviderDescriptor target,
        SessionViewProviderCarryOver? carryOver, string? providerFamily, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(target);
        var store = CreateSessionStore();
        // A session that never ran has no provider state: it switches with nothing to continue, as a move takes it.
        var original = await store.ReadTransferSnapshotAsync(session.SessionId, requireState: false, providerFamily, cancellationToken).ConfigureAwait(false);
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
            ProtocolFamily = target.ProviderType, ModelId = carryOver?.ModelId, ReasoningEffort = carryOver?.ReasoningEffort, UpdatedAt = now };
        var state = original.State with { ProviderKey = target.ProviderId.Value, ProtocolFamily = target.ProviderType,
            ProviderSessionId = null, ProviderState = null, UpdatedAt = now };
        local.ProviderKey = target.ProviderId.Value;
        local.ModelId = carryOver?.ModelId;
        local.ReasoningEffort = carryOver?.ReasoningEffort;
        // The modes are those of a provider: the session starts on the new one with its configured mode, unless the
        // switch names the mode it keeps.
        local.PermissionMode = carryOver?.PermissionMode;
        // The agent of the new provider is told of the review before commit again at its first prompt.
        local.ReviewNoted = null;
        var selected = SessionViewJournalHeader.FromDescriptor(session).ToDescriptor();
        selected.ProviderId = target.ProviderId.Value;
        selected.ProviderKey = target.ProviderId.Value;
        await store.CommitProviderSelectionAsync(original, summary, state, CreateHeaderEvent(selected).ToJson(),
            [CreateStateEvent(selected, local).ToJson()], cancellationToken).ConfigureAwait(false);
        InvalidateLatestStateCache(GetPath(session.SessionId, session.CreatedAt));
    }
}
