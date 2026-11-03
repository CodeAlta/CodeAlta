using System.Collections.Concurrent;
using System.Net;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Fleet.Server;

namespace CodeAlta.Server.Tests;

/// <summary>
/// The server client and the connector of CodeAlta.Fleet against a real server of this process: setup, enrollment and
/// approval with entries signed by the client's own device key, then the control connection, the publication of facts
/// with their versions, its resumption after a dropped connection, and what is never published.
/// </summary>
[TestClass]
public sealed class FleetConnectorTests
{
    private static readonly DateTimeOffset Created = new(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task TheClientSetsUpEnrollsApprovesAndRevokesWithItsOwnKeys()
    {
        await using var server = await ServerFixture.StartAsync();
        using var root = new TempDirectory();
        using var desktopKey = DeviceIdentity.LoadOrCreate(Path.Combine(root.Path, "desktop"));
        using var nodeKey = DeviceIdentity.LoadOrCreate(Path.Combine(root.Path, "node"));
        using var desktop = new FleetServerClient(server.BaseAddress, desktopKey);
        using var node = new FleetServerClient(server.BaseAddress, nodeKey);

        Assert.IsFalse((await desktop.GetHealthAsync()).SetUp);
        var wrong = await Assert.ThrowsExactlyAsync<FleetServerException>(() => desktop.SetUpAsync("WRONG-CODE", "Alex", "Desktop"));
        Assert.AreEqual((HttpStatusCode.Unauthorized, CoordinatorErrorCodes.SetupCodeInvalid), (wrong.Status, wrong.Code));
        var setup = await desktop.SetUpAsync(server.SetupCode, "Alex", "Desktop", "Home");
        Assert.AreEqual(("Home", desktopKey.DeviceId, FleetDeviceKinds.Controller), (setup.Organization.Name, setup.Device.Id, setup.Device.Kind));

        var token = await desktop.CreateEnrollmentTokenAsync(TimeSpan.FromMinutes(30), "Mac mini");
        var pending = await node.EnrollAsync(token.Token, "Mac mini");
        Assert.AreEqual((nodeKey.DeviceId, "pending", FleetDeviceKinds.Node), (pending.Id, pending.Status, pending.Kind));
        var used = await Assert.ThrowsExactlyAsync<FleetServerException>(() => node.EnrollAsync(token.Token, "Mac mini"));
        Assert.AreEqual(CoordinatorErrorCodes.EnrollmentTokenUsed, used.Code);
        // A pending device cannot call the API: it is told to wait.
        var waiting = await Assert.ThrowsExactlyAsync<FleetServerException>(() => node.GetDeviceAsync());
        Assert.AreEqual((FleetErrorCodes.PendingApproval, FleetErrorHints.WaitForApproval), (waiting.Code, waiting.Hint));
        // A node cannot make tokens; only a controller device can.
        Assert.IsTrue((await desktop.ListDevicesAsync()).Any(device => device.Id == nodeKey.DeviceId && device.Status == "pending"));

        var approved = await desktop.ApproveAsync(nodeKey.DeviceId);
        Assert.AreEqual(("approved", setup.Owner.Id), (approved.Status, approved.OwnerId));
        Assert.AreEqual(2, (await desktop.GetDeviceListAsync()).Count);
        Assert.AreEqual("approved", (await node.GetDeviceAsync()).Status);
        var forbidden = await Assert.ThrowsExactlyAsync<FleetServerException>(() => node.CreateEnrollmentTokenAsync());
        Assert.AreEqual(FleetErrorCodes.Forbidden, forbidden.Code);

        var revoked = await desktop.RevokeAsync(nodeKey.DeviceId);
        Assert.AreEqual("revoked", revoked.Status);
        Assert.AreEqual(3, (await desktop.GetDeviceListAsync()).Count);
    }

    [TestMethod]
    public async Task ADesktopJoinsAsAControllerAndApprovesWithAVerifiedList()
    {
        await using var fleet = await FleetFixture.StartAsync(approve: false);
        using var laptopKey = DeviceIdentity.LoadOrCreate(Path.Combine(fleet.Root.Path, "laptop"));
        using var laptop = new FleetServerClient(fleet.Server.BaseAddress, laptopKey);
        using var node = new FleetServerClient(fleet.Server.BaseAddress, fleet.NodeKey);

        var pending = await laptop.EnrollAsync((await fleet.Desktop.CreateEnrollmentTokenAsync()).Token, "Laptop", FleetDeviceKinds.Controller);
        Assert.AreEqual((FleetDeviceKinds.Controller, "pending"), (pending.Kind, pending.Status));
        Assert.AreEqual("approved", (await fleet.Desktop.ApproveAsync(laptopKey.DeviceId)).Status);

        // The laptop approves the node with an entry it signs after the list it verified.
        var approved = await laptop.ApproveAsync(fleet.NodeKey.DeviceId);
        Assert.AreEqual(("approved", FleetDeviceKinds.Node), (approved.Status, approved.Kind));
        var state = await node.ReadVerifiedDeviceListAsync();
        Assert.AreEqual(3L, state.Sequence);
        Assert.IsTrue(state.IsController(laptopKey.DeviceId));
        Assert.AreEqual(fleet.NodeKey.DeviceId, state.Members[fleet.NodeKey.DeviceId].Key.DeviceId);
        Assert.AreEqual(laptop.PublicKey, (await node.GetTrustedMemberAsync(laptopKey.DeviceId))!.Key.Text);

        // A node signs no entry: its client refuses before it sends anything.
        using var otherKey = DeviceIdentity.LoadOrCreate(Path.Combine(fleet.Root.Path, "other"));
        using (var other = new FleetServerClient(fleet.Server.BaseAddress, otherKey))
            await other.EnrollAsync((await laptop.CreateEnrollmentTokenAsync()).Token, "Other");
        var refused = await Assert.ThrowsExactlyAsync<FleetDeviceListException>(() => node.ApproveAsync(otherKey.DeviceId));
        StringAssert.Contains(refused.Message, "not a controller device");
        Assert.AreEqual("pending", (await laptop.GetDeviceAsync(otherKey.DeviceId)).Status);
    }

    [TestMethod]
    public async Task TheClientRefusesToSignAfterADeviceListThatDoesNotVerify()
    {
        await using var fleet = await FleetFixture.StartAsync();
        using var strangerKey = DeviceIdentity.LoadOrCreate(Path.Combine(fleet.Root.Path, "stranger"));
        using var pcKey = DeviceIdentity.LoadOrCreate(Path.Combine(fleet.Root.Path, "pc"));
        using (var pc = new FleetServerClient(fleet.Server.BaseAddress, pcKey)) await pc.EnrollAsync((await fleet.Desktop.CreateEnrollmentTokenAsync()).Token, "PC");

        // A server that lies: it shows an entry the node signed, which adds a device of its choice after the real ones.
        using var lying = new ForgingHandler(fleet.NodeKey, strangerKey) { InnerHandler = new HttpClientHandler() };
        using var desktop = new FleetServerClient(fleet.Server.BaseAddress, fleet.DesktopKey, lying);
        var broken = await Assert.ThrowsExactlyAsync<FleetDeviceListException>(() => desktop.ApproveAsync(pcKey.DeviceId));
        Assert.AreEqual((3L, "its signer is not a controller device"), (broken.BrokenAt, broken.Reason));
        Assert.AreEqual("pending", (await fleet.Desktop.GetDeviceAsync(pcKey.DeviceId)).Status, "nothing was signed or sent");
        await Assert.ThrowsExactlyAsync<FleetDeviceListException>(() => desktop.GetTrustedMemberAsync(strangerKey.DeviceId));

        // The true list verifies, and the stranger is not in it.
        Assert.IsNull(await fleet.Desktop.GetTrustedMemberAsync(strangerKey.DeviceId));
    }

    [TestMethod]
    public async Task OnlyTheSessionsOfLinkedSpacesThatAreNotPrivateArePublished()
    {
        await using var fleet = await FleetFixture.StartAsync();
        var source = new FakeSource();
        source.Set(Session("s-work", "work"), Session("s-private", "work", isPrivate: true), Session("s-home", "home"), Session("s-default", null));
        await using var connector = fleet.NodeConnector(source, space => space == "work");
        connector.Start();

        await WaitForAsync(async () => (await fleet.Desktop.ListSessionsAsync()).Any(session => session.Id == "s-work"));
        var machines = await fleet.Desktop.ListMachinesAsync();
        Assert.AreEqual(("Mac mini", "macos", true), (machines.Single().Inventory.Name, machines.Single().Inventory.Os, machines.Single().Online));

        // A change is published; still nothing of the other sessions.
        source.Set(Session("s-work", "work", FleetSessionStates.Running), Session("s-private", "work", FleetSessionStates.Running, isPrivate: true),
            Session("s-home", "home", FleetSessionStates.Running), Session("s-default", null, FleetSessionStates.Running));
        await WaitForAsync(async () => (await fleet.Desktop.ListSessionsAsync()).Any(session => session.Id == "s-work" && session.State == FleetSessionStates.Running));
        var sessions = await fleet.Desktop.ListSessionsAsync();
        CollectionAssert.AreEqual(new[] { "s-work" }, sessions.Select(static session => session.Id).ToArray());
        Assert.AreEqual((fleet.NodeKey.DeviceId, 2L, "anthropic", "claude-opus-5-5"), (sessions[0].MachineId, sessions[0].Version, sessions[0].ProviderType, sessions[0].Model));
        Assert.IsNull(sessions[0].SealedMetadata, "titles are not published");
    }

    [TestMethod]
    public async Task ASessionMadePrivateOrWhoseSpaceIsUnlinkedHasItsFactsWithdrawn()
    {
        await using var fleet = await FleetFixture.StartAsync();
        var source = new FakeSource();
        var linked = System.Collections.Immutable.ImmutableHashSet.Create("work", "home");
        var withdrawn = new ConcurrentQueue<string>();
        await using var connector = fleet.NodeConnector(source, space => Volatile.Read(ref linked).Contains(space ?? ""));
        connector.Withdrawn += (_, result) => { foreach (var id in result.Removed) withdrawn.Enqueue(id); };
        connector.Start();
        source.Set(Session("s-a", "work"), Session("s-b", "home"), Session("s-c", "work"));
        await WaitForAsync(async () => (await fleet.Desktop.ListSessionsAsync()).Count == 3);
        Assert.AreEqual(new FleetSessionCounts(1, 2, 300, 40), (await fleet.Desktop.ListSessionsAsync()).Single(session => session.Id == "s-a").Counts);

        source.Set(Session("s-a", "work", isPrivate: true), Session("s-b", "home"), Session("s-c", "work"));
        await WaitForAsync(async () => !(await fleet.Desktop.ListSessionsAsync()).Any(session => session.Id == "s-a"));
        Volatile.Write(ref linked, ["work"]);
        source.Set(Session("s-a", "work", isPrivate: true), Session("s-b", "home"), Session("s-c", "work"));
        await WaitForAsync(async () => !(await fleet.Desktop.ListSessionsAsync()).Any(session => session.Id == "s-b"));
        CollectionAssert.AreEqual(new[] { "s-a", "s-b" }, withdrawn.ToArray());

        // A session gone from the machine (deleted) keeps its facts; one made public again is published again.
        source.Set(Session("s-a", "work"), Session("s-d", "work"));
        await WaitForAsync(async () => (await fleet.Desktop.ListSessionsAsync()) is var listed && listed.Any(session => session.Id == "s-a")
            && listed.Any(session => session.Id == "s-d"));
        Assert.IsTrue((await fleet.Desktop.ListSessionsAsync()).Any(session => session.Id == "s-c"));
        Assert.AreEqual(2, withdrawn.Count);
    }

    [TestMethod]
    public async Task TitlesArePublishedSealedWithTheKeyOfThePersonAndOpenOnItsDevices()
    {
        await using var fleet = await FleetFixture.StartAsync();
        var desktopKeys = new FleetMetadataKeyring(new FleetOrganizationDevices(fleet.Server.BaseAddress, fleet.DesktopKey));
        var nodeKeys = new FleetMetadataKeyring(new FleetOrganizationDevices(fleet.Server.BaseAddress, fleet.NodeKey));
        // A node never makes the key: it waits for the copy a controller device of its person gives it.
        Assert.IsNull(await nodeKeys.RefreshAsync("work", CancellationToken.None));
        Assert.AreEqual(0, (await fleet.Desktop.ListMetadataKeysAsync()).Count);
        var key = await desktopKeys.RefreshAsync("work", CancellationToken.None);
        Assert.IsNotNull(key);
        var made = (await fleet.Desktop.ListMetadataKeysAsync()).Single();
        CollectionAssert.AreEquivalent(new[] { fleet.DesktopKey.DeviceId, fleet.NodeKey.DeviceId }, made.WrappedTo.ToArray());
        Assert.AreEqual(key.Id, (await desktopKeys.RefreshAsync("work", CancellationToken.None))!.Id, "read again, the same key");

        var source = new FakeSource();
        source.Set(Session("s1", "work") with { Title = "Fix the parser" }, Session("s2", "work"));
        await using var connector = fleet.NodeConnector(source, static _ => true, keys: nodeKeys);
        connector.Start();
        await WaitForAsync(async () => (await fleet.Desktop.ListSessionsAsync()).Any(session => session.Id == "s1" && session.SealedMetadata is not null));
        var listed = (await fleet.Desktop.ListSessionsAsync()).ToDictionary(static session => session.Id);
        Assert.IsFalse(listed["s1"].SealedMetadata!.Contains("parser", StringComparison.Ordinal), "the server holds ciphertext only");
        Assert.IsNull(listed["s2"].SealedMetadata, "no title, nothing sealed");
        Assert.IsTrue(key.TryOpen("s1", listed["s1"].SealedMetadata, out var metadata));
        Assert.AreEqual("Fix the parser", metadata!.Title);

        // Polls seal again nothing that did not change: the version stays. A new title is a new version.
        var version = listed["s1"].Version;
        await Task.Delay(TimeSpan.FromMilliseconds(800));
        Assert.AreEqual(version, (await fleet.Desktop.ListSessionsAsync()).Single(session => session.Id == "s1").Version);
        source.Set(Session("s1", "work") with { Title = "Fix the lexer" }, Session("s2", "work"));
        await WaitForAsync(async () => (await fleet.Desktop.ListSessionsAsync()).Single(session => session.Id == "s1") is var session
            && key.TryOpen("s1", session.SealedMetadata, out var changed) && changed!.Title == "Fix the lexer");
        Assert.AreEqual(version + 1, (await fleet.Desktop.ListSessionsAsync()).Single(session => session.Id == "s1").Version);
    }

    [TestMethod]
    public async Task AReplacementChainWithTheVictimsKeyGetsNoKeyAndNoSignatureOnceTheListIsPinned()
    {
        await using var fleet = await FleetFixture.StartAsync();
        var pin = Path.Combine(fleet.Root.Path, "link", FleetOrganizationDevices.PinFileName);
        await new FleetOrganizationDevices(fleet.Server.BaseAddress, fleet.DesktopKey, pin).RefreshAsync(CancellationToken.None);
        using var attackerKey = DeviceIdentity.LoadOrCreate(Path.Combine(fleet.Root.Path, "attacker"));
        using var plantedKey = DeviceIdentity.LoadOrCreate(Path.Combine(fleet.Root.Path, "planted"));
        using var pcKey = DeviceIdentity.LoadOrCreate(Path.Combine(fleet.Root.Path, "pc"));
        using (var pc = new FleetServerClient(fleet.Server.BaseAddress, pcKey)) await pc.EnrollAsync((await fleet.Desktop.CreateEnrollmentTokenAsync()).Token, "PC");

        // A valid chain the server roots in a key of its own: the victim's real key is a controller in it, and a device
        // of the attacker is of the victim's person, so that the victim would wrap its metadata key to that device.
        var organization = (await fleet.Desktop.GetOrganizationAsync()).Id;
        var person = (await fleet.Desktop.GetDeviceAsync()).OwnerId;
        var first = SignedEntry(attackerKey, 1, null, attackerKey, FleetDeviceKinds.Controller, null, null);
        var victim = SignedEntry(attackerKey, 2, first.Hash, fleet.DesktopKey, FleetDeviceKinds.Controller, organization, person);
        var planted = SignedEntry(attackerKey, 3, victim.Hash, plantedKey, FleetDeviceKinds.Node, organization, person);
        Assert.IsTrue(FleetDeviceListVerifier.VerifyOrThrow([first.Entry, victim.Entry, planted.Entry], organization).IsController(fleet.DesktopKey.DeviceId));
        var replacing = new ReplacingHandler([first, victim, planted]) { InnerHandler = new HttpClientHandler() };

        // Through the anchor of the link, which pinned the genuine list: the key goes to nobody, no entry is signed.
        var devices = new FleetOrganizationDevices(fleet.Server.BaseAddress, fleet.DesktopKey, pin, replacing);
        await Assert.ThrowsExactlyAsync<FleetDeviceListException>(() => new FleetMetadataKeyring(devices).RefreshAsync("work", CancellationToken.None));
        using var client = new FleetServerClient(fleet.Server.BaseAddress, fleet.DesktopKey, replacing) { DeviceList = devices };
        await Assert.ThrowsExactlyAsync<FleetDeviceListException>(() => client.ApproveAsync(pcKey.DeviceId));
        Assert.AreEqual(0, replacing.Writes, "nothing was wrapped or signed after the replaced list");
        Assert.AreEqual(0, (await fleet.Desktop.ListMetadataKeysAsync()).Count);
    }

    [TestMethod]
    public async Task ANodeTakesNoKeyTheServerMadeOrRelabelledAndSealsWithTheOneItsPersonGave()
    {
        await using var fleet = await FleetFixture.StartAsync();
        var genuine = (await new FleetMetadataKeyring(new FleetOrganizationDevices(fleet.Server.BaseAddress, fleet.DesktopKey)).RefreshAsync("work", CancellationToken.None))!;
        using var serverKey = DeviceIdentity.LoadOrCreate(Path.Combine(fleet.Root.Path, "server-key"));
        var organization = (await fleet.Desktop.GetOrganizationAsync()).Id;
        var person = (await fleet.Desktop.GetDeviceAsync()).OwnerId!;
        using var node = new FleetServerClient(fleet.Server.BaseAddress, fleet.NodeKey);
        var list = await node.ReadVerifiedDeviceListAsync();
        var nodeKey = list.Members[fleet.NodeKey.DeviceId].Key;
        var copy = (await node.ListMetadataKeysAsync()).Single();

        // The server lists newer keys for the node: one of its own, signed by a key of its own or by nobody, and the
        // genuine copy relabelled for a newer epoch.
        var forged = FleetMetadataKey.Generate();
        var grant = new FleetKeyGrant(organization, person, 3, list.Sequence) { SpaceId = copy.Space };
        var signedByServer = FleetKeyWrap.Wrap(forged, nodeKey, grant with { Epoch = 4 }, serverKey);
        var unsigned = System.Text.Json.Nodes.JsonNode.Parse(FleetKeyWrap.Wrap(forged, nodeKey, grant, fleet.DesktopKey))!.AsObject();
        unsigned.Remove("sig");
        var injecting = new InjectingHandler(
            new MetadataKeyDto(forged.Id, 4, fleet.DesktopKey.DeviceId, Created, signedByServer, [fleet.NodeKey.DeviceId], copy.Space, forged.SealSpace(copy.Space!, "work")),
            new MetadataKeyDto("unsigned-copy", 3, fleet.DesktopKey.DeviceId, Created, unsigned.ToJsonString(), [fleet.NodeKey.DeviceId], copy.Space, copy.SealedDescriptor),
            copy with { Id = copy.Id, Epoch = 2 }) { InnerHandler = new HttpClientHandler() };
        var nodeKeys = new FleetMetadataKeyring(new FleetOrganizationDevices(fleet.Server.BaseAddress, fleet.NodeKey, null, injecting));
        Assert.AreEqual(genuine.Id, (await nodeKeys.RefreshAsync("work", CancellationToken.None))?.Id, "the node keeps the key a controller of its person gave it");

        var source = new FakeSource();
        source.Set(Session("s1", "work") with { Title = "Fix the parser" });
        await using var connector = fleet.NodeConnector(source, static _ => true, keys: nodeKeys);
        connector.Start();
        await WaitForAsync(async () => (await fleet.Desktop.ListSessionsAsync()).Any(session => session.Id == "s1" && session.SealedMetadata is not null));
        var sealedText = (await fleet.Desktop.ListSessionsAsync()).Single(session => session.Id == "s1").SealedMetadata;
        Assert.IsTrue(genuine.TryOpen("s1", sealedText, out _));
        Assert.IsFalse(forged.TryOpen("s1", sealedText, out _), "the server's key opens nothing the node publishes");
    }

    [TestMethod]
    public async Task ARemovalGivesTheOtherDevicesANewEpochThatTheRemovedDeviceCannotOpen()
    {
        await using var fleet = await FleetFixture.StartAsync();
        using var pcKey = DeviceIdentity.LoadOrCreate(Path.Combine(fleet.Root.Path, "pc"));
        using (var pc = new FleetServerClient(fleet.Server.BaseAddress, pcKey)) await pc.EnrollAsync((await fleet.Desktop.CreateEnrollmentTokenAsync()).Token, "PC");
        await fleet.Desktop.ApproveAsync(pcKey.DeviceId);
        var desktopKeys = new FleetMetadataKeyring(new FleetOrganizationDevices(fleet.Server.BaseAddress, fleet.DesktopKey));
        var nodeKeys = new FleetMetadataKeyring(new FleetOrganizationDevices(fleet.Server.BaseAddress, fleet.NodeKey));
        var first = (await desktopKeys.RefreshAsync("work", CancellationToken.None))!;
        Assert.AreEqual(first.Id, (await new FleetMetadataKeyring(new FleetOrganizationDevices(fleet.Server.BaseAddress, pcKey)).RefreshAsync("work", CancellationToken.None))?.Id,
            "the PC holds the first key");
        Assert.AreEqual(first.Id, (await nodeKeys.RefreshAsync("work", CancellationToken.None))?.Id);

        await fleet.Desktop.RevokeAsync(pcKey.DeviceId);
        // Until the new epoch reaches it, the node seals nothing with the key the removed device holds.
        Assert.IsNull(await nodeKeys.RefreshAsync("work", CancellationToken.None));
        var second = (await desktopKeys.RefreshAsync("work", CancellationToken.None))!;
        Assert.AreEqual((2, false), (second.Grant!.Epoch, second.Id == first.Id));
        CollectionAssert.AreEquivalent(new[] { fleet.DesktopKey.DeviceId, fleet.NodeKey.DeviceId },
            (await fleet.Desktop.ListMetadataKeysAsync()).Single(key => key.Id == second.Id).WrappedTo.ToArray(), "the new epoch goes to the devices that remain");
        Assert.AreEqual(second.Id, (await nodeKeys.RefreshAsync("work", CancellationToken.None))?.Id);
        Assert.AreEqual(second.Id, (await desktopKeys.RefreshAsync("work", CancellationToken.None))?.Id, "read again, the same epoch");

        var source = new FakeSource();
        source.Set(Session("s1", "work") with { Title = "After the removal" });
        await using var connector = fleet.NodeConnector(source, static _ => true, keys: nodeKeys);
        connector.Start();
        await WaitForAsync(async () => (await fleet.Desktop.ListSessionsAsync()).Any(session => session.Id == "s1" && session.SealedMetadata is not null));
        var sealedText = (await fleet.Desktop.ListSessionsAsync()).Single(session => session.Id == "s1").SealedMetadata;
        Assert.IsTrue(second.TryOpen("s1", sealedText, out var opened));
        Assert.AreEqual("After the removal", opened!.Title);
        Assert.IsFalse(first.TryOpen("s1", sealedText, out _), "the key the removed device holds opens nothing published after its removal");
    }

    [TestMethod]
    public async Task EachSpaceSealsWithAKeyOfItsOwn_WhichTheDevicesOfThePersonFindByTheirSpace()
    {
        await using var fleet = await FleetFixture.StartAsync();
        var desktopKeys = new FleetMetadataKeyring(new FleetOrganizationDevices(fleet.Server.BaseAddress, fleet.DesktopKey));
        var nodeKeys = new FleetMetadataKeyring(new FleetOrganizationDevices(fleet.Server.BaseAddress, fleet.NodeKey));
        var made = await desktopKeys.RefreshAsync(["work", "home"], CancellationToken.None);
        var (work, home) = (made["work"], made["home"]);
        Assert.AreNotEqual(work.Id, home.Id);
        Assert.AreNotEqual(work.Grant!.SpaceId, home.Grant!.SpaceId);
        var rows = await fleet.Desktop.ListMetadataKeysAsync();
        Assert.AreEqual(2, rows.Count);
        Assert.IsTrue(rows.All(static row => row.Space is { Length: > 0 } && row.SealedDescriptor is { } descriptor && !descriptor.Contains("work", StringComparison.Ordinal)
            && !descriptor.Contains("home", StringComparison.Ordinal)), "the server knows a space by an opaque id only");

        // The node finds the key of its own space by what the descriptor says, and never gets one made for it by itself.
        Assert.AreEqual(work.Id, (await nodeKeys.RefreshAsync("work", CancellationToken.None))?.Id);
        Assert.IsNull(await nodeKeys.RefreshAsync("lab", CancellationToken.None), "a node makes no key, not even for a space no controller linked");
        var source = new FakeSource();
        source.Set(Session("s1", "work") with { Title = "Fix the parser" }, Session("s2", "home") with { Title = "Plan the trip" });
        await using var connector = fleet.NodeConnector(source, static _ => true, keys: nodeKeys);
        connector.Start();
        await WaitForAsync(async () => (await fleet.Desktop.ListSessionsAsync()).Count(static session => session.SealedMetadata is not null) == 2);
        var listed = (await fleet.Desktop.ListSessionsAsync()).ToDictionary(static session => session.Id);
        Assert.IsTrue(work.TryOpen("s1", listed["s1"].SealedMetadata, out var opened));
        Assert.AreEqual("Fix the parser", opened!.Title);
        Assert.IsFalse(home.TryOpen("s1", listed["s1"].SealedMetadata, out _), "the key of another space opens nothing of this one");
        Assert.IsTrue(home.TryOpen("s2", listed["s2"].SealedMetadata, out _));
        Assert.IsTrue(desktopKeys.TryOpen("s2", listed["s2"].SealedMetadata, out var byRing) && byRing!.Title == "Plan the trip", "the keyring opens by the key a text names");
        Assert.AreNotEqual(FleetProjectKeys.Of(work, "repo:x"), FleetProjectKeys.Of(home, "repo:x"), "a project is matched within its space");

        // Unlinked, the space has no key that seals; its key still opens what it sealed.
        var linked = await desktopKeys.RefreshAsync(["work"], CancellationToken.None);
        Assert.IsFalse(linked.ContainsKey("home"));
        Assert.IsNull(desktopKeys.CurrentFor("home"));
        Assert.IsTrue(desktopKeys.TryOpen("s2", listed["s2"].SealedMetadata, out _));
    }

    [TestMethod]
    public async Task TwoSpacesMadeForOneSpaceOfThePersonConvergeOnTheFirstMade()
    {
        await using var fleet = await FleetFixture.StartAsync();
        using var pcKey = DeviceIdentity.LoadOrCreate(Path.Combine(fleet.Root.Path, "pc"));
        using (var pc = new FleetServerClient(fleet.Server.BaseAddress, pcKey)) await pc.EnrollAsync((await fleet.Desktop.CreateEnrollmentTokenAsync()).Token, "PC", FleetDeviceKinds.Controller);
        await fleet.Desktop.ApproveAsync(pcKey.DeviceId);
        var desktopKeys = new FleetMetadataKeyring(new FleetOrganizationDevices(fleet.Server.BaseAddress, fleet.DesktopKey));
        var first = (await desktopKeys.RefreshAsync("work", CancellationToken.None))!;

        // The PC made its own "work" space at the same time, before it read the desktop's.
        var list = await fleet.Desktop.ReadVerifiedDeviceListAsync();
        var person = (await fleet.Desktop.GetDeviceAsync()).OwnerId!;
        var organization = (await fleet.Desktop.GetOrganizationAsync()).Id;
        var late = FleetMetadataKey.Generate(new FleetKeyGrant(organization, person, 1, list.Sequence) { SpaceId = "late-space" });
        using (var pc = new FleetServerClient(fleet.Server.BaseAddress, pcKey))
        {
            await pc.CreateSpaceKeyAsync(late.Id, 1, "late-space", late.SealSpace("late-space", "work"),
                [.. list.Members.Values.Select(member => new MetadataKeyWrapDto(member.DeviceId, FleetKeyWrap.Wrap(late, member.Key, late.Grant!, pcKey)))]);
        }

        // The first made wins; the clock of the test server stands still, so the opaque id orders them, the same way on every device.
        var rows = await fleet.Desktop.ListMetadataKeysAsync();
        var winner = rows.OrderBy(static row => row.CreatedAt).ThenBy(static row => row.Space, StringComparer.Ordinal).First().Id;
        var pcKeys = new FleetMetadataKeyring(new FleetOrganizationDevices(fleet.Server.BaseAddress, pcKey));
        Assert.AreEqual(winner, (await pcKeys.RefreshAsync("work", CancellationToken.None))?.Id);
        Assert.AreEqual(winner, (await desktopKeys.RefreshAsync("work", CancellationToken.None))?.Id, "the desktop moves to it when it was not its own");
        Assert.AreEqual(winner, (await new FleetMetadataKeyring(new FleetOrganizationDevices(fleet.Server.BaseAddress, fleet.NodeKey)).RefreshAsync("work", CancellationToken.None))?.Id);
        Assert.IsTrue(pcKeys.Held.Any(key => key.Id == late.Id) && pcKeys.Held.Any(key => key.Id == first.Id), "what either one sealed still opens");
    }

    [TestMethod]
    public async Task TheKeyOfThePersonMadeBeforeSpacesStillOpensAndReachesNewDevicesButSealsNothing()
    {
        await using var fleet = await FleetFixture.StartAsync();
        // A key of the person, as a desktop of an earlier version made it: wrapped to that desktop only.
        var list = await fleet.Desktop.ReadVerifiedDeviceListAsync();
        var person = (await fleet.Desktop.GetDeviceAsync()).OwnerId!;
        var organization = (await fleet.Desktop.GetOrganizationAsync()).Id;
        var legacy = FleetMetadataKey.Generate(new FleetKeyGrant(organization, person, 1, list.Sequence));
        await fleet.Desktop.CreateMetadataKeyAsync(legacy.Id, 1,
            [new MetadataKeyWrapDto(fleet.DesktopKey.DeviceId, FleetKeyWrap.Wrap(legacy, list.Members[fleet.DesktopKey.DeviceId].Key, legacy.Grant!, fleet.DesktopKey))]);
        var sealedBefore = legacy.Seal("s0", new FleetSessionMetadata("Before spaces"));

        var desktopKeys = new FleetMetadataKeyring(new FleetOrganizationDevices(fleet.Server.BaseAddress, fleet.DesktopKey));
        var work = (await desktopKeys.RefreshAsync("work", CancellationToken.None))!;
        Assert.AreNotEqual(legacy.Id, work.Id, "a space gets a key of its own, never the person's");
        Assert.IsTrue(desktopKeys.TryOpen("s0", sealedBefore, out var opened) && opened!.Title == "Before spaces");
        CollectionAssert.AreEquivalent(new[] { fleet.DesktopKey.DeviceId, fleet.NodeKey.DeviceId },
            (await fleet.Desktop.ListMetadataKeysAsync()).Single(key => key.Id == legacy.Id).WrappedTo.ToArray(), "the devices of the person still get it, to open what it sealed");
        var nodeKeys = new FleetMetadataKeyring(new FleetOrganizationDevices(fleet.Server.BaseAddress, fleet.NodeKey));
        Assert.AreEqual(work.Id, (await nodeKeys.RefreshAsync("work", CancellationToken.None))?.Id);
        Assert.IsTrue(nodeKeys.TryOpen("s0", sealedBefore, out _));
        Assert.AreEqual(1, (await fleet.Desktop.ListMetadataKeysAsync()).Count(static key => key.Space is null), "no new key of the person");
    }

    [TestMethod]
    public async Task PublicationResumesFromTheCursorsAfterADroppedConnection()
    {
        await using var fleet = await FleetFixture.StartAsync();
        var source = new FakeSource();
        source.Set(Session("s1", "work"));
        var transports = new ConcurrentQueue<FleetWebSocketTransport>();
        var results = new ConcurrentQueue<FleetFactsPublishResult>();
        var outbox = Path.Combine(fleet.Root.Path, "outbox.json");
        await using (var connector = fleet.NodeConnector(source, static _ => true, outbox, async (address, token) =>
               {
                   var transport = await FleetWebSocketTransport.ConnectAsync(address, null, token);
                   transports.Enqueue(transport);
                   return transport;
               }))
        {
            connector.Published += (_, result) => results.Enqueue(result);
            connector.Start();
            await WaitForAsync(async () => (await fleet.Desktop.ListSessionsAsync()).Any(session => session.Id == "s1"));

            // The connection drops under the node; the session changes meanwhile.
            transports.Last().Abort();
            await WaitForAsync(() => Task.FromResult(connector.State != FleetConnectorState.Online || transports.Count > 1));
            source.Set(Session("s1", "work", FleetSessionStates.Running), Session("s2", "work"));
            await WaitForAsync(async () => (await fleet.Desktop.ListSessionsAsync()) is var listed
                && listed.Any(session => session.Id == "s1" && session.Version == 2) && listed.Any(session => session.Id == "s2"));
            Assert.IsTrue(transports.Count >= 2, "it reconnected");
            // Each batch went on from the cursor the server gave back, never repeating a sequence.
            var acks = results.Select(static result => result.Ack).ToArray();
            CollectionAssert.AreEqual(acks.Order().Distinct().ToArray(), acks);
            Assert.AreEqual(connector.Cursors.Facts, acks[^1]);
        }

        // Started again on its outbox, it publishes nothing the server already holds.
        results.Clear();
        await using (var again = fleet.NodeConnector(source, static _ => true, outbox))
        {
            again.Published += (_, result) => results.Enqueue(result);
            again.Start();
            await WaitForAsync(() => Task.FromResult(again.State == FleetConnectorState.Online));
            source.Set(Session("s1", "work", FleetSessionStates.Running), Session("s2", "work"), Session("s3", "work"));
            again.Nudge();
            await WaitForAsync(async () => (await fleet.Desktop.ListSessionsAsync()).Any(session => session.Id == "s3"));
            Assert.AreEqual(0, results.Sum(static result => result.Unchanged), "the versions the server held were not sent again");
            Assert.AreEqual(1L, (await fleet.Desktop.ListSessionsAsync()).Single(session => session.Id == "s3").Version);
        }
    }

    [TestMethod]
    public async Task APendingNodeWaitsForItsApprovalThenConnectsAndARevokedOneStops()
    {
        await using var fleet = await FleetFixture.StartAsync(approve: false);
        var source = new FakeSource();
        source.Set(Session("s1", "work"));
        await using var connector = fleet.NodeConnector(source, static _ => true);
        connector.Start();
        await WaitForAsync(() => Task.FromResult(connector.State == FleetConnectorState.PendingApproval));
        Assert.AreEqual(FleetErrorCodes.PendingApproval, connector.LastError?.Code);

        await fleet.Desktop.ApproveAsync(fleet.NodeKey.DeviceId);
        await WaitForAsync(() => Task.FromResult(connector.State == FleetConnectorState.Online));
        await WaitForAsync(async () => (await fleet.Desktop.ListSessionsAsync()).Count == 1);

        await fleet.Desktop.RevokeAsync(fleet.NodeKey.DeviceId);
        await WaitForAsync(() => Task.FromResult(connector.State == FleetConnectorState.Revoked));
        Assert.AreEqual(FleetErrorCodes.Revoked, connector.LastError?.Code);
    }

    [TestMethod]
    public async Task ADesktopFollowsTheMachinesSessionsAndPresenceOfItsOrganization()
    {
        await using var fleet = await FleetFixture.StartAsync();
        await using var desktop = new FleetConnector(new FleetConnectorOptions
        {
            ServerAddress = fleet.Server.BaseAddress, Identity = fleet.DesktopKey, OutboxPath = Path.Combine(fleet.Root.Path, "desktop-outbox.json"),
            Client = new FleetClientInfo("desktop", "4.0.0", "windows"), WatchDirectory = true, MinimumBackoff = TimeSpan.FromMilliseconds(100),
        });
        var changed = 0;
        desktop.DirectoryChanged += _ => Interlocked.Increment(ref changed);
        desktop.Start();
        await WaitForAsync(() => Task.FromResult(desktop.State == FleetConnectorState.Online));

        var source = new FakeSource();
        source.Set(Session("s1", "work", FleetSessionStates.RequiresAction));
        await using (var node = fleet.NodeConnector(source, static _ => true))
        {
            node.Start();
            await WaitForAsync(() => Task.FromResult(desktop.Directory.Sessions.ContainsKey("s1") && desktop.Directory.Machines.ContainsKey(fleet.NodeKey.DeviceId)
                && desktop.Directory.IsOnline(fleet.NodeKey.DeviceId)));
            var session = desktop.Directory.Sessions["s1"];
            Assert.AreEqual((fleet.NodeKey.DeviceId, FleetSessionStates.RequiresAction), (session.MachineId, session.Fact.State));
            Assert.AreEqual("Mac mini", desktop.Directory.Machines[fleet.NodeKey.DeviceId].Inventory.Name);
        }

        await WaitForAsync(() => Task.FromResult(!desktop.Directory.IsOnline(fleet.NodeKey.DeviceId)));
        Assert.IsTrue(Volatile.Read(ref changed) > 0);
    }

    private static FleetLocalSession Session(string id, string? space, string state = FleetSessionStates.Idle, bool isPrivate = false)
        => new(id, space, isPrivate, 0, state, Created, Created.AddMinutes(state == FleetSessionStates.Idle ? 1 : 2), "anthropic", "claude-opus-5-5",
            new FleetSessionCounts(1, 2, 300, 40));

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + Waits.Hang;
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("The condition did not hold in time.");
            await Task.Delay(50);
        }
    }

    /// <summary>A server set up by a desktop key, with a node key enrolled (and approved unless asked otherwise).</summary>
    private sealed class FleetFixture : IAsyncDisposable
    {
        private FleetFixture(ServerFixture server, TempDirectory root, DeviceIdentity desktopKey, DeviceIdentity nodeKey, FleetServerClient desktop)
        {
            Server = server;
            Root = root;
            DesktopKey = desktopKey;
            NodeKey = nodeKey;
            Desktop = desktop;
        }

        public ServerFixture Server { get; }

        public TempDirectory Root { get; }

        public DeviceIdentity DesktopKey { get; }

        public DeviceIdentity NodeKey { get; }

        public FleetServerClient Desktop { get; }

        public static async Task<FleetFixture> StartAsync(bool approve = true)
        {
            var server = await ServerFixture.StartAsync();
            var root = new TempDirectory();
            var desktopKey = DeviceIdentity.LoadOrCreate(Path.Combine(root.Path, "desktop"));
            var nodeKey = DeviceIdentity.LoadOrCreate(Path.Combine(root.Path, "node"));
            var desktop = new FleetServerClient(server.BaseAddress, desktopKey);
            await desktop.SetUpAsync(server.SetupCode, "Alex", "Desktop");
            using var node = new FleetServerClient(server.BaseAddress, nodeKey);
            await node.EnrollAsync((await desktop.CreateEnrollmentTokenAsync()).Token, "Mac mini");
            if (approve) await desktop.ApproveAsync(nodeKey.DeviceId);
            return new FleetFixture(server, root, desktopKey, nodeKey, desktop);
        }

        public FleetConnector NodeConnector(IFleetPublicationSource source, Func<string?, bool> publishes, string? outbox = null,
            Func<Uri, CancellationToken, Task<IFleetTransport>>? connect = null, FleetMetadataKeyring? keys = null)
            => new(new FleetConnectorOptions
            {
                ServerAddress = Server.BaseAddress, Identity = NodeKey, OutboxPath = outbox ?? Path.Combine(Root.Path, $"outbox-{Guid.NewGuid():N}.json"),
                Client = new FleetClientInfo("node", "4.0.0", "macos"), Source = source, PublishesSpace = publishes,
                MinimumBackoff = TimeSpan.FromMilliseconds(100), MaximumBackoff = TimeSpan.FromMilliseconds(500), PendingApprovalRetry = TimeSpan.FromMilliseconds(200),
                PublishDelay = TimeSpan.FromMilliseconds(20), PollInterval = TimeSpan.FromMilliseconds(200),
                ConnectTransport = connect is null ? null : async (address, token) => await connect(address, token), MetadataKeys = keys,
            });

        public async ValueTask DisposeAsync()
        {
            Desktop.Dispose();
            DesktopKey.Dispose();
            NodeKey.Dispose();
            await Server.DisposeAsync();
            Root.Dispose();
        }
    }

    /// <summary>A server that adds to the device list it answers an entry an execution node signed, with the right sequence and hash.</summary>
    private sealed class ForgingHandler(DeviceIdentity signer, DeviceIdentity added) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (request.Method != HttpMethod.Get || request.RequestUri?.AbsolutePath.EndsWith("/api/v1/device-list", StringComparison.Ordinal) != true) return response;
            var list = System.Text.Json.JsonSerializer.Deserialize(await response.Content.ReadAsByteArrayAsync(cancellationToken),
                ServerApiJsonContext.Default.ListResponseDeviceListEntryDto)!;
            var last = list.Items[^1];
            FleetDeviceListEntries.TryParsePayload(last.Entry.Payload, out var lastPayload);
            var payload = FleetDeviceListEntries.SerializePayload(new FleetDeviceListEntryPayload(1, lastPayload!.OrganizationId, last.Sequence + 1, last.Hash,
                FleetDeviceListOperations.Add, added.DeviceId, Convert.ToBase64String(added.ExportSubjectPublicKeyInfo()), FleetDeviceKinds.Controller, lastPayload.OwnerId,
                signer.DeviceId, Created));
            var entry = new FleetSignedDeviceListEntry(payload, System.Buffers.Text.Base64Url.EncodeToString(signer.SignData(FleetDeviceListEntries.GetSignedBytes(payload))));
            var forged = new ListResponse<DeviceListEntryDto>([.. list.Items, new DeviceListEntryDto(last.Sequence + 1, FleetDeviceListEntries.Hash(entry), entry, Created)]);
            response.Content = new ByteArrayContent(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(forged, ServerApiJsonContext.Default.ListResponseDeviceListEntryDto));
            return response;
        }
    }

    private static DeviceListEntryDto SignedEntry(DeviceIdentity signer, long sequence, string? previous, DeviceIdentity added, string kind, string? organization, string? owner)
    {
        var payload = FleetDeviceListEntries.SerializePayload(new FleetDeviceListEntryPayload(1, organization, sequence, previous, FleetDeviceListOperations.Add, added.DeviceId,
            Convert.ToBase64String(added.ExportSubjectPublicKeyInfo()), kind, owner, signer.DeviceId, Created));
        var entry = new FleetSignedDeviceListEntry(payload, System.Buffers.Text.Base64Url.EncodeToString(signer.SignData(FleetDeviceListEntries.GetSignedBytes(payload))));
        return new DeviceListEntryDto(sequence, FleetDeviceListEntries.Hash(entry), entry, Created);
    }

    /// <summary>A server that answers another device list, and counts the writes (metadata keys, device-list entries) it is sent.</summary>
    private sealed class ReplacingHandler(IReadOnlyList<DeviceListEntryDto> chain) : DelegatingHandler
    {
        private int _writes;

        public int Writes => Volatile.Read(ref _writes);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && (path.Contains("/metadata-keys", StringComparison.Ordinal) || path.EndsWith("/approve", StringComparison.Ordinal)
                || path.EndsWith("/revoke", StringComparison.Ordinal)))
            {
                Interlocked.Increment(ref _writes);
            }

            if (request.Method != HttpMethod.Get || !path.EndsWith("/api/v1/device-list", StringComparison.Ordinal)) return await base.SendAsync(request, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new ListResponse<DeviceListEntryDto>(chain),
                    ServerApiJsonContext.Default.ListResponseDeviceListEntryDto)),
            };
        }
    }

    /// <summary>A server that adds keys of its own to the metadata keys it lists.</summary>
    private sealed class InjectingHandler(params MetadataKeyDto[] injected) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (request.Method != HttpMethod.Get || request.RequestUri?.AbsolutePath.EndsWith("/api/v1/metadata-keys", StringComparison.Ordinal) != true) return response;
            var keys = System.Text.Json.JsonSerializer.Deserialize(await response.Content.ReadAsByteArrayAsync(cancellationToken), ServerApiJsonContext.Default.ListResponseMetadataKeyDto)!;
            response.Content = new ByteArrayContent(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new ListResponse<MetadataKeyDto>([.. keys.Items, .. injected]),
                ServerApiJsonContext.Default.ListResponseMetadataKeyDto));
            return response;
        }
    }

    /// <summary>Sessions a test sets; each change says so.</summary>
    private sealed class FakeSource : IFleetPublicationSource
    {
        private IReadOnlyList<FleetLocalSession> _sessions = [];

        public event Action? Changed;

        public void Set(params FleetLocalSession[] sessions)
        {
            Volatile.Write(ref _sessions, sessions);
            Changed?.Invoke();
        }

        public ValueTask<FleetMachineInventory> ReadInventoryAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(new FleetMachineInventory("Mac mini", "macos", "arm64", "4.0.0", ["moves"], null, ["anthropic"]));

        public ValueTask<IReadOnlyList<FleetLocalSession>> ReadSessionsAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Volatile.Read(ref _sessions));
    }
}
