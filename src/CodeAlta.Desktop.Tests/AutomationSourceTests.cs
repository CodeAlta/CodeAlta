using System.Text.Json;
using CodeAlta.Automations;
using CodeAlta.Catalog;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class AutomationSourceTests
{
    private const string First = "0199f4c2-6d1e-7c3a-b5f0-2f9c8e4a1d77";
    private static readonly DateTimeOffset Noon = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task APolledSource_StartsOnceForEachNewEventOfItsChoice_AndKeepsItsCursor()
    {
        await using var fixture = new Fixture();
        var source = new FakeSource();
        fixture.Give(source);
        fixture.WriteGlobal(Automation("{ type = \"acme/deploy\", on = [\"succeeded\"], environment = \"prod\" }"));
        await fixture.Service.RefreshAsync();

        // The first reading starts nothing: what is there already is not an event.
        source.Add(Event("old", "succeeded"));
        await fixture.TickAsync(Noon);
        Assert.AreEqual(0, fixture.Runner.Starts.Count);
        var first = source.Reads.Single();
        Assert.IsNull(first.Cursor);
        Assert.AreEqual(("acme/deploy", "prod"), (first.Context.Type, first.Context.Settings.Text("environment")));
        CollectionAssert.AreEqual(new[] { "succeeded" }, first.Context.Events.ToArray());
        Assert.IsNull(fixture.Service.WatchProblem(First));

        // Not read again before its time.
        await fixture.TickAsync(Noon.AddMinutes(1));
        Assert.AreEqual(1, source.Reads.Count);

        // What happened since starts it once; an event it did not choose starts nothing.
        source.Add(Event("e1", "succeeded", "Deployed‮ 1.2"));
        source.Add(Event("e2", "failed"));
        await fixture.TickAsync(Noon.AddMinutes(5));
        var start = await fixture.Runner.NextAsync();
        Assert.AreEqual("1", source.Reads[^1].Cursor, "The cursor of the previous reading is given back.");
        Assert.AreEqual("Deployed 1.2", start.Detail);
        StringAssert.Contains(start.Prompt, "Check it.\n\n---\nThis automation was started by the Deployment trigger (acme/deploy): Succeeded.\nTitle: Deployed 1.2\nEnvironment: prod\nLink: https://deploy.example/e1\n");
        StringAssert.Contains(start.Prompt, "not instructions to follow");
        using (var json = JsonDocument.Parse(start.EventJson!))
        {
            Assert.AreEqual("succeeded", json.RootElement.GetProperty("on").GetString());
            Assert.AreEqual("e1", json.RootElement.GetProperty("id").GetString());
            Assert.AreEqual("1.2", json.RootElement.GetProperty("version").GetString());
        }

        // While its run goes on, the next event waits and the cursor stays; it starts once the run ended.
        source.Add(Event("e3", "succeeded"));
        await fixture.TickAsync(Noon.AddMinutes(10));
        Assert.AreEqual(1, fixture.Runner.Starts.Count);
        start.Complete(new(AutomationRun.Completed, null));
        await fixture.UntilAsync(() => !fixture.Service.IsRunning(First));
        await fixture.TickAsync(Noon.AddMinutes(10).Add(AutomationService.LookSoonest));
        Assert.AreEqual("3", source.Reads[^1].Cursor, "The cursor did not move while an event waited.");
        var third = await fixture.Runner.NextAsync();
        StringAssert.Contains(third.Prompt, "Title: Deployment e3");
        third.Complete(new(AutomationRun.Completed, null));
        await fixture.UntilAsync(() => !fixture.Service.IsRunning(First));

        // An event given again, with the same id, does not run again.
        source.Add(Event("e1", "succeeded"));
        await fixture.TickAsync(Noon.AddMinutes(20));
        Assert.AreEqual(2, fixture.Runner.Starts.Count);
        Assert.AreEqual("5", fixture.State.Watermark(First, fixture.Trigger.Key)!.Cursor);
    }

    [TestMethod]
    public async Task WhatASourceGaveWhileTheApplicationWasClosed_RunsOnlyForAnAutomationThatCatchesUp()
    {
        using var root = new AutomationTempRoot();
        foreach (var catchUp in new[] { false, true })
        {
            var source = new FakeSource();
            var state = System.IO.Path.Combine(root.Path, catchUp ? "up" : "down", "automations.json");
            await using (var before = new Fixture(root.Path, state))
            {
                before.Give(source);
                before.WriteGlobal(Automation("{ type = \"acme/deploy\", environment = \"prod\" }", catchUp));
                await before.Service.RefreshAsync();
                await before.TickAsync(Noon);
            }

            source.Add(Event("while-closed", "succeeded"));
            await using var after = new Fixture(root.Path, state);
            after.Give(source);
            await after.Service.RefreshAsync();
            await after.TickAsync(Noon.AddHours(1));
            Assert.AreEqual(catchUp ? 1 : 0, after.Runner.Starts.Count, catchUp ? "It catches up." : "What happened while closed is skipped.");
            Assert.AreEqual("1", after.State.Watermark(First, after.Trigger.Key)!.Cursor);
        }
    }

    [TestMethod]
    public async Task ATriggerItsSourceCannotTake_SaysWhy_AndIsNotRead()
    {
        await using var fixture = new Fixture();
        var source = new FakeSource();
        fixture.WriteGlobal(Automation("{ type = \"acme/deploy\", environment = \"prod\" }"));
        await fixture.Service.RefreshAsync();

        await fixture.TickAsync(Noon);
        StringAssert.Contains(fixture.Service.WatchProblem(First), "not running");
        StringAssert.Contains(fixture.Service.WatchProblem(First), "(acme)");

        fixture.Give(source);
        foreach (var (trigger, expected) in new[]
        {
            ("{ type = \"acme/deploy\" }", "needs Environment (environment)"),
            ("{ type = \"acme/deploy\", on = [\"rolled_back\"], environment = \"prod\" }", "has no event rolled_back"),
            ("{ type = \"acme/deploy\", environment = \"prod\", region = \"eu\" }", "has no setting region"),
            ("{ type = \"acme/deploy\", environment = 3 }", "is a text"),
            ("{ type = \"acme/deploy\", environment = \"prod\", token = \"abc\" }", "${secret:NAME}"),
            ("{ type = \"acme/deploy\", environment = \"${secret:PROD}\" }", "not a secret"),
            ("{ type = \"acme/deploy\", environment = \"prod\", stage = \"qa\" }", "is one of beta, live"),
        })
        {
            fixture.WriteGlobal(Automation(trigger));
            await fixture.Service.RefreshAsync();
            await fixture.TickAsync(Noon.AddMinutes(1));
            StringAssert.Contains(fixture.Service.WatchProblem(First), expected, trigger);
        }

        Assert.AreEqual(0, source.Reads.Count, "A trigger with a problem is not read.");

        // A source that needs a project says so for a chat.
        var project = new FakeSource { Project = PluginTriggerProject.Required };
        fixture.Give(project);
        fixture.WriteGlobal(Automation("{ type = \"acme/deploy\", environment = \"prod\" }"));
        await fixture.Service.RefreshAsync();
        await fixture.TickAsync(Noon.AddMinutes(2));
        StringAssert.Contains(fixture.Service.WatchProblem(First), "needs a project");
    }

    [TestMethod]
    public async Task ASourceThatFails_IsShownOnTheAutomation_AndReadAgainLater()
    {
        await using var fixture = new Fixture();
        var source = new FakeSource { Problem = "The deployments cannot be read." };
        fixture.Give(source);
        fixture.WriteGlobal(Automation("{ type = \"acme/deploy\", environment = \"prod\" }"));
        await fixture.Service.RefreshAsync();
        await fixture.TickAsync(Noon);
        Assert.AreEqual("The deployments cannot be read.", fixture.Service.WatchProblem(First));

        source.Problem = null;
        source.Throws = new InvalidOperationException("boom");
        await fixture.TickAsync(Noon.AddMinutes(5));
        Assert.AreEqual("The trigger source failed: boom", fixture.Service.WatchProblem(First));

        source.Throws = null;
        source.Invalid = "The environment prod is not known.";
        await fixture.TickAsync(Noon.AddMinutes(10));
        Assert.AreEqual("The environment prod is not known.", fixture.Service.WatchProblem(First));

        source.Invalid = null;
        await fixture.TickAsync(Noon.AddMinutes(15));
        Assert.IsNull(fixture.Service.WatchProblem(First));
        Assert.IsNotNull(fixture.State.Watermark(First, fixture.Trigger.Key)?.Cursor, "The first reading that worked keeps its cursor.");
    }

    [TestMethod]
    public async Task ASourceThatSignals_IsReadAgainSoon()
    {
        await using var fixture = new Fixture();
        var source = new FakeSource();
        fixture.Give(source);
        fixture.WriteGlobal(Automation("{ type = \"acme/deploy\", environment = \"prod\" }"));
        await fixture.Service.RefreshAsync();
        await fixture.TickAsync(Noon);
        await fixture.TickAsync(Noon.AddSeconds(10));
        Assert.AreEqual(1, source.Reads.Count);

        source.Signal.Raise();
        await fixture.TickAsync(Noon.AddSeconds(11));
        Assert.AreEqual(2, source.Reads.Count, "A signal reads the source again without waiting for the next look.");
        await fixture.TickAsync(Noon.AddSeconds(12));
        Assert.AreEqual(2, source.Reads.Count, "A signal is taken once.");
    }

    [TestMethod]
    public async Task APushedSource_ListensWhileItsTriggerIsArmed_AndAnEventRunsOnce()
    {
        await using var fixture = new Fixture();
        var source = new FakeSource { Delivery = PluginTriggerDelivery.Pushed };
        fixture.Give(source);
        fixture.WriteGlobal(Automation("{ type = \"acme/deploy\", on = [\"succeeded\", \"failed\"], environment = \"prod\" }"));
        await fixture.Service.RefreshAsync();

        await fixture.TickAsync(Noon);
        var sink = await source.ListeningAsync();
        Assert.AreEqual(0, source.Reads.Count, "A pushed source is not read.");
        Assert.IsTrue(await sink.PublishAsync(Event("p1", "failed"), default));
        var start = await fixture.Runner.NextAsync();
        StringAssert.Contains(start.Prompt, "(acme/deploy): Failed.");

        // Handed again, it ran already; another one waits for the run in progress, and the source hands it again later.
        Assert.IsTrue(await sink.PublishAsync(Event("p1", "failed"), default));
        Assert.IsFalse(await sink.PublishAsync(Event("p2", "succeeded"), default));
        start.Complete(new(AutomationRun.Completed, null));
        await fixture.UntilAsync(() => !fixture.Service.IsRunning(First));
        Assert.IsTrue(await sink.PublishAsync(Event("p2", "succeeded"), default));
        (await fixture.Runner.NextAsync()).Complete(new(AutomationRun.Completed, null));
        await fixture.UntilAsync(() => !fixture.Service.IsRunning(First));
        Assert.AreEqual(2, fixture.Runner.Starts.Count);

        // What the source says of itself is shown on the automation.
        sink.SetProblem("The deployment service is not reachable.");
        Assert.AreEqual("The deployment service is not reachable.", fixture.Service.WatchProblem(First));
        sink.SetProblem(null);
        Assert.IsNull(fixture.Service.WatchProblem(First));

        // Turned off, its listener ends, and what it still hands starts nothing.
        Assert.IsNull(await fixture.Service.SetEnabledAsync(First, false, default));
        await fixture.TickAsync(Noon.AddMinutes(1));
        await fixture.UntilAsync(() => source.Stopped);
        Assert.IsFalse(await sink.PublishAsync(Event("p3", "succeeded"), default));
        Assert.AreEqual(2, fixture.Runner.Starts.Count);
    }

    [TestMethod]
    public async Task AListenerThatFails_IsShownAndStartedAgainAfterAPause()
    {
        await using var fixture = new Fixture();
        var source = new FakeSource { Delivery = PluginTriggerDelivery.Pushed, Throws = new InvalidOperationException("socket closed") };
        fixture.Give(source);
        fixture.WriteGlobal(Automation("{ type = \"acme/deploy\", environment = \"prod\" }"));
        await fixture.Service.RefreshAsync();

        await fixture.TickAsync(Noon);
        await fixture.UntilAsync(() => fixture.Service.WatchProblem(First) is not null);
        Assert.AreEqual("The listener of the trigger failed: socket closed", fixture.Service.WatchProblem(First));
        Assert.AreEqual(1, source.Listens);

        await fixture.TickAsync(Noon.AddSeconds(30));
        Assert.AreEqual(1, source.Listens, "It waits before it listens again.");
        source.Throws = null;
        await fixture.TickAsync(Noon.AddMinutes(2));
        await source.ListeningAsync();
        Assert.AreEqual(2, source.Listens);
        await fixture.UntilAsync(() => fixture.Service.WatchProblem(First) is null);
    }

    [TestMethod]
    public void TheEventOfASource_ReachesAScriptAsAnObject()
    {
        var json = AutomationService.SourceEventJson(Event("e1", "succeeded") with { DataJson = null, Fields = [new("Environment", "prod")] });
        using (var document = JsonDocument.Parse(json!))
        {
            var root = document.RootElement;
            Assert.AreEqual(("succeeded", "e1", "Deployment e1", "prod"),
                (root.GetProperty("on").GetString(), root.GetProperty("id").GetString(), root.GetProperty("title").GetString(), root.GetProperty("fields").GetProperty("Environment").GetString()));
        }

        // What the source gives is kept, but on and id are the host's.
        var data = AutomationService.SourceEventJson(Event("e2", "failed") with { DataJson = "{\"on\":\"forged\",\"id\":\"x\",\"project\":{\"slug\":\"alpha\"}}" });
        Assert.AreEqual("{\"on\":\"failed\",\"id\":\"e2\",\"project\":{\"slug\":\"alpha\"}}", data);
        Assert.IsNull(AutomationService.SourceEventJson(Event("e3", "failed") with { DataJson = "[1, 2]" }));
        Assert.IsNull(AutomationService.SourceEventJson(Event("e4", "failed") with { DataJson = "{\"a\":\"" + new string('x', AutomationService.MaximumEventJsonLength) + "\"}" }));
    }

    [TestMethod]
    public void ATriggerOfAPlugin_IsCheckedBeforeItIsRegistered()
    {
        var valid = new FakeSource().Contribution;
        Assert.IsNull(valid.Validate());
        Assert.IsNotNull((valid with { Type = "deploy" }).Validate(), "A plugin's type has its prefix.");
        Assert.IsNotNull((valid with { Events = [] }).Validate());
        Assert.IsNotNull((valid with { Events = [new("ok", "Ok"), new("ok", "Again")] }).Validate());
        Assert.IsNotNull((valid with { Fields = [new("on", "On", PluginTriggerFieldKind.Text)] }).Validate());
        Assert.IsNotNull((valid with { Fields = [new("stage", "Stage", PluginTriggerFieldKind.Choice)] }).Validate());
        Assert.IsNotNull((valid with { DisplayName = " " }).Validate());
    }

    private static string Automation(string trigger, bool catchUp = false) => $$"""
        [automations.{{First}}]
        name = "Deploy"
        prompt = "Check it."
        {{(catchUp ? "catch_up = true" : string.Empty)}}
        triggers = [{{trigger}}]
        """;

    private static PluginTriggerEvent Event(string id, string on, string? title = null)
        => new(id, on, title ?? "Deployment " + id)
        {
            Fields = [new("Environment", "prod")], Url = "https://deploy.example/" + id, DataJson = "{\"version\":\"1.2\"}",
        };

    // A source of a plugin whose events a test adds; its cursor is the number of events it gave.
    private sealed class FakeSource : IAutomationTriggerSource
    {
        private readonly Lock _gate = new();
        private readonly List<PluginTriggerEvent> _events = [];
        private TaskCompletionSource<IPluginTriggerSink> _listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _listens;
        private int _stopped;

        public PluginTriggerDelivery Delivery { get; init; } = PluginTriggerDelivery.Polled;

        public PluginTriggerProject Project { get; init; } = PluginTriggerProject.Either;

        public PluginTriggerSignal Signal { get; } = new();

        public List<(PluginTriggerContext Context, string? Cursor)> Reads { get; } = [];

        public string? Problem { get; set; }

        public string? Invalid { get; set; }

        public Exception? Throws { get; set; }

        public int Listens => Volatile.Read(ref _listens);

        public bool Stopped => Volatile.Read(ref _stopped) != 0;

        public PluginAutomationTriggerContribution Contribution => new()
        {
            Type = "acme/deploy", DisplayName = "Deployment", Source = this, Signal = Signal, Project = Project,
            Events = [new("succeeded", "Succeeded"), new("failed", "Failed")],
            Fields =
            [
                new("environment", "Environment", PluginTriggerFieldKind.Text) { Required = true },
                new("token", "Token", PluginTriggerFieldKind.Secret),
                new("stage", "Stage", PluginTriggerFieldKind.Choice) { Choices = [new("beta", "Beta"), new("live", "Live")] },
            ],
        };

        public void Add(PluginTriggerEvent item)
        {
            lock (_gate) _events.Add(item);
        }

        public ValueTask<string?> ValidateAsync(PluginTriggerContext context, CancellationToken cancellationToken) => ValueTask.FromResult(Invalid);

        public ValueTask<PluginTriggerPage> ReadAsync(PluginTriggerContext context, string? cursor, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                Reads.Add((context, cursor));
                if (Throws is { } exception) throw exception;
                if (Problem is { } problem) return ValueTask.FromResult(new PluginTriggerPage([], null) { Problem = problem });
                var from = cursor is null ? _events.Count : int.Parse(cursor, System.Globalization.CultureInfo.InvariantCulture);
                return ValueTask.FromResult(new PluginTriggerPage([.. _events.Skip(from)], _events.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }
        }

        public async Task ListenAsync(PluginTriggerContext context, IPluginTriggerSink sink, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _listens);
            if (Throws is { } exception) throw exception;
            _listening.TrySetResult(sink);
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            finally
            {
                Interlocked.Exchange(ref _stopped, 1);
            }
        }

        public async Task<IPluginTriggerSink> ListeningAsync()
        {
            var sink = await _listening.Task.WithinAsync(TestTime.Seconds(10));
            _listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return sink;
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly AutomationTempRoot? _owned;
        private readonly string _root;

        internal Fixture() : this(null, null)
        {
        }

        internal Fixture(string? root, string? statePath)
        {
            if (root is null) _owned = new AutomationTempRoot();
            _root = root ?? _owned!.Path;
            State = new AutomationStateStore(statePath ?? System.IO.Path.Combine(_root, "state", "automations.json"), false);
            Service = new AutomationService(GlobalPath, _ => Task.FromResult<IReadOnlyList<ProjectDescriptor>>([]), State, Runner, Clock, TimeZoneInfo.Utc);
        }

        internal string GlobalPath => System.IO.Path.Combine(_root, "home", "config.toml");

        internal AutomationClock Clock { get; } = new() { Now = Noon };

        internal FakeAutomationRunner Runner { get; } = new();

        internal AutomationStateStore State { get; }

        internal AutomationService Service { get; }

        internal AutomationTrigger Trigger => Service.Snapshot.Find(First)!.Definition.Triggers.Single();

        internal void Give(FakeSource source)
            => Service.Sources.Plugins = () => [new PluginAutomationTriggerRegistration(source.Contribution, "acme", PluginScope.Global, null)];

        internal void WriteGlobal(string content)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(GlobalPath)!);
            File.WriteAllText(GlobalPath, content.ReplaceLineEndings("\n"));
            File.SetLastWriteTimeUtc(GlobalPath, File.GetLastWriteTimeUtc(GlobalPath).AddSeconds(Random.Shared.Next(1, 1000)));
        }

        // One tick of the service at a moment, and the readings it started.
        internal async Task TickAsync(DateTimeOffset now)
        {
            Clock.Now = now;
            Service.Tick(now);
            await Service.SourceReadsAsync();
        }

        internal async Task UntilAsync(Func<bool> condition)
        {
            for (var attempt = 0; attempt < 500 && !condition(); attempt++) await Task.Delay(10);
            Assert.IsTrue(condition());
        }

        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
            _owned?.Dispose();
        }
    }
}
