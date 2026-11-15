using CodeAlta.Catalog.Storage;

namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed class ProfileStorageScannerTests
{
    [TestMethod]
    public async Task ProgressIsIncrementalAndCancelingTheWorkerDoesNotLockSessionFiles()
    {
        var root = NewRoot();
        Directory.CreateDirectory(root);
        using var cancellation = new CancellationTokenSource();
        using var release = new ManualResetEventSlim();
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reports = new List<ProfileStorageProgress>();
        Task<ProfileStorageSnapshot>? worker = null;
        try
        {
            var journal = Path.Combine(root, "session.jsonl");
            await File.WriteAllTextAsync(journal, "initial");
            await File.WriteAllTextAsync(Path.Combine(root, "second"), "fixture");
            var progress = new Progress(value =>
            {
                reports.Add(value);
                if (value.ProcessedFiles != 1) return;
                paused.SetResult();
                if (!release.Wait(TestTime.Seconds(10))) throw new TimeoutException("Test did not release scan.");
            });
            worker = new ProfileStorageScanner(new CatalogOptions { GlobalRoot = root }).ScanAsync(progress, cancellation.Token);
            await paused.Task.WithinAsync(TestTime.Seconds(10));
            Assert.IsFalse(worker.IsCompleted);
            await using (var writer = new FileStream(journal, FileMode.Append, FileAccess.Write, FileShare.None))
                await writer.WriteAsync(new byte[8]);
            cancellation.Cancel();
            release.Set();
            await Assert.ThrowsAsync<OperationCanceledException>(() => worker);
            Assert.AreEqual(new ProfileStorageProgress(0, 0), reports[0]);
            Assert.AreEqual(new ProfileStorageProgress(1, 0), reports[^1]);
            var finalReports = new List<ProfileStorageProgress>();
            var completed = await new ProfileStorageScanner(new CatalogOptions { GlobalRoot = root }).ScanAsync(new Progress(finalReports.Add), default);
            Assert.AreEqual(new ProfileStorageProgress(2, 0), finalReports[^1]);
            Assert.AreEqual(2L, completed.ProcessedFiles);
        }
        finally
        {
            release.Set();
            if (worker is not null) { try { await worker; } catch (OperationCanceledException) { } }
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task UsesEnumeratedFileMetadataWithoutReopeningPaths()
    {
        var root = NewRoot();
        var scanned = Path.Combine(root, "profile");
        Directory.CreateDirectory(scanned);
        // Move into this fixture's own parent, not the shared temp directory. Windows MoveFileEx
        // needs a writable destination-directory handle, which other cleanup fixtures pin read-only.
        var moved = Path.Combine(root, "moved.jsonl");
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(scanned, "session.jsonl"), new byte[64]);
            var scanner = new ProfileStorageScanner(new CatalogOptions { GlobalRoot = scanned })
            {
                EntryEnumerated = (path, _) =>
                {
                    try { File.Move(path, moved); }
                    catch (IOException ex) { Assert.Fail($"Could not move the metadata-test fixture: {ex}"); }
                },
            };
            var result = await scanner.ScanAsync(CancellationToken.None);
            Assert.IsTrue(File.Exists(moved), "The test must invalidate the enumerated path.");
            Assert.AreEqual(64L, result.TotalBytes, "Measurement uses enumeration metadata, not another I/O for each file and ancestor.");
            Assert.IsFalse(result.IsPartial);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task DirectoryReplacedByLinkAfterDiscoveryIsNotTraversed()
    {
        TestLinks.RequireSymbolicLinks();
        var root = NewRoot();
        var outside = NewRoot();
        Directory.CreateDirectory(Path.Combine(root, "child"));
        Directory.CreateDirectory(outside);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(outside, "not-profile-data"), new byte[100]);
            var scanner = new ProfileStorageScanner(new CatalogOptions { GlobalRoot = root })
            {
                EntryEnumerated = (path, isDirectory) =>
                {
                    if (!isDirectory) return;
                    Directory.Delete(path);
                    Directory.CreateSymbolicLink(path, outside);
                },
            };
            var result = await scanner.ScanAsync(CancellationToken.None);
            Assert.AreEqual(0L, result.TotalBytes);
            Assert.AreEqual(1L, result.SkippedLinks);
        }
        finally { Directory.Delete(root, true); Directory.Delete(outside, true); }
    }

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
                ("reviews/project/project.json", "reviews", 24),
                ("dev/sessions/session.jsonl", "developer", 21), ("dev/data/alta.sqlite3", "developer", 22),
                ("unrecognized/keep", "other", 23),
                ("browser/profile/Cookies", "browser", 25), ("llama/models/model.gguf", "models", 26),
                ("llama/bin/llama-server", "models", 27), ("dev/browser/profile/Cookies", "developer", 28),
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
        TestLinks.RequireSymbolicLinks();
        var root = NewRoot();
        var outside = NewRoot();
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(outside, "not-profile-data"), new byte[100]);
            var link = Path.Combine(root, "linked");
            Directory.CreateSymbolicLink(link, outside);
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

    [TestMethod]
    public async Task OldScratchpadFiles_AreTheCandidatesByAge()
    {
        var root = NewRoot();
        try
        {
            var now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            // The scan runs a month from now: every file written today is old then, unless its last write says otherwise.
            var scanTime = now.AddDays(30);
            var recent = (scanTime - StorageSettings.ScratchpadMaxAge + TimeSpan.FromHours(1)).UtcDateTime;
            async Task<string> Write(string relative, int bytes, DateTime? lastWrite = null)
            {
                var path = Path.Combine(root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllBytesAsync(path, new byte[bytes]);
                if (lastWrite is { } time) File.SetLastWriteTimeUtc(path, time);
                return path;
            }

            await Write("scratchpad/old.md", 10);
            await Write("scratchpad/run/old-in-recent-folder.md", 20);
            await Write("scratchpad/run/recent.md", 40, recent);
            await Write("scratchpad/recent.md", 80, recent);
            // Exactly at the cutoff is not before it: not old.
            await Write("scratchpad/at-cutoff.md", 640, (scanTime - StorageSettings.ScratchpadMaxAge).UtcDateTime);
            // A folder written recently keeps nothing old it holds: the cleanup goes by each file.
            Directory.SetLastWriteTimeUtc(Path.Combine(root, "scratchpad/run"), recent);
            // Not the scratchpad of this instance: other categories, and the developer's own scratchpad.
            await Write("dev/scratchpad/old.md", 160);
            await Write("sessions/2026/11/10/old.jsonl", 320);

            var scanner = new ProfileStorageScanner(new CatalogOptions { GlobalRoot = root }) { Time = new FixedTime(scanTime) };
            var snapshot = await scanner.ScanAsync(CancellationToken.None);

            Assert.AreEqual(scanTime, snapshot.StartedAt);
            Assert.AreEqual(scanTime, snapshot.CompletedAt, "both scan timestamps use the same clock");
            Assert.AreEqual(scanTime - StorageSettings.ScratchpadMaxAge, snapshot.ScratchpadCutoff);
            Assert.AreEqual(30L, snapshot.OldScratchpadBytes);
            Assert.AreEqual(2L, snapshot.OldScratchpadFiles);
            Assert.AreEqual(790L, snapshot.Categories.Single(category => category.Id == "scratchpad").Bytes);

            var developer = new ProfileStorageScanner(new CatalogOptions { GlobalRoot = root, StateRoot = Path.Combine(root, "dev") }) { Time = new FixedTime(scanTime) };
            var ofDeveloper = await developer.ScanAsync(CancellationToken.None);
            Assert.AreEqual(160L, ofDeveloper.OldScratchpadBytes, "the developer instance clears its own scratchpad, not the normal one");
            Assert.AreEqual(1L, ofDeveloper.OldScratchpadFiles);

            var today = await new ProfileStorageScanner(new CatalogOptions { GlobalRoot = root }).ScanAsync(CancellationToken.None);
            Assert.AreEqual(0L, today.OldScratchpadBytes, "nothing is a week old yet");
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task ScratchpadFileCreatedRecently_ButWrittenLongAgo_IsNotOld()
    {
        var root = NewRoot();
        try
        {
            var scanTime = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()).AddDays(30);
            var recent = (scanTime - StorageSettings.ScratchpadMaxAge + TimeSpan.FromHours(1)).UtcDateTime;
            var path = Path.Combine(root, "scratchpad", "copied.md");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, new byte[10]);
            // A copy: created anew, with the last write of its source, long before the cutoff.
            File.SetCreationTimeUtc(path, recent);
            File.SetLastWriteTimeUtc(path, (scanTime - TimeSpan.FromDays(60)).UtcDateTime);
            if (File.GetCreationTimeUtc(path) != recent) Assert.Inconclusive("This file system does not let a test set the creation time of a file.");

            var scanner = new ProfileStorageScanner(new CatalogOptions { GlobalRoot = root }) { Time = new FixedTime(scanTime) };
            var snapshot = await scanner.ScanAsync(CancellationToken.None);

            Assert.AreEqual(0L, snapshot.OldScratchpadFiles, "the later of the creation and the last write counts");
            Assert.AreEqual(10L, snapshot.Categories.Single(category => category.Id == "scratchpad").Bytes);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task LinkInScratchpad_IsNotCountedAsOld()
    {
        TestLinks.RequireSymbolicLinks();
        var root = NewRoot();
        var outside = NewRoot();
        Directory.CreateDirectory(Path.Combine(root, "scratchpad"));
        Directory.CreateDirectory(outside);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(outside, "large"), new byte[100]);
            Directory.CreateSymbolicLink(Path.Combine(root, "scratchpad", "folder"), outside);
            File.CreateSymbolicLink(Path.Combine(root, "scratchpad", "file"), Path.Combine(outside, "large"));
            var scanner = new ProfileStorageScanner(new CatalogOptions { GlobalRoot = root }) { Time = new FixedTime(DateTimeOffset.UtcNow.AddDays(30)) };
            var snapshot = await scanner.ScanAsync(CancellationToken.None);
            Assert.AreEqual(0L, snapshot.OldScratchpadBytes);
            Assert.AreEqual(0L, snapshot.OldScratchpadFiles);
            Assert.AreEqual(2L, snapshot.SkippedLinks);
        }
        finally { Directory.Delete(root, true); Directory.Delete(outside, true); }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "codealta-storage-scan-" + Guid.NewGuid().ToString("N"));

    private sealed class Progress(Action<ProfileStorageProgress> report) : IProgress<ProfileStorageProgress>
    {
        public void Report(ProfileStorageProgress value) => report(value);
    }
}
