using CodeAlta.Catalog;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Hosting;

namespace CodeAlta.Node.Tests;

/// <summary>
/// A provider sign-in that is about to expire is a problem of the node (<c>doc/fleet/observability.md</c>, health checks): when
/// the provider tells when its sign-in expires and that it does not renew itself, the node reports it in its heartbeat a few days
/// ahead; one that renews (a refresh token, the GitHub credential of Copilot) is no problem, nor one far away.
/// </summary>
[TestClass]
public sealed class NodeSignInHealthTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task ASignInThatExpiresSoon_AndDoesNotRenew_IsReportedAFewDaysAhead()
    {
        var token = TestContext.CancellationToken;
        using var root = new NodeTestRoot();
        await using var node = await NodeHost.StartAsync(root.Options(), token);
        var now = DateTimeOffset.UtcNow;
        var statuses = new Dictionary<string, ProviderLoginStatus>(StringComparer.Ordinal)
        {
            ["grok"] = new(true, null, null, now.AddDays(2)),
            ["grok-renews"] = new(true, null, null, now.AddHours(1)) { Renews = true },
            ["grok-later"] = new(true, null, null, now.AddDays(20)),
            ["grok-out"] = new(false, null, null, null),
        };
        var health = new NodeHealth(node)
        {
            MinimumFreeBytes = 0,
            SignInDefinitions = () => [.. statuses.Keys.Select(static key => new CodeAltaProviderDocument { ProviderKey = key, ProviderType = "xai" })],
            SignInStatus = (definition, _) => Task.FromResult(statuses[definition.ProviderKey]),
        };

        // The stored states are read off the heartbeat, which reports what was read last.
        await health.RefreshSignInsAsync(token);
        var problem = health.Read().Single();
        Assert.AreEqual((FleetHealthKinds.SignIn, "grok", now.AddDays(2).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)),
            (problem.Kind, problem.Subject, problem.Detail));

        // Signed in again for longer: no problem any more.
        statuses["grok"] = new(true, null, null, now.AddDays(30));
        await health.RefreshSignInsAsync(token);
        Assert.AreEqual(0, health.Read().Count);
    }
}
