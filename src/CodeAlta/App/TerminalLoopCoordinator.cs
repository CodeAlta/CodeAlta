using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Threading;

namespace CodeAlta.App;

internal sealed class TerminalLoopCoordinator
{
    private readonly CodeAltaShellController _shellController;
    private readonly RuntimeEventPump _runtimeEventPump;
    private readonly Action<IUiDispatcher> _attachUiDispatcher;
    private readonly Action _applyPendingSidebarSelection;
    private readonly Action _syncSidebarSelection;
    private bool _started;

    public TerminalLoopCoordinator(
        CodeAltaShellController shellController,
        RuntimeEventPump runtimeEventPump,
        Action<IUiDispatcher> attachUiDispatcher,
        Action applyPendingSidebarSelection,
        Action syncSidebarSelection)
    {
        ArgumentNullException.ThrowIfNull(shellController);
        ArgumentNullException.ThrowIfNull(runtimeEventPump);
        ArgumentNullException.ThrowIfNull(attachUiDispatcher);
        ArgumentNullException.ThrowIfNull(applyPendingSidebarSelection);
        ArgumentNullException.ThrowIfNull(syncSidebarSelection);

        _shellController = shellController;
        _runtimeEventPump = runtimeEventPump;
        _attachUiDispatcher = attachUiDispatcher;
        _applyPendingSidebarSelection = applyPendingSidebarSelection;
        _syncSidebarSelection = syncSidebarSelection;
    }

    public bool HasStarted => _started;

    public TerminalLoopResult OnIteration(CancellationToken cancellationToken)
    {
        if (!_started)
        {
            _started = true;
            var uiDispatcher = new TerminalUiDispatcher(Dispatcher.Current);
            _attachUiDispatcher(uiDispatcher);
            _shellController.AttachUiDispatcher(uiDispatcher);
            _shellController.StartInitialization(cancellationToken);
            _runtimeEventPump.Start(cancellationToken);
        }

        _applyPendingSidebarSelection();
        _syncSidebarSelection();
        return TerminalLoopResult.Continue;
    }
}