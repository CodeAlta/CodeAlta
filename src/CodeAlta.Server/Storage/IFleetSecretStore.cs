namespace CodeAlta.Server.Storage;

/// <summary>A named value of a person as the server holds it: what is known of it, never its value.</summary>
/// <param name="Id">The id of the value.</param>
/// <param name="OrganizationId">The organization.</param>
/// <param name="OwnerPersonId">The person whose value it is.</param>
/// <param name="Kind">Its kind: <c>secret</c> or <c>variable</c>.</param>
/// <param name="Name">Its name.</param>
/// <param name="ScopeKind">The kind of its scope.</param>
/// <param name="ScopeId">The id of its scope; empty for the person.</param>
/// <param name="HostOnly">Whether it goes only to host-side tools.</param>
/// <param name="Version">Its current version.</param>
/// <param name="SetByDeviceId">The device that set the current version.</param>
/// <param name="SetByPersonId">The person of that device.</param>
/// <param name="SetAt">When the current version was set.</param>
/// <param name="CreatedAt">When it was first set.</param>
public sealed record FleetSecretRecord(string Id, string OrganizationId, string OwnerPersonId, string Kind, string Name, string ScopeKind, string ScopeId, bool HostOnly,
    int Version, string SetByDeviceId, string? SetByPersonId, DateTimeOffset SetAt, DateTimeOffset CreatedAt);

/// <summary>A copy of a value sealed for one device, of one version.</summary>
/// <param name="SecretId">The value.</param>
/// <param name="DeviceId">The device.</param>
/// <param name="Version">The version it is of.</param>
/// <param name="Sealed">The sealed copy, as opaque text.</param>
/// <param name="SealedByDeviceId">The controller device that gave it.</param>
/// <param name="CreatedAt">When it was stored.</param>
public sealed record FleetSecretCopyRecord(string SecretId, string DeviceId, int Version, string Sealed, string SealedByDeviceId, DateTimeOffset CreatedAt);

/// <summary>A value with the copies of its current version.</summary>
/// <param name="Secret">The value.</param>
/// <param name="Copies">The copies.</param>
public sealed record FleetSecretEntry(FleetSecretRecord Secret, IReadOnlyList<FleetSecretCopyRecord> Copies);

/// <summary>What a write of a value found.</summary>
public enum FleetSecretPutOutcome
{
    /// <summary>Written.</summary>
    Written,

    /// <summary>The value is not at the version before this one (a rotation raced, or a replay): nothing was written.</summary>
    Stale,

    /// <summary>Another value has this name in this scope: nothing was written.</summary>
    NameTaken,
}

/// <summary>An environment of a person as the server holds it.</summary>
/// <param name="Id">The environment.</param>
/// <param name="OrganizationId">The organization.</param>
/// <param name="OwnerPersonId">The person whose environment it is.</param>
/// <param name="Name">Its name.</param>
/// <param name="Number">The version of its definition.</param>
/// <param name="Removed">Whether it was taken away.</param>
/// <param name="Payload">The definition as signed.</param>
/// <param name="Signature">The signature.</param>
/// <param name="SignerDeviceId">The controller device that signed it.</param>
/// <param name="UpdatedAt">When it was stored.</param>
public sealed record FleetEnvironmentRecord(string Id, string OrganizationId, string OwnerPersonId, string Name, long Number, bool Removed, string Payload, string Signature,
    string SignerDeviceId, DateTimeOffset UpdatedAt);

/// <summary>An approval a run asked, and the approval an approver's device signed once given.</summary>
/// <param name="Id">The approval.</param>
/// <param name="OrganizationId">The organization.</param>
/// <param name="EnvironmentId">The environment.</param>
/// <param name="EnvironmentNumber">The version of its definition the machine verified.</param>
/// <param name="MachineId">The machine of the run.</param>
/// <param name="RunId">The run.</param>
/// <param name="Nonce">The nonce of the machine.</param>
/// <param name="AutomationId">The automation of the run; null for a session.</param>
/// <param name="Title">What the run is.</param>
/// <param name="Status"><c>pending</c> or <c>approved</c>.</param>
/// <param name="RequestedAt">When the machine asked.</param>
/// <param name="ApprovalPayload">The signed approval, once given.</param>
/// <param name="ApprovalSignature">Its signature.</param>
/// <param name="ApproverDeviceId">The device that signed it.</param>
/// <param name="ApprovedAt">When it was given.</param>
public sealed record FleetApprovalRecord(string Id, string OrganizationId, string EnvironmentId, long EnvironmentNumber, string MachineId, string RunId, string Nonce,
    string? AutomationId, string? Title, string Status, DateTimeOffset RequestedAt, string? ApprovalPayload, string? ApprovalSignature, string? ApproverDeviceId,
    DateTimeOffset? ApprovedAt);

/// <summary>A use of a value a machine published.</summary>
/// <param name="OrganizationId">The organization.</param>
/// <param name="SecretId">The value.</param>
/// <param name="MachineId">The machine.</param>
/// <param name="Name">The name of the value.</param>
/// <param name="Version">The version used.</param>
/// <param name="RunId">The run.</param>
/// <param name="UsedAt">When it was used.</param>
/// <param name="ReceivedAt">When the server received it.</param>
public sealed record FleetSecretUseRecord(string OrganizationId, string SecretId, string MachineId, string Name, int Version, string RunId, DateTimeOffset UsedAt,
    DateTimeOffset ReceivedAt);

/// <summary>The secrets, variables, environments, approvals and uses of the organization, as the store keeps them.</summary>
public interface IFleetSecretStore
{
    /// <summary>Lists the values of a person, each with the copies of its current version.</summary>
    /// <param name="organizationId">The organization.</param>
    /// <param name="ownerPersonId">The person.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The values, by name.</returns>
    ValueTask<IReadOnlyList<FleetSecretEntry>> ListFleetSecretsAsync(string organizationId, string ownerPersonId, CancellationToken cancellationToken = default);

    /// <summary>Gets a value with the copies of its current version.</summary>
    /// <param name="organizationId">The organization.</param>
    /// <param name="id">The value.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The value, or null.</returns>
    ValueTask<FleetSecretEntry?> GetFleetSecretAsync(string organizationId, string id, CancellationToken cancellationToken = default);

    /// <summary>Lists the values a device holds a copy of the current version of, each with that copy only.</summary>
    /// <param name="organizationId">The organization.</param>
    /// <param name="deviceId">The device.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The values.</returns>
    ValueTask<IReadOnlyList<FleetSecretEntry>> ListHeldFleetSecretsAsync(string organizationId, string deviceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a version of a value with its copies, audited in the same transaction: version 1 adds it, a later version
    /// replaces the one before it and all its copies.
    /// </summary>
    /// <param name="secret">The value at its new version.</param>
    /// <param name="copies">The copies of that version.</param>
    /// <param name="audit">The audit record.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>What the write found.</returns>
    ValueTask<FleetSecretPutOutcome> PutFleetSecretAsync(FleetSecretRecord secret, IReadOnlyList<FleetSecretCopyRecord> copies, AuditEntry audit,
        CancellationToken cancellationToken = default);

    /// <summary>Adds copies of the current version for devices that hold none, audited in the same transaction; a device keeps the copy it has.</summary>
    /// <param name="organizationId">The organization.</param>
    /// <param name="id">The value.</param>
    /// <param name="version">The version the copies are of; nothing is added when it is not the current one.</param>
    /// <param name="copies">The copies.</param>
    /// <param name="audit">The audit record.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The devices that got a copy; null when the version is not the current one.</returns>
    ValueTask<IReadOnlyList<string>?> AddFleetSecretCopiesAsync(string organizationId, string id, int version, IReadOnlyList<FleetSecretCopyRecord> copies, AuditEntry audit,
        CancellationToken cancellationToken = default);

    /// <summary>Removes the copy of a device, audited in the same transaction.</summary>
    /// <param name="organizationId">The organization.</param>
    /// <param name="id">The value.</param>
    /// <param name="deviceId">The device.</param>
    /// <param name="audit">The audit record.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Whether it held one.</returns>
    ValueTask<bool> RemoveFleetSecretCopyAsync(string organizationId, string id, string deviceId, AuditEntry audit, CancellationToken cancellationToken = default);

    /// <summary>Deletes a value and its copies, audited in the same transaction.</summary>
    /// <param name="organizationId">The organization.</param>
    /// <param name="id">The value.</param>
    /// <param name="audit">The audit record.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Whether it existed.</returns>
    ValueTask<bool> DeleteFleetSecretAsync(string organizationId, string id, AuditEntry audit, CancellationToken cancellationToken = default);

    /// <summary>Stores a version of an environment when it is newer than the one held, audited in the same transaction.</summary>
    /// <param name="environment">The environment.</param>
    /// <param name="audit">The audit record.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Whether it was written: false for a version not newer, or a name another environment of the person has.</returns>
    ValueTask<bool> PutFleetEnvironmentAsync(FleetEnvironmentRecord environment, AuditEntry audit, CancellationToken cancellationToken = default);

    /// <summary>Lists the environments of the organization, or of one person.</summary>
    /// <param name="organizationId">The organization.</param>
    /// <param name="ownerPersonId">The person; null for every person.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The environments, by name.</returns>
    ValueTask<IReadOnlyList<FleetEnvironmentRecord>> ListFleetEnvironmentsAsync(string organizationId, string? ownerPersonId, CancellationToken cancellationToken = default);

    /// <summary>Records the approval a run asks; the one asked already for that machine and run is kept.</summary>
    /// <param name="approval">The approval asked.</param>
    /// <param name="audit">The audit record, written when it is new.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The approval held for that machine and run.</returns>
    ValueTask<FleetApprovalRecord> RequestFleetApprovalAsync(FleetApprovalRecord approval, AuditEntry audit, CancellationToken cancellationToken = default);

    /// <summary>Gets an approval.</summary>
    /// <param name="organizationId">The organization.</param>
    /// <param name="id">The approval.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The approval, or null.</returns>
    ValueTask<FleetApprovalRecord?> GetFleetApprovalAsync(string organizationId, string id, CancellationToken cancellationToken = default);

    /// <summary>Lists the approvals of environments, the newest first.</summary>
    /// <param name="organizationId">The organization.</param>
    /// <param name="environmentIds">The environments.</param>
    /// <param name="limit">The most approvals.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The approvals.</returns>
    ValueTask<IReadOnlyList<FleetApprovalRecord>> ListFleetApprovalsAsync(string organizationId, IReadOnlyCollection<string> environmentIds, int limit,
        CancellationToken cancellationToken = default);

    /// <summary>Gives an approval that waits, audited in the same transaction.</summary>
    /// <param name="organizationId">The organization.</param>
    /// <param name="id">The approval.</param>
    /// <param name="payload">The signed approval.</param>
    /// <param name="signature">Its signature.</param>
    /// <param name="approverDeviceId">The device that signed it.</param>
    /// <param name="at">When it was given.</param>
    /// <param name="audit">The audit record.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Whether it waited.</returns>
    ValueTask<bool> ApproveFleetApprovalAsync(string organizationId, string id, string payload, string signature, string approverDeviceId, DateTimeOffset at, AuditEntry audit,
        CancellationToken cancellationToken = default);

    /// <summary>Records the uses a machine published; a use recorded already (same value, machine, run and version) is kept once.</summary>
    /// <param name="uses">The uses.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>How many were new.</returns>
    ValueTask<int> RecordFleetSecretUsesAsync(IReadOnlyList<FleetSecretUseRecord> uses, CancellationToken cancellationToken = default);

    /// <summary>Lists the uses of a value, the newest first.</summary>
    /// <param name="organizationId">The organization.</param>
    /// <param name="secretId">The value.</param>
    /// <param name="limit">The most uses.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The uses.</returns>
    ValueTask<IReadOnlyList<FleetSecretUseRecord>> ListFleetSecretUsesAsync(string organizationId, string secretId, int limit, CancellationToken cancellationToken = default);
}
