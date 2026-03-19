namespace CodeAlta.Tests;

[TestClass]
public sealed class CodeAltaCliOptionsTests
{
    [TestMethod]
    public void TryParse_UsesDefaultTenSecondDurationForTestMode()
    {
        var result = CodeAltaCliOptions.TryParse(["--test"], out var options, out var error);

        Assert.IsTrue(result);
        Assert.IsNull(error);
        Assert.IsNotNull(options);
        Assert.IsTrue(options.TestMode);
        Assert.AreEqual(TimeSpan.FromSeconds(10), options.TestDuration);
    }

    [TestMethod]
    public void TryParse_ParsesExplicitTestDuration()
    {
        var result = CodeAltaCliOptions.TryParse(["--test", "--test-duration", "15"], out var options, out var error);

        Assert.IsTrue(result);
        Assert.IsNull(error);
        Assert.IsNotNull(options);
        Assert.IsTrue(options.TestMode);
        Assert.AreEqual(TimeSpan.FromSeconds(15), options.TestDuration);
    }

    [TestMethod]
    public void TryParse_RejectsDurationWithoutTestMode()
    {
        var result = CodeAltaCliOptions.TryParse(["--test-duration", "15"], out var options, out var error);

        Assert.IsFalse(result);
        Assert.IsNull(options);
        Assert.AreEqual("--test-duration requires --test.", error);
    }

    [TestMethod]
    public void TryParse_RejectsUnknownArgument()
    {
        var result = CodeAltaCliOptions.TryParse(["--wat"], out var options, out var error);

        Assert.IsFalse(result);
        Assert.IsNull(options);
        Assert.AreEqual("Unknown argument '--wat'. Supported arguments: --test [--test-duration <seconds>].", error);
    }
}
