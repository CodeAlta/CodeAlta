using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Server.Api;
using CodeAlta.Server.Control;
using CodeAlta.Server.Identity;
using CodeAlta.Server.Storage;

namespace CodeAlta.Server.Services;

/// <summary>
/// The devices of the organization (<c>doc/fleet/identity.md</c>, enrolling nodes): enrollment tokens, the pending
/// devices they let register, their approval by a controller device that signs them into the device list, and
/// revocation. The server signs nothing: what makes a device a member is an entry signed by a controller device.
/// </summary>
public sealed class DeviceService
{
    /// <summary>The shortest life of an enrollment token.</summary>
    public static readonly TimeSpan MinTokenLifetime = TimeSpan.FromMinutes(10);

    /// <summary>The longest life of an enrollment token.</summary>
    public static readonly TimeSpan MaxTokenLifetime = TimeSpan.FromHours(24);

    /// <summary>The life of an enrollment token when none is asked.</summary>
    public static readonly TimeSpan DefaultTokenLifetime = TimeSpan.FromHours(1);

    private const string TokenPrefix = "aet_";

    private readonly ICoordinatorStore _store;
    private readonly RequestAuthenticator _authenticator;
    private readonly PresenceRegistry _presence;
    private readonly TimeProvider _time;

    /// <summary>Initializes a new instance of the <see cref="DeviceService"/> class.</summary>
    /// <param name="store">The store.</param>
    /// <param name="authenticator">Checks the signature of an enrollment with the key it names.</param>
    /// <param name="presence">The devices online, to disconnect a revoked one.</param>
    /// <param name="time">The clock.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public DeviceService(ICoordinatorStore store, RequestAuthenticator authenticator, PresenceRegistry presence, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(authenticator);
        ArgumentNullException.ThrowIfNull(presence);
        ArgumentNullException.ThrowIfNull(time);
        _store = store;
        _authenticator = authenticator;
        _presence = presence;
        _time = time;
    }

    /// <summary>Makes an enrollment token; only a controller device may.</summary>
    /// <param name="actor">The calling device.</param>
    /// <param name="request">The request.</param>
    /// <param name="origin">Where it comes from.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The token, whose value is shown once.</returns>
    /// <exception cref="CoordinatorException">The device is not a controller, or the lifetime is out of bounds.</exception>
    public async Task<EnrollmentTokenResponse> CreateEnrollmentTokenAsync(DeviceRecord actor, EnrollmentTokenRequest? request, RequestOrigin origin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        RequireController(actor);
        var lifetime = request?.TtlSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : DefaultTokenLifetime;
        if (lifetime < MinTokenLifetime || lifetime > MaxTokenLifetime)
        {
            throw CoordinatorException.BadRequest(CoordinatorErrorCodes.InvalidBody,
                $"ttlSeconds must be from {MinTokenLifetime.TotalSeconds:0} to {MaxTokenLifetime.TotalSeconds:0}.");
        }

        var label = request?.Label is null ? null : SetupService.RequireName(request.Label, "label");
        Span<byte> secret = stackalloc byte[32];
        RandomNumberGenerator.Fill(secret);
        var token = TokenPrefix + Base64Url.EncodeToString(secret);
        var now = _time.GetUtcNow();
        var record = new EnrollmentTokenRecord(SetupService.NewId(), actor.OrganizationId, HashToken(token), label, actor.OwnerPersonId, actor.Id, now, now + lifetime, null, null);
        await _store.CreateEnrollmentTokenAsync(record, origin.Audit("enrollment_token.create", actor, record.Id), cancellationToken).ConfigureAwait(false);
        return new EnrollmentTokenResponse(record.Id, token, record.ExpiresAt);
    }

    /// <summary>Registers a machine as a pending device with an enrollment token; the request is signed by the machine's key.</summary>
    /// <param name="request">The request.</param>
    /// <param name="signed">The signature of the request.</param>
    /// <param name="origin">Where it comes from.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The pending device.</returns>
    /// <exception cref="CoordinatorException">The request does not verify, the token is unknown, used or expired, or the device exists.</exception>
    public async Task<EnrollResponse> EnrollAsync(EnrollRequest? request, SignedRequestData signed, RequestOrigin origin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(signed);
        ArgumentNullException.ThrowIfNull(origin);
        if (request is null || string.IsNullOrEmpty(request.Token) || request.Token.Length > 128)
        {
            throw CoordinatorException.BadRequest(CoordinatorErrorCodes.InvalidBody, "The request needs an enrollment token, a public key and a device name.");
        }

        var name = SetupService.RequireName(request.DeviceName, "deviceName");
        if (!DevicePublicKey.TryParse(request.PublicKey, out var key))
        {
            throw CoordinatorException.BadRequest(CoordinatorErrorCodes.InvalidBody, "publicKey must be the base64 of the SubjectPublicKeyInfo of a P-256 key.");
        }

        _authenticator.VerifyWithKey(signed, key);
        var now = _time.GetUtcNow();
        // Organization, owner and enroller come from the token, in the store's transaction.
        var device = new DeviceRecord(key.DeviceId, string.Empty, name, FleetDeviceKinds.Node, key.Text, key.Fingerprint, null, DeviceStatuses.Pending, null, null, now, null, null, 1);
        var audit = new AuditEntry("device.enroll", AuditEntry.Success, null, key.DeviceId, key.DeviceId, origin.SourceAddress, origin.RequestId);
        var (outcome, enrolled) = await _store.EnrollDeviceAsync(HashToken(request.Token), device, now, audit, cancellationToken).ConfigureAwait(false);
        if (outcome != EnrollmentOutcome.Enrolled)
        {
            var (code, message) = outcome switch
            {
                EnrollmentOutcome.TokenUsed => (CoordinatorErrorCodes.EnrollmentTokenUsed, "The enrollment token was already used: ask for a new one."),
                EnrollmentOutcome.TokenExpired => (CoordinatorErrorCodes.EnrollmentTokenExpired, "The enrollment token expired: ask for a new one."),
                EnrollmentOutcome.DeviceExists => (CoordinatorErrorCodes.DeviceExists, "A device with this key is already registered."),
                _ => (CoordinatorErrorCodes.EnrollmentTokenInvalid, "The enrollment token is not known to this server."),
            };
            await _store.AppendAuditAsync(audit with { Result = code }, cancellationToken).ConfigureAwait(false);
            throw outcome == EnrollmentOutcome.DeviceExists ? CoordinatorException.Conflict(code, message) : CoordinatorException.Unauthenticated(code, message);
        }

        return new EnrollResponse(enrolled!.ToDto(false));
    }

    /// <summary>
    /// Approves a pending device: the calling controller device signed the entry that adds it to the device list, which
    /// must follow the last entry and say exactly what the server holds of the device.
    /// </summary>
    /// <param name="actor">The calling device.</param>
    /// <param name="deviceId">The device to approve.</param>
    /// <param name="request">The request, with the signed entry.</param>
    /// <param name="origin">Where it comes from.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The approved device.</returns>
    /// <exception cref="CoordinatorException">The caller is not a controller, the device is unknown or not pending, or the entry is wrong, badly signed or behind the list.</exception>
    public async Task<DeviceDto> ApproveAsync(DeviceRecord actor, string deviceId, DeviceListChangeRequest? request, RequestOrigin origin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        RequireController(actor);
        var device = await RequireDeviceAsync(actor, deviceId, cancellationToken).ConfigureAwait(false);
        if (device.Status != DeviceStatuses.Pending) throw CoordinatorException.Conflict(CoordinatorErrorCodes.DeviceState, $"The device is {device.Status}, not pending.");
        var (payload, record) = await ReadEntryAsync(actor, request?.DeviceListEntry, cancellationToken).ConfigureAwait(false);
        DeviceListChain.Require(payload.Operation == FleetDeviceListOperations.Add, "add the device");
        DeviceListChain.Require(payload.DeviceId == device.Id && payload.PublicKey == device.PublicKey, "name the device and its key as the server holds them");
        DeviceListChain.Require(payload.Kind == device.Kind, $"add the device as a {device.Kind}");
        // A controller signs the devices of its own person: a personal machine is its owner's (team machines and their signers come later).
        DeviceListChain.Require(payload.OwnerId is not null && payload.OwnerId == actor.OwnerPersonId, "give the device to the person of the signing device");

        var audit = origin.Audit("device.approve", actor, device.Id, details: $"{{\"seq\":{payload.Sequence}}}");
        var (outcome, approved) = await _store.AppendDeviceListEntryAsync(record, DeviceStatuses.Pending, DeviceStatuses.Approved, payload.OwnerId, audit, cancellationToken)
            .ConfigureAwait(false);
        ThrowIfNotAppended(outcome, approved);
        return approved!.ToDto(_presence.IsOnline(approved!.Id));
    }

    /// <summary>
    /// Revokes a device: an approved device leaves the device list by an entry the calling controller device signed; a
    /// pending one is refused without an entry. Its control connection is closed.
    /// </summary>
    /// <param name="actor">The calling device.</param>
    /// <param name="deviceId">The device to revoke.</param>
    /// <param name="request">The request, with the signed entry for an approved device.</param>
    /// <param name="origin">Where it comes from.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The revoked device.</returns>
    /// <exception cref="CoordinatorException">The caller is not a controller, the device is unknown, revoked or the last controller, or the entry is wrong.</exception>
    public async Task<DeviceDto> RevokeAsync(DeviceRecord actor, string deviceId, DeviceListChangeRequest? request, RequestOrigin origin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        RequireController(actor);
        var device = await RequireDeviceAsync(actor, deviceId, cancellationToken).ConfigureAwait(false);
        DeviceRecord revoked;
        switch (device.Status)
        {
            case DeviceStatuses.Pending:
                revoked = await _store.RefusePendingDeviceAsync(device.Id, _time.GetUtcNow(), origin.Audit("device.refuse", actor, device.Id), cancellationToken).ConfigureAwait(false)
                    ?? throw CoordinatorException.Conflict(CoordinatorErrorCodes.DeviceState, "The device is no longer pending.");
                break;
            case DeviceStatuses.Approved:
                if (device.Kind == FleetDeviceKinds.Controller)
                {
                    var devices = await _store.ListDevicesAsync(actor.OrganizationId, cancellationToken).ConfigureAwait(false);
                    if (devices.Count(static other => other.Kind == FleetDeviceKinds.Controller && other.Status == DeviceStatuses.Approved) <= 1)
                    {
                        throw CoordinatorException.Conflict(CoordinatorErrorCodes.LastController, "The last controller device cannot be revoked: approve another one first.");
                    }
                }

                var (payload, record) = await ReadEntryAsync(actor, request?.DeviceListEntry, cancellationToken).ConfigureAwait(false);
                DeviceListChain.Require(payload.Operation == FleetDeviceListOperations.Remove && payload.DeviceId == device.Id, "remove the device");
                var audit = origin.Audit("device.revoke", actor, device.Id, details: $"{{\"seq\":{payload.Sequence}}}");
                var (outcome, changed) = await _store.AppendDeviceListEntryAsync(record, DeviceStatuses.Approved, DeviceStatuses.Revoked, null, audit, cancellationToken)
                    .ConfigureAwait(false);
                ThrowIfNotAppended(outcome, changed);
                revoked = changed!;
                break;
            default:
                throw CoordinatorException.Conflict(CoordinatorErrorCodes.DeviceState, "The device is already revoked.");
        }

        await _presence.DisconnectAsync(revoked.Id, new FleetError(FleetErrorCodes.Revoked, null, "The device was revoked.")).ConfigureAwait(false);
        return revoked.ToDto(false);
    }

    internal static string HashToken(string token) => Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private async Task<(FleetDeviceListEntryPayload Payload, DeviceListEntryRecord Record)> ReadEntryAsync(DeviceRecord actor, FleetSignedDeviceListEntry? entry,
        CancellationToken cancellationToken)
    {
        if (!DevicePublicKey.TryParse(actor.PublicKey, out var key)) throw new InvalidOperationException("The key of a device in the table does not read.");
        var payload = DeviceListChain.ReadSigned(entry, key);
        DeviceListChain.Require(payload.SignerDeviceId == actor.Id, "be signed by the device that sends it");
        DeviceListChain.Require(payload.OrganizationId == actor.OrganizationId, "name the organization of the server");
        var last = await _store.GetLastDeviceListEntryAsync(actor.OrganizationId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("An organization without a device list.");
        if (payload.Sequence != last.Sequence + 1 || !string.Equals(payload.Previous, last.Hash, StringComparison.Ordinal))
        {
            throw CoordinatorException.Conflict(CoordinatorErrorCodes.EntryConflict,
                $"The entry must follow the last entry of the device list (seq {last.Sequence + 1}, prev {last.Hash}): read the list again.");
        }

        return (payload, new DeviceListEntryRecord(actor.OrganizationId, payload.Sequence, payload.DeviceId, payload.Operation, actor.Id, entry!.Payload, entry.Signature,
            FleetDeviceListEntries.Hash(entry), _time.GetUtcNow()));
    }

    private async Task<DeviceRecord> RequireDeviceAsync(DeviceRecord actor, string deviceId, CancellationToken cancellationToken)
    {
        var device = string.IsNullOrEmpty(deviceId) ? null : await _store.GetDeviceAsync(deviceId, cancellationToken).ConfigureAwait(false);
        if (device is null || device.OrganizationId != actor.OrganizationId) throw CoordinatorException.NotFound(CoordinatorErrorCodes.DeviceNotFound, "No device has this id.");
        return device;
    }

    private static void ThrowIfNotAppended(DeviceListAppendOutcome outcome, DeviceRecord? device)
    {
        switch (outcome)
        {
            case DeviceListAppendOutcome.Conflict:
                throw CoordinatorException.Conflict(CoordinatorErrorCodes.EntryConflict, "The device list moved on: read it again and sign an entry that follows its last one.");
            case DeviceListAppendOutcome.DeviceStateChanged:
                throw CoordinatorException.Conflict(CoordinatorErrorCodes.DeviceState, $"The device is {device?.Status ?? "gone"} now.");
        }
    }

    private static void RequireController(DeviceRecord actor)
    {
        if (actor.Kind != FleetDeviceKinds.Controller)
        {
            throw CoordinatorException.Forbidden(FleetErrorCodes.Forbidden, "Only a controller device (a person's desktop or phone) may do this; a node acts only as itself.");
        }
    }
}
