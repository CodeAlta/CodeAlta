using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Server.Api;
using CodeAlta.Server.Hosting;
using CodeAlta.Server.Services;
using Microsoft.Extensions.DependencyInjection;

namespace CodeAlta.Server.Tests;

/// <summary>A clock the test moves.</summary>
internal sealed class TestClock(DateTimeOffset start) : TimeProvider
{
    private long _ticks = start.UtcTicks;

    public DateTimeOffset Now => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
}

/// <summary>A folder of its own, deleted at the end of the test.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory() => Directory.CreateDirectory(Path);

    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "altaserver-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A file another process of the test machine holds for an instant: the temporary folder is cleaned later.
        }
    }
}

/// <summary>A device of a test: a P-256 key, as CodeAlta's devices make one.</summary>
internal sealed class TestDevice : IFleetHandshakeSigner, IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public TestDevice(string name)
    {
        Name = name;
        PublicKey = Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());
        FleetDevicePublicKey.TryParse(PublicKey, out var parsed);
        DeviceId = parsed!.DeviceId;
    }

    public string Name { get; }

    public string PublicKey { get; }

    public string DeviceId { get; }

    public string Sign(ReadOnlySpan<byte> data)
        => Base64Url.EncodeToString(_key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    public ValueTask<string> SignAsync(FleetProofContext context, CancellationToken cancellationToken) => ValueTask.FromResult(Sign(context.GetSignedBytes()));

    public FleetSignedDeviceListEntry SignEntry(FleetDeviceListEntryPayload payload)
    {
        var text = FleetDeviceListEntries.SerializePayload(payload);
        return new FleetSignedDeviceListEntry(text, Sign(FleetDeviceListEntries.GetSignedBytes(text)));
    }

    public FleetSignedDeviceListEntry FirstEntry(DateTimeOffset time)
        => SignEntry(new FleetDeviceListEntryPayload(1, null, 1, null, FleetDeviceListOperations.Add, DeviceId, PublicKey, FleetDeviceKinds.Controller, null, DeviceId, time));

    public FleetSignedTelemetrySettings SignTelemetry(FleetTelemetrySettings settings)
    {
        var text = FleetTelemetrySettingsDocuments.Serialize(settings);
        return new FleetSignedTelemetrySettings(text, Sign(FleetTelemetrySettingsDocuments.GetSignedBytes(text)));
    }

    public void Dispose() => _key.Dispose();
}

/// <summary>What the server answered: the status and the JSON body.</summary>
internal sealed record ApiAnswer(HttpStatusCode Status, JsonElement Body)
{
    public string? Code => Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty("code", out var code) ? code.GetString() : null;

    public T Read<T>() => Body.Deserialize<T>(JsonOptions)!;

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolverChain = { ServerApiJsonContext.Default, FleetServerJsonContext.Default, FleetDeviceListJsonContext.Default, FleetTelemetryJsonContext.Default },
    };
}

/// <summary>A server on the loopback, with a data root and a clock of its own.</summary>
internal sealed class ServerFixture : IAsyncDisposable
{
    private readonly TempDirectory? _ownedRoot;

    private ServerFixture(TempDirectory? ownedRoot, CoordinatorServer server, TestClock clock)
    {
        _ownedRoot = ownedRoot;
        Server = server;
        Clock = clock;
        BaseAddress = server.Addresses.First(static address => address.Scheme == "http");
        Http = new HttpClient { BaseAddress = BaseAddress, Timeout = TimeSpan.FromSeconds(30) };
    }

    public CoordinatorServer Server { get; }

    public TestClock Clock { get; }

    public Uri BaseAddress { get; }

    public HttpClient Http { get; }

    public string SetupCode => Server.SetupCode?.Code ?? throw new InvalidOperationException("The server has an owner.");

    /// <summary>Starts a server; a data root given stays the caller's, one made here is deleted with the fixture.</summary>
    public static async Task<ServerFixture> StartAsync(TempDirectory? root = null, TestClock? clock = null, Control.RelayOptions? relay = null,
        Func<FleetUpdateStatus?>? updateStatus = null, Func<string, string?>? environment = null, TimeSpan? telemetryInterval = null)
    {
        var owned = root is null ? new TempDirectory() : null;
        root ??= owned!;
        clock ??= new TestClock(DateTimeOffset.UtcNow);
        var server = await CoordinatorServer.StartAsync(new CoordinatorServerOptions
        {
            DataRoot = root.Path,
            Urls = ["http://127.0.0.1:0"],
            TimeProvider = clock,
            Relay = relay ?? new Control.RelayOptions(),
            ConfigureLogging = static _ => { },
            UpdateStatus = updateStatus,
            // The OTEL_* variables of the machine that runs the tests never reach a test's server.
            EnvironmentVariable = environment ?? (static _ => null),
            TelemetryExportInterval = telemetryInterval,
        });
        return new ServerFixture(owned, server, clock);
    }

    public T GetService<T>() where T : notnull => Server.Services.GetRequiredService<T>();

    /// <summary>Sends a request signed by a device, with the time of the clock unless one is given.</summary>
    public async Task<ApiAnswer> SendAsync(TestDevice? device, HttpMethod method, string path, object? body = null, Action<HttpRequestMessage>? tamper = null,
        long? timestamp = null, string? nonce = null)
    {
        var bytes = body is null ? [] : JsonSerializer.SerializeToUtf8Bytes(body, body.GetType(), ApiAnswer.JsonOptions);
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = new ByteArrayContent(bytes);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        if (device is not null)
        {
            var time = timestamp ?? Clock.Now.ToUnixTimeMilliseconds();
            nonce ??= FleetSignedRequest.CreateNonce();
            var signed = FleetSignedRequest.GetSignedBytes(method.Method, path, time, nonce, FleetSignedRequest.HashBody(bytes), device.DeviceId);
            request.Headers.Add(FleetSignedRequest.DeviceHeader, device.DeviceId);
            request.Headers.Add(FleetSignedRequest.TimestampHeader, time.ToString(System.Globalization.CultureInfo.InvariantCulture));
            request.Headers.Add(FleetSignedRequest.NonceHeader, nonce);
            request.Headers.Add(FleetSignedRequest.SignatureHeader, device.Sign(signed));
        }

        tamper?.Invoke(request);
        using var response = await Http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        var json = string.IsNullOrEmpty(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
        return new ApiAnswer(response.StatusCode, json);
    }

    /// <summary>Posts bytes, signed by a device unless none is given, and gives the status of the answer.</summary>
    public async Task<HttpStatusCode> PostBytesAsync(TestDevice? device, string path, byte[] body, string contentType = "application/x-protobuf")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        if (device is not null)
        {
            var time = Clock.Now.ToUnixTimeMilliseconds();
            var nonce = FleetSignedRequest.CreateNonce();
            var signed = FleetSignedRequest.GetSignedBytes("POST", path, time, nonce, FleetSignedRequest.HashBody(body), device.DeviceId);
            request.Headers.Add(FleetSignedRequest.DeviceHeader, device.DeviceId);
            request.Headers.Add(FleetSignedRequest.TimestampHeader, time.ToString(System.Globalization.CultureInfo.InvariantCulture));
            request.Headers.Add(FleetSignedRequest.NonceHeader, nonce);
            request.Headers.Add(FleetSignedRequest.SignatureHeader, device.Sign(signed));
        }

        using var response = await Http.SendAsync(request);
        return response.StatusCode;
    }

    /// <summary>Sets the server up with an owner device.</summary>
    public async Task<SetupResponse> SetUpAsync(TestDevice owner)
    {
        var answer = await SendAsync(owner, HttpMethod.Post, "/api/v1/setup",
            new SetupRequest(SetupCode, "Alex", owner.Name, owner.PublicKey, owner.FirstEntry(Clock.Now)));
        Assert.AreEqual(HttpStatusCode.Created, answer.Status, answer.Body.ToString());
        return answer.Read<SetupResponse>();
    }

    /// <summary>Enrolls a device (a node unless told otherwise) with a new token of a controller.</summary>
    public async Task<DeviceDto> EnrollAsync(TestDevice controller, TestDevice node, string? kind = null)
    {
        var token = await SendAsync(controller, HttpMethod.Post, "/api/v1/enrollment-tokens", new EnrollmentTokenRequest(600, "test"));
        Assert.AreEqual(HttpStatusCode.Created, token.Status, token.Body.ToString());
        var enrolled = await SendAsync(node, HttpMethod.Post, "/api/v1/enroll", new EnrollRequest(token.Read<EnrollmentTokenResponse>().Token, node.PublicKey, node.Name, kind));
        Assert.AreEqual(HttpStatusCode.Created, enrolled.Status, enrolled.Body.ToString());
        return enrolled.Read<EnrollResponse>().Device;
    }

    /// <summary>Makes the entry that follows the last one of the device list, signed by a controller.</summary>
    public async Task<FleetSignedDeviceListEntry> NextEntryAsync(TestDevice signer, string operation, string deviceId, string? publicKey, string? kind, string? ownerId,
        string organizationId)
    {
        var list = await SendAsync(signer, HttpMethod.Get, "/api/v1/device-list");
        Assert.AreEqual(HttpStatusCode.OK, list.Status, list.Body.ToString());
        var last = list.Read<ListResponse<DeviceListEntryDto>>().Items[^1];
        return signer.SignEntry(new FleetDeviceListEntryPayload(1, organizationId, last.Sequence + 1, last.Hash, operation, deviceId, publicKey, kind, ownerId, signer.DeviceId,
            Clock.Now));
    }

    /// <summary>Sets up an owner and approves a node; returns the setup and the node.</summary>
    public async Task<(SetupResponse Setup, DeviceDto Node)> SetUpWithNodeAsync(TestDevice owner, TestDevice node)
    {
        var setup = await SetUpAsync(owner);
        var pending = await EnrollAsync(owner, node);
        var entry = await NextEntryAsync(owner, FleetDeviceListOperations.Add, pending.Id, node.PublicKey, FleetDeviceKinds.Node, setup.Owner.Id, setup.Organization.Id);
        var approved = await SendAsync(owner, HttpMethod.Post, $"/api/v1/devices/{pending.Id}/approve", new DeviceListChangeRequest(entry));
        Assert.AreEqual(HttpStatusCode.OK, approved.Status, approved.Body.ToString());
        return (setup, approved.Read<DeviceDto>());
    }

    /// <summary>Opens a control connection as a device; the handshake's refusal is thrown.</summary>
    public async Task<(FleetConnection Connection, FleetWelcome Welcome)> ConnectAsync(TestDevice device, Action<FleetConnection>? configure = null)
    {
        var address = new Uri(BaseAddress.ToString().Replace("http://", "ws://", StringComparison.Ordinal).TrimEnd('/') + FleetServerMethods.ControlPath);
        var transport = await FleetWebSocketTransport.ConnectAsync(address, null, CancellationToken.None);
        FleetWelcome welcome;
        try
        {
            welcome = await FleetHandshake.ConnectAsync(transport, new FleetHandshakeClientOptions
            {
                Protocol = new FleetProtocolRange(1, 1),
                Client = new FleetClientInfo("node", "4.0.0", "windows"),
            }, device, CancellationToken.None);
        }
        catch
        {
            await transport.DisposeAsync();
            throw;
        }

        var connection = new FleetConnection(transport, FleetConnectionOptions.FromPolicy(welcome.Policy));
        configure?.Invoke(connection);
        connection.Start();
        return (connection, welcome);
    }

    public static RequestOrigin Origin(string address) => new(IPAddress.Parse(address), Guid.NewGuid().ToString("N"));

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        await Server.DisposeAsync();
        _ownedRoot?.Dispose();
    }
}

/// <summary>Bounds for waits that only tell a hang.</summary>
internal static class Waits
{
    public static readonly TimeSpan Hang = TimeSpan.FromSeconds(30);
}

/// <summary>Bytes of a text, for tests.</summary>
internal static class TestText
{
    public static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);
}
