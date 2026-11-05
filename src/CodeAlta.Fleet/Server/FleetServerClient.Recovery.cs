using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;

namespace CodeAlta.Fleet.Server;

/// <summary>The calls of the recovery key of a person (<see cref="FleetRecoveryKey"/>), and of a device that leaves the server.</summary>
public sealed partial class FleetServerClient
{
    /// <summary>
    /// Makes a recovery key the recovery key of this controller device's person: this device signs the entry that adds it
    /// to the device list, after the one the person had, which leaves the list first, and the server keeps the anchor of
    /// the list sealed with it. The person's controller devices then give it a copy of their keys.
    /// </summary>
    /// <param name="key">The recovery key.</param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    /// <returns>The recovery key as a device of the list.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is null.</exception>
    /// <exception cref="FleetDeviceListException">The device list does not verify, or this device is not a controller device in it: nothing was signed.</exception>
    /// <exception cref="FleetServerException">The server refused the key.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<DeviceDto> AddRecoveryKeyAsync(FleetRecoveryKey key, CancellationToken cancellationToken = default) => AddRecoveryKeyAsync(key, null, cancellationToken);

    /// <summary>
    /// Makes a recovery key the recovery key of this controller device's person as
    /// <see cref="AddRecoveryKeyAsync(FleetRecoveryKey, CancellationToken)"/> does, with the confirmation that the person
    /// signed in again from this device, which a person with a way to sign in needs (for the removal of the one they had too).
    /// </summary>
    /// <param name="key">The recovery key.</param>
    /// <param name="confirmation">The confirmation of a sign-in again (<c>purpose</c> <c>confirm</c>); null for none.</param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    /// <returns>The recovery key as a device of the list.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is null.</exception>
    /// <exception cref="FleetDeviceListException">The device list does not verify, or this device is not a controller device in it: nothing was signed.</exception>
    /// <exception cref="FleetServerException">The server refused the key (<c>sign_in.confirmation_required</c> without a confirmation it needs).</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<DeviceDto> AddRecoveryKeyAsync(FleetRecoveryKey key, string? confirmation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        using var recovery = key.CreateIdentity();
        var publicKey = Convert.ToBase64String(recovery.ExportSubjectPublicKeyInfo());
        var organization = await GetOrganizationAsync(cancellationToken).ConfigureAwait(false);
        var self = await GetDeviceAsync(null, cancellationToken).ConfigureAwait(false);
        for (var attempt = 0; ; attempt++)
        {
            var state = await ReadAnchoredDeviceListAsync(cancellationToken).ConfigureAwait(false);
            if (state.OrganizationId is { } named && !string.Equals(named, organization.Id, StringComparison.Ordinal))
                throw new FleetDeviceListException(state.Sequence, "the server names another organization than its device list");
            if (!state.IsController(DeviceId)) throw new FleetDeviceListException(state.Sequence, "this device is not a controller device of the list, so it signs no entry");
            var owner = state.Members[DeviceId].OwnerId ?? self.OwnerId ?? throw new FleetDeviceListException(state.Sequence, "this device has no person to make a recovery key for");
            // One recovery key per person: the one kept before stops working.
            if (state.Members.Values.FirstOrDefault(member => member.Kind == FleetDeviceKinds.Recovery && member.OwnerId == owner) is { } old)
            {
                if (old.DeviceId == recovery.DeviceId) return await GetDeviceAsync(old.DeviceId, cancellationToken).ConfigureAwait(false);
                await ChangeListAsync(old.DeviceId, "revoke",
                    static (_, _, _) => Task.FromResult<(string, string?, string?, string?)>((FleetDeviceListOperations.Remove, null, null, null)), cancellationToken, confirmation)
                    .ConfigureAwait(false);
                continue;
            }

            var entry = Sign(new FleetDeviceListEntryPayload(FleetDeviceListEntryPayload.CurrentVersion, organization.Id, state.Sequence + 1, state.LastHash,
                FleetDeviceListOperations.Add, recovery.DeviceId, publicKey, FleetDeviceKinds.Recovery, owner, DeviceId, Now()));
            if (!FleetDeviceListVerifier.TryAppend(state, entry, organization.Id, out _, out var reason))
                throw new FleetDeviceListException(state.Sequence + 1, $"the entry this device would sign does not hold ({reason})");
            try
            {
                return await SendAsync(HttpMethod.Post, "api/v1/recovery-keys",
                    Body(new CreateRecoveryKeyRequest(publicKey, entry, key.SealAnchor(organization.Id, state.FirstHash!), confirmation), ServerApiJsonContext.Default.CreateRecoveryKeyRequest),
                    ServerApiJsonContext.Default.DeviceDto, cancellationToken).ConfigureAwait(false);
            }
            catch (FleetServerException exception) when (exception.Code == "device_list.conflict" && attempt == 0)
            {
                // Another controller appended first: the entry is signed again after the new last one.
            }
        }
    }

    /// <summary>
    /// Leaves the server: it withdraws what this device published there (facts and sealed metadata, statistics, inventory,
    /// run reports), its copies of keys and the automations given to it, and refuses it from then on. A controller device
    /// that is not the last one of the list signs its own removal too, so that the keys it held get new epochs; a device
    /// whose list does not verify leaves without signing anything.
    /// </summary>
    /// <param name="cancellationToken">Cancels the calls.</param>
    /// <returns>What was withdrawn.</returns>
    /// <exception cref="FleetServerException">The server refused the departure.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<LeaveResponse> LeaveAsync(CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            FleetSignedDeviceListEntry? entry = null;
            try
            {
                var state = await ReadAnchoredDeviceListAsync(cancellationToken).ConfigureAwait(false);
                if (state.IsController(DeviceId) && state.OrganizationId is { } organization
                    && state.Members.Values.Count(static member => member.Kind == FleetDeviceKinds.Controller) > 1)
                {
                    entry = Sign(new FleetDeviceListEntryPayload(FleetDeviceListEntryPayload.CurrentVersion, organization, state.Sequence + 1, state.LastHash,
                        FleetDeviceListOperations.Remove, DeviceId, null, null, null, DeviceId, Now()));
                    if (!FleetDeviceListVerifier.TryAppend(state, entry, organization, out _, out _)) entry = null;
                }
            }
            catch (FleetDeviceListException)
            {
                // Nothing is signed after a list that does not verify: the device leaves without its removal.
            }

            try
            {
                return await SendAsync(HttpMethod.Post, "api/v1/devices/self/leave", Body(new LeaveRequest(entry), ServerApiJsonContext.Default.LeaveRequest),
                    ServerApiJsonContext.Default.LeaveResponse, cancellationToken).ConfigureAwait(false);
            }
            catch (FleetServerException exception) when (exception.Code == "device_list.conflict" && attempt == 0)
            {
                // Another controller appended first: the removal is signed again after the new last one.
            }
        }
    }

    /// <summary>Reads the anchor of the device list this recovery key sealed; the client is the recovery key's.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The sealed anchor.</returns>
    /// <exception cref="FleetServerException">The server knows no such recovery key (wrong words, or a key replaced since).</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<string> GetRecoveryAnchorAsync(CancellationToken cancellationToken = default)
        => (await SendAsync(HttpMethod.Get, "api/v1/recovery-keys/self", null, ServerApiJsonContext.Default.RecoveryAnchorDto, cancellationToken).ConfigureAwait(false)).SealedAnchor;

    // The entry the recovery key (the identity of this client) signs to add a new controller device of its person, after a verified list.
    internal FleetSignedDeviceListEntry SignRecoveredDevice(FleetDeviceListState state, string organizationId, string owner, FleetDevicePublicKey device)
    {
        var entry = Sign(new FleetDeviceListEntryPayload(FleetDeviceListEntryPayload.CurrentVersion, organizationId, state.Sequence + 1, state.LastHash,
            FleetDeviceListOperations.Add, device.DeviceId, device.Text, FleetDeviceKinds.Controller, owner, DeviceId, Now()));
        if (!FleetDeviceListVerifier.TryAppend(state, entry, organizationId, out _, out var reason))
            throw new FleetDeviceListException(state.Sequence + 1, $"the entry the recovery key would sign does not hold ({reason})");
        return entry;
    }

    // Sends the entry the recovery key signed, as the new device it adds.
    internal Task<DeviceDto> RecoverAsync(FleetSignedDeviceListEntry entry, string deviceName, CancellationToken cancellationToken)
        => SendAsync(HttpMethod.Post, "api/v1/recover", Body(new RecoverRequest(PublicKey, deviceName, entry), ServerApiJsonContext.Default.RecoverRequest),
            ServerApiJsonContext.Default.DeviceDto, cancellationToken);
}
