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
                Assert.ThrowsExactly<UnauthorizedAccessException>(() => StagedFileReplacement.MoveOver(staged, path, renameWithPosixSemantics: false));
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
}
