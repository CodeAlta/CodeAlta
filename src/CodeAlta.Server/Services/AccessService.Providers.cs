using System.Collections.Frozen;
using System.Text.Json;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Server.Storage;

namespace CodeAlta.Server.Services;

// The providers of the teams and of the organization (doc/fleet/orchestration.md, credentials of unattended runs): defined
// once by who answers for the scope, the key of their account sealed by that person's device for the machines of the scope
// only. The server keeps the definition, the sealed key and one copy of its sealing key per machine; it never reads the key.
internal sealed partial class AccessService
{
    private const int MaxModels = 50;

    // The types of provider that run with the key of an account: what an automation of a team or of the organization runs with.
    private static readonly FrozenSet<string> AccountProviderTypes =
        FrozenSet.ToFrozenSet(["openai-chat", "openai-responses", "azure-openai", "anthropic", "google-genai", "mistral", "xai"], StringComparer.Ordinal);

    /// <summary>Lists the providers of the teams and of the organization: their definitions and the machines that hold their keys.</summary>
    /// <param name="actor">The calling controller device.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The providers.</returns>
    /// <exception cref="CoordinatorException">The caller is not a controller device of an active person.</exception>
    public async Task<AccessListDto<ScopeProviderDto>> ListProvidersAsync(DeviceRecord actor, CancellationToken cancellationToken)
    {
        _ = await ReadAsync(actor, cancellationToken).ConfigureAwait(false);
        var providers = await _access.ListScopeProvidersAsync(actor.OrganizationId, cancellationToken).ConfigureAwait(false);
        return new([.. providers.Select(static provider => ToDto(provider.Provider, provider.Machines))]);
    }

    /// <summary>
    /// Sets a provider of a team or of the organization: for an owner or an admin for the organization, a maintainer for a team;
    /// its key sealed for the machines of that scope, and for no other, at a version above the one held.
    /// </summary>
    /// <param name="actor">The calling controller device.</param>
    /// <param name="providerId">The provider.</param>
    /// <param name="request">Its definition and its sealed key.</param>
    /// <param name="origin">Where the request comes from.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The provider.</returns>
    /// <exception cref="CoordinatorException">The caller may not, the provider does not hold, or a newer version is held.</exception>
    public async Task<ScopeProviderDto> PutProviderAsync(DeviceRecord actor, string providerId, PutScopeProviderRequest? request, RequestOrigin origin,
        CancellationToken cancellationToken)
    {
        var (snapshot, person) = await ReadAsync(actor, cancellationToken).ConfigureAwait(false);
        if (!IsProviderId(providerId)) throw Invalid("The id of a provider is 1 to 64 lowercase letters, digits and dashes: its key on the machines.");
        if (request is null || request.OwnerKind is not (FleetPrincipalKinds.Team or FleetPrincipalKinds.Organization) || string.IsNullOrEmpty(request.OwnerId))
            throw Invalid("A provider belongs to a team or to the organization.");
        if (request.OwnerKind == FleetPrincipalKinds.Organization && request.OwnerId != actor.OrganizationId) throw Invalid("The organization is the one of this server.");
        if (!AnswersFor(snapshot, person, request.OwnerKind, request.OwnerId))
            throw Denied("A provider of the organization is set by an owner or an admin, one of a team by a maintainer of the team.");
        // A provider held already is changed by who answers for the scope it belongs to, and stays in that scope.
        var existing = (await _access.ListScopeProvidersAsync(actor.OrganizationId, cancellationToken).ConfigureAwait(false)).FirstOrDefault(provider => provider.Provider.Id == providerId)
            .Provider;
        if (existing is not null && !AnswersFor(snapshot, person, existing.OwnerKind, existing.OwnerId))
            throw Denied("This provider belongs to a scope you do not answer for: only who answers for it changes it.");
        if (existing is not null && (existing.OwnerKind != request.OwnerKind || existing.OwnerId != request.OwnerId))
            throw Invalid("A provider keeps the scope it was made for: delete it, and make one for the other scope.");
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 64 || request.Name.Any(char.IsControl)) throw Invalid("A provider has a name of 1 to 64 characters.");
        if (!AccountProviderTypes.Contains(request.Type ?? string.Empty)) throw Invalid("A provider of a scope runs with the key of an account: " + string.Join(", ", AccountProviderTypes) + ".");
        if (request.Endpoint is { } endpoint && (!Uri.TryCreate(endpoint, UriKind.Absolute, out var address) || address.Scheme != Uri.UriSchemeHttps && !address.IsLoopback))
            throw Invalid("The endpoint of a provider is an https:// address.");
        if (request.Models is not { Count: > 0 and <= MaxModels } models || models.Any(static model => string.IsNullOrWhiteSpace(model) || model.Length > 128 || model.Any(char.IsControl)))
            throw Invalid($"A provider offers 1 to {MaxModels} models.");
        if (request.Version < 1 || string.IsNullOrEmpty(request.Sealed) || request.Sealed.Length > MaxSealedLength || request.Wraps is not { Count: > 0 and <= MaxWraps })
            throw Invalid("A provider comes with its key sealed for the machines of its scope.");
        var machines = await ScopeMachinesAsync(actor.OrganizationId, snapshot, request.OwnerKind, request.OwnerId, cancellationToken).ConfigureAwait(false);
        if (request.Wraps.Any(wrap => wrap is null || !machines.Contains(wrap.DeviceId) || string.IsNullOrEmpty(wrap.Wrapped) || wrap.Wrapped.Length > MaxWrappedLength)
            || request.Wraps.Select(static wrap => wrap.DeviceId).Distinct(StringComparer.Ordinal).Count() != request.Wraps.Count)
            throw Invalid("The key of a provider goes once to each machine of its scope, and to no other.");
        var record = new ScopeProviderRecord(providerId, actor.OrganizationId, request.OwnerKind, request.OwnerId, request.Name.Trim(), request.Type!, request.Endpoint,
            JsonSerializer.Serialize(models.Select(static model => model.Trim()).ToArray(), SignInSettingsJsonContext.Default.StringArray), request.Version, request.Sealed,
            person.Id, _time.GetUtcNow());
        if (!await _access.PutScopeProviderAsync(record, [.. request.Wraps.Select(static wrap => (wrap.DeviceId, wrap.Wrapped))],
                origin.Audit("provider.put", actor, providerId, details: Json(("owner", request.OwnerKind + ":" + request.OwnerId), ("type", record.Type))), cancellationToken)
                .ConfigureAwait(false))
            throw CoordinatorException.Conflict(CoordinatorErrorCodes.AccessConflict, "The server holds this version of the provider or a newer one: read it again.");
        Changed(actor.OrganizationId);
        return ToDto(record, [.. request.Wraps.Select(static wrap => wrap.DeviceId)]);
    }

    /// <summary>Deletes a provider of a team or of the organization, for who answers for its scope.</summary>
    /// <param name="actor">The calling controller device.</param>
    /// <param name="providerId">The provider.</param>
    /// <param name="origin">Where the request comes from.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The providers left.</returns>
    /// <exception cref="CoordinatorException">The caller may not, or no provider has this id.</exception>
    public async Task<AccessListDto<ScopeProviderDto>> DeleteProviderAsync(DeviceRecord actor, string providerId, RequestOrigin origin, CancellationToken cancellationToken)
    {
        var (snapshot, person) = await ReadAsync(actor, cancellationToken).ConfigureAwait(false);
        var held = (await _access.ListScopeProvidersAsync(actor.OrganizationId, cancellationToken).ConfigureAwait(false)).FirstOrDefault(provider => provider.Provider.Id == providerId);
        if (held.Provider is null) throw NotFound("No provider of a scope has this id.");
        if (!AnswersFor(snapshot, person, held.Provider.OwnerKind, held.Provider.OwnerId)) throw Denied("Only who answers for its scope deletes a provider.");
        await _access.DeleteScopeProviderAsync(actor.OrganizationId, providerId, origin.Audit("provider.delete", actor, providerId), cancellationToken).ConfigureAwait(false);
        Changed(actor.OrganizationId);
        return await ListProvidersAsync(actor, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Lists the providers whose key the calling machine holds a copy of: their definitions and its copies.</summary>
    /// <param name="actor">The calling machine.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The providers and the copies, which the machine verifies against its access document.</returns>
    /// <exception cref="CoordinatorException">The caller is not an approved machine.</exception>
    public async Task<AccessListDto<ScopeProviderKeyDto>> ListProviderKeysAsync(DeviceRecord actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (actor.Kind != FleetDeviceKinds.Node) throw Denied("The keys of providers go to machines.");
        var keys = await _access.ListScopeProviderKeysAsync(actor.Id, cancellationToken).ConfigureAwait(false);
        return new([.. keys.Select(key => new ScopeProviderKeyDto(ToDto(key.Provider, [actor.Id]), key.Provider.Sealed, key.Wrapped))]);
    }

    private static bool IsProviderId(string? id) => id is { Length: > 0 and <= 64 } && id.All(static c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');

    private static bool AnswersFor(AccessSnapshot snapshot, PersonRecord person, string ownerKind, string ownerId) => ownerKind switch
    {
        FleetPrincipalKinds.Organization => person.IsActive && FleetOrganizationRoles.Administers(person.Role),
        FleetPrincipalKinds.Team => snapshot.TeamRole(ownerId, person.Id) == FleetTeamRoles.Maintainer,
        _ => false,
    };

    // The machines of a scope: the approved machines its owner record gives to the team, or to the organization.
    private async Task<HashSet<string>> ScopeMachinesAsync(string organizationId, AccessSnapshot snapshot, string ownerKind, string ownerId, CancellationToken cancellationToken)
    {
        var owned = snapshot.Owners.Where(owner => owner.ResourceKind == FleetResourceKinds.Machine && owner.OwnerKind == ownerKind && owner.OwnerId == ownerId)
            .Select(static owner => owner.ResourceId).ToHashSet(StringComparer.Ordinal);
        var devices = await _store.ListDevicesAsync(organizationId, cancellationToken).ConfigureAwait(false);
        return devices.Where(device => owned.Contains(device.Id) && device.Status == DeviceStatuses.Approved).Select(static device => device.Id).ToHashSet(StringComparer.Ordinal);
    }

    private static ScopeProviderDto ToDto(ScopeProviderRecord provider, IReadOnlyList<string> machines)
        => new(provider.Id, provider.OwnerKind, provider.OwnerId, provider.Name, provider.Type, provider.Endpoint,
            JsonSerializer.Deserialize(provider.Models, SignInSettingsJsonContext.Default.StringArray) ?? [], provider.Version, provider.UpdatedByPersonId, provider.UpdatedAt, machines);
}
