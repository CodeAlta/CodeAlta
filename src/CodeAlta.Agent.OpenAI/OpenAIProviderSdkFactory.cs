#pragma warning disable OPENAI001

using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using CodeAlta.Agent.LocalRuntime;
using CodeAlta.Agent.ModelCatalog;
using CodeAlta.Agent.OpenAI.CodexSubscription;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Models;
using OpenAI.Responses;
using XenoAtom.Logging;

namespace CodeAlta.Agent.OpenAI;

internal static class OpenAIProviderSdkFactory
{
    private static readonly TimeSpan CodexSubscriptionNetworkTimeout = TimeSpan.FromMinutes(10);
    private static readonly Logger Logger = LogManager.GetLogger("CodeAlta.Agent.OpenAI");
    private static readonly HttpClient CodexOAuthHttpClient = new();

    public static OpenAIClient CreateClient(OpenAIProviderOptions provider)
        => new(CreateCredential(provider), CreateClientOptions(provider));

    public static ResponsesClient CreateResponsesClient(OpenAIProviderOptions provider, string? model)
        => provider.ResponsesClientFactory is not null
            ? provider.ResponsesClientFactory(model)
            : new ResponsesClient(CreateCredential(provider), CreateResponsesClientOptions(provider));

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

        return new ResponsesClient(CreateCredential(provider), CreateResponsesClientOptions(provider));
    }

    public static ChatClient CreateChatClient(OpenAIProviderOptions provider, string? model)
        => provider.ChatClientFactory is not null
            ? provider.ChatClientFactory(model)
            : new ChatClient(model ?? string.Empty, CreateCredential(provider), CreateClientOptions(provider));

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
        LocalAgentProviderDescriptor providerDescriptor,
        CancellationToken cancellationToken)
    {
        var models = await ListModelsCoreAsync(provider, providerDescriptor, cancellationToken).ConfigureAwait(false);
        return AgentModelMetadataEnricher.EnrichModels(
            models,
            provider.ModelCatalog,
            provider.ModelsDevProviderId,
            provider.ModelOverrides);
    }

    private static async Task<IReadOnlyList<AgentModelInfo>> ListModelsCoreAsync(
        OpenAIProviderOptions provider,
        LocalAgentProviderDescriptor providerDescriptor,
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
                    $"Using configured single-model catalog backend={providerDescriptor.BackendId.Value} provider={providerDescriptor.ProviderKey} displayName={providerDescriptor.DisplayName} model={provider.SingleModelId.Trim()}");
                return
                [
                    CreateSingleModelInfo(provider.SingleModelId, providerDescriptor),
                ];
            }

            if (provider.ModelListAsync is not null)
            {
                return await provider.ModelListAsync(cancellationToken).ConfigureAwait(false);
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
        LocalAgentProviderDescriptor providerDescriptor,
        CancellationToken cancellationToken)
    {
        var options = provider.CodexSubscription
            ?? throw new InvalidOperationException("Codex subscription options are required.");
        if (string.Equals(options.ModelDiscovery, "static", StringComparison.OrdinalIgnoreCase))
        {
            return ListCodexSubscriptionStaticModels(provider, providerDescriptor);
        }

        try
        {
            var stateRootPath = ResolveStateRootPath(provider);
            var authManager = CreateCodexSubscriptionAuthManager(provider, options, stateRootPath);
            var discoveryClient = new CodexSubscriptionModelDiscoveryClient(
                provider.CodexSubscriptionHttpClient ?? new HttpClient(),
                authManager,
                options,
                CreateCodeAltaUserAgentApplicationId());
            var discoveredModels = await discoveryClient.GetModelsAsync(
                provider.BaseUri ?? new Uri("https://chatgpt.com/backend-api/codex"),
                cancellationToken).ConfigureAwait(false);
            var models = MapCodexSubscriptionDiscoveredModels(
                discoveredModels,
                providerDescriptor,
                options,
                provider.SingleModelId);
            LogInfo(
                $"Using Codex subscription authenticated model catalog backend={providerDescriptor.BackendId.Value} provider={providerDescriptor.ProviderKey} displayName={providerDescriptor.DisplayName} models={models.Count}");
            return models;
        }
        catch (Exception ex) when (ShouldUseCodexStaticModelFallback(options, ex))
        {
            LogWarn(
                ex,
                $"Codex model discovery failed; falling back to static catalog backend={providerDescriptor.BackendId.Value} provider={providerDescriptor.ProviderKey} displayName={providerDescriptor.DisplayName}");
            return ListCodexSubscriptionStaticModels(provider, providerDescriptor);
        }
    }

    private static IReadOnlyList<AgentModelInfo> ListCodexSubscriptionStaticModels(
        OpenAIProviderOptions provider,
        LocalAgentProviderDescriptor providerDescriptor)
    {
        var models = CodexSubscriptionStaticModelCatalog.List(providerDescriptor, provider.SingleModelId);
        LogInfo(
            $"Using Codex subscription static model catalog backend={providerDescriptor.BackendId.Value} provider={providerDescriptor.ProviderKey} displayName={providerDescriptor.DisplayName} models={models.Count}");
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
        LocalAgentProviderDescriptor providerDescriptor,
        OpenAICodexSubscriptionOptions options,
        string? configuredModelId)
    {
        var includeWebSocketRequiredModels = AllowsWebSocketRequiredModels(options);
        var supportedModels = discoveredModels
            .Where(model => model.SupportedInApi && (includeWebSocketRequiredModels || !model.RequiresWebSocket))
            .GroupBy(static model => model.Id, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToArray();

        if (!string.IsNullOrWhiteSpace(configuredModelId))
        {
            var trimmedModelId = configuredModelId.Trim();
            var configuredModel = supportedModels.FirstOrDefault(model =>
                string.Equals(model.Id, trimmedModelId, StringComparison.OrdinalIgnoreCase));
            if (configuredModel is null)
            {
                throw new InvalidOperationException(
                    $"Codex subscription model '{trimmedModelId}' is not available from the authenticated API-supported model catalog.");
            }

            return [CreateModelInfo(configuredModel, providerDescriptor)];
        }

        return supportedModels
            .Where(static model => model.Listable && !model.Hidden)
            .Select(model => CreateModelInfo(model, providerDescriptor))
            .OrderBy(static model => model.DisplayName ?? model.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static AgentModelInfo CreateModelInfo(
        CodexSubscriptionDiscoveredModel model,
        LocalAgentProviderDescriptor providerDescriptor)
    {
        var capabilities = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["source"] = "codex-endpoint",
            ["supportedInApi"] = model.SupportedInApi,
            ["hidden"] = model.Hidden,
            ["listable"] = model.Listable,
            ["supportsReasoningSummary"] = model.SupportsReasoningSummary,
            ["supportsEncryptedReasoning"] = model.SupportsEncryptedReasoning,
            ["supportsTextVerbosity"] = model.SupportsTextVerbosity,
            ["supportsTools"] = model.SupportsTools,
            ["supportsImageInput"] = model.SupportsImageInput,
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
            SupportedReasoningEfforts: model.SupportsReasoningEffort
                ?
                [
                    AgentReasoningEffort.Low,
                    AgentReasoningEffort.Medium,
                    AgentReasoningEffort.High,
                    AgentReasoningEffort.XHigh,
                ]
                : [],
            Capabilities: capabilities);
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
            _ => null,
        };

    private static ApiKeyCredential CreateCredential(OpenAIProviderOptions provider)
        => new(provider.ApiKey ?? string.Empty);

    private static OpenAIClientOptions CreateClientOptions(OpenAIProviderOptions provider)
        => new()
        {
            Endpoint = provider.BaseUri,
            OrganizationId = provider.OrganizationId,
            ProjectId = provider.ProjectId,
            // Codex subscription responses can legitimately sit in model-side reasoning for longer than
            // the SDK pipeline's default per-read timeout. Keep a finite watchdog, but avoid cutting off
            // quiet long-running SSE streams too aggressively.
            NetworkTimeout = provider.CodexSubscription is null ? null : CodexSubscriptionNetworkTimeout,
        };

    private static OpenAIClientOptions CreateResponsesClientOptions(OpenAIProviderOptions provider)
        => CreateClientOptions(provider);

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
        var clientOptions = CreateResponsesClientOptions(provider);
        clientOptions.UserAgentApplicationId = CreateCodeAltaUserAgentApplicationId();
        clientOptions.AddPolicy(
            new CodexSubscriptionHeadersPolicy(
                new CodexSubscriptionHeaderContext(
                    AccountId: options.AccountId,
                    SessionId: context.SessionId,
                    IsFedRamp: false,
                    SendResponsesBetaHeader: options.SendResponsesBetaHeader,
                    TurnState: new CodexTurnState(),
                    AuthManager: authManager)),
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
        LocalAgentProviderDescriptor providerDescriptor,
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
            $"Remote model discovery failed; falling back to models.dev catalog backend={providerDescriptor.BackendId.Value} provider={providerDescriptor.ProviderKey} displayName={providerDescriptor.DisplayName} modelsDevProviderId={provider.ModelsDevProviderId}");
        LogInfo(
            $"Using models.dev fallback catalog backend={providerDescriptor.BackendId.Value} provider={providerDescriptor.ProviderKey} displayName={providerDescriptor.DisplayName} modelsDevProviderId={provider.ModelsDevProviderId} models={catalogModels.Length}");
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
        LocalAgentProviderDescriptor providerDescriptor)
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
        if (LogManager.IsInitialized && Logger.IsEnabled(LogLevel.Info))
        {
            Logger.Info(message);
        }
    }

    private static void LogWarn(Exception exception, string message)
    {
        if (LogManager.IsInitialized && Logger.IsEnabled(LogLevel.Warn))
        {
            Logger.Warn(exception, message);
        }
    }
}
