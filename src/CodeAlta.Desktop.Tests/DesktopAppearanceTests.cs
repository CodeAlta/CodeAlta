using CodeAlta.Desktop;
using CodeAlta.Desktop.Rpc;
using NeoAstra;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class DesktopAppearanceTests
{
    [TestMethod]
    public void Appearance_IsKeptForTheNextStart()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodeAlta-appearance-" + Guid.NewGuid().ToString("N"));
        try
        {
            // A first start has nothing to read: the default dark theme.
            Assert.AreEqual(DesktopAppearance.Default, DesktopAppearance.Load(root));

            Assert.IsTrue(DesktopAppearance.TryCreate("light", "#F6f7F9", out var light));
            light.Save(root);
            var loaded = DesktopAppearance.Load(root);
            Assert.IsFalse(loaded.Dark);
            Assert.AreEqual(new NeoColor(0xf6, 0xf7, 0xf9, 0xff), loaded.Background);
            Assert.AreEqual("#f6f7f9", loaded.BackgroundText);

            // A file that is not an appearance leaves the default; it is never an error at start-up.
            File.WriteAllText(Path.Combine(root, "appearance.json"), """{"theme":"blue","background":"#000000"}""");
            Assert.AreEqual(DesktopAppearance.Default, DesktopAppearance.Load(root));
            File.WriteAllText(Path.Combine(root, "appearance.json"), "not json");
            Assert.AreEqual(DesktopAppearance.Default, DesktopAppearance.Load(root));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow("dark", "#1c2127", true)]
    [DataRow("light", "#FFFFFF", true)]
    [DataRow("system", "#1c2127", false)]
    [DataRow("dark", "1c2127", false)]
    [DataRow("dark", "#1c212", false)]
    [DataRow("dark", "#1c2127ff", false)]
    [DataRow("dark", "#gggggg", false)]
    [DataRow(null, "#1c2127", false)]
    [DataRow("dark", null, false)]
    public void Appearance_AcceptsAThemeAndAnOpaqueColorOnly(string? theme, string? background, bool expected)
        => Assert.AreEqual(expected, DesktopAppearance.TryCreate(theme, background, out _));

    [TestMethod]
    public void Window_StartsInTheThemeItLastHad()
    {
        Assert.IsTrue(DesktopAppearance.TryCreate("light", "#f6f7f9", out var light));
        var options = DesktopWindowChrome.WindowOptions(developer: false, light);

        // The window is painted in the theme's background, and its controls are drawn for that theme rather
        // than for the system's: dark symbols on a light title bar, light ones on a dark one.
        Assert.AreEqual(light.Background, options.BackgroundColor);
        Assert.AreEqual(NeoWindowTitleBarStyle.Overlay, options.TitleBar.Style);
        Assert.AreEqual(DesktopWindowChrome.TitleBarHeight, options.TitleBar.Height);
        Assert.IsTrue(Brightness(options.TitleBar.SymbolColor) < 96, "dark symbols on a light theme");
        Assert.IsTrue(Brightness(DesktopWindowChrome.WindowOptions(developer: false, DesktopAppearance.Default).TitleBar.SymbolColor) > 192,
            "light symbols on a dark theme");
        Assert.AreEqual(byte.MaxValue, options.TitleBar.SymbolColor.Alpha, "a transparent symbol color follows the system theme");
    }

    [TestMethod]
    public void Boot_RemembersTheAppearanceThePageReports()
    {
        DesktopAppearance? remembered = null;
        var boot = new BootService("epoch") { RememberAppearance = value => remembered = value };

        Assert.AreEqual("invalid_request", boot.Appearance(new("neon", "#000000")).Status);
        Assert.AreEqual("invalid_request", boot.Appearance(new("dark", "black")).Status);
        Assert.IsNull(remembered);
        Assert.AreEqual("ok", boot.Appearance(new("light", "#f6f7f9")).Status);
        Assert.IsNotNull(remembered);
        Assert.IsFalse(remembered.Dark);
        // A host that keeps no appearance says so.
        Assert.AreEqual("unavailable", new BootService("epoch").Appearance(new("light", "#f6f7f9")).Status);
    }

    private static int Brightness(NeoColor color) => (color.Red + color.Green + color.Blue) / 3;
}
