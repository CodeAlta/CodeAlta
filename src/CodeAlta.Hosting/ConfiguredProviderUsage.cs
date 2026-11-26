using CodeAlta.Agent;
using CodeAlta.Agent.Claude;
using CodeAlta.Agent.Copilot;
using CodeAlta.Agent.OpenAI.Codex;
using CodeAlta.Catalog;
using CodeAlta.CodexAppServer;

namespace CodeAlta.Hosting;

/// <summary>
/// Reads the usage of the subscription behind a configured provider, for a frontend that does not name the provider
/// packages: the windows of a ChatGPT plan for Codex, the quotas of GitHub Copilot, the limits of a Claude plan for
/// Claude Code, the windows of a Google AI plan for Antigravity. Each provider is asked in its own way, and none of
/// them is sent a model turn.
/// </summary>
public static class ConfiguredProviderUsage
{
    /// <summary>Gets whether a provider type is a subscription whose usage can be read.</summary>
    /// <param name="providerType">The provider type of a definition.</param>
    /// <returns>
    /// True for Codex, Codex CLI, Copilot and Claude Code, and for an ACP agent, of which only the Antigravity CLI is
    /// described as a definition (<see cref="DescribePluginProvider"/>).
    /// </returns>
    public static bool Supports(string? providerType) => providerType is "codex" or "codex-cli" or "copilot" or "claude-code" or AcpType;

    private const string AcpType = "acp";

    /// <summary>
    /// Describes a provider of a type of a built-in plugin whose usage can be read as a definition, from the settings
    /// of its section: a Codex CLI provider (<c>codex-cli</c>), and an ACP agent that runs the Antigravity CLI
    /// (<c>acp</c> with <c>agy acp</c>); null for any other provider of a plugin.
    /// </summary>
    /// <param name="provider">The section of the provider.</param>
    /// <returns>The definition, or <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="provider"/> is null.</exception>
    public static CodeAltaProviderDocument? DescribePluginProvider(CodeAltaExternalProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        string? Value(string name) => provider.Values.TryGetValue(name, out var value) && value is string text && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;
        if (string.Equals(provider.ProviderType, AcpType, StringComparison.Ordinal))
        {
            var arguments = provider.Values.TryGetValue("arguments", out var list) && list is System.Collections.IEnumerable items and not string
                ? items.OfType<string>()
                : [];
            return Value("command") is { } agent && AntigravityCli.IsAcpServer(agent, arguments)
                ? new CodeAltaProviderDocument
                {
                    ProviderKey = provider.ProviderKey,
                    ProviderType = provider.ProviderType,
                    Enabled = provider.Values.TryGetValue("enabled", out var on) && on is bool value ? value : null,
                    Command = agent,
                }
                : null;
        }

        if (!string.Equals(provider.ProviderType, "codex-cli", StringComparison.Ordinal))
        {
            return null;
        }

        return new CodeAltaProviderDocument
        {
            ProviderKey = provider.ProviderKey,
            ProviderType = provider.ProviderType,
            Enabled = provider.Values.TryGetValue("enabled", out var enabled) && enabled is bool flag ? flag : null,
            Command = Value("command"),
            CodexHome = Value("codex_home"),
        };
    }

    /// <summary>Asks the provider of a definition for the usage of its subscription.</summary>
    /// <param name="definition">The type-completed provider definition.</param>
    /// <param name="stateRootPath">The global state root that holds provider credentials.</param>
    /// <param name="httpClient">The client that sends the requests of the providers that are asked over HTTP.</param>
    /// <param name="cancellationToken">Stops the reading.</param>
    /// <returns>
    /// The usage, or why there is none: no account signed in, or an account that gives no usage. Codex, Claude Code
    /// and Antigravity are read by asking their own CLI, when the machine has it; for Codex it must be signed in with
    /// the same account as CodeAlta, whose own sign-in is not given the usage of the plan.
    /// </returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">The root is blank, or the provider type is not one whose usage can be read.</exception>
    /// <exception cref="IOException">Provider state cannot be read.</exception>
    /// <exception cref="HttpRequestException">A provider could not be reached or answered with an error.</exception>
    /// <exception cref="System.Text.Json.JsonException">A provider answered with something else than its usage.</exception>
    /// <exception cref="InvalidOperationException">The CLI of Codex, of Claude Code or of Antigravity did not answer.</exception>
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
                    ? await CodexAccountUsage.ReadAsync(credential.Email ?? credential.AccountLabel, Text(definition.CodexHome), cancellationToken).ConfigureAwait(false)
                    : AgentSubscriptionUsageReading.NotSignedIn;
            case "codex-cli":
                // The app-server of the provider, in its own CODEX_HOME: the pinned one CodeAlta downloaded, else the
                // command its settings name. Its sign-in is the one of that home, so any account of it is shown.
                var command = Text(definition.Command) is { } named
                    ? CodexAppServerCommand.FromUserCommand(named)?.FileName
                    : new CodexAppServerInstaller(Path.Combine(stateRootPath, "cache", "codex-cli")).Installed?.Executable;
                return command is null
                    ? AgentSubscriptionUsageReading.NoTool
                    : await CodexAccountUsage.ReadAsync(expectedAccount: null, CodexAppServerHome.Resolve(Text(definition.CodexHome), stateRootPath, definition.ProviderKey), cancellationToken, command)
                        .ConfigureAwait(false);
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
                        ConfigDirectory = Text(definition.ConfigDir),
                        EnvironmentVariables = definition.Env is { Count: > 0 } variables ? new Dictionary<string, string>(variables, StringComparer.Ordinal) : new Dictionary<string, string>(StringComparer.Ordinal),
                        PinnedCliRoot = new CatalogOptions { GlobalRoot = stateRootPath }.ClaudeCodeRoot,
                        ExtraArguments = definition.Arguments is { Count: > 0 } arguments ? [.. arguments] : [],
                        ApiKeyPolicy = ClaudeCodeModelProviderRuntimeOptions.ParseApiKeyPolicy(definition.AnthropicApiKey),
                    },
                    cancellationToken).ConfigureAwait(false);
            case AcpType:
                // The Antigravity CLI the provider runs as its agent: its sign-in is the one of the agent.
                return Text(definition.Command) is { } agy
                    ? await AntigravityAccountUsage.ReadAsync(agy, cancellationToken).ConfigureAwait(false)
                    : AgentSubscriptionUsageReading.NoTool;
            default:
                throw new ArgumentException($"The provider type '{definition.ProviderType}' has no usage to read.", nameof(definition));
        }
    }

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
