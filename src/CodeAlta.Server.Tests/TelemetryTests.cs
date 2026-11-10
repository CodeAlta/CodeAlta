using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Fleet.Tests.Otlp;
using CodeAlta.Server.Api;
using CodeAlta.Server.Control;
using CodeAlta.Server.Identity;
using CodeAlta.Server.Services;
using CodeAlta.Server.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

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
        Assert.AreEqual(signed, await pushed.Reader.ReadAsync().AsTask().WithinAsync(Waits.Hang));
        var later = Channel.CreateUnbounded<FleetSignedTelemetrySettings>();
        var (controller, _) = await fixture.ConnectAsync(owner, connection => connection.SetEventHandler((fleetEvent, _) =>
        {
            if (fleetEvent.Event == FleetServerMethods.TelemetrySettings)
                later.Writer.TryWrite(fleetEvent.Payload!.Value.Deserialize(FleetServerJsonContext.Default.FleetSignedTelemetrySettings)!);
            return ValueTask.CompletedTask;
        }));
        await using var __ = controller;
        Assert.AreEqual(signed, await later.Reader.ReadAsync().AsTask().WithinAsync(Waits.Hang));

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
        var received = await pushed.Reader.ReadAsync().AsTask().WithinAsync(Waits.Hang);
        Assert.IsFalse(received.Payload.Contains("server-secret", StringComparison.Ordinal), "the headers of the server route never go to the machines");
        Assert.IsFalse(fixture.DatabaseHolds("server-secret"), "the headers of the server route are sealed in the database");
        var read = await fixture.SendAsync(owner, HttpMethod.Get, "/api/v1/telemetry");
        Assert.IsFalse(read.Body.ToString().Contains("server-secret", StringComparison.Ordinal), "no API gives the value back, not even to the owner");
        Assert.AreEqual(string.Empty, read.Read<TelemetrySettingsDto>().ServerHeaders!["x-api-key"], "its name only");

        // The owner changes the setting and keeps the header by its name: the server keeps its value.
        signed = owner.SignTelemetry(Settings(owner, setup.Organization.Id, 2, receiver.Endpoint, FleetTelemetryRoutes.Server,
            [FleetTelemetrySignals.Traces, FleetTelemetrySignals.Metrics]));
        var unknown = await fixture.SendAsync(owner, HttpMethod.Put, "/api/v1/telemetry", new SetTelemetrySettingsRequest(signed, null, ["x-other"]));
        Assert.AreEqual((HttpStatusCode.BadRequest, CoordinatorErrorCodes.TelemetryInvalid), (unknown.Status, unknown.Code), "only a header held is kept");
        var kept = await fixture.SendAsync(owner, HttpMethod.Put, "/api/v1/telemetry", new SetTelemetrySettingsRequest(signed, null, ["X-Api-Key"]));
        Assert.AreEqual(HttpStatusCode.OK, kept.Status, kept.Body.ToString());
        await pushed.Reader.ReadAsync().AsTask().WithinAsync(Waits.Hang);

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

    [TestMethod]
    public async Task TheServerReadsAtMostTheLimitOfACollectorsAnswer_AndGivesUpOnOneThatStalls()
    {
        await using var fixture = await ServerFixture.StartAsync(telemetryInterval: Interval);
        using var owner = new TestDevice("Desktop");
        using var node = new TestDevice("Mac mini");
        var (setup, _) = await fixture.SetUpWithNodeAsync(owner, node);
        var signed = owner.SignTelemetry(Settings(owner, setup.Organization.Id, 1, new Uri("http://127.0.0.1:9"), FleetTelemetryRoutes.Server, [FleetTelemetrySignals.Traces]));
        var set = await fixture.SendAsync(owner, HttpMethod.Put, "/api/v1/telemetry", new SetTelemetrySettingsRequest(signed));
        Assert.AreEqual(HttpStatusCode.OK, set.Status, set.Body.ToString());

        // A collector whose answer never ends: the server stops reading past the limit and relays no body.
        var endless = new AnswerStream(stall: false);
        using (var collector = new CollectorHandler(endless))
        using (var forwarder = new OtlpForwarder(fixture.Server.Telemetry, fixture.GetService<RequestAuthenticator>(), NullLogger.Instance, collector, TimeSpan.FromSeconds(10)))
        {
            var (context, aborted) = SignedOtlpContext(fixture, node, [0x0A, 0x01]);
            using var _ = aborted;
            await forwarder.HandleAsync(context, FleetTelemetrySignals.Traces).WithinAsync(Waits.Hang);
            Assert.AreEqual(StatusCodes.Status200OK, context.Response.StatusCode);
            Assert.AreEqual(0L, context.Response.Body.Length, "an answer past the limit is not relayed");
            Assert.IsTrue(endless.BytesRead <= 128 * 1024, $"the server read {endless.BytesRead} bytes of an answer limited to 64 KiB");
        }

        // A collector that sends its headers, then nothing: the timeout covers the body too, and the machine gets a 502 to retry.
        var stalled = new AnswerStream(stall: true);
        using (var collector = new CollectorHandler(stalled))
        using (var forwarder = new OtlpForwarder(fixture.Server.Telemetry, fixture.GetService<RequestAuthenticator>(), NullLogger.Instance, collector, TimeSpan.FromMilliseconds(500)))
        {
            var (context, aborted) = SignedOtlpContext(fixture, node, [0x0A, 0x02]);
            using var _ = aborted;
            try
            {
                await forwarder.HandleAsync(context, FleetTelemetrySignals.Traces).WithinAsync(TestTime.Seconds(10));
            }
            finally
            {
                // Ends the request of a forwarder that would still wait.
                await aborted.CancelAsync();
            }

            Assert.AreEqual(StatusCodes.Status502BadGateway, context.Response.StatusCode);
            Assert.AreEqual(0L, context.Response.Body.Length);
        }
    }

    [TestMethod]
    public async Task TwoSettingsSetAtOnceLeaveTheNewerOneAppliedByTheServer()
    {
        await using var fixture = await ServerFixture.StartAsync(telemetryInterval: Interval);
        using var owner = new TestDevice("Desktop");
        var setup = await fixture.SetUpAsync(owner);
        var (store, hold) = HoldFirstTelemetryStore.Wrap(fixture.Server.Store);
        var service = new TelemetrySettingsService(store, fixture.GetService<PresenceRegistry>(), fixture.Server.Telemetry, fixture.Clock, NullLogger.Instance);
        var actor = (await fixture.Server.Store.GetDeviceAsync(owner.DeviceId))!;
        var origin = new RequestOrigin(IPAddress.Loopback, "test");
        SetTelemetrySettingsRequest Request(long sequence)
            => new(owner.SignTelemetry(Settings(owner, setup.Organization.Id, sequence, new Uri($"http://127.0.0.1:{10_000 + sequence}"), FleetTelemetryRoutes.Server,
                [FleetTelemetrySignals.Traces])));

        // The first setting is stored, then held before the server applies it; the second one is set meanwhile.
        var first = service.SetAsync(actor, Request(1), origin, CancellationToken.None);
        await hold.Stored.Task.WithinAsync(Waits.Hang);
        var second = service.SetAsync(actor, Request(2), origin, CancellationToken.None);
        await Task.WhenAny(second, Task.Delay(300));
        hold.Release();
        await first.WithinAsync(Waits.Hang);
        await second.WithinAsync(Waits.Hang);

        Assert.AreEqual(2L, (await fixture.Server.Store.GetTelemetrySettingsAsync(setup.Organization.Id))?.Sequence);
        Assert.AreEqual(10_002, fixture.Server.Telemetry.Forward?.Endpoint.Port, "the server applies the setting it stored last, not an older one that applied late");
    }

    /// <summary>A store whose first <c>SetTelemetrySettingsAsync</c> writes, then holds its answer until the test releases it.</summary>
    public class HoldFirstTelemetryStore : System.Reflection.DispatchProxy
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private ICoordinatorStore _inner = null!;
        private int _calls;

        /// <summary>Gets the task that completes once the first setting is written.</summary>
        public TaskCompletionSource Stored { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal static (ICoordinatorStore Store, HoldFirstTelemetryStore Hold) Wrap(ICoordinatorStore inner)
        {
            var store = Create<ICoordinatorStore, HoldFirstTelemetryStore>();
            var hold = (HoldFirstTelemetryStore)(object)store;
            hold._inner = inner;
            return (store, hold);
        }

        /// <summary>Lets the first setting's answer go.</summary>
        public void Release() => _release.TrySetResult();

        /// <inheritdoc />
        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            try
            {
                var result = targetMethod.Invoke(_inner, args);
                if (targetMethod.Name == nameof(ICoordinatorStore.SetTelemetrySettingsAsync) && Interlocked.Increment(ref _calls) == 1)
                    return new ValueTask<bool>(HoldAsync((ValueTask<bool>)result!));
                return result;
            }
            catch (System.Reflection.TargetInvocationException exception) when (exception.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(exception.InnerException);
                throw;
            }
        }

        private async Task<bool> HoldAsync(ValueTask<bool> stored)
        {
            var result = await stored;
            Stored.TrySetResult();
            await _release.Task;
            return result;
        }
    }

    // A request of the OTLP endpoint, signed by a device as its exporter signs them, with a response body the test reads.
    private static (DefaultHttpContext Context, CancellationTokenSource Aborted) SignedOtlpContext(ServerFixture fixture, TestDevice device, byte[] body)
    {
        const string Path = "/otlp/v1/traces";
        var aborted = new CancellationTokenSource();
        var context = new DefaultHttpContext { RequestAborted = aborted.Token };
        context.Request.Method = "POST";
        context.Request.Path = Path;
        context.Request.ContentType = "application/x-protobuf";
        context.Request.Body = new MemoryStream(body);
        context.Request.ContentLength = body.Length;
        context.Response.Body = new MemoryStream();
        var time = fixture.Clock.Now.ToUnixTimeMilliseconds();
        var nonce = FleetSignedRequest.CreateNonce();
        context.Request.Headers[FleetSignedRequest.DeviceHeader] = device.DeviceId;
        context.Request.Headers[FleetSignedRequest.TimestampHeader] = time.ToString(System.Globalization.CultureInfo.InvariantCulture);
        context.Request.Headers[FleetSignedRequest.NonceHeader] = nonce;
        context.Request.Headers[FleetSignedRequest.SignatureHeader] = device.Sign(FleetSignedRequest.GetSignedBytes("POST", Path, time, nonce, FleetSignedRequest.HashBody(body), device.DeviceId));
        return (context, aborted);
    }

    // A collector that answers 200 with the stream of the test as its body.
    private sealed class CollectorHandler(AnswerStream answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(answer) });
    }

    // The body of a collector's answer: 32 MiB of zeros, or nothing at all until it is cancelled. Counts what was read.
    private sealed class AnswerStream(bool stall) : Stream
    {
        private const long Size = 32L * 1024 * 1024;
        private long _read;

        public long BytesRead => Interlocked.Read(ref _read);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (stall) await Task.Delay(Timeout.Infinite, cancellationToken);
            var count = (int)Math.Min(buffer.Length, Size - BytesRead);
            buffer.Span[..count].Clear();
            Interlocked.Add(ref _read, count);
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static FleetTelemetrySettings Settings(TestDevice signer, string organizationId, long sequence, Uri endpoint, string route = FleetTelemetryRoutes.Direct,
        IReadOnlyList<string>? signals = null, Dictionary<string, string>? headers = null)
        => new(1, organizationId, sequence, true, endpoint.ToString().TrimEnd('/'), FleetTelemetryProtocols.HttpProtobuf, route,
            signals ?? [FleetTelemetrySignals.Traces, FleetTelemetrySignals.Metrics, FleetTelemetrySignals.Logs], headers, "test", signer.DeviceId, DateTimeOffset.UtcNow);
}
