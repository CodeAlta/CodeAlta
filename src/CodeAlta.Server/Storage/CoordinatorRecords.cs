namespace CodeAlta.Server.Storage;

/// <summary>The organization a server hosts: a household or a company (one per server).</summary>
/// <param name="Id">The id of the organization.</param>
/// <param name="Name">Its name.</param>
/// <param name="CreatedAt">When it was set up.</param>
public sealed record OrganizationRecord(string Id, string Name, DateTimeOffset CreatedAt);

/// <summary>The roles of a person (<see cref="CodeAlta.Fleet.Protocol.FleetOrganizationRoles"/>), as the store keeps them.</summary>
public static class PersonRoles
{
    /// <summary>An owner of the organization: the person who set the server up, and those an owner makes owners.</summary>
    public const string Owner = CodeAlta.Fleet.Protocol.FleetOrganizationRoles.Owner;

    /// <summary>An admin of the organization.</summary>
    public const string Admin = CodeAlta.Fleet.Protocol.FleetOrganizationRoles.Admin;

    /// <summary>A member of the organization.</summary>
    public const string Member = CodeAlta.Fleet.Protocol.FleetOrganizationRoles.Member;
}

/// <summary>A person of the organization.</summary>
/// <param name="Id">The id of the person.</param>
/// <param name="OrganizationId">Their organization.</param>
/// <param name="DisplayName">The name shown for them.</param>
/// <param name="Role">Their role (<see cref="PersonRoles"/>).</param>
/// <param name="CreatedAt">When they were added.</param>
/// <param name="Status">Whether they act (<see cref="CodeAlta.Fleet.Protocol.FleetPersonStatuses"/>).</param>
/// <param name="SuspendedAt">When they were suspended; null while they act.</param>
public sealed record PersonRecord(string Id, string OrganizationId, string DisplayName, string Role, DateTimeOffset CreatedAt,
    string Status = CodeAlta.Fleet.Protocol.FleetPersonStatuses.Active, DateTimeOffset? SuspendedAt = null)
{
    /// <summary>Gets whether the person acts: not suspended.</summary>
    public bool IsActive => Status == CodeAlta.Fleet.Protocol.FleetPersonStatuses.Active;
}

/// <summary>The states of the enrollment of a device; its connection (online or not) is presence, kept in memory.</summary>
public static class DeviceStatuses
{
    /// <summary>Registered with an enrollment token, waiting for a controller device to sign it into the device list.</summary>
    public const string Pending = "pending";

    /// <summary>A member of the device list.</summary>
    public const string Approved = "approved";

    /// <summary>Removed from the device list, or refused while pending.</summary>
    public const string Revoked = "revoked";
}

/// <summary>A device of the organization: a controller device or a node, known by its key.</summary>
/// <param name="Id">The id of the device, derived from its key.</param>
/// <param name="OrganizationId">Its organization.</param>
/// <param name="Name">Its name.</param>
/// <param name="Kind">Its class (<see cref="CodeAlta.Fleet.Protocol.FleetDeviceKinds"/>).</param>
/// <param name="PublicKey">Its public key, the base64 of its DER SubjectPublicKeyInfo.</param>
/// <param name="Fingerprint">The SHA-256 of its key, base64url.</param>
/// <param name="OwnerPersonId">The person who owns it, once known.</param>
/// <param name="Status">The state of its enrollment (<see cref="DeviceStatuses"/>).</param>
/// <param name="EnrolledByDeviceId">The controller device that let it in: the creator of its enrollment token, or the device itself at setup.</param>
/// <param name="EnrollmentTokenId">The enrollment token it used, if any.</param>
/// <param name="CreatedAt">When it registered.</param>
/// <param name="ApprovedAt">When it joined the device list.</param>
/// <param name="RevokedAt">When it was revoked.</param>
/// <param name="Version">The version of the record, raised by each change.</param>
public sealed record DeviceRecord(
    string Id,
    string OrganizationId,
    string Name,
    string Kind,
    string PublicKey,
    string Fingerprint,
    string? OwnerPersonId,
    string Status,
    string? EnrolledByDeviceId,
    string? EnrollmentTokenId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ApprovedAt,
    DateTimeOffset? RevokedAt,
    long Version);

/// <summary>An entry of the signed device list, as stored.</summary>
/// <param name="OrganizationId">The organization of the list.</param>
/// <param name="Sequence">Its position, from 1.</param>
/// <param name="DeviceId">The device it adds or removes.</param>
/// <param name="Operation">What it does (<see cref="CodeAlta.Fleet.Protocol.FleetDeviceListOperations"/>).</param>
/// <param name="SignerDeviceId">The controller device that signed it.</param>
/// <param name="Payload">The JSON text that was signed.</param>
/// <param name="Signature">The signature.</param>
/// <param name="Hash">Its hash, which the next entry names.</param>
/// <param name="RecordedAt">When the server recorded it.</param>
public sealed record DeviceListEntryRecord(
    string OrganizationId,
    long Sequence,
    string DeviceId,
    string Operation,
    string SignerDeviceId,
    string Payload,
    string Signature,
    string Hash,
    DateTimeOffset RecordedAt);

/// <summary>An enrollment token: it lets one machine register its key as a pending device, once, until it expires.</summary>
/// <param name="Id">The id of the token.</param>
/// <param name="OrganizationId">Its organization.</param>
/// <param name="TokenHash">The SHA-256 of the token; the token itself is shown once and never stored.</param>
/// <param name="Label">What the token is for, or <see langword="null"/>.</param>
/// <param name="OwnerPersonId">The person the enrolled machine will belong to.</param>
/// <param name="CreatedByDeviceId">The controller device that made it.</param>
/// <param name="CreatedAt">When it was made.</param>
/// <param name="ExpiresAt">When it stops working.</param>
/// <param name="UsedAt">When it was used, if it was.</param>
/// <param name="UsedByDeviceId">The device that used it.</param>
public sealed record EnrollmentTokenRecord(
    string Id,
    string OrganizationId,
    string TokenHash,
    string? Label,
    string? OwnerPersonId,
    string CreatedByDeviceId,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? UsedAt,
    string? UsedByDeviceId);

/// <summary>A machine: the last inventory snapshot a device published.</summary>
/// <param name="DeviceId">The device of the machine.</param>
/// <param name="OrganizationId">Its organization.</param>
/// <param name="Inventory">The snapshot, as JSON text.</param>
/// <param name="InventorySequence">The sequence of the publication it came with.</param>
/// <param name="UpdatedAt">When it was received.</param>
public sealed record MachineRecord(string DeviceId, string OrganizationId, string Inventory, long InventorySequence, DateTimeOffset UpdatedAt);

/// <summary>The published facts of a session: the allow-list, and the sealed metadata as opaque text.</summary>
/// <param name="Id">The id of the session.</param>
/// <param name="OrganizationId">Its organization.</param>
/// <param name="MachineId">The device of the machine that owns it.</param>
/// <param name="Epoch">Its ownership epoch.</param>
/// <param name="Version">The version of the facts in the epoch.</param>
/// <param name="State">Its state.</param>
/// <param name="CreatedAt">When it was created.</param>
/// <param name="UpdatedAt">When it last changed.</param>
/// <param name="ProviderType">The type of its provider.</param>
/// <param name="Model">Its model.</param>
/// <param name="Turns">Its turns.</param>
/// <param name="ToolCalls">Its tool calls.</param>
/// <param name="InputTokens">Its input tokens.</param>
/// <param name="OutputTokens">Its output tokens.</param>
/// <param name="SealedMetadata">Its sealed metadata, which the server cannot read.</param>
/// <param name="ReceivedAt">When the server received this version.</param>
public sealed record SessionRecord(
    string Id,
    string OrganizationId,
    string MachineId,
    long Epoch,
    long Version,
    string State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? ProviderType,
    string? Model,
    long Turns,
    long ToolCalls,
    long InputTokens,
    long OutputTokens,
    string? SealedMetadata,
    DateTimeOffset ReceivedAt);

/// <summary>A metadata key of a space of a person: what the server knows of it, never the key itself.</summary>
/// <param name="Id">The id of the key.</param>
/// <param name="OrganizationId">Its organization.</param>
/// <param name="PersonId">The person whose devices hold it.</param>
/// <param name="Epoch">Its epoch, counted per space.</param>
/// <param name="CreatedByDeviceId">The controller device that made it.</param>
/// <param name="CreatedAt">When it was made.</param>
/// <param name="SpaceId">The opaque id of its space; empty for a key of the person made before spaces had keys.</param>
/// <param name="SealedDescriptor">Which space it is on the devices, sealed with the key; null for a key of the person.</param>
public sealed record MetadataKeyRecord(string Id, string OrganizationId, string PersonId, int Epoch, string CreatedByDeviceId, DateTimeOffset CreatedAt,
    string SpaceId = "", string? SealedDescriptor = null);

/// <summary>The recovery key of a person, as the server keeps it: its device in the list and the anchor it sealed.</summary>
/// <param name="DeviceId">The device id the recovery key has in the device list.</param>
/// <param name="OrganizationId">Its organization.</param>
/// <param name="PersonId">Its person.</param>
/// <param name="SealedAnchor">The anchor of the device list, sealed with the recovery key.</param>
/// <param name="CreatedAt">When it was made.</param>
public sealed record RecoveryKeyRecord(string DeviceId, string OrganizationId, string PersonId, string SealedAnchor, DateTimeOffset CreatedAt);

/// <summary>A metadata key wrapped to one device, as opaque text.</summary>
/// <param name="KeyId">The key.</param>
/// <param name="DeviceId">The device it is wrapped to.</param>
/// <param name="Wrapped">The wrapped key.</param>
/// <param name="WrappedByDeviceId">The controller device that wrapped it.</param>
/// <param name="CreatedAt">When it was given.</param>
public sealed record MetadataKeyWrapRecord(string KeyId, string DeviceId, string Wrapped, string WrappedByDeviceId, DateTimeOffset CreatedAt);

/// <summary>A metadata key and its wraps.</summary>
/// <param name="Key">The key.</param>
/// <param name="Wraps">Its copies, one per device.</param>
public sealed record MetadataKeyWithWraps(MetadataKeyRecord Key, IReadOnlyList<MetadataKeyWrapRecord> Wraps);

/// <summary>An event of the audit log to append, without content.</summary>
/// <param name="Action">What was done, such as <c>device.approve</c>.</param>
/// <param name="Result">How it ended: <c>success</c> or a stable error code.</param>
/// <param name="ActorPersonId">The person behind it.</param>
/// <param name="ActorDeviceId">The device that asked.</param>
/// <param name="Target">What it was done to, such as a device id.</param>
/// <param name="SourceAddress">The address the request came from.</param>
/// <param name="RequestId">The id of the request.</param>
/// <param name="Details">More facts, as JSON text, never content.</param>
public sealed record AuditEntry(
    string Action,
    string Result,
    string? ActorPersonId,
    string? ActorDeviceId,
    string? Target,
    string? SourceAddress,
    string? RequestId,
    string? Details = null)
{
    /// <summary>The result of an action that succeeded.</summary>
    public const string Success = "success";
}

/// <summary>A record of the audit log, chained to the one before it.</summary>
/// <param name="Sequence">Its position, from 1.</param>
/// <param name="RecordedAt">When it was recorded.</param>
/// <param name="Entry">What it records.</param>
/// <param name="PreviousHash">The hash of the record before it, empty for the first one.</param>
/// <param name="Hash">Its hash (<see cref="AuditChain.ComputeHash"/>).</param>
public sealed record AuditRecord(long Sequence, DateTimeOffset RecordedAt, AuditEntry Entry, string PreviousHash, string Hash);

/// <summary>The result of checking the chain of the audit log.</summary>
/// <param name="IsValid">Whether every record names the hash of the one before and has its own hash.</param>
/// <param name="Count">How many records were checked.</param>
/// <param name="BrokenAt">The sequence of the first record that does not hold, or <see langword="null"/>.</param>
public sealed record AuditVerification(bool IsValid, long Count, long? BrokenAt);

/// <summary>What happened to an enrollment.</summary>
public enum EnrollmentOutcome
{
    /// <summary>The device was registered as pending and the token used.</summary>
    Enrolled,

    /// <summary>No token has that value.</summary>
    TokenUnknown,

    /// <summary>The token was already used.</summary>
    TokenUsed,

    /// <summary>The token expired.</summary>
    TokenExpired,

    /// <summary>A device with that key is already registered.</summary>
    DeviceExists,
}

/// <summary>What happened to an entry appended to the device list.</summary>
public enum DeviceListAppendOutcome
{
    /// <summary>The entry was appended and the device changed.</summary>
    Appended,

    /// <summary>The list moved on: the entry does not follow its last entry.</summary>
    Conflict,

    /// <summary>The device is not in the state the entry expects.</summary>
    DeviceStateChanged,
}

/// <summary>What happened to one published session fact.</summary>
public enum SessionFactOutcome
{
    /// <summary>It changed what the server holds.</summary>
    Applied,

    /// <summary>The server already held this version or a newer one.</summary>
    Unchanged,

    /// <summary>Refused: an older epoch, or another machine at the same epoch.</summary>
    Stale,
}

/// <summary>The result of a publication of session facts.</summary>
/// <param name="Ack">The acknowledged sequence of the stream.</param>
/// <param name="Outcomes">What happened to each fact, in order, with the record the server holds after it.</param>
public sealed record SessionFactsPublication(long Ack, IReadOnlyList<(SessionRecord Record, SessionFactOutcome Outcome)> Outcomes);

/// <summary>The result of a publication of an inventory.</summary>
/// <param name="Ack">The acknowledged sequence of the stream.</param>
/// <param name="Applied">Whether the snapshot replaced the one the server held.</param>
public sealed record InventoryPublication(long Ack, bool Applied);

/// <summary>The telemetry setting of an organization, as a controller device signed it, and the headers only the server holds.</summary>
/// <param name="OrganizationId">The organization.</param>
/// <param name="Sequence">The sequence of the setting.</param>
/// <param name="Payload">The JSON text that was signed.</param>
/// <param name="Signature">The signature.</param>
/// <param name="SignerDeviceId">The controller device that signed it.</param>
/// <param name="ServerHeaders">The headers the server sends to the collector on the server route, as JSON, or null.</param>
/// <param name="UpdatedAt">When it was set.</param>
public sealed record TelemetrySettingsRecord(string OrganizationId, long Sequence, string Payload, string Signature, string SignerDeviceId, string? ServerHeaders,
    DateTimeOffset UpdatedAt);

/// <summary>An update campaign of an organization.</summary>
/// <param name="Id">The id of the campaign.</param>
/// <param name="OrganizationId">The organization.</param>
/// <param name="Sequence">The sequence of its manifest; for an automatic campaign, the sequence of its policy.</param>
/// <param name="Automatic">Whether the automatic policy started it.</param>
/// <param name="TargetVersion">The version it installs.</param>
/// <param name="Feed">The feed it reads.</param>
/// <param name="Rollback">Whether a person chose an older version.</param>
/// <param name="NotBefore">When it starts, or null for at once.</param>
/// <param name="TimeZone">The time zone the time was chosen in, for display.</param>
/// <param name="Payload">The manifest as signed; for an automatic campaign, the policy as signed.</param>
/// <param name="Signature">Its signature.</param>
/// <param name="CreatedByDeviceId">The controller device that signed the manifest; null for an automatic campaign.</param>
/// <param name="State">Its state (<see cref="Fleet.Protocol.FleetUpdateCampaignStates"/>).</param>
/// <param name="ServerState">Where the server's own update stands (<see cref="Fleet.Protocol.FleetUpdateMachineStates"/>).</param>
/// <param name="ServerMessage">What happened to the server's update, or null.</param>
/// <param name="CreatedAt">When the server received it.</param>
/// <param name="UpdatedAt">When it last changed.</param>
public sealed record UpdateCampaignRecord(string Id, string OrganizationId, long Sequence, bool Automatic, string TargetVersion, string Feed, bool Rollback,
    DateTimeOffset? NotBefore, string? TimeZone, string Payload, string Signature, string? CreatedByDeviceId, string State, string ServerState, string? ServerMessage,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>A machine of an update campaign.</summary>
/// <param name="CampaignId">The campaign.</param>
/// <param name="DeviceId">The device of the node.</param>
/// <param name="State">Where it stands (<see cref="Fleet.Protocol.FleetUpdateMachineStates"/>).</param>
/// <param name="Running">The version it last said it runs, or null.</param>
/// <param name="Message">What happened, for a failure or a refusal; null otherwise.</param>
/// <param name="InstructedAt">When the server last sent it the instruction, or null.</param>
/// <param name="UpdatedAt">When its state last changed.</param>
/// <param name="CancelPending">Whether the node was asked before the campaign was cancelled and has not acknowledged the cancellation yet.</param>
public sealed record UpdateCampaignMachineRecord(string CampaignId, string DeviceId, string State, string? Running, string? Message, DateTimeOffset? InstructedAt,
    DateTimeOffset UpdatedAt, bool CancelPending = false);

/// <summary>The update policy of an organization, as a controller device signed it, and its last check of the feed.</summary>
/// <param name="OrganizationId">The organization.</param>
/// <param name="Sequence">The sequence of the policy.</param>
/// <param name="Payload">The JSON text that was signed.</param>
/// <param name="Signature">The signature.</param>
/// <param name="SignerDeviceId">The controller device that signed it.</param>
/// <param name="LastCheckAt">When an automatic policy last read its feed, or null.</param>
/// <param name="LastCheckMessage">What that check found, or null.</param>
/// <param name="UpdatedAt">When it was set.</param>
public sealed record UpdatePolicyRecord(string OrganizationId, long Sequence, string Payload, string Signature, string SignerDeviceId, DateTimeOffset? LastCheckAt,
    string? LastCheckMessage, DateTimeOffset UpdatedAt);
