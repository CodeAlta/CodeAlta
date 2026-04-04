namespace CodeAlta.Search;

/// <summary>
/// Represents recent-usage metadata for a project file or directory.
/// </summary>
/// <param name="ProjectRoot">Project root path.</param>
/// <param name="RelativePath">Normalized relative path.</param>
/// <param name="Kind">File or directory kind.</param>
/// <param name="LastAccessedAt">Most recent access time.</param>
/// <param name="AccessCount">Bounded access count.</param>
/// <param name="LastAccessKind">Last access source when available.</param>
public sealed record ProjectFileUsageEntry(
    string ProjectRoot,
    string RelativePath,
    ProjectFileSearchItemKind Kind,
    DateTimeOffset LastAccessedAt,
    long AccessCount,
    ProjectFileUsageAccessKind? LastAccessKind);
