using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using static CodeAlta.Plugin.Mobile.Tests.MobileSettingsTests;

namespace CodeAlta.Plugin.Mobile.Tests;

/// <summary>
/// The provider of the Internet: a domain name, the port 443 of the router forwarded by the user, and a certificate of Let's Encrypt (an
/// authority of the tests here) checked with the challenge tls-alpn-01 on the server of the view itself, as Let's Encrypt does it.
/// </summary>
[TestClass]
public sealed class MobileInternetTests
{
    [TestMethod]
    public void TheCertificateOfTheChallenge_IsForTheName_WithTheHashOfTheKeyAuthorization_InACriticalExtension()
    {
        using var certificate = MobileAcme.TlsAlpnCertificate("pulsar.example.com", "token.thumbprint");

        Assert.IsTrue(certificate.MatchesHostname("pulsar.example.com"));
        Assert.IsTrue(certificate.HasPrivateKey);
        var extension = certificate.Extensions[MobileAcme.AcmeIdentifierOid];
        Assert.IsNotNull(extension);
        Assert.IsTrue(extension.Critical);
        CollectionAssert.AreEqual(new byte[] { 0x04, 32 }.Concat(SHA256.HashData(Encoding.UTF8.GetBytes("token.thumbprint"))).ToArray(), extension.RawData);
    }

    [TestMethod]
    public async Task OnTheInternet_LetsEncryptChecksTheNameOnTheServerItself_AndThePhonesGetItsCertificate()
    {
        await using var world = await World.StartAsync();
        await using var fixture = world.Fixture();
        await fixture.StartAsync(FreePort.Next(), 4443);
        var (page, _) = await fixture.OpenAsync(MobilePlugin.SettingsCanvasId);
        var (tab, _) = await fixture.OpenAsync(MobilePlugin.CanvasId);

        var answer = await page.InvokeAsync<MobileSettingsChange, MobileSettingsView>("mobile.settings.set", new MobileSettingsChange("internet", world.Port, null, " Pulsar.Example.com "));

        Assert.IsNull(answer.Error);
        Assert.AreEqual(("internet", "pulsar.example.com"), (answer.Provider.Id, answer.Host));
        await WaitAsync(() => fixture.Plugin.Settings().Provider.State is not ("pending" or "starting"), "Let's Encrypt gives the certificate");
        var settings = fixture.Plugin.Settings();
        Assert.AreEqual(("serving", "https://pulsar.example.com"), (settings.Provider.State, settings.Address), settings.Provider.Detail);
        Assert.AreEqual(1, world.Checks, "Let's Encrypt checked the name once, over TLS with acme-tls/1, on the server of the view");
        Assert.AreEqual(("dns", "pulsar.example.com"), (world.Acme.Orders.Single().Type, world.Acme.Orders.Single().Value));

        var status = await tab.InvokeAsync<MobileEmpty, MobileStatus>("mobile.status", new MobileEmpty());
        StringAssert.StartsWith(status.ConnectUrl, "https://pulsar.example.com/#k=", "the code holds the name, on port 443, and the pairing token");
        using (var phone = world.Phone())
        {
            using var served = await phone.GetAsync("/");
            Assert.AreEqual(HttpStatusCode.OK, served.StatusCode, "the phone page is served with the certificate of Let's Encrypt and its chain");
        }

        Assert.IsFalse(await world.AnswersChallengeAsync("pulsar.example.com", "anything"), "once checked, the server answers no challenge");

        // A restart keeps the certificate: Let's Encrypt is not asked again.
        await fixture.DisposeAsync();
        await using var restarted = world.Fixture(fixture.State);
        await restarted.StartAsync(null);
        Assert.AreEqual("serving", restarted.Plugin.Settings().Provider.State, restarted.Plugin.Settings().Provider.Detail);
        Assert.AreEqual(1, world.Acme.Orders.Count);
        using var again = world.Phone();
        Assert.AreEqual(HttpStatusCode.OK, (await again.GetAsync("/")).StatusCode);
    }

    [TestMethod]
    public async Task ACheckThatFails_SaysWhatToCheck_TheNameAndPort443()
    {
        await using var world = await World.StartAsync();
        world.Acme.Verify = (_, _, _) => Task.FromResult(false);
        await using var fixture = world.Fixture();
        await fixture.StartAsync(FreePort.Next(), 4443);
        var (page, _) = await fixture.OpenAsync(MobilePlugin.SettingsCanvasId);

        await page.InvokeAsync<MobileSettingsChange, MobileSettingsView>("mobile.settings.set", new MobileSettingsChange("internet", world.Port, null, "pulsar.example.com"));
        await WaitAsync(() => fixture.Plugin.Settings().Provider.State is not ("pending" or "starting"), "Let's Encrypt answers");

        var settings = fixture.Plugin.Settings();
        Assert.AreEqual("certificate-failed", settings.Provider.State);
        StringAssert.Contains(settings.Provider.Detail, "Timeout during connect");
        StringAssert.Contains(settings.Provider.Detail, $"port 443 of the router is forwarded to port {world.Port} of this computer");
        Assert.IsNull(settings.Address);
    }

    [TestMethod]
    public async Task ANameThatIsNotOne_IsRefused_AndAnUnknownNameIsSaid()
    {
        await using var world = await World.StartAsync();
        await using var fixture = world.Fixture();
        await fixture.StartAsync(FreePort.Next(), 4443);
        var saved = fixture.State.Text;
        var (page, _) = await fixture.OpenAsync(MobilePlugin.SettingsCanvasId);

        foreach (var (name, words) in new (string?, string)[] { (null, "Give the domain name"), ("", "Give the domain name"), ("203.0.113.7", "not a domain name"),
            ("pulsar", "not a domain name"), ("https://pulsar.example.com", "not a domain name"), ("-bad.example.com", "not a domain name") })
        {
            var answer = await page.InvokeAsync<MobileSettingsChange, MobileSettingsView>("mobile.settings.set", new MobileSettingsChange("internet", world.Port, null, name));
            Assert.IsNotNull(answer.Error, name);
            StringAssert.Contains(answer.Error, words, name);
            Assert.AreEqual("tailscale", answer.Provider.Id);
        }

        Assert.AreEqual(saved, fixture.State.Text, "nothing is saved");

        await page.InvokeAsync<MobileSettingsChange, MobileSettingsView>("mobile.settings.set", new MobileSettingsChange("internet", world.Port, null, "missing.example.com"));
        await WaitAsync(() => fixture.Plugin.Settings().Provider.State is not ("pending" or "starting"), "the name is looked up");
        Assert.AreEqual("dns-missing", fixture.Plugin.Settings().Provider.State);
        Assert.IsEmpty(world.Acme.Orders, "Let's Encrypt is not asked for a name that points nowhere");
    }

    [TestMethod]
    public void AClientHelloAsksForAcme_OnlyWhenItsAlpnListsAcmeTls()
    {
        Assert.IsTrue(MobileTls.AsksAcme(ClientHello("h2", "acme-tls/1")));
        Assert.IsFalse(MobileTls.AsksAcme(ClientHello("h2", "http/1.1")));
        Assert.IsFalse(MobileTls.AsksAcme(ClientHello()));
        Assert.IsFalse(MobileTls.AsksAcme([22, 3, 1, 0, 5, 1, 0, 0]), "a ClientHello cut short asks for nothing");
        Assert.IsFalse(MobileTls.AsksAcme([]));
    }

    // A TLS ClientHello record with the ALPN names given, after an extension of another type.
    private static byte[] ClientHello(params string[] protocols)
    {
        var extensions = new List<byte> { 0, 0, 0, 0 }; // server_name, empty
        if (protocols.Length > 0)
        {
            var list = protocols.SelectMany(static protocol => new[] { (byte)protocol.Length }.Concat(Encoding.ASCII.GetBytes(protocol))).ToArray();
            extensions.AddRange([0, 16, (byte)((list.Length + 2) >> 8), (byte)(list.Length + 2), (byte)(list.Length >> 8), (byte)list.Length, .. list]);
        }

        var body = new List<byte> { 3, 3 };
        body.AddRange(new byte[32]);                 // random
        body.Add(0);                                  // session id
        body.AddRange([0, 2, 0x13, 0x01]);            // one cipher suite
        body.AddRange([1, 0]);                        // compression
        body.AddRange([(byte)(extensions.Count >> 8), (byte)extensions.Count, .. extensions]);
        var handshake = new List<byte> { 1, 0, (byte)(body.Count >> 8), (byte)body.Count };
        handshake.AddRange(body);
        return [22, 3, 1, (byte)(handshake.Count >> 8), (byte)handshake.Count, .. handshake];
    }

    /// <summary>
    /// The Internet of a test: a name that resolves, an ACME authority that checks it over TLS with acme-tls/1 on the port of the server
    /// (where port 443 of the router would lead), and a phone that trusts the authority of the tests.
    /// </summary>
    private sealed class World : IAsyncDisposable
    {
        private int _checks;

        private World(TestCertificates certificates, FakeAcme acme)
        {
            Certificates = certificates;
            Acme = acme;
            Acme.Verify = async (name, _, keyAuthorization) =>
            {
                Interlocked.Increment(ref _checks);
                return await AnswersChallengeAsync(name, keyAuthorization);
            };
        }

        internal TestCertificates Certificates { get; }

        internal FakeAcme Acme { get; }

        internal FakeTailscaleCli Cli { get; } = new(installed: false);

        internal int Port { get; } = FreePort.Next();

        internal int Checks => Volatile.Read(ref _checks);

        internal static async Task<World> StartAsync()
        {
            var certificates = new TestCertificates();
            return new World(certificates, await FakeAcme.StartAsync(certificates));
        }

        internal Fixture Fixture(MemoryState? state = null)
            => new(Cli, state, plugin: new MobilePlugin(Cli, new FakeNoTunnel(), new MobileInternetOptions(Acme.Directory, new HttpClient(new SocketsHttpHandler { UseProxy = false }),
                (name, _) => Task.FromResult(name.StartsWith("missing.", StringComparison.Ordinal) ? Array.Empty<IPAddress>() : [IPAddress.Parse("203.0.113.7")]))));

        /// <summary>
        /// Checks the challenge as Let's Encrypt does: a TLS handshake with the name and acme-tls/1, which must be the protocol chosen, and a
        /// certificate whose critical extension acmeIdentifier holds the hash of the key authorization.
        /// </summary>
        internal async Task<bool> AnswersChallengeAsync(string name, string keyAuthorization)
        {
            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(IPAddress.Loopback, Port);
                await using var ssl = new SslStream(tcp.GetStream());
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = name, ApplicationProtocols = [new SslApplicationProtocol(MobileTls.AcmeProtocol)], RemoteCertificateValidationCallback = (_, _, _, _) => true,
                });
                if (ssl.NegotiatedApplicationProtocol.ToString() != MobileTls.AcmeProtocol || ssl.RemoteCertificate is null) return false;
                using var certificate = X509CertificateLoader.LoadCertificate(ssl.RemoteCertificate.GetRawCertData());
                var extension = certificate.Extensions[MobileAcme.AcmeIdentifierOid];
                return extension is { Critical: true } && certificate.MatchesHostname(name)
                    && extension.RawData.AsSpan(2).SequenceEqual(SHA256.HashData(Encoding.UTF8.GetBytes(keyAuthorization)));
            }
            catch (Exception exception) when (exception is IOException or SocketException or System.Security.Authentication.AuthenticationException)
            {
                return false;
            }
        }

        // A phone on the Internet: it opens https://pulsar.example.com, port 443, which the router leads to the port of the server.
        internal HttpClient Phone()
        {
            var authority = Certificates.Authority;
            var port = Port;
            return new HttpClient(new SocketsHttpHandler
            {
                UseProxy = false,
                ConnectCallback = async (_, ct) =>
                {
                    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    await socket.ConnectAsync(IPAddress.Loopback, port, ct);
                    return new NetworkStream(socket, ownsSocket: true);
                },
                SslOptions = new SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = (_, presented, _, errors) =>
                    {
                        if (presented is null || (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != 0) return false;
                        using var chain = new X509Chain();
                        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                        chain.ChainPolicy.CustomTrustStore.Add(authority);
                        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                        return chain.Build(X509CertificateLoader.LoadCertificate(presented.GetRawCertData()));
                    },
                },
            }) { BaseAddress = new Uri("https://pulsar.example.com") };
        }

        public async ValueTask DisposeAsync()
        {
            await Acme.DisposeAsync();
            Certificates.Dispose();
        }
    }

    // A Dev Tunnels command line that is not there: the tests of the Internet do not use it.
    private sealed class FakeNoTunnel : IMobileDevTunnelCli
    {
        public bool IsInstalled => false;

        public Task<MobileCliResult> RunAsync(IReadOnlyList<string> args, TimeSpan patience, CancellationToken cancellationToken) => Task.FromResult(new MobileCliResult(-1, ""));

        public IMobileHostedProcess Start(IReadOnlyList<string> args) => throw new InvalidOperationException("devtunnel is not installed.");

        public Task<int> RunInWindowAsync(IReadOnlyList<string> args, TimeSpan patience, CancellationToken cancellationToken) => Task.FromResult(-1);
    }

    /// <summary>
    /// An ACME authority of the tests: it checks the signature, the nonce and the URL of each request, checks the answer of the challenge
    /// <c>tls-alpn-01</c> with <see cref="Verify"/>, and signs the certificate with the authority of <see cref="TestCertificates"/>.
    /// </summary>
    internal sealed class FakeAcme : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly int _port;
        private readonly TestCertificates _certificates;
        private readonly ConcurrentDictionary<string, ECDsa> _keys = new();
        private readonly ConcurrentDictionary<string, bool> _nonces = new();
        private readonly ConcurrentDictionary<int, OrderState> _orders = new();
        private int _accounts;
        private int _counter;

        private FakeAcme(WebApplication app, int port, TestCertificates certificates)
        {
            _app = app;
            _port = port;
            _certificates = certificates;
        }

        /// <summary>Checks the answer of a challenge: the identifier, the token and the key authorization expected.</summary>
        internal Func<string, string, string, Task<bool>> Verify { get; set; } = (_, _, _) => Task.FromResult(false);

        /// <summary>Refuses the nonce of the next request, once.</summary>
        internal bool RefuseNextNonce { get; set; }

        /// <summary>How long the certificates last.</summary>
        internal TimeSpan Lifetime { get; set; } = TimeSpan.FromDays(90);

        internal int Accounts => Volatile.Read(ref _accounts);

        internal IReadOnlyList<OrderState> Orders => [.. _orders.OrderBy(static pair => pair.Key).Select(static pair => pair.Value)];

        internal Uri Directory => new(Url("/directory"));

        internal string Url(string path) => $"http://127.0.0.1:{_port}{path}";

        internal MobileAcme Client() => new(new HttpClient(new SocketsHttpHandler { UseProxy = false }), Directory, (_, _) => Task.Delay(10));

        internal static async Task<FakeAcme> StartAsync(TestCertificates certificates)
        {
            var port = FreePort.Next();
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, port));
            var app = builder.Build();
            var acme = new FakeAcme(app, port, certificates);
            acme.Map();
            await app.StartAsync();
            return acme;
        }

        private void Map()
        {
            _app.MapGet("/directory", () => Results.Json(new Dictionary<string, string>
            {
                ["newNonce"] = Url("/nonce"), ["newAccount"] = Url("/account"), ["newOrder"] = Url("/order"),
            }));
            _app.MapMethods("/nonce", ["HEAD", "GET"], (HttpContext context) => { Nonce(context); return Results.Ok(); });
            _app.MapPost("/{**path}", async (HttpContext context, string path) =>
            {
                Nonce(context);
                var (payload, kid, problem) = await ReadAsync(context);
                if (problem is not null) return problem;
                return path switch
                {
                    "account" => Results.Created(Url("/acct/" + kid), new JsonObject { ["status"] = "valid" }),
                    "order" => NewOrder(payload!),
                    _ when path.StartsWith("order/", StringComparison.Ordinal) => Results.Json(OrderJson(Order(path))),
                    _ when path.StartsWith("authz/", StringComparison.Ordinal) => Results.Json(AuthorizationJson(Order(path))),
                    _ when path.StartsWith("chall/", StringComparison.Ordinal) => await ChallengeAsync(Order(path), kid!),
                    _ when path.StartsWith("finalize/", StringComparison.Ordinal) => Finalize(Order(path), payload!),
                    _ when path.StartsWith("cert/", StringComparison.Ordinal) => Results.Text(Order(path).Chain!, "application/pem-certificate-chain"),
                    _ => Results.NotFound(),
                };
            });
        }

        private OrderState Order(string path) => _orders[int.Parse(path[(path.IndexOf('/', StringComparison.Ordinal) + 1)..], System.Globalization.CultureInfo.InvariantCulture)];

        private void Nonce(HttpContext context)
        {
            var nonce = MobileAcme.Base64Url(RandomNumberGenerator.GetBytes(16));
            _nonces[nonce] = true;
            context.Response.Headers["Replay-Nonce"] = nonce;
        }

        // Checks a JWS as an authority does: a nonce it gave once, the URL of the request, the key of the account or its own for a new one.
        private async Task<(JsonObject? Payload, string? Kid, IResult? Problem)> ReadAsync(HttpContext context)
        {
            if (context.Request.ContentType != "application/jose+json") return (null, null, Fail("malformed", "Not JOSE.", 415));
            var body = JsonNode.Parse(await new StreamReader(context.Request.Body).ReadToEndAsync())!.AsObject();
            var protectedPart = (string)body["protected"]!;
            var payloadPart = (string)body["payload"]!;
            var header = JsonNode.Parse(Decode(protectedPart))!.AsObject();
            if (RefuseNextNonce)
            {
                RefuseNextNonce = false;
                return (null, null, Fail("badNonce", "The nonce is old."));
            }

            if (!_nonces.TryRemove((string)header["nonce"]!, out _)) return (null, null, Fail("badNonce", "Unknown nonce."));
            if ((string)header["url"]! != Url(context.Request.Path)) return (null, null, Fail("unauthorized", "The URL is not the one of the request."));
            if ((string)header["alg"]! != "ES256") return (null, null, Fail("badSignatureAlgorithm", "ES256 only."));
            string kid;
            ECDsa key;
            if (header["jwk"] is JsonObject jwk)
            {
                if (context.Request.Path != "/account") return (null, null, Fail("malformed", "A key is given to make an account only."));
                key = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = Decode((string)jwk["x"]!), Y = Decode((string)jwk["y"]!) } });
                var thumbprint = MobileAcme.Thumbprint(key);
                var known = _keys.FirstOrDefault(pair => MobileAcme.Thumbprint(pair.Value) == thumbprint).Key;
                kid = known ?? Interlocked.Increment(ref _accounts).ToString(System.Globalization.CultureInfo.InvariantCulture);
                _keys.TryAdd(kid, key);
            }
            else
            {
                kid = ((string)header["kid"]!).Split('/')[^1];
                if (!_keys.TryGetValue(kid, out key!)) return (null, null, Fail("accountDoesNotExist", "Unknown account."));
            }

            if (!key.VerifyData(Encoding.ASCII.GetBytes(protectedPart + "." + payloadPart), Decode((string)body["signature"]!), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                return (null, null, Fail("unauthorized", "The signature is wrong."));
            return (payloadPart.Length == 0 ? null : JsonNode.Parse(Decode(payloadPart))!.AsObject(), kid, null);
        }

        private IResult NewOrder(JsonObject payload)
        {
            var identifier = payload["identifiers"]![0]!.AsObject();
            var id = Interlocked.Increment(ref _counter);
            var order = new OrderState((string)identifier["type"]!, (string)identifier["value"]!, (string?)payload["profile"], MobileAcme.Base64Url(RandomNumberGenerator.GetBytes(16)));
            _orders[id] = order;
            order.Id = id;
            return Results.Created(Url($"/order/{id}"), OrderJson(order));
        }

        private JsonObject OrderJson(OrderState order)
        {
            var json = new JsonObject
            {
                ["status"] = order.Status, ["authorizations"] = new JsonArray(Url($"/authz/{order.Id}")), ["finalize"] = Url($"/finalize/{order.Id}"),
            };
            if (order.Chain is not null) json["certificate"] = Url($"/cert/{order.Id}");
            return json;
        }

        private JsonObject AuthorizationJson(OrderState order)
        {
            var challenge = new JsonObject { ["type"] = "tls-alpn-01", ["url"] = Url($"/chall/{order.Id}"), ["token"] = order.Token, ["status"] = order.Challenge };
            if (order.Error is not null) challenge["error"] = new JsonObject { ["type"] = "urn:ietf:params:acme:error:connection", ["detail"] = order.Error };
            return new JsonObject
            {
                ["status"] = order.Challenge, ["identifier"] = new JsonObject { ["type"] = order.Type, ["value"] = order.Value },
                ["challenges"] = new JsonArray(new JsonObject { ["type"] = "dns-01", ["url"] = Url($"/chall/{order.Id}/dns"), ["token"] = "dns", ["status"] = "pending" }, challenge),
            };
        }

        // Reads the answer at once: the authorization is valid or invalid when the client reads it again.
        private async Task<IResult> ChallengeAsync(OrderState order, string kid)
        {
            var expected = order.Token + "." + MobileAcme.Thumbprint(_keys[kid]);
            var valid = await Verify(order.Value, order.Token, expected);
            order.Challenge = valid ? "valid" : "invalid";
            if (!valid) order.Error = $"{order.Value}: Timeout during connect (likely firewall problem)";
            else order.Status = "ready";
            return Results.Json(new JsonObject { ["type"] = "tls-alpn-01", ["status"] = "processing", ["token"] = order.Token });
        }

        private IResult Finalize(OrderState order, JsonObject payload)
        {
            if (order.Status != "ready") return Fail("orderNotReady", "Not authorized.", 403);
            var request = CertificateRequest.LoadSigningRequest(Decode((string)payload["csr"]!), HashAlgorithmName.SHA256, CertificateRequestLoadOptions.UnsafeLoadCertificateExtensions);
            var names = request.CertificateExtensions.OfType<X509SubjectAlternativeNameExtension>().Single();
            var asked = order.Type == "ip" ? names.EnumerateIPAddresses().Select(static address => address.ToString()) : names.EnumerateDnsNames();
            if (!asked.SequenceEqual([order.Value])) return Fail("badCSR", "The request is not for the order.");
            order.Chain = _certificates.Sign(request, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow + Lifetime);
            order.Status = "valid";
            return Results.Json(OrderJson(order));
        }

        private static IResult Fail(string type, string detail, int status = 400)
            => Results.Json(new JsonObject { ["type"] = "urn:ietf:params:acme:error:" + type, ["detail"] = detail }, statusCode: status, contentType: "application/problem+json");

        private static byte[] Decode(string text)
        {
            var base64 = text.Replace('-', '+').Replace('_', '/');
            return Convert.FromBase64String(base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '='));
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        /// <summary>An order: what it is for, and how far it went.</summary>
        internal sealed class OrderState(string type, string value, string? profile, string token)
        {
            internal int Id { get; set; }

            internal string Type { get; } = type;

            internal string Value { get; } = value;

            internal string? Profile { get; } = profile;

            internal string Token { get; } = token;

            internal string Status { get; set; } = "pending";

            internal string Challenge { get; set; } = "pending";

            internal string? Error { get; set; }

            internal string? Chain { get; set; }
        }
    }
}

/// <summary>
/// Certificates of the tests, in a folder of their own: an authority, and certificates it signs as PEM files (the certificate and its
/// chain, and the key apart) and as a file of PKCS#12 without password.
/// </summary>
internal sealed class TestCertificates : IDisposable
{
    private readonly X509Certificate2 _authority;

    internal TestCertificates()
    {
        Directory = Path.Combine(Path.GetTempPath(), "codealta-mobile-certificates-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=CodeAlta Test Authority", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        _authority = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddYears(-10), DateTimeOffset.UtcNow.AddYears(10));
    }

    internal string Directory { get; }

    /// <summary>The authority, which a client of the tests trusts.</summary>
    internal X509Certificate2 Authority => _authority;

    /// <summary>A certificate the authority signs for a name, valid a year around now unless the test says otherwise.</summary>
    internal (string Certificate, string Key, string Pfx) Leaf(string host, DateTimeOffset? notBefore = null, DateTimeOffset? notAfter = null, string name = "leaf")
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = Request(host, key);
        var serial = RandomNumberGenerator.GetBytes(12);
        using var signed = request.Create(_authority, notBefore ?? DateTimeOffset.UtcNow.AddDays(-1), notAfter ?? DateTimeOffset.UtcNow.AddYears(1), serial);
        using var withKey = signed.CopyWithPrivateKey(key);
        var certificate = Path.Combine(Directory, name + ".pem");
        var keyPath = Path.Combine(Directory, name + ".key");
        var pfx = Path.Combine(Directory, name + ".pfx");
        File.WriteAllText(certificate, signed.ExportCertificatePem() + "\n" + _authority.ExportCertificatePem() + "\n");
        File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());
        var collection = new X509Certificate2Collection { withKey, X509CertificateLoader.LoadCertificate(_authority.RawData) };
        File.WriteAllBytes(pfx, collection.Export(X509ContentType.Pkcs12)!);
        return (certificate, keyPath, pfx);
    }

    /// <summary>Signs a request of a certificate as an authority does: the certificate and the authority, PEM.</summary>
    internal string Sign(CertificateRequest request, DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        using var signed = request.Create(_authority, notBefore, notAfter, RandomNumberGenerator.GetBytes(12));
        return signed.ExportCertificatePem() + "\n" + _authority.ExportCertificatePem() + "\n";
    }

    private static CertificateRequest Request(string host, ECDsa key)
    {
        var request = new CertificateRequest("CN=" + host, key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(host);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        return request;
    }

    public void Dispose()
    {
        _authority.Dispose();
        try { System.IO.Directory.Delete(Directory, recursive: true); }
        catch (IOException) { }
    }
}
