using CodeAlta.Fleet.Protocol;
using CodeAlta.Server.Storage;

namespace CodeAlta.Server.Services;

/// <summary>
/// The person a device answers to at the server (<c>doc/fleet/access-control.md</c>, ownership over time): a personal machine given
/// to another person, who accepted it, is theirs by its owner record, whom the device list does not name; any other device is of
/// the person the device list names. The budgets, hooks, metadata keys, values and environments of a machine follow that person,
/// as the machines do once the signed access document says so. A machine of a team has no person: the maintainers of the team
/// manage it (its budgets, hooks, values and environments are theirs), its maintainers and members use it for their own sessions
/// (their devices give it the keys of their spaces), and the person the device list names gets nothing of it by that alone.
/// </summary>
internal static class MachineOwners
{
    /// <summary>Gets the person a device answers to, from the owner records of its organization.</summary>
    /// <param name="snapshot">The access model of the organization of the device; null reads the device record alone.</param>
    /// <param name="device">The device.</param>
    /// <returns>The person, or null for a device of no person, a machine of a team included.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="device"/> is null.</exception>
    public static string? PersonOf(AccessSnapshot? snapshot, DeviceRecord device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.Kind != FleetDeviceKinds.Node) return device.OwnerPersonId;
        return snapshot?.Owner(FleetResourceKinds.Machine, device.Id) switch
        {
            { OwnerKind: FleetPrincipalKinds.Person } owner => owner.OwnerId,
            { OwnerKind: FleetPrincipalKinds.Team } => null,
            _ => device.OwnerPersonId,
        };
    }

    /// <summary>Gets the team that owns a machine, by its owner record; null for any other device.</summary>
    /// <param name="snapshot">The access model of the organization of the device; null for none.</param>
    /// <param name="device">The device.</param>
    /// <returns>The team, or null.</returns>
    public static string? TeamOf(AccessSnapshot? snapshot, DeviceRecord device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return device.Kind == FleetDeviceKinds.Node && snapshot?.Owner(FleetResourceKinds.Machine, device.Id) is { OwnerKind: FleetPrincipalKinds.Team } owner ? owner.OwnerId : null;
    }

    /// <summary>Gets whether a device takes the values, budgets, environments and hooks of a person: on a machine of a team, a maintainer of it; else its person.</summary>
    /// <param name="snapshot">The access model of the organization of the device; null reads the device record alone.</param>
    /// <param name="device">The device.</param>
    /// <param name="personId">The person.</param>
    /// <returns>Whether it takes them.</returns>
    public static bool HoldsValuesOf(AccessSnapshot? snapshot, DeviceRecord device, string? personId) => Holds(snapshot, device, personId, FleetTeamRoles.Maintainer);

    /// <summary>Gets whether a device takes the metadata keys of a person: on a machine of a team, a maintainer or a member of it; else its person.</summary>
    /// <param name="snapshot">The access model of the organization of the device; null reads the device record alone.</param>
    /// <param name="device">The device.</param>
    /// <param name="personId">The person.</param>
    /// <returns>Whether it takes them.</returns>
    public static bool HoldsKeysOf(AccessSnapshot? snapshot, DeviceRecord device, string? personId) => Holds(snapshot, device, personId, null);

    /// <summary>Gets whether a device takes the values, budgets, environments and hooks of a person (<see cref="HoldsValuesOf"/>), reading the access model.</summary>
    /// <param name="access">The access store; null reads the device record alone.</param>
    /// <param name="device">The device.</param>
    /// <param name="personId">The person.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Whether it takes them.</returns>
    public static async ValueTask<bool> HoldsValuesOfAsync(IAccessStore? access, DeviceRecord device, string? personId, CancellationToken cancellationToken)
        => HoldsValuesOf(await ReadAsync(access, device.OrganizationId, cancellationToken).ConfigureAwait(false), device, personId);

    /// <summary>Gets the people whose values, budgets and environments a device takes: the maintainers of the team of a team machine, else its person.</summary>
    /// <param name="snapshot">The access model of the organization of the device; null reads the device record alone.</param>
    /// <param name="device">The device.</param>
    /// <returns>The people; none for a device of no person.</returns>
    public static IReadOnlyList<string> ValuePeopleOf(AccessSnapshot? snapshot, DeviceRecord device) => PeopleOf(snapshot, device, FleetTeamRoles.Maintainer);

    /// <summary>Gets the people whose metadata keys a device takes: the maintainers and members of the team of a team machine, else its person.</summary>
    /// <param name="snapshot">The access model of the organization of the device; null reads the device record alone.</param>
    /// <param name="device">The device.</param>
    /// <returns>The people; none for a device of no person.</returns>
    public static IReadOnlyList<string> KeyPeopleOf(AccessSnapshot? snapshot, DeviceRecord device) => PeopleOf(snapshot, device, null);

    /// <summary>Gets the people whose values, budgets and environments a device takes (<see cref="ValuePeopleOf"/>), reading the access model.</summary>
    /// <param name="access">The access store; null reads the device record alone.</param>
    /// <param name="device">The device.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The people.</returns>
    public static async ValueTask<IReadOnlyList<string>> ValuePeopleOfAsync(IAccessStore? access, DeviceRecord device, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (access is null || device.Kind != FleetDeviceKinds.Node) return device.OwnerPersonId is { } person ? [person] : [];
        return ValuePeopleOf(await access.ReadAccessAsync(device.OrganizationId, cancellationToken).ConfigureAwait(false), device);
    }

    /// <summary>Reads the access model once for a check of several devices; null without an access store.</summary>
    /// <param name="access">The access store; null for none.</param>
    /// <param name="organizationId">The organization.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The model, or null.</returns>
    public static async ValueTask<AccessSnapshot?> ReadAsync(IAccessStore? access, string organizationId, CancellationToken cancellationToken)
        => access is null ? null : await access.ReadAccessAsync(organizationId, cancellationToken).ConfigureAwait(false);

    // A machine of a team holds what the roles of its living team give; any other device, its person's.
    private static bool Holds(AccessSnapshot? snapshot, DeviceRecord device, string? personId, string? role)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (personId is null) return false;
        if (TeamOf(snapshot, device) is { } team) return snapshot!.TeamRole(team, personId) is { } held && (role is null || held == role);
        return PersonOf(snapshot, device) == personId;
    }

    private static IReadOnlyList<string> PeopleOf(AccessSnapshot? snapshot, DeviceRecord device, string? role)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (TeamOf(snapshot, device) is { } team)
        {
            return snapshot!.Team(team) is null ? [] : [.. snapshot.Members.Where(member => member.TeamId == team && (role is null || member.Role == role))
                .Select(static member => member.PersonId).Distinct(StringComparer.Ordinal)];
        }

        return PersonOf(snapshot, device) is { } person ? [person] : [];
    }
}
