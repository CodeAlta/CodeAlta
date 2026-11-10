using CodeAlta.Fleet.Client;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Fleet.Server;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// The seats of a node (<c>doc/fleet/node.md</c>, limits): beyond the sessions its owner lets work at once, a prompt of another
/// computer waits for its turn, first in first out, says its place, survives a restart, and is sent once a run ends.
/// </summary>
[TestClass]
public sealed class FleetNodeSeatTests
{
    private static readonly TimeSpan Patience = TestTime.Seconds(30);

    public TestContext TestContext { get; set; } = null!;

    private static async Task WaitUntilAsync(Func<bool> condition, string message, CancellationToken token)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(50, token);
        Assert.IsTrue(condition(), message);
    }

    [TestMethod]
    public async Task ASecondSession_IsQueuedWithItsPlace_AndSentOnceTheFirstEnds()
    {
        var token = TestContext.CancellationToken;
        await using var fixture = await FleetNodeFixture.StartAsync(token);
        fixture.Service.Seats.Poll = TimeSpan.FromMilliseconds(50);
        FleetMachineLimits.WriteSessions(fixture.Host.CatalogOptions.FleetRoot, 1);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Provider.Turns.Enqueue(ScriptedFleetProvider.WaitThenReply(gate.Task, "First done.", started));
        await using var client = await FleetNodeClient.ConnectAsync(await fixture.PairViewerAsync(token), fixture.ViewerIdentity, token);

        var first = await client.CreateSessionAsync(new FleetCreateSessionParams(Title: "First", Prompt: "The first work."), "create-first", token);
        Assert.AreEqual("accepted", first.Send?.Status);
        await started.Task.WithinAsync(Patience, token);

        var second = await client.CreateSessionAsync(new FleetCreateSessionParams(Title: "Second", Prompt: "The second work."), "create-second", token);
        Assert.AreEqual((FleetSeatStatuses.Queued, 1), (second.Send?.Status, second.Send?.Position));
        // Its place is in the list of sessions and in the seats of the machine; it waits for nobody.
        var listed = (await client.ListSessionsAsync(cancellationToken: token)).Sessions.Single(session => session.Id == second.SessionId);
        Assert.AreEqual((1, "idle"), (listed.QueuedPosition, listed.State));
        var seats = await client.ListSeatsAsync(token);
        Assert.AreEqual((1, 1), (seats.Limit, seats.Working));
        Assert.AreEqual(second.SessionId, seats.Queue.Single().SessionId);
        Assert.IsFalse((await fixture.Host.ListSessionsWaitingForPersonAsync()).Contains(second.SessionId), "a send that waits for a seat waits for nobody");
        Assert.IsFalse(fixture.Provider.Prompts.Any(static prompt => prompt.Contains("The second work.", StringComparison.Ordinal)));

        gate.TrySetResult();
        await WaitUntilAsync(() => fixture.Provider.Prompts.Any(static prompt => prompt.Contains("The second work.", StringComparison.Ordinal)),
            "the queued prompt is sent once the first run ended", token);
        Assert.IsEmpty((await client.ListSeatsAsync(token)).Queue);
    }

    [TestMethod]
    public async Task AQueuedPrompt_SurvivesARestart_AndIsSentOnce()
    {
        var token = TestContext.CancellationToken;
        await using var fixture = await FleetNodeFixture.StartAsync(token);
        FleetMachineLimits.WriteSessions(fixture.Host.CatalogOptions.FleetRoot, 1);
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Provider.Turns.Enqueue(ScriptedFleetProvider.WaitThenReply(never.Task, "Never.", started));
        string queuedSession;
        await using (var client = await FleetNodeClient.ConnectAsync(await fixture.PairViewerAsync(token), fixture.ViewerIdentity, token))
        {
            _ = await client.CreateSessionAsync(new FleetCreateSessionParams(Title: "Busy", Prompt: "The busy work."), "create-busy", token);
            await started.Task.WithinAsync(Patience, token);
            var queued = await client.CreateSessionAsync(new FleetCreateSessionParams(Title: "Queued", Prompt: "The queued work."), "create-queued", token);
            Assert.AreEqual(FleetSeatStatuses.Queued, queued.Send?.Status);
            queuedSession = queued.SessionId;
        }

        // The node restarts: the busy run is gone with its process, and the queued prompt is taken again, once.
        await fixture.RestartHostAsync(token);
        fixture.Service.ResumeHeldSends();
        await WaitUntilAsync(() => fixture.Provider.Prompts.Count(static prompt => prompt.Contains("The queued work.", StringComparison.Ordinal)) == 1,
            "the queued prompt is sent after the restart", token);
        fixture.Service.ResumeHeldSends();
        await Task.Delay(300, token);
        Assert.AreEqual(1, fixture.Provider.Prompts.Count(static prompt => prompt.Contains("The queued work.", StringComparison.Ordinal)), "sent once");
        Assert.IsFalse((await fixture.Host.ListSessionsWaitingForPersonAsync()).Contains(queuedSession));
    }

    [TestMethod]
    public async Task StartNow_StartsAQueuedRunPastTheLimit_AndCancel_DropsIt()
    {
        var token = TestContext.CancellationToken;
        await using var fixture = await FleetNodeFixture.StartAsync(token);
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Provider.Turns.Enqueue(ScriptedFleetProvider.WaitThenReply(never.Task, "Never.", started));
        // A computer that manages the machine sets its limit, and starts a run past it.
        var (offer, _) = fixture.Service.Pairing.CreateOffer("test-node", fixture.Addresses, TimeSpan.FromMinutes(1), true);
        var managed = await FleetNodeClient.PairAsync(offer, fixture.ViewerIdentity, "viewer", fixture.ViewerMachines, token);
        await using var client = await FleetNodeClient.ConnectAsync(managed, fixture.ViewerIdentity, token);
        Assert.AreEqual(1, (await client.SetLimitsAsync(1, token)).Sessions);
        Assert.AreEqual(1, (await client.GetLimitsAsync(token)).Sessions);

        _ = await client.CreateSessionAsync(new FleetCreateSessionParams(Title: "Busy", Prompt: "The busy work."), "create-busy", token);
        await started.Task.WithinAsync(Patience, token);
        var dropped = await client.CreateSessionAsync(new FleetCreateSessionParams(Title: "Dropped", Prompt: "The dropped work."), "create-dropped", token);
        var urgent = await client.CreateSessionAsync(new FleetCreateSessionParams(Title: "Urgent", Prompt: "The urgent work."), "create-urgent", token);
        Assert.AreEqual((1, 2), (dropped.Send?.Position, urgent.Send?.Position));
        var queue = (await client.ListSeatsAsync(token)).Queue;

        Assert.AreEqual("ok", (await client.CancelSeatAsync(queue[0].Id, token)).Status);
        Assert.AreEqual("ok", (await client.StartSeatAsync(queue[1].Id, token)).Status);
        await WaitUntilAsync(() => fixture.Provider.Prompts.Any(static prompt => prompt.Contains("The urgent work.", StringComparison.Ordinal)),
            "started now, past the limit", token);
        Assert.AreEqual("not_found", (await client.StartSeatAsync(queue[0].Id, token)).Status);
        Assert.IsEmpty((await client.ListSeatsAsync(token)).Queue);
        await Task.Delay(200, token);
        Assert.IsFalse(fixture.Provider.Prompts.Any(static prompt => prompt.Contains("The dropped work.", StringComparison.Ordinal)), "a cancelled run is never sent");
    }
}
