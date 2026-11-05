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
