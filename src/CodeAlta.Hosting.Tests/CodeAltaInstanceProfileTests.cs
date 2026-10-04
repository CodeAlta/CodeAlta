using CodeAlta.Hosting;

namespace CodeAlta.Hosting.Tests;

[TestClass]
public sealed class CodeAltaInstanceProfileTests
{
    [TestMethod]
    public void NormalInstance_KeepsItsStateInTheGlobalRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "alta-profile-fixture");

        var profile = CodeAltaInstanceProfile.Create(root, developer: false);

        Assert.IsFalse(profile.IsDeveloper);
        Assert.AreEqual(Path.GetFullPath(root), profile.GlobalRoot);
        Assert.AreEqual(profile.GlobalRoot, profile.StateRoot);
        Assert.AreEqual(Path.Combine(profile.GlobalRoot, "alta.lock"), profile.LockFilePath);
    }

    [TestMethod]
    public void DeveloperInstance_SharesTheGlobalRootAndLocksItsOwnStateDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "alta-profile-fixture");

        var normal = CodeAltaInstanceProfile.Create(root, developer: false);
        var developer = CodeAltaInstanceProfile.Create(root, developer: true);

        Assert.IsTrue(developer.IsDeveloper);
        Assert.AreEqual(normal.GlobalRoot, developer.GlobalRoot);
        Assert.AreEqual(Path.Combine(normal.GlobalRoot, "dev"), developer.StateRoot);
        // Two locks: the developer instance is admitted while the normal one runs.
        Assert.AreNotEqual(normal.LockFilePath, developer.LockFilePath);
        Assert.AreEqual(Path.Combine(developer.StateRoot, "alta.lock"), developer.LockFilePath);
    }

    [TestMethod]
    public void CommandLine_SelectsTheDeveloperInstanceOnlyWithItsExactOption()
    {
        Assert.IsTrue(CodeAltaInstanceProfile.IsDeveloperRequested(["--dev"]));
        Assert.IsTrue(CodeAltaInstanceProfile.IsDeveloperRequested(["altatui.dll", "--no-plugins", "--dev"]));
        Assert.IsFalse(CodeAltaInstanceProfile.IsDeveloperRequested([]));
        Assert.IsFalse(CodeAltaInstanceProfile.IsDeveloperRequested(["--DEV", "--develop", "dev", "--dev=1"]));

        var normal = CodeAltaInstanceProfile.FromArguments([]);
        var developer = CodeAltaInstanceProfile.FromArguments(["--dev"]);
        Assert.AreEqual(CodeAltaInstanceProfile.GetDefaultGlobalRoot(), normal.GlobalRoot);
        Assert.AreEqual(normal.GlobalRoot, developer.GlobalRoot);
        Assert.IsFalse(normal.IsDeveloper);
        Assert.IsTrue(developer.IsDeveloper);
    }

    [TestMethod]
    public void Create_RejectsAnEmptyRoot()
    {
        Assert.ThrowsExactly<ArgumentException>(() => CodeAltaInstanceProfile.Create(" ", developer: true));
        Assert.ThrowsExactly<ArgumentNullException>(() => CodeAltaInstanceProfile.FromArguments(null!));
    }
}
