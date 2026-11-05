using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Fleet.Server;
using CodeAlta.Server.Storage;

namespace CodeAlta.Server.Tests;

/// <summary>
/// The recovery key of a person and the approval of a new device: every device lost, then a new one signed in by the
/// recovery key with the keys of the person; the anchor that keeps a server from answering a list of its own; and an
/// approval bound to the words the person compared.
/// </summary>
public sealed partial class FleetConnectorTests
{
    [TestMethod]
    public async Task EveryDeviceLostThenTheRecoveryKeySignsInANewDeviceThatOpensWhatWasSealed()
    {
        await using var fleet = await FleetFixture.StartAsync();
        string words;
        using (var made = FleetRecoveryKey.Generate())
        {
            words = made.Words;
            var added = await fleet.Desktop.AddRecoveryKeyAsync(made);
            Assert.AreEqual((FleetDeviceKinds.Recovery, "approved"), (added.Kind, added.Status));
        }

        Assert.AreEqual(32, words.Split(' ').Length);
        // The desktop gives the recovery key its copy of the key of the space, as to any device of the person.
        var desktopKeys = new FleetMetadataKeyring(new FleetOrganizationDevices(fleet.Server.BaseAddress, fleet.DesktopKey));
        var work = (await desktopKeys.RefreshAsync("work", CancellationToken.None))!;
        using (var check = FleetRecoveryKey.TryParse(words, out var parsed) ? parsed : null)
        using (var recoveryIdentity = check!.CreateIdentity())
        {
            CollectionAssert.Contains((await fleet.Desktop.ListMetadataKeysAsync()).Single().WrappedTo.ToArray(), recoveryIdentity.DeviceId);
        }

        var nodeKeys = new FleetMetadataKeyring(new FleetOrganizationDevices(fleet.Server.BaseAddress, fleet.NodeKey));
        Assert.AreEqual(work.Id, (await nodeKeys.RefreshAsync("work", CancellationToken.None))?.Id);
        var source = new FakeSource();
        source.Set(Session("s1", "work") with { Title = "Fix the parser" });
        string sealedText;
        await using (var connector = fleet.NodeConnector(source, static _ => true, keys: nodeKeys))
        {
            connector.Start();
            await WaitForAsync(async () => (await fleet.Desktop.ListSessionsAsync()).Any(session => session.Id == "s1" && session.SealedMetadata is not null));
            sealedText = (await fleet.Desktop.ListSessionsAsync()).Single(session => session.Id == "s1").SealedMetadata!;
        }

        // The desktop is lost. A new laptop, with the words the person kept (typed in capitals, one per line).
        using var laptopKey = DeviceIdentity.LoadOrCreate(Path.Combine(fleet.Root.Path, "new-laptop"));
        var pin = Path.Combine(fleet.Root.Path, "new-link", FleetOrganizationDevices.PinFileName);
        Assert.IsTrue(FleetRecoveryKey.TryParse(words.ToUpperInvariant().Replace(' ', '\n'), out var typed));
        FleetRecoveryResult result;
        using (typed) result = await FleetRecovery.RecoverAsync(fleet.Server.BaseAddress, typed, laptopKey, "New laptop", pin, null, CancellationToken.None);
        Assert.AreEqual((1, (await fleet.Desktop.GetDeviceAsync()).OwnerId), (result.KeysGiven, result.PersonId));

        // The laptop is a controller device of the person in the signed list, and opens what the node sealed before.
        var laptopDevices = new FleetOrganizationDevices(fleet.Server.BaseAddress, laptopKey, pin);
        var list = await laptopDevices.RefreshAsync(CancellationToken.None);
        Assert.IsTrue(list.IsController(laptopKey.DeviceId));
        var laptopKeys = new FleetMetadataKeyring(laptopDevices);
        Assert.AreEqual(work.Id, (await laptopKeys.RefreshAsync("work", CancellationToken.None))?.Id);
        Assert.IsTrue(laptopKeys.TryOpen("s1", sealedText, out var opened));
        Assert.AreEqual("Fix the parser", opened!.Title);

        // It removes the lost desktop: the next epoch goes to the devices that remain, the recovery key included, never to the desktop.
        using (var laptop = new FleetServerClient(fleet.Server.BaseAddress, laptopKey) { DeviceList = laptopDevices }) await laptop.RevokeAsync(fleet.DesktopKey.DeviceId);
        var second = (await laptopKeys.RefreshAsync("work", CancellationToken.None))!;
        Assert.AreEqual(2, second.Grant!.Epoch);
        using var laptopClient = new FleetServerClient(fleet.Server.BaseAddress, laptopKey);
        var wrappedTo = (await laptopClient.ListMetadataKeysAsync()).Single(key => key.Id == second.Id).WrappedTo;
        Assert.AreEqual(3, wrappedTo.Count, "the laptop, the node and the recovery key");
        Assert.IsFalse(wrappedTo.Contains(fleet.DesktopKey.DeviceId));
        Assert.AreEqual(second.Id, (await nodeKeys.RefreshAsync("work", CancellationToken.None))?.Id);

        // Words that are not the recovery key sign nothing in.
        using var otherKey = DeviceIdentity.LoadOrCreate(Path.Combine(fleet.Root.Path, "thief"));
        using var guess = FleetRecoveryKey.Generate();
        await Assert.ThrowsExactlyAsync<FleetRecoveryException>(() => FleetRecovery.RecoverAsync(fleet.Server.BaseAddress, guess, otherKey, "Thief",
            Path.Combine(fleet.Root.Path, "thief-link", FleetOrganizationDevices.PinFileName), null, CancellationToken.None));
        Assert.IsFalse((await laptopClient.ListDevicesAsync()).Any(device => device.Id == otherKey.DeviceId));
    }

    [TestMethod]
    public async Task ARecoveryPinsTheListTheRecoveryKeySealedSoAServerCannotAnswerOneOfItsOwn()
    {
        await using var fleet = await FleetFixture.StartAsync();
        using var key = FleetRecoveryKey.Generate();
        await fleet.Desktop.AddRecoveryKeyAsync(key);
        using var recoveryIdentity = key.CreateIdentity();
        using var attackerKey = DeviceIdentity.LoadOrCreate(Path.Combine(fleet.Root.Path, "attacker"));
        using var laptopKey = DeviceIdentity.LoadOrCreate(Path.Combine(fleet.Root.Path, "laptop"));

        // A valid list the server roots in a key of its own, where the person's recovery key is a member.
        var organization = (await fleet.Desktop.GetOrganizationAsync()).Id;
        var person = (await fleet.Desktop.GetDeviceAsync()).OwnerId;
        var first = SignedEntry(attackerKey, 1, null, attackerKey, FleetDeviceKinds.Controller, null, null);
        var planted = SignedEntry(attackerKey, 2, first.Hash, recoveryIdentity, FleetDeviceKinds.Recovery, organization, person);
        Assert.IsTrue(FleetDeviceListVerifier.Verify([first.Entry, planted.Entry], organization).IsValid);
        var replacing = new ReplacingHandler([first, planted]) { InnerHandler = new HttpClientHandler() };

        await Assert.ThrowsExactlyAsync<FleetDeviceListException>(() => FleetRecovery.RecoverAsync(fleet.Server.BaseAddress, key, laptopKey, "Laptop",
            Path.Combine(fleet.Root.Path, "link", FleetOrganizationDevices.PinFileName), replacing, CancellationToken.None));
        Assert.IsFalse((await fleet.Desktop.ListDevicesAsync()).Any(device => device.Id == laptopKey.DeviceId), "the recovery key signed nothing");

        // A new recovery key replaces the old one, whose words stop working.
        using var newer = FleetRecoveryKey.Generate();
        await fleet.Desktop.AddRecoveryKeyAsync(newer);
        var list = await fleet.Desktop.ReadVerifiedDeviceListAsync();
        Assert.AreEqual(1, list.Members.Values.Count(static member => member.Kind == FleetDeviceKinds.Recovery));
        Assert.IsTrue(list.Removed.Contains(recoveryIdentity.DeviceId));
        await Assert.ThrowsExactlyAsync<FleetRecoveryException>(() => FleetRecovery.RecoverAsync(fleet.Server.BaseAddress, key, laptopKey, "Laptop",
            Path.Combine(fleet.Root.Path, "link-2", FleetOrganizationDevices.PinFileName), null, CancellationToken.None));
    }

    [TestMethod]
    public async Task ANodeThatLeavesTheServerTakesBackWhatItPublishedItsKeysAndItsAutomations()
    {
        await using var fleet = await FleetFixture.StartAsync();
        var desktopKeys = new FleetMetadataKeyring(new FleetOrganizationDevices(fleet.Server.BaseAddress, fleet.DesktopKey));
        await desktopKeys.RefreshAsync("work", CancellationToken.None);
        var nodeKeys = new FleetMetadataKeyring(new FleetOrganizationDevices(fleet.Server.BaseAddress, fleet.NodeKey));
        await nodeKeys.RefreshAsync("work", CancellationToken.None);
        var source = new FakeSource();
        source.Set(Session("s1", "work") with { Title = "Fix the parser" }, Session("s2", "work"));
        await using (var connector = fleet.NodeConnector(source, static _ => true, keys: nodeKeys))
        {
            connector.Start();
            await WaitForAsync(async () => (await fleet.Desktop.ListSessionsAsync()).Count(session => session.SealedMetadata is not null) == 1
                && (await fleet.Desktop.ListMachinesAsync()).Any(machine => machine.DeviceId == fleet.NodeKey.DeviceId));
        }

        // Its statistics and an automation the desktop gave it through the server.
        var store = fleet.Server.GetService<ICoordinatorStore>();
        var organization = (await fleet.Desktop.GetOrganizationAsync()).Id;
        var person = (await fleet.Desktop.GetDeviceAsync()).OwnerId!;
        await ((CodeAlta.Server.Storage.IStatisticsFactStore)store).PublishStatisticsAsync(fleet.NodeKey.DeviceId, organization, 1, "g1",
            [new CodeAlta.Server.Storage.StatisticsSetRecord("s1", 0, 1, null, [new FleetStatisticsRow("histogram", 100, "anthropic", "m", null, new Dictionary<string, long> { ["n"] = 1 })])],
            DateTimeOffset.UtcNow);
        await ((CodeAlta.Server.Storage.IAutomationStore)store).PutAutomationAsync(new CodeAlta.Server.Storage.AutomationRecord(organization, "daily", fleet.NodeKey.DeviceId, person, 1,
            false, true, "[]", "{}", "signature", fleet.DesktopKey.DeviceId, DateTimeOffset.UtcNow, "pending", null),
            new CodeAlta.Server.Storage.AuditEntry("automation.put", CodeAlta.Server.Storage.AuditEntry.Success, person, fleet.DesktopKey.DeviceId, "daily", null, null, null));

        using (var node = new FleetServerClient(fleet.Server.BaseAddress, fleet.NodeKey) { DeviceList = nodeKeys.Devices })
        {
            var left = await node.LeaveAsync();
            Assert.AreEqual(new LeaveResponse(2, 1, 1, 1, false), left, "a node signs no entry: it leaves the list to a controller device of its person");
            await Assert.ThrowsExactlyAsync<FleetServerException>(() => node.ListSessionsAsync(), "refused from then on");
        }

        Assert.AreEqual(0, (await fleet.Desktop.ListSessionsAsync()).Count, "the facts and their sealed titles are gone");
        Assert.IsFalse((await fleet.Desktop.ListMachinesAsync()).Any(machine => machine.DeviceId == fleet.NodeKey.DeviceId));
        Assert.AreEqual(0, (await ((CodeAlta.Server.Storage.IStatisticsFactStore)store).ListStatisticsPublishersAsync(organization)).Count);
        Assert.IsNull(await ((CodeAlta.Server.Storage.IAutomationStore)store).GetAutomationAsync(organization, "daily", fleet.NodeKey.DeviceId));
        Assert.AreEqual("revoked", (await fleet.Desktop.GetDeviceAsync(fleet.NodeKey.DeviceId)).Status);
        // The desktop's keys go on: the device that left gets no copy, and the others still do.
        await desktopKeys.RefreshAsync(["work", "home"], CancellationToken.None);
        Assert.IsTrue((await fleet.Desktop.ListMetadataKeysAsync()).All(key => !key.WrappedTo.Contains(fleet.NodeKey.DeviceId)));
        Assert.AreEqual(2, (await fleet.Desktop.ListMetadataKeysAsync()).Count);
    }

    [TestMethod]
    public async Task AControllerThatLeavesSignsItsRemovalSoThatItsKeysGetANewEpoch()
    {
        await using var fleet = await FleetFixture.StartAsync();
        using var pcKey = DeviceIdentity.LoadOrCreate(Path.Combine(fleet.Root.Path, "pc"));
        using (var pc = new FleetServerClient(fleet.Server.BaseAddress, pcKey)) await pc.EnrollAsync((await fleet.Desktop.CreateEnrollmentTokenAsync()).Token, "PC", FleetDeviceKinds.Controller);
        await fleet.Desktop.ApproveAsync(pcKey.DeviceId, FleetDeviceKinds.Controller);
        var desktopKeys = new FleetMetadataKeyring(new FleetOrganizationDevices(fleet.Server.BaseAddress, fleet.DesktopKey));
        var first = (await desktopKeys.RefreshAsync("work", CancellationToken.None))!;

        using (var pc = new FleetServerClient(fleet.Server.BaseAddress, pcKey))
        {
            var left = await pc.LeaveAsync();
            Assert.AreEqual((1, true), (left.KeyCopies, left.RemovedFromList));
        }

        var list = await fleet.Desktop.ReadVerifiedDeviceListAsync();
        Assert.IsTrue(list.Removed.Contains(pcKey.DeviceId), "removed from the signed list by its own entry");
        var second = (await desktopKeys.RefreshAsync("work", CancellationToken.None))!;
        Assert.AreEqual((2, false), (second.Grant!.Epoch, second.Id == first.Id), "what the PC held seals nothing more");

        // The last controller device leaves without its removal: the list keeps a controller.
        var left2 = await fleet.Desktop.LeaveAsync();
        Assert.IsFalse(left2.RemovedFromList);
    }

    [TestMethod]
    public async Task ANewDeviceGetsNoKeyBeforeAnotherDeviceOfThePersonConfirmedItsWords()
    {
        await using var fleet = await FleetFixture.StartAsync();
        using var laptopKey = DeviceIdentity.LoadOrCreate(Path.Combine(fleet.Root.Path, "laptop"));
        using (var laptop = new FleetServerClient(fleet.Server.BaseAddress, laptopKey))
            await laptop.EnrollAsync((await fleet.Desktop.CreateEnrollmentTokenAsync()).Token, "Laptop", FleetDeviceKinds.Controller);
        var desktopKeys = new FleetMetadataKeyring(new FleetOrganizationDevices(fleet.Server.BaseAddress, fleet.DesktopKey));
        await desktopKeys.RefreshAsync("work", CancellationToken.None);
        Assert.IsFalse((await fleet.Desktop.ListMetadataKeysAsync()).Single().WrappedTo.Contains(laptopKey.DeviceId), "pending: no key");

        // The words compared are not the ones of its key: nothing is signed, it stays pending.
        await Assert.ThrowsExactlyAsync<FleetDeviceListException>(() => fleet.Desktop.ApproveAsync(laptopKey.DeviceId, FleetDeviceKinds.Controller, "otter river robin saddle salmon pearl"));
        Assert.AreEqual("pending", (await fleet.Desktop.GetDeviceAsync(laptopKey.DeviceId)).Status);

        // The same words on both screens: approved, then it gets its copy.
        await fleet.Desktop.ApproveAsync(laptopKey.DeviceId, FleetDeviceKinds.Controller, laptopKey.Fingerprint.ToShortWords());
        await desktopKeys.RefreshAsync("work", CancellationToken.None);
        Assert.IsTrue((await fleet.Desktop.ListMetadataKeysAsync()).Single().WrappedTo.Contains(laptopKey.DeviceId));
    }
}
