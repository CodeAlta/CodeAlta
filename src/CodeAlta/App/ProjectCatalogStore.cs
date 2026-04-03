using CodeAlta.Catalog;

namespace CodeAlta.App;

internal sealed class ProjectCatalogStore : IProjectCatalogStore
{
    private readonly ProjectCatalog _projectCatalog;

    public ProjectCatalogStore(ProjectCatalog projectCatalog)
    {
        ArgumentNullException.ThrowIfNull(projectCatalog);
        _projectCatalog = projectCatalog;
    }

    public Task<IReadOnlyList<ProjectDescriptor>> LoadAsync(CancellationToken cancellationToken)
        => _projectCatalog.LoadAsync(cancellationToken);

    public Task<ProjectDescriptor?> GetByIdAsync(string projectId, CancellationToken cancellationToken)
        => _projectCatalog.GetByIdAsync(projectId, cancellationToken);

    public Task<ProjectDescriptor> UpsertFromPathAsync(string projectPath, CancellationToken cancellationToken)
        => _projectCatalog.UpsertFromPathAsync(projectPath, cancellationToken);

    public Task SaveAsync(ProjectDescriptor project, CancellationToken cancellationToken)
        => _projectCatalog.SaveAsync(project, cancellationToken);
}
