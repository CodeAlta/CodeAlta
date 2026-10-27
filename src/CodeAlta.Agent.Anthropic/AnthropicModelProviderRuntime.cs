using System.Text.Json;
using Anthropic;
using Anthropic.Core;
using Anthropic.Credentials;
using Anthropic.Models.Models;
using CodeAlta.Agent.Runtime;
using CodeAlta.Agent.Runtime.Compaction;
using CodeAlta.Agent.ModelCatalog;
using Microsoft.Extensions.AI;
using XenoAtom.Logging;

namespace CodeAlta.Agent.Anthropic;

/// <summary>
/// Anthropic Messages model-provider runtime.
/// </summary>
public sealed class AnthropicModelProviderRuntime : IAgentModelProviderRuntime
{
    // The capability of a model that names the effort its highest effort is sent as: "xhigh" or "max".
    private const string HighestEffortCapabilityKey = "anthropicHighestEffort";

    // The capability of a model that the endpoint says takes adaptive thinking and its effort, whatever its name.
    private const string AdaptiveThinkingCapabilityKey = "anthropicAdaptiveThinking";

    // The wait of a rate limit of the API whose retry-after is not known: its limits are per minute.
    private const int UnknownRateLimitRetryAfterSeconds = 30;
    private static readonly Logger Logger = LogManager.GetLogger("CodeAlta.Agent.Anthropic");
    private readonly IAgentModelProviderRuntime _runtime;

    /// <summary>
    /// Initializes a new instance of the <see cref="AnthropicModelProviderRuntime"/> class.
    /// </summary>
    /// <param name="options">The provider runtime options.</param>
    public AnthropicModelProviderRuntime(AnthropicModelProviderRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Providers.Count == 0)
        {
            throw new ArgumentException("At least one provider registration is required.", nameof(options));
        }

        _runtime = CreateProviderRuntime(options.Providers[0]);

    }


    /// <inheritdoc />
    public ModelProviderDescriptor Descriptor => _runtime.Descriptor;

    /// <inheritdoc />
    public ModelProviderRuntimeDescriptor RuntimeDescriptor => _runtime.RuntimeDescriptor;

    /// <inheritdoc />
    public IModelProviderModelCatalog? ModelCatalog => _runtime.ModelCatalog;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken = default)
        => _runtime.StartAsync(cancellationToken);

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken = default)
        => _runtime.StopAsync(cancellationToken);

    /// <inheritdoc />
    public Task<ModelProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
        => _runtime.ProbeAsync(cancellationToken);

    /// <inheritdoc />
    public IModelProviderTurnExecutor CreateTurnExecutor() => _runtime.CreateTurnExecutor();

    /// <inheritdoc />
    public AgentRuntimeProviderRegistration CreateProviderRegistration() => _runtime.CreateProviderRegistration();

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _runtime.DisposeAsync();

    private static AgentModelProviderRuntime CreateProviderRuntime(AnthropicProviderOptions provider)
    {
        var providerKey = provider.ProviderKey.Trim();
        var displayName = string.IsNullOrWhiteSpace(provider.DisplayName) ? providerKey : provider.DisplayName.Trim();
        var runtimeDescriptor = new ModelProviderRuntimeDescriptor
        {
            ProtocolFamily = "anthropic-messages",
            ProviderKey = providerKey,
            DisplayName = displayName,
            TransportKind = AgentTransportKind.AnthropicMessages,
            BaseUri = provider.BaseUri,
            IsDefault = provider.IsDefault,
            Profile = provider.Profile ?? new AgentProviderProfile
            {
                SupportsDeveloperRole = false,
                StreamsUsage = true,
                SupportsThoughtSignatures = true,
            },
            Compaction = provider.Compaction ?? AgentCompactionSettings.Default,
        };
        var descriptor = new ModelProviderDescriptor(new ModelProviderId(providerKey), displayName, "anthropic")
        {
            BaseUri = provider.BaseUri,
            IsDefault = provider.IsDefault,
            DefaultModelId = provider.SingleModelId,
        };
        return new AgentModelProviderRuntime(
            descriptor,
            runtimeDescriptor,
            CreateTurnExecutor(provider));
    }

    internal static IModelProviderTurnExecutor CreateTurnExecutor(AnthropicProviderOptions provider)
    {
        return new ChatClientTurnExecutor(
            (providerDescriptor, cancellationToken) => CreateChatClientAsync(provider, providerDescriptor, cancellationToken),
            (providerDescriptor, cancellationToken) => ListModelsAsync(provider, providerDescriptor, cancellationToken),
            configureOptions: ConfigureOptions,
            // The results of parallel tool calls go out in one user message, then the markers go on that message.
            prepareRequest: (request, messages, options) => MarkCacheBreakpoints(
                request,
                AnthropicToolResultMessages.MergeConsecutive(messages),
                MarkDeferredTools(provider, request, options),
                provider.PromptCacheTtl),
            classifyFailure: ClassifyFailure,
            promptCacheLifetime: provider.HonorsPromptCacheTtl ? provider.PromptCacheTtl : null,
            describeContentFilter: DescribeRefusal,
            reviewResponse: ReviewFallback,
            describeFailure: DescribeFailure,
            // The Claude API takes an image inside the tool_result that returns it; another endpoint of the protocol
            // (Copilot, a gateway) gets it in a user message unless its profile says otherwise.
            toolResultsCarryImages: request => request.Provider.Profile?.SupportsToolResultImages
                ?? IsClaudeApi(provider.BaseUri ?? request.Provider.BaseUri))
        {
            ToolDeferral = request => DefersTools(provider, request),
            AttachFailureDiagnostics = AttachFailureDiagnostics,
        };
    }

    // What the error card, the log and `alta` show of an error of the API: its status and its body (an error event of
    // the stream already has its data in its message).
    internal static void AttachFailureDiagnostics(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is global::Anthropic.Exceptions.AnthropicApiException api)
            {
                AgentErrorData.Set(api, AgentErrorData.HttpStatus, (int)api.StatusCode);
                AgentErrorData.Set(api, AgentErrorData.Payload, api.ResponseBody);
            }
        }
    }

    // The Claude API takes deferred tools from Claude 4.5 on; another endpoint of the protocol only when its profile says so.
    internal static bool DefersTools(AnthropicProviderOptions provider, AgentTurnRequest request)
        => request.Provider.Profile?.SupportsToolDeferral
            ?? (IsClaudeApi(provider.BaseUri ?? request.Provider.BaseUri) && AgentToolDeferral.IsClaudeModelWithToolSearch(request.ModelId));

    // The server-side fallback marks where another model went on with a `fallback` content block, which the SDK
    // keeps as raw JSON: {"type":"fallback","from":{"model":...},"to":{"model":...}}. What the declined model thought
    // or called before it is not replayed (its text is: the fallback model was given it); the model that answered is
    // the one of the last block, or the one message_start names when the conversation already fell back.
    internal static string? ReviewFallback(List<ChatResponseUpdate> updates, AgentTurnRequest request)
    {
        var boundary = -1;
        string? fallbackModel = null;
        for (var index = 0; index < updates.Count; index++)
        {
            if (updates[index].RawRepresentation is global::Anthropic.Models.Messages.RawMessageStreamEvent { Value: global::Anthropic.Models.Messages.RawContentBlockStartEvent start } &&
                start.ContentBlock.Value is null &&
                TryReadFallbackModel(start.ContentBlock.Json) is { } model)
            {
                boundary = index;
                fallbackModel = model;
            }
        }

        for (var index = 0; index < boundary; index++)
        {
            var update = updates[index];
            if (update.Contents.Any(static content => content is TextReasoningContent or FunctionCallContent))
            {
                update.Contents = [.. update.Contents.Where(static content => content is not (TextReasoningContent or FunctionCallContent))];
            }
        }

        var served = fallbackModel ?? updates.Select(static update => update.ModelId).FirstOrDefault(static model => !string.IsNullOrWhiteSpace(model));
        return IsReroute(request.ModelId, served) ? served : null;
    }

    private static string? TryReadFallbackModel(JsonElement block)
        => block.ValueKind == JsonValueKind.Object &&
            block.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "fallback" &&
            block.TryGetProperty("to", out var to) && to.ValueKind == JsonValueKind.Object &&
            to.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(model.GetString())
                ? model.GetString()
                : null;

    // An alias answered by its dated model (claude-haiku-4-5 by claude-haiku-4-5-20251001) is not a reroute.
    private static bool IsReroute(string? requested, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? served)
        => !string.IsNullOrWhiteSpace(requested) && !string.IsNullOrWhiteSpace(served) &&
            !served.StartsWith(requested, StringComparison.OrdinalIgnoreCase) &&
            !requested.StartsWith(served, StringComparison.OrdinalIgnoreCase);

    // A refusal (stop_reason "refusal") names its policy category and an explanation in the stop_details of the
    // message_delta event, when the API gives them.
    internal static string DescribeRefusal(IReadOnlyList<ChatResponseUpdate> updates)
    {
        string? category = null;
        string? explanation = null;
        for (var index = updates.Count - 1; index >= 0; index--)
        {
            if (updates[index].RawRepresentation is not global::Anthropic.Models.Messages.RawMessageStreamEvent streamEvent ||
                streamEvent.Value is not global::Anthropic.Models.Messages.RawMessageDeltaEvent delta)
            {
                continue;
            }

            try
            {
                var details = delta.Delta.StopDetails;
                category = details?.Category?.Raw();
                explanation = details?.Explanation;
            }
            catch (global::Anthropic.Exceptions.AnthropicInvalidDataException)
            {
            }

            break;
        }

        var text = new System.Text.StringBuilder("The model declined to answer (stop reason: refusal");
        if (!string.IsNullOrWhiteSpace(category)) text.Append(", category: ").Append(category.Trim());
        text.Append(')');
        text.Append(string.IsNullOrWhiteSpace(explanation) ? "." : $": {explanation.Trim().TrimEnd('.')}.");
        text.Append(" Its partial answer was not kept. Rephrase the request, or continue with another model.");
        return text.ToString();
    }

    // The SDK's exceptions carry the HTTP status and body; an error in the stream carries its error type
    // (overloaded_error, rate_limit_error...).
    internal static AgentFailureInfo? ClassifyFailure(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case global::Anthropic.Exceptions.AnthropicApiException { StatusCode: System.Net.HttpStatusCode.RequestEntityTooLarge }:
                    // Over the 32 MB the API takes: another provider is not the answer (DescribeFailure).
                    return new AgentFailureInfo(AgentFailureKind.Other);
                case global::Anthropic.Exceptions.AnthropicApiException api:
                    return ClassifyError((int)api.StatusCode, api.ResponseBody ?? api.Message);
                case global::Anthropic.Exceptions.AnthropicSseException sse:
                    return ClassifyError(status: null, sse.Message) ?? new AgentFailureInfo(AgentFailureKind.Other);
                case global::Anthropic.Exceptions.AnthropicIOException:
                    return new AgentFailureInfo(AgentFailureKind.Unavailable);
            }
        }

        return AgentFailureClassifier.Classify(exception);
    }

    // An error of the API, of Copilot's Claude models or of a compatible endpoint (Kimi), from its status (none in the
    // stream) and its body: a spend cap or an empty credit balance is an exhausted quota, whatever its status; a long
    // limit of Copilot is not the short per-minute limit of the API.
    private static AgentFailureInfo? ClassifyError(int? status, string body)
    {
        var resetsAt = TryGetRegainAccess(body);
        int? retryAfterSeconds = null;
        if (AgentFailureClassifier.TryParseRetryDelay(body, out var delay))
        {
            retryAfterSeconds = (int)Math.Ceiling(delay.TotalSeconds);
            resetsAt ??= DateTimeOffset.UtcNow + delay;
        }

        if (IsUsageLimit(body) || body.Contains("billing_error", StringComparison.OrdinalIgnoreCase))
        {
            return new AgentFailureInfo(AgentFailureKind.QuotaExhausted, resetsAt, retryAfterSeconds);
        }

        if (body.Contains("model_not_supported", StringComparison.OrdinalIgnoreCase) ||
            (status == 404 && body.Contains("\"message\":\"model:", StringComparison.OrdinalIgnoreCase)))
        {
            return new AgentFailureInfo(AgentFailureKind.ModelUnavailable);
        }

        if (body.Contains("overloaded", StringComparison.OrdinalIgnoreCase) ||
            (status is null && (body.Contains("api_error", StringComparison.OrdinalIgnoreCase) || body.Contains("timeout_error", StringComparison.OrdinalIgnoreCase))))
        {
            return new AgentFailureInfo(AgentFailureKind.Unavailable, resetsAt, retryAfterSeconds);
        }

        // The weekly or global limits of Copilot last hours: never a short limit.
        if (body.Contains("_rate_limited", StringComparison.OrdinalIgnoreCase))
        {
            return new AgentFailureInfo(AgentFailureKind.RateLimited, resetsAt, retryAfterSeconds);
        }

        if (status is { } code)
        {
            return AsShortRateLimit(AgentFailureClassifier.FromHttpStatus(code, body, retryAfterSeconds, resetsAt));
        }

        if (body.Contains("authentication_error", StringComparison.OrdinalIgnoreCase) || body.Contains("permission_error", StringComparison.OrdinalIgnoreCase))
        {
            return new AgentFailureInfo(AgentFailureKind.Auth);
        }

        return body.Contains("rate_limit", StringComparison.OrdinalIgnoreCase)
            ? AsShortRateLimit(new AgentFailureInfo(
                AgentFailureClassifier.IsQuotaMessage(body) ? AgentFailureKind.QuotaExhausted : AgentFailureKind.RateLimited,
                resetsAt,
                retryAfterSeconds))
            : null;
    }

    // A spend cap of the organization or of the workspace, an empty credit balance, or a usage limit of a plan (Kimi
    // answers its 5-hour limit with a 403).
    private static bool IsUsageLimit(string body)
        => body.Contains("enforced_spend_limit_reached", StringComparison.OrdinalIgnoreCase) ||
           body.Contains("credit balance is too low", StringComparison.OrdinalIgnoreCase) ||
           body.Contains("API usage limits", StringComparison.OrdinalIgnoreCase) ||
           body.Contains("usage limit", StringComparison.OrdinalIgnoreCase) ||
           body.Contains("quota will reset", StringComparison.OrdinalIgnoreCase);

    // "You will regain access on 2026-09-01 at 00:00 UTC."
    private static DateTimeOffset? TryGetRegainAccess(string body)
    {
        var match = RegainAccessPattern.Match(body);
        return match.Success &&
               DateTimeOffset.TryParseExact(
                   $"{match.Groups["date"].Value} {match.Groups["time"].Value}",
                   "yyyy-MM-dd HH:mm",
                   System.Globalization.CultureInfo.InvariantCulture,
                   System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                   out var resetsAt)
            ? resetsAt
            : null;
    }

    private static readonly System.Text.RegularExpressions.Regex RegainAccessPattern = new(
        @"regain access on (?<date>\d{4}-\d{2}-\d{2}) at (?<time>\d{2}:\d{2}) UTC",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    // A rate limit of the API (rate_limit_error, a limit per minute) that does not speak of a quota: the SDK has already
    // waited its retry-after on its own retries, and its exception keeps no header. It is a short limit, not an exhausted
    // quota, which would leave Anthropic for another provider and skip it for 15 minutes.
    private static AgentFailureInfo? AsShortRateLimit(AgentFailureInfo? failure)
        => failure is { Kind: AgentFailureKind.RateLimited, RetryAfterSeconds: null, ResetsAt: null }
            ? failure with { RetryAfterSeconds = UnknownRateLimitRetryAfterSeconds }
            : failure;
    private static (IReadOnlyList<ChatMessage> Messages, ChatOptions Options) MarkCacheBreakpoints(
        AgentTurnRequest request,
        IReadOnlyList<ChatMessage> messages,
        ChatOptions options,
        TimeSpan lifetime)
    {
        // A request that stands alone would pay for a cache write that nothing reads, and a profile can say that
        // the endpoint takes no marker.
        if (request.IsStandalone || request.Provider.Profile?.SupportsCacheControl == false)
        {
            return (messages, options);
        }

        var marked = AnthropicPromptCache.MarkBreakpoints(messages, options, lifetime);
        return (marked.Messages, marked.Options ?? options);
    }

    // A deferred tool of a request that defers tools is sent with defer_loading; one sent to a provider that does not is a
    // tool the conversation loaded, sent as any other.
    private static ChatOptions MarkDeferredTools(AnthropicProviderOptions provider, AgentTurnRequest request, ChatOptions options)
        => DefersTools(provider, request)
            ? AnthropicToolDeferral.MarkDeferred(options, request.Tools.Where(static tool => tool.Spec.Deferred).Select(static tool => tool.Spec.Name).ToHashSet(StringComparer.Ordinal))
            : options;

    private static void ConfigureOptions(AgentTurnRequest request, ChatOptions options)
    {
        // The adaptive thinking client sends the highest effort of the client (ExtraHigh) as the one the model lists, and
        // as max when the session asks for Max, which the client has no level for.
        var highestEffort = request.ReasoningEffort == AgentReasoningEffort.Max ? "max" : ReadHighestEffort(request.ModelInfo);
        if (highestEffort is not null)
        {
            (options.AdditionalProperties ??= [])[AnthropicAdaptiveThinkingChatClient.HighestEffortKey] = highestEffort;
        }

        // A model of another vendor (Kimi for Coding) is sent adaptive thinking where its endpoint lists its efforts.
        if (IsTrue(request.ModelInfo?.Capabilities, AdaptiveThinkingCapabilityKey))
        {
            (options.AdditionalProperties ??= [])[AnthropicAdaptiveThinkingChatClient.AdaptiveThinkingKey] = true;
        }

        if (options.MaxOutputTokens is not null)
        {
            return;
        }

        var tokenBudget = AgentTokenBudgetResolver.Resolve(
            request.ModelInfo,
            request.Provider.Compaction ?? AgentCompactionSettings.Default);
        if (tokenBudget.MaxOutputTokens is > 0)
        {
            options.MaxOutputTokens = (int)Math.Min(tokenBudget.MaxOutputTokens.Value, int.MaxValue);
            return;
        }

        var modelId = string.IsNullOrWhiteSpace(request.ModelId) ? "<unknown>" : request.ModelId;
        throw new AgentTurnExecutionException(new AgentTurnFailure(
            $"Anthropic model '{modelId}' does not declare a maximum output-token limit. " +
            "Configure output_token_limit in the provider's model_overrides settings.",
            IsContextOverflow: false));
    }

    private static ValueTask<IChatClient> CreateChatClientAsync(
        AnthropicProviderOptions provider,
        ModelProviderRuntimeDescriptor providerDescriptor,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var useStreamingCompatibilityFallback = ShouldUseStreamingCompatibilityFallback(providerDescriptor);

        if (provider.ChatClientFactory is not null)
        {
            var claudeApi = IsClaudeApi(provider.BaseUri ?? providerDescriptor.BaseUri);
            return ValueTask.FromResult(
                WrapChatClient(
                    provider.ChatClientFactory(),
                    providerDescriptor,
                    useStreamingCompatibilityFallback,
                    claudeApi,
                    claudeApi && provider.RefusalFallback));
        }

        var client = CreateSdkClient(provider, providerDescriptor);
        // Adaptive thinking, with its output_config.effort, is what AnthropicAdaptiveThinkingChatClient gives the models
        // that take it. Every other model gets the thinking budget of the effort (thinking.type=enabled): older models
        // reject adaptive thinking and an effort, which the SDK sends by default.
        var chatClient = client.AsIChatClient(defaultModelId: null, defaultMaxOutputTokens: null, AnthropicThinkingMode.Extended);
        if (provider.HttpClient is null)
        {
            chatClient = new OwnedChatClient(chatClient, client);
        }

        // The SDK resolves the endpoint: the provider's URL, else ANTHROPIC_BASE_URL, else the Claude API.
        var isClaudeApi = IsClaudeApi(new Uri(client.BaseUrl));
        return ValueTask.FromResult<IChatClient>(
            WrapChatClient(chatClient, providerDescriptor, useStreamingCompatibilityFallback, isClaudeApi, isClaudeApi && provider.RefusalFallback));
    }

    // Only the Claude API is known to take the thinking block binding control: an Anthropic-compatible endpoint
    // (Copilot, MiniMax, a gateway) may refuse the field or the beta.
    internal static bool IsClaudeApi(Uri? baseUri)
        => baseUri is null || HasHost(baseUri, "api.anthropic.com");

    private static async Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(
        AnthropicProviderOptions provider,
        ModelProviderRuntimeDescriptor providerDescriptor,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<AgentModelInfo> models;
        if (provider.ModelListAsync is not null)
        {
            models = await provider.ModelListAsync(cancellationToken).ConfigureAwait(false);
            return AgentModelMetadataEnricher.EnrichModels(
                models,
                provider.ModelCatalog,
                provider.ModelsDevProviderId,
                provider.ModelOverrides,
                provider.ModelsIncludeRegex);
        }

        if (!string.IsNullOrWhiteSpace(provider.SingleModelId))
        {
            models =
            [
                CreateSingleModelInfo(provider.SingleModelId, providerDescriptor),
            ];
            return AgentModelMetadataEnricher.EnrichModels(
                models,
                provider.ModelCatalog,
                provider.ModelsDevProviderId,
                provider.ModelOverrides,
                provider.ModelsIncludeRegex);
        }

        var client = CreateSdkClient(provider, providerDescriptor);
        try
        {
            var results = new List<AgentModelInfo>();
            var page = await client.Models.List(cancellationToken: cancellationToken).ConfigureAwait(false);
            while (true)
            {
                // A retired model fails every request: the API lists it only when asked for, and it is not offered.
                results.AddRange(page.Items
                    .Where(static model => ReadLifecycle(model.RawData) != "retired")
                    .Select(model => ToAgentModelInfo(providerDescriptor, model)));
                if (!page.HasNext())
                {
                    break;
                }

                page = await page.Next(cancellationToken).ConfigureAwait(false);
            }

            models = results;
        }
        finally
        {
            if (provider.HttpClient is null)
            {
                client.Dispose();
            }
        }

        return AgentModelMetadataEnricher.EnrichModels(
            models,
            provider.ModelCatalog,
            provider.ModelsDevProviderId,
            provider.ModelOverrides,
            provider.ModelsIncludeRegex);
    }

    private static AnthropicClient CreateSdkClient(
        AnthropicProviderOptions provider,
        ModelProviderRuntimeDescriptor providerDescriptor)
    {
        var options = new ClientOptions
        {
            ApiKey = provider.ApiKey,
            ExtraHeaders = provider.ExtraHeaders,
        };
        if (!string.IsNullOrEmpty(provider.AuthToken))
        {
            // Route bearer tokens through the SDK's Credentials pipeline so the Authorization
            // header is set via the HttpRequestHeaders.Authorization setter (which accepts
            // tokens containing characters such as ';' or ',' that GitHub Copilot tokens
            // include) instead of the validating Headers.Add path used by ClientOptions.AuthToken.
            options.Credentials = new StaticTokenCredentials(provider.AuthToken);
        }
        if (provider.HttpClient is not null)
        {
            options.HttpClient = provider.HttpClient;
        }
        // The tool references of a request are rewritten into tool_reference blocks first, and the headers of another vendor's
        // endpoint are those it took before the SDK's version 13, before any handler of the provider.
        options.Handlers = provider.HttpHandlerFactory is null
            ? [new AnthropicToolDeferral.ToolReferenceHandler(), new AnthropicCompatibleEndpointHandler(provider.ApiKey)]
            : [new AnthropicToolDeferral.ToolReferenceHandler(), new AnthropicCompatibleEndpointHandler(provider.ApiKey), provider.HttpHandlerFactory()];

        if (provider.BaseUri is not null)
        {
            options.BaseUrl = provider.BaseUri.ToString();
        }

        return new AnthropicClient(options);
    }

    private static IChatClient WrapChatClient(
        IChatClient chatClient,
        ModelProviderRuntimeDescriptor providerDescriptor,
        bool useStreamingCompatibilityFallback,
        bool isClaudeApi,
        bool refusalFallback)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(providerDescriptor);

        var adaptiveThinkingChatClient = new AnthropicAdaptiveThinkingChatClient(chatClient, dropMismatchedThinking: isClaudeApi, refusalFallback);
        if (!useStreamingCompatibilityFallback)
        {
            return adaptiveThinkingChatClient;
        }

        LogInfo(
            $"Using Anthropic streaming compatibility fallback provider={providerDescriptor.ProviderKey} displayName={providerDescriptor.DisplayName}");
        return new AnthropicStreamingCompatibilityChatClient(adaptiveThinkingChatClient);
    }

    private static bool ShouldUseStreamingCompatibilityFallback(ModelProviderRuntimeDescriptor providerDescriptor)
    {
        ArgumentNullException.ThrowIfNull(providerDescriptor);

        return HasHost(providerDescriptor.BaseUri, "minimax.io") ||
            HasHost(providerDescriptor.BaseUri, "minimaxi.com");
    }

    private static bool HasHost(Uri? baseUri, string expectedHost)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedHost);

        var host = baseUri?.Host;
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        return host.Equals(expectedHost, StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith($".{expectedHost}", StringComparison.OrdinalIgnoreCase);
    }

    private static AgentModelInfo ToAgentModelInfo(
        ModelProviderRuntimeDescriptor provider,
        ModelInfo model)
    {
        var capabilities = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["createdAt"] = model.CreatedAt,
            ["maxInputTokens"] = model.MaxInputTokens,
            ["maxTokens"] = model.MaxTokens,
        };
        var efforts = ReadReasoningEfforts(model.RawData);
        if (efforts is not null)
        {
            capabilities[AgentReasoningEffortCapability.CompleteListKey] = true;
        }

        if (ReadHighestEffort(model.RawData) is { } highestEffort)
        {
            capabilities[HighestEffortCapabilityKey] = highestEffort;
        }

        if (TakesAdaptiveThinking(model.RawData))
        {
            capabilities[AdaptiveThinkingCapabilityKey] = true;
        }

        // A deprecated model still runs until it retires: it is marked with the status models.dev gives a model.
        if (ReadLifecycle(model.RawData) == "deprecated")
        {
            capabilities["status"] = "deprecated";
            if (ReadDate(model.RawData, "retires_at") is { } retiresAt)
            {
                capabilities["retiresAt"] = retiresAt;
            }
        }

        // Kimi for Coding says so with supports_image_in.
        if ((ReadImageInput(model.RawData) ?? ReadFlag(model.RawData, "supports_image_in")) is { } imageInput)
        {
            capabilities["supportsImageInput"] = imageInput;
        }

        // The context Kimi for Coding gives the model depends on the plan of the account (262,144 or 1,048,576 tokens
        // for K3.1 Pro): models.dev cannot know it.
        if (model.RawData.TryGetValue("context_length", out var contextLength) &&
            contextLength.ValueKind == JsonValueKind.Number &&
            contextLength.TryGetInt64(out var contextTokens) && contextTokens > 0)
        {
            capabilities["contextWindow"] = contextTokens;
            capabilities["contextWindowTokens"] = contextTokens;
        }

        if (ReadSupported(model.RawData, "pdf_input") is { } pdfInput)
        {
            capabilities[AgentPdfInputs.CapabilityKey] = pdfInput;
        }

        return new AgentModelInfo(
            model.ID,
            DisplayName: model.DisplayName,
            Description: null,
            Provider: provider.ProviderKey,
            DefaultReasoningEffort: ReadDefaultEffort(model.RawData),
            SupportedReasoningEfforts: efforts,
            Capabilities: capabilities);
    }

    // The efforts a model takes, from the capabilities of the Models API:
    // {"effort":{"supported":true,"low":{"supported":true},"medium":{...},"high":{...},"xhigh":{...},"max":{...}}},
    // or from the reasoning the models of Kimi for Coding describe:
    // {"reasoning":{"type":"only","effort":{"valid":["low","high","max"],"default":"high"}}}.
    // Null when the endpoint says nothing of them (another Anthropic-compatible endpoint, an older API).
    internal static IReadOnlyList<AgentReasoningEffort>? ReadReasoningEfforts(IReadOnlyDictionary<string, JsonElement> rawData)
    {
        if (!TryGetEffortCapability(rawData, out var capabilities, out var effort))
        {
            // A model whose reasoning names no effort (Kimi's K2.7 Code Highspeed) thinks as it does, and is sent none.
            return TryGetReasoning(rawData, out var reasoning)
                ? ReadListedEffortLevels(reasoning) is { } listed ? ToEfforts(listed) : []
                : null;
        }

        if (!IsSupported(effort))
        {
            // A model that thinks with a budget (thinking.type=enabled), such as Claude Haiku 4.5 and Sonnet 4.5, takes
            // no effort: the client sends the budget each effort stands for (1024, 8192, 16384 or 32768 tokens, below
            // max_tokens) instead.
            return TakesThinkingBudget(capabilities)
                ? [AgentReasoningEffort.Low, AgentReasoningEffort.Medium, AgentReasoningEffort.High, AgentReasoningEffort.XHigh]
                : [];
        }

        return ToEfforts(ReadSupportedEffortLevels(effort));

        // {"thinking":{"supported":true,"types":{"enabled":{"supported":true}}}}
        static bool TakesThinkingBudget(JsonElement capabilities)
            => capabilities.TryGetProperty("thinking", out var thinking) && IsSupported(thinking) &&
                thinking.TryGetProperty("types", out var types) && types.ValueKind == JsonValueKind.Object &&
                IsSupported(types, "enabled");
    }

    // The effort the highest effort of a session is sent as: "xhigh" where the model lists it, else "max" where it lists
    // that. Null when the endpoint says neither.
    internal static string? ReadHighestEffort(IReadOnlyDictionary<string, JsonElement> rawData)
        => ReadEffortLevels(rawData) is not { } levels ? null
            : levels.Contains("xhigh") ? "xhigh"
            : levels.Contains("max") ? "max"
            : null;

    // The effort a model thinks with when it is sent none, where the endpoint names it (Kimi for Coding does).
    internal static AgentReasoningEffort? ReadDefaultEffort(IReadOnlyDictionary<string, JsonElement> rawData)
    {
        if (!TryGetReasoning(rawData, out var reasoning) ||
            ReadListedEffortLevels(reasoning) is not { } levels ||
            !reasoning.TryGetProperty("effort", out var effort) ||
            !effort.TryGetProperty("default", out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var name = value.GetString()?.Trim().ToLowerInvariant();
        return name switch
        {
            "low" => AgentReasoningEffort.Low,
            "medium" => AgentReasoningEffort.Medium,
            "high" => AgentReasoningEffort.High,
            "xhigh" => AgentReasoningEffort.XHigh,
            "max" => levels.Contains("xhigh") ? AgentReasoningEffort.Max : AgentReasoningEffort.XHigh,
            _ => null,
        };
    }

    // Whether the model takes adaptive thinking with output_config.effort: a model of the Models API that lists both,
    // or a model of Kimi for Coding that lists its efforts (its endpoint takes them as the Claude API does).
    internal static bool TakesAdaptiveThinking(IReadOnlyDictionary<string, JsonElement> rawData)
    {
        if (TryGetEffortCapability(rawData, out var capabilities, out var effort))
        {
            return IsSupported(effort) &&
                capabilities.TryGetProperty("thinking", out var thinking) && IsSupported(thinking) &&
                thinking.TryGetProperty("types", out var types) && types.ValueKind == JsonValueKind.Object &&
                IsSupported(types, "adaptive");
        }

        return TryGetReasoning(rawData, out var reasoning) && ReadListedEffortLevels(reasoning) is { Count: > 0 };
    }

    // XHigh is sent as xhigh where the model takes it, else as max (AnthropicAdaptiveThinkingChatClient); Max is an
    // effort of its own only where the model takes both.
    private static List<AgentReasoningEffort> ToEfforts(IReadOnlySet<string> levels)
    {
        var efforts = new List<AgentReasoningEffort>();
        if (levels.Contains("low")) efforts.Add(AgentReasoningEffort.Low);
        if (levels.Contains("medium")) efforts.Add(AgentReasoningEffort.Medium);
        if (levels.Contains("high")) efforts.Add(AgentReasoningEffort.High);
        if (levels.Contains("xhigh") || levels.Contains("max")) efforts.Add(AgentReasoningEffort.XHigh);
        if (levels.Contains("xhigh") && levels.Contains("max")) efforts.Add(AgentReasoningEffort.Max);
        return efforts;
    }

    // The effort levels the endpoint lists for the model, in either form; null when it lists none.
    private static IReadOnlySet<string>? ReadEffortLevels(IReadOnlyDictionary<string, JsonElement> rawData)
        => TryGetEffortCapability(rawData, out _, out var effort) ? ReadSupportedEffortLevels(effort)
            : TryGetReasoning(rawData, out var reasoning) ? ReadListedEffortLevels(reasoning)
            : null;

    private static bool TryGetEffortCapability(IReadOnlyDictionary<string, JsonElement> rawData, out JsonElement capabilities, out JsonElement effort)
    {
        effort = default;
        return rawData.TryGetValue("capabilities", out capabilities) &&
            capabilities.ValueKind == JsonValueKind.Object &&
            capabilities.TryGetProperty("effort", out effort) &&
            effort.ValueKind == JsonValueKind.Object;
    }

    // {"low":{"supported":true},"high":{"supported":false},...}
    private static HashSet<string> ReadSupportedEffortLevels(JsonElement effort)
    {
        var levels = new HashSet<string>(StringComparer.Ordinal);
        foreach (var level in (ReadOnlySpan<string>)["low", "medium", "high", "xhigh", "max"])
        {
            if (IsSupported(effort, level))
            {
                levels.Add(level);
            }
        }

        return levels;
    }

    // The reasoning of a model of Kimi for Coding: {"type":"only","effort":{"valid":[...],"default":"..."}}.
    private static bool TryGetReasoning(IReadOnlyDictionary<string, JsonElement> rawData, out JsonElement reasoning)
        => rawData.TryGetValue("reasoning", out reasoning) && reasoning.ValueKind == JsonValueKind.Object;

    // {"effort":{"valid":["low","high","max"]}}; null when the reasoning names no effort.
    private static HashSet<string>? ReadListedEffortLevels(JsonElement reasoning)
    {
        if (!reasoning.TryGetProperty("effort", out var effort) ||
            effort.ValueKind != JsonValueKind.Object ||
            !effort.TryGetProperty("valid", out var valid) ||
            valid.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var levels = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in valid.EnumerateArray())
        {
            if (value.ValueKind == JsonValueKind.String && value.GetString() is { } level && !string.IsNullOrWhiteSpace(level))
            {
                levels.Add(level.Trim().ToLowerInvariant());
            }
        }

        return levels;
    }

    // {"supported":true}, or the level of that name in it.
    private static bool IsSupported(JsonElement element, string? level = null)
    {
        if (level is not null && !element.TryGetProperty(level, out element))
        {
            return false;
        }

        return element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty("supported", out var supported) &&
            supported.ValueKind == JsonValueKind.True;
    }

    // "active", "deprecated" or "retired"; null when the endpoint says nothing of it (an Anthropic-compatible endpoint).
    private static string? ReadLifecycle(IReadOnlyDictionary<string, JsonElement> rawData)
        => rawData.TryGetValue("lifecycle", out var lifecycle) && lifecycle.ValueKind == JsonValueKind.String
            ? lifecycle.GetString()
            : null;

    private static DateTimeOffset? ReadDate(IReadOnlyDictionary<string, JsonElement> rawData, string name)
        => rawData.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(value.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var date)
                ? date
                : null;

    // A model the API does not serve (404 not_found_error naming the model): retired, or not open to the account.
    internal static string? DescribeFailure(Exception exception, AgentTurnRequest request)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is global::Anthropic.Exceptions.AnthropicApiException { StatusCode: System.Net.HttpStatusCode.RequestEntityTooLarge })
            {
                return "The request is larger than the 32 MB the Anthropic API takes: the images and PDF documents of the conversation " +
                    "are sent with it. Compact the session, or go on in a new one without the largest attachments.";
            }

            if (current is global::Anthropic.Exceptions.AnthropicNotFoundException notFound &&
                (notFound.ResponseBody ?? string.Empty).Contains("model", StringComparison.OrdinalIgnoreCase))
            {
                var model = string.IsNullOrWhiteSpace(request.ModelId) ? "of this session" : $"'{request.ModelId}'";
                return $"Anthropic does not serve the model {model}: it is retired, or not open to this account. Choose another model for this session.";
            }
        }

        return null;
    }

    // The highest effort a listed model recorded; a capability read back from a saved list may be a JSON string.
    private static string? ReadHighestEffort(AgentModelInfo? model)
    {
        if (model?.Capabilities?.TryGetValue(HighestEffortCapabilityKey, out var value) != true)
        {
            return null;
        }

        var effort = value switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => null,
        };
        return effort is "xhigh" or "max" ? effort : null;
    }

    private static bool? ReadFlag(IReadOnlyDictionary<string, JsonElement> rawData, string name)
        => rawData.TryGetValue(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.ValueKind == JsonValueKind.True
            : null;

    // A flag of a listed model; one read back from a saved list may be a JSON value.
    private static bool IsTrue(IReadOnlyDictionary<string, object?>? capabilities, string key)
        => capabilities?.TryGetValue(key, out var value) == true &&
            value is true or JsonElement { ValueKind: JsonValueKind.True };

    // Whether a model takes images, from the capabilities of the Models API: {"image_input":{"supported":true}}.
    // Null when the endpoint says nothing of it (an Anthropic-compatible endpoint, an older API).
    internal static bool? ReadImageInput(IReadOnlyDictionary<string, JsonElement> rawData)
        => ReadSupported(rawData, "image_input");

    // A capability of the Models API: {"<name>":{"supported":true}}. Null when the endpoint says nothing of it.
    private static bool? ReadSupported(IReadOnlyDictionary<string, JsonElement> rawData, string name)
    {
        if (!rawData.TryGetValue("capabilities", out var capabilities) ||
            capabilities.ValueKind != JsonValueKind.Object ||
            !capabilities.TryGetProperty(name, out var capability) ||
            capability.ValueKind != JsonValueKind.Object ||
            !capability.TryGetProperty("supported", out var supported))
        {
            return null;
        }

        return supported.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    private static void LogInfo(string message)
    {
        Logger.Info(message);
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
}
