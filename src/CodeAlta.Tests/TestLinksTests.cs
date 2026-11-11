namespace CodeAlta.Tests;

[TestClass]
public sealed class TestLinksTests
{
    [TestMethod]
    public void CanCreateSymbolicLinks_SaysWhatTheSystemDoes()
    {
        using var temp = TestTempDirectory.Create();
        var target = Directory.CreateDirectory(Path.Combine(temp.Path, "target")).FullName;
        var link = Path.Combine(temp.Path, "link");
        if (TestLinks.CanCreateSymbolicLinks)
        {
            Directory.CreateSymbolicLink(link, target);
            Assert.IsNotNull(new DirectoryInfo(link).LinkTarget);
        }
        else
        {
            // What Windows says without the privilege and without Developer Mode: the tests that need links skip instead.
            var refused = Assert.ThrowsExactly<IOException>(() => Directory.CreateSymbolicLink(link, target));
            Assert.IsFalse(Directory.Exists(link), refused.Message);
        }
    }

    [TestMethod]
    public void HoldsPrivilege_ReadsThePrivilegesOfTheToken()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Privileges are a Windows notion.");
            return;
        }

        // Every Windows token holds the privilege of traversing folders; no token holds a privilege Windows does not know.
        Assert.IsTrue(TestLinks.HoldsPrivilege("SeChangeNotifyPrivilege"));
        Assert.IsFalse(TestLinks.HoldsPrivilege("SeNoSuchPrivilege"));
    }
}
