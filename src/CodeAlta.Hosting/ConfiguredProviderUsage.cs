using CodeAlta.Agent;
using CodeAlta.Agent.Claude;
using CodeAlta.Agent.Copilot;
using CodeAlta.Agent.OpenAI.Codex;
using CodeAlta.Catalog;

namespace CodeAlta.Hosting;

/// <summary>
/// Reads the usage of the subscription behind a configured provider, for a frontend that does not name the provider
/// packages: the windows of a ChatGPT plan for Codex, the quotas of GitHub Copilot, the limits of a Claude plan for
/// Claude Code. Each provider is asked in its own way, and none of them is sent a model turn.
/// </summary>
public static class ConfiguredProviderUsage
{
    /// <summary>Gets whether a provider type is a subscription whose usage can be read.</summary>
    /// <param name="providerType">The provider type of a definition.</param>
    /// <returns>True for Codex, Copilot and Claude Code.</returns>
    public static bool Supports(string? providerType) => providerType is "codex" or "copilot" or "claude-code";

    /// <summary>Asks the provider of a definition for the usage of its subscription.</summary>
    /// <param name="definition">The type-completed provider definition.</param>
    /// <param name="stateRootPath">The global state root that holds provider credentials.</param>
    /// <param name="httpClient">The client that sends the requests of the providers that are asked over HTTP.</param>
    /// <param name="cancellationToken">Stops the reading.</param>
    /// <returns>
    /// The usage, or why there is none: no account signed in, or an account that gives no usage. Codex and Claude
    /// Code are read by asking their own CLI, when the machine has it; for Codex it must be signed in with the same
    /// account as CodeAlta, whose own sign-in is not given the usage of the plan.
    /// </returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">The root is blank, or the provider type is not one whose usage can be read.</exception>
    /// <exception cref="IOException">Provider state cannot be read.</exception>
    /// <exception cref="HttpRequestException">A provider could not be reached or answered with an error.</exception>
    /// <exception cref="System.Text.Json.JsonException">A provider answered with something else than its usage.</exception>
    /// <exception cref="InvalidOperationException">The CLI of Codex or of Claude Code did not answer.</exception>
    /// <exception cref="OperationCanceledException">The reading was canceled.</exception>
    public static async Task<AgentSubscriptionUsageReading> ReadAsync(
        CodeAltaProviderDocument definition, string stateRootPath, HttpClient httpClient, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRootPath);
        ArgumentNullException.ThrowIfNull(httpClient);
        switch (definition.ProviderType)
        {
            case "codex":
                var credential = await new FileOpenAICodexSubscriptionCredentialStore(stateRootPath)
                    .LoadAsync(definition.ProviderKey, cancellationToken).ConfigureAwait(false);
                return OpenAICodexSubscriptionLoginManager.IsRegistration(credential) && !string.IsNullOrWhiteSpace(credential.AccessToken)
                    ? await CodexAccountUsage.ReadAsync(credential.Email ?? credential.AccountLabel, cancellationToken).ConfigureAwait(false)
                    : AgentSubscriptionUsageReading.NotSignedIn;
            case "copilot":
                var source = Text(definition.AuthSource) ?? CopilotDirectAuthSources.GitHubDeviceFlow;
                // A Copilot token of the environment comes without the GitHub account that has the quotas.
                return source == CopilotDirectAuthSources.CopilotTokenEnvironment
                    ? AgentSubscriptionUsageReading.NotAvailable
                    : await CopilotAccountUsage.ReadAsync(
                        httpClient, stateRootPath, definition.ProviderKey, Text(definition.GitHubEnterpriseUrl),
                        source == CopilotDirectAuthSources.GitHubTokenEnvironment ? Text(definition.GitHubTokenEnv) : null, cancellationToken).ConfigureAwait(false);
            case "claude-code":
                return await ClaudeCodeAccountUsage.ReadAsync(
                    new ClaudeCodeModelProviderRuntimeOptions
                    {
                        ProviderKey = definition.ProviderKey,
                        Command = Text(definition.Command),
                        ExtraArguments = definition.Arguments is { Count: > 0 } arguments ? [.. arguments] : [],
                    },
                    cancellationToken).ConfigureAwait(false);
            default:
                throw new ArgumentException($"The provider type '{definition.ProviderType}' has no usage to read.", nameof(definition));
        }
    }

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
