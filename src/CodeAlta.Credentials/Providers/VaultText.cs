using System.Security.Cryptography;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Credentials.Providers;

// What the providers of the password managers share to read what a program printed and the paths of their settings.
internal static class VaultText
{
    public static string TrimOneNewline(string text)
        => text.EndsWith("\r\n", StringComparison.Ordinal) ? text[..^2] : text.EndsWith('\n') ? text[..^1] : text;

    public static void Zero(byte[] bytes) => CryptographicOperations.ZeroMemory(bytes);

    // A path of a setting: ~ is the folder of the user, and the variables of the environment are expanded.
    public static string? ExpandPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var expanded = Environment.ExpandEnvironmentVariables(path.Trim());
        if (expanded == "~" || expanded.StartsWith("~/", StringComparison.Ordinal) || expanded.StartsWith("~\\", StringComparison.Ordinal))
            expanded = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), expanded[1..].TrimStart('/', '\\'));
        return Path.GetFullPath(expanded);
    }

    // A failure of a program: the message says how it ended, in words of the provider; what the program wrote goes to the detail,
    // which only the user reads, because it can hold a value the masking does not know yet.
    public static CredentialProviderException Failure(CredentialFailure failure, string message, VaultCommandResult result)
        => new(failure, message) { Detail = result.ErrorDetail() };

    // A status after a program failed, with what it wrote as its detail.
    public static CredentialProviderStatus WithDetail(this CredentialProviderStatus status, VaultCommandResult result)
        => status with { Detail = result.ErrorDetail() };
}
