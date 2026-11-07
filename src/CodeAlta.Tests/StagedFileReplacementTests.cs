using CodeAlta.Agent.Runtime;

namespace CodeAlta.Tests;

[TestClass]
public sealed class StagedFileReplacementTests
{
    [TestMethod]
    public async Task AFileHeldOpenByAReader_IsReplacedWhileItIsRead_AndTheReaderKeepsWhatItOpened()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-staged-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "credential.json");
            var staged = path + ".tmp";
            File.WriteAllText(path, "before");
            File.WriteAllText(staged, "after");

            // A reader as the credential stores open them: Windows refuses a plain move onto the file while it is open.
            await using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                await StagedFileReplacement.MoveOverAsync(staged, path).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.AreEqual("after", File.ReadAllText(path), "The file is replaced while the reader holds it.");
                using var text = new StreamReader(reader);
                Assert.AreEqual("before", text.ReadToEnd(), "The reader keeps the file it opened.");
            }

            Assert.AreEqual("after", File.ReadAllText(path));
            Assert.IsFalse(File.Exists(staged));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void AFileNoRenameCanReplace_IsReplacedAnotherWay_AndAReadOnlyOneIsStillRefused()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-staged-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "project.json");
            var staged = path + ".tmp";
            File.WriteAllText(path, "before");
            File.WriteAllText(staged, "after");

            // Without the POSIX rename, the file held open is one that no rename replaces, as one a file system
            // filter (an antivirus scanning what the process wrote) holds: it is replaced all the same.
            using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                StagedFileReplacement.MoveOver(staged, path, renameWithPosixSemantics: false);
                using var text = new StreamReader(reader);
                Assert.AreEqual("before", text.ReadToEnd(), "The reader keeps the file it opened.");
            }

            Assert.AreEqual("after", File.ReadAllText(path));
            Assert.IsFalse(File.Exists(staged));

            // On Windows, a read-only file is never replaced, by a rename or otherwise (elsewhere a rename replaces it).
            if (!OperatingSystem.IsWindows()) return;
            File.WriteAllText(staged, "refused");
            File.SetAttributes(path, FileAttributes.ReadOnly);
            try
            {
                var started = System.Diagnostics.Stopwatch.StartNew();
                Assert.ThrowsExactly<UnauthorizedAccessException>(() => StagedFileReplacement.MoveOver(staged, path, renameWithPosixSemantics: false));
                Assert.IsLessThan(TimeSpan.FromSeconds(1), started.Elapsed, "A read-only file is refused at once: no reader lets it go.");
                Assert.AreEqual("after", File.ReadAllText(path));
            }
            finally
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void WithoutAGap_AFileHeldBeyondTheRetries_StaysInPlace_AndTheWriteFails()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Elsewhere a rename replaces a file that is open.");
        var root = Path.Combine(Path.GetTempPath(), "codealta-staged-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "store.json");
            var staged = path + ".tmp";
            File.WriteAllText(path, "before");
            File.WriteAllText(staged, "after");

            // A store whose readers take a missing file for an empty one (secrets, checkpoints) is never moved out of the way.
            using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                Assert.Throws<UnauthorizedAccessException>(() => StagedFileReplacement.MoveOverWithoutGap(staged, path, renameWithPosixSemantics: false));
                Assert.IsTrue(File.Exists(path), "the file was there all along");
            }

            Assert.AreEqual("before", File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task AReadInTheMomentAReplacementLeavesNoFile_GetsTheNewOne_AndAMissingFileIsNoneAtOnce()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Elsewhere a rename replaces a file in one step for every reader.");
        var root = Path.Combine(Path.GetTempPath(), "codealta-staged-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "credential.json");
            var staged = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(staged, "after");

            // On Windows a rename that replaces a file can leave no file for tens of milliseconds (rarely, but a refresh
            // read as a sign-out): the previous file is gone, the staged one not yet in its place.
            var read = Task.Run(async () =>
            {
                await using var stream = await StagedFileReplacement.OpenReplacedFileAsync(path, CancellationToken.None);
                return stream is null ? null : await new StreamReader(stream).ReadToEndAsync();
            });
            await Task.Delay(100);
            File.Move(staged, path);
            Assert.AreEqual("after", await read.WaitAsync(TimeSpan.FromSeconds(10)));

            // A file a replacement holds for a moment (a sharing violation, or an access denied) is read once it lets go.
            var holder = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            read = Task.Run(async () =>
            {
                await using var stream = await StagedFileReplacement.OpenReplacedFileAsync(path, CancellationToken.None);
                return stream is null ? null : await new StreamReader(stream).ReadToEndAsync();
            });
            await Task.Delay(100);
            await holder.DisposeAsync();
            Assert.AreEqual("after", await read.WaitAsync(TimeSpan.FromSeconds(10)));

            // No write in progress: a missing file is none, without waiting.
            File.Delete(path);
            var started = System.Diagnostics.Stopwatch.StartNew();
            Assert.IsNull(await StagedFileReplacement.OpenReplacedFileAsync(path, CancellationToken.None));

            // Nor for a staged file a write left behind long ago (a process that ended in the middle of it).
            File.WriteAllText(staged, "left");
            File.SetLastWriteTimeUtc(staged, DateTime.UtcNow.AddHours(-1));
            Assert.IsNull(await StagedFileReplacement.OpenReplacedFileAsync(path, CancellationToken.None));
            Assert.IsNull(await StagedFileReplacement.OpenReplacedFileAsync(Path.Combine(root, "missing", "credential.json"), CancellationToken.None));
            Assert.IsLessThan(TimeSpan.FromSeconds(1), started.Elapsed);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task AFolderInThePlaceOfTheFile_IsRefusedAtOnce()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-staged-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "records.json");
            var staged = path + ".tmp";
            Directory.CreateDirectory(path);
            File.WriteAllText(staged, "after");

            // Nothing holds it for a moment: waiting would only delay the refusal the writer reports.
            var started = System.Diagnostics.Stopwatch.StartNew();
            var sync = Assert.Throws<Exception>(() => StagedFileReplacement.MoveOver(staged, path, renameWithPosixSemantics: false));
            var async = await Assert.ThrowsAsync<Exception>(async () => await StagedFileReplacement.MoveOverAsync(staged, path));
            Assert.IsInstanceOfType(sync, typeof(IOException).IsInstanceOfType(sync) ? typeof(IOException) : typeof(UnauthorizedAccessException));
            Assert.IsInstanceOfType(async, typeof(IOException).IsInstanceOfType(async) ? typeof(IOException) : typeof(UnauthorizedAccessException));
            Assert.IsLessThan(TimeSpan.FromSeconds(1), started.Elapsed);
            Assert.IsTrue(Directory.Exists(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
