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
        Assert.IsTrue(PromptFeatures.IsKnown("reminders"));
        Assert.IsTrue(PromptFeatures.IsKnown("ask"));
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
            return bundle.SystemMessage + "\n" + bundle.DeveloperInstructions + "\n" + bundle.SessionInstructions;
        }

        var all = Prompt("default");
        Assert.DoesNotContain("<!--", all, "No marker is left in the prompt.");
        foreach (var part in new[] { "write them down first", "alta notes set", "## Review by the user", "## Follow-up tasks", "## Sub-agents", "## Mode handoff",
                     "Agent prompt profiles available", "- Scratchpad file:", "- Model tiers:", "and clear notes when done", "request their review as \"Review by the user\" says",
                     "Otherwise set one reminder first, `alta reminder create --duration <delay> --stdin`", "set one reminder, for example `alta reminder create --duration 00:05:00",
                     "(a background job, a reminder)", "Do not ask questions or use `alta ask --stdin` by default", "- Ambiguity: ask one targeted clarification" })
        {
            StringAssert.Contains(all, part);
        }

        var lean = Prompt("default", PromptFeatures.Scratchpad, PromptFeatures.Notes, PromptFeatures.Tasks, PromptFeatures.Review, PromptFeatures.Reminders, PromptFeatures.Ask, PromptFeatures.ModeHandoff);
        Assert.DoesNotContain("<!--", lean);
        foreach (var part in new[] { "write them down first", "alta notes", "## Review by the user", "## Follow-up tasks", "alta task", "## Mode handoff",
                     "Agent prompt profiles available", "Scratchpad file", "\"Review by the user\"", "reminder", "alta ask", "ask one targeted clarification" })
        {
            Assert.DoesNotContain(part, lean, part);
        }

        // What a headless run keeps: the rules, the working loop, git branches, plan files and sub-agents; it decides by itself.
        foreach (var part in new[] { "## Working loop", "leave the changes uncommitted. The user's", "## Git branches", "## Executing plan files (if any)", "## Sub-agents", "- Model tiers:",
                     "Do not start a job for a command that ends in a few seconds.", "(a background job).", "- Ambiguity: choose the narrowest safe interpretation, and state what you assumed." })
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
        StringAssert.Contains(plan, "set one reminder with `alta reminder create");
        var leanPlan = Prompt("plan", PromptFeatures.Review, PromptFeatures.Notes, PromptFeatures.SubAgents, PromptFeatures.Reminders);
        Assert.DoesNotContain("<!--", leanPlan);
        StringAssert.Contains(leanPlan, "5. Result");
        foreach (var part in new[] { "5. Review", "## Review payload patterns", "sticky notes", "## Sub-agents", "explore them in parallel with sub-agents", "child sessions briefed", "reminder" })
        {
            Assert.DoesNotContain(part, leanPlan, part);
        }
    }

    [TestMethod]
    public void TheCodeModeSection_OfASessionWithoutSubAgents_BatchesToolCallsWithoutAgents()
    {
        using var temp = new TempFolder();
        var builder = new SystemPromptBuilder();
        string Prompt(params string[] off) => builder.Build(new SystemPromptBuildRequest
        {
            ProviderKey = "fake",
            Session = new SessionViewDescriptor { SessionId = "session-1", ProviderId = "fake", ProviderKey = "fake", WorkingDirectory = temp.Path, Kind = SessionViewKind.ProjectSession },
            UserCodeAltaRoot = Path.Combine(temp.Path, "global"),
            CodeMode = "on",
            DisabledFeatures = off.ToHashSet(StringComparer.Ordinal),
        }).DeveloperInstructions!;

        var full = Prompt();
        StringAssert.Contains(full, "`agent()` starts a sub-agent and follows the rules of \"Sub-agents\"");
        StringAssert.Contains(full, "--no-sub-sessions");

        var lean = Prompt(PromptFeatures.SubAgents);
        StringAssert.Contains(lean, "# Code Mode");
        StringAssert.Contains(lean, "Several independent reads or searches: one `exec`");
        StringAssert.Contains(lean, "Several edits you have decided: one `exec`");
        // What the session is told of being a sub-agent itself stays (the review, the branches): not the sessions it would start.
        foreach (var part in new[] { "agent(", "## Sub-agents", "your sub-agents", "alta session create", "--no-sub-sessions", "delegating implementation", "writing child" })
        {
            Assert.DoesNotContain(part, lean, part);
        }
    }

    [TestMethod]
    public void TheTextWrittenForExec_IsInThePromptOfASessionInCodeModeOnly()
    {
        using var temp = new TempFolder();
        var builder = new SystemPromptBuilder();
        string Prompt(string codeMode, string agentPrompt = "default", params string[] disabledTools)
        {
            var bundle = builder.Build(new SystemPromptBuildRequest
            {
                ProviderKey = "fake",
                SelectedPromptName = agentPrompt,
                Session = new SessionViewDescriptor { SessionId = "session-1", ProviderId = "fake", ProviderKey = "fake", WorkingDirectory = temp.Path, Kind = SessionViewKind.ProjectSession },
                UserCodeAltaRoot = Path.Combine(temp.Path, "global"),
                CodeMode = codeMode,
                DisabledTools = disabledTools.ToHashSet(StringComparer.Ordinal),
            });
            return bundle.SystemMessage + "\n" + bundle.DeveloperInstructions;
        }

        const string batch = "make all the calls in the same response, or in one `exec` script.";
        const string child = "`alta session send <child-id> --stdin`, or with `agent()` in an `exec` script.";
        foreach (var mode in new[] { "on", "only" })
        {
            StringAssert.Contains(Prompt(mode), batch, mode);
            StringAssert.Contains(Prompt(mode), child, mode);
            StringAssert.Contains(Prompt(mode, "plan"), child, mode);
        }

        // Without Code Mode, or with exec turned off, the session has no exec: it is not told about it.
        foreach (var prompt in new[] { Prompt("off"), Prompt("on", "default", "exec"), Prompt("off", "plan") })
        {
            Assert.DoesNotContain("`exec`", prompt);
            Assert.DoesNotContain("<!--", prompt);
        }

        StringAssert.Contains(Prompt("off"), "make all the calls in the same response. When you have decided several edits");
        StringAssert.Contains(Prompt("off"), "`alta session send <child-id> --stdin`. It inherits your model");
    }

    [TestMethod]
    public void TheBackgroundWorkOfTheWorkingLoop_ReadsWithAndWithoutTheShellAndTheReminders()
    {
        using var temp = new TempFolder();
        var builder = new SystemPromptBuilder();
        string Bullet(string[] disabledTools, params string[] off)
        {
            var instructions = builder.Build(new SystemPromptBuildRequest
            {
                ProviderKey = "fake",
                Session = new SessionViewDescriptor { SessionId = "session-1", ProviderId = "fake", ProviderKey = "fake", WorkingDirectory = temp.Path, Kind = SessionViewKind.ProjectSession },
                UserCodeAltaRoot = Path.Combine(temp.Path, "global"),
                DisabledTools = disabledTools.ToHashSet(StringComparer.Ordinal),
                DisabledFeatures = off.ToHashSet(StringComparer.Ordinal),
            }).DeveloperInstructions!;
            return instructions.Split('\n').Single(static line => line.StartsWith("- Do not end your turn saying", StringComparison.Ordinal)).TrimEnd('\r');
        }

        string[] shell = [];
        string[] noShell = [PromptToolConditions.ShellCommand];
        StringAssert.EndsWith(Bullet(shell), "a few seconds. Otherwise set one reminder first, `alta reminder create --duration <delay> --stdin`, with a delay that fits how long the thing takes and a content that says what to check, and set another when it fires too early. Say in your answer what brings you back: the job or the reminder.");
        StringAssert.Contains(Bullet(shell, PromptFeatures.Reminders), "within the turn when that is short. When a command can do the waiting");
        StringAssert.EndsWith(Bullet(shell, PromptFeatures.Reminders), "Do not start a job for a command that ends in a few seconds.");
        StringAssert.EndsWith(Bullet(noShell), "within the turn when that is short. Set one reminder first, `alta reminder create --duration <delay> --stdin`, with a delay that fits how long the thing takes and a content that says what to check, and set another when it fires too early. Say in your answer what brings you back: the job or the reminder.");
        StringAssert.EndsWith(Bullet(noShell, PromptFeatures.Reminders), "Wait for it within the turn when that is short.");
    }

    [TestMethod]
    public void TheSubAgentsOfAPrompt_FollowTheOptionsOfTheSession_WhateverTheHostSays()
    {
        var on = new SessionViewDescriptor { SessionId = "a" };
        var off = new SessionViewDescriptor { SessionId = "b", Options = new CodeAlta.Agent.AgentSessionOptions { SubSessions = false } };
        var none = new HashSet<string>(StringComparer.Ordinal);
        var hostOff = new HashSet<string>([PromptFeatures.SubAgents, PromptFeatures.Notes], StringComparer.Ordinal);

        Assert.AreSame(none, Runtime.AgentInstructionTemplateProvider.SessionPromptFeatures(none, on));
        CollectionAssert.AreEquivalent(new[] { PromptFeatures.SubAgents }, Runtime.AgentInstructionTemplateProvider.SessionPromptFeatures(none, off).ToArray());
        CollectionAssert.AreEquivalent(new[] { PromptFeatures.Notes }, Runtime.AgentInstructionTemplateProvider.SessionPromptFeatures(hostOff, on).ToArray());
        Assert.AreSame(hostOff, Runtime.AgentInstructionTemplateProvider.SessionPromptFeatures(hostOff, off));
        CollectionAssert.DoesNotContain(PromptFeatures.All.ToArray(), PromptFeatures.SubAgents);
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
