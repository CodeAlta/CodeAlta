using CodeAlta.Sandbox.Mxc;

namespace CodeAlta.Sandbox.Tests;

/// <summary>The check of the namespaces a sandbox with a network needs on Linux, and what it tells to set up.</summary>
[TestClass]
public sealed class LinuxNetworkNamespacesTests
{
    [TestMethod]
    public void ARefusedUnshare_GetsTheAppArmorProfileThatAllowsIt_ForItsRealPath()
    {
        var (reason, setup) = LinuxNetworkNamespaces.Refused("/usr/bin/unshare", "unshare: write failed /proc/self/uid_map: Operation not permitted\n");

        StringAssert.Contains(reason, "uid_map: Operation not permitted", "what unshare said, on one line");
        StringAssert.Contains(reason, "a sandbox with a network cannot start");
        StringAssert.Contains(setup, "profile unshare /usr/bin/unshare flags=(unconfined)");
        StringAssert.Contains(setup, "userns,");
        StringAssert.Contains(setup, "sudo tee /etc/apparmor.d/unshare");
        StringAssert.Contains(setup, "sudo apparmor_parser -r /etc/apparmor.d/unshare");
        Assert.DoesNotContain("\n", setup, "one line, to copy");
    }

    [TestMethod]
    public void TheProbe_IsTheOneOfMxc_AndAMissingUnshareSaysWhatToInstall()
    {
        CollectionAssert.AreEqual(new[] { "--user", "--map-current-user", "--keep-caps", "--net", "sh", "-c", ":" }, LinuxNetworkNamespaces.ProbeArguments);

        var missing = LinuxNetworkNamespaces.Check(static _ => null);
        Assert.IsNotNull(missing);
        StringAssert.Contains(missing.Value.Setup, "util-linux");
    }
}
