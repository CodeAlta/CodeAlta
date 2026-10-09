using System.Text.Json;

namespace CodeAlta.Agent.Claude;

/// <summary>
/// What a provider does with the API key of the environment (<c>ANTHROPIC_API_KEY</c>).
/// </summary>
/// <remarks>
/// Run interactively, Claude Code asks once whether to use such a key and saves the answer. Started by CodeAlta it
/// asks nothing and uses the key, which bills the API account of the key instead of the plan of the user's login:
/// CodeAlta decides instead, by starting the CLI with or without the variable.
/// </remarks>
public enum ClaudeCodeApiKeyPolicy
{
    /// <summary>
    /// Follows the answer Claude Code saved for the key. A turn does not start while there is none.
    /// </summary>
    FollowClaudeCode,

    /// <summary>
    /// The CLI uses the key: its usage is billed to the API account of the key.
    /// </summary>
    Use,

    /// <summary>
    /// The CLI is started without the key: it signs in with the user's login.
    /// </summary>
    Ignore,
}

/// <summary>
/// Whether a CLI process is started with the API key of the environment.
/// </summary>
internal enum ClaudeCodeApiKeyDecision
{
    /// <summary>The environment has no key, or the CLI uses a cloud provider: nothing to decide.</summary>
    NoKey,

    /// <summary>The CLI is given the key.</summary>
    Use,

    /// <summary>The CLI is started without the key.</summary>
    Ignore,

    /// <summary>The environment has a key and nothing says whether to use it.</summary>
    Undecided,
}

/// <summary>
/// Decides whether the CLI is given the API key of the environment.
/// </summary>
internal static class ClaudeCodeApiKey
{
    /// <summary>The variable the CLI reads the key from.</summary>
    public const string Variable = "ANTHROPIC_API_KEY";

    /// <summary>The message of a turn that does not start because nothing says whether to use the key.</summary>
    public const string UndecidedMessage =
        "ANTHROPIC_API_KEY is set and Claude Code has no saved answer for it. Choose whether to use it (billed to its " +
        "API account) or ignore it (your Claude login) in Settings > Providers > Claude Code, or with anthropic_api_key " +
        "in config.toml, then send again.";

    // The variables that make the CLI use a cloud provider, which signs in with its own credentials.
    private static readonly string[] CloudProviderVariables = ["CLAUDE_CODE_USE_BEDROCK", "CLAUDE_CODE_USE_VERTEX", "CLAUDE_CODE_USE_FOUNDRY"];

    // Claude Code saves an answer as the last characters of the trimmed key.
    private const int SavedKeyLength = 20;

    /// <summary>Decides for the environment of this process, which the CLI inherits.</summary>
    public static ClaudeCodeApiKeyDecision Decide(ClaudeCodeModelProviderRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var environment = options.GetEnvironmentVariable ?? Environment.GetEnvironmentVariable;
        return Decide(options.ApiKeyPolicy, environment, options.ReadClaudeConfig ?? (() => ReadClaudeConfig(environment)));
    }

    /// <summary>Decides from a policy, the environment and the configuration file of Claude Code.</summary>
    /// <param name="policy">The policy of the provider.</param>
    /// <param name="environment">Reads a variable of the environment the CLI inherits.</param>
    /// <param name="readClaudeConfig">Reads the configuration file of Claude Code; null when there is none.</param>
    public static ClaudeCodeApiKeyDecision Decide(ClaudeCodeApiKeyPolicy policy, Func<string, string?> environment, Func<string?> readClaudeConfig)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(readClaudeConfig);
        if (string.IsNullOrWhiteSpace(environment(Variable)) || CloudProviderVariables.Any(name => IsSet(environment(name))))
        {
            return ClaudeCodeApiKeyDecision.NoKey;
        }

        return policy switch
        {
            ClaudeCodeApiKeyPolicy.Use => ClaudeCodeApiKeyDecision.Use,
            ClaudeCodeApiKeyPolicy.Ignore => ClaudeCodeApiKeyDecision.Ignore,
            _ => ReadSavedAnswer(readClaudeConfig(), environment(Variable)!),
        };
    }

    /// <summary>
    /// Reads the answer Claude Code saved for a key in its configuration file: <c>customApiKeyResponses</c> lists the
    /// approved and the rejected keys by their last characters. A file that cannot be read, or that has another shape,
    /// saved no answer.
    /// </summary>
    internal static ClaudeCodeApiKeyDecision ReadSavedAnswer(string? config, string key)
    {
        if (string.IsNullOrWhiteSpace(config))
        {
            return ClaudeCodeApiKeyDecision.Undecided;
        }

        var trimmed = key.Trim();
        var saved = trimmed.Length > SavedKeyLength ? trimmed[^SavedKeyLength..] : trimmed;
        try
        {
            using var document = JsonDocument.Parse(config, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (!ClaudeCodeJson.TryGetObject(document.RootElement, "customApiKeyResponses", out var responses))
            {
                return ClaudeCodeApiKeyDecision.Undecided;
            }

            // An approval wins, as in the CLI.
            return Lists(responses, "approved", saved) ? ClaudeCodeApiKeyDecision.Use
                : Lists(responses, "rejected", saved) ? ClaudeCodeApiKeyDecision.Ignore
                : ClaudeCodeApiKeyDecision.Undecided;
        }
        catch (JsonException)
        {
            return ClaudeCodeApiKeyDecision.Undecided;
        }
    }

    /// <summary>The configuration file of Claude Code: <c>.claude.json</c> in <c>CLAUDE_CONFIG_DIR</c>, else in the home folder.</summary>
    internal static string ClaudeConfigPath(Func<string, string?> environment)
    {
        var folder = environment("CLAUDE_CONFIG_DIR");
        return Path.Combine(string.IsNullOrWhiteSpace(folder) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : folder, ".claude.json");
    }

    private static string? ReadClaudeConfig(Func<string, string?> environment)
    {
        try
        {
            return File.ReadAllText(ClaudeConfigPath(environment));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool Lists(JsonElement responses, string name, string saved)
    {
        if (!ClaudeCodeJson.TryGetArray(responses, name, out var entries))
        {
            return false;
        }

        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.String && string.Equals(entry.GetString(), saved, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    // As the CLI reads such a flag.
    private static bool IsSet(string? value)
        => value?.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on";
}
