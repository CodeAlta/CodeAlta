using System.Text;

namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed class ProjectCatalogBoundedOwnershipTests
{
    private const string Id = "01963b36-0d70-7a11-b3c2-1f2e3d4c5b6a";
    private const string OtherId = "01963b36-0d70-7a11-b3c2-1f2e3d4c5b6b";

    [TestMethod]
    public async Task FlatAndLegacyMustAgree_FlatArchivedValueTakesPrecedence()
    {
        using var fixture = new Fixture();
        fixture.Write("fixture", Id, fixture.Workspace, legacy: true, archived: false, Encoding.Unicode);
        Assert.AreEqual(ProjectOwnershipStatus.Match,
            (await fixture.ReadAsync(Id, fixture.Workspace)).Status);
        fixture.Write("fixture", Id, fixture.Workspace, legacy: false, archived: true, new UTF8Encoding(true));
        var result = await fixture.ReadAsync(Id, fixture.Workspace);
        Assert.AreEqual(ProjectOwnershipStatus.Match, result.Status);
        Assert.AreEqual(true, result.Archived);
        Assert.IsTrue(result.Complete);
        Assert.AreEqual(ProjectOwnershipStatus.Missing, (await fixture.ReadAsync(OtherId, fixture.Workspace)).Status);
        Assert.AreEqual(ProjectOwnershipStatus.Missing, (await fixture.ReadAsync(Id, fixture.OtherWorkspace)).Status);
        fixture.Write("fixture", OtherId, fixture.Workspace, legacy: false);
        Assert.AreEqual(ProjectOwnershipStatus.Ambiguous, (await fixture.ReadAsync(Id, fixture.Workspace)).Status);
    }

    [TestMethod]
    public async Task ConflictingIdentityOrPathAndLateDuplicatesCannotCertifyOwnership()
    {
        using var fixture = new Fixture();
        fixture.Write("alpha", Id, fixture.Workspace);
        fixture.Write("bravo", Id, fixture.OtherWorkspace);
        Assert.AreEqual(ProjectOwnershipStatus.Ambiguous, (await fixture.ReadAsync(Id, fixture.Workspace)).Status);
        fixture.Write("bravo", OtherId, fixture.Workspace);
        Assert.AreEqual(ProjectOwnershipStatus.Ambiguous, (await fixture.ReadAsync(Id, fixture.Workspace)).Status);
        fixture.Write("bravo", OtherId, fixture.OtherWorkspace);
        Assert.AreEqual(ProjectOwnershipStatus.Match, (await fixture.ReadAsync(Id, fixture.Workspace)).Status);
        Assert.AreEqual(ProjectOwnershipStatus.Missing, (await fixture.ReadAsync(Id, fixture.OtherWorkspace)).Status);
    }

    [TestMethod]
    public async Task UnknownRowsAndBudgetsNeverCertifyEarlyMatchOrMissing()
    {
        using var fixture = new Fixture();
        fixture.Write("alpha", Id, fixture.Workspace);
        fixture.Write("bravo", OtherId, fixture.OtherWorkspace, body: new string('x', ProjectCatalog.MaximumOwnershipFileBytes));
        Assert.AreEqual(ProjectOwnershipStatus.Incomplete, (await fixture.ReadAsync(Id, fixture.Workspace)).Status);
        File.Delete(Path.Combine(fixture.Projects, "bravo.md"));
        for (var i = 0; i < ProjectCatalog.MaximumOwnershipEntries + 1; i++)
            Directory.CreateDirectory(Path.Combine(fixture.Projects, $"unrelated-{i}"));
        var count = await fixture.ReadAsync(Id, fixture.Workspace);
        Assert.AreEqual(ProjectOwnershipStatus.Incomplete, count.Status);
        Assert.AreEqual(ProjectCatalog.MaximumOwnershipEntries + 1, count.EntriesVisited);
        Assert.AreEqual(ProjectOwnershipStatus.Incomplete, (await fixture.ReadAsync(OtherId, fixture.Workspace)).Status);
    }

    [TestMethod]
    public async Task AggregateActualReadAndInvalidMetadataFailClosed()
    {
        using var fixture = new Fixture();
        fixture.Write("alpha", Id, fixture.Workspace);
        for (var i = 0; i < 20; i++)
            fixture.Write($"extra-{i}", Guid.NewGuid().ToString("D"), fixture.OtherWorkspace + "-" + i,
                body: new string('x', ProjectCatalog.MaximumOwnershipFileBytes - 512));
        var budget = await fixture.ReadAsync(Id, fixture.Workspace);
        Assert.AreEqual(ProjectOwnershipStatus.Incomplete, budget.Status);
        Assert.IsTrue(budget.BytesRead <= ProjectCatalog.MaximumOwnershipTotalBytes);
        foreach (var file in Directory.EnumerateFiles(fixture.Projects, "extra-*.md")) File.Delete(file);
        fixture.Write("bad", "not-a-project-id", fixture.OtherWorkspace);
        Assert.AreEqual(ProjectOwnershipStatus.Invalid, (await fixture.ReadAsync(Id, fixture.Workspace)).Status);
        File.Delete(Path.Combine(fixture.Projects, "bad.md"));
        fixture.Write("bad", OtherId, fixture.OtherWorkspace, body: "", overrideText: "---\nid: [broken\n---");
        Assert.AreEqual(ProjectOwnershipStatus.Invalid, (await fixture.ReadAsync(Id, fixture.Workspace)).Status);
        fixture.Write("bad", OtherId, fixture.OtherWorkspace, overrideText: "not a project descriptor");
        Assert.AreEqual(ProjectOwnershipStatus.Invalid, (await fixture.ReadAsync(Id, fixture.Workspace)).Status);
    }

    [TestMethod]
    public async Task LockedSourceAndMalformedEncodingCannotCertifyOwnership()
    {
        using var fixture = new Fixture();
        fixture.Write("alpha", Id, fixture.Workspace);
        var source = Path.Combine(fixture.Projects, "alpha.md");
        using (var locked = new FileStream(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.AreEqual(ProjectOwnershipStatus.ReadError, (await fixture.ReadAsync(Id, fixture.Workspace)).Status);

        await File.WriteAllBytesAsync(source, [0xef, 0xbb, 0xbf, 0xff, 0xff]);
        Assert.AreEqual(ProjectOwnershipStatus.Invalid, (await fixture.ReadAsync(Id, fixture.Workspace)).Status);
    }

    [TestMethod]
    public async Task RelevantButUnexpectedSourceNamingFailsClosed()
    {
        using var fixture = new Fixture();
        fixture.Write("alpha", Id, fixture.Workspace);
        fixture.Write("bravo", OtherId, fixture.OtherWorkspace);
        File.Move(Path.Combine(fixture.Projects, "bravo.md"), Path.Combine(fixture.Projects, "renamed.md"));
        Assert.AreEqual(ProjectOwnershipStatus.Invalid, (await fixture.ReadAsync(Id, fixture.Workspace)).Status);
    }

    [TestMethod]
    public async Task SameSlugDifferentIdentityAcrossFlatAndLegacyIsAmbiguous()
    {
        using var fixture = new Fixture();
        fixture.Write("alpha", Id, fixture.Workspace);
        fixture.Write("alpha", OtherId, fixture.OtherWorkspace, legacy: true);
        Assert.AreEqual(ProjectOwnershipStatus.Ambiguous, (await fixture.ReadAsync(Id, fixture.Workspace)).Status);
    }

    [TestMethod]
    public async Task MissingRootInputsCancellationAndLinkedFilesFailClosed()
    {
        using var fixture = new Fixture(createProjects: false);
        Assert.AreEqual(ProjectOwnershipStatus.Missing, (await fixture.ReadAsync(Id, fixture.Workspace)).Status);
        Assert.AreEqual(ProjectOwnershipStatus.Invalid, (await fixture.ReadAsync("bad", fixture.Workspace)).Status);
        Assert.AreEqual(ProjectOwnershipStatus.Invalid, (await fixture.ReadAsync(Id, "relative/path")).Status);
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Catalog.ReadBoundedOwnershipAsync(Id, fixture.Workspace, new CancellationToken(true)));
        Directory.CreateDirectory(fixture.Projects);
        fixture.Write("real", Id, fixture.Workspace);
        try
        {
            File.CreateSymbolicLink(Path.Combine(fixture.Projects, "linked.md"), Path.Combine(fixture.Projects, "real.md"));
            Assert.AreEqual(ProjectOwnershipStatus.ReadError, (await fixture.ReadAsync(Id, fixture.Workspace)).Status);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
        {
            // Some hosts disallow symlink creation; ordinary bounded fixtures above remain authoritative.
        }
        var linkedRoot = fixture.Root + "-linked";
        try
        {
            Directory.CreateSymbolicLink(linkedRoot, fixture.Root);
            var linkedCatalog = new ProjectCatalog(new CatalogOptions { GlobalRoot = linkedRoot });
            Assert.AreEqual(ProjectOwnershipStatus.ReadError,
                (await linkedCatalog.ReadBoundedOwnershipAsync(Id, fixture.Workspace)).Status);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
        {
            // Some hosts disallow directory symlinks.
        }
        finally
        {
            if (Directory.Exists(linkedRoot)) Directory.Delete(linkedRoot);
        }
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "codealta-bounded-ownership-" + Guid.NewGuid().ToString("N"));
        public string Projects => Path.Combine(Root, "projects");
        public string Workspace => Path.Combine(Root, "workspace");
        public string OtherWorkspace => Path.Combine(Root, "other");
        public ProjectCatalog Catalog { get; }

        public Fixture(bool createProjects = true)
        {
            Directory.CreateDirectory(Root);
            if (createProjects) Directory.CreateDirectory(Projects);
            Directory.CreateDirectory(Workspace);
            Directory.CreateDirectory(OtherWorkspace);
            Catalog = new ProjectCatalog(new CatalogOptions { GlobalRoot = Root });
        }

        public Task<ProjectOwnershipResult> ReadAsync(string id, string path) => Catalog.ReadBoundedOwnershipAsync(id, path);

        public void Write(string slug, string id, string path, bool legacy = false, bool archived = false,
            Encoding? encoding = null, string body = "", string? overrideText = null)
        {
            var location = legacy ? Path.Combine(Projects, slug, "readme.md") : Path.Combine(Projects, slug + ".md");
            Directory.CreateDirectory(Path.GetDirectoryName(location)!);
            var text = overrideText ?? $"---\nid: '{id}'\nslug: '{slug}'\nname: '{slug}'\npath: '{path}'\narchived: {archived.ToString().ToLowerInvariant()}\n---\n{body}";
            File.WriteAllText(location, text, encoding ?? new UTF8Encoding(false));
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
