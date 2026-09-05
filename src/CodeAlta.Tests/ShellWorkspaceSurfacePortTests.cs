using CodeAlta.Tui.App;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;

namespace CodeAlta.Tests;

[TestClass]
public sealed class ShellWorkspaceSurfacePortTests
{
    [TestMethod]
    public void QueriesAndCommands_ForwardThroughDelegates()
    {
        var bounds = new Rectangle(1, 2, 30, 10);
        var focusTarget = new TextBlock("prompt");
        var bootstrap = new TextBlock("welcome");
        Visual? capturedBootstrap = null;
        var focused = false;
        var workspaceReason = (ShellWorkspaceRefreshReason?)null;
        var sidebarReason = (SidebarRefreshReason?)null;
        var port = new ShellWorkspaceSurfacePort(
            () => true,
            () => bounds,
            () => focusTarget,
            visual => capturedBootstrap = visual,
            () => focused = true,
            reason => workspaceReason = reason,
            reason => sidebarReason = reason);

        Assert.IsTrue(port.HasWorkspaceSurface);
        Assert.AreEqual(bounds, port.GetWorkspaceBounds());
        Assert.AreSame(focusTarget, port.GetPromptFocusTarget());

        port.ShowBootstrapSurface(bootstrap);
        port.FocusPromptTarget();
        port.RefreshWorkspace(ShellWorkspaceRefreshReason.RuntimeEvent);
        port.RefreshSidebar(SidebarRefreshReason.CatalogChanged);

        Assert.AreSame(bootstrap, capturedBootstrap);
        Assert.IsTrue(focused);
        Assert.AreEqual(ShellWorkspaceRefreshReason.RuntimeEvent, workspaceReason);
        Assert.AreEqual(SidebarRefreshReason.CatalogChanged, sidebarReason);
    }

    [TestMethod]
    public void ShowBootstrapSurface_RejectsNullContent()
    {
        var port = new ShellWorkspaceSurfacePort(
            () => false,
            () => null,
            () => null,
            _ => { },
            () => { },
            _ => { },
            _ => { });

        Assert.ThrowsExactly<ArgumentNullException>(() => port.ShowBootstrapSurface(null!));
    }
}
