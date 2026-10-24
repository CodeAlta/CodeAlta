using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeAlta.Agent.ModelCatalog;

/// <summary>
/// Represents the models.dev provider and model database snapshot.
/// </summary>
public sealed class ModelsDevDatabase
{
    private readonly IReadOnlyDictionary<string, ModelsDevProviderEntry> _providersById;

    internal ModelsDevDatabase(
        IReadOnlyDictionary<string, ModelsDevProviderDefinition> providers,
        IReadOnlyDictionary<string, ModelsDevProviderEntry> providersById)
    {
        Providers = providers;
        _providersById = providersById;
    }

    /// <summary>
    /// Gets the providers keyed by models.dev provider identifier.
    /// </summary>
    public IReadOnlyDictionary<string, ModelsDevProviderDefinition> Providers { get; }

    /// <summary>
    /// Tries to resolve a provider by models.dev provider identifier.
    /// </summary>
    /// <param name="providerId">The models.dev provider identifier.</param>
    /// <param name="provider">The resolved provider when found.</param>
    /// <returns><see langword="true"/> when the provider exists; otherwise <see langword="false"/>.</returns>
    public bool TryGetProvider(string providerId, [NotNullWhen(true)] out ModelsDevProviderDefinition? provider)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            provider = null;
            return false;
        }

        if (_providersById.TryGetValue(providerId.Trim(), out var entry))
        {
            provider = entry.Provider;
            return true;
        }

        provider = null;
        return false;
    }

    /// <summary>
    /// Tries to resolve a model by models.dev provider identifier and model identifier.
    /// </summary>
    /// <param name="providerId">The models.dev provider identifier.</param>
    /// <param name="modelId">The model identifier.</param>
    /// <param name="model">The resolved model when found.</param>
    /// <returns><see langword="true"/> when the model exists; otherwise <see langword="false"/>.</returns>
    public bool TryGetModel(
        string providerId,
        string modelId,
        [NotNullWhen(true)] out ModelsDevModelDefinition? model)
    {
        if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(modelId))
        {
            model = null;
            return false;
        }

        if (_providersById.TryGetValue(providerId.Trim(), out var providerEntry))
        {
            foreach (var candidate in GetModelIdCandidates(modelId.Trim()))
            {
                foreach (var lookupKey in AgentModelIdentity.GetLookupKeys(candidate))
                {
                    if (providerEntry.ModelsById.TryGetValue(lookupKey, out var resolved))
                    {
                        model = resolved;
                        return true;
                    }
                }
            }
        }

        model = null;
        return false;
    }

    /// <summary>
    /// Tries to find the models.dev provider whose API endpoint is the given one: the same host, port and path, a
    /// trailing <c>/v1</c> aside. A path several providers share (a coding plan and the API of the same vendor) gives
    /// the one named <paramref name="preferredProviderId"/>, or none.
    /// </summary>
    /// <param name="apiUrl">The endpoint a provider is configured with.</param>
    /// <param name="preferredProviderId">The provider chosen among several with the same endpoint, or <see langword="null"/>.</param>
    /// <param name="providerId">The models.dev provider identifier when found.</param>
    /// <returns><see langword="true"/> when one provider has this endpoint; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="apiUrl"/> is <see langword="null"/>.</exception>
    public bool TryFindProviderByApiUrl(
        Uri apiUrl,
        string? preferredProviderId,
        [NotNullWhen(true)] out string? providerId)
    {
        ArgumentNullException.ThrowIfNull(apiUrl);
        providerId = null;
        if (!apiUrl.IsAbsoluteUri || string.IsNullOrEmpty(apiUrl.Host))
        {
            return false;
        }

        var endpoint = NormalizeEndpoint(apiUrl);
        var matches = new List<string>();
        foreach (var entry in Providers)
        {
            if (Uri.TryCreate(entry.Value.ApiBaseUrl, UriKind.Absolute, out var providerUrl) &&
                !string.IsNullOrEmpty(providerUrl.Host) &&
                string.Equals(NormalizeEndpoint(providerUrl), endpoint, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(entry.Key);
            }
        }

        if (matches.Count == 1)
        {
            providerId = matches[0];
            return true;
        }

        providerId = matches.FirstOrDefault(match => string.Equals(match, preferredProviderId?.Trim(), StringComparison.OrdinalIgnoreCase));
        return providerId is not null;
    }

    // The identifiers a model is looked up by, the one given first: a name under a path (`models/gemini-2.5-pro`)
    // also by its last part, and a version written with a dot or a dash between digits (`claude-opus-4.8`,
    // `claude-opus-4-8`) also the other way, as providers write them differently.
    private static IEnumerable<string> GetModelIdCandidates(string modelId)
    {
        yield return modelId;
        var slash = modelId.LastIndexOf('/');
        var name = slash >= 0 && slash < modelId.Length - 1 ? modelId[(slash + 1)..] : null;
        if (name is not null)
        {
            yield return name;
        }

        var bare = name ?? modelId;
        if (SwapVersionSeparator(bare, '.', '-') is { } dashed)
        {
            yield return dashed;
        }

        if (SwapVersionSeparator(bare, '-', '.') is { } dotted)
        {
            yield return dotted;
        }
    }

    private static string? SwapVersionSeparator(string value, char from, char to)
    {
        char[]? swapped = null;
        for (var i = 1; i < value.Length - 1; i++)
        {
            // A single digit before the separator: `4-8` is a version, `2025-10` or `v4-pro` is not.
            if (value[i] == from && char.IsAsciiDigit(value[i - 1]) && char.IsAsciiDigit(value[i + 1]) &&
                (i < 2 || !char.IsAsciiDigit(value[i - 2])) && (i + 2 >= value.Length || !char.IsAsciiDigit(value[i + 2])))
            {
                swapped ??= value.ToCharArray();
                swapped[i] = to;
            }
        }

        return swapped is null ? null : new string(swapped);
    }

    private static string NormalizeEndpoint(Uri url)
    {
        var path = url.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^3];
        }

        return $"{url.Host}:{url.Port}{path}";
    }

    internal static ModelsDevDatabase CreateNormalized(
        Dictionary<string, ModelsDevProviderDefinition>? providers)
    {
        var normalizedProviders = new Dictionary<string, ModelsDevProviderDefinition>(StringComparer.OrdinalIgnoreCase);
        var providersById = new Dictionary<string, ModelsDevProviderEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in providers ?? [])
        {
            var provider = entry.Value;
            var providerId = NormalizeIdentifier(entry.Key) ?? NormalizeIdentifier(provider.Id);
            if (providerId is null)
            {
                continue;
            }

            normalizedProviders[providerId] = provider;
            providersById[providerId] = new ModelsDevProviderEntry(
                provider,
                NormalizeModels(provider.Models));
        }

        return new ModelsDevDatabase(normalizedProviders, providersById);
    }

    private static Dictionary<string, ModelsDevModelDefinition> NormalizeModels(
        Dictionary<string, ModelsDevModelDefinition>? models)
    {
        var normalized = new Dictionary<string, ModelsDevModelDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in models ?? [])
        {
            var modelId = NormalizeIdentifier(entry.Key) ?? NormalizeIdentifier(entry.Value.Id);
            if (modelId is null)
            {
                continue;
            }

            foreach (var lookupKey in AgentModelIdentity.GetLookupKeys(modelId, entry.Value.Id, entry.Value.Name))
            {
                normalized.TryAdd(lookupKey, entry.Value);
            }
        }

        return normalized;
    }

    private static string? NormalizeIdentifier(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal sealed record ModelsDevProviderEntry(
        ModelsDevProviderDefinition Provider,
        IReadOnlyDictionary<string, ModelsDevModelDefinition> ModelsById);
}

/// <summary>
/// Represents one models.dev provider entry.
/// </summary>
public sealed class ModelsDevProviderDefinition
{
    /// <summary>
    /// Gets or sets the provider identifier.
    /// </summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>
    /// Gets or sets the provider display name.
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets the documentation URL.
    /// </summary>
    [JsonPropertyName("doc")]
    public string? DocumentationUrl { get; set; }

    /// <summary>
    /// Gets or sets the provider npm package identifier.
    /// </summary>
    [JsonPropertyName("npm")]
    public string? NpmPackage { get; set; }

    /// <summary>
    /// Gets or sets the provider API endpoint when applicable.
    /// </summary>
    [JsonPropertyName("api")]
    public string? ApiBaseUrl { get; set; }

    /// <summary>
    /// Gets or sets the provider environment variable names.
    /// </summary>
    [JsonPropertyName("env")]
    public string[]? EnvironmentVariables { get; set; }

    /// <summary>
    /// Gets or sets the models keyed by model identifier.
    /// </summary>
    [JsonPropertyName("models")]
    public Dictionary<string, ModelsDevModelDefinition> Models { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets or sets unrecognized provider properties.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// Represents one models.dev model entry.
/// </summary>
public sealed class ModelsDevModelDefinition
{
    /// <summary>
    /// Gets or sets the model identifier.
    /// </summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>
    /// Gets or sets the display name.
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets the model family identifier.
    /// </summary>
    [JsonPropertyName("family")]
    public string? Family { get; set; }

    /// <summary>
    /// Gets or sets whether attachments are supported.
    /// </summary>
    [JsonPropertyName("attachment")]
    public bool? Attachment { get; set; }

    /// <summary>
    /// Gets or sets whether reasoning is supported.
    /// </summary>
    [JsonPropertyName("reasoning")]
    public bool? Reasoning { get; set; }

    /// <summary>
    /// Gets or sets whether tool calling is supported.
    /// </summary>
    [JsonPropertyName("tool_call")]
    public bool? ToolCall { get; set; }

    /// <summary>
    /// Gets or sets whether structured output is supported.
    /// </summary>
    [JsonPropertyName("structured_output")]
    public bool? StructuredOutput { get; set; }

    /// <summary>
    /// Gets or sets whether temperature control is supported.
    /// </summary>
    [JsonPropertyName("temperature")]
    public bool? Temperature { get; set; }

    /// <summary>
    /// Gets or sets the knowledge cutoff.
    /// </summary>
    [JsonPropertyName("knowledge")]
    public string? Knowledge { get; set; }

    /// <summary>
    /// Gets or sets the release date.
    /// </summary>
    [JsonPropertyName("release_date")]
    public string? ReleaseDate { get; set; }

    /// <summary>
    /// Gets or sets the last updated date.
    /// </summary>
    [JsonPropertyName("last_updated")]
    public string? LastUpdated { get; set; }

    /// <summary>
    /// Gets or sets whether the model has open weights.
    /// </summary>
    [JsonPropertyName("open_weights")]
    public bool? OpenWeights { get; set; }

    /// <summary>
    /// Gets or sets the model status.
    /// </summary>
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    /// <summary>
    /// Gets or sets the supported modalities.
    /// </summary>
    [JsonPropertyName("modalities")]
    public ModelsDevModalitiesDefinition? Modalities { get; set; }

    /// <summary>
    /// Gets or sets the cost information.
    /// </summary>
    [JsonPropertyName("cost")]
    public ModelsDevCostDefinition? Cost { get; set; }

    /// <summary>
    /// Gets or sets the token-limit information.
    /// </summary>
    [JsonPropertyName("limit")]
    public ModelsDevLimitDefinition? Limit { get; set; }

    /// <summary>
    /// Gets or sets the interleaved-reasoning description.
    /// </summary>
    [JsonPropertyName("interleaved")]
    [JsonConverter(typeof(ModelsDevInterleavedDefinitionJsonConverter))]
    public ModelsDevInterleavedDefinition? Interleaved { get; set; }

    /// <summary>
    /// Gets or sets unrecognized model properties.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// Represents supported input and output modalities for a model.
/// </summary>
public sealed class ModelsDevModalitiesDefinition
{
    /// <summary>
    /// Gets or sets the supported input modalities.
    /// </summary>
    [JsonPropertyName("input")]
    public string[]? Input { get; set; }

    /// <summary>
    /// Gets or sets the supported output modalities.
    /// </summary>
    [JsonPropertyName("output")]
    public string[]? Output { get; set; }

    /// <summary>
    /// Gets or sets unrecognized modalities properties.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// Represents cost information for a model.
/// </summary>
public sealed class ModelsDevCostDefinition
{
    /// <summary>
    /// Gets or sets the cost per million input tokens.
    /// </summary>
    [JsonPropertyName("input")]
    public decimal? Input { get; set; }

    /// <summary>
    /// Gets or sets the cost per million output tokens.
    /// </summary>
    [JsonPropertyName("output")]
    public decimal? Output { get; set; }

    /// <summary>
    /// Gets or sets the cost per million reasoning tokens.
    /// </summary>
    [JsonPropertyName("reasoning")]
    public decimal? Reasoning { get; set; }

    /// <summary>
    /// Gets or sets the cost per million cached read tokens.
    /// </summary>
    [JsonPropertyName("cache_read")]
    public decimal? CacheRead { get; set; }

    /// <summary>
    /// Gets or sets the cost per million cached write tokens.
    /// </summary>
    [JsonPropertyName("cache_write")]
    public decimal? CacheWrite { get; set; }

    /// <summary>
    /// Gets or sets the cost per million input-audio tokens.
    /// </summary>
    [JsonPropertyName("input_audio")]
    public decimal? InputAudio { get; set; }

    /// <summary>
    /// Gets or sets the cost per million output-audio tokens.
    /// </summary>
    [JsonPropertyName("output_audio")]
    public decimal? OutputAudio { get; set; }

    /// <summary>
    /// Gets or sets unrecognized cost properties.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// Represents token limits for a model.
/// </summary>
public sealed class ModelsDevLimitDefinition
{
    /// <summary>
    /// Gets or sets the context-window size.
    /// </summary>
    [JsonPropertyName("context")]
    public long? Context { get; set; }

    /// <summary>
    /// Gets or sets the maximum input tokens.
    /// </summary>
    [JsonPropertyName("input")]
    public long? Input { get; set; }

    /// <summary>
    /// Gets or sets the maximum output tokens.
    /// </summary>
    [JsonPropertyName("output")]
    public long? Output { get; set; }

    /// <summary>
    /// Gets or sets unrecognized limit properties.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// Represents models.dev interleaved reasoning metadata.
/// </summary>
public sealed class ModelsDevInterleavedDefinition
{
    /// <summary>
    /// Gets or sets whether interleaved reasoning is supported.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the interleaved output field name when known.
    /// </summary>
    public string? Field { get; set; }
}

internal sealed class ModelsDevInterleavedDefinitionJsonConverter : JsonConverter<ModelsDevInterleavedDefinition?>
{
    public override ModelsDevInterleavedDefinition? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.True => new ModelsDevInterleavedDefinition { Enabled = true },
            JsonTokenType.False => new ModelsDevInterleavedDefinition { Enabled = false },
            JsonTokenType.StartObject => ReadObject(ref reader),
            _ => throw new JsonException("Expected a boolean or object for models.dev interleaved metadata."),
        };
    }

    public override void Write(Utf8JsonWriter writer, ModelsDevInterleavedDefinition? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        if (string.IsNullOrWhiteSpace(value.Field))
        {
            writer.WriteBooleanValue(value.Enabled);
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("field", value.Field);
        writer.WriteEndObject();
    }

    private static ModelsDevInterleavedDefinition ReadObject(ref Utf8JsonReader reader)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var enabled = true;
        string? field = null;

        if (document.RootElement.TryGetProperty("field", out var fieldElement) &&
            fieldElement.ValueKind == JsonValueKind.String)
        {
            field = fieldElement.GetString();
        }

        if (document.RootElement.TryGetProperty("enabled", out var enabledElement))
        {
            enabled = enabledElement.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => enabled,
            };
        }

        return new ModelsDevInterleavedDefinition
        {
            Enabled = enabled,
            Field = field,
        };
    }
}
