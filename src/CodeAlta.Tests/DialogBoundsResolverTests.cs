using CodeAlta.Tui.App;
using System.Reflection;
using XenoAtom.Terminal;
using XenoAtom.Terminal.Backends;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Hosting;

namespace CodeAlta.Tests;

[TestClass]
public sealed class DialogBoundsResolverTests
{
    [TestMethod]
    public void ResolveAppBounds_Returns_Null_For_Missing_Target()
        => Assert.IsNull(DialogBoundsResolver.ResolveAppBounds(null));

    [TestMethod]
    public void ResolveAppBounds_Uses_App_Root_Bounds_When_Target_Is_Attached()
    {
        var sidebarTree = new TreeView
        {
            MinWidth = 18,
            MaxWidth = 18,
            MinHeight = 8,
        };

        var layout = new Grid()
            .Rows(new RowDefinition { Height = GridLength.Star(1) })
            .Columns(
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Star(1) });
        layout.Cell(sidebarTree, 0, 0);
        layout.Cell(new Placeholder(), 0, 1);

        using var session = Terminal.Open(new InMemoryTerminalBackend(new TerminalSize(100, 30)), new TerminalOptions { ImplicitStartInput = true }, force: true);
        var app = new TerminalApp(
            layout,
            session.Instance,
            new TerminalAppOptions
            {
                HostKind = TerminalHostKind.Fullscreen,
                EnableMouse = true,
                MouseMode = TerminalMouseMode.Move,
            });

        InvokeTerminalApp(app, "BeginRun");
        try
        {
            TickTerminalApp(app);

            var resolvedBounds = DialogBoundsResolver.ResolveAppBounds(sidebarTree);

            Assert.IsNotNull(resolvedBounds);
            Assert.AreEqual(app.Root.GetAbsoluteBounds(), resolvedBounds.Value);
            Assert.AreNotEqual(sidebarTree.GetAbsoluteBounds().Width, resolvedBounds.Value.Width);
        }
        finally
        {
            InvokeTerminalApp(app, "EndRun");
        }
    }

    private static void TickTerminalApp(TerminalApp app)
    {
        var tickMethod = typeof(TerminalApp).GetMethod("Tick", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(tickMethod);
        tickMethod.Invoke(app, [null]);
    }

    private static void InvokeTerminalApp(TerminalApp app, string methodName)
    {
        var method = typeof(TerminalApp).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(method);
        method.Invoke(app, null);
    }
}
