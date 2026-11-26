namespace CodeAlta.Hosting.Tests;

[TestClass]
public sealed class IncompleteInstanceLockTests
{
    [TestMethod]
    public void MissingOrUnreadableLock_IsNotAbandoned()
    {
        var root = Directory.CreateTempSubdirectory("CodeAlta-incomplete-lock-");
        try
        {
            var path = Path.Combine(root.FullName, "alta.lock");
            Assert.IsFalse(CodeAltaSingleInstanceGuard.IsAbandoned(null, path), "A missing lock has no age to reclaim.");
            File.WriteAllText(path, string.Empty);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-2)); // not a limit
            using (var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.IsFalse(CodeAltaSingleInstanceGuard.IsAbandoned(null, path), "An unreadable lock is not an empty lock.");
            }

            Assert.IsTrue(CodeAltaSingleInstanceGuard.IsAbandoned(null, path), "An old, readable empty lock can be reclaimed.");
            File.WriteAllText(path, "incomplete");
            Assert.IsFalse(CodeAltaSingleInstanceGuard.IsAbandoned(null, path), "A recent incomplete lock may still be starting.");
            File.WriteAllText(path, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-2)); // not a limit
            Assert.IsFalse(CodeAltaSingleInstanceGuard.IsAbandoned(null, path), "A completed PID must be read again before reclamation.");
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
