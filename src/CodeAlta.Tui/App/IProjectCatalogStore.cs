using CodeAlta.Catalog;

namespace CodeAlta.Tui.App;

internal interface IProjectCatalogStore : IProjectCatalogLoader
{
    Task<ProjectDescriptor?> GetByIdAsync(string projectId, CancellationToken cancellationToken);

    Task<ProjectDescriptor> UpsertFromPathAsync(string projectPath, CancellationToken cancellationToken);

    Task SaveAsync(ProjectDescriptor project, CancellationToken cancellationToken);

    Task<bool> DeleteAsync(ProjectDescriptor project, CancellationToken cancellationToken);
}
