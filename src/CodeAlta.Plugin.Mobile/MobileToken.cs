using System.Security.Cryptography;
using System.Text;

namespace CodeAlta.Plugin.Mobile;

/// <summary>The access token of the mobile server: 32 random bytes, base64url.</summary>
internal static class MobileToken
{
    internal static string Create()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static bool IsValid(string? token)
        => token is { Length: >= 40 } && token.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>Compares in constant time.</summary>
    internal static bool Matches(string? given, string expected)
    {
        if (string.IsNullOrEmpty(given)) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(expected));
    }
}
