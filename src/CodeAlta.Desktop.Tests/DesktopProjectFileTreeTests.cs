using System.Text;
using CodeAlta.Catalog;
using CodeAlta.Desktop;
using CodeAlta.Desktop.Rpc;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// The folders, file operations, images and search of the code editor over real temporary folders and a real
/// project catalog; only the trash and the file manager are literals.
/// </summary>
[TestClass]
public sealed class DesktopProjectFileTreeTests
{
    private const string Epoch = "epoch-1";
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13];

    [TestMethod]
    public async Task WithoutAnOwnedHost_EveryOperationIsUnavailable_AndAnotherEpochIsRefused()
    {
        var unavailable = new ProjectFilesService();
        Assert.AreEqual("unavailable", (await unavailable.ListAsync(new(Epoch, "p", [new("", null)], false), default)).Status);
        Assert.AreEqual("unavailable", (await unavailable.StatAsync(new(Epoch, "p", ["a.txt"]), default)).Status);
        Assert.AreEqual("unavailable", (await unavailable.CreateAsync(new(Epoch, "p", "a.txt", false), default)).Status);
        Assert.AreEqual("unavailable", (await unavailable.RenameAsync(new(Epoch, "p", "a.txt", "b.txt"), default)).Status);
        Assert.AreEqual("unavailable", (await unavailable.DeleteAsync(new(Epoch, "p", "a.txt", true), default)).Status);
        Assert.AreEqual("unavailable", (await unavailable.ImageAsync(new(Epoch, "p", "a.png"), default)).Status);
        Assert.AreEqual("unavailable", (await unavailable.RevealAsync(new(Epoch, "p", "a.txt"), default)).Status);
        Assert.AreEqual("unavailable", (await Search(unavailable, new(Epoch, "p", "x", false, false, false, null, null))).Single().Status);
        Assert.IsEmpty(await Collect(unavailable.WatchAsync(new(Epoch), default)));

        using var fixture = await Fixture.CreateAsync();
        var path = fixture.Write("a.txt", "text");
        Assert.AreEqual("stale_epoch", (await fixture.Service.ListAsync(new("other", fixture.ProjectId, [new("", null)], false), default)).Status);
        Assert.AreEqual("stale_epoch", (await fixture.Service.StatAsync(new(null, fixture.ProjectId, ["a.txt"]), default)).Status);
        Assert.AreEqual("stale_epoch", (await fixture.Service.CreateAsync(new("other", fixture.ProjectId, "b.txt", false), default)).Status);
        Assert.AreEqual("stale_epoch", (await fixture.Service.RenameAsync(new("other", fixture.ProjectId, "a.txt", "b.txt"), default)).Status);
        Assert.AreEqual("stale_epoch", (await fixture.Service.DeleteAsync(new("other", fixture.ProjectId, "a.txt", true), default)).Status);
        Assert.AreEqual("stale_epoch", (await Search(fixture.Service, new("other", fixture.ProjectId, "text", false, false, false, null, null))).Single().Status);
        Assert.AreEqual("unknown_project", (await fixture.Service.ListAsync(new(Epoch, "missing", [new("", null)], false), default)).Status);
        Assert.IsTrue(File.Exists(path));
        Assert.IsFalse(File.Exists(Path.Combine(fixture.ProjectPath, "b.txt")));
    }

    [TestMethod]
    public async Task List_ReturnsTheEntriesOfEachFolder_AndUnchangedForTheRevisionThePageHas()
    {
        using var fixture = await Fixture.CreateAsync(git: true);
        fixture.Write("readme.md", "r");
        fixture.Write("src/app.cs", "a");
        fixture.Write("src/lib/util.cs", "u");
        fixture.Write("bin/app.dll", "b");
        fixture.Write(".gitignore", "bin/\n");

        var first = await fixture.Service.ListAsync(new(Epoch, fixture.ProjectId, [new("", null), new("src", null), new("src\\lib", null)], false), default);

        Assert.AreEqual("ok", first.Status);
        Assert.IsTrue(first.Trash);
        Assert.HasCount(3, first.Folders);
        // One folder at a time: "src" is an entry of the project folder, and its own entries come with it only when asked.
        CollectionAssert.AreEqual(new[] { "src/", ".gitignore", "readme.md" }, Names(first.Folders[0]));
        CollectionAssert.AreEqual(new[] { "lib/", "app.cs" }, Names(first.Folders[1]));
        Assert.AreEqual("src/lib", first.Folders[2].Path);
        CollectionAssert.AreEqual(new[] { "util.cs" }, Names(first.Folders[2]));
        Assert.IsTrue(first.Folders.All(folder => folder is { Status: "ok", Truncated: false, Revision.Length: 16 }));

        // The same entries are not sent again; a folder that changed is.
        fixture.Write("src/new.cs", "n");
        var second = await fixture.Service.ListAsync(new(Epoch, fixture.ProjectId,
            [new("", first.Folders[0].Revision), new("src", first.Folders[1].Revision)], false), default);
        Assert.AreEqual("unchanged", second.Folders[0].Status);
        Assert.IsEmpty(second.Folders[0].Entries);
        Assert.AreEqual(first.Folders[0].Revision, second.Folders[0].Revision);
        Assert.AreEqual("ok", second.Folders[1].Status);
        CollectionAssert.AreEqual(new[] { "lib/", "app.cs", "new.cs" }, Names(second.Folders[1]));

        // What git ignores is listed only when asked for, and marked.
        var ignored = await fixture.Service.ListAsync(new(Epoch, fixture.ProjectId, [new("", first.Folders[0].Revision)], true), default);
        CollectionAssert.AreEqual(new[] { "bin/", "src/", ".gitignore", "readme.md" }, Names(ignored.Folders[0]));
        CollectionAssert.AreEqual(new[] { "bin" }, ignored.Folders[0].Entries.Where(entry => entry.Ignored).Select(entry => entry.Name).ToArray());
    }

    [TestMethod]
    public async Task List_AnswersEachFolderOnItsOwn()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Write("src/app.cs", "a");

        var response = await fixture.Service.ListAsync(new(Epoch, fixture.ProjectId,
            [new("src", null), new("missing", null), new("src/app.cs", null), new("../outside", null), new("a//b", null), new(null, null)], false), default);

        Assert.AreEqual("ok", response.Status);
        CollectionAssert.AreEqual(new[] { "ok", "not_found", "not_found", "outside_root", "invalid", "ok" }, response.Folders.Select(folder => folder.Status).ToArray());
        Assert.AreEqual("../outside", response.Folders[3].Path);
        CollectionAssert.AreEqual(new[] { "src/" }, Names(response.Folders[5]));

        Assert.AreEqual("invalid", (await fixture.Service.ListAsync(new(Epoch, fixture.ProjectId, [], false), default)).Status);
        Assert.AreEqual("invalid", (await fixture.Service.ListAsync(new(Epoch, fixture.ProjectId, null, false), default)).Status);
        var many = Enumerable.Repeat(new ProjectFileFolderQuery("", null), ProjectFilesService.MaximumListedFolders + 1).ToArray();
        Assert.AreEqual("invalid", (await fixture.Service.ListAsync(new(Epoch, fixture.ProjectId, many, false), default)).Status);
    }

    [TestMethod]
    public async Task Stat_ReturnsAStampThatChangesWithTheFile_AndAReadNamesIt()
    {
        using var fixture = await Fixture.CreateAsync();
        var path = fixture.Write("src/a.txt", "one");

        var read = await fixture.Service.ReadAsync(new(Epoch, fixture.ProjectId, "src/a.txt"), default);
        var stat = await fixture.Service.StatAsync(new(Epoch, fixture.ProjectId, ["src\\a.txt", "missing.txt", "src", "../x", ""]), default);

        Assert.AreEqual("ok", stat.Status);
        CollectionAssert.AreEqual(new[] { "ok", "not_found", "not_found", "outside_root", "invalid" }, stat.Files.Select(file => file.Status).ToArray());
        Assert.AreEqual("src/a.txt", stat.Files[0].Path);
        Assert.AreEqual(3, stat.Files[0].Length);
        Assert.IsFalse(stat.Files[0].ReadOnly);
        Assert.AreEqual(read.Stamp, stat.Files[0].Stamp);
        Assert.AreEqual("UTF-8", read.Encoding);

        // A write through the service names the stamp the file has afterwards.
        var written = await fixture.Service.WriteAsync(new(Epoch, fixture.ProjectId, "src/a.txt", "three", read.Revision, false), default);
        Assert.AreEqual("ok", written.Status);
        var after = await fixture.Service.StatAsync(new(Epoch, fixture.ProjectId, ["src/a.txt"]), default);
        Assert.AreEqual(written.Stamp, after.Files[0].Stamp);
        Assert.AreNotEqual(read.Stamp, written.Stamp);

        // Another program changes the file: the stamp changes without anything being read.
        File.WriteAllText(path, "changed elsewhere");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        var changed = await fixture.Service.StatAsync(new(Epoch, fixture.ProjectId, ["src/a.txt"]), default);
        Assert.AreNotEqual(written.Stamp, changed.Files[0].Stamp);
        Assert.IsTrue(changed.Files[0].ReadOnly);

        Assert.AreEqual("invalid", (await fixture.Service.StatAsync(new(Epoch, fixture.ProjectId, []), default)).Status);
        // A file read again because it changed is not recorded as opened again.
        Assert.HasCount(1, fixture.Usage.Events);
        await fixture.Service.ReadAsync(new(Epoch, fixture.ProjectId, "src/a.txt", Reload: true), default);
        Assert.HasCount(1, fixture.Usage.Events);
    }

    [TestMethod]
    public async Task Create_MakesAnEmptyFileOrAFolder_WithTheFoldersAboveIt_AndReplacesNothing()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Write("taken.txt", "kept");

        Assert.AreEqual("ok", (await fixture.Service.CreateAsync(new(Epoch, fixture.ProjectId, "new.txt", false), default)).Status);
        var nested = await fixture.Service.CreateAsync(new(Epoch, fixture.ProjectId, "a\\b/c.txt", false), default);
        Assert.AreEqual("ok", nested.Status);
        Assert.AreEqual("a/b/c.txt", nested.Path);
        Assert.AreEqual("ok", (await fixture.Service.CreateAsync(new(Epoch, fixture.ProjectId, "docs/images", true), default)).Status);

        Assert.AreEqual(0, new FileInfo(Path.Combine(fixture.ProjectPath, "new.txt")).Length);
        Assert.IsTrue(File.Exists(Path.Combine(fixture.ProjectPath, "a", "b", "c.txt")));
        Assert.IsTrue(Directory.Exists(Path.Combine(fixture.ProjectPath, "docs", "images")));
        // No staging file is left beside what was created.
        CollectionAssert.AreEqual(new[] { "c.txt" }, Directory.GetFileSystemEntries(Path.Combine(fixture.ProjectPath, "a", "b")).Select(Path.GetFileName).ToArray());

        Assert.AreEqual("exists", (await fixture.Service.CreateAsync(new(Epoch, fixture.ProjectId, "taken.txt", false), default)).Status);
        Assert.AreEqual("exists", (await fixture.Service.CreateAsync(new(Epoch, fixture.ProjectId, "docs", true), default)).Status);
        Assert.AreEqual("exists", (await fixture.Service.CreateAsync(new(Epoch, fixture.ProjectId, "docs", false), default)).Status);
        // A file is not a folder to create something in.
        Assert.AreEqual("not_found", (await fixture.Service.CreateAsync(new(Epoch, fixture.ProjectId, "taken.txt/inside.txt", false), default)).Status);
        Assert.AreEqual("outside_root", (await fixture.Service.CreateAsync(new(Epoch, fixture.ProjectId, "../escaped.txt", false), default)).Status);
        Assert.AreEqual("invalid", (await fixture.Service.CreateAsync(new(Epoch, fixture.ProjectId, "bad./x.txt", false), default)).Status);
        Assert.AreEqual("invalid", (await fixture.Service.CreateAsync(new(Epoch, fixture.ProjectId, "", true), default)).Status);
        Assert.AreEqual("kept", File.ReadAllText(Path.Combine(fixture.ProjectPath, "taken.txt")));
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Root, "escaped.txt")));
    }

    [TestMethod]
    public async Task Rename_MovesAFileOrAFolder_AndReplacesNothing()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Write("a.txt", "a");
        fixture.Write("taken.txt", "kept");
        fixture.Write("src/one.cs", "1");
        fixture.Write("src/deep/two.cs", "2");

        var renamed = await fixture.Service.RenameAsync(new(Epoch, fixture.ProjectId, "a.txt", "b.txt"), default);
        Assert.AreEqual("ok", renamed.Status);
        Assert.AreEqual("b.txt", renamed.Path);
        Assert.AreEqual("a", File.ReadAllText(Path.Combine(fixture.ProjectPath, "b.txt")));
        Assert.IsFalse(File.Exists(Path.Combine(fixture.ProjectPath, "a.txt")));

        // Into another folder, created as needed; a folder moves with what it holds.
        Assert.AreEqual("ok", (await fixture.Service.RenameAsync(new(Epoch, fixture.ProjectId, "b.txt", "moved/here/b.txt"), default)).Status);
        Assert.IsTrue(File.Exists(Path.Combine(fixture.ProjectPath, "moved", "here", "b.txt")));
        Assert.AreEqual("ok", (await fixture.Service.RenameAsync(new(Epoch, fixture.ProjectId, "src", "source"), default)).Status);
        Assert.AreEqual("2", File.ReadAllText(Path.Combine(fixture.ProjectPath, "source", "deep", "two.cs")));
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.ProjectPath, "src")));

        Assert.AreEqual("exists", (await fixture.Service.RenameAsync(new(Epoch, fixture.ProjectId, "moved/here/b.txt", "taken.txt"), default)).Status);
        Assert.AreEqual("exists", (await fixture.Service.RenameAsync(new(Epoch, fixture.ProjectId, "source", "moved"), default)).Status);
        Assert.AreEqual("invalid", (await fixture.Service.RenameAsync(new(Epoch, fixture.ProjectId, "source", "source/deep/inside"), default)).Status);
        Assert.AreEqual("not_found", (await fixture.Service.RenameAsync(new(Epoch, fixture.ProjectId, "gone.txt", "x.txt"), default)).Status);
        Assert.AreEqual("outside_root", (await fixture.Service.RenameAsync(new(Epoch, fixture.ProjectId, "taken.txt", "../out.txt"), default)).Status);
        Assert.AreEqual("outside_root", (await fixture.Service.RenameAsync(new(Epoch, fixture.ProjectId, "../global", "in.txt"), default)).Status);
        Assert.AreEqual("invalid", (await fixture.Service.RenameAsync(new(Epoch, fixture.ProjectId, "taken.txt", "bad?.txt " ), default)).Status);
        // The same name changes nothing.
        Assert.AreEqual("ok", (await fixture.Service.RenameAsync(new(Epoch, fixture.ProjectId, "taken.txt", "taken.txt"), default)).Status);
        Assert.AreEqual("kept", File.ReadAllText(Path.Combine(fixture.ProjectPath, "taken.txt")));
        Assert.IsTrue(Directory.Exists(Path.Combine(fixture.ProjectPath, "source")));

        if (OperatingSystem.IsWindows())
        {
            // Another case of the same name is the entry itself, not a name that is taken.
            Assert.AreEqual("ok", (await fixture.Service.RenameAsync(new(Epoch, fixture.ProjectId, "taken.txt", "Taken.TXT"), default)).Status);
            CollectionAssert.Contains(Directory.GetFiles(fixture.ProjectPath).Select(Path.GetFileName).ToArray(), "Taken.TXT");
            Assert.AreEqual("ok", (await fixture.Service.RenameAsync(new(Epoch, fixture.ProjectId, "source", "Source"), default)).Status);
            CollectionAssert.Contains(Directory.GetDirectories(fixture.ProjectPath).Select(Path.GetFileName).ToArray(), "Source");
        }
    }

    [TestMethod]
    public async Task Delete_MovesToTheTrash_AndRemovesForGoodOnlyWhenAsked()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Write("a.txt", "a");
        fixture.Write("b.txt", "b");
        fixture.Write("old/one.txt", "1");
        fixture.Write("old/deep/two.txt", "2");
        fixture.Write("keep/three.txt", "3");

        Assert.AreEqual("ok", (await fixture.Service.DeleteAsync(new(Epoch, fixture.ProjectId, "a.txt", false), default)).Status);
        Assert.AreEqual("ok", (await fixture.Service.DeleteAsync(new(Epoch, fixture.ProjectId, "old", false), default)).Status);
        CollectionAssert.AreEqual(new[] { Path.Combine(fixture.ProjectPath, "a.txt"), Path.Combine(fixture.ProjectPath, "old") }, fixture.Trash.Moved);
        Assert.IsFalse(File.Exists(Path.Combine(fixture.ProjectPath, "a.txt")));
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.ProjectPath, "old")));

        // A trash that fails removes nothing: the page asks before anything is deleted for good.
        fixture.Trash.Fail = true;
        Assert.AreEqual("trash_failed", (await fixture.Service.DeleteAsync(new(Epoch, fixture.ProjectId, "b.txt", false), default)).Status);
        Assert.IsTrue(File.Exists(Path.Combine(fixture.ProjectPath, "b.txt")));
        fixture.Trash.Available = false;
        Assert.AreEqual("trash_unavailable", (await fixture.Service.DeleteAsync(new(Epoch, fixture.ProjectId, "b.txt", false), default)).Status);
        Assert.IsFalse((await fixture.Service.ListAsync(new(Epoch, fixture.ProjectId, [new("", null)], false), default)).Trash);
        Assert.IsTrue(File.Exists(Path.Combine(fixture.ProjectPath, "b.txt")));

        Assert.AreEqual("ok", (await fixture.Service.DeleteAsync(new(Epoch, fixture.ProjectId, "b.txt", true), default)).Status);
        Assert.AreEqual("ok", (await fixture.Service.DeleteAsync(new(Epoch, fixture.ProjectId, "keep", true), default)).Status);
        Assert.IsFalse(File.Exists(Path.Combine(fixture.ProjectPath, "b.txt")));
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.ProjectPath, "keep")));
        Assert.HasCount(2, fixture.Trash.Moved);

        Assert.AreEqual("not_found", (await fixture.Service.DeleteAsync(new(Epoch, fixture.ProjectId, "b.txt", true), default)).Status);
        Assert.AreEqual("outside_root", (await fixture.Service.DeleteAsync(new(Epoch, fixture.ProjectId, "../global", true), default)).Status);
        // The project folder itself has no name to delete it by.
        foreach (var path in new[] { "", ".", "./" })
            Assert.AreEqual("invalid", (await fixture.Service.DeleteAsync(new(Epoch, fixture.ProjectId, path, true), default)).Status, path);
        Assert.IsTrue(Directory.Exists(fixture.ProjectPath));
        Assert.IsTrue(Directory.Exists(Path.Combine(fixture.Root, "global")));
    }

    [TestMethod]
    public async Task Image_ReturnsAnImageByItsContent()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Write("img/logo.png", Png);
        fixture.Write("img/photo.txt", [0xFF, 0xD8, 0xFF, 0xE0, 1, 2]);
        fixture.Write("img/fake.png", Encoding.UTF8.GetBytes("not an image"));

        var image = await fixture.Service.ImageAsync(new(Epoch, fixture.ProjectId, "img\\logo.png"), default);

        Assert.AreEqual("ok", image.Status);
        Assert.AreEqual("img/logo.png", image.Path);
        Assert.AreEqual("image/png", image.MediaType);
        CollectionAssert.AreEqual(Png, Convert.FromBase64String(image.Base64!));
        Assert.AreEqual(Png.Length, image.Length);
        Assert.AreEqual((await fixture.Service.StatAsync(new(Epoch, fixture.ProjectId, ["img/logo.png"]), default)).Files[0].Stamp, image.Stamp);
        // The content decides, not the name.
        Assert.AreEqual("image/jpeg", (await fixture.Service.ImageAsync(new(Epoch, fixture.ProjectId, "img/photo.txt"), default)).MediaType);
        Assert.AreEqual("unsupported_type", (await fixture.Service.ImageAsync(new(Epoch, fixture.ProjectId, "img/fake.png"), default)).Status);
        Assert.AreEqual("not_found", (await fixture.Service.ImageAsync(new(Epoch, fixture.ProjectId, "img/none.png"), default)).Status);
        Assert.AreEqual("not_found", (await fixture.Service.ImageAsync(new(Epoch, fixture.ProjectId, "img"), default)).Status);
        Assert.AreEqual("outside_root", (await fixture.Service.ImageAsync(new(Epoch, fixture.ProjectId, "../x.png"), default)).Status);

        Assert.AreEqual("image/gif", ProjectFilesService.ImageMediaType("GIF89a"u8));
        Assert.AreEqual("image/webp", ProjectFilesService.ImageMediaType("RIFF\0\0\0\0WEBPVP8 "u8));
        Assert.AreEqual("image/bmp", ProjectFilesService.ImageMediaType("BM\0\0"u8));
        Assert.AreEqual("image/x-icon", ProjectFilesService.ImageMediaType([0, 0, 1, 0, 1, 0]));
        Assert.AreEqual("image/avif", ProjectFilesService.ImageMediaType([0, 0, 0, 0x1C, .. "ftypavif"u8]));
        Assert.IsNull(ProjectFilesService.ImageMediaType("<svg xmlns='http://www.w3.org/2000/svg'/>"u8));
        Assert.IsNull(ProjectFilesService.ImageMediaType([]));
    }

    [TestMethod]
    public async Task Reveal_ShowsTheEntry_OrTheProjectFolder()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Write("src/a.txt", "a");

        Assert.AreEqual("ok", (await fixture.Service.RevealAsync(new(Epoch, fixture.ProjectId, "src/a.txt"), default)).Status);
        Assert.AreEqual("ok", (await fixture.Service.RevealAsync(new(Epoch, fixture.ProjectId, "src"), default)).Status);
        Assert.AreEqual("ok", (await fixture.Service.RevealAsync(new(Epoch, fixture.ProjectId, null), default)).Status);
        CollectionAssert.AreEqual(new[] { Path.Combine(fixture.ProjectPath, "src", "a.txt"), Path.Combine(fixture.ProjectPath, "src"), Path.GetFullPath(fixture.ProjectPath) }, fixture.Revealed);

        Assert.AreEqual("not_found", (await fixture.Service.RevealAsync(new(Epoch, fixture.ProjectId, "gone"), default)).Status);
        Assert.AreEqual("outside_root", (await fixture.Service.RevealAsync(new(Epoch, fixture.ProjectId, "../global"), default)).Status);
        Assert.HasCount(3, fixture.Revealed);
    }

    [TestMethod]
    public async Task Search_FindsATextOrAnExpression_InTheFilesTheGlobsSelect()
    {
        using var fixture = await Fixture.CreateAsync(git: true);
        fixture.Write("src/app.ts", "const Total = 1;\r\nlet total = Total + subtotal;\n// TOTAL\n");
        fixture.Write("src/app.test.ts", "expect(total).toBe(1);\n");
        fixture.Write("docs/notes.md", "The total is shown.\n");
        fixture.Write("bin/out.js", "total\n");
        fixture.Write("data.bin", [(byte)'t', (byte)'o', (byte)'t', (byte)'a', (byte)'l', 0, 1, 2]);
        fixture.Write(".gitignore", "bin/\n");

        var plain = await Search(fixture.Service, new(Epoch, fixture.ProjectId, "total", false, false, false, null, null));

        // What git ignores and what is not text are not searched; each file comes once, then the end with the totals.
        var done = plain[^1];
        Assert.AreEqual(("done", "ok", 3, 7, false), (done.Kind, done.Status, done.Files, done.MatchCount, done.Truncated));
        CollectionAssert.AreEquivalent(new[] { "docs/notes.md", "src/app.test.ts", "src/app.ts" }, plain[..^1].Select(file => file.Path).ToArray());
        var app = plain.Single(file => file.Path == "src/app.ts").Matches!;
        CollectionAssert.AreEqual(new[] { (1, 7), (2, 5), (2, 13), (2, 24), (3, 4) }, app.Select(match => (match.Line, match.Column)).ToArray());
        Assert.AreEqual("let total = Total + subtotal;", app[1].Text);
        Assert.AreEqual("total", app[1].Text.Substring(app[1].Start, app[1].Shown));

        var exact = await Search(fixture.Service, new(Epoch, fixture.ProjectId, "total", false, true, true, null, null));
        Assert.AreEqual(3, exact[^1].MatchCount);
        CollectionAssert.AreEqual(new[] { (2, 5) }, exact.Single(file => file.Path == "src/app.ts").Matches!.Select(match => (match.Line, match.Column)).ToArray());

        var expression = await Search(fixture.Service, new(Epoch, fixture.ProjectId, @"^(const|let)\s+(\w+)", true, true, false, "*.ts", "*.test.ts"));
        Assert.AreEqual(("done", "ok", 1, 2), (expression[^1].Kind, expression[^1].Status, expression[^1].Files, expression[^1].MatchCount));
        CollectionAssert.AreEqual(new[] { "const Total", "let total" },
            expression[0].Matches!.Select(match => match.Text.Substring(match.Start, match.Shown)).ToArray());

        Assert.AreEqual(1, (await Search(fixture.Service, new(Epoch, fixture.ProjectId, "total", false, false, false, "docs", null)))[^1].Files);
        Assert.AreEqual(2, (await Search(fixture.Service, new(Epoch, fixture.ProjectId, "total", false, false, false, " src/ , nothing ", null)))[^1].Files);
        Assert.AreEqual(0, (await Search(fixture.Service, new(Epoch, fixture.ProjectId, "absent-text", false, false, false, null, null)))[^1].MatchCount);

        Assert.AreEqual("invalid_pattern", (await Search(fixture.Service, new(Epoch, fixture.ProjectId, "(unclosed", true, false, false, null, null))).Single().Status);
        Assert.AreEqual("invalid", (await Search(fixture.Service, new(Epoch, fixture.ProjectId, "", false, false, false, null, null))).Single().Status);
        Assert.AreEqual("invalid", (await Search(fixture.Service, new(Epoch, fixture.ProjectId, "two\nlines", false, false, false, null, null))).Single().Status);
        Assert.AreEqual("unknown_project", (await Search(fixture.Service, new(Epoch, "missing", "total", false, false, false, null, null))).Single().Status);
    }

    [TestMethod]
    public async Task Search_StopsAtItsLimits_AndWhenThePageStopsReading()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Write("many.txt", string.Join('\n', Enumerable.Repeat("hit", ProjectFileSearch.MaximumFileMatches + 50)));
        for (var index = 0; index < 12; index++) fixture.Write($"more/{index}.txt", string.Join('\n', Enumerable.Repeat("hit hit", ProjectFileSearch.MaximumFileMatches)));

        var events = await Search(fixture.Service, new(Epoch, fixture.ProjectId, "hit", false, true, false, null, null));

        var done = events[^1];
        Assert.AreEqual(("done", "ok", true), (done.Kind, done.Status, done.Truncated));
        Assert.AreEqual(ProjectFileSearch.MaximumMatches, done.MatchCount);
        Assert.IsTrue(events[..^1].All(file => file.Matches!.Count <= ProjectFileSearch.MaximumFileMatches && file.Truncated == (file.Matches.Count == ProjectFileSearch.MaximumFileMatches) || file.Truncated));
        Assert.AreEqual(done.MatchCount, events[..^1].Sum(file => file.Matches!.Count));

        using var stop = new CancellationTokenSource();
        var read = 0;
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in fixture.Service.SearchAsync(new(Epoch, fixture.ProjectId, "hit", false, true, false, null, null), stop.Token))
                if (++read == 2) stop.Cancel();
        });
        Assert.AreEqual(2, read);
    }

    [TestMethod]
    public void SearchMatches_AreFoundLineByLine_WithATextToShow()
    {
        Assert.AreEqual("ok", ProjectFileSearch.TryCreate("needle", false, false, false, null, null, out var query));
        var text = "first needle\rsecond\r\n\tindented NEEDLE and needle\n" + new string('x', 500) + "needle" + new string('y', 500);

        var matches = ProjectFileSearch.Find(query!, text, 10, out var truncated);

        Assert.IsFalse(truncated);
        // CR, CR LF and LF each end a line, as they do in the editor.
        CollectionAssert.AreEqual(new[] { (1, 7), (3, 11), (3, 22), (4, 501) }, matches.Select(match => (match.Line, match.Column)).ToArray());
        Assert.AreEqual("indented NEEDLE and needle", matches[1].Text);
        Assert.AreEqual(9, matches[1].Start);
        // A match far into a long line is shown with a little before it, and the line is cut.
        var far = matches[3];
        Assert.IsTrue(far.Text.StartsWith('…') && far.Text.EndsWith('…'));
        Assert.IsLessThanOrEqualTo(ProjectFileSearch.MaximumPreviewLength + 2, far.Text.Length);
        Assert.AreEqual("needle", far.Text.Substring(far.Start, far.Shown));

        Assert.HasCount(2, ProjectFileSearch.Find(query!, text, 2, out truncated));
        Assert.IsTrue(truncated);

        // A match longer than what is shown is cut at the end of the text.
        Assert.AreEqual("ok", ProjectFileSearch.TryCreate("a+", true, true, false, null, null, out var run));
        var longRun = ProjectFileSearch.Find(run!, new string('a', 1000), 10, out _).Single();
        Assert.AreEqual((1000, ProjectFileSearch.MaximumPreviewLength), (longRun.Length, longRun.Shown));
        // An expression that matches nothing at all in a line is not a match on every line.
        Assert.AreEqual("ok", ProjectFileSearch.TryCreate("x*", true, true, false, null, null, out var empty));
        CollectionAssert.AreEqual(new[] { 2 }, ProjectFileSearch.Find(empty!, "a\nxx\nb", 10, out _).Select(match => match.Line).ToArray());

        Assert.AreEqual("ok", ProjectFileSearch.TryCreate("a.b", false, true, true, null, null, out var word));
        CollectionAssert.AreEqual(new[] { 3 }, ProjectFileSearch.Find(word!, "xa.b a.bx a.b", 10, out _).Select(match => match.Column - 8).ToArray());
        Assert.IsEmpty(ProjectFileSearch.Find(word!, "axb A.B", 10, out _));
    }

    [TestMethod]
    public void SearchGlobs_SelectFilesByNameFolderOrPath()
    {
        static bool Selects(string? include, string? exclude, string path)
        {
            Assert.AreEqual("ok", ProjectFileSearch.TryCreate("x", false, false, false, include, exclude, out var query));
            return ProjectFileSearch.Selects(query!, path);
        }

        Assert.IsTrue(Selects(null, null, "any/file.bin"));
        Assert.IsTrue(Selects("*.ts", null, "a.ts"));
        Assert.IsTrue(Selects("*.ts", null, "src/deep/a.ts"));
        Assert.IsFalse(Selects("*.ts", null, "src/a.tsx"));
        Assert.IsTrue(Selects("src", null, "src/a.cs"));
        Assert.IsTrue(Selects("src", null, "apps/src/deep/a.cs"));
        Assert.IsFalse(Selects("src", null, "source/a.cs"));
        Assert.IsTrue(Selects("./src/app/, *.md", null, "src/app/a.cs"));
        Assert.IsTrue(Selects("src\\app, *.md", null, "docs/readme.md"));
        Assert.IsFalse(Selects("src/app, *.md", null, "src/other/a.cs"));
        Assert.IsFalse(Selects(null, "*.test.ts, node_modules", "src/a.test.ts"));
        Assert.IsFalse(Selects(null, "*.test.ts, node_modules", "web/node_modules/x/index.js"));
        Assert.IsTrue(Selects("*.ts", "*.test.ts", "src/a.ts"));
        Assert.IsFalse(Selects("*.ts", "*.test.ts", "src/a.test.ts"));
        Assert.AreEqual(OperatingSystem.IsWindows(), Selects("*.TS", null, "src/a.ts"));

        Assert.AreEqual("invalid", ProjectFileSearch.TryCreate("x", false, false, false, new string('a', ProjectFileSearch.MaximumQueryLength + 1), null, out _));
        Assert.AreEqual("invalid", ProjectFileSearch.TryCreate(new string('a', ProjectFileSearch.MaximumQueryLength + 1), false, false, false, null, null, out _));
    }

    [TestMethod]
    public async Task Watch_PassesTheRequestsOfTheEditorView()
    {
        using var fixture = await Fixture.CreateAsync();
        // Nothing listens yet: the request is not kept for later.
        Assert.IsFalse(fixture.View.Open(fixture.ProjectId, "a.txt", 3, 4));
        Assert.IsEmpty(await Collect(fixture.Service.WatchAsync(new("other"), default)));

        using var stop = new CancellationTokenSource();
        var received = new List<ProjectFileShowEvent>();
        var watching = Task.Run(async () =>
        {
            try { await foreach (var value in fixture.Service.WatchAsync(new(Epoch), stop.Token)) { lock (received) received.Add(value); } }
            catch (OperationCanceledException) { }
        });
        await WaitAsync(() => fixture.View.Open(fixture.ProjectId, "src/a.txt", 12, 5));
        Assert.IsTrue(fixture.View.Open(fixture.ProjectId, null, 7, 2));
        Assert.IsTrue(fixture.View.Open(fixture.ProjectId, " ", null, null));
        Assert.IsTrue(fixture.View.Open(fixture.ProjectId, "b.txt", null, 9));
        await WaitAsync(() => { lock (received) return received.Count >= 4; });
        stop.Cancel();
        await watching;

        // A line needs a file, and a column needs a line.
        CollectionAssert.AreEqual(new ProjectFileShowEvent[] { new(fixture.ProjectId, "src/a.txt", 12, 5), new(fixture.ProjectId, null, null, null),
            new(fixture.ProjectId, null, null, null), new(fixture.ProjectId, "b.txt", null, null) }, received[^4..]);
        Assert.IsFalse(fixture.View.Open(fixture.ProjectId, "a.txt", null, null));
    }

    private static string[] Names(ProjectFileFolder folder) => folder.Entries.Select(static entry => entry.Directory ? entry.Name + "/" : entry.Name).ToArray();

    private static Task<List<ProjectFileSearchEvent>> Search(ProjectFilesService service, ProjectFileSearchRequest request) => Collect(service.SearchAsync(request, default));

    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> values)
    {
        var result = new List<T>();
        await foreach (var value in values) result.Add(value);
        return result;
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 400; attempt++)
        {
            if (condition()) return;
            await Task.Delay(10);
        }

        Assert.Fail("The condition was not met in time.");
    }

    private sealed class FakeTrash : IDesktopFileTrash
    {
        public bool Available { get; set; } = true;
        public bool Fail { get; set; }
        public List<string> Moved { get; } = [];

        public ValueTask<bool> MoveAsync(string fullPath, CancellationToken cancellationToken)
        {
            if (Fail) return ValueTask.FromResult(false);
            Moved.Add(fullPath);
            if (Directory.Exists(fullPath)) Directory.Delete(fullPath, recursive: true);
            else File.Delete(fullPath);
            return ValueTask.FromResult(true);
        }
    }

    private sealed class RecentFiles : IProjectFileSearchService
    {
        public List<ProjectFileUsageEvent> Events { get; } = [];

        public ValueTask RecordUsageAsync(ProjectFileUsageEvent usageEvent, CancellationToken cancellationToken = default)
        {
            Events.Add(usageEvent);
            return ValueTask.CompletedTask;
        }

        public ValueTask<IProjectFileSearchSession> CreateSessionAsync(ProjectFileSearchSessionOptions options, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<ProjectFileResolution> ResolveAsync(ProjectFileResolveQuery query, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask InvalidateAsync(string projectRoot, ProjectFileInvalidationReason reason, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class Fixture : IDisposable
    {
        private Fixture(string root, ProjectCatalog projects, ProjectDescriptor project)
        {
            Root = root;
            Project = project;
            Service = new ProjectFilesService(projects, Epoch, Usage, View, Trash, path => { Revealed.Add(path); return true; });
        }

        public static async Task<Fixture> CreateAsync(bool git = false)
        {
            var root = Path.Combine(Path.GetTempPath(), "CodeAlta-project-tree-" + Guid.NewGuid().ToString("N"));
            var projects = new ProjectCatalog(new CatalogOptions { GlobalRoot = Directory.CreateDirectory(Path.Combine(root, "global")).FullName });
            var folder = Directory.CreateDirectory(Path.Combine(root, "project")).FullName;
            if (git) Directory.CreateDirectory(Path.Combine(folder, ".git"));
            return new Fixture(root, projects, await projects.UpsertFromPathAsync(folder));
        }

        public string Root { get; }
        public ProjectDescriptor Project { get; }
        public RecentFiles Usage { get; } = new();
        public FakeTrash Trash { get; } = new();
        public DesktopEditorView View { get; } = new();
        public List<string> Revealed { get; } = [];
        public ProjectFilesService Service { get; }
        public string ProjectId => Project.Id;
        public string ProjectPath => Project.ProjectPath;

        public string Write(string relative, string text) => Write(relative, Encoding.UTF8.GetBytes(text));

        public string Write(string relative, byte[] bytes)
        {
            var path = Path.Combine(ProjectPath, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        public void Dispose()
        {
            try
            {
                if (!Directory.Exists(Root)) return;
                // A read-only file would keep its folder from being deleted.
                foreach (var file in new DirectoryInfo(Root).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                    if (file.IsReadOnly) file.IsReadOnly = false;
                Directory.Delete(Root, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup of a temporary directory.
            }
        }
    }
}
