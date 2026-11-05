using System.Collections.Frozen;

namespace CodeAlta.Fleet.Protocol;

/// <summary>The roles of a person in the organization (<c>doc/fleet/access-control.md</c>, organization roles).</summary>
public static class FleetOrganizationRoles
{
    /// <summary>Everything: people, teams, roles, sign-in, policies; never the content of others.</summary>
    public const string Owner = "owner";

    /// <summary>People, teams, team machines, projects, organization automations and providers, budgets, audit; never the content of others.</summary>
    public const string Admin = "admin";

    /// <summary>Their devices and machines, their sessions and automations, and what their team roles give.</summary>
    public const string Member = "member";

    /// <summary>Reads the audit log, fleet facts and statistics as policy allows; changes nothing, reads no content.</summary>
    public const string Auditor = "auditor";

    /// <summary>What is shared with them, nothing else.</summary>
    public const string Guest = "guest";

    /// <summary>Gets the roles, from the widest.</summary>
    public static IReadOnlyList<string> All { get; } = [Owner, Admin, Member, Auditor, Guest];

    /// <summary>Gets whether a text is a role.</summary>
    /// <param name="role">The text.</param>
    /// <returns>Whether it is one of <see cref="All"/>.</returns>
    public static bool IsKnown(string? role) => role is Owner or Admin or Member or Auditor or Guest;

    /// <summary>Gets whether a role administers the organization: an owner or an admin.</summary>
    /// <param name="role">The role.</param>
    /// <returns>Whether it is <see cref="Owner"/> or <see cref="Admin"/>.</returns>
    public static bool Administers(string? role) => role is Owner or Admin;
}

/// <summary>The roles of a person in a team.</summary>
public static class FleetTeamRoles
{
    /// <summary>Manages the team's machines, automations, budgets, providers and members.</summary>
    public const string Maintainer = "maintainer";

    /// <summary>Uses the team's machines and projects, sees the team's runs, answers the approvals routed to the team.</summary>
    public const string Member = "member";

    /// <summary>Gets whether a text is a team role.</summary>
    /// <param name="role">The text.</param>
    /// <returns>Whether it is <see cref="Maintainer"/> or <see cref="Member"/>.</returns>
    public static bool IsKnown(string? role) => role is Maintainer or Member;
}

/// <summary>Whether a person may act in the organization.</summary>
public static class FleetPersonStatuses
{
    /// <summary>The person acts with their devices.</summary>
    public const string Active = "active";

    /// <summary>Deprovisioned or suspended: their devices are refused, their work stopped on team machines, what they owned given to a successor.</summary>
    public const string Suspended = "suspended";
}

/// <summary>The kinds of principals: who owns, who is granted, who acts.</summary>
public static class FleetPrincipalKinds
{
    /// <summary>A person.</summary>
    public const string Person = "person";

    /// <summary>A team, through its maintainers and members.</summary>
    public const string Team = "team";

    /// <summary>The organization, through its owners and admins.</summary>
    public const string Organization = "organization";

    /// <summary>A service identity of a team or of the organization, for unattended work.</summary>
    public const string Service = "service";

    /// <summary>Gets whether a text is a kind that can own a resource: a person, a team or the organization.</summary>
    /// <param name="kind">The text.</param>
    /// <returns>Whether it can own.</returns>
    public static bool CanOwn(string? kind) => kind is Person or Team or Organization;

    /// <summary>Gets whether a text is a kind that can be granted a capability: a person, a team or a service identity.</summary>
    /// <param name="kind">The text.</param>
    /// <returns>Whether it can be granted.</returns>
    public static bool CanBeGranted(string? kind) => kind is Person or Team or Service;
}

/// <summary>The scopes a grant or a resource lives in: they nest from the organization down to the resource itself.</summary>
public static class FleetScopeKinds
{
    /// <summary>The whole organization.</summary>
    public const string Organization = "organization";

    /// <summary>A team and what it owns.</summary>
    public const string Team = "team";

    /// <summary>A project, by its repository key or blind index.</summary>
    public const string Project = "project";

    /// <summary>One resource, named by its kind and id (<c>machine:&lt;id&gt;</c>).</summary>
    public const string Resource = "resource";

    /// <summary>Gets whether a text is a scope.</summary>
    /// <param name="kind">The text.</param>
    /// <returns>Whether it is a scope.</returns>
    public static bool IsKnown(string? kind) => kind is Organization or Team or Project or Resource;
}

/// <summary>The kinds of resources of a fleet that have an owner and capabilities.</summary>
public static class FleetResourceKinds
{
    /// <summary>A machine: a node or a desktop that hosts sessions.</summary>
    public const string Machine = "machine";

    /// <summary>A session.</summary>
    public const string Session = "session";

    /// <summary>An automation.</summary>
    public const string Automation = "automation";

    /// <summary>A run of an automation.</summary>
    public const string Run = "run";

    /// <summary>A workflow script.</summary>
    public const string Script = "script";

    /// <summary>The statistics of the fleet.</summary>
    public const string Statistics = "statistics";

    /// <summary>A project channel.</summary>
    public const string Channel = "channel";

    /// <summary>An organization provider or a secret.</summary>
    public const string Provider = "provider";

    /// <summary>A budget.</summary>
    public const string Budget = "budget";

    /// <summary>A policy bundle.</summary>
    public const string Policy = "policy";

    /// <summary>The audit log.</summary>
    public const string Audit = "audit";

    /// <summary>The people, teams, roles and grants of the organization.</summary>
    public const string Access = "access";

    /// <summary>Gets whether a text is a kind of resource.</summary>
    /// <param name="kind">The text.</param>
    /// <returns>Whether it is known.</returns>
    public static bool IsKnown(string? kind) => kind is not null && FleetCapabilities.ByResource.ContainsKey(kind);

    /// <summary>Gets whether a resource of a kind can be given to another owner (<c>doc/fleet/access-control.md</c>, ownership over time).</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>Whether it transfers: machines and automations in this version.</returns>
    public static bool Transfers(string? kind) => kind is Machine or Automation;
}

/// <summary>
/// The capabilities of each kind of resource (<c>doc/fleet/access-control.md</c>, grants), written <c>&lt;kind&gt;.&lt;name&gt;</c>,
/// such as <c>automation.edit</c>. Management capabilities flow down the scopes; content capabilities (<see cref="IsContent"/>)
/// never do: they come from ownership or from a grant on the resource itself.
/// </summary>
public static class FleetCapabilities
{
    /// <summary>Gets the capabilities by kind of resource, without the kind.</summary>
    public static FrozenDictionary<string, IReadOnlyList<string>> ByResource { get; } = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
    {
        [FleetResourceKinds.Machine] = ["view", "start", "manage", "logs"],
        [FleetResourceKinds.Session] = ["view-facts", "view-content", "interact", "approve", "move", "share", "delete"],
        [FleetResourceKinds.Automation] = ["view", "run", "pause", "edit", "approve", "manage-access", "transfer", "delete"],
        [FleetResourceKinds.Run] = ["view-facts", "view-content", "cancel", "retry"],
        [FleetResourceKinds.Script] = ["use", "publish", "approve-version"],
        [FleetResourceKinds.Statistics] = ["view-own", "view-team", "view-organization", "view-per-person"],
        [FleetResourceKinds.Channel] = ["read", "post", "moderate"],
        [FleetResourceKinds.Provider] = ["use", "manage", "rotate"],
        [FleetResourceKinds.Budget] = ["view", "set"],
        [FleetResourceKinds.Policy] = ["view", "set", "sign"],
        [FleetResourceKinds.Audit] = ["read", "export"],
        [FleetResourceKinds.Access] = ["view", "manage", "sign", "review"],
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>Gets every capability, qualified by its kind of resource.</summary>
    public static IReadOnlyList<string> All { get; } = [.. ByResource.SelectMany(static pair => pair.Value.Select(name => pair.Key + "." + name)).Order(StringComparer.Ordinal)];

    /// <summary>Gets whether a text is a capability <c>&lt;kind&gt;.&lt;name&gt;</c>.</summary>
    /// <param name="capability">The text.</param>
    /// <returns>Whether it is known.</returns>
    public static bool IsKnown(string? capability)
        => TrySplit(capability, out var kind, out var name) && ByResource.TryGetValue(kind, out var names) && names.Contains(name, StringComparer.Ordinal);

    /// <summary>Splits a capability into its kind of resource and its name.</summary>
    /// <param name="capability">The capability.</param>
    /// <param name="kind">Its kind of resource.</param>
    /// <param name="name">Its name.</param>
    /// <returns>Whether it has both.</returns>
    public static bool TrySplit(string? capability, out string kind, out string name)
    {
        kind = name = string.Empty;
        var dot = capability?.IndexOf('.') ?? -1;
        if (dot <= 0 || dot == capability!.Length - 1) return false;
        kind = capability[..dot];
        name = capability[(dot + 1)..];
        return true;
    }

    /// <summary>
    /// Gets whether a capability reads or acts on content (<c>view-content</c>, <c>interact</c>, the approvals of a session,
    /// the log of a machine): it never flows down the scopes, so an admin has it only on what they own or were granted.
    /// </summary>
    /// <param name="capability">The capability.</param>
    /// <returns>Whether it is a content capability.</returns>
    public static bool IsContent(string? capability)
        => capability is "session.view-content" or "session.interact" or "session.approve" or "run.view-content" or "machine.logs" or "statistics.view-per-person";

    /// <summary>Gets whether a capability only reads facts, never content and never a change.</summary>
    /// <param name="capability">The capability.</param>
    /// <returns>Whether it reads facts.</returns>
    public static bool IsFacts(string? capability)
        => TrySplit(capability, out _, out var name) && !IsContent(capability) && name is "view" or "view-facts" or "read" or "view-own" or "view-team" or "view-organization" or "review";
}

/// <summary>Where a grant stands.</summary>
public static class FleetGrantStatuses
{
    /// <summary>The grant applies.</summary>
    public const string Active = "active";

    /// <summary>
    /// A grant of content sealed from the server (a Private space): it applies once a device that holds the keys wrapped
    /// them to the grantee's devices; the server never marks it active by itself.
    /// </summary>
    public const string PendingKeys = "pending_keys";

    /// <summary>The grant was taken back.</summary>
    public const string Revoked = "revoked";
}

/// <summary>Where a transfer stands.</summary>
public static class FleetTransferStates
{
    /// <summary>Offered, waiting for the new owner.</summary>
    public const string Offered = "offered";

    /// <summary>The new owner took it.</summary>
    public const string Accepted = "accepted";

    /// <summary>The new owner refused it.</summary>
    public const string Declined = "declined";

    /// <summary>The one who offered it took the offer back.</summary>
    public const string Cancelled = "cancelled";
}

/// <summary>Why a resource changes owner.</summary>
public static class FleetTransferReasons
{
    /// <summary>Its owner gave it.</summary>
    public const string Transfer = "transfer";

    /// <summary>Its owner left the organization: it went to a successor, paused.</summary>
    public const string Deprovision = "deprovision";

    /// <summary>Its team was dissolved: it went to the organization.</summary>
    public const string Dissolve = "dissolve";
}
