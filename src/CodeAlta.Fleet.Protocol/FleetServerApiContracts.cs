using System.Text.Json.Serialization;

namespace CodeAlta.Fleet.Protocol.Api;

/// <summary>The body of <c>POST /api/v1/setup</c>, signed with the key it names (signed request headers).</summary>
/// <param name="Code">The one-time setup code (<c>altaserver setup-code</c>).</param>
/// <param name="OwnerName">The name of the owner.</param>
/// <param name="DeviceName">The name of the owner's first device.</param>
/// <param name="PublicKey">The public key of that device, the base64 of its DER SubjectPublicKeyInfo.</param>
/// <param name="DeviceListEntry">The first entry of the device list: the device adds itself as a controller, signed by its own key.</param>
/// <param name="OrganizationName">The name of the organization, or <see langword="null"/> for one made from the owner's name.</param>
public sealed record SetupRequest(string Code, string OwnerName, string DeviceName, string PublicKey, FleetSignedDeviceListEntry DeviceListEntry, string? OrganizationName = null);

/// <summary>The answer of a setup.</summary>
/// <param name="Organization">The organization made.</param>
/// <param name="Owner">Its owner.</param>
/// <param name="Device">The owner's first device.</param>
public sealed record SetupResponse(OrganizationDto Organization, PersonDto Owner, DeviceDto Device);

/// <summary>The organization of the server.</summary>
/// <param name="Id">Its id.</param>
/// <param name="Name">Its name.</param>
/// <param name="CreatedAt">When it was set up.</param>
/// <param name="People">Its people.</param>
public sealed record OrganizationDto(string Id, string Name, DateTimeOffset CreatedAt, IReadOnlyList<PersonDto> People);

/// <summary>A person.</summary>
/// <param name="Id">Their id.</param>
/// <param name="DisplayName">Their name.</param>
/// <param name="Role">Their role: <c>owner</c> or <c>member</c>.</param>
public sealed record PersonDto(string Id, string DisplayName, string Role);

/// <summary>A device.</summary>
/// <param name="Id">Its id, derived from its key.</param>
/// <param name="Name">Its name.</param>
/// <param name="Kind">Its class: <c>controller</c> or <c>node</c>.</param>
/// <param name="PublicKey">Its public key.</param>
/// <param name="Fingerprint">The fingerprint of its key.</param>
/// <param name="Status">Its enrollment: <c>pending</c>, <c>approved</c> or <c>revoked</c>.</param>
/// <param name="OwnerId">The person who owns it.</param>
/// <param name="EnrolledBy">The controller device that let it in.</param>
/// <param name="CreatedAt">When it registered.</param>
/// <param name="ApprovedAt">When it joined the device list.</param>
/// <param name="RevokedAt">When it was revoked.</param>
/// <param name="Online">Whether it holds a control connection now.</param>
public sealed record DeviceDto(
    string Id,
    string Name,
    string Kind,
    string PublicKey,
    string Fingerprint,
    string Status,
    string? OwnerId,
    string? EnrolledBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ApprovedAt,
    DateTimeOffset? RevokedAt,
    bool Online);

/// <summary>A machine and its last inventory.</summary>
/// <param name="DeviceId">The device of the machine.</param>
/// <param name="Name">The name of the device.</param>
/// <param name="Kind">The class of the device.</param>
/// <param name="Status">The enrollment of the device.</param>
/// <param name="Online">Whether it is online.</param>
/// <param name="Inventory">Its inventory.</param>
/// <param name="UpdatedAt">When the inventory was received.</param>
public sealed record MachineDto(string DeviceId, string Name, string Kind, string Status, bool Online, FleetMachineInventory Inventory, DateTimeOffset UpdatedAt);

/// <summary>A published session: its facts and its sealed metadata.</summary>
/// <param name="Id">Its id.</param>
/// <param name="MachineId">The machine that owns it.</param>
/// <param name="Epoch">Its ownership epoch.</param>
/// <param name="Version">The version of its facts.</param>
/// <param name="State">Its state.</param>
/// <param name="CreatedAt">When it was created.</param>
/// <param name="UpdatedAt">When it last changed.</param>
/// <param name="ProviderType">The type of its provider.</param>
/// <param name="Model">Its model.</param>
/// <param name="Counts">Its counts.</param>
/// <param name="SealedMetadata">Its sealed metadata, opaque to the server.</param>
public sealed record SessionDto(
    string Id,
    string MachineId,
    long Epoch,
    long Version,
    string State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? ProviderType,
    string? Model,
    FleetSessionCounts Counts,
    string? SealedMetadata);

/// <summary>A page of a list. <see cref="Next"/> is the cursor of the next page, <see langword="null"/> at the end.</summary>
/// <typeparam name="T">The type of the items.</typeparam>
/// <param name="Items">The items.</param>
/// <param name="Next">The cursor of the next page.</param>
public sealed record ListResponse<T>(IReadOnlyList<T> Items, string? Next = null);

/// <summary>The body of <c>POST /api/v1/enrollment-tokens</c>.</summary>
/// <param name="TtlSeconds">How long the token works: 10 minutes to 24 hours; 1 hour when absent.</param>
/// <param name="Label">What the token is for.</param>
public sealed record EnrollmentTokenRequest(int? TtlSeconds = null, string? Label = null);

/// <summary>A new enrollment token: its value is shown once and never stored.</summary>
/// <param name="Id">The id of the token.</param>
/// <param name="Token">The value to give the machine.</param>
/// <param name="ExpiresAt">When it stops working.</param>
public sealed record EnrollmentTokenResponse(string Id, string Token, DateTimeOffset ExpiresAt);

/// <summary>The body of <c>POST /api/v1/enroll</c>, signed with the key it names.</summary>
/// <param name="Token">The enrollment token.</param>
/// <param name="PublicKey">The public key of the machine.</param>
/// <param name="DeviceName">Its name.</param>
/// <param name="Kind">
/// Its class (<see cref="FleetDeviceKinds"/>): <c>controller</c> for a person's desktop, which a controller device of the
/// same person approves and which then approves others; <c>node</c> (the default) for an execution device.
/// </param>
public sealed record EnrollRequest(string Token, string PublicKey, string DeviceName, string? Kind = null);

/// <summary>The answer of an enrollment.</summary>
/// <param name="Device">The device, pending until a controller device signs it into the device list.</param>
public sealed record EnrollResponse(DeviceDto Device);

/// <summary>The body of <c>POST /api/v1/devices/{id}/approve</c> and <c>/revoke</c>.</summary>
/// <param name="DeviceListEntry">The entry that adds (approve) or removes (revoke) the device, signed by the calling controller device;
/// a pending device is refused without one.</param>
public sealed record DeviceListChangeRequest(FleetSignedDeviceListEntry? DeviceListEntry);

/// <summary>An entry of the device list.</summary>
/// <param name="Sequence">Its position.</param>
/// <param name="Hash">Its hash, which the next entry names.</param>
/// <param name="Entry">The signed entry.</param>
/// <param name="RecordedAt">When the server recorded it.</param>
public sealed record DeviceListEntryDto(long Sequence, string Hash, FleetSignedDeviceListEntry Entry, DateTimeOffset RecordedAt);

/// <summary>A record of the audit log.</summary>
/// <param name="Sequence">Its position.</param>
/// <param name="RecordedAt">When it was recorded.</param>
/// <param name="Action">What was done.</param>
/// <param name="Result">How it ended.</param>
/// <param name="ActorPersonId">The person behind it.</param>
/// <param name="ActorDeviceId">The device that asked.</param>
/// <param name="Target">What it was done to.</param>
/// <param name="SourceAddress">Where the request came from.</param>
/// <param name="RequestId">The id of the request.</param>
/// <param name="Details">More facts, as JSON text.</param>
/// <param name="PreviousHash">The hash of the record before.</param>
/// <param name="Hash">Its hash.</param>
public sealed record AuditRecordDto(
    long Sequence,
    DateTimeOffset RecordedAt,
    string Action,
    string Result,
    string? ActorPersonId,
    string? ActorDeviceId,
    string? Target,
    string? SourceAddress,
    string? RequestId,
    string? Details,
    string PreviousHash,
    string Hash);

/// <summary>The check of the chain of the audit log.</summary>
/// <param name="Valid">Whether it holds.</param>
/// <param name="Count">How many records were checked.</param>
/// <param name="BrokenAt">The first record that does not hold.</param>
public sealed record AuditVerificationDto(bool Valid, long Count, long? BrokenAt);

/// <summary>The health of the server, for <c>altaserver status</c> and probes.</summary>
/// <param name="Status">Always <c>ok</c> when it answers.</param>
/// <param name="Version">The version of the server.</param>
/// <param name="SetUp">Whether it has an owner.</param>
/// <param name="SchemaVersion">The version of its database schema.</param>
public sealed record ServerHealthDto(string Status, string Version, bool SetUp, int SchemaVersion);

/// <summary>A refusal of the REST API, as problem details (RFC 9457) with the stable code and hint of the fleet.</summary>
/// <param name="Status">The HTTP status.</param>
/// <param name="Title">The title: the code.</param>
/// <param name="Detail">The description for people.</param>
/// <param name="Code">The stable code.</param>
/// <param name="Hint">What the client should do, or <see langword="null"/>.</param>
/// <param name="RequestId">The id of the request.</param>
public sealed record ApiProblemDto(int? Status, string? Title, string? Detail, string? Code, string? Hint, string? RequestId);

/// <summary>A copy of a metadata key wrapped to one device: only that device opens it.</summary>
/// <param name="DeviceId">The device the key is wrapped to.</param>
/// <param name="Wrapped">The wrapped key, opaque to the server (ECDH-ES P-256, HKDF-SHA256, AES-256-GCM; the algorithm id is inside).</param>
public sealed record MetadataKeyWrapDto(string DeviceId, string Wrapped);

/// <summary>
/// A metadata key of a person (<c>doc/fleet/security-and-privacy.md</c>, keys): it seals the descriptive metadata of the
/// sessions their devices publish (titles). The server keeps it wrapped to each of the person's devices and never in clear.
/// </summary>
/// <param name="Id">The id of the key, which sealed metadata names.</param>
/// <param name="Epoch">Its epoch: 1 until keys rotate.</param>
/// <param name="CreatedByDeviceId">The controller device that made it.</param>
/// <param name="CreatedAt">When it was made.</param>
/// <param name="Wrapped">The copy wrapped to the calling device, or <see langword="null"/> when it has none yet.</param>
/// <param name="WrappedTo">The devices that have a copy.</param>
public sealed record MetadataKeyDto(string Id, int Epoch, string CreatedByDeviceId, DateTimeOffset CreatedAt, string? Wrapped, IReadOnlyList<string> WrappedTo);

/// <summary>The body of <c>POST /api/v1/metadata-keys</c>: a new key of the calling device's person, wrapped to its devices, the caller included.</summary>
/// <param name="Id">The id of the key, made by the device: 22 characters of base64url.</param>
/// <param name="Epoch">Its epoch: 1.</param>
/// <param name="Wraps">Its copies, one per device.</param>
public sealed record CreateMetadataKeyRequest(string Id, int Epoch, IReadOnlyList<MetadataKeyWrapDto> Wraps);

/// <summary>The body of <c>POST /api/v1/metadata-keys/{id}/wraps</c>: copies of the key for devices that have none.</summary>
/// <param name="Wraps">The copies.</param>
public sealed record AddMetadataKeyWrapsRequest(IReadOnlyList<MetadataKeyWrapDto> Wraps);

/// <summary>
/// The telemetry setting of the fleet as the server holds it (<c>GET /api/v1/telemetry</c>, for the owner's controller
/// devices): the setting a controller device signed, and the headers of the collector only the server holds (those of
/// the server route).
/// </summary>
/// <param name="Settings">The signed setting, or null when the fleet has none.</param>
/// <param name="ServerHeaders">The headers the server sends to the collector on the server route, or null.</param>
/// <param name="UpdatedAt">When it was last set, or null.</param>
public sealed record TelemetrySettingsDto(FleetSignedTelemetrySettings? Settings, IReadOnlyDictionary<string, string>? ServerHeaders, DateTimeOffset? UpdatedAt);

/// <summary>The body of <c>PUT /api/v1/telemetry</c>: a setting the calling controller device signed, numbered after the one held.</summary>
/// <param name="Settings">The signed setting.</param>
/// <param name="ServerHeaders">The headers the server sends to the collector on the server route (API keys); never pushed to machines.</param>
public sealed record SetTelemetrySettingsRequest(FleetSignedTelemetrySettings Settings, IReadOnlyDictionary<string, string>? ServerHeaders = null);

/// <summary>The source-generated contracts of the REST API.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SetupRequest))]
[JsonSerializable(typeof(SetupResponse))]
[JsonSerializable(typeof(OrganizationDto))]
[JsonSerializable(typeof(PersonDto))]
[JsonSerializable(typeof(DeviceDto))]
[JsonSerializable(typeof(ListResponse<DeviceDto>))]
[JsonSerializable(typeof(MachineDto))]
[JsonSerializable(typeof(ListResponse<MachineDto>))]
[JsonSerializable(typeof(SessionDto))]
[JsonSerializable(typeof(ListResponse<SessionDto>))]
[JsonSerializable(typeof(EnrollmentTokenRequest))]
[JsonSerializable(typeof(EnrollmentTokenResponse))]
[JsonSerializable(typeof(EnrollRequest))]
[JsonSerializable(typeof(EnrollResponse))]
[JsonSerializable(typeof(DeviceListChangeRequest))]
[JsonSerializable(typeof(DeviceListEntryDto))]
[JsonSerializable(typeof(ListResponse<DeviceListEntryDto>))]
[JsonSerializable(typeof(AuditRecordDto))]
[JsonSerializable(typeof(ListResponse<AuditRecordDto>))]
[JsonSerializable(typeof(AuditVerificationDto))]
[JsonSerializable(typeof(ServerHealthDto))]
[JsonSerializable(typeof(ApiProblemDto))]
[JsonSerializable(typeof(FleetMachineInventory))]
[JsonSerializable(typeof(MetadataKeyDto))]
[JsonSerializable(typeof(ListResponse<MetadataKeyDto>))]
[JsonSerializable(typeof(CreateMetadataKeyRequest))]
[JsonSerializable(typeof(AddMetadataKeyWrapsRequest))]
[JsonSerializable(typeof(TelemetrySettingsDto))]
[JsonSerializable(typeof(SetTelemetrySettingsRequest))]
public sealed partial class ServerApiJsonContext : JsonSerializerContext;
