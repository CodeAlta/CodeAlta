using CodeAlta.Agent;
using CodeAlta.Catalog;

namespace CodeAlta.Automations;

/// <summary>
/// The kind of credential a run of an automation would use, derived from how its provider starts
/// (<c>doc/fleet/orchestration.md</c>, "Credentials of unattended runs").
/// </summary>
internal enum AutomationCredentialKind
{
    /// <summary>It cannot be told: such a run is refused where the kind is checked.</summary>
    Unknown,

    /// <summary>A plan or a login of a person (the Codex subscription, Copilot, Claude Code signed in with a Claude plan).</summary>
    Personal,

    /// <summary>A key or a cloud identity of an account (an API key, a gateway key), or a local model that needs none.</summary>
    Account,
}

/// <summary>Tells the credential kind of a provider from its type and its configuration, never from a secret's value.</summary>
internal static class AutomationCredentials
{
    // The types that run with a key of an account when one is configured.
    private static readonly HashSet<string> KeyTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "openai-chat", "openai-responses", "azure-openai", "anthropic", "google-genai", "mistral", "xai",
    };

    /// <summary>The name of a kind as the API and the messages write it.</summary>
    internal static string Name(AutomationCredentialKind kind) => kind switch
    {
        AutomationCredentialKind.Personal => "personal",
        AutomationCredentialKind.Account => "account",
        _ => "unknown",
    };

    /// <summary>The credential kind a provider runs with.</summary>
    /// <param name="providerType">The type of the provider (<see cref="ModelProviderDescriptor.ProviderType"/>).</param>
    /// <param name="configuration">Its entry in the configuration of the profile; null when it has none (a provider of a plugin).</param>
    /// <param name="environment">Reads a variable of the environment of the host; null for the one of the process.</param>
    /// <returns>The kind; <see cref="AutomationCredentialKind.Unknown"/> when it cannot be told.</returns>
    internal static AutomationCredentialKind Classify(string providerType, CodeAltaProviderDocument? configuration, Func<string, string?>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(providerType);
        environment ??= Environment.GetEnvironmentVariable;
        switch (providerType.Trim().ToLowerInvariant())
        {
            case "codex":
            case "copilot":
            case "mistral-vibe":
                return AutomationCredentialKind.Personal;
            case "claude-code":
                // Claude Code runs with the key of the environment only when its provider says to use it.
                return string.Equals(configuration?.AnthropicApiKey?.Trim(), "use", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(environment("ANTHROPIC_API_KEY"))
                    ? AutomationCredentialKind.Account : AutomationCredentialKind.Personal;
            case "llama-cpp":
                return AutomationCredentialKind.Account;
            case "vertex-ai":
                return configuration is null ? AutomationCredentialKind.Unknown : AutomationCredentialKind.Account;
            case var type when KeyTypes.Contains(type):
                if (configuration is null) return AutomationCredentialKind.Unknown;
                if (!string.IsNullOrWhiteSpace(configuration.ApiKey)) return AutomationCredentialKind.Account;
                return configuration.ApiKeyEnv is { } variable && !string.IsNullOrWhiteSpace(environment(variable.Trim()))
                    ? AutomationCredentialKind.Account : AutomationCredentialKind.Unknown;
            default:
                return AutomationCredentialKind.Unknown;
        }
    }
}
