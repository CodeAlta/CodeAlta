using CodeAlta.Catalog;

namespace CodeAlta.App;

internal interface IWorkThreadDeleter
{
    Task<bool> DeleteThreadAsync(WorkThreadDescriptor thread, CancellationToken cancellationToken);
}
