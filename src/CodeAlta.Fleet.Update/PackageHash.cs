using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace CodeAlta.Fleet.Update;

/// <summary>
/// The SHA-512 of a package file, the hash NuGet feeds publish (<c>packageHash</c>, base64). An expected hash is accepted
/// in base64, in hexadecimal, or prefixed with <c>sha512:</c>.
/// </summary>
public sealed class PackageHash : IEquatable<PackageHash>
{
    private readonly byte[] _bytes;

    private PackageHash(byte[] bytes) => _bytes = bytes;

    /// <summary>Gets the hash in base64, as NuGet writes it.</summary>
    public string Base64 => Convert.ToBase64String(_bytes);

    /// <inheritdoc />
    public override string ToString() => "sha512:" + Base64;

    /// <summary>Parses a SHA-512 hash.</summary>
    /// <param name="text">The hash: base64, hexadecimal, or either after <c>sha512:</c>.</param>
    /// <param name="hash">The hash, or null.</param>
    /// <returns>Whether the text is a SHA-512 hash.</returns>
    public static bool TryParse(string? text, [NotNullWhen(true)] out PackageHash? hash)
    {
        hash = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var value = text.Trim();
        if (value.StartsWith("sha512:", StringComparison.OrdinalIgnoreCase)) value = value[7..];
        byte[]? bytes = null;
        if (value.Length == SHA512.HashSizeInBytes * 2 && value.All(char.IsAsciiHexDigit))
        {
            bytes = Convert.FromHexString(value);
        }
        else
        {
            var buffer = new byte[SHA512.HashSizeInBytes + 3];
            if (Convert.TryFromBase64String(value, buffer, out var written) && written == SHA512.HashSizeInBytes) bytes = buffer[..written];
        }

        if (bytes is null) return false;
        hash = new PackageHash(bytes);
        return true;
    }

    /// <summary>Parses a SHA-512 hash.</summary>
    /// <param name="text">The hash.</param>
    /// <returns>The hash.</returns>
    /// <exception cref="FormatException">The text is not a SHA-512 hash.</exception>
    public static PackageHash Parse(string text)
        => TryParse(text, out var hash) ? hash : throw new FormatException("A SHA-512 hash is 64 bytes in base64 or 128 hexadecimal digits.");

    /// <summary>Hashes a file.</summary>
    /// <param name="path">The file.</param>
    /// <param name="cancellationToken">Cancels the hash.</param>
    /// <returns>Its SHA-512.</returns>
    public static async Task<PackageHash> ComputeAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return new PackageHash(await SHA512.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public bool Equals(PackageHash? other) => other is not null && CryptographicOperations.FixedTimeEquals(_bytes, other._bytes);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is PackageHash other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => BitConverter.ToInt32(_bytes, 0);
}
