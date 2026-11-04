using System.Net;
using System.Net.Sockets;
using CodeAlta.Fleet.Client;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Server;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// A personal machine and the people of its organization (<c>doc/fleet/access-control.md</c>, what the node decides): a person the
/// machine does not name gets nothing, and their first try asks its owner; once the owner names them, they start and drive their
/// own sessions there, never the owner's; a refusal is not asked again.
/// </summary>
[TestClass]
public sealed class NamedPeopleTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task APersonTheMachineDoesNotNameAsksItsOwner_AndOnceNamedUsesTheirOwnSessionsOnly()
    {
        var token = TestContext.CancellationToken;
        await using var fixture = await FleetNodeFixture.StartAsync(token);
        using var beaKey = DeviceIdentity.LoadOrCreate(Path.Combine(fixture.Root, "bea"));
        using var cyKey = DeviceIdentity.LoadOrCreate(Path.Combine(fixture.Root, "cy"));
        var organization = new OtherPeople { [beaKey.Fingerprint] = ("bea", beaKey.DeviceId), [cyKey.Fingerprint] = ("cy", cyKey.DeviceId) };
        var paired = await fixture.PairViewerAsync(token);
        FleetCreateSessionResult mine;
        await using (var owner = await FleetNodeClient.ConnectAsync(paired, fixture.ViewerIdentity, token))
            mine = await owner.CreateSessionAsync(new FleetCreateSessionParams(Title: "The owner's work"), "create-owner", token);

        await using (var bea = await ConnectAsync(fixture, organization, beaKey, token))
        {
            var refused = await Assert.ThrowsExactlyAsync<FleetException>(() => bea.ListSessionsAsync(null, token));
            Assert.AreEqual(FleetErrorCodes.Forbidden, refused.Error.Code);
            StringAssert.Contains(refused.Error.Message, "owner of this machine was asked");
        }

        var asks = fixture.Service.People.Read().Asks;
        Assert.AreEqual(("bea", beaKey.DeviceId), (asks.Single().PersonId, asks.Single().DeviceId), "the first try is a request for the owner");

        // The owner names Bea; Cy is refused and not asked about again.
        fixture.Service.People.Set(new FleetNodePeopleChange("bea", ["view", "drive", "start"]));
        await using (var cy = await ConnectAsync(fixture, organization, cyKey, token)) await Assert.ThrowsExactlyAsync<FleetException>(() => cy.ListSessionsAsync(null, token));
        fixture.Service.People.Set(new FleetNodePeopleChange("cy", Refuse: true));
        await using (var cy = await ConnectAsync(fixture, organization, cyKey, token)) await Assert.ThrowsExactlyAsync<FleetException>(() => cy.ListSessionsAsync(null, token));
        Assert.AreEqual(0, fixture.Service.People.Read().Asks.Count, "a person refused is not asked about again");
        Assert.ThrowsExactly<ArgumentException>(() => fixture.Service.People.Set(new FleetNodePeopleChange("dan", ["manage"])), "a named person never manages the machine");

        await using (var bea = await ConnectAsync(fixture, organization, beaKey, token))
        {
            Assert.AreEqual(0, (await bea.ListSessionsAsync(null, token)).Sessions.Count, "the owner's sessions are not hers");
            var hidden = await Assert.ThrowsExactlyAsync<FleetException>(() => bea.GetSessionStateAsync(mine.SessionId, token));
            Assert.AreEqual(FleetNodeErrorCodes.SessionNotFound, hidden.Error.Code);
            var created = await bea.CreateSessionAsync(new FleetCreateSessionParams(Title: "Bea's work"), "create-bea", token);
            CollectionAssert.AreEqual(new[] { created.SessionId }, (await bea.ListSessionsAsync(null, token)).Sessions.Select(static session => session.Id).ToArray());
            Assert.AreEqual("bea", fixture.Service.PersonOfSession(created.SessionId), "the server is told whose session it is");
        }
    }

    private static async Task<FleetNodeClient> ConnectAsync(FleetNodeFixture fixture, IDeviceTrustPolicy trust, DeviceIdentity identity, CancellationToken token)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var connecting = new TcpClient();
        var connect = connecting.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, token).AsTask();
        var accepted = await listener.AcceptTcpClientAsync(token);
        await connect;
        _ = fixture.Service.ServeRelayedAsync(accepted.GetStream(), trust, CancellationToken.None);
        return await FleetNodeClient.ConnectAsync(connecting.GetStream(), fixture.NodeIdentity.Fingerprint, identity, token);
    }

    // The trust of the organization on a personal machine for the controller devices of other people: nothing, with their person.
    private sealed class OtherPeople : Dictionary<DeviceFingerprint, (string Person, string DeviceId)>, IDeviceTrustPolicy
    {
        public TrustedDevice? FindTrusted(DeviceFingerprint fingerprint)
            => TryGetValue(fingerprint, out var member)
                ? new TrustedDevice(member.DeviceId, member.DeviceId, fingerprint, DevicePermissions.None, DateTimeOffset.UnixEpoch, null) { PersonId = member.Person, OwnSessionsOnly = true }
                : null;

        public bool IsRevoked(DeviceFingerprint fingerprint) => false;
    }
}
