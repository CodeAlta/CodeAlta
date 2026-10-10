using System.Runtime.InteropServices;
using CodeAlta.Desktop;
using NeoAstra;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class DesktopWindowBackgroundTests
{
    private const int ClassBackground = -10; // GCLP_HBRBACKGROUND
    private const int WindowColor = 5; // COLOR_WINDOW

    [TestMethod]
    public void Color_IsWrittenAsWindowsWritesIt()
        => Assert.AreEqual(0x00272119u, DesktopWindowBackground.ColorReference(new NeoColor(0x19, 0x21, 0x27, 0x80)));

    [TestMethod]
    public void Window_FillsWhatItHasNotDrawnInTheThemeBackground_AndFollowsAChangeOfTheme()
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess) Assert.Inconclusive("The class brush of a window exists on 64-bit Windows only.");
        // A window class as NeoAstra registers it: erased with the system's window color, white.
        var name = "CodeAlta.Tests.Background." + Guid.NewGuid().ToString("N");
        var procedure = GetProcAddress(GetModuleHandleW("user32.dll"), "DefWindowProcW");
        var registration = new WindowClass
        {
            Size = (uint)Marshal.SizeOf<WindowClass>(), Procedure = procedure, Instance = GetModuleHandleW(null),
            Background = WindowColor + 1, ClassName = name,
        };
        Assert.AreNotEqual(0, RegisterClassExW(ref registration), "the class is registered");
        var handle = CreateWindowExW(0, name, "background", 0, 0, 0, 100, 100, 0, 0, registration.Instance, 0);
        try
        {
            Assert.AreNotEqual(0, handle, "the window is created");
            var background = new DesktopWindowBackground(() => handle);

            Assert.IsTrue(background.Apply(new NeoColor(0x1c, 0x21, 0x27, 0xff)));
            var dark = GetClassLongPtrW(handle, ClassBackground);
            Assert.AreEqual(0x0027211cu, BrushColor(dark), "the dark theme's background");

            Assert.IsTrue(background.Apply(new NeoColor(0xf6, 0xf7, 0xf9, 0xff)));
            Assert.AreEqual(0x00f9f7f6u, BrushColor(GetClassLongPtrW(handle, ClassBackground)), "the light theme's background");
            Assert.AreEqual(0, GetObjectW(dark, Marshal.SizeOf<LogicalBrush>(), out _), "the replaced brush is deleted");

            Assert.IsFalse(new DesktopWindowBackground(() => 0).Apply(new NeoColor(0, 0, 0, 0xff)), "no window, nothing to color");
        }
        finally
        {
            if (handle != 0) DestroyWindow(handle);
            UnregisterClassW(name, registration.Instance);
        }
    }

    private static uint BrushColor(nint brush)
    {
        Assert.AreNotEqual(0, GetObjectW(brush, Marshal.SizeOf<LogicalBrush>(), out var logical), "a brush the application made");
        Assert.AreEqual(0u, logical.Style, "a solid brush");
        return logical.Color;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size;
        public uint Style;
        public nint Procedure;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public string? MenuName;
        public string ClassName;
        public nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LogicalBrush
    {
        public uint Style;
        public uint Color;
        public nint Hatch;
    }

    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WindowClass registration);

    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClassW(string name, nint instance);

    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(uint extendedStyle, string className, string title, uint style, int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint parameter);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetClassLongPtrW(nint window, int index);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern int GetObjectW(nint handle, int size, out LogicalBrush value);

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? name);

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Ansi)]
    private static extern nint GetProcAddress(nint module, string name);
}
