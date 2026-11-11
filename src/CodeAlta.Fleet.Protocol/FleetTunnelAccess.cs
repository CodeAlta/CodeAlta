namespace CodeAlta.Fleet.Protocol.Api;

/// <summary>A short-lived Dev Tunnel connect credential. It opens the network gate, never the fleet's device authentication.</summary>
/// <param name="Address">The HTTPS address of the server, to which the credential is bound.</param>
/// <param name="Token">The secret connect-only token. Never put it in an address, a log or a diagnostic.</param>
/// <param name="ExpiresAt">When the token expires.</param>
public sealed record FleetTunnelAccessDto(string Address, string Token, DateTimeOffset ExpiresAt)
{
    /// <summary>Describes the credential without disclosing its token.</summary>
    /// <returns>A redacted description.</returns>
    public override string ToString() => $"Dev Tunnel access for {Address}, expires {ExpiresAt:O}";
}

/// <summary>An enrollment invitation that also opens a private Dev Tunnel; the whole invitation is secret.</summary>
/// <param name="EnrollmentToken">The single-use enrollment code.</param>
/// <param name="Access">The transport credential, bound to the server.</param>
public sealed record FleetTunnelInvitationDto(string EnrollmentToken, FleetTunnelAccessDto Access)
{
    /// <summary>Describes the invitation without disclosing either credential.</summary>
    /// <returns>A redacted description.</returns>
    public override string ToString() => "Private Dev Tunnel enrollment invitation";
}
