namespace CodeAlta.Tests;

using XenoAtom.CommandLine;

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
        Assert.AreEqual("Unknown option: --wat", error);
    }

    [TestMethod]
    public void TryParse_ParsesPluginSafeMode()
    {
        var result = CodeAltaCliOptions.TryParse(["--no-plugins"], out var options, out var error);

        Assert.IsTrue(result);
        Assert.IsNull(error);
        Assert.IsNotNull(options);
        Assert.IsTrue(options.PluginSafeMode);
        Assert.IsTrue(CodeAltaCliOptions.GetPluginBootstrapOptions(["--no-plugins"]).PluginSafeMode);
    }

    [TestMethod]
    public void TryParse_ParsesPluginsStatusHeadlessCommand()
    {
        var result = CodeAltaCliOptions.TryParse(["--plugins-status"], out var options, out var error);

        Assert.IsTrue(result);
        Assert.IsNull(error);
        Assert.IsNotNull(options);
        Assert.IsTrue(options.PluginsStatus);
        Assert.IsTrue(CodeAltaCliOptions.GetPluginBootstrapOptions(["--plugins-status"]).PluginsStatus);
    }

    [TestMethod]
    public void TryParse_ParsesPluginsWaitForEnter()
    {
        var result = CodeAltaCliOptions.TryParse(["--plugins-wait-for-enter"], out var options, out var error);

        Assert.IsTrue(result);
        Assert.IsNull(error);
        Assert.IsNotNull(options);
        Assert.IsTrue(options.WaitForEnterAfterPluginLiveOutput);
        Assert.IsTrue(CodeAltaCliOptions.GetPluginBootstrapOptions(["--plugins-wait-for-enter"]).WaitForEnterAfterPluginLiveOutput);
    }

    [TestMethod]
    public void GetPluginBootstrapOptions_IgnoresPluginContributedArguments()
    {
        var options = CodeAltaCliOptions.GetPluginBootstrapOptions(["plugin-command", "--plugin-test-option", "--plugins-wait-for-enter"]);

        Assert.IsFalse(options.PluginSafeMode);
        Assert.IsFalse(options.PluginsStatus);
        Assert.IsTrue(options.WaitForEnterAfterPluginLiveOutput);
    }

    [TestMethod]
    public async Task CreateCommandApp_AcceptsPluginOptionsBeforeDefaultUnknownValidation()
    {
        var pluginOptionSeen = false;
        var app = CodeAltaCliOptions.CreateCommandApp(
            _ => new ValueTask<int>(23),
            [new TestPluginOption(() => pluginOptionSeen = true)]);

        var result = await app.RunAsync(["--plugin-test-option"]);

        Assert.AreEqual(23, result);
        Assert.IsTrue(pluginOptionSeen);
    }

    private sealed class TestPluginOption(Action onParse) : Option("plugin-test-option", "Plugin test option", maxValueCount: 0)
    {
        protected override void OnParseComplete(OptionContext c)
            => onParse();
    }
}
