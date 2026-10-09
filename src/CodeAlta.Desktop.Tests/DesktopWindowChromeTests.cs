using CodeAlta.Desktop;
using CodeAlta.Orchestration.Runtime;
using NeoAstra;
using NeoAstra.Desktop.WindowState;
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
    public void WindowIcon_IsThePaddedPictureOnMacOS()
    {
        // The Dock draws a picture set by the application as large as its canvas: the full-bleed tile of the other
        // desktops looks oversized there. A PNG, because the window services refuse any file but .ico and .png.
        Assert.AreEqual("alta-dock.png", DesktopWindowChrome.WindowIconFile(windows: false, macOS: true));
        Assert.AreEqual("alta.ico", DesktopWindowChrome.WindowIconFile(windows: true, macOS: false));
        Assert.AreEqual("alta.png", DesktopWindowChrome.WindowIconFile(windows: false, macOS: false));
        // An icon family (.icns) was refused without a word, and the Dock showed the icon of a plain executable.
        foreach (var (windows, macOS) in new[] { (true, false), (false, true), (false, false) })
        {
            var file = DesktopWindowChrome.WindowIconFile(windows, macOS);
            CollectionAssert.Contains(new[] { ".ico", ".png" }, Path.GetExtension(file), file);
            Assert.IsTrue(File.Exists(Path.Combine(AppContext.BaseDirectory, file)), file);
        }
    }

    [TestMethod]
    public void WindowIcon_IsLeftToTheBundleOnMacOS()
    {
        // The Dock draws the icon of the bundle, which macOS 26 shapes like its neighbours: a picture set by the application would replace it.
        Assert.IsFalse(DesktopWindowChrome.AppliesWindowIcon(macOS: true, DesktopIntegration.MacBundleIdentifier, bundleIconChanged: false));
        Assert.IsTrue(DesktopWindowChrome.AppliesWindowIcon(macOS: true, bundleIdentifier: null, bundleIconChanged: false));
        Assert.IsTrue(DesktopWindowChrome.AppliesWindowIcon(macOS: false, bundleIdentifier: null, bundleIconChanged: false));
        // The Dock keeps the picture it has of an application that runs: the start that replaced the icon of the
        // bundle (the first one after an update) sets the picture itself, until the next start shows the new icon.
        Assert.IsTrue(DesktopWindowChrome.AppliesWindowIcon(macOS: true, DesktopIntegration.MacBundleIdentifier, bundleIconChanged: true));
        // A test host is started by its executable, as a build output or `alta` in a terminal is.
        if (OperatingSystem.IsMacOS()) Assert.IsNull(DesktopIntegration.MacRunningBundleIdentifier());
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
        // The history of the view holds the start-up screen, which nothing leaves: back never loads it again.
        Assert.IsFalse(options.BrowserFeatures.HistoryNavigation);
        Assert.IsTrue(options.BrowserFeatures.TabFocusesLinks);
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

    [TestMethod]
    public async Task WindowsTaskbarProgress_TracksSimulatedSessionActivity()
    {
        var states = new List<NeoWindowProgressState>();
        await using var progress = new DesktopWindowsTaskbarProgress((state, _) =>
        {
            states.Add(state);
            return Task.CompletedTask;
        });

        // An idle runtime, one running session, another notification while it is running, then completion.
        await progress.SetActivityAsync(hasRunningSessions: false);
        await progress.SetActivityAsync(hasRunningSessions: true);
        await progress.SetActivityAsync(hasRunningSessions: true);
        await progress.SetActivityAsync(hasRunningSessions: false);

        CollectionAssert.AreEqual(
            new[] { NeoWindowProgressState.None, NeoWindowProgressState.Indeterminate, NeoWindowProgressState.None },
            states);
    }

    [TestMethod]
    public async Task WindowsTaskbarProgress_DisposalClearsAnActiveIndicator()
    {
        var states = new List<NeoWindowProgressState>();
        var progress = new DesktopWindowsTaskbarProgress((state, _) =>
        {
            states.Add(state);
            return Task.CompletedTask;
        });

        await progress.SetActivityAsync(hasRunningSessions: true);
        await progress.DisposeAsync();
        await progress.DisposeAsync();

        CollectionAssert.AreEqual(
            new[] { NeoWindowProgressState.Indeterminate, NeoWindowProgressState.None },
            states);
    }

    [TestMethod]
    public void WindowsTaskbarProgress_UsesRuntimeRunningStateAndIgnoresBackgroundTasksAlone()
    {
        // SessionRuntimeOverview.Running covers runs and queue drains; background tasks are reported separately.
        var idleWithBackgroundTask = new SessionRuntimeOverview("background", null, "Background", false, 1, false);
        var running = new SessionRuntimeOverview("running", null, "Running", true, 0, false);

        Assert.IsFalse(DesktopWindowsTaskbarProgress.HasRunningSessions([idleWithBackgroundTask]));
        Assert.IsTrue(DesktopWindowsTaskbarProgress.HasRunningSessions([idleWithBackgroundTask, running]));
        Assert.IsFalse(DesktopWindowsTaskbarProgress.HasRunningSessions([]));
    }
}
