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
    public void AFileAnotherProgramHoldsDeleteOnClose_IsLeftOutOfTheChain()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("The chain of parent folders is for Windows.");
        using var temp = new TempTree();
        var project = temp.Folder("parent/project");
        var parent = Path.Combine(temp.Root, "parent");
        var plain = Path.Combine(parent, "plain.txt");
        File.WriteAllText(plain, "x");
        // A temporary file of another program, as %TEMP% holds many: the container fails to start (0x80070020) with
        // it denied, because it opens a denied path without sharing its deletion.
        var held = Path.Combine(parent, "held.tmp");
        using var holder = new FileStream(held, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.DeleteOnClose);

        var request = MxcRequests.Create(Policy(temp, project, SandboxNetwork.Off), "cmd /c echo", null);

        CollectionAssert.Contains(request.Filesystem!.DeniedPaths, plain);
        CollectionAssert.DoesNotContain(request.Filesystem.DeniedPaths, held);
    }

    [TestMethod]
    public void TheNetwork_IsOffWithTheContainersOwnLoopback_ThroughTheProxy_OrOnWithoutTheHost()
    {
        using var temp = new TempTree();
        var project = temp.Folder("project");

        var off = MxcRequests.Create(Policy(temp, project, SandboxNetwork.Off), "cmd", null).Network!;
        var hosts = MxcRequests.Create(Policy(temp, project, SandboxNetwork.Hosts), "cmd", "http://127.0.0.1:50000").Network!;
        var onPolicy = Policy(temp, project, SandboxNetwork.On);
        var on = MxcRequests.Create(onPolicy, "cmd", onPolicy.ThroughProxy ? "http://127.0.0.1:50001" : null).Network!;

        Assert.AreEqual(NetworkAction.Deny, off.Egress!.Default);
        if (OperatingSystem.IsWindows())
        {
            Assert.AreEqual("127.0.0.0/8", off.Egress.Allow!.Single().To!.Single().Cidr);
            Assert.AreEqual(NetworkAction.Deny, off.Ingress!.HostLoopback);
        }

        Assert.AreEqual(NetworkAction.Deny, hosts.Egress!.Default);
        Assert.AreEqual("http://127.0.0.1:50000", hosts.RuntimeConfig!.NetworkProxy);
        Assert.AreEqual(NetworkAction.Deny, on.Ingress!.HostLoopback, "the internet, never the services of this computer");
        if (OperatingSystem.IsWindows())
        {
            // The container itself keeps the networks of this computer out.
            Assert.IsFalse(onPolicy.ThroughProxy);
            Assert.AreEqual(NetworkAction.Allow, on.Egress!.Default);
            CollectionAssert.IsSubsetOf(new[] { "10.0.0.0/8", "169.254.0.0/16", "192.168.0.0/16", "fe80::/10" }, on.Egress.Deny!.Single().To!.Select(static peer => peer.Cidr).ToArray());
        }
        else
        {
            // Seatbelt and Bubblewrap do not: every host of the internet goes through the proxy, which refuses them.
            Assert.IsTrue(onPolicy.ThroughProxy);
            Assert.IsNull(onPolicy.ProxyHosts);
            Assert.AreEqual(NetworkAction.Deny, on.Egress!.Default);
            Assert.AreEqual("http://127.0.0.1:50001", on.RuntimeConfig!.NetworkProxy);
        }
    }

    [TestMethod]
    public void WithoutGrantingTheParents_TheirOtherEntriesAreDenied_ButNotWhatLeadsToAGrantNorWhatIsOutsideTheHome()
    {
        using var temp = new TempTree();
        var home = temp.Folder("home");
        var project = temp.Folder("home/code/project");
        var sibling = temp.Folder("home/code/other-project");
        var editorConfig = Path.Combine(home, "code", ".editorconfig");
        File.WriteAllText(editorConfig, "root = true");
        var dataTemp = temp.Folder("home/Library/Caches/sandbox/sessions/s/temp");
        var tools = temp.Folder("home/.dotnet/tools");
        var dotnetSecrets = temp.Folder("home/.dotnet/secrets");
        var outside = temp.Folder("outside-the-home");
        var filesystem = new FilesystemPolicy();
        foreach (var path in new[] { project, dataTemp }) filesystem.ReadwritePaths.Add(path);
        filesystem.ReadonlyPaths.Add(tools);

        MxcRequests.AddParentChain(filesystem, grantParents: false, unchained: Path.Combine(home, "Library", "Caches", "sandbox"), within: home);

        CollectionAssert.Contains(filesystem.DeniedPaths, editorConfig, "a file a build looks for above the project is not there, rather than unreadable");
        CollectionAssert.Contains(filesystem.DeniedPaths, sibling);
        CollectionAssert.Contains(filesystem.DeniedPaths, dotnetSecrets, "beside a tool folder");
        foreach (var kept in new[] { project, Path.Combine(home, "code"), Path.Combine(home, "Library"), Path.Combine(home, ".dotnet"), tools })
            CollectionAssert.DoesNotContain(filesystem.DeniedPaths, kept, "it leads to a grant");
        CollectionAssert.DoesNotContain(filesystem.DeniedPaths, outside, "the entries of folders outside the home are the system's");
        Assert.IsFalse(filesystem.DeniedPaths.Any(path => SandboxPolicyBuilder.IsInside(Path.Combine(home, "Library"), path)), "the parents of the data folder are not chained");
        Assert.AreEqual(2, filesystem.ReadwritePaths.Count, "no volume root is granted");
        Assert.AreEqual(1, filesystem.ReadonlyPaths.Count, "nor a parent folder, which would open what it holds");
    }

    [TestMethod]
    public void OnMacOS_TheSandboxReachesTheServicesThatCheckCertificates_NotTheKeychains()
    {
        if (!OperatingSystem.IsMacOS()) Assert.Inconclusive("Seatbelt is the container of macOS.");
        using var temp = new TempTree();
        var project = temp.Folder("project");

        var seatbelt = (Containment.Seatbelt)MxcRequests.Create(Policy(temp, project, SandboxNetwork.Off), "sh", null).Containment!;

        CollectionAssert.IsSubsetOf(new[] { "com.apple.SecurityServer", "com.apple.trustd.agent", "com.apple.system.opendirectoryd.libinfo" }, seatbelt.ExtraMachLookups,
            "TLS of .NET and the names of users and groups");
        Assert.IsFalse(seatbelt.KeychainAccess, "the keychains stay out of reach");
    }

    private static SandboxPolicy Policy(TempTree temp, string project, SandboxNetwork network)
        => SandboxPolicyBuilder.Build(
            SandboxProfiles.Resolve("t", [new SandboxProfile { Name = "t", Network = network }]),
            new SandboxSessionFolders { WorkingDirectory = project },
            new SandboxHost { DataRoot = temp.Folder("data"), HomeDirectory = temp.Folder("home"), Environment = new Dictionary<string, string>() },
            [], "s");
}
