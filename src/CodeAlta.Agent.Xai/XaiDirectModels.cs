using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAlta.Agent;
using CodeAlta.Agent.LocalRuntime;
using CodeAlta.Agent.ModelCatalog;
using XenoAtom.Logging;

namespace CodeAlta.Agent.Xai;

internal sealed class XaiModelDiscoveryClient
{
    private static readonly Logger Logger = LogManager.GetLogger("CodeAlta.Agent.Xai");
    private readonly XaiProviderOptions _provider;
    private readonly XaiDirectAuthManager _authManager;
    private readonly HttpClient _httpClient;

    public XaiModelDiscoveryClient(XaiProviderOptions provider, XaiDirectAuthManager authManager, HttpClient httpClient)
    {
        _provider = provider;
        _authManager = authManager;
        _httpClient = httpClient;
    }

    public async Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(
        LocalAgentProviderDescriptor providerDescriptor,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_provider.SingleModelId))
        {
            return ApplyOverrides([CreateSingleModel(_provider.SingleModelId.Trim(), providerDescriptor)]);
        }

        if (string.Equals(_provider.ModelDiscovery, XaiModelDiscoveryModes.Static, StringComparison.OrdinalIgnoreCase))
        {
            return ApplyOverrides(XaiStaticModelFallbackCatalog.List(providerDescriptor));
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_provider.ModelDiscoveryTimeout);
            var credential = await _authManager.GetCredentialAsync(timeout.Token).ConfigureAwait(false);
            // Use /v1/language-models instead of /v1/models so non-language entries
            // (e.g. grok-imagine-image, grok-imagine-video) are filtered out at the
            // source. The response shape uses `models` instead of `data` and also
            // carries aliases / modality / pricing metadata.
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(credential.BaseUri, "language-models"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            XaiDirectHeaders.ApplyStaticHeaders(request.Headers);
            using var response = await _httpClient.SendAsync(request, timeout.Token).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"xAI model discovery failed with HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            var payload = JsonSerializer.Deserialize(json, XaiDirectJsonContext.Default.XaiModelsResponse)
                ?? throw new InvalidOperationException("xAI model discovery response was empty.");
            var models = payload.Models
                .Where(static item => !string.IsNullOrWhiteSpace(item.Id))
                .Select(item => MapModel(item, providerDescriptor))
                .ToArray();
            Logger.Info($"Using xAI direct model catalog provider={providerDescriptor.ProviderKey} models={models.Length}");
            return ApplyOverrides(models);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (string.Equals(_provider.ModelDiscovery, XaiModelDiscoveryModes.EndpointWithStaticFallback, StringComparison.OrdinalIgnoreCase))
        {
            Logger.Warn(ex, $"xAI model discovery failed; using static fallback provider={providerDescriptor.ProviderKey}");
            return ApplyOverrides(XaiStaticModelFallbackCatalog.List(providerDescriptor));
        }
    }

    private static AgentModelInfo MapModel(XaiModelItem item, LocalAgentProviderDescriptor providerDescriptor)
    {
        var capabilities = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["created"] = item.Created,
            ["ownedBy"] = item.OwnedBy,
        };
        return new AgentModelInfo(
            item.Id,
            DisplayName: string.IsNullOrWhiteSpace(item.Id) ? item.Id : item.Id,
            Provider: providerDescriptor.ProviderKey,
            SupportedReasoningEfforts: XaiReasoningCapability.GetSupportedEfforts(item.Id),
            Capabilities: capabilities);
    }

    private IReadOnlyList<AgentModelInfo> ApplyOverrides(IReadOnlyList<AgentModelInfo> models)
    {
        if (_provider.ModelOverrides is null || _provider.ModelOverrides.Count == 0)
        {
            return models;
        }

        return models.Select(ApplyOverride).ToArray();
    }

    private AgentModelInfo ApplyOverride(AgentModelInfo model)
    {
        if (_provider.ModelOverrides is null ||
            !_provider.ModelOverrides.TryGetValue(model.Id, out var modelOverride))
        {
            return model;
        }

        var capabilities = model.Capabilities is null
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            : new Dictionary<string, object?>(model.Capabilities, StringComparer.Ordinal);
        SetIfNotNull(capabilities, "contextWindow", modelOverride.ContextWindowTokens);
        SetIfNotNull(capabilities, "inputTokenLimit", modelOverride.InputTokenLimit);
        SetIfNotNull(capabilities, "outputTokenLimit", modelOverride.OutputTokenLimit);
        SetIfNotNull(capabilities, "maxTokens", modelOverride.MaxTokens);
        SetIfNotNull(capabilities, "supportsReasoning", modelOverride.SupportsReasoning);
        SetIfNotNull(capabilities, "supportsToolCall", modelOverride.SupportsToolCall);
        SetIfNotNull(capabilities, "supportsAttachments", modelOverride.SupportsAttachments);
        SetIfNotNull(capabilities, "supportsStructuredOutput", modelOverride.SupportsStructuredOutput);
        return model with
        {
            DisplayName = string.IsNullOrWhiteSpace(modelOverride.DisplayName) ? model.DisplayName : modelOverride.DisplayName,
            Description = string.IsNullOrWhiteSpace(modelOverride.Description) ? model.Description : modelOverride.Description,
            Capabilities = capabilities,
        };
    }

    private static void SetIfNotNull<T>(IDictionary<string, object?> capabilities, string key, T? value)
        where T : struct
    {
        if (value is not null)
        {
            capabilities[key] = value.Value;
        }
    }

    private static AgentModelInfo CreateSingleModel(string modelId, LocalAgentProviderDescriptor providerDescriptor)
        => new(
            modelId,
            DisplayName: modelId,
            Provider: providerDescriptor.ProviderKey,
            SupportedReasoningEfforts: XaiReasoningCapability.GetSupportedEfforts(modelId));
}

internal static class XaiStaticModelFallbackCatalog
{
    public static IReadOnlyList<AgentModelInfo> List(LocalAgentProviderDescriptor provider)
        =>
        [
            Create("grok-4.3", "Grok 4.3", provider),
            Create("grok-4", "Grok 4", provider),
            Create("grok-4-fast", "Grok 4 Fast", provider),
        ];

    private static AgentModelInfo Create(string id, string displayName, LocalAgentProviderDescriptor provider)
        => new(
            id,
            DisplayName: displayName,
            Provider: provider.ProviderKey,
            SupportedReasoningEfforts: XaiReasoningCapability.GetSupportedEfforts(id));
}

internal static class XaiReasoningCapability
{
    // xAI does not expose a "supports reasoning_effort" capability on /v1/models or
    // /v1/language-models, so we infer it from the model id. Sending reasoning.effort
    // to a non-reasoning model is rejected with HTTP 400 "does not support parameter
    // reasoningEffort", so we return an empty support list for those ids to make the
    // OpenAI Responses executor skip the field entirely.
    //
    // Returning null means "model supports the full reasoning effort range" — the
    // executor falls through to the requested value.
    public static IReadOnlyList<AgentReasoningEffort>? GetSupportedEfforts(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return null;
        }

        var normalized = modelId.Trim().ToLowerInvariant();
        if (normalized.StartsWith("grok-build", StringComparison.Ordinal) ||
            normalized.StartsWith("grok-code", StringComparison.Ordinal) ||
            normalized.Contains("non-reasoning", StringComparison.Ordinal))
        {
            return [];
        }

        return null;
    }
}

internal sealed class XaiModelsResponse
{
    [JsonPropertyName("models")]
    public List<XaiModelItem> Models { get; set; } = [];
}

internal sealed class XaiModelItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("created")]
    public long? Created { get; set; }

    [JsonPropertyName("owned_by")]
    public string? OwnedBy { get; set; }
}
