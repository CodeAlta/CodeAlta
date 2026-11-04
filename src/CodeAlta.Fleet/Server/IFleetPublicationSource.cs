using CodeAlta.Fleet.Protocol;

namespace CodeAlta.Fleet.Server;

/// <summary>The states of a session as its facts say them (<c>doc/fleet/protocol.md</c>).</summary>
public static class FleetSessionStates
{
    /// <summary>A turn runs.</summary>
    public const string Running = "running";

    /// <summary>The session waits for a person: a question, a permission.</summary>
    public const string RequiresAction = "requires_action";

    /// <summary>Nothing runs.</summary>
    public const string Idle = "idle";

    /// <summary>The session is archived.</summary>
    public const string Archived = "archived";
}

/// <summary>
/// A session of this instance as a <see cref="FleetConnector"/> may publish it: the facts of the allow-list, what
/// decides whether it is published at all (its space, whether it is Private), and its title, which leaves the machine
/// only sealed with the metadata key of its person. Never a project, a path or content.
/// </summary>
/// <param name="SessionId">The id of the session.</param>
/// <param name="SpaceId">The space it is in; the default space when null.</param>
/// <param name="IsPrivate">Whether it is Private: such a session is never published.</param>
/// <param name="Epoch">Its ownership epoch on this machine (0 until it was moved here).</param>
/// <param name="State">Its state (<see cref="FleetSessionStates"/>).</param>
/// <param name="CreatedAt">When it was created.</param>
/// <param name="UpdatedAt">When it last changed.</param>
/// <param name="ProviderType">The type of its provider, such as <c>anthropic</c>; null when unknown.</param>
/// <param name="Model">Its model; null when unknown.</param>
/// <param name="Counts">Its counts; null when unknown.</param>
/// <param name="Title">Its title; null when it has none. Published only sealed (<see cref="FleetMetadataKey"/>).</param>
/// <param name="ProjectIdentity">
/// What recognizes its project on the person's machines (<see cref="FleetProjectKeys"/>); null for a session without a project.
/// Never published: its statistics carry its blind index only.
/// </param>
/// <param name="PersonId">The person who started it here through a team membership (a team machine); null otherwise.</param>
/// <param name="PersonOrganizationId">
/// The organization of <paramref name="PersonId"/>: only its server is told the person, a person's id being unique within one organization.
/// </param>
public sealed record FleetLocalSession(
    string SessionId,
    string? SpaceId,
    bool IsPrivate,
    long Epoch,
    string State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? ProviderType = null,
    string? Model = null,
    FleetSessionCounts? Counts = null,
    string? Title = null,
    string? ProjectIdentity = null,
    string? PersonId = null,
    string? PersonOrganizationId = null);

/// <summary>What an instance has to publish to its servers: the inventory of the machine and its sessions.</summary>
public interface IFleetPublicationSource
{
    /// <summary>Raised when something to publish may have changed; the connector reads the source again soon after.</summary>
    event Action? Changed;

    /// <summary>Reads the inventory of the machine.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The inventory.</returns>
    ValueTask<FleetMachineInventory> ReadInventoryAsync(CancellationToken cancellationToken);

    /// <summary>Reads the sessions of the instance, Private ones and those of every space included: the connector chooses what it publishes.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The sessions.</returns>
    ValueTask<IReadOnlyList<FleetLocalSession>> ReadSessionsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Reads the sessions this instance gave away (moved to another machine): it no longer publishes their facts, but it
    /// keeps publishing the statistics it counted of them while it owned them, which the machine that has them now leaves
    /// out of its own. Their space, privacy, epoch and project are as they were here.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The sessions; empty for an instance that moves none.</returns>
    ValueTask<IReadOnlyList<FleetLocalSession>> ReadRetainedSessionsAsync(CancellationToken cancellationToken);
}
