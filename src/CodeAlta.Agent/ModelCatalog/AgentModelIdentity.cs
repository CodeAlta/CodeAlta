using System.Text;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Agent.ModelCatalog;

internal static class AgentModelIdentity
{
    public static AgentModelInfo? FindBestMatch(
        IReadOnlyList<AgentModelInfo> models,
        string? modelId)
    {
        if (models.Count == 0 || string.IsNullOrWhiteSpace(modelId))
        {
            return null;
        }

        var normalizedModelId = modelId.Trim();
        var exact = models.FirstOrDefault(model =>
            string.Equals(model.Id, normalizedModelId, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        var lookupKeys = GetLookupKeySet(normalizedModelId);
        if (lookupKeys.Count == 0)
        {
            return null;
        }

        return models.FirstOrDefault(model => Matches(model, lookupKeys));
    }

    public static bool AreEquivalent(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        if (string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var leftKeys = GetLookupKeySet(left);
        return leftKeys.Count > 0 && Matches(right, leftKeys);
    }

    public static IEnumerable<string> GetLookupKeys(params string?[] values)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            AddLookupKeys(keys, value);
        }

        return keys;
    }

    public static bool Matches(AgentModelInfo model, IReadOnlySet<string> lookupKeys)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(lookupKeys);

        return Matches(model.Id, lookupKeys) || Matches(model.DisplayName, lookupKeys);
    }

    public static bool Matches(string? value, IReadOnlySet<string> lookupKeys)
    {
        ArgumentNullException.ThrowIfNull(lookupKeys);

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        foreach (var key in GetLookupKeys(value))
        {
            if (lookupKeys.Contains(key))
            {
                return true;
            }
        }

        return false;
    }

    public static HashSet<string> GetLookupKeySet(params string?[] values)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            AddLookupKeys(keys, value);
        }

        return keys;
    }

    private static void AddLookupKeys(ISet<string> keys, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var trimmed = value.Trim();
        keys.Add(trimmed);

        var normalized = NormalizeLookupKey(trimmed);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            keys.Add(normalized);
        }

        var withoutDateSuffix = StripDateSuffix(trimmed);
        if (withoutDateSuffix is null)
        {
            return;
        }

        keys.Add(withoutDateSuffix);
        var normalizedWithoutDate = NormalizeLookupKey(withoutDateSuffix);
        if (!string.IsNullOrWhiteSpace(normalizedWithoutDate))
        {
            keys.Add(normalizedWithoutDate);
        }
    }

    private static string? StripDateSuffix(string value)
    {
        const int DateSuffixLength = 11;
        if (value.Length <= DateSuffixLength || value[^DateSuffixLength] != '-')
        {
            return null;
        }

        var dateSlice = value.AsSpan(value.Length - 10);
        return IsIsoDate(dateSlice)
            ? value[..^DateSuffixLength]
            : null;
    }

    private static bool IsIsoDate(ReadOnlySpan<char> value)
    {
        return value.Length == 10 &&
               char.IsDigit(value[0]) &&
               char.IsDigit(value[1]) &&
               char.IsDigit(value[2]) &&
               char.IsDigit(value[3]) &&
               value[4] == '-' &&
               char.IsDigit(value[5]) &&
               char.IsDigit(value[6]) &&
               value[7] == '-' &&
               char.IsDigit(value[8]) &&
               char.IsDigit(value[9]);
    }

    private static string NormalizeLookupKey(string value)
    {
        var builder = new StringBuilder(value.Length);
        var pendingSeparator = false;
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c) || c == '.')
            {
                if (pendingSeparator && builder.Length > 0)
                {
                    builder.Append('-');
                }

                builder.Append(char.ToLowerInvariant(c));
                pendingSeparator = false;
            }
            else if (c is '-' or '_' or ' ' or '/' or ':')
            {
                pendingSeparator = builder.Length > 0;
            }
        }

        return builder.ToString().TrimEnd('-');
    }
}
