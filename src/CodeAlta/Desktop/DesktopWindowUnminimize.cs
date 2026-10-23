using NeoAstra;

namespace CodeAlta.Desktop;

/// <summary>
/// Shows a window again, and brings a minimized one back in the state it had before it was minimized: maximized,
/// fullscreen or normal. A maximized window that was minimized came back in its normal state, with the smaller
/// bounds it had before it was maximized, when it was shown again (a click on its icon in the Dock, the tray, a
/// second start of the application): <see cref="NeoWindow.Restore"/> asks for the normal state, and on macOS
/// <see cref="NeoWindow.Show"/> of a minimized window brings it back in its normal state too.
/// </summary>
/// <remarks>Used on the thread of the window, where its state changes are raised.</remarks>
internal sealed class DesktopWindowUnminimize
{
    private NeoWindowState _before;

    /// <summary>Creates what remembers the state of a window.</summary>
    /// <param name="state">The state the window is in now; a minimized one goes back to the normal state.</param>
    internal DesktopWindowUnminimize(NeoWindowState state) => _before = state == NeoWindowState.Minimized ? NeoWindowState.Normal : state;

    /// <summary>The state the window goes back to when it is minimized: the one it has, or the one it had before it was minimized.</summary>
    internal NeoWindowState Before => _before;

    /// <summary>Remembers the state of a window from now on.</summary>
    /// <param name="window">The window.</param>
    /// <returns>What shows the window again in the state it had before it was minimized.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="window"/> is null.</exception>
    internal static DesktopWindowUnminimize Follow(NeoWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var unminimize = new DesktopWindowUnminimize(window.State);
        window.StateChanged += (_, change) => unminimize.Changed(change.OldState, change.NewState);
        return unminimize;
    }

    /// <summary>Follows a change of the state of the window.</summary>
    /// <param name="oldState">The state it had.</param>
    /// <param name="newState">The state it has now.</param>
    internal void Changed(NeoWindowState oldState, NeoWindowState newState)
    {
        if (newState != NeoWindowState.Minimized) _before = newState;
        else if (oldState != NeoWindowState.Minimized) _before = oldState;
    }

    /// <summary>Shows the window, and brings it back in the state it had before it was minimized when it is minimized.</summary>
    /// <param name="window">The window this follows.</param>
    /// <exception cref="ArgumentNullException"><paramref name="window"/> is null.</exception>
    internal void Show(NeoWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        // Read before the window is shown: bringing it back changes its state, and so what is remembered.
        NeoWindowState? before = window.State == NeoWindowState.Minimized ? _before : null;
        window.Show();
        if (before is not { } state) return;
        if (window.State == NeoWindowState.Minimized) window.Restore();
        if (state != NeoWindowState.Normal && window.State != state) window.State = state;
    }
}
