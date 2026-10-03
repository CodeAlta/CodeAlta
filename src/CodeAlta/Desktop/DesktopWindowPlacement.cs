using System.Runtime.InteropServices;
using NeoAstra;

namespace CodeAlta.Desktop;

/// <summary>Computes the initial main-window placement: a share of the primary work area, centered.</summary>
internal static class DesktopWindowPlacement
{
    private const double WorkAreaShare = 0.8;
    private const int FallbackWidth = 1280;
    private const int FallbackHeight = 860;
    private const int MinimumWidth = 960;
    private const int MinimumHeight = 640;

    /// <summary>Applies the default size and centered startup location to the main-window options.</summary>
    internal static NeoWindowOptions Apply(NeoWindowOptions window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var (width, height) = TryGetPrimaryWorkArea(out var area) ? Size(area.Width, area.Height) : (FallbackWidth, FallbackHeight);
        window.Width = width;
        window.Height = height;
        window.StartupLocation = NeoWindowStartupLocation.Center;
        return window;
    }

    /// <summary>Returns the default window size for a work area, never larger than the area itself.</summary>
    internal static (int Width, int Height) Size(int workAreaWidth, int workAreaHeight)
    {
        if (workAreaWidth <= 0 || workAreaHeight <= 0) return (FallbackWidth, FallbackHeight);
        return (Math.Min(workAreaWidth, Math.Max(MinimumWidth, (int)(workAreaWidth * WorkAreaShare))),
            Math.Min(workAreaHeight, Math.Max(MinimumHeight, (int)(workAreaHeight * WorkAreaShare))));
    }

    private static bool TryGetPrimaryWorkArea(out (int Width, int Height) area)
    {
        area = default;
        // NeoAstra exposes no display metrics yet; other platforms keep the fallback size.
        if (!OperatingSystem.IsWindows()) return false;
        var rectangle = default(Rectangle);
        if (SystemParametersInfoW(GetWorkArea, 0, ref rectangle, 0) == 0) return false;
        area = (rectangle.Right - rectangle.Left, rectangle.Bottom - rectangle.Top);
        return area.Width > 0 && area.Height > 0;
    }

    private const uint GetWorkArea = 0x0030;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int SystemParametersInfoW(uint action, uint parameter, ref Rectangle value, uint update);
}
