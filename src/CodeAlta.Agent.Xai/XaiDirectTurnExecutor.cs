#pragma warning disable OPENAI001

using System.ClientModel;
using CodeAlta.Agent.LocalRuntime;
using CodeAlta.Agent.OpenAI;

namespace CodeAlta.Agent.Xai;

internal sealed class XaiDirectTurnExecutor : ILocalAgentTurnExecutor
{
    private static readonly LocalAgentProviderProfile OpenAIResponsesProfile = new()
    {
        SupportsDeveloperRole = true,
        SupportsReasoningEffort = true,
        SupportsStore = false,
        StreamsUsage = true,
        MaxTokensFieldName = "max_output_tokens",
        ReasoningFieldNames = ["reasoning"],
    };

    private readonly XaiProviderOptions _provider;
    private readonly HttpClient _httpClient;
    private readonly XaiDirectAuthManager _authManager;
    private readonly XaiModelDiscoveryClient _modelDiscovery;

    public XaiDirectTurnExecutor(XaiProviderOptions provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _provider = provider;
        _httpClient = provider.HttpClient ?? new HttpClient();
        _authManager = new XaiDirectAuthManager(provider, _httpClient);
        _modelDiscovery = new XaiModelDiscoveryClient(provider, _authManager, _httpClient);
    }

    public Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(
        LocalAgentProviderDescriptor provider,
        CancellationToken cancellationToken = default)
        => _modelDiscovery.ListModelsAsync(provider, cancellationToken);

    public Task<LocalAgentTurnResponse> ExecuteTurnAsync(
        LocalAgentTurnRequest request,
        Func<LocalAgentTurnDelta, CancellationToken, ValueTask> onUpdate,
        CancellationToken cancellationToken = default)
        => ExecuteTurnAsync(
            request,
            onUpdate,
            static (_, _) => ValueTask.CompletedTask,
            cancellationToken);

    public async Task<LocalAgentTurnResponse> ExecuteTurnAsync(
        LocalAgentTurnRequest request,
        Func<LocalAgentTurnDelta, CancellationToken, ValueTask> onUpdate,
        Func<LocalAgentTurnSessionUpdate, CancellationToken, ValueTask> onSessionUpdate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(onUpdate);
        ArgumentNullException.ThrowIfNull(onSessionUpdate);

        try
        {
            return await ExecuteTurnCoreAsync(request, onUpdate, onSessionUpdate, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ShouldRefreshCredential(ex))
        {
            await _authManager.ForceRefreshAsync(cancellationToken).ConfigureAwait(false);
            return await ExecuteTurnCoreAsync(request, onUpdate, onSessionUpdate, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<LocalAgentTurnResponse> ExecuteTurnCoreAsync(
        LocalAgentTurnRequest request,
        Func<LocalAgentTurnDelta, CancellationToken, ValueTask> onUpdate,
        Func<LocalAgentTurnSessionUpdate, CancellationToken, ValueTask> onSessionUpdate,
        CancellationToken cancellationToken)
    {
        var credential = await _authManager.GetCredentialAsync(cancellationToken).ConfigureAwait(false);
        var profile = _provider.Profile ?? OpenAIResponsesProfile;
        var provider = CreateOpenAIProviderOptions(credential, profile);
        await using var executor = new OpenAIResponsesTurnExecutor(provider);
        return await executor.ExecuteTurnAsync(
            CreateDelegatedRequest(request, credential, profile),
            onUpdate,
            onSessionUpdate,
            cancellationToken).ConfigureAwait(false);
    }

    private OpenAIProviderOptions CreateOpenAIProviderOptions(
        XaiDirectCredential credential,
        LocalAgentProviderProfile profile)
        => new()
        {
            ProviderKey = _provider.ProviderKey,
            DisplayName = _provider.DisplayName,
            ApiKey = credential.Token,
            BaseUri = credential.BaseUri,
            HttpClient = _httpClient,
            IsDefault = _provider.IsDefault,
            Profile = profile,
            Compaction = _provider.Compaction,
            ModelOverrides = _provider.ModelOverrides,
            ProtocolTracing = _provider.ProtocolTraceEnabled
                ? new OpenAIProtocolTraceOptions { Enabled = true, StateRootPath = _provider.StateRootPath }
                : null,
        };

    private static LocalAgentTurnRequest CreateDelegatedRequest(
        LocalAgentTurnRequest request,
        XaiDirectCredential credential,
        LocalAgentProviderProfile profile)
    {
        var provider = request.Provider with
        {
            ProtocolFamily = "openai-responses",
            TransportKind = LocalAgentTransportKind.OpenAIResponses,
            BaseUri = credential.BaseUri,
            Profile = profile,
        };
        return request with { Provider = provider };
    }

    private static bool ShouldRefreshCredential(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is ClientResultException { Status: 401 })
            {
                return true;
            }

            if (current is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized })
            {
                return true;
            }

            var typeName = current.GetType().Name;
            if (typeName.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
