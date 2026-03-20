using CodeAlta.Catalog;

namespace CodeAlta.App;

internal interface IProjectCatalogLoader
{
    Task<IReadOnlyList<ProjectDescriptor>> LoadAsync(CancellationToken cancellationToken);
}