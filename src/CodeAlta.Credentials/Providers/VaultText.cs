using System.Security.Cryptography;

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

    // The first line of an error that says something: a program that writes "Error:" alone on its first line says why on the next.
    public static string ErrorLine(VaultCommandResult result)
    {
        foreach (var line in result.Error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // The causes of bws are numbered: "0: the cause".
            var digits = 0;
            while (digits < line.Length && char.IsAsciiDigit(line[digits])) digits++;
            var text = digits > 0 && digits < line.Length && line[digits] == ':' ? line[(digits + 1)..].Trim() : line;
            if (text.Length == 0 || text.Equals("Error:", StringComparison.OrdinalIgnoreCase) || text.Equals("Error", StringComparison.OrdinalIgnoreCase)) continue;
            return text.Length > 300 ? text[..300] : text;
        }

        return result.ErrorLine();
    }
}
