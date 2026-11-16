using System.Security.Cryptography;
using System.Text;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugins.Tests;

/// <summary>The files of a page a host without a window serves: those its list names, and only as the build made them.</summary>
[TestClass]
public sealed class FolderPluginAppFilesTests
{
    [TestMethod]
    public async Task ReadAsync_GivesTheFilesTheListNames_AndNothingElseOfTheFolder()
    {
        using var temp = new TestTempDirectory();
        Write(temp.Path, ("mobile.html", "<!doctype html>"), ("assets/mobile-CK1DWdvA.js", "export {}"), ("assets/chart-worker.js", "self"));
        File.WriteAllText(Path.Combine(temp.Path, "secret.txt"), "not listed");

        var files = FolderPluginAppFiles.TryOpen(temp.Path);

        Assert.IsNotNull(files);
        Assert.IsTrue(files.HasFiles);
        var page = await files.ReadAsync("mobile.html");
        Assert.AreEqual("text/html; charset=utf-8", page!.ContentType);
        Assert.IsFalse(page.Immutable, "the page is read again each time");
        Assert.AreEqual("<!doctype html>", Encoding.UTF8.GetString(page.Content.Span));
        var chunk = await files.ReadAsync("assets/mobile-CK1DWdvA.js");
        Assert.AreEqual("text/javascript; charset=utf-8", chunk!.ContentType);
        Assert.IsTrue(chunk.Immutable, "a chunk named by its hash never changes");
        Assert.IsFalse((await files.ReadAsync("assets/chart-worker.js"))!.Immutable, "a file with a fixed name can change");
        Assert.IsNull(await files.ReadAsync("secret.txt"), "a file the list does not name is no file");
        Assert.IsNull(await files.ReadAsync("files.json"));
        Assert.IsNull(await files.ReadAsync("../" + Path.GetFileName(temp.Path) + "/mobile.html"));
    }

    [TestMethod]
    public async Task ReadAsync_RefusesAFileThatIsNotTheOneTheBuildMade()
    {
        using var temp = new TestTempDirectory();
        Write(temp.Path, ("mobile.html", "<!doctype html>"));
        var files = FolderPluginAppFiles.TryOpen(temp.Path)!;
        File.WriteAllText(Path.Combine(temp.Path, "mobile.html"), "<!doctype html><script>alert(1)</script>");

        Assert.IsNull(await files.ReadAsync("mobile.html"));
    }

    [TestMethod]
    public void TryOpen_GivesNothing_WithoutAListThatReads_OrWithAPathThatLeavesTheFolder()
    {
        using var temp = new TestTempDirectory();
        Assert.IsNull(FolderPluginAppFiles.TryOpen(temp.Path), "no list");
        File.WriteAllText(Path.Combine(temp.Path, FolderPluginAppFiles.ListFileName), "{ not json");
        Assert.IsNull(FolderPluginAppFiles.TryOpen(temp.Path), "a list that does not read");
        foreach (var path in new[] { "../outside.html", "/etc/passwd", "a//b.js", "C:/x.js", "a\\b.js" })
        {
            File.WriteAllText(Path.Combine(temp.Path, FolderPluginAppFiles.ListFileName),
                $$"""{"files":[{"path":{{System.Text.Json.JsonSerializer.Serialize(path)}},"bytes":1,"sha256":"{{new string('0', 64)}}"}]}""");
            Assert.IsNull(FolderPluginAppFiles.TryOpen(temp.Path), path);
        }
    }

    // Writes files and their list, as scripts/mobile-page.mjs does.
    internal static void Write(string root, params (string Path, string Content)[] files)
    {
        var list = new List<string>();
        foreach (var (path, content) in files)
        {
            var target = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var bytes = Encoding.UTF8.GetBytes(content);
            File.WriteAllBytes(target, bytes);
            list.Add($$"""{"path":"{{path}}","bytes":{{bytes.Length}},"sha256":"{{Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}}"}""");
        }

        File.WriteAllText(Path.Combine(root, FolderPluginAppFiles.ListFileName), "{\"files\":[" + string.Join(",", list) + "]}");
    }
}
