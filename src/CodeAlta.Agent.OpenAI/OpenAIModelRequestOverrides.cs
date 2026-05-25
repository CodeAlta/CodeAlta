using CodeAlta.Agent.ModelCatalog;

namespace CodeAlta.Agent.OpenAI;

internal static class OpenAIModelRequestOverrides
{
    public static AgentModelRequestOverride? Find(
        IReadOnlyDictionary<string, AgentModelRequestOverride>? overrides,
        string? modelId)
    {
        if (overrides is not { Count: > 0 } || string.IsNullOrWhiteSpace(modelId))
        {
            return null;
        }

        var normalizedModelId = modelId.Trim();
        foreach (var entry in overrides)
        {
            if (AreEquivalent(entry.Key, normalizedModelId))
            {
                return entry.Value;
            }
        }

        return null;
    }

    public static IReadOnlyDictionary<string, object?>? MergeExtraBody(
        IReadOnlyDictionary<string, object?>? providerBody,
        AgentModelRequestOverride? modelRequest)
    {
        if (modelRequest is null)
        {
            return providerBody;
        }

        Dictionary<string, object?>? merged = providerBody is null || providerBody.Count == 0
            ? null
            : new Dictionary<string, object?>(providerBody, StringComparer.Ordinal);

        if (modelRequest.RemoveExtraBody is { Count: > 0 } removeFields)
        {
            foreach (var field in removeFields)
            {
                if (!string.IsNullOrWhiteSpace(field))
                {
                    merged?.Remove(field.Trim());
                }
            }
        }

        if (modelRequest.ExtraBody is { Count: > 0 } extraBody)
        {
            merged ??= new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var entry in extraBody)
            {
                if (!string.IsNullOrWhiteSpace(entry.Key))
                {
                    merged[entry.Key.Trim()] = entry.Value;
                }
            }
        }

        return merged is null || merged.Count == 0 ? null : merged;
    }

    public static IReadOnlyDictionary<string, string>? MergeHeaders(
        IReadOnlyDictionary<string, string>? providerHeaders,
        AgentModelRequestOverride? modelRequest)
    {
        if (modelRequest is null)
        {
            return null;
        }

        Dictionary<string, string>? merged = providerHeaders is null || providerHeaders.Count == 0
            ? null
            : new Dictionary<string, string>(providerHeaders, StringComparer.OrdinalIgnoreCase);

        if (modelRequest.RemoveHeaders is { Count: > 0 })
        {
            foreach (var header in modelRequest.RemoveHeaders)
            {
                if (!string.IsNullOrWhiteSpace(header) && !IsRequiredAuthHeader(header))
                {
                    merged?.Remove(header.Trim());
                }
            }
        }

        if (modelRequest.Headers is { Count: > 0 })
        {
            merged ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in modelRequest.Headers)
            {
                if (!string.IsNullOrWhiteSpace(header.Key) && !IsRequiredAuthHeader(header.Key))
                {
                    merged[header.Key.Trim()] = header.Value ?? string.Empty;
                }
            }
        }

        return merged ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsRequiredAuthHeader(string headerName)
        => headerName.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
           headerName.Equals("api-key", StringComparison.OrdinalIgnoreCase) ||
           headerName.Equals("x-api-key", StringComparison.OrdinalIgnoreCase);

    private static bool AreEquivalent(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        if (string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var leftKeys = GetLookupKeys(left);
        foreach (var key in GetLookupKeys(right))
        {
            if (leftKeys.Contains(key))
            {
                return true;
            }
        }

        return false;
    }

    private static HashSet<string> GetLookupKeys(string value)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddLookupKeys(keys, value);
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
        normalized = NormalizeLookupKey(withoutDateSuffix);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            keys.Add(normalized);
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
        return dateSlice.Length == 10 &&
               char.IsDigit(dateSlice[0]) &&
               char.IsDigit(dateSlice[1]) &&
               char.IsDigit(dateSlice[2]) &&
               char.IsDigit(dateSlice[3]) &&
               dateSlice[4] == '-' &&
               char.IsDigit(dateSlice[5]) &&
               char.IsDigit(dateSlice[6]) &&
               dateSlice[7] == '-' &&
               char.IsDigit(dateSlice[8]) &&
               char.IsDigit(dateSlice[9])
            ? value[..^DateSuffixLength]
            : null;
    }

    private static string NormalizeLookupKey(string value)
    {
        Span<char> buffer = value.Length <= 256 ? stackalloc char[value.Length] : new char[value.Length];
        var index = 0;
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                buffer[index++] = char.ToLowerInvariant(c);
            }
        }

        return new string(buffer[..index]);
    }
}
