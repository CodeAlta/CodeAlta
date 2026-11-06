using CodeAlta.Automations;
using CodeAlta.Catalog;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class AutomationRunTriggerTests
{
    private const string Build = "0199f4c2-6d1e-7c3a-b5f0-2f9c8e4a1d77";
    private const string Triage = "0199f4c2-6d1e-7c3a-b5f0-2f9c8e4a1d78";
    private static readonly DateTimeOffset Noon = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task ARunOfAnotherAutomation_StartsTheAutomationThatFollowsIt_ForTheEventsItChose()
    {
        await using var fixture = new Fixture($$"""
            [automations.{{Build}}]
            name = "Nightly build"
            prompt = "Build it."

            [automations.{{Triage}}]
            name = "Triage the build"
            prompt = "Look at what the build did."
            triggers = [{ type = "automation", on = ["failed", "waiting"], automations = ["nightly build"] }]
            """);
        await fixture.TickAsync(Noon);

        // A run that completes is not an event it chose.
        var completed = await fixture.RunByHandAsync(Build, new(AutomationRun.Completed, null));
        await fixture.TickAsync(Noon.AddMinutes(1));
        Assert.AreEqual(1, fixture.Runner.Starts.Count);

        // A run that fails starts it, with the run it follows as its event.
        var failed = await fixture.RunByHandAsync(Build, new(AutomationRun.Failed, "The tests failed."));
        await fixture.TickAsync(Noon.AddMinutes(2));
        var triage = await fixture.Runner.NextAsync();
        Assert.AreEqual((Triage, "Nightly build failed"), (triage.Entry.Id, triage.Detail));
        StringAssert.Contains(triage.Prompt, "This automation was started by the Automation trigger (automation): Failed.\nTitle: Nightly build failed\nAutomation: Nightly build\nStatus: failed\nMessage: The tests failed.\nStarted by: manual\n");
        StringAssert.Contains(triage.EventJson, $"\"runId\":\"{failed}\"");
        Assert.AreEqual(failed, fixture.Service.Run(triage.RunId)!.Cause);
        Assert.AreNotEqual(completed, failed);
        triage.Complete(new(AutomationRun.Completed, null));
        await fixture.UntilAsync(() => !fixture.Service.IsRunning(Triage));

        // Its own run that ends is never an event for it; a run read again is not an event again.
        await fixture.TickAsync(Noon.AddMinutes(3));
        Assert.AreEqual(3, fixture.Runner.Starts.Count);

        // A run that starts to wait for an approval is one, once for each approval.
        fixture.State.Add(new AutomationRun("waiting-run", Build, Noon.AddMinutes(4), "manual") { Name = "Nightly build", Status = AutomationRun.Waiting, Approval = "a1" });
        await fixture.TickAsync(Noon.AddMinutes(4));
        var waits = await fixture.Runner.NextAsync();
        StringAssert.Contains(waits.Prompt, "(automation): Waits for an approval.\nTitle: Nightly build waits for an approval");
        waits.Complete(new(AutomationRun.Completed, null));
        await fixture.UntilAsync(() => !fixture.Service.IsRunning(Triage));
        await fixture.TickAsync(Noon.AddMinutes(10));
        Assert.AreEqual(4, fixture.Runner.Starts.Count, "the same approval is not an event again");
    }

    [TestMethod]
    public async Task TwoAutomationsThatStartEachOther_StopAfterAChainOfFive()
    {
        await using var fixture = new Fixture($$"""
            [automations.{{Build}}]
            name = "Ping"
            prompt = "Ping."
            triggers = [{ type = "automation", automations = ["{{Triage}}"] }]

            [automations.{{Triage}}]
            name = "Pong"
            prompt = "Pong."
            triggers = [{ type = "automation", automations = ["{{Build}}"] }]
            """);
        var now = Noon;
        await fixture.TickAsync(now);
        await fixture.RunByHandAsync(Build, new(AutomationRun.Completed, null));
        for (var step = 0; step < 8; step++)
        {
            now = now.AddMinutes(1);
            await fixture.TickAsync(now);
            if (fixture.Runner.Starts.Count == step + 1) break;
            var next = await fixture.Runner.NextAsync();
            next.Complete(new(AutomationRun.Completed, null));
            await fixture.UntilAsync(() => !fixture.Service.IsRunning(next.Entry.Id));
        }

        // The run by hand, then five that followed one another; the sixth is said, not run.
        Assert.AreEqual(1 + AutomationRunTriggerSource.MaximumChain, fixture.Runner.Starts.Count);
        var skipped = fixture.Service.Runs(null, 50).Single(static run => run.Status == AutomationRun.Skipped);
        StringAssert.Contains(skipped.Message, "chain");
        Assert.AreEqual(Build, skipped.AutomationId, "Ping would have followed the fifth run, of Pong");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly AutomationTempRoot _root = new();

        internal Fixture(string config)
        {
            var path = Path.Combine(_root.Path, "home", "config.toml");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, config.ReplaceLineEndings("\n"));
            State = new AutomationStateStore(Path.Combine(_root.Path, "state.json"), false);
            Service = new AutomationService(path, _ => Task.FromResult<IReadOnlyList<ProjectDescriptor>>([]), State, Runner, Clock, TimeZoneInfo.Utc);
        }

        internal AutomationClock Clock { get; } = new() { Now = Noon };

        internal FakeAutomationRunner Runner { get; } = new();

        internal AutomationStateStore State { get; }

        internal AutomationService Service { get; }

        internal async Task TickAsync(DateTimeOffset now)
        {
            Clock.Now = now;
            if (Service.Snapshot.Entries.Count == 0) await Service.RefreshAsync();
            Service.Tick(now);
            await Service.SourceReadsAsync();
        }

        // Runs an automation by hand and ends its run as said; returns the id of the run.
        internal async Task<string> RunByHandAsync(string id, AutomationOutcome outcome)
        {
            var run = await Service.RunAsync(id, default);
            var start = await Runner.NextAsync();
            start.Complete(outcome);
            await UntilAsync(() => !Service.IsRunning(id));
            return run!.Id;
        }

        internal async Task UntilAsync(Func<bool> condition)
        {
            for (var attempt = 0; attempt < 500 && !condition(); attempt++) await Task.Delay(10);
            Assert.IsTrue(condition());
        }

        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
            _root.Dispose();
        }
    }
}
