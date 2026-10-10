using System.Runtime.InteropServices;
using NeoAstra;

namespace CodeAlta.Desktop;

/// <summary>
/// The color Windows fills the main window with where nothing has drawn yet: the part a resize uncovers, until the
/// view covers it. NeoAstra registers its window class with the system's window color, white, and does not paint
/// the background it is given, so a resize showed white edges in a dark theme. The class is given a brush of the
/// page's background instead, and another one when the page changes its theme. Elsewhere this does nothing.
/// </summary>
/// <remarks>
/// The brush belongs to the window class, so it is the one of every window of that class in the process; the
/// application has one main window. The brush in use is kept for the life of the process, as the class uses it;
/// a brush that is replaced is deleted.
/// </remarks>
internal sealed class DesktopWindowBackground
{
    private const int ClassBackground = -10; // GCLP_HBRBACKGROUND

    private readonly Func<nint> _handle;
    private nint _brush;

    /// <summary>Creates the background of a window of the application.</summary>
    /// <param name="window">The window.</param>
    /// <exception cref="ArgumentNullException"><paramref name="window"/> is null.</exception>
    internal DesktopWindowBackground(NeoWindow window)
        : this(() => window.IsClosed ? 0 : window.GetNativeHandle(NeoNativeHandleKind.Win32Hwnd).Value)
    {
        ArgumentNullException.ThrowIfNull(window);
    }

    /// <summary>Creates the background of the native window a function gives, 0 when there is none.</summary>
    /// <param name="handle">Gives the handle of the window.</param>
    /// <exception cref="ArgumentNullException"><paramref name="handle"/> is null.</exception>
    internal DesktopWindowBackground(Func<nint> handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        _handle = handle;
    }

    /// <summary>
    /// Makes Windows fill what the window has not drawn in a color. Called on the thread of the window; a
    /// failure leaves the previous color.
    /// </summary>
    /// <param name="color">The color, whose transparency is ignored.</param>
    /// <returns>True when the window now uses that color.</returns>
    internal bool Apply(NeoColor color)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess) return false;
        try
        {
            var handle = _handle();
            if (handle == 0) return false;
            var brush = CreateSolidBrush(ColorReference(color));
            if (brush == 0) return false;
            if (SetClassLongPtrW(handle, ClassBackground, brush) == 0 && Marshal.GetLastPInvokeError() != 0)
            {
                DeleteObject(brush);
                return false;
            }
            if (_brush != 0) DeleteObject(_brush);
            _brush = brush;
            return true;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException or NotSupportedException)
        {
            return false; // The window keeps the color it has.
        }
    }

    /// <summary>The color as Windows writes it: <c>0x00BBGGRR</c>.</summary>
    internal static uint ColorReference(NeoColor color) => color.Red | (uint)color.Green << 8 | (uint)color.Blue << 16;

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern nint CreateSolidBrush(uint color);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern bool DeleteObject(nint handle);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern nint SetClassLongPtrW(nint window, int index, nint value);
}
