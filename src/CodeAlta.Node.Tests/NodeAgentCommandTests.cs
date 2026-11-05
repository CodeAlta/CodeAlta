using CodeAlta.Fleet.NodeApi;

namespace CodeAlta.Node.Tests;

/// <summary>
/// <c>altanode agents</c>: the owner of a node allows agents of other machines to start sessions there, space by space,
/// with a sandbox and without bypassing permissions unless told; off until then.
/// </summary>
[TestClass]
public sealed class NodeAgentCommandTests
{
    [TestMethod]
    public async Task Agents_AreOffByDefault_AllowedPerSpace_AndDeniedAgain()
    {
        using var root = new NodeTestRoot();
        var profile = root.Profile();
        var fleetRoot = new CodeAlta.Catalog.CatalogOptions { GlobalRoot = profile.GlobalRoot, StateRoot = profile.StateRoot }.FleetRoot;

        var (code, output, _) = await RunAsync(root, "agents");
        Assert.AreEqual(NodeExitCodes.Success, code);
        StringAssert.Contains(output, "may start no session here");

        (code, output, _) = await RunAsync(root, "agents", "allow", "--space", "work", "--space", "default", "--sandbox", "strict");
        Assert.AreEqual(NodeExitCodes.Success, code);
        StringAssert.Contains(output, "work, default");
        StringAssert.Contains(output, "never bypass permissions");
        var policy = new FleetAgentSessions(fleetRoot).ReadPolicy();
        CollectionAssert.AreEqual(new[] { "work", "default" }, policy.Spaces.ToArray(), "The node reads the file at each request.");
        Assert.AreEqual(("strict", false), (policy.Sandbox, policy.AllowBypass));

        Assert.AreEqual(NodeExitCodes.Success, (await RunAsync(root, "agents", "allow", "--bypass", "--sandbox", "none")).Code);
        Assert.AreEqual((null, true), (new FleetAgentSessions(fleetRoot).ReadPolicy().Sandbox, new FleetAgentSessions(fleetRoot).ReadPolicy().AllowBypass));
        Assert.AreEqual(NodeExitCodes.Success, (await RunAsync(root, "agents", "deny", "--space", "WORK")).Code);
        CollectionAssert.AreEqual(new[] { "default" }, new FleetAgentSessions(fleetRoot).ReadPolicy().Spaces.ToArray());
        Assert.AreEqual(NodeExitCodes.Success, (await RunAsync(root, "agents", "deny", "--all")).Code);
        Assert.AreEqual(0, new FleetAgentSessions(fleetRoot).ReadPolicy().Spaces.Count);

        Assert.AreEqual(NodeExitCodes.InvalidUsage, (await RunAsync(root, "agents", "allow")).Code, "allow names what it allows.");
        Assert.AreEqual(NodeExitCodes.InvalidUsage, (await RunAsync(root, "agents", "deny")).Code, "deny names the spaces, or --all.");
        Assert.AreEqual(NodeExitCodes.InvalidUsage, (await RunAsync(root, "agents", "forget")).Code);
    }

    private static async Task<(int Code, string Output, string Error)> RunAsync(NodeTestRoot root, params string[] words)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await new NodeApplication(output, error) { InitializeLogging = false, ServiceEnvironment = static () => null }
            .RunAsync([.. words, "--data-root", root.DataRoot]);
        return (code, output.ToString(), error.ToString());
    }
}
