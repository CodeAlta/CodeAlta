using CodeAlta.Agent.LocalRuntime;
using CodeAlta.Agent.LocalRuntime.Compaction;
using CodeAlta.Agent.OpenAI.CodexSubscription;

namespace CodeAlta.Agent.OpenAI;

internal static class OpenAIBackendFactory
{
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

    public static IAgentBackend CreateChatBackend(OpenAIChatAgentBackendOptions options)
        => CreateBackend(
            options.BackendIdOverride ?? AgentBackendIds.OpenAIChat,
            string.IsNullOrWhiteSpace(options.DisplayNameOverride) ? "OpenAI Chat" : options.DisplayNameOverride.Trim(),
            "openai-chat",
            LocalAgentTransportKind.OpenAIChatCompletions,
            options,
            static provider => new OpenAIChatTurnExecutor(provider));

    private static IAgentBackend CreateBackend(
        AgentBackendId backendId,
        string displayName,
        string protocolFamily,
        LocalAgentTransportKind transportKind,
        OpenAIAgentBackendOptions options,
        Func<OpenAIProviderOptions, ILocalAgentTurnExecutor> executorFactory,
        Func<OpenAIProviderOptions, string>? protocolFamilySelector = null)
    {
        if (options.Providers.Count == 0)
        {
            throw new ArgumentException("At least one provider registration is required.", nameof(options));
        }

        foreach (var provider in options.Providers)
        {
            provider.StateRootPath ??= options.StateRootPath;
            if (provider.ProtocolTracing is { } protocolTracing && string.IsNullOrWhiteSpace(protocolTracing.StateRootPath))
            {
                protocolTracing.StateRootPath = provider.StateRootPath;
            }
        }

        return new LocalAgentBackend(
            backendId,
            displayName,
            new LocalAgentBackendOptions
            {
                StateRootPath = options.StateRootPath,
                Providers =
                [
                    .. options.Providers.Select(provider => new LocalAgentBackendProviderRegistration
                    {
                        Provider = new LocalAgentProviderDescriptor
                        {
                            ProtocolFamily = protocolFamilySelector?.Invoke(provider) ?? protocolFamily,
                            ProviderKey = provider.ProviderKey.Trim(),
                            DisplayName = string.IsNullOrWhiteSpace(provider.DisplayName) ? provider.ProviderKey.Trim() : provider.DisplayName.Trim(),
                            BackendId = backendId,
                            TransportKind = transportKind,
                            BaseUri = provider.BaseUri,
                            IsDefault = provider.IsDefault,
                            Profile = provider.Profile ?? CreateDefaultProfile(transportKind),
                            Compaction = provider.Compaction ?? LocalAgentCompactionSettings.Default,
                        },
                        TurnExecutor = executorFactory(provider),
                    }),
                ],
            });
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
