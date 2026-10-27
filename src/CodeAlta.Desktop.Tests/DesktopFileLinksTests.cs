using CodeAlta.Catalog;
using CodeAlta.Desktop.Rpc;

namespace CodeAlta.Desktop.Tests;

/// <summary>Real temporary folders and a real project catalog; the window and the system are lists.</summary>
[TestClass]
public sealed class DesktopFileLinksTests
{
    private const string Epoch = "3b0a7c1e-52c9-4f0b-8a55-6d0c2f9e1b77";
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
        Assert.IsTrue(DesktopFileLink.TryParse(target, out var link), target);
        Assert.AreEqual(path.Replace('/', Separator), link.Path);
        Assert.AreEqual(line, link.Line);
        Assert.AreEqual(column, link.Column);
        Assert.IsFalse(link.IsAddress);
    }

    [TestMethod]
    public void ATarget_NamesADriveOrAnAddressOfAFile_AsTheSystemWritesThem()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var target in new[] { "C:/code/a.cs:12", "C:%5Ccode%5Ca.cs:12", "/C:/code/a.cs#L12", "c:/code/a.cs#L12" })
            {
                Assert.IsTrue(DesktopFileLink.TryParse(target, out var link), target);
                Assert.AreEqual(@"C:\code\a.cs", link.Path, ignoreCase: true);
                Assert.AreEqual(12, link.Line);
            }

            Assert.IsTrue(DesktopFileLink.TryParse("file:///C:/code/my%20report.html#L4", out var address));
            Assert.AreEqual(@"C:\code\my report.html", address.Path);
            Assert.IsTrue(address.IsAddress);
            Assert.AreEqual(4, address.Line);
            // A stream of a file, and a path that depends on the current folder of a drive.
            Assert.IsFalse(DesktopFileLink.TryParse("C:/code/a.cs:stream", out _));
            Assert.IsFalse(DesktopFileLink.TryParse("C:a.cs", out _));
            Assert.IsFalse(DesktopFileLink.TryParse("file:///code/a.cs", out _));
        }
        else
        {
            Assert.IsTrue(DesktopFileLink.TryParse("file:///home/me/my%20report.html", out var address));
            Assert.AreEqual("/home/me/my report.html", address.Path);
            Assert.IsTrue(address.IsAddress);
            Assert.IsTrue(DesktopFileLink.TryParse("notes/a:b.md", out var named));
            Assert.AreEqual("notes/a:b.md", named.Path);
        }
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("#L10")]
    [DataRow(" src/a.cs")]
    [DataRow("src/a.cs\n")]
    [DataRow("src/a%0A.cs")]
    [DataRow("//server/share/a.cs")]
    [DataRow("%5C%5Cserver%5Cshare%5Ca.cs")]
    [DataRow("//?/C:/code/a.cs")]
    [DataRow("//./pipe/a")]
    [DataRow("file://server/share/a.cs")]
    [DataRow("file:////server/share/a.cs")]
    [DataRow("mailto:someone@example.invalid")]
    [DataRow("javascript:alert(1)")]
    [DataRow("vscode://file/C:/code/a.cs")]
    [DataRow("app://codealta/index.html")]
    [DataRow("https://user:secret@example.invalid/")]
    public void ATarget_ThatIsNoFileOfThisComputer_IsNotRead(string? target)
        => Assert.IsFalse(DesktopFileLink.TryParse(target, out _), target);

    [TestMethod]
    public void ATarget_LongerThanTheLimit_IsNotRead()
    {
        Assert.IsTrue(DesktopFileLink.TryParse(new string('a', DesktopFileLink.MaximumLength), out _));
        Assert.IsFalse(DesktopFileLink.TryParse(new string('a', DesktopFileLink.MaximumLength + 1), out _));
    }

    [TestMethod]
    public async Task ARelativeLinkOfASession_OpensTheFileInTheEditorOfItsProject()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Write(fixture.ProjectPath, "src/Program.cs", "class Program;");

        Assert.AreEqual("ok", await fixture.OpenAsync("src/Program.cs#L12", session: "session"));
        Assert.AreEqual(new ProjectFileShowEvent(fixture.ProjectId, "src/Program.cs", 12, null), fixture.Shown.Single());

        // The same file by its full path, and as a site writes a path from its root.
        fixture.Shown.Clear();
        Assert.AreEqual("ok", await fixture.OpenAsync(Path.Combine(fixture.ProjectPath, "src", "Program.cs").Replace('\\', '/') + ":3:5"));
        Assert.AreEqual("ok", await fixture.OpenAsync("/src/Program.cs", session: "session"));
        CollectionAssert.AreEqual(new[] { new ProjectFileShowEvent(fixture.ProjectId, "src/Program.cs", 3, 5), new ProjectFileShowEvent(fixture.ProjectId, "src/Program.cs", null, null) }, fixture.Shown);

        // A folder of the project shows the files of the project.
        fixture.Shown.Clear();
        Assert.AreEqual("ok", await fixture.OpenAsync("src", session: "session"));
        Assert.AreEqual(new ProjectFileShowEvent(fixture.ProjectId, null, null, null), fixture.Shown.Single());
    }

    [TestMethod]
    public async Task ARelativeLink_StartsFromTheFolderOfItsDocument_AndFromNothingWithoutOne()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Write(fixture.ProjectPath, "doc/guide/setup.md", "# Setup");

        Assert.AreEqual("ok", await fixture.OpenAsync("guide/setup.md", project: fixture.ProjectId, directory: "doc"));
        Assert.AreEqual("ok", await fixture.OpenAsync("doc/guide/setup.md", project: fixture.ProjectId));
        Assert.AreEqual(2, fixture.Shown.Count(shown => shown == new ProjectFileShowEvent(fixture.ProjectId, "doc/guide/setup.md", null, null)));

        fixture.Shown.Clear();
        Assert.AreEqual("not_found", await fixture.OpenAsync("doc/guide/setup.md"));
        Assert.AreEqual("not_found", await fixture.OpenAsync("doc/guide/setup.md", session: "unknown"));
        Assert.AreEqual("not_found", await fixture.OpenAsync("doc/guide/setup.md", project: "unknown"));
        Assert.AreEqual("not_found", await fixture.OpenAsync("guide/setup.md", project: fixture.ProjectId, directory: "../doc"));
        Assert.AreEqual("not_found", await fixture.OpenAsync("doc/missing.md", session: "session"));
        Assert.IsEmpty(fixture.Shown);
    }

    [TestMethod]
    public async Task AFileOfNoProject_OpensTheEditorOnItsFolder_WhichTheEditorThenReads()
    {
        using var fixture = await Fixture.CreateAsync();
        var file = fixture.Write(Path.Combine(fixture.Root, "elsewhere"), "notes.txt", "some notes");
        fixture.Write(Path.Combine(fixture.Root, "elsewhere"), "beside.txt", "beside");

        Assert.AreEqual("ok", await fixture.OpenAsync(file.Replace('\\', '/') + "#L2", session: "session"));

        var shown = fixture.Shown.Single();
        StringAssert.StartsWith(shown.ProjectId, DiskFolders.Prefix);
        Assert.AreEqual(("notes.txt", 2, "elsewhere", Path.GetDirectoryName(file)), (shown.Path, shown.Line, shown.Name, shown.Root));
        // The id names the folder for the code editor: the file, and the others of its folder.
        Assert.AreEqual("some notes", (await fixture.Files.ReadAsync(new(Epoch, shown.ProjectId, "notes.txt"), default)).Content);
        Assert.AreEqual("beside", (await fixture.Files.ReadAsync(new(Epoch, shown.ProjectId, "beside.txt"), default)).Content);
        Assert.AreEqual("outside_root", (await fixture.Files.ReadAsync(new(Epoch, shown.ProjectId, "../project/a.txt"), default)).Status);
        // An id that this host did not give names no folder.
        Assert.AreEqual("unknown_project", (await fixture.Files.ReadAsync(new(Epoch, DiskFolders.Prefix + new string('0', 24), "notes.txt"), default)).Status);

        // A folder of no project opens with its files shown.
        fixture.Shown.Clear();
        Assert.AreEqual("ok", await fixture.OpenAsync(Path.GetDirectoryName(file)!.Replace('\\', '/')));
        Assert.AreEqual((shown.ProjectId, (string?)null), (fixture.Shown.Single().ProjectId, fixture.Shown.Single().Path));
    }

    [TestMethod]
    public async Task AFileOfTheFolderASessionWorksIn_OpensTheEditorOnThatFolder()
    {
        using var fixture = await Fixture.CreateAsync();
        // A git worktree: a checkout of its own, outside the folder of the project.
        var worktree = Directory.CreateDirectory(Path.Combine(fixture.Root, "trees", "feature")).FullName;
        fixture.Write(worktree, "src/deep/Program.cs", "class Program;");
        fixture.SessionFolders["worktree-session"] = worktree;

        Assert.AreEqual("ok", await fixture.OpenAsync("src/deep/Program.cs:7", session: "worktree-session"));

        var shown = fixture.Shown.Single();
        StringAssert.StartsWith(shown.ProjectId, DiskFolders.Prefix);
        Assert.AreEqual(("src/deep/Program.cs", 7, "feature", worktree), (shown.Path, shown.Line, shown.Name, shown.Root));
        Assert.AreEqual("ok", (await fixture.Files.ReadAsync(new(Epoch, shown.ProjectId, "src/deep/Program.cs"), default)).Status);
    }

    [TestMethod]
    public async Task AFileThatIsNotText_IsNotOpened_AndAPictureIs()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Write(fixture.ProjectPath, "tool.bin", [0x4D, 0x5A, 0x00, 0x01, 0x02]);
        fixture.Write(fixture.ProjectPath, "wide.txt", [0xFF, 0xFE, (byte)'a', 0x00]);
        fixture.Write(fixture.ProjectPath, "logo.png", [0x89, 0x50, 0x4E, 0x47, 0x00]);

        Assert.AreEqual("binary", await fixture.OpenAsync("tool.bin", session: "session"));
        Assert.IsEmpty(fixture.Shown);
        Assert.AreEqual("ok", await fixture.OpenAsync("wide.txt", session: "session"));
        Assert.AreEqual("ok", await fixture.OpenAsync("logo.png", session: "session"));
        CollectionAssert.AreEqual(new[] { "wide.txt", "logo.png" }, fixture.Shown.Select(static shown => shown.Path).ToArray());
    }

    [TestMethod]
    public async Task AnAddressOfADocument_IsHandedToTheSystem_AndAnyOtherFileToTheEditor()
    {
        using var fixture = await Fixture.CreateAsync();
        var page = fixture.Write(fixture.ProjectPath, "out/report.html", "<p>report</p>");
        var source = fixture.Write(fixture.ProjectPath, "out/report.cs", "class Report;");

        Assert.AreEqual("ok", await fixture.OpenAsync(new Uri(page).AbsoluteUri));
        CollectionAssert.AreEqual(new[] { page }, fixture.Documents);
        Assert.IsEmpty(fixture.Shown);
        // The same page named by its path is a file to read, like any other.
        Assert.AreEqual("ok", await fixture.OpenAsync("out/report.html", session: "session"));
        Assert.AreEqual("ok", await fixture.OpenAsync(new Uri(source).AbsoluteUri + "#L1"));
        CollectionAssert.AreEqual(new[] { "out/report.html", "out/report.cs" }, fixture.Shown.Select(static shown => shown.Path).ToArray());
        Assert.AreEqual(1, fixture.Documents.Count);

        fixture.SystemOpens = false;
        Assert.AreEqual("failed", await fixture.OpenAsync(new Uri(page).AbsoluteUri));
        Assert.AreEqual("not_found", await fixture.OpenAsync(new Uri(Path.Combine(fixture.ProjectPath, "out", "missing.html")).AbsoluteUri));
    }

    [TestMethod]
    public async Task TheLinksOfTheWindow_GoToTheBrowserOrToTheEditor_AndNothingElseIsFollowed()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Write(fixture.ProjectPath, "src/Program.cs", "class Program;");
        var pages = new List<string>();
        var service = new MarkdownLinksService(Epoch, address => { pages.Add(address); return true; }, fixture.Links);

        Assert.AreEqual("ok", (await service.OpenAsync(new(Epoch, "https://example.invalid/docs"), default)).Status);
        Assert.AreEqual("ok", (await service.OpenAsync(new(Epoch, "src/Program.cs#L3", "session"), default)).Status);
        Assert.AreEqual("ok", (await service.OpenAsync(new(Epoch, "Program.cs", ProjectId: fixture.ProjectId, Directory: "src"), default)).Status);
        Assert.AreEqual("not_found", (await service.OpenAsync(new(Epoch, "src/Missing.cs", "session"), default)).Status);
        CollectionAssert.AreEqual(new[] { "https://example.invalid/docs" }, pages);
        CollectionAssert.AreEqual(new[] { new ProjectFileShowEvent(fixture.ProjectId, "src/Program.cs", 3, null), new ProjectFileShowEvent(fixture.ProjectId, "src/Program.cs", null, null) }, fixture.Shown);

        fixture.Shown.Clear();
        foreach (var address in new[] { "//server/share/Program.cs", "file://server/share/Program.cs", "mailto:someone@example.invalid", "javascript:alert(1)", "#L3", "https://user:secret@example.invalid/" })
            Assert.AreEqual("invalid_request", (await service.OpenAsync(new(Epoch, address, "session"), default)).Status, address);
        Assert.AreEqual("stale_epoch", (await service.OpenAsync(new("4b0a7c1e-52c9-4f0b-8a55-6d0c2f9e1b77", "src/Program.cs", "session"), default)).Status);
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.OpenAsync(new(Epoch, "src/Program.cs", "session"), new CancellationToken(true)));
        Assert.IsEmpty(fixture.Shown);
        Assert.AreEqual(1, pages.Count);

        // No window is there to show the file.
        fixture.Watching.Dispose();
        Assert.AreEqual("failed", (await service.OpenAsync(new(Epoch, "src/Program.cs", "session"), default)).Status);
    }

    [TestMethod]
    public async Task APictureOfALink_IsReadForTheWindow_ByItsFullOrRelativePath()
    {
        using var fixture = await Fixture.CreateAsync();
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01, 0x02];
        var file = fixture.Write(fixture.ProjectPath, "out/chart.png", png);
        var service = new MarkdownLinksService(Epoch, _ => true, fixture.Links);

        var relative = await service.ImageAsync(new(Epoch, "out/chart.png", "session"), default);
        Assert.AreEqual(("ok", Epoch, "image/png", Convert.ToBase64String(png), file), (relative.Status, relative.HostEpoch, relative.MediaType, relative.Base64, relative.FullPath));
        var full = await service.ImageAsync(new(Epoch, file.Replace('\\', '/')), default);
        Assert.AreEqual(("ok", file), (full.Status, full.FullPath));
        Assert.AreEqual("ok", (await service.ImageAsync(new(Epoch, "chart.png", ProjectId: fixture.ProjectId, Directory: "out"), default)).Status);
        // Reading a picture opens nothing.
        Assert.IsEmpty(fixture.Shown);
    }

    [TestMethod]
    public async Task AFileThatIsNoPictureToDraw_IsNotRead_AndOnlyItsStatusIsAnswered()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Write(fixture.ProjectPath, "notes.txt", "some notes");
        fixture.Write(fixture.ProjectPath, "fake.png", "not a picture");
        fixture.Write(fixture.ProjectPath, "icon.ico", [0x00, 0x00, 0x01, 0x00, 0x01]);
        fixture.Write(fixture.ProjectPath, "folder.png/inside.txt", "a folder named as a picture");
        // A JPEG named as a PNG is still a picture: its content decides its type.
        fixture.Write(fixture.ProjectPath, "photo.png", [0xFF, 0xD8, 0xFF, 0xE0]);
        var huge = fixture.Write(fixture.ProjectPath, "huge.bmp", [0x42, 0x4D]);
        using (var stream = File.OpenWrite(huge)) stream.SetLength(ProjectFilesService.MaximumImageBytes + 1L);
        var service = new MarkdownLinksService(Epoch, _ => true, fixture.Links);

        foreach (var (address, status) in new[] { ("notes.txt", "not_image"), ("fake.png", "not_image"), ("icon.ico", "not_image"), ("huge.bmp", "too_large"),
            ("missing.png", "not_found"), ("folder.png", "not_found") })
        {
            var reply = await service.ImageAsync(new(Epoch, address, "session"), default);
            Assert.AreEqual((status, (string?)null, (string?)null, (string?)null), (reply.Status, reply.MediaType, reply.Base64, reply.FullPath), address);
        }

        Assert.AreEqual("image/jpeg", (await service.ImageAsync(new(Epoch, "photo.png", "session"), default)).MediaType);
        Assert.AreEqual("not_found", (await service.ImageAsync(new(Epoch, "photo.png"), default)).Status);
        Assert.AreEqual("invalid_request", (await service.ImageAsync(new(Epoch, "https://example.invalid/a.png", "session"), default)).Status);
        Assert.AreEqual("invalid_request", (await service.ImageAsync(new(Epoch, "//server/share/a.png", "session"), default)).Status);
        Assert.AreEqual("stale_epoch", (await service.ImageAsync(new("4b0a7c1e-52c9-4f0b-8a55-6d0c2f9e1b77", "photo.png", "session"), default)).Status);
        Assert.AreEqual("invalid_request", (await new MarkdownLinksService(Epoch, _ => true).ImageAsync(new(Epoch, "photo.png", "session"), default)).Status);
    }

    [TestMethod]
    public async Task TheFileOfALink_IsShownInTheFileManager_WhenItIsThere()
    {
        using var fixture = await Fixture.CreateAsync();
        var file = fixture.Write(fixture.ProjectPath, "out/chart.png", [0x89, 0x50, 0x4E, 0x47]);
        var revealed = new List<string>();
        var reveals = true;
        var service = new MarkdownLinksService(Epoch, _ => true, new DesktopFileLinks(fixture.Projects, new DiskFolders(), new DesktopEditorView(),
            (sessionId, _) => ValueTask.FromResult(fixture.SessionFolders.GetValueOrDefault(sessionId)), fixture.Files.RootAsync, _ => true, fixture.Root)
        {
            Reveal = path => { revealed.Add(path); return reveals; },
        });

        Assert.AreEqual("ok", (await service.RevealAsync(new(Epoch, "out/chart.png", "session"), default)).Status);
        CollectionAssert.AreEqual(new[] { file }, revealed);
        Assert.AreEqual("not_found", (await service.RevealAsync(new(Epoch, "out/missing.png", "session"), default)).Status);
        Assert.AreEqual("not_found", (await service.RevealAsync(new(Epoch, "out/chart.png"), default)).Status);
        Assert.AreEqual("invalid_request", (await service.RevealAsync(new(Epoch, "mailto:someone@example.invalid", "session"), default)).Status);
        Assert.AreEqual("stale_epoch", (await service.RevealAsync(new("4b0a7c1e-52c9-4f0b-8a55-6d0c2f9e1b77", "out/chart.png", "session"), default)).Status);
        Assert.AreEqual(1, revealed.Count);

        reveals = false;
        Assert.AreEqual("failed", (await service.RevealAsync(new(Epoch, "out/chart.png", "session"), default)).Status);
    }

    [TestMethod]
    public void AFolderOfTheDisk_KeepsItsId_AndTheOldestIsForgotten()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "CodeAlta-disk-folders-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var folders = new DiskFolders();
            var first = folders.Give(root);
            Assert.AreEqual(first, folders.Give(root + Separator));
            Assert.AreEqual((Path.GetFileName(root), root), (first.Name, first.Root));
            Assert.IsTrue(DiskFolders.IsId(first.Id) && !DiskFolders.IsId("0196c1f0-0000-7000-8000-000000000000") && !DiskFolders.IsId(null));
            Assert.AreEqual(("ok", root), folders.Resolve(first.Id));
            Assert.AreEqual(("unknown_project", (string?)null), folders.Resolve(DiskFolders.Prefix + "unknown"));
            Assert.ThrowsExactly<ArgumentException>(() => folders.Give("relative"));

            var gone = folders.Give(Path.Combine(root, "gone"));
            Assert.AreEqual(("project_unavailable", (string?)null), folders.Resolve(gone.Id));
            for (var index = 0; index < DiskFolders.MaximumFolders - 1; index++) folders.Give(Path.Combine(root, "folder-" + index));
            Assert.AreEqual(("unknown_project", (string?)null), folders.Resolve(first.Id));
            Assert.AreEqual("project_unavailable", folders.Resolve(gone.Id).Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private Fixture(string root, ProjectCatalog projects, ProjectDescriptor project)
        {
            Root = root;
            Projects = projects;
            Project = project;
            var folders = new DiskFolders();
            var view = new DesktopEditorView();
            Watching = view.Watch(Shown.Add);
            Files = new ProjectFilesService(projects, Epoch, view: view, folders: folders);
            SessionFolders["session"] = project.ProjectPath;
            Links = new DesktopFileLinks(projects, folders, view,
                (sessionId, _) => ValueTask.FromResult(SessionFolders.GetValueOrDefault(sessionId)), Files.RootAsync,
                path => { if (SystemOpens) Documents.Add(path); return SystemOpens; }, Path.Combine(root, "home"));
        }

        public static async Task<Fixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "CodeAlta-file-links-" + Guid.NewGuid().ToString("N"));
            var projects = new ProjectCatalog(new CatalogOptions { GlobalRoot = Directory.CreateDirectory(Path.Combine(root, "global")).FullName });
            var project = await projects.UpsertFromPathAsync(Directory.CreateDirectory(Path.Combine(root, "project")).FullName);
            return new Fixture(root, projects, project);
        }

        public string Root { get; }
        public ProjectCatalog Projects { get; }
        public ProjectDescriptor Project { get; }
        public string ProjectId => Project.Id;
        public string ProjectPath => Project.ProjectPath;
        public ProjectFilesService Files { get; }
        public DesktopFileLinks Links { get; }
        public IDisposable Watching { get; }
        public Dictionary<string, string> SessionFolders { get; } = new(StringComparer.Ordinal);
        public List<ProjectFileShowEvent> Shown { get; } = [];
        public List<string> Documents { get; } = [];
        public bool SystemOpens { get; set; } = true;

        public async Task<string> OpenAsync(string target, string? session = null, string? project = null, string? directory = null)
        {
            Assert.IsTrue(DesktopFileLink.TryParse(target, out var link), target);
            return await Links.OpenAsync(link, session, project, directory, default);
        }

        public string Write(string folder, string relative, string text) => Write(folder, relative, System.Text.Encoding.UTF8.GetBytes(text));

        public string Write(string folder, string relative, byte[] bytes)
        {
            var path = Path.Combine(folder, relative.Replace('/', Separator));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup of a temporary directory.
            }
        }
    }
}
