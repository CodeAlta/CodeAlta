using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CodeAlta.Server.Storage;

/// <summary>
/// The hash chain of the audit log: each record hashes the hash of the record before it with its own fields, so that
/// a record changed, removed or inserted afterwards breaks every hash after it (<c>doc/fleet/security-and-privacy.md</c>,
/// audit log). Every store computes it the same way, so a log copied from SQLite to PostgreSQL still verifies.
/// </summary>
public static class AuditChain
{
    private const string Domain = "codealta-audit/1";

    /// <summary>Computes the hash of a record.</summary>
    /// <param name="previousHash">The hash of the record before, empty for the first one.</param>
    /// <param name="sequence">The sequence of the record.</param>
    /// <param name="recordedAt">When it was recorded, to the millisecond.</param>
    /// <param name="entry">What it records.</param>
    /// <returns>The base64url of the SHA-256 of the fields, each with its length so that no two records share bytes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="previousHash"/> or <paramref name="entry"/> is <see langword="null"/>.</exception>
    public static string ComputeHash(string previousHash, long sequence, DateTimeOffset recordedAt, AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(previousHash);
        ArgumentNullException.ThrowIfNull(entry);
        var builder = new StringBuilder(256);
        Append(builder, Domain);
        Append(builder, previousHash);
        Append(builder, sequence.ToString(CultureInfo.InvariantCulture));
        Append(builder, recordedAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
        Append(builder, entry.Action);
        Append(builder, entry.Result);
        Append(builder, entry.ActorPersonId);
        Append(builder, entry.ActorDeviceId);
        Append(builder, entry.Target);
        Append(builder, entry.SourceAddress);
        Append(builder, entry.RequestId);
        Append(builder, entry.Details);
        return Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    /// <summary>Checks a run of records, in order of sequence, from the first one.</summary>
    /// <param name="records">The records.</param>
    /// <returns>Whether the chain holds, and where it breaks.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="records"/> is <see langword="null"/>.</exception>
    public static AuditVerification Verify(IEnumerable<AuditRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var previous = string.Empty;
        long expected = 1;
        long count = 0;
        foreach (var record in records)
        {
            count++;
            if (record.Sequence != expected
                || !string.Equals(record.PreviousHash, previous, StringComparison.Ordinal)
                || !string.Equals(record.Hash, ComputeHash(record.PreviousHash, record.Sequence, record.RecordedAt, record.Entry), StringComparison.Ordinal))
            {
                return new AuditVerification(false, count, record.Sequence);
            }

            previous = record.Hash;
            expected++;
        }

        return new AuditVerification(true, count, null);
    }

    // "<length>:<value>\n", or "-\n" for a missing field: a field can never be read as part of another.
    private static void Append(StringBuilder builder, string? value)
    {
        if (value is null)
        {
            builder.Append("-\n");
            return;
        }

        builder.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append('\n');
    }
}
