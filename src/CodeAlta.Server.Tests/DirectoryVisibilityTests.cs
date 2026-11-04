using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Server.Services;
using CodeAlta.Server.Storage;

namespace CodeAlta.Server.Tests;

/// <summary>
/// What each caller sees listed (<c>doc/fleet/access-control.md</c>, "the server filters per caller"): a member sees their own
/// machines and sessions and their team's machines, on a team machine only the sessions they started there; an admin sees that
/// every session exists and what it costs; the directory snapshot and the changes pushed after it follow the same rules, and a
/// person taken out of a team stops seeing its machine at once.
/// </summary>
[TestClass]
public sealed class DirectoryVisibilityTests
{
    [TestMethod]
    public async Task EachCallerSeesTheMachinesAndSessionsAccessGivesThem_OverRestAndTheDirectory()
    {
        await using var server = await ServerFixture.StartAsync();
        using var owner = new TestDevice("Alex desktop");
        using var home = new TestDevice("Alex node");
        using var box = new TestDevice("Build box");
        using var beaNode = new TestDevice("Bea node");
        var (setup, _) = await server.SetUpWithNodeAsync(owner, home);
        await ApproveNodeAsync(server, owner, box, setup.Owner.Id, setup.Organization.Id);
        var (bea, beaId) = await AccessControlTests.AddPersonAsync(server, owner, setup, "Bea");
        var (max, maxId) = await AccessControlTests.AddPersonAsync(server, owner, setup, "Max");
        var (ada, _) = await AccessControlTests.AddPersonAsync(server, owner, setup, "Ada", FleetOrganizationRoles.Admin);
        await ApproveNodeAsync(server, bea, beaNode, beaId, setup.Organization.Id);
        var team = (await server.SendAsync(owner, HttpMethod.Post, "/api/v1/teams", new CreateTeamRequest("Infra"))).Read<TeamDto>();
        await server.SendAsync(owner, HttpMethod.Put, $"/api/v1/teams/{team.Id}/members/{maxId}", new SetTeamMemberRequest(FleetTeamRoles.Member));
        var offer = (await server.SendAsync(owner, HttpMethod.Post, "/api/v1/transfers",
            new OfferTransferRequest(FleetResourceKinds.Machine, box.DeviceId, FleetPrincipalKinds.Team, team.Id))).Read<TransferDto>();
        await server.SendAsync(owner, HttpMethod.Post, $"/api/v1/transfers/{offer.Id}/accept");

        await PublishAsync(server, home, "s-home");
        await PublishAsync(server, beaNode, "s-bea");
        await PublishAsync(server, box, "s-max", maxId);
        await PublishAsync(server, box, "s-other", "someone-else");

        Assert.AreEqual((Ids(beaNode.DeviceId), Ids("s-bea")), (await MachinesAsync(server, bea), await SessionsAsync(server, bea)), "Bea sees her own");
        Assert.AreEqual((Ids(box.DeviceId), Ids("s-max")), (await MachinesAsync(server, max), await SessionsAsync(server, max)),
            "Max sees the team machine and, there, the sessions he started");
        Assert.AreEqual((Ids(box.DeviceId, home.DeviceId, beaNode.DeviceId), Ids("s-bea", "s-home", "s-max", "s-other")),
            (await MachinesAsync(server, ada), await SessionsAsync(server, ada)), "an admin sees that every session exists");
        var beaDevices = (await server.SendAsync(bea, HttpMethod.Get, "/api/v1/devices")).Read<ListResponse<DeviceDto>>().Items.Select(static device => device.Id).ToHashSet();
        CollectionAssert.AreEquivalent(new[] { bea.DeviceId, beaNode.DeviceId }, beaDevices.ToArray(), "the devices of others are not listed to a member");
        Assert.AreEqual(4, (await server.SendAsync(bea, HttpMethod.Get, "/api/v1/organization")).Read<OrganizationDto>().People.Count, "a member sees the people");
        var nodeView = (await server.SendAsync(box, HttpMethod.Get, "/api/v1/sessions")).Read<ListResponse<SessionDto>>().Items.Select(static session => session.Id).ToHashSet();
        Assert.IsTrue(nodeView.SetEquals(["s-max", "s-other"]), "a node sees its own sessions");
        Assert.AreEqual(0, (await server.SendAsync(box, HttpMethod.Get, "/api/v1/organization")).Read<OrganizationDto>().People.Count, "a node lists no person");

        // The directory: the snapshot, then the changes, filtered alike.
        var events = new ConcurrentQueue<FleetDirectoryChange>();
        var (connection, _) = await server.ConnectAsync(max, connection => connection.SetEventHandler((fleetEvent, _) =>
        {
            if (fleetEvent.Event == FleetServerMethods.DirectoryChanged && fleetEvent.Payload?.Deserialize(FleetServerJsonContext.Default.FleetDirectoryChange) is { } change)
                events.Enqueue(change);
            return ValueTask.CompletedTask;
        }));
        await using (connection)
        {
            var reply = await connection.SendRequestAsync(FleetServerMethods.DirectoryWatch, null);
            var snapshot = reply!.Value.Deserialize(FleetServerJsonContext.Default.FleetDirectorySnapshot)!;
            CollectionAssert.AreEquivalent(new[] { "s-max" }, snapshot.Sessions.Select(static session => session.Fact.SessionId).ToArray());
            CollectionAssert.AreEquivalent(new[] { box.DeviceId }, snapshot.Machines.Select(static machine => machine.DeviceId).ToArray());

            await PublishAsync(server, home, "s-home-2");
            await PublishAsync(server, box, "s-max-2", maxId);
            await WaitForAsync(() => events.Any(change => change.Id == "s-max-2"));
            Assert.IsFalse(events.Any(change => change.Id == "s-home-2"), "a change Max may not see is not sent to him");

            // Out of the team, he no longer gets what happens on its machine; the sessions he started there stay his.
            await server.SendAsync(owner, HttpMethod.Delete, $"/api/v1/teams/{team.Id}/members/{maxId}");
            var before = events.Count(change => change.Machine?.DeviceId == box.DeviceId);
            await PublishAsync(server, box, "s-other-2", "someone-else");
            await PublishAsync(server, box, "s-max-3", maxId);
            await WaitForAsync(() => events.Any(change => change.Id == "s-max-3"));
            Assert.AreEqual(before, events.Count(change => change.Machine?.DeviceId == box.DeviceId), "the team machine is no longer sent to him");
            Assert.IsFalse(events.Any(change => change.Id == "s-other-2"));
            Assert.AreEqual(Ids(), await MachinesAsync(server, max), "nor listed");
            Assert.AreEqual(Ids("s-max", "s-max-2", "s-max-3"), await SessionsAsync(server, max));
        }
    }

    private static string Ids(params string[] ids) => string.Join(",", ids.Order(StringComparer.Ordinal));

    private static async Task<string> MachinesAsync(ServerFixture server, TestDevice device)
    {
        var answer = await server.SendAsync(device, HttpMethod.Get, "/api/v1/machines");
        Assert.AreEqual(HttpStatusCode.OK, answer.Status, answer.Body.ToString());
        return Ids([.. answer.Read<ListResponse<MachineDto>>().Items.Select(static machine => machine.DeviceId)]);
    }

    private static async Task<string> SessionsAsync(ServerFixture server, TestDevice device)
        => Ids([.. (await server.SendAsync(device, HttpMethod.Get, "/api/v1/sessions")).Read<ListResponse<SessionDto>>().Items.Select(static session => session.Id)]);

    private static async Task ApproveNodeAsync(ServerFixture server, TestDevice controller, TestDevice node, string personId, string organizationId)
    {
        var pending = await server.EnrollAsync(controller, node);
        var entry = await server.NextEntryAsync(controller, FleetDeviceListOperations.Add, pending.Id, node.PublicKey, FleetDeviceKinds.Node, personId, organizationId);
        Assert.AreEqual(HttpStatusCode.OK, (await server.SendAsync(controller, HttpMethod.Post, $"/api/v1/devices/{pending.Id}/approve", new DeviceListChangeRequest(entry))).Status);
    }

    // A machine publishes its inventory and the fact of one session, as its control connection does.
    private static async Task PublishAsync(ServerFixture server, TestDevice machine, string sessionId, string? personId = null)
    {
        // Each publication of a machine follows the one before it.
        var cursors = await server.GetService<ICoordinatorStore>().GetPublicationCursorsAsync(machine.DeviceId);
        var sequence = Math.Max(cursors.Facts, cursors.Inventory) + 1;
        var device = (await server.GetService<ICoordinatorStore>().GetDeviceAsync(machine.DeviceId))!;
        var publication = server.GetService<PublicationService>();
        await publication.PublishInventoryAsync(device, new FleetInventoryPublish(sequence, new FleetMachineInventory(machine.Name, "linux")), CancellationToken.None);
        await publication.PublishFactsAsync(device, new FleetFactsPublish(sequence, [new FleetSessionFact(sessionId, 1, 1, "idle", server.Clock.Now,
            server.Clock.Now, "anthropic", "claude", new FleetSessionCounts(1, 0, 10, 5), PersonId: personId)]), CancellationToken.None);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("Timed out.");
            await Task.Delay(25);
        }
    }
}
