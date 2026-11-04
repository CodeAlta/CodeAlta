using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Server.Control;
using CodeAlta.Server.Storage;

namespace CodeAlta.Server.Services;

/// <summary>
/// The secrets, variables and environments of people (<c>doc/fleet/secrets.md</c>), as GitHub Actions manages those of a
/// repository: values are write-only (set, rotate, delete, list names), sealed end to end by a controller device of their
/// person for the devices that use them; the server keeps the copies as opaque text and gives each device its own. It
/// never holds a value, and cannot give one to a machine: a copy it made carries no grant a controller device signed,
/// which the machine checks before it opens anything.
/// </summary>
/// <remarks>
/// The server checks what its tables say (approved devices of the same person, versions in order, signatures of the
/// environments and approvals by the key it records); the machines check the same against the device list they anchored,
/// which is what protects them from a server that lies.
/// </remarks>
internal sealed class FleetSecretService
{
    /// <summary>The most copies in one request.</summary>
    public const int MaxCopies = 64;

    /// <summary>The longest sealed copy, in characters.</summary>
    public const int MaxCopyLength = 128 * 1024;

    /// <summary>The most uses in one publication.</summary>
    public const int MaxUses = 500;

    /// <summary>The most approvals or uses listed.</summary>
    public const int MaxListed = 200;

    private readonly ICoordinatorStore _store;
    private readonly IFleetSecretStore _secrets;
    private readonly TimeProvider _time;

    /// <summary>Initializes a new instance of the <see cref="FleetSecretService"/> class.</summary>
    /// <param name="store">The store of the organization.</param>
    /// <param name="secrets">The store of the values.</param>
    /// <param name="time">The clock.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public FleetSecretService(ICoordinatorStore store, IFleetSecretStore secrets, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(time);
        _store = store;
        _secrets = secrets;
        _time = time;
    }

    /// <summary>Gets the devices online, which are told when their values change (<see cref="FleetSecretMethods.Changed"/>); null tells none.</summary>
    public PresenceRegistry? Presence { get; init; }

    /// <summary>Lists the values of the calling controller device's person: what is known of each, with the copy sealed for the caller.</summary>
    /// <param name="actor">The calling device.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The values.</returns>
    /// <exception cref="CoordinatorException">The caller is not a controller device of a person.</exception>
    public async Task<FleetSecretListDto> ListAsync(DeviceRecord actor, CancellationToken cancellationToken)
    {
        RequireController(actor);
        var entries = await _secrets.ListFleetSecretsAsync(actor.OrganizationId, actor.OwnerPersonId!, cancellationToken).ConfigureAwait(false);
        var revoked = await RevokedAsync(actor, cancellationToken).ConfigureAwait(false);
        return new FleetSecretListDto([.. entries.Select(entry => ToDto(entry, actor.Id, revoked, holderCopies: true))]);
    }

    /// <summary>Lists the values the calling device holds a copy of the current version of, each with its copy only.</summary>
    /// <param name="actor">The calling device.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The values.</returns>
    public async Task<FleetSecretListDto> ListHeldAsync(DeviceRecord actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        // The authentication refuses a revoked device already: its copies are never given again.
        if (actor.Status != DeviceStatuses.Approved || actor.OwnerPersonId is null) return new FleetSecretListDto([]);
        var entries = await _secrets.ListHeldFleetSecretsAsync(actor.OrganizationId, actor.Id, cancellationToken).ConfigureAwait(false);
        return new FleetSecretListDto([.. entries.Where(entry => entry.Secret.OwnerPersonId == actor.OwnerPersonId)
            .Select(entry => ToDto(entry, actor.Id, ImmutableEmpty))]);
    }

    /// <summary>Sets a value (version 1) or rotates it (the next version), with the copies the calling controller device sealed.</summary>
    /// <param name="actor">The calling device.</param>
    /// <param name="id">The id of the value.</param>
    /// <param name="request">The value as sealed.</param>
    /// <param name="origin">Where the request comes from.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The value as the caller sees it.</returns>
    /// <exception cref="CoordinatorException">The caller is not a controller device, the request does not hold, the name is taken, or the version is not the next one.</exception>
    public async Task<FleetSecretDto> PutAsync(DeviceRecord actor, string id, SetFleetSecretRequest? request, RequestOrigin origin, CancellationToken cancellationToken)
    {
        RequireController(actor);
        ArgumentNullException.ThrowIfNull(origin);
        if (!IsId(id) || request is null) throw Invalid("The request needs the id of the value (base64url, at most 64 characters) and the value as sealed.");
        if (!FleetSecretKinds.IsKnown(request.Kind)) throw Invalid("A value is a secret or a variable.");
        if (!IsName(request.Name)) throw Invalid("The name of a value is the name of an environment variable: letters, digits and _, not starting with a digit, at most 128.");
        if (request.Version < 1) throw Invalid("A value has a version from 1.");
        var scope = await CheckScopeAsync(actor, request.Scope, cancellationToken).ConfigureAwait(false);
        var now = _time.GetUtcNow();
        var copies = await CheckCopiesAsync(actor, id, request.Version, request.Copies, cancellationToken).ConfigureAwait(false);
        if (!copies.Any(copy => copy.DeviceId == actor.Id)) throw Invalid("A value is sealed for the device that sets it too.");
        var held = await _secrets.GetFleetSecretAsync(actor.OrganizationId, id, cancellationToken).ConfigureAwait(false);
        if (held is not null && held.Secret.OwnerPersonId != actor.OwnerPersonId) throw NotFound();
        var record = new FleetSecretRecord(id, actor.OrganizationId, actor.OwnerPersonId!, request.Kind, request.Name, scope.Kind, scope.Id ?? string.Empty, request.HostOnly,
            request.Version, actor.Id, actor.OwnerPersonId, now, held?.Secret.CreatedAt ?? now);
        var action = request.Version == 1 ? "secret.set" : "secret.rotate";
        var audit = origin.Audit(action, actor, id, details: Details(record, ("devices", copies.Count.ToString(CultureInfo.InvariantCulture))));
        switch (await _secrets.PutFleetSecretAsync(record, copies, audit, cancellationToken).ConfigureAwait(false))
        {
            case FleetSecretPutOutcome.NameTaken:
                throw CoordinatorException.Conflict(CoordinatorErrorCodes.SecretExists, $"A {request.Kind} named {request.Name} exists in this scope: rotate it instead.");
            case FleetSecretPutOutcome.Stale:
                throw CoordinatorException.Conflict(CoordinatorErrorCodes.SecretConflict,
                    held is null ? "No value has this id: set version 1." : $"The value is at version {held.Secret.Version}: the next version is {held.Secret.Version + 1}, of the same name and scope.");
        }

        Notify([.. copies.Select(static copy => copy.DeviceId), .. held?.Copies.Select(static copy => copy.DeviceId) ?? []], actor.Id);
        return await GetDtoAsync(actor, id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Gives copies of the current version to devices of the person that hold none; the caller holds a copy itself.</summary>
    /// <param name="actor">The calling device.</param>
    /// <param name="id">The value.</param>
    /// <param name="request">The version and the copies.</param>
    /// <param name="origin">Where the request comes from.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The value as the caller sees it.</returns>
    /// <exception cref="CoordinatorException">The caller is not a controller device holding the value, the value is unknown, the version is not the current one, or a copy is wrong.</exception>
    public async Task<FleetSecretDto> AddCopiesAsync(DeviceRecord actor, string id, AddFleetSecretCopiesRequest? request, RequestOrigin origin, CancellationToken cancellationToken)
    {
        RequireController(actor);
        ArgumentNullException.ThrowIfNull(origin);
        var entry = await GetOwnAsync(actor, id, cancellationToken).ConfigureAwait(false);
        if (request is null) throw Invalid("The request needs the version and the copies.");
        if (!entry.Copies.Any(copy => copy.DeviceId == actor.Id))
            throw CoordinatorException.Forbidden(FleetErrorCodes.Forbidden, "Only a device that holds the value gives it to another.");
        var copies = await CheckCopiesAsync(actor, id, request.Version, request.Copies, cancellationToken).ConfigureAwait(false);
        var audit = origin.Audit("secret.grant", actor, id, details: Details(entry.Secret, ("devices", string.Join(' ', copies.Select(static copy => copy.DeviceId)))));
        if (await _secrets.AddFleetSecretCopiesAsync(actor.OrganizationId, id, request.Version, copies, audit, cancellationToken).ConfigureAwait(false) is null)
            throw CoordinatorException.Conflict(CoordinatorErrorCodes.SecretConflict, $"The value is at version {entry.Secret.Version}: give copies of that version.");
        Notify([.. copies.Select(static copy => copy.DeviceId)], actor.Id);
        return await GetDtoAsync(actor, id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Takes the copy of a value away from a device: it does not get the value at its next run.</summary>
    /// <param name="actor">The calling device.</param>
    /// <param name="id">The value.</param>
    /// <param name="deviceId">The device.</param>
    /// <param name="origin">Where the request comes from.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The value as the caller sees it.</returns>
    /// <exception cref="CoordinatorException">The caller is not a controller device of the person, or the value is unknown.</exception>
    public async Task<FleetSecretDto> RemoveCopyAsync(DeviceRecord actor, string id, string deviceId, RequestOrigin origin, CancellationToken cancellationToken)
    {
        RequireController(actor);
        ArgumentNullException.ThrowIfNull(origin);
        var entry = await GetOwnAsync(actor, id, cancellationToken).ConfigureAwait(false);
        if (deviceId == actor.Id) throw Invalid("A controller device keeps its copy of the values of its person.");
        await _secrets.RemoveFleetSecretCopyAsync(actor.OrganizationId, id, deviceId, origin.Audit("secret.withdraw", actor, id,
            details: Details(entry.Secret, ("device", deviceId))), cancellationToken).ConfigureAwait(false);
        Notify([deviceId], actor.Id);
        return await GetDtoAsync(actor, id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Deletes a value and its copies.</summary>
    /// <param name="actor">The calling device.</param>
    /// <param name="id">The value.</param>
    /// <param name="origin">Where the request comes from.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The values left.</returns>
    /// <exception cref="CoordinatorException">The caller is not a controller device of the person, or the value is unknown.</exception>
    public async Task<FleetSecretListDto> DeleteAsync(DeviceRecord actor, string id, RequestOrigin origin, CancellationToken cancellationToken)
    {
        RequireController(actor);
        ArgumentNullException.ThrowIfNull(origin);
        var entry = await GetOwnAsync(actor, id, cancellationToken).ConfigureAwait(false);
        await _secrets.DeleteFleetSecretAsync(actor.OrganizationId, id, origin.Audit("secret.delete", actor, id, details: Details(entry.Secret)), cancellationToken)
            .ConfigureAwait(false);
        Notify([.. entry.Copies.Select(static copy => copy.DeviceId)], actor.Id);
        return await ListAsync(actor, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Records the uses a machine published of values of its person: which run, which value, which version, never a value.</summary>
    /// <param name="actor">The calling device.</param>
    /// <param name="request">The uses.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The uses taken.</returns>
    /// <exception cref="CoordinatorException">The request does not hold.</exception>
    public async Task<FleetSecretUseListDto> PublishUsesAsync(DeviceRecord actor, PublishFleetSecretUsesRequest? request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (actor.OwnerPersonId is null) throw CoordinatorException.Forbidden(FleetErrorCodes.Forbidden, "A device of no person uses no value.");
        if (request?.Uses is not { Count: <= MaxUses } uses) throw Invalid($"A publication holds at most {MaxUses} uses.");
        var now = _time.GetUtcNow();
        var known = new Dictionary<string, FleetSecretRecord?>(StringComparer.Ordinal);
        var taken = new List<FleetSecretUseRecord>(uses.Count);
        foreach (var use in uses)
        {
            if (use is null || !IsId(use.SecretId) || !IsName(use.Name) || use.Version < 1 || !IsText(use.RunId, 128)) throw Invalid("A use names its value, version and run.");
            if (!known.TryGetValue(use.SecretId, out var secret))
                known[use.SecretId] = secret = (await _secrets.GetFleetSecretAsync(actor.OrganizationId, use.SecretId, cancellationToken).ConfigureAwait(false))?.Secret;
            // A use of a value of another person, or of a value deleted since, is not kept.
            if (secret is null || secret.OwnerPersonId != actor.OwnerPersonId) continue;
            taken.Add(new FleetSecretUseRecord(actor.OrganizationId, use.SecretId, actor.Id, secret.Name, use.Version, use.RunId, use.UsedAt > now ? now : use.UsedAt, now));
        }

        await _secrets.RecordFleetSecretUsesAsync(taken, cancellationToken).ConfigureAwait(false);
        return new FleetSecretUseListDto([.. taken.Select(static use => ToDto(use))]);
    }

    /// <summary>Lists the uses of a value of the calling controller device's person, the newest first.</summary>
    /// <param name="actor">The calling device.</param>
    /// <param name="id">The value.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The uses.</returns>
    /// <exception cref="CoordinatorException">The caller is not a controller device of the person, or the value is unknown.</exception>
    public async Task<FleetSecretUseListDto> ListUsesAsync(DeviceRecord actor, string id, CancellationToken cancellationToken)
    {
        RequireController(actor);
        await GetOwnAsync(actor, id, cancellationToken).ConfigureAwait(false);
        var uses = await _secrets.ListFleetSecretUsesAsync(actor.OrganizationId, id, MaxListed, cancellationToken).ConfigureAwait(false);
        return new FleetSecretUseListDto([.. uses.Select(static use => ToDto(use))]);
    }

    /// <summary>Lists the environments of the calling device's person.</summary>
    /// <param name="actor">The calling device.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The environments.</returns>
    public async Task<FleetEnvironmentListDto> ListEnvironmentsAsync(DeviceRecord actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (actor.OwnerPersonId is null) return new FleetEnvironmentListDto([]);
        var environments = await _secrets.ListFleetEnvironmentsAsync(actor.OrganizationId, actor.OwnerPersonId, cancellationToken).ConfigureAwait(false);
        return new FleetEnvironmentListDto([.. environments.Select(static environment => ToDto(environment))]);
    }

    /// <summary>Stores an environment the calling controller device signed for its person, or a newer version of it.</summary>
    /// <param name="actor">The calling device.</param>
    /// <param name="id">The environment.</param>
    /// <param name="request">The signed definition.</param>
    /// <param name="origin">Where the request comes from.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The environment as stored.</returns>
    /// <exception cref="CoordinatorException">The caller is not a controller device, the definition does not hold or is not signed by the caller, or it is not newer.</exception>
    public async Task<FleetEnvironmentDto> PutEnvironmentAsync(DeviceRecord actor, string id, PutFleetEnvironmentRequest? request, RequestOrigin origin,
        CancellationToken cancellationToken)
    {
        RequireController(actor);
        ArgumentNullException.ThrowIfNull(origin);
        if (request?.Environment is not { } signed || !FleetEnvironmentDocuments.TryRead(signed.Payload, out var definition, out _))
            throw CoordinatorException.BadRequest(CoordinatorErrorCodes.EnvironmentInvalid, "The environment does not hold.");
        if (definition.EnvironmentId != id || definition.OrganizationId != actor.OrganizationId || definition.PersonId != actor.OwnerPersonId
            || definition.SignerDeviceId != actor.Id || !VerifiedBy(actor, FleetEnvironmentDocuments.GetSignedBytes(signed.Payload), signed.Signature))
        {
            throw CoordinatorException.BadRequest(CoordinatorErrorCodes.EnvironmentInvalid,
                "An environment is of the person of the controller device that signs it, which sends it, for this organization and this id.");
        }

        var people = (await _store.ListPeopleAsync(actor.OrganizationId, cancellationToken).ConfigureAwait(false)).Select(static person => person.Id).ToHashSet(StringComparer.Ordinal);
        if (definition.Approvers.Any(approver => !people.Contains(approver)))
            throw CoordinatorException.BadRequest(CoordinatorErrorCodes.EnvironmentInvalid, "The approvers of an environment are people of the organization.");
        var record = new FleetEnvironmentRecord(id, actor.OrganizationId, actor.OwnerPersonId!, definition.Name, definition.Number, definition.Removed, signed.Payload,
            signed.Signature, actor.Id, _time.GetUtcNow());
        var audit = origin.Audit(definition.Removed ? "environment.remove" : "environment.set", actor, id,
            details: Json(("name", definition.Name), ("version", definition.Number.ToString(CultureInfo.InvariantCulture)), ("approvers", string.Join(' ', definition.Approvers)),
                ("labels", string.Join(' ', definition.MachineLabels)), ("automations", string.Join(' ', definition.Automations))));
        if (!await _secrets.PutFleetEnvironmentAsync(record, audit, cancellationToken).ConfigureAwait(false))
            throw CoordinatorException.Conflict(CoordinatorErrorCodes.EnvironmentConflict, "The environment held is as new, or another environment of the person has this name.");
        // The machines of the person read their environments again.
        Notify([.. (await _store.ListDevicesAsync(actor.OrganizationId, cancellationToken).ConfigureAwait(false))
            .Where(device => device.OwnerPersonId == actor.OwnerPersonId && device.Status == DeviceStatuses.Approved).Select(static device => device.Id)], actor.Id);
        return ToDto(record);
    }

    /// <summary>Records the approval a run of the calling machine asks before it uses an environment.</summary>
    /// <param name="actor">The calling device.</param>
    /// <param name="environmentId">The environment.</param>
    /// <param name="request">The run and its nonce.</param>
    /// <param name="origin">Where the request comes from.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The approval held for that run.</returns>
    /// <exception cref="CoordinatorException">The environment is not of the machine's person, or the request does not hold.</exception>
    public async Task<FleetApprovalDto> RequestApprovalAsync(DeviceRecord actor, string environmentId, RequestFleetApprovalRequest? request, RequestOrigin origin,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(origin);
        var environment = await GetEnvironmentAsync(actor, environmentId, cancellationToken).ConfigureAwait(false);
        if (environment.OwnerPersonId != actor.OwnerPersonId || environment.Removed) throw EnvironmentNotFound();
        if (request is null || !IsText(request.RunId, 128) || !IsText(request.Nonce, 128) || request.EnvironmentVersion < 1
            || request.AutomationId is not null && !IsText(request.AutomationId, 128) || request.Title is { Length: > 200 })
            throw CoordinatorException.BadRequest(CoordinatorErrorCodes.ApprovalInvalid, "An approval names its run, its nonce and the version of the environment; a title of at most 200 characters.");
        var approval = new FleetApprovalRecord(Base64UrlId(), actor.OrganizationId, environment.Id, request.EnvironmentVersion, actor.Id, request.RunId, request.Nonce,
            request.AutomationId, request.Title, FleetApprovalStates.Pending, _time.GetUtcNow(), null, null, null, null);
        var held = await _secrets.RequestFleetApprovalAsync(approval, origin.Audit("environment.approval.request", actor, approval.Id,
            details: Json(("environment", environment.Name), ("run", request.RunId))), cancellationToken).ConfigureAwait(false);
        if (held.EnvironmentId != environment.Id || held.Nonce != request.Nonce)
            throw CoordinatorException.Conflict(CoordinatorErrorCodes.ApprovalConflict, "This run asked another approval already.");
        return ToDto(held, environment.Name);
    }

    /// <summary>Reads an approval: its machine, or a controller device of the environment's person or of one of its approvers.</summary>
    /// <param name="actor">The calling device.</param>
    /// <param name="id">The approval.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The approval.</returns>
    /// <exception cref="CoordinatorException">The approval is unknown to the caller.</exception>
    public async Task<FleetApprovalDto> GetApprovalAsync(DeviceRecord actor, string id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var (approval, environment, _) = await ReadApprovalAsync(actor, id, cancellationToken).ConfigureAwait(false);
        return ToDto(approval, environment.Name);
    }

    /// <summary>Lists the approvals of the environments of the calling device's person, and of those it approves, the newest first.</summary>
    /// <param name="actor">The calling device.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The approvals.</returns>
    public async Task<FleetApprovalListDto> ListApprovalsAsync(DeviceRecord actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (actor.OwnerPersonId is null) return new FleetApprovalListDto([]);
        var environments = (await _secrets.ListFleetEnvironmentsAsync(actor.OrganizationId, null, cancellationToken).ConfigureAwait(false))
            .Where(environment => environment.OwnerPersonId == actor.OwnerPersonId || Approvers(environment).Contains(actor.OwnerPersonId, StringComparer.Ordinal))
            .ToDictionary(static environment => environment.Id, StringComparer.Ordinal);
        var approvals = await _secrets.ListFleetApprovalsAsync(actor.OrganizationId, environments.Keys, MaxListed, cancellationToken).ConfigureAwait(false);
        // A machine sees the approvals of its own runs only.
        return new FleetApprovalListDto([.. approvals.Where(approval => actor.Kind == FleetDeviceKinds.Controller || approval.MachineId == actor.Id)
            .Select(approval => ToDto(approval, environments[approval.EnvironmentId].Name))]);
    }

    /// <summary>Gives an approval that waits, with the approval the calling controller device of an approver signed.</summary>
    /// <param name="actor">The calling device.</param>
    /// <param name="id">The approval.</param>
    /// <param name="request">The signed approval.</param>
    /// <param name="origin">Where the request comes from.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The approval, given.</returns>
    /// <exception cref="CoordinatorException">The caller is not a controller device of an approver, the signed approval does not match the one asked, or it was given already.</exception>
    public async Task<FleetApprovalDto> ApproveAsync(DeviceRecord actor, string id, ApproveFleetApprovalRequest? request, RequestOrigin origin, CancellationToken cancellationToken)
    {
        RequireController(actor);
        ArgumentNullException.ThrowIfNull(origin);
        var (approval, environment, approvers) = await ReadApprovalAsync(actor, id, cancellationToken).ConfigureAwait(false);
        if (!approvers.Contains(actor.OwnerPersonId!, StringComparer.Ordinal))
            throw CoordinatorException.Forbidden(FleetErrorCodes.Forbidden, "Only an approver of the environment approves its runs.");
        if (approval.Status != FleetApprovalStates.Pending) throw CoordinatorException.Conflict(CoordinatorErrorCodes.ApprovalConflict, "The approval was given already.");
        if (request?.Approval is not { } signed || !FleetEnvironmentDocuments.TryReadApproval(signed.Payload, out var read, out _)
            || read.ApprovalId != approval.Id || read.OrganizationId != actor.OrganizationId || read.EnvironmentId != approval.EnvironmentId
            || read.EnvironmentNumber != approval.EnvironmentNumber || read.MachineId != approval.MachineId || read.RunId != approval.RunId || read.Nonce != approval.Nonce
            || read.ApproverPersonId != actor.OwnerPersonId || read.SignerDeviceId != actor.Id
            || !VerifiedBy(actor, FleetEnvironmentDocuments.GetApprovalSignedBytes(signed.Payload), signed.Signature))
        {
            throw CoordinatorException.BadRequest(CoordinatorErrorCodes.ApprovalInvalid,
                "The approval is signed by the controller device that sends it, for this approval, environment, machine, run and nonce.");
        }

        var now = _time.GetUtcNow();
        if (!await _secrets.ApproveFleetApprovalAsync(actor.OrganizationId, id, signed.Payload, signed.Signature, actor.Id, now,
                origin.Audit("environment.approve", actor, id, details: Json(("environment", environment.Name), ("machine", approval.MachineId), ("run", approval.RunId))),
                cancellationToken).ConfigureAwait(false))
            throw CoordinatorException.Conflict(CoordinatorErrorCodes.ApprovalConflict, "The approval was given already.");
        Notify([approval.MachineId], actor.Id);
        return ToDto(approval with { Status = FleetApprovalStates.Approved, ApprovalPayload = signed.Payload, ApprovalSignature = signed.Signature, ApproverDeviceId = actor.Id,
            ApprovedAt = now }, environment.Name);
    }

    private async Task<(FleetApprovalRecord Approval, FleetEnvironmentRecord Environment, IReadOnlyList<string> Approvers)> ReadApprovalAsync(DeviceRecord actor, string id,
        CancellationToken cancellationToken)
    {
        if (!IsText(id, 64) || actor.OwnerPersonId is null) throw ApprovalNotFound();
        var approval = await _secrets.GetFleetApprovalAsync(actor.OrganizationId, id, cancellationToken).ConfigureAwait(false) ?? throw ApprovalNotFound();
        var environment = (await _secrets.ListFleetEnvironmentsAsync(actor.OrganizationId, null, cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(candidate => candidate.Id == approval.EnvironmentId) ?? throw ApprovalNotFound();
        var approvers = Approvers(environment);
        var mayRead = approval.MachineId == actor.Id
            || actor.Kind == FleetDeviceKinds.Controller && (environment.OwnerPersonId == actor.OwnerPersonId || approvers.Contains(actor.OwnerPersonId, StringComparer.Ordinal));
        if (!mayRead) throw ApprovalNotFound();
        return (approval, environment, approvers);
    }

    private async Task<FleetEnvironmentRecord> GetEnvironmentAsync(DeviceRecord actor, string environmentId, CancellationToken cancellationToken)
    {
        if (!IsText(environmentId, 128) || actor.OwnerPersonId is null) throw EnvironmentNotFound();
        return (await _secrets.ListFleetEnvironmentsAsync(actor.OrganizationId, actor.OwnerPersonId, cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(candidate => candidate.Id == environmentId) ?? throw EnvironmentNotFound();
    }

    private static IReadOnlyList<string> Approvers(FleetEnvironmentRecord environment)
        => FleetEnvironmentDocuments.TryRead(environment.Payload, out var definition, out _) ? definition.Approvers : [];

    // The scope of a value: one of a person, with its id where it names one; an environment must be one of the person.
    private async Task<FleetSecretScopeDto> CheckScopeAsync(DeviceRecord actor, FleetSecretScopeDto? scope, CancellationToken cancellationToken)
    {
        if (scope is null || !FleetSecretScopeKinds.IsKnown(scope.Kind)) throw Invalid("A value is scoped to the person, a space, a project or an environment.");
        if (!FleetSecretScopeKinds.IsAvailable(scope.Kind)) throw Invalid($"The values of a {scope.Kind} come with its roles: scope it to the person, a space, a project or an environment.");
        if (FleetSecretScopeKinds.HasId(scope.Kind) ? !IsText(scope.Id, 128) : scope.Id is { Length: > 0 }) throw Invalid("A scope names its space, project or environment; the person's names none.");
        if (scope.Kind == FleetSecretScopeKinds.Environment)
        {
            var environment = await GetEnvironmentAsync(actor, scope.Id!, cancellationToken).ConfigureAwait(false);
            if (environment.Removed) throw EnvironmentNotFound();
        }

        return scope.Kind == FleetSecretScopeKinds.Person ? new FleetSecretScopeDto(scope.Kind) : scope;
    }

    // Each copy goes to an approved device of the caller's person, once, as bounded text. A device the server added by
    // itself gets nothing it can open: the devices seal only to the keys of the device list they verified.
    private async Task<IReadOnlyList<FleetSecretCopyRecord>> CheckCopiesAsync(DeviceRecord actor, string id, int version, IReadOnlyList<FleetSecretCopyDto>? copies,
        CancellationToken cancellationToken)
    {
        if (copies is not { Count: > 0 and <= MaxCopies }) throw Invalid($"The request needs from 1 to {MaxCopies} copies.");
        if (copies.Select(static copy => copy?.DeviceId).Distinct(StringComparer.Ordinal).Count() != copies.Count) throw Invalid("A device gets one copy of a value.");
        var now = _time.GetUtcNow();
        var records = new List<FleetSecretCopyRecord>(copies.Count);
        foreach (var copy in copies)
        {
            if (copy is null || string.IsNullOrEmpty(copy.DeviceId) || !IsText(copy.Sealed, MaxCopyLength)) throw Invalid($"A copy names a device and holds at most {MaxCopyLength} characters.");
            var device = await _store.GetDeviceAsync(copy.DeviceId, cancellationToken).ConfigureAwait(false);
            if (device is null || device.OrganizationId != actor.OrganizationId || device.Status != DeviceStatuses.Approved || device.OwnerPersonId != actor.OwnerPersonId
                || device.Kind == FleetDeviceKinds.Recovery)
            {
                throw Invalid("A value is sealed only for approved devices of its person.");
            }

            records.Add(new FleetSecretCopyRecord(id, device.Id, version, copy.Sealed, actor.Id, now));
        }

        return records;
    }

    private async Task<FleetSecretEntry> GetOwnAsync(DeviceRecord actor, string id, CancellationToken cancellationToken)
    {
        if (!IsId(id)) throw NotFound();
        var entry = await _secrets.GetFleetSecretAsync(actor.OrganizationId, id, cancellationToken).ConfigureAwait(false);
        return entry is not null && entry.Secret.OwnerPersonId == actor.OwnerPersonId ? entry : throw NotFound();
    }

    private async Task<FleetSecretDto> GetDtoAsync(DeviceRecord actor, string id, CancellationToken cancellationToken)
    {
        var entry = await GetOwnAsync(actor, id, cancellationToken).ConfigureAwait(false);
        return ToDto(entry, actor.Id, await RevokedAsync(actor, cancellationToken).ConfigureAwait(false), holderCopies: true);
    }

    // The devices of the organization revoked: one that held a copy of the current version flags the value for rotation.
    private async Task<IReadOnlySet<string>> RevokedAsync(DeviceRecord actor, CancellationToken cancellationToken)
        => (await _store.ListDevicesAsync(actor.OrganizationId, cancellationToken).ConfigureAwait(false))
            .Where(static device => device.Status == DeviceStatuses.Revoked).Select(static device => device.Id).ToHashSet(StringComparer.Ordinal);

    // Tells the devices online that their values changed, but the one that changed them; one that is offline reads them when it runs next.
    private void Notify(IEnumerable<string> devices, string except)
    {
        if (Presence is not { } presence) return;
        foreach (var device in devices.Distinct(StringComparer.Ordinal))
        {
            if (device == except || presence.Get(device) is not { } session) continue;
            _ = TellAsync(session);
        }
    }

    private static async Task TellAsync(ControlSession session)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await session.Connection.SendEventAsync(new FleetEvent(FleetSecretMethods.Changed, null), timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is FleetException or FleetConnectionClosedException or OperationCanceledException or InvalidOperationException)
        {
            // The machine reads its values again before its next run in any case.
        }
    }

    // The key the server records for the device that sends the request verifies what it signed.
    private static bool VerifiedBy(DeviceRecord actor, byte[] signed, string? signature)
        => FleetDevicePublicKey.TryParse(actor.PublicKey, out var key) && key.Verify(signed, signature);

    // A controller device of the person gets the copies of the holders too: their signed grants tell it who a controller gave the value to.
    private static FleetSecretDto ToDto(FleetSecretEntry entry, string deviceId, IReadOnlySet<string> revoked, bool holderCopies = false)
    {
        var secret = entry.Secret;
        var holders = entry.Copies.Select(static copy => copy.DeviceId).ToArray();
        return new FleetSecretDto(secret.Id, secret.Kind, secret.Name, new FleetSecretScopeDto(secret.ScopeKind, secret.ScopeId.Length == 0 ? null : secret.ScopeId), secret.Version,
            secret.HostOnly, secret.SetByDeviceId, secret.SetByPersonId, secret.SetAt, secret.CreatedAt, [.. holders.Where(holder => !revoked.Contains(holder))],
            [.. holders.Where(revoked.Contains)], entry.Copies.FirstOrDefault(copy => copy.DeviceId == deviceId)?.Sealed,
            holderCopies ? [.. entry.Copies.Where(copy => !revoked.Contains(copy.DeviceId)).Select(static copy => new FleetSecretCopyDto(copy.DeviceId, copy.Sealed))] : null);
    }

    private static FleetSecretUseDto ToDto(FleetSecretUseRecord use) => new(use.SecretId, use.Name, use.Version, use.RunId, use.UsedAt, use.MachineId);

    private static FleetEnvironmentDto ToDto(FleetEnvironmentRecord environment)
        => new(environment.Id, environment.Name, environment.Number, environment.SignerDeviceId, environment.UpdatedAt, new FleetSignedEnvironment(environment.Payload, environment.Signature));

    private static FleetApprovalDto ToDto(FleetApprovalRecord approval, string environmentName)
        => new(approval.Id, approval.EnvironmentId, environmentName, approval.EnvironmentNumber, approval.MachineId, approval.RunId, approval.Nonce, approval.AutomationId,
            approval.Title, approval.Status, approval.RequestedAt,
            approval.ApprovalPayload is { } payload && approval.ApprovalSignature is { } signature ? new FleetSignedApproval(payload, signature) : null);

    private static string Details(FleetSecretRecord secret, params (string Name, string Value)[] more)
        => Json([("kind", secret.Kind), ("name", secret.Name), ("scope", secret.ScopeKind), ("scopeId", secret.ScopeId), ("version", secret.Version.ToString(CultureInfo.InvariantCulture)),
            ("hostOnly", secret.HostOnly ? "true" : "false"), .. more]);

    private static string Json(params (string Name, string Value)[] fields)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (name, value) in fields) writer.WriteString(name, value);
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string Base64UrlId() => System.Buffers.Text.Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));

    private static void RequireController(DeviceRecord actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (actor.Kind != FleetDeviceKinds.Controller || actor.OwnerPersonId is null)
            throw CoordinatorException.Forbidden(FleetErrorCodes.Forbidden, "Only a controller device of a person manages its secrets, variables and environments; a machine reads its own copies.");
    }

    private static bool IsId(string? id) => id is { Length: > 0 and <= 64 } && id.All(static c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_');

    private static bool IsName(string? name) => name is { Length: > 0 and <= 128 } && !char.IsAsciiDigit(name[0]) && name.All(static ch => char.IsAsciiLetterOrDigit(ch) || ch == '_');

    private static bool IsText(string? text, int max) => !string.IsNullOrEmpty(text) && text.Length <= max && !text.Any(char.IsControl);

    private static readonly IReadOnlySet<string> ImmutableEmpty = System.Collections.Frozen.FrozenSet<string>.Empty;

    private static CoordinatorException Invalid(string message) => CoordinatorException.BadRequest(CoordinatorErrorCodes.SecretInvalid, message);

    private static CoordinatorException NotFound() => CoordinatorException.NotFound(CoordinatorErrorCodes.SecretNotFound, "No value of this person has this id.");

    private static CoordinatorException EnvironmentNotFound() => CoordinatorException.NotFound(CoordinatorErrorCodes.EnvironmentNotFound, "No environment of this person has this id.");

    private static CoordinatorException ApprovalNotFound() => CoordinatorException.NotFound(CoordinatorErrorCodes.ApprovalNotFound, "No approval you may see has this id.");
}
