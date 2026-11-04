using System.Net;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Server.Identity;
using CodeAlta.Server.Storage;

namespace CodeAlta.Server.Tests;

/// <summary>
/// The limits of the requests of devices the server does not know yet (<c>doc/fleet/security-and-privacy.md</c>): each
/// address, and each person a request names, has so many attempts per window; past it the server answers 429 with when
/// to retry, records the first refusal of the window in the audit log, and admits again once the window ends.
/// </summary>
[TestClass]
public sealed class RateLimitTests
{
    private const string Redirect = "http://127.0.0.1:53682/callback";

    [TestMethod]
    public async Task SignInStartsPastTheLimitOfAnAddressAreRefusedWithARetryAfter_AuditedOnce_AndAdmittedAgainLater()
    {
        var limits = new AuthenticationRateLimits { SignInStart = new RateLimit(3, TimeSpan.FromMinutes(1)) };
        await using var server = await ServerFixture.StartAsync(configure: options => Configure(options, limits));
        using var owner = new TestDevice("Desktop");
        using var laptop = new TestDevice("Laptop");
        await server.SetUpAsync(owner);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var started = await server.SendAsync(laptop, HttpMethod.Post, "/api/v1/sign-in/start", new StartSignInRequest(FleetSignInMethods.Passkey, laptop.PublicKey, Redirect));
            Assert.AreEqual(HttpStatusCode.OK, started.Status, started.Body.ToString());
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var refused = await server.SendAsync(laptop, HttpMethod.Post, "/api/v1/sign-in/start", new StartSignInRequest(FleetSignInMethods.Passkey, laptop.PublicKey, Redirect));
            Assert.AreEqual((HttpStatusCode.TooManyRequests, CoordinatorErrorCodes.RateLimited), (refused.Status, refused.Code), refused.Body.ToString());
            Assert.IsTrue(refused.Body.GetProperty("retryAfter").GetInt32() is > 0 and <= 60, refused.Body.ToString());
        }

        // The header says it too, for any HTTP client.
        using (var raw = await server.Http.PostAsync("/api/v1/sign-in/start", new StringContent("{}", System.Text.Encoding.UTF8, "application/json")))
        {
            Assert.AreEqual(HttpStatusCode.TooManyRequests, raw.StatusCode);
            Assert.IsTrue(raw.Headers.RetryAfter?.Delta is { TotalSeconds: > 0 and <= 60 }, "Retry-After");
        }

        var audit = (await server.SendAsync(owner, HttpMethod.Get, "/api/v1/audit?limit=500")).Read<ListResponse<AuditRecordDto>>().Items;
        Assert.AreEqual(1, audit.Count(static record => record.Action == RateLimitedActions.SignInStart && record.Result == CoordinatorErrorCodes.RateLimited),
            "the first refusal of a window is audited, not each one of a flood");

        // The window ends by itself: nobody is refused for longer.
        server.Clock.Advance(TimeSpan.FromMinutes(1));
        var again = await server.SendAsync(laptop, HttpMethod.Post, "/api/v1/sign-in/start", new StartSignInRequest(FleetSignInMethods.Passkey, laptop.PublicKey, Redirect));
        Assert.AreEqual(HttpStatusCode.OK, again.Status, again.Body.ToString());

        // The calls of a device the server knows are not counted with them.
        Assert.AreEqual(HttpStatusCode.OK, (await server.SendAsync(owner, HttpMethod.Get, "/api/v1/devices")).Status);
    }

    [TestMethod]
    public async Task EnrollmentsPollsPasskeysRecoveriesAndTheHooksIngressAreLimitedPerAddress()
    {
        var one = new RateLimit(1, TimeSpan.FromMinutes(1));
        var limits = new AuthenticationRateLimits { Enroll = one, SignInPoll = one, Passkey = one, Recover = one, HookIngress = one, Setup = one, SignInComplete = one };
        await using var server = await ServerFixture.StartAsync(configure: options => Configure(options, limits));
        using var owner = new TestDevice("Desktop");
        using var node = new TestDevice("Node");

        // Setup: the code is also dropped after five wrong ones; the limit comes before.
        var setup = await server.SendAsync(owner, HttpMethod.Post, "/api/v1/setup", new SetupRequest("WRONG", "Alex", owner.Name, owner.PublicKey, owner.FirstEntry(server.Clock.Now)));
        Assert.AreEqual(CoordinatorErrorCodes.SetupCodeInvalid, setup.Code);
        Assert.AreEqual(CoordinatorErrorCodes.RateLimited, (await server.SendAsync(owner, HttpMethod.Post, "/api/v1/setup",
            new SetupRequest(server.SetupCode, "Alex", owner.Name, owner.PublicKey, owner.FirstEntry(server.Clock.Now)))).Code);
        server.Clock.Advance(TimeSpan.FromMinutes(1));
        await server.SetUpAsync(owner);

        foreach (var path in new[] { "/api/v1/enroll", "/api/v1/sign-in/poll", "/api/v1/sign-in/complete", "/api/v1/sign-in/passkey/options", "/api/v1/recover", "/api/v1/hooks/unknown" })
        {
            Assert.AreNotEqual(HttpStatusCode.TooManyRequests, await server.PostBytesAsync(node, path, TestText.Utf8("{}"), "application/json"), path);
            Assert.AreEqual(HttpStatusCode.TooManyRequests, await server.PostBytesAsync(node, path, TestText.Utf8("{}"), "application/json"), path);
        }
    }

    [TestMethod]
    public void APersonPastTheirLimitKeepsASlowPath_AndAnAddressWindowEnds()
    {
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var limiter = new AuthenticationRateLimiter(new AuthenticationRateLimits
        {
            RecoverPerPerson = new RateLimit(2, TimeSpan.FromMinutes(15)), PersonSlowPath = TimeSpan.FromMinutes(1), Recover = new RateLimit(2, TimeSpan.FromMinutes(1)),
        }, clock);
        Assert.IsTrue(limiter.TryAcquirePerson(RateLimitedActions.Recover, "owner").Allowed);
        Assert.IsTrue(limiter.TryAcquirePerson(RateLimitedActions.Recover, "owner").Allowed);
        var refused = limiter.TryAcquirePerson(RateLimitedActions.Recover, "owner");
        Assert.IsFalse(refused.Allowed);
        Assert.IsTrue(refused.FirstRefusal);
        Assert.IsTrue(refused.RetryAfter <= TimeSpan.FromMinutes(1), "the slow path is the next chance, not the end of the 15 minutes");
        Assert.IsFalse(limiter.TryAcquirePerson(RateLimitedActions.Recover, "owner").FirstRefusal);
        Assert.IsTrue(limiter.TryAcquirePerson(RateLimitedActions.Recover, "someone else").Allowed, "a person's flood is theirs only");

        // A flood in the owner's name slows them down to one attempt a minute; it never locks them out.
        for (var minute = 0; minute < 5; minute++)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            Assert.IsTrue(limiter.TryAcquirePerson(RateLimitedActions.Recover, "owner").Allowed, $"minute {minute}");
            Assert.IsFalse(limiter.TryAcquirePerson(RateLimitedActions.Recover, "owner").Allowed, $"minute {minute}");
        }

        var address = IPAddress.Parse("203.0.113.7");
        Assert.IsTrue(limiter.TryAcquireAddress(RateLimitedActions.Recover, address).Allowed);
        Assert.IsTrue(limiter.TryAcquireAddress(RateLimitedActions.Recover, address).Allowed);
        Assert.IsFalse(limiter.TryAcquireAddress(RateLimitedActions.Recover, address).Allowed);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.IsTrue(limiter.TryAcquireAddress(RateLimitedActions.Recover, address).Allowed);
    }

    [TestMethod]
    public void AnIPv6AddressCountsByItsSlash64()
    {
        Assert.AreEqual(AuthenticationRateLimiter.AddressKey(IPAddress.Parse("2001:db8:1:2:aaaa::1")), AuthenticationRateLimiter.AddressKey(IPAddress.Parse("2001:db8:1:2:ffff::9")));
        Assert.AreNotEqual(AuthenticationRateLimiter.AddressKey(IPAddress.Parse("2001:db8:1:2::1")), AuthenticationRateLimiter.AddressKey(IPAddress.Parse("2001:db8:1:3::1")));
        Assert.AreEqual("192.0.2.1", AuthenticationRateLimiter.AddressKey(IPAddress.Parse("::ffff:192.0.2.1")));
        Assert.AreEqual("::1", AuthenticationRateLimiter.AddressKey(IPAddress.IPv6Loopback));
    }

    private static Hosting.CoordinatorServerOptions Configure(Hosting.CoordinatorServerOptions options, AuthenticationRateLimits limits)
        => new()
        {
            DataRoot = options.DataRoot, Urls = options.Urls, TimeProvider = options.TimeProvider, Relay = options.Relay, ConfigureLogging = options.ConfigureLogging,
            UpdateStatus = options.UpdateStatus, EnvironmentVariable = options.EnvironmentVariable, TelemetryExportInterval = options.TelemetryExportInterval,
            SignIn = options.SignIn, RateLimits = limits,
        };
}
