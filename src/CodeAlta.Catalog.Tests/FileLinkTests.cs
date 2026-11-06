using CodeAlta.Catalog;

namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed class FileLinkTests
{
    private static readonly char Separator = Path.DirectorySeparatorChar;

    [TestMethod]
    [DataRow("src/Program.cs", "src/Program.cs", null, null)]
    [DataRow("src/Program.cs#L42", "src/Program.cs", 42, null)]
    [DataRow("src/Program.cs#L42C7", "src/Program.cs", 42, 7)]
    [DataRow("src/Program.cs#L42-L50", "src/Program.cs", 42, null)]
    [DataRow("src/Program.cs#42", "src/Program.cs", 42, null)]
    [DataRow("src/Program.cs#heading", "src/Program.cs", null, null)]
    [DataRow("src/Program.cs:42", "src/Program.cs", 42, null)]
    [DataRow("src/Program.cs:42:7", "src/Program.cs", 42, 7)]
    [DataRow("src/Program.cs:42-50", "src/Program.cs", 42, null)]
    [DataRow("src/Program.cs:0", "src/Program.cs", null, null)]
    [DataRow("README.md:3", "README.md", 3, null)]
    [DataRow("src%5CProgram.cs", "src/Program.cs", null, null)]
    [DataRow("my%20notes/a%20b.md", "my notes/a b.md", null, null)]
    [DataRow("caf%C3%A9.md", "café.md", null, null)]
    [DataRow("../other/a.cs", "../other/a.cs", null, null)]
    [DataRow("/src/Program.cs#L3", "/src/Program.cs", 3, null)]
    [DataRow("~/.alta/config.toml", "~/.alta/config.toml", null, null)]
    public void ATarget_IsReadAsAPathAndThePlaceInTheFile(string target, string path, int? line, int? column)
    {
        Assert.IsTrue(FileLink.TryParse(target, out var link), target);
        Assert.AreEqual(path.Replace('/', Separator), link.Path);
        Assert.AreEqual(line, link.Line);
        Assert.AreEqual(column, link.Column);
        Assert.IsFalse(link.IsAddress);
    }

    [TestMethod]
    [DataRow("a.cs", null, null)]
    [DataRow("a.cs#L42", 42, null)]
    [DataRow("a.cs#L42-L50", 42, 50)]
    [DataRow("a.cs#L42C7-L50C2", 42, 50)]
    [DataRow("a.cs#42-50", 42, 50)]
    [DataRow("a.cs:42-50", 42, 50)]
    [DataRow("a.cs:42:7-50:2", 42, 50)]
    [DataRow("a.cs#L42-L42", 42, 42)]
    [DataRow("a.cs#L50-L42", 50, null)]
    [DataRow("a.cs#L0-L42", null, null)]
    public void ARange_KeepsItsEnd(string target, int? line, int? endLine)
    {
        Assert.IsTrue(FileLink.TryParse(target, out var link), target);
        Assert.AreEqual("a.cs", link.Path);
        Assert.AreEqual(line, link.Line);
        Assert.AreEqual(endLine, link.EndLine);
    }

    [TestMethod]
    public void ARange_OfAnAddress_KeepsItsEnd()
    {
        var address = OperatingSystem.IsWindows() ? "file:///C:/code/a.cs" : "file:///code/a.cs";
        Assert.IsTrue(FileLink.TryParse(address + "#L4-L9", out var link));
        Assert.AreEqual((4, 9), (link.Line, link.EndLine));
        Assert.IsTrue(FileLink.TryParse(address + ":4-9", out link));
        Assert.AreEqual((4, 9), (link.Line, link.EndLine));
    }

    [TestMethod]
    public void ATarget_NamesADriveOrAnAddressOfAFile_AsTheSystemWritesThem()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var target in new[] { "C:/code/a.cs:12", "C:%5Ccode%5Ca.cs:12", "/C:/code/a.cs#L12", "c:/code/a.cs#L12" })
            {
                Assert.IsTrue(FileLink.TryParse(target, out var link), target);
                Assert.AreEqual(@"C:\code\a.cs", link.Path, ignoreCase: true);
                Assert.AreEqual(12, link.Line);
            }

            Assert.IsTrue(FileLink.TryParse("file:///C:/code/my%20report.html#L4", out var address));
            Assert.AreEqual(@"C:\code\my report.html", address.Path);
            Assert.IsTrue(address.IsAddress);
            Assert.AreEqual(4, address.Line);
            // A stream of a file, and a path that depends on the current folder of a drive.
            Assert.IsFalse(FileLink.TryParse("C:/code/a.cs:stream", out _));
            Assert.IsFalse(FileLink.TryParse("C:a.cs", out _));
            Assert.IsFalse(FileLink.TryParse("file:///code/a.cs", out _));
        }
        else
        {
            Assert.IsTrue(FileLink.TryParse("file:///home/me/my%20report.html", out var address));
            Assert.AreEqual("/home/me/my report.html", address.Path);
            Assert.IsTrue(address.IsAddress);
            Assert.IsTrue(FileLink.TryParse("notes/a:b.md", out var named));
            Assert.AreEqual("notes/a:b.md", named.Path);
        }
    }

    // Only parsed, never opened: opening one of these is what would send the credentials of the user to that computer.
    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("#L10")]
    [DataRow(" src/a.cs")]
    [DataRow("src/a.cs\n")]
    [DataRow("src/a%0A.cs")]
    [DataRow("//server/share/a.cs")]
    [DataRow(@"\\server\share\a.cs")]
    [DataRow(@"/\server/share/a.cs")]
    [DataRow("%5C%5Cserver%5Cshare%5Ca.cs")]
    [DataRow("%2F%2Fserver/share/a.cs")]
    [DataRow("//?/C:/code/a.cs")]
    [DataRow("//./pipe/a")]
    [DataRow(@"\\?\UNC\server\share\a.cs")]
    [DataRow(@"\\.\UNC\server\share\a.cs")]
    [DataRow(@"\??\UNC\server\share\a.cs")]
    [DataRow(@"/??\UNC\server\share\a.cs")]
    [DataRow("/??/UNC/server/share/a.cs")]
    [DataRow("%5C%3F%3F%5CUNC%5Cserver%5Cshare%5Ca.cs")]
    [DataRow("file://server/share/a.cs")]
    [DataRow("file:////server/share/a.cs")]
    [DataRow("file:///%5C%5Cserver/share/a.cs")]
    [DataRow("file:///%5C%3F%3F%5CUNC%5Cserver%5Cshare%5Ca.cs")]
    [DataRow(@"file:\\server\share\a.cs")]
    [DataRow("mailto:someone@example.invalid")]
    [DataRow("javascript:alert(1)")]
    [DataRow("vscode://file/C:/code/a.cs")]
    [DataRow("app://codealta/index.html")]
    [DataRow("https://user:secret@example.invalid/")]
    public void ATarget_ThatIsNoFileOfThisComputer_IsNotRead(string? target)
        => Assert.IsFalse(FileLink.TryParse(target, out _), target);

    [TestMethod]
    public void ATarget_LongerThanTheLimit_IsNotRead()
    {
        Assert.IsTrue(FileLink.TryParse(new string('a', FileLink.MaximumLength), out _));
        Assert.IsFalse(FileLink.TryParse(new string('a', FileLink.MaximumLength + 1), out _));
    }

    [TestMethod]
    public void ALink_IsLocatedFromTheFolderItIsReadIn_OrTheHomeFolder()
    {
        var start = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "file-link-start"));
        var home = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "file-link-home"));

        Assert.AreEqual(Path.Combine(start, "src", "a.cs"), Located("src/a.cs#L3", start, home));
        Assert.AreEqual(Path.GetFullPath(Path.Combine(start, "..", "a.cs")), Located("../a.cs", start, home));
        Assert.AreEqual(Path.Combine(home, ".alta", "a.toml"), Located("~/.alta/a.toml", start, home));
        Assert.AreEqual(home, Located("~", start, home));

        Assert.IsNull(Located("a.cs", null, home), "A relative path needs a folder to start from.");
        Assert.IsNull(Located("a.cs", "relative/folder", home), "So does one that starts from a folder that is not full.");
        Assert.IsNull(Located("~/a.cs", start, ""), "Without a home folder, a path of the shell names nothing.");

        // A path from the root is a file of the project unless that file is there.
        Assert.AreEqual(Path.Combine(start, "not-there-1f3c", "a.cs"), Located("/not-there-1f3c/a.cs", start, home));
    }

    private static string? Located(string target, string? start, string home)
    {
        Assert.IsTrue(FileLink.TryParse(target, out var link), target);
        return link.Locate(start, home);
    }
}
