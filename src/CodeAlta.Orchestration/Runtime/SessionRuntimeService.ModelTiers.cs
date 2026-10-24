using CodeAlta.Catalog;
using CodeAlta.Catalog.ModelTiers;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>A run of an owned session that failed on its provider, as a fallback reads it.</summary>
/// <param name="SessionId">The session.</param>
/// <param name="ParentSessionId">The session that delegated it, or <see langword="null"/>.</param>
/// <param name="ProviderKey">The provider the run failed on.</param>
/// <param name="ModelId">The model of the run, when the session names one.</param>
/// <param name="ReasoningEffort">The reasoning effort of the run, when the session names one.</param>
/// <param name="PermissionMode">The permission mode of the session, or <see langword="null"/> for the one of its provider.</param>
/// <param name="AgentPromptId">The agent prompt of the session.</param>
/// <param name="Tier">The tier the session runs on, or <see langword="null"/> for a model chosen by hand.</param>
/// <param name="Error">The error that ended the run.</param>
/// <param name="HasQueuedPrompts">Whether prompts wait in the queue of the session.</param>
/// <param name="Scope">The tiers of the space of the session, and the providers available there.</param>
internal sealed record RunFailureNotice(
    string SessionId,
    string? ParentSessionId,
    string ProviderKey,
    string? ModelId,
    CodeAlta.Agent.AgentReasoningEffort? ReasoningEffort,
    string? PermissionMode,
    string? AgentPromptId,
    ModelTier? Tier,
    CodeAlta.Agent.AgentErrorEvent Error,
    bool HasQueuedPrompts,
    ModelTierScope Scope);

public sealed partial class SessionRuntimeService
{
    /// <summary>
    /// Decides on the actor of a session whether a run that failed on its provider goes on with another model. It
    /// returns what starts the fallback, which then owns what was held back of the failure, or <see langword="null"/>
    /// to let the failure through. It must not wait.
    /// </summary>
    internal Func<RunFailureNotice, Action<HeldRunFailure>?>? RunFailureFallback { get; set; }

    /// <summary>
    /// Reads the model tiers and the fallback of the settings. A file that cannot be read, or whose tiers are not
    /// valid, gives no tier and no fallback: the settings say what is wrong when they are opened.
    /// </summary>
    /// <returns>The settings.</returns>
    public ModelSettings LoadModelSettings()
    {
        try { return _configStore.LoadGlobalModelSettings(); }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return ModelSettings.Default;
        }
    }

    /// <summary>
    /// Reads the tiers of models and the fallback of a space: its own when its file sets tiers, the global ones
    /// otherwise, with the providers available there.
    /// </summary>
    /// <param name="spaceId">The space the session records, or <see langword="null"/>.</param>
    /// <param name="projectId">The project of the session, or <see langword="null"/> for a chat.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The tiers of the space.</returns>
    public async Task<ModelTierScope> LoadModelTierScopeAsync(string? spaceId, string? projectId, CancellationToken cancellationToken = default)
    {
        // The space and the providers it has are those the sends and the provider switches of the session check.
        var (id, space) = await ResolveSpaceAsync(spaceId, projectId, cancellationToken).ConfigureAwait(false);
        var scope = ModelTierScope.Of(LoadModelSettings(), space, LoadSpaceAssignments());
        return scope with { SpaceId = id };
    }

    // Runs on the actor of the session, with the error of a run.
    private async ValueTask<Action<HeldRunFailure>?> TryClaimRunFailureAsync(string sessionId, RuntimeSessionEntry entry, CodeAlta.Agent.AgentErrorEvent error)
    {
        if (RunFailureFallback is not { } fallback || entry.IsTerminated || error.RunId is null
            || error.Failure is not { Kind: CodeAlta.Agent.AgentFailureKind.QuotaExhausted or CodeAlta.Agent.AgentFailureKind.RateLimited or CodeAlta.Agent.AgentFailureKind.Unavailable or CodeAlta.Agent.AgentFailureKind.ModelUnavailable })
            return null;
        try
        {
            var local = await ReadLatestLocalStateAsync(sessionId, entry.CreatedAt, CancellationToken.None).ConfigureAwait(false);
            var scope = await LoadModelTierScopeAsync(entry.SpaceId, entry.ProjectId, CancellationToken.None).ConfigureAwait(false);
            return fallback(new RunFailureNotice(
                sessionId,
                entry.ParentSessionId,
                entry.ProviderKey,
                entry.Model ?? local?.ModelId,
                entry.ReasoningEffort ?? local?.ReasoningEffort,
                entry.PermissionMode,
                entry.PendingAgentPromptId ?? entry.AgentPromptId,
                ModelTiers.Parse(local?.Tier),
                error,
                local?.HasPendingQueuedPrompts() ?? false,
                scope));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            // What cannot be read is a failure that goes through as it is.
            return null;
        }
    }

    /// <summary>
    /// What a fallback held back of a failed run: the notification of its parent and the drain of its queue. Either
    /// the session went on with another model (<see cref="Replace"/>), or they go out as they would have
    /// (<see cref="ReleaseAsync"/>).
    /// </summary>
    internal sealed class HeldRunFailure
    {
        private readonly SessionRuntimeService _runtime;
        private readonly IReadOnlyList<ParentNotificationWork> _notifications;
        private int _settled;

        internal HeldRunFailure(SessionRuntimeService runtime, string sessionId, IReadOnlyList<ParentNotificationWork> notifications)
        {
            _runtime = runtime;
            SessionId = sessionId;
            _notifications = notifications;
        }

        /// <summary>Gets the session.</summary>
        public string SessionId { get; }

        /// <summary>Gets whether the failure has a parent to tell.</summary>
        public bool NotifiesParent => _notifications.Count > 0;

        /// <summary>The session went on: its parent is not told of the failure that was replaced.</summary>
        public void Replace() => Interlocked.Exchange(ref _settled, 1);

        /// <summary>The fallback did not happen: the parent is told of the failure and the queue drains.</summary>
        public async Task ReleaseAsync()
        {
            if (Interlocked.Exchange(ref _settled, 1) != 0) return;
            foreach (var notification in _notifications)
            {
                await _runtime.DeliverParentNotificationAsync(notification).ConfigureAwait(false);
            }

            await _runtime.TryDrainNextQueuedPromptAsync(SessionId).ConfigureAwait(false);
        }
    }

    /// <summary>Reads the tier of models a session runs on, as it was last chosen.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns>The tier, or <see langword="null"/> for a session whose model was chosen by hand or that is not known.</returns>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is blank.</exception>
    /// <exception cref="ObjectDisposedException">The runtime is closing.</exception>
    public async Task<ModelTier?> GetSessionTierAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return await AdmitAsync(
                () => GetActorForWork(sessionId).QueryAsync(token => ReadSessionTierAsync(sessionId, token), CancellationToken.None).AsTask(),
                cancellationToken)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Records the tier of models a session runs on, or that its model was chosen by hand. It changes what the
    /// session falls back on, not the model it runs: the model is chosen with the selection of its next prompt.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="tier">The tier, or <see langword="null"/> for a model chosen by hand.</param>
    /// <param name="cancellationToken">Stops waiting; what was started is completed.</param>
    /// <returns>Whether the session is known.</returns>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is blank.</exception>
    /// <exception cref="ObjectDisposedException">The runtime is closing.</exception>
    public async Task<bool> SetSessionTierAsync(string sessionId, ModelTier? tier, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return await AdmitAsync(async () =>
        {
            while (true)
            {
                ObjectDisposedException.ThrowIf(_forwarding.IsClosed, this);
                var (transition, known) = await GetActorForWork(sessionId).QueryAsync(async token =>
                {
                    // An attachment that is being made reads what is saved: it is waited for first.
                    if (_transitions.TryGetValue(sessionId, out var pending)) return ((Task?)pending, false);
                    return (null, await WriteSessionTierAsync(sessionId, tier, token).ConfigureAwait(false));
                }, CancellationToken.None).ConfigureAwait(false);
                if (transition is null) return known;
                // Its failure belongs to the one that attaches.
                await transition.ContinueWith(static _ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).ConfigureAwait(false);
            }
        }, cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    // Runs on the actor of the session.
    private async ValueTask<ModelTier?> ReadSessionTierAsync(string sessionId, CancellationToken token)
    {
        var session = await ResolveOwnedSessionBodyAsync(sessionId, token).ConfigureAwait(false);
        if (session is null) return null;
        return ModelTiers.Parse((await ReadLatestLocalStateAsync(sessionId, session.CreatedAt, token).ConfigureAwait(false))?.Tier);
    }

    // Runs on the actor of the session.
    private async ValueTask<bool> WriteSessionTierAsync(string sessionId, ModelTier? tier, CancellationToken token)
    {
        var session = await ResolveOwnedSessionBodyAsync(sessionId, token).ConfigureAwait(false);
        if (session is null) return false;
        var name = tier is { } chosen ? ModelTiers.NameOf(chosen) : null;
        var localState = await ReadLatestLocalStateAsync(sessionId, session.CreatedAt, token).ConfigureAwait(false);
        if (localState is not null && string.Equals(localState.Tier, name, StringComparison.Ordinal)) return true;
        if (localState is null)
        {
            localState = new SessionViewLocalState();
            CopySessionMetadata(session, localState);
        }

        localState.Tier = name;
        await _sessionViewCatalog.JournalStore.AppendStateAsync(session, localState, token).ConfigureAwait(false);
        return true;
    }
}
