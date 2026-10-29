namespace CodeAlta.Agent.Mistral;

internal static class MistralDefaults
{
    public const string DefaultBaseUrl = "https://api.mistral.ai";

    // Mistral's Python SDK v2 kept the public HTTP API paths unchanged.
    public const string ChatCompletionsPath = "/v1/chat/completions";

    public const string ModelsPath = "/v1/models";

    // The models Mistral Vibe offers, with their reasoning efforts; the endpoint asks for the surface.
    public const string VibeModelCatalogPath = "/v1/model-catalog?surface=vibe-code";

    public const string ResponsesBasePath = "/v1";
}
