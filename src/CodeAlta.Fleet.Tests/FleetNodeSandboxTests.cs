using CodeAlta.Fleet.NodeApi;
using CodeAlta.Sandbox;

namespace CodeAlta.Fleet.Tests;

/// <summary>The sandbox the sessions of a node run in by default: workspace, and the hosts of git hosting.</summary>
[TestClass]
public sealed class FleetNodeSandboxTests
{
    [TestMethod]
    public void TheProfileOfANode_ReachesTheRegistriesAndTheGitHosts_NeverTheInternet()
    {
        var profile = SandboxProfiles.Resolve(FleetNodeSandbox.ProfileName, []);
        var workspace = SandboxProfiles.BuiltIn.Single(static builtIn => builtIn.Name == SandboxProfiles.Workspace);

        Assert.AreEqual(SandboxNetwork.Hosts, profile.Network, "only the hosts it names, through the proxy");
        CollectionAssert.IsSubsetOf(workspace.Hosts.ToArray(), profile.Hosts.ToArray(), "what workspace reaches");
        foreach (var host in new[] { "github.com", "api.github.com", "objects.githubusercontent.com", "gitlab.com", "bitbucket.org", "dev.azure.com", "contoso.visualstudio.com" })
            Assert.IsTrue(SandboxHostPattern.Matches(host, profile.Hosts), host);
        Assert.IsFalse(SandboxHostPattern.Matches("example.com", profile.Hosts), "nothing else of the internet");
        Assert.AreEqual((workspace.Outside, workspace.Fleet), (profile.Outside, profile.Fleet), "and no more than workspace otherwise");
        Assert.AreEqual(0, profile.Write.Count + profile.Read.Count + profile.Env.Count, "no folder or variable of the machine more");
        CollectionAssert.Contains(SandboxProfiles.Names([]).ToArray(), FleetNodeSandbox.ProfileName, "every computer knows it, for a session that moves");
    }

    [TestMethod]
    public void ASandboxWithoutTheNetworkOfItsProfile_IsSaidNotToRun_WithWhatToSetUp()
    {
        // Ubuntu 24.04: bwrap starts, but AppArmor refuses unshare the namespaces of the proxy, so no network but off.
        var offOnly = new SandboxAvailability(true, "Linux Bubblewrap", "This system refuses the namespaces.", "Give `unshare` an AppArmor profile.", new HashSet<SandboxNetwork> { SandboxNetwork.Off });

        var node = FleetNodeSandbox.Describe(offOnly, SandboxProfiles.Node, []);
        Assert.IsFalse(node.Available, "its sessions in node run no command: the page must not say they run");
        Assert.AreEqual(("node", "This system refuses the namespaces.", "Give `unshare` an AppArmor profile."), (node.Profile, node.Reason, node.Setup));

        var strict = FleetNodeSandbox.Describe(offOnly, SandboxProfiles.Strict, []);
        Assert.IsTrue(strict.Available, "a profile without a network runs there");

        var unexplained = FleetNodeSandbox.Describe(offOnly with { Reason = null, Setup = null }, SandboxProfiles.Node, []);
        Assert.IsFalse(unexplained.Available);
        StringAssert.Contains(unexplained.Reason, "network 'hosts'");

        Assert.IsNull(FleetNodeSandbox.Describe(offOnly, "off", []).Profile, "no profile, nothing refused");
        Assert.IsTrue(FleetNodeSandbox.Describe(offOnly, "missing-profile", []).Available, "a profile that does not resolve is said elsewhere");
    }
}
