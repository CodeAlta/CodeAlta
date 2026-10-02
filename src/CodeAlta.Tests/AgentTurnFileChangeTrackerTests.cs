using CodeAlta.Agent.Runtime;

namespace CodeAlta.Tests;

[TestClass]
public sealed class AgentTurnFileChangeTrackerTests
{
    private const int SnapshotByteLimit = 1024 * 1024;
    private const int DiffCharacterLimit = 1024 * 1024;

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CreateUnifiedDiff_OmitsOversizedFileContent(bool delete)
    {
        using var temp = TestTempDirectory.Create();
        var path = Path.Combine(temp.Path, "large.txt");
        await File.WriteAllTextAsync(path, new string('x', SnapshotByteLimit + 1));
        var tracker = new AgentTurnFileChangeTracker(temp.Path);
        await tracker.CaptureBeforeAsync([path], CancellationToken.None);
        if (delete)
        {
            File.Delete(path);
        }
        else
        {
            await File.WriteAllTextAsync(path, new string('y', SnapshotByteLimit + 1));
        }

        await tracker.CaptureAfterAsync([path], CancellationToken.None);
        var diff = tracker.CreateUnifiedDiff();

        Assert.IsNotNull(diff);
        Assert.IsTrue(diff.Length <= DiffCharacterLimit);
        StringAssert.Contains(diff, "diff --git a/large.txt b/large.txt");
        StringAssert.Contains(diff, "File content diff omitted");
        Assert.IsFalse(diff.Contains("@@", StringComparison.Ordinal));
        if (delete)
        {
            StringAssert.Contains(diff, "deleted file mode 100644");
        }
    }

    [TestMethod]
    public async Task CreateUnifiedDiff_OmitsFilesExceedingTotalSnapshotBudget()
    {
        using var temp = TestTempDirectory.Create();
        var paths = Enumerable.Range(0, 9).Select(index => Path.Combine(temp.Path, $"{index}.txt")).ToArray();
        var text = new string('x', SnapshotByteLimit);
        foreach (var path in paths)
        {
            await File.WriteAllTextAsync(path, text);
        }

        var tracker = new AgentTurnFileChangeTracker(temp.Path);
        await tracker.CaptureBeforeAsync(paths, CancellationToken.None);
        File.Delete(paths[^1]);
        await tracker.CaptureAfterAsync([paths[^1]], CancellationToken.None);

        var diff = tracker.CreateUnifiedDiff();
        Assert.IsNotNull(diff);
        Assert.IsTrue(diff.Length < 1024);
        StringAssert.Contains(diff, "diff --git a/8.txt b/8.txt");
        StringAssert.Contains(diff, "File content diff omitted");
    }

    [TestMethod]
    public async Task CreateUnifiedDiff_BoundsAggregateDirectoryDeletionDiff()
    {
        using var temp = TestTempDirectory.Create();
        var directory = Path.Combine(temp.Path, "deleted");
        Directory.CreateDirectory(directory);
        for (var index = 0; index < 6; index++)
        {
            await File.WriteAllTextAsync(Path.Combine(directory, $"{index}.txt"), new string('x', SnapshotByteLimit / 4));
        }

        var tracker = new AgentTurnFileChangeTracker(temp.Path);
        await tracker.CaptureBeforeAsync([directory], CancellationToken.None);
        Directory.Delete(directory, recursive: true);
        await tracker.CaptureAfterAsync([directory], CancellationToken.None);

        var diff = tracker.CreateUnifiedDiff();
        Assert.IsNotNull(diff);
        Assert.IsTrue(diff.Length <= DiffCharacterLimit);
        StringAssert.Contains(diff, "diff --git a/deleted/0.txt b/deleted/0.txt");
        StringAssert.Contains(diff, "--- a/deleted/0.txt");
        StringAssert.Contains(diff, "+++ /dev/null");
        StringAssert.Contains(diff, "Remaining file diffs omitted");
    }

    [TestMethod]
    public async Task CreateUnifiedDiff_BoundsOneFileDiffWithLongLines()
    {
        using var temp = TestTempDirectory.Create();
        var path = Path.Combine(temp.Path, "long-lines.txt");
        await File.WriteAllTextAsync(path, new string('x', SnapshotByteLimit / 2));
        var tracker = new AgentTurnFileChangeTracker(temp.Path);
        await tracker.CaptureBeforeAsync([path], CancellationToken.None);
        await File.WriteAllTextAsync(path, new string('y', SnapshotByteLimit / 2));
        await tracker.CaptureAfterAsync([path], CancellationToken.None);

        var diff = tracker.CreateUnifiedDiff();
        Assert.IsNotNull(diff);
        Assert.IsTrue(diff.Length <= DiffCharacterLimit);
        StringAssert.Contains(diff, "Remaining file diffs omitted");
    }

    [TestMethod]
    public async Task CreateUnifiedDiff_RepeatedCapturesDoNotConsumeAdditionalSnapshotBudget()
    {
        using var temp = TestTempDirectory.Create();
        var path = Path.Combine(temp.Path, "repeated.txt");
        var context = new string('x', SnapshotByteLimit / 2) + "\n";
        await File.WriteAllTextAsync(path, context + "before\n");
        var tracker = new AgentTurnFileChangeTracker(temp.Path);
        for (var index = 0; index < 20; index++)
        {
            await tracker.CaptureBeforeAsync([path], CancellationToken.None);
        }

        await File.WriteAllTextAsync(path, context + "after\n");
        for (var index = 0; index < 20; index++)
        {
            await tracker.CaptureAfterAsync([path], CancellationToken.None);
        }

        var diff = tracker.CreateUnifiedDiff();
        Assert.IsNotNull(diff);
        Assert.IsTrue(diff.Length <= DiffCharacterLimit);
        Assert.IsFalse(diff.Contains("omitted", StringComparison.Ordinal));
        StringAssert.Contains(diff, "-before");
        StringAssert.Contains(diff, "+after");
    }

    [TestMethod]
    public async Task CreateUnifiedDiff_DirectoryDeletionReleasesAfterSnapshotBudget()
    {
        using var temp = TestTempDirectory.Create();
        var directory = Path.Combine(temp.Path, "z-deleted");
        Directory.CreateDirectory(directory);
        var text = new string('x', SnapshotByteLimit);
        for (var index = 0; index < 4; index++)
        {
            await File.WriteAllTextAsync(Path.Combine(directory, $"{index}.txt"), text);
        }

        var tracker = new AgentTurnFileChangeTracker(temp.Path);
        await tracker.CaptureBeforeAsync([directory], CancellationToken.None);
        await tracker.CaptureAfterAsync([directory], CancellationToken.None);
        Directory.Delete(directory, recursive: true);
        await tracker.CaptureAfterAsync([directory], CancellationToken.None);
        var path = Path.Combine(temp.Path, "a-created.txt");
        await File.WriteAllTextAsync(path, "created\n");
        await tracker.CaptureAfterAsync([path], CancellationToken.None);

        var diff = tracker.CreateUnifiedDiff();
        Assert.IsNotNull(diff);
        Assert.IsTrue(diff.Length <= DiffCharacterLimit);
        StringAssert.Contains(diff, "diff --git a/a-created.txt b/a-created.txt");
        StringAssert.Contains(diff, "+created");
    }
}
