using CodeAlta.Fleet.Protocol;
using CodeAlta.Server.Storage;

namespace CodeAlta.Server.Services;

/// <summary>
/// The person a device answers to at the server (<c>doc/fleet/access-control.md</c>, ownership over time): a personal machine given
/// to another person, who accepted it, is theirs by its owner record, whom the device list does not name; any other device is of
/// the person the device list names. The budgets, hooks, metadata keys, values and environments of a machine follow that person,
/// as the machines do once the signed access document says so.
/// </summary>
internal static class MachineOwners
{
    /// <summary>Gets the person a device answers to, from the owner records of its organization.</summary>
    /// <param name="snapshot">The access model of the organization of the device; null reads the device record alone.</param>
    /// <param name="device">The device.</param>
    /// <returns>The person, or null for a device of no person.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="device"/> is null.</exception>
    public static string? PersonOf(AccessSnapshot? snapshot, DeviceRecord device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return device.Kind == FleetDeviceKinds.Node && snapshot?.Owner(FleetResourceKinds.Machine, device.Id) is { OwnerKind: FleetPrincipalKinds.Person } owner
            ? owner.OwnerId
            : device.OwnerPersonId;
    }

    /// <summary>Gets the person a device answers to; a controller device, or a server without an access store, reads its own record only.</summary>
    /// <param name="access">The access store; null reads the device record alone.</param>
    /// <param name="device">The device.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The person, or null for a device of no person.</returns>
    public static async ValueTask<string?> PersonOfAsync(IAccessStore? access, DeviceRecord device, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (access is null || device.Kind != FleetDeviceKinds.Node) return device.OwnerPersonId;
        return PersonOf(await access.ReadAccessAsync(device.OrganizationId, cancellationToken).ConfigureAwait(false), device);
    }

    /// <summary>Reads the access model once for a check of several devices; null without an access store.</summary>
    /// <param name="access">The access store; null for none.</param>
    /// <param name="organizationId">The organization.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The model, or null.</returns>
    public static async ValueTask<AccessSnapshot?> ReadAsync(IAccessStore? access, string organizationId, CancellationToken cancellationToken)
        => access is null ? null : await access.ReadAccessAsync(organizationId, cancellationToken).ConfigureAwait(false);
}
