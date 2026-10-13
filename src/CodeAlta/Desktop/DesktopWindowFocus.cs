using System.Runtime.InteropServices;
using NeoAstra;

namespace CodeAlta.Desktop;

/// <summary>
/// Gives the keyboard back to the page when the main window is used again. On Windows a window that is activated
/// gives the keyboard to itself, and NeoAstra does not pass it on to the browser inside it: after a switch to
/// another application and back, nothing could be typed in the prompt until the page was clicked. The page is given
/// the keyboard when its window holds it, and the browser then gives it to what had it. Elsewhere this does nothing.
/// </summary>
internal sealed class DesktopWindowFocus
{
    private const int MoveFocusSlot = 12; // ICoreWebView2Controller::MoveFocus, after the three methods of IUnknown.
    private const int Programmatic = 0; // COREWEBVIEW2_MOVE_FOCUS_REASON_PROGRAMMATIC: the page keeps what had the focus.

    private readonly Func<nint> _window;
    private readonly Func<nint> _keyboard;
    private readonly Func<bool> _give;

    /// <summary>Creates the focus of a window over what a test gives.</summary>
    /// <param name="window">Gives the handle of the window, 0 when there is none.</param>
    /// <param name="keyboard">Gives the handle of the window that holds the keyboard in this thread, 0 when none does.</param>
    /// <param name="give">Gives the keyboard to the page; false when it could not.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    internal DesktopWindowFocus(Func<nint> window, Func<nint> keyboard, Func<bool> give)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(keyboard);
        ArgumentNullException.ThrowIfNull(give);
        (_window, _keyboard, _give) = (window, keyboard, give);
    }

    /// <summary>Gives the keyboard to the page of a window each time the window is used again.</summary>
    /// <param name="window">The main window.</param>
    /// <param name="view">The view that fills it.</param>
    /// <param name="dispatcher">The dispatcher of the thread of the window.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    internal static void Follow(NeoWindow window, global::NeoAstra.NeoAstra view, NeoDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(dispatcher);
        if (!OperatingSystem.IsWindows()) return;
        var focus = new DesktopWindowFocus(
            () => window.IsClosed ? 0 : window.GetNativeHandle(NeoNativeHandleKind.Win32Hwnd).Value,
            GetFocus,
            () => MoveFocus(view.GetNativeHandle(NeoNativeHandleKind.WebView2Controller).Value));
        window.FocusChanged += (_, _) =>
        {
            if (!window.IsFocused || window.IsClosed) return;
            // The window is told it is used before Windows gives it the keyboard: the page takes it once that is done.
            try { dispatcher.Post(() => focus.Restore()); }
            catch (ObjectDisposedException) { /* The application is ending. */ }
        };
    }

    /// <summary>Gives the keyboard to the page when the window itself holds it.</summary>
    /// <returns>
    /// True when the page was given the keyboard. False when there is no window, when something else holds the
    /// keyboard (the page already, or a native control), or when the browser refused.
    /// </returns>
    internal bool Restore()
    {
        try
        {
            var window = _window();
            return window != 0 && _keyboard() == window && _give();
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or ObjectDisposedException
            or DllNotFoundException or EntryPointNotFoundException)
        {
            return false; // The window is closing, or the view is not a WebView2: the keyboard stays where it is.
        }
    }

    // The controller is a COM object borrowed from NeoAstra: its method is called through its table, without a wrapper.
    private static bool MoveFocus(nint controller)
    {
        if (controller == 0) return false;
        var method = Marshal.ReadIntPtr(Marshal.ReadIntPtr(controller), MoveFocusSlot * nint.Size);
        return Marshal.GetDelegateForFunctionPointer<MoveFocusMethod>(method)(controller, Programmatic) >= 0;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int MoveFocusMethod(nint controller, int reason);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetFocus();
}
