using CodeAlta.Agent;
using CodeAlta.Agent.Anthropic;
using CodeAlta.Agent.GoogleGenAI;
using CodeAlta.Agent.LocalRuntime;
using CodeAlta.Agent.LocalRuntime.Compaction;
using CodeAlta.Agent.ModelCatalog;
using CodeAlta.Agent.OpenAI;
using CodeAlta.Catalog;

namespace CodeAlta.App;

internal static class RawApiBackendRegistrar
{
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
        RegisterOpenAIBackends(backendFactory, configStore, stateRootPath, descriptors, modelCatalog);
        RegisterAnthropicBackend(backendFactory, configStore, stateRootPath, descriptors, modelCatalog);
        RegisterGoogleGenAIBackend(backendFactory, configStore, stateRootPath, descriptors, modelCatalog);
        return descriptors;
    }

    private static void RegisterOpenAIBackends(
        AgentBackendFactory backendFactory,
        CodeAltaConfigStore configStore,
        string stateRootPath,
        List<AgentBackendDescriptor> descriptors,
        ModelsDevCatalogService? modelCatalog)
    {
        var responseOptions = new OpenAIResponsesAgentBackendOptions
        {
            StateRootPath = stateRootPath,
        };
        var chatOptions = new OpenAIChatAgentBackendOptions
        {
            StateRootPath = stateRootPath,
        };

        foreach (var definition in configStore.LoadGlobalOpenAIProviderDefinitions())
        {
            var apiKey = ResolveSecret(definition.ApiKey, definition.ApiKeyEnv);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                continue;
            }

            var baseUri = ParseUri(definition.BaseUri);

            if (definition.EnableResponses)
            {
                responseOptions.Providers.Add(new OpenAIProviderOptions
                {
                    ProviderKey = definition.ProviderKey,
                    DisplayName = definition.DisplayName,
                    ApiKey = apiKey,
                    BaseUri = baseUri,
                    OrganizationId = definition.OrganizationId,
                    ProjectId = definition.ProjectId,
                    IsDefault = definition.DefaultResponses,
                    Profile = CreateOpenAIResponsesProfile(definition.Profile),
                    Compaction = CreateCompactionSettings(definition.Compaction),
                    ModelsDevProviderId = ResolveModelsDevProviderId(definition.ModelsDevProviderId, "openai"),
                    ModelOverrides = CreateModelOverrides(definition.ModelOverrides),
                    ModelCatalog = modelCatalog,
                });
            }

            if (definition.EnableChat)
            {
                chatOptions.Providers.Add(new OpenAIProviderOptions
                {
                    ProviderKey = definition.ProviderKey,
                    DisplayName = definition.DisplayName,
                    ApiKey = apiKey,
                    BaseUri = baseUri,
                    OrganizationId = definition.OrganizationId,
                    ProjectId = definition.ProjectId,
                    IsDefault = definition.DefaultChat,
                    Profile = CreateOpenAIChatProfile(definition.Profile),
                    Compaction = CreateCompactionSettings(definition.Compaction),
                    ModelsDevProviderId = ResolveModelsDevProviderId(definition.ModelsDevProviderId, "openai"),
                    ModelOverrides = CreateModelOverrides(definition.ModelOverrides),
                    ModelCatalog = modelCatalog,
                });
            }
        }

        if (responseOptions.Providers.Count > 0)
        {
            backendFactory.RegisterOpenAIResponses(responseOptions);
            descriptors.Add(new AgentBackendDescriptor(AgentBackendIds.OpenAIResponses, "OpenAI Responses"));
        }

        if (chatOptions.Providers.Count > 0)
        {
            backendFactory.RegisterOpenAIChat(chatOptions);
            descriptors.Add(new AgentBackendDescriptor(AgentBackendIds.OpenAIChat, "OpenAI Chat"));
        }
    }

    private static void RegisterAnthropicBackend(
        AgentBackendFactory backendFactory,
        CodeAltaConfigStore configStore,
        string stateRootPath,
        List<AgentBackendDescriptor> descriptors,
        ModelsDevCatalogService? modelCatalog)
    {
        var options = new AnthropicAgentBackendOptions
        {
            StateRootPath = stateRootPath,
        };

        foreach (var definition in configStore.LoadGlobalAnthropicProviderDefinitions())
        {
            var apiKey = ResolveSecret(definition.ApiKey, definition.ApiKeyEnv);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                continue;
            }

            options.Providers.Add(new AnthropicProviderOptions
            {
                ProviderKey = definition.ProviderKey,
                DisplayName = definition.DisplayName,
                ApiKey = apiKey,
                BaseUri = ParseUri(definition.BaseUri),
                IsDefault = definition.IsDefault,
                Profile = CreateAnthropicProfile(definition.Profile),
                Compaction = CreateCompactionSettings(definition.Compaction),
                ModelsDevProviderId = ResolveModelsDevProviderId(definition.ModelsDevProviderId, "anthropic"),
                ModelOverrides = CreateModelOverrides(definition.ModelOverrides),
                ModelCatalog = modelCatalog,
            });
        }

        if (options.Providers.Count == 0)
        {
            return;
        }

        backendFactory.RegisterAnthropic(options);
        descriptors.Add(new AgentBackendDescriptor(AgentBackendIds.AnthropicMessages, "Anthropic Messages"));
    }

    private static void RegisterGoogleGenAIBackend(
        AgentBackendFactory backendFactory,
        CodeAltaConfigStore configStore,
        string stateRootPath,
        List<AgentBackendDescriptor> descriptors,
        ModelsDevCatalogService? modelCatalog)
    {
        var options = new GoogleGenAIAgentBackendOptions
        {
            StateRootPath = stateRootPath,
        };

        foreach (var definition in configStore.LoadGlobalGoogleGenAIProviderDefinitions())
        {
            var apiKey = ResolveSecret(definition.ApiKey, definition.ApiKeyEnv);
            if (!definition.UseVertexAI && string.IsNullOrWhiteSpace(apiKey))
            {
                continue;
            }

            if (definition.UseVertexAI &&
                (string.IsNullOrWhiteSpace(definition.Project) || string.IsNullOrWhiteSpace(definition.Location)))
            {
                continue;
            }

            options.Providers.Add(new GoogleGenAIProviderOptions
            {
                ProviderKey = definition.ProviderKey,
                DisplayName = definition.DisplayName,
                ApiKey = apiKey,
                UseVertexAI = definition.UseVertexAI,
                Project = definition.Project,
                Location = definition.Location,
                BaseUri = ParseUri(definition.BaseUri),
                IsDefault = definition.IsDefault,
                Profile = CreateGoogleGenAIProfile(definition.Profile),
                Compaction = CreateCompactionSettings(definition.Compaction),
                ModelsDevProviderId = ResolveModelsDevProviderId(definition.ModelsDevProviderId, "google"),
                ModelOverrides = CreateModelOverrides(definition.ModelOverrides),
                ModelCatalog = modelCatalog,
            });
        }

        if (options.Providers.Count == 0)
        {
            return;
        }

        backendFactory.RegisterGoogleGenAI(options);
        descriptors.Add(new AgentBackendDescriptor(AgentBackendIds.GoogleGenAI, "Google GenAI"));
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

    private static LocalAgentProviderProfile? CreateOpenAIResponsesProfile(CodeAltaRawApiProviderProfileDocument? document)
    {
        if (document is null)
        {
            return null;
        }

        return ApplyProfileOverrides(
            new LocalAgentProviderProfile
            {
                SupportsDeveloperRole = true,
                SupportsStore = true,
                SupportsReasoningEffort = true,
                StreamsUsage = true,
                MaxTokensFieldName = "max_output_tokens",
                ReasoningFieldNames = ["reasoning"],
            },
            document);
    }

    private static LocalAgentProviderProfile? CreateOpenAIChatProfile(CodeAltaRawApiProviderProfileDocument? document)
    {
        if (document is null)
        {
            return null;
        }

        return ApplyProfileOverrides(
            new LocalAgentProviderProfile
            {
                SupportsDeveloperRole = true,
                SupportsStore = true,
                SupportsReasoningEffort = true,
                StreamsUsage = true,
                MaxTokensFieldName = "max_completion_tokens",
                ReasoningFieldNames = ["reasoning_content", "reasoning"],
            },
            document);
    }

    private static LocalAgentProviderProfile? CreateAnthropicProfile(CodeAltaRawApiProviderProfileDocument? document)
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

    private static LocalAgentProviderProfile? CreateGoogleGenAIProfile(CodeAltaRawApiProviderProfileDocument? document)
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
        CodeAltaRawApiProviderProfileDocument document)
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
        };
    }

    private static Uri? ParseUri(string? uriText)
        => Uri.TryCreate(NormalizeText(uriText), UriKind.Absolute, out var uri)
            ? uri
            : null;

    private static string? NormalizeText(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string ResolveModelsDevProviderId(string? configuredProviderId, string defaultProviderId)
        => NormalizeText(configuredProviderId)?.ToLowerInvariant() ?? defaultProviderId;

    private static IReadOnlyDictionary<string, AgentModelOverride>? CreateModelOverrides(
        Dictionary<string, CodeAltaRawApiModelOverrideDocument>? overrides)
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

    private static LocalAgentCompactionSettings CreateCompactionSettings(CodeAltaRawApiCompactionDocument? compaction)
    {
        var normalized = compaction ?? new CodeAltaRawApiCompactionDocument();
        return new LocalAgentCompactionSettings(
            Enabled: normalized.Enabled ?? LocalAgentCompactionSettings.Default.Enabled,
            TriggerThreshold: normalized.TriggerThreshold ?? LocalAgentCompactionSettings.Default.TriggerThreshold,
            TargetThreshold: normalized.TargetThreshold ?? LocalAgentCompactionSettings.Default.TargetThreshold,
            ReservedOutputTokens: normalized.ReservedOutputTokens ?? LocalAgentCompactionSettings.Default.ReservedOutputTokens,
            ReservedOverheadTokens: normalized.ReservedOverheadTokens ?? LocalAgentCompactionSettings.Default.ReservedOverheadTokens,
            KeepLastUserMessage: normalized.KeepLastUserMessage ?? LocalAgentCompactionSettings.Default.KeepLastUserMessage,
            AllowSplitTurn: normalized.AllowSplitTurn ?? LocalAgentCompactionSettings.Default.AllowSplitTurn);
    }
}
