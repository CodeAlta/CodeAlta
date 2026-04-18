using CodeAlta.Agent;
using CodeAlta.Agent.Anthropic;
using CodeAlta.Agent.GoogleGenAI;
using CodeAlta.Agent.LocalRuntime;
using CodeAlta.Agent.LocalRuntime.Compaction;
using CodeAlta.Agent.ModelCatalog;
using CodeAlta.Agent.OpenAI;
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
                LogInfo(
                    $"Skipping raw-API provider backend=<openai> provider={definition.ProviderKey} displayName={FormatDisplayName(definition.DisplayName)} reason=missing-credentials");
                continue;
            }

            var baseUri = ParseUri(definition.BaseUri);
            var configuredExtraBody = CreateExtraBody(definition.ExtraBody);

            if (definition.EnableResponses)
            {
                LogInfo(
                    $"Configuring raw-API provider backend={AgentBackendIds.OpenAIResponses.Value} provider={definition.ProviderKey} displayName={FormatDisplayName(definition.DisplayName)} baseUri={FormatUri(baseUri)} default={definition.DefaultResponses}");
                responseOptions.Providers.Add(new OpenAIProviderOptions
                {
                    ProviderKey = definition.ProviderKey,
                    DisplayName = definition.DisplayName,
                    ApiKey = apiKey,
                    BaseUri = baseUri,
                    OrganizationId = definition.OrganizationId,
                    ProjectId = definition.ProjectId,
                    IsDefault = definition.DefaultResponses,
                    Profile = CreateOpenAIResponsesProfile(
                        definition.ProviderKey,
                        baseUri,
                        definition.Profile),
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
                        configuredExtraBody),
                    ModelOverrides = CreateModelOverrides(definition.ModelOverrides),
                    ModelCatalog = modelCatalog,
                });
            }

            if (definition.EnableChat)
            {
                LogInfo(
                    $"Configuring raw-API provider backend={AgentBackendIds.OpenAIChat.Value} provider={definition.ProviderKey} displayName={FormatDisplayName(definition.DisplayName)} baseUri={FormatUri(baseUri)} default={definition.DefaultChat}");
                chatOptions.Providers.Add(new OpenAIProviderOptions
                {
                    ProviderKey = definition.ProviderKey,
                    DisplayName = definition.DisplayName,
                    ApiKey = apiKey,
                    BaseUri = baseUri,
                    OrganizationId = definition.OrganizationId,
                    ProjectId = definition.ProjectId,
                    IsDefault = definition.DefaultChat,
                    Profile = CreateOpenAIChatProfile(
                        definition.ProviderKey,
                        baseUri,
                        definition.Profile),
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
                        configuredExtraBody),
                    ModelOverrides = CreateModelOverrides(definition.ModelOverrides),
                    ModelCatalog = modelCatalog,
                });
            }
        }

        if (responseOptions.Providers.Count > 0)
        {
            backendFactory.RegisterOpenAIResponses(responseOptions);
            var displayName = ResolveBackendDisplayName("OpenAI Responses", responseOptions.Providers.Select(static provider => provider.DisplayName));
            descriptors.Add(new AgentBackendDescriptor(AgentBackendIds.OpenAIResponses, displayName));
            LogInfo(
                $"Registered raw-API backend backend={AgentBackendIds.OpenAIResponses.Value} displayName={displayName} providers={responseOptions.Providers.Count}");
        }

        if (chatOptions.Providers.Count > 0)
        {
            backendFactory.RegisterOpenAIChat(chatOptions);
            var displayName = ResolveBackendDisplayName("OpenAI Chat", chatOptions.Providers.Select(static provider => provider.DisplayName));
            descriptors.Add(new AgentBackendDescriptor(AgentBackendIds.OpenAIChat, displayName));
            LogInfo(
                $"Registered raw-API backend backend={AgentBackendIds.OpenAIChat.Value} displayName={displayName} providers={chatOptions.Providers.Count}");
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
                LogInfo(
                    $"Skipping raw-API provider backend=<anthropic> provider={definition.ProviderKey} displayName={FormatDisplayName(definition.DisplayName)} reason=missing-credentials");
                continue;
            }

            LogInfo(
                $"Configuring raw-API provider backend={AgentBackendIds.AnthropicMessages.Value} provider={definition.ProviderKey} displayName={FormatDisplayName(definition.DisplayName)} baseUri={FormatUri(ParseUri(definition.BaseUri))} default={definition.IsDefault}");
            options.Providers.Add(new AnthropicProviderOptions
            {
                ProviderKey = definition.ProviderKey,
                DisplayName = definition.DisplayName,
                ApiKey = apiKey,
                BaseUri = ParseUri(definition.BaseUri),
                IsDefault = definition.IsDefault,
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
            });
        }

        if (options.Providers.Count == 0)
        {
            return;
        }

        backendFactory.RegisterAnthropic(options);
        var displayName = ResolveBackendDisplayName("Anthropic Messages", options.Providers.Select(static provider => provider.DisplayName));
        descriptors.Add(new AgentBackendDescriptor(AgentBackendIds.AnthropicMessages, displayName));
        LogInfo(
            $"Registered raw-API backend backend={AgentBackendIds.AnthropicMessages.Value} displayName={displayName} providers={options.Providers.Count}");
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
                LogInfo(
                    $"Skipping raw-API provider backend=<google-genai> provider={definition.ProviderKey} displayName={FormatDisplayName(definition.DisplayName)} reason=missing-credentials");
                continue;
            }

            if (definition.UseVertexAI &&
                (string.IsNullOrWhiteSpace(definition.Project) || string.IsNullOrWhiteSpace(definition.Location)))
            {
                LogInfo(
                    $"Skipping raw-API provider backend=<google-genai> provider={definition.ProviderKey} displayName={FormatDisplayName(definition.DisplayName)} reason=incomplete-vertex-settings");
                continue;
            }

            LogInfo(
                $"Configuring raw-API provider backend={AgentBackendIds.GoogleGenAI.Value} provider={definition.ProviderKey} displayName={FormatDisplayName(definition.DisplayName)} baseUri={FormatUri(ParseUri(definition.BaseUri))} default={definition.IsDefault} vertex={definition.UseVertexAI}");
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
                ModelsDevProviderId = ResolveModelsDevProviderId(
                    definition.ModelsDevProviderId,
                    definition.ProviderKey,
                    "google",
                    modelCatalog),
                SingleModelId = NormalizeText(definition.SingleModelId),
                ModelOverrides = CreateModelOverrides(definition.ModelOverrides),
                ModelCatalog = modelCatalog,
            });
        }

        if (options.Providers.Count == 0)
        {
            return;
        }

        backendFactory.RegisterGoogleGenAI(options);
        var displayName = ResolveBackendDisplayName("Google GenAI", options.Providers.Select(static provider => provider.DisplayName));
        descriptors.Add(new AgentBackendDescriptor(AgentBackendIds.GoogleGenAI, displayName));
        LogInfo(
            $"Registered raw-API backend backend={AgentBackendIds.GoogleGenAI.Value} displayName={displayName} providers={options.Providers.Count}");
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
        Uri? baseUri,
        CodeAltaRawApiProviderProfileDocument? document)
    {
        var profile = CreateOpenAIBaseProfile(LocalAgentTransportKind.OpenAIResponses, providerKey, baseUri, responses: true);
        return document is null ? profile : ApplyProfileOverrides(profile, document);
    }

    private static LocalAgentProviderProfile CreateOpenAIChatProfile(
        string providerKey,
        Uri? baseUri,
        CodeAltaRawApiProviderProfileDocument? document)
    {
        var profile = CreateOpenAIBaseProfile(LocalAgentTransportKind.OpenAIChatCompletions, providerKey, baseUri, responses: false);
        return document is null ? profile : ApplyProfileOverrides(profile, document);
    }

    private static LocalAgentProviderProfile CreateOpenAIBaseProfile(
        LocalAgentTransportKind transportKind,
        string providerKey,
        Uri? baseUri,
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

        return RawApiProviderDefaultsCatalog.ApplyProfileDefaults(transportKind, providerKey, baseUri, profile);
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

    private static string ResolveBackendDisplayName(string fallbackDisplayName, IEnumerable<string?> providerDisplayNames)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackDisplayName);
        ArgumentNullException.ThrowIfNull(providerDisplayNames);

        var distinctDisplayNames = providerDisplayNames
            .Select(NormalizeText)
            .Where(static displayName => !string.IsNullOrWhiteSpace(displayName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return distinctDisplayNames.Length == 1 ? distinctDisplayNames[0]! : fallbackDisplayName.Trim();
    }

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
            AllowSplitTurn: normalized.AllowSplitTurn ?? LocalAgentCompactionSettings.Default.AllowSplitTurn)
        {
            TargetContextRatioIdeal = normalized.TargetContextRatioIdeal ?? LocalAgentCompactionSettings.Default.TargetContextRatioIdeal,
            TargetContextRatioMax = normalized.TargetContextRatioMax ?? LocalAgentCompactionSettings.Default.TargetContextRatioMax,
            RecentSuffixTargetTokens = normalized.RecentSuffixTargetTokens ?? LocalAgentCompactionSettings.Default.RecentSuffixTargetTokens,
            SummaryOutputTokens = normalized.SummaryOutputTokens ?? LocalAgentCompactionSettings.Default.SummaryOutputTokens,
            SummaryInputTokens = normalized.SummaryInputTokens ?? LocalAgentCompactionSettings.Default.SummaryInputTokens,
            ToolResultCharsPerItem = normalized.ToolResultCharsPerItem ?? LocalAgentCompactionSettings.Default.ToolResultCharsPerItem,
            ToolResultCharsTotal = normalized.ToolResultCharsTotal ?? LocalAgentCompactionSettings.Default.ToolResultCharsTotal,
            ReasoningCharsPerItem = normalized.ReasoningCharsPerItem ?? LocalAgentCompactionSettings.Default.ReasoningCharsPerItem,
            ReasoningCharsTotal = normalized.ReasoningCharsTotal ?? LocalAgentCompactionSettings.Default.ReasoningCharsTotal,
            ReasoningMode = ParseReasoningMode(normalized.ReasoningMode),
            MaxChunkPasses = normalized.MaxChunkPasses ?? LocalAgentCompactionSettings.Default.MaxChunkPasses,
            AllowOversizedAnchorReduction = normalized.AllowOversizedAnchorReduction ?? LocalAgentCompactionSettings.Default.AllowOversizedAnchorReduction,
            PreferRecentMessages = normalized.PreferRecentMessages ?? LocalAgentCompactionSettings.Default.PreferRecentMessages,
            PreferRecentToolOutputs = normalized.PreferRecentToolOutputs ?? LocalAgentCompactionSettings.Default.PreferRecentToolOutputs,
            DropMessagesOnlyWhenSummaryInputExceedsBudget = normalized.DropMessagesOnlyWhenSummaryInputExceedsBudget ?? LocalAgentCompactionSettings.Default.DropMessagesOnlyWhenSummaryInputExceedsBudget,
        };
    }

    private static LocalAgentCompactionReasoningMode ParseReasoningMode(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "none" => LocalAgentCompactionReasoningMode.None,
            "summary_only" => LocalAgentCompactionReasoningMode.SummaryOnly,
            _ => LocalAgentCompactionReasoningMode.Adaptive,
        };

    private static string FormatDisplayName(string? displayName)
        => NormalizeText(displayName) ?? "<none>";

    private static string FormatUri(Uri? uri)
        => uri?.ToString() ?? "<default>";

    private static void LogInfo(string message)
    {
        if (LogManager.IsInitialized && Logger.IsEnabled(LogLevel.Info))
        {
            Logger.Info(message);
        }
    }
}
