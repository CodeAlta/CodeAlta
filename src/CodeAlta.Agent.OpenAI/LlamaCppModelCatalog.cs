using System.Text.Json;

namespace CodeAlta.Agent.OpenAI;

/// <summary>
/// Reads the models of a llama.cpp server (llama-server) from its list of models, the one request it answers cheaply
/// without waking a sleeping model: the context the server was started with, and whether it reads images.
/// </summary>
internal static class LlamaCppModelCatalog
{
    /// <summary>Lists the models the server serves.</summary>
    /// <param name="provider">The provider of the server.</param>
    /// <param name="cancellationToken">A token that cancels the request.</param>
    /// <returns>The models of the server, with what it says of them.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="provider"/> is null.</exception>
    public static async Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(OpenAIProviderOptions provider, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var collection = await OpenAIProviderSdkFactory.CreateModelClient(provider).GetModelsAsync(cancellationToken).ConfigureAwait(false);
        return ReadModels(collection.GetRawResponse().Content, provider.ProviderKey);
    }

    // The data of OpenAI holds each model with the context of the server in its meta (n_ctx). Beside it, the server
    // lists its models as Ollama does, with their capabilities: "multimodal" when it was started with the projector of a
    // vision model (--mmproj), and only "completion" when it reads no image.
    internal static IReadOnlyList<AgentModelInfo> ReadModels(BinaryData content, string providerKey)
    {
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var readsImages = ReadImageInput(root);
        var models = new List<AgentModelInfo>();
        foreach (var entry in data.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object ||
                !entry.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String ||
                id.GetString() is not { Length: > 0 } modelId)
            {
                continue;
            }

            var capabilities = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (entry.TryGetProperty("owned_by", out var ownedBy) && ownedBy.ValueKind == JsonValueKind.String)
            {
                capabilities["ownedBy"] = ownedBy.GetString();
            }

            if (entry.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Object &&
                meta.TryGetProperty("n_ctx", out var context) && context.TryGetInt64(out var contextTokens) && contextTokens > 0)
            {
                capabilities["contextWindow"] = contextTokens;
                capabilities["contextWindowTokens"] = contextTokens;
            }

            if (readsImages.TryGetValue(modelId, out var images))
            {
                capabilities["supportsImageInput"] = images;
            }

            models.Add(new AgentModelInfo(modelId, DisplayName: modelId, Provider: providerKey, Capabilities: capabilities));
        }

        return models;
    }

    private static Dictionary<string, bool> ReadImageInput(JsonElement root)
    {
        var readsImages = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (!root.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
        {
            return readsImages;
        }

        foreach (var model in models.EnumerateArray())
        {
            if (model.ValueKind == JsonValueKind.Object &&
                model.TryGetProperty("model", out var id) && id.ValueKind == JsonValueKind.String &&
                id.GetString() is { Length: > 0 } modelId &&
                model.TryGetProperty("capabilities", out var capabilities) && capabilities.ValueKind == JsonValueKind.Array)
            {
                readsImages[modelId] = capabilities.EnumerateArray().Any(static capability =>
                    capability.ValueKind == JsonValueKind.String && capability.ValueEquals("multimodal"));
            }
        }

        return readsImages;
    }
}
