using CodeAlta.Agent.LocalRuntime;
using CodeAlta.Agent.LocalRuntime.Compaction;
using CodeAlta.Agent.OpenAI.Codex;

namespace CodeAlta.Agent.OpenAI;

internal static class OpenAIBackendFactory
{
    public static ICodeAltaModelProviderRuntime CreateResponsesProviderRuntime(OpenAIResponsesAgentBackendOptions options)
    {
        var codexSubscriptionConcurrencyLimiter = options.CodexSubscriptionConcurrencyLimiter ?? new CodexSubscriptionConcurrencyLimiter();
        return CreateSingleProviderRuntime(
            options.BackendIdOverride ?? AgentBackendIds.OpenAIResponses,
            string.IsNullOrWhiteSpace(options.DisplayNameOverride) ? "OpenAI Responses" : options.DisplayNameOverride.Trim(),
            "openai-responses",
            LocalAgentTransportKind.OpenAIResponses,
            options,
            provider => new OpenAIResponsesTurnExecutor(provider, codexSubscriptionConcurrencyLimiter),
            static provider => provider.CodexSubscription is null
                ? "openai-responses"
                : "codex");
    }

    public static IAgentBackend CreateResponsesBackend(OpenAIResponsesAgentBackendOptions options)
    {
        var codexSubscriptionConcurrencyLimiter = options.CodexSubscriptionConcurrencyLimiter ?? new CodexSubscriptionConcurrencyLimiter();
        return CreateBackend(
            options.BackendIdOverride ?? AgentBackendIds.OpenAIResponses,
            string.IsNullOrWhiteSpace(options.DisplayNameOverride) ? "OpenAI Responses" : options.DisplayNameOverride.Trim(),
            "openai-responses",
            LocalAgentTransportKind.OpenAIResponses,
            options,
            provider => new OpenAIResponsesTurnExecutor(provider, codexSubscriptionConcurrencyLimiter),
            static provider => provider.CodexSubscription is null
                ? "openai-responses"
                : "codex");
    }

    public static ICodeAltaModelProviderRuntime CreateChatProviderRuntime(OpenAIChatAgentBackendOptions options)
        => CreateSingleProviderRuntime(
            options.BackendIdOverride ?? AgentBackendIds.OpenAIChat,
            string.IsNullOrWhiteSpace(options.DisplayNameOverride) ? "OpenAI Chat" : options.DisplayNameOverride.Trim(),
            "openai-chat",
            LocalAgentTransportKind.OpenAIChatCompletions,
            options,
            static provider => new OpenAIChatTurnExecutor(provider));

    public static IAgentBackend CreateChatBackend(OpenAIChatAgentBackendOptions options)
        => CreateBackend(
            options.BackendIdOverride ?? AgentBackendIds.OpenAIChat,
            string.IsNullOrWhiteSpace(options.DisplayNameOverride) ? "OpenAI Chat" : options.DisplayNameOverride.Trim(),
            "openai-chat",
            LocalAgentTransportKind.OpenAIChatCompletions,
            options,
            static provider => new OpenAIChatTurnExecutor(provider));

    private static ICodeAltaModelProviderRuntime CreateSingleProviderRuntime(
        AgentBackendId backendId,
        string displayName,
        string providerType,
        LocalAgentTransportKind transportKind,
        OpenAIAgentBackendOptions options,
        Func<OpenAIProviderOptions, IModelProviderTurnExecutor> executorFactory,
        Func<OpenAIProviderOptions, string>? protocolFamilySelector = null)
    {
        if (options.Providers.Count != 1)
        {
            throw new ArgumentException("Exactly one provider registration is required for a model provider runtime.", nameof(options));
        }

        PrepareProviders(options);
        return CreateProviderRuntime(
            backendId,
            displayName,
            providerType,
            transportKind,
            options.Providers[0],
            executorFactory,
            protocolFamilySelector);
    }

    private static IAgentBackend CreateBackend(
        AgentBackendId backendId,
        string displayName,
        string providerType,
        LocalAgentTransportKind transportKind,
        OpenAIAgentBackendOptions options,
        Func<OpenAIProviderOptions, IModelProviderTurnExecutor> executorFactory,
        Func<OpenAIProviderOptions, string>? protocolFamilySelector = null)
    {
        if (options.Providers.Count == 0)
        {
            throw new ArgumentException("At least one provider registration is required.", nameof(options));
        }

        PrepareProviders(options);
        var registrations = options.Providers
            .Select(provider => CreateProviderRuntime(
                backendId,
                displayName,
                providerType,
                transportKind,
                provider,
                executorFactory,
                protocolFamilySelector).CreateProviderRegistration())
            .ToArray();

        return new CodeAltaAgentRuntime(
            backendId,
            displayName,
            new CodeAltaAgentRuntimeOptions
            {
                StateRootPath = options.StateRootPath,
                Providers = registrations,
            });
    }

    private static CodeAltaModelProviderRuntime CreateProviderRuntime(
        AgentBackendId backendId,
        string displayName,
        string providerType,
        LocalAgentTransportKind transportKind,
        OpenAIProviderOptions provider,
        Func<OpenAIProviderOptions, IModelProviderTurnExecutor> executorFactory,
        Func<OpenAIProviderOptions, string>? protocolFamilySelector)
    {
        var providerKey = provider.ProviderKey.Trim();
        var providerDisplayName = string.IsNullOrWhiteSpace(provider.DisplayName) ? providerKey : provider.DisplayName.Trim();
        var runtimeDescriptor = new ModelProviderRuntimeDescriptor
        {
            ProtocolFamily = protocolFamilySelector?.Invoke(provider) ?? providerType,
            ProviderKey = providerKey,
            DisplayName = providerDisplayName,
            TransportKind = transportKind,
            BaseUri = provider.BaseUri,
            IsDefault = provider.IsDefault,
            Profile = provider.Profile ?? CreateDefaultProfile(transportKind),
            Compaction = provider.Compaction ?? LocalAgentCompactionSettings.Default,
        };
        var descriptor = new ModelProviderDescriptor(new ModelProviderId(providerKey), providerDisplayName, providerType)
        {
            BaseUri = provider.BaseUri,
            IsDefault = provider.IsDefault,
            DefaultModelId = provider.SingleModelId,
        };
        return new CodeAltaModelProviderRuntime(
            descriptor,
            runtimeDescriptor,
            executorFactory(provider));
    }

    private static void PrepareProviders(OpenAIAgentBackendOptions options)
    {
        foreach (var provider in options.Providers)
        {
            provider.StateRootPath ??= options.StateRootPath;
            if (provider.ProtocolTracing is { } protocolTracing && string.IsNullOrWhiteSpace(protocolTracing.StateRootPath))
            {
                protocolTracing.StateRootPath = provider.StateRootPath;
            }

            if (provider.ModelRequestOverrides?.Values.Any(static request => request.Headers is { Count: > 0 } || request.RemoveHeaders is { Count: > 0 }) == true)
            {
                provider.RequestHeaderContext ??= new OpenAIRequestHeaderContext();
            }
        }
    }

    private static LocalAgentProviderProfile CreateDefaultProfile(LocalAgentTransportKind transportKind)
    {
        return transportKind switch
        {
            LocalAgentTransportKind.OpenAIResponses => new LocalAgentProviderProfile
            {
                SupportsDeveloperRole = true,
                SupportsStore = true,
                SupportsReasoningEffort = true,
                StreamsUsage = true,
                MaxTokensFieldName = "max_output_tokens",
                ReasoningFieldNames = ["reasoning"],
            },
            _ => new LocalAgentProviderProfile
            {
                SupportsDeveloperRole = true,
                SupportsStore = true,
                SupportsReasoningEffort = true,
                StreamsUsage = true,
                MaxTokensFieldName = "max_completion_tokens",
                ReasoningFieldNames = ["reasoning_content", "reasoning"],
            },
        };
    }
}
