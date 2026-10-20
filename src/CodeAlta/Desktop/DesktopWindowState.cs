using NeoAstra;
using NeoAstra.Desktop.SystemInfo;
using NeoAstra.Desktop.WindowState;
using XenoAtom.Logging;

namespace CodeAlta.Desktop;

/// <summary>
/// Keeps where a window is, its size and whether it is maximized or fullscreen, for the next start: a window that
/// was maximized when the application ended comes back maximized, and goes back to the size it had before when
/// it is restored.
/// </summary>
/// <remarks>
/// <para>
/// The placement of the main window is in <c>window.json</c> of the application data root, beside the
/// preferences, so the normal and the developer instance each have their own; a secondary window has a file of
/// its own, <c>window-&lt;id&gt;.json</c>. NeoAstra writes it a moment after the window was moved, resized,
/// maximized or restored (<see cref="NeoWindowStateController"/>), with the display the window was on, and once
/// more when the application ends.
/// </para>
/// <para>
/// It is given back before the window is shown: a hidden window takes its position and its size at once and
/// its state when it is shown, so the window appears where it was, never in its default place first. The
/// position is only given back when the displays are known, and then inside one of them: a window is not put
/// where a display used to be. A window that was minimized comes back in the state it had before.
/// </para>
/// </remarks>
internal sealed class DesktopWindowState : IAsyncDisposable
{
    /// <summary>The name of the placement of the main window in the store: its file is <c>window.json</c>.</summary>
    internal const string Key = "window";

    /// <summary>How long a start waits for the displays where the platform reports them a moment after it started.</summary>
    internal static readonly TimeSpan DisplayWait = TimeSpan.FromSeconds(1);

    /// <summary>How long the end of the application waits for the last placement to be written.</summary>
    internal static readonly TimeSpan LastWriteWait = TimeSpan.FromSeconds(2);

    private readonly NeoWindowStateController _controller;

    private DesktopWindowState(NeoWindowStateController controller) => _controller = controller;

    /// <summary>What a kept placement gives a new window.</summary>
    /// <param name="Position">Where the window goes, or null to leave it where a new window is put.</param>
    /// <param name="Size">The size of its content in its normal state.</param>
    /// <param name="State">The state it is shown in: normal, maximized or fullscreen.</param>
    internal readonly record struct Restored(NeoPoint? Position, NeoSize Size, NeoWindowState State);

    /// <summary>The key of the placement of a secondary window in the store: its file is <c>window-&lt;id&gt;.json</c>.</summary>
    /// <param name="windowId">The id of the window, letters, digits, dots, dashes and underscores.</param>
    internal static string KeyOf(string windowId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(windowId);
        return $"{Key}-{windowId}";
    }

    /// <summary>
    /// Gives the main window the placement that was kept for it, if any, and keeps the one it has from now on.
    /// Call it while the window is still hidden.
    /// </summary>
    /// <param name="window">The main window, not shown yet.</param>
    /// <param name="dataRoot">The application data root.</param>
    /// <param name="system">What reports the displays when the native library does not.</param>
    /// <returns>What keeps the placement until it is disposed, or null when it cannot be kept.</returns>
    /// <exception cref="ArgumentNullException">A required value is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="dataRoot"/> is blank.</exception>
    internal static ValueTask<DesktopWindowState?> StartAsync(NeoApplication application, NeoWindow window, string dataRoot, NeoSystemInfoService system)
        => StartAsync(application, window, dataRoot, Key, pinnedDisplayId: null, system);

    /// <summary>
    /// Gives a window the placement that was kept under a key, if any, and keeps the one it has from now on.
    /// Call it while the window is still hidden.
    /// </summary>
    /// <param name="application">The application of the window, which reports the displays.</param>
    /// <param name="window">The window, not shown yet.</param>
    /// <param name="dataRoot">The application data root.</param>
    /// <param name="key">The name of its placement in the store.</param>
    /// <param name="pinnedDisplayId">A display the window goes to whenever it is connected; null for where it was.</param>
    /// <param name="system">What reports the displays when the native library does not; null for none.</param>
    /// <returns>What keeps the placement until it is disposed, or null when it cannot be kept.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="application"/> or <paramref name="window"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="dataRoot"/> or <paramref name="key"/> is blank.</exception>
    internal static async ValueTask<DesktopWindowState?> StartAsync(NeoApplication application, NeoWindow window, string dataRoot, string key, string? pinnedDisplayId, NeoSystemInfoService? system)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(window);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        DesktopWindowState? kept = null;
        try
        {
            var store = new NeoJsonWindowStateStore(dataRoot);
            // Read off the window's thread; what follows is on it again.
            var saved = await Task.Run(async () => await store.LoadAsync(key).ConfigureAwait(false)).ConfigureAwait(true);
            Restored? restored = null;
            if (saved is not null)
            {
                // The displays of the native library are in the units of the window; the older snapshots are the fallback.
                var displays = Displays(application);
                if (displays.Count > 0) restored = Restore(saved, displays, pinnedDisplayId);
                else if (system is not null) restored = Restore(saved, await DisplaysAsync(system).ConfigureAwait(true));
                else restored = Restore(saved, Array.Empty<NeoDisplay>(), pinnedDisplayId);
            }
            else if (pinnedDisplayId is not null && Displays(application).FirstOrDefault(display => display.Id == pinnedDisplayId) is { } pinned)
            {
                // A window that was never placed and is pinned to a display opens in the middle of it.
                var area = pinned.WorkArea;
                var (width, height) = DesktopWindowPlacement.Size(area.Width, area.Height);
                restored = new(new(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2), new(width, height), NeoWindowState.Normal);
            }
            if (window.IsClosed) return null;
            if (restored is { } placement)
            {
                if (placement.Position is { } position) window.Bounds = new(position.X, position.Y, placement.Size.Width, placement.Size.Height);
                else window.ClientSize = placement.Size;
            }
            // The controller takes the bounds the window has now for the ones it goes back to: it starts before the state is given.
            kept = new(new NeoWindowStateController(window, store, key));
            if (restored is { State: not NeoWindowState.Normal } other) window.State = other.State;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The window keeps the placement it has, which is the default one unless it was already given another.
            LogManager.GetLogger("CodeAlta.Desktop").Warn($"The placement of the window could not be given back: {exception.Message}");
        }
        return kept;
    }

    /// <summary>The displays the native library reports, in the units of the windows; empty when it reports none.</summary>
    internal static IReadOnlyList<NeoDisplay> Displays(NeoApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);
        try { return application.GetDisplays(); }
        catch (Exception exception) when (exception is InvalidOperationException or NeoAstraException) { return []; }
    }

    /// <summary>
    /// What a kept placement gives a new window on the displays there are now: its size and its state, and its
    /// position when the displays are known, moved and shrunk to fit the ones it is on.
    /// </summary>
    /// <param name="saved">The placement that was kept.</param>
    /// <param name="displays">The displays there are now; empty when the platform does not report them.</param>
    /// <returns>What to give the window, or null when the placement is not one.</returns>
    /// <exception cref="ArgumentNullException">A value is null.</exception>
    internal static Restored? Restore(NeoWindowPlacement saved, IReadOnlyList<NeoDisplaySnapshot> displays)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(displays);
        try
        {
            // A window that was minimized comes back in its normal state, here as on a display that is still there.
            var placement = NeoWindowStateRestore.Clamp(saved, displays);
            return new(displays.Count == 0 ? null : placement.NormalBounds.Position, placement.NormalBounds.Size, placement.State);
        }
        catch (ArgumentException)
        {
            return null; // A placement or a display that is malformed: the window is placed as a new one.
        }
    }

    /// <summary>
    /// What a kept placement gives a new window on the displays there are now, in the units of the windows: the
    /// display it was saved on while that one is connected, the one it overlapped most otherwise, or the primary
    /// one; and the display it is pinned to before all of them, while that one is connected.
    /// </summary>
    /// <param name="saved">The placement that was kept.</param>
    /// <param name="displays">The displays there are now; empty when the platform does not report them.</param>
    /// <param name="pinnedDisplayId">A display the window goes to when it is connected; null for none.</param>
    /// <returns>What to give the window, or null when the placement is not one.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="saved"/> or <paramref name="displays"/> is null.</exception>
    internal static Restored? Restore(NeoWindowPlacement saved, IReadOnlyList<NeoDisplay> displays, string? pinnedDisplayId)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(displays);
        try
        {
            var placement = saved;
            // A window pinned to a display that is connected goes to it: at its place there when it was saved on it,
            // otherwise in the middle of it, with the size it had.
            if (pinnedDisplayId is not null && saved.DisplayId != pinnedDisplayId && displays.FirstOrDefault(display => display.Id == pinnedDisplayId) is { } pinned)
            {
                var area = pinned.WorkArea;
                var width = Math.Min(saved.NormalBounds.Width, area.Width);
                var height = Math.Min(saved.NormalBounds.Height, area.Height);
                placement = new NeoWindowPlacement(new(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height),
                    saved.State, pinned.Id, pinned.ScaleFactor, saved.WasVisible);
            }
            var clamped = NeoWindowStateRestore.Clamp(placement, displays);
            return new(displays.Count == 0 ? null : clamped.NormalBounds.Position, clamped.NormalBounds.Size, clamped.State);
        }
        catch (ArgumentException)
        {
            return null; // A placement or a display that is malformed: the window is placed as a new one.
        }
    }

    // The displays, waited for a moment where the platform reads them after the start (macOS, Linux).
    private static async ValueTask<IReadOnlyList<NeoDisplaySnapshot>> DisplaysAsync(NeoSystemInfoService system)
    {
        if (system.Displays.Count > 0) return system.Displays;
        var reported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(object? sender, IReadOnlyList<NeoDisplaySnapshot> displays) => reported.TrySetResult();
        system.DisplaysChanged += Changed;
        try
        {
            if (system.Displays.Count == 0) await Task.WhenAny(reported.Task, Task.Delay(DisplayWait)).ConfigureAwait(true);
        }
        finally
        {
            system.DisplaysChanged -= Changed;
        }
        return system.Displays;
    }

    /// <summary>Writes the placement the window has now and stops keeping it. Never throws, and never waits long.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await _controller.DisposeAsync().AsTask().WaitAsync(LastWriteWait).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The placement written after the last change of the window stays.
        }
    }
}
