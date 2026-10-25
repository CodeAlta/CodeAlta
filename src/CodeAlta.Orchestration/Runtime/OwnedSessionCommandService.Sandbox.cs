using CodeAlta.Sandbox;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>The outcome of choosing the sandbox of an owned session.</summary>
/// <param name="Status">
/// <c>ok</c>; <c>unknown_profile</c> when no profile has the name; <c>unavailable</c> when the session is unknown or
/// its attachment is not owned; <c>busy</c> while its provider is being changed or it is being deleted;
/// <c>session_in_use</c> when another process holds it; <c>closed</c> when the owner is closing.
/// </param>
/// <param name="Sandbox">The choice of the session after the request when <paramref name="Status"/> is <c>ok</c>; null when its scopes decide.</param>
public sealed record OwnedSandboxResult(string Status, string? Sandbox);

public sealed partial class OwnedSessionCommandService
{
    /// <summary>The value of <see cref="SetSandboxAsync"/> that gives the session back to its scopes.</summary>
    public const string ScopeSandbox = "scope";

    /// <summary>
    /// Chooses the sandbox of an owned session: a profile, <see cref="SandboxProfiles.Off"/>, or
    /// <see cref="ScopeSandbox"/> to follow its model, provider, project, space and the setting of every session. It is
    /// saved with the session and applies from its next command.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="sandbox">The choice.</param>
    /// <param name="cancellationToken">Cancels the request before the choice is saved.</param>
    /// <returns>The outcome and, when it is <c>ok</c>, the choice of the session.</returns>
    /// <exception cref="ArgumentException">The session identity or the choice is empty.</exception>
    /// <exception cref="OperationCanceledException">The request was cancelled.</exception>
    public async Task<OwnedSandboxResult> SetSandboxAsync(string sessionId, string sandbox, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sandbox);
        lock (_gate)
        {
            if (_closed || _retained) return new("closed", null);
            if (Reserved(sessionId)) return new("busy", null);
        }

        var value = sandbox.Trim();
        string? chosen = value.Equals(ScopeSandbox, StringComparison.OrdinalIgnoreCase) ? null
            : value.Equals(SandboxProfiles.Off, StringComparison.OrdinalIgnoreCase) ? SandboxProfiles.Off
            : value;
        if (chosen is not null and not SandboxProfiles.Off
            && !SandboxProfiles.Names(_runtime.ReadSandboxSettings().Profiles).Contains(chosen, StringComparer.OrdinalIgnoreCase))
            return new("unknown_profile", null);
        try
        {
            var session = await _runtime.ResolveOwnedSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (session is null || !string.Equals(session.SessionId, sessionId, StringComparison.OrdinalIgnoreCase)) return new("unavailable", null);
            cancellationToken.ThrowIfCancellationRequested();
            await _runtime.SetOwnedSandboxAsync(session, chosen).ConfigureAwait(false);
            return new("ok", chosen);
        }
        catch (ObjectDisposedException) { return new("closed", null); }
        catch (SessionInUseException) { return new("session_in_use", null); }
        catch (InvalidOperationException) { return new("unavailable", null); }
    }
}
