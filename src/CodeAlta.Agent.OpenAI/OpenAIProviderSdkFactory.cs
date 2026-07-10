#pragma warning disable OPENAI001

using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using Azure.AI.OpenAI;
using CodeAlta.Agent.Runtime;
using CodeAlta.Agent.ModelCatalog;
using CodeAlta.Agent.OpenAI.Codex;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Models;
using OpenAI.Responses;
using XenoAtom.Logging;

namespace CodeAlta.Agent.OpenAI;

internal static class OpenAIProviderSdkFactory
{
    private static readonly Logger Logger = LogManager.GetLogger("CodeAlta.Agent.OpenAI");
    private static readonly HttpClient CodexOAuthHttpClient = new();

    public static OpenAIClient CreateClient(OpenAIProviderOptions provider)
        => new(CreateCredential(provider), CreateClientOptions(provider));

    public static ResponsesClient CreateResponsesClient(OpenAIProviderOptions provider, string? model)
    {
        if (provider.ResponsesClientFactory is not null)
        {
            return provider.ResponsesClientFactory(model);
        }

        if (provider.IsAzureOpenAI)
        {
            throw CreateAzureOpenAIResponsesNotSupportedException();
        }

        return new ResponsesClient(CreateCredential(provider), CreateResponsesClientOptions(provider));
    }

    public static ResponsesClient CreateResponsesClient(
        OpenAIProviderOptions provider,
        OpenAIResponsesClientFactoryContext context)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(context);

        if (provider.ResponsesClientContextFactory is not null)
        {
            return provider.ResponsesClientContextFactory(context);
        }

        if (provider.ResponsesClientFactory is not null)
        {
            return provider.ResponsesClientFactory(context.ModelId);
        }

        if (provider.CodexSubscription is not null)
        {
            return CreateCodexSubscriptionResponsesClient(provider, context);
        }

        if (provider.IsAzureOpenAI)
        {
            throw CreateAzureOpenAIResponsesNotSupportedException();
        }

        var protocolTrace = OpenAIProtocolTraceLogger.Create(provider.ProtocolTracing, context);
        return new ResponsesClient(CreateCredential(provider), CreateResponsesClientOptions(provider, protocolTrace));
    }

    public static ChatClient CreateChatClient(
        OpenAIProviderOptions provider,
        string? model,
        OpenAIProtocolTraceLogger? protocolTrace = null)
        => provider.ChatClientFactory is not null
            ? provider.ChatClientFactory(model)
            : provider.IsAzureOpenAI
                ? CreateAzureOpenAIClient(provider, protocolTrace).GetChatClient(model ?? string.Empty)
            : new ChatClient(model ?? string.Empty, CreateCredential(provider), CreateClientOptionsCore(provider, protocolTrace));

    public static async ValueTask ForceRefreshCodexSubscriptionCredentialAsync(
        OpenAIProviderOptions provider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (provider.CodexSubscription is not { } options)
        {
            return;
        }

        if (provider.CodexSubscriptionCredentialRefreshAsync is not null)
        {
            await provider.CodexSubscriptionCredentialRefreshAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var authManager = CreateCodexSubscriptionAuthManager(
            provider,
            options,
            ResolveStateRootPath(provider));
        await authManager.ForceRefreshCredentialAsync(cancellationToken).ConfigureAwait(false);
    }

    public static OpenAIModelClient CreateModelClient(OpenAIProviderOptions provider)
        => CreateClient(provider).GetOpenAIModelClient();

    public static async Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(
        OpenAIProviderOptions provider,
        ModelProviderRuntimeDescriptor providerDescriptor,
        CancellationToken cancellationToken)
    {
        var models = await ListModelsCoreAsync(provider, providerDescriptor, cancellationToken).ConfigureAwait(false);
        return AgentModelMetadataEnricher.EnrichModels(
            models,
            provider.ModelCatalog,
            provider.ModelsDevProviderId,
            provider.ModelOverrides,
            provider.ModelsIncludeRegex);
    }

    private static async Task<IReadOnlyList<AgentModelInfo>> ListModelsCoreAsync(
        OpenAIProviderOptions provider,
        ModelProviderRuntimeDescriptor providerDescriptor,
        CancellationToken cancellationToken)
    {
        try
        {
            if (provider.CodexSubscription is not null)
            {
                return await ListCodexSubscriptionModelsAsync(
                    provider,
                    providerDescriptor,
                    cancellationToken).ConfigureAwait(false);
            }

            if (!string.IsNullOrWhiteSpace(provider.SingleModelId))
            {
                LogInfo(
                    $"Using configured single-model catalog provider={providerDescriptor.ProviderKey} displayName={providerDescriptor.DisplayName} model={provider.SingleModelId.Trim()}");
                return
                [
                    CreateSingleModelInfo(provider.SingleModelId, providerDescriptor),
                ];
            }

            if (provider.ModelListAsync is not null)
            {
                return await provider.ModelListAsync(cancellationToken).ConfigureAwait(false);
            }

            if (provider.IsAzureOpenAI)
            {
                LogInfo(
                    $"Azure OpenAI model discovery is not supported by the Azure OpenAI SDK; using empty catalog provider={providerDescriptor.ProviderKey} displayName={providerDescriptor.DisplayName}");
                return [];
            }

            var client = CreateModelClient(provider);
            var collection = await client.GetModelsAsync(cancellationToken).ConfigureAwait(false);
            return collection.Value
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
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (TryListModelsFromCatalog(provider, providerDescriptor, ex, out var models))
        {
            return models;
        }
    }

    private static async ValueTask<IReadOnlyList<AgentModelInfo>> ListCodexSubscriptionModelsAsync(
        OpenAIProviderOptions provider,
        ModelProviderRuntimeDescriptor providerDescriptor,
        CancellationToken cancellationToken)
    {
        var options = provider.CodexSubscription
            ?? throw new InvalidOperationException("Codex subscription options are required.");
        if (string.Equals(options.ModelDiscovery, "static", StringComparison.OrdinalIgnoreCase))
        {
            return ListCodexSubscriptionStaticModels(providerDescriptor);
        }

        try
        {
            var stateRootPath = ResolveStateRootPath(provider);
            var authManager = CreateCodexSubscriptionAuthManager(provider, options, stateRootPath);
            var httpClient = CodexSubscriptionHttpRequestFactory.ResolveHttpClient(provider, out var ownsHttpClient);
            IReadOnlyList<CodexSubscriptionDiscoveredModel> discoveredModels;
            try
            {
                var discoveryClient = new CodexSubscriptionModelDiscoveryClient(
                    httpClient,
                    authManager,
                    options,
                    CreateCodeAltaUserAgentApplicationId());
                discoveredModels = await discoveryClient.GetModelsAsync(
                    provider.BaseUri ?? CodexSubscriptionHttpRequestFactory.DefaultBaseUri,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (ownsHttpClient)
                {
                    httpClient.Dispose();
                }
            }
            var models = MapCodexSubscriptionDiscoveredModels(
                discoveredModels,
                providerDescriptor,
                options);
            LogInfo(
                $"Using Codex subscription authenticated model catalog provider={providerDescriptor.ProviderKey} displayName={providerDescriptor.DisplayName} models={models.Count}");
            return models;
        }
        catch (Exception ex) when (ShouldUseCodexStaticModelFallback(options, ex))
        {
            LogWarn(
                ex,
                $"Codex model discovery failed; falling back to static catalog provider={providerDescriptor.ProviderKey} displayName={providerDescriptor.DisplayName}");
            return ListCodexSubscriptionStaticModels(providerDescriptor);
        }
    }

    private static IReadOnlyList<AgentModelInfo> ListCodexSubscriptionStaticModels(
        ModelProviderRuntimeDescriptor providerDescriptor)
    {
        var models = CodexSubscriptionStaticModelCatalog.List(providerDescriptor);
        LogInfo(
            $"Using Codex subscription static model catalog provider={providerDescriptor.ProviderKey} displayName={providerDescriptor.DisplayName} models={models.Count}");
        return models;
    }

    private static bool ShouldUseCodexStaticModelFallback(
        OpenAICodexSubscriptionOptions options,
        Exception exception)
    {
        if (!string.Equals(
                options.ModelDiscovery,
                "codex_endpoint_with_static_fallback",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return exception switch
        {
            HttpRequestException => true,
            TimeoutException => true,
            InvalidOperationException invalidOperationException
                when invalidOperationException.Message.Contains("login is required", StringComparison.OrdinalIgnoreCase) => true,
            JsonException => true,
            CodexSubscriptionModelDiscoveryException { StatusCode: System.Net.HttpStatusCode.Unauthorized } => false,
            CodexSubscriptionModelDiscoveryException => true,
            _ => false,
        };
    }

    private static IReadOnlyList<AgentModelInfo> MapCodexSubscriptionDiscoveredModels(
        IReadOnlyList<CodexSubscriptionDiscoveredModel> discoveredModels,
        ModelProviderRuntimeDescriptor providerDescriptor,
        OpenAICodexSubscriptionOptions options)
    {
        var includeWebSocketRequiredModels = AllowsWebSocketRequiredModels(options);
        var supportedModels = discoveredModels
            .Where(model => model.SupportedInApi && (includeWebSocketRequiredModels || !model.RequiresWebSocket))
            .GroupBy(static model => model.Id, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToArray();

        return supportedModels
            .Where(static model => model.Listable && !model.Hidden)
            .Select(model => CreateModelInfo(model, providerDescriptor))
            .OrderBy(static model => model.DisplayName ?? model.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static AgentModelInfo CreateModelInfo(
        CodexSubscriptionDiscoveredModel model,
        ModelProviderRuntimeDescriptor providerDescriptor)
    {
        var capabilities = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["source"] = "codex-endpoint",
            ["supportedInApi"] = model.SupportedInApi,
            ["hidden"] = model.Hidden,
            ["listable"] = model.Listable,
            ["supportsReasoningSummary"] = model.SupportsReasoningSummary,
            ["supportsReasoningSummaries"] = model.SupportsReasoningSummary,
            ["supportsEncryptedReasoning"] = model.SupportsEncryptedReasoning,
            ["supportsTextVerbosity"] = model.SupportsTextVerbosity,
            ["supportVerbosity"] = model.SupportsTextVerbosity,
            ["supportsTools"] = model.SupportsTools,
            ["supportsImageInput"] = model.SupportsImageInput,
            ["supportsParallelToolCalls"] = model.SupportsParallelToolCalls,
            ["supportsImageDetailOriginal"] = model.SupportsImageDetailOriginal,
            ["useResponsesLite"] = model.UseResponsesLite,
            ["requiresWebSocket"] = model.RequiresWebSocket,
        };
        if (model.ContextWindow is { } contextWindow)
        {
            capabilities["contextWindow"] = contextWindow;
        }

        if (!string.IsNullOrWhiteSpace(model.DefaultTextVerbosity))
        {
            capabilities["defaultTextVerbosity"] = model.DefaultTextVerbosity;
        }

        if (!string.IsNullOrWhiteSpace(model.ETag))
        {
            capabilities["etag"] = model.ETag;
        }

        return new AgentModelInfo(
            model.Id,
            DisplayName: model.DisplayName,
            Provider: providerDescriptor.ProviderKey,
            DefaultReasoningEffort: ParseReasoningEffort(model.DefaultReasoningEffort),
            SupportedReasoningEfforts: GetSupportedReasoningEfforts(model),
            Capabilities: capabilities);
    }

    private static IReadOnlyList<AgentReasoningEffort> GetSupportedReasoningEfforts(
        CodexSubscriptionDiscoveredModel model)
    {
        if (!model.SupportsReasoningEffort)
        {
            return [];
        }

        if (model.SupportedReasoningEfforts is { } advertisedEfforts)
        {
            return advertisedEfforts
                .Select(ParseReasoningEffort)
                .Where(static effort => effort is not null)
                .Select(static effort => effort!.Value)
                .Distinct()
                .ToArray();
        }

        return
        [
            AgentReasoningEffort.Low,
            AgentReasoningEffort.Medium,
            AgentReasoningEffort.High,
            AgentReasoningEffort.XHigh,
        ];
    }

    private static bool AllowsWebSocketRequiredModels(OpenAICodexSubscriptionOptions? options)
    {
        return options is not null &&
               !string.Equals(options.ResponseTransport, "http", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(options.ResponseTransport, "sse", StringComparison.OrdinalIgnoreCase);
    }

    private static AgentReasoningEffort? ParseReasoningEffort(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "minimal" => AgentReasoningEffort.Minimal,
            "low" => AgentReasoningEffort.Low,
            "medium" => AgentReasoningEffort.Medium,
            "high" => AgentReasoningEffort.High,
            "xhigh" => AgentReasoningEffort.XHigh,
            "max" => AgentReasoningEffort.Max,
            _ => null,
        };

    private static ApiKeyCredential CreateCredential(OpenAIProviderOptions provider)
        => new(provider.ApiKey ?? string.Empty);

    private static OpenAIClientOptions CreateClientOptions(OpenAIProviderOptions provider)
        => CreateClientOptionsCore(provider, protocolTrace: null);

    private static OpenAIClientOptions CreateClientOptionsCore(
        OpenAIProviderOptions provider,
        OpenAIProtocolTraceLogger? protocolTrace = null)
    {
        var options = new OpenAIClientOptions
        {
            Endpoint = provider.BaseUri,
            OrganizationId = provider.OrganizationId,
            ProjectId = provider.ProjectId,
            Transport = provider.HttpClient is null ? null : new HttpClientPipelineTransport(provider.HttpClient),
            // Keep the OpenAI SDK network timeout configurable while preserving its default
            // timeout unless a provider explicitly overrides it.
            NetworkTimeout = ResolveNetworkTimeout(provider),
        };

        if (protocolTrace is not null)
        {
            options.AddPolicy(protocolTrace.CreateHttpPolicy(), PipelinePosition.BeforeTransport);
        }

        if (provider.ExtraHeaders is { Count: > 0 } || provider.RequestHeaderContext is not null)
        {
            options.AddPolicy(new OpenAIExtraHeadersPolicy(provider.ExtraHeaders, provider.RequestHeaderContext), PipelinePosition.BeforeTransport);
        }

        return options;
    }

    private sealed class OpenAIExtraHeadersPolicy(
        IReadOnlyDictionary<string, string>? headers,
        OpenAIRequestHeaderContext? requestHeaderContext) : PipelinePolicy
    {
        public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            Apply(message);
            ProcessNext(message, pipeline, currentIndex);
        }

        public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            Apply(message);
            await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
        }

        private void Apply(PipelineMessage message)
        {
            ArgumentNullException.ThrowIfNull(message);
            ApplyHeaders(message, requestHeaderContext?.Current ?? headers);
        }

        private static void ApplyHeaders(
            PipelineMessage message,
            IReadOnlyDictionary<string, string>? headersToApply)
        {
            if (headersToApply is not { Count: > 0 })
            {
                return;
            }

            foreach (var header in headersToApply)
            {
                if (!string.IsNullOrWhiteSpace(header.Key) && header.Value is not null)
                {
                    message.Request.Headers.Set(header.Key, header.Value);
                }
            }
        }
    }

    private static ResponsesClientOptions CreateResponsesClientOptions(
        OpenAIProviderOptions provider,
        OpenAIProtocolTraceLogger? protocolTrace = null)
    {
        var options = new ResponsesClientOptions
        {
            Endpoint = provider.BaseUri,
            OrganizationId = provider.OrganizationId,
            ProjectId = provider.ProjectId,
            Transport = provider.HttpClient is null ? null : new HttpClientPipelineTransport(provider.HttpClient),
            // Keep the OpenAI SDK network timeout configurable while preserving its default
            // timeout unless a provider explicitly overrides it.
            NetworkTimeout = ResolveNetworkTimeout(provider),
        };

        if (protocolTrace is not null)
        {
            options.AddPolicy(protocolTrace.CreateHttpPolicy(), PipelinePosition.BeforeTransport);
        }

        if (provider.ExtraHeaders is { Count: > 0 } || provider.RequestHeaderContext is not null)
        {
            options.AddPolicy(new OpenAIExtraHeadersPolicy(provider.ExtraHeaders, provider.RequestHeaderContext), PipelinePosition.BeforeTransport);
        }

        return options;
    }

    private static AzureOpenAIClient CreateAzureOpenAIClient(
        OpenAIProviderOptions provider,
        OpenAIProtocolTraceLogger? protocolTrace)
    {
        if (provider.BaseUri is not { } endpoint)
        {
            throw new InvalidOperationException("Azure OpenAI providers require an Azure OpenAI resource endpoint in api_url.");
        }

        return new AzureOpenAIClient(endpoint, CreateCredential(provider), CreateAzureOpenAIClientOptions(provider, protocolTrace));
    }

    private static AzureOpenAIClientOptions CreateAzureOpenAIClientOptions(
        OpenAIProviderOptions provider,
        OpenAIProtocolTraceLogger? protocolTrace)
    {
        var options = new AzureOpenAIClientOptions
        {
            Transport = provider.HttpClient is null ? null : new HttpClientPipelineTransport(provider.HttpClient),
            NetworkTimeout = ResolveNetworkTimeout(provider),
        };

        if (protocolTrace is not null)
        {
            options.AddPolicy(protocolTrace.CreateHttpPolicy(), PipelinePosition.BeforeTransport);
        }

        if (provider.ExtraHeaders is { Count: > 0 } || provider.RequestHeaderContext is not null)
        {
            options.AddPolicy(new OpenAIExtraHeadersPolicy(provider.ExtraHeaders, provider.RequestHeaderContext), PipelinePosition.BeforeTransport);
        }

        return options;
    }

    private static InvalidOperationException CreateAzureOpenAIResponsesNotSupportedException()
        => new("Azure OpenAI is supported through the azure-openai chat-completions provider. The Azure.AI.OpenAI 2.1 SDK used by CodeAlta does not expose the Responses client.");

    private static TimeSpan? ResolveNetworkTimeout(OpenAIProviderOptions provider)
        => provider.NetworkTimeout;

    private static ResponsesClient CreateCodexSubscriptionResponsesClient(
        OpenAIProviderOptions provider,
        OpenAIResponsesClientFactoryContext context)
    {
        var options = provider.CodexSubscription
            ?? throw new InvalidOperationException("Codex subscription options are required.");
        var authManager = CreateCodexSubscriptionAuthManager(
            provider,
            options,
            ResolveStateRootPath(provider));
        var protocolTrace = OpenAIProtocolTraceLogger.Create(provider.ProtocolTracing, context);
        var clientOptions = CreateResponsesClientOptions(provider, protocolTrace);
        clientOptions.UserAgentApplicationId = CreateCodeAltaUserAgentApplicationId();
        clientOptions.AddPolicy(
            new CodexSubscriptionHeadersPolicy(
                new CodexSubscriptionHeaderContext(
                    AccountId: options.AccountId,
                    SessionId: context.SessionId,
                    IsFedRamp: false,
                    SendResponsesBetaHeader: options.SendResponsesBetaHeader,
                    TurnState: context.TurnState ?? new CodexTurnState(),
                    AuthManager: authManager,
                    RequestContext: context.RequestContext)),
            PipelinePosition.BeforeTransport);

        return new ResponsesClient(
            new ChatGptOAuthAuthenticationPolicy(authManager),
            clientOptions);
    }

    internal static OpenAICodexSubscriptionAuthManager CreateCodexSubscriptionAuthManager(
        OpenAIProviderOptions provider,
        OpenAICodexSubscriptionOptions options,
        string stateRootPath)
    {
        var credentialStore = new FileOpenAICodexSubscriptionCredentialStore(stateRootPath);
        return new OpenAICodexSubscriptionAuthManager(
            credentialStore,
            new OpenAICodexSubscriptionOAuthClient(CodexOAuthHttpClient),
            provider.ProviderKey,
            options.AuthSource,
            options.AccountId,
            CodexAuthFileReader.ResolveCodexHome());
    }

    internal static string ResolveStateRootPath(OpenAIProviderOptions provider)
        => string.IsNullOrWhiteSpace(provider.StateRootPath)
            ? Path.Combine(AppContext.BaseDirectory, ".codealta-state")
            : provider.StateRootPath;

    internal static string CreateCodeAltaUserAgentApplicationId()
    {
        var version = typeof(OpenAIProviderSdkFactory).Assembly.GetName().Version?.ToString();
        return string.IsNullOrWhiteSpace(version)
            ? "CodeAlta"
            : "CodeAlta/" + version;
    }

    private static bool TryListModelsFromCatalog(
        OpenAIProviderOptions provider,
        ModelProviderRuntimeDescriptor providerDescriptor,
        Exception exception,
        out IReadOnlyList<AgentModelInfo> models)
    {
        models = [];

        if (provider.ModelCatalog is null ||
            string.IsNullOrWhiteSpace(provider.ModelsDevProviderId) ||
            !ShouldUseCatalogFallback(provider, exception) ||
            provider.ModelCatalog.TryGetProvider(provider.ModelsDevProviderId, out var modelsDevProvider) is not true)
        {
            return false;
        }

        var catalogModels = modelsDevProvider.Models.Values
            .Where(static model => !string.IsNullOrWhiteSpace(model.Id))
            .GroupBy(static model => model.Id!, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var model = group.First();
                return new AgentModelInfo(
                    model.Id!.Trim(),
                    DisplayName: model.Name,
                    Provider: providerDescriptor.ProviderKey);
            })
            .OrderBy(static model => model.DisplayName ?? model.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (catalogModels.Length == 0)
        {
            return false;
        }

        LogWarn(
            exception,
            $"Remote model discovery failed; falling back to models.dev catalog provider={providerDescriptor.ProviderKey} displayName={providerDescriptor.DisplayName} modelsDevProviderId={provider.ModelsDevProviderId}");
        LogInfo(
            $"Using models.dev fallback catalog provider={providerDescriptor.ProviderKey} displayName={providerDescriptor.DisplayName} modelsDevProviderId={provider.ModelsDevProviderId} models={catalogModels.Length}");
        models = catalogModels;
        return true;
    }

    private static bool ShouldUseCatalogFallback(OpenAIProviderOptions provider, Exception exception)
    {
        if (string.Equals(provider.ModelsDevProviderId, "openai", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return TryGetStatusCode(exception, out var statusCode) && statusCode == 404;
    }

    private static bool TryGetStatusCode(Exception exception, out int statusCode)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is ClientResultException clientResultException)
        {
            statusCode = clientResultException.Status;
            return true;
        }

        statusCode = 0;
        return false;
    }

    private static AgentModelInfo CreateSingleModelInfo(
        string modelId,
        ModelProviderRuntimeDescriptor providerDescriptor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentNullException.ThrowIfNull(providerDescriptor);

        var trimmedModelId = modelId.Trim();
        return new AgentModelInfo(
            trimmedModelId,
            DisplayName: trimmedModelId,
            Provider: providerDescriptor.ProviderKey);
    }

    private static void LogInfo(string message)
    {
        Logger.Info(message);
    }

    private static void LogWarn(Exception exception, string message)
    {
        Logger.Warn(exception, message);
    }
}
