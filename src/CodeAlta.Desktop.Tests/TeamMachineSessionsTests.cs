using System.Net;
using System.Net.Sockets;
using CodeAlta.Fleet;
using CodeAlta.Fleet.Client;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Server;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// A team machine (<c>doc/fleet/access-control.md</c>, what the node decides; <c>doc/fleet/security-and-privacy.md</c>, shared
/// machines): each member of the team sees and drives only the sessions they started there, and the sessions of a person the
/// signed access document suspends are stopped, their channels closed.
/// </summary>
[TestClass]
public sealed class TeamMachineSessionsTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task EachMemberReachesOnlyTheirOwnSessions_AndASuspendedPersonsWorkStops()
    {
        var token = TestContext.CancellationToken;
        await using var fixture = await FleetNodeFixture.StartAsync(token);
        using var maxKey = DeviceIdentity.LoadOrCreate(Path.Combine(fixture.Root, "max"));
        using var miaKey = DeviceIdentity.LoadOrCreate(Path.Combine(fixture.Root, "mia"));
        var trust = new TeamTrust();
        trust.Members[maxKey.Fingerprint] = ("max", maxKey.DeviceId);
        trust.Members[miaKey.Fingerprint] = ("mia", miaKey.DeviceId);

        await using var max = await ConnectAsync(fixture, trust, maxKey, token);
        await using var mia = await ConnectAsync(fixture, trust, miaKey, token);
        var created = await max.CreateSessionAsync(new FleetCreateSessionParams(Title: "Max's work"), "create-max", token);

        CollectionAssert.Contains((await max.ListSessionsAsync(null, token)).Sessions.Select(static session => session.Id).ToList(), created.SessionId);
        Assert.AreEqual(0, (await mia.ListSessionsAsync(null, token)).Sessions.Count, "another member does not see it");
        var hidden = await Assert.ThrowsExactlyAsync<FleetException>(() => mia.GetSessionStateAsync(created.SessionId, token));
        Assert.AreEqual(FleetNodeErrorCodes.SessionNotFound, hidden.Error.Code, "nor reaches it by its id");
        Assert.AreEqual(created.SessionId, (await max.GetSessionStateAsync(created.SessionId, token)).SessionId);
        await Assert.ThrowsExactlyAsync<FleetException>(() => mia.ListMovesAsync(token), "the moves of a team machine are not a member's");

        // The signed access document suspends Max: his session stops and his channel closes.
        trust.Suspended.Add("max");
        var stopped = await fixture.Service.ApplyAccessAsync(FleetAccessState.Empty with { Suspended = ["max"] }, token);
        CollectionAssert.AreEqual(new[] { created.SessionId }, stopped.ToArray());
        await Assert.ThrowsExactlyAsync<FleetConnectionClosedException>(() => max.ListSessionsAsync(null, token), "his channel is closed");
        Assert.AreEqual(0, (await mia.ListSessionsAsync(null, token)).Sessions.Count, "Mia goes on");
    }

    [TestMethod]
    public async Task AMembersSession_GetsNoneOfTheOwnersSecrets()
    {
        var token = TestContext.CancellationToken;
        await using var fixture = await FleetNodeFixture.StartAsync(token, null, null, null, appliesSpaceIdentities: true);
        // The owner of the machine keeps a token in its store; the space hands it to the CLI of its sessions.
        new CodeAlta.Catalog.Secrets.LocalSecretStore(fixture.Host.CatalogOptions.GlobalRoot).SetSecret("GRAFANA_TOKEN", "owner-token-value", "owner (tests)");
        await fixture.Host.SpaceCatalog.UpdateSettingsAsync(CodeAlta.Catalog.SpaceDescriptor.DefaultId, settings => settings with
        {
            Identity = new CodeAlta.Catalog.SpaceIdentity(Cli: [new CodeAlta.Catalog.SpaceCliProfile("grafana", [new("GRAFANA_TOKEN", "${secret:GRAFANA_TOKEN}")])]),
        }, token);
        var owner = await fixture.Host.Identities.ResolveAsync(CodeAlta.Catalog.SpaceDescriptor.DefaultId, null, "owner-session", token);
        Assert.IsTrue(owner.Environment.Variables.Any(static variable => variable.Value == "owner-token-value"), "the owner's work gets it");

        using var maxKey = DeviceIdentity.LoadOrCreate(Path.Combine(fixture.Root, "max"));
        var trust = new TeamTrust();
        trust.Members[maxKey.Fingerprint] = ("max", maxKey.DeviceId);
        await using var max = await ConnectAsync(fixture, trust, maxKey, token);
        var created = await max.CreateSessionAsync(new FleetCreateSessionParams(Title: "Max's work", SpaceId: CodeAlta.Catalog.SpaceDescriptor.DefaultId), "create-max", token);
        var identity = await fixture.Host.RuntimeService.GetSessionIdentityAsync(created.SessionId, token);
        Assert.IsFalse(identity.Environment.Variables.Any(static variable => variable.Value.Contains("owner-token-value", StringComparison.Ordinal)),
            "a session another person started never gets the owner's secrets");
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

    // The trust a team machine gives the members of its team, as FleetOrganizationTrust gives it from a signed access document.
    private sealed class TeamTrust : IDeviceTrustPolicy
    {
        public Dictionary<DeviceFingerprint, (string Person, string DeviceId)> Members { get; } = [];

        public HashSet<string> Suspended { get; } = [];

        public TrustedDevice? FindTrusted(DeviceFingerprint fingerprint)
            => Members.TryGetValue(fingerprint, out var member) && !Suspended.Contains(member.Person)
                ? new TrustedDevice(member.DeviceId, member.DeviceId, fingerprint, FleetOrganizationTrust.TeamMemberPermissions, DateTimeOffset.UnixEpoch, null)
                {
                    PersonId = member.Person,
                    OwnSessionsOnly = true,
                }
                : null;

        public bool IsRevoked(DeviceFingerprint fingerprint) => Members.TryGetValue(fingerprint, out var member) && Suspended.Contains(member.Person);
    }
}
