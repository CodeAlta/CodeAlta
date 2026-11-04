using System.Collections.Frozen;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Server.Storage;

namespace CodeAlta.Server.Services;

/// <summary>
/// What a capability is asked on: the organization, a team, a project or one resource, with the owner of that resource and
/// the team it lives in.
/// </summary>
/// <param name="ScopeKind">The scope (<see cref="FleetScopeKinds"/>).</param>
/// <param name="TeamId">The team the target lives in or belongs to; null when none.</param>
/// <param name="ProjectKey">The project the target lives in; null when none.</param>
/// <param name="ResourceKind">The kind of the resource; null for a scope.</param>
/// <param name="ResourceId">Its id; null for a scope.</param>
/// <param name="OwnerKind">The owner of the resource (<see cref="FleetPrincipalKinds"/>); null for a scope.</param>
/// <param name="OwnerId">Their id.</param>
internal sealed record AccessTarget(string ScopeKind, string? TeamId, string? ProjectKey, string? ResourceKind, string? ResourceId, string? OwnerKind, string? OwnerId)
{
    /// <summary>Gets the organization itself.</summary>
    public static AccessTarget Organization { get; } = new(FleetScopeKinds.Organization, null, null, null, null, null, null);

    /// <summary>A team itself: its members, its machines, its settings.</summary>
    /// <param name="teamId">The team.</param>
    /// <returns>The target.</returns>
    public static AccessTarget Team(string teamId) => new(FleetScopeKinds.Team, teamId, null, null, null, FleetPrincipalKinds.Team, teamId);

    /// <summary>A resource and its owner.</summary>
    /// <param name="kind">The kind of resource.</param>
    /// <param name="id">Its id.</param>
    /// <param name="ownerKind">The kind of its owner.</param>
    /// <param name="ownerId">The owner.</param>
    /// <param name="projectKey">The project it lives in, if any.</param>
    /// <returns>The target.</returns>
    public static AccessTarget Resource(string kind, string id, string ownerKind, string ownerId, string? projectKey = null)
        => new(FleetScopeKinds.Resource, ownerKind == FleetPrincipalKinds.Team ? ownerId : null, projectKey, kind, id, ownerKind, ownerId);

    /// <summary>Gets the id of the resource as a grant names it: <c>&lt;kind&gt;:&lt;id&gt;</c>.</summary>
    public string? GrantScopeId => ResourceKind is null || ResourceId is null ? null : ResourceKind + ":" + ResourceId;
}

/// <summary>A decision: whether the capability is held, and where it comes from.</summary>
/// <param name="Allowed">Whether it is held.</param>
/// <param name="Source">Where it comes from (<c>owner</c>, <c>role:admin</c>, <c>team:&lt;id&gt;:member</c>, <c>grant:&lt;id&gt;</c>), or why not.</param>
internal sealed record AccessDecision(bool Allowed, string Source);

/// <summary>
/// The rules of access control (<c>doc/fleet/access-control.md</c>, roles and grants), without state: denied unless granted;
/// a capability comes from owning the resource, from an organization role, from a team role on what the team owns, or from
/// an explicit grant. Management flows down the scopes (organization, team, project, resource); content
/// (<see cref="FleetCapabilities.IsContent"/>) never does: it comes from ownership, from the team that owns the resource, or
/// from a grant on that resource itself, so an owner or an admin sees that a session exists, never what it says.
/// </summary>
/// <remarks>
/// This decides what the server answers and lets happen. What runs on a machine is decided by that machine, from the
/// signed access document and its own policy: never from these rules alone.
/// </remarks>
internal static class AccessPolicy
{
    // What a member of a team may do with what the team owns; maintainers may do everything.
    private static readonly FrozenSet<string> TeamMemberNames = new[]
    {
        "view", "view-facts", "view-content", "start", "approve", "use", "read", "post", "view-team", "run",
    }.ToFrozenSet(StringComparer.Ordinal);

    // What every person of the organization may do at its scope.
    private static readonly FrozenSet<string> EveryoneCapabilities = new[] { "statistics.view-own", "access.view" }.ToFrozenSet(StringComparer.Ordinal);

    // What an auditor reads beyond the facts.
    private static readonly FrozenSet<string> AuditorCapabilities = new[]
    {
        "audit.read", "audit.export", "access.view", "access.review", "statistics.view-organization", "statistics.view-team", "statistics.view-own",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Decides whether a person holds a capability on a target.</summary>
    /// <param name="snapshot">The access of the organization.</param>
    /// <param name="personId">The person.</param>
    /// <param name="capability">The capability (<see cref="FleetCapabilities"/>).</param>
    /// <param name="target">What it is asked on.</param>
    /// <param name="now">The time, for grants that expire.</param>
    /// <returns>The decision.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The capability is not known.</exception>
    public static AccessDecision Decide(AccessSnapshot snapshot, string personId, string capability, AccessTarget target, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(personId);
        ArgumentNullException.ThrowIfNull(target);
        if (!FleetCapabilities.TrySplit(capability, out _, out var name) || !FleetCapabilities.IsKnown(capability))
            throw new ArgumentException($"'{capability}' is not a capability.", nameof(capability));
        var person = snapshot.Person(personId);
        if (person is null) return new(false, "unknown");
        if (!person.IsActive) return new(false, "suspended");
        var content = FleetCapabilities.IsContent(capability);

        // What a person owns is theirs, content included.
        if (target.OwnerKind == FleetPrincipalKinds.Person && target.OwnerId == personId) return new(true, "owner");

        // What a team owns: its maintainers manage it, its members use it and see the content of its work.
        if (target.OwnerKind == FleetPrincipalKinds.Team && target.OwnerId is { } team && snapshot.TeamRole(team, personId) is { } teamRole)
        {
            if (teamRole == FleetTeamRoles.Maintainer) return new(true, $"team:{team}:{teamRole}");
            if (TeamMemberNames.Contains(name)) return new(true, $"team:{team}:{teamRole}");
        }

        // The roles of the organization: management and facts everywhere for owners and admins, never content.
        if (!content)
        {
            switch (person.Role)
            {
                case FleetOrganizationRoles.Owner:
                case FleetOrganizationRoles.Admin:
                    // A personal machine's start and its sessions are its owner's: the node decides, and an admin is not its user.
                    if (capability is "machine.start" && target.OwnerKind == FleetPrincipalKinds.Person) break;
                    return new(true, "role:" + person.Role);
                case FleetOrganizationRoles.Auditor:
                    if (AuditorCapabilities.Contains(capability) || FleetCapabilities.IsFacts(capability)) return new(true, "role:auditor");
                    break;
            }

            if (target.ScopeKind == FleetScopeKinds.Organization && person.Role is not FleetOrganizationRoles.Guest && EveryoneCapabilities.Contains(capability))
                return new(true, "role:" + person.Role);
        }

        // Grants: to the person, or to a team they are in.
        foreach (var grant in snapshot.Grants)
        {
            if (grant.Status != FleetGrantStatuses.Active || grant.Capability != capability || grant.ExpiresAt is { } expires && expires <= now) continue;
            if (!IsSubject(snapshot, grant, personId)) continue;
            if (Covers(grant, target, content)) return new(true, "grant:" + grant.Id);
        }

        return new(false, "denied");
    }

    /// <summary>Gets whether a person holds a capability on a target.</summary>
    /// <param name="snapshot">The access of the organization.</param>
    /// <param name="personId">The person.</param>
    /// <param name="capability">The capability.</param>
    /// <param name="target">What it is asked on.</param>
    /// <param name="now">The time.</param>
    /// <returns>Whether it is held.</returns>
    public static bool Allows(AccessSnapshot snapshot, string? personId, string capability, AccessTarget target, DateTimeOffset now)
        => personId is not null && Decide(snapshot, personId, capability, target, now).Allowed;

    /// <summary>Lists the capabilities a person holds at the organization's scope.</summary>
    /// <param name="snapshot">The access of the organization.</param>
    /// <param name="personId">The person.</param>
    /// <param name="now">The time.</param>
    /// <returns>The capabilities, in order.</returns>
    public static IReadOnlyList<string> OrganizationCapabilities(AccessSnapshot snapshot, string personId, DateTimeOffset now)
        => [.. FleetCapabilities.All.Where(capability => Decide(snapshot, personId, capability, AccessTarget.Organization, now).Allowed)];

    /// <summary>
    /// Lists the people who answer the approvals of the runs of an automation: those who hold <c>automation.approve</c> on
    /// it as its owner, through a team that owns it, or by a grant. The roles of the organization give it only on what the
    /// organization owns: an admin manages every automation, but is not asked for the approvals of a person's or a team's.
    /// </summary>
    /// <param name="snapshot">The access of the organization.</param>
    /// <param name="automation">The automation and its owner.</param>
    /// <param name="now">The time, for grants that expire.</param>
    /// <returns>The people, in the order of the snapshot.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static IReadOnlyList<string> Approvers(AccessSnapshot snapshot, AccessTarget automation, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(automation);
        var byRole = automation.OwnerKind == FleetPrincipalKinds.Organization;
        return [.. snapshot.People.Where(person => Decide(snapshot, person.Id, "automation.approve", automation, now) is { Allowed: true } decision
            && (byRole || !decision.Source.StartsWith("role:", StringComparison.Ordinal))).Select(static person => person.Id)];
    }

    /// <summary>
    /// Lists the people who answer the approvals of an automation now (<see cref="Approvers"/>), each with when that ends: null
    /// for one who holds it with no end, the latest end of their grants for one who holds it by grants with an end date only.
    /// </summary>
    /// <param name="snapshot">The access of the organization.</param>
    /// <param name="automation">The automation and its owner.</param>
    /// <param name="now">The time.</param>
    /// <returns>The people and their ends, in the order of the snapshot.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static IReadOnlyList<(string PersonId, DateTimeOffset? Until)> ApproversWithEnds(AccessSnapshot snapshot, AccessTarget automation, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(automation);
        var forever = new HashSet<string>(Approvers(snapshot, automation, DateTimeOffset.MaxValue), StringComparer.Ordinal);
        var ending = snapshot.Grants.Where(grant => grant.Status == FleetGrantStatuses.Active && grant.Capability == "automation.approve" && grant.ExpiresAt > now).ToList();
        var list = new List<(string, DateTimeOffset?)>();
        foreach (var person in Approvers(snapshot, automation, now))
        {
            if (forever.Contains(person))
            {
                list.Add((person, null));
                continue;
            }

            // Only grants that end make them an approver: their latest end is theirs.
            DateTimeOffset? end = null;
            foreach (var grant in ending)
            {
                if (Decide(snapshot with { Grants = [grant] }, person, "automation.approve", automation, now) is { Allowed: true } decision
                    && decision.Source.StartsWith("grant:", StringComparison.Ordinal) && (end is null || grant.ExpiresAt > end))
                    end = grant.ExpiresAt;
            }

            if (end is not null) list.Add((person, end));
        }

        return list;
    }

    /// <summary>Gets whether a person's devices sign the access document: an active owner or admin.</summary>
    /// <param name="snapshot">The access of the organization.</param>
    /// <param name="personId">The person.</param>
    /// <returns>Whether they sign.</returns>
    public static bool IsSigner(AccessSnapshot snapshot, string? personId)
        => snapshot.Person(personId) is { IsActive: true } person && FleetOrganizationRoles.Administers(person.Role);

    /// <summary>Gets whether a grant's subject is the person or one of their teams.</summary>
    private static bool IsSubject(AccessSnapshot snapshot, GrantRecord grant, string personId)
        => grant.SubjectKind switch
        {
            FleetPrincipalKinds.Person => grant.SubjectId == personId,
            FleetPrincipalKinds.Team => snapshot.TeamRole(grant.SubjectId, personId) is not null,
            _ => false,
        };

    /// <summary>
    /// Gets whether a grant's scope covers a target: the organization covers all, a team what it owns or holds, a project
    /// what lives in it, a resource itself; a content capability only on the resource itself.
    /// </summary>
    private static bool Covers(GrantRecord grant, AccessTarget target, bool content)
    {
        if (content) return grant.ScopeKind == FleetScopeKinds.Resource && grant.ScopeId is not null && grant.ScopeId == target.GrantScopeId;
        return grant.ScopeKind switch
        {
            FleetScopeKinds.Organization => true,
            FleetScopeKinds.Team => grant.ScopeId is not null && grant.ScopeId == target.TeamId,
            FleetScopeKinds.Project => grant.ScopeId is not null && grant.ScopeId == target.ProjectKey,
            FleetScopeKinds.Resource => grant.ScopeId is not null && grant.ScopeId == target.GrantScopeId,
            _ => false,
        };
    }
}
