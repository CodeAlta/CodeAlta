using CodeAlta.Tui.Threading;
using CodeAlta.Tui.Presentation.Editing;
using CodeAlta.Tui.Views;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;

namespace CodeAlta.Tui.App.Context;

internal sealed class SessionTabContext
{
    private readonly ISessionTabSurfacePort _surface;
    private readonly ISessionTabLifecyclePort _lifecycle;
    private readonly IFileEditorTabPort _fileEditors;

    public SessionTabContext(
        ISessionTabSurfacePort surface,
        ISessionTabLifecyclePort lifecycle,
        IFileEditorTabPort fileEditors)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(fileEditors);

        _surface = surface;
        _lifecycle = lifecycle;
        _fileEditors = fileEditors;
    }

    public TabControl? GetTabControl()
        => _surface.GetTabControl();

    public SessionWorkspaceView? GetWorkspaceView()
        => _surface.GetWorkspaceView();

    public ComputedVisual CreateComputedVisual(Func<Visual> build)
    {
        ArgumentNullException.ThrowIfNull(build);
        return _surface.CreateComputedVisual(build);
    }

    public IUiDispatcher GetUiDispatcher()
        => _surface.GetUiDispatcher();

    public void ActivateDraftTab()
        => _lifecycle.ActivateDraftTab();

    public void ActivateSessionSurface()
        => _lifecycle.ActivateSessionSurface();

    public void CloseSessionTab(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        _lifecycle.CloseSessionTab(sessionId);
    }

    public void CloseDraftTab()
        => _lifecycle.CloseDraftTab();

    public void OpenSession(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        _lifecycle.OpenSessionTab(sessionId);
    }

    public FileEditorTab? GetFileTab(string tabId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tabId);
        return _fileEditors.GetFileTab(tabId);
    }

    public void SelectFileTab(string tabId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tabId);
        _fileEditors.SelectFileTab(tabId);
    }

    public void CloseFileTab(string tabId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tabId);
        _fileEditors.CloseFileTab(tabId);
    }
}
