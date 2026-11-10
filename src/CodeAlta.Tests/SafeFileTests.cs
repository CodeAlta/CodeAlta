using System.Text;
using CodeAlta.Core.IO;

namespace CodeAlta.Tests;

[TestClass]
public sealed class SafeFileTests
{
    private static readonly TimeSpan Hang = TestTime.Seconds(30);

    [TestMethod]
    public async Task AWholeFile_IsWrittenAndRead_AndNoStagedFileIsLeft()
    {
        using var temp = TestTempDirectory.Create();
        var path = Path.Combine(temp.Path, "state.json");

        Assert.IsNull(SafeFile.ReadAllBytes(path));
        Assert.IsNull(await SafeFile.ReadAllTextAsync(path, CancellationToken.None));

        SafeFile.WriteAllText(path, "first");
        Assert.AreEqual("first", SafeFile.ReadAllText(path));
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("first"), File.ReadAllBytes(path), "UTF-8 without a byte order mark.");

        await SafeFile.WriteAllTextAsync(path, "second", CancellationToken.None);
        Assert.AreEqual("second", await SafeFile.ReadAllTextAsync(path, CancellationToken.None));

        await SafeFile.WriteAllBytesAsync(path, new byte[] { 1, 2, 3 }, CancellationToken.None);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await SafeFile.ReadAllBytesAsync(path, CancellationToken.None));

        SafeFile.WriteAllBytesWithoutGap(path, new byte[] { 4 });
        CollectionAssert.AreEqual(new byte[] { 4 }, SafeFile.ReadAllBytes(path));

        SafeFile.WriteAllText(path, "unicode", Encoding.Unicode);
        Assert.AreEqual("unicode", SafeFile.ReadAllText(path), "The preamble is written and read.");
        Assert.AreEqual(1, Directory.GetFiles(temp.Path).Length, "No staged file is left behind.");
    }

    [TestMethod]
    public void AWriteThatFails_LeavesNoStagedFile_AndTheFileAsItWas()
    {
        using var temp = TestTempDirectory.Create();
        var path = Path.Combine(temp.Path, "records.json");
        Directory.CreateDirectory(path);

        var failure = Assert.Throws<Exception>(() => SafeFile.WriteAllText(path, "content"));
        Assert.IsTrue(failure is IOException or UnauthorizedAccessException, failure.ToString());
        Assert.IsTrue(Directory.Exists(path));
        Assert.AreEqual(0, Directory.GetFiles(temp.Path).Length, "No staged file is left behind.");
    }

    [TestMethod]
    public async Task ReadersOfAFileThatWritersReplace_NeverFindItMissingNorHalfWritten()
    {
        using var temp = TestTempDirectory.Create();
        var path = Path.Combine(temp.Path, "shared.json");
        const int length = 64 * 1024;
        SafeFile.WriteAllBytes(path, Content(0));

        using var stop = new CancellationTokenSource();
        using var start = new ManualResetEventSlim();
        var readers = Enumerable.Range(0, 4).Select(reader => Task.Run(async () =>
        {
            start.Wait();
            var reads = 0;
            while (!stop.IsCancellationRequested || reads == 0)
            {
                var bytes = reader % 2 == 0 ? SafeFile.ReadAllBytes(path) : await SafeFile.ReadAllBytesAsync(path, CancellationToken.None);
                Assert.IsNotNull(bytes, "A reader found no file while it was replaced.");
                Assert.AreEqual(length, bytes.Length, "A reader found half a file.");
                Assert.IsTrue(bytes.AsSpan().IndexOfAnyExcept(bytes[0]) < 0, "A reader found two writes in one file.");
                reads++;
            }
        })).ToArray();
        var writers = Enumerable.Range(0, 4).Select(writer => Task.Run(async () =>
        {
            start.Wait();
            for (var index = 0; index < 50; index++)
            {
                if (writer % 2 == 0) SafeFile.WriteAllBytes(path, Content(writer * 50 + index));
                else await SafeFile.WriteAllBytesAsync(path, Content(writer * 50 + index), CancellationToken.None);
            }
        })).ToArray();

        start.Set();
        try
        {
            await Task.WhenAll(writers).WithinAsync(Hang);
        }
        finally
        {
            await stop.CancelAsync();
        }

        await Task.WhenAll(readers).WithinAsync(Hang);
        Assert.AreEqual(1, Directory.GetFiles(temp.Path).Length, "No staged file is left behind.");

        static byte[] Content(int index)
        {
            var bytes = new byte[length];
            bytes.AsSpan().Fill((byte)('a' + index % 26));
            return bytes;
        }
    }

    [TestMethod]
    public async Task AReadInTheMomentAReplacementLeavesNoFile_WaitsForTheNewOne_AlsoWhenItBlocks()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Elsewhere a rename replaces a file in one step for every reader.");
        using var temp = TestTempDirectory.Create();
        var path = Path.Combine(temp.Path, "policy.json");
        var staged = StagedFileReplacement.StagedPathOf(path);
        Assert.AreEqual(Path.GetFileName(path), Path.GetFileName(staged)[..Path.GetFileName(path).Length]);
        File.WriteAllText(staged, "after");

        // The previous file is gone, the staged one not yet in its place: the read waits rather than find none.
        var read = Task.Run(() => SafeFile.ReadAllText(path));
        await Task.Delay(100);
        File.Move(staged, path);
        Assert.AreEqual("after", await read.WithinAsync(Hang));
    }

    [TestMethod]
    public async Task AFileAReaderHoldsWithoutSharingItsDeletion_IsReplacedOnceTheReaderLetsItGo()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Elsewhere a reader never refuses a rename.");
        using var temp = TestTempDirectory.Create();
        var path = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(path, "before");

        // A reader as File.ReadAllText opens it: a replacement is refused while it holds the file, for a moment.
        var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var write = Task.Run(() => SafeFile.WriteAllText(path, "after"));
        await Task.Delay(100);
        await reader.DisposeAsync();
        await write.WithinAsync(Hang);
        Assert.AreEqual("after", File.ReadAllText(path));
        Assert.AreEqual(1, Directory.GetFiles(temp.Path).Length, "No staged file is left behind.");
    }

    [TestMethod]
    public async Task AFileHeldWithoutSharingItsDeletionButSharingItsWrites_IsWrittenInPlace()
    {
        // As a sandbox holds a protected file while its container runs: nothing replaces it, but it shares its writes.
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Elsewhere an open file never refuses a rename.");
        using var temp = TestTempDirectory.Create();
        var path = Path.Combine(temp.Path, "config.toml");
        File.WriteAllText(path, "a longer content than the next one");

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Write))
        {
            await Task.Run(() => SafeFile.WriteAllText(path, "after")).WithinAsync(Hang);
            Assert.AreEqual("after", ReadShared(path));
            await SafeFile.WriteAllTextAsync(path, "after again", CancellationToken.None).AsTask().WithinAsync(Hang);
            Assert.AreEqual("after again", ReadShared(path));
        }

        Assert.AreEqual(1, Directory.GetFiles(temp.Path).Length, "No staged file is left behind.");
    }

    [TestMethod]
    public async Task AFileHeldWithoutSharingItsWrites_IsNotWrittenInPlace()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Elsewhere an open file never refuses a rename.");
        using var temp = TestTempDirectory.Create();
        var path = Path.Combine(temp.Path, "config.toml");
        File.WriteAllText(path, "before");

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Task.Run(() => SafeFile.WriteAllText(path, "after")).WithinAsync(Hang));
        }

        Assert.AreEqual("before", File.ReadAllText(path));
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
