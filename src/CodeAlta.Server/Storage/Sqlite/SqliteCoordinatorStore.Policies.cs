using Microsoft.Data.Sqlite;

namespace CodeAlta.Server.Storage.Sqlite;

// The signed policy of the organization and its kill switch (the migration "policies").
public sealed partial class SqliteCoordinatorStore : IPolicyStore
{
    private const string PolicyDocumentColumns = "SELECT organization_id, number, payload, signature, signer_device_id, hash, recorded_at FROM policy_documents";

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<PolicyDocumentRecord>> ListPolicyDocumentsAsync(string organizationId, long afterNumber, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(organizationId);
        return ReadAsync(connection => QueryList(connection, null, PolicyDocumentColumns + " WHERE organization_id = $org AND number > $after ORDER BY number",
            ReadPolicyDocument, ("$org", organizationId), ("$after", Math.Max(0, afterNumber))), cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<bool> AppendPolicyDocumentAsync(PolicyDocumentRecord document, AuditEntry audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(audit);
        return WriteAsync((connection, transaction) =>
        {
            var last = QuerySingle(connection, transaction, "SELECT MAX(number) FROM policy_documents WHERE organization_id = $org",
                static reader => reader.IsDBNull(0) ? 0L : reader.GetInt64(0), ("$org", document.OrganizationId));
            if (last != document.Number - 1) return false;
            Execute(connection, transaction,
                "INSERT INTO policy_documents (organization_id, number, payload, signature, signer_device_id, hash, recorded_at) " +
                "VALUES ($org, $number, $payload, $signature, $signer, $hash, $at)",
                ("$org", document.OrganizationId), ("$number", document.Number), ("$payload", document.Payload), ("$signature", document.Signature),
                ("$signer", document.SignerDeviceId), ("$hash", document.Hash), ("$at", ToTime(document.RecordedAt)));
            AppendAudit(connection, transaction, audit);
            return true;
        }, cancellationToken);
    }

    private static PolicyDocumentRecord ReadPolicyDocument(SqliteDataReader reader)
        => new(reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), FromTime(reader.GetInt64(6)));
}
