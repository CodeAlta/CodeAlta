using CodeAlta.Desktop;
using CodeAlta.Orchestration.Runtime;
using NeoAstra;
using NeoAstra.Desktop;
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
    public async Task WindowsTaskbarProgress_FollowsTheActivityItReads()
    {
        var states = new List<NeoWindowProgressState>();
        var running = false;
        await using var progress = new DesktopWindowsTaskbarProgress((state, _) =>
        {
            states.Add(state);
            return ValueTask.FromResult(NeoDesktopStatus.Success);
        }, () => running, static () => true, static message => Assert.Fail(message));

        // Nothing runs, a session runs, it still runs at the next reading, then it ends.
        await progress.RefreshAsync();
        running = true;
        await progress.RefreshAsync();
        await progress.RefreshAsync();
        running = false;
        await progress.RefreshAsync();

        CollectionAssert.AreEqual(
            new[] { NeoWindowProgressState.None, NeoWindowProgressState.Indeterminate, NeoWindowProgressState.None },
            states);
    }

    [TestMethod]
    public async Task WindowsTaskbarProgress_SetsAgainAStateTheDesktopDidNotAccept()
    {
        var states = new List<NeoWindowProgressState>();
        var failures = new List<string>();
        var answers = new Queue<NeoDesktopStatus>([NeoDesktopStatus.Failed, NeoDesktopStatus.Failed, NeoDesktopStatus.Success]);
        await using var progress = new DesktopWindowsTaskbarProgress((state, _) =>
        {
            states.Add(state);
            return answers.TryDequeue(out var answer) ? ValueTask.FromResult(answer) : throw new InvalidOperationException("The taskbar is gone.");
        }, static () => true, static () => true, failures.Add);

        // Refused twice, accepted, then left alone: a failure is not remembered as the state of the taskbar.
        await progress.RefreshAsync();
        await progress.RefreshAsync();
        await progress.RefreshAsync();
        await progress.RefreshAsync();

        CollectionAssert.AreEqual(Enumerable.Repeat(NeoWindowProgressState.Indeterminate, 3).ToArray(), states);
        Assert.AreEqual(1, failures.Count, "A failure that lasts is reported once, not at each reading.");
        StringAssert.Contains(failures[0], "Failed");

        // A setter that throws does not stop the indicator either: disposal still asks to clear it.
        await progress.DisposeAsync();
        Assert.AreEqual(NeoWindowProgressState.None, states[^1]);
        Assert.AreEqual(2, failures.Count);
        StringAssert.Contains(failures[1], "The taskbar is gone.");
    }

    [TestMethod]
    public async Task WindowsTaskbarProgress_IsSetAgainOnTheButtonOfAWindowShownAgain()
    {
        var states = new List<NeoWindowProgressState>();
        var shown = false;
        var progress = new DesktopWindowsTaskbarProgress((state, _) =>
        {
            states.Add(state);
            return ValueTask.FromResult(NeoDesktopStatus.Success);
        }, static () => true, () => shown, static message => Assert.Fail(message));

        // A window that is not shown yet has no taskbar button.
        await progress.RefreshAsync();
        Assert.AreEqual(0, states.Count);

        // Shown: its button may not be there at the reading that sees it, so the next one sets the progress.
        shown = true;
        await progress.RefreshAsync();
        Assert.AreEqual(0, states.Count);
        await progress.RefreshAsync();
        await progress.RefreshAsync();
        CollectionAssert.AreEqual(new[] { NeoWindowProgressState.Indeterminate }, states);

        // Closed to the notification area while the session runs, then opened again: the new button has no progress.
        shown = false;
        await progress.RefreshAsync();
        shown = true;
        await progress.RefreshAsync();
        await progress.RefreshAsync();
        CollectionAssert.AreEqual(new[] { NeoWindowProgressState.Indeterminate, NeoWindowProgressState.Indeterminate }, states);

        // A hidden window has no progress to clear.
        shown = false;
        await progress.RefreshAsync();
        await progress.DisposeAsync();
        Assert.AreEqual(2, states.Count);
    }

    [TestMethod]
    public async Task WindowsTaskbarProgress_ReadsTheActivityByItselfAndClearsWhenDisposed()
    {
        var states = new List<NeoWindowProgressState>();
        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = false;
        var progress = new DesktopWindowsTaskbarProgress((state, _) =>
        {
            lock (states) states.Add(state);
            if (state == NeoWindowProgressState.Indeterminate) shown.TrySetResult();
            else cleared.TrySetResult();
            return ValueTask.FromResult(NeoDesktopStatus.Success);
        }, () => Volatile.Read(ref running), static () => true, static message => Assert.Fail(message), TimeSpan.FromMilliseconds(10));

        // A queued prompt that is sent, a run started by an agent: no event tells the indicator, it reads.
        await cleared.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Volatile.Write(ref running, true);
        await shown.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await progress.DisposeAsync();
        await progress.DisposeAsync();

        lock (states)
        {
            CollectionAssert.AreEqual(
                new[] { NeoWindowProgressState.None, NeoWindowProgressState.Indeterminate, NeoWindowProgressState.None },
                states);
        }
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
