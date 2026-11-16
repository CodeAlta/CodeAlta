using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugin.Mobile.Tests;

public sealed partial class MobileServerTests
{
    [TestMethod]
    public async Task OtherMachines_AreUnknownUntilOptedIn_AndSessionIdsAlwaysKeepTheirMachine()
    {
        var machines = new Machines();
        var enabled = false;
        await using var fixture = await Fixture.StartAsync(machines, () => enabled);
        fixture.Alta.Answer("session list", FakeAlta.Session("same", "p1", "personal", "2026-11-15T12:00:00Z"))
            .Answer("session status same", FakeAlta.Status("same", "p1", "personal"));
        await fixture.SignInAsync();
        var list = JsonNode.Parse(await fixture.Client.GetStringAsync("/api/machines"))!;
        Assert.AreEqual(1, list["machines"]!.AsArray().Count, "An already paired phone is still local-only.");
        Assert.AreEqual(HttpStatusCode.NotFound, (await fixture.Client.GetAsync("/api/machines/remote/sessions/same")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await fixture.PostAsync("/api/machines/remote/sessions/same/abort", new())).StatusCode);
        Assert.IsEmpty(machines.Alta.Calls, "Not even an existence probe reaches the other machine while off.");

        enabled = true;
        list = JsonNode.Parse(await fixture.Client.GetStringAsync("/api/machines"))!;
        Assert.AreEqual(2, list["machines"]!.AsArray().Count);
        var all = JsonNode.Parse(await fixture.Client.GetStringAsync("/api/sessions?machine=all"))!["sessions"]!.AsArray();
        Assert.AreEqual(2, all.Count);
        Assert.AreEqual("remote", (string?)all[0]!["machineId"], "The more recently active machine is first.");
        Assert.AreEqual("local", (string?)all[1]!["machineId"]);
        Assert.AreEqual("same", (string?)all[0]!["id"]);
        Assert.AreEqual("same", (string?)all[1]!["id"], "Identical ids do not collapse the list.");
        var remote = JsonNode.Parse(await fixture.Client.GetStringAsync("/api/machines/remote/sessions/same"))!;
        Assert.AreEqual("remote", (string?)remote["machineId"]);
        Assert.AreEqual("Node", (string?)remote["machine"]);
        var local = JsonNode.Parse(await fixture.Client.GetStringAsync("/api/sessions/same"))!;
        Assert.IsFalse(local.AsObject().ContainsKey("machineId"), "An old link retains its strictly local meaning.");
        enabled = false;
        Assert.AreEqual(HttpStatusCode.NotFound, (await fixture.Client.GetAsync("/api/machines/remote/sessions/same")).StatusCode);
    }

    [TestMethod]
    public async Task RemoteWrites_UseTheRemoteAltaAndStableActionKey_AndNeverForwardFiles()
    {
        var machines = new Machines();
        await using var fixture = await Fixture.StartAsync(machines);
        await fixture.SignInAsync();
        fixture.Client.DefaultRequestHeaders.Add("X-Alta-Request-Id", "1c113744-8e75-491c-a87c-e22b228cc987");
        var prompt = new JsonObject { ["text"] = "Work", ["model"] = "node-model", ["reasoning"] = "high", ["agent"] = "node-agent", ["permissionMode"] = "ask" };
        using var first = await fixture.PostAsync("/api/machines/remote/sessions/same/prompt", prompt);
        using var retried = await fixture.PostAsync("/api/machines/remote/sessions/same/prompt", prompt);
        Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, retried.StatusCode);
        var calls = machines.Alta.Calls.ToArray();
        var options = machines.Alta.Options.ToArray();
        var sends = calls.Select((call, index) => (call, options[index])).Where(static pair => pair.call.Args[1] == "send").ToArray();
        Assert.AreEqual(2, sends.Length);
        Assert.AreEqual(sends[0].Item2!.RequestId, sends[1].Item2!.RequestId, "The node's ledger can replay the admission after a lost HTTP response.");
        Assert.AreEqual("1c1137448e75491ca87ce22b228cc987:session:send", sends[0].Item2!.RequestId);
        CollectionAssert.Contains(sends[0].call.Args, "node-model");
        CollectionAssert.Contains(sends[0].call.Args, "high");
        CollectionAssert.Contains(sends[0].call.Args, "node-agent");
        Assert.AreEqual("Work", sends[0].call.Stdin);
        Assert.IsEmpty(fixture.Alta.Calls, "No local session was used.");

        var files = new JsonObject
        {
            ["text"] = "With a file", ["attachments"] = new JsonArray(new JsonObject { ["name"] = "test.txt", ["mediaType"] = "text/plain", ["base64"] = "aGk=" }),
        };
        using var refused = await fixture.PostAsync("/api/machines/remote/sessions/same/prompt", files);
        Assert.AreEqual(HttpStatusCode.BadRequest, refused.StatusCode);
        StringAssert.Contains(await refused.Content.ReadAsStringAsync(), "cannot be sent");
        Assert.AreEqual(2, machines.Alta.CallsOf("session send").Length);
    }

    [TestMethod]
    public async Task RemoteSpaces_AreFailClosed_EvenForUnknownSpaces_AndOfflineCardsAreReadOnly()
    {
        var machines = new Machines();
        var spaces = new FakeSpaces("personal");
        await using var fixture = await Fixture.StartAsync(machines, scope: spaces);
        await fixture.SignInAsync();
        machines.Alta.Answer("session status outside", FakeAlta.Status("outside", "p1", "other"))
            .Answer("session status unknown", new JsonObject { ["type"] = "alta.session.status", ["sessionId"] = "unknown" });
        foreach (var id in new[] { "outside", "unknown" })
        {
            Assert.AreEqual(HttpStatusCode.NotFound, (await fixture.Client.GetAsync($"/api/machines/remote/sessions/{id}/events")).StatusCode);
            Assert.AreEqual(HttpStatusCode.NotFound, (await fixture.PostAsync($"/api/machines/remote/sessions/{id}/abort", new())).StatusCode);
        }
        var online = JsonNode.Parse(await fixture.Client.GetStringAsync("/api/sessions?machine=remote"))!;
        Assert.AreEqual(1, online["sessions"]!.AsArray().Count);
        machines.Machine = machines.Machine with { State = "offline" };
        var offline = JsonNode.Parse(await fixture.Client.GetStringAsync("/api/sessions?machine=remote"))!;
        Assert.AreEqual(1, offline["sessions"]!.AsArray().Count, "Only the last bounded list is retained.");
        Assert.IsFalse((bool)offline["sessions"]![0]!["can"]!["drive"]!);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, (await fixture.PostAsync("/api/machines/remote/sessions/same/abort", new())).StatusCode);
        spaces.Assigned = ["other"];
        var narrowed = JsonNode.Parse(await fixture.Client.GetStringAsync("/api/sessions?machine=remote"))!;
        Assert.AreEqual(0, narrowed["sessions"]!.AsArray().Count, "Changing the assigned spaces drops even the offline cards.");
    }

    [TestMethod]
    public async Task ViewOnlyAndOlderMachines_ExplainTheirLimits_AndNeverAdmitWrites()
    {
        var machines = new Machines();
        await using var fixture = await Fixture.StartAsync(machines);
        await fixture.SignInAsync();
        machines.Machine = machines.Machine with { Permissions = ["view"] };
        Assert.AreEqual(HttpStatusCode.OK, (await fixture.Client.GetAsync("/api/machines/remote/sessions/same")).StatusCode);
        foreach (var path in new[] { "/api/machines/remote/sessions", "/api/machines/remote/sessions/same/prompt", "/api/machines/remote/sessions/same/abort" })
            Assert.AreEqual(HttpStatusCode.Forbidden, (await fixture.PostAsync(path, new JsonObject { ["text"] = "No" })).StatusCode);
        Assert.AreEqual(0, machines.Alta.CallsOf("session send").Length);
        machines.Machine = machines.Machine with { Features = [] };
        var listing = JsonNode.Parse(await fixture.Client.GetStringAsync("/api/machines"))!;
        Assert.AreEqual(0, listing["machines"]![1]!["features"]!.AsArray().Count);
        Assert.AreEqual(HttpStatusCode.Conflict, (await fixture.Client.GetAsync("/api/machines/remote/sessions/same")).StatusCode);
        machines.Machine = machines.Machine with { State = "unauthorized" };
        Assert.AreEqual(HttpStatusCode.Forbidden, (await fixture.Client.GetAsync("/api/machines/remote/sessions/same")).StatusCode);
    }

    [TestMethod]
    public async Task ALocalPrompt_KeepsItsActionIdAcrossRetries_AndRemoteJobsNameTheirOwningSession()
    {
        var machines = new Machines();
        await using var fixture = await Fixture.StartAsync(machines);
        fixture.Alta.Answer("session status same", FakeAlta.Status("same", "p1", "personal"));
        machines.Alta.Answer("job status job-1", new JsonObject { ["type"] = "alta.job.status", ["sessionId"] = "same", ["jobId"] = "job-1" })
            .Answer("job output job-1", new JsonObject { ["type"] = "alta.job.output", ["text"] = "Done" });
        await fixture.SignInAsync();
        fixture.Client.DefaultRequestHeaders.Add("X-Alta-Request-Id", "1c113744-8e75-491c-a87c-e22b228cc987");
        for (var retry = 0; retry < 2; retry++)
            using (var response = await fixture.PostAsync("/api/sessions/same/prompt", new() { ["text"] = "Once" }))
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var prompts = fixture.Alta.Calls.Zip(fixture.Alta.Options).Where(pair => pair.First.Args is ["session", "send", ..]).ToArray();
        Assert.AreEqual(1, prompts.Length, "The request sent again after a lost answer gets the first answer: this computer runs the prompt once.");
        Assert.IsNotNull(prompts[0].Second?.RequestId);
        fixture.Client.DefaultRequestHeaders.Remove("X-Alta-Request-Id");
        fixture.Client.DefaultRequestHeaders.Add("X-Alta-Request-Id", "2c113744-8e75-491c-a87c-e22b228cc987");
        using (var other = await fixture.PostAsync("/api/sessions/same/prompt", new() { ["text"] = "Once" }))
            Assert.AreEqual(HttpStatusCode.OK, other.StatusCode);
        Assert.AreEqual(2, fixture.Alta.CallsOf("session send").Length, "Another action of the phone is another prompt.");
        var output = JsonNode.Parse(await fixture.Client.GetStringAsync("/api/machines/remote/sessions/same/jobs/job-1"))!;
        Assert.AreEqual("Done", (string?)output["text"]);
        var reads = machines.Alta.Calls.Zip(machines.Alta.Options).Where(pair => pair.First.Args[0] == "job").ToArray();
        Assert.AreEqual(2, reads.Length);
        Assert.IsTrue(reads.All(pair => pair.Second?.SourceSessionId == "same"), "The serving node can authorize every job against its session.");
    }

    [TestMethod]
    public async Task AMovedLocalSession_IsNotListedOrDriven_AndLinksOnlyToItsRecordedAllowedDestination()
    {
        var machines = new Machines();
        machines.Moves["same"] = new("remote", "Node");
        var enabled = false;
        await using var fixture = await Fixture.StartAsync(machines, () => enabled);
        fixture.Alta.Answer("session list", FakeAlta.Session("same", "p1", "personal", "2026-11-15T12:00:00Z"))
            .Answer("session status same", FakeAlta.Status("same", "p1", "personal"));
        await fixture.SignInAsync();
        Assert.AreEqual(0, JsonNode.Parse(await fixture.Client.GetStringAsync("/api/sessions"))!["sessions"]!.AsArray().Count);
        using var hidden = await fixture.Client.GetAsync("/api/sessions/same");
        Assert.AreEqual(HttpStatusCode.Conflict, hidden.StatusCode);
        Assert.IsNull(JsonNode.Parse(await hidden.Content.ReadAsStringAsync())!["machineId"], "No remote capability is disclosed before opt-in.");
        enabled = true;
        using var moved = await fixture.Client.GetAsync("/api/sessions/same");
        var location = JsonNode.Parse(await moved.Content.ReadAsStringAsync())!;
        Assert.AreEqual("not_on_this_machine", (string?)location["code"]);
        Assert.AreEqual("remote", (string?)location["machineId"]);
        using var refused = await fixture.PostAsync("/api/sessions/same/prompt", new() { ["text"] = "Do not run here" });
        Assert.AreEqual(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.IsEmpty(fixture.Alta.CallsOf("session send"));
        machines.Moves["same"] = new(null, "Node");
        using var ambiguous = await fixture.Client.GetAsync("/api/sessions/same");
        Assert.IsNull(JsonNode.Parse(await ambiguous.Content.ReadAsStringAsync())!["machineId"], "A display name never selects a machine.");
    }

    [TestMethod]
    public async Task ArchivingWithChildren_IsRefused_WhenAChildOfAnotherMachineHasNoKnownSpace()
    {
        var machines = new Machines();
        await using var fixture = await Fixture.StartAsync(machines, scope: new FakeSpaces("personal"));
        await fixture.SignInAsync();
        machines.Alta.Answer("session children same", new JsonObject { ["type"] = "alta.session.item", ["sessionId"] = "legacy", ["state"] = "idle" },
            new JsonObject { ["type"] = "alta.session.children.summary", ["count"] = 1 });
        using var refused = await fixture.PostAsync("/api/machines/remote/sessions/same/archive?withChildren=true", new());
        var answer = JsonNode.Parse(await refused.Content.ReadAsStringAsync())!;
        Assert.IsFalse((bool)answer["ok"]!, "A session the phone could not open is not archived through its parent.");
        Assert.IsEmpty(machines.Alta.CallsOf("session archive"));
    }

    [TestMethod]
    public async Task WhatAMachineSaysWhenItIsReached_UpdatesThePhoneAndNotifiesOfNothing_AndALaterChangeNotifiesOnce()
    {
        var machines = new Machines();
        var live = new MobileLive();
        var push = new MobilePushEvents();
        var spaces = new FakeSpaces("personal");
        var local = new FakeAlta();
        var sessions = new MobileSessions(local, spaces, live);
        var router = new MobileMachines(machines, spaces, new(new PluginMachine("local", "Desktop", "online", null, null, ["view", "drive", "start"], [], 0),
            sessions, new MobileApi(local, sessions), new MobileAsks(new FakeAsks(), sessions), new MobilePermissions(new FakePermissions(), sessions, null, local)), live, push, () => true);
        using var stop = new CancellationTokenSource();
        var following = router.FollowAsync(stop.Token);
        var phone = live.Subscribe();
        async Task<string> NextAsync()
        {
            using var patience = new CancellationTokenSource(TestTime.Seconds(10));
            return await phone.Reader.ReadAsync(patience.Token);
        }

        var at = DateTimeOffset.UtcNow;
        await machines.Changes.Writer.WriteAsync(new("remote", null, "online", at, null, false));
        StringAssert.StartsWith(await NextAsync(), "event: machines", "A machine that comes back is announced: its sessions are driven again.");
        // The baseline: a session that works, one of another space, and an idle one.
        await machines.Changes.Writer.WriteAsync(new("remote", "other", "working", at, "work", true));
        await machines.Changes.Writer.WriteAsync(new("remote", "quiet", "idle", at, "personal", true));
        await machines.Changes.Writer.WriteAsync(new("remote", "same", "working", at, "personal", true));
        var baseline = await NextAsync();
        StringAssert.Contains(baseline, "\"id\":\"same\"", "Neither the session of another space nor the idle one is told.");
        StringAssert.Contains(baseline, "\"live\":\"running\"");
        // A turn that ends, then the same state again.
        await machines.Changes.Writer.WriteAsync(new("remote", "same", "idle", at, "personal", false));
        StringAssert.Contains(await NextAsync(), "\"live\":\"idle\"");
        await machines.Changes.Writer.WriteAsync(new("remote", "same", "idle", at, "personal", false));
        await machines.Changes.Writer.WriteAsync(new("remote", "same", "working", at, "personal", false));
        StringAssert.Contains(await NextAsync(), "\"live\":\"running\"", "The repeated state told nothing.");
        Assert.IsTrue(push.Reader.TryRead(out var notice));
        Assert.AreEqual(("same", "completed", "remote"), (notice!.SessionId, notice.Kind, notice.MachineId));
        Assert.IsFalse(push.Reader.TryRead(out _), "The baseline notified of nothing.");
        // A reconnection: the baseline replaces what was held, without a notification.
        await machines.Changes.Writer.WriteAsync(new("remote", "same", "idle", at, "personal", true));
        StringAssert.Contains(await NextAsync(), "\"live\":\"idle\"", "The state of before the disconnection does not stay over the reads.");
        Assert.IsFalse(push.Reader.TryRead(out _));
        await stop.CancelAsync();
        try { await following; } catch (OperationCanceledException) { }
    }

    private sealed class Machines : IPluginMachineService
    {
        internal System.Threading.Channels.Channel<PluginMachineChange> Changes { get; } = System.Threading.Channels.Channel.CreateUnbounded<PluginMachineChange>();
        internal Dictionary<string, PluginSessionMove> Moves { get; } = new(StringComparer.Ordinal);
        public ValueTask<IReadOnlyDictionary<string, PluginSessionMove>> LocalSessionMovesAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyDictionary<string, PluginSessionMove>>(Moves);
        internal FakeAlta Alta { get; } = new FakeAlta()
            .Answer("session list", FakeAlta.Session("same", "p1", "personal", "2026-11-16T12:00:00Z"))
            .Answer("session status same", FakeAlta.Status("same", "p1", "personal"))
            .Answer("session send", new JsonObject { ["type"] = "alta.session.sent" });
        internal PluginMachine Machine { get; set; } = new("remote", "Node", "online", "relayed", DateTimeOffset.UtcNow, ["view", "drive", "start"], ["sessions.read"], 0);
        public bool HasMachines => true;
        public ValueTask<IReadOnlyList<PluginMachine>> ListAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<PluginMachine>>([Machine]);
        public ValueTask<PluginMachineServices?> GetAsync(string machineId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<PluginMachineServices?>(machineId == Machine.Id ? new(Machine, Alta, new FakeAsks(), new FakePermissions()) : null);
        public IAsyncEnumerable<PluginMachineChange> WatchAsync(CancellationToken cancellationToken = default) => Changes.Reader.ReadAllAsync(cancellationToken);
        public async IAsyncEnumerable<PluginMachineDisplay> WatchSessionAsync(string machineId, string sessionId, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            yield break;
        }
    }
}
