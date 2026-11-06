using System.Net;
using CodeAlta.Browser.Tools;
using CodeAlta.Plugin.Browser;

namespace CodeAlta.Browser.Tests;

[TestClass]
public sealed class ProjectFileServerTests
{
    [TestMethod]
    public async Task TheFilesOfVersionControlAndOfSecrets_AreNeverServed()
    {
        using var folder = new BrowserPluginTests.TempFolder();
        var root = Directory.CreateDirectory(Path.Combine(folder.Path, "project")).FullName;
        var files = new Dictionary<string, string>
        {
            ["index.html"] = "<p>page</p>",
            [".env"] = "SECRET=1",
            [".git/config"] = "[remote \"origin\"]\n\turl = https://user:token@example.com/repo.git\n",
            [".git/HEAD"] = "ref: refs/heads/main\n",
            [".git/objects/ab/cdef0123456789"] = "blob",
            [".hg/hgrc"] = "[paths]\ndefault = https://user:token@example.com/repo\n",
            [".svn/wc.db"] = "db",
            ["sub/.git"] = "gitdir: ../.git/worktrees/sub\n",
            ["plain.txt"] = "plain",
        };
        foreach (var (name, text) in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, name))!);
            await File.WriteAllTextAsync(Path.Combine(root, name), text);
        }

        // Links inside the project, by a plain name to a private file and by a private name to a plain file.
        File.CreateSymbolicLink(Path.Combine(root, "config.txt"), Path.Combine(root, ".git", "config"));
        Directory.CreateDirectory(Path.Combine(root, "lib"));
        File.CreateSymbolicLink(Path.Combine(root, "lib", ".env"), Path.Combine(root, "plain.txt"));

        await using var server = new ProjectFileServer();
        var page = server.Address(root, Path.Combine(root, "index.html"));
        using var http = new HttpClient();
        Assert.AreEqual("<p>page</p>", await http.GetStringAsync(page));
        Assert.AreEqual("plain", await http.GetStringAsync(new Uri(page, "plain.txt")));
        foreach (var path in new[] { ".env", ".git/config", ".git/HEAD", ".git/objects/ab/cdef0123456789", ".hg/hgrc", ".svn/wc.db", "sub/.git", "config.txt", "lib/.env", ".GIT/config" })
        {
            var response = await http.GetAsync(new Uri(page, path));
            Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode, path);
        }
    }

    [TestMethod]
    public async Task AFileADevServerIsWriting_IsServedStill()
    {
        using var folder = new BrowserPluginTests.TempFolder();
        var root = BrowserFiles.RealPath(Directory.CreateDirectory(Path.Combine(folder.Path, "project")).FullName);
        var page = Path.Combine(root, "page.html");
        await File.WriteAllTextAsync(page, "inside");
        Assert.IsTrue(ProjectFileServer.Readable(root, page, out var real));

        // A watcher or an editor keeps it open for writing, and may replace it, as Vite and tsc do on Windows.
        using var writer = new FileStream(page, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        using var stream = ProjectFileServer.OpenChecked(root, real);
        Assert.IsNotNull(stream);
        Assert.AreEqual("inside", await new StreamReader(stream).ReadToEndAsync());
    }

    [TestMethod]
    public async Task AFileReplacedByALinkAfterTheCheck_IsNotServed()
    {
        using var folder = new BrowserPluginTests.TempFolder();
        var root = BrowserFiles.RealPath(Directory.CreateDirectory(Path.Combine(folder.Path, "project")).FullName);
        var outside = Directory.CreateDirectory(Path.Combine(folder.Path, "outside")).FullName;
        await File.WriteAllTextAsync(Path.Combine(outside, "page.html"), "outside");
        var page = Path.Combine(root, "page.html");
        await File.WriteAllTextAsync(page, "inside");
        Assert.IsTrue(ProjectFileServer.Readable(root, page, out var real));

        // Between the check and the opening, the file becomes a link to a file outside the project.
        File.Delete(page);
        File.CreateSymbolicLink(page, Path.Combine(outside, "page.html"));
        using (var stream = ProjectFileServer.OpenChecked(root, real))
        {
            Assert.IsNull(stream, "the file opened is outside the project");
        }

        // The same with a folder of its path.
        var sub = Directory.CreateDirectory(Path.Combine(root, "sub")).FullName;
        await File.WriteAllTextAsync(Path.Combine(sub, "page.html"), "inside");
        Assert.IsTrue(ProjectFileServer.Readable(root, Path.Combine(sub, "page.html"), out real));
        Directory.Delete(sub, recursive: true);
        Directory.CreateSymbolicLink(sub, outside);
        using (var stream = ProjectFileServer.OpenChecked(root, real))
        {
            Assert.IsNull(stream, "the folder of the file opened is outside the project");
        }

        // A file of secrets swapped in is refused too.
        var plain = Path.Combine(root, "plain.txt");
        await File.WriteAllTextAsync(plain, "plain");
        await File.WriteAllTextAsync(Path.Combine(root, ".env"), "SECRET=1");
        Assert.IsTrue(ProjectFileServer.Readable(root, plain, out real));
        File.Delete(plain);
        File.CreateSymbolicLink(plain, Path.Combine(root, ".env"));
        using (var stream = ProjectFileServer.OpenChecked(root, real))
        {
            Assert.IsNull(stream, "the file opened holds secrets");
        }

        await File.WriteAllTextAsync(Path.Combine(root, "kept.txt"), "kept");
        Assert.IsTrue(ProjectFileServer.Readable(root, Path.Combine(root, "kept.txt"), out real));
        using (var stream = ProjectFileServer.OpenChecked(root, real))
        {
            Assert.IsNotNull(stream, "a file that stays where it was is served");
        }
    }
}
