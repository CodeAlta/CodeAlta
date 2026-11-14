using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using CodeAlta.Catalog.Storage;
using Microsoft.Win32.SafeHandles;

namespace CodeAlta.Hosting.Tests;

[TestClass]
public sealed class ScratchpadStartupCleanupTests
{
    [TestMethod]
    public async Task RunningSnapshotAdvancesBeforeCleanupCompletes()
    {
        RequireCleanupPlatform();
        using var fixture = new Fixture(false);
        fixture.Write("scratchpad/one", "remove");
        fixture.Write("scratchpad/two", "remove");
        using var release = new ManualResetEventSlim();
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var opened = 0;
        var cleanup = new ScratchpadStartupCleanup(fixture.Profile, fixture.Guard)
        {
            MaxAge = TimeSpan.Zero,
            BeforeEntryOpen = _ =>
            {
                if (++opened != 2) return;
                paused.SetResult();
                if (!release.Wait(TestTime.Seconds(10))) throw new TimeoutException("Test did not release cleanup.");
            },
        };
        var worker = cleanup.RunAsync(true, true, CancellationToken.None);
        try
        {
            await paused.Task.WithinAsync(TestTime.Seconds(10));
            Assert.IsFalse(worker.IsCompleted);
            Assert.AreEqual(ScratchpadCleanupState.Running, cleanup.Snapshot.State);
            Assert.AreEqual(1L, cleanup.Snapshot.RemovedFiles, "A long cleanup must publish progress before its final result.");
            Assert.AreEqual(1L, cleanup.Snapshot.ProcessedFiles);
            Assert.AreEqual(6L, cleanup.Snapshot.RemovedBytes, "The progress says the size removed so far.");
            ScratchpadCleanupSnapshot? replay = null;
            using var subscription = cleanup.Subscribe(snapshot => replay = snapshot);
            Assert.AreEqual(cleanup.Snapshot, replay);
        }
        finally { release.Set(); await worker; }
        Assert.AreEqual(2L, cleanup.Snapshot.RemovedFiles);
        Assert.AreEqual(2L, cleanup.Snapshot.ProcessedFiles);
        Assert.AreEqual(12L, cleanup.Snapshot.RemovedBytes, "The size of the files removed, \"remove\" twice.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RemovesOnlyOwnedContentsAndPreservesRootAndProtectedSentinels(bool developer)
    {
        RequireCleanupPlatform();
        using var fixture = new Fixture(developer);
        var scratchpad = fixture.Scratchpad;
        var deep = scratchpad;
        for (var i = 0; i < 40; i++) deep = Path.Combine(deep, "d");
        Directory.CreateDirectory(deep);
        File.WriteAllText(Path.Combine(deep, "note.md"), "remove");
        File.WriteAllText(Path.Combine(scratchpad, ".hidden.md"), "remove");
        var protectedPaths = new[] { "config.toml", "auth/key", "plugins/data", "sessions/note", "data/db", "unknown", "dev/scratchpad/peer.md" };
        if (developer) protectedPaths = ["config.toml", "scratchpad/legacy.md", "auth/key", "dev/sessions/note"];
        foreach (var path in protectedPaths) fixture.Write(path, "protected");

        var cleanup = fixture.Cleanup();
        await cleanup.RunAsync(true, true, CancellationToken.None);

        Assert.AreEqual(ScratchpadCleanupState.Completed, cleanup.Snapshot.State, string.Join(",", cleanup.Snapshot.Reasons));
        Assert.AreEqual(2L, cleanup.Snapshot.RemovedFiles);
        Assert.AreEqual(12L, cleanup.Snapshot.RemovedBytes, "Only the files removed count: the protected ones stay.");
        Assert.AreEqual(40L, cleanup.Snapshot.RemovedDirectories);
        Assert.AreEqual(2L, cleanup.Snapshot.ProcessedFiles);
        Assert.AreEqual(40L, cleanup.Snapshot.ProcessedDirectories);
        Assert.IsTrue(Directory.Exists(scratchpad));
        Assert.AreEqual(0, Directory.GetFileSystemEntries(scratchpad).Length);
        foreach (var path in protectedPaths) Assert.AreEqual("protected", File.ReadAllText(Path.Combine(fixture.Root, path)));
    }

    [TestMethod]
    public async Task KeepsEntriesChangedWithinTheRetentionAndRemovesOlderOnes()
    {
        RequireCleanupPlatform();
        // Linux sets neither a birth time nor a change time in the past, and the removal reads one of
        // them: no entry a test writes can look old there. Windows and macOS set the creation time.
        if (OperatingSystem.IsLinux()) Assert.Inconclusive("Linux cannot age the birth or change time of a file.");
        using var fixture = new Fixture(false);
        var old = DateTime.UtcNow - StorageSettings.ScratchpadMaxAge - TimeSpan.FromDays(1);
        fixture.Write("scratchpad/old.md", "remove");
        fixture.Write("scratchpad/recent.md", "keep");
        fixture.Write("scratchpad/old-folder/note.md", "remove");
        fixture.Write("scratchpad/mixed/old.md", "remove");
        fixture.Write("scratchpad/mixed/recent.md", "keep");
        fixture.Write("scratchpad/mixed/inner/recent.md", "keep");
        fixture.Write("scratchpad/copied.md", "keep");
        Directory.CreateDirectory(Path.Combine(fixture.Scratchpad, "recent-empty"));
        Age(Path.Combine(fixture.Scratchpad, "old.md"), old);
        Age(Path.Combine(fixture.Scratchpad, "old-folder", "note.md"), old);
        Age(Path.Combine(fixture.Scratchpad, "mixed", "old.md"), old);
        // A copy keeps the write time of its source, but it was created now: it is recent.
        File.SetLastWriteTimeUtc(Path.Combine(fixture.Scratchpad, "copied.md"), old);
        // Directory times last: writing an entry in a directory changes its time.
        Age(Path.Combine(fixture.Scratchpad, "mixed", "inner"), old);
        Age(Path.Combine(fixture.Scratchpad, "mixed"), old);
        Age(Path.Combine(fixture.Scratchpad, "old-folder"), old);

        var cleanup = new ScratchpadStartupCleanup(fixture.Profile, fixture.Guard);
        await cleanup.RunAsync(true, true, CancellationToken.None);

        Assert.AreEqual(ScratchpadCleanupState.Completed, cleanup.Snapshot.State, string.Join(",", cleanup.Snapshot.Reasons));
        Assert.AreEqual(0L, cleanup.Snapshot.SkippedEntries);
        Assert.AreEqual(3L, cleanup.Snapshot.RemovedFiles);
        Assert.AreEqual(1L, cleanup.Snapshot.RemovedDirectories);
        CollectionAssert.AreEquivalent(
            new[] { "copied.md", "mixed", "mixed/inner", "mixed/inner/recent.md", "mixed/recent.md", "recent-empty", "recent.md" },
            Directory.GetFileSystemEntries(fixture.Scratchpad, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(fixture.Scratchpad, path).Replace('\\', '/')).ToArray());

        static void Age(string path, DateTime time)
        {
            if (Directory.Exists(path))
            {
                Directory.SetCreationTimeUtc(path, time);
                Directory.SetLastWriteTimeUtc(path, time);
            }
            else
            {
                File.SetCreationTimeUtc(path, time);
                File.SetLastWriteTimeUtc(path, time);
            }
        }
    }

    [TestMethod]
    [DataRow(false, true, "disabled")]
    [DataRow(true, false, "invalid-configuration")]
    public async Task DisabledOrInvalidSettingNeverDeletes(bool enabled, bool valid, string reason)
    {
        using var fixture = new Fixture(false);
        fixture.Write("scratchpad/note.md", "keep");
        var cleanup = fixture.Cleanup();
        await cleanup.RunAsync(enabled, valid, CancellationToken.None);
        Assert.AreEqual(ScratchpadCleanupState.Skipped, cleanup.Snapshot.State);
        CollectionAssert.Contains(cleanup.Snapshot.Reasons.ToArray(), reason);
        Assert.AreEqual("keep", File.ReadAllText(Path.Combine(fixture.Scratchpad, "note.md")));
    }

    [TestMethod]
    public async Task MissingOwnershipCannotAuthorizeDeletion()
    {
        using var fixture = new Fixture(false);
        fixture.Write("scratchpad/note.md", "keep");
        var cleanup = new ScratchpadStartupCleanup(fixture.Profile, null);
        await cleanup.RunAsync(true, true, CancellationToken.None);
        Assert.AreEqual(ScratchpadCleanupState.Skipped, cleanup.Snapshot.State);
        Assert.IsTrue(File.Exists(Path.Combine(fixture.Scratchpad, "note.md")));
    }

    [TestMethod]
    [DataRow(0, ScratchpadCleanupState.Skipped)]
    [DataRow(1, ScratchpadCleanupState.Partial)]
    public async Task UnexpectedManagedFailureStopsWorkerAndPublishesBoundedResult(int filesBeforeFault, ScratchpadCleanupState expected)
    {
        RequireCleanupPlatform();
        using var fixture = new Fixture(false);
        fixture.Write("scratchpad/one", "remove or retain");
        fixture.Write("scratchpad/two", "retain");
        var attempts = 0;
        var cleanup = new ScratchpadStartupCleanup(fixture.Profile, fixture.Guard)
        {
            MaxAge = TimeSpan.Zero,
            BeforeEntryDelete = _ =>
            {
                if (attempts++ == filesBeforeFault) throw new InvalidOperationException("Sensitive unexpected failure details");
            },
        };
        var worker = cleanup.RunAsync(true, true, CancellationToken.None);
        await worker;
        Assert.IsTrue(worker.IsCompletedSuccessfully);
        Assert.AreSame(worker, cleanup.RunAsync(true, true, CancellationToken.None));
        var diagnostic = $"Attempts: {attempts}; reasons: {string.Join(", ", cleanup.Snapshot.Reasons)}";
        Assert.AreEqual(expected, cleanup.Snapshot.State, diagnostic);
        Assert.AreEqual((long)filesBeforeFault, cleanup.Snapshot.RemovedFiles);
        Assert.AreEqual(2 - filesBeforeFault, Directory.GetFiles(fixture.Scratchpad).Length);
        CollectionAssert.AreEqual(new[] { "unexpected-cleanup-failure" }, cleanup.Snapshot.Reasons.ToArray(), diagnostic);
        Assert.IsNotNull(cleanup.Snapshot.CompletedAt);
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Root, "dev", "alta.lock")));
        ScratchpadCleanupSnapshot? replayed = null;
        using var subscription = cleanup.Subscribe(snapshot => replayed = snapshot);
        Assert.AreEqual(cleanup.Snapshot, replayed);
        using var successor = CodeAltaSingleInstanceGuard.Acquire(Path.Combine(fixture.Root, "dev", "alta.lock"));
    }

    [TestMethod]
    public async Task MissingRootIsCompletedWithoutCreatingScratchpad()
    {
        RequireCleanupPlatform();
        using var fixture = new Fixture(false);
        var cleanup = fixture.Cleanup();
        await cleanup.RunAsync(true, true, CancellationToken.None);
        Assert.AreEqual(ScratchpadCleanupState.Completed, cleanup.Snapshot.State);
        Assert.IsFalse(Directory.Exists(fixture.Scratchpad));
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Root, "dev", "alta.lock")));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("unknown")]
    [DataRow("2147483647")]
    public async Task EveryExistingLegacyLockFailsClosedWithoutReclaiming(string contents)
    {
        RequireCleanupPlatform();
        using var fixture = new Fixture(false);
        fixture.Write("scratchpad/note.md", "keep");
        fixture.Write("dev/alta.lock", contents);
        var cleanup = fixture.Cleanup();
        await cleanup.RunAsync(true, true, CancellationToken.None);
        Assert.AreEqual(ScratchpadCleanupState.Skipped, cleanup.Snapshot.State);
        CollectionAssert.Contains(cleanup.Snapshot.Reasons.ToArray(), "legacy-developer-exclusion-unavailable");
        Assert.AreEqual(contents, File.ReadAllText(Path.Combine(fixture.Root, "dev", "alta.lock")));
        Assert.AreEqual("keep", File.ReadAllText(Path.Combine(fixture.Scratchpad, "note.md")));
    }

    [TestMethod]
    public async Task LegacyOwnerPreventsNormalCleanupButNormalOwnerDoesNotPreventDeveloperCleanup()
    {
        RequireCleanupPlatform();
        using var fixture = new Fixture(false);
        var developer = CodeAltaInstanceProfile.Create(fixture.Root, true);
        using var peer = CodeAltaSingleInstanceGuard.Acquire(developer.LockFilePath);
        fixture.Write("scratchpad/normal.md", "keep");
        fixture.Write("dev/scratchpad/dev.md", "remove");
        var normal = fixture.Cleanup();
        var isolated = new ScratchpadStartupCleanup(developer, peer) { MaxAge = TimeSpan.Zero };
        await Task.WhenAll(normal.RunAsync(true, true, CancellationToken.None), isolated.RunAsync(true, true, CancellationToken.None));
        Assert.AreEqual(ScratchpadCleanupState.Skipped, normal.Snapshot.State);
        Assert.AreEqual(ScratchpadCleanupState.Completed, isolated.Snapshot.State);
        Assert.IsTrue(File.Exists(Path.Combine(fixture.Scratchpad, "normal.md")));
        Assert.IsFalse(File.Exists(Path.Combine(developer.StateRoot, "scratchpad", "dev.md")));
    }

    [TestMethod]
    public async Task LegacyAdmissionAndDelayedPathDeleteCannotBreakTemporaryGate()
    {
        RequireCleanupPlatform();
        using var fixture = new Fixture(false);
        fixture.Write("scratchpad/note.md", "remove");
        var developerLock = Path.Combine(fixture.Root, "dev", "alta.lock");
        var checkedGate = false;
        var cleanup = new ScratchpadStartupCleanup(fixture.Profile, fixture.Guard)
        {
            MaxAge = TimeSpan.Zero,
            BeforeEntryDelete = _ =>
            {
                if (OperatingSystem.IsWindows())
                {
                    // Delete-pending names fail before even the legacy PID-reader fallback.
                    Assert.Throws<UnauthorizedAccessException>(() => CodeAltaSingleInstanceGuard.Acquire(developerLock));
                    Assert.Throws<UnauthorizedAccessException>(() => File.Delete(developerLock));
                }
                else
                {
                    // The reservation holds the live PID of this process, which the legacy guard never reclaims.
                    Assert.Throws<CodeAltaAlreadyRunningException>(() => CodeAltaSingleInstanceGuard.Acquire(developerLock));
                    Assert.AreEqual(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), File.ReadAllText(developerLock));
                    Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(developerLock));
                }
                checkedGate = true;
            },
        };
        await cleanup.RunAsync(true, true, CancellationToken.None);
        Assert.IsTrue(checkedGate);
        Assert.AreEqual(ScratchpadCleanupState.Completed, cleanup.Snapshot.State);
        using var successor = CodeAltaSingleInstanceGuard.Acquire(developerLock);
    }

    [TestMethod]
    public async Task LockedAndReadOnlyEntriesRemainAndOtherEntriesAreRemoved()
    {
        RequireWindows();
        using var fixture = new Fixture(true);
        fixture.Write("dev/scratchpad/locked", "keep");
        fixture.Write("dev/scratchpad/readonly", "keep");
        fixture.Write("dev/scratchpad/free", "remove");
        var readOnly = Path.Combine(fixture.Scratchpad, "readonly");
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);
        try
        {
            using var locked = new FileStream(Path.Combine(fixture.Scratchpad, "locked"), FileMode.Open, FileAccess.Read, FileShare.Read);
            var cleanup = fixture.Cleanup();
            await cleanup.RunAsync(true, true, CancellationToken.None);
            Assert.AreEqual(ScratchpadCleanupState.Partial, cleanup.Snapshot.State);
            Assert.AreEqual(1L, cleanup.Snapshot.RemovedFiles);
            Assert.AreEqual(2L, cleanup.Snapshot.SkippedEntries);
            Assert.IsTrue(File.Exists(readOnly));
            Assert.IsFalse(File.Exists(Path.Combine(fixture.Scratchpad, "free")));
            Assert.IsTrue((File.GetAttributes(readOnly) & FileAttributes.ReadOnly) != 0);
        }
        finally { File.SetAttributes(readOnly, FileAttributes.Normal); }
    }

    [TestMethod]
    public async Task DescendantLinksAreLeftInPlaceAndOutsideFilesAreUntouched()
    {
        RequireCleanupPlatform();
        using var fixture = new Fixture(true);
        fixture.Write("outside/sentinel", "protected");
        Directory.CreateDirectory(fixture.Scratchpad);
        var link = Path.Combine(fixture.Scratchpad, "link");
        CreateDirectoryLink(link, Path.Combine(fixture.Root, "outside"));
        try
        {
            var cleanup = fixture.Cleanup();
            await cleanup.RunAsync(true, true, CancellationToken.None);
            Assert.AreEqual(ScratchpadCleanupState.Partial, cleanup.Snapshot.State);
            Assert.AreEqual("protected", File.ReadAllText(Path.Combine(fixture.Root, "outside", "sentinel")));
            Assert.IsNotNull(new DirectoryInfo(link).LinkTarget);
        }
        finally { Directory.Delete(link); }
    }

    [TestMethod]
    public async Task LinkedScratchpadRootIsSkipped()
    {
        RequireCleanupPlatform();
        using var fixture = new Fixture(true);
        fixture.Write("outside/sentinel", "protected");
        CreateDirectoryLink(fixture.Scratchpad, Path.Combine(fixture.Root, "outside"));
        try
        {
            var cleanup = fixture.Cleanup();
            await cleanup.RunAsync(true, true, CancellationToken.None);
            Assert.AreEqual(ScratchpadCleanupState.Skipped, cleanup.Snapshot.State);
            Assert.AreEqual("protected", File.ReadAllText(Path.Combine(fixture.Root, "outside", "sentinel")));
        }
        finally { Directory.Delete(fixture.Scratchpad); }
    }

    [TestMethod]
    public async Task JunctionSwapAfterEnumerationCannotRedirectRemoval()
    {
        RequireWindows();
        using var fixture = new Fixture(true);
        fixture.Write("outside/sentinel", "protected");
        var child = Path.Combine(fixture.Scratchpad, "child");
        Directory.CreateDirectory(child);
        var swapped = false;
        Exception? swapFailure = null;
        var cleanup = new ScratchpadStartupCleanup(fixture.Profile, fixture.Guard)
        {
            MaxAge = TimeSpan.Zero,
            BeforeEntryOpen = entry =>
            {
                if (Path.GetFileName(entry) != "child" || swapped) return;
                // Exchange the ordinary enumerated directory for a junction before the worker opens it.
                try
                {
                    CreateJunction(child, Path.Combine(fixture.Root, "outside"));
                    swapped = true;
                }
                catch (Exception error) { swapFailure = error; throw; }
            },
        };
        try
        {
            await cleanup.RunAsync(true, true, CancellationToken.None);
            Assert.IsNull(swapFailure, swapFailure?.ToString());
            Assert.IsTrue(swapped);
            Assert.AreEqual(ScratchpadCleanupState.Partial, cleanup.Snapshot.State);
            Assert.AreEqual(0L, cleanup.Snapshot.RemovedFiles);
            CollectionAssert.Contains(cleanup.Snapshot.Reasons.ToArray(), "linked-entry");
            Assert.AreEqual("protected", File.ReadAllText(Path.Combine(fixture.Root, "outside", "sentinel")));
            Assert.IsTrue((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0);
        }
        finally
        {
            if (Directory.Exists(child) && (File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) Directory.Delete(child);
        }
    }

    [TestMethod]
    public async Task HandlesPreventFileRootAndAncestorPathSwapsDuringRemoval()
    {
        RequireWindows();
        using var fixture = new Fixture(true);
        fixture.Write("dev/scratchpad/child/note", "remove");
        var checkedSwap = false;
        var cleanup = new ScratchpadStartupCleanup(fixture.Profile, fixture.Guard)
        {
            MaxAge = TimeSpan.Zero,
            BeforeEntryDelete = entry =>
            {
                Assert.Throws<IOException>(() => Directory.Move(fixture.Root, fixture.Root + "-swapped"));
                Assert.Throws<IOException>(() => Directory.Move(fixture.Scratchpad, fixture.Scratchpad + "-swapped"));
                if (File.Exists(entry)) Assert.Throws<IOException>(() => File.Move(entry, entry + "-swapped"));
                checkedSwap = true;
            },
        };
        await cleanup.RunAsync(true, true, CancellationToken.None);
        Assert.IsTrue(checkedSwap);
        Assert.AreEqual(ScratchpadCleanupState.Completed, cleanup.Snapshot.State);
    }

    [TestMethod]
    public async Task CancellationAndRepeatedWaitersJoinActualWorkerAndLateSubscribersReplayResult()
    {
        RequireCleanupPlatform();
        using var fixture = new Fixture(true);
        fixture.Write("dev/scratchpad/note", "keep");
        using var cancellation = new CancellationTokenSource();
        using var workerEntered = new ManualResetEventSlim();
        using var releaseWorker = new ManualResetEventSlim();
        var cleanup = new ScratchpadStartupCleanup(fixture.Profile, fixture.Guard)
        {
            MaxAge = TimeSpan.Zero,
            WorkerStarted = _ => { workerEntered.Set(); Assert.IsTrue(releaseWorker.Wait(TestTime.Seconds(10))); },
        };
        var observed = new List<ScratchpadCleanupState>();
        using var observer = cleanup.Subscribe(snapshot => observed.Add(snapshot.State));
        var original = cleanup.RunAsync(true, true, cancellation.Token);
        try
        {
            Assert.IsTrue(workerEntered.Wait(TestTime.Seconds(10)));
            cancellation.Cancel();
            var second = cleanup.RunAsync(false, false, new CancellationToken(true));
            Assert.AreSame(original, second);
            Assert.IsFalse(original.IsCompleted, "Cancellation must not open host admission while the worker is running.");
            Assert.AreEqual(ScratchpadCleanupState.Running, cleanup.Snapshot.State);
        }
        finally { releaseWorker.Set(); await original; }
        Assert.AreEqual(ScratchpadCleanupState.Canceled, cleanup.Snapshot.State);
        ScratchpadCleanupSnapshot? replayed = null;
        using var late = cleanup.Subscribe(snapshot => replayed = snapshot);
        Assert.AreEqual(cleanup.Snapshot, replayed);
        CollectionAssert.AreEqual(new[] { ScratchpadCleanupState.NotStarted, ScratchpadCleanupState.Running, ScratchpadCleanupState.Canceled }, observed);
        Assert.AreEqual("keep", File.ReadAllText(Path.Combine(fixture.Scratchpad, "note")));
    }

    [TestMethod]
    public async Task LinkedStateAncestorIsRejectedEvenWithAnAcquiredGuard()
    {
        RequireWindows();
        using var fixture = new Fixture(false);
        fixture.Write("target/scratchpad/sentinel", "protected");
        var alias = Path.Combine(fixture.Root, "alias");
        CreateDirectoryLink(alias, Path.Combine(fixture.Root, "target"));
        try
        {
            var profile = CodeAltaInstanceProfile.Create(alias, false);
            using var guard = CodeAltaSingleInstanceGuard.Acquire(profile.LockFilePath);
            var cleanup = new ScratchpadStartupCleanup(profile, guard);
            await cleanup.RunAsync(true, true, CancellationToken.None);
            Assert.AreEqual(ScratchpadCleanupState.Skipped, cleanup.Snapshot.State);
            Assert.AreEqual("protected", File.ReadAllText(Path.Combine(fixture.Root, "target", "scratchpad", "sentinel")));
        }
        finally { Directory.Delete(alias); }
    }

    [TestMethod]
    public async Task CycleAndLegacyLockDirectoryLinksCannotEscapeTheBoundary()
    {
        RequireCleanupPlatform();
        using var fixture = new Fixture(false);
        fixture.Write("scratchpad/sentinel", "keep");
        fixture.Write("outside/sentinel", "protected");
        var dev = Path.Combine(fixture.Root, "dev");
        CreateDirectoryLink(dev, Path.Combine(fixture.Root, "outside"));
        try
        {
            var cleanup = fixture.Cleanup();
            await cleanup.RunAsync(true, true, CancellationToken.None);
            Assert.AreEqual(ScratchpadCleanupState.Skipped, cleanup.Snapshot.State);
            Assert.IsFalse(File.Exists(Path.Combine(fixture.Root, "outside", "alta.lock")));
            Assert.AreEqual("keep", File.ReadAllText(Path.Combine(fixture.Scratchpad, "sentinel")));
        }
        finally { Directory.Delete(dev); }
        var cycle = Path.Combine(fixture.Scratchpad, "cycle");
        CreateDirectoryLink(cycle, fixture.Scratchpad);
        try
        {
            var cleanup = fixture.Cleanup();
            await cleanup.RunAsync(true, true, CancellationToken.None);
            Assert.AreEqual(ScratchpadCleanupState.Partial, cleanup.Snapshot.State);
            Assert.AreEqual(1L, cleanup.Snapshot.RemovedFiles);
            Assert.AreEqual(1L, cleanup.Snapshot.SkippedEntries);
        }
        finally { Directory.Delete(cycle); }
    }

    [TestMethod]
    public async Task CancellationDuringRemovalRetainsLegacyExclusionAndOwnershipUntilWorkerStops()
    {
        RequireCleanupPlatform();
        using var fixture = new Fixture(false);
        fixture.Write("scratchpad/note", "keep");
        using var cancellation = new CancellationTokenSource();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var cleanup = new ScratchpadStartupCleanup(fixture.Profile, fixture.Guard)
        {
            MaxAge = TimeSpan.Zero,
            BeforeEntryDelete = _ => { entered.Set(); Assert.IsTrue(release.Wait(TestTime.Seconds(10))); },
        };
        var worker = cleanup.RunAsync(true, true, cancellation.Token);
        try
        {
            Assert.IsTrue(entered.Wait(TestTime.Seconds(10)));
            cancellation.Cancel();
            // Even premature guard disposal cannot close the OS ownership handle or remove the lock while deletion runs.
            fixture.Guard.Dispose();
            Assert.IsFalse(worker.IsCompleted);
            Assert.Throws<CodeAltaAlreadyRunningException>(() => CodeAltaSingleInstanceGuard.Acquire(fixture.Profile.LockFilePath));
            if (OperatingSystem.IsWindows())
                Assert.Throws<UnauthorizedAccessException>(() => CodeAltaSingleInstanceGuard.Acquire(Path.Combine(fixture.Root, "dev", "alta.lock")));
            else
                Assert.Throws<CodeAltaAlreadyRunningException>(() => CodeAltaSingleInstanceGuard.Acquire(Path.Combine(fixture.Root, "dev", "alta.lock")));
            Assert.AreEqual(ScratchpadCleanupState.Running, cleanup.Snapshot.State);
        }
        finally { release.Set(); await worker; }
        Assert.AreEqual(ScratchpadCleanupState.Canceled, cleanup.Snapshot.State);
        Assert.AreEqual(0L, cleanup.Snapshot.RemovedFiles);
        Assert.AreEqual("keep", File.ReadAllText(Path.Combine(fixture.Scratchpad, "note")));
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Root, "dev", "alta.lock")));
        // The disposal deferred by the pass released the lock once the worker stopped.
        using var successor = CodeAltaSingleInstanceGuard.Acquire(fixture.Profile.LockFilePath);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task WritableDirectoryHandlesCannotCoexistWithPathPins(bool ancestor)
    {
        RequireWindows();
        using var fixture = new Fixture(true);
        fixture.Write("dev/scratchpad/note", "keep");
        var pinned = ancestor ? fixture.Root : fixture.Scratchpad;
        using (var writer = OpenWritableDirectory(pinned))
        {
            Assert.IsFalse(writer.IsInvalid);
            var blocked = fixture.Cleanup();
            await blocked.RunAsync(true, true, CancellationToken.None);
            Assert.AreEqual(ScratchpadCleanupState.Skipped, blocked.Snapshot.State);
            CollectionAssert.Contains(blocked.Snapshot.Reasons.ToArray(), "filesystem-or-ownership-unavailable");
            Assert.IsTrue(File.Exists(Path.Combine(fixture.Scratchpad, "note")));
        }
        var attemptedMutation = false;
        var cleanup = new ScratchpadStartupCleanup(fixture.Profile, fixture.Guard)
        {
            MaxAge = TimeSpan.Zero,
            BeforeEntryDelete = _ =>
            {
                // A reparse-data mutation needs a writable handle; it cannot be acquired after pinning.
                using var writer = OpenWritableDirectory(pinned);
                Assert.IsTrue(writer.IsInvalid);
                Assert.AreEqual(32, Marshal.GetLastWin32Error()); // ERROR_SHARING_VIOLATION
                attemptedMutation = true;
            },
        };
        await cleanup.RunAsync(true, true, CancellationToken.None);
        Assert.IsTrue(attemptedMutation);
        Assert.AreEqual(ScratchpadCleanupState.Completed, cleanup.Snapshot.State);
    }

    [TestMethod]
    public async Task AWritableHandleOfAnAncestor_ThatGoesSoon_DoesNotSkipThePass()
    {
        RequireWindows();
        using var fixture = new Fixture(true);
        fixture.Write("dev/scratchpad/note", "remove");
        // Another process that moves an entry into an ancestor (the temp directory of a test run) holds it open for
        // writing for that instant: the pass waits for it instead of giving up.
        var writer = OpenWritableDirectory(fixture.Root);
        Assert.IsFalse(writer.IsInvalid);
        var release = new Thread(() => { Thread.Sleep(100); writer.Dispose(); }) { IsBackground = true };
        try
        {
            var cleanup = new ScratchpadStartupCleanup(fixture.Profile, fixture.Guard) { MaxAge = TimeSpan.Zero, WorkerStarted = _ => release.Start() };
            await cleanup.RunAsync(true, true, CancellationToken.None);
            Assert.AreEqual(ScratchpadCleanupState.Completed, cleanup.Snapshot.State, string.Join(", ", cleanup.Snapshot.Reasons));
            Assert.AreEqual(1L, cleanup.Snapshot.RemovedFiles);
        }
        finally
        {
            if (release.IsAlive) release.Join();
            writer.Dispose();
        }
    }

    [TestMethod]
    public async Task PathPinsBlockMovesIntoSharedAncestorsButNotIsolatedFixtureDirectories()
    {
        RequireWindows();
        using var fixture = new Fixture(true);
        fixture.Write("dev/scratchpad/note", "remove");
        fixture.Write("other-fixture/profile/session.jsonl", "keep");
        var otherRoot = Path.Combine(fixture.Root, "other-fixture");
        var source = Path.Combine(otherRoot, "profile", "session.jsonl");
        var sharedDestination = Path.Combine(fixture.Root, "moved.jsonl");
        var isolatedDestination = Path.Combine(otherRoot, "moved.jsonl");
        var attemptedMove = false;
        var cleanup = new ScratchpadStartupCleanup(fixture.Profile, fixture.Guard)
        {
            MaxAge = TimeSpan.Zero,
            BeforeEntryDelete = _ =>
            {
                // MoveFileEx opens the destination directory for writing. A shared ancestor
                // (such as the temp directory) is pinned by cleanup even for a different fixture.
                var error = Assert.Throws<IOException>(() => File.Move(source, sharedDestination));
                Assert.AreEqual(32, error.HResult & 0xffff); // ERROR_SHARING_VIOLATION
                File.Move(source, isolatedDestination);
                attemptedMove = true;
            },
        };
        await cleanup.RunAsync(true, true, CancellationToken.None);
        Assert.IsTrue(attemptedMove, string.Join(", ", cleanup.Snapshot.Reasons));
        Assert.AreEqual(ScratchpadCleanupState.Completed, cleanup.Snapshot.State);
        Assert.AreEqual("keep", File.ReadAllText(isolatedDestination));
        Assert.IsFalse(File.Exists(sharedDestination));
    }

    [TestMethod]
    public async Task UnixLinkSwapAfterEnumerationCannotRedirectRemoval()
    {
        RequireUnix();
        using var fixture = new Fixture(true);
        fixture.Write("outside/sentinel", "protected");
        var child = Path.Combine(fixture.Scratchpad, "child");
        Directory.CreateDirectory(child);
        var swapped = false;
        var cleanup = new ScratchpadStartupCleanup(fixture.Profile, fixture.Guard)
        {
            MaxAge = TimeSpan.Zero,
            BeforeEntryOpen = entry =>
            {
                if (Path.GetFileName(entry) != "child" || swapped) return;
                // Exchange the enumerated directory for a link to the outside before the worker looks at it.
                Directory.Delete(child);
                Directory.CreateSymbolicLink(child, Path.Combine(fixture.Root, "outside"));
                swapped = true;
            },
        };
        await cleanup.RunAsync(true, true, CancellationToken.None);
        Assert.IsTrue(swapped);
        Assert.AreEqual(ScratchpadCleanupState.Partial, cleanup.Snapshot.State);
        Assert.AreEqual(0L, cleanup.Snapshot.RemovedFiles);
        CollectionAssert.Contains(cleanup.Snapshot.Reasons.ToArray(), "linked-entry");
        Assert.AreEqual("protected", File.ReadAllText(Path.Combine(fixture.Root, "outside", "sentinel")));
        Assert.IsNotNull(new DirectoryInfo(child).LinkTarget);
    }

    [TestMethod]
    public async Task UnixLinkedStateAncestorCleansOnlyTheScratchpadItLeadsTo()
    {
        RequireUnix();
        using var fixture = new Fixture(false);
        fixture.Write("target/scratchpad/note", "remove");
        fixture.Write("target/config.toml", "protected");
        fixture.Write("scratchpad/sentinel", "protected");
        var alias = Path.Combine(fixture.Root, "alias");
        Directory.CreateSymbolicLink(alias, Path.Combine(fixture.Root, "target"));
        // On Unix the identity of the state root is proven by the inode of the lock held, not by its
        // pathname: an ancestor link (as /var on macOS) is followed once, then never again.
        var profile = CodeAltaInstanceProfile.Create(alias, false);
        using var guard = CodeAltaSingleInstanceGuard.Acquire(profile.LockFilePath);
        var cleanup = new ScratchpadStartupCleanup(profile, guard) { MaxAge = TimeSpan.Zero };
        await cleanup.RunAsync(true, true, CancellationToken.None);
        Assert.AreEqual(ScratchpadCleanupState.Completed, cleanup.Snapshot.State, string.Join(",", cleanup.Snapshot.Reasons));
        Assert.AreEqual(1L, cleanup.Snapshot.RemovedFiles);
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Root, "target", "scratchpad", "note")));
        Assert.AreEqual("protected", File.ReadAllText(Path.Combine(fixture.Root, "target", "config.toml")));
        Assert.AreEqual("protected", File.ReadAllText(Path.Combine(fixture.Scratchpad, "sentinel")));
        Assert.IsNotNull(new DirectoryInfo(alias).LinkTarget);
    }

    [TestMethod]
    public async Task UnixFileReplacedBeforeItsRemovalIsKept()
    {
        RequireUnix();
        using var fixture = new Fixture(true);
        fixture.Write("dev/scratchpad/note", "old");
        fixture.Write("dev/replacement", "new");
        FileStream? previous = null;
        var cleanup = new ScratchpadStartupCleanup(fixture.Profile, fixture.Guard)
        {
            MaxAge = TimeSpan.Zero,
            BeforeEntryDelete = entry =>
            {
                // Keeping the examined file open keeps its inode from being reused by the replacement.
                previous = File.OpenRead(entry);
                File.Move(Path.Combine(fixture.Root, "dev", "replacement"), entry, overwrite: true);
            },
        };
        try
        {
            await cleanup.RunAsync(true, true, CancellationToken.None);
        }
        finally { previous?.Dispose(); }
        Assert.AreEqual(ScratchpadCleanupState.Partial, cleanup.Snapshot.State);
        Assert.AreEqual(0L, cleanup.Snapshot.RemovedFiles);
        CollectionAssert.Contains(cleanup.Snapshot.Reasons.ToArray(), "entry-locked-denied-or-changed");
        Assert.AreEqual("new", File.ReadAllText(Path.Combine(fixture.Scratchpad, "note")));
    }

    [TestMethod]
    public async Task UnixReplacedInstanceLockSkipsThePass()
    {
        RequireUnix();
        using var fixture = new Fixture(false);
        fixture.Write("scratchpad/note", "keep");
        // The guard still holds the original file open; the name now leads to another one.
        File.Delete(fixture.Profile.LockFilePath);
        File.WriteAllText(fixture.Profile.LockFilePath, "1");
        var cleanup = fixture.Cleanup();
        await cleanup.RunAsync(true, true, CancellationToken.None);
        Assert.AreEqual(ScratchpadCleanupState.Skipped, cleanup.Snapshot.State);
        CollectionAssert.AreEqual(new[] { "instance-ownership-path-changed" }, cleanup.Snapshot.Reasons.ToArray());
        Assert.AreEqual("keep", File.ReadAllText(Path.Combine(fixture.Scratchpad, "note")));
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Root, "dev", "alta.lock")));
    }

    [TestMethod]
    public async Task UnixFifoIsRemovedWithoutOpeningIt()
    {
        RequireUnix();
        using var fixture = new Fixture(true);
        Directory.CreateDirectory(fixture.Scratchpad);
        var fifo = Path.Combine(fixture.Scratchpad, "pipe");
        Assert.AreEqual(0, MakeFifo(fifo, 0x180 /* 0600 */), $"mkfifo failed: {Marshal.GetLastPInvokeError()}");
        var cleanup = fixture.Cleanup();
        // Opening a FIFO without a writer blocks: the pass must not open it.
        await cleanup.RunAsync(true, true, CancellationToken.None).WithinAsync(TestTime.Seconds(10));
        Assert.AreEqual(ScratchpadCleanupState.Completed, cleanup.Snapshot.State, string.Join(",", cleanup.Snapshot.Reasons));
        Assert.AreEqual(1L, cleanup.Snapshot.RemovedFiles);
        Assert.AreEqual(0, Directory.GetFileSystemEntries(fixture.Scratchpad).Length);
    }

    [TestMethod]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public async Task UnixReadOnlyFileGoesButEntriesOfAReadOnlyDirectoryStay()
    {
        RequireUnix();
        using var fixture = new Fixture(true);
        fixture.Write("dev/scratchpad/readonly", "remove");
        fixture.Write("dev/scratchpad/sealed/note", "keep");
        // Unix removes a name by the permission of its directory, not of the file: a file of mode 0444
        // in the writable scratchpad goes, a file in a directory without write permission stays.
        File.SetUnixFileMode(Path.Combine(fixture.Scratchpad, "readonly"), UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        var sealedDirectory = Path.Combine(fixture.Scratchpad, "sealed");
        File.SetUnixFileMode(sealedDirectory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var cleanup = fixture.Cleanup();
            await cleanup.RunAsync(true, true, CancellationToken.None);
            Assert.AreEqual(ScratchpadCleanupState.Partial, cleanup.Snapshot.State);
            Assert.AreEqual(1L, cleanup.Snapshot.RemovedFiles);
            Assert.AreEqual(2L, cleanup.Snapshot.SkippedEntries, string.Join(",", cleanup.Snapshot.Reasons));
            CollectionAssert.AreEquivalent(new[] { "entry-locked-denied-or-changed", "directory-not-empty-or-unavailable" }, cleanup.Snapshot.Reasons.ToArray());
            Assert.IsFalse(File.Exists(Path.Combine(fixture.Scratchpad, "readonly")));
            Assert.AreEqual("keep", File.ReadAllText(Path.Combine(sealedDirectory, "note")));
        }
        finally { File.SetUnixFileMode(sealedDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    }

    [TestMethod]
    public async Task KeepsEntriesCreatedAfterTheCutoffEvenWithAnOldWriteTime()
    {
        RequireCleanupPlatform();
        using var fixture = new Fixture(false);
        // Entries written before the cutoff are old, those written after it recent: no time is set back
        // except the write time of the copy, so this holds on Linux, which sets no birth time.
        fixture.Write("scratchpad/old.md", "remove");
        fixture.Write("scratchpad/old-folder/note.md", "remove");
        fixture.Write("scratchpad/mixed/old.md", "remove");
        Directory.CreateDirectory(Path.Combine(fixture.Scratchpad, "old-empty"));
        Thread.Sleep(50);
        var cutoff = DateTimeOffset.UtcNow;
        Thread.Sleep(50);
        fixture.Write("scratchpad/recent.md", "keep");
        fixture.Write("scratchpad/mixed/recent.md", "keep");
        fixture.Write("scratchpad/copied.md", "keep");
        Directory.CreateDirectory(Path.Combine(fixture.Scratchpad, "recent-empty"));
        // A copy keeps the write time of its source, but it was created after the cutoff: it is recent.
        File.SetLastWriteTimeUtc(Path.Combine(fixture.Scratchpad, "copied.md"), new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var cleanup = new ScratchpadStartupCleanup(fixture.Profile, fixture.Guard)
        {
            MaxAge = StorageSettings.ScratchpadMaxAge,
            Time = new FixedTime(cutoff + StorageSettings.ScratchpadMaxAge),
        };
        await cleanup.RunAsync(true, true, CancellationToken.None);

        Assert.AreEqual(ScratchpadCleanupState.Completed, cleanup.Snapshot.State, string.Join(",", cleanup.Snapshot.Reasons));
        Assert.AreEqual(3L, cleanup.Snapshot.RemovedFiles);
        Assert.AreEqual(2L, cleanup.Snapshot.RemovedDirectories);
        CollectionAssert.AreEquivalent(
            new[] { "copied.md", "mixed", "mixed/recent.md", "recent-empty", "recent.md" },
            Directory.GetFileSystemEntries(fixture.Scratchpad, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(fixture.Scratchpad, path).Replace('\\', '/')).ToArray());
    }

    [TestMethod]
    public async Task UnixFileRenamedOntoTheNameAfterTheClaimSurvives()
    {
        RequireUnix();
        using var fixture = new Fixture(true);
        fixture.Write("dev/scratchpad/victim", "old");
        fixture.Write("dev/replacement", "new");
        var victim = Path.Combine(fixture.Scratchpad, "victim");
        var cleanup = new ScratchpadStartupCleanup(fixture.Profile, fixture.Guard)
        {
            MaxAge = TimeSpan.Zero,
            // After the last look at the name: what the claim moved is the examined file, the name is free.
            AfterEntryClaim = entry => File.Move(Path.Combine(fixture.Root, "dev", "replacement"), entry),
        };
        await cleanup.RunAsync(true, true, CancellationToken.None);
        Assert.AreEqual(ScratchpadCleanupState.Completed, cleanup.Snapshot.State, string.Join(",", cleanup.Snapshot.Reasons));
        Assert.AreEqual(1L, cleanup.Snapshot.RemovedFiles);
        Assert.AreEqual("new", File.ReadAllText(victim));
        CollectionAssert.AreEqual(new[] { "victim" }, Directory.GetFileSystemEntries(fixture.Scratchpad).Select(Path.GetFileName).ToArray());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UnixFileSwappedUnderTheClaimNameIsNeverRemoved(bool nameTakenAgain)
    {
        RequireUnix();
        using var fixture = new Fixture(true);
        fixture.Write("dev/scratchpad/victim", "old");
        fixture.Write("dev/replacement", "new");
        var victim = Path.Combine(fixture.Scratchpad, "victim");
        var cleanup = new ScratchpadStartupCleanup(fixture.Profile, fixture.Guard)
        {
            MaxAge = TimeSpan.Zero,
            AfterEntryClaim = _ =>
            {
                // A process that guessed the claim name puts a fresh file there, after the last look at the name.
                var claim = Directory.GetFiles(fixture.Scratchpad, ".alta-cleanup-*").Single();
                File.Move(Path.Combine(fixture.Root, "dev", "replacement"), claim, overwrite: true);
                if (nameTakenAgain) File.WriteAllText(victim, "blocker");
            },
        };
        await cleanup.RunAsync(true, true, CancellationToken.None);
        Assert.AreEqual(ScratchpadCleanupState.Partial, cleanup.Snapshot.State);
        Assert.AreEqual(0L, cleanup.Snapshot.RemovedFiles);
        CollectionAssert.Contains(cleanup.Snapshot.Reasons.ToArray(), "entry-locked-denied-or-changed");
        var claims = Directory.GetFiles(fixture.Scratchpad, ".alta-cleanup-*");
        if (nameTakenAgain)
        {
            // The name could not be given back without replacing the blocker: the fresh file stays under the claim name.
            Assert.AreEqual("blocker", File.ReadAllText(victim));
            Assert.AreEqual("new", File.ReadAllText(claims.Single()));
        }
        else
        {
            Assert.AreEqual("new", File.ReadAllText(victim));
            Assert.AreEqual(0, claims.Length);
        }
    }

    [TestMethod]
    public async Task UnixDirectoryMovedOutDuringThePassKeepsItsFiles()
    {
        RequireUnix();
        using var fixture = new Fixture(true);
        fixture.Write("dev/scratchpad/child/note", "keep");
        fixture.Write("dev/scratchpad/child/other", "keep");
        var child = Path.Combine(fixture.Scratchpad, "child");
        var outside = Path.Combine(fixture.Root, "outside");
        var moved = false;
        var cleanup = new ScratchpadStartupCleanup(fixture.Profile, fixture.Guard)
        {
            MaxAge = TimeSpan.Zero,
            BeforeEntryDelete = entry =>
            {
                if (moved || Path.GetDirectoryName(entry) != child) return;
                // The worker holds the directory open: its descriptor now leads outside the scratchpad.
                Directory.Move(child, outside);
                moved = true;
            },
        };
        await cleanup.RunAsync(true, true, CancellationToken.None);
        Assert.IsTrue(moved);
        Assert.AreEqual(ScratchpadCleanupState.Partial, cleanup.Snapshot.State);
        Assert.AreEqual(0L, cleanup.Snapshot.RemovedFiles);
        CollectionAssert.Contains(cleanup.Snapshot.Reasons.ToArray(), "entry-locked-denied-or-changed");
        Assert.AreEqual("keep", File.ReadAllText(Path.Combine(outside, "note")));
        Assert.AreEqual("keep", File.ReadAllText(Path.Combine(outside, "other")));
        Assert.AreEqual(0, Directory.GetFiles(outside, ".alta-cleanup-*").Length);
    }

    [TestMethod]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public async Task UnixLegacyLockThatCannotBeReleasedMakesThePassPartial()
    {
        RequireUnix();
        using var fixture = new Fixture(false);
        fixture.Write("scratchpad/note", "remove");
        var dev = Path.Combine(fixture.Root, "dev");
        var cleanup = new ScratchpadStartupCleanup(fixture.Profile, fixture.Guard)
        {
            MaxAge = TimeSpan.Zero,
            // Without write permission on dev, the reservation cannot be removed at the end of the pass.
            BeforeEntryDelete = _ => File.SetUnixFileMode(dev, UnixFileMode.UserRead | UnixFileMode.UserExecute),
        };
        try
        {
            await cleanup.RunAsync(true, true, CancellationToken.None);
            Assert.AreEqual(ScratchpadCleanupState.Partial, cleanup.Snapshot.State);
            Assert.AreEqual(1L, cleanup.Snapshot.RemovedFiles);
            CollectionAssert.AreEqual(new[] { "legacy-developer-exclusion-release-failed" }, cleanup.Snapshot.Reasons.ToArray());
            Assert.IsTrue(File.Exists(Path.Combine(dev, "alta.lock")));
        }
        finally { File.SetUnixFileMode(dev, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    }

    [TestMethod]
    public async Task UnixScratchpadRenamedDuringThePassKeepsItsFiles()
    {
        RequireUnix();
        using var fixture = new Fixture(true);
        fixture.Write("dev/scratchpad/note", "keep");
        var preserved = Path.Combine(fixture.Profile.StateRoot, "preserved");
        var cleanup = new ScratchpadStartupCleanup(fixture.Profile, fixture.Guard)
        {
            MaxAge = TimeSpan.Zero,
            // The root stays in the state root, so the ".." chain still holds: only its name tells it moved.
            BeforeEntryDelete = _ => Directory.Move(fixture.Scratchpad, preserved),
        };
        await cleanup.RunAsync(true, true, CancellationToken.None);
        Assert.AreEqual(ScratchpadCleanupState.Partial, cleanup.Snapshot.State);
        Assert.AreEqual(0L, cleanup.Snapshot.RemovedFiles);
        CollectionAssert.Contains(cleanup.Snapshot.Reasons.ToArray(), "entry-locked-denied-or-changed");
        Assert.AreEqual("keep", File.ReadAllText(Path.Combine(preserved, "note")));
        Assert.AreEqual(0, Directory.GetFiles(preserved, ".alta-cleanup-*").Length);
    }

    [TestMethod]
    public async Task UnixCancellationAfterTheClaimGivesTheNameBack()
    {
        RequireUnix();
        using var fixture = new Fixture(true);
        fixture.Write("dev/scratchpad/note", "keep");
        using var cancellation = new CancellationTokenSource();
        var cleanup = new ScratchpadStartupCleanup(fixture.Profile, fixture.Guard)
        {
            MaxAge = TimeSpan.Zero,
            AfterEntryClaim = _ => cancellation.Cancel(),
        };
        await cleanup.RunAsync(true, true, cancellation.Token);
        Assert.AreEqual(ScratchpadCleanupState.Canceled, cleanup.Snapshot.State);
        Assert.AreEqual(0L, cleanup.Snapshot.RemovedFiles);
        Assert.AreEqual("keep", File.ReadAllText(Path.Combine(fixture.Scratchpad, "note")));
        Assert.AreEqual(0, Directory.GetFiles(fixture.Scratchpad, ".alta-cleanup-*").Length);
    }

    [TestMethod]
    [SupportedOSPlatform("linux")]
    public async Task LinuxReadsTheMountIdAndOrdinaryDirectoriesShareIt()
    {
        RequireUnix();
        if (!OperatingSystem.IsLinux()) Assert.Inconclusive("The mount id is read on Linux only.");
        using var fixture = new Fixture(true);
        fixture.Write("dev/scratchpad/child/note", "remove");
        var rootMount = UnixScratchpadRemoval.MountIdOf(fixture.Scratchpad);
        if (rootMount is null) Assert.Inconclusive("This kernel returns no mount id (before 5.8): the device check applies.");
        Assert.AreNotEqual(0UL, rootMount.Value);
        Assert.AreEqual(rootMount, UnixScratchpadRemoval.MountIdOf(Path.Combine(fixture.Scratchpad, "child")));
        Assert.AreEqual(rootMount, UnixScratchpadRemoval.MountIdOf(fixture.Profile.StateRoot));
        var cleanup = fixture.Cleanup();
        await cleanup.RunAsync(true, true, CancellationToken.None);
        Assert.AreEqual(ScratchpadCleanupState.Completed, cleanup.Snapshot.State, string.Join(",", cleanup.Snapshot.Reasons));
        Assert.AreEqual(1L, cleanup.Snapshot.RemovedDirectories);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [SupportedOSPlatform("linux")]
    public async Task LinuxBindMountsOfTheSameFileSystemAreNeverEntered(bool scratchpadMounted)
    {
        RequireUnix();
        if (!OperatingSystem.IsLinux() || !Environment.IsPrivilegedProcess) Assert.Inconclusive("A bind mount needs root on Linux.");
        using var fixture = new Fixture(true);
        fixture.Write("outside/sentinel", "protected");
        fixture.Write("dev/scratchpad/note", "remove");
        if (UnixScratchpadRemoval.MountIdOf(fixture.Scratchpad) is null)
            Assert.Inconclusive("This kernel returns no mount id (before 5.8): a bind mount of the same file system is not told.");
        var outside = Path.Combine(fixture.Root, "outside");
        var target = scratchpadMounted ? fixture.Scratchpad : Path.Combine(fixture.Scratchpad, "child");
        if (scratchpadMounted)
        {
            // The pass looks at the root only after it opened it: mount over it with its own content.
            File.Copy(Path.Combine(fixture.Scratchpad, "note"), Path.Combine(outside, "note"));
        }
        else Directory.CreateDirectory(target);
        // Root in a container without CAP_SYS_ADMIN may not mount: the fixture is then unavailable, not failing.
        if (RunTool("mount", "--bind", outside, target) is { } refused) Assert.Inconclusive("A bind mount is not permitted here: " + refused);
        try
        {
            var cleanup = fixture.Cleanup();
            await cleanup.RunAsync(true, true, CancellationToken.None);
            CollectionAssert.Contains(cleanup.Snapshot.Reasons.ToArray(), scratchpadMounted ? "mount-point-root" : "mount-point-entry");
            Assert.AreEqual(scratchpadMounted ? ScratchpadCleanupState.Skipped : ScratchpadCleanupState.Partial, cleanup.Snapshot.State);
            Assert.AreEqual("protected", File.ReadAllText(Path.Combine(outside, "sentinel")));
            if (scratchpadMounted) Assert.IsTrue(File.Exists(Path.Combine(outside, "note")));
        }
        finally { Assert.IsNull(RunTool("umount", target), "umount failed."); }
    }

    // Runs a tool to its end; returns null when it succeeded, else what it wrote on its error output.
    private static string? RunTool(string tool, params string[] arguments)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(tool, arguments) { RedirectStandardError = true })!;
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TestTime.Seconds(10)))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            Assert.Fail($"{tool} did not end.");
        }
        return process.ExitCode == 0 ? null : $"{tool} exited with {process.ExitCode}: {error.GetAwaiter().GetResult().Trim()}";
    }

    [TestMethod]
    public void LinuxRunsTheCleanup()
    {
        if (!OperatingSystem.IsLinux()) Assert.Inconclusive("Linux qualification.");
        if (RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
            Assert.Inconclusive("Only Linux x64 and arm64 are qualified.");
        Assert.IsTrue(OperatingSystem.IsLinux() && UnixScratchpadRemoval.IsSupported,
            "Linux x64 and arm64 with statx and renameat2 (glibc 2.28, musl 1.2.5 or later) run the cleanup.");
    }

    [TestMethod]
    public async Task MacOSRunsTheCleanup()
    {
        if (!OperatingSystem.IsMacOS()) Assert.Inconclusive("macOS qualification.");
        Assert.IsTrue(OperatingSystem.IsMacOS() && UnixScratchpadRemoval.IsSupported);
        using var fixture = new Fixture(false);
        fixture.Write("scratchpad/note", "remove");
        var cleanup = fixture.Cleanup();
        await cleanup.RunAsync(true, true, CancellationToken.None);
        CollectionAssert.DoesNotContain(cleanup.Snapshot.Reasons.ToArray(), "platform-path-safety-unavailable");
        Assert.AreEqual(ScratchpadCleanupState.Completed, cleanup.Snapshot.State);
        Assert.AreEqual(1L, cleanup.Snapshot.RemovedFiles);
    }

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MakeFifo(string path, int mode);

    private static SafeFileHandle OpenWritableDirectory(string path)
        => CreateFile(path, 0x40000000 /* GENERIC_WRITE */, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);

    private static void CreateJunction(string path, string target)
    {
        // A real NTFS mount-point reparse buffer; unlike symbolic links this needs no developer-mode privilege.
        Directory.CreateDirectory(path);
        using var handle = OpenWritableDirectory(path);
        Assert.IsFalse(handle.IsInvalid);
        var substitute = System.Text.Encoding.Unicode.GetBytes(@"\??\" + target);
        var print = System.Text.Encoding.Unicode.GetBytes(target);
        var buffer = new byte[16 + substitute.Length + 2 + print.Length + 2];
        BitConverter.TryWriteBytes(buffer.AsSpan(0), 0xA0000003u); // IO_REPARSE_TAG_MOUNT_POINT
        BitConverter.TryWriteBytes(buffer.AsSpan(4), checked((ushort)(buffer.Length - 8)));
        BitConverter.TryWriteBytes(buffer.AsSpan(10), checked((ushort)substitute.Length));
        BitConverter.TryWriteBytes(buffer.AsSpan(12), checked((ushort)(substitute.Length + 2)));
        BitConverter.TryWriteBytes(buffer.AsSpan(14), checked((ushort)print.Length));
        substitute.CopyTo(buffer, 16);
        print.CopyTo(buffer, 16 + substitute.Length + 2);
        Assert.IsTrue(DeviceIoControl(handle, 0x000900A4 /* FSCTL_SET_REPARSE_POINT */, buffer, buffer.Length,
            IntPtr.Zero, 0, out _, IntPtr.Zero), $"Junction fixture failed: {Marshal.GetLastWin32Error()}");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint control, byte[] input, int inputSize,
        IntPtr output, int outputSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Windows handle-safety qualification.");
    }

    private static void RequireCleanupPlatform()
    {
        if (OperatingSystem.IsWindows()) return;
        RequireUnix();
    }

    private static void RequireUnix()
    {
        if (!(OperatingSystem.IsMacOS() || OperatingSystem.IsLinux()) || !UnixScratchpadRemoval.IsSupported)
            Assert.Inconclusive("No qualified path-safe removal on this platform; the pass is skipped there.");
    }

    private static void CreateDirectoryLink(string link, string target)
    {
        try { Directory.CreateSymbolicLink(link, target); }
        catch (UnauthorizedAccessException) { Assert.Inconclusive("The test process cannot create symbolic links."); }
        catch (IOException error) { Assert.Inconclusive("Symbolic-link fixture unavailable: " + error.Message); }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class Fixture : IDisposable
    {
        internal Fixture(bool developer)
        {
            Root = Path.Combine(Path.GetTempPath(), "CodeAlta-scratchpad-tests-" + Guid.NewGuid().ToString("N"));
            Profile = CodeAltaInstanceProfile.Create(Root, developer);
            Guard = CodeAltaSingleInstanceGuard.Acquire(Profile.LockFilePath);
        }
        internal string Root { get; }
        internal CodeAltaInstanceProfile Profile { get; }
        internal CodeAltaSingleInstanceGuard Guard { get; }
        internal string Scratchpad => Path.Combine(Profile.StateRoot, "scratchpad");
        // Entries the tests write are new: they all count as old unless a test keeps the default age.
        internal ScratchpadStartupCleanup Cleanup() => new(Profile, Guard) { MaxAge = TimeSpan.Zero };
        internal void Write(string path, string content)
        {
            var full = Path.Combine(Root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
        public void Dispose() { Guard.Dispose(); Directory.Delete(Root, recursive: true); }
    }
}
