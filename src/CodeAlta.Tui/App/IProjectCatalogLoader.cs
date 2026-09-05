using CodeAlta.Catalog;

namespace CodeAlta.Tui.App;

internal interface IProjectCatalogLoader
{
    Task<IReadOnlyList<ProjectDescriptor>> LoadAsync(CancellationToken cancellationToken);
}
