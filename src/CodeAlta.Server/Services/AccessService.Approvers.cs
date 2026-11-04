using CodeAlta.Fleet.Protocol;

namespace CodeAlta.Server.Services;

internal sealed partial class AccessService
{
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
    internal async Task<IReadOnlyList<string>> ApproversOfAsync(string organizationId, string automationId, string personId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(automationId);
        ArgumentNullException.ThrowIfNull(personId);
        var snapshot = await _access.ReadAccessAsync(organizationId, cancellationToken).ConfigureAwait(false);
        var (ownerKind, ownerId) = snapshot.Owner(FleetResourceKinds.Automation, automationId) is { } own ? (own.OwnerKind, own.OwnerId) : (FleetPrincipalKinds.Person, personId);
        var approvers = AccessPolicy.Approvers(snapshot, AccessTarget.Resource(FleetResourceKinds.Automation, automationId, ownerKind, ownerId), _time.GetUtcNow());
        return approvers.Count > 0 ? approvers : [personId];
    }
}
