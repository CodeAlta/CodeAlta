using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAlta.Agent;

namespace CodeAlta.Plugin.Acp;

/// <summary>The models an agent listed when it was last asked, kept so that the pickers have them before it starts.</summary>
internal sealed class AcpModelCache
{
    [JsonPropertyName("models")]
    public List<AcpCachedModel> Models { get; set; } = [];

    [JsonPropertyName("currentModel")]
    public string? CurrentModel { get; set; }

    [JsonPropertyName("fetchedAt")]
    public DateTimeOffset FetchedAt { get; set; }

    /// <summary>The values chosen in CodeAlta for the options of the models, by model then by option.</summary>
    [JsonPropertyName("choices")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, Dictionary<string, string>>? Choices { get; set; }

    public static AcpModelCache From(IReadOnlyList<AcpListedModel> models, string? currentModel,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? choices = null)
        => new()
        {
            Models = [.. models.Select(static model => new AcpCachedModel
            {
                Id = model.Id,
                Name = model.Name,
                Description = model.Description,
                Meta = model.Meta.ValueKind == JsonValueKind.Object ? model.Meta : null,
                Efforts = model.Efforts?.Select(static effort => effort.ToString().ToLowerInvariant()).ToList(),
                DefaultEffort = model.DefaultEffort?.ToString().ToLowerInvariant(),
                Options = model.Options?.Select(static option => new AcpCachedOption
                {
                    Id = option.Id,
                    Name = option.Name,
                    Description = option.Description,
                    Value = option.Value,
                    Values = [.. option.Values.Select(static value => new AcpCachedOptionValue { Value = value.Value, Name = value.Name })],
                }).ToList(),
            })],
            CurrentModel = currentModel,
            FetchedAt = DateTimeOffset.UtcNow,
            Choices = choices is { Count: > 0 }
                ? choices.ToDictionary(static entry => entry.Key, static entry => new Dictionary<string, string>(entry.Value, StringComparer.Ordinal), StringComparer.Ordinal)
                : null,
        };

    public IReadOnlyList<AcpListedModel> ToModels()
        => [.. Models.Where(static model => !string.IsNullOrWhiteSpace(model.Id)).Select(static model => new AcpListedModel(model.Id, model.Name, model.Description, model.Meta ?? default)
        {
            // A list kept by an older version has no efforts: they are not known.
            Efforts = model.Efforts?.Select(AcpConfigOptions.ToEffort).OfType<AgentReasoningEffort>().Distinct().ToArray(),
            DefaultEffort = AcpConfigOptions.ToEffort(model.DefaultEffort),
            Options = model.Options?
                .Where(static option => !string.IsNullOrWhiteSpace(option.Id) && option.Values is { Count: > 1 } && option.Value is not null)
                .Select(static option => new AgentModelOption(option.Id, string.IsNullOrWhiteSpace(option.Name) ? option.Id : option.Name,
                    [.. option.Values!.Where(static value => !string.IsNullOrWhiteSpace(value.Value)).Select(static value => new AgentModelOptionValue(value.Value, value.Name))],
                    option.Value!)
                {
                    Description = option.Description,
                })
                .ToArray(),
        })];
}

/// <summary>An option of a model of <see cref="AcpModelCache"/>, with the value the agent gave it.</summary>
internal sealed class AcpCachedOption
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    [JsonPropertyName("value")]
    public string? Value { get; set; }

    [JsonPropertyName("values")]
    public List<AcpCachedOptionValue>? Values { get; set; }
}

/// <summary>A value of an <see cref="AcpCachedOption"/>.</summary>
internal sealed class AcpCachedOptionValue
{
    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }
}

/// <summary>A model of <see cref="AcpModelCache"/>.</summary>
internal sealed class AcpCachedModel
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("meta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Meta { get; set; }

    [JsonPropertyName("efforts")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Efforts { get; set; }

    [JsonPropertyName("defaultEffort")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DefaultEffort { get; set; }

    [JsonPropertyName("options")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<AcpCachedOption>? Options { get; set; }
}

/// <summary>
/// Reads and writes the model lists of the ACP providers, one file per provider, with the values chosen for the options
/// of their models. Every runtime of a provider, in every CodeAlta that runs on the profile, reads the choices from the
/// file when it needs them.
/// </summary>
internal sealed class AcpModelCacheStore(string root)
{
    private readonly Lock _gate = new();

    /// <summary>Returns the values chosen for the options of a model, empty when none was chosen.</summary>
    public IReadOnlyDictionary<string, string> ChoicesOf(string providerKey, string modelId)
        => Read(providerKey)?.Choices is { } choices && choices.TryGetValue(modelId, out var chosen) ? chosen : new Dictionary<string, string>();

    /// <summary>Returns the values chosen for the options of the models of a provider, by model then by option.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Choices(string providerKey)
        => Read(providerKey)?.Choices?.ToDictionary(static entry => entry.Key, static entry => (IReadOnlyDictionary<string, string>)entry.Value, StringComparer.Ordinal)
            ?? new Dictionary<string, IReadOnlyDictionary<string, string>>();

    /// <summary>Records the value chosen for an option of a model, with the models the file has.</summary>
    public void Choose(string providerKey, string modelId, string optionId, string value, Func<AcpModelCache> create)
    {
        lock (_gate)
        {
            var cache = Read(providerKey) ?? create();
            cache.Choices ??= new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            if (!cache.Choices.TryGetValue(modelId, out var chosen))
            {
                cache.Choices[modelId] = chosen = new Dictionary<string, string>(StringComparer.Ordinal);
            }

            chosen[optionId] = value;
            // A choice that is not kept is not taken: the turns of the model would not send it.
            if (!WriteCore(providerKey, cache))
            {
                throw new IOException($"The choices of {providerKey} could not be written in {root}.");
            }
        }
    }

    public AcpModelCache? Read(string providerKey)
    {
        var path = PathOf(providerKey);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize(stream, AcpPluginJsonContext.Default.AcpModelCache);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Writes the models of a provider; a cache without choices keeps those the file has.</summary>
    public void Write(string providerKey, AcpModelCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        lock (_gate)
        {
            cache.Choices ??= Read(providerKey)?.Choices;
            // A list not written is asked again next time.
            _ = WriteCore(providerKey, cache);
        }
    }

    private bool WriteCore(string providerKey, AcpModelCache cache)
    {
        var path = PathOf(providerKey);
        try
        {
            Directory.CreateDirectory(root);
            var temporary = path + ".tmp";
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(cache, AcpPluginJsonContext.Default.AcpModelCache));
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private string PathOf(string providerKey)
    {
        var name = new string([.. providerKey.Select(static character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' ? character : '_')]);
        return Path.Combine(root, name + ".json");
    }
}
