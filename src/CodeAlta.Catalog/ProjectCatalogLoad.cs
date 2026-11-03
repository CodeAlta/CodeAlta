namespace CodeAlta.Catalog;

/// <summary>A project file a load skipped (<see cref="ProjectCatalog.LoadReadableAsync"/>): it could not be read, or is not a valid project.</summary>
/// <param name="SourcePath">The path of the file.</param>
/// <param name="Message">Why it could not be read.</param>
public sealed record UnreadableProjectFile(string SourcePath, string Message);

/// <summary>The result of <see cref="ProjectCatalog.LoadReadableAsync"/>.</summary>
/// <param name="Projects">The projects that could be read, as <see cref="ProjectCatalog.LoadAsync"/> gives them.</param>
/// <param name="Unreadable">The project files that were skipped.</param>
public sealed record ProjectCatalogLoad(IReadOnlyList<ProjectDescriptor> Projects, IReadOnlyList<UnreadableProjectFile> Unreadable);
