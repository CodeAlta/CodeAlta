using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;

namespace CodeAlta.Server.Tests;

/// <summary>
/// The hooks of the server: a person makes one for a machine of theirs; a tool pushes its events there without a device;
/// each event is kept once, as it came, handed to the machine now or when it connects, and only its facts stay once the
/// machine answered. The server checks no signature: it holds no secret of the sender.
/// </summary>
[TestClass]
public sealed class HookServerTests
{
    private const string Body = """{"event_type":"public_incident.incident_created_v2","public_incident.incident_created_v2":{"id":"01INC"}}""";

    [TestMethod]
    public async Task AnEventPushedToAHook_IsHandedToItsMachineAsItCame_Once()
    {
        await using var fixture = await ServerFixture.StartAsync();
        using var owner = new TestDevice("laptop");
        using var node = new TestDevice("node");
        await fixture.SetUpWithNodeAsync(owner, node);
        var events = Channel.CreateUnbounded<FleetHookEvent>();
        var (connection, welcome) = await ConnectAsync(fixture, node, events);
        await using var _ = connection;
        CollectionAssert.Contains(welcome.Features!.ToList(), FleetHookMethods.Feature);

        var hook = await CreateAsync(fixture, owner, node.DeviceId);
        Assert.AreEqual(($"/api/v1/hooks/{hook.Id}", node.DeviceId, HookSources.IncidentIo, "incident.io"), (hook.Path, hook.MachineId, hook.Source, hook.Label));
        Assert.IsTrue(hook.Id.Length >= 26 && hook.Id.All(static ch => ch is >= 'a' and <= 'z' or >= '2' and <= '7'));

        var (status, receipt) = await PushAsync(fixture, hook.Path, Body, "msg_1");
        Assert.AreEqual(HttpStatusCode.Accepted, status);
        Assert.IsFalse(receipt.Duplicate);
        var handed = await NextAsync(events);
        Assert.AreEqual((receipt.DeliveryId, hook.Id, Body), (handed.DeliveryId, handed.HookId, Encoding.UTF8.GetString(Convert.FromBase64String(handed.Body))));
        Assert.AreEqual("msg_1", handed.Headers["webhook-id"]);
        Assert.AreEqual("v1,signature", handed.Headers["webhook-signature"]);
        Assert.IsFalse(handed.Headers.ContainsKey("cookie"), "what says something of the connection stays at the server");

        // The sender retries: one delivery.
        var (_, again) = await PushAsync(fixture, hook.Path, Body, "msg_1");
        Assert.AreEqual((receipt.DeliveryId, true), (again.DeliveryId, again.Duplicate));
        // A forged event under the same id is another one: it cannot take the place of the real one.
        var forged = (await PushAsync(fixture, hook.Path, Body.Replace("01INC", "02INC", StringComparison.Ordinal), "msg_1")).Receipt;
        Assert.IsFalse(forged.Duplicate);
        // Handed over at least once: the resend of what waits, when the machine connected, may cross a push; the machine takes an event once.
        while ((await NextAsync(events)).DeliveryId != forged.DeliveryId) { }

        await connection.SendRequestAsync(FleetHookMethods.Ack, JsonSerializer.SerializeToElement(new FleetHookAcks([new FleetHookAck(receipt.DeliveryId, true, "Incidents: started.")]),
            FleetHookServerJsonContext.Default.FleetHookAcks));
        var listed = await EventsAsync(fixture, owner, hook.Id);
        var answered = listed.Single(item => item.DeliveryId == receipt.DeliveryId);
        Assert.AreEqual((HookEventStates.Accepted, "Incidents: started."), (answered.State, answered.Message));
        Assert.AreEqual(1, (await ListAsync(fixture, owner)).Single().Pending, "the forged one waits for its answer");
        while (events.Reader.TryRead(out var extra)) CollectionAssert.Contains(new[] { receipt.DeliveryId, forged.DeliveryId }, extra.DeliveryId, "no other event was handed");
    }

    // What a sender authenticates itself with (a bearer token, an API key) is no part of the event: the ingress keeps the headers
    // of the event and of its signature only, so that no credential is ever stored in clear while the event waits.
    [TestMethod]
    public async Task TheIngress_KeepsTheHeadersOfTheEventAndItsSignature_NeverACredential()
    {
        await using var fixture = await ServerFixture.StartAsync();
        using var owner = new TestDevice("laptop");
        using var node = new TestDevice("node");
        await fixture.SetUpWithNodeAsync(owner, node);
        var hook = await CreateAsync(fixture, owner, node.DeviceId, HookSources.Webhook);
        const string Canary = "canary-0f9e8d7c6b5a";

        using var request = new HttpRequestMessage(HttpMethod.Post, hook.Path) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(Body)) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Canary);
        request.Headers.TryAddWithoutValidation("X-Api-Key", Canary);
        request.Headers.TryAddWithoutValidation("X-Gitlab-Token", Canary);
        request.Headers.TryAddWithoutValidation("X-Custom-Secret", Canary);
        request.Headers.TryAddWithoutValidation("Sentry-Hook-Signature", "abcdef");
        request.Headers.TryAddWithoutValidation("X-Hub-Signature-256", "sha256=abcdef");
        request.Headers.TryAddWithoutValidation("X-GitHub-Event", "issues");
        request.Headers.TryAddWithoutValidation("webhook-timestamp", "1800000000");
        using (var response = await fixture.Http.SendAsync(request)) Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);

        var store = fixture.GetService<CodeAlta.Server.Storage.ICoordinatorStore>();
        var device = await store.GetDeviceAsync(node.DeviceId, CancellationToken.None);
        var stored = (await ((CodeAlta.Server.Storage.IHookStore)store).ListPendingHookEventsAsync(device!.OrganizationId, node.DeviceId, fixture.Clock.GetUtcNow(), 10)).Single();
        Assert.DoesNotContain(Canary, stored.Headers ?? string.Empty, "no credential is kept");
        var headers = JsonSerializer.Deserialize<Dictionary<string, string>>(stored.Headers!)!;
        foreach (var kept in new[] { "content-type", "sentry-hook-signature", "x-hub-signature-256", "x-github-event", "webhook-timestamp" })
            Assert.IsTrue(headers.ContainsKey(kept), kept);
        foreach (var dropped in new[] { "authorization", "x-api-key", "x-gitlab-token", "x-custom-secret" })
            Assert.IsFalse(headers.ContainsKey(dropped), dropped);
    }

    [TestMethod]
    public async Task AMachineOffline_GetsItsEventsWhenItConnects_WithinADay()
    {
        await using var fixture = await ServerFixture.StartAsync();
        using var owner = new TestDevice("laptop");
        using var node = new TestDevice("node");
        await fixture.SetUpWithNodeAsync(owner, node);
        var hook = await CreateAsync(fixture, owner, node.DeviceId, HookSources.Webhook);

        var (_, first) = await PushAsync(fixture, hook.Path, """{"n":1}""", "a");
        var events = Channel.CreateUnbounded<FleetHookEvent>();
        var (connection, _) = await ConnectAsync(fixture, node, events);
        await using (connection)
        {
            Assert.AreEqual(first.DeliveryId, (await NextAsync(events)).DeliveryId);
            await connection.SendRequestAsync(FleetHookMethods.Ack, JsonSerializer.SerializeToElement(new FleetHookAcks([new FleetHookAck(first.DeliveryId, false, "The signature does not verify.")]),
                FleetHookServerJsonContext.Default.FleetHookAcks));
        }

        var (_, late) = await PushAsync(fixture, hook.Path, """{"n":2}""", "b");
        fixture.Clock.Advance(TimeSpan.FromHours(25));
        // A day later, it is no longer handed over: it expired.
        await PushAsync(fixture, hook.Path, """{"n":3}""", "c");
        var states = (await EventsAsync(fixture, owner, hook.Id)).ToDictionary(static item => item.DeliveryId, static item => item.State);
        Assert.AreEqual(HookEventStates.Refused, states[first.DeliveryId]);
        Assert.AreEqual(HookEventStates.Expired, states[late.DeliveryId]);
    }

    [TestMethod]
    public async Task TheIngress_RefusesWhatIsNotAHook_TooLarge_OrTooFast_AndAHookIsAPersonsOwn()
    {
        await using var fixture = await ServerFixture.StartAsync();
        using var owner = new TestDevice("laptop");
        using var node = new TestDevice("node");
        await fixture.SetUpWithNodeAsync(owner, node);
        var hook = await CreateAsync(fixture, owner, node.DeviceId, HookSources.Webhook);

        Assert.AreEqual(HttpStatusCode.NotFound, (await PushAsync(fixture, "/api/v1/hooks/nosuchhook0123456789abcdef", Body, "x")).Status);
        Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, (await PushAsync(fixture, hook.Path, new string('x', 300 * 1024), "big")).Status);
        for (var index = 0; index < 60; index++) Assert.AreEqual(HttpStatusCode.Accepted, (await PushAsync(fixture, hook.Path, "{}", "n" + index)).Status);
        Assert.AreEqual(HttpStatusCode.TooManyRequests, (await PushAsync(fixture, hook.Path, "{}", "over")).Status);

        // A node manages no hook; a hook goes to a machine, not to a controller device.
        Assert.AreEqual(HttpStatusCode.Forbidden, (await fixture.SendAsync(node, HttpMethod.Post, "/api/v1/hooks", new CreateHookRequest(node.DeviceId, HookSources.Webhook, null))).Status);
        Assert.AreEqual(HttpStatusCode.NotFound, (await fixture.SendAsync(owner, HttpMethod.Post, "/api/v1/hooks", new CreateHookRequest(owner.DeviceId, HookSources.Webhook, null))).Status);
        Assert.AreEqual("hook.invalid", (await fixture.SendAsync(owner, HttpMethod.Post, "/api/v1/hooks", new CreateHookRequest(node.DeviceId, "pagerduty", null))).Code);

        var deleted = await fixture.SendAsync(owner, HttpMethod.Delete, "/api/v1/hooks/" + hook.Id);
        Assert.AreEqual(HttpStatusCode.OK, deleted.Status, deleted.Body.ToString());
        Assert.AreEqual(0, deleted.Read<HookListDto>().Hooks.Count);
        fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.AreEqual(HttpStatusCode.NotFound, (await PushAsync(fixture, hook.Path, "{}", "gone")).Status);
    }

    private static async Task<HookDto> CreateAsync(ServerFixture fixture, TestDevice owner, string machineId, string source = HookSources.IncidentIo)
    {
        var answer = await fixture.SendAsync(owner, HttpMethod.Post, "/api/v1/hooks", new CreateHookRequest(machineId, source, null));
        Assert.AreEqual(HttpStatusCode.Created, answer.Status, answer.Body.ToString());
        return answer.Read<HookDto>();
    }

    private static async Task<IReadOnlyList<HookDto>> ListAsync(ServerFixture fixture, TestDevice owner)
    {
        var answer = await fixture.SendAsync(owner, HttpMethod.Get, "/api/v1/hooks");
        Assert.AreEqual(HttpStatusCode.OK, answer.Status, answer.Body.ToString());
        return answer.Read<HookListDto>().Hooks;
    }

    private static async Task<IReadOnlyList<HookEventDto>> EventsAsync(ServerFixture fixture, TestDevice owner, string id)
    {
        var answer = await fixture.SendAsync(owner, HttpMethod.Get, $"/api/v1/hooks/{id}/events");
        Assert.AreEqual(HttpStatusCode.OK, answer.Status, answer.Body.ToString());
        return answer.Read<HookEventListDto>().Events;
    }

    // As a tool pushes: no device, the headers of its signature, and a cookie a proxy may add.
    private static async Task<(HttpStatusCode Status, HookReceiptDto Receipt)> PushAsync(ServerFixture fixture, string path, string body, string id)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Add("webhook-id", id);
        request.Headers.Add("webhook-timestamp", "1800000000");
        request.Headers.Add("webhook-signature", "v1,signature");
        request.Headers.Add("Cookie", "session=abc");
        using var response = await fixture.Http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, response.StatusCode == HttpStatusCode.Accepted
            ? JsonSerializer.Deserialize(text, FleetHookApiJsonContext.Default.HookReceiptDto)! : new HookReceiptDto(string.Empty, false));
    }

    private static async Task<(FleetConnection Connection, FleetWelcome Welcome)> ConnectAsync(ServerFixture fixture, TestDevice node, Channel<FleetHookEvent> events)
        => await fixture.ConnectAsync(node, connection => connection.SetEventHandler((fleetEvent, _) =>
        {
            if (fleetEvent.Event == FleetHookMethods.Event) events.Writer.TryWrite(fleetEvent.Payload!.Value.Deserialize(FleetHookServerJsonContext.Default.FleetHookEvent)!);
            return ValueTask.CompletedTask;
        }));

    private static async Task<T> NextAsync<T>(Channel<T> channel)
    {
        using var timeout = new CancellationTokenSource(Waits.Hang);
        return await channel.Reader.ReadAsync(timeout.Token);
    }
}
