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
    public void Restore_KeepsAWindowOverTwoDisplaysWhereItWas()
    {
        // The right third of the left display and all of the right one, as a window stretched over both is.
        NeoDisplaySnapshot left = Display("left", 0, 0, 3840, 2160), right = Display("right", 3840, 0, 3840, 2160, primary: false);
        var over = DesktopWindowState.Restore(Placement(2688, 0, 4992, 2120), [left, right])!.Value;
        Assert.AreEqual(new NeoPoint(2688, 0), over.Position);
        Assert.AreEqual(new NeoSize(4992, 2120), over.Size);

        // The left display is gone: the window comes back whole on the one that is left.
        var alone = DesktopWindowState.Restore(Placement(2688, 0, 4992, 2120), [right])!.Value;
        Assert.AreEqual(new NeoPoint(3840, 0), alone.Position);
        Assert.AreEqual(new NeoSize(3840, 2120), alone.Size);
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

    // A display of the native library, in the units of the windows, with a work area 40 units shorter.
    private static NeoDisplay Monitor(string id, int x, int y, int width, int height, bool primary = false, double scale = 1)
        => new(id, id.ToUpperInvariant(), new(x, y, width, height), new(x, y, width, height - 40), scale, primary) { IsIdPersistent = true, IsWorkAreaReported = true };

    private static NeoWindowPlacement PlacementOn(string? display, int x, int y, int width, int height, double scale = 1)
        => new(new(x, y, width, height), NeoWindowState.Normal, display, scale, true);

    [TestMethod]
    public void Restore_GoesBackToTheDisplayItWasSavedOn()
    {
        NeoDisplay[] displays = [Monitor("left", 0, 0, 1920, 1080, primary: true), Monitor("right", 1920, 0, 2560, 1440)];
        var restored = DesktopWindowState.Restore(PlacementOn("right", 2100, 100, 1280, 800), displays, pinnedDisplayId: null)!.Value;
        Assert.AreEqual(new NeoPoint(2100, 100), restored.Position);
        Assert.AreEqual(new NeoSize(1280, 800), restored.Size);
    }

    [TestMethod]
    public void Restore_NeverOpensAWindowWhereNoDisplayIs()
    {
        // The display at the right is not connected any more: the window comes back on the one that is, whole.
        NeoDisplay[] left = [Monitor("left", 0, 0, 1920, 1080, primary: true)];
        var restored = DesktopWindowState.Restore(PlacementOn("right", 2100, 100, 1280, 800), left, pinnedDisplayId: null)!.Value;
        Assert.IsTrue(Inside(restored, left[0].WorkArea), $"{restored} is on the display there is");

        // A display above the primary one that is gone, and a window much larger than the one that is left.
        var huge = DesktopWindowState.Restore(PlacementOn("top", -500, -1600, 5000, 3000), left, pinnedDisplayId: null)!.Value;
        Assert.IsTrue(Inside(huge, left[0].WorkArea), $"{huge} fits the display there is");

        // A pin to a display that is not connected changes nothing of that.
        var pinned = DesktopWindowState.Restore(PlacementOn("right", 2100, 100, 1280, 800), left, pinnedDisplayId: "right")!.Value;
        Assert.IsTrue(Inside(pinned, left[0].WorkArea));
    }

    [TestMethod]
    public void Restore_GoesToThePinnedDisplayWhenItIsConnected()
    {
        NeoDisplay[] displays = [Monitor("left", 0, 0, 1920, 1080, primary: true), Monitor("right", 1920, 0, 2560, 1440)];
        // Saved on the left one, pinned to the right one: in the middle of the right one, with its size.
        var restored = DesktopWindowState.Restore(PlacementOn("left", 100, 100, 1280, 800), displays, pinnedDisplayId: "right")!.Value;
        Assert.IsTrue(Inside(restored, displays[1].WorkArea), $"{restored} is on the pinned display");
        Assert.AreEqual(new NeoSize(1280, 800), restored.Size);

        // Saved on the pinned display: where it was there.
        Assert.AreEqual(new NeoPoint(2100, 100), DesktopWindowState.Restore(PlacementOn("right", 2100, 100, 1280, 800), displays, "right")!.Value.Position);
    }

    [TestMethod]
    public void Restore_FitsAWindowSavedOnADisplayOfAnotherScaleOrResolution()
    {
        // The same monitor, at a lower resolution and a higher scale: the window still fits in its work area.
        NeoDisplay[] smaller = [Monitor("one", 0, 0, 1280, 720, primary: true, scale: 1.5)];
        var restored = DesktopWindowState.Restore(PlacementOn("one", 1000, 600, 1600, 1000, scale: 1), smaller, pinnedDisplayId: null)!.Value;
        Assert.IsTrue(Inside(restored, smaller[0].WorkArea), $"{restored} fits the display");
    }

    private static bool Inside(DesktopWindowState.Restored restored, NeoRect area)
        => restored.Position is { } position && position.X >= area.X && position.Y >= area.Y
            && (long)position.X + restored.Size.Width <= (long)area.X + area.Width && (long)position.Y + restored.Size.Height <= (long)area.Y + area.Height;

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
