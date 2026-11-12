using System.Text.Json;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Agent.ModelCatalog;

/// <summary>
/// Leaves out of the models of a gateway those it serves through another API than the one of the provider. A gateway
/// such as OpenCode lists all of its models at each of its endpoints, but serves each one through one API only: Chat
/// Completions, Responses, Anthropic Messages or Gemini. models.dev says which, by the package of the AI SDK of each model
/// (<c>provider.npm</c>, else the package of the provider). A provider whose models all go through the API of the
/// provider, or a model models.dev does not know, is kept as it is.
/// </summary>
internal static class AgentModelApiFilter
{
    /// <summary>The models a provider of this transport can call.</summary>
    /// <param name="models">The models, as the provider lists them.</param>
    /// <param name="catalog">models.dev, or null for none: nothing is left out then.</param>
    /// <param name="modelsDevProviderId">The models.dev provider of the provider, or null.</param>
    /// <param name="transport">The API the provider calls, or null when it is not known: nothing is left out then.</param>
    /// <returns>The models, without those models.dev says another API serves.</returns>
    public static IReadOnlyList<AgentModelInfo> KeepOwnApi(
        IReadOnlyList<AgentModelInfo> models,
        ModelsDevCatalogService? catalog,
        string? modelsDevProviderId,
        AgentTransportKind? transport)
    {
        ArgumentNullException.ThrowIfNull(models);
        if (models.Count == 0 || catalog is null || transport is null || string.IsNullOrWhiteSpace(modelsDevProviderId) ||
            !catalog.TryGetProvider(modelsDevProviderId, out var provider))
        {
            return models;
        }

        var providerApi = ApiOf(provider.NpmPackage);
        // Only a gateway that serves some of its models through another API than its own: the models of any other
        // provider all go through the API it is configured with.
        if (!provider.Models.Values.Any(model => ApiOf(PackageOf(model)) is { } api && api != providerApi))
        {
            return models;
        }

        var kept = models.Where(model => !provider.Models.TryGetValue(model.Id, out var known) ||
                                         (ApiOf(PackageOf(known)) ?? providerApi) is not { } api || api == transport)
            .ToArray();
        return kept.Length == models.Count ? models : kept;
    }

    /// <summary>The API a package of the AI SDK calls, or null for one that is not one of these four.</summary>
    internal static AgentTransportKind? ApiOf(string? package) => package?.Trim() switch
    {
        "@ai-sdk/openai-compatible" => AgentTransportKind.OpenAIChatCompletions,
        "@ai-sdk/openai" => AgentTransportKind.OpenAIResponses,
        "@ai-sdk/anthropic" => AgentTransportKind.AnthropicMessages,
        "@ai-sdk/google" => AgentTransportKind.GoogleGeminiApi,
        _ => null,
    };

    // The package of a model of models.dev: its own ({"provider":{"npm":"@ai-sdk/anthropic"}}), else none.
    private static string? PackageOf(ModelsDevModelDefinition model)
        => model.ExtensionData?.TryGetValue("provider", out var provider) == true && provider.ValueKind == JsonValueKind.Object &&
           provider.TryGetProperty("npm", out var npm) && npm.ValueKind == JsonValueKind.String
            ? npm.GetString()
            : null;
}
