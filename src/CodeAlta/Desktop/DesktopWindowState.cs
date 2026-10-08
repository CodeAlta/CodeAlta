using NeoAstra;
using NeoAstra.Desktop.SystemInfo;
using NeoAstra.Desktop.WindowState;
using XenoAtom.Logging;

namespace CodeAlta.Desktop;

/// <summary>
/// Keeps where the main window is, its size and whether it is maximized or fullscreen, for the next start: a
/// window that was maximized when the application ended comes back maximized, and goes back to the size it had
/// before when it is restored.
/// </summary>
/// <remarks>
/// <para>
/// The placement is in <c>window.json</c> of the application data root, beside the preferences, so the normal
/// and the developer instance each have their own. NeoAstra writes it a moment after the window was moved,
/// resized, maximized or restored (<see cref="NeoWindowStateController"/>), and once more when the application
/// ends.
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
    /// <summary>The name of the placement in the store: its file is <c>window.json</c>.</summary>
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

    /// <summary>
    /// Gives the window the placement that was kept for it, if any, and keeps the one it has from now on.
    /// Call it while the window is still hidden.
    /// </summary>
    /// <param name="window">The main window, not shown yet.</param>
    /// <param name="dataRoot">The application data root.</param>
    /// <param name="system">What reports the displays.</param>
    /// <returns>What keeps the placement until it is disposed, or null when it cannot be kept.</returns>
    /// <exception cref="ArgumentNullException">A required value is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="dataRoot"/> is blank.</exception>
    internal static async ValueTask<DesktopWindowState?> StartAsync(NeoWindow window, string dataRoot, NeoSystemInfoService system)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentNullException.ThrowIfNull(system);
        DesktopWindowState? kept = null;
        try
        {
            var store = new NeoJsonWindowStateStore(dataRoot);
            // Read off the window's thread; what follows is on it again.
            var saved = await Task.Run(async () => await store.LoadAsync(Key).ConfigureAwait(false)).ConfigureAwait(true);
            var restored = saved is null ? null : Restore(saved, await DisplaysAsync(system).ConfigureAwait(true));
            if (window.IsClosed) return null;
            if (restored is { } placement)
            {
                if (placement.Position is { } position) window.Position = position;
                window.ClientSize = placement.Size;
            }
            // The controller takes the bounds the window has now for the ones it goes back to: it starts before the state is given.
            kept = new(new NeoWindowStateController(window, store, Key));
            if (restored is { State: not NeoWindowState.Normal } other) window.State = other.State;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The window keeps the placement it has, which is the default one unless it was already given another.
            LogManager.GetLogger("CodeAlta.Desktop").Warn($"The placement of the window could not be given back: {exception.Message}");
        }
        return kept;
    }

    /// <summary>
    /// What a kept placement gives a new window on the displays there are now: its size and its state, and its
    /// position when the displays are known, moved and shrunk to fit the one it is on.
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
