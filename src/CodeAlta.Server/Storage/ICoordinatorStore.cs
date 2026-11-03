using CodeAlta.Fleet.Protocol;

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
public interface ICoordinatorStore : IAsyncDisposable
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
