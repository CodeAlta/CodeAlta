using CodeAlta.Agent;
using CodeAlta.Fleet.Client;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Fleet.Protocol;

namespace CodeAlta.Node.Tests;

/// <summary>
/// A session marked Private is never reachable remotely (<c>doc/fleet/sessions-across-machines.md</c>): not listed, not
/// read, not watched, not driven, not moved, over a direct channel (here) as over a relayed one (<see cref="RelayedChannelTests"/>).
/// </summary>
[TestClass]
public sealed class FleetNodePrivateSessionTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task APrivateSession_IsNeitherListedNorReadNorDrivenNorMoved_OverADirectChannel()
    {
        await using var fixture = await NodeFixture.StartAsync(TestContext.CancellationToken);
        var token = TestContext.CancellationToken;
        var machine = await fixture.PairViewerAsync(token);
        // Every permission, so that only the privacy of the session refuses.
        fixture.Fleet.Service.TrustedDevices.Add("viewer", fixture.ViewerIdentity.Fingerprint,
            DevicePermissions.View | DevicePermissions.Drive | DevicePermissions.Start | DevicePermissions.Move);
        await using var client = await FleetNodeClient.ConnectAsync(machine, fixture.ViewerIdentity, token);
        var shared = await client.CreateSessionAsync(new FleetCreateSessionParams(Title: "Shared"), "create-shared", token);
        var hidden = await client.CreateSessionAsync(new FleetCreateSessionParams(Title: "Hidden"), "create-hidden", token);

        await using var watch = await client.WatchAsync(hidden.SessionId, token);
        await MarkPrivateAsync(fixture.Node, hidden.SessionId, token);

        await AssertPrivateAsync(client, hidden.SessionId, shared.SessionId, token);
        var ended = await WaitForEndAsync(watch, token);
        Assert.AreEqual("private", ended, "A watch ends when its session becomes Private.");

        var move = await Assert.ThrowsExactlyAsync<FleetException>(() => client.StartMoveAsync(new FleetMoveStartParams(hidden.SessionId, "move-private", Probe: true),
            cancellationToken: token));
        Assert.AreEqual(FleetNodeErrorCodes.SessionNotFound, move.Error.Code, "A Private session is not moved away by another machine.");
    }

    internal static async Task MarkPrivateAsync(NodeHost node, string sessionId, CancellationToken cancellationToken)
    {
        var result = await node.Host.Commands.SetSessionOptionsAsync(sessionId, new AgentSessionOptions { Private = true }, cancellationToken);
        Assert.AreEqual("ok", result.Status);
    }

    // What a viewer can no longer do with a Private session, while the other session stays reachable.
    internal static async Task AssertPrivateAsync(FleetNodeClient client, string hidden, string shared, CancellationToken token)
    {
        var listed = await client.ListSessionsAsync(new FleetSessionListParams(IncludeArchived: true), token);
        Assert.IsTrue(listed.Sessions.Any(session => session.Id == shared), "The other sessions are listed.");
        Assert.IsFalse(listed.Sessions.Any(session => session.Id == hidden), "A Private session is not listed.");

        await RefusedAsync(() => client.GetSessionStateAsync(hidden, token), "state");
        await RefusedAsync(() => client.ReadHistoryAsync(new FleetHistoryParams(hidden), token), "history");
        await RefusedAsync(() => client.ReadHistoryAsync(new FleetHistoryParams(hidden, Direction: "forward"), token), "forward history");
        await RefusedAsync(() => client.ReadHistoryAsync(new FleetHistoryParams(hidden, Format: "events"), token), "timeline");
        await RefusedAsync(() => client.WatchAsync(hidden, token), "watch");
        await RefusedAsync(() => client.SendAsync(hidden, "Hello", retryKey: "send-hidden", cancellationToken: token), "send");
        await RefusedAsync(() => client.QueueAsync(hidden, "Later", "queue-hidden", token), "queue");
        await RefusedAsync(() => client.SteerAsync(new FleetSteerParams(hidden, "Steer", Guid.NewGuid(), 1, "run"), "steer-hidden", token), "steer");
        await RefusedAsync(() => client.AbortAsync(new FleetAbortParams(hidden, null, Guid.NewGuid(), 1, "run"), "abort-hidden", token), "abort");
        await RefusedAsync(() => client.ListPermissionsAsync(hidden, token), "permissions");
        await RefusedAsync(() => client.AnswerPermissionAsync(hidden, Guid.NewGuid(), "deny", token), "permission answer");
        await RefusedAsync(() => client.ListAsksAsync(hidden, token), "asks");
        await RefusedAsync(() => client.AnswerAskAsync(new FleetAskAnswerParams(hidden, "ask", []), "answer-hidden", token), "ask answer");

        Assert.IsNotNull(await client.GetSessionStateAsync(shared, token), "The other session is still reachable.");
    }

    private static async Task RefusedAsync<T>(Func<Task<T>> call, string what)
    {
        var refused = await Assert.ThrowsExactlyAsync<FleetException>(call, $"The {what} of a Private session is refused.");
        Assert.AreEqual(FleetNodeErrorCodes.SessionNotFound, refused.Error.Code, $"The {what} of a Private session answers as for a session the node does not have.");
    }

    private static async Task<string?> WaitForEndAsync(FleetSessionWatch watch, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Patience);
        try
        {
            await foreach (var _ in watch.Updates.ReadAllAsync(timeout.Token))
            {
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Assert.Fail("The watch of a session that became Private never ended.");
        }

        return watch.EndReason;
    }
}
