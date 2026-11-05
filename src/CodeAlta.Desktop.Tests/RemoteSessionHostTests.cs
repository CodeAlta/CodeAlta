using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Desktop.Fleet;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Desktop.SessionHosts;
using CodeAlta.Fleet.Client;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// The host of a paired machine against a real node in this process (a <see cref="CodeAlta.Orchestration.Hosting.CodeAltaHost"/>
/// served by <see cref="FleetNodeService"/> on loopback), through the whole channel stack.
/// </summary>
[TestClass]
public sealed class RemoteSessionHostTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private static readonly RemoteSessionHostOptions Quick = new()
    {
        MinimumBackoff = TimeSpan.FromMilliseconds(50), MaximumBackoff = TimeSpan.FromMilliseconds(200), ListInterval = TimeSpan.FromMilliseconds(200),
    };

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task RemoteHost_ListsReadsAndDrivesTheSessionsOfTheNode()
    {
        var token = TestContext.CancellationToken;
        await using var fixture = await FleetNodeFixture.StartAsync(token);
        var machine = await fixture.PairViewerAsync(token);
        await using var host = new RemoteSessionHost(machine, cancellation => FleetNodeClient.ConnectAsync(machine, fixture.ViewerIdentity, cancellation), Quick,
            localDeviceId: fixture.ViewerIdentity.DeviceId);
        var registry = new SessionHostRegistry(new SessionHostRegistryTests.RecordingSessionHost(null));
        registry.TakeMembership().Add(host);
        host.Start();
        await WaitForAsync(() => host.State == RemoteMachineState.Online, "the machine to be online", token);
        Assert.AreEqual("test-node", host.Listing.Info?.Name);

        var created = await host.CreateRemoteSessionAsync(new FleetCreateSessionParams(Title: "Remote session"), "create-1", token);
        Assert.IsTrue(await host.WaitListedAsync(created.SessionId, Patience, token), "The new session is listed once the list is read again.");
        Assert.AreSame(host, registry.Resolve(created.SessionId), "The registry gives a session to the machine that lists it.");
        Assert.AreSame(registry.Local, registry.Resolve(Guid.NewGuid().ToString()), "A session no machine lists is one of this process.");
        // Its composer names the provider as the machine does: a provider of the same key here may be another one.
        var choices = host.SelectionChoices(created.SessionId)!;
        Assert.AreEqual(("scripted", "Scripted"), (choices.Current.ProviderKey, choices.HostProviderName));

        // The display of the node, watched before the send, shows the answer of the turn.
        using var watching = CancellationTokenSource.CreateLinkedTokenSource(token);
        watching.CancelAfter(Patience);
        var answered = Task.Run(async () =>
        {
            await foreach (var replacement in host.ObserveDisplayAsync(created.SessionId, watching.Token))
                if (replacement.Snapshot.Sessions.SelectMany(static session => session.Text)
                    .Any(static text => text.Kind == AgentContentKind.Assistant && text.IsComplete && text.Text.Contains("Hello from the node.", StringComparison.Ordinal)))
                    return true;
            return false;
        }, token);
        var gate = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        fixture.Provider.Turns.Enqueue(ScriptedFleetProvider.WaitThenReply(gate.Task, "Hello from the node.", started));
        await Task.Delay(300, token);

        var admission = host.AdmitSend(new OwnedTextSendRequest("send-1", created.SessionId, "Hello node"), token);
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.Accepted, admission.Kind);
        Assert.IsTrue(host.Admitted(admission.Receipt!.OperationId), "An abort of the send finds its host.");
        Assert.AreSame(host, registry.ResolveOperation(admission.Receipt.OperationId));
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.Replay, host.AdmitSend(new OwnedTextSendRequest("send-1", created.SessionId, "Hello node"), token).Kind,
            "A retry of the same key is the same receipt.");
        var result = await admission.Receipt.Completion.WaitAsync(Patience, token);
        Assert.AreEqual(OwnedSessionCommandOutcome.Completed, result.Outcome, result.Code);

        await started.Task.WaitAsync(Patience, token);
        var state = await host.ReadRuntimeStateAsync(created.SessionId, token);
        Assert.IsNotNull(state.Entry?.ActiveRunId, "The runtime state names the run at work, which steer and stop are bound to.");
        gate.SetResult();
        Assert.IsTrue(await answered.WaitAsync(Patience, token), "The live display shows the answer.");

        // The timeline: the journal's own events, with the ends of their records and the revision, as the page reads them here.
        OwnedTimelineTail tail = null!;
        await WaitForAsync(async () =>
        {
            tail = await host.ReadTimelineTailAsync(created.SessionId, null, token);
            return tail.Page!.Entries.Any(static entry => entry.Event is AgentContentCompletedEvent { Kind: AgentContentKind.Assistant } content
                && content.Content == "Hello from the node.");
        }, "the answer in the timeline", token);
        Assert.IsNotNull(tail.Page!.Revision);
        Assert.IsTrue(tail.Page.Entries.All(static entry => entry.SourceEnd > entry.Offset), "Each record has its end.");
        Assert.IsTrue(tail.Page.Entries.Any(static entry => entry.Event is AgentContentCompletedEvent { Kind: AgentContentKind.User } content
            && content.Content.Contains("Hello node", StringComparison.Ordinal)), "The prompt sent from here is in the journal of the node.");
        var prompt = tail.Page.Entries.Select(static entry => entry.Event).OfType<AgentContentCompletedEvent>().First(static content => content.Kind == AgentContentKind.User);
        Assert.AreEqual("send-1", PromptImageHistory.ReadClientRequestId(prompt.Details), "The page finds its prompt by the key it gave it.");
        if (tail.Stamp is { } stamp)
            Assert.IsTrue((await host.ReadTimelineTailAsync(created.SessionId, stamp, token)).Unchanged, "An unchanged journal is not read again.");
        var forward = await host.ReadHistoryPageAsync(created.SessionId, null, token);
        Assert.IsTrue(forward.Entries.Count > 0);
        var record = await host.ReadHistoryRecordAsync(created.SessionId, tail.Page.Entries[^1].Offset, token);
        Assert.AreEqual(tail.Page.Entries[^1].Event.GetType(), record?.GetType(), "One record is read by its offset.");

        Assert.AreEqual(0, (await host.ListPermissionsAsync(created.SessionId, token)).Entries.Count);
        _ = host.ListAsks(created.SessionId);
        Assert.IsNull(host.ListAsks(created.SessionId).Head);

        // The list of the page: the session under a project that stands for its machine, which it names.
        var merged = WorkspaceService.WithRemoteSessions(new WorkspaceSnapshot(true, [], [], false, false, false), [host]);
        var row = merged.Sessions.Single(session => session.Id == created.SessionId);
        Assert.AreEqual(machine.DeviceId, row.MachineId);
        Assert.AreEqual("test-node", row.MachineName);
        Assert.AreEqual("Scripted", row.MachineProviderName, "Its row names the provider as the machine does.");
        Assert.AreEqual(WorkspaceService.MachineScopePrefix + machine.DeviceId, row.ProjectId);
        Assert.AreEqual(machine.DeviceId, merged.Projects.Single(project => project.Id == row.ProjectId).MachineId);

        // A session this process lists keeps its row, unless it gave the session to the machine that lists it (a move).
        var copy = new WorkspaceSession(created.SessionId, "Moved", "/here", "scripted", DateTimeOffset.UtcNow, "Moved", false, null, "project", "here", null,
            DateTimeOffset.UtcNow);
        var local = new WorkspaceSnapshot(true, [], [copy], false, false, false);
        Assert.IsNull(WorkspaceService.WithRemoteSessions(local, [host], static _ => null, _ => false).Sessions.Single(session => session.Id == created.SessionId).MachineId);
        Assert.AreEqual(machine.DeviceId, WorkspaceService.WithRemoteSessions(local, [host], static _ => null, _ => true).Sessions.Single(session => session.Id == created.SessionId).MachineId,
            "The read-only copy of a moved session gives its row to the live one.");
    }

    [TestMethod]
    public async Task APairedMachineWithNoSession_HasItsRow_WithItsState()
    {
        var token = TestContext.CancellationToken;
        await using var fixture = await FleetNodeFixture.StartAsync(token);
        var machine = await fixture.PairViewerAsync(token);
        await using var host = new RemoteSessionHost(machine, cancellation => FleetNodeClient.ConnectAsync(machine, fixture.ViewerIdentity, cancellation), Quick,
            localDeviceId: fixture.ViewerIdentity.DeviceId);
        var empty = new WorkspaceSnapshot(true, [], [], false, false, false);
        Assert.AreSame(empty, WorkspaceService.WithRemoteSessions(empty, []), "Without a paired machine, the list is the one of this process.");

        // Before it answered, and once it answered with no session: the row of the machine is there, so is its +.
        var before = WorkspaceService.WithRemoteSessions(empty, [host]).Projects.Single();
        Assert.AreEqual(WorkspaceService.MachineScopePrefix + machine.DeviceId, before.Id);
        Assert.AreEqual(machine.DeviceId, before.MachineId);
        Assert.AreEqual("connecting", before.MachineState);
        host.Start();
        await WaitForAsync(() => host.State == RemoteMachineState.Online && host.Listing.Info is not null, "the machine to be online", token);
        var merged = WorkspaceService.WithRemoteSessions(empty, [host]);
        Assert.AreEqual(0, merged.Sessions.Length);
        var row = merged.Projects.Single();
        Assert.AreEqual("test-node", row.Name);
        Assert.AreEqual("online", row.MachineState);
    }

    [TestMethod]
    public async Task RemoteHost_GoesOffline_AndConnectsAgain_WhenTheChannelDrops()
    {
        var token = TestContext.CancellationToken;
        await using var fixture = await FleetNodeFixture.StartAsync(token);
        var machine = await fixture.PairViewerAsync(token);
        var clients = new List<FleetNodeClient>();
        var states = new List<RemoteMachineState>();
        await using var host = new RemoteSessionHost(machine, async cancellation =>
        {
            var client = await FleetNodeClient.ConnectAsync(machine, fixture.ViewerIdentity, cancellation);
            lock (clients) clients.Add(client);
            return client;
        }, Quick);
        host.Changed += changed => { lock (states) states.Add(changed.State); };
        host.Start();
        await WaitForAsync(() => host.State == RemoteMachineState.Online, "the machine to be online", token);

        FleetNodeClient first;
        lock (clients) first = clients[0];
        await first.CloseAsync(token);
        await WaitForAsync(() => { lock (clients) return clients.Count >= 2 && host.State == RemoteMachineState.Online; }, "the machine to be online again", token);
        lock (states) CollectionAssert.IsSubsetOf(new[] { RemoteMachineState.Offline, RemoteMachineState.Connecting, RemoteMachineState.Online }, states.ToArray());
        Assert.IsNotNull(host.LastSeen);
    }

    [TestMethod]
    public async Task RemoteHost_AdmitsNothing_AndReadsNothing_WhileTheMachineIsOffline()
    {
        var token = TestContext.CancellationToken;
        await using var fixture = await FleetNodeFixture.StartAsync(token);
        var machine = await fixture.PairViewerAsync(token);
        // A machine whose addresses no longer answer.
        var gone = machine with { Addresses = ["127.0.0.1:1"] };
        await using var host = new RemoteSessionHost(gone, cancellation => FleetNodeClient.ConnectAsync(gone, fixture.ViewerIdentity, cancellation), Quick);
        host.Start();
        await WaitForAsync(() => host.State == RemoteMachineState.Offline && host.Problem is not null, "the machine to be offline", token);

        Assert.AreEqual(OwnedSessionCommandAdmissionKind.Closed, host.AdmitSend(new OwnedTextSendRequest("send-1", "some-session", "Hello"), token).Kind);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => host.ReadTimelineTailAsync("some-session", null, token));
        Assert.AreEqual("metadata_unavailable", (await host.ObserveRuntimeStateAsync("some-session", DateTimeOffset.UtcNow, "global", null, null, token)).Status);
    }

    [TestMethod]
    public async Task RemoteHost_IsUnauthorized_OnceTheNodeRevokesTheDevice()
    {
        var token = TestContext.CancellationToken;
        await using var fixture = await FleetNodeFixture.StartAsync(token);
        var machine = await fixture.PairViewerAsync(token);
        await using var host = new RemoteSessionHost(machine, cancellation => FleetNodeClient.ConnectAsync(machine, fixture.ViewerIdentity, cancellation), Quick);
        host.Start();
        await WaitForAsync(() => host.State == RemoteMachineState.Online, "the machine to be online", token);

        Assert.IsTrue(await fixture.Service.RevokeAsync(fixture.ViewerIdentity.DeviceId));
        await WaitForAsync(() => host.State == RemoteMachineState.Unauthorized, "the machine to refuse this device", token);
    }

    [TestMethod]
    public async Task DesktopFleet_APairingLinkOfAMachineReachedThroughItsServerOnly_IsNotPairedAlready()
    {
        var token = TestContext.CancellationToken;
        await using var fixture = await FleetNodeFixture.StartAsync(token);
        var registry = new SessionHostRegistry(new SessionHostRegistryTests.RecordingSessionHost(null));
        var fleetRoot = Path.Combine(fixture.ViewerFleetRoot, "..", "desktop-fleet");
        await using var fleet = new DesktopFleet(fleetRoot, registry.TakeMembership(), "desktop",
            static (machine, identity) => new RemoteSessionHost(machine, cancellation => FleetNodeClient.ConnectAsync(machine, identity, cancellation), Quick));
        await fleet.SyncOrganizationMachinesAsync([(fixture.NodeIdentity.DeviceId, "test-node")], token);
        Assert.IsNotNull(fleet.Find(fixture.NodeIdentity.DeviceId), "The server lists the machine: this computer reaches it through its relay.");

        Assert.IsTrue(fleet.TryPreview(fixture.CreatePairingLink(), out var preview, out _));
        Assert.IsFalse(preview.AlreadyPaired, "A machine reached through its server only is not paired: pairing does not replace a pairing.");
    }

    [TestMethod]
    public async Task DesktopFleet_GivesTheSpacesItsMachinesPublish_ForTheirKeys()
    {
        var token = TestContext.CancellationToken;
        await using var fixture = await FleetNodeFixture.StartAsync(token);
        // The machine publishes its Default to a server; this computer links no space of its own.
        await fixture.Host.SpaceCatalog.UpdateSettingsAsync(SpaceDescriptor.DefaultId, settings => settings with { Fleet = new SpaceFleet("https://fleet.example") }, token);
        var registry = new SessionHostRegistry(new SessionHostRegistryTests.RecordingSessionHost(null));
        var fleetRoot = Path.Combine(fixture.ViewerFleetRoot, "..", "desktop-fleet");
        await using var fleet = new DesktopFleet(fleetRoot, registry.TakeMembership(), "desktop",
            static (machine, identity) => new RemoteSessionHost(machine, cancellation => FleetNodeClient.ConnectAsync(machine, identity, cancellation), Quick));
        Assert.AreEqual(0, fleet.PublishedSpaces().Count);
        await fleet.PairAsync(fixture.CreatePairingLink(), token);

        await WaitForAsync(() => fleet.PublishedSpaces().Contains(SpaceDescriptor.DefaultId), "the space the machine publishes", token);
    }

    [TestMethod]
    public async Task DesktopFleet_PairsFromALink_AddsTheHost_AndForgetsTheMachine()
    {
        var token = TestContext.CancellationToken;
        await using var fixture = await FleetNodeFixture.StartAsync(token);
        var registry = new SessionHostRegistry(new SessionHostRegistryTests.RecordingSessionHost(null));
        var fleetRoot = Path.Combine(fixture.ViewerFleetRoot, "..", "desktop-fleet");
        await using var fleet = new DesktopFleet(fleetRoot, registry.TakeMembership(), "desktop",
            static (machine, identity) => new RemoteSessionHost(machine, cancellation => FleetNodeClient.ConnectAsync(machine, identity, cancellation), Quick));
        await fleet.StartAsync(token);
        Assert.AreEqual(0, fleet.Hosts.Count);
        Assert.IsFalse(File.Exists(Path.Combine(fleetRoot, "device-key.json")), "No key is made before a pairing.");

        var link = fixture.CreatePairingLink();
        Assert.IsTrue(fleet.TryPreview(link, out var preview, out _));
        Assert.AreEqual("test-node", preview.Name);
        Assert.AreEqual(fixture.NodeIdentity.Fingerprint.ToShortWords(), preview.Words, "The words are the ones of the key of the node.");
        Assert.AreEqual(6, preview.Words.Split(' ').Length);
        Assert.IsFalse(fleet.TryPreview("codealta-pair://nonsense", out _, out _));

        var machine = await fleet.PairAsync(link, token);
        Assert.AreEqual(fixture.NodeIdentity.DeviceId, machine.DeviceId);
        var host = fleet.Find(machine.DeviceId);
        Assert.IsNotNull(host);
        CollectionAssert.Contains(registry.Remotes.ToArray(), host);
        await WaitForAsync(() => host.State == RemoteMachineState.Online, "the paired machine to be online", token);
        Assert.AreEqual("online", MachinesService.View(host).State);

        // Another instance of the profile reads the same machines at its start.
        var again = new SessionHostRegistry(new SessionHostRegistryTests.RecordingSessionHost(null));
        await using (var restarted = new DesktopFleet(fleetRoot, again.TakeMembership(), "desktop"))
        {
            await restarted.StartAsync(token);
            Assert.AreEqual(machine.DeviceId, restarted.Hosts.Single().MachineId);
        }

        Assert.IsTrue(await fleet.ForgetAsync(machine.DeviceId, token));
        Assert.AreEqual(0, fleet.Hosts.Count);
        Assert.AreEqual(0, registry.Remotes.Count);
        Assert.AreEqual(0, new PairedMachineStore(fleetRoot).List().Count);
        Assert.ThrowsExactly<InvalidOperationException>(() => registry.TakeMembership(), "The membership has one owner.");
    }

    [TestMethod]
    public void ToReplacement_KeepsTheTextsToolsAndLifecycleOfTheNode()
    {
        var display = new FleetSessionDisplay("w", "s", 7, 5, true, false, new FleetDisplayLifecycle("RunSubmitted", "run-1", null), 2, null, null,
            [new FleetDisplayText("run-1", "c1", "Assistant", "Hi", false, false) { ClientRequestId = "fleet:me:key-1" }, new FleetDisplayText("run-1", "c2", "NoSuchKind", "?", true, false),
                new FleetDisplayText("run-0", "c0", "Assistant", "An earlier run", true, false)],
            [new FleetDisplayTool("a1", "run-1", "Started", "shell")]);

        var first = RemoteSessionHost.ToReplacement(Guid.NewGuid(), display, initial: true, "fleet:me:");
        var next = RemoteSessionHost.ToReplacement(first.Snapshot.Epoch, display, initial: false);

        Assert.IsTrue(first.IsInitial);
        Assert.IsNull(first.PreviousRevision);
        Assert.IsFalse(first.HasGap);
        Assert.AreEqual(5, next.PreviousRevision);
        Assert.IsTrue(next.HasGap);
        var session = first.Snapshot.Sessions.Single();
        Assert.AreEqual(1, session.Text.Length, "A kind this version does not know, and the texts of earlier runs, are left out.");
        Assert.AreEqual(AgentContentKind.Assistant, session.Text[0].Kind);
        Assert.AreEqual("key-1", session.Text[0].ClientRequestId, "A text of a prompt of this device has the key the page gave it.");
        Assert.AreEqual("shell", session.ToolActivities.Single().Name);
        Assert.AreEqual(SessionLifecycleEventKind.RunSubmitted, session.Lifecycle?.Kind);
        Assert.AreEqual(2, session.QueuedPromptCount);
    }

    private static Task WaitForAsync(Func<bool> condition, string what, CancellationToken cancellationToken)
        => WaitForAsync(() => Task.FromResult(condition()), what, cancellationToken);

    private static async Task WaitForAsync(Func<Task<bool>> condition, string what, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"Waited too long for {what}.");
            await Task.Delay(50, cancellationToken);
        }
    }
}
