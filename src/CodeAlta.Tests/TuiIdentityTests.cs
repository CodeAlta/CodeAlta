using CodeAlta.Tui.Views;

namespace CodeAlta.Tests;

[TestClass]
public sealed class TuiIdentityTests
{
    [TestMethod]
    public void CommandAndAssembly_IdentifyTerminalHead()
    {
        var app = CodeAltaCliOptions.CreateCommandApp(static _ => ValueTask.FromResult(0));

        Assert.AreEqual("altatui", app.Name);
        Assert.AreEqual("altatui", typeof(CodeAltaCliOptions).Assembly.GetName().Name);
    }

    [TestMethod]
    public void UpdatePackage_IdentifiesTerminalHeadWithoutRenamingProduct()
    {
        CollectionAssert.AreEqual(
            new[] { "CodeAlta.Tui", "CodeAlta" },
            new[] { CodeAltaUpdateChecker.PackageId, CodeAltaApplicationInfo.ProductName });
        var snapshot = new CodeAltaUpdateCheckSnapshot(
            CodeAltaUpdateCheckStatus.UpdateAvailable,
            CodeAltaUpdateChecker.PackageId,
            "1.0.0", "1.1.0",
            LatestVersionIsPrerelease: false,
            IncludePrerelease: false,
            ErrorMessage: null);

        Assert.AreEqual("dotnet tool update -g CodeAlta.Tui", snapshot.UpdateCommand);
    }

    [TestMethod]
    public void WelcomeFont_RetainsStableResourceIdentity()
    {
        using var stream = typeof(CodeAltaCliOptions).Assembly.GetManifestResourceStream("CodeAlta.Assets.3d.flf");

        Assert.IsNotNull(stream);
        Assert.IsTrue(stream.Length > 0);
    }
}
