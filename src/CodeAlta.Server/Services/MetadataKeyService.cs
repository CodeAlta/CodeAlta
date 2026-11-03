using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Server.Storage;

namespace CodeAlta.Server.Services;

/// <summary>
/// The metadata keys of people (<c>doc/fleet/security-and-privacy.md</c>, keys): a controller device makes its person's
/// key and wraps it to each device of that person; the server keeps the wrapped copies as opaque text and gives each
/// device its own. It never holds a key in clear, and it cannot read the titles a key seals.
/// </summary>
/// <remarks>
/// The server checks who may hold a copy (approved devices of the same person) as its tables say; devices wrap only to
/// the keys of the signed device list they verified, so a device the server added by itself never gets a copy.
/// </remarks>
public sealed class MetadataKeyService
{
    /// <summary>The most wraps in one request.</summary>
    public const int MaxWraps = 64;

    /// <summary>The largest wrapped key, in characters.</summary>
    public const int MaxWrappedLength = 4096;

    private readonly ICoordinatorStore _store;
    private readonly TimeProvider _time;

    /// <summary>Initializes a new instance of the <see cref="MetadataKeyService"/> class.</summary>
    /// <param name="store">The store.</param>
    /// <param name="time">The clock.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public MetadataKeyService(ICoordinatorStore store, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(time);
        _store = store;
        _time = time;
    }

    /// <summary>Lists the metadata keys of the calling device's person, each with the copy wrapped to that device.</summary>
    /// <param name="actor">The calling device.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The keys.</returns>
    public async Task<ListResponse<MetadataKeyDto>> ListAsync(DeviceRecord actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (actor.OwnerPersonId is null) return new([]);
        var keys = await _store.ListMetadataKeysAsync(actor.OwnerPersonId, cancellationToken).ConfigureAwait(false);
        return new([.. keys.Select(key => new MetadataKeyDto(key.Key.Id, key.Key.Epoch, key.Key.CreatedByDeviceId, key.Key.CreatedAt,
            key.Wraps.FirstOrDefault(wrap => wrap.DeviceId == actor.Id)?.Wrapped, [.. key.Wraps.Select(static wrap => wrap.DeviceId)]))]);
    }

    /// <summary>Records a new metadata key of the calling controller device's person, wrapped to its devices, the caller included.</summary>
    /// <param name="actor">The calling device.</param>
    /// <param name="request">The key and its wraps.</param>
    /// <param name="origin">Where the request comes from.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The key as the caller sees it.</returns>
    /// <exception cref="CoordinatorException">The caller is not a controller, a wrap is wrong, or the person has a key at that epoch.</exception>
    public async Task<MetadataKeyDto> CreateAsync(DeviceRecord actor, CreateMetadataKeyRequest? request, RequestOrigin origin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(origin);
        RequireController(actor);
        if (request is null || !IsKeyId(request.Id) || request.Epoch < 1) throw Invalid("The request needs the id of the key (base64url, at most 64 characters) and an epoch from 1.");
        var wraps = await CheckWrapsAsync(actor, request.Id, request.Wraps, cancellationToken).ConfigureAwait(false);
        if (!wraps.Any(wrap => wrap.DeviceId == actor.Id)) throw Invalid("A new key is wrapped to the device that makes it too.");
        var key = new MetadataKeyRecord(request.Id, actor.OrganizationId, actor.OwnerPersonId!, request.Epoch, actor.Id, _time.GetUtcNow());
        var audit = origin.Audit("metadata_key.create", actor, key.Id, details: $"{{\"epoch\":{key.Epoch},\"devices\":{wraps.Count}}}");
        if (!await _store.CreateMetadataKeyAsync(key, wraps, audit, cancellationToken).ConfigureAwait(false))
        {
            throw CoordinatorException.Conflict(CoordinatorErrorCodes.MetadataKeyExists, "The person has a metadata key at this epoch: read it and add wraps instead.");
        }

        return new MetadataKeyDto(key.Id, key.Epoch, key.CreatedByDeviceId, key.CreatedAt, wraps.First(wrap => wrap.DeviceId == actor.Id).Wrapped,
            [.. wraps.Select(static wrap => wrap.DeviceId)]);
    }

    /// <summary>Adds copies of a metadata key for devices of the person that have none; the caller must hold a copy.</summary>
    /// <param name="actor">The calling device.</param>
    /// <param name="keyId">The key.</param>
    /// <param name="request">The wraps.</param>
    /// <param name="origin">Where the request comes from.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The key as the caller sees it.</returns>
    /// <exception cref="CoordinatorException">The caller is not a controller holding the key, the key is unknown, or a wrap is wrong.</exception>
    public async Task<MetadataKeyDto> AddWrapsAsync(DeviceRecord actor, string keyId, AddMetadataKeyWrapsRequest? request, RequestOrigin origin,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(origin);
        RequireController(actor);
        var key = (await _store.ListMetadataKeysAsync(actor.OwnerPersonId!, cancellationToken).ConfigureAwait(false)).FirstOrDefault(candidate => candidate.Key.Id == keyId)
            ?? throw CoordinatorException.NotFound(CoordinatorErrorCodes.MetadataKeyNotFound, "No metadata key of this person has this id.");
        var mine = key.Wraps.FirstOrDefault(wrap => wrap.DeviceId == actor.Id)
            ?? throw CoordinatorException.Forbidden(FleetErrorCodes.Forbidden, "Only a device that holds the key wraps it to others.");
        var wraps = await CheckWrapsAsync(actor, keyId, request?.Wraps, cancellationToken).ConfigureAwait(false);
        var added = await _store.AddMetadataKeyWrapsAsync(wraps, origin.Audit("metadata_key.wrap", actor, keyId), cancellationToken).ConfigureAwait(false);
        return new MetadataKeyDto(key.Key.Id, key.Key.Epoch, key.Key.CreatedByDeviceId, key.Key.CreatedAt, mine.Wrapped,
            [.. key.Wraps.Select(static wrap => wrap.DeviceId).Concat(added).Distinct(StringComparer.Ordinal)]);
    }

    // Each wrap goes to an approved device of the caller's person, once, and reads as bounded text.
    private async Task<IReadOnlyList<MetadataKeyWrapRecord>> CheckWrapsAsync(DeviceRecord actor, string keyId, IReadOnlyList<MetadataKeyWrapDto>? wraps,
        CancellationToken cancellationToken)
    {
        if (wraps is not { Count: > 0 and <= MaxWraps }) throw Invalid($"The request needs from 1 to {MaxWraps} wraps.");
        if (wraps.Select(static wrap => wrap?.DeviceId).Distinct(StringComparer.Ordinal).Count() != wraps.Count) throw Invalid("A device gets one wrap of a key.");
        var now = _time.GetUtcNow();
        var records = new List<MetadataKeyWrapRecord>(wraps.Count);
        foreach (var wrap in wraps)
        {
            if (wrap is null || string.IsNullOrEmpty(wrap.DeviceId) || string.IsNullOrEmpty(wrap.Wrapped) || wrap.Wrapped.Length > MaxWrappedLength
                || wrap.Wrapped.Any(char.IsControl))
            {
                throw Invalid($"A wrap names a device and holds at most {MaxWrappedLength} characters.");
            }

            var device = await _store.GetDeviceAsync(wrap.DeviceId, cancellationToken).ConfigureAwait(false);
            if (device is null || device.OrganizationId != actor.OrganizationId || device.Status != DeviceStatuses.Approved || device.OwnerPersonId != actor.OwnerPersonId)
            {
                throw Invalid("A key is wrapped only to approved devices of its person.");
            }

            records.Add(new MetadataKeyWrapRecord(keyId, device.Id, wrap.Wrapped, actor.Id, now));
        }

        return records;
    }

    private static bool IsKeyId(string? id) => id is { Length: > 0 and <= 64 } && id.All(static c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_');

    private static void RequireController(DeviceRecord actor)
    {
        if (actor.Kind != FleetDeviceKinds.Controller || actor.OwnerPersonId is null)
        {
            throw CoordinatorException.Forbidden(FleetErrorCodes.Forbidden, "Only a controller device of a person makes or wraps its metadata key; a node only reads its own copy.");
        }
    }

    private static CoordinatorException Invalid(string message) => CoordinatorException.BadRequest(CoordinatorErrorCodes.MetadataKeyInvalid, message);
}
