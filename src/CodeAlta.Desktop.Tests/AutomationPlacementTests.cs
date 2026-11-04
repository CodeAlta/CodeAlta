using CodeAlta.Automations;
using CodeAlta.Catalog;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// What the engine gives the hosts of the fleet: claims of due times, automations placed on another machine
/// (<c>runs_on</c>), the file of the automations other devices assigned, and the credential kind of a provider.
/// </summary>
[TestClass]
public sealed class AutomationPlacementTests
{
    private const string First = "0199f4c2-6d1e-7c3a-b5f0-2f9c8e4a1e01";
    private const string Second = "0199f4c2-6d1e-7c3a-b5f0-2f9c8e4a1e02";
    private static readonly DateTimeOffset Noon = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task Claims_RunEachDueTimeOnce_WhoeverFiresIt_AndAfterARestart()
    {
        using var root = new AutomationTempRoot();
        var global = Path.Combine(root.Path, "config.toml");
        File.WriteAllText(global, $$"""
            [automations.{{First}}]
            name = "Half past"
            prompt = "hello"
            triggers = [{ type = "hourly", minute = 30 }]
            """);
        var clock = new AutomationClock { Now = Noon };
        var claimsPath = Path.Combine(root.Path, "fleet", "automation-claims.json");
        var claims = new AutomationClaimStore(claimsPath, "node-a");
        var runner = new FakeAutomationRunner();
        await using var one = Create(root.Path, "one", global, runner, clock, claims);
        await using var two = Create(root.Path, "two", global, runner, clock, claims);
        await one.RefreshAsync();
        await two.RefreshAsync();

        // Both engines see the same due time: the first claims it and runs, the second records that it was taken.
        clock.Now = Noon.AddMinutes(30).AddSeconds(5);
        one.Tick(clock.Now);
        var start = await runner.NextAsync();
        two.Tick(clock.Now);
        Assert.AreEqual(1, runner.Starts.Count, "A due time runs once.");
        var skipped = two.Runs(First, 10).Single();
        Assert.AreEqual(AutomationRun.Skipped, skipped.Status);
        StringAssert.Contains(skipped.Message, "already claimed");
        var occurrence = AutomationService.Occurrence(new AutomationTrigger(AutomationTriggerKind.Hourly) { Minute = 30 }, Noon.AddMinutes(30));
        Assert.AreEqual("hourly:30/1@2026-10-06T12:30:00Z", occurrence);
        var claim = claims.Find(First, occurrence);
        Assert.IsNotNull(claim);
        Assert.AreEqual((start.RunId, "node-a", (DateTimeOffset?)null), (claim.RunId, claim.Holder, claim.EndedAt));

        // The lease is renewed while the run works, and released when it ends; the claim stays.
        clock.Now = clock.Now.AddMinutes(2);
        one.Tick(clock.Now);
        Assert.AreEqual(clock.Now + AutomationClaimStore.LeaseDuration, claims.Find(First, occurrence)!.LeaseUntil);
        start.Complete(new(AutomationRun.Completed));
        await UntilAsync(() => claims.Find(First, occurrence)!.EndedAt is not null);

        // After a restart the claim is read from its file: the same time does not run again.
        var reopened = new AutomationClaimStore(claimsPath, "node-a");
        Assert.IsFalse(reopened.TryClaim(First, occurrence, "another-run", clock.Now));
        Assert.IsTrue(reopened.TryClaim(First, "hourly:30/1@2026-10-06T13:30:00Z", "next-run", clock.Now), "The next due time is free.");
    }

    [TestMethod]
    public async Task RunsOn_LeavesTheTriggersToTheOtherMachine_AndRunsByHandThroughThePlacement()
    {
        using var root = new AutomationTempRoot();
        var global = Path.Combine(root.Path, "config.toml");
        File.WriteAllText(global, $$"""
            [automations.{{First}}]
            name = "On the mini"
            runs_on = "node-device-1"
            prompt = "hello"
            triggers = [{ type = "hourly", minute = 30 }]
            """);
        var clock = new AutomationClock { Now = Noon };
        var runner = new FakeAutomationRunner();
        await using var service = Create(root.Path, "state", global, runner, clock, null);
        await service.RefreshAsync();
        var entry = service.Snapshot.Find(First)!;
        Assert.AreEqual("node-device-1", entry.Definition.RunsOn);
        Assert.IsNull(service.NextDue(First), "Its schedule fires on the machine it is placed on.");
        Assert.AreEqual(0, service.Upcoming(TimeSpan.FromDays(1), 24, 10).Count);
        clock.Now = Noon.AddMinutes(30).AddSeconds(5);
        service.Tick(clock.Now);
        Assert.AreEqual(0, runner.Starts.Count);

        // By hand without a placement, it fails and says why; with one, the other machine runs it.
        var refused = await service.RunAsync(First, CancellationToken.None);
        Assert.AreEqual(AutomationRun.Failed, refused!.Status);
        StringAssert.Contains(refused.Message, "another machine");
        var placement = new FakePlacement();
        service.Placement = placement;
        var remote = await service.RunAsync(First, CancellationToken.None);
        Assert.AreEqual(("remote-run", First), (remote!.Id, placement.Ran.Single().Id));
        Assert.AreEqual(0, runner.Starts.Count, "Nothing runs here.");

        // The machine is part of what the automation is, and written back as it was read.
        Assert.AreNotEqual(entry.Definition.Fingerprint(), (entry.Definition with { RunsOn = null }).Fingerprint());
        var written = AutomationConfig.Write(string.Empty, entry.Definition);
        StringAssert.Contains(written, "runs_on = \"node-device-1\"");
        Assert.AreEqual("node-device-1", AutomationConfig.Read(written, new AutomationSource(global, null, null)).Definitions.Single().RunsOn);
        Assert.IsNull(AutomationConfig.Read(written, new AutomationSource("p", "project", root.Path)).Definitions.Single().RunsOn,
            "Only an automation of the user is placed elsewhere.");
        Assert.IsNull(AutomationConfig.Read(written, new AutomationSource("a", null, null) { IsAssigned = true }).Definitions.Single().RunsOn,
            "One assigned to this machine runs here.");
        StringAssert.Contains(AutomationConfig.Validate(entry.Definition with { RunsOn = "two words" }), "paired machine");
    }

    [TestMethod]
    public async Task AssignedFile_IsReadBesideTheUsersConfig_AndKeptApartFromIt()
    {
        using var root = new AutomationTempRoot();
        var global = Path.Combine(root.Path, "config.toml");
        File.WriteAllText(global, $$"""
            [automations.{{Second}}]
            name = "Mine"
            prompt = "mine"
            """);
        var assigned = Path.Combine(root.Path, "fleet", "assigned-automations.toml");
        var clock = new AutomationClock { Now = Noon };
        var runner = new FakeAutomationRunner();
        await using var service = Create(root.Path, "state", global, runner, clock, null, assigned);
        await service.RefreshAsync();

        var definition = new AutomationDefinition(First, "From the laptop")
        {
            Prompt = "hello", RunsOn = "ignored", Triggers = [new AutomationTrigger(AutomationTriggerKind.Hourly) { Minute = 30 }],
        };
        Assert.IsNull(await service.SaveAssignedAsync(definition, CancellationToken.None));
        var entry = service.Snapshot.Find(First)!;
        Assert.IsTrue(entry.Source.IsAssigned);
        Assert.IsTrue(service.IsAssigned(First));
        Assert.IsFalse(service.IsAssigned(Second));
        Assert.IsNull(entry.Definition.RunsOn, "An assigned automation runs on the machine it was assigned to.");
        Assert.IsTrue(File.ReadAllText(assigned).Contains("From the laptop", StringComparison.Ordinal));
        Assert.IsFalse(File.ReadAllText(global).Contains("From the laptop", StringComparison.Ordinal), "The user's file is left alone.");
        Assert.IsTrue(service.IsAllowed(entry), "What a device assigned runs as what the user saves does.");
        Assert.AreEqual(Noon.AddMinutes(30), service.NextDue(First));

        // Neither file takes the place of the other.
        StringAssert.Contains(await service.SaveAsync(definition with { Name = "Taken over" }, null, CancellationToken.None), "assigned to this machine");
        StringAssert.Contains(await service.SaveAssignedAsync(new AutomationDefinition(Second, "Overwritten") { Prompt = "x" }, CancellationToken.None),
            "automation of this machine");
        Assert.AreEqual("Mine", service.Snapshot.Find(Second)!.Definition.Name);

        Assert.IsNull(await service.DeleteAsync(First, CancellationToken.None));
        Assert.IsNull(service.Snapshot.Find(First));
        Assert.AreEqual("Mine", service.Snapshot.Find(Second)!.Definition.Name);
    }

    [TestMethod]
    public void CredentialKind_FollowsHowTheProviderStarts_AndUnknownWhenItCannotBeTold()
    {
        static CodeAltaProviderDocument Config(string? apiKey = null, string? apiKeyEnv = null, string? anthropicKey = null)
            => new() { ApiKey = apiKey, ApiKeyEnv = apiKeyEnv, AnthropicApiKey = anthropicKey };
        var environment = new Dictionary<string, string?> { ["MY_KEY"] = "k", ["ANTHROPIC_API_KEY"] = "a" };
        string? Read(string name) => environment.GetValueOrDefault(name);
        Assert.AreEqual(AutomationCredentialKind.Personal, AutomationCredentials.Classify("codex", null, Read));
        Assert.AreEqual(AutomationCredentialKind.Personal, AutomationCredentials.Classify("copilot", Config(), Read));
        Assert.AreEqual(AutomationCredentialKind.Personal, AutomationCredentials.Classify("claude-code", Config(), Read), "Claude Code with its own login.");
        Assert.AreEqual(AutomationCredentialKind.Account, AutomationCredentials.Classify("claude-code", Config(anthropicKey: "use"), Read), "With the key of the environment.");
        environment["ANTHROPIC_API_KEY"] = null;
        Assert.AreEqual(AutomationCredentialKind.Personal, AutomationCredentials.Classify("claude-code", Config(anthropicKey: "use"), Read), "Without a key, its own login.");
        Assert.AreEqual(AutomationCredentialKind.Account, AutomationCredentials.Classify("openai-chat", Config(apiKey: "sk"), Read));
        Assert.AreEqual(AutomationCredentialKind.Account, AutomationCredentials.Classify("anthropic", Config(apiKeyEnv: "MY_KEY"), Read));
        Assert.AreEqual(AutomationCredentialKind.Unknown, AutomationCredentials.Classify("anthropic", Config(apiKeyEnv: "MISSING"), Read));
        Assert.AreEqual(AutomationCredentialKind.Unknown, AutomationCredentials.Classify("openai-responses", null, Read), "A provider without configuration.");
        Assert.AreEqual(AutomationCredentialKind.Account, AutomationCredentials.Classify("llama-cpp", null, Read), "A local model needs no person's plan.");
        Assert.AreEqual(AutomationCredentialKind.Unknown, AutomationCredentials.Classify("acp", Config(), Read), "An agent whose credential cannot be told.");
        Assert.AreEqual("account", AutomationCredentials.Name(AutomationCredentialKind.Account));
    }

    private static AutomationService Create(string root, string name, string global, IAutomationRunner runner, AutomationClock clock, IAutomationClaims? claims, string? assigned = null)
        => new(global, _ => Task.FromResult<IReadOnlyList<ProjectDescriptor>>([]), new AutomationStateStore(Path.Combine(root, name, "automations.json"), false),
            runner, clock, TimeZoneInfo.Utc, claims: claims, assignedConfigPath: assigned);

    private static async Task UntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 500 && !condition(); attempt++) await Task.Delay(10);
        Assert.IsTrue(condition());
    }

    private sealed class FakePlacement : IAutomationPlacement
    {
        internal List<AutomationEntry> Ran { get; } = [];

        public Task<AutomationRun> RunAsync(AutomationEntry entry, CancellationToken cancellationToken)
        {
            Ran.Add(entry);
            return Task.FromResult(new AutomationRun("remote-run", entry.Id, Noon, "manual") { Status = AutomationRun.Running });
        }

        public string? WaitsHere(string id) => null;
    }
}
