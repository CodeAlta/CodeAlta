using System.Buffers.Text;
using System.Diagnostics;
using CodeAlta.Catalog;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Server;
using CodeAlta.Fleet.Tests.Otlp;
using CodeAlta.Server.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeAlta.Node.Tests;

/// <summary>
/// The telemetry of a node (<c>doc/fleet/observability.md</c>): its spans, metrics and log records over OTLP, its own
/// file that wins over the fleet, the fleet's setting it applies only once a controller device of its organization signed
/// it, and the server route, where it sends to its server signed with its device key.
/// </summary>
[TestClass]
public sealed class NodeTelemetryTests
{
    private const string Organization = "org-1";
    private const string Owner = "person-1";
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan Patience = TestTime.Seconds(30);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task ARunningNodeExportsToTheCollectorOfItsOwnFile()
    {
        await using var receiver = await OtlpReceiver.StartAsync();
        using var root = new NodeTestRoot();
        var fleetRoot = FleetRoot(root);
        Directory.CreateDirectory(fleetRoot);
        File.WriteAllText(Path.Combine(fleetRoot, FleetLocalTelemetrySettings.FileName),
            $$"""{ "endpoint": "{{receiver.Endpoint}}", "signals": ["traces", "metrics", "logs"], "environment": "lab" }""");
        var started = new TaskCompletionSource<Action>(TaskCreationOptions.RunContinuationsAsynchronously);
        var node = new NodeApplication(TextWriter.Null, TextWriter.Null)
        {
            InitializeLogging = false,
            ConfigureHost = root.Isolate,
            ConfigureTelemetry = static options => options with { Variable = static _ => null, ExportInterval = Interval },
            Started = (_, stop) => started.TrySetResult(stop),
            ServiceEnvironment = static () => null,
        };
        var running = node.RunAsync(["run", "--data-root", root.DataRoot, "--no-listen", "--name", "test-node"]);
        var stop = await started.Task.WithinAsync(Patience, TestContext.CancellationToken);
        try
        {
            var metrics = await receiver.WaitAsync(static request => request.Signal == "metrics" && request.Contains("codealta.node.sessions.working"));
            Assert.AreEqual("altanode", metrics.ServiceName);
            using (var key = DeviceIdentity.LoadOrCreate(fleetRoot)) Assert.AreEqual(key.DeviceId, metrics.Resource["service.instance.id"]);
            Assert.AreEqual(("lab", Environment.MachineName), (metrics.Resource["deployment.environment.name"], metrics.Resource["host.name"]));
            Assert.IsTrue(metrics.Contains("codealta.node.devices.connected") && metrics.Contains("codealta.node.servers.online"));
            var logs = await receiver.WaitAsync(static request => request.Signal == "logs" && request.Contains("Telemetry:"));
            Assert.AreEqual("altanode", logs.ServiceName);
        }
        finally
        {
            stop();
            Assert.AreEqual(NodeExitCodes.Success, await running.WithinAsync(Patience, TestContext.CancellationToken));
        }
    }

    [TestMethod]
    public async Task TheLogRecordsExported_HaveTheSecretsOfTheMachineMasked_InTheirTextAndTheirException()
    {
        const string secret = "sk-node-7c1e9a44d2";
        await using var receiver = await OtlpReceiver.StartAsync();
        using var root = new NodeTestRoot();
        var fleetRoot = FleetRoot(root);
        Directory.CreateDirectory(fleetRoot);
        File.WriteAllText(Path.Combine(fleetRoot, FleetLocalTelemetrySettings.FileName), $$"""{ "endpoint": "{{receiver.Endpoint}}", "signals": ["logs"] }""");
        Exception failure;
        try
        {
            throw new InvalidOperationException($"failed with {secret}", new IOException($"inner {secret}"));
        }
        catch (InvalidOperationException exception)
        {
            failure = exception;
        }

        using (var telemetry = new NodeTelemetry(Options(fleetRoot) with { SecretMasker = new CodeAlta.Agent.Runtime.AgentSecretMasker(() => [secret]) }))
        {
            telemetry.LogWriter.Write("CodeAlta.Node", LogLevel.Warning, $"A record that names {secret}", failure);
            var logs = await receiver.WaitAsync(static request => request.Signal == "logs" && request.Contains("A record that names"));

            Assert.IsFalse(logs.Contains(secret), "No secret of the machine goes to the collector.");
            Assert.IsTrue(logs.Contains("A record that names ***") && logs.Contains("failed with ***") && logs.Contains("inner ***"));
            Assert.IsTrue(logs.Contains("InvalidOperationException"), "The type of the exception is still told.");
        }
    }

    [TestMethod]
    public async Task ANodeAppliesOnlyASettingAControllerOfItsOrganizationSigned()
    {
        await using var receiver = await OtlpReceiver.StartAsync();
        using var root = new NodeTestRoot();
        var fleetRoot = FleetRoot(root);
        using var desktop = DeviceIdentity.LoadOrCreate(Path.Combine(root.Root, "desktop"));
        using var outsider = DeviceIdentity.LoadOrCreate(Path.Combine(root.Root, "outsider"));
        using var nodeKey = DeviceIdentity.LoadOrCreate(fleetRoot);
        var link = Link(fleetRoot, nodeKey);
        var devices = DeviceList(desktop, nodeKey);

        using (var telemetry = new NodeTelemetry(Options(fleetRoot)))
        {
            Assert.AreEqual(FleetTelemetrySources.Off, telemetry.Plan.Source, "nothing is sent before a setting");

            // A key outside the list, a node of the list, a signature that does not match, another organization: refused.
            Assert.IsFalse(telemetry.Accept(link, Sign(outsider, Settings(outsider, 1, receiver.Endpoint)), devices, Organization, out var reason));
            StringAssert.Contains(reason, "not signed by a controller device");
            Assert.IsFalse(telemetry.Accept(link, Sign(nodeKey, Settings(nodeKey, 1, receiver.Endpoint)), devices, Organization, out _));
            var genuine = Sign(desktop, Settings(desktop, 2, receiver.Endpoint));
            Assert.IsFalse(telemetry.Accept(link, genuine with { Signature = "" }, devices, Organization, out _));
            Assert.IsFalse(telemetry.Accept(link, genuine with { Payload = genuine.Payload.Replace("127.0.0.1", "10.0.0.66", StringComparison.Ordinal) }, devices, Organization, out _),
                "a server cannot redirect where the machines send");
            Assert.IsFalse(telemetry.Accept(link, genuine, devices, "org-2", out _));
            Assert.AreEqual(FleetTelemetrySources.Off, telemetry.Plan.Source);

            Assert.IsTrue(telemetry.Accept(link, genuine, devices, Organization, out reason), reason);
            Assert.AreEqual((FleetTelemetrySources.Fleet, 2L), (telemetry.Plan.Source, telemetry.Plan.FleetSequence));
            Assert.IsTrue(telemetry.Accept(link, genuine, devices, Organization, out _), "the same setting sent again changes nothing");
            Assert.IsFalse(telemetry.Accept(link, Sign(desktop, Settings(desktop, 1, receiver.Endpoint) with { Enabled = false }), devices, Organization, out reason),
                "an older setting a server replays is refused");
            StringAssert.Contains(reason, "not newer");

            // Spans of the node API and of runs reach the fleet's collector, without content whatever the environment asked.
            using (var runs = new ActivitySource(CodeAlta.Agent.Telemetry.AgentTelemetry.SourceName))
            using (var run = runs.StartActivity("invoke_agent CodeAlta"))
            {
                run?.SetTag("gen_ai.input.messages", "[the secret prompt]");
                run?.SetTag("gen_ai.conversation.id", "session-1");
            }

            var call = telemetry.Metrics.StartNodeApiRequest("sessions.list", desktop.DeviceId);
            telemetry.Metrics.EndNodeApiRequest(call, "sessions.list", Stopwatch.GetTimestamp(), null);
            var spans = await receiver.WaitAsync(static request => request.Signal == "traces" && request.Contains("node_api sessions.list"));
            Assert.AreEqual(("altanode", Organization), (spans.ServiceName, spans.Resource["codealta.fleet.organization.id"]));
            Assert.AreEqual("production", spans.Resource["deployment.environment.name"]);
            Assert.IsFalse(spans.Contains(desktop.DeviceId), "a device is named in short");
            var run2 = await receiver.WaitAsync(static request => request.Signal == "traces" && request.Contains("session-1"));
            Assert.IsFalse(run2.Contains("the secret prompt"), "no content goes to the fleet's collector");

            // The node's log records, with their category and exception.
            telemetry.LogWriter.Write("CodeAlta.Node", LogLevel.Warning, "A test record of the node", new InvalidOperationException("the failure"));
            var logs = await receiver.WaitAsync(static request => request.Signal == "logs" && request.Contains("A test record of the node"));
            Assert.IsTrue(logs.Contains("CodeAlta.Node") && logs.Contains("the failure") && logs.Contains("InvalidOperationException"));
        }

        // The setting is kept: the node applies it at its next start, before it reaches a server.
        using (var restarted = new NodeTelemetry(Options(fleetRoot)))
        {
            Assert.AreEqual((FleetTelemetrySources.Fleet, 2L), (restarted.Plan.Source, restarted.Plan.FleetSequence));

            // The owner of the machine refuses the fleet's setting, then names a collector of their own, then turns everything off.
            var file = Path.Combine(fleetRoot, FleetLocalTelemetrySettings.FileName);
            File.WriteAllText(file, """{ "fleet": false }""");
            restarted.Reapply();
            Assert.AreEqual(FleetTelemetrySources.Off, restarted.Plan.Source);
            File.WriteAllText(file, """{ "endpoint": "http://127.0.0.1:4318" }""");
            restarted.Reapply();
            Assert.AreEqual((FleetTelemetrySources.Local, "http://127.0.0.1:4318/"), (restarted.Plan.Source, restarted.Plan.Endpoint?.ToString()));
            File.WriteAllText(file, """{ "enabled": false, "endpoint": "http://127.0.0.1:4318" }""");
            restarted.Reapply();
            Assert.AreEqual(FleetTelemetrySources.Off, restarted.Plan.Source);
            Assert.IsTrue(restarted.Accept(link, Sign(desktop, Settings(desktop, 3, receiver.Endpoint)), devices, Organization, out _));
            Assert.AreEqual(FleetTelemetrySources.Off, restarted.Plan.Source, "a newer setting of the fleet does not turn on what the owner turned off");
        }
    }

    [TestMethod]
    public void SettingsAcceptedAtTheSameTimeNeverLeaveAnOlderOneHeldOrKept()
    {
        using var root = new NodeTestRoot();
        var fleetRoot = FleetRoot(root);
        using var desktop = DeviceIdentity.LoadOrCreate(Path.Combine(root.Root, "desktop"));
        using var nodeKey = DeviceIdentity.LoadOrCreate(fleetRoot);
        var link = Link(fleetRoot, nodeKey);
        var devices = DeviceList(desktop, nodeKey);
        const int Threads = 8;
        const int Rounds = 25;
        // Signed beforehand: the race is between the checks, not the signatures. Each setting names its own collector,
        // so that the plan applied says which one won (a plan that only renumbers the one applied changes nothing).
        static Uri Endpoint(long sequence) => new($"http://127.0.0.1:{10_000 + sequence}");
        var signed = Enumerable.Range(1, Threads * Rounds).Select(sequence => Sign(desktop, Settings(desktop, sequence, Endpoint(sequence)))).ToArray();

        using var telemetry = new NodeTelemetry(Options(fleetRoot) with { ExportInterval = TimeSpan.FromHours(1) });
        for (var round = 0; round < Rounds; round++)
        {
            // The pushes of a round come at once, in any order: whatever the order, the newest one stays.
            using var start = new Barrier(Threads);
            var accepts = Enumerable.Range(0, Threads).Select(index => Task.Factory.StartNew(() =>
            {
                start.SignalAndWait(Patience);
                telemetry.Accept(link, signed[round * Threads + (Threads - 1 - index)], devices, Organization, out _);
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
            Task.WaitAll(accepts, Patience);

            var newest = (long)(round + 1) * Threads;
            Assert.AreEqual((newest, Endpoint(newest).Port), (telemetry.Plan.FleetSequence, telemetry.Plan.Endpoint?.Port), $"round {round}: the setting applied is the newest one");
            var kept = System.Text.Json.JsonSerializer.Deserialize(File.ReadAllBytes(Path.Combine(new FleetServerLinkStore(fleetRoot).LinkFolder(link.Id), NodeTelemetry.SettingsFileName)),
                FleetTelemetryJsonContext.Default.FleetSignedTelemetrySettings);
            Assert.AreEqual(signed[newest - 1].Payload, kept?.Payload, $"round {round}: the setting kept on disk is the newest one");
        }
    }

    [TestMethod]
    public async Task ANodeSendsThroughItsServerWhenTheFleetSaysSo()
    {
        await using var receiver = await OtlpReceiver.StartAsync();
        using var root = new NodeTestRoot();
        var token = TestContext.CancellationToken;
        await using var server = await CoordinatorServer.StartAsync(new CoordinatorServerOptions
        {
            DataRoot = Path.Combine(root.Root, "server"), Urls = ["http://127.0.0.1:0"], ConfigureLogging = static _ => { }, EnvironmentVariable = static _ => null,
        }, token);
        var address = server.Addresses.First(static uri => uri.Scheme == "http");
        using var desktopKey = DeviceIdentity.LoadOrCreate(Path.Combine(root.Root, "desktop-fleet"));
        using var desktop = new FleetServerClient(address, desktopKey);
        var setup = await desktop.SetUpAsync(server.SetupCode!.Code, "Alex", "Desktop", "Home", token);
        var enrollment = await desktop.CreateEnrollmentTokenAsync(null, "test-node", token);

        var started = new TaskCompletionSource<Action>(TaskCreationOptions.RunContinuationsAsynchronously);
        var node = new NodeApplication(TextWriter.Null, TextWriter.Null)
        {
            InitializeLogging = false,
            ConfigureHost = root.Isolate,
            ConfigureTelemetry = static options => options with { Variable = static _ => null, ExportInterval = Interval },
            Started = (_, stop) => started.TrySetResult(stop),
            ConfigureConnector = static options => new FleetConnectorOptions
            {
                ServerAddress = options.ServerAddress, Identity = options.Identity, OutboxPath = options.OutboxPath, Client = options.Client, Source = options.Source,
                PublishesSpace = options.PublishesSpace, WatchDirectory = options.WatchDirectory, PendingApprovalRetry = TimeSpan.FromMilliseconds(200),
                MinimumBackoff = TimeSpan.FromMilliseconds(100), MaximumBackoff = TimeSpan.FromSeconds(1), PollInterval = TimeSpan.FromMilliseconds(500),
                DeviceListPinPath = options.DeviceListPinPath, TelemetrySettings = options.TelemetrySettings, Metrics = options.Metrics,
            },
            ServiceEnvironment = static () => null,
        };
        var running = node.RunAsync(["run", "--data-root", root.DataRoot, "--no-listen", "--name", "test-node"]);
        var stop = await started.Task.WithinAsync(Patience, token);
        try
        {
            var joined = await new NodeApplication(TextWriter.Null, TextWriter.Null) { InitializeLogging = false, ServiceEnvironment = static () => null }
                .RunAsync(["join", address.ToString(), "--token", enrollment.Token, "--data-root", root.DataRoot, "--name", "test-node", "--no-wait"]);
            Assert.AreEqual(NodeExitCodes.Success, joined);
            var deadline = DateTime.UtcNow + Patience;
            string? pending = null;
            while (pending is null)
            {
                pending = (await desktop.ListDevicesAsync(token)).FirstOrDefault(static device => device.Status == "pending")?.Id;
                if (pending is not null) break;
                if (DateTime.UtcNow > deadline) Assert.Fail("The node did not enroll.");
                await Task.Delay(100, token);
            }

            await desktop.ApproveAsync(pending, FleetDeviceKinds.Node, token);
            while (!(await desktop.ListMachinesAsync(token)).Any(static machine => machine.Online))
            {
                if (DateTime.UtcNow > deadline) Assert.Fail("The node did not connect.");
                await Task.Delay(100, token);
            }

            // The owner sends the fleet through the server, whose header the node never holds.
            await desktop.SetTelemetryAsync(new FleetTelemetryChange(true, receiver.Endpoint.ToString().TrimEnd('/'), FleetTelemetryProtocols.HttpProtobuf,
                FleetTelemetryRoutes.Server, [FleetTelemetrySignals.Traces, FleetTelemetrySignals.Metrics], null, null),
                new Dictionary<string, string> { ["x-api-key"] = "server-secret" }, token);

            var forwarded = await receiver.WaitAsync(static request => request.Signal == "metrics" && request.ServiceName == "altanode");
            Assert.AreEqual("server-secret", forwarded.Headers["x-api-key"], "the server adds the header it holds");
            Assert.AreEqual(setup.Organization.Id, forwarded.Resource["codealta.fleet.organization.id"]);
            Assert.AreEqual(pending, forwarded.Resource["service.instance.id"]);
            Assert.IsFalse(File.ReadAllText(Path.Combine(new FleetServerLinkStore(FleetRoot(root)).LinkFolder(new FleetServerLinkStore(FleetRoot(root)).List().Single().Id),
                NodeTelemetry.SettingsFileName)).Contains("server-secret", StringComparison.Ordinal), "the node never received the server's header");
        }
        finally
        {
            stop();
            Assert.AreEqual(NodeExitCodes.Success, await running.WithinAsync(Patience, token));
        }
    }

    private static string FleetRoot(NodeTestRoot root) => new CatalogOptions { GlobalRoot = root.DataRoot, StateRoot = root.DataRoot }.FleetRoot;

    private static NodeTelemetryOptions Options(string fleetRoot)
        => new() { FleetRoot = fleetRoot, Version = "4.0.0", Variable = static name => name == "OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT" ? "true" : null, ExportInterval = Interval };

    private static FleetServerLink Link(string fleetRoot, DeviceIdentity node)
    {
        var store = new FleetServerLinkStore(fleetRoot);
        var now = DateTimeOffset.UtcNow;
        var link = new FleetServerLink(FleetServerLinkStore.NewId(), "http://127.0.0.1:2586", Organization, "Home", node.DeviceId, FleetDeviceKinds.Node,
            FleetServerLinkStates.Approved, now, now);
        store.Save(link);
        return link;
    }

    private static FleetDeviceListState DeviceList(DeviceIdentity desktop, DeviceIdentity node)
    {
        var time = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var desktopKey = Convert.ToBase64String(desktop.ExportSubjectPublicKeyInfo());
        var first = SignEntry(desktop, new FleetDeviceListEntryPayload(1, null, 1, null, FleetDeviceListOperations.Add, desktop.DeviceId, desktopKey, FleetDeviceKinds.Controller,
            null, desktop.DeviceId, time));
        var second = SignEntry(desktop, new FleetDeviceListEntryPayload(1, Organization, 2, FleetDeviceListEntries.Hash(first), FleetDeviceListOperations.Add, node.DeviceId,
            Convert.ToBase64String(node.ExportSubjectPublicKeyInfo()), FleetDeviceKinds.Node, Owner, desktop.DeviceId, time));
        return FleetDeviceListVerifier.VerifyOrThrow([first, second], Organization);
    }

    private static FleetSignedDeviceListEntry SignEntry(DeviceIdentity signer, FleetDeviceListEntryPayload payload)
    {
        var text = FleetDeviceListEntries.SerializePayload(payload);
        return new FleetSignedDeviceListEntry(text, Base64Url.EncodeToString(signer.SignData(FleetDeviceListEntries.GetSignedBytes(text))));
    }

    private static FleetTelemetrySettings Settings(DeviceIdentity signer, long sequence, Uri endpoint)
        => new(1, Organization, sequence, true, endpoint.ToString().TrimEnd('/'), FleetTelemetryProtocols.HttpProtobuf, FleetTelemetryRoutes.Direct,
            [FleetTelemetrySignals.Traces, FleetTelemetrySignals.Metrics, FleetTelemetrySignals.Logs], null, "production", signer.DeviceId, DateTimeOffset.UtcNow);

    private static FleetSignedTelemetrySettings Sign(DeviceIdentity signer, FleetTelemetrySettings settings)
    {
        var text = FleetTelemetrySettingsDocuments.Serialize(settings);
        return new FleetSignedTelemetrySettings(text, Base64Url.EncodeToString(signer.SignData(FleetTelemetrySettingsDocuments.GetSignedBytes(text))));
    }
}
