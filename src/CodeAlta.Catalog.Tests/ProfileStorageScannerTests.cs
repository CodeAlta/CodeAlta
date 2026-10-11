using CodeAlta.Catalog.Storage;

namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed class ProfileStorageScannerTests
{
    [TestMethod]
    public async Task MeasuresHiddenUnknownAndDeveloperFilesOnce_WithoutChangingAnything()
    {
        var root = NewRoot();
        try
        {
            var fixtures = new (string Path, string Category, int Bytes)[]
            {
                ("sessions/2026/10/10/session.jsonl", "sessions", 10),
                ("sessions/2026/10/10/session.attachments/image.png", "sessions", 11),
                ("scratchpad/.hidden", "scratchpad", 12), ("config.toml", "configuration", 13),
                ("auth/secret", "configuration", 14), ("plugin-data/keep", "extensions", 15),
                ("projects/project/state", "projects", 16), ("data/alta.sqlite3-wal", "database", 17),
                ("data/backups/backup", "database", 18), ("cache/cache", "cacheLogs", 19),
                ("saved_statistics_after_deleted_sessions/archives.sqlite3", "savedStatistics", 20),
                ("dev/sessions/session.jsonl", "developer", 21), ("dev/data/alta.sqlite3", "developer", 22),
                ("unrecognized/keep", "other", 23),
            };
            foreach (var fixture in fixtures)
            {
                var path = Path.Combine(root, fixture.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllBytesAsync(path, Enumerable.Repeat((byte)42, fixture.Bytes).ToArray());
            }
            if (OperatingSystem.IsWindows()) File.SetAttributes(Path.Combine(root, "scratchpad/.hidden"), FileAttributes.Hidden);
            var snapshot = await new ProfileStorageScanner(new CatalogOptions { GlobalRoot = root, StateRoot = Path.Combine(root, "dev") }).ScanAsync(CancellationToken.None);
            Assert.IsFalse(snapshot.IsPartial);
            Assert.AreEqual(fixtures.Sum(f => (long)f.Bytes), snapshot.TotalBytes);
            Assert.AreEqual(snapshot.TotalBytes, snapshot.Categories.Sum(c => c.Bytes));
            foreach (var group in fixtures.GroupBy(f => f.Category))
            {
                var category = snapshot.Categories.Single(c => c.Id == group.Key);
                Assert.AreEqual(group.Sum(f => (long)f.Bytes), category.Bytes);
                Assert.AreEqual((long)group.Count(), category.FileCount);
            }
            foreach (var fixture in fixtures)
                CollectionAssert.AreEqual(Enumerable.Repeat((byte)42, fixture.Bytes).ToArray(), await File.ReadAllBytesAsync(Path.Combine(root, fixture.Path)));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task MissingRoot_IsEmpty_AndCanceledScanCreatesNothing()
    {
        var root = NewRoot();
        var scanner = new ProfileStorageScanner(new CatalogOptions { GlobalRoot = root });
        var snapshot = await scanner.ScanAsync(CancellationToken.None);
        Assert.AreEqual(0L, snapshot.TotalBytes);
        Assert.IsFalse(snapshot.IsPartial);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => scanner.ScanAsync(cancellation.Token));
        Assert.IsFalse(Directory.Exists(root));
    }

    [TestMethod]
    public async Task FileContentsNeedNotBeReadable_AndInvalidRootIsPartial()
    {
        var root = NewRoot();
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "locked");
            await File.WriteAllBytesAsync(file, new byte[64]);
            using (var locked = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var result = await new ProfileStorageScanner(new CatalogOptions { GlobalRoot = root }).ScanAsync(CancellationToken.None);
                Assert.AreEqual(64L, result.TotalBytes, "the scanner does not open file contents");
                Assert.IsFalse(result.IsPartial);
            }
            var invalid = await new ProfileStorageScanner(new CatalogOptions { GlobalRoot = file }).ScanAsync(CancellationToken.None);
            Assert.IsTrue(invalid.IsPartial);
            Assert.AreEqual(0L, invalid.TotalBytes);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task LinksAndLinkedRoots_AreSkipped()
    {
        var root = NewRoot();
        var outside = NewRoot();
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(outside, "not-profile-data"), new byte[100]);
            var link = Path.Combine(root, "linked");
            try { Directory.CreateSymbolicLink(link, outside); }
            catch (UnauthorizedAccessException) { Assert.Inconclusive("Creating symlinks requires platform permission."); }
            var snapshot = await new ProfileStorageScanner(new CatalogOptions { GlobalRoot = root }).ScanAsync(CancellationToken.None);
            Assert.AreEqual(0L, snapshot.TotalBytes);
            Assert.AreEqual(1L, snapshot.SkippedLinks);
            Assert.IsTrue(snapshot.IsPartial);
            var linked = await new ProfileStorageScanner(new CatalogOptions { GlobalRoot = link }).ScanAsync(CancellationToken.None);
            Assert.AreEqual(0L, linked.TotalBytes);
            Assert.IsTrue(linked.IsPartial);
            Assert.IsTrue(File.Exists(Path.Combine(outside, "not-profile-data")));
        }
        finally { Directory.Delete(root, true); Directory.Delete(outside, true); }
    }

    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "codealta-storage-scan-" + Guid.NewGuid().ToString("N"));
}
