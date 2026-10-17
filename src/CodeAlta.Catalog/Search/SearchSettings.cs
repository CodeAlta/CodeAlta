namespace CodeAlta.Catalog.Search;

/// <summary>The choices for search, from the global <c>[search]</c> table.</summary>
/// <param name="Enabled">Whether the index exists: keyword search, the conversations of the search window, <c>alta search</c> and tool search.</param>
/// <param name="Sessions">Whether the prompts and final answers of sessions are indexed.</param>
/// <param name="Semantic">Whether search by meaning is on: the embedding model computes vectors and queries use them.</param>
/// <param name="Model">The id of the embedding preset.</param>
/// <param name="PromptCatalog">How skills are listed in the prompt: <see cref="PromptCatalogAuto"/>, <see cref="PromptCatalogFull"/> or <see cref="PromptCatalogCompact"/>.</param>
public sealed record SearchSettings(bool Enabled, bool Sessions, bool Semantic, string Model, string PromptCatalog)
{
    /// <summary>Gets the ids of the projects kept out of search: nothing of them is indexed or found.</summary>
    public IReadOnlyList<string> ExcludedProjects { get; init; } = [];

    /// <summary>Gets the ids of the spaces kept out of search: nothing of their sessions is indexed or found.</summary>
    public IReadOnlyList<string> ExcludedSpaces { get; init; } = [];

    /// <summary>The whole list while there are few skills, then the short form.</summary>
    public const string PromptCatalogAuto = "auto";

    /// <summary>Always the whole list, as before search existed.</summary>
    public const string PromptCatalogFull = "full";

    /// <summary>Always the short form.</summary>
    public const string PromptCatalogCompact = "compact";

    /// <summary>The id of the embedding preset used when none is chosen.</summary>
    public const string DefaultModel = "embeddinggemma-2";

    private const int MaximumModelLength = 128;

    /// <summary>Gets the choices when configuration is absent: the index on, sessions indexed, search by meaning off.</summary>
    public static SearchSettings Default { get; } = new(true, true, false, DefaultModel, PromptCatalogAuto);

    /// <summary>Reads the global search table.</summary>
    /// <param name="document">The table, or null when absent.</param>
    /// <returns>The choices.</returns>
    /// <exception cref="InvalidDataException">The model is not a preset id, or the prompt catalog mode is unknown.</exception>
    public static SearchSettings Read(CodeAltaSearchSettingsDocument? document)
    {
        var model = document?.Model ?? DefaultModel;
        var catalog = document?.PromptCatalog ?? PromptCatalogAuto;
        if ((GetModelError(model) ?? GetPromptCatalogError(catalog)) is { } error) throw new InvalidDataException(error);
        return new(document?.Enabled ?? true, document?.Sessions ?? true, document?.Semantic ?? false, model, catalog)
        {
            ExcludedProjects = Normalize(document?.ExcludedProjects),
            ExcludedSpaces = Normalize(document?.ExcludedSpaces),
        };
    }

    /// <summary>Gets whether a project or a space is kept out of search.</summary>
    /// <param name="projectId">The project, or null.</param>
    /// <param name="spaceId">The space, or null.</param>
    /// <returns>True when either is excluded.</returns>
    public bool IsExcluded(string? projectId, string? spaceId)
        => (projectId is not null && ExcludedProjects.Contains(projectId, StringComparer.OrdinalIgnoreCase))
            || (spaceId is not null && ExcludedSpaces.Contains(spaceId, StringComparer.OrdinalIgnoreCase));

    /// <summary>Gets the ids of a list once each, without the empty ones.</summary>
    /// <param name="ids">The ids, or null.</param>
    /// <returns>The ids.</returns>
    public static IReadOnlyList<string> Normalize(IEnumerable<string>? ids)
        => ids is null ? [] : [.. ids.Where(static id => !string.IsNullOrWhiteSpace(id)).Select(static id => id.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>Gets a configuration error, or null when the mode is one of the three.</summary>
    /// <param name="mode">The mode.</param>
    /// <returns>The error, or null.</returns>
    public static string? GetPromptCatalogError(string? mode)
        => mode is PromptCatalogAuto or PromptCatalogFull or PromptCatalogCompact ? null : "The search prompt_catalog must be auto, full or compact.";

    /// <summary>Gets a configuration error, or null when the model is a preset id: lowercase letters, digits, <c>-</c> and <c>.</c>.</summary>
    /// <param name="model">The model id.</param>
    /// <returns>The error, or null.</returns>
    public static string? GetModelError(string? model)
    {
        const string error = "The search model must be the id of an embedding preset.";
        if (string.IsNullOrEmpty(model) || model.Length > MaximumModelLength || model[0] is '-' or '.') return error;
        foreach (var character in model)
        {
            if (character is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '.')) return error;
        }

        return null;
    }
}
