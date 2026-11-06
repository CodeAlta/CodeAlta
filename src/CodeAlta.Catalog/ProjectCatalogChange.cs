namespace CodeAlta.Catalog;

/// <summary>What happened to a project file of the catalog.</summary>
public enum ProjectCatalogChangeKind
{
    /// <summary>A project file was written where there was none: a project was added.</summary>
    Added,

    /// <summary>The file of a project was written again: the project changed, was archived or restored.</summary>
    Saved,

    /// <summary>The file of a project was deleted: the project left the catalog.</summary>
    Deleted,
}

/// <summary>A change of a project of the catalog, as <see cref="ProjectCatalog.Changed"/> tells it.</summary>
/// <param name="Project">A copy of the project as it was written, or as it was before it was deleted.</param>
/// <param name="Kind">What happened to it.</param>
/// <param name="Origin">What added the project, or brought it back from the archive (<see cref="ProjectOrigins"/>); null when not known or for another change.</param>
public sealed record ProjectCatalogChange(ProjectDescriptor Project, ProjectCatalogChangeKind Kind, string? Origin);

/// <summary>The ways a project is added to the catalog, as a change of the catalog names them.</summary>
public static class ProjectOrigins
{
    /// <summary>The window of CodeAlta: <b>Add project</b>, or a folder opened there.</summary>
    public const string Window = "window";

    /// <summary><c>alta project add</c>, by a person or an agent.</summary>
    public const string AltaAdd = "alta project add";

    /// <summary><c>alta project upsert</c>, by a person or an agent.</summary>
    public const string AltaUpsert = "alta project upsert";

    /// <summary>A session started in a folder the catalog did not list.</summary>
    public const string Session = "session";

    /// <summary>Another machine of the fleet: a project it created here, or a session it moved here.</summary>
    public const string Fleet = "fleet";

    /// <summary>Not known: the project was added by another process, or while this one was not running.</summary>
    public const string Unknown = "unknown";
}
