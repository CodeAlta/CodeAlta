using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime.SystemPrompts;

namespace CodeAlta.Orchestration.Tests;

/// <summary>The parts of the agent workflow a host can leave out of the prompt (altacli run), and the prompt that keeps them all.</summary>
[TestClass]
public sealed class PromptFeatureTests
{
    [TestMethod]
    public void Apply_KeepsTheTextOfAFeatureThatIsOn_AndTheElseOfOneThatIsOff()
    {
        const string text = "A <!-- if-feature: notes -->notes<!-- else -->no notes<!-- end-if-feature --> B";

        Assert.AreEqual("A notes B", PromptFeatureConditions.Apply(text, null));
        Assert.AreEqual("A no notes B", PromptFeatureConditions.Apply(text, new HashSet<string> { PromptFeatures.Notes }));
        Assert.AreEqual("A notes B", PromptFeatureConditions.Apply(text, new HashSet<string> { PromptFeatures.Tasks }));
    }

    [TestMethod]
    public void Apply_TakesTheLineOfAMarkerAloneOnItsLine_SoThatAPromptWithEveryFeatureReadsAsWithoutMarkers()
    {
        const string text = "- one\r\n<!-- if-feature: tasks -->\r\n- two\r\n<!-- else -->\r\n- other\r\n<!-- end-if-feature -->\r\n- three";

        Assert.AreEqual("- one\r\n- two\r\n- three", PromptFeatureConditions.Apply(text, null));
        Assert.AreEqual("- one\r\n- other\r\n- three", PromptFeatureConditions.Apply(text, new HashSet<string> { PromptFeatures.Tasks }));
    }

    [TestMethod]
    public void Apply_ReadsNestedBlocks()
    {
        const string text = "<!-- if-feature: mode-handoff -->handoff<!-- if-feature: notes -->, notes<!-- end-if-feature -->.<!-- end-if-feature -->";

        Assert.AreEqual("handoff, notes.", PromptFeatureConditions.Apply(text, null));
        Assert.AreEqual("handoff.", PromptFeatureConditions.Apply(text, new HashSet<string> { PromptFeatures.Notes }));
        Assert.AreEqual(string.Empty, PromptFeatureConditions.Apply(text, new HashSet<string> { PromptFeatures.ModeHandoff }));
    }

    [TestMethod]
    public void IsKnown_NamesTheFeaturesInLowerCase()
    {
        foreach (var feature in PromptFeatures.All) Assert.IsTrue(PromptFeatures.IsKnown(feature), feature);
        Assert.IsFalse(PromptFeatures.IsKnown("Notes"));
        Assert.IsFalse(PromptFeatures.IsKnown("web"));
        Assert.IsFalse(PromptFeatures.IsKnown(null));
    }

    [TestMethod]
    public void TheShippedPrompts_LeaveOutTheWorkflowOfTheFeaturesTurnedOff_AndKeepTheRest()
    {
        using var temp = new TempFolder();
        var builder = new SystemPromptBuilder();
        string Prompt(string agentPrompt, params string[] off)
        {
            var bundle = builder.Build(new SystemPromptBuildRequest
            {
                ProviderKey = "fake",
                SelectedPromptName = agentPrompt,
                Session = new SessionViewDescriptor { SessionId = "session-1", ProviderId = "fake", ProviderKey = "fake", WorkingDirectory = temp.Path, Kind = SessionViewKind.ProjectSession },
                UserCodeAltaRoot = Path.Combine(temp.Path, "global"),
                ModelTiers = "`quick` (fake:small)",
                DisabledFeatures = off.ToHashSet(StringComparer.Ordinal),
            });
            return bundle.SystemMessage + "\n" + bundle.DeveloperInstructions;
        }

        var all = Prompt("default");
        Assert.DoesNotContain("<!--", all, "No marker is left in the prompt.");
        foreach (var part in new[] { "write them down first", "alta notes set", "## Review by the user", "## Follow-up tasks", "## Sub-agents", "## Mode handoff",
                     "Agent prompt profiles available", "- Scratchpad file:", "- Model tiers:", "and clear notes when done", "request their review as \"Review by the user\" says" })
        {
            StringAssert.Contains(all, part);
        }

        var lean = Prompt("default", PromptFeatures.Scratchpad, PromptFeatures.Notes, PromptFeatures.Tasks, PromptFeatures.Review, PromptFeatures.ModeHandoff);
        Assert.DoesNotContain("<!--", lean);
        foreach (var part in new[] { "write them down first", "alta notes", "## Review by the user", "## Follow-up tasks", "alta task", "## Mode handoff",
                     "Agent prompt profiles available", "Scratchpad file", "\"Review by the user\"" })
        {
            Assert.DoesNotContain(part, lean, part);
        }

        // What a headless run keeps: the rules, the working loop, git branches, plan files and sub-agents.
        foreach (var part in new[] { "## Working loop", "leave the changes uncommitted. The user's", "## Git branches", "## Executing plan files (if any)", "## Sub-agents", "- Model tiers:" })
        {
            StringAssert.Contains(lean, part);
        }

        var withoutSubAgents = Prompt("default", PromptFeatures.SubAgents);
        Assert.DoesNotContain("## Sub-agents", withoutSubAgents);
        Assert.DoesNotContain("- Model tiers:", withoutSubAgents);
        StringAssert.Contains(Prompt("default", PromptFeatures.Notes), "proceed as Default/build mode: edit as needed, verify, update progress when done.");

        var plan = Prompt("plan");
        StringAssert.Contains(plan, "5. Review");
        StringAssert.Contains(plan, "## Review payload patterns");
        Assert.DoesNotContain("5. Result", plan);
        var leanPlan = Prompt("plan", PromptFeatures.Review, PromptFeatures.Notes, PromptFeatures.SubAgents);
        Assert.DoesNotContain("<!--", leanPlan);
        StringAssert.Contains(leanPlan, "5. Result");
        foreach (var part in new[] { "5. Review", "## Review payload patterns", "sticky notes", "## Sub-agents", "explore them in parallel with sub-agents" })
        {
            Assert.DoesNotContain(part, leanPlan, part);
        }
    }

    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } = Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codealta-prompt-features-" + Guid.NewGuid().ToString("N"))).FullName;

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
        }
    }
}
