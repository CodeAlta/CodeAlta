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

    public static AcpModelCache From(IReadOnlyList<AgentModelInfo> models, string? currentModel)
        => new()
        {
            Models = [.. models.Select(static model => new AcpCachedModel { Id = model.Id, Name = model.DisplayName, Description = model.Description })],
            CurrentModel = currentModel,
            FetchedAt = DateTimeOffset.UtcNow,
        };

    public IReadOnlyList<AgentModelInfo> ToModels()
        => [.. Models.Where(static model => !string.IsNullOrWhiteSpace(model.Id)).Select(static model => new AgentModelInfo(model.Id, model.Name ?? model.Id, model.Description))];
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
}

/// <summary>Reads and writes the model lists of the ACP providers, one file per provider.</summary>
internal sealed class AcpModelCacheStore(string root)
{
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

    public void Write(string providerKey, AcpModelCache cache)
    {
        var path = PathOf(providerKey);
        try
        {
            Directory.CreateDirectory(root);
            var temporary = path + ".tmp";
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(cache, AcpPluginJsonContext.Default.AcpModelCache));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The list is asked again next time.
        }
    }

    private string PathOf(string providerKey)
    {
        var name = new string([.. providerKey.Select(static character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' ? character : '_')]);
        return Path.Combine(root, name + ".json");
    }
}
