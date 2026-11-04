namespace CodeAlta.LiveTool;

/// <summary>
/// People and access of a server, as the person of this computer may see them (<c>doc/fleet/access-control.md</c>), for
/// <c>alta fleet access</c>: read only for agents, who propose changes that a person applies.
/// </summary>
public interface IAltaFleetAccess
{
    /// <summary>Reads people and access at a server.</summary>
    /// <param name="server">The server, by its link id or its address; null for the one online.</param>
    /// <param name="cancellationToken">Stops waiting for the server.</param>
    /// <returns>What the person may see, or why it could not be read (<c>no_server</c>, <c>unreachable</c>...).</returns>
    Task<AltaFleetAccess> ReadAccessAsync(string? server, CancellationToken cancellationToken);

    /// <summary>Records a change of access an agent proposes for the person of this computer; it changes nothing until a person applies it.</summary>
    /// <param name="server">The server; null for the one online.</param>
    /// <param name="kind">What it changes: <c>role</c>, <c>team-member</c>, <c>grant</c>, <c>transfer</c> or <c>team</c>.</param>
    /// <param name="summary">What it does and why, in a sentence.</param>
    /// <param name="payload">The change, as the JSON object of its request.</param>
    /// <param name="sessionId">The session that proposes it.</param>
    /// <param name="cancellationToken">Stops waiting for the server.</param>
    /// <returns>The proposal, or why it was refused.</returns>
    Task<AltaFleetAccessProposal> ProposeAccessAsync(string? server, string kind, string summary, string payload, string? sessionId, CancellationToken cancellationToken);
}

/// <summary>People and access at a server.</summary>
/// <param name="Status"><c>ok</c>, <c>no_server</c>, <c>unreachable</c> or the code the server answered.</param>
/// <param name="Message">What was said of a failure.</param>
public sealed record AltaFleetAccess(string Status, string? Message)
{
    /// <summary>Gets the address of the server.</summary>
    public string? Server { get; init; }

    /// <summary>Gets the person of this computer.</summary>
    public AltaFleetPerson? Me { get; init; }

    /// <summary>Gets what they may do at the organization's scope, such as <c>access.manage</c>.</summary>
    public IReadOnlyList<string> Capabilities { get; init; } = [];

    /// <summary>Gets whether their devices sign the access document.</summary>
    public bool Signer { get; init; }

    /// <summary>Gets whether the organization uses teams, grants or roles beyond owner and members.</summary>
    public bool Advanced { get; init; }

    /// <summary>Gets the number of the last signed access document; 0 when none.</summary>
    public long DocumentNumber { get; init; }

    /// <summary>Gets the changes machines do not see yet, waiting for a signer.</summary>
    public IReadOnlyList<string> Pending { get; init; } = [];

    /// <summary>Gets the people.</summary>
    public IReadOnlyList<AltaFleetPerson> People { get; init; } = [];

    /// <summary>Gets the teams.</summary>
    public IReadOnlyList<AltaFleetTeam> Teams { get; init; } = [];

    /// <summary>Gets the grants the person may see.</summary>
    public IReadOnlyList<AltaFleetGrant> Grants { get; init; } = [];

    /// <summary>Gets the resources the person may see and their owners.</summary>
    public IReadOnlyList<AltaFleetOwner> Owners { get; init; } = [];

    /// <summary>Gets the transfers that concern the person.</summary>
    public IReadOnlyList<AltaFleetTransfer> Transfers { get; init; } = [];

    /// <summary>Gets who can do what; null when the person may not review access.</summary>
    public IReadOnlyList<AltaFleetAccessLine>? Review { get; init; }

    /// <summary>Gets the changes agents proposed.</summary>
    public IReadOnlyList<AltaFleetAccessProposal> Proposals { get; init; } = [];
}

/// <summary>A person of the organization.</summary>
/// <param name="Id">Their id.</param>
/// <param name="Name">Their name.</param>
/// <param name="Role"><c>owner</c>, <c>admin</c>, <c>member</c>, <c>auditor</c> or <c>guest</c>.</param>
/// <param name="Status"><c>active</c> or <c>suspended</c>.</param>
/// <param name="Teams">Their teams, as <c>name (role)</c>.</param>
/// <param name="Devices">How many approved devices they have; 0 for a person invited who has not joined yet.</param>
public sealed record AltaFleetPerson(string Id, string Name, string Role, string Status, IReadOnlyList<string> Teams, int Devices);

/// <summary>A team.</summary>
/// <param name="Id">Its id.</param>
/// <param name="Name">Its name.</param>
/// <param name="Maintainers">The names of its maintainers.</param>
/// <param name="Members">The names of its other members.</param>
/// <param name="Machines">The names of its team machines.</param>
/// <param name="RunsMemberAutomations">Whether its machines run the automations of its members.</param>
public sealed record AltaFleetTeam(string Id, string Name, IReadOnlyList<string> Maintainers, IReadOnlyList<string> Members, IReadOnlyList<string> Machines,
    bool RunsMemberAutomations);

/// <summary>A grant.</summary>
/// <param name="Id">Its id.</param>
/// <param name="Subject">Who is granted, as <c>kind:name</c>.</param>
/// <param name="Capability">What they may do.</param>
/// <param name="Scope">Where, as <c>kind:id</c>.</param>
/// <param name="Status"><c>active</c> or <c>pending_keys</c>.</param>
/// <param name="ExpiresAt">When it stops applying.</param>
public sealed record AltaFleetGrant(string Id, string Subject, string Capability, string Scope, string Status, DateTimeOffset? ExpiresAt);

/// <summary>A resource and its owner.</summary>
/// <param name="Kind"><c>machine</c> or <c>automation</c>.</param>
/// <param name="Id">Its id.</param>
/// <param name="Name">Its name, when the server knows one.</param>
/// <param name="Owner">Who answers for it, as <c>kind:name</c>.</param>
/// <param name="Paused">Whether it waits for its owner to resume it.</param>
/// <param name="OwnerSuspended">Whether its owner is suspended: someone takes it.</param>
public sealed record AltaFleetOwner(string Kind, string Id, string? Name, string Owner, bool Paused, bool OwnerSuspended);

/// <summary>A transfer.</summary>
/// <param name="Id">Its id.</param>
/// <param name="Resource">What, as <c>kind:id</c>.</param>
/// <param name="From">The owner it leaves.</param>
/// <param name="To">The owner it goes to.</param>
/// <param name="State"><c>offered</c>, <c>accepted</c>, <c>declined</c> or <c>cancelled</c>.</param>
/// <param name="Reason"><c>transfer</c>, <c>deprovision</c> or <c>dissolve</c>.</param>
public sealed record AltaFleetTransfer(string Id, string Resource, string From, string To, string State, string Reason);

/// <summary>A line of who can do what.</summary>
/// <param name="Principal">Who, as <c>kind:name</c>.</param>
/// <param name="Capability">What, or <c>*</c>.</param>
/// <param name="Scope">Where.</param>
/// <param name="Source">Where it comes from.</param>
public sealed record AltaFleetAccessLine(string Principal, string Capability, string Scope, string Source);

/// <summary>A change of access an agent proposed.</summary>
/// <param name="Status"><c>ok</c>, or why it was refused.</param>
/// <param name="Message">What was said of a failure.</param>
/// <param name="Id">Its id.</param>
/// <param name="Kind">What it changes.</param>
/// <param name="Summary">What it does and why.</param>
/// <param name="State"><c>open</c>, <c>applied</c> or <c>dismissed</c>.</param>
public sealed record AltaFleetAccessProposal(string Status, string? Message, string? Id, string? Kind, string? Summary, string? State);
