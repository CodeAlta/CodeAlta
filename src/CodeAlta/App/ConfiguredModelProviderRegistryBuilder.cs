using CodeAlta.Agent;
using CodeAlta.Agent.Anthropic;
using CodeAlta.Agent.Copilot;
using CodeAlta.Agent.Xai;
using CodeAlta.Agent.GoogleGenAI;
using CodeAlta.Agent.Mistral;
using CodeAlta.Agent.Runtime;
using CodeAlta.Agent.Runtime.Compaction;
using CodeAlta.Agent.ModelCatalog;
using CodeAlta.Agent.OpenAI;
using CodeAlta.Agent.OpenAI.Codex;
using CodeAlta.Catalog;
using Tomlyn.Model;
using XenoAtom.Logging;

namespace CodeAlta.App;

internal static class ConfiguredModelProviderRegistryBuilder
{
    private static readonly Logger Logger = LogManager.GetLogger("CodeAlta.RawApi");

    public static IReadOnlyList<ModelProviderDescriptor> RegisterConfiguredProviders(
        ModelProviderRegistry modelProviderRegistry,
        CodeAltaConfigStore configStore,
        string stateRootPath,
        ModelsDevCatalogService? modelCatalog = null)
    {
        ArgumentNullException.ThrowIfNull(modelProviderRegistry);
        ArgumentNullException.ThrowIfNull(configStore);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRootPath);

        var descriptors = new List<ModelProviderDescriptor>();
        var codexSubscriptionConcurrencyLimiter = new CodexSubscriptionConcurrencyLimiter();
        foreach (var definition in configStore.LoadGlobalProviderDefinitions())
        {
            if (TryCreateProviderRegistration(definition, stateRootPath, modelCatalog, codexSubscriptionConcurrencyLimiter, out var descriptor, out var createRuntime))
            {
                modelProviderRegistry.RegisterOrReplace(descriptor, createRuntime);
                descriptors.Add(descriptor);
            }
        }

        return descriptors;
    }

    public static IReadOnlyList<ModelProviderDescriptor> RegisterOrReplaceConfiguredProviders(
        ModelProviderRegistry modelProviderRegistry,
        IEnumerable<CodeAltaProviderDocument> definitions,
        string stateRootPath,
        ModelsDevCatalogService? modelCatalog = null)
    {
        ArgumentNullException.ThrowIfNull(modelProviderRegistry);
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRootPath);

        var descriptors = new List<ModelProviderDescriptor>();
        var codexSubscriptionConcurrencyLimiter = new CodexSubscriptionConcurrencyLimiter();
        foreach (var definition in definitions)
        {
            if (!TryCreateProviderRegistration(definition, stateRootPath, modelCatalog, codexSubscriptionConcurrencyLimiter, out var descriptor, out var createRuntime))
            {
                continue;
            }

            modelProviderRegistry.RegisterOrReplace(descriptor, createRuntime);
            descriptors.Add(descriptor);
        }

        return descriptors;
    }

    public static bool TryCreateProviderRegistration(
        CodeAltaProviderDocument definition,
        string stateRootPath,
        ModelsDevCatalogService? modelCatalog,
        out ModelProviderDescriptor descriptor,
        out Func<IModelProviderRuntime> createRuntime)
        => TryCreateProviderRegistration(
            definition,
            stateRootPath,
            modelCatalog,
            new CodexSubscriptionConcurrencyLimiter(),
            out descriptor,
            out createRuntime);

    private static bool TryCreateProviderRegistration(
        CodeAltaProviderDocument definition,
        string stateRootPath,
        ModelsDevCatalogService? modelCatalog,
        CodexSubscriptionConcurrencyLimiter codexSubscriptionConcurrencyLimiter,
        out ModelProviderDescriptor descriptor,
        out Func<IModelProviderRuntime> createRuntime)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(codexSubscriptionConcurrencyLimiter);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRootPath);

        switch (definition.ProviderType)
        {
            case "openai-chat":
                return TryCreateOpenAIChatProvider(definition, stateRootPath, modelCatalog, out descriptor, out createRuntime);
            case "openai-responses":
                return TryCreateOpenAIResponsesProvider(definition, stateRootPath, modelCatalog, out descriptor, out createRuntime);
            case "azure-openai":
                return TryCreateAzureOpenAIProvider(definition, stateRootPath, modelCatalog, out descriptor, out createRuntime);
            case "codex":
                return TryCreateCodexSubscriptionProvider(definition, stateRootPath, modelCatalog, codexSubscriptionConcurrencyLimiter, out descriptor, out createRuntime);
            case "copilot":
                return TryCreateCopilotDirectProvider(definition, stateRootPath, modelCatalog, out descriptor, out createRuntime);
            case "xai":
                return TryCreateXaiProvider(definition, stateRootPath, modelCatalog, out descriptor, out createRuntime);
            case "anthropic":
                return TryCreateAnthropicProvider(definition, stateRootPath, modelCatalog, out descriptor, out createRuntime);
            case "google-genai":
                return TryCreateGoogleGenAIProvider(definition, stateRootPath, modelCatalog, out descriptor, out createRuntime);
            case "vertex-ai":
                return TryCreateVertexAIProvider(definition, stateRootPath, modelCatalog, out descriptor, out createRuntime);
            case "mistral":
                return TryCreateMistralProvider(definition, stateRootPath, modelCatalog, out descriptor, out createRuntime);
            default:
                descriptor = null!;
                createRuntime = null!;
                return false;
        }
    }

    private static bool TryCreateOpenAIChatProvider(
        CodeAltaProviderDocument definition,
        string stateRootPath,
        ModelsDevCatalogService? modelCatalog,
        out ModelProviderDescriptor descriptor,
        out Func<IModelProviderRuntime> createRuntime)
    {
        var apiKey = ResolveSecret(definition.ApiKey, definition.ApiKeyEnv);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            LogInfo(
                $"Skipping provider provider={definition.ProviderKey} type={definition.ProviderType} displayName={FormatDisplayName(definition.DisplayName)} reason=missing-credentials");
            descriptor = null!;
            createRuntime = null!;
            return false;
        }

        var providerId = new ModelProviderId(definition.ProviderKey);
        var displayName = ResolveProviderDisplayName(definition);
        var baseUri = ParseUri(definition.ApiUrl);
        var options = new OpenAIChatModelProviderRuntimeOptions
        {
            ProviderIdOverride = providerId,
            DisplayNameOverride = displayName,
            StateRootPath = stateRootPath,
            Providers =
            {
                new OpenAIProviderOptions
                {
                    ProviderKey = definition.ProviderKey,
                    DisplayName = displayName,
                    ApiKey = apiKey,
                    BaseUri = baseUri,
                    NetworkTimeout = CreateNetworkTimeout(definition),
                    OrganizationId = definition.OrganizationId,
                    ProjectId = definition.ProjectId,
                    IsDefault = true,
                    Profile = CreateOpenAIChatProfile(definition.ProviderKey, baseUri, definition.Profile),
                    Compaction = CreateCompactionSettings(definition.Compaction),
                    ModelsDevProviderId = ResolveModelsDevProviderId(
                        definition.ModelsDevProviderId,
                        definition.ProviderKey,
                        "openai",
                        modelCatalog),
                    SingleModelId = NormalizeText(definition.SingleModelId),
                    ModelsIncludeRegex = NormalizeText(definition.ModelsIncludeRegex),
                    ExtraHeaders = CreateRequestHeaders(
                        AgentTransportKind.OpenAIChatCompletions,
                        definition.ProviderKey,
                        baseUri,
                        definition.Request),
                    ExtraBody = CreateOpenAIExtraBody(
                        AgentTransportKind.OpenAIChatCompletions,
                        definition.ProviderKey,
                        baseUri,
                        definition),
                    ModelOverrides = CreateModelOverrides(definition.ModelOverrides),
                    ModelRequestOverrides = CreateModelRequestOverrides(definition.ModelRequest),
                    ModelCatalog = modelCatalog,
                    ProtocolTracing = CreateOpenAIProtocolTraceOptions(definition, stateRootPath),
                },
            },
        };

        descriptor = CreateProviderDescriptor(definition, displayName, baseUri);
        createRuntime = () => new OpenAIChatModelProviderRuntime(options);
        LogInfo(
            $"Registered model provider key={providerId.Value} type={definition.ProviderType} displayName={displayName} apiUrl={FormatUri(baseUri)}");
        return true;
    }

    private static bool TryCreateOpenAIResponsesProvider(
        CodeAltaProviderDocument definition,
        string stateRootPath,
        ModelsDevCatalogService? modelCatalog,
        out ModelProviderDescriptor descriptor,
        out Func<IModelProviderRuntime> createRuntime)
    {
        var apiKey = ResolveSecret(definition.ApiKey, definition.ApiKeyEnv);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            LogInfo(
                $"Skipping provider provider={definition.ProviderKey} type={definition.ProviderType} displayName={FormatDisplayName(definition.DisplayName)} reason=missing-credentials");
            descriptor = null!;
            createRuntime = null!;
            return false;
        }

        var providerId = new ModelProviderId(definition.ProviderKey);
        var displayName = ResolveProviderDisplayName(definition);
        var baseUri = ParseUri(definition.ApiUrl);
        var options = new OpenAIResponsesModelProviderRuntimeOptions
        {
            ProviderIdOverride = providerId,
            DisplayNameOverride = displayName,
            StateRootPath = stateRootPath,
            Providers =
            {
                new OpenAIProviderOptions
                {
                    ProviderKey = definition.ProviderKey,
                    DisplayName = displayName,
                    ApiKey = apiKey,
                    BaseUri = baseUri,
                    NetworkTimeout = CreateNetworkTimeout(definition),
                    OrganizationId = definition.OrganizationId,
                    ProjectId = definition.ProjectId,
                    IsDefault = true,
                    Profile = CreateOpenAIResponsesProfile(definition.ProviderKey, baseUri, definition.Profile),
                    Compaction = CreateCompactionSettings(definition.Compaction),
                    ModelsDevProviderId = ResolveModelsDevProviderId(
                        definition.ModelsDevProviderId,
                        definition.ProviderKey,
                        "openai",
                        modelCatalog),
                    SingleModelId = NormalizeText(definition.SingleModelId),
                    ModelsIncludeRegex = NormalizeText(definition.ModelsIncludeRegex),
                    ExtraHeaders = CreateRequestHeaders(
                        AgentTransportKind.OpenAIResponses,
                        definition.ProviderKey,
                        baseUri,
                        definition.Request),
                    ExtraBody = CreateOpenAIExtraBody(
                        AgentTransportKind.OpenAIResponses,
                        definition.ProviderKey,
                        baseUri,
                        definition),
                    ModelOverrides = CreateModelOverrides(definition.ModelOverrides),
                    ModelRequestOverrides = CreateModelRequestOverrides(definition.ModelRequest),
                    ModelCatalog = modelCatalog,
                    ProtocolTracing = CreateOpenAIProtocolTraceOptions(definition, stateRootPath),
                },
            },
        };

        descriptor = CreateProviderDescriptor(definition, displayName, baseUri);
        createRuntime = () => new OpenAIResponsesModelProviderRuntime(options);
        LogInfo(
            $"Registered model provider key={providerId.Value} type={definition.ProviderType} displayName={displayName} apiUrl={FormatUri(baseUri)}");
        return true;
    }

    private static bool TryCreateAzureOpenAIProvider(
        CodeAltaProviderDocument definition,
        string stateRootPath,
        ModelsDevCatalogService? modelCatalog,
        out ModelProviderDescriptor descriptor,
        out Func<IModelProviderRuntime> createRuntime)
    {
        var apiKey = ResolveSecret(definition.ApiKey, definition.ApiKeyEnv);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            LogInfo(
                $"Skipping provider provider={definition.ProviderKey} type={definition.ProviderType} displayName={FormatDisplayName(definition.DisplayName)} reason=missing-credentials");
            descriptor = null!;
            createRuntime = null!;
            return false;
        }

        var providerId = new ModelProviderId(definition.ProviderKey);
        var displayName = ResolveProviderDisplayName(definition);
        var baseUri = ParseUri(definition.ApiUrl);
        var singleModelId = NormalizeText(definition.SingleModelId) ?? NormalizeText(definition.Model);
        var options = new OpenAIChatModelProviderRuntimeOptions
        {
            ProviderIdOverride = providerId,
            DisplayNameOverride = displayName,
            StateRootPath = stateRootPath,
            Providers =
            {
                new OpenAIProviderOptions
                {
                    ProviderKey = definition.ProviderKey,
                    DisplayName = displayName,
                    ApiKey = apiKey,
                    IsAzureOpenAI = true,
                    BaseUri = baseUri,
                    NetworkTimeout = CreateNetworkTimeout(definition),
                    IsDefault = true,
                    Profile = CreateAzureOpenAIProfile(definition.Profile),
                    Compaction = CreateCompactionSettings(definition.Compaction),
                    ModelsDevProviderId = ResolveModelsDevProviderId(
                        definition.ModelsDevProviderId,
                        definition.ProviderKey,
                        "openai",
                        modelCatalog),
                    SingleModelId = singleModelId,
                    ModelsIncludeRegex = NormalizeText(definition.ModelsIncludeRegex),
                    ModelOverrides = CreateModelOverrides(definition.ModelOverrides),
                    ModelCatalog = modelCatalog,
                    ProtocolTracing = CreateOpenAIProtocolTraceOptions(definition, stateRootPath),
                },
            },
        };

        descriptor = CreateProviderDescriptor(definition, displayName, baseUri);
        createRuntime = () => new OpenAIChatModelProviderRuntime(options);
        LogInfo(
            $"Registered model provider key={providerId.Value} type={definition.ProviderType} displayName={displayName} apiUrl={FormatUri(baseUri)} azureOpenAI=true");
        return true;
    }

    private static bool TryCreateCodexSubscriptionProvider(
        CodeAltaProviderDocument definition,
        string stateRootPath,
        ModelsDevCatalogService? modelCatalog,
        CodexSubscriptionConcurrencyLimiter codexSubscriptionConcurrencyLimiter,
        out ModelProviderDescriptor descriptor,
        out Func<IModelProviderRuntime> createRuntime)
    {
        var providerId = new ModelProviderId(definition.ProviderKey);
        var displayName = ResolveProviderDisplayName(definition);
        var baseUri = ParseUri(definition.ApiUrl) ?? new Uri("https://chatgpt.com/backend-api/codex");
        var providerOptions = new OpenAIProviderOptions
        {
            ProviderKey = definition.ProviderKey,
            DisplayName = displayName,
            BaseUri = baseUri,
            NetworkTimeout = CreateNetworkTimeout(definition),
            IsDefault = true,
            Profile = CreateCodexSubscriptionProfile(definition.Profile),
            Compaction = CreateCompactionSettings(definition.Compaction),
            ModelCatalog = modelCatalog,
            ModelsIncludeRegex = NormalizeText(definition.ModelsIncludeRegex),
            ProtocolTracing = CreateOpenAIProtocolTraceOptions(definition, stateRootPath),
            CodexSubscription = new OpenAICodexSubscriptionOptions
            {
                AuthSource = NormalizeText(definition.AuthSource) ?? "codealta_oauth",
                AccountId = NormalizeText(definition.AccountId),
                MaxConcurrentRequests = definition.MaxConcurrentRequests ?? 16,
                TextVerbosity = NormalizeText(definition.TextVerbosity) ?? "medium",
                IncludeEncryptedReasoning = definition.IncludeEncryptedReasoning ?? true,
                ModelDiscovery = NormalizeText(definition.ModelDiscovery) ?? "codex_endpoint_with_static_fallback",
                ResponseTransport = NormalizeText(definition.ResponseTransport) ?? "websocket_with_http_fallback",
                SendResponsesBetaHeader = definition.SendResponsesBetaHeader ?? false,
                SendInstallationId = definition.SendInstallationId ?? false,
                InstallationIdSource = NormalizeText(definition.InstallationIdSource) ?? "codealta_state",
                Experimental = definition.Experimental == true,
            },
        };

        var options = new OpenAIResponsesModelProviderRuntimeOptions
        {
            ProviderIdOverride = providerId,
            DisplayNameOverride = displayName,
            StateRootPath = stateRootPath,
            CodexSubscriptionConcurrencyLimiter = codexSubscriptionConcurrencyLimiter,
            Providers = { providerOptions },
        };

        descriptor = CreateProviderDescriptor(definition, displayName, baseUri);
        createRuntime = () => new OpenAIResponsesModelProviderRuntime(options);
        LogInfo(
            $"Registered model provider key={providerId.Value} type={definition.ProviderType} displayName={displayName} apiUrl={FormatUri(baseUri)} authSource={providerOptions.CodexSubscription.AuthSource}");
        return true;
    }

    private static bool TryCreateCopilotDirectProvider(
        CodeAltaProviderDocument definition,
        string stateRootPath,
        ModelsDevCatalogService? modelCatalog,
        out ModelProviderDescriptor descriptor,
        out Func<IModelProviderRuntime> createRuntime)
    {
        var authSource = NormalizeText(definition.AuthSource) ?? CopilotDirectAuthSources.GitHubDeviceFlow;
        var githubTokenEnv = NormalizeText(definition.GitHubTokenEnv);
        var copilotTokenEnv = NormalizeText(definition.CopilotTokenEnv);
        if (authSource == CopilotDirectAuthSources.GitHubTokenEnvironment &&
            (githubTokenEnv is null || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(githubTokenEnv))))
        {
            LogInfo(
                $"Skipping provider provider={definition.ProviderKey} type={definition.ProviderType} displayName={FormatDisplayName(definition.DisplayName)} reason=missing-github-token-env");
            descriptor = null!;
            createRuntime = null!;
            return false;
        }

        if (authSource == CopilotDirectAuthSources.CopilotTokenEnvironment &&
            (copilotTokenEnv is null || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(copilotTokenEnv))))
        {
            LogInfo(
                $"Skipping provider provider={definition.ProviderKey} type={definition.ProviderType} displayName={FormatDisplayName(definition.DisplayName)} reason=missing-copilot-token-env");
            descriptor = null!;
            createRuntime = null!;
            return false;
        }

        var providerId = new ModelProviderId(definition.ProviderKey);
        var displayName = ResolveProviderDisplayName(definition);
        var baseUri = ParseUri(definition.ApiUrl);
        var providerOptions = new CopilotDirectProviderOptions
        {
            ProviderKey = definition.ProviderKey,
            DisplayName = displayName,
            BaseUri = baseUri,
            IsDefault = true,
            Profile = CreateCopilotDirectProfile(definition.Profile),
            Compaction = CreateCompactionSettings(definition.Compaction),
            Auth = new CopilotDirectAuthOptions
            {
                AuthSource = authSource,
                EnterpriseDomain = NormalizeDomain(definition.GitHubEnterpriseUrl),
                GitHubTokenEnvironmentVariable = githubTokenEnv,
                CopilotTokenEnvironmentVariable = copilotTokenEnv,
            },
            ModelDiscovery = NormalizeText(definition.ModelDiscovery) ?? CopilotDirectModelDiscoveryModes.EndpointWithStaticFallback,
            EnableModelPolicies = definition.EnableModelPolicies == true,
            IncludePreviewModels = definition.IncludePreviewModels == true,
            ModelsDevProviderId = ResolveModelsDevProviderId(
                definition.ModelsDevProviderId,
                definition.ProviderKey,
                "github-copilot",
                modelCatalog),
            SingleModelId = NormalizeText(definition.SingleModelId),
            ModelsIncludeRegex = NormalizeText(definition.ModelsIncludeRegex),
            ModelOverrides = CreateModelOverrides(definition.ModelOverrides),
            ModelCatalog = modelCatalog,
            ProtocolTraceEnabled = definition.ProtocolTrace == true,
        };

        var options = new CopilotDirectModelProviderRuntimeOptions
        {
            ProviderIdOverride = providerId,
            DisplayNameOverride = displayName,
            StateRootPath = stateRootPath,
            Providers = { providerOptions },
        };

        descriptor = CreateProviderDescriptor(definition, displayName, baseUri);
        createRuntime = () => new CopilotDirectModelProviderRuntime(options);
        LogInfo(
            $"Registered model provider key={providerId.Value} type={definition.ProviderType} displayName={displayName} apiUrl={FormatUri(baseUri)} authSource={providerOptions.Auth.AuthSource} modelDiscovery={providerOptions.ModelDiscovery}");
        return true;
    }

    private static bool TryCreateXaiProvider(
        CodeAltaProviderDocument definition,
        string stateRootPath,
        ModelsDevCatalogService? modelCatalog,
        out ModelProviderDescriptor descriptor,
        out Func<IModelProviderRuntime> createRuntime)
    {
        var authSource = NormalizeText(definition.AuthSource) ?? XaiAuthSources.XaiBrowserOAuth;
        if (authSource != XaiAuthSources.XaiBrowserOAuth && authSource != XaiAuthSources.XaiDeviceFlow)
        {
            LogInfo(
                $"Skipping provider provider={definition.ProviderKey} type={definition.ProviderType} displayName={FormatDisplayName(definition.DisplayName)} reason=unsupported-auth-source authSource={authSource}");
            descriptor = null!;
            createRuntime = null!;
            return false;
        }

        var providerId = new ModelProviderId(definition.ProviderKey);
        var displayName = ResolveProviderDisplayName(definition);
        var baseUri = ParseUri(definition.ApiUrl);
        var providerOptions = new XaiProviderOptions
        {
            ProviderKey = definition.ProviderKey,
            DisplayName = displayName,
            BaseUri = baseUri,
            IsDefault = true,
            Profile = CreateXaiProfile(definition.Profile),
            Compaction = CreateCompactionSettings(definition.Compaction),
            Auth = new XaiAuthOptions
            {
                AuthSource = authSource,
            },
            ModelDiscovery = NormalizeText(definition.ModelDiscovery) ?? XaiModelDiscoveryModes.EndpointWithStaticFallback,
            SingleModelId = NormalizeText(definition.SingleModelId),
            ModelsIncludeRegex = NormalizeText(definition.ModelsIncludeRegex),
            ModelsDevProviderId = NormalizeText(definition.ModelsDevProviderId),
            ExtraHeaders = CreateRequestHeaders(
                AgentTransportKind.OpenAIResponses,
                definition.ProviderKey,
                baseUri,
                definition.Request),
            ExtraBody = CreateOpenAIExtraBody(
                AgentTransportKind.OpenAIResponses,
                definition.ProviderKey,
                baseUri,
                definition),
            ModelOverrides = CreateModelOverrides(definition.ModelOverrides),
            ModelRequestOverrides = CreateModelRequestOverrides(definition.ModelRequest),
            ModelCatalog = modelCatalog,
            ProtocolTraceEnabled = definition.ProtocolTrace == true,
        };

        var options = new XaiModelProviderRuntimeOptions
        {
            ProviderIdOverride = providerId,
            DisplayNameOverride = displayName,
            StateRootPath = stateRootPath,
            Providers = { providerOptions },
        };

        descriptor = CreateProviderDescriptor(definition, displayName, baseUri);
        createRuntime = () => new XaiDirectModelProviderRuntime(options);
        LogInfo(
            $"Registered model provider key={providerId.Value} type={definition.ProviderType} displayName={displayName} apiUrl={FormatUri(baseUri)} authSource={providerOptions.Auth.AuthSource} modelDiscovery={providerOptions.ModelDiscovery}");
        return true;
    }

    private static AgentProviderProfile CreateXaiProfile(CodeAltaProviderProfileDocument? document)
    {
        var profile = new AgentProviderProfile
        {
            SupportsDeveloperRole = true,
            SupportsStore = false,
            SupportsReasoningEffort = true,
            StreamsUsage = true,
            MaxTokensFieldName = "max_output_tokens",
            ReasoningFieldNames = ["reasoning"],
        };

        return document is null ? profile : ApplyProfileOverrides(profile, document);
    }

    private static bool TryCreateAnthropicProvider(
        CodeAltaProviderDocument definition,
        string stateRootPath,
        ModelsDevCatalogService? modelCatalog,
        out ModelProviderDescriptor descriptor,
        out Func<IModelProviderRuntime> createRuntime)
    {
        var apiKey = ResolveSecret(definition.ApiKey, definition.ApiKeyEnv);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            LogInfo(
                $"Skipping provider provider={definition.ProviderKey} type={definition.ProviderType} displayName={FormatDisplayName(definition.DisplayName)} reason=missing-credentials");
            descriptor = null!;
            createRuntime = null!;
            return false;
        }

        var providerId = new ModelProviderId(definition.ProviderKey);
        var displayName = ResolveProviderDisplayName(definition);
        var baseUri = ParseUri(definition.ApiUrl);
        var options = new AnthropicModelProviderRuntimeOptions
        {
            ProviderIdOverride = providerId,
            DisplayNameOverride = displayName,
            StateRootPath = stateRootPath,
            Providers =
            {
                new AnthropicProviderOptions
                {
                    ProviderKey = definition.ProviderKey,
                    DisplayName = displayName,
                    ApiKey = apiKey,
                    BaseUri = baseUri,
                    IsDefault = true,
                    Profile = CreateAnthropicProfile(definition.Profile),
                    Compaction = CreateCompactionSettings(definition.Compaction),
                    ModelsDevProviderId = ResolveModelsDevProviderId(
                        definition.ModelsDevProviderId,
                        definition.ProviderKey,
                        "anthropic",
                        modelCatalog),
                    SingleModelId = NormalizeText(definition.SingleModelId),
                    ModelsIncludeRegex = NormalizeText(definition.ModelsIncludeRegex),
                    ExtraHeaders = CreateRequestHeaders(
                        AgentTransportKind.AnthropicMessages,
                        definition.ProviderKey,
                        baseUri,
                        definition.Request),
                    ModelOverrides = CreateModelOverrides(definition.ModelOverrides),
                    ModelCatalog = modelCatalog,
                },
            },
        };

        descriptor = CreateProviderDescriptor(definition, displayName, baseUri);
        createRuntime = () => new AnthropicModelProviderRuntime(options);
        LogInfo(
            $"Registered model provider key={providerId.Value} type={definition.ProviderType} displayName={displayName} apiUrl={FormatUri(baseUri)}");
        return true;
    }

    private static bool TryCreateGoogleGenAIProvider(
        CodeAltaProviderDocument definition,
        string stateRootPath,
        ModelsDevCatalogService? modelCatalog,
        out ModelProviderDescriptor descriptor,
        out Func<IModelProviderRuntime> createRuntime)
    {
        var apiKey = ResolveSecret(definition.ApiKey, definition.ApiKeyEnv);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            LogInfo(
                $"Skipping provider provider={definition.ProviderKey} type={definition.ProviderType} displayName={FormatDisplayName(definition.DisplayName)} reason=missing-credentials");
            descriptor = null!;
            createRuntime = null!;
            return false;
        }

        return TryCreateGoogleProvider(
            definition,
            stateRootPath,
            modelCatalog,
            useVertexAI: false,
            apiKey,
            out descriptor,
            out createRuntime);
    }

    private static bool TryCreateVertexAIProvider(
        CodeAltaProviderDocument definition,
        string stateRootPath,
        ModelsDevCatalogService? modelCatalog,
        out ModelProviderDescriptor descriptor,
        out Func<IModelProviderRuntime> createRuntime)
    {
        return TryCreateGoogleProvider(
            definition,
            stateRootPath,
            modelCatalog,
            useVertexAI: true,
            apiKey: null,
            out descriptor,
            out createRuntime);
    }

    private static bool TryCreateGoogleProvider(
        CodeAltaProviderDocument definition,
        string stateRootPath,
        ModelsDevCatalogService? modelCatalog,
        bool useVertexAI,
        string? apiKey,
        out ModelProviderDescriptor descriptor,
        out Func<IModelProviderRuntime> createRuntime)
    {
        var providerId = new ModelProviderId(definition.ProviderKey);
        var displayName = ResolveProviderDisplayName(definition);
        var baseUri = ParseUri(definition.ApiUrl);
        var options = new GoogleGenAIModelProviderRuntimeOptions
        {
            ProviderIdOverride = providerId,
            DisplayNameOverride = displayName,
            StateRootPath = stateRootPath,
            Providers =
            {
                new GoogleGenAIProviderOptions
                {
                    ProviderKey = definition.ProviderKey,
                    DisplayName = displayName,
                    ApiKey = apiKey,
                    UseVertexAI = useVertexAI,
                    Project = definition.Project,
                    Location = definition.Location,
                    BaseUri = baseUri,
                    IsDefault = true,
                    Profile = CreateGoogleGenAIProfile(definition.Profile),
                    Compaction = CreateCompactionSettings(definition.Compaction),
                    ModelsDevProviderId = ResolveModelsDevProviderId(
                        definition.ModelsDevProviderId,
                        definition.ProviderKey,
                        "google",
                        modelCatalog),
                    SingleModelId = NormalizeText(definition.SingleModelId),
                    ModelsIncludeRegex = NormalizeText(definition.ModelsIncludeRegex),
                    ExtraHeaders = CreateRequestHeaders(
                        useVertexAI ? AgentTransportKind.GoogleVertexAI : AgentTransportKind.GoogleGeminiApi,
                        definition.ProviderKey,
                        baseUri,
                        definition.Request),
                    ModelOverrides = CreateModelOverrides(definition.ModelOverrides),
                    ModelCatalog = modelCatalog,
                },
            },
        };

        descriptor = CreateProviderDescriptor(definition, displayName, baseUri);
        createRuntime = () => new GoogleGenAIModelProviderRuntime(options);
        LogInfo(
            $"Registered model provider key={providerId.Value} type={definition.ProviderType} displayName={displayName} apiUrl={FormatUri(baseUri)} vertex={useVertexAI}");
        return true;
    }

    private static bool TryCreateMistralProvider(
        CodeAltaProviderDocument definition,
        string stateRootPath,
        ModelsDevCatalogService? modelCatalog,
        out ModelProviderDescriptor descriptor,
        out Func<IModelProviderRuntime> createRuntime)
    {
        var apiKey = ResolveSecret(definition.ApiKey, definition.ApiKeyEnv);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            LogInfo(
                $"Skipping provider provider={definition.ProviderKey} type={definition.ProviderType} displayName={FormatDisplayName(definition.DisplayName)} reason=missing-credentials");
            descriptor = null!;
            createRuntime = null!;
            return false;
        }

        var providerId = new ModelProviderId(definition.ProviderKey);
        var displayName = ResolveProviderDisplayName(definition);
        var baseUri = ParseUri(definition.ApiUrl);
        var options = new MistralModelProviderRuntimeOptions
        {
            ProviderIdOverride = providerId,
            DisplayNameOverride = displayName,
            StateRootPath = stateRootPath,
            Providers =
            {
                new MistralProviderOptions
                {
                    ProviderKey = definition.ProviderKey,
                    DisplayName = displayName,
                    ApiKey = apiKey,
                    BaseUri = baseUri,
                    IsDefault = true,
                    Profile = CreateMistralProfile(definition.Profile),
                    Compaction = CreateCompactionSettings(definition.Compaction),
                    ModelsDevProviderId = ResolveModelsDevProviderId(
                        definition.ModelsDevProviderId,
                        definition.ProviderKey,
                        "mistral",
                        modelCatalog),
                    SingleModelId = NormalizeText(definition.SingleModelId),
                    ModelsIncludeRegex = NormalizeText(definition.ModelsIncludeRegex),
                    ExtraHeaders = CreateRequestHeaders(
                        AgentTransportKind.MistralChat,
                        definition.ProviderKey,
                        baseUri,
                        definition.Request),
                    ModelOverrides = CreateModelOverrides(definition.ModelOverrides),
                    ModelCatalog = modelCatalog,
                },
            },
        };

        descriptor = CreateProviderDescriptor(definition, displayName, baseUri);
        createRuntime = () => new MistralModelProviderRuntime(options);
        LogInfo(
            $"Registered model provider key={providerId.Value} type={definition.ProviderType} displayName={displayName} apiUrl={FormatUri(baseUri)}");
        return true;
    }

    private static string ResolveProviderDisplayName(CodeAltaProviderDocument definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return NormalizeText(definition.DisplayName) ?? definition.ProviderKey;
    }

    private static ModelProviderDescriptor CreateProviderDescriptor(
        CodeAltaProviderDocument definition,
        string displayName,
        Uri? baseUri)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return new ModelProviderDescriptor(new ModelProviderId(definition.ProviderKey), displayName, definition.ProviderType)
        {
            BaseUri = baseUri,
            IsDefault = true,
            IsEnabled = definition.Enabled != false,
            DefaultModelId = NormalizeText(definition.SingleModelId) ?? NormalizeText(definition.Model),
            DefaultReasoningEffort = ParseReasoningEffort(definition.ReasoningEffort),
        };
    }

    private static AgentReasoningEffort? ParseReasoningEffort(string? value)
    {
        return NormalizeText(value)?.ToLowerInvariant() switch
        {
            "none" => AgentReasoningEffort.None,
            "minimal" => AgentReasoningEffort.Minimal,
            "low" => AgentReasoningEffort.Low,
            "medium" => AgentReasoningEffort.Medium,
            "high" => AgentReasoningEffort.High,
            "xhigh" => AgentReasoningEffort.XHigh,
            "max" => AgentReasoningEffort.Max,
            _ => null,
        };
    }

    private static string? ResolveSecret(string? literal, string? environmentVariableName)
    {
        var normalizedLiteral = NormalizeText(literal);
        if (!string.IsNullOrWhiteSpace(normalizedLiteral))
        {
            return normalizedLiteral;
        }

        var normalizedEnvironmentVariableName = NormalizeText(environmentVariableName);
        if (string.IsNullOrWhiteSpace(normalizedEnvironmentVariableName))
        {
            return null;
        }

        return NormalizeText(Environment.GetEnvironmentVariable(normalizedEnvironmentVariableName));
    }

    private static AgentProviderProfile CreateOpenAIResponsesProfile(
        string providerKey,
        Uri? apiUrl,
        CodeAltaProviderProfileDocument? document)
    {
        var profile = CreateOpenAIBaseProfile(AgentTransportKind.OpenAIResponses, providerKey, apiUrl, responses: true);
        if (document is not null)
        {
            var overridden = ApplyProfileOverrides(profile, document);
            profile = new AgentProviderProfile
            {
                SupportsDeveloperRole = overridden.SupportsDeveloperRole,
                SupportsStore = false,
                SupportsReasoningEffort = overridden.SupportsReasoningEffort,
                StreamsUsage = overridden.StreamsUsage,
                SupportsThoughtSignatures = overridden.SupportsThoughtSignatures,
                MaxTokensFieldName = overridden.MaxTokensFieldName,
                ReasoningFieldNames = overridden.ReasoningFieldNames,
                ReasoningInputFieldName = overridden.ReasoningInputFieldName,
            };
        }

        return profile;
    }

    private static AgentProviderProfile CreateOpenAIChatProfile(
        string providerKey,
        Uri? apiUrl,
        CodeAltaProviderProfileDocument? document)
    {
        var profile = CreateOpenAIBaseProfile(AgentTransportKind.OpenAIChatCompletions, providerKey, apiUrl, responses: false);
        return document is null ? profile : ApplyProfileOverrides(profile, document);
    }

    private static AgentProviderProfile CreateAzureOpenAIProfile(CodeAltaProviderProfileDocument? document)
    {
        var profile = new AgentProviderProfile
        {
            SupportsDeveloperRole = true,
            SupportsStore = false,
            SupportsReasoningEffort = true,
            StreamsUsage = true,
            MaxTokensFieldName = "max_completion_tokens",
            ReasoningFieldNames = ["reasoning_content", "reasoning"],
        };

        return document is null ? profile : ApplyProfileOverrides(profile, document);
    }

    private static AgentProviderProfile CreateOpenAIBaseProfile(
        AgentTransportKind transportKind,
        string providerKey,
        Uri? apiUrl,
        bool responses)
    {
        var profile = responses
            ? new AgentProviderProfile
            {
                SupportsDeveloperRole = true,
                SupportsStore = true,
                SupportsReasoningEffort = true,
                SupportsStrictTools = true,
                StreamsUsage = true,
                MaxTokensFieldName = "max_output_tokens",
                ReasoningFieldNames = ["reasoning"],
            }
            : new AgentProviderProfile
            {
                SupportsDeveloperRole = true,
                SupportsStore = true,
                SupportsReasoningEffort = true,
                SupportsStrictTools = true,
                StreamsUsage = true,
                MaxTokensFieldName = "max_completion_tokens",
                ReasoningFieldNames = ["reasoning_content", "reasoning"],
            };

        return RawApiProviderDefaultsCatalog.ApplyProfileDefaults(transportKind, providerKey, apiUrl, profile);
    }

    private static AgentProviderProfile CreateCodexSubscriptionProfile(CodeAltaProviderProfileDocument? document)
    {
        var profile = new AgentProviderProfile
        {
            SupportsDeveloperRole = true,
            SupportsStore = false,
            SupportsReasoningEffort = true,
            StreamsUsage = true,
            MaxTokensFieldName = "max_output_tokens",
            ReasoningFieldNames = ["reasoning"],
        };

        return document is null ? profile : ApplyProfileOverrides(profile, document);
    }

    private static AgentProviderProfile CreateCopilotDirectProfile(CodeAltaProviderProfileDocument? document)
    {
        var profile = new AgentProviderProfile
        {
            SupportsDeveloperRole = true,
            SupportsStore = false,
            SupportsReasoningEffort = true,
            StreamsUsage = true,
            MaxTokensFieldName = "max_completion_tokens",
            ReasoningFieldNames = ["reasoning_text", "reasoning_content", "reasoning"],
            ReasoningInputFieldName = "reasoning_opaque",
        };

        return document is null ? profile : ApplyProfileOverrides(profile, document);
    }

    private static AgentProviderProfile? CreateAnthropicProfile(CodeAltaProviderProfileDocument? document)
    {
        if (document is null)
        {
            return null;
        }

        return ApplyProfileOverrides(
            new AgentProviderProfile
            {
                SupportsDeveloperRole = false,
                StreamsUsage = true,
                SupportsThoughtSignatures = true,
            },
            document);
    }

    private static AgentProviderProfile? CreateGoogleGenAIProfile(CodeAltaProviderProfileDocument? document)
    {
        if (document is null)
        {
            return null;
        }

        return ApplyProfileOverrides(
            new AgentProviderProfile
            {
                SupportsDeveloperRole = false,
                SupportsReasoningEffort = true,
                StreamsUsage = true,
                SupportsThoughtSignatures = true,
            },
            document);
    }

    private static AgentProviderProfile? CreateMistralProfile(CodeAltaProviderProfileDocument? document)
    {
        if (document is null)
        {
            return null;
        }

        return ApplyProfileOverrides(
            new AgentProviderProfile
            {
                SupportsDeveloperRole = true,
                SupportsStore = false,
                StreamsUsage = true,
                MaxTokensFieldName = "max_tokens",
            },
            document);
    }

    private static AgentProviderProfile ApplyProfileOverrides(
        AgentProviderProfile profile,
        CodeAltaProviderProfileDocument document)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(document);

        return new AgentProviderProfile
        {
            SupportsDeveloperRole = document.SupportsDeveloperRole ?? profile.SupportsDeveloperRole,
            SupportsStore = document.SupportsStore ?? profile.SupportsStore,
            SupportsReasoningEffort = document.SupportsReasoningEffort ?? profile.SupportsReasoningEffort,
            SupportsParallelToolCalls = document.SupportsParallelToolCalls ?? profile.SupportsParallelToolCalls,
            StreamsUsage = document.StreamsUsage ?? profile.StreamsUsage,
            SupportsThoughtSignatures = document.SupportsThoughtSignatures ?? profile.SupportsThoughtSignatures,
            RequiresToolResultName = document.RequiresToolResultName ?? profile.RequiresToolResultName,
            RequiresAssistantAfterToolResult = document.RequiresAssistantAfterToolResult ?? profile.RequiresAssistantAfterToolResult,
            SupportsCacheControl = document.SupportsCacheControl ?? profile.SupportsCacheControl,
            SupportsStrictTools = document.SupportsStrictTools ?? profile.SupportsStrictTools,
            ThinkingFormat = document.ThinkingFormat ?? profile.ThinkingFormat,
            MaxTokensFieldName = document.MaxTokensFieldName ?? profile.MaxTokensFieldName,
            ReasoningFieldNames = document.ReasoningFieldNames is null
                ? profile.ReasoningFieldNames
                : [.. document.ReasoningFieldNames],
            ReasoningInputFieldName = document.ReasoningInputFieldName ?? profile.ReasoningInputFieldName,
        };
    }

    private static Uri? ParseUri(string? uriText)
        => Uri.TryCreate(NormalizeText(uriText), UriKind.Absolute, out var uri)
            ? uri
            : null;

    private static TimeSpan? CreateNetworkTimeout(CodeAltaProviderDocument definition)
        => definition.NetworkTimeoutSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null;

    private static OpenAIProtocolTraceOptions? CreateOpenAIProtocolTraceOptions(
        CodeAltaProviderDocument definition,
        string stateRootPath)
        => definition.ProtocolTrace == true
            ? new OpenAIProtocolTraceOptions
            {
                Enabled = true,
                StateRootPath = stateRootPath,
            }
            : null;

    private static string? NormalizeText(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizeDomain(string? value)
    {
        var normalized = NormalizeText(value);
        if (normalized is null)
        {
            return null;
        }

        if (!normalized.Contains("://", StringComparison.Ordinal))
        {
            normalized = "https://" + normalized;
        }

        return Uri.TryCreate(normalized, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host)
            ? uri.Host
            : null;
    }

    private static string ResolveModelsDevProviderId(
        string? configuredProviderId,
        string providerKey,
        string defaultProviderId,
        ModelsDevCatalogService? modelCatalog)
    {
        var configured = NormalizeModelsDevProviderId(configuredProviderId);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var normalizedProviderKey = NormalizeModelsDevProviderId(providerKey);
        if (!string.IsNullOrWhiteSpace(normalizedProviderKey) &&
            modelCatalog?.TryGetProvider(normalizedProviderKey, out _) == true)
        {
            return normalizedProviderKey;
        }

        return defaultProviderId;
    }

    private static string? NormalizeModelsDevProviderId(string? value)
    {
        var normalized = NormalizeText(value)?.ToLowerInvariant();
        return normalized switch
        {
            "copilot" => "github-copilot",
            "gemini" => "google",
            _ => normalized,
        };
    }

    private static IReadOnlyDictionary<string, AgentModelOverride>? CreateModelOverrides(
        Dictionary<string, CodeAltaProviderModelOverrideDocument>? overrides)
    {
        if (overrides is null || overrides.Count == 0)
        {
            return null;
        }

        return overrides.ToDictionary(
            static entry => entry.Key,
            static entry => new AgentModelOverride
            {
                DisplayName = entry.Value.DisplayName,
                Description = entry.Value.Description,
                ContextWindowTokens = entry.Value.ContextWindow,
                InputTokenLimit = entry.Value.InputTokenLimit,
                OutputTokenLimit = entry.Value.OutputTokenLimit,
                MaxTokens = entry.Value.MaxTokens,
                SupportsReasoning = entry.Value.SupportsReasoning,
                SupportsToolCall = entry.Value.SupportsToolCall,
                SupportsAttachments = entry.Value.SupportsAttachments,
                SupportsStructuredOutput = entry.Value.SupportsStructuredOutput,
            },
            StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<string, AgentModelRequestOverride>? CreateModelRequestOverrides(
        Dictionary<string, CodeAltaProviderModelRequestDocument>? overrides)
    {
        if (overrides is null || overrides.Count == 0)
        {
            return null;
        }

        return overrides.ToDictionary(
            static entry => entry.Key,
            static entry => new AgentModelRequestOverride
            {
                Headers = CreateHeaderDictionary(entry.Value.Headers),
                RemoveHeaders = entry.Value.RemoveHeaders,
                ExtraBody = CreateExtraBody(entry.Value.ExtraBody),
                RemoveExtraBody = entry.Value.RemoveExtraBody,
            },
            StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<string, string>? CreateHeaderDictionary(Dictionary<string, string>? headers)
    {
        if (headers is null || headers.Count == 0)
        {
            return null;
        }

        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in headers)
        {
            if (!string.IsNullOrWhiteSpace(header.Key) && !IsRequiredAuthHeader(header.Key))
            {
                normalized[header.Key.Trim()] = header.Value ?? string.Empty;
            }
        }

        return normalized.Count == 0 ? null : normalized;
    }

    private static IReadOnlyDictionary<string, string>? CreateRequestHeaders(
        AgentTransportKind transportKind,
        string providerKey,
        Uri? baseUri,
        CodeAltaProviderRequestDocument? request)
    {
        var headers = ToMutableHeaders(RawApiProviderDefaultsCatalog.CreateHeaderDefaults(
            transportKind,
            providerKey,
            baseUri));

        if (request?.RemoveHeaders is { Count: > 0 })
        {
            foreach (var headerName in request.RemoveHeaders)
            {
                if (!string.IsNullOrWhiteSpace(headerName) && !IsRequiredAuthHeader(headerName))
                {
                    headers?.Remove(headerName.Trim());
                }
            }
        }

        if (request?.Headers is { Count: > 0 })
        {
            headers ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in request.Headers)
            {
                if (!string.IsNullOrWhiteSpace(header.Key) && !IsRequiredAuthHeader(header.Key))
                {
                    headers[header.Key.Trim()] = header.Value ?? string.Empty;
                }
            }
        }

        return headers is null || headers.Count == 0 ? null : headers;
    }

    private static IReadOnlyDictionary<string, object?>? CreateOpenAIExtraBody(
        AgentTransportKind transportKind,
        string providerKey,
        Uri? baseUri,
        CodeAltaProviderDocument definition)
    {
        var body = ToMutableBody(RawApiProviderDefaultsCatalog.CreateOpenAIExtraBodyDefaults(
            transportKind,
            providerKey,
            baseUri));

        if (definition.Request?.RemoveExtraBody is { Count: > 0 })
        {
            foreach (var fieldName in definition.Request.RemoveExtraBody)
            {
                if (!string.IsNullOrWhiteSpace(fieldName))
                {
                    body?.Remove(fieldName.Trim());
                }
            }
        }

        body = MergeBody(body, CreateExtraBody(definition.Request?.ExtraBody));
        body = MergeBody(body, CreateExtraBody(definition.ExtraBody));
        return body is null || body.Count == 0 ? null : body;
    }

    private static Dictionary<string, string>? ToMutableHeaders(IReadOnlyDictionary<string, string>? headers)
        => headers is null || headers.Count == 0
            ? null
            : new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, object?>? ToMutableBody(IReadOnlyDictionary<string, object?>? body)
        => body is null || body.Count == 0
            ? null
            : new Dictionary<string, object?>(body, StringComparer.Ordinal);

    private static Dictionary<string, object?>? MergeBody(
        Dictionary<string, object?>? target,
        IReadOnlyDictionary<string, object?>? source)
    {
        if (source is not { Count: > 0 })
        {
            return target;
        }

        target ??= [];
        foreach (var entry in source)
        {
            target[entry.Key] = entry.Value;
        }

        return target;
    }

    private static bool IsRequiredAuthHeader(string headerName)
        => headerName.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
           headerName.Equals("api-key", StringComparison.OrdinalIgnoreCase) ||
           headerName.Equals("x-api-key", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, object?>? CreateExtraBody(TomlTable? extraBody)
    {
        if (extraBody is null || extraBody.Count == 0)
        {
            return null;
        }

        var normalized = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var entry in extraBody)
        {
            if (string.IsNullOrWhiteSpace(entry.Key))
            {
                continue;
            }

            normalized[entry.Key.Trim()] = NormalizeTomlValue(entry.Value);
        }

        return normalized.Count == 0 ? null : normalized;
    }

    private static object? NormalizeTomlValue(object? value)
    {
        return value switch
        {
            null => null,
            TomlTable table => CreateExtraBody(table),
            TomlArray array => array.Select(NormalizeTomlValue).ToArray(),
            _ => value,
        };
    }

    private static AgentCompactionSettings CreateCompactionSettings(CodeAltaProviderCompactionDocument? compaction)
    {
        var normalized = compaction ?? new CodeAltaProviderCompactionDocument();
        return new AgentCompactionSettings(
            Enabled: normalized.Enabled ?? AgentCompactionSettings.Default.Enabled,
            Ratio: normalized.Ratio ?? AgentCompactionSettings.Default.Ratio,
            KeepLastUserMessage: normalized.KeepLastUserMessage ?? AgentCompactionSettings.Default.KeepLastUserMessage,
            AllowSplitTurn: normalized.AllowSplitTurn ?? AgentCompactionSettings.Default.AllowSplitTurn)
        {
            SummaryOutputRatio = normalized.SummaryOutputRatio ?? AgentCompactionSettings.Default.SummaryOutputRatio,
            PostCompactionTargetRatio = normalized.PostCompactionTargetRatio ?? AgentCompactionSettings.Default.PostCompactionTargetRatio,
            SummaryShareOfTarget = normalized.SummaryShareOfTarget ?? AgentCompactionSettings.Default.SummaryShareOfTarget,
            FileContextShareOfSummaryTarget = normalized.FileContextShareOfSummaryTarget ?? AgentCompactionSettings.Default.FileContextShareOfSummaryTarget,
        };
    }

    private static string FormatDisplayName(string? displayName)
        => NormalizeText(displayName) ?? "<none>";

    private static string FormatUri(Uri? uri)
        => uri?.ToString() ?? "<default>";

    private static void LogInfo(string message)
    {
        Logger.Info(message);
    }

}
