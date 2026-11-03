using System.Net;
using CodeAlta.Server.Hosting;
using CodeAlta.Server.Storage.Sqlite;

namespace CodeAlta.Server.Tests;

[TestClass]
public sealed class HealthProbeTests
{
    [TestMethod]
    public async Task TheProbesAnswerWithoutADeviceBeforeAndAfterTheSetup()
    {
        await using var server = await ServerFixture.StartAsync();
        using var owner = new TestDevice("Desktop");

        await AssertProbesAsync(server, HttpStatusCode.OK, "live", "ready", "ready");

        await server.SetUpAsync(owner);
        await AssertProbesAsync(server, HttpStatusCode.OK, "live", "ready", "ready");
    }

    [TestMethod]
    public async Task TheProbesLeakNothingOfTheServer()
    {
        await using var server = await ServerFixture.StartAsync();
        using var owner = new TestDevice("Desktop");
        var setup = await server.SetUpAsync(owner);

        foreach (var path in new[] { "/health", "/health/live", "/health/ready" })
        {
            var body = await server.Http.GetStringAsync(path);
            Assert.IsFalse(body.Contains(setup.Organization.Id, StringComparison.Ordinal), path);
            Assert.IsFalse(body.Contains(setup.Organization.Name, StringComparison.Ordinal), path);
            Assert.IsFalse(body.Contains(ServerVersion.Current, StringComparison.Ordinal), path);
            Assert.IsTrue(body.Length < 64, $"{path} answers a short status: {body}");
        }
    }

    [TestMethod]
    public async Task TheReadyProbeFailsWhenTheStoreIsGoneWhileTheLiveProbeStaysUp()
    {
        await using var server = await ServerFixture.StartAsync();
        await ((SqliteCoordinatorStore)server.Server.Store).DisposeAsync();

        await AssertProbesAsync(server, HttpStatusCode.ServiceUnavailable, "live", "unavailable", "unavailable", liveStatus: HttpStatusCode.OK);
    }

    [TestMethod]
    public async Task TheDeviceFreeHealthOfTheApiStaysWhereItWas()
    {
        await using var server = await ServerFixture.StartAsync();
        var health = await server.SendAsync(null, HttpMethod.Get, "/api/v1/server/health");
        Assert.AreEqual(HttpStatusCode.OK, health.Status);
    }

    private static async Task AssertProbesAsync(ServerFixture server, HttpStatusCode readyStatus, string live, string ready, string health, HttpStatusCode? liveStatus = null)
    {
        await AssertProbeAsync(server, "/health/live", liveStatus ?? HttpStatusCode.OK, live);
        await AssertProbeAsync(server, "/health/ready", readyStatus, ready);
        await AssertProbeAsync(server, "/health", readyStatus, health);
    }

    private static async Task AssertProbeAsync(ServerFixture server, string path, HttpStatusCode status, string expected)
    {
        using var response = await server.Http.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(status, response.StatusCode, path + ": " + body);
        Assert.AreEqual("application/json", response.Content.Headers.ContentType?.MediaType, path);
        Assert.AreEqual($$"""{"status":"{{expected}}"}""", body, path);
        Assert.IsTrue(response.Headers.CacheControl?.NoStore ?? false, path + " is never cached");
    }
}
