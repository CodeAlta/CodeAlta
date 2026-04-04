namespace CodeAlta.Search;

/// <summary>
/// Identifies whether a search result is a file or directory.
/// </summary>
public enum ProjectFileSearchItemKind
{
    /// <summary>
    /// A regular file.
    /// </summary>
    File = 0,

    /// <summary>
    /// A directory.
    /// </summary>
    Directory = 1,
}
