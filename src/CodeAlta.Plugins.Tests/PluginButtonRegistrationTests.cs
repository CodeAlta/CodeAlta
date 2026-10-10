using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugins.Tests;

[TestClass]
public sealed class PluginButtonRegistrationTests
{
    [TestMethod]
    public async Task Activation_KeepsTheValidButtons_AndLeavesOutTheExtrasAndTheInvalidOnesWithAWarning()
    {
        var registry = new PluginContributionRegistry();
        var activator = new PluginRuntimeActivator(registry);
        var discovered = new DiscoveredPluginType { Type = typeof(ButtonsPlugin), Descriptor = PluginDescriptorFactory.FromType(typeof(ButtonsPlugin)) };

        var result = await activator.ActivateAsync(discovered, null, null, new PluginActivationOptions { HostInfo = HostInfo() });

        Assert.IsTrue(result.Succeeded, "a button that is left out is a warning, not a failure of the plugin");
        var kept = registry.GetSnapshot().Where(static item => item.Contribution is PluginButtonContribution).Select(static item => ((PluginButtonContribution)item.Contribution).Id).ToArray();
        CollectionAssert.AreEquivalent(new[] { "a", "b", "rail", "menu" }, kept);
        var warnings = result.Diagnostics.Where(static diagnostic => diagnostic.Severity == PluginDiagnosticSeverity.Warning).Select(static diagnostic => diagnostic.Message).ToArray();
        Assert.AreEqual(4, warnings.Length, string.Join(Environment.NewLine, warnings));
        Assert.IsTrue(warnings.Any(static message => message.Contains("'c'", StringComparison.Ordinal) && message.Contains("one too many", StringComparison.Ordinal)), "the third in the title bar");
        Assert.IsTrue(warnings.Any(static message => message.Contains("'rail2'", StringComparison.Ordinal)), "the second in the rail");
        Assert.IsTrue(warnings.Any(static message => message.Contains("'both'", StringComparison.Ordinal) && message.Contains("exactly one", StringComparison.Ordinal)));
        Assert.IsTrue(warnings.Any(static message => message.Contains("'a'", StringComparison.Ordinal) && message.Contains("repeats", StringComparison.Ordinal)), "an identifier is used once");
        Assert.IsTrue(registry.GetSnapshot().Any(static item => item.Contribution is PluginStatusContribution), "what is not a button passes");
        await result.ActivePlugin!.DeactivateAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(0, registry.GetSnapshot().Count(static item => item.Contribution is PluginButtonContribution));
    }

    [TestMethod]
    public async Task TwoPluginsMayUseTheSameButtonIdentifier_WithoutShadowingEachOther()
    {
        var registry = new PluginContributionRegistry();
        var activator = new PluginRuntimeActivator(registry);
        foreach (var type in new[] { typeof(ButtonsPlugin), typeof(OtherButtonsPlugin) })
        {
            var discovered = new DiscoveredPluginType { Type = type, Descriptor = PluginDescriptorFactory.FromType(type) };
            await activator.ActivateAsync(discovered, null, null, new PluginActivationOptions { HostInfo = HostInfo() });
        }

        Assert.AreEqual(0, registry.GetDiagnostics().Count(static diagnostic => diagnostic.Message.Contains("'a'", StringComparison.Ordinal) && diagnostic.Message.Contains("shadows", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task TheSummaryOfAButton_NamesItsIdentifierAndItsPlace()
    {
        var registry = new PluginContributionRegistry();
        var activator = new PluginRuntimeActivator(registry);
        var discovered = new DiscoveredPluginType { Type = typeof(StatefulPlugin), Descriptor = PluginDescriptorFactory.FromType(typeof(StatefulPlugin)) };
        var result = await activator.ActivateAsync(discovered, null, null, new PluginActivationOptions { HostInfo = HostInfo() });

        var summaries = registry.GetContributionSummaries().Where(static summary => summary.Handle.Point == PluginPoint.Ui).ToArray();

        CollectionAssert.AreEquivalent(new[] { "counted:TitleBar button", "throws:TitleBar button", "plain:Rail button" }, summaries.Select(static summary => $"{summary.Handle.NaturalName}:{summary.Detail}").ToArray());
        await result.ActivePlugin!.DeactivateAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task TheAdapter_ReadsTheStateOfEachButtonForAContext_AndAFailingCallbackLeavesTheDefault()
    {
        var registry = new PluginContributionRegistry();
        var activator = new PluginRuntimeActivator(registry);
        var discovered = new DiscoveredPluginType { Type = typeof(StatefulPlugin), Descriptor = PluginDescriptorFactory.FromType(typeof(StatefulPlugin)) };
        var result = await activator.ActivateAsync(discovered, null, null, new PluginActivationOptions { HostInfo = HostInfo() });
        var adapter = new PluginContributionAdapterService(registry);
        var options = new PluginAdapterOperationOptions { ProjectId = "project-1", SessionId = "session-1", HasInteractiveUi = true };

        var entries = adapter.GetButtonEntries([result.ActivePlugin!], place: null, "space-1", options);

        Assert.AreEqual(3, entries.Count);
        var counted = entries.Single(static entry => entry.Button.Id == "counted");
        Assert.AreEqual((PluginButtonBadgeKind.Count, 4), (counted.State.Badge.Kind, counted.State.Badge.Count));
        Assert.AreEqual(PluginStatusTone.Warning, counted.State.Tone);
        Assert.AreEqual("TitleBar:space-1:project-1:session-1", ((StatefulPlugin)result.ActivePlugin!.Instance!).LastContext, "the callback is given the place, space, project and session");
        Assert.AreEqual(PluginButtonBadgeKind.None, entries.Single(static entry => entry.Button.Id == "throws").State.Badge.Kind);
        Assert.AreSame(PluginButtonState.Default, entries.Single(static entry => entry.Button.Id == "plain").State);

        var rail = adapter.GetButtonEntries([result.ActivePlugin!], PluginButtonPlace.Rail, null, options);
        Assert.AreEqual("plain", rail.Single().Button.Id);
        Assert.AreEqual(0, adapter.GetButtonEntries([result.ActivePlugin!], null, null, options with { HasInteractiveUi = false }).Count, "a frontend without a window draws none");
        await result.ActivePlugin!.DeactivateAsync(TimeSpan.FromSeconds(5));
    }

    private static PluginHostInfo HostInfo() => new()
    {
        ApplicationName = "CodeAlta.Tests", Version = "1.0.0", HostApiVersion = "1.0.0", UserDataDirectory = Path.GetTempPath(), IsHeadless = true,
    };

    public sealed class ButtonsPlugin : PluginBase
    {
        public override IEnumerable<PluginUiContribution> GetUiContributions()
        {
            yield return PluginUi.Button(PluginButtonPlace.TitleBar, "a", "box", "A") with { Command = "go" };
            yield return PluginUi.Button(PluginButtonPlace.TitleBar, "b", "box", "B") with { Canvas = "board" };
            yield return PluginUi.Button(PluginButtonPlace.TitleBar, "c", "box", "C") with { Command = "go" };
            yield return PluginUi.Button(PluginButtonPlace.Rail, "rail", "box", "Rail") with { Command = "go" };
            yield return PluginUi.Button(PluginButtonPlace.Rail, "rail2", "box", "Rail 2") with { Command = "go" };
            yield return PluginUi.Button(PluginButtonPlace.ProjectMenu, "menu", "box", "Menu") with { Command = "go" };
            yield return PluginUi.Button(PluginButtonPlace.SessionMenu, "both", "box", "Both") with { Command = "go", Canvas = "board" };
            yield return PluginUi.Button(PluginButtonPlace.ProjectMenu, "a", "box", "Again") with { Command = "go" };
            yield return PluginUi.SessionStatus("label", "text");
        }
    }

    public sealed class OtherButtonsPlugin : PluginBase
    {
        public override IEnumerable<PluginUiContribution> GetUiContributions()
        {
            yield return PluginUi.Button(PluginButtonPlace.TitleBar, "a", "box", "A") with { Command = "go" };
        }
    }

    public sealed class StatefulPlugin : PluginBase
    {
        /// <summary>The context the state of the button was last read for: what this instance of the plugin was given.</summary>
        public string? LastContext { get; private set; }

        public override IEnumerable<PluginUiContribution> GetUiContributions()
        {
            yield return PluginUi.Button(PluginButtonPlace.TitleBar, "counted", "box", "Counted") with
            {
                Command = "go",
                GetState = context =>
                {
                    LastContext = $"{context.Place}:{context.SpaceId}:{context.ProjectId}:{context.SessionId}";
                    return new PluginButtonState { Badge = 4, Tone = PluginStatusTone.Warning };
                },
            };
            yield return PluginUi.Button(PluginButtonPlace.TitleBar, "throws", "box", "Throws") with
            {
                Command = "go", GetState = static _ => throw new InvalidOperationException("boom"),
            };
            yield return PluginUi.Button(PluginButtonPlace.Rail, "plain", "box", "Plain") with { Command = "go" };
        }
    }
}
