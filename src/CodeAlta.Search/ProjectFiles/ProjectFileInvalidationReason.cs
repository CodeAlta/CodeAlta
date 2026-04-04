namespace CodeAlta.Search;

/// <summary>
/// Describes why a project-file snapshot was marked dirty.
/// </summary>
public enum ProjectFileInvalidationReason
{
    /// <summary>
    /// The source of invalidation is unknown.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// A session explicitly requested a refresh.
    /// </summary>
    SessionRefresh = 1,

    /// <summary>
    /// CodeAlta performed a known file write or create operation.
    /// </summary>
    FileSystemWrite = 2,

    /// <summary>
    /// The active project root changed.
    /// </summary>
    ProjectChanged = 3,
}
