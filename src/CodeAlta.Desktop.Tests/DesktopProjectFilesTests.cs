using System.Security.Cryptography;
using System.Text;
using CodeAlta.Catalog;
using CodeAlta.Desktop.Rpc;

namespace CodeAlta.Desktop.Tests;

/// <summary>Real temporary folders and a real project catalog; only the recent-files sink is a literal.</summary>
[TestClass]
public sealed class DesktopProjectFilesTests
{
    private const string Epoch = "epoch-1";
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    [TestMethod]
    public async Task WithoutAnOwnedHost_EveryOperationIsUnavailable_AndAnotherEpochIsRefused()
    {
        var unavailable = new ProjectFilesService();
        Assert.AreEqual("unavailable", (await unavailable.ReadAsync(new(Epoch, "project", "a.txt"), default)).Status);
        Assert.AreEqual("unavailable", (await unavailable.WriteAsync(new(Epoch, "project", "a.txt", "text", null, true), default)).Status);

        using var fixture = await Fixture.CreateAsync();
        var path = fixture.Write("a.txt", "original");
        Assert.AreEqual("stale_epoch", (await fixture.Service.ReadAsync(new("another", fixture.ProjectId, "a.txt"), default)).Status);
        Assert.AreEqual("stale_epoch", (await fixture.Service.ReadAsync(new(null, fixture.ProjectId, "a.txt"), default)).Status);
        Assert.AreEqual("stale_epoch", (await fixture.Service.WriteAsync(new("another", fixture.ProjectId, "a.txt", "changed", null, true), default)).Status);
        Assert.AreEqual("original", File.ReadAllText(path));
        Assert.IsEmpty(fixture.Usage.Events);
    }

    [TestMethod]
    public async Task Read_ReturnsTheTextItsRevisionAndRecordsARecentFile()
    {
        using var fixture = await Fixture.CreateAsync();
        var path = fixture.Write("src/notes.txt", "first\r\nsecond\nthird é");

        var response = await fixture.Service.ReadAsync(new(Epoch, fixture.ProjectId, "src\\notes.txt"), default);

        Assert.AreEqual("ok", response.Status);
        Assert.AreEqual("src/notes.txt", response.Path);
        Assert.AreEqual("first\r\nsecond\nthird é", response.Content);
        Assert.AreEqual(Revision(path), response.Revision);
        Assert.AreEqual(new FileInfo(path).Length, response.Length);
        Assert.IsFalse(response.ReadOnly);
        var usage = fixture.Usage.Events.Single();
        Assert.AreEqual("src/notes.txt", usage.RelativePath);
        Assert.AreEqual(ProjectFileSearchItemKind.File, usage.Kind);
        Assert.AreEqual(ProjectFileUsageAccessKind.EditorOpened, usage.AccessKind);
        Assert.AreEqual(Path.GetFullPath(fixture.ProjectPath), Path.GetFullPath(usage.ProjectRoot));

        // A failing recent-files store never fails the read, and no store is needed at all.
        fixture.Usage.Fail = true;
        Assert.AreEqual("ok", (await fixture.Service.ReadAsync(new(Epoch, fixture.ProjectId, "src/notes.txt"), default)).Status);
        var plain = new ProjectFilesService(fixture.Projects, Epoch);
        Assert.AreEqual("ok", (await plain.ReadAsync(new(Epoch, fixture.ProjectId, "src/notes.txt"), default)).Status);
    }

    [TestMethod]
    public async Task Read_DecodesUtf16WithABom()
    {
        using var fixture = await Fixture.CreateAsync();
        var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("wide\r\ntext")).ToArray();
        fixture.Write("wide.txt", bytes);

        var response = await fixture.Service.ReadAsync(new(Epoch, fixture.ProjectId, "wide.txt"), default);

        Assert.AreEqual("ok", response.Status);
        Assert.AreEqual("wide\r\ntext", response.Content);
        Assert.AreEqual(bytes.Length, response.Length);
    }

    [TestMethod]
    public async Task PathsThatLeaveTheProject_AreRefusedForReadAndWrite()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Write("inside.txt", "inside");
        var secret = Path.Combine(fixture.Root, "secret.txt");
        File.WriteAllText(secret, "secret");
        var outside = new[]
        {
            "../secret.txt", "..\\secret.txt", "src/../../secret.txt", "src/..", secret, secret.Replace('\\', '/'), "/secret.txt", "\\secret.txt",
            "\\\\server\\share\\secret.txt", "C:secret.txt", "C:\\secret.txt", "inside.txt:stream", "inside.txt::$DATA",
        };
        foreach (var path in outside)
        {
            var read = await fixture.Service.ReadAsync(new(Epoch, fixture.ProjectId, path), default);
            Assert.AreEqual("outside_root", read.Status, path);
            Assert.IsNull(read.Content, path);
            Assert.IsNull(read.Path, path);
            Assert.AreEqual("outside_root", (await fixture.Service.WriteAsync(new(Epoch, fixture.ProjectId, path, "changed", null, true), default)).Status, path);
        }

        var invalid = new string?[]
        {
            null, "", " ", "src//inside.txt", "./inside.txt", "src/./inside.txt", "src/", "inside.txt.", "inside.txt ", " inside.txt",
            "in\nside.txt", "in\0side.txt", new string('a', ProjectFilesService.MaximumPathLength + 1),
        };
        foreach (var path in invalid)
        {
            Assert.AreEqual("invalid", (await fixture.Service.ReadAsync(new(Epoch, fixture.ProjectId, path), default)).Status, path);
            Assert.AreEqual("invalid", (await fixture.Service.WriteAsync(new(Epoch, fixture.ProjectId, path, "changed", null, true), default)).Status, path);
        }

        if (OperatingSystem.IsWindows())
        {
            // A device name resolves outside every folder.
            Assert.AreEqual("outside_root", (await fixture.Service.ReadAsync(new(Epoch, fixture.ProjectId, "NUL"), default)).Status);
            Assert.AreEqual("invalid", (await fixture.Service.ReadAsync(new(Epoch, fixture.ProjectId, "in*.txt"), default)).Status);
        }

        Assert.AreEqual("secret", File.ReadAllText(secret));
        Assert.AreEqual("inside", File.ReadAllText(Path.Combine(fixture.ProjectPath, "inside.txt")));
        Assert.IsEmpty(fixture.Usage.Events);
    }

    [TestMethod]
    public async Task LinksInsideTheProject_AreNotFollowed()
    {
        using var fixture = await Fixture.CreateAsync();
        var elsewhere = Directory.CreateDirectory(Path.Combine(fixture.Root, "elsewhere")).FullName;
        var secret = Path.Combine(elsewhere, "secret.txt");
        File.WriteAllText(secret, "secret");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(fixture.ProjectPath, "linked"), elsewhere);
            File.CreateSymbolicLink(Path.Combine(fixture.ProjectPath, "alias.txt"), secret);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Assert.Inconclusive("Creating symbolic links is not permitted on this machine.");
        }

        foreach (var path in new[] { "linked/secret.txt", "alias.txt" })
        {
            var read = await fixture.Service.ReadAsync(new(Epoch, fixture.ProjectId, path), default);
            Assert.AreEqual("outside_root", read.Status, path);
            Assert.IsNull(read.Content, path);
            Assert.AreEqual("outside_root", (await fixture.Service.WriteAsync(new(Epoch, fixture.ProjectId, path, "changed", null, true), default)).Status, path);
        }

        Assert.AreEqual("secret", File.ReadAllText(secret));
    }

    [TestMethod]
    public async Task Read_RefusesLargeBinaryAndMissingFiles()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Write("limit.txt", Enumerable.Repeat((byte)'a', ProjectFilesService.MaximumFileBytes).ToArray());
        fixture.Write("large.txt", Enumerable.Repeat((byte)'a', ProjectFilesService.MaximumFileBytes + 1).ToArray());
        fixture.Write("latin1.txt", [(byte)'a', 0xC3, 0x28]);
        fixture.Write("nul.bin", [(byte)'a', 0, (byte)'b']);
        fixture.Write("folder/file.txt", "text");

        var limit = await fixture.Service.ReadAsync(new(Epoch, fixture.ProjectId, "limit.txt"), default);
        Assert.AreEqual("ok", limit.Status);
        Assert.AreEqual(ProjectFilesService.MaximumFileBytes, limit.Content!.Length);

        var large = await fixture.Service.ReadAsync(new(Epoch, fixture.ProjectId, "large.txt"), default);
        Assert.AreEqual("too_large", large.Status);
        Assert.AreEqual("large.txt", large.Path);
        Assert.AreEqual(ProjectFilesService.MaximumFileBytes + 1, large.Length);
        Assert.IsNull(large.Content);
        Assert.IsNull(large.Revision);

        Assert.AreEqual("binary", (await fixture.Service.ReadAsync(new(Epoch, fixture.ProjectId, "latin1.txt"), default)).Status);
        Assert.AreEqual("binary", (await fixture.Service.ReadAsync(new(Epoch, fixture.ProjectId, "nul.bin"), default)).Status);
        foreach (var missing in new[] { "missing.txt", "missing/file.txt", "folder", "folder/file.txt/more" })
            Assert.AreEqual("not_found", (await fixture.Service.ReadAsync(new(Epoch, fixture.ProjectId, missing), default)).Status, missing);
    }

    [TestMethod]
    public async Task Write_KeepsTheBomAndTheNewlinesAsGiven()
    {
        using var fixture = await Fixture.CreateAsync();
        var path = fixture.Write("doc.md", [.. Utf8Bom, .. "one\r\ntwo\r\n"u8]);
        var read = await fixture.Service.ReadAsync(new(Epoch, fixture.ProjectId, "doc.md"), default);
        Assert.AreEqual("one\r\ntwo\r\n", read.Content);

        const string Changed = "one\r\nTWO\nthree é";
        var written = await fixture.Service.WriteAsync(new(Epoch, fixture.ProjectId, "doc.md", Changed, read.Revision, false), default);

        Assert.AreEqual("ok", written.Status);
        Assert.AreEqual("doc.md", written.Path);
        CollectionAssert.AreEqual(Utf8Bom.Concat(Encoding.UTF8.GetBytes(Changed)).ToArray(), File.ReadAllBytes(path));
        Assert.AreEqual(Revision(path), written.Revision);
        Assert.AreNotEqual(read.Revision, written.Revision);
        Assert.AreEqual(written.Revision, (await fixture.Service.ReadAsync(new(Epoch, fixture.ProjectId, "doc.md"), default)).Revision);
        Assert.IsEmpty(Directory.GetFiles(fixture.ProjectPath, "*.tmp"), "The staging file is gone.");

        // A file without a BOM stays without one, and UTF-16 stays UTF-16.
        var bare = fixture.Write("bare.txt", "bare\n");
        var bareRead = await fixture.Service.ReadAsync(new(Epoch, fixture.ProjectId, "bare.txt"), default);
        Assert.AreEqual("ok", (await fixture.Service.WriteAsync(new(Epoch, fixture.ProjectId, "bare.txt", "bare é\r\n", bareRead.Revision, false), default)).Status);
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("bare é\r\n"), File.ReadAllBytes(bare));
        var wide = fixture.Write("wide.txt", [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("wide")]);
        var wideRead = await fixture.Service.ReadAsync(new(Epoch, fixture.ProjectId, "wide.txt"), default);
        Assert.AreEqual("ok", (await fixture.Service.WriteAsync(new(Epoch, fixture.ProjectId, "wide.txt", "wider\r\n", wideRead.Revision, false), default)).Status);
        CollectionAssert.AreEqual(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("wider\r\n")).ToArray(), File.ReadAllBytes(wide));
    }

    [TestMethod]
    public async Task Write_ReportsAConflictWithTheCurrentRevision_AndOverwritesOnlyWhenAsked()
    {
        using var fixture = await Fixture.CreateAsync();
        var path = fixture.Write("a.txt", "read");
        var read = await fixture.Service.ReadAsync(new(Epoch, fixture.ProjectId, "a.txt"), default);
        File.WriteAllText(path, "changed elsewhere");

        var conflict = await fixture.Service.WriteAsync(new(Epoch, fixture.ProjectId, "a.txt", "mine", read.Revision, false), default);

        Assert.AreEqual("conflict", conflict.Status);
        Assert.AreEqual(Revision(path), conflict.Revision);
        Assert.AreNotEqual(read.Revision, conflict.Revision);
        Assert.AreEqual("changed elsewhere", File.ReadAllText(path));
        // A write that names no revision is not an overwrite.
        Assert.AreEqual("conflict", (await fixture.Service.WriteAsync(new(Epoch, fixture.ProjectId, "a.txt", "mine", null, false), default)).Status);
        Assert.AreEqual("changed elsewhere", File.ReadAllText(path));

        var overwritten = await fixture.Service.WriteAsync(new(Epoch, fixture.ProjectId, "a.txt", "mine", read.Revision, true), default);
        Assert.AreEqual("ok", overwritten.Status);
        Assert.AreEqual("mine", File.ReadAllText(path));
        Assert.AreEqual(Revision(path), overwritten.Revision);

        // The revision of the conflict is the one a retry can name.
        File.WriteAllText(path, "changed again");
        var again = await fixture.Service.WriteAsync(new(Epoch, fixture.ProjectId, "a.txt", "mine too", overwritten.Revision, false), default);
        Assert.AreEqual("conflict", again.Status);
        Assert.AreEqual("ok", (await fixture.Service.WriteAsync(new(Epoch, fixture.ProjectId, "a.txt", "mine too", again.Revision, false), default)).Status);
        Assert.AreEqual("mine too", File.ReadAllText(path));
    }

    [TestMethod]
    public async Task Write_RefusesReadOnlyMissingLargeAndBinaryFiles()
    {
        using var fixture = await Fixture.CreateAsync();
        var locked = fixture.Write("locked.txt", "locked");
        File.SetAttributes(locked, File.GetAttributes(locked) | FileAttributes.ReadOnly);
        var read = await fixture.Service.ReadAsync(new(Epoch, fixture.ProjectId, "locked.txt"), default);
        Assert.AreEqual("ok", read.Status);
        Assert.IsTrue(read.ReadOnly);
        foreach (var overwrite in new[] { false, true })
        {
            var refused = await fixture.Service.WriteAsync(new(Epoch, fixture.ProjectId, "locked.txt", "changed", read.Revision, overwrite), default);
            Assert.AreEqual("read_only", refused.Status);
            Assert.AreEqual(read.Revision, refused.Revision);
        }

        Assert.AreEqual("locked", File.ReadAllText(locked));

        // The service edits files: it creates neither a file nor a folder.
        Assert.AreEqual("not_found", (await fixture.Service.WriteAsync(new(Epoch, fixture.ProjectId, "new.txt", "text", null, true), default)).Status);
        Assert.AreEqual("not_found", (await fixture.Service.WriteAsync(new(Epoch, fixture.ProjectId, "new/file.txt", "text", null, true), default)).Status);
        Assert.IsFalse(File.Exists(Path.Combine(fixture.ProjectPath, "new.txt")));
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.ProjectPath, "new")));

        var small = fixture.Write("small.txt", "small");
        var smallRead = await fixture.Service.ReadAsync(new(Epoch, fixture.ProjectId, "small.txt"), default);
        var tooManyUnits = new string('a', ProjectFilesService.MaximumFileBytes + 1);
        Assert.AreEqual("too_large", (await fixture.Service.WriteAsync(new(Epoch, fixture.ProjectId, "small.txt", tooManyUnits, smallRead.Revision, false), default)).Status);
        // Within the limit in UTF-16 units, beyond it once encoded as UTF-8.
        var tooManyBytes = new string('é', ProjectFilesService.MaximumFileBytes / 2 + 1);
        Assert.AreEqual("too_large", (await fixture.Service.WriteAsync(new(Epoch, fixture.ProjectId, "small.txt", tooManyBytes, smallRead.Revision, false), default)).Status);
        Assert.AreEqual("invalid", (await fixture.Service.WriteAsync(new(Epoch, fixture.ProjectId, "small.txt", "lone \ud800 surrogate", smallRead.Revision, false), default)).Status);
        Assert.AreEqual("invalid", (await fixture.Service.WriteAsync(new(Epoch, fixture.ProjectId, "small.txt", null, smallRead.Revision, false), default)).Status);
        Assert.AreEqual("small", File.ReadAllText(small));

        var binary = fixture.Write("image.bin", [(byte)'a', 0, 0xFF]);
        Assert.AreEqual("binary", (await fixture.Service.WriteAsync(new(Epoch, fixture.ProjectId, "image.bin", "text", null, true), default)).Status);
        CollectionAssert.AreEqual(new byte[] { (byte)'a', 0, 0xFF }, File.ReadAllBytes(binary));
    }

    [TestMethod]
    public async Task ProjectsThatCannotBeEdited_AreRefused()
    {
        using var fixture = await Fixture.CreateAsync();
        var path = fixture.Write("a.txt", "original");
        var read = await fixture.Service.ReadAsync(new(Epoch, fixture.ProjectId, "a.txt"), default);
        var write = new ProjectFileWriteRequest(Epoch, fixture.ProjectId, "a.txt", "changed", read.Revision, false);

        Assert.AreEqual("invalid", (await fixture.Service.ReadAsync(new(Epoch, null, "a.txt"), default)).Status);
        Assert.AreEqual("invalid", (await fixture.Service.WriteAsync(write with { ProjectId = null }, default)).Status);
        var unknown = Guid.NewGuid().ToString("D");
        Assert.AreEqual("unknown_project", (await fixture.Service.ReadAsync(new(Epoch, unknown, "a.txt"), default)).Status);
        Assert.AreEqual("unknown_project", (await fixture.Service.WriteAsync(write with { ProjectId = unknown }, default)).Status);
        Assert.AreEqual("unknown_project", (await fixture.Service.ReadAsync(new(Epoch, fixture.ProjectPath, "a.txt"), default)).Status, "A path is not a project id.");

        fixture.Project.Archived = true;
        await fixture.Projects.SaveAsync(fixture.Project);
        Assert.AreEqual("archived_project", (await fixture.Service.WriteAsync(write, default)).Status);
        Assert.AreEqual("archived_project", (await fixture.Service.WriteAsync(write with { Overwrite = true }, default)).Status);
        Assert.AreEqual("archived_project", (await fixture.Service.ReadAsync(new(Epoch, fixture.ProjectId, "a.txt"), default)).Status);
        Assert.AreEqual("original", File.ReadAllText(path));

        fixture.Project.Archived = false;
        await fixture.Projects.SaveAsync(fixture.Project);
        Assert.AreEqual("ok", (await fixture.Service.WriteAsync(write, default)).Status);
        Directory.Delete(fixture.ProjectPath, recursive: true);
        Assert.AreEqual("project_unavailable", (await fixture.Service.ReadAsync(new(Epoch, fixture.ProjectId, "a.txt"), default)).Status);
        Assert.AreEqual("project_unavailable", (await fixture.Service.WriteAsync(write, default)).Status);
    }

    private static string Revision(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private sealed class RecentFiles : IProjectFileSearchService
    {
        public List<ProjectFileUsageEvent> Events { get; } = [];
        public bool Fail { get; set; }

        public ValueTask RecordUsageAsync(ProjectFileUsageEvent usageEvent, CancellationToken cancellationToken = default)
        {
            if (Fail) throw new IOException("The usage store is not writable.");
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
            Projects = projects;
            Project = project;
            Service = new ProjectFilesService(projects, Epoch, Usage);
        }

        public static async Task<Fixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "CodeAlta-project-files-" + Guid.NewGuid().ToString("N"));
            var projects = new ProjectCatalog(new CatalogOptions { GlobalRoot = Directory.CreateDirectory(Path.Combine(root, "global")).FullName });
            var project = await projects.UpsertFromPathAsync(Directory.CreateDirectory(Path.Combine(root, "project")).FullName);
            return new Fixture(root, projects, project);
        }

        public string Root { get; }
        public ProjectCatalog Projects { get; }
        public ProjectDescriptor Project { get; }
        public RecentFiles Usage { get; } = new();
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
