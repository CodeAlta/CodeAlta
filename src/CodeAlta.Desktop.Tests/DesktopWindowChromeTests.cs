using CodeAlta.Desktop;
using NeoAstra;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class DesktopWindowChromeTests
{
    [TestMethod]
    public void WindowOptions_HandTheTitleBarToThePage()
    {
        var options = DesktopWindowChrome.WindowOptions();
        Assert.AreEqual("main", options.Label);
        Assert.AreEqual("CodeAlta", options.Title);
        Assert.IsFalse(options.IsVisible);
        Assert.AreEqual(NeoWindowTitleBarStyle.Overlay, options.TitleBar.Style);
        Assert.AreEqual(DesktopWindowChrome.TitleBarHeight, options.TitleBar.Height);
        Assert.AreEqual(NeoWindowStartupLocation.Center, options.StartupLocation);
    }

    [TestMethod]
    public void WindowIcon_IsThePaddedIconFamilyOnMacOS()
    {
        // The Dock draws an icon as large as its canvas: the full-bleed tile of the other desktops looks oversized there.
        Assert.AreEqual("alta.icns", DesktopWindowChrome.WindowIconFile(windows: false, macOS: true));
        Assert.AreEqual("alta.ico", DesktopWindowChrome.WindowIconFile(windows: true, macOS: false));
        Assert.AreEqual("alta.png", DesktopWindowChrome.WindowIconFile(windows: false, macOS: false));
    }

    [TestMethod]
    public void ViewOptions_ShowTheMainViewAsAnApplicationShell()
    {
        var options = DesktopWindowChrome.ViewOptions();
        Assert.AreEqual("main", options.ViewLabel);
        Assert.IsFalse(options.BrowserFeatures.AcceleratorKeys);
        Assert.IsFalse(options.BrowserFeatures.ContextMenus);
        Assert.IsFalse(options.BrowserFeatures.StatusBar);
        Assert.IsFalse(options.BrowserFeatures.ZoomControls);
    }

    [TestMethod]
    public void Capabilities_ResolveForThisPlatform()
    {
        var capabilities = DesktopWindowChrome.ResolveCapabilities();
        Assert.AreEqual(NeoSecurityProfile.ProductionLocalApp, capabilities.Profile);
    }

    [TestMethod]
    public async Task Services_StartBelowTheDataRootOnly()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-chrome-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (DesktopWindowChrome.CreateServices(root, dispatcher: null))
                Assert.IsTrue(Directory.Exists(Path.Combine(root, "desktop")));
            Assert.ThrowsExactly<ArgumentException>(() => DesktopWindowChrome.CreateServices(" ", dispatcher: null));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
