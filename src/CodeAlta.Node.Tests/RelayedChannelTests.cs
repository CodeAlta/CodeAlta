using System.Text;
using CodeAlta.Fleet.Client;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Server;
using CodeAlta.Server.Hosting;

namespace CodeAlta.Node.Tests;

/// <summary>
/// A session channel through the relay of a CodeAlta Server of this process: a node that listens on no port, joined to
/// the server, and a viewer of the same organization that never paired with it. The viewer lists, watches and drives a
/// session of the node; the bytes the server pipes are TLS records only; another device of the organization may do
/// nothing, and once revoked it is cut and refused; a channel whose relay dropped connects again.
/// </summary>
[TestClass]
public sealed class RelayedChannelTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);
    private const string Marker = "RELAY-PLAINTEXT-MARKER-7f3c";

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task AViewerOfTheOrganizationReadsWatchesAndDrivesASessionThroughTheRelay_AndTheServerSeesCiphertextOnly()
    {
        var token = TestContext.CancellationToken;
        await using var rig = await RelayRig.StartAsync(token);
        var tap = new RelayTap();
        await using var viewer = rig.Connector(rig.DesktopIdentity, "desktop", tap);
        viewer.Start();
        await WaitForAsync(() => viewer.Directory.IsOnline(rig.NodeIdentity.DeviceId), token);

        await using var client = await FleetNodeClient.ConnectRelayedAsync(viewer, rig.NodeIdentity.DeviceId, rig.DesktopIdentity, null, token);
        Assert.AreEqual(FleetChannelPath.Relayed, client.Path);
        Assert.AreEqual(rig.NodeIdentity.DeviceId, client.NodeDeviceId, "TLS authenticated the node with the key the signed device list names.");
        Assert.AreEqual("test-node", (await client.GetInfoAsync(token)).Name);
        // A device trusted through the organization never manages the machine: neither its devices nor its secrets.
        Assert.AreEqual(FleetErrorCodes.Forbidden, (await Assert.ThrowsExactlyAsync<FleetException>(() => client.ListSecretsAsync(token))).Error.Code);
        Assert.AreEqual(FleetErrorCodes.Forbidden, (await Assert.ThrowsExactlyAsync<FleetException>(() =>
            client.SetSecretAsync(new FleetSecretSetParams("RELAYED", "value"), cancellationToken: token))).Error.Code);
        Assert.AreEqual(FleetErrorCodes.Forbidden, (await Assert.ThrowsExactlyAsync<FleetException>(() => client.ListDevicesAsync(token))).Error.Code);

        var created = await client.CreateSessionAsync(new FleetCreateSessionParams(Title: Marker + " title"), "create-relayed", token);
        Assert.IsTrue((await client.ListSessionsAsync(cancellationToken: token)).Sessions.Any(session => session.Id == created.SessionId), "The session is listed.");
        var gate = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        rig.Provider.Turns.Enqueue(ScriptedNodeProvider.WaitThenReply(gate.Task, Marker + " answer", started));
        await using (var watch = await client.WatchAsync(created.SessionId, token))
        {
            var sent = await client.SendAsync(created.SessionId, Marker + " prompt", retryKey: "send-relayed", cancellationToken: token);
            Assert.AreEqual("accepted", sent.Status);
            await started.Task.WaitAsync(Patience, token);
            gate.SetResult();
            await ReadUntilAsync(watch, display => display.Text.Any(text => text.Kind == "Assistant" && text.IsComplete && text.Text.Contains(Marker + " answer", StringComparison.Ordinal)),
                token);
        }

        // What the server piped: TLS records both ways, and not one byte of what the session said.
        var (up, down) = tap.Bytes();
        Assert.IsTrue(up.Length > 0 && down.Length > 0);
        Assert.AreEqual((0x16, 0x16), (up[0], down[0]), "Each side starts with a TLS handshake record.");
        var marker = Encoding.UTF8.GetBytes(Marker);
        Assert.AreEqual(-1, up.AsSpan().IndexOf(marker), "The prompt never crosses the server in clear.");
        Assert.AreEqual(-1, down.AsSpan().IndexOf(marker), "The answer and the title never cross the server in clear.");
        Assert.AreEqual(-1, down.AsSpan().IndexOf(Encoding.UTF8.GetBytes("test-node")), "Even the name of the node is inside TLS.");
    }

    [TestMethod]
    public async Task AChannelWhoseRelayDroppedConnectsAgain()
    {
        var token = TestContext.CancellationToken;
        await using var rig = await RelayRig.StartAsync(token);
        var tap = new RelayTap();
        await using var viewer = rig.Connector(rig.DesktopIdentity, "desktop", tap);
        viewer.Start();
        await WaitForAsync(() => viewer.Directory.IsOnline(rig.NodeIdentity.DeviceId), token);
        await using var first = await FleetNodeClient.ConnectRelayedAsync(viewer, rig.NodeIdentity.DeviceId, rig.DesktopIdentity, null, token);
        var created = await first.CreateSessionAsync(new FleetCreateSessionParams(Title: "Kept"), "create-kept", token);

        tap.Cut();
        var closed = await first.Completion.WaitAsync(Patience, token);
        Assert.IsFalse(closed.IsClean && closed.ClosedLocally, "The channel ended with its relay.");

        await using var again = await FleetNodeClient.ConnectRelayedAsync(viewer, rig.NodeIdentity.DeviceId, rig.DesktopIdentity, null, token);
        Assert.IsTrue((await again.ListSessionsAsync(cancellationToken: token)).Sessions.Any(session => session.Id == created.SessionId),
            "A new relay reaches the same node and its sessions.");
    }

    [TestMethod]
    public async Task APrivateSession_IsNeitherListedNorReadNorDriven_ThroughTheRelay()
    {
        var token = TestContext.CancellationToken;
        await using var rig = await RelayRig.StartAsync(token);
        await using var viewer = rig.Connector(rig.DesktopIdentity, "desktop", null);
        viewer.Start();
        await WaitForAsync(() => viewer.Directory.IsOnline(rig.NodeIdentity.DeviceId), token);
        await using var client = await FleetNodeClient.ConnectRelayedAsync(viewer, rig.NodeIdentity.DeviceId, rig.DesktopIdentity, null, token);
        var shared = await client.CreateSessionAsync(new FleetCreateSessionParams(Title: "Shared"), "create-shared", token);
        var hidden = await client.CreateSessionAsync(new FleetCreateSessionParams(Title: "Hidden"), "create-hidden", token);

        await FleetNodePrivateSessionTests.MarkPrivateAsync(rig.Node, hidden.SessionId, token);

        await FleetNodePrivateSessionTests.AssertPrivateAsync(client, hidden.SessionId, shared.SessionId, token);
    }

    [TestMethod]
    public async Task AnotherDeviceOfTheOrganizationMayDoNothing_AndOnceRevokedItIsCutAndRefused()
    {
        var token = TestContext.CancellationToken;
        await using var rig = await RelayRig.StartAsync(token);
        using var otherIdentity = DeviceIdentity.LoadOrCreate(Path.Combine(rig.Root.Root, "other", "fleet"));
        await rig.ApproveNodeAsync(otherIdentity, "other-node", token);
        await using var other = rig.Connector(otherIdentity, "node", null);
        other.Start();
        await WaitForAsync(() => other.State == FleetConnectorState.Online, token);

        await using var client = await FleetNodeClient.ConnectRelayedAsync(other, rig.NodeIdentity.DeviceId, otherIdentity, null, token);
        var forbidden = await Assert.ThrowsExactlyAsync<FleetException>(() => client.ListSessionsAsync(cancellationToken: token));
        Assert.AreEqual(FleetErrorCodes.Forbidden, forbidden.Error.Code, "A member that is not one of the owner's controller devices may do nothing yet.");

        await rig.Desktop.RevokeAsync(otherIdentity.DeviceId, token);
        await client.Completion.WaitAsync(Patience, token);
        await WaitForAsync(() => other.State == FleetConnectorState.Revoked, token);
        var refused = await Assert.ThrowsExactlyAsync<FleetServerException>(() => FleetNodeClient.ConnectRelayedAsync(other, rig.NodeIdentity.DeviceId, otherIdentity, null, token));
        Assert.AreEqual(FleetErrorCodes.Revoked, refused.Code, "A revoked device opens no relay.");

        // The node itself refuses the removed key, whatever a server says: its device list is signed.
        var list = await rig.NodeDevices.RefreshAsync(token);
        Assert.IsTrue(new FleetOrganizationTrust(() => list, rig.NodeIdentity.DeviceId).IsRevoked(otherIdentity.Fingerprint));
    }

    private static async Task ReadUntilAsync(FleetSessionWatch watch, Func<FleetSessionDisplay, bool> condition, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Patience);
        await foreach (var display in watch.Updates.ReadAllAsync(timeout.Token))
            if (condition(display)) return;
        Assert.Fail($"The watch never showed what was expected; ended: {watch.EndReason}");
    }

    private static async Task WaitForAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("The condition never held.");
            await Task.Delay(50, cancellationToken);
        }
    }

    /// <summary>
    /// A server set up by a desktop key, and a node of another profile that joined it and listens on no port: its server
    /// connection relays channels to its node API.
    /// </summary>
    private sealed class RelayRig : IAsyncDisposable
    {
        private readonly NodeHost _node;
        private readonly NodeFleet _fleet;
        private readonly NodeServers _servers;
        private readonly CoordinatorServer _server;

        private RelayRig(NodeTestRoot root, CoordinatorServer server, NodeHost node, NodeFleet fleet, NodeServers servers, ScriptedNodeProvider provider,
            DeviceIdentity desktopIdentity, DeviceIdentity nodeIdentity, FleetServerClient desktop)
        {
            Root = root;
            _server = server;
            _node = node;
            _fleet = fleet;
            _servers = servers;
            Provider = provider;
            DesktopIdentity = desktopIdentity;
            NodeIdentity = nodeIdentity;
            Desktop = desktop;
            NodeDevices = new FleetOrganizationDevices(Address, nodeIdentity);
        }

        public NodeTestRoot Root { get; }

        public NodeHost Node => _node;

        public Uri Address => _server.Addresses.First(static uri => uri.Scheme == "http");

        public ScriptedNodeProvider Provider { get; }

        public DeviceIdentity DesktopIdentity { get; }

        public DeviceIdentity NodeIdentity { get; }

        public FleetServerClient Desktop { get; }

        public FleetOrganizationDevices NodeDevices { get; }

        public static async Task<RelayRig> StartAsync(CancellationToken cancellationToken)
        {
            var root = new NodeTestRoot();
            var provider = new ScriptedNodeProvider();
            var server = await CoordinatorServer.StartAsync(new CoordinatorServerOptions
            {
                DataRoot = Path.Combine(root.Root, "server"), Urls = ["http://127.0.0.1:0"], ConfigureLogging = static _ => { },
            }, cancellationToken);
            var address = server.Addresses.First(static uri => uri.Scheme == "http");
            var desktopIdentity = DeviceIdentity.LoadOrCreate(Path.Combine(root.Root, "desktop", "fleet"));
            var desktop = new FleetServerClient(address, desktopIdentity);
            await desktop.SetUpAsync(server.SetupCode!.Code, "Alex", "Desktop", "Home", cancellationToken);

            var node = await NodeHost.StartAsync(root.Options() with { ConfigureModelProviders = provider.Register }, cancellationToken);
            // No listener: the node has no inbound port, and is reached through the server only.
            var fleet = NodeFleet.Start(node, new NodeListenOptions { Enabled = false }, "test-node", "1.0.0");
            Assert.IsNull(fleet.EndPoint);
            var nodeIdentity = DeviceIdentity.LoadOrCreate(node.Host.CatalogOptions);
            await ApproveAsync(desktop, address, nodeIdentity, "test-node", cancellationToken);
            var now = DateTimeOffset.UtcNow;
            new FleetServerLinkStore(node.Host.CatalogOptions.FleetRoot).Save(new FleetServerLink(FleetServerLinkStore.NewId(), FleetServerAddress.Format(address), null, null,
                nodeIdentity.DeviceId, FleetDeviceKinds.Node, FleetServerLinkStates.Approved, now, now));
            var servers = NodeServers.Start(node, fleet.Service, "test-node", "1.0.0", Fast);
            return new RelayRig(root, server, node, fleet, servers, provider, desktopIdentity, nodeIdentity, desktop);
        }

        public Task ApproveNodeAsync(DeviceIdentity identity, string name, CancellationToken cancellationToken) => ApproveAsync(Desktop, Address, identity, name, cancellationToken);

        private static async Task ApproveAsync(FleetServerClient desktop, Uri address, DeviceIdentity identity, string name, CancellationToken cancellationToken)
        {
            var enrollment = await desktop.CreateEnrollmentTokenAsync(null, name, cancellationToken);
            using var client = new FleetServerClient(address, identity);
            await client.EnrollAsync(enrollment.Token, name, cancellationToken);
            await desktop.ApproveAsync(identity.DeviceId, cancellationToken);
        }

        public FleetConnector Connector(DeviceIdentity identity, string kind, RelayTap? tap)
            => new(Fast(new FleetConnectorOptions
            {
                ServerAddress = Address,
                Identity = identity,
                OutboxPath = Path.Combine(Root.Root, identity.DeviceId, "outbox.json"),
                Client = new FleetClientInfo(kind, "1.0.0", "windows"),
                WatchDirectory = true,
                WrapRelayStream = tap is null ? null : tap.Wrap,
            }));

        // The same options, with waits that suit a test.
        private static FleetConnectorOptions Fast(FleetConnectorOptions options) => new()
        {
            ServerAddress = options.ServerAddress, Identity = options.Identity, OutboxPath = options.OutboxPath, Client = options.Client, Source = options.Source,
            PublishesSpace = options.PublishesSpace, WatchDirectory = options.WatchDirectory, AcceptRelay = options.AcceptRelay, DeviceListPinPath = options.DeviceListPinPath,
            WrapRelayStream = options.WrapRelayStream, PendingApprovalRetry = TimeSpan.FromMilliseconds(200), MinimumBackoff = TimeSpan.FromMilliseconds(100),
            MaximumBackoff = TimeSpan.FromSeconds(1), PollInterval = TimeSpan.FromMilliseconds(500),
        };

        public async ValueTask DisposeAsync()
        {
            await _servers.DisposeAsync();
            await _fleet.DisposeAsync();
            await _node.DisposeAsync();
            await _server.DisposeAsync();
            Desktop.Dispose();
            DesktopIdentity.Dispose();
            NodeIdentity.Dispose();
            Root.Dispose();
        }
    }

    /// <summary>Keeps what the viewer wrote to its relays and read from them: the bytes the server piped.</summary>
    private sealed class RelayTap
    {
        private readonly Lock _gate = new();
        private readonly MemoryStream _up = new();
        private readonly MemoryStream _down = new();
        private Stream? _last;

        public Stream Wrap(Stream inner)
        {
            var tapped = new TapStream(inner, this);
            lock (_gate) _last = inner;
            return tapped;
        }

        public (byte[] Up, byte[] Down) Bytes()
        {
            lock (_gate) return (_up.ToArray(), _down.ToArray());
        }

        // Drops the last relay, as a network that fails would.
        public void Cut()
        {
            Stream? last;
            lock (_gate) last = _last;
            last?.Dispose();
        }

        private void Record(ReadOnlySpan<byte> bytes, bool up)
        {
            lock (_gate) (up ? _up : _down).Write(bytes);
        }

        private sealed class TapStream(Stream inner, RelayTap tap) : Stream
        {
            public override bool CanRead => inner.CanRead;

            public override bool CanSeek => false;

            public override bool CanWrite => inner.CanWrite;

            public override long Length => throw new NotSupportedException();

            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override void Flush() => inner.Flush();

            public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

            public override int Read(byte[] buffer, int offset, int count)
            {
                var read = inner.Read(buffer, offset, count);
                tap.Record(buffer.AsSpan(offset, read), up: false);
                return read;
            }

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                var read = await inner.ReadAsync(buffer, cancellationToken);
                tap.Record(buffer.Span[..read], up: false);
                return read;
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
                => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

            public override void Write(byte[] buffer, int offset, int count)
            {
                tap.Record(buffer.AsSpan(offset, count), up: true);
                inner.Write(buffer, offset, count);
            }

            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                tap.Record(buffer.Span, up: true);
                return inner.WriteAsync(buffer, cancellationToken);
            }

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
                => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing) inner.Dispose();
                base.Dispose(disposing);
            }

            public override ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
}
