using CodeAlta.Core.IO;

namespace CodeAlta.Tests;

[TestClass]
public sealed class FileIdentityTests
{
    [TestMethod]
    public void AFolder_KeepsItsIdentity_AndAnotherFolderOrALinkToItHasAnother()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "CodeAlta-identity-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var folder = Directory.CreateDirectory(Path.Combine(root, "folder")).FullName;
            var identity = FileIdentity.Of(folder);
            Assert.IsNotNull(identity, "the identity of a folder is read on this system");
            Assert.AreEqual(identity, FileIdentity.Of(folder + Path.DirectorySeparatorChar));
            File.WriteAllText(Path.Combine(folder, "file.txt"), "changes the folder, not its identity");
            Assert.AreEqual(identity, FileIdentity.Of(folder));

            // The folder moved aside and another created at its path, while the first still exists.
            Directory.Move(folder, folder + "-old");
            Directory.CreateDirectory(folder);
            Assert.AreNotEqual(identity, FileIdentity.Of(folder));
            Assert.AreEqual(identity, FileIdentity.Of(folder + "-old"));

            // A link is not followed: it has an identity of its own.
            TestLinks.RequireSymbolicLinks();
            var link = Path.Combine(root, "link");
            Directory.CreateSymbolicLink(link, folder + "-old");
            Assert.AreNotEqual(identity, FileIdentity.Of(link));
            Assert.IsNull(FileIdentity.Of(Path.Combine(root, "missing")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
