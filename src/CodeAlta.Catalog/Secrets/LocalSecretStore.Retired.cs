using System.Text;
using System.Text.Json.Serialization;

namespace CodeAlta.Catalog.Secrets;

// The values of secrets the store no longer gives (doc/fleet/secrets.md, masking): a rotation, a deletion, a value the
// fleet took away or a link that is gone. A run that took the old value may still hold it (a process, a stream, a file it
// wrote), so the store keeps it, protected as its other values are, to mask it; it is never given to a reference again.
// Kept for a while and bounded, the oldest going first.
public sealed partial class LocalSecretStore
{
    /// <summary>The most values kept for masking once taken away.</summary>
    public const int MaximumRetired = 256;

    /// <summary>How long a value taken away is still masked.</summary>
    public static readonly TimeSpan RetiredKept = TimeSpan.FromDays(30);

    // Keeps protected values taken away, for masking only.
    private static void Retire(StoreDocument document, IEnumerable<string?> protectedValues)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var value in protectedValues)
        {
            if (string.IsNullOrEmpty(value) || document.Retired.Any(retired => retired.Value == value)) continue;
            document.Retired.Add(new RetiredDocument { Value = value, RetiredAt = now });
        }

        document.Retired.RemoveAll(retired => now - retired.RetiredAt > RetiredKept);
        if (document.Retired.Count > MaximumRetired) document.Retired.RemoveRange(0, document.Retired.Count - MaximumRetired);
    }

    // The values taken away of a reading of the file, read back once for that reading; one that does not read back is left out.
    private IReadOnlyList<string> RetiredValues(Snapshot snapshot)
    {
        lock (_gate)
        {
            if (snapshot.RetiredValues is { } known) return known;
            var values = new List<string>(snapshot.Document.Retired.Count);
            if (snapshot.Document.Retired.Count > 0)
            {
                ISecretProtector protector;
                try
                {
                    protector = ProtectorFor(snapshot.Document);
                }
                catch (SecretStoreUnavailableException)
                {
                    return snapshot.RetiredValues = values;
                }

                var now = DateTimeOffset.UtcNow;
                foreach (var retired in snapshot.Document.Retired)
                {
                    if (now - retired.RetiredAt > RetiredKept) continue;
                    try
                    {
                        var bytes = protector.Unprotect(Convert.FromBase64String(retired.Value ?? string.Empty));
                        values.Add(Encoding.UTF8.GetString(bytes));
                        System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
                    }
                    catch (Exception exception) when (exception is FormatException or SecretStoreUnavailableException or System.Security.Cryptography.CryptographicException)
                    {
                        // What is not read cannot leak.
                    }
                }
            }

            return snapshot.RetiredValues = values;
        }
    }
}

internal sealed class RetiredDocument
{
    // The protected value, in base 64.
    [JsonPropertyName("value")]
    public string? Value { get; set; }

    [JsonPropertyName("retired_at")]
    public DateTimeOffset RetiredAt { get; set; }
}
