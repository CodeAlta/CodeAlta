using System.Runtime.InteropServices;
using NeoAstra;

namespace CodeAlta.Desktop;

/// <summary>
/// Shows the main window without the white frame a new window has before its view has drawn. On Windows
/// the window is shown cloaked: it exists for the browser, which draws the start-up screen into it, and the
/// user sees it only once that is done. Elsewhere, and when cloaking is refused, it is simply shown.
/// </summary>
internal static class DesktopWindowReveal
{
    private const uint Cloak = 13; // DWMWA_CLOAK

    /// <summary>Shows the window so its view can draw, hidden from the user where the platform allows.</summary>
    /// <returns>True when the window is cloaked and <see cref="Reveal"/> must follow.</returns>
    internal static bool ShowCloaked(NeoWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var cloaked = SetCloak(window, true);
        window.Show();
        return cloaked;
    }

    /// <summary>Lets the user see a window shown by <see cref="ShowCloaked"/>.</summary>
    internal static void Reveal(NeoWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!window.IsClosed) SetCloak(window, false);
    }

    private static bool SetCloak(NeoWindow window, bool cloak)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var handle = window.GetNativeHandle(NeoNativeHandleKind.Win32Hwnd).Value;
            var value = cloak ? 1 : 0;
            return handle != 0 && DwmSetWindowAttribute(handle, Cloak, ref value, sizeof(int)) == 0;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException or NotSupportedException)
        {
            return false; // The window is shown as it is.
        }
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(nint window, uint attribute, ref int value, int size);
}
