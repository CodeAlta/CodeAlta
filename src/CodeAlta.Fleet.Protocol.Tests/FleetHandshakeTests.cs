using System.Buffers;
using System.Text;

namespace CodeAlta.Fleet.Protocol.Tests;

[TestClass]
public sealed class FleetHandshakeTests
{
    private static readonly FleetClientInfo Desktop = new("desktop", "4.0.0", "windows");

    [TestMethod]
    public async Task AClientWithAValidProofIsWelcomedWithTheHighestCommonVersion()
    {
        var (clientTransport, serverTransport) = FleetInMemoryTransport.CreatePair();
        var keys = new TestDeviceKeys("device-1");
        var binding = Encoding.UTF8.GetBytes("tls-exporter");
        var serverOptions = new FleetHandshakeServerOptions
        {
            Protocol = new FleetProtocolRange(1, 3),
            Policy = new FleetConnectionPolicy(1 << 20, 20_000),
            Features = ["relay", "replica"],
            ChannelBinding = binding,
            ResolveCursorsAsync = (hello, _) => ValueTask.FromResult<System.Text.Json.JsonElement?>(Json.Parse("""{"deliveries":5}""")),
        };
        var clientOptions = new FleetHandshakeClientOptions
        {
            Protocol = new FleetProtocolRange(2, 4),
            Client = Desktop,
            ChannelBinding = binding,
            ResumeCursors = Json.Parse("""{"directory":9}"""),
        };

        var accept = FleetHandshake.AcceptAsync(serverTransport, serverOptions, keys, CancellationToken.None);
        var welcome = await FleetHandshake.ConnectAsync(clientTransport, clientOptions, keys, CancellationToken.None).WaitAsync(Json.Hang);
        var accepted = await accept.WaitAsync(Json.Hang);

        Assert.AreEqual(3, welcome.Protocol);
        CollectionAssert.AreEqual(new[] { "relay", "replica" }, welcome.Features.ToArray());
        Assert.AreEqual(1 << 20, welcome.Policy.MaxFrameBytes);
        Assert.AreEqual(5, welcome.Cursors!.Value.GetProperty("deliveries").GetInt32());
        Assert.AreEqual(Desktop, accepted.Hello.Client);
        Assert.AreEqual("device-1", accepted.Hello.Device.Id);
        Assert.AreEqual(9, accepted.Hello.Resume!.Cursors!.Value.GetProperty("directory").GetInt32());
        Assert.IsTrue(keys.VerifiedContext!.ChannelBinding.Span.SequenceEqual(binding));

        // The connection takes its limits from the welcome.
        var options = FleetConnectionOptions.FromPolicy(welcome.Policy);
        Assert.AreEqual(1 << 20, options.MaxFrameBytes);
        Assert.AreEqual(TimeSpan.FromSeconds(20), options.HeartbeatInterval);
        Assert.AreEqual(TimeSpan.FromSeconds(60), options.DeadPeerTimeout);
    }

    [TestMethod]
    public async Task VersionsWithNothingInCommonAreRefusedWithUpdateRequired()
    {
        var (clientTransport, serverTransport) = FleetInMemoryTransport.CreatePair();
        var keys = new TestDeviceKeys("device-1");
        var accept = FleetHandshake.AcceptAsync(serverTransport, new FleetHandshakeServerOptions { Protocol = new FleetProtocolRange(3, 4) }, keys, CancellationToken.None);
        var connect = FleetHandshake.ConnectAsync(clientTransport, new FleetHandshakeClientOptions { Protocol = new FleetProtocolRange(1, 2), Client = Desktop }, keys, CancellationToken.None);

        var clientError = await Assert.ThrowsExactlyAsync<FleetHandshakeException>(() => connect.WaitAsync(Json.Hang));
        var serverError = await Assert.ThrowsExactlyAsync<FleetHandshakeException>(() => accept.WaitAsync(Json.Hang));

        Assert.AreEqual(FleetErrorCodes.UpdateRequired, clientError.Error.Code);
        Assert.AreEqual(FleetErrorHints.Update, clientError.Error.Hint);
        Assert.AreEqual(FleetErrorCodes.UpdateRequired, serverError.Error.Code);
        Assert.IsNull(keys.VerifiedContext, "No proof is checked for a client that cannot speak the protocol.");

        // The refusing side closed: nothing follows the error.
        Assert.AreEqual(FleetFrameType.Closed, await clientTransport.ReceiveAsync(new ArrayBufferWriter<byte>(), 1024, CancellationToken.None).AsTask().WaitAsync(Json.Hang));
    }

    [TestMethod]
    public async Task ARefusedProofGivesTheVerifiersErrorToTheClient()
    {
        var (clientTransport, serverTransport) = FleetInMemoryTransport.CreatePair();
        var verifier = new TestDeviceKeys("device-1") { Refusal = new FleetError(FleetErrorCodes.PendingApproval, FleetErrorHints.WaitForApproval) };
        var accept = FleetHandshake.AcceptAsync(serverTransport, new FleetHandshakeServerOptions { Protocol = new FleetProtocolRange(1, 1) }, verifier, CancellationToken.None);
        var connect = FleetHandshake.ConnectAsync(clientTransport, new FleetHandshakeClientOptions { Protocol = new FleetProtocolRange(1, 1), Client = Desktop },
            new TestDeviceKeys("device-1"), CancellationToken.None);

        var error = await Assert.ThrowsExactlyAsync<FleetHandshakeException>(() => connect.WaitAsync(Json.Hang));
        Assert.AreEqual(FleetErrorCodes.PendingApproval, error.Error.Code);
        Assert.AreEqual(FleetErrorHints.WaitForApproval, error.Error.Hint);
        await Assert.ThrowsExactlyAsync<FleetHandshakeException>(() => accept.WaitAsync(Json.Hang));
    }

    [TestMethod]
    public async Task AProofSignedWithAnotherKeyIsUnauthenticated()
    {
        var (clientTransport, serverTransport) = FleetInMemoryTransport.CreatePair();
        var accept = FleetHandshake.AcceptAsync(serverTransport, new FleetHandshakeServerOptions { Protocol = new FleetProtocolRange(1, 1) }, new TestDeviceKeys("device-1"), CancellationToken.None);
        var connect = FleetHandshake.ConnectAsync(clientTransport, new FleetHandshakeClientOptions { Protocol = new FleetProtocolRange(1, 1), Client = Desktop },
            new TestDeviceKeys("device-1", "another-key"), CancellationToken.None);

        var error = await Assert.ThrowsExactlyAsync<FleetHandshakeException>(() => connect.WaitAsync(Json.Hang));
        Assert.AreEqual(FleetErrorCodes.Unauthenticated, error.Error.Code);
        await Assert.ThrowsExactlyAsync<FleetHandshakeException>(() => accept.WaitAsync(Json.Hang));
    }

    [TestMethod]
    public async Task AFirstFrameThatIsNotAHelloIsAProtocolError()
    {
        var (clientTransport, serverTransport) = FleetInMemoryTransport.CreatePair();
        var accept = FleetHandshake.AcceptAsync(serverTransport, new FleetHandshakeServerOptions { Protocol = new FleetProtocolRange(1, 1) }, new TestDeviceKeys("device-1"), CancellationToken.None);
        await clientTransport.SendAsync(new FleetRequest("1", "node.info").Text().Utf8(), FleetFrameType.Text, CancellationToken.None);

        var error = await Assert.ThrowsExactlyAsync<FleetHandshakeException>(() => accept.WaitAsync(Json.Hang));
        Assert.AreEqual(FleetErrorCodes.ProtocolError, error.Error.Code);
    }

    [TestMethod]
    public async Task AClientThatNeverAnswersTimesOut()
    {
        var (_, serverTransport) = FleetInMemoryTransport.CreatePair();
        var options = new FleetHandshakeServerOptions { Protocol = new FleetProtocolRange(1, 1), Timeout = TimeSpan.FromMilliseconds(100) };
        await Assert.ThrowsExactlyAsync<TimeoutException>(() => FleetHandshake.AcceptAsync(serverTransport, options, new TestDeviceKeys("d"), CancellationToken.None).WaitAsync(Json.Hang));
    }
}
