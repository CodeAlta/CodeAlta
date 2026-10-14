using CodeAlta.Agent;

namespace CodeAlta.Orchestration.Runtime;

public sealed partial class OwnedSessionCommandService
{
    /// <summary>Gets the last fallback of each session that made one since the start of the host.</summary>
    public IReadOnlyList<SessionFallbackRecord> LastFallbacks => Fallback.LastFallbacks;

    /// <summary>
    /// Records the tier of models a session runs on, or that its model was chosen by hand, as the user chose it with
    /// the model of its next prompt.
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
        try { return await _runtime.SetSessionTierAsync(sessionId, tier, cancellationToken).ConfigureAwait(false) ? "ok" : "unavailable"; }
        catch (ObjectDisposedException) { return "closed"; }
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
