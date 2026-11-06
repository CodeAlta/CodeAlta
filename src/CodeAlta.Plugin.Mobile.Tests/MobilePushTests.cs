using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CodeAlta.Agent;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugin.Mobile.Tests;

[TestClass]
public sealed class MobilePushTests
{
    [TestMethod]
    public void OnlyTurnEndsAndNewActionableRequestsNotify_WithoutRepeatingPolls()
    {
        var events = new MobilePushEvents();
        events.Observe(Turn(AgentActivityPhase.Started));
        var ask = new PluginAskSummary("s1", "ask", "0", "pending", false);
        events.Pending([ask], []);
        Assert.IsFalse(events.Reader.TryRead(out _), "An ordinary ask is not actionable until the turn ends.");
        events.Observe(Turn(AgentActivityPhase.Completed));
        events.Observe(Turn(AgentActivityPhase.Completed));
        Assert.AreEqual(new MobilePushNotice("s1", "completed"), Read(events));
        events.Pending([ask], []);
        Assert.AreEqual(new MobilePushNotice("s1", "answer"), Read(events));
        events.Pending([ask], []);
        events.Pending(null, null);
        Assert.IsFalse(events.Reader.TryRead(out _), "Unchanged polls and failed reads do not notify again.");
        events.Pending([ask with { Revision = "1" }], [new("s2", "permission", 1)]);
        Assert.AreEqual(new MobilePushNotice("s1", "answer"), Read(events));
        Assert.AreEqual(new MobilePushNotice("s2", "approval"), Read(events));
        events.Pending([ask with { Revision = "1" }], [new("s2", "permission", 2)]);
        Assert.IsFalse(events.Reader.TryRead(out _), "The same first permission, even with more queued, is not new.");
        events.Pending([], [new("s2", "next-permission", 1)]);
        Assert.AreEqual(new MobilePushNotice("s2", "approval"), Read(events));
        events.Pending([ask], []);
        Assert.AreEqual(new MobilePushNotice("s1", "answer"), Read(events), "A new request after none is told of.");
        events.Observe(Turn(AgentActivityPhase.Started, "turn-2"));
        events.Pending([ask with { Immediate = true }], []);
        Assert.AreEqual(new MobilePushNotice("s1", "answer"), Read(events), "An in-turn question is actionable immediately.");
        events.Observe(Turn(AgentActivityPhase.Failed, "turn-2"));
        Assert.AreEqual(new MobilePushNotice("s1", "failed"), Read(events));
        events.Observe(Turn(AgentActivityPhase.Canceled, "turn-3"));
        Assert.IsFalse(events.Reader.TryRead(out _), "Stopping a turn is not a completion alert.");
    }

    [TestMethod]
    public void SubscriptionsCannotMakeTheHostCallArbitraryOrRedirectedEndpoints()
    {
        foreach (var endpoint in new[] { "http://fcm.googleapis.com/a", "https://127.0.0.1/a", "https://localhost/a", "https://fcm.googleapis.com.evil.test/a",
            "https://user@fcm.googleapis.com/a", "https://fcm.googleapis.com:444/a", "https://fcm.googleapis.com/a#fragment", "https://evil.test/a", "not a URL" })
            Assert.IsFalse(MobilePush.ValidEndpoint(endpoint), endpoint);
        foreach (var endpoint in new[] { "https://fcm.googleapis.com/a", "https://web.push.apple.com/a", "https://updates.push.services.mozilla.com/a", "https://wns2.notify.windows.com/a" })
            Assert.IsTrue(MobilePush.ValidEndpoint(endpoint), endpoint);
        Assert.IsFalse(MobilePush.ValidKeys("bad", "bad"));
        Assert.IsFalse(MobilePush.ValidKeys(Base64Url.EncodeToString(new byte[65]), Base64Url.EncodeToString(new byte[16])));
    }

    [TestMethod]
    public async Task SubscriptionsAndIdentitySurviveRestart_EncryptedMessagesOpenTheSession_AndResetStopsDelivery()
    {
        var state = new MobileSettingsTests.MemoryState();
        var access = await MobileAccess.LoadAsync(state, default);
        var phone = await SignInAsync(access);
        using var receiver = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var subscription = Subscription(receiver);
        string publicKey;
        using (var first = await MobilePush.LoadAsync(state, access, default))
        {
            publicKey = first.PublicKey;
            Assert.IsFalse(await first.SubscribeAsync("invalid", subscription, default));
            Assert.IsTrue(await first.SubscribeAsync(phone, subscription, default));
            Assert.IsFalse(state.AllText.Contains(phone, StringComparison.Ordinal), "The phone's sign-in key is never persisted.");
        }

        var delivery = new Delivery();
        using var restarted = await MobilePush.LoadAsync(state, access, default, delivery);
        Assert.AreEqual(publicKey, restarted.PublicKey);
        var alta = new FakeAlta().Answer("session status s1", FakeAlta.Status("s1", "p1", "personal"));
        var sessions = new MobileSessions(alta, new FakeSpaces(), new MobileLive());
        await restarted.SendAsync(new("s1", "completed"), sessions, default);
        Assert.AreEqual(1, delivery.Requests.Count);
        var sent = delivery.Requests[0];
        StringAssert.StartsWith(sent.Authorization!, "vapid ");
        Assert.AreEqual("aes128gcm", sent.Encoding);
        Assert.AreEqual("600", sent.Ttl);
        var decoded = JsonNode.Parse(Decrypt(sent.Body, receiver, Base64Url.DecodeFromChars((string)subscription["keys"]!["auth"]!)))!;
        Assert.AreEqual("s1", (string?)decoded["sessionId"]);
        Assert.AreEqual("completed", (string?)decoded["kind"]);
        Assert.AreEqual(2, decoded.AsObject().Count, "No conversation, title or commands leave the computer.");

        await access.ResetAsync(default);
        await restarted.SendAsync(new("s1", "answer"), sessions, default);
        Assert.AreEqual(1, delivery.Requests.Count, "Reset immediately revokes notifications, including saved subscriptions.");
    }

    [TestMethod]
    public async Task HiddenSessionsAreNotPushed_ExpiredSubscriptionsAreRemoved_AndUnsubscribeOnlyAffectsItsPhone()
    {
        var state = new MobileSettingsTests.MemoryState();
        var access = await MobileAccess.LoadAsync(state, default);
        var phone = await SignInAsync(access);
        var second = await SignInAsync(access);
        using var receiver = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var delivery = new Delivery { Status = HttpStatusCode.Gone };
        using var push = await MobilePush.LoadAsync(state, access, default, delivery);
        Assert.IsTrue(await push.SubscribeAsync(phone, Subscription(receiver), default));
        Assert.IsTrue(await push.SubscribeAsync(second, Subscription(receiver, "second"), default));
        var alta = new FakeAlta().Answer("session status s1", FakeAlta.Status("s1", "p1", "private"));
        var spaces = new FakeSpaces("personal");
        var sessions = new MobileSessions(alta, spaces, new MobileLive());
        await push.SendAsync(new("s1", "approval"), sessions, default);
        Assert.AreEqual(0, delivery.Requests.Count, "Space restrictions apply to push as well as the page.");
        await push.UnsubscribeAsync(phone, default);
        spaces.Assigned = [];
        await push.SendAsync(new("s1", "approval"), sessions, default);
        Assert.AreEqual(1, delivery.Requests.Count);
        StringAssert.EndsWith(delivery.Requests[0].Endpoint, "/second");
        await push.SendAsync(new("s1", "approval"), sessions, default);
        Assert.AreEqual(1, delivery.Requests.Count, "A 410 removes the expired subscription.");
        var saved = await state.ReadJsonAsync<MobilePushFile>(PluginStateScope.User, MobilePush.FileName);
        Assert.AreEqual(0, saved!.Phones.Count);
    }

    private static AgentActivityEvent Turn(AgentActivityPhase phase, string id = "turn-1")
        => new(new ModelProviderId("test"), "s1", DateTimeOffset.UtcNow, null, AgentActivityKind.Turn, phase, id, null, null, null);

    private static MobilePushNotice Read(MobilePushEvents events)
    {
        Assert.IsTrue(events.Reader.TryRead(out var notice));
        return notice;
    }

    internal static async Task<string> SignInAsync(MobileAccess access)
        => (await access.SignInAsync(await access.PairAsync(default), default))!;

    internal static JsonObject Subscription(ECDiffieHellman receiver, string path = "first")
    {
        var point = receiver.ExportParameters(false).Q;
        return new JsonObject
        {
            ["endpoint"] = "https://fcm.googleapis.com/" + path,
            ["keys"] = new JsonObject { ["p256dh"] = Base64Url.EncodeToString([4, .. point.X!, .. point.Y!]), ["auth"] = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16)) },
        };
    }

    // Independent RFC 8291 receiver: verifies that the delivered payload really is decryptable by the subscribed phone.
    private static string Decrypt(byte[] record, ECDiffieHellman receiver, byte[] auth)
    {
        var serverPoint = record[21..(21 + record[20])];
        using var server = ECDiffieHellman.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = serverPoint[1..33], Y = serverPoint[33..] } });
        var point = receiver.ExportParameters(false).Q;
        var secret = receiver.DeriveRawSecretAgreement(server.PublicKey);
        byte[] info = [.. Encoding.ASCII.GetBytes("WebPush: info\0"), 4, .. point.X!, .. point.Y!, .. serverPoint];
        var material = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, auth, info);
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, material, 16, record[..16], Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0"));
        var nonce = HKDF.DeriveKey(HashAlgorithmName.SHA256, material, 12, record[..16], Encoding.ASCII.GetBytes("Content-Encoding: nonce\0"));
        var start = 21 + record[20];
        var plaintext = new byte[record.Length - start - 16];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(nonce, record.AsSpan(start, plaintext.Length), record.AsSpan(record.Length - 16), plaintext);
        var delimiter = plaintext.Length - 1;
        while (plaintext[delimiter] == 0) delimiter--;
        Assert.AreEqual(2, plaintext[delimiter]);
        return Encoding.UTF8.GetString(plaintext.AsSpan(0, delimiter));
    }

    private sealed record Sent(string Endpoint, string? Authorization, string Encoding, string Ttl, byte[] Body);

    private sealed class Delivery : HttpMessageHandler
    {
        internal List<Sent> Requests { get; } = [];
        internal HttpStatusCode Status { get; init; } = HttpStatusCode.Created;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new(request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(), request.Content!.Headers.ContentEncoding.Single(),
                request.Headers.GetValues("TTL").Single(), await request.Content.ReadAsByteArrayAsync(cancellationToken)));
            return new HttpResponseMessage(Status);
        }
    }
}
