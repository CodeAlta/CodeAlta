using System.Globalization;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Server.Storage;

namespace CodeAlta.Server.Services;

/// <summary>
/// The signed policy of an organization at the server (<c>doc/fleet/security-and-privacy.md</c>, policies and the kill switch):
/// the server keeps the chain and relays it; it never writes a version. A version is kept only when it verifies as every
/// machine verifies it (the device list and the access document the server holds), and every device of the organization
/// online is told at once, so that a stop reaches the machines in seconds.
/// </summary>
internal sealed class PolicyService
{
    private readonly ICoordinatorStore _store;
    private readonly IAccessStore _access;
    private readonly IPolicyStore _policies;
    private readonly NotificationService _notifications;
    private readonly TimeProvider _time;

    /// <summary>Initializes a new instance of the <see cref="PolicyService"/> class.</summary>
    /// <param name="store">The store of the organization.</param>
    /// <param name="access">The store of access control, whose signed document says who signs.</param>
    /// <param name="policies">The store of the policy.</param>
    /// <param name="notifications">What tells the devices online.</param>
    /// <param name="time">The clock.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public PolicyService(ICoordinatorStore store, IAccessStore access, IPolicyStore policies, NotificationService notifications, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(policies);
        ArgumentNullException.ThrowIfNull(notifications);
        ArgumentNullException.ThrowIfNull(time);
        _store = store;
        _access = access;
        _policies = policies;
        _notifications = notifications;
        _time = time;
    }

    /// <summary>Lists the versions after a number, for any approved device of the organization: machines verify them themselves.</summary>
    /// <param name="actor">The calling device.</param>
    /// <param name="after">The number after which to list.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The versions.</returns>
    /// <exception cref="CoordinatorException">The caller is not an approved device.</exception>
    public async Task<PolicyDocumentListDto> ListAsync(DeviceRecord actor, long after, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (actor.Status != DeviceStatuses.Approved) throw AccessService.Denied("Only a device of the organization reads its policy.");
        var documents = await _policies.ListPolicyDocumentsAsync(actor.OrganizationId, after, cancellationToken).ConfigureAwait(false);
        return new PolicyDocumentListDto([.. documents.Select(ToDto)]);
    }

    /// <summary>
    /// Appends a version the calling controller device signed: it reads, it is signed by that device, and the chain with it
    /// verifies against the device list and the access document the server holds, as machines verify it.
    /// </summary>
    /// <param name="actor">The calling controller device, which signed it.</param>
    /// <param name="request">The signed version.</param>
    /// <param name="origin">Where the request comes from.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The version as stored.</returns>
    /// <exception cref="CoordinatorException">The version does not hold, its signer may not sign it, or another version was signed meanwhile.</exception>
    public async Task<PolicyDocumentDto> AppendAsync(DeviceRecord actor, AppendPolicyDocumentRequest? request, RequestOrigin origin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(origin);
        if (actor.Status != DeviceStatuses.Approved || actor.Kind != FleetDeviceKinds.Controller)
            throw AccessService.Denied("Only the controller device of an owner or an admin who signs signs the policy.");
        if (!FleetDevicePublicKey.TryParse(actor.PublicKey, out var key)) throw new InvalidOperationException("The key of a device in the table does not read.");
        if (!FleetPolicyDocuments.TryReadSigned(request?.Document, key, out var document, out var reason)) throw Invalid(reason);
        if (document.OrganizationId != actor.OrganizationId) throw Invalid("it names another organization");

        var entries = await _store.ListDeviceListAsync(actor.OrganizationId, cancellationToken).ConfigureAwait(false);
        var devices = FleetDeviceListVerifier.VerifyOrThrow(entries.Select(static entry => new FleetSignedDeviceListEntry(entry.Payload, entry.Signature)), actor.OrganizationId);
        var access = await AccessHistoryAsync(actor.OrganizationId, devices, cancellationToken).ConfigureAwait(false);
        // Who signs now: a signer of the last access document, or the person who set the server up while there is none.
        var latest = access.Count == 0 ? null : access[access.Keys.Max()];
        var founder = devices.FirstDeviceId is { } first && devices.Tenures.TryGetValue(first, out var tenure) ? tenure.Member.OwnerId : null;
        if (latest is null ? actor.OwnerPersonId != founder : !latest.IsSigner(actor.OwnerPersonId))
            throw AccessService.Denied("Only the devices of the owners and of the admins who sign, as the access document lists them, sign the policy.");
        var held = await _policies.ListPolicyDocumentsAsync(actor.OrganizationId, 0, cancellationToken).ConfigureAwait(false);
        if (document.Number != held.Count + 1)
            throw CoordinatorException.Conflict(CoordinatorErrorCodes.PolicyConflict, "Another version of the policy was signed meanwhile: read it again and sign the next one.");
        var chain = FleetPolicyDocuments.Verify([.. held.Select(static item => new FleetSignedPolicyDocument(item.Payload, item.Signature)), request!.Document], devices,
            number => access.GetValueOrDefault(number), actor.OrganizationId);
        if (!chain.IsValid) throw Invalid(chain.Reason!);

        var before = held.Count == 0 || !FleetPolicyDocuments.TryParse(held[^1].Payload, out var last) ? null : last;
        // The audit says what kind of change it is: the kill switch set, lifted, or the rules.
        var action = document.Stop is not null && before?.Stop is null ? "policy.stop" : document.Stop is null && before?.Stop is not null ? "policy.lift" : "policy.sign";
        var record = new PolicyDocumentRecord(actor.OrganizationId, document.Number, request.Document.Payload, request.Document.Signature, actor.Id, chain.State.LastHash!,
            _time.GetUtcNow());
        var details = AccessService.Json(("number", document.Number.ToString(CultureInfo.InvariantCulture)), ("access", document.AccessNumber.ToString(CultureInfo.InvariantCulture)),
            ("stop", document.Stop?.Scope ?? "none"), ("machines", (document.Stop?.Machines.Count ?? 0).ToString(CultureInfo.InvariantCulture)),
            ("teams", (document.Stop?.Teams.Count ?? 0).ToString(CultureInfo.InvariantCulture)));
        if (!await _policies.AppendPolicyDocumentAsync(record, origin.Audit(action, actor, document.Number.ToString(CultureInfo.InvariantCulture), details: details), cancellationToken)
                .ConfigureAwait(false))
            throw CoordinatorException.Conflict(CoordinatorErrorCodes.PolicyConflict, "Another version of the policy was signed meanwhile: read it again and sign the next one.");
        _notifications.TellOrganization(actor.OrganizationId, FleetPolicyMethods.Changed);
        return ToDto(record);
    }

    // The access document at each version the server holds, as machines replay it: who signs the policy at each.
    private async Task<Dictionary<long, FleetAccessState>> AccessHistoryAsync(string organizationId, FleetDeviceListState devices, CancellationToken cancellationToken)
    {
        var history = new Dictionary<long, FleetAccessState>();
        var state = FleetAccessState.Empty;
        foreach (var item in await _access.ListAccessDocumentsAsync(organizationId, 0, cancellationToken).ConfigureAwait(false))
        {
            if (!FleetAccessDocuments.TryAppend(state, new FleetSignedAccessDocument(item.Payload, item.Signature), devices, organizationId, out state, out _)) break;
            history[state.Number] = state;
        }

        return history;
    }

    private static PolicyDocumentDto ToDto(PolicyDocumentRecord record)
        => new(record.Number, record.Hash, new FleetSignedPolicyDocument(record.Payload, record.Signature), record.RecordedAt);

    private static CoordinatorException Invalid(string reason)
        => CoordinatorException.BadRequest(CoordinatorErrorCodes.PolicyInvalid, $"The version of the policy does not hold: {reason}.");
}
