using CodeAlta.Desktop;
using NeoAstra;
using NeoAstra.Desktop.SystemInfo;
using NeoAstra.Desktop.WindowState;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class DesktopWindowStateTests
{
    // A scale of 1 everywhere: a unit is a pixel, whatever the platform counts its windows in.
    private static NeoDisplaySnapshot Display(string id, int x, int y, int width, int height, bool primary = true)
        => new(id, new(x, y, width, height), new(x, y, width, height - 40), 1, primary, null, null);

    private static NeoWindowPlacement Placement(int x, int y, int width, int height, NeoWindowState state = NeoWindowState.Normal)
        => new(new(x, y, width, height), state, null, 1, true);

    [TestMethod]
    public void Restore_GivesBackTheStateAndTheBoundsTheWindowGoesBackTo()
    {
        NeoDisplaySnapshot[] displays = [Display("one", 0, 0, 1920, 1080)];

        // Maximized when the application ended: maximized again, with the size it is restored to behind it.
        Assert.AreEqual(new DesktopWindowState.Restored(new(200, 120), new(1280, 800), NeoWindowState.Maximized),
            DesktopWindowState.Restore(Placement(200, 120, 1280, 800, NeoWindowState.Maximized), displays));
        Assert.AreEqual(new DesktopWindowState.Restored(new(200, 120), new(1280, 800), NeoWindowState.Normal),
            DesktopWindowState.Restore(Placement(200, 120, 1280, 800), displays));
        Assert.AreEqual(NeoWindowState.Fullscreen, DesktopWindowState.Restore(Placement(200, 120, 1280, 800, NeoWindowState.Fullscreen), displays)!.Value.State);
    }

    [TestMethod]
    public void Restore_NeverShowsTheWindowMinimized()
    {
        var restored = DesktopWindowState.Restore(Placement(200, 120, 1280, 800, NeoWindowState.Minimized), [Display("one", 0, 0, 1920, 1080)]);
        Assert.AreEqual(NeoWindowState.Normal, restored!.Value.State);
        // Without displays too.
        Assert.AreEqual(NeoWindowState.Normal, DesktopWindowState.Restore(Placement(200, 120, 1280, 800, NeoWindowState.Minimized), [])!.Value.State);
    }

    [TestMethod]
    public void Restore_PutsTheWindowInsideADisplayThatIsThere()
    {
        // It was on a display at the left that is gone: it comes back on the one that is left, whole.
        var moved = DesktopWindowState.Restore(Placement(-1800, 100, 1280, 800, NeoWindowState.Maximized), [Display("one", 0, 0, 1920, 1080)])!.Value;
        Assert.AreEqual(new NeoPoint(0, 100), moved.Position);
        Assert.AreEqual(new NeoSize(1280, 800), moved.Size);
        Assert.AreEqual(NeoWindowState.Maximized, moved.State);

        // It was larger than the display it comes back on: it takes the work area of that display.
        var shrunk = DesktopWindowState.Restore(Placement(100, 100, 3000, 2000), [Display("small", 0, 0, 1366, 768)])!.Value;
        Assert.AreEqual(new NeoPoint(0, 0), shrunk.Position);
        Assert.AreEqual(new NeoSize(1366, 728), shrunk.Size);

        // With two displays it stays on the one it was on.
        NeoDisplaySnapshot[] two = [Display("left", 0, 0, 1920, 1080), Display("right", 1920, 0, 2560, 1440, primary: false)];
        Assert.AreEqual(new NeoPoint(2400, 200), DesktopWindowState.Restore(Placement(2400, 200, 1600, 1000), two)!.Value.Position);
    }

    [TestMethod]
    public void Restore_LeavesTheWindowWhereANewOneIsPutWhenTheDisplaysAreNotKnown()
    {
        var restored = DesktopWindowState.Restore(Placement(-1800, 100, 1280, 800, NeoWindowState.Maximized), [])!.Value;
        Assert.IsNull(restored.Position);
        Assert.AreEqual(new NeoSize(1280, 800), restored.Size);
        Assert.AreEqual(NeoWindowState.Maximized, restored.State);
    }

    [TestMethod]
    public void Restore_TakesNothingFromAPlacementThatIsNotOne()
    {
        Assert.IsNull(DesktopWindowState.Restore(Placement(0, 0, 0, 800), [Display("one", 0, 0, 1920, 1080)]));
        Assert.IsNull(DesktopWindowState.Restore(Placement(0, 0, 1280, 800), [new("broken", new(0, 0, 1920, 1080), new(0, 0, 0, 0), 1, true, null, null)]));
        Assert.ThrowsExactly<ArgumentNullException>(() => DesktopWindowState.Restore(null!, []));
        Assert.ThrowsExactly<ArgumentNullException>(() => DesktopWindowState.Restore(Placement(0, 0, 1280, 800), null!));
    }

    [TestMethod]
    public async Task ThePlacementIsKeptInOneFileOfTheDataRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-window-state-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new NeoJsonWindowStateStore(root);
            Assert.IsNull(await store.LoadAsync(DesktopWindowState.Key));
            var placement = Placement(200, 120, 1280, 800, NeoWindowState.Maximized);
            await store.SaveAsync(DesktopWindowState.Key, placement);
            Assert.IsTrue(File.Exists(Path.Combine(root, "window.json")));
            Assert.AreEqual(placement, await new NeoJsonWindowStateStore(root).LoadAsync(DesktopWindowState.Key));

            // A file that is not a placement is as if there were none: the window is placed as a new one.
            await File.WriteAllTextAsync(Path.Combine(root, "window.json"), "{\"normalBounds\":");
            Assert.IsNull(await store.LoadAsync(DesktopWindowState.Key));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
