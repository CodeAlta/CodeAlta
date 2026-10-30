using System.Net.Http.Headers;
using System.Text.Json;
using CodeAlta.Agent.Runtime;
using CodeAlta.Agent.Runtime.Compaction;
using CodeAlta.Agent.ModelCatalog;
using Microsoft.Extensions.AI;

using IChatClient = Microsoft.Extensions.AI.IChatClient;

namespace CodeAlta.Agent.Mistral;

/// <summary>
/// Mistral chat model-provider runtime.
/// </summary>
public sealed class MistralModelProviderRuntime : IAgentModelProviderRuntime
{
    private readonly IAgentModelProviderRuntime _runtime;

    /// <summary>
    /// Initializes a new instance of the <see cref="MistralModelProviderRuntime"/> class.
    /// </summary>
    /// <param name="options">The provider runtime options.</param>
    public MistralModelProviderRuntime(MistralModelProviderRuntimeOptions options)
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

    private static AgentModelProviderRuntime CreateProviderRuntime(MistralProviderOptions provider)
    {
        var providerKey = provider.ProviderKey.Trim();
        var displayName = string.IsNullOrWhiteSpace(provider.DisplayName) ? providerKey : provider.DisplayName.Trim();
        var responses = provider.Api == MistralApi.Responses;
        var runtimeDescriptor = new ModelProviderRuntimeDescriptor
        {
            ProtocolFamily = responses ? MistralResponsesTurnExecutor.ProtocolFamily : "mistral-chat",
            ProviderKey = providerKey,
            DisplayName = displayName,
            TransportKind = responses ? AgentTransportKind.OpenAIResponses : AgentTransportKind.MistralChat,
            BaseUri = provider.BaseUri,
            IsDefault = provider.IsDefault,
            Profile = provider.Profile ?? (responses ? MistralResponsesTurnExecutor.DefaultProfile : new AgentProviderProfile
            {
                SupportsDeveloperRole = true,
                SupportsStore = false,
                StreamsUsage = true,
                MaxTokensFieldName = "max_tokens",
            }),
            Compaction = provider.Compaction ?? AgentCompactionSettings.Default,
        };
        var descriptor = new ModelProviderDescriptor(new ModelProviderId(providerKey), displayName, "mistral")
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

    internal static IModelProviderTurnExecutor CreateTurnExecutor(MistralProviderOptions provider)
    {
        if (provider.Api == MistralApi.Responses)
        {
            return new MistralResponsesTurnExecutor(provider);
        }

        return new ChatClientTurnExecutor(
            (providerDescriptor, cancellationToken) => CreateChatClientAsync(provider, providerDescriptor, cancellationToken),
            (providerDescriptor, cancellationToken) => ListModelsAsync(provider, providerDescriptor, cancellationToken),
            SupportsMistralReasoningEffort,
            ConfigureMistralChatOptions,
            classifyFailure: ClassifyFailure);
    }

    // The message of a 429 that CodeAlta writes speaks of a rate limit "or quota": the kind comes from the status, the
    // Retry-After and the body of the response instead.
    internal static AgentFailureInfo? ClassifyFailure(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is not MistralApiException api)
            {
                continue;
            }

            var body = string.Join(' ', api.ResponseBody, api.ErrorType, api.ErrorCode);
            int? retryAfterSeconds = api.RetryAfter is { } delay ? (int)Math.Ceiling(delay.TotalSeconds) : null;
            DateTimeOffset? resetsAt = api.RetryAfter is { } wait ? DateTimeOffset.UtcNow + wait : null;
            if (api.ErrorType is "invalid_model")
            {
                return new AgentFailureInfo(AgentFailureKind.ModelUnavailable);
            }

            if (api.StatusCode is { } status)
            {
                return (int)status == 429
                    ? new AgentFailureInfo(
                        AgentFailureClassifier.IsQuotaMessage(body) ? AgentFailureKind.QuotaExhausted : AgentFailureKind.RateLimited,
                        resetsAt,
                        retryAfterSeconds)
                    : AgentFailureClassifier.FromHttpStatus((int)status, body, retryAfterSeconds, resetsAt);
            }

            // An error in the stream, after the answer started: a limit when it says so, else the service failed.
            return body.Contains("rate_limit", StringComparison.OrdinalIgnoreCase) || body.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
                ? new AgentFailureInfo(AgentFailureKind.RateLimited, resetsAt, retryAfterSeconds)
                : AgentFailureClassifier.IsQuotaMessage(body)
                    ? new AgentFailureInfo(AgentFailureKind.QuotaExhausted, resetsAt, retryAfterSeconds)
                    : new AgentFailureInfo(AgentFailureKind.Unavailable);
        }

        return AgentFailureClassifier.Classify(exception);
    }

    private static async ValueTask<IChatClient> CreateChatClientAsync(
        MistralProviderOptions provider,
        ModelProviderRuntimeDescriptor providerDescriptor,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (provider.ChatClientFactory is not null)
        {
            return provider.ChatClientFactory();
        }

        return new MistralChatClient(provider, await provider.ResolveApiKeyAsync(cancellationToken).ConfigureAwait(false));
    }

    internal static async Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(
        MistralProviderOptions provider,
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

        models = await ListRemoteModelsAsync(provider, providerDescriptor, cancellationToken).ConfigureAwait(false);

        return CompleteReasoningEfforts(AgentModelMetadataEnricher.EnrichModels(
            models,
            provider.ModelCatalog,
            provider.ModelsDevProviderId,
            provider.ModelOverrides,
            provider.ModelsIncludeRegex));
    }

    private static async Task<IReadOnlyList<AgentModelInfo>> ListRemoteModelsAsync(
        MistralProviderOptions provider,
        ModelProviderRuntimeDescriptor providerDescriptor,
        CancellationToken cancellationToken)
    {
        var apiKey = await provider.ResolveApiKeyAsync(cancellationToken).ConfigureAwait(false);
        var httpClient = provider.HttpClient ?? new HttpClient();
        var disposeHttpClient = provider.HttpClient is null;
        try
        {
            using var response = await SendModelsRequestWithRetriesAsync(httpClient, provider, apiKey, cancellationToken).ConfigureAwait(false);

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            return provider.UsesVibeModelCatalog
                ? ParseVibeModelCatalog(document.RootElement, providerDescriptor)
                : ParseModelsResponse(document.RootElement, providerDescriptor);
        }
        finally
        {
            if (disposeHttpClient)
            {
                httpClient.Dispose();
            }
        }
    }

    private static Uri CreateModelsUri(MistralProviderOptions provider)
    {
        var baseUri = provider.BaseUri ?? provider.HttpClient?.BaseAddress ?? new Uri(MistralDefaults.DefaultBaseUrl);
        var path = provider.UsesVibeModelCatalog ? MistralDefaults.VibeModelCatalogPath : MistralDefaults.ModelsPath;
        return new Uri($"{baseUri.AbsoluteUri.TrimEnd('/')}{path}", UriKind.Absolute);
    }

    private static async Task<HttpResponseMessage> SendModelsRequestWithRetriesAsync(
        HttpClient httpClient,
        MistralProviderOptions provider,
        string? apiKey,
        CancellationToken cancellationToken)
    {
        var maxAttempts = MistralChatClient.GetMaxRetryAttempts(provider);
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, CreateModelsUri(provider));
            ConfigureRequestHeaders(request, provider, apiKey);
            HttpResponseMessage? response = null;
            try
            {
                response = await httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    return response;
                }

                var exception = await MistralChatClient.CreateApiExceptionAsync(response, "model listing", cancellationToken).ConfigureAwait(false);
                response.Dispose();
                response = null;
                if (!MistralChatClient.ShouldRetry(exception, attempt, maxAttempts))
                {
                    throw exception;
                }

                await MistralChatClient.DelayBeforeRetryAsync(provider, exception, attempt, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (MistralChatClient.ShouldRetry(ex, attempt, maxAttempts))
            {
                response?.Dispose();
                await MistralChatClient.DelayBeforeRetryAsync(provider, ex, attempt, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                response?.Dispose();
                throw;
            }
        }
    }

    private static void ConfigureRequestHeaders(HttpRequestMessage request, MistralProviderOptions provider, string? apiKey)
    {
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        if (provider.ExtraHeaders is { Count: > 0 } extraHeaders)
        {
            foreach (var pair in extraHeaders)
            {
                request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
            }
        }
    }

    private static IReadOnlyList<AgentModelInfo> ParseModelsResponse(
        JsonElement root,
        ModelProviderRuntimeDescriptor providerDescriptor)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return [];
        }

        if (data.ValueKind is not JsonValueKind.Array)
        {
            throw new JsonException("Mistral model response data must be an array.");
        }

        var results = new List<AgentModelInfo>();
        var seenModelIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind is not JsonValueKind.Object ||
                !TryGetString(item, "id", out var id) ||
                string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var modelId = id.Trim();
            if (seenModelIds.Add(modelId))
            {
                results.Add(ToAgentModelInfo(providerDescriptor, item, modelId));
            }
        }

        return results;
    }

    // The models Mistral Vibe offers, with the reasoning efforts each takes. Vibe calls only models that chat and call
    // tools; the others are left out.
    private static IReadOnlyList<AgentModelInfo> ParseVibeModelCatalog(
        JsonElement root,
        ModelProviderRuntimeDescriptor providerDescriptor)
    {
        if (!root.TryGetProperty("models", out var models) || models.ValueKind is not JsonValueKind.Array)
        {
            throw new JsonException("The model catalog of Mistral Vibe must have an array of models.");
        }

        var results = new List<AgentModelInfo>();
        var seenModelIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in models.EnumerateArray())
        {
            if (item.ValueKind is not JsonValueKind.Object ||
                !TryGetString(item, "id", out var id) ||
                string.IsNullOrWhiteSpace(id) ||
                !seenModelIds.Add(id.Trim()) ||
                !TryGetProperty(item, out var flags, "capabilities") ||
                flags.ValueKind is not JsonValueKind.Object ||
                TryGetScalar(flags, "completion_chat") is not true ||
                TryGetScalar(flags, "function_calling") is not true)
            {
                continue;
            }

            var modelId = id.Trim();
            IReadOnlyList<AgentReasoningEffort> efforts = TryGetProperty(item, out var parameters, "parameters") &&
                parameters.ValueKind is JsonValueKind.Object &&
                parameters.TryGetProperty("reasoning", out var reasoning)
                    ? ParseReasoningEffortArray(reasoning)
                    : [];
            var label = TryGetTrimmedString(item, "label");
            results.Add(new AgentModelInfo(
                modelId,
                DisplayName: label ?? modelId,
                Description: TryGetTrimmedString(item, "description"),
                Provider: providerDescriptor.ProviderKey,
                DefaultReasoningEffort: ResolveMistralDefaultReasoningEffort(efforts),
                SupportedReasoningEfforts: efforts,
                Capabilities: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["capabilities"] = CreateCapabilities(flags),
                    ["maxContextLength"] = TryGetScalar(item, "max_context_length"),
                    ["ownedBy"] = TryGetTrimmedString(item, "publisher"),
                }));
        }

        return results;
    }

    private static AgentModelInfo ToAgentModelInfo(
        ModelProviderRuntimeDescriptor provider,
        JsonElement model,
        string modelId)
    {
        var capabilities = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["capabilities"] = TryGetProperty(model, out var modelCapabilities, "capabilities") &&
                modelCapabilities.ValueKind is JsonValueKind.Object
                    ? CreateCapabilities(modelCapabilities)
                    : null,
            ["maxContextLength"] = TryGetScalar(model, "max_context_length", "maxContextLength"),
            ["ownedBy"] = TryGetTrimmedString(model, "owned_by", "ownedBy"),
            ["created"] = TryGetScalar(model, "created"),
        };

        var supportsReasoning = TryGetCapabilityBoolean(model, "reasoning");
        // A model that reasons and is not known has no efforts here: models.dev gives them, else the default ones
        // (CompleteReasoningEfforts).
        var supportedReasoningEfforts = TryGetSupportedReasoningEfforts(model, modelId, supportsReasoning);
        var name = TryGetTrimmedString(model, "name");
        return new AgentModelInfo(
            modelId,
            DisplayName: string.IsNullOrWhiteSpace(name) ? modelId : name,
            Description: TryGetTrimmedString(model, "description"),
            Provider: provider.ProviderKey,
            DefaultReasoningEffort: ResolveMistralDefaultReasoningEffort(supportedReasoningEfforts),
            SupportedReasoningEfforts: supportedReasoningEfforts,
            Capabilities: capabilities);
    }

    private static IReadOnlyDictionary<string, object?> CreateCapabilities(JsonElement capabilities)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["completionChat"] = TryGetScalar(capabilities, "completion_chat", "completionChat"),
            ["functionCalling"] = TryGetScalar(capabilities, "function_calling", "functionCalling"),
            ["reasoning"] = TryGetScalar(capabilities, "reasoning"),
            ["completionFim"] = TryGetScalar(capabilities, "completion_fim", "completionFim"),
            ["fineTuning"] = TryGetScalar(capabilities, "fine_tuning", "fineTuning"),
            ["vision"] = TryGetScalar(capabilities, "vision"),
            ["ocr"] = TryGetScalar(capabilities, "ocr"),
            ["classification"] = TryGetScalar(capabilities, "classification"),
            ["moderation"] = TryGetScalar(capabilities, "moderation"),
            ["audio"] = TryGetScalar(capabilities, "audio"),
            ["audioTranscription"] = TryGetScalar(capabilities, "audio_transcription", "audioTranscription"),
            ["audioTranscriptionRealtime"] = TryGetScalar(capabilities, "audio_transcription_realtime", "audioTranscriptionRealtime"),
            ["audioSpeech"] = TryGetScalar(capabilities, "audio_speech", "audioSpeech"),
        };

        return values;
    }

    private static bool SupportsMistralReasoningEffort(
        AgentTurnRequest request,
        AgentReasoningEffort reasoningEffort)
        => reasoningEffort is AgentReasoningEffort.High;

    // Microsoft.Extensions.AI names no minimal and no max, and leaves out none: the value of Mistral goes as it is, for
    // an effort the model takes. None too, so that a model that reasons by default does not.
    private static void ConfigureMistralChatOptions(AgentTurnRequest request, ChatOptions options)
    {
        if (MistralReasoningEfforts.Resolve(request) is not { } effort)
        {
            return;
        }

        options.Reasoning = null;
        options.AdditionalProperties ??= [];
        options.AdditionalProperties["reasoning_effort"] = effort;
    }

    private static AgentReasoningEffort? ResolveMistralDefaultReasoningEffort(
        IReadOnlyList<AgentReasoningEffort>? supportedReasoningEfforts)
    {
        if (supportedReasoningEfforts is not { Count: > 0 })
        {
            return null;
        }

        if (supportedReasoningEfforts.Contains(AgentReasoningEffort.High))
        {
            return AgentReasoningEffort.High;
        }

        foreach (var effort in supportedReasoningEfforts)
        {
            if (effort != AgentReasoningEffort.None)
            {
                return effort;
            }
        }

        return null;
    }

    private static IReadOnlyList<AgentReasoningEffort>? TryGetSupportedReasoningEfforts(
        JsonElement model,
        string modelId,
        bool? supportsReasoning)
    {
        if (TryGetReasoningEffortArray(model, out var values))
        {
            return values;
        }

        return supportsReasoning switch
        {
            null => null,
            false => [],
            true => MistralReasoningEfforts.Find(modelId, TryGetAliases(model)),
        };
    }

    private static IEnumerable<string> TryGetAliases(JsonElement model)
    {
        if (!model.TryGetProperty("aliases", out var aliases) || aliases.ValueKind is not JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var alias in aliases.EnumerateArray())
        {
            if (alias.ValueKind is JsonValueKind.String && alias.GetString() is { Length: > 0 } value)
            {
                yield return value;
            }
        }
    }

    // A model that reasons and has no efforts yet, neither from the endpoint nor from models.dev, gets the default ones;
    // its default effort follows the efforts it has.
    internal static IReadOnlyList<AgentModelInfo> CompleteReasoningEfforts(IReadOnlyList<AgentModelInfo> models)
    {
        var completed = new AgentModelInfo[models.Count];
        for (var index = 0; index < models.Count; index++)
        {
            var model = models[index];
            var efforts = model.SupportedReasoningEfforts;
            if (efforts is null &&
                model.Capabilities?.TryGetValue("capabilities", out var listed) == true &&
                listed is IReadOnlyDictionary<string, object?> flags &&
                flags.TryGetValue("reasoning", out var reasons) && reasons is true)
            {
                efforts = MistralReasoningEfforts.Default;
            }

            completed[index] = efforts == model.SupportedReasoningEfforts && model.DefaultReasoningEffort is not null
                ? model
                : model with
                {
                    SupportedReasoningEfforts = efforts,
                    DefaultReasoningEffort = model.DefaultReasoningEffort ?? ResolveMistralDefaultReasoningEffort(efforts),
                };
        }

        return completed;
    }

    private static bool TryGetReasoningEffortArray(
        JsonElement model,
        out IReadOnlyList<AgentReasoningEffort> efforts)
    {
        if (TryGetReasoningEffortProperty(model, out var value))
        {
            efforts = ParseReasoningEffortArray(value);
            return true;
        }

        efforts = [];
        return false;
    }

    private static bool TryGetReasoningEffortProperty(JsonElement model, out JsonElement value)
    {
        if (TryGetProperty(
            model,
            out value,
            "supported_reasoning_efforts",
            "supportedReasoningEfforts",
            "reasoning_efforts",
            "reasoningEfforts"))
        {
            return true;
        }

        if (TryGetProperty(model, out var capabilities, "capabilities") &&
            capabilities.ValueKind is JsonValueKind.Object &&
            TryGetProperty(
                capabilities,
                out value,
                "supported_reasoning_efforts",
                "supportedReasoningEfforts",
                "reasoning_efforts",
                "reasoningEfforts"))
        {
            return true;
        }

        value = default;
        return false;
    }

    private static IReadOnlyList<AgentReasoningEffort> ParseReasoningEffortArray(JsonElement value)
    {
        if (value.ValueKind is not JsonValueKind.Array)
        {
            return [];
        }

        var efforts = new List<AgentReasoningEffort>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind is JsonValueKind.String &&
                TryParseMistralReasoningEffort(item.GetString(), out var effort) &&
                !efforts.Contains(effort))
            {
                efforts.Add(effort);
            }
        }

        return efforts;
    }

    private static bool TryParseMistralReasoningEffort(string? value, out AgentReasoningEffort effort)
    {
        effort = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "none":
                effort = AgentReasoningEffort.None;
                return true;
            case "minimal":
                effort = AgentReasoningEffort.Minimal;
                return true;
            case "low":
                effort = AgentReasoningEffort.Low;
                return true;
            case "medium":
                effort = AgentReasoningEffort.Medium;
                return true;
            case "high":
                effort = AgentReasoningEffort.High;
                return true;
            case "xhigh":
                effort = AgentReasoningEffort.XHigh;
                return true;
            case "max":
                effort = AgentReasoningEffort.Max;
                return true;
            default:
                return false;
        }
    }

    private static bool? TryGetCapabilityBoolean(JsonElement model, string propertyName)
    {
        if (!model.TryGetProperty("capabilities", out var capabilities) || capabilities.ValueKind is not JsonValueKind.Object)
        {
            return null;
        }

        if (!capabilities.TryGetProperty(propertyName, out var value))
        {
            return false;
        }

        return value.ValueKind is JsonValueKind.True;
    }

    private static bool TryGetProperty(JsonElement element, out JsonElement property, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out property))
            {
                return true;
            }
        }

        property = default;
        return false;
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string? value)
    {
        value = null;
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind is not JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString();
        return true;
    }

    private static string? TryGetTrimmedString(JsonElement element, params string[] names)
    {
        if (!TryGetProperty(element, out var property, names) || property.ValueKind is not JsonValueKind.String)
        {
            return null;
        }

        var value = property.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static object? TryGetScalar(JsonElement element, params string[] names)
    {
        if (!TryGetProperty(element, out var property, names))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.TryGetInt64(out var integer) ? (object)integer : property.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
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
