using CodeAlta.Catalog;

namespace CodeAlta.App;

internal interface IRecoverableSessionSource
{
    IAsyncEnumerable<SessionViewDescriptor> ListRecoverableSessionsAsync(CancellationToken cancellationToken);
}
