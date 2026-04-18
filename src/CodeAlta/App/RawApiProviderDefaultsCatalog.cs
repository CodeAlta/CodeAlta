using CodeAlta.Agent.LocalRuntime;

namespace CodeAlta.App;

internal static class RawApiProviderDefaultsCatalog
{
    private static readonly IReadOnlyList<RawApiProviderDefaultsRule> Rules =
    [
        new(
            "MiniMax OpenAI Chat",
            static context =>
                context.TransportKind == LocalAgentTransportKind.OpenAIChatCompletions &&
                (string.Equals(context.ProviderKey, "minimax", StringComparison.OrdinalIgnoreCase) ||
                 HasHost(context.BaseUri, "minimax.io") ||
                 HasHost(context.BaseUri, "minimaxi.com")),
            static profile => profile with
            {
                SupportsDeveloperRole = false,
                ReasoningFieldNames = PrependDistinct(
                    profile.ReasoningFieldNames,
                    "reasoning_details[0].text"),
            },
            static extraBody => MergeExtraBody(
                extraBody,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["reasoning_split"] = true,
                })),
    ];

    public static LocalAgentProviderProfile ApplyProfileDefaults(
        LocalAgentTransportKind transportKind,
        string providerKey,
        Uri? baseUri,
        LocalAgentProviderProfile profile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);
        ArgumentNullException.ThrowIfNull(profile);

        var context = new RawApiProviderDefaultsContext(transportKind, providerKey.Trim(), baseUri);
        foreach (var rule in Rules)
        {
            if (rule.IsMatch(context))
            {
                profile = rule.ApplyProfile(profile);
            }
        }

        return profile;
    }

    public static IReadOnlyDictionary<string, object?>? ApplyOpenAIExtraBodyDefaults(
        LocalAgentTransportKind transportKind,
        string providerKey,
        Uri? baseUri,
        IReadOnlyDictionary<string, object?>? extraBody)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);

        var context = new RawApiProviderDefaultsContext(transportKind, providerKey.Trim(), baseUri);
        foreach (var rule in Rules)
        {
            if (rule.IsMatch(context) && rule.ApplyOpenAIExtraBody is not null)
            {
                extraBody = rule.ApplyOpenAIExtraBody(extraBody);
            }
        }

        return extraBody;
    }

    private static bool HasHost(Uri? baseUri, string expectedHost)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedHost);

        var host = baseUri?.Host;
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        return host.Equals(expectedHost, StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith($".{expectedHost}", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record RawApiProviderDefaultsRule(
        string Name,
        Func<RawApiProviderDefaultsContext, bool> IsMatch,
        Func<LocalAgentProviderProfile, LocalAgentProviderProfile> ApplyProfile,
        Func<IReadOnlyDictionary<string, object?>?, IReadOnlyDictionary<string, object?>?>? ApplyOpenAIExtraBody = null);

    private readonly record struct RawApiProviderDefaultsContext(
        LocalAgentTransportKind TransportKind,
        string ProviderKey,
        Uri? BaseUri);

    private static IReadOnlyList<string> PrependDistinct(
        IReadOnlyList<string> existingValues,
        params string[] newValues)
    {
        ArgumentNullException.ThrowIfNull(existingValues);
        ArgumentNullException.ThrowIfNull(newValues);

        return [.. newValues.Concat(existingValues).Where(static value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal)];
    }

    private static IReadOnlyDictionary<string, object?>? MergeExtraBody(
        IReadOnlyDictionary<string, object?>? configured,
        IReadOnlyDictionary<string, object?> defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);

        if (configured is null || configured.Count == 0)
        {
            return defaults.Count == 0
                ? null
                : new Dictionary<string, object?>(defaults, StringComparer.Ordinal);
        }

        var merged = new Dictionary<string, object?>(defaults, StringComparer.Ordinal);
        foreach (var entry in configured)
        {
            merged[entry.Key] = entry.Value;
        }

        return merged;
    }
}
