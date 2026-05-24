using CodeAlta.Agent;
using CodeAlta.Agent.Anthropic;
using CodeAlta.Agent.Copilot;
using CodeAlta.Agent.Xai;
using CodeAlta.Agent.GoogleGenAI;
using CodeAlta.Agent.LocalRuntime;
using CodeAlta.Agent.LocalRuntime.Compaction;
using CodeAlta.Agent.ModelCatalog;
using CodeAlta.Agent.OpenAI;
using CodeAlta.Agent.OpenAI.Codex;
using CodeAlta.Catalog;
using Tomlyn.Model;
using XenoAtom.Logging;

namespace CodeAlta.App;

internal static class RawApiBackendRegistrar
{
    private static readonly Logger Logger = LogManager.GetLogger("CodeAlta.RawApi");

    public static IReadOnlyList<AgentBackendDescriptor> RegisterConfiguredBackends(
        AgentBackendFactory backendFactory,
        CodeAltaConfigStore configStore,
        string stateRootPath,
        ModelsDevCatalogService? modelCatalog = null)
    {
        ArgumentNullException.ThrowIfNull(backendFactory);
        ArgumentNullException.ThrowIfNull(configStore);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRootPath);

        var descriptors = new List<AgentBackendDescriptor>();
        var codexSubscriptionConcurrencyLimiter = new CodexSubscriptionConcurrencyLimiter();
        foreach (var definition in configStore.LoadGlobalProviderDefinitions())
        {
            if (TryCreateBackendRegistration(definition, stateRootPath, modelCatalog, codexSubscriptionConcurrencyLimiter, out var descriptor, out var createBackend))
            {
                backendFactory.RegisterOrReplace(
                    descriptor.BackendId,
                    createBackend,
                    AgentBackendRegistrationOptions.SharedSessionMetadataStore);
                descriptors.Add(descriptor);
            }
        }

        return descriptors;
    }

    public static IReadOnlyList<AgentBackendDescriptor> RegisterOrReplaceConfiguredBackends(
        AgentBackendFactory backendFactory,
        IEnumerable<CodeAltaProviderDocument> definitions,
        string stateRootPath,
        ModelsDevCatalogService? modelCatalog = null)
    {
        ArgumentNullException.ThrowIfNull(backendFactory);
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRootPath);

        var descriptors = new List<AgentBackendDescriptor>();
        var codexSubscriptionConcurrencyLimiter = new CodexSubscriptionConcurrencyLimiter();
        foreach (var definition in definitions)
        {
            if (!TryCreateBackendRegistration(definition, stateRootPath, modelCatalog, codexSubscriptionConcurrencyLimiter, out var descriptor, out var createBackend))
            {
                continue;
            }

            backendFactory.RegisterOrReplace(
                descriptor.BackendId,
                createBackend,
                AgentBackendRegistrationOptions.SharedSessionMetadataStore);
            descriptors.Add(descriptor);
        }

        return descriptors;
    }

    public static bool TryCreateBackendRegistration(
        CodeAltaProviderDocument definition,
        string stateRootPath,
        ModelsDevCatalogService? modelCatalog,
        out AgentBackendDescriptor descriptor,
        out Func<IAgentBackend> createBackend)
        => TryCreateBackendRegistration(
            definition,
            stateRootPath,
            modelCatalog,
            new CodexSubscriptionConcurrencyLimiter(),
            out descriptor,
            out createBackend);

    private static bool TryCreateBackendRegistration(
        CodeAltaProviderDocument definition,
        string stateRootPath,
        ModelsDevCatalogService? modelCatalog,
        CodexSubscriptionConcurrencyLimiter codexSubscriptionConcurrencyLimiter,
        out AgentBackendDescriptor descriptor,
        out Func<IAgentBackend> createBackend)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(codexSubscriptionConcurrencyLimiter);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRootPath);

        switch (definition.ProviderType)
        {
            case "openai-chat":
                return TryCreateOpenAIChatProvider(definition, stateRootPath, modelCatalog, out descriptor, out createBackend);
            case "openai-responses":
                return TryCreateOpenAIResponsesProvider(definition, stateRootPath, modelCatalog, out descriptor, out createBackend);
            case "azure-openai":
                return TryCreateAzureOpenAIProvider(definition, stateRootPath, modelCatalog, out descriptor, out createBackend);
            case "codex":
                return TryCreateCodexSubscriptionProvider(definition, stateRootPath, modelCatalog, codexSubscriptionConcurrencyLimiter, out descriptor, out createBackend);
            case "copilot":
                return TryCreateCopilotDirectProvider(definition, stateRootPath, out descriptor, out createBackend);
            case "xai":
                return TryCreateXaiProvider(definition, stateRootPath, out descriptor, out createBackend);
            case "anthropic":
                return TryCreateAnthropicProvider(definition, stateRootPath, modelCatalog, out descriptor, out createBackend);
            case "google-genai":
                return TryCreateGoogleGenAIProvider(definition, stateRootPath, modelCatalog, out descriptor, out createBackend);
            case "vertex-ai":
                return TryCreateVertexAIProvider(definition, stateRootPath, modelCatalog, out descriptor, out createBackend);
            default:
                descriptor = null!;
                createBackend = null!;
                return false;
        }
    }

    private static bool TryCreateOpenAIChatProvider(
        CodeAltaProviderDocument definition,
        string stateRootPath,
        ModelsDevCatalogService? modelCatalog,
        out AgentBackendDescriptor descriptor,
        out Func<IAgentBackend> createBackend)
    {
        var apiKey = ResolveSecret(definition.ApiKey, definition.ApiKeyEnv);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            LogInfo(
                $"Skipping provider provider={definition.ProviderKey} type={definition.ProviderType} displayName={FormatDisplayName(definition.DisplayName)} reason=missing-credentials");
            descriptor = null!;
            createBackend = null!;
            return false;
        }

        var backendId = new AgentBackendId(definition.ProviderKey);
        var displayName = ResolveProviderDisplayName(definition);
        var baseUri = ParseUri(definition.ApiUrl);
        var options = new OpenAIChatAgentBackendOptions
        {
            BackendIdOverride = backendId,
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
                    ExtraBody = RawApiProviderDefaultsCatalog.ApplyOpenAIExtraBodyDefaults(
                        LocalAgentTransportKind.OpenAIChatCompletions,
                        definition.ProviderKey,
                        baseUri,
                        CreateExtraBody(definition.ExtraBody)),
                    ModelOverrides = CreateModelOverrides(definition.ModelOverrides),
                    ModelCatalog = modelCatalog,
                    ProtocolTracing = CreateOpenAIProtocolTraceOptions(definition, stateRootPath),
                },
            },
        };

        descriptor = new AgentBackendDescriptor(backendId, displayName);
        createBackend = () => new OpenAIChatAgentBackend(options);
        LogInfo(
            $"Registered provider backend={backendId.Value} type={definition.ProviderType} displayName={displayName} apiUrl={FormatUri(baseUri)}");
        return true;
    }

    private static bool TryCreateOpenAIResponsesProvider(
        CodeAltaProviderDocument definition,
        string stateRootPath,
        ModelsDevCatalogService? modelCatalog,
        out AgentBackendDescriptor descriptor,
        out Func<IAgentBackend> createBackend)
    {
        var apiKey = ResolveSecret(definition.ApiKey, definition.ApiKeyEnv);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            LogInfo(
                $"Skipping provider provider={definition.ProviderKey} type={definition.ProviderType} displayName={FormatDisplayName(definition.DisplayName)} reason=missing-credentials");
            descriptor = null!;
            createBackend = null!;
            return false;
        }

        var backendId = new AgentBackendId(definition.ProviderKey);
        var displayName = ResolveProviderDisplayName(definition);
        var baseUri = ParseUri(definition.ApiUrl);
        var options = new OpenAIResponsesAgentBackendOptions
        {
            BackendIdOverride = backendId,
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
                    ExtraBody = RawApiProviderDefaultsCatalog.ApplyOpenAIExtraBodyDefaults(
                        LocalAgentTransportKind.OpenAIResponses,
                        definition.ProviderKey,
                        baseUri,
                        CreateExtraBody(definition.ExtraBody)),
                    ModelOverrides = CreateModelOverrides(definition.ModelOverrides),
                    ModelCatalog = modelCatalog,
                    ProtocolTracing = CreateOpenAIProtocolTraceOptions(definition, stateRootPath),
                },
            },
        };

        descriptor = new AgentBackendDescriptor(backendId, displayName);
        createBackend = () => new OpenAIResponsesAgentBackend(options);
        LogInfo(
            $"Registered provider backend={backendId.Value} type={definition.ProviderType} displayName={displayName} apiUrl={FormatUri(baseUri)}");
        return true;
    }

    private static bool TryCreateAzureOpenAIProvider(
        CodeAltaProviderDocument definition,
        string stateRootPath,
        ModelsDevCatalogService? modelCatalog,
        out AgentBackendDescriptor descriptor,
        out Func<IAgentBackend> createBackend)
    {
        var apiKey = ResolveSecret(definition.ApiKey, definition.ApiKeyEnv);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            LogInfo(
                $"Skipping provider provider={definition.ProviderKey} type={definition.ProviderType} displayName={FormatDisplayName(definition.DisplayName)} reason=missing-credentials");
            descriptor = null!;
            createBackend = null!;
            return false;
        }

        var backendId = new AgentBackendId(definition.ProviderKey);
        var displayName = ResolveProviderDisplayName(definition);
        var baseUri = ParseUri(definition.ApiUrl);
        var singleModelId = NormalizeText(definition.SingleModelId) ?? NormalizeText(definition.Model);
        var options = new OpenAIChatAgentBackendOptions
        {
            BackendIdOverride = backendId,
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
                    IsDefault = true,
                    Profile = CreateAzureOpenAIProfile(definition.Profile),
                    Compaction = CreateCompactionSettings(definition.Compaction),
                    ModelsDevProviderId = ResolveModelsDevProviderId(
                        definition.ModelsDevProviderId,
                        definition.ProviderKey,
                        "openai",
                        modelCatalog),
                    SingleModelId = singleModelId,
                    ModelOverrides = CreateModelOverrides(definition.ModelOverrides),
                    ModelCatalog = modelCatalog,
                    ProtocolTracing = CreateOpenAIProtocolTraceOptions(definition, stateRootPath),
                },
            },
        };

        descriptor = new AgentBackendDescriptor(backendId, displayName);
        createBackend = () => new OpenAIChatAgentBackend(options);
        LogInfo(
            $"Registered provider backend={backendId.Value} type={definition.ProviderType} displayName={displayName} apiUrl={FormatUri(baseUri)} azureOpenAI=true");
        return true;
    }

    private static bool TryCreateCodexSubscriptionProvider(
        CodeAltaProviderDocument definition,
        string stateRootPath,
        ModelsDevCatalogService? modelCatalog,
        CodexSubscriptionConcurrencyLimiter codexSubscriptionConcurrencyLimiter,
        out AgentBackendDescriptor descriptor,
        out Func<IAgentBackend> createBackend)
    {
        var backendId = new AgentBackendId(definition.ProviderKey);
        var displayName = ResolveProviderDisplayName(definition);
        var baseUri = ParseUri(definition.ApiUrl) ?? new Uri("https://chatgpt.com/backend-api/codex");
        var providerOptions = new OpenAIProviderOptions
        {
            ProviderKey = definition.ProviderKey,
            DisplayName = displayName,
            BaseUri = baseUri,
            IsDefault = true,
            Profile = CreateCodexSubscriptionProfile(definition.Profile),
            Compaction = CreateCompactionSettings(definition.Compaction),
            ModelCatalog = modelCatalog,
            ProtocolTracing = CreateOpenAIProtocolTraceOptions(definition, stateRootPath),
            CodexSubscription = new OpenAICodexSubscriptionOptions
            {
                AuthSource = NormalizeText(definition.AuthSource) ?? "codealta_oauth",
                AccountId = NormalizeText(definition.AccountId),
                MaxConcurrentRequests = definition.MaxConcurrentRequests ?? 16,
                TextVerbosity = NormalizeText(definition.TextVerbosity) ?? "medium",
                IncludeEncryptedReasoning = definition.IncludeEncryptedReasoning ?? true,
                ModelDiscovery = NormalizeText(definition.ModelDiscovery) ?? "static",
                ResponseTransport = NormalizeText(definition.ResponseTransport) ?? "websocket_with_http_fallback",
                SendResponsesBetaHeader = definition.SendResponsesBetaHeader ?? true,
                SendInstallationId = definition.SendInstallationId ?? false,
                InstallationIdSource = NormalizeText(definition.InstallationIdSource) ?? "codealta_state",
                Experimental = definition.Experimental == true,
            },
        };

        var options = new OpenAIResponsesAgentBackendOptions
        {
            BackendIdOverride = backendId,
            DisplayNameOverride = displayName,
            StateRootPath = stateRootPath,
            CodexSubscriptionConcurrencyLimiter = codexSubscriptionConcurrencyLimiter,
            Providers = { providerOptions },
        };

        descriptor = new AgentBackendDescriptor(backendId, displayName);
        createBackend = () => new OpenAIResponsesAgentBackend(options);
        LogInfo(
            $"Registered provider backend={backendId.Value} type={definition.ProviderType} displayName={displayName} apiUrl={FormatUri(baseUri)} authSource={providerOptions.CodexSubscription.AuthSource}");
        return true;
    }

    private static bool TryCreateCopilotDirectProvider(
        CodeAltaProviderDocument definition,
        string stateRootPath,
        out AgentBackendDescriptor descriptor,
        out Func<IAgentBackend> createBackend)
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
            createBackend = null!;
            return false;
        }

        if (authSource == CopilotDirectAuthSources.CopilotTokenEnvironment &&
            (copilotTokenEnv is null || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(copilotTokenEnv))))
        {
            LogInfo(
                $"Skipping provider provider={definition.ProviderKey} type={definition.ProviderType} displayName={FormatDisplayName(definition.DisplayName)} reason=missing-copilot-token-env");
            descriptor = null!;
            createBackend = null!;
            return false;
        }

        var backendId = new AgentBackendId(definition.ProviderKey);
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
            SingleModelId = NormalizeText(definition.SingleModelId),
            ModelOverrides = CreateModelOverrides(definition.ModelOverrides),
            ProtocolTraceEnabled = definition.ProtocolTrace == true,
        };

        var options = new CopilotDirectAgentBackendOptions
        {
            BackendIdOverride = backendId,
            DisplayNameOverride = displayName,
            StateRootPath = stateRootPath,
            Providers = { providerOptions },
        };

        descriptor = new AgentBackendDescriptor(backendId, displayName);
        createBackend = () => new CopilotDirectAgentBackend(options);
        LogInfo(
            $"Registered provider backend={backendId.Value} type={definition.ProviderType} displayName={displayName} apiUrl={FormatUri(baseUri)} authSource={providerOptions.Auth.AuthSource} modelDiscovery={providerOptions.ModelDiscovery}");
        return true;
    }

    private static bool TryCreateXaiProvider(
        CodeAltaProviderDocument definition,
        string stateRootPath,
        out AgentBackendDescriptor descriptor,
        out Func<IAgentBackend> createBackend)
    {
        var authSource = NormalizeText(definition.AuthSource) ?? XaiAuthSources.XaiBrowserOAuth;
        if (authSource != XaiAuthSources.XaiBrowserOAuth && authSource != XaiAuthSources.XaiDeviceFlow)
        {
            LogInfo(
                $"Skipping provider provider={definition.ProviderKey} type={definition.ProviderType} displayName={FormatDisplayName(definition.DisplayName)} reason=unsupported-auth-source authSource={authSource}");
            descriptor = null!;
            createBackend = null!;
            return false;
        }

        var backendId = new AgentBackendId(definition.ProviderKey);
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
            ModelOverrides = CreateModelOverrides(definition.ModelOverrides),
            ProtocolTraceEnabled = definition.ProtocolTrace == true,
        };

        var options = new XaiAgentBackendOptions
        {
            BackendIdOverride = backendId,
            DisplayNameOverride = displayName,
            StateRootPath = stateRootPath,
            Providers = { providerOptions },
        };

        descriptor = new AgentBackendDescriptor(backendId, displayName);
        createBackend = () => new XaiDirectAgentBackend(options);
        LogInfo(
            $"Registered provider backend={backendId.Value} type={definition.ProviderType} displayName={displayName} apiUrl={FormatUri(baseUri)} authSource={providerOptions.Auth.AuthSource} modelDiscovery={providerOptions.ModelDiscovery}");
        return true;
    }

    private static LocalAgentProviderProfile CreateXaiProfile(CodeAltaProviderProfileDocument? document)
    {
        var profile = new LocalAgentProviderProfile
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
        out AgentBackendDescriptor descriptor,
        out Func<IAgentBackend> createBackend)
    {
        var apiKey = ResolveSecret(definition.ApiKey, definition.ApiKeyEnv);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            LogInfo(
                $"Skipping provider provider={definition.ProviderKey} type={definition.ProviderType} displayName={FormatDisplayName(definition.DisplayName)} reason=missing-credentials");
            descriptor = null!;
            createBackend = null!;
            return false;
        }

        var backendId = new AgentBackendId(definition.ProviderKey);
        var displayName = ResolveProviderDisplayName(definition);
        var baseUri = ParseUri(definition.ApiUrl);
        var options = new AnthropicAgentBackendOptions
        {
            BackendIdOverride = backendId,
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
                    ModelOverrides = CreateModelOverrides(definition.ModelOverrides),
                    ModelCatalog = modelCatalog,
                },
            },
        };

        descriptor = new AgentBackendDescriptor(backendId, displayName);
        createBackend = () => new AnthropicAgentBackend(options);
        LogInfo(
            $"Registered provider backend={backendId.Value} type={definition.ProviderType} displayName={displayName} apiUrl={FormatUri(baseUri)}");
        return true;
    }

    private static bool TryCreateGoogleGenAIProvider(
        CodeAltaProviderDocument definition,
        string stateRootPath,
        ModelsDevCatalogService? modelCatalog,
        out AgentBackendDescriptor descriptor,
        out Func<IAgentBackend> createBackend)
    {
        var apiKey = ResolveSecret(definition.ApiKey, definition.ApiKeyEnv);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            LogInfo(
                $"Skipping provider provider={definition.ProviderKey} type={definition.ProviderType} displayName={FormatDisplayName(definition.DisplayName)} reason=missing-credentials");
            descriptor = null!;
            createBackend = null!;
            return false;
        }

        return TryCreateGoogleProvider(
            definition,
            stateRootPath,
            modelCatalog,
            useVertexAI: false,
            apiKey,
            out descriptor,
            out createBackend);
    }

    private static bool TryCreateVertexAIProvider(
        CodeAltaProviderDocument definition,
        string stateRootPath,
        ModelsDevCatalogService? modelCatalog,
        out AgentBackendDescriptor descriptor,
        out Func<IAgentBackend> createBackend)
    {
        return TryCreateGoogleProvider(
            definition,
            stateRootPath,
            modelCatalog,
            useVertexAI: true,
            apiKey: null,
            out descriptor,
            out createBackend);
    }

    private static bool TryCreateGoogleProvider(
        CodeAltaProviderDocument definition,
        string stateRootPath,
        ModelsDevCatalogService? modelCatalog,
        bool useVertexAI,
        string? apiKey,
        out AgentBackendDescriptor descriptor,
        out Func<IAgentBackend> createBackend)
    {
        var backendId = new AgentBackendId(definition.ProviderKey);
        var displayName = ResolveProviderDisplayName(definition);
        var baseUri = ParseUri(definition.ApiUrl);
        var options = new GoogleGenAIAgentBackendOptions
        {
            BackendIdOverride = backendId,
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
                    ModelOverrides = CreateModelOverrides(definition.ModelOverrides),
                    ModelCatalog = modelCatalog,
                },
            },
        };

        descriptor = new AgentBackendDescriptor(backendId, displayName);
        createBackend = () => new GoogleGenAIAgentBackend(options);
        LogInfo(
            $"Registered provider backend={backendId.Value} type={definition.ProviderType} displayName={displayName} apiUrl={FormatUri(baseUri)} vertex={useVertexAI}");
        return true;
    }

    private static string ResolveProviderDisplayName(CodeAltaProviderDocument definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return NormalizeText(definition.DisplayName) ?? definition.ProviderKey;
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

    private static LocalAgentProviderProfile CreateOpenAIResponsesProfile(
        string providerKey,
        Uri? apiUrl,
        CodeAltaProviderProfileDocument? document)
    {
        var profile = CreateOpenAIBaseProfile(LocalAgentTransportKind.OpenAIResponses, providerKey, apiUrl, responses: true);
        if (document is not null)
        {
            var overridden = ApplyProfileOverrides(profile, document);
            profile = new LocalAgentProviderProfile
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

    private static LocalAgentProviderProfile CreateOpenAIChatProfile(
        string providerKey,
        Uri? apiUrl,
        CodeAltaProviderProfileDocument? document)
    {
        var profile = CreateOpenAIBaseProfile(LocalAgentTransportKind.OpenAIChatCompletions, providerKey, apiUrl, responses: false);
        return document is null ? profile : ApplyProfileOverrides(profile, document);
    }

    private static LocalAgentProviderProfile CreateAzureOpenAIProfile(CodeAltaProviderProfileDocument? document)
    {
        var profile = new LocalAgentProviderProfile
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

    private static LocalAgentProviderProfile CreateOpenAIBaseProfile(
        LocalAgentTransportKind transportKind,
        string providerKey,
        Uri? apiUrl,
        bool responses)
    {
        var profile = responses
            ? new LocalAgentProviderProfile
            {
                SupportsDeveloperRole = true,
                SupportsStore = true,
                SupportsReasoningEffort = true,
                StreamsUsage = true,
                MaxTokensFieldName = "max_output_tokens",
                ReasoningFieldNames = ["reasoning"],
            }
            : new LocalAgentProviderProfile
            {
                SupportsDeveloperRole = true,
                SupportsStore = true,
                SupportsReasoningEffort = true,
                StreamsUsage = true,
                MaxTokensFieldName = "max_completion_tokens",
                ReasoningFieldNames = ["reasoning_content", "reasoning"],
            };

        return RawApiProviderDefaultsCatalog.ApplyProfileDefaults(transportKind, providerKey, apiUrl, profile);
    }

    private static LocalAgentProviderProfile CreateCodexSubscriptionProfile(CodeAltaProviderProfileDocument? document)
    {
        var profile = new LocalAgentProviderProfile
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

    private static LocalAgentProviderProfile CreateCopilotDirectProfile(CodeAltaProviderProfileDocument? document)
    {
        var profile = new LocalAgentProviderProfile
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

    private static LocalAgentProviderProfile? CreateAnthropicProfile(CodeAltaProviderProfileDocument? document)
    {
        if (document is null)
        {
            return null;
        }

        return ApplyProfileOverrides(
            new LocalAgentProviderProfile
            {
                SupportsDeveloperRole = false,
                StreamsUsage = true,
                SupportsThoughtSignatures = true,
            },
            document);
    }

    private static LocalAgentProviderProfile? CreateGoogleGenAIProfile(CodeAltaProviderProfileDocument? document)
    {
        if (document is null)
        {
            return null;
        }

        return ApplyProfileOverrides(
            new LocalAgentProviderProfile
            {
                SupportsDeveloperRole = false,
                SupportsReasoningEffort = true,
                StreamsUsage = true,
                SupportsThoughtSignatures = true,
            },
            document);
    }

    private static LocalAgentProviderProfile ApplyProfileOverrides(
        LocalAgentProviderProfile profile,
        CodeAltaProviderProfileDocument document)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(document);

        return new LocalAgentProviderProfile
        {
            SupportsDeveloperRole = document.SupportsDeveloperRole ?? profile.SupportsDeveloperRole,
            SupportsStore = document.SupportsStore ?? profile.SupportsStore,
            SupportsReasoningEffort = document.SupportsReasoningEffort ?? profile.SupportsReasoningEffort,
            StreamsUsage = document.StreamsUsage ?? profile.StreamsUsage,
            SupportsThoughtSignatures = document.SupportsThoughtSignatures ?? profile.SupportsThoughtSignatures,
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
        var configured = NormalizeText(configuredProviderId)?.ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var normalizedProviderKey = NormalizeText(providerKey)?.ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(normalizedProviderKey) &&
            modelCatalog?.TryGetProvider(normalizedProviderKey, out _) == true)
        {
            return normalizedProviderKey;
        }

        return defaultProviderId;
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

    private static LocalAgentCompactionSettings CreateCompactionSettings(CodeAltaProviderCompactionDocument? compaction)
    {
        var normalized = compaction ?? new CodeAltaProviderCompactionDocument();
        return new LocalAgentCompactionSettings(
            Enabled: normalized.Enabled ?? LocalAgentCompactionSettings.Default.Enabled,
            Ratio: normalized.Ratio ?? LocalAgentCompactionSettings.Default.Ratio,
            KeepLastUserMessage: normalized.KeepLastUserMessage ?? LocalAgentCompactionSettings.Default.KeepLastUserMessage,
            AllowSplitTurn: normalized.AllowSplitTurn ?? LocalAgentCompactionSettings.Default.AllowSplitTurn)
        {
            SummaryOutputRatio = normalized.SummaryOutputRatio ?? LocalAgentCompactionSettings.Default.SummaryOutputRatio,
            PostCompactionTargetRatio = normalized.PostCompactionTargetRatio ?? LocalAgentCompactionSettings.Default.PostCompactionTargetRatio,
            SummaryShareOfTarget = normalized.SummaryShareOfTarget ?? LocalAgentCompactionSettings.Default.SummaryShareOfTarget,
            FileContextShareOfSummaryTarget = normalized.FileContextShareOfSummaryTarget ?? LocalAgentCompactionSettings.Default.FileContextShareOfSummaryTarget,
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
