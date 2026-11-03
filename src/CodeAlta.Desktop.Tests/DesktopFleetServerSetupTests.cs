using System.Collections.Frozen;
using System.Net;
using System.Text.Json;
using CodeAlta.Desktop.Fleet;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Fleet.Server;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Server.Hosting;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// The setup of a server by this desktop (<see cref="DesktopFleetServers.CreateServerAsync(Uri, string, string, string?, CancellationToken)"/>)
/// against a server that answers, from the first read on, a device list of its own making.
/// </summary>
[TestClass]
public sealed class DesktopFleetServerSetupTests
{
    private static readonly DateTimeOffset Created = new(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task ASetUpServer_IsPinnedToTheEntryThisDesktopSigned_SoAListRootedInAnotherKeyIsRefusedFromTheFirstRead()
    {
        var token = TestContext.CancellationToken;
        var root = Directory.CreateTempSubdirectory("codealta-fleet-setup-pin-").FullName;
        string At(string name) => Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        try
        {
            await using var server = await CoordinatorServer.StartAsync(new CoordinatorServerOptions
            {
                DataRoot = At("server"), Urls = ["http://127.0.0.1:0"], ConfigureLogging = static _ => { },
            }, token);
            var address = server.Addresses.First(static uri => uri.Scheme == "http");
            await using var host = await CodeAltaHost.CreateAsync(new CodeAltaHostOptions
            {
                GlobalRoot = At("global"), CurrentProjectPath = At("project"), DiscoveryScope = new(At("home"), root), BuiltInSkillRoot = At("builtin"),
                PluginEnvironment = FrozenDictionary<string, string?>.Empty, StartPlugins = false, IsHeadless = true, OwnsLogging = false,
            }, token);
            // The key of this desktop, as the servers load it, and the keys of the server's making.
            using var desktopKey = DeviceIdentity.LoadOrCreate(host.CatalogOptions.FleetRoot);
            using var attackerKey = DeviceIdentity.LoadOrCreate(At("attacker"));
            using var plantedKey = DeviceIdentity.LoadOrCreate(At("planted"));
            using var lying = new RootSwappingHandler(attackerKey, desktopKey, plantedKey) { InnerHandler = new HttpClientHandler() };

            // Every read of the device list, the connector's first one included, goes through the lying server.
            await using var servers = new DesktopFleetServers(host, "Desktop", "4.0.0", options =>
            {
                var devices = new FleetOrganizationDevices(options.ServerAddress, options.Identity, options.DeviceListPinPath, lying);
                return new FleetConnectorOptions
                {
                    ServerAddress = options.ServerAddress, Identity = options.Identity, OutboxPath = options.OutboxPath, Client = options.Client, Source = options.Source,
                    PublishesSpace = options.PublishesSpace, LinkedSpaces = options.LinkedSpaces, WatchDirectory = options.WatchDirectory, Devices = devices,
                    MetadataKeys = new FleetMetadataKeyring(devices), DeviceListPinPath = options.DeviceListPinPath, MinimumBackoff = TimeSpan.FromMilliseconds(100),
                    PublishDelay = TimeSpan.FromMilliseconds(20), PollInterval = TimeSpan.FromMilliseconds(200), PendingApprovalRetry = TimeSpan.FromMilliseconds(200),
                };
            }) { Handler = lying };

            var link = await servers.CreateServerAsync(address, server.SetupCode!.Code, "Alex", "Home", token);
            Assert.IsTrue(lying.Armed, "the server knows the organization and the person, to forge a list that holds");

            // The anchor is the entry this desktop signed: the list the server forges, which holds and holds this desktop as a
            // controller, is refused, so that no key is wrapped to the server's devices and nothing is signed after it.
            var pin = Path.Combine(new FleetServerLinkStore(host.CatalogOptions.FleetRoot).LinkFolder(link.Id), FleetOrganizationDevices.PinFileName);
            var refused = await Assert.ThrowsExactlyAsync<FleetDeviceListException>(() => new FleetOrganizationDevices(address, desktopKey, pin, lying).RefreshAsync(token));
            Assert.AreEqual(1L, refused.BrokenAt);
            Assert.AreNotEqual(attackerKey.DeviceId, servers.Connections.Single().Connector!.Devices.Current?.FirstDeviceId);

            // The genuine list verifies through the same anchor, and starts with this desktop.
            var genuine = await new FleetOrganizationDevices(address, desktopKey, pin).RefreshAsync(token);
            Assert.AreEqual(desktopKey.DeviceId, genuine.FirstDeviceId);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// A server that, once set up, answers a device list it roots in a key of its own: the desktop's real key is a controller
    /// in it, of the desktop's person, and a device of the server's choice too, so that the desktop would wrap its keys to it.
    /// </summary>
    private sealed class RootSwappingHandler(DeviceIdentity attacker, DeviceIdentity victim, DeviceIdentity planted) : DelegatingHandler
    {
        private SetupResponse? _setup;

        public bool Armed => Volatile.Read(ref _setup) is not null;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path.EndsWith("/api/v1/device-list", StringComparison.Ordinal) && Volatile.Read(ref _setup) is { } setup)
            {
                var first = Entry(attacker, 1, null, attacker, FleetDeviceKinds.Controller, null, null);
                var desktop = Entry(attacker, 2, first.Hash, victim, FleetDeviceKinds.Controller, setup.Organization.Id, setup.Owner.Id);
                var device = Entry(attacker, 3, desktop.Hash, planted, FleetDeviceKinds.Node, setup.Organization.Id, setup.Owner.Id);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(new ListResponse<DeviceListEntryDto>([first, desktop, device]),
                        ServerApiJsonContext.Default.ListResponseDeviceListEntryDto)),
                };
            }

            var response = await base.SendAsync(request, cancellationToken);
            if (request.Method == HttpMethod.Post && path.EndsWith("/api/v1/setup", StringComparison.Ordinal) && response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                Volatile.Write(ref _setup, JsonSerializer.Deserialize(body, ServerApiJsonContext.Default.SetupResponse));
                response.Content = new ByteArrayContent(body) { Headers = { ContentType = new("application/json") } };
            }

            return response;
        }

        private static DeviceListEntryDto Entry(DeviceIdentity signer, long sequence, string? previous, DeviceIdentity added, string kind, string? organization, string? owner)
        {
            var payload = FleetDeviceListEntries.SerializePayload(new FleetDeviceListEntryPayload(1, organization, sequence, previous, FleetDeviceListOperations.Add, added.DeviceId,
                Convert.ToBase64String(added.ExportSubjectPublicKeyInfo()), kind, owner, signer.DeviceId, Created));
            var entry = new FleetSignedDeviceListEntry(payload, System.Buffers.Text.Base64Url.EncodeToString(signer.SignData(FleetDeviceListEntries.GetSignedBytes(payload))));
            return new DeviceListEntryDto(sequence, FleetDeviceListEntries.Hash(entry), entry, Created);
        }
    }
}
