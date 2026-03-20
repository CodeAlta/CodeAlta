using CodeAlta.Catalog;

namespace CodeAlta.App;

internal sealed class ProjectCatalogLoader : IProjectCatalogLoader
{
    private readonly ProjectCatalog _projectCatalog;

    public ProjectCatalogLoader(ProjectCatalog projectCatalog)
    {
        ArgumentNullException.ThrowIfNull(projectCatalog);
        _projectCatalog = projectCatalog;
    }

    public Task<IReadOnlyList<ProjectDescriptor>> LoadAsync(CancellationToken cancellationToken)
        => _projectCatalog.LoadAsync(cancellationToken);
}