namespace CodeAlta.Orchestration.Runtime;

/// <summary>The outcome of choosing the permission mode of an owned session.</summary>
/// <param name="Status">
/// <c>ok</c>; <c>invalid_mode</c> when the provider of the session does not offer the mode; <c>unavailable</c> when the
/// session is unknown, has no choices, or its attachment is not owned; <c>busy</c> while its provider is being changed
/// or it is being deleted; <c>closed</c> when the owner is closing.
/// </param>
/// <param name="PermissionMode">The mode of the session after the request when <paramref name="Status"/> is <c>ok</c>; null for the one of its provider.</param>
public sealed record OwnedPermissionModeResult(string Status, string? PermissionMode);

/// <summary>The permission modes a new session of a provider can be given, as the composer of a session offers them.</summary>
/// <param name="Modes">The modes; empty when the provider offers none.</param>
/// <param name="DefaultMode">
/// The mode a session without one runs in: the one its provider is configured with, else the one of the host; null
/// where the provider decides by itself.
/// </param>
public sealed record OwnedPermissionModeChoices(IReadOnlyList<string> Modes, string? DefaultMode);

public sealed partial class OwnedSessionCommandService
{
    /// <summary>
    /// Reads the permission modes a session of a provider can be created with, and the one it runs in when it is given
    /// none: the same as the choices of an existing session of that provider (<see cref="OwnedSelectionChoices.PermissionModes"/>).
    /// Nothing is started.
    /// </summary>
    /// <param name="providerKey">The provider.</param>
    /// <returns>The modes and the default one.</returns>
    /// <exception cref="ArgumentException">The provider key is empty.</exception>
    public OwnedPermissionModeChoices GetNewSessionPermissionModes(string providerKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);
        var (modes, defaultMode) = PermissionModesOf(providerKey);
        return new([.. modes.Take(16)], defaultMode);
    }

    // A session is created only in a mode its provider offers.
    private bool OffersNewSessionPermissionMode(string providerKey, string permissionMode)
        => GetNewSessionPermissionModes(providerKey).Modes.Contains(permissionMode, StringComparer.Ordinal);

    /// <summary>
    /// Chooses the permission mode of an owned session, at once: it is saved with the session, a run in progress
    /// follows it from now on, and the requests that wait for the user and that it grants are allowed
    /// (<see cref="SessionRuntimeService.SetOwnedPermissionModeAsync"/>). A send that chooses a mode does the same.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="permissionMode">
    /// One of the modes its provider offers (<see cref="OwnedSelectionChoices.PermissionModes"/>), or
    /// <see cref="OwnedSessionSelection.ProviderPermissionMode"/> for the one its provider is configured with.
    /// </param>
    /// <param name="cancellationToken">Cancels the request before the mode is saved.</param>
    /// <returns>The outcome and, when it is <c>ok</c>, the mode of the session.</returns>
    /// <exception cref="ArgumentException">The session identity or the mode is empty.</exception>
    /// <exception cref="OperationCanceledException">The request was cancelled.</exception>
    public async Task<OwnedPermissionModeResult> SetPermissionModeAsync(string sessionId, string permissionMode, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(permissionMode);
        lock (_gate)
        {
            if (_closed || _retained) return new("closed", null);
            // A provider that is being changed, or a session that is being deleted, keeps its mode meanwhile.
            if (Reserved(sessionId)) return new("busy", null);
        }

        try
        {
            var session = await _runtime.ResolveOwnedSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (session is null || !string.Equals(session.SessionId, sessionId, StringComparison.OrdinalIgnoreCase)) return new("unavailable", null);
            var choices = await GetSelectionChoicesAsync(session.SessionId, cancellationToken).ConfigureAwait(false);
            if (choices is null) return new("unavailable", null);
            // Only the mode is checked: the rest of the selection is chosen with the next send.
            if (choices.PermissionModes.Count == 0 || permissionMode != OwnedSessionSelection.ProviderPermissionMode
                && !choices.PermissionModes.Contains(permissionMode, StringComparer.Ordinal)) return new("invalid_mode", null);
            var chosen = permissionMode == OwnedSessionSelection.ProviderPermissionMode ? null : permissionMode;
            cancellationToken.ThrowIfCancellationRequested();
            await _runtime.SetOwnedPermissionModeAsync(session, chosen).ConfigureAwait(false);
            return new("ok", chosen);
        }
        catch (ObjectDisposedException) { return new("closed", null); }
        catch (InvalidOperationException) { return new("unavailable", null); }
    }
}
