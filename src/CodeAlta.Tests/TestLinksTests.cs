namespace CodeAlta.Tests;

[TestClass]
public sealed class TestLinksTests
{
    [TestMethod]
    public void CanCreateSymbolicLinks_SaysWhatTheSystemDoes()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-test-links-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "target"));
        try
        {
            var link = Path.Combine(root, "link");
            if (TestLinks.CanCreateSymbolicLinks)
            {
                Directory.CreateSymbolicLink(link, Path.Combine(root, "target"));
                Assert.IsNotNull(new DirectoryInfo(link).LinkTarget);
            }
            else
            {
                // What Windows says without the privilege and without Developer Mode: the tests that need links skip instead.
                var refused = Assert.ThrowsExactly<IOException>(() => Directory.CreateSymbolicLink(link, Path.Combine(root, "target")));
                Assert.IsFalse(Directory.Exists(link), refused.Message);
                StringAssert.Contains(TestLinks.Unavailable, "Developer Mode");
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
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
