using CodeAlta.Sandbox;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>
/// What a session carries to the work it starts or drives on another machine (<c>doc/fleet/security-and-privacy.md</c>,
/// "Remote execution", rule 8): its permission mode and its sandbox are the floor of that work, and a sandbox that does
/// not allow the fleet keeps it from any. The <c>alta</c> commands that reach another machine, the <c>agent({ machine })</c>
/// of scripts and the moves read it here, in the host, outside the sandbox of the session.
/// </summary>
/// <param name="PermissionMode">The mode the session runs in: the widest one what it starts or drives may run in.</param>
/// <param name="Sandbox">The name of the profile of its sandbox; null when it is not sandboxed.</param>
/// <param name="Confinement">What that profile confines; null when the session is not sandboxed, or its profile cannot be used.</param>
/// <param name="Refusal">Why the session may start or drive nothing on another machine, for the agent; null when it may.</param>
public sealed record SessionFleetFloor(string PermissionMode, string? Sandbox, SandboxConfinement? Confinement, string? Refusal)
{
    /// <summary>Gets whether the session is sandboxed.</summary>
    public bool IsSandboxed => Sandbox is not null;

    /// <summary>
    /// Says why the confinement of a session of another machine, or of this one, is weaker than this floor: no sandbox
    /// where this floor has one, or a profile that lets it do more (<see cref="SandboxConfinement.WeakerThan"/>).
    /// </summary>
    /// <param name="sandbox">The confinement of that session; null when it is not sandboxed.</param>
    /// <returns>Why it is weaker, for a person; null when it is at least as confined.</returns>
    public string? WeakerSandbox(SandboxConfinement? sandbox)
    {
        if (Confinement is not { } floor) return Sandbox is null ? null : $"the sandbox '{Sandbox}' cannot be used here";
        if (sandbox is null) return "it runs without a sandbox";
        return sandbox.WeakerThan(floor) is { } why ? $"its sandbox '{sandbox.Profile}' is weaker than '{floor.Profile}': {why}" : null;
    }
}

public sealed partial class SessionRuntimeService
{
    /// <summary>
    /// Gets what a session carries to the work it starts or drives on another machine: its permission mode, its sandbox
    /// and what that sandbox confines, or why it may do nothing there (its profile does not allow the fleet, or cannot be
    /// used). Read again at each call, as the sandbox of a session is.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The floor.</returns>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is empty.</exception>
    public async Task<SessionFleetFloor> GetFleetFloorAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var mode = GetEffectivePermissionMode(sessionId);
        var sandbox = await GetSessionSandboxAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (!sandbox.IsSandboxed || sandbox.Choice.Profile is not { } name) return new(mode, null, null, null);
        if (sandbox.Profile is not { } profile)
            return new(mode, name, null, $"This session is sandboxed (profile '{name}'), and its profile cannot be used ({sandbox.Problem}): nothing of it goes to another machine. Ask the user.");
        return new(mode, name, SandboxConfinement.Of(profile), profile.Fleet ? null
            : $"This session is sandboxed (profile '{name}'), which does not allow the fleet: it starts and drives nothing on another machine, which would run outside its sandbox. Ask the user.");
    }

    /// <summary>
    /// Gets the permission mode a session runs in, attached or not: its own, else the one its provider is configured with,
    /// else what no mode means on this host. Unlike <see cref="GetEffectivePermissionMode"/>, a session that is not attached
    /// is not taken to ask: a prompt would attach it in that mode.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The mode.</returns>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is empty.</exception>
    public async Task<string> GetPermissionModeAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (_entries.ContainsKey(sessionId)) return GetEffectivePermissionMode(sessionId);
        var session = await ResolveOwnedSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return ModeOrDefaults(session?.PermissionMode, session?.ResolvedProviderKey) ?? Runtime.SessionPermissionModes.Default(!_autoApproveOwnedPermissions());
    }

    /// <summary>Gets what a profile of this computer confines: the floor of a session started elsewhere in that sandbox.</summary>
    /// <param name="profile">The name of the profile.</param>
    /// <returns>The confinement; null when no profile of this computer has that name, or it cannot be used.</returns>
    public SandboxConfinement? ResolveSandboxConfinement(string profile)
    {
        if (string.IsNullOrWhiteSpace(profile) || string.Equals(profile.Trim(), SandboxProfiles.Off, StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            return SandboxConfinement.Of(SandboxProfiles.Resolve(profile.Trim(), ReadSandboxSettings().Profiles));
        }
        catch (SandboxProfileException)
        {
            return null;
        }
    }
}
