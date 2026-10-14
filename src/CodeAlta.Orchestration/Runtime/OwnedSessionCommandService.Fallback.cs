using CodeAlta.Agent;

namespace CodeAlta.Orchestration.Runtime;

public sealed partial class OwnedSessionCommandService
{
    /// <summary>Gets the last fallback of each session that made one since the start of the host.</summary>
    public IReadOnlyList<SessionFallbackRecord> LastFallbacks => Fallback.LastFallbacks;

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
