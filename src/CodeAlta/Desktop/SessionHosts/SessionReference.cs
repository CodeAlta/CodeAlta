namespace CodeAlta.Desktop.SessionHosts;

/// <summary>
/// Names a session for the session hosts of the window: its global identifier, and the machine that owns it.
/// </summary>
/// <param name="SessionId">The identifier of the session, the same on every machine.</param>
/// <param name="MachineId">The machine that owns the session; null for a session of this process.</param>
internal readonly record struct SessionReference(string SessionId, string? MachineId = null)
{
    /// <summary>Gets whether the session belongs to the host of this process.</summary>
    public bool IsLocal => MachineId is null;
}
