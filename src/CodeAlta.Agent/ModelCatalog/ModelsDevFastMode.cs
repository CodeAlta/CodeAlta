using System.Text.Json;

namespace CodeAlta.Agent.ModelCatalog;

/// <summary>
/// The fast mode models.dev gives a model of a provider (<c>experimental.modes.fast</c>, or <c>modes.fast</c>): what a
/// request of the model adds to run faster, at a higher price per token.
/// </summary>
/// <param name="Body">The properties the body of a request takes, such as <c>speed</c> = <c>"fast"</c>; empty for none.</param>
/// <param name="Headers">The headers a request takes, such as <c>anthropic-beta</c>; empty for none.</param>
/// <remarks>
/// models.dev keeps the modes of a model under <c>experimental</c>, whose shape can change: a mode that is not written
/// as it is read here is no fast mode, never an error. The modes are also read on the model itself, where they would be
/// once they are no longer experimental. A provider sends only what it knows of a mode, not whatever the catalog names:
/// the catalog is downloaded.
/// </remarks>
internal sealed record ModelsDevFastMode(IReadOnlyDictionary<string, JsonElement> Body, IReadOnlyDictionary<string, string> Headers)
{
    /// <summary>
    /// Finds the fast mode of a model as a provider serves it. The mode the vendor of a model gives it is not the one
    /// of a provider that resells the model.
    /// </summary>
    /// <param name="catalog">The catalog, or null.</param>
    /// <param name="modelsDevProviderId">The models.dev provider that serves the model, or null.</param>
    /// <param name="modelId">The model.</param>
    /// <returns>The fast mode, or null when models.dev gives the model none on this provider.</returns>
    public static ModelsDevFastMode? Find(ModelsDevCatalogService? catalog, string? modelsDevProviderId, string modelId)
        => catalog is not null && !string.IsNullOrWhiteSpace(modelsDevProviderId) && catalog.TryGetModel(modelsDevProviderId, modelId, out var model)
            ? Read(model)
            : null;

    /// <summary>
    /// Tells whether a catalog gives a fast mode to any model. One that gives none is not written as it is read here:
    /// models.dev has given some since it describes modes.
    /// </summary>
    /// <param name="database">The catalog.</param>
    /// <returns><see langword="true"/> when a model of a provider has a fast mode.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="database"/> is null.</exception>
    public static bool IsIn(ModelsDevDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        return database.Providers.Values.Any(static provider =>
            provider?.Models?.Values.Any(static model => model is not null && Read(model) is not null) == true);
    }

    /// <summary>Reads the fast mode of a model of models.dev.</summary>
    /// <param name="model">The model.</param>
    /// <returns>The fast mode, or null when the model has none that names what a request takes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="model"/> is null.</exception>
    public static ModelsDevFastMode? Read(ModelsDevModelDefinition model)
    {
        ArgumentNullException.ThrowIfNull(model);

        // {"experimental":{"modes":{"fast":{"cost":{...},"provider":{"body":{"speed":"fast"},"headers":{"anthropic-beta":"..."}}}}}}
        if (model.ExtensionData is not { } properties ||
            !TryGetModes(properties, out var modes) ||
            !TryGetObject(modes, "fast", out var fast) ||
            !TryGetObject(fast, "provider", out var provider))
        {
            return null;
        }

        var body = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (provider.TryGetProperty("body", out var bodyElement))
        {
            if (bodyElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var property in bodyElement.EnumerateObject())
            {
                body[property.Name] = property.Value;
            }
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (provider.TryGetProperty("headers", out var headersElement))
        {
            if (headersElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var header in headersElement.EnumerateObject())
            {
                if (header.Value.ValueKind != JsonValueKind.String)
                {
                    return null;
                }

                headers[header.Name] = header.Value.GetString()!;
            }
        }

        return body.Count > 0 || headers.Count > 0 ? new ModelsDevFastMode(body, headers) : null;
    }

    private static bool TryGetModes(Dictionary<string, JsonElement> properties, out JsonElement modes)
        => properties.TryGetValue("modes", out modes) && modes.ValueKind == JsonValueKind.Object ||
            properties.TryGetValue("experimental", out var experimental) && TryGetObject(experimental, "modes", out modes);

    private static bool TryGetObject(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Object;
    }
}
