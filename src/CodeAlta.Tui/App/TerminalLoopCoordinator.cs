using CodeAlta.Tui.Threading;
using XenoAtom.Terminal.UI;

namespace CodeAlta.Tui.App;

internal sealed class TerminalLoopCoordinator
{
    private readonly CodeAltaShellController _shellController;
    private readonly RuntimeEventPump _runtimeEventPump;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly Action _applyPendingSidebarSelection;
    private bool _started;

    public TerminalLoopCoordinator(
        CodeAltaShellController shellController,
        RuntimeEventPump runtimeEventPump,
        IUiDispatcher uiDispatcher,
        Action applyPendingSidebarSelection)
    {
        ArgumentNullException.ThrowIfNull(shellController);
        ArgumentNullException.ThrowIfNull(runtimeEventPump);
        ArgumentNullException.ThrowIfNull(uiDispatcher);
        ArgumentNullException.ThrowIfNull(applyPendingSidebarSelection);

        _shellController = shellController;
        _runtimeEventPump = runtimeEventPump;
        _uiDispatcher = uiDispatcher;
        _applyPendingSidebarSelection = applyPendingSidebarSelection;
    }

    public bool HasStarted => _started;

    public void Start(CancellationToken cancellationToken)
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _shellController.AttachUiDispatcher(_uiDispatcher);
        _shellController.StartInitialization(cancellationToken);
        _runtimeEventPump.Start(cancellationToken);
    }

    public TerminalLoopResult OnIteration(CancellationToken cancellationToken)
    {
        Start(cancellationToken);
        _applyPendingSidebarSelection();
        return TerminalLoopResult.Continue;
    }
}
