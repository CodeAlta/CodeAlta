using System.Security.Cryptography;

namespace CodeAlta.Catalog;

/// <summary>
/// Identifies the raw bytes of a text file, including its BOM, or its absence.
/// This is content identity, not a timestamp or an authorization token.
/// </summary>
public sealed record TextFileRevision
{
    private TextFileRevision(string? contentHash) => ContentHash = contentHash;

    /// <summary>Gets the SHA-256 hexadecimal content hash, or null for a missing file.</summary>
    public string? ContentHash { get; }

    /// <summary>Gets whether the file existed when this revision was observed.</summary>
    public bool Exists => ContentHash is not null;

    /// <summary>Gets the revision of a missing file, distinct from an empty file.</summary>
    public static TextFileRevision Missing => new((string?)null);

    internal static TextFileRevision FromBytes(ReadOnlySpan<byte> bytes)
        => new(Convert.ToHexString(SHA256.HashData(bytes)));
}
