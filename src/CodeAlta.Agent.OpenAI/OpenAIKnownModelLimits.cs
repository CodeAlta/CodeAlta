using CodeAlta.Agent.ModelCatalog;

namespace CodeAlta.Agent.OpenAI;

/// <summary>
/// The limits of OpenAI's models on OpenAI's own endpoints (the API and the ChatGPT subscription) that models.dev does
/// not give: it gives the largest context a model accepts, where a session should keep to the one it is priced and
/// tuned for.
/// </summary>
internal static class OpenAIKnownModelLimits
{
    /// <summary>
    /// The share of the <c>context_window</c> of Codex's catalog at which Codex compacts, and a session of these models.
    /// </summary>
    public const double CodexCompactionRatio = 0.90;

    /// <summary>
    /// The table, first match wins. GPT-6: models.dev gives 922,000 input tokens, but past 272,000 the input costs
    /// twice as much and the output 1.5 times; Codex compacts at 90% of the <c>context_window</c> of its catalog. The
    /// table is for a model whose provider gives no limit (the Models API of OpenAI): the <c>context_window</c> of
    /// Codex's catalog, 272,000 or more for a newer model, is kept.
    /// </summary>
    public static IReadOnlyList<AgentModelKnownLimit> Table { get; } =
    [
        new("gpt-6", InputTokenLimit: 272_000, ContextWindowTokens: 400_000, CompactionRatio: CodexCompactionRatio),
    ];

    /// <summary>The table for the provider: OpenAI's own endpoints only, null for another one.</summary>
    public static IReadOnlyList<AgentModelKnownLimit>? For(OpenAIProviderOptions provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        if (provider.CodexSubscription is not null)
        {
            return Table;
        }

        return !provider.IsAzureOpenAI &&
            (provider.BaseUri is null || string.Equals(provider.BaseUri.Host, "api.openai.com", StringComparison.OrdinalIgnoreCase))
                ? Table
                : null;
    }
}
