using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Geometry;

namespace CodeAlta.Tui.App;

internal enum ShellWorkspaceRefreshReason
{
    SelectionChanged,
    CatalogChanged,
    RuntimeEvent,
    PromptChanged,
    Explicit,
}

internal enum SidebarRefreshReason
{
    SelectionChanged,
    CatalogChanged,
    RuntimeEvent,
    PromptChanged,
    Explicit,
}

internal interface IWorkspaceSurface
{
    bool HasWorkspaceSurface { get; }

    Rectangle? GetWorkspaceBounds();

    Visual? GetPromptFocusTarget();

    void ShowBootstrapSurface(Visual content);

    void FocusPromptTarget();

    void RefreshWorkspace(ShellWorkspaceRefreshReason reason);

    void RefreshSidebar(SidebarRefreshReason reason);
}

internal sealed class ShellWorkspaceSurfacePort : IWorkspaceSurface
{
    private readonly Func<bool> _hasWorkspaceSurface;
    private readonly Func<Rectangle?> _getWorkspaceBounds;
    private readonly Func<Visual?> _getPromptFocusTarget;
    private readonly Action<Visual> _showBootstrapSurface;
    private readonly Action _focusPromptTarget;
    private readonly Action<ShellWorkspaceRefreshReason> _refreshWorkspace;
    private readonly Action<SidebarRefreshReason> _refreshSidebar;

    public ShellWorkspaceSurfacePort(
        Func<bool> hasWorkspaceSurface,
        Func<Rectangle?> getWorkspaceBounds,
        Func<Visual?> getPromptFocusTarget,
        Action<Visual> showBootstrapSurface,
        Action focusPromptTarget,
        Action<ShellWorkspaceRefreshReason> refreshWorkspace,
        Action<SidebarRefreshReason> refreshSidebar)
    {
        ArgumentNullException.ThrowIfNull(hasWorkspaceSurface);
        ArgumentNullException.ThrowIfNull(getWorkspaceBounds);
        ArgumentNullException.ThrowIfNull(getPromptFocusTarget);
        ArgumentNullException.ThrowIfNull(showBootstrapSurface);
        ArgumentNullException.ThrowIfNull(focusPromptTarget);
        ArgumentNullException.ThrowIfNull(refreshWorkspace);
        ArgumentNullException.ThrowIfNull(refreshSidebar);

        _hasWorkspaceSurface = hasWorkspaceSurface;
        _getWorkspaceBounds = getWorkspaceBounds;
        _getPromptFocusTarget = getPromptFocusTarget;
        _showBootstrapSurface = showBootstrapSurface;
        _focusPromptTarget = focusPromptTarget;
        _refreshWorkspace = refreshWorkspace;
        _refreshSidebar = refreshSidebar;
    }

    public bool HasWorkspaceSurface => _hasWorkspaceSurface();

    public Rectangle? GetWorkspaceBounds()
        => _getWorkspaceBounds();

    public Visual? GetPromptFocusTarget()
        => _getPromptFocusTarget();

    public void ShowBootstrapSurface(Visual content)
    {
        ArgumentNullException.ThrowIfNull(content);
        _showBootstrapSurface(content);
    }

    public void FocusPromptTarget()
        => _focusPromptTarget();

    public void RefreshWorkspace(ShellWorkspaceRefreshReason reason)
        => _refreshWorkspace(reason);

    public void RefreshSidebar(SidebarRefreshReason reason)
        => _refreshSidebar(reason);
}
