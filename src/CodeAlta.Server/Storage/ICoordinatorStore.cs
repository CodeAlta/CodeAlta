using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;

namespace CodeAlta.Server.Storage;

/// <summary>
/// What the coordinator keeps: organizations, people, devices and their signed list, enrollment tokens, machines,
/// the facts of published sessions and the audit log.
/// </summary>
/// <remarks>
/// <para>
/// Operations are those of the domain, not SQL, so that SQLite (<see cref="Sqlite.SqliteCoordinatorStore"/>, the default)
/// and PostgreSQL (to come, behind the same interface and the same contract tests) implement them each in their own
/// dialect. Every mutation that a person or a device asks for takes the <see cref="AuditEntry"/> it records, appended
/// in the same transaction: a change is never kept without its record, nor a record without its change.
/// </para>
/// <para>
/// Writes run one at a time; reads never wait for them. Presence is not here: it lives in memory, and a heartbeat is
/// never a write.
/// </para>
/// </remarks>
public interface ICoordinatorStore : IStatisticsFactStore, IAsyncDisposable
{
    /// <summary>Opens the store and brings its schema to the current version, each migration in a transaction.</summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when the store is ready.</returns>
    ValueTask InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>Gets the version of the schema: the number of the last migration applied, 0 for none.</summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The version.</returns>
    ValueTask<int> GetSchemaVersionAsync(CancellationToken cancellationToken = default);

    /// <summary>Gets the organization of the server, or <see langword="null"/> before its setup.</summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The organization, or <see langword="null"/>.</returns>
    ValueTask<OrganizationRecord?> GetOrganizationAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the server up: its organization, its owner, the owner's first controller device and the first entry of the
    /// device list, unless an organization exists.
    /// </summary>
    /// <param name="organization">The organization.</param>
    /// <param name="owner">The owner.</param>
    /// <param name="device">The owner's first device, approved.</param>
    /// <param name="firstEntry">The first entry of the device list, signed by that device.</param>
    /// <param name="audit">The audit record of the setup.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns><see langword="false"/> when the server already has an organization: nothing was written.</returns>
    ValueTask<bool> CreateOrganizationAsync(OrganizationRecord organization, PersonRecord owner, DeviceRecord device, DeviceListEntryRecord firstEntry,
        AuditEntry audit, CancellationToken cancellationToken = default);

    /// <summary>Gets a person.</summary>
    /// <param name="personId">The id of the person.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The person, or <see langword="null"/>.</returns>
    ValueTask<PersonRecord?> GetPersonAsync(string personId, CancellationToken cancellationToken = default);

    /// <summary>Lists the people of an organization.</summary>
    /// <param name="organizationId">The organization.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The people, oldest first.</returns>
    ValueTask<IReadOnlyList<PersonRecord>> ListPeopleAsync(string organizationId, CancellationToken cancellationToken = default);

    /// <summary>Gets a device.</summary>
    /// <param name="deviceId">The id of the device.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The device, or <see langword="null"/>.</returns>
    ValueTask<DeviceRecord?> GetDeviceAsync(string deviceId, CancellationToken cancellationToken = default);

    /// <summary>Gets a device as a request it signs needs it: the device, and whether its person is suspended, in one read.</summary>
    /// <param name="deviceId">The id of the device.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The device, or <see langword="null"/>, and whether the person who owns it is suspended.</returns>
    ValueTask<(DeviceRecord? Device, bool PersonSuspended)> GetDeviceOfRequestAsync(string deviceId, CancellationToken cancellationToken = default);

    /// <summary>Lists the devices of an organization.</summary>
    /// <param name="organizationId">The organization.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The devices, oldest first.</returns>
    ValueTask<IReadOnlyList<DeviceRecord>> ListDevicesAsync(string organizationId, CancellationToken cancellationToken = default);

    /// <summary>Lists the signed device list of an organization.</summary>
    /// <param name="organizationId">The organization.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The entries, by sequence.</returns>
    ValueTask<IReadOnlyList<DeviceListEntryRecord>> ListDeviceListAsync(string organizationId, CancellationToken cancellationToken = default);

    /// <summary>Gets the last entry of the device list of an organization.</summary>
    /// <param name="organizationId">The organization.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The entry, or <see langword="null"/> for an empty list.</returns>
    ValueTask<DeviceListEntryRecord?> GetLastDeviceListEntryAsync(string organizationId, CancellationToken cancellationToken = default);

    /// <summary>Records a new enrollment token.</summary>
    /// <param name="token">The token, with the hash of its value.</param>
    /// <param name="audit">The audit record.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when it is recorded.</returns>
    ValueTask CreateEnrollmentTokenAsync(EnrollmentTokenRecord token, AuditEntry audit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Uses an enrollment token to register a pending device, once: the token is marked used in the same transaction,
    /// so two machines racing with one token cannot both enroll.
    /// </summary>
    /// <param name="tokenHash">The hash of the token's value.</param>
    /// <param name="device">The device to register; its organization, owner and enroller are taken from the token.</param>
    /// <param name="now">The time of the enrollment, against which the token's expiry is checked.</param>
    /// <param name="audit">The audit record of a success; a refusal is audited by the caller.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>What happened, and the device as registered when it was.</returns>
    ValueTask<(EnrollmentOutcome Outcome, DeviceRecord? Device)> EnrollDeviceAsync(string tokenHash, DeviceRecord device, DateTimeOffset now, AuditEntry audit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends a signed entry to the device list and changes the device it names, if the entry follows the last one
    /// (its sequence and previous hash) and the device is in the state the entry expects.
    /// </summary>
    /// <param name="entry">The entry, verified by the caller.</param>
    /// <param name="expectedStatus">The status the device must have.</param>
    /// <param name="newStatus">The status it gets.</param>
    /// <param name="ownerPersonId">The owner the device gets, or <see langword="null"/> to keep its owner.</param>
    /// <param name="audit">The audit record.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>What happened, and the device after it.</returns>
    ValueTask<(DeviceListAppendOutcome Outcome, DeviceRecord? Device)> AppendDeviceListEntryAsync(DeviceListEntryRecord entry, string expectedStatus, string newStatus,
        string? ownerPersonId, AuditEntry audit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a device that joins the device list without enrolling (a recovery key, or the device a recovery key signs
    /// in), approved, with the signed entry that adds it, if the entry follows the last one and no device has its key.
    /// </summary>
    /// <param name="device">The device, approved.</param>
    /// <param name="entry">The entry that adds it, verified by the caller.</param>
    /// <param name="recovery">The recovery key it is, with its sealed anchor; null for a device.</param>
    /// <param name="audit">The audit record.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>What happened (<see cref="DeviceListAppendOutcome.DeviceStateChanged"/> when the device exists), and the device.</returns>
    ValueTask<(DeviceListAppendOutcome Outcome, DeviceRecord? Device)> AddDeviceWithEntryAsync(DeviceRecord device, DeviceListEntryRecord entry, RecoveryKeyRecord? recovery,
        AuditEntry audit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Withdraws what a device that leaves the server published and was given there, in one transaction: the facts of its
    /// sessions with their sealed metadata, its statistics, its inventory and cursors, the copies of keys wrapped to it,
    /// the automations given to it and the runs it reported; the device is revoked, with the entry that removes it from the
    /// device list when one is given (if it follows the last one).
    /// </summary>
    /// <param name="deviceId">The device.</param>
    /// <param name="entry">The entry that removes it, verified by the caller; null to keep the list as it is.</param>
    /// <param name="now">The time of the departure.</param>
    /// <param name="audit">The audit record.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>What was withdrawn; null when the entry does not follow the last one (nothing changed).</returns>
    ValueTask<LeaveResponse?> LeaveAsync(string deviceId, DeviceListEntryRecord? entry, DateTimeOffset now, AuditEntry audit, CancellationToken cancellationToken = default);

    /// <summary>Gets the recovery key a device of the list is.</summary>
    /// <param name="deviceId">The device id of the recovery key.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The recovery key, or <see langword="null"/>.</returns>
    ValueTask<RecoveryKeyRecord?> GetRecoveryKeyAsync(string deviceId, CancellationToken cancellationToken = default);

    /// <summary>Refuses a pending device: it becomes revoked without an entry in the device list, which it never joined.</summary>
    /// <param name="deviceId">The device.</param>
    /// <param name="now">The time of the refusal.</param>
    /// <param name="audit">The audit record.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The device after it, or <see langword="null"/> when it is not pending.</returns>
    ValueTask<DeviceRecord?> RefusePendingDeviceAsync(string deviceId, DateTimeOffset now, AuditEntry audit, CancellationToken cancellationToken = default);

    /// <summary>Keeps the inventory a device published, if its sequence is newer than the one held.</summary>
    /// <param name="machine">The machine and its snapshot.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The acknowledged sequence, and whether the snapshot was kept.</returns>
    ValueTask<InventoryPublication> PublishInventoryAsync(MachineRecord machine, CancellationToken cancellationToken = default);

    /// <summary>
    /// Keeps the session facts a machine published: per session the highest <c>(epoch, version)</c>; a fact of an older
    /// epoch, or of another machine at the same epoch, is refused. Publishing the same batch again changes nothing.
    /// </summary>
    /// <param name="deviceId">The machine that publishes.</param>
    /// <param name="sequence">The sequence of the batch on the machine's stream.</param>
    /// <param name="facts">The facts, with <see cref="SessionRecord.MachineId"/> set to <paramref name="deviceId"/>.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The acknowledged sequence and what happened to each fact.</returns>
    ValueTask<SessionFactsPublication> PublishSessionFactsAsync(string deviceId, long sequence, IReadOnlyList<SessionRecord> facts, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the facts of sessions a machine withdraws, those it owns only (a session another machine owns now stays),
    /// and appends the audit record of the deletion when something was deleted.
    /// </summary>
    /// <param name="deviceId">The machine that withdraws.</param>
    /// <param name="sessionIds">The sessions.</param>
    /// <param name="audit">The audit record, whose details the store sets to the sessions deleted.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The facts deleted, as they were held: their epoch and version name what was removed.</returns>
    ValueTask<IReadOnlyList<SessionRecord>> WithdrawSessionFactsAsync(string deviceId, IReadOnlyList<string> sessionIds, AuditEntry audit, CancellationToken cancellationToken = default);

    /// <summary>Gets the cursors of the publications of a device.</summary>
    /// <param name="deviceId">The device.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The last acknowledged sequences, 0 for a stream that never published.</returns>
    ValueTask<FleetPublicationCursors> GetPublicationCursorsAsync(string deviceId, CancellationToken cancellationToken = default);

    /// <summary>Lists the machines of an organization.</summary>
    /// <param name="organizationId">The organization.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The machines.</returns>
    ValueTask<IReadOnlyList<MachineRecord>> ListMachinesAsync(string organizationId, CancellationToken cancellationToken = default);

    /// <summary>Lists the published sessions of an organization.</summary>
    /// <param name="organizationId">The organization.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The sessions, most recently updated first.</returns>
    ValueTask<IReadOnlyList<SessionRecord>> ListSessionsAsync(string organizationId, CancellationToken cancellationToken = default);

    /// <summary>Records a new metadata key of a person with its first wraps, unless the person has a key at that epoch.</summary>
    /// <param name="key">The key.</param>
    /// <param name="wraps">Its wraps.</param>
    /// <param name="audit">The audit record.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns><see langword="false"/> when the person has a key at that epoch already: nothing was written.</returns>
    ValueTask<bool> CreateMetadataKeyAsync(MetadataKeyRecord key, IReadOnlyList<MetadataKeyWrapRecord> wraps, AuditEntry audit, CancellationToken cancellationToken = default);

    /// <summary>Adds wraps of a metadata key for devices that have none; a device that has one keeps it. Audited when one was added.</summary>
    /// <param name="wraps">The wraps.</param>
    /// <param name="audit">The audit record, whose details the store sets to the devices given a wrap.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The devices given a wrap.</returns>
    ValueTask<IReadOnlyList<string>> AddMetadataKeyWrapsAsync(IReadOnlyList<MetadataKeyWrapRecord> wraps, AuditEntry audit, CancellationToken cancellationToken = default);

    /// <summary>Lists the metadata keys of a person and their wraps.</summary>
    /// <param name="personId">The person.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The keys, by epoch.</returns>
    ValueTask<IReadOnlyList<MetadataKeyWithWraps>> ListMetadataKeysAsync(string personId, CancellationToken cancellationToken = default);

    /// <summary>Gets the telemetry setting of an organization.</summary>
    /// <param name="organizationId">The organization.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The setting, or <see langword="null"/> when it has none.</returns>
    ValueTask<TelemetrySettingsRecord?> GetTelemetrySettingsAsync(string organizationId, CancellationToken cancellationToken = default);

    /// <summary>Replaces the telemetry setting of an organization with a newer one, audited in the same transaction.</summary>
    /// <param name="settings">The setting.</param>
    /// <param name="audit">The audit record.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns><see langword="false"/> when the setting held has the same sequence or a later one: nothing was written.</returns>
    ValueTask<bool> SetTelemetrySettingsAsync(TelemetrySettingsRecord settings, AuditEntry audit, CancellationToken cancellationToken = default);

    /// <summary>Lists the update campaigns of an organization, the newest first.</summary>
    /// <param name="organizationId">The organization.</param>
    /// <param name="limit">The most campaigns.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The campaigns.</returns>
    ValueTask<IReadOnlyList<UpdateCampaignRecord>> ListUpdateCampaignsAsync(string organizationId, int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the update campaigns of an organization that still have work, however old, the newest first: the active ones
    /// (scheduled or updating), and the partly done ones with a machine not done with them or whose machines all run the
    /// version (their next step makes them done).
    /// </summary>
    /// <param name="organizationId">The organization.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The campaigns.</returns>
    ValueTask<IReadOnlyList<UpdateCampaignRecord>> ListOutstandingUpdateCampaignsAsync(string organizationId, CancellationToken cancellationToken = default);

    /// <summary>Lists the campaigns the automatic policy of an organization started to a version, however old, the newest first.</summary>
    /// <param name="organizationId">The organization.</param>
    /// <param name="targetVersion">The version, normalized as the campaigns record it.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The campaigns.</returns>
    ValueTask<IReadOnlyList<UpdateCampaignRecord>> ListAutomaticUpdateCampaignsAsync(string organizationId, string targetVersion, CancellationToken cancellationToken = default);

    /// <summary>Gets an update campaign.</summary>
    /// <param name="campaignId">The campaign.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The campaign, or <see langword="null"/>.</returns>
    ValueTask<UpdateCampaignRecord?> GetUpdateCampaignAsync(string campaignId, CancellationToken cancellationToken = default);

    /// <summary>Lists the machines of an update campaign.</summary>
    /// <param name="campaignId">The campaign.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The machines, by device id.</returns>
    ValueTask<IReadOnlyList<UpdateCampaignMachineRecord>> ListUpdateCampaignMachinesAsync(string campaignId, CancellationToken cancellationToken = default);

    /// <summary>Lists the cancelled campaigns a node has not acknowledged the cancellation of yet, the oldest first.</summary>
    /// <param name="organizationId">The organization.</param>
    /// <param name="deviceId">The device of the node.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The ids of the campaigns.</returns>
    ValueTask<IReadOnlyList<string>> ListUpdateCancellationsAsync(string organizationId, string deviceId, CancellationToken cancellationToken = default);

    /// <summary>Gets the highest sequence of the manifests of an organization's campaigns.</summary>
    /// <param name="organizationId">The organization.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The sequence, 0 before the first campaign.</returns>
    ValueTask<long> GetLastUpdateSequenceAsync(string organizationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a new campaign and its machines, audited in the same transaction; the machines still waiting in an older
    /// campaign are superseded by it.
    /// </summary>
    /// <param name="campaign">The campaign.</param>
    /// <param name="machines">Its machines.</param>
    /// <param name="audit">The audit record.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>
    /// <see langword="false"/> when a campaign has the same manifest sequence, or the campaign is active and another one of
    /// the organization is (scheduled or updating): nothing was written.
    /// </returns>
    ValueTask<bool> CreateUpdateCampaignAsync(UpdateCampaignRecord campaign, IReadOnlyList<UpdateCampaignMachineRecord> machines, AuditEntry? audit,
        CancellationToken cancellationToken = default);

    /// <summary>Writes the state of a campaign and of the machines given, in one transaction.</summary>
    /// <param name="campaign">The campaign.</param>
    /// <param name="machines">The machines that changed.</param>
    /// <param name="audit">An audit record, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes once it is written.</returns>
    ValueTask SaveUpdateCampaignAsync(UpdateCampaignRecord campaign, IReadOnlyList<UpdateCampaignMachineRecord> machines, AuditEntry? audit,
        CancellationToken cancellationToken = default);

    /// <summary>Gets the update policy of an organization.</summary>
    /// <param name="organizationId">The organization.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The policy, or <see langword="null"/> when it has none (manual).</returns>
    ValueTask<UpdatePolicyRecord?> GetUpdatePolicyAsync(string organizationId, CancellationToken cancellationToken = default);

    /// <summary>Replaces the update policy of an organization with a newer one, audited in the same transaction.</summary>
    /// <param name="policy">The policy.</param>
    /// <param name="audit">The audit record.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns><see langword="false"/> when the policy held has the same sequence or a later one: nothing was written.</returns>
    ValueTask<bool> SetUpdatePolicyAsync(UpdatePolicyRecord policy, AuditEntry audit, CancellationToken cancellationToken = default);

    /// <summary>Records the last check of the feed of an automatic policy.</summary>
    /// <param name="organizationId">The organization.</param>
    /// <param name="at">When.</param>
    /// <param name="message">What it found.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes once it is written.</returns>
    ValueTask SetUpdatePolicyCheckAsync(string organizationId, DateTimeOffset at, string? message, CancellationToken cancellationToken = default);

    /// <summary>Appends a record to the audit log, alone (a refusal, a read that is audited).</summary>
    /// <param name="audit">The record.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when it is recorded.</returns>
    ValueTask AppendAuditAsync(AuditEntry audit, CancellationToken cancellationToken = default);

    /// <summary>Lists records of the audit log.</summary>
    /// <param name="afterSequence">The sequence after which to start; 0 from the first.</param>
    /// <param name="limit">The most records to return.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The records, by sequence.</returns>
    ValueTask<IReadOnlyList<AuditRecord>> ListAuditAsync(long afterSequence, int limit, CancellationToken cancellationToken = default);

    /// <summary>Checks the hash chain of the whole audit log.</summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Whether it holds, and where it breaks.</returns>
    ValueTask<AuditVerification> VerifyAuditAsync(CancellationToken cancellationToken = default);
}
