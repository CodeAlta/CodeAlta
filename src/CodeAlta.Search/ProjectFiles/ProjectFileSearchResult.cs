namespace CodeAlta.Search;

/// <summary>
/// Represents a ranked project-file search match.
/// </summary>
/// <param name="Item">The matched item.</param>
/// <param name="Score">The aggregate match score.</param>
/// <param name="IsRecent">Indicates whether recent usage contributed to the result.</param>
public sealed record ProjectFileSearchResult(
    ProjectFileSearchItem Item,
    double Score,
    bool IsRecent = false);
