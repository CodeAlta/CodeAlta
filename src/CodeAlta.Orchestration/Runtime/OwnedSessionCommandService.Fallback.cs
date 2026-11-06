using CodeAlta.Agent;

namespace CodeAlta.Orchestration.Runtime;

public sealed partial class OwnedSessionCommandService
{
    /// <summary>Gets the last fallback of each session that made one since the start of the host.</summary>
    public IReadOnlyList<SessionFallbackRecord> LastFallbacks => Fallback.LastFallbacks;

    /// <summary>Gets the resumes after a limit that the sessions wait for.</summary>
    public IReadOnlyList<SessionResumeSnapshot> PendingResumes => Fallback.PendingResumes;

    /// <summary>Gets the resume after a limit a session waits for.</summary>
    /// <param name="sessionId">The session.</param>
    /// <returns>The resume, or <see langword="null"/> when the session waits for none.</returns>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is blank.</exception>
    public SessionResumeSnapshot? GetPendingResume(string sessionId) => Fallback.GetPendingResume(sessionId);

    /// <summary>
    /// Cancels the resume after a limit a session waits for: its parent is told of the failure that stopped it, and its
    /// queue drains.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="resumeId">The resume, as its card names it.</param>
    /// <returns><c>ok</c>, <c>unknown</c> when that resume does not wait, or <c>closed</c>.</returns>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> or <paramref name="resumeId"/> is blank.</exception>
    public async Task<string> CancelResumeAsync(string sessionId, string resumeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(resumeId);
        lock (_gate) { if (_closed || _retained) return "closed"; }
        return await Fallback.CancelResumeAsync(sessionId, resumeId).ConfigureAwait(false) ? "ok" : "unknown";
    }

    /// <summary>
    /// Resumes at once a session that waits for a resume after a limit, through its tier in order. The resume runs on its
    /// own: its cards and its run tell how it goes.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="resumeId">The resume, as its card names it.</param>
    /// <returns><c>ok</c>, <c>unknown</c> when that resume does not wait, or <c>closed</c>.</returns>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> or <paramref name="resumeId"/> is blank.</exception>
    public string ResumeNow(string sessionId, string resumeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(resumeId);
        lock (_gate) { if (_closed || _retained) return "closed"; }
        return Fallback.ResumeNow(sessionId, resumeId) ? "ok" : "unknown";
    }

    /// <summary>
    /// Records the tier of models a session runs on, or that its model was chosen by hand, as the user chose it with
    /// the model of its next prompt. A session on Adaptive leaves it: the user pinned its model.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="tier">The tier, or null for a model chosen by hand.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns><c>ok</c>, <c>unavailable</c> for a session that is not known, or <c>closed</c>.</returns>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is blank.</exception>
    public async Task<string> SetSessionTierAsync(string sessionId, Catalog.ModelTiers.ModelTier? tier, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        lock (_gate) { if (_closed || _retained) return "closed"; }
        try { return await _runtime.SetSessionModelChoiceAsync(sessionId, tier, adaptive: false, cancellationToken).ConfigureAwait(false) ? "ok" : "unavailable"; }
        catch (ObjectDisposedException) { return "closed"; }
    }

    /// <summary>
    /// Puts a session on Adaptive at a level: CodeAlta then runs each of its turns on the model of that level, and has
    /// the critics of the level review each change of code (<c>[models.adaptive]</c>). The model of its next prompt is the
    /// one of the level, which the caller chooses as it chooses the model of a tier.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="level">The level; <see langword="null"/> for the one the session chose before, else the default level.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns>
    /// The status (<c>ok</c>; <c>not_ready</c> when Adaptive is not set up or has no level; <c>unavailable</c> for a
    /// session that is not known; <c>closed</c>) and, with <c>ok</c>, the level it runs on: the one asked, or the nearest
    /// level that is set.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is blank.</exception>
    public async Task<(string Status, Catalog.ModelTiers.AdaptiveLevelSettings? Level)> SetSessionAdaptiveAsync(string sessionId,
        Catalog.ModelTiers.AdaptiveLevel? level = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        lock (_gate) { if (_closed || _retained) return ("closed", null); }
        try
        {
            var session = await _runtime.ResolveOwnedSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (session is null) return ("unavailable", null);
            var scope = await _runtime.LoadModelTierScopeAsync(session.SpaceId, session.ProjectRef, cancellationToken).ConfigureAwait(false);
            if (!scope.IsAdaptiveReady) return ("not_ready", null);
            var asked = level ?? await _runtime.GetSessionAdaptiveLevelAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (scope.Adaptive!.Resolve(asked) is not { } chosen) return ("not_ready", null);
            // A session on Adaptive has no tier: its level names its models, and a fallback follows the tier its model is in.
            return await _runtime.SetSessionModelChoiceAsync(sessionId, tier: null, adaptive: true, chosen.Level, cancellationToken).ConfigureAwait(false)
                ? ("ok", chosen) : ("unavailable", null);
        }
        catch (ObjectDisposedException) { return ("closed", null); }
    }

    // Whether a command of the session still holds it, as the send of a run that just failed does until it is cleaned up.
    internal bool IsSessionHeldForFallback(string sessionId)
    {
        lock (_gate) return !_closed && !_retained && (IsSessionHeld(sessionId) || Asks.List(sessionId).Head is not null);
    }

    // The modes a session of a provider can be given, and the one it runs in when it has none, as the composer offers them.
    internal (IReadOnlyList<string> Modes, string? Default) PermissionModesOf(string providerKey)
    {
        var configured = SelectionProvider?.Invoke(new ModelProviderId(providerKey));
        IReadOnlyList<string> modes = configured?.PermissionModes.Where(static mode => !ProviderOnlyPermissionModes.Contains(mode)).ToArray() ?? [];
        if (modes.Count == 0 && _runtime.SessionPermissionModes) modes = SessionPermissionModes.HostModes;
        var defaultMode = _runtime.SessionPermissionModes ? _runtime.GetDefaultPermissionMode(providerKey) : configured?.DefaultPermissionMode;
        return (modes, defaultMode);
    }
}
