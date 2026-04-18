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
            static profile => profile with { SupportsDeveloperRole = false }),
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
        Func<LocalAgentProviderProfile, LocalAgentProviderProfile> ApplyProfile);

    private readonly record struct RawApiProviderDefaultsContext(
        LocalAgentTransportKind TransportKind,
        string ProviderKey,
        Uri? BaseUri);
}
