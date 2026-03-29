using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.App;

internal sealed class WorkThreadDeleter : IWorkThreadDeleter
{
    private readonly WorkThreadRuntimeService _runtimeService;

    public WorkThreadDeleter(WorkThreadRuntimeService runtimeService)
    {
        ArgumentNullException.ThrowIfNull(runtimeService);
        _runtimeService = runtimeService;
    }

    public Task<bool> DeleteThreadAsync(WorkThreadDescriptor thread, CancellationToken cancellationToken)
        => _runtimeService.DeleteThreadAsync(thread, cancellationToken);
}
