using CodeAlta.Automations;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class AutomationSourceConfigTests
{
    private const string First = "0199f4c2-6d1e-7c3a-b5f0-2f9c8e4a1d77";
    private static readonly AutomationSource Global = new("config.toml", null, null);

    [TestMethod]
    public void TheTriggersOfSources_AreReadWithTheirEventsAndSettings_AndWrittenBackTheSame()
    {
        var (definitions, faults) = AutomationConfig.Read($$"""
            [automations.{{First}}]
            name = "Onboard"
            prompt = "Describe the new project."
            triggers = [
              { type = "project", on = ["added", "Archived", "added"], space = "work" },
              { type = "acme.deploys/deploy", on = "succeeded", environment = "prod", strict = true, retries = 3, regions = ["eu", "us"], token = "${secret:DEPLOY}" },
              { type = "automation" },
            ]
            """, Global);

        Assert.AreEqual(0, faults.Count, string.Join("; ", faults.Select(static fault => fault.Message)));
        var triggers = definitions.Single().Triggers;
        Assert.IsTrue(triggers.All(static trigger => trigger.IsSource && !trigger.IsSchedule && !trigger.IsEvent && !trigger.IsIntegration));
        CollectionAssert.AreEqual(new[] { "project", "acme.deploys/deploy", "automation" }, triggers.Select(static trigger => trigger.KindName).ToArray());

        // An event named twice is read once, and in lower case.
        CollectionAssert.AreEqual(new[] { "added", "archived" }, triggers[0].On.ToArray());
        Assert.AreEqual("work", triggers[0].Settings.Text("space"));
        var plugin = triggers[1].Settings;
        CollectionAssert.AreEqual(new[] { "succeeded" }, triggers[1].On.ToArray());
        Assert.AreEqual("prod", plugin.Text("environment"));
        Assert.IsTrue(plugin.Flag("strict"));
        Assert.AreEqual(3L, plugin.Number("retries"));
        CollectionAssert.AreEqual(new[] { "eu", "us" }, plugin.Texts("regions").ToArray());
        // No event chosen: the source starts on its default one.
        Assert.AreEqual(0, triggers[2].On.Count);

        var written = AutomationConfig.Write(string.Empty, definitions[0]);
        StringAssert.Contains(written, "{ type = \"project\", on = [\"added\", \"archived\"], space = \"work\" }");
        // The settings are written in the order of their keys, whatever the order they were read in.
        StringAssert.Contains(written, "{ type = \"acme.deploys/deploy\", on = [\"succeeded\"], environment = \"prod\", regions = [\"eu\", \"us\"], retries = 3, strict = true, token = \"${secret:DEPLOY}\" }");
        var again = AutomationConfig.Read(written, Global).Definitions.Single();
        CollectionAssert.AreEqual(triggers.Select(static trigger => trigger.FingerprintKey).ToArray(), again.Triggers.Select(static trigger => trigger.FingerprintKey).ToArray());
        Assert.AreEqual(definitions[0].Fingerprint(), again.Fingerprint());
    }

    [TestMethod]
    public void TheKeyOfATriggerOfASource_LeavesItsSecretsOut_ButTheFingerprintDoesNot()
    {
        var trigger = Source("acme.deploys/deploy", ["succeeded"], ("environment", PluginTriggerValue.OfText("prod")), ("token", PluginTriggerValue.OfText("${secret:DEPLOY}")));
        var otherToken = trigger with { Settings = Settings(("environment", PluginTriggerValue.OfText("prod")), ("token", PluginTriggerValue.OfText("${secret:OTHER}"))) };
        var otherPlace = trigger with { Settings = Settings(("environment", PluginTriggerValue.OfText("test")), ("token", PluginTriggerValue.OfText("${secret:DEPLOY}"))) };
        var otherEvent = trigger with { On = ["failed"] };

        // A new token keeps what the trigger has seen; what it watches does not.
        Assert.AreEqual(trigger.Key, otherToken.Key);
        Assert.AreNotEqual(trigger.Key, otherPlace.Key);
        Assert.AreNotEqual(trigger.Key, otherEvent.Key);
        // Any change of the trigger asks to allow the automation again.
        Assert.AreNotEqual(trigger.FingerprintKey, otherToken.FingerprintKey);
        StringAssert.StartsWith(trigger.Key, "source:acme.deploys/deploy:succeeded\t");
    }

    [TestMethod]
    public void ATriggerOfASource_WrittenWrong_IsReportedWithWhatIsWrong()
    {
        Assert.IsNull(Problem("{ type = \"project\", on = [\"added\"] }"));
        StringAssert.Contains(Problem("{ type = \"projects\" }"), "projects");
        StringAssert.Contains(Problem("{ type = \"source\" }"), "source");
        StringAssert.Contains(Problem("{ type = \"Acme/Deploy\" }"), "Acme/Deploy");
        StringAssert.Contains(Problem("{ type = \"acme/deploy/now\" }"), "acme/deploy/now");
        StringAssert.Contains(Problem("{ type = \"project\", on = [\"was-added\"] }"), "was-added");
        StringAssert.Contains(Problem("{ type = \"project\", on = 3 }"), "on");
        StringAssert.Contains(Problem("{ type = \"project\", \"Space-Name\" = \"work\" }"), "Space-Name");
        StringAssert.Contains(Problem("{ type = \"project\", space = { name = \"work\" } }"), "space");
        StringAssert.Contains(Problem("{ type = \"project\", ratio = 0.5 }"), "ratio");
        StringAssert.Contains(Problem($"{{ type = \"project\", space = \"{new string('x', AutomationSourceTypes.MaximumValueLength + 1)}\" }}"), "space");
        var events = string.Join(", ", Enumerable.Range(0, AutomationSourceTypes.MaximumEvents + 1).Select(static index => $"\"e{index}\""));
        StringAssert.Contains(Problem($"{{ type = \"project\", on = [{events}] }}"), "events");

        static string? Problem(string trigger)
        {
            var (definitions, faults) = AutomationConfig.Read($"[automations.{First}]\nname = \"n\"\nprompt = \"p\"\ntriggers = [{trigger}]\n", Global);
            return definitions.Count == 1 ? null : faults.Single().Message;
        }
    }

    [TestMethod]
    public void TheTypeOfASource_IsOneOfCodeAlta_OrAPrefixAndANameForAPlugin()
    {
        foreach (var type in new[] { "project", "automation", "acme/deploy", "acme.deploys/deploy_done", "9lives/x", "a-b_c.d/name2" })
            Assert.IsTrue(AutomationSourceTypes.IsType(type), type);
        foreach (var type in new[] { null, "", "hourly", "Project", "acme/", "/deploy", "acme//deploy", "acme/deploy/now", ".acme/deploy", "acme/Deploy", "acme/2deploy", "ac me/deploy" })
            Assert.IsFalse(AutomationSourceTypes.IsType(type), type ?? "null");
        Assert.AreEqual("acme.deploys", AutomationSourceTypes.PluginOf("acme.deploys/deploy"));
        Assert.IsNull(AutomationSourceTypes.PluginOf("project"));
        // The kind of a source is not a type a file can write.
        Assert.IsFalse(AutomationTrigger.TryParseKind("source", out _));
        Assert.IsFalse(AutomationTrigger.TryParseKind("\0source", out _));
    }

    [TestMethod]
    public void TheTextOfATriggerOfASource_IsItsTable()
    {
        var trigger = Source("project", ["added"], ("space", PluginTriggerValue.OfText("work")));
        var text = AutomationTriggerText.Format(trigger);
        Assert.AreEqual("{ type = \"project\", on = [\"added\"], space = \"work\" }", text);
        Assert.IsTrue(AutomationTriggerText.TryParse(text, out var read, out var problem), problem);
        Assert.AreEqual(trigger.FingerprintKey, read!.FingerprintKey);
    }

    [TestMethod]
    public void TheSettingsOfATrigger_ReadTheSameWhateverTheirOrder()
    {
        var one = Settings(("b", PluginTriggerValue.OfText("2")), ("a", PluginTriggerValue.OfTexts(["x", "y"])));
        var two = Settings(("a", PluginTriggerValue.OfTexts(["x", "y"])), ("b", PluginTriggerValue.OfText("2")));
        Assert.AreEqual(one, two);
        Assert.AreEqual(one.GetHashCode(), two.GetHashCode());
        CollectionAssert.AreEqual(new[] { "a", "b" }, one.Values.Select(static pair => pair.Key).ToArray());
        Assert.AreEqual("{\"a\":[\"x\",\"y\"],\"b\":\"2\"}", one.ToJson());
        CollectionAssert.AreEqual(new[] { "2" }, one.Texts("b").ToArray());
        Assert.IsNull(one.Text("a"));
        Assert.IsNull(one.Number("b"));
        Assert.IsTrue(one.Flag("missing", fallback: true));
        Assert.AreNotEqual(one, Settings(("a", PluginTriggerValue.OfTexts(["y", "x"])), ("b", PluginTriggerValue.OfText("2"))));
        Assert.ThrowsExactly<ArgumentNullException>(() => PluginTriggerValue.OfTexts(["a", null!]));
    }

    private static AutomationTrigger Source(string type, string[] on, params (string Key, PluginTriggerValue Value)[] settings)
        => new(AutomationTriggerKind.Source) { Source = type, On = on, Settings = Settings(settings) };

    private static PluginTriggerSettings Settings(params (string Key, PluginTriggerValue Value)[] settings)
        => new(settings.Select(static pair => new KeyValuePair<string, PluginTriggerValue>(pair.Key, pair.Value)));
}
