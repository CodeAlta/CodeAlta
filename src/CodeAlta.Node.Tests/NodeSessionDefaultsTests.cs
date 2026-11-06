using CodeAlta.Fleet.Client;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Node.Tests;

/// <summary>
/// The permission mode the sessions of a machine run in when nothing else chooses it (<c>doc/fleet/node.md</c>,
/// "Safety"): bypass permissions by default, set by its owner with <c>altanode permission-mode</c> or by a device that
/// manages it, and read by the host for each session.
/// </summary>
[TestClass]
public sealed class NodeSessionDefaultsTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task AManager_MakesTheSessionsAskFirst_OthersAreForbidden_AndTheHostFollows()
    {
        var token = TestContext.CancellationToken;
        await using var fixture = await ManagedNodeFixture.StartAsync(token);
        var managerMachine = await fixture.PairAsync(fixture.Owner, "laptop", manages: true, token);
        var otherMachine = await fixture.PairAsync(fixture.Other, "phone", manages: false, token);
        await using var manager = await FleetNodeClient.ConnectAsync(managerMachine, fixture.Owner, token);
        await using var other = await FleetNodeClient.ConnectAsync(otherMachine, fixture.Other, token);
        var runtime = fixture.Node.Host.RuntimeService;

        Assert.AreEqual(SessionPermissionModes.Bypass, (await manager.GetSessionDefaultsAsync(token)).PermissionMode, "a machine runs agents without asking until told");
        Assert.AreEqual(SessionPermissionModes.Bypass, runtime.GetDefaultPermissionMode(null));
        Assert.AreEqual(SessionPermissionPolicy.Approve, runtime.GetPermissionPolicy("a-session"));

        Assert.AreEqual(SessionPermissionModes.Ask, (await manager.SetSessionDefaultsAsync(new FleetSessionDefaultsInfo("default"), token)).PermissionMode);
        Assert.AreEqual(SessionPermissionModes.Ask, FleetSessionDefaults.Read(fixture.Node.Host.CatalogOptions.FleetRoot).PermissionMode, "kept in the profile of the machine");
        Assert.AreEqual(SessionPermissionModes.Ask, runtime.GetDefaultPermissionMode(null), "the host reads it again");
        Assert.AreEqual(SessionPermissionPolicy.Review, runtime.GetPermissionPolicy("a-session"));

        Assert.AreEqual(FleetErrorCodes.InvalidParams, (await Assert.ThrowsExactlyAsync<FleetException>(
            () => manager.SetSessionDefaultsAsync(new FleetSessionDefaultsInfo("acceptEdits"), token))).Error.Code);
        Assert.AreEqual(FleetErrorCodes.Forbidden, (await Assert.ThrowsExactlyAsync<FleetException>(
            () => other.SetSessionDefaultsAsync(new FleetSessionDefaultsInfo("bypassPermissions"), token))).Error.Code);
        Assert.AreEqual(FleetErrorCodes.Forbidden, (await Assert.ThrowsExactlyAsync<FleetException>(() => other.GetSessionDefaultsAsync(token))).Error.Code);
        Assert.AreEqual(SessionPermissionModes.Ask, (await manager.GetSessionDefaultsAsync(token)).PermissionMode, "a refused change writes nothing");
    }

    [TestMethod]
    public async Task TheOwner_ShowsAndSetsTheMode_WithAltanode()
    {
        using var root = new NodeTestRoot();
        async Task<(int Code, string Output, string Error)> RunAsync(params string[] words)
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            var code = await new NodeApplication(output, error) { InitializeLogging = false, ServiceEnvironment = static () => null }
                .RunAsync(["permission-mode", .. words, "--data-root", root.DataRoot]);
            return (code, output.ToString().Trim(), error.ToString());
        }

        var shown = await RunAsync();
        Assert.AreEqual(NodeExitCodes.Success, shown.Code, shown.Error);
        StringAssert.Contains(shown.Output, "bypass permissions");
        StringAssert.Contains((await RunAsync("ask")).Output, "ask first");
        StringAssert.Contains((await RunAsync()).Output, "ask first");
        StringAssert.Contains((await RunAsync("bypass")).Output, "bypass permissions");
        var refused = await RunAsync("sometimes");
        Assert.AreEqual(NodeExitCodes.InvalidUsage, refused.Code);
        StringAssert.Contains(refused.Error, "bypass or ask");
        Assert.AreEqual(NodeExitCodes.InvalidUsage, (await RunAsync("ask", "bypass")).Code);
    }
}
