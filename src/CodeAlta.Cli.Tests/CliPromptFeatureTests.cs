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
        CollectionAssert.AreEqual(new[] { "scratchpad", "notes", "tasks", "review", "mode-handoff" }, run.ToArray());

        Assert.IsTrue(CliPromptFeatures.TryResolve(CliPromptFeatures.RunDefaults, true, [], [], out var ephemeral, out _));
        CollectionAssert.AreEqual(new[] { "scratchpad", "notes", "tasks", "review", "mode-handoff", "session-title" }, ephemeral.ToArray());

        Assert.IsTrue(CliPromptFeatures.TryResolve(CliPromptFeatures.AcpDefaults, false, [], [], out var acp, out _));
        CollectionAssert.AreEqual(new[] { "review" }, acp.ToArray());

        Assert.IsTrue(CliPromptFeatures.TryResolve(CliPromptFeatures.RunDefaults, true, ["tasks,Notes", "session-title"], ["sub-agents"], out var changed, out _));
        CollectionAssert.AreEqual(new[] { "scratchpad", "review", "sub-agents", "mode-handoff" }, changed.ToArray());

        Assert.IsTrue(CliPromptFeatures.TryResolve(CliPromptFeatures.RunDefaults, true, ["all"], [], out var none, out _));
        Assert.AreEqual(0, none.Count);

        Assert.IsTrue(CliPromptFeatures.TryResolve(CliPromptFeatures.AcpDefaults, false, ["review"], ["all"], out var allButReview, out _));
        CollectionAssert.AreEqual(new[] { "scratchpad", "notes", "tasks", "sub-agents", "mode-handoff", "session-title" }, allButReview.ToArray());
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
        CollectionAssert.AreEqual(new[] { "scratchpad", "notes", "tasks", "review", "mode-handoff" }, DisabledFeatures(run));
        var prompt = PromptOfLastRequest(provider);
        foreach (var part in new[] { "Scratchpad file", "alta notes", "## Follow-up tasks", "## Review by the user", "## Mode handoff", "Agent prompt profiles available" })
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
        foreach (var part in new[] { "Scratchpad file", "alta notes set", "## Follow-up tasks", "## Review by the user", "## Mode handoff", "Agent prompt profiles available", "Session title:" })
        {
            StringAssert.Contains(prompt, part);
        }

        var ephemeral = await root.RunAsync(provider, ["run", Plain, "--ephemeral", "--format", "jsonl", "--disable", "sub-agents", "hello"]);

        Assert.AreEqual(CliExitCodes.Completed, ephemeral.ExitCode, ephemeral.Stderr);
        CollectionAssert.AreEqual(new[] { "scratchpad", "notes", "tasks", "review", "sub-agents", "mode-handoff", "session-title" }, DisabledFeatures(ephemeral));
        Assert.DoesNotContain("Session title:", PromptOfLastRequest(provider));
        Assert.DoesNotContain("## Sub-agents", PromptOfLastRequest(provider));
    }

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
        => provider.Requests.Last().SystemMessage + "\n" + provider.Requests.Last().DeveloperInstructions;
}
