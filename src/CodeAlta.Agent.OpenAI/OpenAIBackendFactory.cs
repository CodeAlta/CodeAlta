using CodeAlta.Agent.LocalRuntime;
using CodeAlta.Agent.LocalRuntime.Compaction;

namespace CodeAlta.Agent.OpenAI;

internal static class OpenAIBackendFactory
{
    public static IAgentBackend CreateResponsesBackend(OpenAIResponsesAgentBackendOptions options)
        => CreateBackend(
            AgentBackendIds.OpenAIResponses,
            "OpenAI Responses",
            "openai-responses",
            LocalAgentTransportKind.OpenAIResponses,
            options,
            static provider => new OpenAIResponsesTurnExecutor(provider));

    public static IAgentBackend CreateChatBackend(OpenAIChatAgentBackendOptions options)
        => CreateBackend(
            AgentBackendIds.OpenAIChat,
            "OpenAI Chat",
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
        Func<OpenAIProviderOptions, ILocalAgentTurnExecutor> executorFactory)
    {
        if (options.Providers.Count == 0)
        {
            throw new ArgumentException("At least one provider registration is required.", nameof(options));
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
                            ProtocolFamily = protocolFamily,
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
