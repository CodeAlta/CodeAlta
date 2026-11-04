namespace CodeAlta.Server.Storage;

/// <summary>A version of the signed policy as the server keeps it.</summary>
/// <param name="OrganizationId">The organization.</param>
/// <param name="Number">Its number.</param>
/// <param name="Payload">The JSON text signed.</param>
/// <param name="Signature">The signature.</param>
/// <param name="SignerDeviceId">The device that signed it.</param>
/// <param name="Hash">Its hash, which the next version names.</param>
/// <param name="RecordedAt">When the server recorded it.</param>
public sealed record PolicyDocumentRecord(string OrganizationId, long Number, string Payload, string Signature, string SignerDeviceId, string Hash, DateTimeOffset RecordedAt);

/// <summary>
/// What the coordinator keeps of the signed policy of an organization (<c>doc/fleet/security-and-privacy.md</c>, policies and
/// the kill switch): its versions, in order, as they were signed. The server never writes one: it keeps and relays them.
/// </summary>
public interface IPolicyStore
{
    /// <summary>Lists the versions of the policy after a number, in order.</summary>
    /// <param name="organizationId">The organization.</param>
    /// <param name="afterNumber">The number after which to list; 0 from the first.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The versions.</returns>
    ValueTask<IReadOnlyList<PolicyDocumentRecord>> ListPolicyDocumentsAsync(string organizationId, long afterNumber, CancellationToken cancellationToken = default);

    /// <summary>Appends a version that follows the last one, with its audit record, in one transaction.</summary>
    /// <param name="document">The version.</param>
    /// <param name="audit">The audit record.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>False when another version took its number meanwhile.</returns>
    ValueTask<bool> AppendPolicyDocumentAsync(PolicyDocumentRecord document, AuditEntry audit, CancellationToken cancellationToken = default);
}
