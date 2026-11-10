using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using CodeAlta.Fleet.Channels;
using CodeAlta.Fleet.Client;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;

namespace CodeAlta.Fleet.Tests;

[TestClass]
public sealed class FleetChannelTests
{
    private static readonly TimeSpan Deadline = TestTime.Minutes(1);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Stack_OverAnyStream_AuthenticatesTheDevice_AndCarriesRequests()
    {
        using var folder = new TestFolder();
        using var node = DeviceIdentity.LoadOrCreate(Path.Combine(folder.Path, "node"));
        using var viewer = DeviceIdentity.LoadOrCreate(Path.Combine(folder.Path, "viewer"));
        using var certificate = node.CreateTlsCertificate();
        var trusted = new TrustedDeviceStore(Path.Combine(folder.Path, "node"), TimeProvider.System);
        trusted.Add("viewer", viewer.Fingerprint, DevicePermissions.View);
        var (serverStream, clientStream) = InMemoryDuplexStream.CreatePair();

        var accepting = FleetChannelStack.AcceptAsync(serverStream, new FleetChannelServerOptions { Certificate = certificate, TrustPolicy = trusted }, TestContext.CancellationToken);
        var connecting = FleetChannelStack.ConnectAsync(clientStream, new FleetChannelClientOptions { Identity = viewer, NodeFingerprint = node.Fingerprint }, TestContext.CancellationToken);
        await using var server = await accepting.WithinAsync(Deadline, TestContext.CancellationToken);
        await using var client = await connecting.WithinAsync(Deadline, TestContext.CancellationToken);

        Assert.AreEqual(viewer.DeviceId, server.RemoteDeviceId, "The node knows the device TLS authenticated.");
        Assert.AreEqual("viewer", server.RemoteDevice?.Name);
        Assert.AreEqual(FleetChannelProtocol.ViewerClientKind, server.Client?.Kind);
        Assert.AreEqual(node.DeviceId, client.RemoteDeviceId);

        server.Connection.RegisterRequestHandler("echo", static (context, _) => ValueTask.FromResult(context.Params));
        server.Connection.Start();
        client.Connection.Start();
        using var document = JsonDocument.Parse("{\"text\":\"hello\"}");
        var echoed = await client.Connection.SendRequestAsync("echo", document.RootElement.Clone(), cancellationToken: TestContext.CancellationToken);
        Assert.AreEqual("hello", echoed?.GetProperty("text").GetString());
    }

    [TestMethod]
    public async Task Stack_RefusesANodeThatPresentsAnotherKey()
    {
        using var folder = new TestFolder();
        using var node = DeviceIdentity.LoadOrCreate(Path.Combine(folder.Path, "node"));
        using var impostor = DeviceIdentity.LoadOrCreate(Path.Combine(folder.Path, "impostor"));
        using var viewer = DeviceIdentity.LoadOrCreate(Path.Combine(folder.Path, "viewer"));
        using var certificate = impostor.CreateTlsCertificate();
        var trusted = new TrustedDeviceStore(Path.Combine(folder.Path, "impostor"), TimeProvider.System);
        trusted.Add("viewer", viewer.Fingerprint, DevicePermissions.View);
        var (serverStream, clientStream) = InMemoryDuplexStream.CreatePair();

        var accepting = FleetChannelStack.AcceptAsync(serverStream, new FleetChannelServerOptions { Certificate = certificate, TrustPolicy = trusted }, TestContext.CancellationToken);
        var refused = await Assert.ThrowsExactlyAsync<DeviceAuthenticationException>(() => FleetChannelStack.ConnectAsync(clientStream,
            new FleetChannelClientOptions { Identity = viewer, NodeFingerprint = node.Fingerprint }, TestContext.CancellationToken));
        Assert.AreEqual(DeviceAuthenticationFailure.ServerKeyMismatch, refused.Failure);
        await Assert.ThrowsAsync<Exception>(() => accepting.WithinAsync(Deadline, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task Listener_TellsAConnectionOfThisMachine()
    {
        var accepted = new TaskCompletionSource<FleetAcceptedStream>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var listener = FleetDirectListener.Start(new IPEndPoint(IPAddress.Loopback, 0), (stream, _) =>
        {
            accepted.TrySetResult(stream);
            return Task.CompletedTask;
        });

        var (stream, address) = await FleetDirectConnector.ConnectAsync([FleetDirectConnector.Format(IPAddress.Loopback, listener.LocalEndPoint.Port)],
            TimeSpan.FromSeconds(5), TestContext.CancellationToken);
        await using (stream)
        {
            var incoming = await accepted.Task.WithinAsync(Deadline, TestContext.CancellationToken);
            await using (incoming.Stream)
            {
                Assert.IsTrue(incoming.IsFromThisMachine);
                StringAssert.StartsWith(address, "127.0.0.1:");
            }
        }

        var remote = new FleetAcceptedStream(Stream.Null, new IPEndPoint(IPAddress.Parse("192.0.2.10"), 50000), new IPEndPoint(IPAddress.Parse("192.0.2.1"), 2585));
        Assert.IsFalse(remote.IsFromThisMachine, "A connection from another address of the network is not of this machine.");
        var self = new FleetAcceptedStream(Stream.Null, new IPEndPoint(IPAddress.Parse("192.0.2.1"), 50000), new IPEndPoint(IPAddress.Parse("192.0.2.1"), 2585));
        Assert.IsTrue(self.IsFromThisMachine, "A connection of this machine to its own network address is.");
    }

    [TestMethod]
    public async Task Connector_NamesTheFailureOfEachAddress_NotOnlyTheLast()
    {
        // A port nothing listens on: the loopback refuses it at once.
        int closed;
        using (var probe = new Socket(SocketType.Stream, ProtocolType.Tcp))
        {
            probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            closed = ((IPEndPoint)probe.LocalEndPoint!).Port;
        }

        var refused = FleetDirectConnector.Format(IPAddress.Loopback, closed);
        var failure = await Assert.ThrowsExactlyAsync<IOException>(() => FleetDirectConnector.ConnectAsync(["not-an-address", refused], TimeSpan.FromSeconds(5),
            TestContext.CancellationToken));
        StringAssert.StartsWith(failure.Message, "No address of the machine answered: not-an-address ");
        StringAssert.Contains(failure.Message, $"; {refused} ");
        var inner = Assert.IsInstanceOfType<AggregateException>(failure.InnerException, "every failure is kept, for the caller to tell why");
        Assert.AreEqual(2, inner.InnerExceptions.Count);
        Assert.IsInstanceOfType<SocketException>(inner.InnerExceptions[1]);
    }

    [TestMethod]
    [DataRow("127.0.0.1:2585", "127.0.0.1", 2585)]
    [DataRow("[::1]:2585", "::1", 2585)]
    [DataRow("mac-mini.local:80", "mac-mini.local", 80)]
    public void Connector_SplitsAddresses(string address, string host, int port)
    {
        Assert.IsTrue(FleetDirectConnector.TrySplit(address, out var actualHost, out var actualPort));
        Assert.AreEqual(host, actualHost);
        Assert.AreEqual(port, actualPort);
        Assert.IsFalse(FleetDirectConnector.TrySplit("no-port", out _, out _));
        Assert.AreEqual("[::1]:2585", FleetDirectConnector.Format(IPAddress.IPv6Loopback, 2585));
    }
}

[TestClass]
public sealed class PairedMachineStoreTests
{
    [TestMethod]
    public void Save_Find_AndRemove_ReadTheFileAsItIsNow()
    {
        using var folder = new TestFolder();
        using var machineKey = DeviceIdentity.LoadOrCreate(Path.Combine(folder.Path, "machine"));
        var store = new PairedMachineStore(Path.Combine(folder.Path, "fleet"));
        Assert.AreEqual(0, store.List().Count);

        var machine = new PairedMachine("Mac mini", machineKey.DeviceId, machineKey.Fingerprint, ["192.0.2.1:2585"], DateTimeOffset.UnixEpoch, ["view"]);
        store.Save(machine);

        // Another store of the same profile (another process) reads it at once.
        var other = new PairedMachineStore(Path.Combine(folder.Path, "fleet"));
        var found = other.Find("mac MINI");
        Assert.IsNotNull(found);
        Assert.AreEqual(machineKey.Fingerprint, found.Fingerprint);
        Assert.AreEqual(machine.DeviceId, other.Find(machine.DeviceId)?.DeviceId);

        store.Save(machine with { Addresses = ["192.0.2.2:2585"] });
        Assert.AreEqual(1, other.List().Count, "Saving a machine again replaces it.");
        CollectionAssert.AreEqual(new[] { "192.0.2.2:2585" }, other.List()[0].Addresses.ToArray());

        Assert.IsTrue(other.Remove(machine.DeviceId));
        Assert.AreEqual(0, store.List().Count);
        Assert.IsFalse(store.Remove(machine.DeviceId));
    }

    [TestMethod]
    public async Task ConcurrentWriters_LoseNoMachine()
    {
        using var folder = new TestFolder();
        var fleet = Path.Combine(folder.Path, "fleet");
        var keys = Enumerable.Range(0, 8).Select(index => DeviceIdentity.LoadOrCreate(Path.Combine(folder.Path, "machine" + index))).ToArray();
        try
        {
            // Each writer has its own store, as two processes of one profile would.
            await Task.WhenAll(keys.Select((key, index) => Task.Run(() => new PairedMachineStore(fleet).Save(
                new PairedMachine("Machine " + index, key.DeviceId, key.Fingerprint, ["192.0.2.1:2585"], DateTimeOffset.UnixEpoch, ["view"])))));

            Assert.AreEqual(keys.Length, new PairedMachineStore(fleet).List().Count);
        }
        finally
        {
            foreach (var key in keys) key.Dispose();
        }
    }

    [TestMethod]
    public void DamagedFile_IsReported_NeverDroppedSilently()
    {
        using var folder = new TestFolder();
        var store = new PairedMachineStore(folder.Path);
        File.WriteAllText(store.FilePath, "{\"version\":1,\"machines\":[{\"name\":\"x\",\"deviceId\":\"abc\",\"fingerprint\":\"nope\"}]}");

        Assert.ThrowsExactly<InvalidDataException>(() => store.List());
    }
}
