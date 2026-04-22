using CodeAlta.Threading;
using CodeAlta.Presentation.Editing;
using CodeAlta.Views;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;

namespace CodeAlta.App.Context;

internal sealed class ThreadTabContext
{
    private readonly Func<TabControl?> _getTabControl;
    private readonly Func<ThreadWorkspaceView?> _getWorkspaceView;
    private readonly Func<Func<Visual>, ComputedVisual> _createComputedVisual;
    private readonly Func<IUiDispatcher> _getUiDispatcher;
    private readonly Action _activateDraftTab;
    private readonly Action _activateThreadSurface;
    private readonly Action<string> _closeThread;
    private readonly Action _closeDraftTab;
    private readonly Action<string> _openThread;
    private readonly Func<string, FileEditorTab?> _getFileTab;
    private readonly Action<string> _selectFileTab;
    private readonly Action<string> _closeFileTab;

    public ThreadTabContext(
        Func<TabControl?> getTabControl,
        Func<ThreadWorkspaceView?> getWorkspaceView,
        Func<Func<Visual>, ComputedVisual> createComputedVisual,
        Func<IUiDispatcher> getUiDispatcher,
        Action activateDraftTab,
        Action activateThreadSurface,
        Action<string> closeThread,
        Action closeDraftTab,
        Action<string> openThread,
        Func<string, FileEditorTab?> getFileTab,
        Action<string> selectFileTab,
        Action<string> closeFileTab)
    {
        ArgumentNullException.ThrowIfNull(getTabControl);
        ArgumentNullException.ThrowIfNull(getWorkspaceView);
        ArgumentNullException.ThrowIfNull(createComputedVisual);
        ArgumentNullException.ThrowIfNull(getUiDispatcher);
        ArgumentNullException.ThrowIfNull(activateDraftTab);
        ArgumentNullException.ThrowIfNull(activateThreadSurface);
        ArgumentNullException.ThrowIfNull(closeThread);
        ArgumentNullException.ThrowIfNull(closeDraftTab);
        ArgumentNullException.ThrowIfNull(openThread);
        ArgumentNullException.ThrowIfNull(getFileTab);
        ArgumentNullException.ThrowIfNull(selectFileTab);
        ArgumentNullException.ThrowIfNull(closeFileTab);

        _getTabControl = getTabControl;
        _getWorkspaceView = getWorkspaceView;
        _createComputedVisual = createComputedVisual;
        _getUiDispatcher = getUiDispatcher;
        _activateDraftTab = activateDraftTab;
        _activateThreadSurface = activateThreadSurface;
        _closeThread = closeThread;
        _closeDraftTab = closeDraftTab;
        _openThread = openThread;
        _getFileTab = getFileTab;
        _selectFileTab = selectFileTab;
        _closeFileTab = closeFileTab;
    }

    public TabControl? GetTabControl()
        => _getTabControl();

    public ThreadWorkspaceView? GetWorkspaceView()
        => _getWorkspaceView();

    public ComputedVisual CreateComputedVisual(Func<Visual> build)
    {
        ArgumentNullException.ThrowIfNull(build);
        return _createComputedVisual(build);
    }

    public IUiDispatcher GetUiDispatcher()
        => _getUiDispatcher();

    public void ActivateDraftTab()
        => _activateDraftTab();

    public void ActivateThreadSurface()
        => _activateThreadSurface();

    public void CloseThread(string threadId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        _closeThread(threadId);
    }

    public void CloseDraftTab()
        => _closeDraftTab();

    public void OpenThread(string threadId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        _openThread(threadId);
    }

    public FileEditorTab? GetFileTab(string tabId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tabId);
        return _getFileTab(tabId);
    }

    public void SelectFileTab(string tabId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tabId);
        _selectFileTab(tabId);
    }

    public void CloseFileTab(string tabId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tabId);
        _closeFileTab(tabId);
    }
}
