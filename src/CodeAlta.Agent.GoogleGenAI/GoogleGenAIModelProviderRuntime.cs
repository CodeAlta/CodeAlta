using CodeAlta.Agent.Runtime;
using CodeAlta.Agent.Runtime.Compaction;
using CodeAlta.Agent.ModelCatalog;
using Google.GenAI;
using Google.GenAI.Types;
using Microsoft.Extensions.AI;

namespace CodeAlta.Agent.GoogleGenAI;

/// <summary>
/// Google GenAI model-provider runtime.
/// </summary>
public sealed class GoogleGenAIModelProviderRuntime : IAgentModelProviderRuntime
{
    private readonly IAgentModelProviderRuntime _runtime;

    /// <summary>
    /// Initializes a new instance of the <see cref="GoogleGenAIModelProviderRuntime"/> class.
    /// </summary>
    /// <param name="options">The provider runtime options.</param>
    public GoogleGenAIModelProviderRuntime(GoogleGenAIModelProviderRuntimeOptions options)
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

    private static AgentModelProviderRuntime CreateProviderRuntime(GoogleGenAIProviderOptions provider)
    {
        var providerKey = provider.ProviderKey.Trim();
        var displayName = string.IsNullOrWhiteSpace(provider.DisplayName) ? providerKey : provider.DisplayName.Trim();
        var runtimeDescriptor = new ModelProviderRuntimeDescriptor
        {
            ProtocolFamily = provider.UseVertexAI ? "vertex-ai" : "google-genai",
            ProviderKey = providerKey,
            DisplayName = displayName,
            TransportKind = provider.UseVertexAI ? AgentTransportKind.GoogleVertexAI : AgentTransportKind.GoogleGeminiApi,
            BaseUri = provider.BaseUri,
            IsDefault = provider.IsDefault,
            Profile = provider.Profile ?? new AgentProviderProfile
            {
                SupportsDeveloperRole = false,
                SupportsReasoningEffort = true,
                StreamsUsage = true,
                SupportsThoughtSignatures = true,
            },
            Compaction = provider.Compaction ?? AgentCompactionSettings.Default,
        };
        var descriptor = new ModelProviderDescriptor(new ModelProviderId(providerKey), displayName, runtimeDescriptor.ProtocolFamily)
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

    private static IModelProviderTurnExecutor CreateTurnExecutor(GoogleGenAIProviderOptions provider)
    {
        return new ChatClientTurnExecutor(
            (providerDescriptor, cancellationToken) => CreateChatClientAsync(provider, providerDescriptor, cancellationToken),
            (providerDescriptor, cancellationToken) => ListModelsAsync(provider, providerDescriptor, cancellationToken),
            classifyFailure: ClassifyFailure);
    }

    // The SDK's ApiException hides the status of its HttpRequestException behind a StatusCode of its own: the generic
    // classifier sees none. Its message is the message of the error and its details (the quota, the retry delay).
    internal static AgentFailureInfo? ClassifyFailure(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is not ApiException { StatusCode: >= 400 and <= 599 } api)
            {
                continue;
            }

            var text = api.Message;
            var status = api.Status ?? string.Empty;
            TimeSpan? delay = AgentFailureClassifier.TryParseRetryDelay(text, out var written) ? written
                : RetryDelayPattern.Match(text) is { Success: true } match &&
                  double.TryParse(match.Groups["seconds"].ValueSpan, System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
                    ? TimeSpan.FromSeconds(seconds)
                    : null;
            int? retryAfterSeconds = delay is { } wait ? (int)Math.Ceiling(wait.TotalSeconds) : null;
            DateTimeOffset? resetsAt = delay is { } until ? DateTimeOffset.UtcNow + until : null;

            // An invalid key is a 400 INVALID_ARGUMENT, not a 401.
            if (text.Contains("API key not valid", StringComparison.OrdinalIgnoreCase) || text.Contains("API_KEY_INVALID", StringComparison.Ordinal))
            {
                return new AgentFailureInfo(AgentFailureKind.Auth);
            }

            if (api.StatusCode == 429 || status == "RESOURCE_EXHAUSTED")
            {
                // Every 429 says "You exceeded your current quota": a daily quota, or a wait of an hour or more, is an
                // exhausted one; a limit per minute is a rate limit.
                var daily = text.Contains("PerDay", StringComparison.Ordinal) || text.Contains("per day", StringComparison.OrdinalIgnoreCase);
                return new AgentFailureInfo(
                    daily || delay >= TimeSpan.FromHours(1) ? AgentFailureKind.QuotaExhausted : AgentFailureKind.RateLimited,
                    resetsAt,
                    retryAfterSeconds);
            }

            if (api.StatusCode == 404 && text.Contains("model", StringComparison.OrdinalIgnoreCase))
            {
                return new AgentFailureInfo(AgentFailureKind.ModelUnavailable);
            }

            return AgentFailureClassifier.FromHttpStatus(api.StatusCode, text, retryAfterSeconds, resetsAt);
        }

        return AgentFailureClassifier.Classify(exception);
    }

    // The RetryInfo of the details: "retryDelay": "37s".
    private static readonly System.Text.RegularExpressions.Regex RetryDelayPattern = new(
        "\"?retryDelay\"?\\s*[:=]\\s*\"?(?<seconds>\\d+(?:\\.\\d+)?)s",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    // The SDK asks again on its own for a service that is unavailable for a moment; a 429 carries its delay and is left
    // to the fallback.
    private static HttpRetryOptions CreateRetryOptions()
        => new()
        {
            Attempts = 3,
            HttpStatusCodes = [408, 500, 502, 503, 504],
        };

    private static ValueTask<IChatClient> CreateChatClientAsync(
        GoogleGenAIProviderOptions provider,
        ModelProviderRuntimeDescriptor providerDescriptor,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (provider.ChatClientFactory is not null)
        {
            return ValueTask.FromResult(provider.ChatClientFactory());
        }

        var client = CreateSdkClient(provider);
        return ValueTask.FromResult<IChatClient>(new OwnedChatClient(client.AsIChatClient(), client));
    }

    private static async Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(
        GoogleGenAIProviderOptions provider,
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

        using var client = CreateSdkClient(provider);
        var pager = await client.Models.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var results = new List<AgentModelInfo>();
        await foreach (var model in pager.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ToAgentModelInfo(providerDescriptor, model));
        }

        models = results;
        return AgentModelMetadataEnricher.EnrichModels(
            models,
            provider.ModelCatalog,
            provider.ModelsDevProviderId,
            provider.ModelOverrides,
            provider.ModelsIncludeRegex);
    }

    private static Client CreateSdkClient(GoogleGenAIProviderOptions provider)
    {
        var httpOptions = new HttpOptions
        {
            BaseUrl = provider.BaseUri?.ToString(),
            Headers = provider.ExtraHeaders is null
                ? null
                : new Dictionary<string, string>(provider.ExtraHeaders, StringComparer.OrdinalIgnoreCase),
            RetryOptions = CreateRetryOptions(),
        };
        return new Client(
            vertexAI: provider.UseVertexAI,
            apiKey: provider.ApiKey,
            project: provider.Project,
            location: provider.Location,
            httpOptions: httpOptions);
    }

    private static AgentModelInfo ToAgentModelInfo(
        ModelProviderRuntimeDescriptor provider,
        Model model)
    {
        var capabilities = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["inputTokenLimit"] = model.InputTokenLimit,
            ["outputTokenLimit"] = model.OutputTokenLimit,
            ["thinking"] = model.Thinking,
            ["supportedActions"] = model.SupportedActions?.ToArray(),
        };

        return new AgentModelInfo(
            model.Name ?? string.Empty,
            DisplayName: model.DisplayName,
            Description: model.Description,
            Provider: provider.ProviderKey,
            Capabilities: capabilities);
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
