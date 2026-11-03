using System.Net;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Server.Api;
using CodeAlta.Server.Identity;

namespace CodeAlta.Server.Tests;

[TestClass]
public sealed class SignedRequestTests
{
    [TestMethod]
    public async Task ASignedRequestOfAnApprovedDevicePasses()
    {
        await using var server = await ServerFixture.StartAsync();
        using var owner = new TestDevice("Desktop");
        await server.SetUpAsync(owner);
        var devices = await server.SendAsync(owner, HttpMethod.Get, "/api/v1/devices?limit=10");
        Assert.AreEqual(HttpStatusCode.OK, devices.Status, devices.Body.ToString());
        Assert.AreEqual(owner.DeviceId, devices.Read<ListResponse<DeviceDto>>().Items.Single().Id);
    }

    [TestMethod]
    public async Task ARequestWithoutSignatureIsRefusedAndSaysHowToSign()
    {
        await using var server = await ServerFixture.StartAsync();
        using var owner = new TestDevice("Desktop");
        await server.SetUpAsync(owner);
        var unsigned = await server.SendAsync(null, HttpMethod.Get, "/api/v1/devices");
        Assert.AreEqual((HttpStatusCode.Unauthorized, CoordinatorErrorCodes.SignatureMissing), (unsigned.Status, unsigned.Code));
        Assert.AreEqual(FleetErrorHints.Reauthenticate, unsigned.Body.GetProperty("hint").GetString());
    }

    [TestMethod]
    public async Task ABadSignatureIsRefused()
    {
        await using var server = await ServerFixture.StartAsync();
        using var owner = new TestDevice("Desktop");
        using var thief = new TestDevice("Thief");
        await server.SetUpAsync(owner);

        // Another key that claims the owner's device id.
        var forged = await server.SendAsync(thief, HttpMethod.Get, "/api/v1/devices", tamper: request =>
        {
            request.Headers.Remove(FleetSignedRequest.DeviceHeader);
            request.Headers.Add(FleetSignedRequest.DeviceHeader, owner.DeviceId);
        });
        Assert.AreEqual((HttpStatusCode.Unauthorized, CoordinatorErrorCodes.SignatureInvalid), (forged.Status, forged.Code));

        // A signed request whose path, method or body changed on the way.
        var otherPath = await server.SendAsync(owner, HttpMethod.Get, "/api/v1/devices", tamper: request => request.RequestUri = new Uri("/api/v1/sessions", UriKind.Relative));
        Assert.AreEqual(CoordinatorErrorCodes.SignatureInvalid, otherPath.Code);
        var otherBody = await server.SendAsync(owner, HttpMethod.Post, "/api/v1/enrollment-tokens", new EnrollmentTokenRequest(600, "a"),
            tamper: request => request.Content = new StringContent("{\"ttlSeconds\":86400,\"label\":\"a\"}", System.Text.Encoding.UTF8, "application/json"));
        Assert.AreEqual(CoordinatorErrorCodes.SignatureInvalid, otherBody.Code);

        // An unknown device answers as a bad signature: the answer does not tell which ids exist.
        var unknown = await server.SendAsync(thief, HttpMethod.Get, "/api/v1/devices");
        Assert.AreEqual(CoordinatorErrorCodes.SignatureInvalid, unknown.Code);
    }

    [TestMethod]
    public async Task AReplayedNonceIsRefused()
    {
        await using var server = await ServerFixture.StartAsync();
        using var owner = new TestDevice("Desktop");
        await server.SetUpAsync(owner);
        var nonce = FleetSignedRequest.CreateNonce();
        var time = server.Clock.Now.ToUnixTimeMilliseconds();
        Assert.AreEqual(HttpStatusCode.OK, (await server.SendAsync(owner, HttpMethod.Get, "/api/v1/machines", timestamp: time, nonce: nonce)).Status);
        var replayed = await server.SendAsync(owner, HttpMethod.Get, "/api/v1/machines", timestamp: time, nonce: nonce);
        Assert.AreEqual((HttpStatusCode.Unauthorized, CoordinatorErrorCodes.Replayed), (replayed.Status, replayed.Code));

        // The same nonce with another signature (another request) is a replay too.
        var other = await server.SendAsync(owner, HttpMethod.Get, "/api/v1/sessions", nonce: nonce);
        Assert.AreEqual(CoordinatorErrorCodes.Replayed, other.Code);
    }

    [TestMethod]
    public async Task ATimeFarFromTheServersIsRefused()
    {
        await using var server = await ServerFixture.StartAsync();
        using var owner = new TestDevice("Desktop");
        await server.SetUpAsync(owner);
        var now = server.Clock.Now;
        foreach (var skew in new[] { TimeSpan.FromMinutes(-6), TimeSpan.FromMinutes(6) })
        {
            var skewed = await server.SendAsync(owner, HttpMethod.Get, "/api/v1/devices", timestamp: (now + skew).ToUnixTimeMilliseconds());
            Assert.AreEqual((HttpStatusCode.Unauthorized, CoordinatorErrorCodes.ClockSkew), (skewed.Status, skewed.Code));
        }

        var close = await server.SendAsync(owner, HttpMethod.Get, "/api/v1/devices", timestamp: (now - TimeSpan.FromMinutes(4)).ToUnixTimeMilliseconds());
        Assert.AreEqual(HttpStatusCode.OK, close.Status);
    }

    [TestMethod]
    public void TheServerDerivesTheIdsAndChecksTheSignaturesOfCodeAltaDevices()
    {
        using var root = new TempDirectory();
        using var identity = DeviceIdentity.LoadOrCreate(root.Path);
        Assert.IsTrue(FleetDevicePublicKey.TryParse(Convert.ToBase64String(identity.ExportSubjectPublicKeyInfo()), out var key));
        Assert.AreEqual(identity.DeviceId, key.DeviceId);
        Assert.AreEqual(identity.Fingerprint.ToString(), key.Fingerprint);
        var signed = FleetSignedRequest.GetSignedBytes("GET", "/api/v1/devices", 1, FleetSignedRequest.CreateNonce(), FleetSignedRequest.HashBody([]), identity.DeviceId);
        Assert.IsTrue(key.Verify(signed, System.Buffers.Text.Base64Url.EncodeToString(identity.SignData(signed))));
        Assert.IsFalse(key.Verify(TestText.Utf8("something else"), System.Buffers.Text.Base64Url.EncodeToString(identity.SignData(signed))));
    }

    [TestMethod]
    public void OnlyP256KeysAreDeviceKeys()
    {
        using var p384 = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP384);
        Assert.IsFalse(FleetDevicePublicKey.TryParse(Convert.ToBase64String(p384.ExportSubjectPublicKeyInfo()), out _));
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        Assert.IsFalse(FleetDevicePublicKey.TryParse(Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo()), out _));
        Assert.IsFalse(FleetDevicePublicKey.TryParse("not base64", out _));
    }

    [TestMethod]
    public async Task AnHttp2BodyWithoutLengthIsHashedAndASubstitutedOneIsRefused()
    {
        await using var server = await ServerFixture.StartAsync();
        using var owner = new TestDevice("Desktop");
        await server.SetUpAsync(owner);
        var authenticator = server.GetService<RequestAuthenticator>();
        var body = TestText.Utf8("{\"label\":\"signed\"}");

        // HTTP/2 sends a body in DATA frames, with neither Content-Length nor Transfer-Encoding: the body is still signed.
        var legit = Http2Request(server, owner, body, FleetSignedRequest.HashBody(body));
        byte[]? seen = null;
        await new DeviceAuthenticationMiddleware(async context =>
        {
            using var read = new MemoryStream();
            await context.Request.Body.CopyToAsync(read);
            seen = read.ToArray();
        }, authenticator).InvokeAsync(legit);
        Assert.AreEqual(FleetSignedRequest.HashBody(body), DeviceAuthenticationMiddleware.GetSigned(legit).BodyHash);
        Assert.AreEqual(owner.DeviceId, DeviceAuthenticationMiddleware.GetDevice(legit).Id);
        CollectionAssert.AreEqual(body, seen, "The endpoint reads the body that was hashed.");

        // A body put under a signature of an empty one is refused, not read as if it were signed.
        var substituted = Http2Request(server, owner, TestText.Utf8("{\"label\":\"injected\"}"), FleetSignedRequest.HashBody([]));
        var reached = false;
        var refused = await Assert.ThrowsExactlyAsync<CoordinatorException>(() => new DeviceAuthenticationMiddleware(_ =>
        {
            reached = true;
            return Task.CompletedTask;
        }, authenticator).InvokeAsync(substituted));
        Assert.AreEqual(CoordinatorErrorCodes.SignatureInvalid, refused.Code);
        Assert.IsFalse(reached);
    }

    // A POST as Kestrel gives an HTTP/2 request: a body that can be read, without any header of its length.
    private static Microsoft.AspNetCore.Http.DefaultHttpContext Http2Request(ServerFixture server, TestDevice device, byte[] body, string signedHash)
    {
        const string path = "/api/v1/enrollment-tokens";
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>(new BodyDetection());
        var request = context.Request;
        request.Protocol = "HTTP/2";
        request.Method = "POST";
        request.Path = path;
        request.Body = new MemoryStream(body);
        var time = server.Clock.Now.ToUnixTimeMilliseconds();
        var nonce = FleetSignedRequest.CreateNonce();
        request.Headers[FleetSignedRequest.DeviceHeader] = device.DeviceId;
        request.Headers[FleetSignedRequest.TimestampHeader] = time.ToString(System.Globalization.CultureInfo.InvariantCulture);
        request.Headers[FleetSignedRequest.NonceHeader] = nonce;
        request.Headers[FleetSignedRequest.SignatureHeader] = device.Sign(FleetSignedRequest.GetSignedBytes("POST", path, time, nonce, signedHash, device.DeviceId));
        Assert.IsNull(request.ContentLength);
        return context;
    }

    private sealed class BodyDetection : Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }

    [TestMethod]
    public void NoncesAreForgottenOnlyAfterTheirWindow()
    {
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var cache = new NonceCache(clock, TimeSpan.FromMinutes(11), capacity: 2);
        Assert.AreEqual(NonceResult.Fresh, cache.TryRecord("d", "n1"));
        Assert.AreEqual(NonceResult.Replayed, cache.TryRecord("d", "n1"));
        Assert.AreEqual(NonceResult.Fresh, cache.TryRecord("other", "n1"), "Nonces are per device.");
        Assert.AreEqual(NonceResult.Full, cache.TryRecord("d", "n2"), "A full cache refuses rather than let a request in unchecked.");
        clock.Advance(TimeSpan.FromMinutes(12));
        Assert.AreEqual(NonceResult.Fresh, cache.TryRecord("d", "n2"));
        Assert.AreEqual(1, cache.Count);
    }
}
