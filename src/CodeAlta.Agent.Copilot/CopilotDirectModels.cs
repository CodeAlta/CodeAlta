using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAlta.Agent;
using CodeAlta.Agent.LocalRuntime;
using CodeAlta.Agent.ModelCatalog;
using XenoAtom.Logging;

namespace CodeAlta.Agent.Copilot;

internal sealed class CopilotModelDiscoveryClient
{
    private static readonly Logger Logger = LogManager.GetLogger("CodeAlta.Agent.Copilot");
    private readonly CopilotDirectProviderOptions _provider;
    private readonly CopilotDirectAuthManager _authManager;
    private readonly HttpClient _httpClient;

    public CopilotModelDiscoveryClient(CopilotDirectProviderOptions provider, CopilotDirectAuthManager authManager, HttpClient httpClient)
    {
        _provider = provider;
        _authManager = authManager;
        _httpClient = httpClient;
    }

    public async Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(LocalAgentProviderDescriptor providerDescriptor, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_provider.SingleModelId))
        {
            return [CreateSingleModel(_provider.SingleModelId.Trim(), providerDescriptor)];
        }

        if (string.Equals(_provider.ModelDiscovery, CopilotDirectModelDiscoveryModes.Static, StringComparison.OrdinalIgnoreCase))
        {
            return CopilotStaticModelFallbackCatalog.List(providerDescriptor);
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_provider.ModelDiscoveryTimeout);
            var credential = await _authManager.GetCredentialAsync(timeout.Token).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(credential.BaseUri, "/models"));
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", credential.Token);
            request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
            CopilotDirectHeaders.ApplyStaticHeaders(request.Headers);
            using var response = await _httpClient.SendAsync(request, timeout.Token).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Copilot model discovery failed with HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            var payload = JsonSerializer.Deserialize(json, CopilotDirectJsonContext.Default.CopilotModelsResponse)
                ?? throw new InvalidOperationException("Copilot model discovery response was empty.");
            if (_provider.EnableModelPolicies)
            {
                await TryEnablePendingModelPoliciesAsync(payload.Data, credential, timeout.Token).ConfigureAwait(false);
            }

            var models = payload.Data
                .Where(IsAllowed)
                .Select(model => MapModel(model, providerDescriptor))
                .ToArray();
            Logger.Info($"Using GitHub Copilot direct model catalog provider={providerDescriptor.ProviderKey} models={models.Length}");
            return ApplyOverrides(models);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (string.Equals(_provider.ModelDiscovery, CopilotDirectModelDiscoveryModes.EndpointWithStaticFallback, StringComparison.OrdinalIgnoreCase))
        {
            Logger.Warn(ex, $"GitHub Copilot model discovery failed; using static fallback provider={providerDescriptor.ProviderKey}");
            return ApplyOverrides(CopilotStaticModelFallbackCatalog.List(providerDescriptor));
        }
    }

    private bool IsAllowed(CopilotModelItem item)
    {
        if (!item.ModelPickerEnabled)
        {
            return false;
        }

        if (string.Equals(item.Policy?.State, "disabled", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!_provider.IncludePreviewModels && IsPreview(item))
        {
            return false;
        }

        return CopilotEndpointDispatcher.TryResolveEndpointKind(item.SupportedEndpoints, item.Capabilities?.Family, out _);
    }

    private async Task TryEnablePendingModelPoliciesAsync(
        IReadOnlyList<CopilotModelItem> models,
        CopilotDirectCredential credential,
        CancellationToken cancellationToken)
    {
        foreach (var model in models)
        {
            if (!model.ModelPickerEnabled ||
                string.IsNullOrWhiteSpace(model.Id) ||
                string.Equals(model.Policy?.State, "disabled", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(model.Policy?.State, "enabled", StringComparison.OrdinalIgnoreCase) ||
                !CopilotEndpointDispatcher.TryResolveEndpointKind(model.SupportedEndpoints, model.Capabilities?.Family, out _))
            {
                continue;
            }

            try
            {
                var escapedId = Uri.EscapeDataString(model.Id);
                using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(credential.BaseUri, $"/models/{escapedId}/policy"));
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", credential.Token);
                request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
                CopilotDirectHeaders.ApplyStaticHeaders(request.Headers);
                request.Headers.TryAddWithoutValidation("OpenAI-Intent", "chat-policy");
                request.Headers.TryAddWithoutValidation("x-interaction-type", "chat-policy");
                request.Content = new StringContent("{\"state\":\"enabled\"}", System.Text.Encoding.UTF8, "application/json");
                using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    Logger.Warn($"GitHub Copilot model policy enablement failed provider={_provider.ProviderKey} model={model.Id} status={(int)response.StatusCode}");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.Warn(ex, $"GitHub Copilot model policy enablement failed provider={_provider.ProviderKey} model={model.Id}");
            }
        }
    }

    private static bool IsPreview(CopilotModelItem item)
        => item.Id.Contains("preview", StringComparison.OrdinalIgnoreCase) ||
           item.Name.Contains("preview", StringComparison.OrdinalIgnoreCase) ||
           item.Version.Contains("preview", StringComparison.OrdinalIgnoreCase);

    private static AgentModelInfo MapModel(CopilotModelItem item, LocalAgentProviderDescriptor providerDescriptor)
    {
        CopilotEndpointDispatcher.TryResolveEndpointKind(item.SupportedEndpoints, item.Capabilities?.Family, out var endpointKind);
        var efforts = MapReasoningEfforts(item.Capabilities?.Supports);
        var defaultReasoningEffort = efforts.Count > 0 ? efforts[0] : (AgentReasoningEffort?)null;
        var supportedReasoningEfforts = efforts.Count > 0 || item.Capabilities?.Supports is not null
            ? efforts
            : null;
        var supportsReasoning = supportedReasoningEfforts is { Count: > 0 } ||
            item.Capabilities?.Supports?.AdaptiveThinking == true ||
            item.Capabilities?.Supports?.MaxThinkingBudget is not null ||
            item.Capabilities?.Supports?.MinThinkingBudget is not null;
        if (supportedReasoningEfforts is { Count: 0 } && supportsReasoning)
        {
            supportedReasoningEfforts = [AgentReasoningEffort.Low, AgentReasoningEffort.Medium, AgentReasoningEffort.High];
            defaultReasoningEffort = AgentReasoningEffort.Medium;
        }

        var limits = item.Capabilities?.Limits;
        var supports = item.Capabilities?.Supports;
        var capabilities = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["family"] = item.Capabilities?.Family,
            ["version"] = item.Version,
            ["releaseDate"] = ExtractReleaseDate(item.Id, item.Version),
            ["supportedEndpoints"] = item.SupportedEndpoints,
            ["copilotEndpointKind"] = endpointKind.ToString(),
            ["contextWindow"] = limits?.MaxContextWindowTokens,
            ["inputTokenLimit"] = limits?.MaxPromptTokens,
            ["outputTokenLimit"] = limits?.MaxOutputTokens,
            ["supportsReasoning"] = supportsReasoning,
            ["supportsToolCall"] = supports?.ToolCalls,
            ["supportsVision"] = supports?.Vision ?? limits?.Vision is not null,
            ["supportsStructuredOutput"] = supports?.StructuredOutputs,
            ["supportsStreaming"] = supports?.Streaming,
            ["supportsAdaptiveThinking"] = supports?.AdaptiveThinking,
            ["minThinkingBudget"] = supports?.MinThinkingBudget,
            ["maxThinkingBudget"] = supports?.MaxThinkingBudget,
            ["policyState"] = item.Policy?.State,
        };
        return new AgentModelInfo(
            item.Id,
            DisplayName: string.IsNullOrWhiteSpace(item.Name) ? item.Id : item.Name,
            Provider: providerDescriptor.ProviderKey,
            DefaultReasoningEffort: defaultReasoningEffort,
            SupportedReasoningEfforts: supportedReasoningEfforts,
            Capabilities: capabilities);
    }

    private static IReadOnlyList<AgentReasoningEffort> MapReasoningEfforts(CopilotModelSupports? supports)
    {
        return supports?.ReasoningEffort?
            .Select(ParseReasoningEffort)
            .Where(static effort => effort is not null)
            .Select(static effort => effort!.Value)
            .Distinct()
            .ToArray() ?? [];
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
    {
        var endpointKind = modelId.Contains("claude", StringComparison.OrdinalIgnoreCase)
            ? CopilotEndpointKind.AnthropicMessages
            : CopilotEndpointKind.ChatCompletions;
        return new AgentModelInfo(
            modelId,
            DisplayName: modelId,
            Provider: providerDescriptor.ProviderKey,
            Capabilities: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["copilotEndpointKind"] = endpointKind.ToString(),
                ["supportedEndpoints"] = endpointKind switch
                {
                    CopilotEndpointKind.AnthropicMessages => new[] { "/v1/messages" },
                    _ => new[] { "/chat/completions" },
                },
            });
    }

    private static AgentReasoningEffort? ParseReasoningEffort(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "minimal" => AgentReasoningEffort.Minimal,
            "low" => AgentReasoningEffort.Low,
            "medium" => AgentReasoningEffort.Medium,
            "high" => AgentReasoningEffort.High,
            "xhigh" => AgentReasoningEffort.XHigh,
            _ => null,
        };

    private static string? ExtractReleaseDate(string id, string version)
        => version.StartsWith(id + "-", StringComparison.Ordinal) ? version[(id.Length + 1)..] : null;
}

internal static class CopilotStaticModelFallbackCatalog
{
    public static IReadOnlyList<AgentModelInfo> List(LocalAgentProviderDescriptor provider)
        =>
        [
            Create("gpt-5.1-codex", "GPT-5.1 Codex", CopilotEndpointKind.Responses, provider),
            Create("gpt-5.1", "GPT-5.1", CopilotEndpointKind.ChatCompletions, provider),
            Create("claude-sonnet-4.5", "Claude Sonnet 4.5", CopilotEndpointKind.AnthropicMessages, provider),
        ];

    private static AgentModelInfo Create(string id, string displayName, CopilotEndpointKind endpointKind, LocalAgentProviderDescriptor provider)
        => new(
            id,
            DisplayName: displayName,
            Provider: provider.ProviderKey,
            Capabilities: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["copilotEndpointKind"] = endpointKind.ToString(),
                ["supportedEndpoints"] = endpointKind switch
                {
                    CopilotEndpointKind.Responses => new[] { "/responses" },
                    CopilotEndpointKind.AnthropicMessages => new[] { "/v1/messages" },
                    _ => new[] { "/chat/completions" },
                },
            });
}

internal enum CopilotEndpointKind
{
    ChatCompletions,
    Responses,
    AnthropicMessages,
}

internal static class CopilotEndpointDispatcher
{
    public static bool TryResolveEndpointKind(IReadOnlyList<string>? endpoints, string? family, out CopilotEndpointKind endpointKind)
    {
        if (endpoints?.Any(static endpoint => string.Equals(endpoint, "/v1/messages", StringComparison.OrdinalIgnoreCase)) == true)
        {
            endpointKind = CopilotEndpointKind.AnthropicMessages;
            return true;
        }

        if (endpoints?.Any(static endpoint => string.Equals(endpoint, "/responses", StringComparison.OrdinalIgnoreCase)) == true)
        {
            endpointKind = CopilotEndpointKind.Responses;
            return true;
        }

        if (endpoints?.Any(static endpoint => string.Equals(endpoint, "/chat/completions", StringComparison.OrdinalIgnoreCase)) == true)
        {
            endpointKind = CopilotEndpointKind.ChatCompletions;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(family) && family.Contains("claude", StringComparison.OrdinalIgnoreCase))
        {
            endpointKind = CopilotEndpointKind.AnthropicMessages;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(family))
        {
            endpointKind = CopilotEndpointKind.ChatCompletions;
            return true;
        }

        endpointKind = default;
        return false;
    }

    public static CopilotEndpointKind Resolve(AgentModelInfo? modelInfo)
    {
        if (modelInfo?.Capabilities is not null && modelInfo.Capabilities.TryGetValue("copilotEndpointKind", out var value))
        {
            if (Enum.TryParse<CopilotEndpointKind>(value?.ToString(), ignoreCase: true, out var endpointKind))
            {
                return endpointKind;
            }
        }

        if (modelInfo?.Capabilities is not null && modelInfo.Capabilities.TryGetValue("supportedEndpoints", out var endpointsValue))
        {
            var endpoints = endpointsValue as IReadOnlyList<string> ?? (endpointsValue as string[]);
            if (TryResolveEndpointKind(endpoints, modelInfo.Capabilities.TryGetValue("family", out var family) ? family?.ToString() : null, out var endpointKind))
            {
                return endpointKind;
            }
        }

        return modelInfo?.Id.Contains("claude", StringComparison.OrdinalIgnoreCase) == true
            ? CopilotEndpointKind.AnthropicMessages
            : CopilotEndpointKind.ChatCompletions;
    }
}

internal sealed class CopilotModelsResponse
{
    [JsonPropertyName("data")]
    public List<CopilotModelItem> Data { get; set; } = [];
}

internal sealed class CopilotModelItem
{
    [JsonPropertyName("model_picker_enabled")]
    public bool ModelPickerEnabled { get; set; }

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("supported_endpoints")]
    public List<string>? SupportedEndpoints { get; set; }

    [JsonPropertyName("policy")]
    public CopilotModelPolicy? Policy { get; set; }

    [JsonPropertyName("capabilities")]
    public CopilotModelCapabilities? Capabilities { get; set; }
}

internal sealed class CopilotModelPolicy
{
    [JsonPropertyName("state")]
    public string? State { get; set; }
}

internal sealed class CopilotModelCapabilities
{
    [JsonPropertyName("family")]
    public string? Family { get; set; }

    [JsonPropertyName("limits")]
    public CopilotModelLimits? Limits { get; set; }

    [JsonPropertyName("supports")]
    public CopilotModelSupports? Supports { get; set; }
}

internal sealed class CopilotModelLimits
{
    [JsonPropertyName("max_context_window_tokens")]
    public long? MaxContextWindowTokens { get; set; }

    [JsonPropertyName("max_output_tokens")]
    public long? MaxOutputTokens { get; set; }

    [JsonPropertyName("max_prompt_tokens")]
    public long? MaxPromptTokens { get; set; }

    [JsonPropertyName("vision")]
    public JsonElement? Vision { get; set; }
}

internal sealed class CopilotModelSupports
{
    [JsonPropertyName("adaptive_thinking")]
    public bool? AdaptiveThinking { get; set; }

    [JsonPropertyName("max_thinking_budget")]
    public long? MaxThinkingBudget { get; set; }

    [JsonPropertyName("min_thinking_budget")]
    public long? MinThinkingBudget { get; set; }

    [JsonPropertyName("reasoning_effort")]
    public List<string>? ReasoningEffort { get; set; }

    [JsonPropertyName("streaming")]
    public bool? Streaming { get; set; }

    [JsonPropertyName("structured_outputs")]
    public bool? StructuredOutputs { get; set; }

    [JsonPropertyName("tool_calls")]
    public bool? ToolCalls { get; set; }

    [JsonPropertyName("vision")]
    public bool? Vision { get; set; }
}
