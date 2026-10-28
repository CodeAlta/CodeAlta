using System.Text.Json;

namespace CodeAlta.Cli.Tests;

/// <summary>The parts of the agent prompt <c>altacli</c> leaves out by default, and <c>--enable</c> / <c>--disable</c>.</summary>
[TestClass]
public sealed class CliPromptFeatureTests
{
    private const string Plain = "--no-plugins";

    [TestMethod]
    public void TryResolve_StartsFromTheDefaults_AndANameSaysMoreThanAll()
    {
        Assert.IsTrue(CliPromptFeatures.TryResolve(CliPromptFeatures.RunDefaults, false, [], [], out var run, out _));
        CollectionAssert.AreEqual(new[] { "scratchpad", "notes", "tasks", "review", "reminders", "ask", "mode-handoff" }, run.ToArray());

        Assert.IsTrue(CliPromptFeatures.TryResolve(CliPromptFeatures.RunDefaults, true, [], [], out var ephemeral, out _));
        CollectionAssert.AreEqual(new[] { "scratchpad", "notes", "tasks", "review", "reminders", "ask", "mode-handoff", "session-title" }, ephemeral.ToArray());

        Assert.IsTrue(CliPromptFeatures.TryResolve(CliPromptFeatures.AcpDefaults, false, [], [], out var acp, out _));
        CollectionAssert.AreEqual(new[] { "review", "reminders", "ask" }, acp.ToArray());

        Assert.IsTrue(CliPromptFeatures.TryResolve(CliPromptFeatures.RunDefaults, true, ["tasks,Notes", "session-title"], ["review"], out var changed, out _));
        CollectionAssert.AreEqual(new[] { "scratchpad", "review", "reminders", "ask", "mode-handoff" }, changed.ToArray());

        Assert.IsTrue(CliPromptFeatures.TryResolve(CliPromptFeatures.RunDefaults, false, ["reminders,Ask"], [], out var asking, out _));
        CollectionAssert.AreEqual(new[] { "scratchpad", "notes", "tasks", "review", "mode-handoff" }, asking.ToArray());

        Assert.IsTrue(CliPromptFeatures.TryResolve(CliPromptFeatures.RunDefaults, true, ["all"], [], out var none, out _));
        Assert.AreEqual(0, none.Count);

        Assert.IsTrue(CliPromptFeatures.TryResolve(CliPromptFeatures.AcpDefaults, false, ["review"], ["all"], out var allButReview, out _));
        CollectionAssert.AreEqual(new[] { "scratchpad", "notes", "tasks", "reminders", "ask", "mode-handoff", "session-title" }, allButReview.ToArray());
    }

    [TestMethod]
    public void TryResolve_RejectsAnUnknownName_AndANameGivenBothWays()
    {
        Assert.IsFalse(CliPromptFeatures.TryResolve(CliPromptFeatures.RunDefaults, false, [], ["web"], out _, out var unknown));
        StringAssert.Contains(unknown, "--disable web");
        StringAssert.Contains(unknown, "--disable-tools");

        Assert.IsFalse(CliPromptFeatures.TryResolve(CliPromptFeatures.RunDefaults, false, ["notes"], ["notes"], out _, out var both));
        StringAssert.Contains(both, "'notes'");

        Assert.IsFalse(CliPromptFeatures.TryResolve(CliPromptFeatures.RunDefaults, false, ["all"], ["all"], out _, out _));
    }

    [TestMethod]
    public async Task Run_LeavesOutTheWorkflowNobodyReads_AndTheHeaderSaysWhich()
    {
        using var root = new CliTestRoot();
        var provider = new FakeProvider();

        var run = await root.RunAsync(provider, ["run", Plain, "--format", "jsonl", "hello"]);

        Assert.AreEqual(CliExitCodes.Completed, run.ExitCode, run.Stderr);
        CollectionAssert.AreEqual(new[] { "scratchpad", "notes", "tasks", "review", "reminders", "ask", "mode-handoff" }, DisabledFeatures(run));
        var prompt = PromptOfLastRequest(provider);
        // What the headless paragraph says is not available is not asked for elsewhere: no reminder, no question.
        foreach (var part in new[] { "Scratchpad file", "alta notes", "## Follow-up tasks", "## Review by the user", "## Mode handoff", "Agent prompt profiles available",
                     "alta reminder create", "alta ask --stdin", "ask one targeted clarification" })
        {
            Assert.DoesNotContain(part, prompt, part);
        }

        StringAssert.Contains(prompt, "## Sub-agents");
        StringAssert.Contains(prompt, "## Git branches");
        StringAssert.Contains(prompt, "## Headless run");
        // A session the run keeps is listed in CodeAlta Desktop: it is titled.
        StringAssert.Contains(prompt, "Session title:");
    }

    [TestMethod]
    public async Task Run_EnableAll_KeepsThePromptOfTheApps_AndEphemeralLeavesOutTheTitle()
    {
        using var root = new CliTestRoot();
        var provider = new FakeProvider();

        var all = await root.RunAsync(provider, ["run", Plain, "--format", "jsonl", "--enable", "all", "hello"]);

        Assert.AreEqual(CliExitCodes.Completed, all.ExitCode, all.Stderr);
        Assert.AreEqual(0, DisabledFeatures(all).Length);
        var prompt = PromptOfLastRequest(provider);
        foreach (var part in new[] { "Scratchpad file", "alta notes set", "## Follow-up tasks", "## Review by the user", "## Mode handoff", "Agent prompt profiles available", "Session title:",
                     "alta reminder create --duration", "alta ask --stdin", "ask one targeted clarification" })
        {
            StringAssert.Contains(prompt, part);
        }

        var ephemeral = await root.RunAsync(provider, ["run", Plain, "--ephemeral", "--format", "jsonl", "hello"]);

        Assert.AreEqual(CliExitCodes.Completed, ephemeral.ExitCode, ephemeral.Stderr);
        CollectionAssert.AreEqual(new[] { "scratchpad", "notes", "tasks", "review", "reminders", "ask", "mode-handoff", "session-title" }, DisabledFeatures(ephemeral));
        Assert.DoesNotContain("Session title:", PromptOfLastRequest(provider));
    }

    [TestMethod]
    public async Task Run_NoSubSessions_LeavesOutTheSubAgents_TheirToolsAndTheirText_AndTheHeaderSaysIt()
    {
        using var root = new CliTestRoot();
        var provider = new FakeProvider();

        var full = await root.RunAsync(provider, ["run", Plain, "--format", "jsonl", "hello"]);
        Assert.AreEqual(CliExitCodes.Completed, full.ExitCode, full.Stderr);
        Assert.IsTrue(SubSessions(full));
        StringAssert.Contains(PromptOfLastRequest(provider), "## Sub-agents");
        StringAssert.Contains(AltaDescription(provider), "child sessions");

        var leaf = await root.RunAsync(provider, ["run", Plain, "--format", "jsonl", "--no-sub-sessions", "hello"]);

        Assert.AreEqual(CliExitCodes.Completed, leaf.ExitCode, leaf.Stderr);
        Assert.IsFalse(SubSessions(leaf));
        var prompt = PromptOfLastRequest(provider);
        foreach (var part in new[] { "## Sub-agents", "alta session create", "--no-sub-sessions" })
        {
            Assert.DoesNotContain(part, prompt, part);
        }

        Assert.DoesNotContain("child sessions", AltaDescription(provider));
    }

    [TestMethod]
    public async Task Run_TheSubAgentsAreNotAPartOfThePrompt_ButAnOptionOfTheSession()
    {
        using var root = new CliTestRoot();
        var provider = new FakeProvider();

        var run = await root.RunAsync(provider, ["run", Plain, "--format", "jsonl", "--disable", "sub-agents", "hello"]);

        Assert.AreEqual(CliExitCodes.InvalidUsage, run.ExitCode);
        StringAssert.Contains(run.Stderr, "--no-sub-sessions");
        Assert.AreEqual(0, provider.Requests.Count);
    }

    private static bool SubSessions(CliRun run)
        => JsonDocument.Parse(run.Lines[0]).RootElement.GetProperty("params").GetProperty("options").GetProperty("subSessions").GetBoolean();

    private static string AltaDescription(FakeProvider provider)
        => provider.Requests.Last().Tools.Single(static tool => tool.Spec.Name == "alta").Spec.Description;

    [TestMethod]
    public async Task Run_AnUnknownFeature_IsInvalidUsage()
    {
        using var root = new CliTestRoot();
        var provider = new FakeProvider();

        var run = await root.RunAsync(provider, ["run", Plain, "--format", "jsonl", "--disable", "web", "hello"]);

        Assert.AreEqual(CliExitCodes.InvalidUsage, run.ExitCode);
        StringAssert.Contains(run.Stderr, "--disable web");
        Assert.AreEqual(0, provider.Requests.Count);
    }

    private static string[] DisabledFeatures(CliRun run)
        => [.. JsonDocument.Parse(run.Lines[0]).RootElement.GetProperty("params").GetProperty("disabledFeatures").EnumerateArray().Select(static item => item.GetString()!)];

    private static string PromptOfLastRequest(FakeProvider provider)
        => provider.Requests.Last().SystemMessage + "\n" + provider.Requests.Last().DeveloperInstructions + "\n" + provider.Requests.Last().SessionInstructions;
}
