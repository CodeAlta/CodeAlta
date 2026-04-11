#pragma warning disable OPENAI001

using System.ClientModel;
using CodeAlta.Agent.LocalRuntime;
using CodeAlta.Agent.ModelCatalog;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Models;
using OpenAI.Responses;

namespace CodeAlta.Agent.OpenAI;

internal static class OpenAIProviderSdkFactory
{
    public static OpenAIClient CreateClient(OpenAIProviderOptions provider)
        => new(CreateCredential(provider), CreateClientOptions(provider));

    public static ResponsesClient CreateResponsesClient(OpenAIProviderOptions provider, string? model)
        => provider.ResponsesClientFactory is not null
            ? provider.ResponsesClientFactory(model)
            : new ResponsesClient(CreateCredential(provider), CreateClientOptions(provider));

    public static ChatClient CreateChatClient(OpenAIProviderOptions provider, string? model)
        => provider.ChatClientFactory is not null
            ? provider.ChatClientFactory(model)
            : new ChatClient(model ?? string.Empty, CreateCredential(provider), CreateClientOptions(provider));

    public static OpenAIModelClient CreateModelClient(OpenAIProviderOptions provider)
        => CreateClient(provider).GetOpenAIModelClient();

    public static async Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(
        OpenAIProviderOptions provider,
        LocalAgentProviderDescriptor providerDescriptor,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<AgentModelInfo> models;
        if (provider.ModelListAsync is not null)
        {
            models = await provider.ModelListAsync(cancellationToken).ConfigureAwait(false);
            return AgentModelMetadataEnricher.EnrichModels(
                models,
                provider.ModelCatalog,
                provider.ModelsDevProviderId,
                provider.ModelOverrides);
        }

        var client = CreateModelClient(provider);
        var collection = await client.GetModelsAsync(cancellationToken).ConfigureAwait(false);
        models = collection.Value
            .Select(model => new AgentModelInfo(
                model.Id,
                DisplayName: model.Id,
                Provider: providerDescriptor.ProviderKey,
                Capabilities: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["createdAt"] = model.CreatedAt,
                    ["ownedBy"] = model.OwnedBy,
                }))
            .ToArray();
        return AgentModelMetadataEnricher.EnrichModels(
            models,
            provider.ModelCatalog,
            provider.ModelsDevProviderId,
            provider.ModelOverrides);
    }

    private static ApiKeyCredential CreateCredential(OpenAIProviderOptions provider)
        => new(provider.ApiKey ?? string.Empty);

    private static OpenAIClientOptions CreateClientOptions(OpenAIProviderOptions provider)
        => new()
        {
            Endpoint = provider.BaseUri,
            OrganizationId = provider.OrganizationId,
            ProjectId = provider.ProjectId,
        };
}
