#pragma warning disable OPENAI001

using CodeAlta.Agent.OpenAI;
using CodeAlta.Agent.Runtime;
using OpenAI.Responses;

namespace CodeAlta.Agent.Mistral;

/// <summary>
/// Runs the turns of a Mistral provider on the Responses API of Mistral, which speaks the Responses protocol of OpenAI
/// without its state: <c>store</c> is false, no earlier response is named, and the whole conversation goes with each
/// request. The models are the ones of the Mistral provider, with their reasoning efforts.
/// </summary>
internal sealed class MistralResponsesTurnExecutor(MistralProviderOptions provider) : IModelProviderTurnExecutor, IModelProviderModelCatalog
{
    /// <summary>The protocol family of a Mistral provider on the Responses API.</summary>
    public const string ProtocolFamily = "mistral-responses";

    /// <summary>What the Responses API of Mistral takes: no stored responses, and the reasoning effort of each model.</summary>
    public static AgentProviderProfile DefaultProfile { get; } = new()
    {
        SupportsDeveloperRole = true,
        SupportsReasoningEffort = true,
        SupportsStore = false,
        StreamsUsage = true,
        MaxTokensFieldName = "max_output_tokens",
        ReasoningFieldNames = ["reasoning"],
    };

    public Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(
        ModelProviderRuntimeDescriptor providerDescriptor,
        CancellationToken cancellationToken = default)
        => MistralModelProviderRuntime.ListModelsAsync(provider, providerDescriptor, cancellationToken);

    public Task<AgentTurnResponse> ExecuteTurnAsync(
        AgentTurnRequest request,
        Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate,
        CancellationToken cancellationToken = default)
        => ExecuteTurnAsync(request, onUpdate, static (_, _) => ValueTask.CompletedTask, cancellationToken);

    public async Task<AgentTurnResponse> ExecuteTurnAsync(
        AgentTurnRequest request,
        Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate,
        Func<AgentTurnSessionUpdate, CancellationToken, ValueTask> onSessionUpdate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(onUpdate);
        ArgumentNullException.ThrowIfNull(onSessionUpdate);

        var apiKey = await provider.ResolveApiKeyAsync(cancellationToken).ConfigureAwait(false);
        var profile = provider.Profile ?? DefaultProfile;
        await using var executor = new OpenAIResponsesTurnExecutor(CreateOpenAIProviderOptions(apiKey, profile));
        return await executor.ExecuteTurnAsync(
            CreateDelegatedRequest(request, profile),
            onUpdate,
            onSessionUpdate,
            cancellationToken).ConfigureAwait(false);
    }

    internal static Uri GetResponsesBaseUri(MistralProviderOptions provider)
    {
        var baseUri = provider.BaseUri ?? provider.HttpClient?.BaseAddress ?? new Uri(MistralDefaults.DefaultBaseUrl);
        return new Uri($"{baseUri.AbsoluteUri.TrimEnd('/')}{MistralDefaults.ResponsesBasePath}", UriKind.Absolute);
    }

    private OpenAIProviderOptions CreateOpenAIProviderOptions(string? apiKey, AgentProviderProfile profile)
        => new()
        {
            ProviderKey = provider.ProviderKey,
            DisplayName = provider.DisplayName,
            ApiKey = apiKey,
            BaseUri = GetResponsesBaseUri(provider),
            HttpClient = provider.HttpClient,
            IsDefault = provider.IsDefault,
            Profile = profile,
            Compaction = provider.Compaction,
            ExtraHeaders = provider.ExtraHeaders,
            ModelOverrides = provider.ModelOverrides,
            ResponsesRequestCustomizer = static context => SetReasoningEffort(context.Request, context.Options),
            ResponseStreamObserver = static stream => new MistralResponsesStream(stream),
        };

    // Each model takes its own efforts: the one asked goes as Mistral names it, none included, so that a model that
    // reasons by default does not.
    private static void SetReasoningEffort(AgentTurnRequest request, CreateResponseOptions options)
    {
        if (MistralReasoningEfforts.Resolve(request) is not { } effort)
        {
            return;
        }

        options.ReasoningOptions ??= new ResponseReasoningOptions();
        options.ReasoningOptions.ReasoningEffortLevel = new ResponseReasoningEffortLevel(effort);
    }

    private AgentTurnRequest CreateDelegatedRequest(AgentTurnRequest request, AgentProviderProfile profile)
        => request with
        {
            Provider = request.Provider with
            {
                ProtocolFamily = "openai-responses",
                TransportKind = AgentTransportKind.OpenAIResponses,
                BaseUri = GetResponsesBaseUri(provider),
                Profile = profile,
            },
        };
}
