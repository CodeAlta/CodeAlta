using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.App;

internal sealed class RecoverableThreadSource : IRecoverableThreadSource
{
    private readonly WorkThreadRuntimeService _runtimeService;

    public RecoverableThreadSource(WorkThreadRuntimeService runtimeService)
    {
        ArgumentNullException.ThrowIfNull(runtimeService);
        _runtimeService = runtimeService;
    }

    public Task<IReadOnlyList<WorkThreadDescriptor>> ListRecoverableThreadsAsync(CancellationToken cancellationToken)
        => _runtimeService.ListRecoverableThreadsAsync(cancellationToken);
}