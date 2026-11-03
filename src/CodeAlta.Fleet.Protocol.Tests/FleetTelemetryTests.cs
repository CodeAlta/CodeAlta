using System.Buffers.Text;
using System.Security.Cryptography;

namespace CodeAlta.Fleet.Protocol.Tests;

/// <summary>
/// The telemetry setting of a fleet: signed by a controller device of the organization, verified by machines against
/// the device list they hold, and resolved with the machine's own setting and its environment, which win over it.
/// </summary>
[TestClass]
public sealed class FleetTelemetryTests
{
    private const string Organization = "org-1";
    private const string Owner = "person-1";
    private static readonly DateTimeOffset Time = new(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void ASettingSignedByAControllerOfTheListVerifies()
    {
        using var desktop = new Key();
        using var node = new Key();
        var list = List(desktop, node);
        var signed = desktop.Sign(Settings(desktop.Id));

        Assert.IsTrue(FleetTelemetrySettingsDocuments.TryVerify(signed, list, Organization, out var settings, out var reason), reason);
        Assert.AreEqual("https://otlp.example:4318", settings.Endpoint);
        Assert.AreEqual(FleetTelemetryRoutes.Direct, settings.Route);
        Assert.AreEqual("key-1", settings.Headers!["x-api-key"]);
    }

    [TestMethod]
    public void ASettingNotSignedByAControllerOfTheOrganizationIsRefused()
    {
        using var desktop = new Key();
        using var node = new Key();
        using var outsider = new Key();
        var list = List(desktop, node);

        // An outsider signs for itself, or claims to be the owner's desktop: neither verifies.
        AssertRefused(outsider.Sign(Settings(outsider.Id)), list, "not signed by a controller device");
        AssertRefused(outsider.Sign(Settings(desktop.Id)), list, "signature does not verify");
        // A node of the organization signs nothing.
        AssertRefused(node.Sign(Settings(node.Id)), list, "not signed by a controller device");
        // A server that changes what the owner signed, or sends it unsigned.
        var signed = desktop.Sign(Settings(desktop.Id));
        AssertRefused(signed with { Payload = signed.Payload.Replace("otlp.example", "evil.example", StringComparison.Ordinal) }, list, "signature does not verify");
        AssertRefused(signed with { Signature = "" }, list, "signature does not verify");
        AssertRefused(null, list, "does not read");
        // A setting of another organization.
        Assert.IsFalse(FleetTelemetrySettingsDocuments.TryVerify(signed, list, "org-2", out _, out var reason));
        StringAssert.Contains(reason, "another organization");
    }

    [TestMethod]
    public void ARevokedControllerSignsNothing()
    {
        using var desktop = new Key();
        using var laptop = new Key();
        var entries = new List<FleetSignedDeviceListEntry> { desktop.First() };
        entries.Add(desktop.SignEntry(new FleetDeviceListEntryPayload(1, Organization, 2, FleetDeviceListEntries.Hash(entries[^1]), FleetDeviceListOperations.Add, laptop.Id,
            laptop.PublicKey, FleetDeviceKinds.Controller, Owner, desktop.Id, Time)));
        entries.Add(desktop.SignEntry(new FleetDeviceListEntryPayload(1, Organization, 3, FleetDeviceListEntries.Hash(entries[^1]), FleetDeviceListOperations.Remove, laptop.Id,
            null, null, null, desktop.Id, Time)));
        var list = FleetDeviceListVerifier.VerifyOrThrow(entries, Organization);

        AssertRefused(laptop.Sign(Settings(laptop.Id)), list, "not signed by a controller device");
    }

    [TestMethod]
    public void ASettingSaysWhatACollectorAndTheServerRouteTake()
    {
        Assert.IsTrue(FleetTelemetrySettingsDocuments.TryValidate(Settings("d") with { Enabled = false, Endpoint = null }, out _), "a disabled setting needs no collector");
        AssertInvalid(Settings("d") with { Endpoint = null }, "address of its collector");
        AssertInvalid(Settings("d") with { Endpoint = "ftp://otlp.example" }, "address of its collector");
        AssertInvalid(Settings("d") with { Route = FleetTelemetryRoutes.Server, Headers = null, Protocol = FleetTelemetryProtocols.Grpc }, "OTLP over HTTP only");
        AssertInvalid(Settings("d") with { Route = FleetTelemetryRoutes.Server }, "headers of the server route stay with the server");
        AssertInvalid(Settings("d") with { Headers = new Dictionary<string, string> { ["Host"] = "evil.example" } }, "not one a setting may set");
        AssertInvalid(Settings("d") with { Headers = new Dictionary<string, string> { ["Alta-Device"] = "x" } }, "not one a setting may set");
        AssertInvalid(Settings("d") with { Headers = new Dictionary<string, string> { ["x-key"] = "a\r\nHost: b" } }, "on one line");
        AssertInvalid(Settings("d") with { Signals = ["traces", "traces"] }, "each once");
        AssertInvalid(Settings("d") with { Protocol = "json" }, "not a protocol");
        Assert.IsTrue(FleetTelemetrySettingsDocuments.TryValidate(Settings("d") with { Route = FleetTelemetryRoutes.Server, Headers = null }, out var reason), reason);
    }

    [TestMethod]
    public void TheMachinesOwnSettingWinsOverTheEnvironmentWhichWinsOverTheFleet()
    {
        var fleet = Settings("d") with { Signals = [FleetTelemetrySignals.Traces, FleetTelemetrySignals.Logs], Environment = "production" };
        var none = Variables();
        var collector = Variables(("OTEL_EXPORTER_OTLP_ENDPOINT", "http://localhost:4318"));

        var fromFleet = FleetTelemetryPlanner.Resolve(null, none, fleet);
        Assert.AreEqual((FleetTelemetrySources.Fleet, "https://otlp.example:4318/", true, false, true), (fromFleet.Source, fromFleet.Endpoint?.ToString(), fromFleet.Traces,
            fromFleet.Metrics, fromFleet.Logs));
        Assert.AreEqual(("production", 7L, false), (fromFleet.Environment, fromFleet.FleetSequence, fromFleet.AllowsContent));

        var fromEnvironment = FleetTelemetryPlanner.Resolve(null, collector, fleet);
        Assert.AreEqual((FleetTelemetrySources.Environment, true, true, false), (fromEnvironment.Source, fromEnvironment.Traces, fromEnvironment.Metrics, fromEnvironment.Logs),
            "the variables send traces and metrics, and logs only when asked");
        Assert.IsTrue(FleetTelemetryPlanner.Resolve(null, Variables(("OTEL_EXPORTER_OTLP_ENDPOINT", "http://x:4318"), ("OTEL_LOGS_EXPORTER", "otlp")), null).Logs);

        var local = new FleetLocalTelemetrySettings { Endpoint = "http://127.0.0.1:4318", Environment = "lab" };
        var fromLocal = FleetTelemetryPlanner.Resolve(local, collector, fleet);
        Assert.AreEqual((FleetTelemetrySources.Local, "http://127.0.0.1:4318/", "lab", true), (fromLocal.Source, fromLocal.Endpoint?.ToString(), fromLocal.Environment,
            fromLocal.AllowsContent));

        Assert.AreEqual(FleetTelemetrySources.Off, FleetTelemetryPlanner.Resolve(new FleetLocalTelemetrySettings { Enabled = false }, collector, fleet).Source,
            "the owner turns telemetry off whatever the fleet and the environment say");
        Assert.AreEqual(FleetTelemetrySources.Off, FleetTelemetryPlanner.Resolve(new FleetLocalTelemetrySettings { AcceptFleet = false }, none, fleet).Source,
            "the owner refuses the fleet's setting");
        Assert.AreEqual(FleetTelemetrySources.Off, FleetTelemetryPlanner.Resolve(null, Variables(("OTEL_SDK_DISABLED", "true")), fleet).Source);
        Assert.AreEqual(FleetTelemetrySources.Off, FleetTelemetryPlanner.Resolve(null, none, fleet with { Enabled = false }).Source);
        Assert.AreEqual("lab", FleetTelemetryPlanner.Resolve(new FleetLocalTelemetrySettings { Environment = "lab" }, none, fleet).Environment,
            "the machine names its own environment under the fleet's collector");
    }

    [TestMethod]
    public void TheFileOfTheMachineIsCheckedWhenItIsRead()
    {
        var folder = Path.Combine(Path.GetTempPath(), "fleet-telemetry-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, FleetLocalTelemetrySettings.FileName);
            Assert.IsNull(FleetLocalTelemetrySettings.Read(path), "no file, no setting");
            File.WriteAllText(path, """{ "enabled": true, "endpoint": "http://localhost:4318", "signals": ["traces", "logs"], "headers": { "x-key": "k" } }""");
            var read = FleetLocalTelemetrySettings.Read(path)!;
            Assert.AreEqual(("http://localhost:4318", 2, "k"), (read.Endpoint, read.Signals!.Count, read.Headers!["x-key"]));
            File.WriteAllText(path, """{ "endpoint": "localhost:4318" }""");
            Assert.ThrowsExactly<InvalidDataException>(() => FleetLocalTelemetrySettings.Read(path));
            File.WriteAllText(path, """{ "signals": ["spans"] }""");
            Assert.ThrowsExactly<InvalidDataException>(() => FleetLocalTelemetrySettings.Read(path));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [TestMethod]
    public void TheAddressOfASignalFollowsTheBaseAddressOfTheCollector()
    {
        Assert.AreEqual("https://otlp.example:4318/v1/traces", FleetTelemetryPaths.SignalAddress(new Uri("https://otlp.example:4318"), "traces").ToString());
        Assert.AreEqual("https://gw.example/otlp/v1/logs", FleetTelemetryPaths.SignalAddress(new Uri("https://gw.example/otlp/"), "logs").ToString());
        Assert.ThrowsExactly<ArgumentException>(() => FleetTelemetryPaths.SignalAddress(new Uri("https://gw.example"), "spans"));
    }

    private static FleetTelemetrySettings Settings(string signer)
        => new(1, Organization, 7, true, "https://otlp.example:4318", FleetTelemetryProtocols.HttpProtobuf, FleetTelemetryRoutes.Direct,
            [FleetTelemetrySignals.Traces, FleetTelemetrySignals.Metrics], new Dictionary<string, string> { ["x-api-key"] = "key-1" }, null, signer, Time);

    private static FleetDeviceListState List(Key desktop, Key node)
    {
        var first = desktop.First();
        var second = desktop.SignEntry(new FleetDeviceListEntryPayload(1, Organization, 2, FleetDeviceListEntries.Hash(first), FleetDeviceListOperations.Add, node.Id,
            node.PublicKey, FleetDeviceKinds.Node, Owner, desktop.Id, Time));
        return FleetDeviceListVerifier.VerifyOrThrow([first, second], Organization);
    }

    private static void AssertRefused(FleetSignedTelemetrySettings? signed, FleetDeviceListState list, string reason)
    {
        Assert.IsFalse(FleetTelemetrySettingsDocuments.TryVerify(signed, list, Organization, out var settings, out var refusal));
        Assert.IsNull(settings);
        StringAssert.Contains(refusal, reason);
    }

    private static void AssertInvalid(FleetTelemetrySettings settings, string reason)
    {
        Assert.IsFalse(FleetTelemetrySettingsDocuments.TryValidate(settings, out var refusal));
        StringAssert.Contains(refusal, reason);
    }

    private static Func<string, string?> Variables(params (string Name, string Value)[] values)
    {
        var map = values.ToDictionary(static pair => pair.Name, static pair => pair.Value, StringComparer.Ordinal);
        return name => map.GetValueOrDefault(name);
    }

    private sealed class Key : IDisposable
    {
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        public Key()
        {
            PublicKey = Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());
            FleetDevicePublicKey.TryParse(PublicKey, out var parsed);
            Id = parsed!.DeviceId;
        }

        public string PublicKey { get; }

        public string Id { get; }

        public FleetSignedDeviceListEntry First()
            => SignEntry(new FleetDeviceListEntryPayload(1, null, 1, null, FleetDeviceListOperations.Add, Id, PublicKey, FleetDeviceKinds.Controller, null, Id, Time));

        public FleetSignedDeviceListEntry SignEntry(FleetDeviceListEntryPayload payload)
        {
            var text = FleetDeviceListEntries.SerializePayload(payload);
            return new FleetSignedDeviceListEntry(text, Sign(FleetDeviceListEntries.GetSignedBytes(text)));
        }

        public FleetSignedTelemetrySettings Sign(FleetTelemetrySettings settings)
        {
            var text = FleetTelemetrySettingsDocuments.Serialize(settings);
            return new FleetSignedTelemetrySettings(text, Sign(FleetTelemetrySettingsDocuments.GetSignedBytes(text)));
        }

        public void Dispose() => _key.Dispose();

        private string Sign(byte[] data)
            => Base64Url.EncodeToString(_key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }
}
