namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed class ProjectPathNameFormatterTests
{
    [TestMethod]
    [DataRow("X.Y")]
    [DataRow("CodeAlta.Tests")]
    [DataRow("my.project.v2")]
    [DataRow("A")]
    public void NameOfAFolderIsTheWholeNameOfTheFolder(string folder)
    {
        var path = Path.Combine(Path.GetTempPath(), "alta-names", folder);

        Assert.AreEqual(folder, ProjectPathNameFormatter.InferDisplayName(path));
        Assert.AreEqual(folder, ProjectPathNameFormatter.InferDisplayName(path + Path.DirectorySeparatorChar));
        Assert.AreEqual(folder, ProjectPathNameFormatter.InferName(path));
    }

    [TestMethod]
    [DataRow("https://github.com/CodeAlta/CodeAlta.Tests.git", "CodeAlta.Tests")]
    [DataRow("https://github.com/CodeAlta/X.Y", "X.Y")]
    [DataRow("https://github.com/CodeAlta/X.Y/", "X.Y")]
    public void NameOfARemoteRepositoryDropsOnlyTheGitOfItsAddress(string address, string expected)
        => Assert.AreEqual(expected, ProjectPathNameFormatter.InferDisplayName(address));
}
