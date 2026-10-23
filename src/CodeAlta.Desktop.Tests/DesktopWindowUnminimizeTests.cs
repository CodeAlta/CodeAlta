using CodeAlta.Desktop;
using NeoAstra;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class DesktopWindowUnminimizeTests
{
    [TestMethod]
    public void AWindowMinimizedWhileMaximized_GoesBackMaximized()
    {
        var unminimize = new DesktopWindowUnminimize(NeoWindowState.Normal);
        unminimize.Changed(NeoWindowState.Normal, NeoWindowState.Maximized);
        unminimize.Changed(NeoWindowState.Maximized, NeoWindowState.Minimized);
        Assert.AreEqual(NeoWindowState.Maximized, unminimize.Before);

        // Brought back, then restored by the user, then minimized again: it goes back to its normal state.
        unminimize.Changed(NeoWindowState.Minimized, NeoWindowState.Maximized);
        unminimize.Changed(NeoWindowState.Maximized, NeoWindowState.Normal);
        unminimize.Changed(NeoWindowState.Normal, NeoWindowState.Minimized);
        Assert.AreEqual(NeoWindowState.Normal, unminimize.Before);
    }

    [TestMethod]
    public void TheStateBeforeTheWindowWasMinimized_IsTheOneTheWindowHadThen()
    {
        // A window that was maximized before it was followed tells it when it is minimized.
        var unminimize = new DesktopWindowUnminimize(NeoWindowState.Normal);
        unminimize.Changed(NeoWindowState.Fullscreen, NeoWindowState.Minimized);
        Assert.AreEqual(NeoWindowState.Fullscreen, unminimize.Before);

        // Minimized twice in a row, it keeps the state it had before the first time.
        unminimize.Changed(NeoWindowState.Minimized, NeoWindowState.Minimized);
        Assert.AreEqual(NeoWindowState.Fullscreen, unminimize.Before);
    }

    [TestMethod]
    public void AWindowFollowedWhileMinimizedOrMaximized_StartsFromItsState()
    {
        Assert.AreEqual(NeoWindowState.Normal, new DesktopWindowUnminimize(NeoWindowState.Minimized).Before);
        Assert.AreEqual(NeoWindowState.Maximized, new DesktopWindowUnminimize(NeoWindowState.Maximized).Before);
        Assert.AreEqual(NeoWindowState.Fullscreen, new DesktopWindowUnminimize(NeoWindowState.Fullscreen).Before);
    }
}
