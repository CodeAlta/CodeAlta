using CodeAlta.Views;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI.Geometry;

namespace CodeAlta.Tests;

[TestClass]
public sealed class ResponsiveDialogSizeTests
{
    [TestMethod]
    public void Resolve_UsesResponsiveScalingWithinBounds()
    {
        var size = ResponsiveDialogSize.Resolve(new Rectangle(0, 0, 100, 50), minWidth: 40, minHeight: 20);

        Assert.AreEqual(80, size.Width);
        Assert.AreEqual(40, size.Height);
    }

    [TestMethod]
    public void Resolve_UsesMinimumSizeWhenBoundsAreSmallerOrMissing()
    {
        var missingBounds = ResponsiveDialogSize.Resolve(bounds: null, minWidth: 60, minHeight: 18);
        var smallBounds = ResponsiveDialogSize.Resolve(new Rectangle(0, 0, 40, 10), minWidth: 60, minHeight: 18);

        Assert.AreEqual(60, missingBounds.Width);
        Assert.AreEqual(18, missingBounds.Height);
        Assert.AreEqual(60, smallBounds.Width);
        Assert.AreEqual(18, smallBounds.Height);
    }

    [TestMethod]
    public void ThreadWorkspaceView_UsesF6ForExpandedPromptShortcut()
        => Assert.AreEqual("F6", ThreadWorkspaceView.ExpandPromptShortcutKey.ToString());
}
