using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Desktop.Rpc;

namespace CodeAlta.Desktop.Tests;

/// <summary>Literal reading callbacks only: no network, no CLI and no stored credential.</summary>
[TestClass]
public sealed class ProviderUsageRpcTests
{
    private const string Epoch = "epoch-1";
    private static readonly DateTimeOffset Start = new(2026, 10, 8, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Reset = new(2026, 10, 8, 11, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task WithoutAnOwnedHost_NothingIsRead_AndARequestMustNameTheHostAndAProvider()
    {
        using var unavailable = new ProviderUsageService();
        Assert.AreEqual("unavailable", (await unavailable.ReadAsync(new(Epoch, "codex", false), default)).Status);

        var fake = new Fake();
        using var service = fake.Service();
        Assert.AreEqual("stale_epoch", (await service.ReadAsync(new("another", "codex", false), default)).Status);
        Assert.AreEqual("stale_epoch", (await service.ReadAsync(new(null, "codex", false), default)).Status);
        Assert.AreEqual("invalid", (await service.ReadAsync(new(Epoch, "bad key", false), default)).Status);
        Assert.AreEqual("invalid", (await service.ReadAsync(new(Epoch, null, false), default)).Status);
        Assert.AreEqual("unknown_provider", (await service.ReadAsync(new(Epoch, "missing", false), default)).Status);

        var local = await service.ReadAsync(new(Epoch, "local", false), default);
        Assert.AreEqual("ok", local.Status);
        Assert.IsFalse(local.Supported, "An API-key provider is no subscription.");
        Assert.AreEqual(0, fake.Reads, "A refused request asks no provider.");

        fake.Load = static () => throw new InvalidDataException("secret");
        Assert.AreEqual("config_invalid", (await service.ReadAsync(new(Epoch, "codex", false), default)).Status);
        fake.Load = static () => throw new IOException("C:\\secret\\path");
        Assert.AreEqual("read_failed", (await service.ReadAsync(new(Epoch, "codex", false), default)).Status);
    }

    [TestMethod]
    public async Task Read_ProjectsThePlanAndTheLimitsInWholeNumbers()
    {
        var fake = new Fake
        {
            Read = static (_, _) => Task.FromResult(new AgentSubscriptionUsageReading(AgentSubscriptionUsageReading.Ok, new AgentSubscriptionUsage("max",
            [
                new AgentSubscriptionLimit("five_hour", UsedPercent: 5.6, ResetsAt: Reset, WindowMinutes: 300),
                new AgentSubscriptionLimit("premium_interactions", UsedPercent: 23.4, ResetsAt: Reset, Used: 4678, Total: 20000, Unit: "credits"),
                new AgentSubscriptionLimit("seven_day:opus", "Opus", UsedPercent: 140, WindowMinutes: 10080),
                new AgentSubscriptionLimit("chat", Unlimited: true),
                new AgentSubscriptionLimit("credits", Unit: "credits", Remaining: 62500.4),
                new AgentSubscriptionLimit("named\u0007badly", "ok"),
                new AgentSubscriptionLimit("odd", new string('n', 200), UsedPercent: double.NaN, WindowMinutes: -5, Used: -1),
            ], Start))),
        };
        using var service = fake.Service();

        var response = await service.ReadAsync(new(Epoch, " Claude ", false), default);

        Assert.AreEqual("ok", response.Status);
        Assert.AreEqual("claude", response.Key);
        Assert.IsTrue(response.Supported);
        Assert.AreEqual("max", response.Plan);
        Assert.AreEqual(Start, response.ObservedAt);
        Assert.AreEqual(new ProviderUsageLimit("five_hour", null, 6, Reset, 300, null, null, null, false, null), response.Limits[0]);
        Assert.AreEqual(new ProviderUsageLimit("premium_interactions", null, 23, Reset, null, 4678, 20000, "credits", false, null), response.Limits[1]);
        Assert.AreEqual(140, response.Limits[2].UsedPercent, "A limit that is passed says by how much.");
        Assert.AreEqual("Opus", response.Limits[2].Name);
        Assert.IsTrue(response.Limits[3].Unlimited);
        Assert.AreEqual(62500, response.Limits[4].Remaining);
        // A name that is not plain short text is left out; so are the figures that are not ones.
        Assert.AreEqual(new ProviderUsageLimit("odd", null, null, null, null, null, null, null, false, null), response.Limits[5]);
        Assert.HasCount(6, response.Limits);
    }

    [TestMethod]
    public async Task Read_SaysWhyThereIsNoUsage_ByCodeOnly()
    {
        var fake = new Fake { Read = static (_, _) => Task.FromResult(AgentSubscriptionUsageReading.NotSignedIn) };
        using var service = fake.Service();
        var signedOut = await service.ReadAsync(new(Epoch, "codex", false), default);
        Assert.AreEqual("signed_out", signedOut.Status);
        Assert.IsTrue(signedOut.Supported);
        Assert.IsEmpty(signedOut.Limits);

        fake.Read = static (_, _) => Task.FromResult(AgentSubscriptionUsageReading.NotAvailable);
        Assert.AreEqual("not_available", (await service.ReadAsync(new(Epoch, "copilot", false), default)).Status);

        // Codex and Claude Code are asked through their own program: the answer names the one that is missing.
        var tools = new Fake { Read = static (definition, _) => Task.FromResult(definition.ProviderType == "codex" ? AgentSubscriptionUsageReading.NoTool : AgentSubscriptionUsageReading.ToolNotSignedIn) };
        using var asked = tools.Service();
        var missing = await asked.ReadAsync(new(Epoch, "codex", false), default);
        Assert.AreEqual(("tool_missing", "Codex CLI"), (missing.Status, missing.Tool));
        var other = await asked.ReadAsync(new(Epoch, "claude", false), default);
        Assert.AreEqual(("tool_signed_out", "Claude Code"), (other.Status, other.Tool));
        Assert.AreEqual(("not_available", null), ((await asked.ReadAsync(new(Epoch, "copilot", false), default)).Status, (await asked.ReadAsync(new(Epoch, "copilot", false), default)).Tool),
            "A provider that is asked over the network has no tool to name.");
        fake.Read = static (_, _) => Task.FromResult(new AgentSubscriptionUsageReading(AgentSubscriptionUsageReading.Ok, new AgentSubscriptionUsage("pro", [], Start)));
        Assert.AreEqual("not_available", (await service.ReadAsync(new(Epoch, "claude", false), default)).Status, "A plan without a limit has no usage to show.");

        var failing = new Fake { Read = static (_, _) => Task.FromException<AgentSubscriptionUsageReading>(new HttpRequestException("https://secret.example.test/path")) };
        using var failed = failing.Service();
        var response = await failed.ReadAsync(new(Epoch, "codex", false), default);
        Assert.AreEqual("failed", response.Status);
        Assert.IsFalse(response.ToString().Contains("secret", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task AnAnswerIsKeptForAMinute_AndARefreshAsksAgain()
    {
        var fake = new Fake();
        using var service = fake.Service();

        var first = await service.ReadAsync(new(Epoch, "codex", false), default);
        fake.Now = Start.AddSeconds(30);
        Assert.AreSame(first, await service.ReadAsync(new(Epoch, "codex", false), default));
        Assert.AreEqual(1, fake.Reads, "A window that is opened again does not ask the provider again.");

        // Each provider has its own answer.
        await service.ReadAsync(new(Epoch, "copilot", false), default);
        Assert.AreEqual(2, fake.Reads);

        Assert.AreNotSame(first, await service.ReadAsync(new(Epoch, "codex", true), default));
        Assert.AreEqual(3, fake.Reads, "A refresh asks the provider.");
        fake.Now = Start.AddSeconds(32);
        await service.ReadAsync(new(Epoch, "codex", true), default);
        Assert.AreEqual(3, fake.Reads, "Refreshes that follow one another within seconds ask once.");

        fake.Now = Start.AddSeconds(32).Add(ProviderUsageService.Freshness);
        await service.ReadAsync(new(Epoch, "codex", false), default);
        Assert.AreEqual(4, fake.Reads);

        // A failure is not kept for the minute: the next look tries again.
        fake.Read = static (_, _) => Task.FromException<AgentSubscriptionUsageReading>(new TimeoutException());
        fake.Now = fake.Now.Add(ProviderUsageService.Freshness);
        Assert.AreEqual("failed", (await service.ReadAsync(new(Epoch, "codex", false), default)).Status);
        fake.Read = Fake.Answer;
        fake.Now = fake.Now.Add(ProviderUsageService.ShortestInterval);
        Assert.AreEqual("ok", (await service.ReadAsync(new(Epoch, "codex", false), default)).Status);
    }

    [TestMethod]
    public async Task ACanceledRequest_IsCanceled_AndKeepsNoAnswer()
    {
        var fake = new Fake { Read = static async (_, token) => { await Task.Delay(Timeout.Infinite, token); return AgentSubscriptionUsageReading.NotAvailable; } };
        using var service = fake.Service();
        using var cancellation = new CancellationTokenSource();
        var reading = service.ReadAsync(new(Epoch, "codex", false), cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => reading);

        fake.Read = Fake.Answer;
        Assert.AreEqual("ok", (await service.ReadAsync(new(Epoch, "codex", false), default)).Status);
    }

    private sealed class Fake
    {
        public static readonly Func<CodeAltaProviderDocument, CancellationToken, Task<AgentSubscriptionUsageReading>> Answer = static (_, _) =>
            Task.FromResult(new AgentSubscriptionUsageReading(AgentSubscriptionUsageReading.Ok,
                new AgentSubscriptionUsage("pro", [new AgentSubscriptionLimit("codex:primary", UsedPercent: 12, ResetsAt: Reset, WindowMinutes: 300)], Start)));

        public Func<IReadOnlyList<CodeAltaProviderDocument>> Load { get; set; } = static () =>
        [
            new() { ProviderKey = "codex", ProviderType = "codex", Enabled = true },
            new() { ProviderKey = "copilot", ProviderType = "copilot", Enabled = true },
            new() { ProviderKey = "claude", ProviderType = "claude-code", Enabled = false },
            new() { ProviderKey = "local", ProviderType = "openai-chat", Enabled = true },
        ];

        public Func<CodeAltaProviderDocument, CancellationToken, Task<AgentSubscriptionUsageReading>> Read { get; set; } = Answer;
        public DateTimeOffset Now { get; set; } = Start;
        public int Reads { get; private set; }

        public ProviderUsageService Service() => new(new ProviderUsageOperations(
            () => Load(),
            (definition, token) => { Reads++; return Read(definition, token); }) { Now = () => Now }, Epoch);
    }
}
