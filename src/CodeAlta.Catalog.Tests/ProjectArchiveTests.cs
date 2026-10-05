using System.Text;

namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed class ProjectArchiveTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BothTransitionsPreserveGeneratedSourceEncodingCommentsUnknownFieldsAndBody(bool legacy)
    {
        using var fixture = new Fixture();
        var project = await fixture.Catalog.UpsertFromPathAsync(fixture.Root);
        var source = project.SourcePath!;
        var text = await File.ReadAllTextAsync(source);
        text = text.Replace("archived: false", "archived: false # metadata only", StringComparison.Ordinal)
            .Replace("\n---\n", "\ncustom: [one, two] # retained\n---\n", StringComparison.Ordinal) + "\nRetained   markdown ✨\n";
        if (legacy)
        {
            var old = source;
            source = Path.Combine(fixture.Catalog.Options.ProjectsRoot, project.Slug, "readme.md");
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            File.Move(old, source);
        }
        var encoding = legacy ? (Encoding)new UnicodeEncoding(false, true, true) : new UTF8Encoding(true, true);
        await File.WriteAllTextAsync(source, text, encoding);
        foreach (var archived in new[] { true, false })
        {
            var evidence = await fixture.Catalog.ReadArchiveAsync(project.Id, fixture.Root);
            Assert.IsNotNull(evidence);
            Assert.AreEqual(source, evidence.SourcePath);
            Assert.AreEqual(!archived, evidence.Archived);
            Assert.AreEqual(ProjectDisplayNameRenameStatus.Updated, await fixture.Catalog.SetArchivedAsync(project.Id, fixture.Root,
                source, evidence.Revision, !archived, archived));
            var expected = archived ? text.Replace("archived: false", "archived: true", StringComparison.Ordinal) : text;
            CollectionAssert.AreEqual(encoding.GetPreamble().Concat(encoding.GetBytes(expected)).ToArray(), await File.ReadAllBytesAsync(source));
            Assert.AreEqual(archived, (await fixture.Catalog.GetByIdAsync(project.Id))!.Archived);
        }
    }

    [TestMethod]
    public async Task ScopeSourceStateRevisionExternalChangeAndDuplicateOwnershipRefuseWithoutWrites()
    {
        using var fixture = new Fixture();
        var p = await fixture.Catalog.UpsertFromPathAsync(fixture.Root);
        var e = (await fixture.Catalog.ReadArchiveAsync(p.Id, fixture.Root))!;
        var before = await File.ReadAllTextAsync(e.SourcePath);
        Assert.AreEqual(ProjectDisplayNameRenameStatus.Unsupported, await fixture.Catalog.SetArchivedAsync("missing", fixture.Root, e.SourcePath, e.Revision, false, true));
        Assert.AreEqual(ProjectDisplayNameRenameStatus.Unsupported, await fixture.Catalog.SetArchivedAsync(p.Id, fixture.Catalog.Options.GlobalRoot, e.SourcePath, e.Revision, false, true));
        Assert.AreEqual(ProjectDisplayNameRenameStatus.Conflict, await fixture.Catalog.SetArchivedAsync(p.Id, fixture.Root, e.SourcePath + ".other", e.Revision, false, true));
        Assert.AreEqual(ProjectDisplayNameRenameStatus.Conflict, await fixture.Catalog.SetArchivedAsync(p.Id, fixture.Root, e.SourcePath, e.Revision, true, false));
        Assert.AreEqual(before, await File.ReadAllTextAsync(e.SourcePath));
        var external = before + "external content";
        await File.WriteAllTextAsync(e.SourcePath, external);
        Assert.AreEqual(ProjectDisplayNameRenameStatus.Conflict, await fixture.Catalog.SetArchivedAsync(p.Id, fixture.Root, e.SourcePath, e.Revision, false, true));
        Assert.AreEqual(external, await File.ReadAllTextAsync(e.SourcePath));
        await File.WriteAllTextAsync(Path.Combine(fixture.Catalog.Options.ProjectsRoot, "duplicate.md"), external.Replace(p.Slug, "duplicate", StringComparison.Ordinal));
        Assert.IsNull(await fixture.Catalog.ReadArchiveAsync(p.Id, fixture.Root));
        Assert.AreEqual(external, await File.ReadAllTextAsync(e.SourcePath));
    }

    [TestMethod]
    public async Task ObservedFileLinkCannotAuthorizeArchiveOfItsTarget()
    {
        using var fixture = new Fixture();
        var p = await fixture.Catalog.UpsertFromPathAsync(fixture.Root);
        var e = (await fixture.Catalog.ReadArchiveAsync(p.Id, fixture.Root))!;
        var target = Path.Combine(fixture.Root, "target.md");
        File.Move(e.SourcePath, target);
        try { File.CreateSymbolicLink(e.SourcePath, target); }
        catch (Exception ex) when (ex is UnauthorizedAccessException || ex.HResult == unchecked((int)0x80070522)) // ERROR_PRIVILEGE_NOT_HELD: Windows without Developer Mode or elevation cannot create symbolic links.
        { Assert.Inconclusive("Disposable symlink creation requires platform permission."); }
        var bytes = await File.ReadAllBytesAsync(target);
        Assert.IsNull(await fixture.Catalog.ReadArchiveAsync(p.Id, fixture.Root));
        Assert.AreEqual(ProjectDisplayNameRenameStatus.Unsupported, await fixture.Catalog.SetArchivedAsync(p.Id, fixture.Root, e.SourcePath, e.Revision, false, true));
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(target));
    }

    [TestMethod]
    public async Task SourceMoveAndCooperatingRenameConflictNeverRetargetArchive()
    {
        using var fixture = new Fixture();
        var p = await fixture.Catalog.UpsertFromPathAsync(fixture.Root);
        var e = (await fixture.Catalog.ReadArchiveAsync(p.Id, fixture.Root))!;
        var attempts = await Task.WhenAll(fixture.Catalog.SetArchivedAsync(p.Id, fixture.Root, e.SourcePath, e.Revision, false, true),
            fixture.Catalog.RenameDisplayNameAsync(p.Id, fixture.Root, e.SourcePath, e.Revision, "Renamed"));
        CollectionAssert.AreEquivalent(new[] { ProjectDisplayNameRenameStatus.Updated, ProjectDisplayNameRenameStatus.Conflict }, attempts);
        e = (await fixture.Catalog.ReadArchiveAsync(p.Id, fixture.Root))!;
        var destination = Path.Combine(fixture.Catalog.Options.ProjectsRoot, p.Slug, "readme.md");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Move(e.SourcePath, destination);
        var before = await File.ReadAllBytesAsync(destination);
        Assert.AreEqual(ProjectDisplayNameRenameStatus.Conflict, await fixture.Catalog.SetArchivedAsync(p.Id, fixture.Root, e.SourcePath, e.Revision, e.Archived, !e.Archived));
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(destination));
        Assert.IsFalse(File.Exists(e.SourcePath));
    }

    [TestMethod]
    public async Task MissingMalformedComplexAndMovedSourcesFailClosed()
    {
        using var fixture = new Fixture();
        var p = await fixture.Catalog.UpsertFromPathAsync(fixture.Root);
        var e = (await fixture.Catalog.ReadArchiveAsync(p.Id, fixture.Root))!;
        var before = await File.ReadAllTextAsync(e.SourcePath);
        foreach (var replacement in new[] { "", "archived: [false]", "archived: false\narchived: true", "archived: &state false" })
        {
            var changed = before.Replace("archived: false", replacement, StringComparison.Ordinal);
            await File.WriteAllTextAsync(e.SourcePath, changed);
            Assert.IsNull(await fixture.Catalog.ReadArchiveAsync(p.Id, fixture.Root));
            Assert.AreEqual(changed, await File.ReadAllTextAsync(e.SourcePath));
        }
        File.Delete(e.SourcePath);
        Assert.IsNull(await fixture.Catalog.ReadArchiveAsync(p.Id, fixture.Root));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "codealta-archive-" + Guid.NewGuid().ToString("N"));
        public ProjectCatalog Catalog { get; }
        public Fixture() { Directory.CreateDirectory(Root); Catalog = new(new CatalogOptions { GlobalRoot = Path.Combine(Root, "global") }); }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
