using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Fleet.Tests.Otlp;

namespace CodeAlta.Server.Tests;

/// <summary>
/// The telemetry of the server (<c>doc/fleet/observability.md</c>): its own traces, metrics and logs over OTLP, the
/// fleet's setting an owner signs, which the server applies at once and pushes to the machines, the owner's file of the
/// server that wins over it, and the OTLP endpoint that forwards what machines send with headers only the server holds.
/// </summary>
[TestClass]
public sealed class TelemetryTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(200);

    [TestMethod]
    public async Task TheServerExportsItsTracesMetricsAndLogsToTheCollectorOfItsFile()
    {
        await using var receiver = await OtlpReceiver.StartAsync();
        using var root = new TempDirectory();
        File.WriteAllText(Path.Combine(root.Path, FleetLocalTelemetrySettings.FileName),
            $$"""{ "endpoint": "{{receiver.Endpoint}}", "signals": ["traces", "metrics", "logs"], "environment": "test" }""");
        await using var fixture = await ServerFixture.StartAsync(root, telemetryInterval: Interval);
        using var owner = new TestDevice("Desktop");
        var setup = await fixture.SetUpAsync(owner);
        Assert.AreEqual(HttpStatusCode.OK, (await fixture.SendAsync(owner, HttpMethod.Get, "/api/v1/organization")).Status);

        var trace = await receiver.WaitAsync(request => request.Signal == "traces" && request.Contains("GET /api/v1/organization")
            && request.Resource.ContainsKey("codealta.fleet.organization.id"));
        Assert.AreEqual("altaserver", trace.ServiceName);
        Assert.AreEqual(setup.Organization.Id, trace.Resource["codealta.fleet.organization.id"]);
        Assert.AreEqual(fixture.Server.Telemetry.InstanceId, trace.Resource["service.instance.id"]);
        Assert.AreEqual("test", trace.Resource["deployment.environment.name"]);
        Assert.AreEqual(Environment.MachineName, trace.Resource["host.name"]);
        Assert.IsTrue(trace.Resource.ContainsKey("os.type") && trace.Resource.ContainsKey("service.version"));
        Assert.IsTrue(trace.Contains("codealta.device.short_id"), "the span of a signed request names its device in short");
        Assert.IsFalse(trace.Contains(owner.PublicKey), "no key in a span");

        var metrics = await receiver.WaitAsync(request => request.Signal == "metrics" && request.Contains("codealta.server.store.duration"));
        Assert.AreEqual("altaserver", metrics.ServiceName);
        await receiver.WaitAsync(request => request.Signal == "metrics" && request.Contains("http.server.request.duration"));

        var logs = await receiver.WaitAsync(request => request.Signal == "logs" && request.Contains("Telemetry:"));
        Assert.AreEqual("altaserver", logs.ServiceName);
    }

    [TestMethod]
    public async Task ASettingAnOwnerSignsAppliesAtOnceAndGoesToTheMachines()
    {
        await using var receiver = await OtlpReceiver.StartAsync();
        await using var fixture = await ServerFixture.StartAsync(telemetryInterval: Interval);
        using var owner = new TestDevice("Desktop");
        using var node = new TestDevice("Mac mini");
        var (setup, _) = await fixture.SetUpWithNodeAsync(owner, node);
        var organization = setup.Organization.Id;
        var pushed = Channel.CreateUnbounded<FleetSignedTelemetrySettings>();
        var (connection, welcome) = await fixture.ConnectAsync(node, connection => connection.SetEventHandler((fleetEvent, _) =>
        {
            if (fleetEvent.Event == FleetServerMethods.TelemetrySettings)
                pushed.Writer.TryWrite(fleetEvent.Payload!.Value.Deserialize(FleetServerJsonContext.Default.FleetSignedTelemetrySettings)!);
            return ValueTask.CompletedTask;
        }));
        await using var _ = connection;
        CollectionAssert.Contains(welcome.Features!.ToArray(), "telemetry");
        Assert.AreEqual(FleetTelemetrySources.Off, fixture.Server.Telemetry.Runtime.Plan.Source, "nothing is sent before a setting");

        var signed = owner.SignTelemetry(Settings(owner, organization, 1, receiver.Endpoint, headers: new() { ["x-api-key"] = "fleet-key" }));
        var set = await fixture.SendAsync(owner, HttpMethod.Put, "/api/v1/telemetry", new SetTelemetrySettingsRequest(signed));
        Assert.AreEqual(HttpStatusCode.OK, set.Status, set.Body.ToString());

        // The server applies it at once: its own spans go to the collector, with the headers of the setting.
        Assert.AreEqual((FleetTelemetrySources.Fleet, 1L), (fixture.Server.Telemetry.Runtime.Plan.Source, fixture.Server.Telemetry.Runtime.Plan.FleetSequence));
        Assert.AreEqual(HttpStatusCode.OK, (await fixture.SendAsync(owner, HttpMethod.Get, "/api/v1/devices")).Status);
        var exported = await receiver.WaitAsync(request => request.Signal == "traces" && request.ServiceName == "altaserver");
        Assert.AreEqual("fleet-key", exported.Headers["x-api-key"]);

        // The machines get it as signed, to check it themselves; one that connects later gets it at once.
        Assert.AreEqual(signed, await pushed.Reader.ReadAsync().AsTask().WaitAsync(Waits.Hang));
        var later = Channel.CreateUnbounded<FleetSignedTelemetrySettings>();
        var (controller, _) = await fixture.ConnectAsync(owner, connection => connection.SetEventHandler((fleetEvent, _) =>
        {
            if (fleetEvent.Event == FleetServerMethods.TelemetrySettings)
                later.Writer.TryWrite(fleetEvent.Payload!.Value.Deserialize(FleetServerJsonContext.Default.FleetSignedTelemetrySettings)!);
            return ValueTask.CompletedTask;
        }));
        await using var __ = controller;
        Assert.AreEqual(signed, await later.Reader.ReadAsync().AsTask().WaitAsync(Waits.Hang));

        var read = await fixture.SendAsync(owner, HttpMethod.Get, "/api/v1/telemetry");
        Assert.AreEqual(HttpStatusCode.OK, read.Status);
        Assert.AreEqual(signed, read.Read<TelemetrySettingsDto>().Settings);

        var audit = await fixture.SendAsync(owner, HttpMethod.Get, "/api/v1/audit");
        var record = audit.Read<ListResponse<AuditRecordDto>>().Items.Single(static item => item.Action == "telemetry.set" && item.Result == "success");
        StringAssert.Contains(record.Details, receiver.Endpoint.Authority);
        Assert.IsFalse(record.Details!.Contains("fleet-key", StringComparison.Ordinal), "the audit never holds a header");
    }

    [TestMethod]
    public async Task OnlyAnOwnersControllerSetsTheTelemetryWithASettingItSigned()
    {
        await using var fixture = await ServerFixture.StartAsync();
        using var owner = new TestDevice("Desktop");
        using var node = new TestDevice("Mac mini");
        using var outsider = new TestDevice("Outsider");
        var (setup, _) = await fixture.SetUpWithNodeAsync(owner, node);
        var organization = setup.Organization.Id;
        var endpoint = new Uri("https://otlp.example:4318");

        var byNode = await fixture.SendAsync(node, HttpMethod.Put, "/api/v1/telemetry", new SetTelemetrySettingsRequest(node.SignTelemetry(Settings(node, organization, 1, endpoint))));
        Assert.AreEqual((HttpStatusCode.Forbidden, FleetErrorCodes.Forbidden), (byNode.Status, byNode.Code), "a node sets nothing");
        Assert.AreEqual(HttpStatusCode.Forbidden, (await fixture.SendAsync(node, HttpMethod.Get, "/api/v1/telemetry")).Status, "nor reads the headers");

        var signedByAnother = await fixture.SendAsync(owner, HttpMethod.Put, "/api/v1/telemetry",
            new SetTelemetrySettingsRequest(outsider.SignTelemetry(Settings(owner, organization, 1, endpoint))));
        Assert.AreEqual((HttpStatusCode.BadRequest, "telemetry.invalid"), (signedByAnother.Status, signedByAnother.Code));
        var otherOrganization = await fixture.SendAsync(owner, HttpMethod.Put, "/api/v1/telemetry",
            new SetTelemetrySettingsRequest(owner.SignTelemetry(Settings(owner, "another-org", 1, endpoint))));
        Assert.AreEqual((HttpStatusCode.BadRequest, "telemetry.invalid"), (otherOrganization.Status, otherOrganization.Code));
        var serverHeadersOnDirect = await fixture.SendAsync(owner, HttpMethod.Put, "/api/v1/telemetry",
            new SetTelemetrySettingsRequest(owner.SignTelemetry(Settings(owner, organization, 1, endpoint)), new Dictionary<string, string> { ["x-key"] = "k" }));
        Assert.AreEqual((HttpStatusCode.BadRequest, "telemetry.invalid"), (serverHeadersOnDirect.Status, serverHeadersOnDirect.Code));

        var first = await fixture.SendAsync(owner, HttpMethod.Put, "/api/v1/telemetry", new SetTelemetrySettingsRequest(owner.SignTelemetry(Settings(owner, organization, 1, endpoint))));
        Assert.AreEqual(HttpStatusCode.OK, first.Status, first.Body.ToString());
        var replayed = await fixture.SendAsync(owner, HttpMethod.Put, "/api/v1/telemetry",
            new SetTelemetrySettingsRequest(owner.SignTelemetry(Settings(owner, organization, 1, endpoint) with { Enabled = false })));
        Assert.AreEqual((HttpStatusCode.Conflict, "telemetry.conflict"), (replayed.Status, replayed.Code), "a setting replaces only an older one");
    }

    [TestMethod]
    public async Task TheOwnersFileOfTheServerWinsOverTheFleet()
    {
        await using var receiver = await OtlpReceiver.StartAsync();
        using var root = new TempDirectory();
        File.WriteAllText(Path.Combine(root.Path, FleetLocalTelemetrySettings.FileName), """{ "enabled": false }""");
        await using var fixture = await ServerFixture.StartAsync(root, telemetryInterval: Interval);
        using var owner = new TestDevice("Desktop");
        using var node = new TestDevice("Mac mini");
        var (setup, _) = await fixture.SetUpWithNodeAsync(owner, node);

        var set = await fixture.SendAsync(owner, HttpMethod.Put, "/api/v1/telemetry", new SetTelemetrySettingsRequest(
            owner.SignTelemetry(Settings(owner, setup.Organization.Id, 1, receiver.Endpoint, FleetTelemetryRoutes.Server)), new Dictionary<string, string> { ["x-key"] = "k" }));
        Assert.AreEqual(HttpStatusCode.OK, set.Status, set.Body.ToString());
        Assert.AreEqual(FleetTelemetrySources.Off, fixture.Server.Telemetry.Runtime.Plan.Source, "the owner of the server turned telemetry off");
        Assert.IsNull(fixture.Server.Telemetry.Forward, "nor does it forward the machines'");
        Assert.AreEqual(HttpStatusCode.NotFound, await fixture.PostBytesAsync(node, "/otlp/v1/traces", [1, 2, 3]));

        // Refusing the fleet keeps the server's own telemetry off while the machines' are still forwarded.
        File.WriteAllText(Path.Combine(root.Path, FleetLocalTelemetrySettings.FileName), """{ "fleet": false }""");
        var again = await fixture.SendAsync(owner, HttpMethod.Put, "/api/v1/telemetry", new SetTelemetrySettingsRequest(
            owner.SignTelemetry(Settings(owner, setup.Organization.Id, 2, receiver.Endpoint, FleetTelemetryRoutes.Server)), new Dictionary<string, string> { ["x-key"] = "k" }));
        Assert.AreEqual(HttpStatusCode.OK, again.Status, again.Body.ToString());
        Assert.AreEqual(FleetTelemetrySources.Off, fixture.Server.Telemetry.Runtime.Plan.Source);
        Assert.IsNotNull(fixture.Server.Telemetry.Forward);
        Assert.AreEqual(0, receiver.Requests.Count(static request => request.ServiceName == "altaserver"), "the server sent nothing of its own");
    }

    [TestMethod]
    public async Task TheServerForwardsTheOtlpOfItsMachinesWithTheHeadersOnlyItHolds()
    {
        await using var receiver = await OtlpReceiver.StartAsync();
        await using var fixture = await ServerFixture.StartAsync(telemetryInterval: Interval);
        using var owner = new TestDevice("Desktop");
        using var node = new TestDevice("Mac mini");
        using var waiting = new TestDevice("Laptop");
        var (setup, _) = await fixture.SetUpWithNodeAsync(owner, node);
        await fixture.EnrollAsync(owner, waiting);
        var pushed = Channel.CreateUnbounded<FleetSignedTelemetrySettings>();
        var (connection, _) = await fixture.ConnectAsync(node, connection => connection.SetEventHandler((fleetEvent, _) =>
        {
            if (fleetEvent.Event == FleetServerMethods.TelemetrySettings)
                pushed.Writer.TryWrite(fleetEvent.Payload!.Value.Deserialize(FleetServerJsonContext.Default.FleetSignedTelemetrySettings)!);
            return ValueTask.CompletedTask;
        }));
        await using var _ = connection;

        var signed = owner.SignTelemetry(Settings(owner, setup.Organization.Id, 1, receiver.Endpoint, FleetTelemetryRoutes.Server,
            [FleetTelemetrySignals.Traces, FleetTelemetrySignals.Metrics]));
        var set = await fixture.SendAsync(owner, HttpMethod.Put, "/api/v1/telemetry",
            new SetTelemetrySettingsRequest(signed, new Dictionary<string, string> { ["x-api-key"] = "server-secret" }));
        Assert.AreEqual(HttpStatusCode.OK, set.Status, set.Body.ToString());
        var received = await pushed.Reader.ReadAsync().AsTask().WaitAsync(Waits.Hang);
        Assert.IsFalse(received.Payload.Contains("server-secret", StringComparison.Ordinal), "the headers of the server route never go to the machines");

        byte[] body = [0x0A, 0x03, 0x01, 0x02, 0x03, 0x42];
        Assert.AreEqual(HttpStatusCode.Unauthorized, await fixture.PostBytesAsync(null, "/otlp/v1/traces", body), "an unsigned request is refused");
        Assert.AreEqual(HttpStatusCode.Forbidden, await fixture.PostBytesAsync(waiting, "/otlp/v1/traces", body), "a device that waits for its approval is refused");
        Assert.IsFalse(receiver.Requests.Any(request => request.Body.SequenceEqual(body)), "nothing refused reached the collector");

        Assert.AreEqual(HttpStatusCode.OK, await fixture.PostBytesAsync(node, "/otlp/v1/traces", body));
        var forwarded = await receiver.WaitAsync(request => request.Body.SequenceEqual(body));
        Assert.AreEqual(("/v1/traces", "server-secret"), (forwarded.Path, forwarded.Headers["x-api-key"]));
        Assert.IsFalse(forwarded.Headers.ContainsKey("alta-signature"), "the signature of the machine stays at the server");
        Assert.AreEqual(HttpStatusCode.NotFound, await fixture.PostBytesAsync(node, "/otlp/v1/logs", body), "the fleet does not send its logs");

        // The collector's refusal comes back to the machine, whose exporter retries.
        receiver.Status = 503;
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, await fixture.PostBytesAsync(node, "/otlp/v1/metrics", body));
    }

    private static FleetTelemetrySettings Settings(TestDevice signer, string organizationId, long sequence, Uri endpoint, string route = FleetTelemetryRoutes.Direct,
        IReadOnlyList<string>? signals = null, Dictionary<string, string>? headers = null)
        => new(1, organizationId, sequence, true, endpoint.ToString().TrimEnd('/'), FleetTelemetryProtocols.HttpProtobuf, route,
            signals ?? [FleetTelemetrySignals.Traces, FleetTelemetrySignals.Metrics, FleetTelemetrySignals.Logs], headers, "test", signer.DeviceId, DateTimeOffset.UtcNow);
}
