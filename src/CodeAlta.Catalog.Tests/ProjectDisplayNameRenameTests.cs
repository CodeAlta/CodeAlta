using System.Text;

namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed class ProjectDisplayNameRenameTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RenamePreservesSourceFormatUnknownYamlCommentsAndBody(bool legacy)
    {
        using var fixture = new Fixture(legacy);
        var before = await fixture.ReadAsync();
        var observed = await fixture.Catalog.ReadDisplayNameAsync(fixture.Id, fixture.ProjectPath);
        Assert.IsNotNull(observed);
        Assert.AreEqual("First", observed.DisplayName);
        Assert.AreEqual(ProjectDisplayNameRenameStatus.Updated, await fixture.Catalog.RenameDisplayNameAsync(
            fixture.Id, fixture.ProjectPath, observed.SourcePath, observed.Revision, "New \"name\" ✨"));
        var after = await fixture.ReadAsync();
        Assert.AreEqual(before.Replace("'First'", "\"New \\\"name\\\" ✨\"", StringComparison.Ordinal), after);
        var reloaded = await new ProjectCatalog(new CatalogOptions { GlobalRoot = fixture.GlobalRoot }).GetByIdAsync(fixture.Id);
        Assert.IsNotNull(reloaded);
        Assert.AreEqual("New \"name\" ✨", reloaded.DisplayName);
        CollectionAssert.AreEqual(new UTF8Encoding(true).GetPreamble().Concat(new UTF8Encoding(true).GetBytes(after)).ToArray(),
            await File.ReadAllBytesAsync(fixture.SourcePath));
        Assert.AreEqual(fixture.ProjectPath, reloaded.ProjectPath);
        Assert.AreEqual(fixture.SourcePath, reloaded.SourcePath);
        if (legacy) Assert.IsFalse(File.Exists(Path.Combine(fixture.GlobalRoot, "projects", "fixture.md")));
        Assert.IsTrue(File.Exists(fixture.Marker));
    }

    [TestMethod]
    public async Task WrongIdentityRevisionAndConcurrentWritesNeverOverwriteOrCreateAnotherFile()
    {
        using var fixture = new Fixture(false);
        var observed = (await fixture.Catalog.ReadDisplayNameAsync(fixture.Id, fixture.ProjectPath))!;
        var originalBytes = await File.ReadAllBytesAsync(fixture.SourcePath);
        var wrong = await fixture.Catalog.RenameDisplayNameAsync("01963b36-0d70-7a11-b3c2-1f2e3d4c5b6a", fixture.ProjectPath, observed.SourcePath, observed.Revision, "Wrong");
        Assert.AreEqual(ProjectDisplayNameRenameStatus.Conflict, wrong);
        Assert.AreEqual(ProjectDisplayNameRenameStatus.Conflict, await fixture.Catalog.RenameDisplayNameAsync(
            fixture.Id, fixture.GlobalRoot, observed.SourcePath, observed.Revision, "Wrong"));
        CollectionAssert.AreEqual(originalBytes, await File.ReadAllBytesAsync(fixture.SourcePath));
        var original = await fixture.ReadAsync();
        var edited = original.Replace("# comment", "# externally changed", StringComparison.Ordinal);
        await File.WriteAllTextAsync(fixture.SourcePath, edited, new UTF8Encoding(true));
        Assert.AreEqual(ProjectDisplayNameRenameStatus.Conflict, await fixture.Catalog.RenameDisplayNameAsync(
            fixture.Id, fixture.ProjectPath, observed.SourcePath, observed.Revision, "Wrong"));
        Assert.AreEqual(edited, await fixture.ReadAsync());
        CollectionAssert.AreEqual(new UTF8Encoding(true).GetPreamble().Concat(new UTF8Encoding(true).GetBytes(edited)).ToArray(),
            await File.ReadAllBytesAsync(fixture.SourcePath));
        var revised = (await fixture.Catalog.ReadDisplayNameAsync(fixture.Id, fixture.ProjectPath))!;
        var attempts = await Task.WhenAll(
            fixture.Catalog.RenameDisplayNameAsync(fixture.Id, fixture.ProjectPath, revised.SourcePath, revised.Revision, "First attempt"),
            fixture.Catalog.RenameDisplayNameAsync(fixture.Id, fixture.ProjectPath, revised.SourcePath, revised.Revision, "Second attempt"));
        CollectionAssert.AreEquivalent(new[] { ProjectDisplayNameRenameStatus.Updated, ProjectDisplayNameRenameStatus.Conflict }, attempts);
        Assert.IsTrue((await fixture.ReadAsync()).Contains("First attempt", StringComparison.Ordinal)
            ^ (await fixture.ReadAsync()).Contains("Second attempt", StringComparison.Ordinal));
        Assert.IsTrue(File.Exists(fixture.Marker));
    }

    [TestMethod]
    public async Task SameSlugBelongingToAnotherProjectIsNeverOverwritten()
    {
        using var fixture = new Fixture(legacy: true);
        var flat = Path.Combine(fixture.GlobalRoot, "projects", "fixture.md");
        var sibling = (await fixture.ReadAsync()).Replace(fixture.Id, "01963b36-0d70-7a11-b3c2-1f2e3d4c5b6a", StringComparison.Ordinal)
            .Replace(fixture.ProjectPath, fixture.GlobalRoot, StringComparison.Ordinal);
        await File.WriteAllTextAsync(flat, sibling, new UTF8Encoding(true));
        var before = await File.ReadAllBytesAsync(flat);
        var observed = (await fixture.Catalog.ReadDisplayNameAsync(fixture.Id, fixture.ProjectPath))!;
        Assert.AreEqual(ProjectDisplayNameRenameStatus.Updated, await fixture.Catalog.RenameDisplayNameAsync(
            fixture.Id, fixture.ProjectPath, observed.SourcePath, observed.Revision, "Legacy renamed"));
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(flat));
        Assert.AreEqual("Legacy renamed", (await fixture.Catalog.GetByIdAsync(fixture.Id))!.DisplayName);
    }

    [TestMethod]
    public async Task NewFlatCopyCannotRetargetLegacySnapshotEvenWhenBytesMatch()
    {
        using var fixture = new Fixture(legacy: true);
        var observed = (await fixture.Catalog.ReadDisplayNameAsync(fixture.Id, fixture.ProjectPath))!;
        var flat = Path.Combine(fixture.GlobalRoot, "projects", "fixture.md");
        File.Copy(fixture.SourcePath, flat);
        var original = await File.ReadAllBytesAsync(flat);
        Assert.AreEqual(ProjectDisplayNameRenameStatus.Conflict, await fixture.Catalog.RenameDisplayNameAsync(
            fixture.Id, fixture.ProjectPath, observed.SourcePath, observed.Revision, "Wrong target"));
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(flat));
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(fixture.SourcePath));
    }

    [TestMethod]
    public async Task GeneratedAltaMarkdownAndUtf16EncodingRemainReadableAfterRename()
    {
        using var fixture = new Fixture(false);
        var descriptor = (await fixture.Catalog.GetByIdAsync(fixture.Id))!;
        var generated = new CatalogYamlSerializer().SerializeProjectMarkdown(descriptor);
        var encoding = new UnicodeEncoding(false, true, true);
        await File.WriteAllBytesAsync(fixture.SourcePath, encoding.GetPreamble().Concat(encoding.GetBytes(generated)).ToArray());
        var observed = (await fixture.Catalog.ReadDisplayNameAsync(fixture.Id, fixture.ProjectPath))!;
        Assert.AreEqual(ProjectDisplayNameRenameStatus.Updated, await fixture.Catalog.RenameDisplayNameAsync(
            fixture.Id, fixture.ProjectPath, observed.SourcePath, observed.Revision, "Compatible"));
        var saved = await new TextFileCodec().LoadAsync(fixture.SourcePath);
        Assert.AreEqual(encoding.CodePage, saved.Encoding.CodePage);
        Assert.IsTrue(saved.HasByteOrderMark);
        Assert.AreEqual("Compatible", (await fixture.Catalog.GetByIdAsync(fixture.Id))!.DisplayName);
        Assert.IsTrue(saved.Text.Contains("# Markdown", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task SwappedIdOrPathCannotReceiveStaleRename()
    {
        using var fixture = new Fixture(false);
        var observed = (await fixture.Catalog.ReadDisplayNameAsync(fixture.Id, fixture.ProjectPath))!;
        var original = await fixture.ReadAsync();
        foreach (var swapped in new[] { original.Replace(fixture.Id, "01963b36-0d70-7a11-b3c2-1f2e3d4c5b6a", StringComparison.Ordinal),
            original.Replace(fixture.ProjectPath, fixture.GlobalRoot, StringComparison.Ordinal) })
        {
            await File.WriteAllTextAsync(fixture.SourcePath, swapped, new UTF8Encoding(true));
            Assert.AreEqual(ProjectDisplayNameRenameStatus.Conflict, await fixture.Catalog.RenameDisplayNameAsync(
                fixture.Id, fixture.ProjectPath, observed.SourcePath, observed.Revision, "Other"));
            Assert.AreEqual(swapped, await fixture.ReadAsync());
        }
    }

    [TestMethod]
    public async Task UnsupportedShapeAndInvalidNameFailClosedWithoutWriting()
    {
        using var fixture = new Fixture(false);
        var original = await fixture.ReadAsync();
        var observed = (await fixture.Catalog.ReadDisplayNameAsync(fixture.Id, fixture.ProjectPath))!;
        foreach (var invalid in new[] { "", " ", " padded", "line\nbreak", "\ud800", new string('x', 257) })
            await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await fixture.Catalog.RenameDisplayNameAsync(
                fixture.Id, fixture.ProjectPath, observed.SourcePath, observed.Revision, invalid));
        foreach (var unsupported in new[] { original.Replace("display_name: 'First'", "display_name: |\r\n  First", StringComparison.Ordinal),
            original.Replace("display_name: 'First' # comment\r\n", "", StringComparison.Ordinal) })
        {
            await File.WriteAllTextAsync(fixture.SourcePath, unsupported, new UTF8Encoding(true));
            // Unsupported YAML is never rewritten even when its revision is current.
            var revision = await new TextFileCodec().GetRevisionAsync(fixture.SourcePath);
            Assert.AreEqual(ProjectDisplayNameRenameStatus.Unsupported, await fixture.Catalog.RenameDisplayNameAsync(
                fixture.Id, fixture.ProjectPath, fixture.SourcePath, revision, "New"));
            Assert.AreEqual(unsupported, await fixture.ReadAsync());
        }
        foreach (var malformed in new[] { original.Replace("display_name: 'First'", "display_name: 'First'\r\ndisplay_name: Second", StringComparison.Ordinal),
            original.Replace("display_name: 'First'", "display_name: *some_alias", StringComparison.Ordinal) })
        {
            await File.WriteAllTextAsync(fixture.SourcePath, malformed, new UTF8Encoding(true));
            await Assert.ThrowsExactlyAsync<SharpYaml.YamlException>(async () => await fixture.Catalog.RenameDisplayNameAsync(
                fixture.Id, fixture.ProjectPath, fixture.SourcePath, await new TextFileCodec().GetRevisionAsync(fixture.SourcePath), "New"));
            Assert.AreEqual(malformed, await fixture.ReadAsync());
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "codealta-project-name-" + Guid.NewGuid().ToString("N"));
        public string Id { get; } = ProjectId.NewVersion7().ToString();
        public string GlobalRoot => Path.Combine(_root, "global");
        public string ProjectPath => Path.Combine(_root, "project");
        public string Marker => Path.Combine(ProjectPath, "leave-alone.txt");
        public string SourcePath { get; }
        public ProjectCatalog Catalog { get; }
        public Fixture(bool legacy)
        {
            Directory.CreateDirectory(ProjectPath);
            Directory.CreateDirectory(GlobalRoot);
            File.WriteAllText(Marker, "user data");
            var projects = Path.Combine(GlobalRoot, "projects");
            SourcePath = legacy ? Path.Combine(projects, "fixture", "readme.md") : Path.Combine(projects, "fixture.md");
            Directory.CreateDirectory(Path.GetDirectoryName(SourcePath)!);
            Catalog = new(new CatalogOptions { GlobalRoot = GlobalRoot });
            var document = $"---\r\nkind: project\r\nid: '{Id}'\r\nslug: fixture\r\nname: Fixture\r\n# comment ✨\r\ndisplay_name: 'First' # comment\r\npath: '{ProjectPath.Replace("'", "''", StringComparison.Ordinal)}'\r\nunknown_nested:\r\n  hello: [1, 2] # leave this\r\n---\r\n\r\n# Markdown\r\n\r\nPreserve   spaces.\r\n";
            File.WriteAllText(SourcePath, document, new UTF8Encoding(true));
        }
        public async Task<string> ReadAsync() => (await new TextFileCodec().LoadAsync(SourcePath)).Text;
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
