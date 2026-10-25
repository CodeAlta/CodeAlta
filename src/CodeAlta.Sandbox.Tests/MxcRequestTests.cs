using CodeAlta.Sandbox.Mxc;
using Microsoft.Mxc.Sdk.V1;

namespace CodeAlta.Sandbox.Tests;

[TestClass]
public sealed class MxcRequestTests
{
    [TestMethod]
    public void ProtectedEntries_AreReadOnlyWhenTheyExist_AndDeniedWhenTheyDoNot()
    {
        using var temp = new TempTree();
        var project = temp.Folder("project");
        var hooks = temp.Folder("project/.git/hooks");

        var request = MxcRequests.Create(Policy(temp, project, SandboxNetwork.Off), "cmd /c echo", null);

        CollectionAssert.Contains(request.Filesystem!.ReadonlyPaths, hooks);
        CollectionAssert.Contains(request.Filesystem.DeniedPaths, Path.Combine(project, ".vscode"));
        Assert.IsFalse(request.Ui!.Disable, "a disabled UI stops tools at start");
        Assert.AreEqual(ClipboardPolicy.None, request.Ui.Clipboard);
        Assert.IsNotNull(request.Environment, "the environment is the policy's, never the user's profile block");
    }

    [TestMethod]
    public void TheParentsOfAGrant_AreReadOnlyWithTheirOtherEntriesDenied_AndTheVolumeRootIsNotReadOnly()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("The chain of parent folders is for Windows.");
        using var temp = new TempTree();
        var project = temp.Folder("parent/project");
        var sibling = temp.Folder("parent/other-project");
        File.WriteAllText(Path.Combine(temp.Root, "parent", "secret.txt"), "x");
        var parent = Path.Combine(temp.Root, "parent");

        var request = MxcRequests.Create(Policy(temp, project, SandboxNetwork.Off), "cmd /c echo", null);

        CollectionAssert.Contains(request.Filesystem!.ReadonlyPaths, parent);
        CollectionAssert.Contains(request.Filesystem.DeniedPaths, sibling);
        CollectionAssert.Contains(request.Filesystem.DeniedPaths, Path.Combine(parent, "secret.txt"));
        CollectionAssert.DoesNotContain(request.Filesystem.DeniedPaths, project);
        var root = Path.GetPathRoot(project)!;
        CollectionAssert.Contains(request.Filesystem.ReadwritePaths, root, "a volume root does not cascade in the read-write list");
        CollectionAssert.DoesNotContain(request.Filesystem.ReadonlyPaths, root, "a read-only volume root opens the whole disk");
    }

    [TestMethod]
    public void TheNetwork_IsOffWithTheContainersOwnLoopback_ThroughTheProxy_OrOnWithoutTheHost()
    {
        using var temp = new TempTree();
        var project = temp.Folder("project");

        var off = MxcRequests.Create(Policy(temp, project, SandboxNetwork.Off), "cmd", null).Network!;
        var hosts = MxcRequests.Create(Policy(temp, project, SandboxNetwork.Hosts), "cmd", "http://127.0.0.1:50000").Network!;
        var on = MxcRequests.Create(Policy(temp, project, SandboxNetwork.On), "cmd", null).Network!;

        Assert.AreEqual(NetworkAction.Deny, off.Egress!.Default);
        if (OperatingSystem.IsWindows())
        {
            Assert.AreEqual("127.0.0.0/8", off.Egress.Allow!.Single().To!.Single().Cidr);
            Assert.AreEqual(NetworkAction.Deny, off.Ingress!.HostLoopback);
        }

        Assert.AreEqual(NetworkAction.Deny, hosts.Egress!.Default);
        Assert.AreEqual("http://127.0.0.1:50000", hosts.RuntimeConfig!.NetworkProxy);
        Assert.AreEqual(NetworkAction.Allow, on.Egress!.Default);
        Assert.AreEqual(NetworkAction.Deny, on.Ingress!.HostLoopback, "the internet, never the services of this computer");
    }

    private static SandboxPolicy Policy(TempTree temp, string project, SandboxNetwork network)
        => SandboxPolicyBuilder.Build(
            SandboxProfiles.Resolve("t", [new SandboxProfile { Name = "t", Network = network }]),
            new SandboxSessionFolders { WorkingDirectory = project },
            new SandboxHost { DataRoot = temp.Folder("data"), HomeDirectory = temp.Folder("home"), Environment = new Dictionary<string, string>() },
            [], "s");
}
