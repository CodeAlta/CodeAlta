using System.Collections.Frozen;

namespace CodeAlta.Plugin.Mcp;

internal static class McpRedactor
{
    private const string Redacted = "[redacted]";

    private static readonly FrozenSet<string> SecretWords = new[]
    {
        "authorization",
        "auth",
        "bearer",
        "cookie",
        "key",
        "password",
        "passwd",
        "secret",
        "token",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyDictionary<string, string> RedactDictionary(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var result = new Dictionary<string, string>(values.Count, StringComparer.Ordinal);
        foreach (var (key, value) in values)
        {
            result[key] = RedactValue(key, value);
        }

        return result;
    }

    public static IReadOnlyList<string> RedactArguments(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count == 0)
        {
            return [];
        }

        var result = new string[arguments.Count];
        var redactNext = false;
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (redactNext)
            {
                result[index] = Redacted;
                redactNext = false;
                continue;
            }

            var separatorIndex = argument.IndexOf('=', StringComparison.Ordinal);
            if (separatorIndex > 0)
            {
                var key = argument[..separatorIndex];
                var value = argument[(separatorIndex + 1)..];
                result[index] = ShouldRedactKey(key) || LooksSecretLike(value)
                    ? key + "=" + Redacted
                    : argument;
                continue;
            }

            result[index] = LooksSecretLike(argument) ? Redacted : argument;
            redactNext = ShouldRedactKey(argument);
        }

        return result;
    }

    public static string? RedactUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var questionIndex = value.IndexOf('?', StringComparison.Ordinal);
        if (questionIndex < 0)
        {
            return value;
        }

        var prefix = value[..(questionIndex + 1)];
        var suffix = value[(questionIndex + 1)..];
        var fragmentIndex = suffix.IndexOf('#', StringComparison.Ordinal);
        var query = fragmentIndex >= 0 ? suffix[..fragmentIndex] : suffix;
        var fragment = fragmentIndex >= 0 ? suffix[fragmentIndex..] : string.Empty;
        var parts = query.Split('&');
        for (var index = 0; index < parts.Length; index++)
        {
            var part = parts[index];
            var separatorIndex = part.IndexOf('=', StringComparison.Ordinal);
            if (separatorIndex <= 0)
            {
                parts[index] = ShouldRedactKey(part) ? Redacted : part;
                continue;
            }

            var key = part[..separatorIndex];
            var parameterValue = part[(separatorIndex + 1)..];
            if (ShouldRedactKey(key) || HasCredentialPrefix(parameterValue))
            {
                parts[index] = key + "=" + Redacted;
            }
        }

        return prefix + string.Join('&', parts) + fragment;
    }

    public static string RedactValue(string? key, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return ShouldRedactKey(key) || HasCredentialPrefix(value) ? Redacted : value;
    }

    private static bool ShouldRedactKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        foreach (var word in SecretWords)
        {
            if (key.Contains(word, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool LooksSecretLike(string value)
    {
        var trimmed = value.Trim();
        return HasCredentialPrefix(trimmed) ||
               trimmed.Length >= 32 && trimmed.Any(char.IsDigit) && trimmed.Any(char.IsLetter);
    }

    private static bool HasCredentialPrefix(string value)
    {
        var trimmed = value.Trim();
        return trimmed.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase);
    }
}
