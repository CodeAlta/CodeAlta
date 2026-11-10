using System.Net;
using System.Net.Sockets;
using CodeAlta.Agent;
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

    [TestMethod]
    public async Task TheSubSessionsAMembersSessionStarts_GetNoneOfTheOwnersSecretsEither()
    {
        var token = TestContext.CancellationToken;
        await using var fixture = await FleetNodeFixture.StartAsync(token, null, null, null, appliesSpaceIdentities: true);
        new CodeAlta.Catalog.Secrets.LocalSecretStore(fixture.Host.CatalogOptions.GlobalRoot).SetSecret("GRAFANA_TOKEN", "owner-token-value", "owner (tests)");
        await fixture.Host.SpaceCatalog.UpdateSettingsAsync(CodeAlta.Catalog.SpaceDescriptor.DefaultId, settings => settings with
        {
            Identity = new CodeAlta.Catalog.SpaceIdentity(Cli: [new CodeAlta.Catalog.SpaceCliProfile("grafana", [new("GRAFANA_TOKEN", "${secret:GRAFANA_TOKEN}")])]),
        }, token);
        using var maxKey = DeviceIdentity.LoadOrCreate(Path.Combine(fixture.Root, "max"));
        var trust = new TeamTrust();
        trust.Members[maxKey.Fingerprint] = ("max", maxKey.DeviceId);
        await using var max = await ConnectAsync(fixture, trust, maxKey, token);
        var created = await max.CreateSessionAsync(new FleetCreateSessionParams(Title: "Max's work", SpaceId: CodeAlta.Catalog.SpaceDescriptor.DefaultId), "create-max", token);

        // Its agent starts a sub-session on this machine, which starts one in turn (alta session create): Max's work too.
        var provider = fixture.Provider.Descriptor;
        var child = await fixture.Host.Commands.CreateDraftSessionAsync(null, provider, "Max's sub-agent", null, null, CodeAlta.Catalog.SpaceDescriptor.DefaultId, null,
            Guid.NewGuid().ToString(), created.SessionId);
        var childIdentity = await fixture.Host.RuntimeService.GetSessionIdentityAsync(child.SessionId, token);
        Assert.IsFalse(childIdentity.Environment.Variables.Any(static variable => variable.Value.Contains("owner-token-value", StringComparison.Ordinal)),
            "a sub-session of another person's session never gets the owner's secrets");
        var grandchild = await fixture.Host.Commands.CreateDraftSessionAsync(null, provider, "Its own sub-agent", null, null, CodeAlta.Catalog.SpaceDescriptor.DefaultId, null,
            Guid.NewGuid().ToString(), child.SessionId);
        var grandchildIdentity = await fixture.Host.RuntimeService.GetSessionIdentityAsync(grandchild.SessionId, token);
        Assert.IsFalse(grandchildIdentity.Environment.Variables.Any(static variable => variable.Value.Contains("owner-token-value", StringComparison.Ordinal)),
            "nor does one it starts");

        // A sub-session of the owner's own session keeps the owner's work.
        var own = await fixture.Host.Commands.CreateDraftSessionAsync(null, provider, "The owner's work", null, null, CodeAlta.Catalog.SpaceDescriptor.DefaultId, null,
            Guid.NewGuid().ToString(), null);
        var ownChild = await fixture.Host.Commands.CreateDraftSessionAsync(null, provider, "The owner's sub-agent", null, null, CodeAlta.Catalog.SpaceDescriptor.DefaultId, null,
            Guid.NewGuid().ToString(), own.SessionId);
        Assert.IsTrue((await fixture.Host.RuntimeService.GetSessionIdentityAsync(ownChild.SessionId, token)).Environment.Variables
            .Any(static variable => variable.Value == "owner-token-value"));
    }

    [TestMethod]
    public async Task TheSessionAnAgentOfAMembersMachineStartsHere_IsThatMembersWork_AndGetsNoneOfTheOwnersSecrets()
    {
        var token = TestContext.CancellationToken;
        await using var fixture = await FleetNodeFixture.StartAsync(token, null, null, null, appliesSpaceIdentities: true);
        new CodeAlta.Catalog.Secrets.LocalSecretStore(fixture.Host.CatalogOptions.GlobalRoot).SetSecret("GRAFANA_TOKEN", "owner-token-value", "owner (tests)");
        await fixture.Host.SpaceCatalog.UpdateSettingsAsync(CodeAlta.Catalog.SpaceDescriptor.DefaultId, settings => settings with
        {
            Identity = new CodeAlta.Catalog.SpaceIdentity(Cli: [new CodeAlta.Catalog.SpaceCliProfile("grafana", [new("GRAFANA_TOKEN", "${secret:GRAFANA_TOKEN}")])]),
        }, token);
        fixture.Service.AgentSessions.WritePolicy(new FleetAgentPolicyInfo([CodeAlta.Catalog.SpaceDescriptor.DefaultId]));
        using var maxKey = DeviceIdentity.LoadOrCreate(Path.Combine(fixture.Root, "max"));
        var trust = new TeamTrust();
        trust.Members[maxKey.Fingerprint] = ("max", maxKey.DeviceId);
        await using var max = await ConnectAsync(fixture, trust, maxKey, token);

        // An agent of a session on Max's laptop starts a sub-agent on this machine: its parent is on his laptop, not here.
        var limits = new FleetAgentLimits([], CodeAlta.Orchestration.Runtime.SessionPermissionModes.Bypass, null, true, 1);
        var signed = FleetAgentSigning.Sign(maxKey, new FleetAgentRequest(FleetAgentRequest.CurrentVersion, FleetAgentRequestKinds.Start, fixture.NodeIdentity.DeviceId, "", "",
            DateTimeOffset.UtcNow, "", "parent-on-max-laptop", "max-laptop", CodeAlta.Catalog.SpaceDescriptor.DefaultId, null, null, FleetAgentRequestDocuments.HashText("Hello"), limits));
        var created = await max.CreateSessionAsync(new FleetCreateSessionParams(SpaceId: CodeAlta.Catalog.SpaceDescriptor.DefaultId, Prompt: "Hello") { Agent = signed },
            "start-agent", token);

        Assert.AreEqual("max", fixture.Service.PersonOfSession(created.SessionId), "the session is Max's work");
        var identity = await fixture.Host.RuntimeService.GetSessionIdentityAsync(created.SessionId, token);
        Assert.IsFalse(identity.Environment.Variables.Any(static variable => variable.Value.Contains("owner-token-value", StringComparison.Ordinal)),
            "the session an agent of a member's machine starts never gets the owner's secrets");
    }

    // Subscriptions are personal (doc/fleet/security-and-privacy.md): a session another person starts on a team machine, and every
    // session it starts, keeps to account credentials, whatever provider it names or changes to; the owner's own work keeps them all.
    [TestMethod]
    public async Task AMembersSession_AndItsSubSessions_AreRefusedAPersonalPlan_TheOwnersWorkIsNot()
    {
        var token = TestContext.CancellationToken;
        await using var fixture = await FleetNodeFixture.StartAsync(token, null, null, null, appliesSpaceIdentities: false, admitEnvironment: null,
            isPersonalProvider: static provider => provider.ProviderId.Value == "personal");
        // A provider of the machine that runs with the plan of the person who enrolled it (only its descriptor is read here).
        fixture.Host.ModelProviderRegistry.RegisterOrReplace(new ModelProviderDescriptor(new ModelProviderId("personal"), "Personal"), static () => throw new NotSupportedException());
        using var maxKey = DeviceIdentity.LoadOrCreate(Path.Combine(fixture.Root, "max"));
        var trust = new TeamTrust();
        trust.Members[maxKey.Fingerprint] = ("max", maxKey.DeviceId);
        await using var max = await ConnectAsync(fixture, trust, maxKey, token);

        // Named at the start: refused, with the providers that would run it.
        var refused = await Assert.ThrowsExactlyAsync<FleetException>(() => max.CreateSessionAsync(new FleetCreateSessionParams(Title: "On a plan",
            Selection: new FleetModelSelection("personal")), "create-personal", token));
        Assert.AreEqual(FleetErrorCodes.Forbidden, refused.Error.Code);
        StringAssert.Contains(refused.Error.Message, "personal plan");
        StringAssert.Contains(refused.Error.Message, "scripted", "it names the providers of an account");

        // A change of provider at an attachment, and a sub-session it starts: refused too.
        var created = await max.CreateSessionAsync(new FleetCreateSessionParams(Title: "Max's work"), "create-max", token);
        var gate = fixture.Host.RuntimeService.ProviderGate!;
        var session = (await fixture.Host.RuntimeService.TryGetRecoverableSessionAsync(created.SessionId, token))!;
        StringAssert.Contains(await gate(session, "personal", token), "personal plan");
        Assert.IsNull(await gate(session, fixture.Provider.Descriptor.ProviderId.Value, token), "an account provider runs it");
        var child = await fixture.Host.Commands.CreateDraftSessionAsync(null, fixture.Provider.Descriptor, "Max's sub-agent", null, null, null, null,
            Guid.NewGuid().ToString(), created.SessionId);
        StringAssert.Contains(await gate(child, "personal", token), "personal plan", "a sub-session of a member's session");

        // The owner's own work keeps the personal plan of the machine.
        var own = await fixture.Host.Commands.CreateDraftSessionAsync(null, fixture.Provider.Descriptor, "The owner's work", null, null, null, null,
            Guid.NewGuid().ToString(), null);
        Assert.IsNull(await gate(own, "personal", token));
    }

    [TestMethod]
    public async Task WhoseWorkASessionIs_IsForgottenWhenItIsDeleted_NeverBecauseItIsNotFoundNow()
    {
        var token = TestContext.CancellationToken;
        await using var fixture = await FleetNodeFixture.StartAsync(token);
        using var maxKey = DeviceIdentity.LoadOrCreate(Path.Combine(fixture.Root, "max"));
        var trust = new TeamTrust();
        trust.Members[maxKey.Fingerprint] = ("max", maxKey.DeviceId);
        await using var max = await ConnectAsync(fixture, trust, maxKey, token);
        var created = await max.CreateSessionAsync(new FleetCreateSessionParams(Title: "Max's work"), "create-max", token);

        // A run of the organization recorded before its session is in the catalog (in flight), then more records than the node keeps
        // without looking: the node does not take a session it does not find now for one that is gone.
        fixture.Service.RecordUnattendedWork("in-flight", "svc-org");
        for (var index = 0; index <= 5000; index++) fixture.Service.RecordUnattendedWork($"other-{index}", "svc-org");
        var until = DateTime.UtcNow + TestTime.Seconds(15);
        while (DateTime.UtcNow < until && !fixture.Host.Identities.IsOwnerWork!("in-flight")) await Task.Delay(200, token);
        Assert.IsFalse(fixture.Host.Identities.IsOwnerWork!("in-flight"), "the run in flight is still not the owner's work");
        Assert.AreEqual("max", fixture.Service.PersonOfSession(created.SessionId));

        // Deleted, the session is gone for sure: its record is forgotten.
        var session = await fixture.Host.RuntimeService.TryGetRecoverableSessionAsync(created.SessionId, token);
        Assert.IsTrue(await fixture.Host.RuntimeService.DeleteSessionAsync(session!, token));
        Assert.IsNull(fixture.Service.PersonOfSession(created.SessionId));
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
