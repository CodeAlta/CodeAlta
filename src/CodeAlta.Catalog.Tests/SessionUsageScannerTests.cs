using CodeAlta.Catalog.Storage;

namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed class SessionUsageScannerTests
{
    [TestMethod]
    public async Task JournalAlone_HasItsBytesAndLastWrite()
    {
        using var profile = new Profile();
        var journal = profile.Write("2026/11/10/alone.jsonl", 40);
        var written = new DateTime(2026, 11, 10, 8, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(journal, written);

        var snapshot = await profile.ScanAsync();

        var session = snapshot.Sessions.Single();
        Assert.AreEqual(new SessionUsage("alone", 40, 1, 0, 0), session with { JournalLastWrite = null });
        Assert.AreEqual(new DateTimeOffset(written), session.JournalLastWrite);
        Assert.AreEqual(0L, snapshot.UnownedBytes);
        Assert.IsFalse(snapshot.IsPartial);
    }

    [TestMethod]
    public async Task JournalWithAttachments_CountsBothApart()
    {
        using var profile = new Profile();
        profile.Write("2026/11/10/with.jsonl", 10);
        profile.Write("2026/11/10/with.attachments/image.png", 100);
        profile.Write("2026/11/10/with.attachments/nested/file.txt", 5);
        profile.Write("traces/with.trace", 7);

        var session = (await profile.ScanAsync()).Sessions.Single();

        Assert.AreEqual(new SessionUsage("with", 10, 1, 112, 3), session with { JournalLastWrite = null });
        Assert.AreEqual(122L, session.TotalBytes);
    }

    [TestMethod]
    public async Task AttachmentsWithoutJournal_HaveNoLastWrite()
    {
        using var profile = new Profile();
        profile.Write("2026/11/10/orphan.attachments/image.png", 30);

        var session = (await profile.ScanAsync()).Sessions.Single();

        Assert.AreEqual(new SessionUsage("orphan", 0, 0, 30, 1), session);
        Assert.IsNull(session.JournalLastWrite);
    }

    [TestMethod]
    public async Task SessionInTwoDayFolders_IsSummed_WithTheLaterWrite()
    {
        using var profile = new Profile();
        var first = profile.Write("2026/11/09/twice.jsonl", 10);
        var second = profile.Write("2026/11/10/twice.jsonl", 20);
        profile.Write("2026/11/09/twice.attachments/a", 3);
        profile.Write("2026/11/10/twice.attachments/b", 4);
        profile.Write("2026/11/10/other.jsonl", 1);
        var later = new DateTime(2026, 11, 12, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(first, later);
        File.SetLastWriteTimeUtc(second, later.AddDays(-2));

        var snapshot = await profile.ScanAsync();

        CollectionAssert.AreEqual(new[] { "other", "twice" }, snapshot.Sessions.Select(session => session.SessionId).ToArray());
        var twice = snapshot.Sessions[1];
        Assert.AreEqual(new SessionUsage("twice", 30, 2, 7, 2), twice with { JournalLastWrite = null });
        Assert.AreEqual(new DateTimeOffset(later), twice.JournalLastWrite);
    }

    [TestMethod]
    public async Task FilesOfNoSession_AreCountedApart()
    {
        using var profile = new Profile();
        profile.Write("2026/11/10/kept.jsonl", 10);
        profile.Write("leases/kept.lease", 11);
        profile.Write("markers/kept.json", 12);
        // A journal's name in the leases folder still belongs to no session: its owner removes it.
        profile.Write("leases/looks-like.jsonl", 13);
        profile.Write("index.sqlite3", 14);
        profile.Write("2026/11/10/notes.txt", 15);

        var snapshot = await profile.ScanAsync();

        Assert.AreEqual("kept", snapshot.Sessions.Single().SessionId);
        Assert.AreEqual(10L, snapshot.Sessions.Single().JournalBytes);
        Assert.AreEqual(11L + 12 + 13 + 14 + 15, snapshot.UnownedBytes);
        Assert.AreEqual(5L, snapshot.UnownedFiles);
    }

    [TestMethod]
    public async Task LinkInAttachments_IsNotFollowed()
    {
        TestLinks.RequireSymbolicLinks();
        using var profile = new Profile();
        var outside = Path.Combine(Path.GetTempPath(), "codealta-session-usage-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(outside, "large"), new byte[4096]);
            profile.Write("2026/11/10/linked.jsonl", 10);
            profile.Write("2026/11/10/linked.attachments/real", 5);
            Directory.CreateSymbolicLink(Path.Combine(profile.Sessions, "2026/11/10/linked.attachments/elsewhere"), outside);
            File.CreateSymbolicLink(Path.Combine(profile.Sessions, "2026/11/10/linked.attachments/file-link"), Path.Combine(outside, "large"));

            var snapshot = await profile.ScanAsync();

            Assert.AreEqual(new SessionUsage("linked", 10, 1, 5, 1), snapshot.Sessions.Single() with { JournalLastWrite = null });
            Assert.AreEqual(2L, snapshot.SkippedLinks);
            Assert.IsTrue(snapshot.IsPartial);
            Assert.IsTrue(File.Exists(Path.Combine(outside, "large")));
        }
        finally { Directory.Delete(outside, true); }
    }

    [TestMethod]
    public async Task SessionsFolderThatIsALink_IsNotFollowed()
    {
        TestLinks.RequireSymbolicLinks();
        var root = Path.Combine(Path.GetTempPath(), "codealta-session-usage-" + Guid.NewGuid().ToString("N"));
        var outside = root + "-outside";
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(outside, "s.jsonl"), new byte[50]);
            Directory.CreateSymbolicLink(Path.Combine(root, "sessions"), outside);
            var snapshot = await new SessionUsageScanner(new CatalogOptions { GlobalRoot = root }).ScanAsync(CancellationToken.None);
            Assert.AreEqual(0, snapshot.Sessions.Count);
            Assert.AreEqual(1L, snapshot.SkippedLinks);
        }
        finally { Directory.Delete(root, true); Directory.Delete(outside, true); }
    }

    [TestMethod]
    public async Task Cancellation_StopsTheWalk_AndProgressIsBounded()
    {
        using var profile = new Profile();
        profile.Write("2026/11/10/a.jsonl", 1);
        profile.Write("2026/11/10/b.jsonl", 1);
        using var cancellation = new CancellationTokenSource();
        var scanner = new SessionUsageScanner(profile.Options) { EntryEnumerated = (_, _) => cancellation.Cancel() };
        await Assert.ThrowsAsync<OperationCanceledException>(() => scanner.ScanAsync(cancellation.Token));

        var reports = new List<ProfileStorageProgress>();
        var completed = await new SessionUsageScanner(profile.Options).ScanAsync(new Progress(reports.Add), CancellationToken.None);
        Assert.AreEqual(new ProfileStorageProgress(0, 0), reports[0]);
        Assert.AreEqual(new ProfileStorageProgress(2, 3), reports[^1]);
        for (var index = 1; index < reports.Count; index++)
        {
            Assert.IsTrue(reports[index].ProcessedFiles >= reports[index - 1].ProcessedFiles, "files never go back");
            Assert.IsTrue(reports[index].ProcessedDirectories >= reports[index - 1].ProcessedDirectories, "folders never go back");
        }

        Assert.AreEqual(2L, completed.ProcessedFiles);
        Assert.AreEqual(3L, completed.ProcessedDirectories);
    }

    [TestMethod]
    public async Task CancellationDuringTheLastProgressReport_GivesNoResult()
    {
        using var profile = new Profile();
        profile.Write("2026/11/10/a.jsonl", 1);
        profile.Write("2026/11/10/b.jsonl", 1);
        using var cancellation = new CancellationTokenSource();
        // The last report has the final counts; a throttled one may have them too: either way no result comes.
        var progress = new Progress(value => { if (value == new ProfileStorageProgress(2, 3)) cancellation.Cancel(); });
        await Assert.ThrowsAsync<OperationCanceledException>(() => new SessionUsageScanner(profile.Options).ScanAsync(progress, cancellation.Token));
    }

    [TestMethod]
    public async Task SessionIdsIgnoreCase_AndKeepOneSpelling()
    {
        using var profile = new Profile();
        profile.Write("2026/11/09/abc.jsonl", 10);
        profile.Write("2026/11/10/ABC.jsonl", 20);
        profile.Write("2026/11/10/ABC.attachments/image.png", 5);

        var session = (await profile.ScanAsync()).Sessions.Single();

        Assert.IsTrue(string.Equals("abc", session.SessionId, StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(session.SessionId is "abc" or "ABC");
        Assert.AreEqual(new SessionUsage(session.SessionId, 30, 2, 5, 1), session with { JournalLastWrite = null });
    }

    [TestMethod]
    public void TotalBytes_SaturatesInsteadOfWrapping()
    {
        Assert.AreEqual(long.MaxValue, new SessionUsage("big", long.MaxValue, 1, 1, 1).TotalBytes);
        Assert.AreEqual(3L, new SessionUsage("small", 1, 1, 2, 1).TotalBytes);
    }

    [TestMethod]
    public async Task ProfileRootReachedThroughALink_IsNotMeasured()
    {
        TestLinks.RequireSymbolicLinks();
        var real = Path.Combine(Path.GetTempPath(), "codealta-session-usage-" + Guid.NewGuid().ToString("N"));
        var link = real + "-link";
        var journal = Path.Combine(real, "sessions/2026/11/10/outside.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(journal)!);
        await File.WriteAllBytesAsync(journal, new byte[50]);
        Directory.CreateSymbolicLink(link, real);
        try
        {
            var snapshot = await new SessionUsageScanner(new CatalogOptions { GlobalRoot = link }).ScanAsync(CancellationToken.None);
            Assert.AreEqual(0, snapshot.Sessions.Count);
            Assert.AreEqual(1L, snapshot.SkippedLinks);
            Assert.IsTrue(snapshot.IsPartial);
        }
        finally { Directory.Delete(link); Directory.Delete(real, true); }
    }

    [TestMethod]
    public async Task FolderWhoseAncestorBecameALinkAfterItWasListed_IsNotEntered()
    {
        TestLinks.RequireSymbolicLinks();
        using var profile = new Profile();
        profile.Write("2026/11/10/moved.jsonl", 50);
        var year = Path.Combine(profile.Sessions, "2026");
        var outside = profile.Root + "-outside";
        try
        {
            var scanner = new SessionUsageScanner(profile.Options)
            {
                FolderTaken = path =>
                {
                    // After listing, release the enumeration handle before moving its ancestor: Windows keeps it open.
                    if (Path.GetFileName(path) != "10" || !Directory.Exists(year) || new DirectoryInfo(year).LinkTarget is not null) return;
                    Directory.Move(year, outside);
                    Directory.CreateSymbolicLink(year, outside);
                },
            };
            var snapshot = await scanner.ScanAsync(CancellationToken.None);
            Assert.IsTrue(new DirectoryInfo(year).LinkTarget is not null, "the test must replace the folder");
            Assert.AreEqual(0, snapshot.Sessions.Count, "the journal is outside the profile now");
            Assert.AreEqual(1L, snapshot.SkippedLinks);
            Assert.IsTrue(snapshot.IsPartial);
        }
        finally { if (Directory.Exists(outside)) Directory.Delete(outside, true); }
    }

    [TestMethod]
    public async Task MissingOrEmptySessionsFolder_IsEmpty_AndNothingIsCreated()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-session-usage-" + Guid.NewGuid().ToString("N"));
        var options = new CatalogOptions { GlobalRoot = root };
        var missing = await new SessionUsageScanner(options).ScanAsync(CancellationToken.None);
        Assert.AreEqual(0, missing.Sessions.Count);
        Assert.IsFalse(missing.IsPartial);
        Assert.IsFalse(Directory.Exists(root));

        Directory.CreateDirectory(options.SessionsRoot);
        try
        {
            var empty = await new SessionUsageScanner(options).ScanAsync(CancellationToken.None);
            Assert.AreEqual(0, empty.Sessions.Count);
            Assert.AreEqual(0L, empty.UnownedBytes);
            Assert.AreEqual(Path.GetFullPath(options.SessionsRoot), empty.SessionsRoot);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task MeasuresTheSessionsOfTheStateRoot()
    {
        using var profile = new Profile();
        var developer = new CatalogOptions { GlobalRoot = profile.Root, StateRoot = Path.Combine(profile.Root, "dev") };
        profile.Write("2026/11/10/normal.jsonl", 1);
        var path = Path.Combine(developer.SessionsRoot, "2026/11/10/developer.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, new byte[2]);

        var snapshot = await new SessionUsageScanner(developer).ScanAsync(CancellationToken.None);

        Assert.AreEqual("developer", snapshot.Sessions.Single().SessionId);
    }

    private sealed class Profile : IDisposable
    {
        public Profile()
        {
            Root = Path.Combine(Path.GetTempPath(), "codealta-session-usage-" + Guid.NewGuid().ToString("N"));
            Options = new CatalogOptions { GlobalRoot = Root };
            Directory.CreateDirectory(Sessions);
        }

        public string Root { get; }
        public CatalogOptions Options { get; }
        public string Sessions => Options.SessionsRoot;

        public string Write(string relative, int bytes)
        {
            var path = Path.Combine(Sessions, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[bytes]);
            return path;
        }

        public Task<SessionUsageSnapshot> ScanAsync() => new SessionUsageScanner(Options).ScanAsync(CancellationToken.None);

        public void Dispose() => Directory.Delete(Root, true);
    }

    private sealed class Progress(Action<ProfileStorageProgress> report) : IProgress<ProfileStorageProgress>
    {
        public void Report(ProfileStorageProgress value) => report(value);
    }
}
