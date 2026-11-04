namespace CodeAlta.LiveTool;

/// <summary>
/// The signed policy of a server and its kill switch, as this computer verified them (<c>doc/fleet/security-and-privacy.md</c>,
/// policies and the kill switch), for <c>alta fleet policy</c>: read only for agents; a person signs a policy or a stop.
/// </summary>
public interface IAltaFleetPolicy
{
    /// <summary>Reads the policy of a server as this computer verified it.</summary>
    /// <param name="server">The server, by its link id or its address; null for the one online.</param>
    /// <param name="cancellationToken">Stops waiting for the server.</param>
    /// <returns>The policy, or why it could not be read.</returns>
    Task<AltaFleetPolicy> ReadPolicyAsync(string? server, CancellationToken cancellationToken);
}

/// <summary>The policy of a server.</summary>
/// <param name="Status"><c>ok</c>, or why not (<c>no_server</c>, <c>unreachable</c>, <c>policy.invalid</c>...).</param>
/// <param name="Message">What was said of a failure.</param>
public sealed record AltaFleetPolicy(string Status, string? Message)
{
    /// <summary>Gets the address of the server.</summary>
    public string? Server { get; init; }

    /// <summary>Gets the number of the version this computer verified; 0 when none was signed.</summary>
    public long Number { get; init; }

    /// <summary>Gets when its signer made it.</summary>
    public DateTimeOffset? SignedAt { get; init; }

    /// <summary>Gets its rules, for people, one per line; empty when it sets none.</summary>
    public IReadOnlyList<string> Rules { get; init; } = [];

    /// <summary>Gets its kill switch, or null while the work of the fleet goes on.</summary>
    public AltaFleetPolicyStop? Stop { get; init; }

    /// <summary>Gets whether the stop reaches this computer.</summary>
    public bool StopsThisComputer { get; init; }

    /// <summary>Gets why the chain the server answers does not verify, or null: the version verified before holds.</summary>
    public string? Refused { get; init; }
}

/// <summary>A stop of the kill switch.</summary>
/// <param name="Scope"><c>fleet</c>, <c>machines</c> or <c>teams</c>.</param>
/// <param name="Names">The names of the machines or teams it names.</param>
/// <param name="Reason">Why.</param>
public sealed record AltaFleetPolicyStop(string Scope, IReadOnlyList<string> Names, string? Reason);
