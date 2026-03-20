using CodeAlta.Catalog;

namespace CodeAlta.App;

internal interface IRecoverableThreadSource
{
    Task<IReadOnlyList<WorkThreadDescriptor>> ListRecoverableThreadsAsync(CancellationToken cancellationToken);
}