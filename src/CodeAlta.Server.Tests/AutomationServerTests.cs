using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;

namespace CodeAlta.Server.Tests;

/// <summary>
/// The automations a person gives their machines through the server: a version signed by a controller device of that
/// person, sealed for the machine, stored once and never replaced by one that is not newer, delivered to the machine
/// now or when it connects, and acknowledged; the due times claimed once whatever the machine, and the runs reported.
/// </summary>
[TestClass]
public sealed class AutomationServerTests
{
    private const string Id = "daily-1";

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task AVersionIsStoredAsSigned_AndDeliveredToItsMachine_NowAndOnReconnect()
    {
        await using var fixture = await ServerFixture.StartAsync();
        using var owner = new TestDevice("laptop");
        using var node = new TestDevice("node");
        var (setup, _) = await fixture.SetUpWithNodeAsync(owner, node);
        var organization = setup.Organization.Id;

        var delivered = Channel.CreateUnbounded<FleetSignedAutomationVersion>();
        var (connection, welcome) = await ConnectNodeAsync(fixture, node, delivered);
        CollectionAssert.Contains(welcome.Features!.ToList(), FleetAutomationMethods.Feature);

        var first = Sign(owner, Version(organization, node.DeviceId, 1));
        var put = await fixture.SendAsync(owner, HttpMethod.Put, $"/api/v1/automations/{Id}", new PutAutomationRequest(first));
        Assert.AreEqual(HttpStatusCode.OK, put.Status, put.Body.ToString());
        var stored = put.Read<AutomationDto>();
        Assert.AreEqual((Id, node.DeviceId, 1L, true, AutomationDeliveryState.Pending), (stored.Id, stored.MachineId, stored.Number, stored.Enabled, stored.State));
        CollectionAssert.AreEqual(new[] { "hourly" }, stored.Triggers.ToArray());
        Assert.AreEqual(first, await NextAsync(delivered), "the machine gets the version as signed");

        // The machine took it.
        await connection.SendRequestAsync(FleetAutomationMethods.Ack, JsonSerializer.SerializeToElement(
            new FleetAutomationAcks([new FleetAutomationAck(Id, 1, true, null)]), FleetAutomationServerJsonContext.Default.FleetAutomationAcks));
        Assert.AreEqual(AutomationDeliveryState.Applied, (await ListAsync(fixture, owner)).Single().State);

        // Offline, the machine gets the newer version when it connects again; the same or an older one is refused.
        await connection.DisposeAsync();
        await WaitOfflineAsync(fixture, node.DeviceId);
        var second = Sign(owner, Version(organization, node.DeviceId, 2) with { Enabled = false });
        Assert.AreEqual(HttpStatusCode.OK, (await fixture.SendAsync(owner, HttpMethod.Put, $"/api/v1/automations/{Id}", new PutAutomationRequest(second))).Status);
        var again = await fixture.SendAsync(owner, HttpMethod.Put, $"/api/v1/automations/{Id}", new PutAutomationRequest(second));
        Assert.AreEqual((HttpStatusCode.Conflict, "automation.conflict"), (again.Status, again.Code));
        var older = await fixture.SendAsync(owner, HttpMethod.Put, $"/api/v1/automations/{Id}", new PutAutomationRequest(first));
        Assert.AreEqual(HttpStatusCode.Conflict, older.Status);
        Assert.AreEqual((2L, false, AutomationDeliveryState.Pending), ((await ListAsync(fixture, owner)).Single() is var listed ? (listed.Number, listed.Enabled, listed.State) : default));

        var later = Channel.CreateUnbounded<FleetSignedAutomationVersion>();
        var (reconnected, _) = await ConnectNodeAsync(fixture, node, later);
        await using (reconnected)
        {
            Assert.AreEqual(second, await NextAsync(later));
            // A refusal is kept with its reason, and the version is not sent again.
            await reconnected.SendRequestAsync(FleetAutomationMethods.Ack, JsonSerializer.SerializeToElement(
                new FleetAutomationAcks([new FleetAutomationAck(Id, 2, false, "the signature does not hold")]), FleetAutomationServerJsonContext.Default.FleetAutomationAcks));
            var refused = (await ListAsync(fixture, owner)).Single();
            Assert.AreEqual((AutomationDeliveryState.Refused, "the signature does not hold"), (refused.State, refused.Message));
        }
    }

    [TestMethod]
    public async Task OnlyAControllerOfTheMachinesPersonGivesIt_AVersionItSignedSealed()
    {
        await using var fixture = await ServerFixture.StartAsync();
        using var owner = new TestDevice("laptop");
        using var node = new TestDevice("node");
        var (setup, _) = await fixture.SetUpWithNodeAsync(owner, node);
        var organization = setup.Organization.Id;
        var version = Version(organization, node.DeviceId, 1);

        async Task<ApiAnswer> PutAsync(TestDevice caller, FleetSignedAutomationVersion signed, string id = Id)
            => await fixture.SendAsync(caller, HttpMethod.Put, $"/api/v1/automations/{id}", new PutAutomationRequest(signed));

        var byNode = await PutAsync(node, Sign(node, version));
        Assert.AreEqual(HttpStatusCode.Forbidden, byNode.Status, "a node gives no automation");
        var tampered = Sign(owner, version);
        Assert.AreEqual("automation.invalid", (await PutAsync(owner, tampered with { Payload = tampered.Payload.Replace("hourly", "minute", StringComparison.Ordinal) })).Code);
        StringAssert.Contains((await PutAsync(owner, Sign(owner, version with { SignerDeviceId = node.DeviceId }))).Body.ToString(), "name the device that signs");
        StringAssert.Contains((await PutAsync(owner, Sign(owner, version with { Sealed = null, Definition = "[automations.daily-1]" }))).Body.ToString(), "never in clear");
        StringAssert.Contains((await PutAsync(owner, Sign(owner, version with { OrganizationId = "another" }))).Body.ToString(), "organization of the server");
        StringAssert.Contains((await PutAsync(owner, Sign(owner, version), "other-id")).Body.ToString(), "another automation than the path");
        Assert.AreEqual(HttpStatusCode.NotFound, (await PutAsync(owner, Sign(owner, version with { MachineId = "no-such-machine" }))).Status);
        Assert.AreEqual(HttpStatusCode.NotFound, (await PutAsync(owner, Sign(owner, version with { MachineId = owner.DeviceId }))).Status, "a controller device is no machine");
        Assert.AreEqual(0, (await ListAsync(fixture, owner)).Count, "nothing refused was stored");
        Assert.AreEqual(HttpStatusCode.OK, (await PutAsync(owner, Sign(owner, version))).Status);
    }

    [TestMethod]
    public async Task ADueTimeRunsOnce_WhateverTheMachine_AndRunsAreReported()
    {
        await using var fixture = await ServerFixture.StartAsync();
        using var owner = new TestDevice("laptop");
        using var node = new TestDevice("node");
        using var other = new TestDevice("other");
        var (setup, _) = await fixture.SetUpWithNodeAsync(owner, node);
        var organization = setup.Organization.Id;
        var pending = await fixture.EnrollAsync(owner, other);
        var entry = await fixture.NextEntryAsync(owner, FleetDeviceListOperations.Add, pending.Id, other.PublicKey, FleetDeviceKinds.Node, setup.Owner.Id, organization);
        Assert.AreEqual(HttpStatusCode.OK, (await fixture.SendAsync(owner, HttpMethod.Post, $"/api/v1/devices/{pending.Id}/approve", new DeviceListChangeRequest(entry))).Status);

        var (connection, _) = await ConnectNodeAsync(fixture, node, Channel.CreateUnbounded<FleetSignedAutomationVersion>());
        await using var _1 = connection;
        // Without a version for it, the machine claims nothing through the server.
        Assert.IsFalse((await ClaimAsync(connection, "run-1", "hourly@2026-11-03T10:00:00Z")).Granted);
        Assert.AreEqual(HttpStatusCode.OK, (await fixture.SendAsync(owner, HttpMethod.Put, $"/api/v1/automations/{Id}",
            new PutAutomationRequest(Sign(owner, Version(organization, node.DeviceId, 1))))).Status);

        Assert.IsTrue((await ClaimAsync(connection, "run-1", "hourly@2026-11-03T10:00:00Z")).Granted);
        Assert.IsTrue((await ClaimAsync(connection, "run-1", "hourly@2026-11-03T10:00:00Z")).Granted, "the same run asks again");
        StringAssert.Contains((await ClaimAsync(connection, "run-2", "hourly@2026-11-03T10:00:00Z")).Message, "another run of this machine");

        // The automation moves to the other machine: the time the first one took is not run again there.
        Assert.AreEqual(HttpStatusCode.OK, (await fixture.SendAsync(owner, HttpMethod.Put, $"/api/v1/automations/{Id}",
            new PutAutomationRequest(Sign(owner, Version(organization, other.DeviceId, 2))))).Status);
        var (second, _) = await ConnectNodeAsync(fixture, other, Channel.CreateUnbounded<FleetSignedAutomationVersion>());
        await using var _2 = second;
        var taken = await ClaimAsync(second, "run-9", "hourly@2026-11-03T10:00:00Z", number: 2);
        Assert.IsFalse(taken.Granted);
        StringAssert.Contains(taken.Message, node.DeviceId);
        Assert.IsTrue((await ClaimAsync(second, "run-10", "hourly@2026-11-03T11:00:00Z", number: 2)).Granted);

        // The runs, as facts: the claimed one ended, one by hand, and one of an automation the server does not hold, which is ignored.
        var now = fixture.Clock.Now;
        await connection.SendRequestAsync(FleetAutomationMethods.Runs, JsonSerializer.SerializeToElement(new FleetAutomationRunFacts([
            new FleetAutomationRunFact("run-1", Id, 1, "hourly", "completed", now, now.AddMinutes(2)) { SessionId = "s1" },
            new FleetAutomationRunFact("run-3", Id, 1, "manual", "failed", now.AddMinutes(3), now.AddMinutes(3)) { Message = "No provider\nis configured." },
            new FleetAutomationRunFact("run-4", "of-the-profile", 1, "manual", "completed", now, now),
        ]), FleetAutomationServerJsonContext.Default.FleetAutomationRunFacts));
        // The second machine cannot rewrite a run of the first one.
        await second.SendRequestAsync(FleetAutomationMethods.Runs, JsonSerializer.SerializeToElement(new FleetAutomationRunFacts([
            new FleetAutomationRunFact("run-1", Id, 2, "hourly", "failed", now, now)]), FleetAutomationServerJsonContext.Default.FleetAutomationRunFacts));

        var runs = await fixture.SendAsync(owner, HttpMethod.Get, $"/api/v1/automations/{Id}/runs");
        Assert.AreEqual(HttpStatusCode.OK, runs.Status, runs.Body.ToString());
        var list = runs.Read<AutomationRunListDto>().Runs;
        Assert.AreEqual("run-3,run-10,run-1", string.Join(',', list.Select(static run => run.RunId)));
        var first = list.Single(static run => run.RunId == "run-1");
        Assert.AreEqual(("completed", node.DeviceId, "hourly@2026-11-03T10:00:00Z", "s1"), (first.Status, first.MachineId, first.Occurrence, first.SessionId));
        Assert.AreEqual("No provider is configured.", list.Single(static run => run.RunId == "run-3").Message);
        Assert.AreEqual(AutomationRunStatus.Claimed, list.Single(static run => run.RunId == "run-10").Status);
        Assert.AreEqual(HttpStatusCode.NotFound, (await fixture.SendAsync(owner, HttpMethod.Get, "/api/v1/automations/of-the-profile/runs")).Status);
    }

    [TestMethod]
    public async Task ARemovalIsAVersionToo_AndTheServerForgetsItOnceTheMachineTookItAway()
    {
        await using var fixture = await ServerFixture.StartAsync();
        using var owner = new TestDevice("laptop");
        using var node = new TestDevice("node");
        var (setup, _) = await fixture.SetUpWithNodeAsync(owner, node);
        var organization = setup.Organization.Id;
        var delivered = Channel.CreateUnbounded<FleetSignedAutomationVersion>();
        var (connection, _) = await ConnectNodeAsync(fixture, node, delivered);
        await using var _1 = connection;

        Assert.AreEqual(HttpStatusCode.OK, (await fixture.SendAsync(owner, HttpMethod.Put, $"/api/v1/automations/{Id}",
            new PutAutomationRequest(Sign(owner, Version(organization, node.DeviceId, 1))))).Status);
        await NextAsync(delivered);
        var delete = $"/api/v1/automations/{Id}?machine={node.DeviceId}";
        Assert.AreEqual(HttpStatusCode.Conflict, (await fixture.SendAsync(owner, HttpMethod.Delete, delete)).Status, "the machine still holds it");

        var removal = Sign(owner, Version(organization, node.DeviceId, 2) with { Removed = true, Sealed = null });
        var put = await fixture.SendAsync(owner, HttpMethod.Put, $"/api/v1/automations/{Id}", new PutAutomationRequest(removal));
        Assert.AreEqual(HttpStatusCode.OK, put.Status, put.Body.ToString());
        Assert.IsTrue(put.Read<AutomationDto>().Removed);
        Assert.AreEqual(removal, await NextAsync(delivered));
        Assert.AreEqual(HttpStatusCode.Conflict, (await fixture.SendAsync(owner, HttpMethod.Delete, delete)).Status, "not taken away yet");
        await connection.SendRequestAsync(FleetAutomationMethods.Ack, JsonSerializer.SerializeToElement(
            new FleetAutomationAcks([new FleetAutomationAck(Id, 2, true, null)]), FleetAutomationServerJsonContext.Default.FleetAutomationAcks));
        var forgotten = await fixture.SendAsync(owner, HttpMethod.Delete, delete);
        Assert.AreEqual(HttpStatusCode.OK, forgotten.Status, forgotten.Body.ToString());
        Assert.AreEqual(0, forgotten.Read<AutomationListDto>().Automations.Count);
        Assert.AreEqual(0, (await ListAsync(fixture, owner)).Count);
    }

    private static class AutomationDeliveryState
    {
        public const string Pending = "pending";
        public const string Applied = "applied";
        public const string Refused = "refused";
    }

    private static class AutomationRunStatus
    {
        public const string Claimed = "claimed";
    }

    private static FleetAutomationVersion Version(string organization, string machine, long number)
        => new(FleetAutomationVersion.CurrentVersion, organization, Id, number, machine, false, true, ["hourly"], null, null, "{\"sealed\":\"for the machine\"}",
            string.Empty, string.Empty, DateTimeOffset.UtcNow);

    // Signs as the device, naming it as the signer unless the version names another one.
    private static FleetSignedAutomationVersion Sign(TestDevice device, FleetAutomationVersion version)
    {
        var named = version with
        {
            SignerDeviceId = version.SignerDeviceId is { Length: > 0 } signer ? signer : device.DeviceId,
            SignerKey = version.SignerKey is { Length: > 0 } key ? key : device.PublicKey,
        };
        var text = FleetAutomationVersionDocuments.Serialize(named);
        return new FleetSignedAutomationVersion(text, device.Sign(FleetAutomationVersionDocuments.GetSignedBytes(text)));
    }

    private static async Task<(FleetConnection Connection, FleetWelcome Welcome)> ConnectNodeAsync(ServerFixture fixture, TestDevice node,
        Channel<FleetSignedAutomationVersion> delivered)
        => await fixture.ConnectAsync(node, connection => connection.SetEventHandler((fleetEvent, _) =>
        {
            if (fleetEvent.Event == FleetAutomationMethods.Assigned)
            {
                foreach (var version in fleetEvent.Payload!.Value.Deserialize(FleetAutomationServerJsonContext.Default.FleetAutomationDelivery)!.Versions)
                    delivered.Writer.TryWrite(version);
            }

            return ValueTask.CompletedTask;
        }));

    private static async Task<FleetAutomationClaimResult> ClaimAsync(FleetConnection connection, string runId, string occurrence, long number = 1)
        => (await connection.SendRequestAsync(FleetAutomationMethods.Claim, new FleetAutomationClaimRequest(Id, number, occurrence, runId, DateTimeOffset.UtcNow.AddMinutes(5)),
            FleetAutomationServerJsonContext.Default.FleetAutomationClaimRequest, FleetAutomationServerJsonContext.Default.FleetAutomationClaimResult))!;

    private static async Task<IReadOnlyList<AutomationDto>> ListAsync(ServerFixture fixture, TestDevice owner)
    {
        var answer = await fixture.SendAsync(owner, HttpMethod.Get, "/api/v1/automations");
        Assert.AreEqual(HttpStatusCode.OK, answer.Status, answer.Body.ToString());
        return answer.Read<AutomationListDto>().Automations;
    }

    private static async Task<T> NextAsync<T>(Channel<T> channel)
    {
        using var timeout = new CancellationTokenSource(Waits.Hang);
        return await channel.Reader.ReadAsync(timeout.Token);
    }

    private static async Task WaitOfflineAsync(ServerFixture fixture, string deviceId)
    {
        var presence = fixture.GetService<Control.PresenceRegistry>();
        var deadline = DateTime.UtcNow + Waits.Hang;
        while (presence.IsOnline(deviceId))
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("The machine stayed online.");
            await Task.Delay(20);
        }
    }
}
