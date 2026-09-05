using CodeAlta.Tui.Views;

namespace CodeAlta.Tests;

[TestClass]
public sealed class CodeAltaUpdateServiceTests
{
    [TestMethod]
    public void UpdateCommand_UsesPrereleaseFlagForPreviewVersions()
    {
        var stable = new CodeAltaUpdateCheckSnapshot(
            CodeAltaUpdateCheckStatus.UpdateAvailable,
            CodeAltaUpdateChecker.PackageId,
            "1.0.0",
            "1.1.0",
            LatestVersionIsPrerelease: false,
            IncludePrerelease: false,
            ErrorMessage: null);
        var preview = stable with
        {
            LatestVersionText = "1.2.0-beta.1",
            LatestVersionIsPrerelease = true,
            IncludePrerelease = true,
        };

        Assert.AreEqual("dotnet tool update -g CodeAlta.Tui", stable.UpdateCommand);
        Assert.AreEqual("dotnet tool update -g CodeAlta.Tui --prerelease", preview.UpdateCommand);
    }
}
