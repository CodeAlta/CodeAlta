#pragma warning disable OPENAI001

using System.ClientModel;
using CodeAlta.Agent.Anthropic;
using CodeAlta.Agent.Runtime;
using CodeAlta.Agent.OpenAI;
using OpenAI.Responses;

namespace CodeAlta.Agent.Copilot;

internal sealed class CopilotDirectTurnExecutor : IModelProviderTurnExecutor, IModelProviderModelCatalog
{
    private static readonly AgentProviderProfile OpenAIChatProfile = new()
    {
        SupportsDeveloperRole = false,
        SupportsReasoningEffort = false,
        SupportsStore = false,
        SupportsStrictTools = true,
        StreamsUsage = true,
        MaxTokensFieldName = "max_completion_tokens",
        ReasoningFieldNames = ["reasoning_text", "reasoning_content", "reasoning"],
        ReasoningInputFieldName = "reasoning_opaque",
    };

    private static readonly AgentProviderProfile OpenAIResponsesProfile = new()
    {
        SupportsDeveloperRole = true,
        SupportsReasoningEffort = true,
        SupportsStore = false,
        SupportsStrictTools = true,
        StreamsUsage = true,
        MaxTokensFieldName = "max_output_tokens",
        ReasoningFieldNames = ["reasoning"],
    };

    private static readonly AgentProviderProfile AnthropicMessagesProfile = new()
    {
        SupportsDeveloperRole = false,
        StreamsUsage = true,
        SupportsThoughtSignatures = true,
    };

    private readonly CopilotDirectProviderOptions _provider;
    private readonly HttpClient _httpClient;
    private readonly CopilotDirectAuthManager _authManager;
    private readonly CopilotModelDiscoveryClient _modelDiscovery;

    public CopilotDirectTurnExecutor(CopilotDirectProviderOptions provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _provider = provider;
        _httpClient = provider.HttpClient ?? new HttpClient();
        _authManager = new CopilotDirectAuthManager(provider, _httpClient);
        _modelDiscovery = new CopilotModelDiscoveryClient(provider, _authManager, _httpClient);
    }

    public Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(
        ModelProviderRuntimeDescriptor provider,
        CancellationToken cancellationToken = default)
        => _modelDiscovery.ListModelsAsync(provider, cancellationToken);

    public Task<AgentTurnResponse> ExecuteTurnAsync(
        AgentTurnRequest request,
        Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate,
        CancellationToken cancellationToken = default)
        => ExecuteTurnAsync(
            request,
            onUpdate,
            static (_, _) => ValueTask.CompletedTask,
            cancellationToken);

    public async Task<AgentTurnResponse> ExecuteTurnAsync(
        AgentTurnRequest request,
        Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate,
        Func<AgentTurnSessionUpdate, CancellationToken, ValueTask> onSessionUpdate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(onUpdate);
        ArgumentNullException.ThrowIfNull(onSessionUpdate);

        var endpointKind = CopilotEndpointDispatcher.Resolve(request.ModelInfo);
        try
        {
            return await ExecuteTurnCoreAsync(request, endpointKind, onUpdate, onSessionUpdate, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ShouldRefreshCredential(ex))
        {
            await _authManager.ForceRefreshAsync(cancellationToken).ConfigureAwait(false);
            return await ExecuteTurnCoreAsync(request, endpointKind, onUpdate, onSessionUpdate, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<AgentTurnResponse> ExecuteTurnCoreAsync(
        AgentTurnRequest request,
        CopilotEndpointKind endpointKind,
        Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate,
        Func<AgentTurnSessionUpdate, CancellationToken, ValueTask> onSessionUpdate,
        CancellationToken cancellationToken)
    {
        var credential = await _authManager.GetCredentialAsync(cancellationToken).ConfigureAwait(false);
        return endpointKind switch
        {
            CopilotEndpointKind.Responses => await ExecuteOpenAIResponsesAsync(request, credential, onUpdate, onSessionUpdate, cancellationToken).ConfigureAwait(false),
            CopilotEndpointKind.AnthropicMessages => await ExecuteAnthropicMessagesAsync(request, credential, onUpdate, cancellationToken).ConfigureAwait(false),
            _ => await ExecuteOpenAIChatAsync(request, credential, onUpdate, cancellationToken).ConfigureAwait(false),
        };
    }

    private async Task<AgentTurnResponse> ExecuteOpenAIChatAsync(
        AgentTurnRequest request,
        CopilotDirectCredential credential,
        Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate,
        CancellationToken cancellationToken)
    {
        var profile = _provider.Profile ?? CreateOpenAIChatProfile(request.ModelInfo);
        var provider = CreateOpenAIProviderOptions(credential, request, profile);
        var executor = new OpenAIChatTurnExecutor(provider);
        return await executor.ExecuteTurnAsync(
            CreateDelegatedRequest(request, credential, "openai-chat", AgentTransportKind.OpenAIChatCompletions, profile),
            onUpdate,
            cancellationToken).ConfigureAwait(false);
    }

    private static AgentProviderProfile CreateOpenAIChatProfile(AgentModelInfo? modelInfo)
        => OpenAIChatProfile with
        {
            SupportsReasoningEffort = modelInfo?.SupportedReasoningEfforts is { Count: > 0 },
        };

    private async Task<AgentTurnResponse> ExecuteOpenAIResponsesAsync(
        AgentTurnRequest request,
        CopilotDirectCredential credential,
        Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate,
        Func<AgentTurnSessionUpdate, CancellationToken, ValueTask> onSessionUpdate,
        CancellationToken cancellationToken)
    {
        var profile = _provider.Profile ?? OpenAIResponsesProfile;
        var provider = CreateOpenAIProviderOptions(credential, request, profile);
        await using var executor = new OpenAIResponsesTurnExecutor(provider);
        return await executor.ExecuteTurnAsync(
            CreateDelegatedRequest(request, credential, "openai-responses", AgentTransportKind.OpenAIResponses, profile),
            onUpdate,
            onSessionUpdate,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<AgentTurnResponse> ExecuteAnthropicMessagesAsync(
        AgentTurnRequest request,
        CopilotDirectCredential credential,
        Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate,
        CancellationToken cancellationToken)
    {
        var provider = new AnthropicProviderOptions
        {
            ProviderKey = _provider.ProviderKey,
            DisplayName = _provider.DisplayName,
            AuthToken = credential.Token,
            BaseUri = credential.BaseUri,
            HttpClient = _httpClient,
            IsDefault = _provider.IsDefault,
            Profile = _provider.Profile ?? AnthropicMessagesProfile,
            Compaction = _provider.Compaction,
            ModelOverrides = _provider.ModelOverrides,
            ExtraHeaders = CreateAnthropicHeaders(IsAgentInitiated(request), HasVisionInput(request)),
        };
        var executor = AnthropicModelProviderRuntime.CreateTurnExecutor(provider);
        return await executor.ExecuteTurnAsync(
            CreateDelegatedRequest(request, credential, "anthropic-messages", AgentTransportKind.AnthropicMessages, _provider.Profile ?? AnthropicMessagesProfile),
            onUpdate,
            cancellationToken).ConfigureAwait(false);
    }

    private OpenAIProviderOptions CreateOpenAIProviderOptions(
        CopilotDirectCredential credential,
        AgentTurnRequest request,
        AgentProviderProfile profile)
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
            ResponsesRequestCustomizer = ConfigureOpenAIResponsesReasoning,
            ExtraHeaders = CreateCopilotTurnHeaders(IsAgentInitiated(request), HasVisionInput(request), anthropicMessages: false),
        };

    private static void ConfigureOpenAIResponsesReasoning(OpenAIResponsesRequestCustomizationContext context)
    {
        var reasoningOptions = context.Options.ReasoningOptions;
        if (reasoningOptions is null)
        {
            return;
        }

        if (reasoningOptions.ReasoningEffortLevel is not null)
        {
            // Copilot's Responses endpoint only streams user-visible reasoning when the
            // request opts into summaries. Use the same conservative summary setting as
            // other Copilot API clients and request encrypted reasoning for local replay.
            reasoningOptions.ReasoningSummaryVerbosity = ResponseReasoningSummaryVerbosity.Auto;
            if (!context.Options.IncludedProperties.Contains(IncludedResponseProperty.ReasoningEncryptedContent))
            {
                context.Options.IncludedProperties.Add(IncludedResponseProperty.ReasoningEncryptedContent);
            }
        }
        else
        {
            context.Options.ReasoningOptions = null;
        }
    }

    private static IReadOnlyDictionary<string, string> CreateAnthropicHeaders(bool isAgentInitiated, bool hasVisionInput)
        => CreateCopilotTurnHeaders(isAgentInitiated, hasVisionInput, anthropicMessages: true);

    private static IReadOnlyDictionary<string, string> CreateCopilotTurnHeaders(bool isAgentInitiated, bool hasVisionInput, bool anthropicMessages)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["User-Agent"] = "CodeAlta/1.0",
            ["Editor-Version"] = "CodeAlta/1.0",
            ["Editor-Plugin-Version"] = "codealta/1.0",
            ["Copilot-Integration-Id"] = "vscode-chat",
            ["Openai-Intent"] = "conversation-edits",
            ["X-Initiator"] = isAgentInitiated ? "agent" : "user",
        };
        if (anthropicMessages)
        {
            headers["anthropic-beta"] = "interleaved-thinking-2025-05-14";
        }

        if (hasVisionInput)
        {
            headers["Copilot-Vision-Request"] = "true";
        }

        return headers;
    }

    private static AgentTurnRequest CreateDelegatedRequest(
        AgentTurnRequest request,
        CopilotDirectCredential credential,
        string protocolFamily,
        AgentTransportKind transportKind,
        AgentProviderProfile profile)
    {
        var provider = request.Provider with
        {
            ProtocolFamily = protocolFamily,
            TransportKind = transportKind,
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

    private static bool IsAgentInitiated(AgentTurnRequest request)
        => request.Conversation.LastOrDefault()?.Role != AgentConversationRole.User;

    private static bool HasVisionInput(AgentTurnRequest request)
        => request.Conversation.SelectMany(static message => message.Parts).Any(static part => part switch
        {
            AgentMessagePart.Uri uri => IsImageMediaType(uri.MediaType),
            AgentMessagePart.Data data => IsImageMediaType(data.MediaType),
            _ => false,
        });

    private static bool IsImageMediaType(string? mediaType)
        => mediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true;
}
