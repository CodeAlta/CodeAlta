using CodeAlta.Tui.Presentation.Editing;
using CodeAlta.Tui.Threading;
using CodeAlta.Tui.Views;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;

namespace CodeAlta.Tui.App;

internal interface ISessionTabSurfacePort
{
    TabControl? GetTabControl();

    SessionWorkspaceView? GetWorkspaceView();

    ComputedVisual CreateComputedVisual(Func<Visual> build);

    IUiDispatcher GetUiDispatcher();
}

internal interface ISessionTabLifecyclePort
{
    void ActivateDraftTab();

    void ActivateSessionSurface();

    void CloseSessionTab(string sessionId);

    void CloseDraftTab();

    void OpenSessionTab(string sessionId);
}

internal interface IFileEditorTabPort
{
    FileEditorTab? GetFileTab(string tabId);

    void SelectFileTab(string tabId);

    void CloseFileTab(string tabId);
}

internal sealed class DelegatingSessionTabSurfacePort : ISessionTabSurfacePort
{
    private readonly Func<TabControl?> _getTabControl;
    private readonly Func<SessionWorkspaceView?> _getWorkspaceView;
    private readonly Func<Func<Visual>, ComputedVisual> _createComputedVisual;
    private readonly IUiDispatcher _uiDispatcher;

    public DelegatingSessionTabSurfacePort(
        Func<TabControl?> getTabControl,
        Func<SessionWorkspaceView?> getWorkspaceView,
        Func<Func<Visual>, ComputedVisual> createComputedVisual,
        IUiDispatcher uiDispatcher)
    {
        ArgumentNullException.ThrowIfNull(getTabControl);
        ArgumentNullException.ThrowIfNull(getWorkspaceView);
        ArgumentNullException.ThrowIfNull(createComputedVisual);
        ArgumentNullException.ThrowIfNull(uiDispatcher);

        _getTabControl = getTabControl;
        _getWorkspaceView = getWorkspaceView;
        _createComputedVisual = createComputedVisual;
        _uiDispatcher = uiDispatcher;
    }

    public TabControl? GetTabControl()
        => _getTabControl();

    public SessionWorkspaceView? GetWorkspaceView()
        => _getWorkspaceView();

    public ComputedVisual CreateComputedVisual(Func<Visual> build)
    {
        ArgumentNullException.ThrowIfNull(build);
        return _createComputedVisual(build);
    }

    public IUiDispatcher GetUiDispatcher()
        => _uiDispatcher;
}

internal sealed class DelegatingSessionTabLifecyclePort : ISessionTabLifecyclePort
{
    private readonly Action _activateDraftTab;
    private readonly Action _activateSessionSurface;
    private readonly Action<string> _closeSessionTab;
    private readonly Action _closeDraftTab;
    private readonly Action<string> _openSessionTab;

    public DelegatingSessionTabLifecyclePort(
        Action activateDraftTab,
        Action activateSessionSurface,
        Action<string> closeSessionTab,
        Action closeDraftTab,
        Action<string> openSessionTab)
    {
        ArgumentNullException.ThrowIfNull(activateDraftTab);
        ArgumentNullException.ThrowIfNull(activateSessionSurface);
        ArgumentNullException.ThrowIfNull(closeSessionTab);
        ArgumentNullException.ThrowIfNull(closeDraftTab);
        ArgumentNullException.ThrowIfNull(openSessionTab);

        _activateDraftTab = activateDraftTab;
        _activateSessionSurface = activateSessionSurface;
        _closeSessionTab = closeSessionTab;
        _closeDraftTab = closeDraftTab;
        _openSessionTab = openSessionTab;
    }

    public void ActivateDraftTab()
        => _activateDraftTab();

    public void ActivateSessionSurface()
        => _activateSessionSurface();

    public void CloseSessionTab(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        _closeSessionTab(sessionId);
    }

    public void CloseDraftTab()
        => _closeDraftTab();

    public void OpenSessionTab(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        _openSessionTab(sessionId);
    }
}

internal sealed class DelegatingFileEditorTabPort : IFileEditorTabPort
{
    private readonly Func<string, FileEditorTab?> _getFileTab;
    private readonly Action<string> _selectFileTab;
    private readonly Action<string> _closeFileTab;

    public DelegatingFileEditorTabPort(
        Func<string, FileEditorTab?> getFileTab,
        Action<string> selectFileTab,
        Action<string> closeFileTab)
    {
        ArgumentNullException.ThrowIfNull(getFileTab);
        ArgumentNullException.ThrowIfNull(selectFileTab);
        ArgumentNullException.ThrowIfNull(closeFileTab);

        _getFileTab = getFileTab;
        _selectFileTab = selectFileTab;
        _closeFileTab = closeFileTab;
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
