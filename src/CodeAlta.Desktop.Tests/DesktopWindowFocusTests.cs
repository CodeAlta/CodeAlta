using CodeAlta.Desktop;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class DesktopWindowFocusTests
{
    [TestMethod]
    public void TheKeyboardOfAWindowThatIsUsedAgain_IsGivenToItsPage_OnlyWhenTheWindowItselfHoldsIt()
    {
        const nint Window = 0x1234, Page = 0x5678;
        var holder = Window;
        var given = 0;
        var focus = new DesktopWindowFocus(() => Window, () => holder, () => { given++; return true; });

        // Windows gave the keyboard to the window that was activated: the page takes it.
        Assert.IsTrue(focus.Restore());
        Assert.AreEqual(1, given);

        // The page has it already, or a native control of the window does: nothing is taken from it.
        holder = Page;
        Assert.IsFalse(focus.Restore());
        holder = 0;
        Assert.IsFalse(focus.Restore(), "no window of this thread holds the keyboard");
        Assert.AreEqual(1, given);
    }

    [TestMethod]
    public void AWindowThatIsGone_OrABrowserThatRefuses_LeavesTheKeyboardWhereItIs()
    {
        var given = 0;
        Assert.IsFalse(new DesktopWindowFocus(() => 0, () => 0, () => { given++; return true; }).Restore(), "a closed window has no handle");
        Assert.AreEqual(0, given);

        Assert.IsFalse(new DesktopWindowFocus(() => 1, () => 1, () => false).Restore(), "the browser refused");
        Assert.IsFalse(new DesktopWindowFocus(() => 1, () => 1, () => throw new NotSupportedException("no WebView2 here")).Restore());
        Assert.IsFalse(new DesktopWindowFocus(() => throw new ObjectDisposedException("window"), () => 1, () => true).Restore());
    }
}
