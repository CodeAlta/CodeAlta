using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodeAlta.Fleet.Protocol.Tests;

/// <summary>A stand-in for the device keys: an HMAC of the signed bytes with a key both sides know.</summary>
internal sealed class TestDeviceKeys : IFleetHandshakeSigner, IFleetHandshakeVerifier
{
    private readonly byte[] _key;

    public TestDeviceKeys(string deviceId, string key = "test-key")
    {
        DeviceId = deviceId;
        _key = Encoding.UTF8.GetBytes(key);
    }

    public string DeviceId { get; }

    /// <summary>The refusal the verifier answers with, or null to check the proof.</summary>
    public FleetError? Refusal { get; init; }

    public FleetProofContext? VerifiedContext { get; private set; }

    public ValueTask<string> SignAsync(FleetProofContext context, CancellationToken cancellationToken)
        => ValueTask.FromResult(Sign(context));

    public ValueTask<FleetProofVerification> VerifyAsync(FleetHello hello, FleetProofContext context, CancellationToken cancellationToken)
    {
        VerifiedContext = context;
        if (Refusal is not null) return ValueTask.FromResult(FleetProofVerification.Reject(Refusal));
        var accepted = hello.Device.Id == DeviceId && hello.Device.Proof == Sign(context);
        return ValueTask.FromResult(accepted
            ? FleetProofVerification.Accept()
            : FleetProofVerification.Reject(new FleetError(FleetErrorCodes.Unauthenticated, FleetErrorHints.Reauthenticate)));
    }

    private string Sign(FleetProofContext context) => Convert.ToBase64String(HMACSHA256.HashData(_key, context.GetSignedBytes()));
}

/// <summary>Two started connections over an in-memory pair.</summary>
internal sealed class ConnectionPair : IAsyncDisposable
{
    private ConnectionPair(FleetConnection client, FleetConnection server)
    {
        Client = client;
        Server = server;
    }

    public FleetConnection Client { get; }

    public FleetConnection Server { get; }

    public static ConnectionPair Start(Action<FleetConnection>? configureServer = null, Action<FleetConnection>? configureClient = null,
        FleetConnectionOptions? clientOptions = null, FleetConnectionOptions? serverOptions = null)
    {
        var (first, second) = FleetInMemoryTransport.CreatePair();
        var client = new FleetConnection(first, clientOptions);
        var server = new FleetConnection(second, serverOptions);
        configureClient?.Invoke(client);
        configureServer?.Invoke(server);
        server.Start();
        client.Start();
        return new ConnectionPair(client, server);
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        await Server.DisposeAsync();
    }
}

/// <summary>A transport that counts the frames it receives.</summary>
internal sealed class CountingTransport(IFleetTransport inner) : IFleetTransport
{
    private int _received;

    public int Received => Volatile.Read(ref _received);

    public ValueTask SendAsync(ReadOnlyMemory<byte> frame, FleetFrameType type, CancellationToken cancellationToken) => inner.SendAsync(frame, type, cancellationToken);

    public async ValueTask<FleetFrameType> ReceiveAsync(IBufferWriter<byte> destination, int maxFrameBytes, CancellationToken cancellationToken)
    {
        var type = await inner.ReceiveAsync(destination, maxFrameBytes, cancellationToken);
        if (type != FleetFrameType.Closed) Interlocked.Increment(ref _received);
        return type;
    }

    public ValueTask CloseAsync(string? description, CancellationToken cancellationToken) => inner.CloseAsync(description, cancellationToken);

    public void Abort() => inner.Abort();

    public ValueTask DisposeAsync() => inner.DisposeAsync();
}

internal static class Json
{
    public static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    public static string Text(this FleetMessage message) => Encoding.UTF8.GetString(FleetMessageSerializer.Serialize(message));

    public static byte[] Utf8(this string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>A deadline that only tells a hang: the tests never rely on it for their result.</summary>
    public static readonly TimeSpan Hang = TimeSpan.FromSeconds(30);
}
