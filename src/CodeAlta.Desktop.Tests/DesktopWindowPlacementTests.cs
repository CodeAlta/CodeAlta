using CodeAlta.Desktop;
using NeoAstra;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class DesktopWindowPlacementTests
{
    [TestMethod]
    public void Size_UsesEightyPercentOfTheWorkArea()
    {
        Assert.AreEqual((2048, 1104), DesktopWindowPlacement.Size(2560, 1380));
        Assert.AreEqual((1536, 832), DesktopWindowPlacement.Size(1920, 1040));
    }

    [TestMethod]
    public void Size_KeepsAUsableMinimumButNeverExceedsTheWorkArea()
    {
        Assert.AreEqual((960, 640), DesktopWindowPlacement.Size(1100, 720));
        Assert.AreEqual((800, 600), DesktopWindowPlacement.Size(800, 600));
        Assert.AreEqual((1280, 860), DesktopWindowPlacement.Size(0, 0));
        Assert.AreEqual((1280, 860), DesktopWindowPlacement.Size(-1, 1080));
    }

    [TestMethod]
    public void Apply_CentersTheWindowAndPreservesItsOtherOptions()
    {
        var options = DesktopWindowPlacement.Apply(new NeoWindowOptions { Label = "main", Title = "title", IsVisible = false });
        Assert.AreEqual(NeoWindowStartupLocation.Center, options.StartupLocation);
        Assert.AreEqual("main", options.Label);
        Assert.AreEqual("title", options.Title);
        Assert.IsFalse(options.IsVisible);
        Assert.IsTrue(options.Width >= 640 && options.Height >= 480);
        Assert.ThrowsExactly<ArgumentNullException>(() => DesktopWindowPlacement.Apply(null!));
    }
}
