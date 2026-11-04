using CodeAlta.Fleet.Protocol;
using CodeAlta.Server.Storage;

namespace CodeAlta.Server.Services;

internal sealed partial class AccessService
{
    /// <summary>What an automation says when the person on call of the team that owns it answers its approvals.</summary>
    internal const string OnCallApprovers = "oncall";

    /// <summary>
    /// Lists the people told that a run of an automation waits for an approval: its owner, the members of the team that owns
    /// it, and those granted <c>automation.approve</c> on it (<see cref="AccessPolicy.Approvers"/>). The person whose version
    /// the server holds answers when nobody else does, so that a run never waits unheard.
    /// </summary>
    /// <param name="organizationId">The organization.</param>
    /// <param name="automationId">The automation.</param>
    /// <param name="personId">The person whose signed version the server holds for the machine.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The people, never empty.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    internal Task<IReadOnlyList<string>> ApproversOfAsync(string organizationId, string automationId, string personId, CancellationToken cancellationToken)
        => ApproversOfAsync(organizationId, automationId, personId, null, cancellationToken);

    /// <summary>
    /// Lists the people told that a run waits for an approval, as <see cref="ApproversOfAsync(string, string, string, CancellationToken)"/>
    /// does; an automation that asks the person on call (<c>approvers = "oncall"</c>) and belongs to a team with an on-call
    /// rotation asks the person on call of that team now, and everyone else who would answer when no one is on call.
    /// </summary>
    /// <param name="organizationId">The organization.</param>
    /// <param name="automationId">The automation.</param>
    /// <param name="personId">The person whose signed version the server holds.</param>
    /// <param name="approvers">Who the automation says answers its approvals, as its run reported it; null for its owner.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The people, never empty.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    internal async Task<IReadOnlyList<string>> ApproversOfAsync(string organizationId, string automationId, string personId, string? approvers, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(automationId);
        ArgumentNullException.ThrowIfNull(personId);
        var snapshot = await _access.ReadAccessAsync(organizationId, cancellationToken).ConfigureAwait(false);
        var (ownerKind, ownerId) = snapshot.Owner(FleetResourceKinds.Automation, automationId) is { } own ? (own.OwnerKind, own.OwnerId) : (FleetPrincipalKinds.Person, personId);
        if (approvers == OnCallApprovers && ownerKind == FleetPrincipalKinds.Team
            && await PersonOnCallAsync(organizationId, ownerId, snapshot, cancellationToken).ConfigureAwait(false) is { } onCall)
            return [onCall];
        var people = AccessPolicy.Approvers(snapshot, AccessTarget.Resource(FleetResourceKinds.Automation, automationId, ownerKind, ownerId), _time.GetUtcNow());
        return people.Count > 0 ? people : [personId];
    }

    /// <summary>Gets whether a person answers the approvals of an automation, and so takes its runs over.</summary>
    /// <param name="organizationId">The organization.</param>
    /// <param name="automationId">The automation.</param>
    /// <param name="ownerPersonId">The person whose signed version the server holds.</param>
    /// <param name="personId">The person who asks.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Whether they do.</returns>
    internal async Task<bool> ApprovesAsync(string organizationId, string automationId, string ownerPersonId, string personId, CancellationToken cancellationToken)
        => personId == ownerPersonId || (await ApproversOfAsync(organizationId, automationId, ownerPersonId, cancellationToken).ConfigureAwait(false)).Contains(personId, StringComparer.Ordinal);

    /// <summary>
    /// Lists, for the signed access document, who answers the approvals of each automation besides the person who placed it:
    /// what machines check before they let someone else take a run over. A person who holds it by grants with an end date only
    /// is named with that end, after which machines refuse them by their own clock; a change of approvers reaches machines
    /// once a signer signs the next version.
    /// </summary>
    /// <param name="organizationId">The organization.</param>
    /// <param name="snapshot">The access of the organization.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The approvers by automation, in a stable order; empty for none.</returns>
    internal async Task<IReadOnlyList<FleetAccessApprovers>> DocumentApproversAsync(string organizationId, AccessSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var placedBy = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var person in snapshot.People)
        {
            foreach (var automation in await _automations.ListAutomationsAsync(organizationId, person.Id, cancellationToken).ConfigureAwait(false))
            {
                if (!automation.Removed) placedBy.TryAdd(automation.AutomationId, automation.OwnerPersonId);
            }
        }

        var list = new List<FleetAccessApprovers>();
        var now = _time.GetUtcNow();
        foreach (var (automationId, personId) in placedBy)
        {
            var (ownerKind, ownerId) = snapshot.Owner(FleetResourceKinds.Automation, automationId) is { } own ? (own.OwnerKind, own.OwnerId) : (FleetPrincipalKinds.Person, personId);
            var people = AccessPolicy.ApproversWithEnds(snapshot, AccessTarget.Resource(FleetResourceKinds.Automation, automationId, ownerKind, ownerId), now)
                .Where(approver => approver.PersonId != personId).OrderBy(static approver => approver.PersonId, StringComparer.Ordinal).ToList();
            if (people.Count == 0) continue;
            var ends = people.Where(static approver => approver.Until is not null).ToDictionary(static approver => approver.PersonId, static approver => approver.Until!.Value, StringComparer.Ordinal);
            list.Add(new FleetAccessApprovers(automationId, [.. people.Select(static approver => approver.PersonId)]) { Until = ends.Count > 0 ? ends : null });
        }

        return list;
    }
}
