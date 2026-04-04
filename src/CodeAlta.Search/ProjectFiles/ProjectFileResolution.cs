namespace CodeAlta.Search;

/// <summary>
/// Represents the outcome of resolving a project-relative file reference.
/// </summary>
/// <param name="IsResolved">Indicates whether the reference resolved successfully.</param>
/// <param name="NormalizedReferenceText">Normalized project-relative reference text without the leading <c>@</c>.</param>
/// <param name="Item">The resolved item when available.</param>
/// <param name="LineRange">Requested line range when available.</param>
public sealed record ProjectFileResolution(
    bool IsResolved,
    string NormalizedReferenceText,
    ProjectFileSearchItem? Item,
    ProjectFileLineRange? LineRange);
