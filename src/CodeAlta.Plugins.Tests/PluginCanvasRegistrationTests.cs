using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugins.Tests;

[TestClass]
public sealed class PluginCanvasRegistrationTests
{
    [TestMethod]
    public async Task Activation_KeepsOneSettingsPageOfTheApplication_AndLeavesOutTheOthersWithAWarning()
    {
        var registry = new PluginContributionRegistry();
        var activator = new PluginRuntimeActivator(registry);
        var discovered = new DiscoveredPluginType { Type = typeof(SettingsPagesPlugin), Descriptor = PluginDescriptorFactory.FromType(typeof(SettingsPagesPlugin)) };

        var result = await activator.ActivateAsync(discovered, null, null, new PluginActivationOptions { HostInfo = HostInfo() });

        Assert.IsTrue(result.Succeeded, "a canvas that is left out is a warning, not a failure of the plugin");
        var kept = registry.GetSnapshot().Where(static item => item.Handle.Point == PluginPoint.Canvas).Select(static item => ((PluginCanvasContribution)item.Contribution).Id).ToArray();
        CollectionAssert.AreEquivalent(new[] { "board", "notes", "settings" }, kept);
        var warnings = result.Diagnostics.Where(static diagnostic => diagnostic.Severity == PluginDiagnosticSeverity.Warning).Select(static diagnostic => diagnostic.Message).ToArray();
        Assert.AreEqual(2, warnings.Length, string.Join(Environment.NewLine, warnings));
        Assert.IsTrue(warnings.Any(static message => message.Contains("'of-project'", StringComparison.Ordinal) && message.Contains("scope Application", StringComparison.Ordinal)));
        Assert.IsTrue(warnings.Any(static message => message.Contains("'second'", StringComparison.Ordinal) && message.Contains("'settings'", StringComparison.Ordinal)), "a plugin has one settings page");

        await result.ActivePlugin!.DeactivateAsync(TimeSpan.FromSeconds(5));
    }

    private static PluginHostInfo HostInfo() => new()
    {
        ApplicationName = "CodeAlta.Tests", Version = "1.0.0", HostApiVersion = "1.0.0", UserDataDirectory = Path.GetTempPath(), IsHeadless = true,
    };

    private static PluginCanvasContribution Canvas(string id, PluginCanvasScope scope = PluginCanvasScope.Application, PluginCanvasPlacement placement = PluginCanvasPlacement.Tab)
        => new() { Id = id, Title = id, Scope = scope, Placement = placement, Open = static (_, _) => ValueTask.FromResult(PluginCanvasView.Html("<p>canvas</p>")) };

    public sealed class SettingsPagesPlugin : PluginBase
    {
        public override IEnumerable<PluginCanvasContribution> GetCanvases()
        {
            yield return Canvas("board");
            yield return Canvas("notes", PluginCanvasScope.Project);
            yield return Canvas("of-project", PluginCanvasScope.Project, PluginCanvasPlacement.Settings);
            yield return Canvas("settings", placement: PluginCanvasPlacement.Settings);
            yield return Canvas("second", placement: PluginCanvasPlacement.Settings);
        }
    }
}
