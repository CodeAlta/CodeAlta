using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.App;

internal sealed class RecoverableSessionSource : IRecoverableSessionSource
{
    private readonly SessionRuntimeService _runtimeService;

    public RecoverableSessionSource(SessionRuntimeService runtimeService)
    {
        ArgumentNullException.ThrowIfNull(runtimeService);
        _runtimeService = runtimeService;
    }

    public IAsyncEnumerable<SessionViewDescriptor> ListRecoverableSessionsAsync(CancellationToken cancellationToken)
        => _runtimeService.ListRecoverableSessionsAsync(cancellationToken);
}
