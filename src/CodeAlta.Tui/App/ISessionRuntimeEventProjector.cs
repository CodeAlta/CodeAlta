using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Tui.App;

internal interface ISessionRuntimeEventProjector
{
    void QueueRuntimeEvent(SessionRuntimeEvent runtimeEvent, CancellationToken cancellationToken);
}
