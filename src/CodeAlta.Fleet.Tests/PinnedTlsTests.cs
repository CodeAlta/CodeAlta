using System.Security.Authentication;
using System.Security.Cryptography;
using CodeAlta.Fleet.Identity;

namespace CodeAlta.Fleet.Tests;

[TestClass]
public sealed class PinnedTlsTests
{
    // Only tells a hang: the handshakes themselves take milliseconds.
    private static readonly TimeSpan HangDeadline = TestTime.Minutes(2);

    [TestMethod]
    public async Task HandshakeSucceedsWithTheRightPins()
    {
        using var fixture = new Fixture();
        fixture.ServerTrusts.Add("Laptop", fixture.Client.Fingerprint, DevicePermissions.View | DevicePermissions.Drive);
        fixture.ClientTrusts.Add("Mac mini", fixture.Server.Fingerprint, DevicePermissions.None);

        var (server, client) = await fixture.ConnectAsync(fixture.ServerOptions(), fixture.ClientOptions(fixture.Server.Fingerprint, fixture.ClientTrusts));
        await using (server)
        await using (client)
        {
            Assert.AreEqual(fixture.Client.Fingerprint, server.RemoteFingerprint);
            Assert.AreEqual(fixture.Client.DeviceId, server.RemoteDeviceId);
            Assert.AreEqual(DevicePermissions.View | DevicePermissions.Drive, server.RemoteDevice?.Permissions);
            Assert.AreEqual(fixture.Server.Fingerprint, client.RemoteFingerprint);
            Assert.AreEqual("Mac mini", client.RemoteDevice?.Name);
            Assert.IsTrue(server.Protocol is SslProtocols.Tls13 or SslProtocols.Tls12, server.Protocol.ToString());

            await client.Stream.WriteAsync("ping"u8.ToArray());
            await client.Stream.FlushAsync();
            var received = new byte[4];
            await server.Stream.ReadExactlyAsync(received);
            CollectionAssert.AreEqual("ping"u8.ToArray(), received);

            await server.Stream.WriteAsync("pong"u8.ToArray());
            await server.Stream.FlushAsync();
            await client.Stream.ReadExactlyAsync(received);
            CollectionAssert.AreEqual("pong"u8.ToArray(), received);
        }
    }

    [TestMethod]
    public async Task ClientRefusesAServerWithAnotherKey()
    {
        using var fixture = new Fixture();
        fixture.ServerTrusts.Add("Laptop", fixture.Client.Fingerprint, DevicePermissions.View);
        using var other = DeviceIdentity.LoadOrCreate(Path.Combine(fixture.Folder.Path, "other"));

        var (serverFailure, clientFailure) = await fixture.FailAsync(fixture.ServerOptions(), fixture.ClientOptions(other.Fingerprint, trust: null));

        Assert.AreEqual(DeviceAuthenticationFailure.ServerKeyMismatch, AssertDeviceFailure(clientFailure).Failure);
        Assert.IsNotNull(serverFailure);
    }

    [TestMethod]
    public async Task ClientRefusesARevokedServer()
    {
        using var fixture = new Fixture();
        fixture.ServerTrusts.Add("Laptop", fixture.Client.Fingerprint, DevicePermissions.View);
        var server = fixture.ClientTrusts.Add("Mac mini", fixture.Server.Fingerprint, DevicePermissions.None);
        Assert.IsTrue(fixture.ClientTrusts.Revoke(server.DeviceId));

        var (serverFailure, clientFailure) = await fixture.FailAsync(fixture.ServerOptions(), fixture.ClientOptions(fixture.Server.Fingerprint, fixture.ClientTrusts));

        Assert.AreEqual(DeviceAuthenticationFailure.ServerRevoked, AssertDeviceFailure(clientFailure).Failure);
        Assert.IsNotNull(serverFailure);
    }

    [TestMethod]
    public async Task ServerRefusesAnUnpairedClient()
    {
        using var fixture = new Fixture();

        var (serverFailure, clientFailure) = await fixture.FailAsync(fixture.ServerOptions(), fixture.ClientOptions(fixture.Server.Fingerprint, trust: null));

        Assert.AreEqual(DeviceAuthenticationFailure.ClientNotTrusted, AssertDeviceFailure(serverFailure).Failure);
        Assert.AreEqual(DeviceAuthenticationFailure.ClientNotTrusted, AssertDeviceFailure(clientFailure).Failure);
    }

    [TestMethod]
    public async Task ServerRefusesARevokedClientEvenForAPairing()
    {
        using var fixture = new Fixture();
        var client = fixture.ServerTrusts.Add("Laptop", fixture.Client.Fingerprint, DevicePermissions.View);
        fixture.ServerTrusts.Revoke(client.DeviceId);

        var (serverFailure, clientFailure) = await fixture.FailAsync(
            fixture.ServerOptions(allowUnpaired: true), fixture.ClientOptions(fixture.Server.Fingerprint, trust: null));

        Assert.AreEqual(DeviceAuthenticationFailure.ClientRevoked, AssertDeviceFailure(serverFailure).Failure);
        Assert.AreEqual(DeviceAuthenticationFailure.ClientRevoked, AssertDeviceFailure(clientFailure).Failure);
    }

    [TestMethod]
    public async Task PairingAcceptsAProvenUnknownClient()
    {
        using var fixture = new Fixture();

        var (server, client) = await fixture.ConnectAsync(
            fixture.ServerOptions(allowUnpaired: true), fixture.ClientOptions(fixture.Server.Fingerprint, trust: null));
        await using (server)
        await using (client)
        {
            Assert.AreEqual(fixture.Client.Fingerprint, server.RemoteFingerprint);
            Assert.IsNull(server.RemoteDevice);
            Assert.IsNull(client.RemoteDevice);
        }
    }

    [TestMethod]
    public void ProofIsBoundToTheChannelAndTheNonce()
    {
        using var fixture = new Fixture();
        using var certificate = fixture.Server.CreateTlsCertificate();
        using var otherCertificate = fixture.Client.CreateTlsCertificate();
        var binding = DeviceProof.ComputeChannelBinding(certificate);
        var nonce = RandomNumberGenerator.GetBytes(DeviceProof.NonceLength);
        var key = fixture.Client.ExportSubjectPublicKeyInfo();

        var signature = DeviceProof.Sign(fixture.Client, binding, nonce);

        Assert.IsTrue(DeviceProof.Verify(key, binding, nonce, signature, out var fingerprint));
        Assert.AreEqual(fixture.Client.Fingerprint, fingerprint);
        Assert.IsFalse(DeviceProof.Verify(key, DeviceProof.ComputeChannelBinding(otherCertificate), nonce, signature, out _));
        Assert.IsFalse(DeviceProof.Verify(key, binding, RandomNumberGenerator.GetBytes(DeviceProof.NonceLength), signature, out _));
        Assert.IsFalse(DeviceProof.Verify(fixture.Server.ExportSubjectPublicKeyInfo(), binding, nonce, signature, out _));
        Assert.IsFalse(DeviceProof.Verify([1, 2, 3], binding, nonce, signature, out _));

        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        Assert.IsFalse(DeviceProof.Verify(p384.ExportSubjectPublicKeyInfo(), binding, nonce, new byte[DeviceProof.SignatureLength], out _));
    }

    private static DeviceAuthenticationException AssertDeviceFailure(Exception? exception)
        => exception as DeviceAuthenticationException
            ?? throw new AssertFailedException($"Expected a {nameof(DeviceAuthenticationException)}, got {exception?.GetType().Name ?? "none"}: {exception}");

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Folder = new TestFolder();
            Server = DeviceIdentity.LoadOrCreate(Path.Combine(Folder.Path, "server"));
            Client = DeviceIdentity.LoadOrCreate(Path.Combine(Folder.Path, "client"));
            ServerCertificate = Server.CreateTlsCertificate();
            ServerTrusts = new TrustedDeviceStore(Path.Combine(Folder.Path, "server"), TimeProvider.System);
            ClientTrusts = new TrustedDeviceStore(Path.Combine(Folder.Path, "client"), TimeProvider.System);
        }

        public TestFolder Folder { get; }

        public DeviceIdentity Server { get; }

        public DeviceIdentity Client { get; }

        public System.Security.Cryptography.X509Certificates.X509Certificate2 ServerCertificate { get; }

        public TrustedDeviceStore ServerTrusts { get; }

        public TrustedDeviceStore ClientTrusts { get; }

        public PinnedTlsServerOptions ServerOptions(bool allowUnpaired = false)
            => new() { Certificate = ServerCertificate, TrustPolicy = ServerTrusts, AllowUnpairedClients = allowUnpaired };

        public PinnedTlsClientOptions ClientOptions(DeviceFingerprint pin, IDeviceTrustPolicy? trust)
            => new() { Identity = Client, ServerFingerprint = pin, TrustPolicy = trust };

        public async Task<(PinnedTlsConnection Server, PinnedTlsConnection Client)> ConnectAsync(PinnedTlsServerOptions server, PinnedTlsClientOptions client)
        {
            using var deadline = new CancellationTokenSource(HangDeadline);
            var (serverEnd, clientEnd) = InMemoryDuplexStream.CreatePair();
            var serverTask = PinnedTls.AuthenticateAsServerAsync(serverEnd, server, deadline.Token);
            var clientTask = PinnedTls.AuthenticateAsClientAsync(clientEnd, client, deadline.Token);
            return (await serverTask, await clientTask);
        }

        public async Task<(Exception? Server, Exception? Client)> FailAsync(PinnedTlsServerOptions server, PinnedTlsClientOptions client)
        {
            using var deadline = new CancellationTokenSource(HangDeadline);
            var (serverEnd, clientEnd) = InMemoryDuplexStream.CreatePair();
            var serverTask = Capture(PinnedTls.AuthenticateAsServerAsync(serverEnd, server, deadline.Token));
            var clientTask = Capture(PinnedTls.AuthenticateAsClientAsync(clientEnd, client, deadline.Token));
            var result = (await serverTask, await clientTask);
            Assert.IsFalse(deadline.IsCancellationRequested, "The handshake hung.");
            return result;
        }

        public void Dispose()
        {
            ServerCertificate.Dispose();
            Server.Dispose();
            Client.Dispose();
            Folder.Dispose();
        }

        private static async Task<Exception?> Capture(Task<PinnedTlsConnection> task)
        {
            try
            {
                await (await task).DisposeAsync();
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        }
    }
}
