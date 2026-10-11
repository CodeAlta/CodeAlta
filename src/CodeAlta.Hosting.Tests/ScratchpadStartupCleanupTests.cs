using System.Runtime.InteropServices;
using CodeAlta.Catalog.Storage;
using Microsoft.Win32.SafeHandles;

namespace CodeAlta.Hosting.Tests;

[TestClass]
public sealed class ScratchpadStartupCleanupTests
{
    [TestMethod]
    public async Task RunningSnapshotAdvancesBeforeCleanupCompletes()
    {
        RequireWindows();
        using var fixture = new Fixture(false);
        fixture.Write("scratchpad/one", "remove");
        fixture.Write("scratchpad/two", "remove");
        using var release = new ManualResetEventSlim();
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var opened = 0;
        var cleanup = new ScratchpadStartupCleanup(fixture.Profile, fixture.Guard)
        {
            BeforeEntryOpen = _ =>
            {
                if (++opened != 2) return;
                paused.SetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test did not release cleanup.");
            },
        };
        var worker = cleanup.RunAsync(true, true, CancellationToken.None);
        try
        {
            await paused.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsFalse(worker.IsCompleted);
            Assert.AreEqual(ScratchpadCleanupState.Running, cleanup.Snapshot.State);
            Assert.AreEqual(1L, cleanup.Snapshot.RemovedFiles, "A long cleanup must publish progress before its final result.");
            Assert.AreEqual(1L, cleanup.Snapshot.ProcessedFiles);
            ScratchpadCleanupSnapshot? replay = null;
            using var subscription = cleanup.Subscribe(snapshot => replay = snapshot);
            Assert.AreEqual(cleanup.Snapshot, replay);
        }
        finally { release.Set(); await worker; }
        Assert.AreEqual(2L, cleanup.Snapshot.RemovedFiles);
        Assert.AreEqual(2L, cleanup.Snapshot.ProcessedFiles);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RemovesOnlyOwnedContentsAndPreservesRootAndProtectedSentinels(bool developer)
    {
        RequireWindows();
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
        Assert.AreEqual(40L, cleanup.Snapshot.RemovedDirectories);
        Assert.AreEqual(2L, cleanup.Snapshot.ProcessedFiles);
        Assert.AreEqual(40L, cleanup.Snapshot.ProcessedDirectories);
        Assert.IsTrue(Directory.Exists(scratchpad));
        Assert.AreEqual(0, Directory.GetFileSystemEntries(scratchpad).Length);
        foreach (var path in protectedPaths) Assert.AreEqual("protected", File.ReadAllText(Path.Combine(fixture.Root, path)));
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
        RequireWindows();
        using var fixture = new Fixture(false);
        fixture.Write("scratchpad/one", "remove or retain");
        fixture.Write("scratchpad/two", "retain");
        var attempts = 0;
        var cleanup = new ScratchpadStartupCleanup(fixture.Profile, fixture.Guard)
        {
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
        RequireWindows();
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
        RequireWindows();
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
        RequireWindows();
        using var fixture = new Fixture(false);
        var developer = CodeAltaInstanceProfile.Create(fixture.Root, true);
        using var peer = CodeAltaSingleInstanceGuard.Acquire(developer.LockFilePath);
        fixture.Write("scratchpad/normal.md", "keep");
        fixture.Write("dev/scratchpad/dev.md", "remove");
        var normal = fixture.Cleanup();
        var isolated = new ScratchpadStartupCleanup(developer, peer);
        await Task.WhenAll(normal.RunAsync(true, true, CancellationToken.None), isolated.RunAsync(true, true, CancellationToken.None));
        Assert.AreEqual(ScratchpadCleanupState.Skipped, normal.Snapshot.State);
        Assert.AreEqual(ScratchpadCleanupState.Completed, isolated.Snapshot.State);
        Assert.IsTrue(File.Exists(Path.Combine(fixture.Scratchpad, "normal.md")));
        Assert.IsFalse(File.Exists(Path.Combine(developer.StateRoot, "scratchpad", "dev.md")));
    }

    [TestMethod]
    public async Task LegacyAdmissionAndDelayedPathDeleteCannotBreakTemporaryGate()
    {
        RequireWindows();
        using var fixture = new Fixture(false);
        fixture.Write("scratchpad/note.md", "remove");
        var developerLock = Path.Combine(fixture.Root, "dev", "alta.lock");
        var checkedGate = false;
        var cleanup = new ScratchpadStartupCleanup(fixture.Profile, fixture.Guard)
        {
            BeforeEntryDelete = _ =>
            {
                // Delete-pending names fail before even the legacy PID-reader fallback.
                Assert.Throws<UnauthorizedAccessException>(() => CodeAltaSingleInstanceGuard.Acquire(developerLock));
                Assert.Throws<UnauthorizedAccessException>(() => File.Delete(developerLock));
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
        RequireWindows();
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
            Assert.IsTrue((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
        }
        finally { Directory.Delete(link); }
    }

    [TestMethod]
    public async Task LinkedScratchpadRootIsSkipped()
    {
        RequireWindows();
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
        RequireWindows();
        using var fixture = new Fixture(true);
        fixture.Write("dev/scratchpad/note", "keep");
        using var cancellation = new CancellationTokenSource();
        using var workerEntered = new ManualResetEventSlim();
        using var releaseWorker = new ManualResetEventSlim();
        var cleanup = new ScratchpadStartupCleanup(fixture.Profile, fixture.Guard)
        {
            WorkerStarted = _ => { workerEntered.Set(); Assert.IsTrue(releaseWorker.Wait(TimeSpan.FromSeconds(10))); },
        };
        var observed = new List<ScratchpadCleanupState>();
        using var observer = cleanup.Subscribe(snapshot => observed.Add(snapshot.State));
        var original = cleanup.RunAsync(true, true, cancellation.Token);
        try
        {
            Assert.IsTrue(workerEntered.Wait(TimeSpan.FromSeconds(10)));
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
        RequireWindows();
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
        RequireWindows();
        using var fixture = new Fixture(false);
        fixture.Write("scratchpad/note", "keep");
        using var cancellation = new CancellationTokenSource();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var cleanup = new ScratchpadStartupCleanup(fixture.Profile, fixture.Guard)
        {
            BeforeEntryDelete = _ => { entered.Set(); Assert.IsTrue(release.Wait(TimeSpan.FromSeconds(10))); },
        };
        var worker = cleanup.RunAsync(true, true, cancellation.Token);
        try
        {
            Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(10)));
            cancellation.Cancel();
            // Even premature guard disposal cannot close the OS ownership handle while deletion runs.
            fixture.Guard.Dispose();
            Assert.IsFalse(worker.IsCompleted);
            Assert.Throws<CodeAltaAlreadyRunningException>(() => CodeAltaSingleInstanceGuard.Acquire(fixture.Profile.LockFilePath));
            Assert.Throws<UnauthorizedAccessException>(() => CodeAltaSingleInstanceGuard.Acquire(Path.Combine(fixture.Root, "dev", "alta.lock")));
            Assert.AreEqual(ScratchpadCleanupState.Running, cleanup.Snapshot.State);
        }
        finally { release.Set(); await worker; }
        Assert.AreEqual(ScratchpadCleanupState.Canceled, cleanup.Snapshot.State);
        Assert.AreEqual(0L, cleanup.Snapshot.RemovedFiles);
        Assert.AreEqual("keep", File.ReadAllText(Path.Combine(fixture.Scratchpad, "note")));
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Root, "dev", "alta.lock")));
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
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Windows handle-safety qualification; other platforms fail closed.");
    }

    private static void CreateDirectoryLink(string link, string target)
    {
        try { Directory.CreateSymbolicLink(link, target); }
        catch (UnauthorizedAccessException) { Assert.Inconclusive("The test process cannot create symbolic links."); }
        catch (IOException error) { Assert.Inconclusive("Symbolic-link fixture unavailable: " + error.Message); }
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
        internal ScratchpadStartupCleanup Cleanup() => new(Profile, Guard);
        internal void Write(string path, string content)
        {
            var full = Path.Combine(Root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
        public void Dispose() { Guard.Dispose(); Directory.Delete(Root, recursive: true); }
    }
}
