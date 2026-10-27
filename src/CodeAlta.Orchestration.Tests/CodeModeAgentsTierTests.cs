using CodeAlta.Catalog.ModelTiers;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime.Scripts;

namespace CodeAlta.Orchestration.Tests;

/// <summary>
/// The tier a script asks its sessions for: one that is not set up runs them on the model of the script's session, and the
/// run is told once; one that is set up gives its model; a model named that does not exist is refused.
/// </summary>
[TestClass]
public sealed class CodeModeAgentsTierTests
{
    private static readonly TimeSpan Hang = TestDeadlines.Hang;

    private static async Task<(HeadlessTestRoot Root, ScriptedTurnProvider Provider, CodeAltaHost Host, HeadlessSessionDriver Driver, HeadlessSession Parent)> CreateAsync(string? config)
    {
        var root = new HeadlessTestRoot();
        if (config is not null) await File.WriteAllTextAsync(Path.Combine(root.GlobalRoot, "config.toml"), config);
        var provider = new ScriptedTurnProvider();
        var host = await root.CreateHostAsync(provider, readOnlyGlobalState: true, headlessInstructions: true);
        var driver = new HeadlessSessionDriver(host);
        var opened = await driver.OpenAsync(new HeadlessSessionRequest { ProjectFolder = root.ProjectRoot, ModelRef = "scripted:scripted-model@high" });
        Assert.AreEqual(HeadlessOpenStatus.Created, opened.Status, opened.Message);
        var parent = opened.Session!;
        provider.Script(parent.SessionId, ScriptedTurnProvider.Reply("Parent ready."));
        Assert.AreEqual(HeadlessRunStatus.Completed, (await driver.RunAsync(parent, new HeadlessPrompt("start")).WaitAsync(Hang)).Status);
        return (root, provider, host, driver, parent);
    }

    [TestMethod]
    public async Task ATierThatIsNotSetUp_RunsTheSessionOnTheModelOfTheScriptsSession_AndTellsTheRunOnce()
    {
        var (root, provider, host, driver, parent) = await CreateAsync(null);
        await using var scope = root;
        await using var closing = driver;
        provider.Turns.Enqueue(ScriptedTurnProvider.Reply("First."));
        provider.Turns.Enqueue(ScriptedTurnProvider.Reply("Second."));

        var first = await host.CodeModeAgents.RunAsync(parent.SessionId, new CodeModeAgentRequest("Look.") { RunId = "run-tiers", Tier = "quick" }, CancellationToken.None).WaitAsync(Hang);
        var second = await host.CodeModeAgents.RunAsync(parent.SessionId, new CodeModeAgentRequest("Look again.") { RunId = "run-tiers", Tier = "quick" }, CancellationToken.None).WaitAsync(Hang);
        var kept = await host.CodeModeAgents.OpenAsync(parent.SessionId, new CodeModeAgentRequest(string.Empty) { RunId = "run-tiers", Tier = "complex" }, CancellationToken.None).WaitAsync(Hang);

        Assert.IsTrue(first.Succeeded, first.Failure);
        Assert.IsTrue(second.Succeeded, second.Failure);
        Assert.AreEqual("tier `quick` is not set up: its sessions use this session's model (scripted:scripted-model).", first.Warning);
        Assert.IsNull(second.Warning, "the run is told once per tier");
        Assert.AreEqual("tier `complex` is not set up: its sessions use this session's model (scripted:scripted-model).", kept.Warning, "session() is told too, for its own tier");
        var own = (await host.RuntimeService.TryGetResumableSessionAsync(parent.SessionId, CancellationToken.None))!;
        foreach (var child in new[] { first.SessionId!, kept.SessionId! })
        {
            var descriptor = await host.RuntimeService.TryGetResumableSessionAsync(child, CancellationToken.None);
            Assert.AreEqual(("scripted", "scripted-model"), (descriptor!.ResolvedProviderKey, descriptor.ModelId), "the model of the session that runs the script");
            Assert.IsNull(await host.RuntimeService.GetSessionTierAsync(child), "it runs on no tier");
        }

        // A session takes its effort with its first prompt: the one that answered runs with the effort of the script's session.
        Assert.AreEqual(own.ReasoningEffort, (await host.RuntimeService.TryGetResumableSessionAsync(first.SessionId!, CancellationToken.None))!.ReasoningEffort);
        Assert.AreEqual(CodeAlta.Agent.AgentReasoningEffort.High, own.ReasoningEffort);

        await ((CodeModeAgents)host.CodeModeAgents).ForgetRunAsync("run-tiers");
    }

    [TestMethod]
    public async Task ATierThatIsSetUp_GivesItsModel_WithoutWarning_AndAnUnknownModelIsStillRefused()
    {
        var (root, provider, host, driver, parent) = await CreateAsync("""
            [models.tiers]
            quick = ["scripted:scripted-model@low"]
            """);
        await using var scope = root;
        await using var closing = driver;
        provider.Turns.Enqueue(ScriptedTurnProvider.Reply("Quick."));

        var quick = await host.CodeModeAgents.RunAsync(parent.SessionId, new CodeModeAgentRequest("Look.") { RunId = "run-set-up", Tier = "quick" }, CancellationToken.None).WaitAsync(Hang);
        var unknown = await host.CodeModeAgents.RunAsync(parent.SessionId, new CodeModeAgentRequest("Look.") { RunId = "run-set-up", ModelRef = "nowhere:no-model" }, CancellationToken.None).WaitAsync(Hang);

        Assert.IsTrue(quick.Succeeded, quick.Failure);
        Assert.IsNull(quick.Warning);
        var descriptor = await host.RuntimeService.TryGetResumableSessionAsync(quick.SessionId!, CancellationToken.None);
        Assert.AreEqual(CodeAlta.Agent.AgentReasoningEffort.Low, descriptor!.ReasoningEffort, "the effort of the tier, not of the script's session");
        Assert.AreEqual(ModelTier.Quick, await host.RuntimeService.GetSessionTierAsync(quick.SessionId!));
        Assert.AreEqual("refused", unknown.Status);
        Assert.IsNull(unknown.Warning);
        await ((CodeModeAgents)host.CodeModeAgents).ForgetRunAsync("run-set-up");
    }
}
