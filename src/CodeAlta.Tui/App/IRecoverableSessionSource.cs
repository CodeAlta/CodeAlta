using CodeAlta.Catalog;

namespace CodeAlta.Tui.App;

internal interface IRecoverableSessionSource
{
    IAsyncEnumerable<SessionViewDescriptor> ListRecoverableSessionsAsync(CancellationToken cancellationToken);

    Task<bool> ReconcileRecoverableSessionsAsync(CancellationToken cancellationToken);
}
