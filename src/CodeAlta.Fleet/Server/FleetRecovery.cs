using System.Net;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;

namespace CodeAlta.Fleet.Server;

/// <summary>What a recovery did.</summary>
/// <param name="OrganizationId">The organization of the server.</param>
/// <param name="OrganizationName">Its name.</param>
/// <param name="PersonId">The person whose recovery key it was, whose controller device this device is now.</param>
/// <param name="KeysGiven">How many keys the recovery key gave this device.</param>
public sealed record FleetRecoveryResult(string OrganizationId, string OrganizationName, string PersonId, int KeysGiven);

/// <summary>A recovery that cannot be done: words that are not a recovery key of the server, or an anchor the recovery key did not seal.</summary>
public sealed class FleetRecoveryException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="FleetRecoveryException"/> class.</summary>
    /// <param name="message">What went wrong, for people.</param>
    /// <param name="inner">The cause, or null.</param>
    public FleetRecoveryException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// The recovery of a person's devices with their recovery key (<c>doc/fleet/security-and-privacy.md</c>, keys), on a new
/// device, when every device of the person is lost: the recovery key reads the anchor of the device list it sealed and
/// pins the list by it, signs this device in as a controller device of its person, and gives it the copies of the keys
/// it holds, with the grants they were given. Everything is decrypted on the device; the server sees ciphertext only.
/// </summary>
/// <remarks>The devices that were lost stay in the list until this device removes them, which gives the keys new epochs.</remarks>
public static class FleetRecovery
{
    /// <summary>Signs this device in with a recovery key and gives it the keys of its person.</summary>
    /// <param name="server">The address of the server.</param>
    /// <param name="key">The recovery key, from the words the person kept.</param>
    /// <param name="device">The key of this device.</param>
    /// <param name="deviceName">The name of this device.</param>
    /// <param name="pinPath">The file of the anchor of the device list in the folder of the link this device makes.</param>
    /// <param name="handler">The HTTP handler, for tests; null for the usual one.</param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    /// <returns>The organization, the person, and how many keys this device got.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="FleetRecoveryException">The words are not the recovery key of a person of this server, or the server gave an anchor the key did not seal.</exception>
    /// <exception cref="FleetDeviceListException">The device list does not start with the anchor or does not verify: nothing was signed.</exception>
    /// <exception cref="FleetServerException">The server refused a call.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public static Task<FleetRecoveryResult> RecoverAsync(Uri server, FleetRecoveryKey key, DeviceIdentity device, string deviceName, string pinPath,
        HttpMessageHandler? handler, CancellationToken cancellationToken)
        => RecoverAsync(server, key, device, deviceName, pinPath, handler, null, cancellationToken);

    /// <summary>Signs this device in with a recovery key at a server that may pin its own certificate, and gives it the keys of its person.</summary>
    /// <param name="server">The address of the server.</param>
    /// <param name="key">The recovery key, from the words the person kept.</param>
    /// <param name="device">The key of this device.</param>
    /// <param name="deviceName">The name of this device.</param>
    /// <param name="pinPath">The file of the anchor of the device list in the folder of the link this device makes.</param>
    /// <param name="handler">The HTTP handler, for tests; null for the usual one.</param>
    /// <param name="certificatePin">The pin of the server's own certificate (<see cref="FleetServerCertificates"/>); null to validate it as usual.</param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    /// <returns>The organization, the person, and how many keys this device got.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="FleetRecoveryException">The words are not the recovery key of a person of this server, or the server gave an anchor the key did not seal.</exception>
    /// <exception cref="FleetDeviceListException">The device list does not start with the anchor or does not verify: nothing was signed.</exception>
    /// <exception cref="FleetServerException">The server refused a call.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public static async Task<FleetRecoveryResult> RecoverAsync(Uri server, FleetRecoveryKey key, DeviceIdentity device, string deviceName, string pinPath,
        HttpMessageHandler? handler, string? certificatePin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(pinPath);
        using var recovery = key.CreateIdentity();
        using var asRecovery = new FleetServerClient(server, recovery, handler, null, certificatePin);
        string sealedAnchor;
        try
        {
            sealedAnchor = await asRecovery.GetRecoveryAnchorAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (FleetServerException exception) when (exception.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            throw new FleetRecoveryException("These words are not the recovery key of a person of this server.", exception);
        }

        // The anchor only the recovery key opens: the list must start with the entry the person's first device signed.
        if (!key.TryOpenAnchor(sealedAnchor, out var organizationId, out var root))
            throw new FleetRecoveryException("The server gave an anchor of the device list this recovery key did not seal.");
        FleetOrganizationDevices.Anchor(pinPath, root!, organizationId!);
        var devices = new FleetOrganizationDevices(server, recovery, pinPath, handler) { CertificatePin = certificatePin };
        var list = await devices.RefreshAsync(cancellationToken).ConfigureAwait(false);
        if (list.OrganizationId != organizationId || !list.Members.TryGetValue(recovery.DeviceId, out var member) || member.Kind != FleetDeviceKinds.Recovery
            || member.OwnerId is not { } person)
        {
            throw new FleetRecoveryException("This recovery key is not in the device list of the server any more: a newer one replaced it.");
        }

        if (!FleetDevicePublicKey.TryParse(Convert.ToBase64String(device.ExportSubjectPublicKeyInfo()), out var devicePublic))
            throw new InvalidOperationException("The key of this device does not read.");
        using var asDevice = new FleetServerClient(server, device, handler, null, certificatePin);
        for (var attempt = 0; !list.Members.ContainsKey(device.DeviceId); attempt++)
        {
            var entry = asRecovery.SignRecoveredDevice(list, organizationId!, person, devicePublic);
            try
            {
                await asDevice.RecoverAsync(entry, deviceName, cancellationToken).ConfigureAwait(false);
            }
            catch (FleetServerException exception) when (exception.Code == "device_list.conflict" && attempt == 0)
            {
                // Another entry came first: signed again after it.
            }

            list = await devices.RefreshAsync(cancellationToken).ConfigureAwait(false);
            if (attempt > 0 && !list.Members.ContainsKey(device.DeviceId)) throw new FleetDeviceListException(list.Sequence, "the server did not keep the entry that adds this device");
        }

        // The copies the recovery key holds go to this device with the grants they were given: a key a removed device
        // may hold stays stale, and this device makes its next epoch.
        var deviceKey = list.Members[device.DeviceId].Key;
        var given = 0;
        foreach (var row in await asRecovery.ListMetadataKeysAsync(cancellationToken).ConfigureAwait(false))
        {
            if (row.Wrapped is not { } wrapped || row.WrappedTo.Contains(device.DeviceId)) continue;
            FleetMetadataKey opened;
            try
            {
                opened = FleetKeyWrap.Unwrap(wrapped, row.Id, row.Epoch, row.Space, recovery, list);
            }
            catch (InvalidDataException)
            {
                continue;
            }

            await asRecovery.AddMetadataKeyWrapsAsync(row.Id, [new MetadataKeyWrapDto(device.DeviceId, FleetKeyWrap.Wrap(opened, deviceKey, opened.Grant!, recovery))],
                cancellationToken).ConfigureAwait(false);
            given++;
        }

        var organization = await asDevice.GetOrganizationAsync(cancellationToken).ConfigureAwait(false);
        return new FleetRecoveryResult(organizationId!, organization.Name, person, given);
    }
}
