using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Server.Api;
using CodeAlta.Server.Hosting;
using CodeAlta.Server.Identity;
using CodeAlta.Server.Storage;
using Microsoft.AspNetCore.Http;

namespace CodeAlta.Server.Services;

/// <summary>
/// The setup of a server (<c>doc/fleet/identity.md</c>, setting up the owner): with the one-time code, from this machine
/// only, while no owner exists, a device becomes the first controller device of a new organization and its person the
/// owner. "First request wins" never applies to an exposed port.
/// </summary>
public sealed class SetupService
{
    private const int MaxNameLength = 128;

    private readonly ICoordinatorStore _store;
    private readonly SetupCodeStore _codes;
    private readonly RequestAuthenticator _authenticator;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Initializes a new instance of the <see cref="SetupService"/> class.</summary>
    /// <param name="store">The store.</param>
    /// <param name="codes">The setup code.</param>
    /// <param name="authenticator">Checks the signature of the request with the key it names.</param>
    /// <param name="time">The clock.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public SetupService(ICoordinatorStore store, SetupCodeStore codes, RequestAuthenticator authenticator, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(codes);
        ArgumentNullException.ThrowIfNull(authenticator);
        ArgumentNullException.ThrowIfNull(time);
        _store = store;
        _codes = codes;
        _authenticator = authenticator;
        _time = time;
    }

    /// <summary>Sets the server up.</summary>
    /// <param name="request">The body of the request.</param>
    /// <param name="signed">The signature of the request.</param>
    /// <param name="origin">Where it comes from.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The organization, its owner and their device.</returns>
    /// <exception cref="CoordinatorException">The server has an owner, the request is not local, the code is wrong or expired, or the request or its first entry does not verify.</exception>
    public async Task<SetupResponse> SetUpAsync(SetupRequest? request, SignedRequestData signed, RequestOrigin origin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(signed);
        ArgumentNullException.ThrowIfNull(origin);
        // One setup at a time: two requests with the one code cannot both pass the check before the code is consumed.
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await _store.GetOrganizationAsync(cancellationToken).ConfigureAwait(false) is not null)
            {
                throw CoordinatorException.Conflict(CoordinatorErrorCodes.AlreadySetUp, "The server already has an owner.");
            }

            if (!origin.IsLoopback)
            {
                throw CoordinatorException.Forbidden(CoordinatorErrorCodes.SetupNotLocal,
                    "Set the server up from its own machine (the loopback address) until it has an owner.");
            }

            if (request is null) throw CoordinatorException.BadRequest(CoordinatorErrorCodes.InvalidBody, "The request needs a body.");
            switch (_codes.Check(request.Code))
            {
                case SetupCodeCheck.Invalid:
                    await _store.AppendAuditAsync(origin.Audit("server.setup", null, null, CoordinatorErrorCodes.SetupCodeInvalid), cancellationToken).ConfigureAwait(false);
                    throw CoordinatorException.Unauthenticated(CoordinatorErrorCodes.SetupCodeInvalid, "The setup code is not the one the server waits for: altaserver setup-code prints it.");
                case SetupCodeCheck.Expired:
                    throw new CoordinatorException(StatusCodes.Status410Gone, CoordinatorErrorCodes.SetupCodeExpired,
                        "The setup code expired: altaserver setup-code makes a new one.");
            }

            var ownerName = RequireName(request.OwnerName, "ownerName");
            var deviceName = RequireName(request.DeviceName, "deviceName");
            var organizationName = string.IsNullOrWhiteSpace(request.OrganizationName) ? ownerName : RequireName(request.OrganizationName, "organizationName");
            if (!FleetDevicePublicKey.TryParse(request.PublicKey, out var key))
            {
                throw CoordinatorException.BadRequest(CoordinatorErrorCodes.InvalidBody, "publicKey must be the base64 of the SubjectPublicKeyInfo of a P-256 key.");
            }

            _authenticator.VerifyWithKey(signed, key);

            var payload = DeviceListChain.ReadSigned(request.DeviceListEntry, key);
            DeviceListChain.Require(payload.Sequence == 1 && payload.Previous is null, "be the first one (seq 1, no prev)");
            DeviceListChain.Require(payload.Operation == FleetDeviceListOperations.Add, "add the device");
            DeviceListChain.Require(payload.DeviceId == key.DeviceId && payload.SignerDeviceId == key.DeviceId, "name the device as both the device and the signer");
            DeviceListChain.Require(payload.PublicKey == key.Text, "carry the public key of the device");
            DeviceListChain.Require(payload.Kind == FleetDeviceKinds.Controller, "add a controller device");

            var now = _time.GetUtcNow();
            var organization = new OrganizationRecord(NewId(), organizationName, now);
            var owner = new PersonRecord(NewId(), organization.Id, ownerName, PersonRoles.Owner, now);
            var device = new DeviceRecord(key.DeviceId, organization.Id, deviceName, FleetDeviceKinds.Controller, key.Text, key.Fingerprint, owner.Id,
                DeviceStatuses.Approved, key.DeviceId, null, now, now, null, 1);
            var signedEntry = request.DeviceListEntry;
            var entry = new DeviceListEntryRecord(organization.Id, 1, device.Id, FleetDeviceListOperations.Add, device.Id, signedEntry.Payload, signedEntry.Signature,
                FleetDeviceListEntries.Hash(signedEntry), now);
            var audit = new AuditEntry("server.setup", AuditEntry.Success, owner.Id, device.Id, organization.Id, origin.SourceAddress, origin.RequestId);
            if (!await _store.CreateOrganizationAsync(organization, owner, device, entry, audit, cancellationToken).ConfigureAwait(false))
            {
                throw CoordinatorException.Conflict(CoordinatorErrorCodes.AlreadySetUp, "The server already has an owner.");
            }

            _codes.Consume();
            return new SetupResponse(new OrganizationDto(organization.Id, organization.Name, organization.CreatedAt, [owner.ToDto()]), owner.ToDto(), device.ToDto(false));
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static string NewId() => Guid.CreateVersion7().ToString("N");

    internal static string RequireName(string? value, string field)
    {
        var name = value?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength || name.Any(char.IsControl))
        {
            throw CoordinatorException.BadRequest(CoordinatorErrorCodes.InvalidBody, $"{field} must be a name of 1 to {MaxNameLength} characters.");
        }

        return name;
    }
}
