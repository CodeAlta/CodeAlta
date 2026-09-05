using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Tui.App;

internal sealed class SessionDeleter : ISessionDeleter
{
    private readonly SessionRuntimeService _runtimeService;

    public SessionDeleter(SessionRuntimeService runtimeService)
    {
        ArgumentNullException.ThrowIfNull(runtimeService);
        _runtimeService = runtimeService;
    }

    public Task<bool> DeleteSessionAsync(SessionViewDescriptor session, CancellationToken cancellationToken)
        => _runtimeService.DeleteSessionAsync(session, cancellationToken);
}
