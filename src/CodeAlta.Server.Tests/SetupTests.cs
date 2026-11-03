using System.Net;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Server.Api;
using CodeAlta.Server.Hosting;
using CodeAlta.Server.Identity;
using CodeAlta.Server.Services;
using CodeAlta.Server.Storage;

namespace CodeAlta.Server.Tests;

[TestClass]
public sealed class SetupTests
{
    [TestMethod]
    public async Task TheOwnerSetsTheServerUpWithTheCodeAndBecomesItsFirstController()
    {
        await using var server = await ServerFixture.StartAsync();
        using var owner = new TestDevice("Desktop");
        var health = await server.SendAsync(null, HttpMethod.Get, "/api/v1/server/health");
        Assert.IsFalse(health.Read<ServerHealthDto>().SetUp);

        var setup = await server.SetUpAsync(owner);
        Assert.AreEqual(("Alex", PersonRoles.Owner), (setup.Owner.DisplayName, setup.Owner.Role));
        Assert.AreEqual((owner.DeviceId, FleetDeviceKinds.Controller, "approved", setup.Owner.Id), (setup.Device.Id, setup.Device.Kind, setup.Device.Status, setup.Device.OwnerId));
        Assert.IsTrue((await server.SendAsync(null, HttpMethod.Get, "/api/v1/server/health")).Read<ServerHealthDto>().SetUp);

        // The code is gone: it worked once.
        Assert.IsFalse(File.Exists(server.Server.Paths.SetupCodePath));
        using var other = new TestDevice("Other");
        var again = await server.SendAsync(other, HttpMethod.Post, "/api/v1/setup",
            new SetupRequest("ABCD-EFGH-JKMN-PQRS", "Mallory", other.Name, other.PublicKey, other.FirstEntry(server.Clock.Now)));
        Assert.AreEqual((HttpStatusCode.Conflict, CoordinatorErrorCodes.AlreadySetUp), (again.Status, again.Code));

        // The owner's device now calls the API; the device list starts with its own entry.
        var organization = await server.SendAsync(owner, HttpMethod.Get, "/api/v1/organization");
        Assert.AreEqual(HttpStatusCode.OK, organization.Status, organization.Body.ToString());
        Assert.AreEqual(setup.Organization.Id, organization.Read<OrganizationDto>().Id);
        var list = (await server.SendAsync(owner, HttpMethod.Get, "/api/v1/device-list")).Read<ListResponse<DeviceListEntryDto>>();
        Assert.AreEqual(1, list.Items.Count);
        var audit = (await server.SendAsync(owner, HttpMethod.Get, "/api/v1/audit")).Read<ListResponse<AuditRecordDto>>();
        Assert.AreEqual("server.setup", audit.Items.Single().Action);
    }

    [TestMethod]
    public async Task AWrongCodeIsRefusedAndFiveDropTheCode()
    {
        await using var server = await ServerFixture.StartAsync();
        using var owner = new TestDevice("Desktop");
        var code = server.SetupCode;
        for (var attempt = 1; attempt <= SetupCodeStore.MaxFailedAttempts; attempt++)
        {
            var wrong = await server.SendAsync(owner, HttpMethod.Post, "/api/v1/setup",
                new SetupRequest("WRONG-CODE-" + attempt, "Alex", owner.Name, owner.PublicKey, owner.FirstEntry(server.Clock.Now)));
            Assert.AreEqual((HttpStatusCode.Unauthorized, CoordinatorErrorCodes.SetupCodeInvalid), (wrong.Status, wrong.Code));
        }

        // The right code no longer works: altaserver setup-code makes a new one.
        var late = await server.SendAsync(owner, HttpMethod.Post, "/api/v1/setup",
            new SetupRequest(code, "Alex", owner.Name, owner.PublicKey, owner.FirstEntry(server.Clock.Now)));
        Assert.AreEqual(CoordinatorErrorCodes.SetupCodeInvalid, late.Code);

        var renewed = server.GetService<SetupCodeStore>().Create(TimeSpan.FromMinutes(30));
        var setup = await server.SendAsync(owner, HttpMethod.Post, "/api/v1/setup",
            new SetupRequest(renewed.Code.ToLowerInvariant().Replace("-", " ", StringComparison.Ordinal), "Alex", owner.Name, owner.PublicKey, owner.FirstEntry(server.Clock.Now)));
        Assert.AreEqual(HttpStatusCode.Created, setup.Status, "The code reads in any case, with spaces in place of its dashes.");
    }

    [TestMethod]
    public async Task AnExpiredCodeIsRefused()
    {
        await using var server = await ServerFixture.StartAsync();
        using var owner = new TestDevice("Desktop");
        var code = server.SetupCode;
        server.Clock.Advance(TimeSpan.FromMinutes(31));
        var expired = await server.SendAsync(owner, HttpMethod.Post, "/api/v1/setup",
            new SetupRequest(code, "Alex", owner.Name, owner.PublicKey, owner.FirstEntry(server.Clock.Now)));
        Assert.AreEqual((HttpStatusCode.Gone, CoordinatorErrorCodes.SetupCodeExpired), (expired.Status, expired.Code));
        Assert.AreEqual(410, expired.Body.GetProperty("status").GetInt32(), "The refusal is a problem details document.");
    }

    [TestMethod]
    public async Task SetupIsAcceptedOnlyFromTheLoopback()
    {
        await using var server = await ServerFixture.StartAsync();
        using var owner = new TestDevice("Desktop");
        var setup = server.GetService<SetupService>();
        var request = new SetupRequest(server.SetupCode, "Alex", owner.Name, owner.PublicKey, owner.FirstEntry(server.Clock.Now));
        var signed = Sign(owner, server.Clock, "/api/v1/setup");
        foreach (var address in new[] { "192.168.1.20", "100.64.0.7", "::ffff:10.0.0.3", "2001:db8::1" })
        {
            var refused = await Assert.ThrowsExactlyAsync<CoordinatorException>(() => setup.SetUpAsync(request, signed, ServerFixture.Origin(address), CancellationToken.None));
            Assert.AreEqual(CoordinatorErrorCodes.SetupNotLocal, refused.Code);
        }

        // The code still waits: a refusal for the address does not use it.
        var accepted = await setup.SetUpAsync(request, signed, ServerFixture.Origin("::1"), CancellationToken.None);
        Assert.AreEqual(owner.DeviceId, accepted.Device.Id);
    }

    [TestMethod]
    public async Task TheFirstEntryMustBeSignedByTheDeviceAndAddItAsAController()
    {
        await using var server = await ServerFixture.StartAsync();
        using var owner = new TestDevice("Desktop");
        using var stranger = new TestDevice("Stranger");
        var signedByStranger = stranger.SignEntry(new FleetDeviceListEntryPayload(1, null, 1, null, FleetDeviceListOperations.Add, owner.DeviceId, owner.PublicKey,
            FleetDeviceKinds.Controller, null, owner.DeviceId, server.Clock.Now));
        var badSignature = await server.SendAsync(owner, HttpMethod.Post, "/api/v1/setup",
            new SetupRequest(server.SetupCode, "Alex", owner.Name, owner.PublicKey, signedByStranger));
        Assert.AreEqual((HttpStatusCode.BadRequest, CoordinatorErrorCodes.EntrySignatureInvalid), (badSignature.Status, badSignature.Code));

        var asNode = owner.SignEntry(new FleetDeviceListEntryPayload(1, null, 1, null, FleetDeviceListOperations.Add, owner.DeviceId, owner.PublicKey,
            FleetDeviceKinds.Node, null, owner.DeviceId, server.Clock.Now));
        var wrongKind = await server.SendAsync(owner, HttpMethod.Post, "/api/v1/setup", new SetupRequest(server.SetupCode, "Alex", owner.Name, owner.PublicKey, asNode));
        Assert.AreEqual(CoordinatorErrorCodes.EntryInvalid, wrongKind.Code);

        // The request itself must be signed by the key it registers.
        var signedByOther = await server.SendAsync(stranger, HttpMethod.Post, "/api/v1/setup",
            new SetupRequest(server.SetupCode, "Alex", owner.Name, owner.PublicKey, owner.FirstEntry(server.Clock.Now)));
        Assert.AreEqual(CoordinatorErrorCodes.SignatureInvalid, signedByOther.Code);
        Assert.IsFalse((await server.SendAsync(null, HttpMethod.Get, "/api/v1/server/health")).Read<ServerHealthDto>().SetUp);
    }

    [TestMethod]
    public async Task TheServerMakesACodeAtItsFirstStartOnlyAndOneServerUsesADataRoot()
    {
        using var root = new TempDirectory();
        var clock = new TestClock(DateTimeOffset.UtcNow);
        await using (var server = await ServerFixture.StartAsync(root, clock))
        {
            Assert.IsNotNull(server.Server.SetupCode);
            Assert.AreEqual(server.SetupCode, new SetupCodeStore(server.Server.Paths.SetupCodePath, clock).GetCurrent()!.Code, "altaserver setup-code prints the same code.");
            Assert.IsTrue(CoordinatorServer.IsRunning(server.Server.Paths));
            Assert.AreEqual(Environment.ProcessId, CoordinatorServer.ReadState(server.Server.Paths)!.ProcessId);
            await Assert.ThrowsAsync<InvalidOperationException>(() => CoordinatorServer.StartAsync(new CoordinatorServerOptions
            {
                DataRoot = root.Path,
                Urls = ["http://127.0.0.1:0"],
                ConfigureLogging = static _ => { },
            }));
            using var owner = new TestDevice("Desktop");
            await server.SetUpAsync(owner);
        }

        var paths = new ServerPaths(root.Path);
        Assert.IsFalse(CoordinatorServer.IsRunning(paths));
        Assert.IsNull(CoordinatorServer.ReadState(paths));
        var restarted = await CoordinatorServer.StartAsync(new CoordinatorServerOptions { DataRoot = root.Path, Urls = ["http://127.0.0.1:0"], ConfigureLogging = static _ => { } });
        await using (restarted)
        {
            Assert.IsNull(restarted.SetupCode, "A server with an owner makes no setup code.");
        }
    }

    private static SignedRequestData Sign(TestDevice device, TestClock clock, string path)
    {
        var time = clock.Now.ToUnixTimeMilliseconds();
        var nonce = FleetSignedRequest.CreateNonce();
        var hash = FleetSignedRequest.HashBody([]);
        return new SignedRequestData("POST", path, device.DeviceId, time.ToString(System.Globalization.CultureInfo.InvariantCulture), nonce,
            device.Sign(FleetSignedRequest.GetSignedBytes("POST", path, time, nonce, hash, device.DeviceId)), hash);
    }
}
