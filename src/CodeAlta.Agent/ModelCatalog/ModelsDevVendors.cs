using System.Collections.Immutable;

namespace CodeAlta.Agent.ModelCatalog;

/// <summary>
/// The models.dev provider of the vendor that makes a model, found from the name of the model. A model a provider
/// resells (GitHub Copilot, a coding plan, a subscription CLI) or lists under a name of its own is looked up there
/// when its provider does not list it, and its price is the one of the vendor when its provider gives none.
/// </summary>
internal static class ModelsDevVendors
{
    // The start of a model name, after any path (`models/`, `anthropic/`), and the vendor that makes it.
    private static readonly ImmutableArray<(string Prefix, string ProviderId)> Prefixes =
    [
        ("claude-", "anthropic"),
        ("gpt-", "openai"),
        ("chatgpt-", "openai"),
        ("codex-", "openai"),
        ("o1", "openai"),
        ("o3", "openai"),
        ("o4-", "openai"),
        ("gemini-", "google"),
        ("gemma-", "google"),
        ("grok-", "xai"),
        ("glm-", "zai"),
        ("kimi-", "moonshotai"),
        ("deepseek-", "deepseek"),
        ("qwen", "alibaba"),
        ("qwq-", "alibaba"),
        ("minimax-", "minimax"),
        ("mimo-", "xiaomi"),
        ("mistral-", "mistral"),
        ("codestral", "mistral"),
        ("devstral", "mistral"),
        ("magistral", "mistral"),
        ("ministral", "mistral"),
        ("pixtral", "mistral"),
        ("voxtral", "mistral"),
    ];

    /// <summary>
    /// Returns the models.dev provider of the vendor of a model, or <see langword="null"/> when the name tells none.
    /// </summary>
    public static string? Find(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return null;
        }

        // A display name ("Kimi K3") is read as an identifier ("kimi-k3").
        var name = modelId.Trim().Replace(' ', '-');
        var slash = name.LastIndexOf('/');
        if (slash >= 0)
        {
            name = name[(slash + 1)..];
        }

        foreach (var (prefix, providerId) in Prefixes)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return providerId;
            }
        }

        return null;
    }
}
