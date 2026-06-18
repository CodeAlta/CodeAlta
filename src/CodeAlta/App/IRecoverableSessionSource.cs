using CodeAlta.Catalog;

namespace CodeAlta.App;

internal interface IRecoverableSessionSource
{
    IAsyncEnumerable<SessionViewDescriptor> ListRecoverableSessionsAsync(CancellationToken cancellationToken);

    Task<bool> ReconcileRecoverableSessionsAsync(CancellationToken cancellationToken);
}
