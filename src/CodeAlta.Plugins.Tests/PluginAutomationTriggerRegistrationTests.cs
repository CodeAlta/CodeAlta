using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugins.Tests;

[TestClass]
public sealed class PluginAutomationTriggerRegistrationTests
{
    [TestMethod]
    public async Task Activation_KeepsTheValidTriggers_AndLeavesOutTheInvalidOnesWithAWarning()
    {
        var registry = new PluginContributionRegistry();
        var activator = new PluginRuntimeActivator(registry);
        var discovered = new DiscoveredPluginType { Type = typeof(TriggersPlugin), Descriptor = PluginDescriptorFactory.FromType(typeof(TriggersPlugin)) };

        var result = await activator.ActivateAsync(discovered, null, null, new PluginActivationOptions { HostInfo = HostInfo() });

        Assert.IsTrue(result.Succeeded, "a trigger that is left out is a warning, not a failure of the plugin");
        var kept = registry.GetSnapshot().Where(static item => item.Handle.Point == PluginPoint.AutomationTrigger).ToArray();
        CollectionAssert.AreEqual(new[] { "acme/deploy", "acme/release" }, kept.Select(static item => ((PluginAutomationTriggerContribution)item.Contribution).Type).ToArray());
        CollectionAssert.AreEqual(new[] { "acme/deploy", "acme/release" }, kept.Select(static item => item.Handle.NaturalName).ToArray());
        var warnings = result.Diagnostics.Where(static diagnostic => diagnostic.Severity == PluginDiagnosticSeverity.Warning).Select(static diagnostic => diagnostic.Message).ToArray();
        Assert.AreEqual(3, warnings.Length, string.Join(Environment.NewLine, warnings));
        Assert.IsTrue(warnings.Any(static message => message.Contains("'deploy'", StringComparison.Ordinal) && message.Contains("<prefix>/<name>", StringComparison.Ordinal)));
        Assert.IsTrue(warnings.Any(static message => message.Contains("'acme/empty'", StringComparison.Ordinal) && message.Contains("no event", StringComparison.Ordinal)));
        Assert.IsTrue(warnings.Any(static message => message.Contains("'acme/deploy'", StringComparison.Ordinal) && message.Contains("repeats", StringComparison.Ordinal)));

        await result.ActivePlugin!.DeactivateAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(0, registry.GetSnapshot().Count(static item => item.Handle.Point == PluginPoint.AutomationTrigger), "the triggers go with the plugin");
    }

    [TestMethod]
    public async Task TwoPluginsThatGiveTheSameType_AreAConflict()
    {
        var registry = new PluginContributionRegistry();
        var activator = new PluginRuntimeActivator(registry);
        foreach (var type in new[] { typeof(TriggersPlugin), typeof(OtherTriggersPlugin) })
        {
            var discovered = new DiscoveredPluginType { Type = type, Descriptor = PluginDescriptorFactory.FromType(type) };
            await activator.ActivateAsync(discovered, null, null, new PluginActivationOptions { HostInfo = HostInfo() });
        }

        Assert.AreEqual(2, registry.GetSnapshot().Count(static item => item.Contribution is PluginAutomationTriggerContribution { Type: "acme/deploy" }));
        Assert.IsTrue(registry.GetDiagnostics().Any(static diagnostic => diagnostic.Message.Contains("acme/deploy", StringComparison.Ordinal)), "the second one is reported");
    }

    private static PluginAutomationTriggerContribution Trigger(string type, params PluginTriggerEventType[] events)
        => new() { Type = type, DisplayName = "Trigger " + type, Events = events, Source = new Source() };

    private sealed class Source : IAutomationTriggerSource
    {
        public PluginTriggerDelivery Delivery => PluginTriggerDelivery.Polled;
    }

    private sealed class TriggersPlugin : PluginBase
    {
        public override IEnumerable<PluginAutomationTriggerContribution> GetAutomationTriggers()
        {
            yield return Trigger("acme/deploy", new PluginTriggerEventType("succeeded", "Succeeded"));
            yield return Trigger("deploy", new PluginTriggerEventType("succeeded", "Succeeded"));
            yield return Trigger("acme/empty");
            yield return Trigger("acme/deploy", new PluginTriggerEventType("failed", "Failed"));
            yield return Trigger("acme/release", new PluginTriggerEventType("published", "Published")) with { Order = 1 };
        }
    }

    private sealed class OtherTriggersPlugin : PluginBase
    {
        public override IEnumerable<PluginAutomationTriggerContribution> GetAutomationTriggers()
        {
            yield return Trigger("acme/deploy", new PluginTriggerEventType("succeeded", "Succeeded"));
        }
    }

    private static PluginHostInfo HostInfo() => new()
    {
        ApplicationName = "CodeAlta.Tests", Version = "1.0.0", HostApiVersion = "1.0.0", UserDataDirectory = Path.GetTempPath(), IsHeadless = true,
    };
}
