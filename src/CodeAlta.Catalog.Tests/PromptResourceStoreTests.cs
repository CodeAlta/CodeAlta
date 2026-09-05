using System.Text;

namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed class PromptResourceStoreTests
{
    [TestMethod]
    public void RoundTripsAgentAndSystemMetadataModeAndBody()
    {
        using var fixture = new Fixture();
        foreach (var kind in new[] { PromptResourceKind.Agent, PromptResourceKind.System })
        {
            var id = new PromptResourceIdentity(PromptResourceScope.Global, kind, "custom");
            var content = new PromptFileContent("A: \"quoted\" \\ name", "line one\nline two", "custom", " body\nnext ", true);
            Assert.IsFalse(fixture.Store.Create(id, content).IsConflict);
            var loaded = fixture.Store.Load(id);
            Assert.AreEqual("body\nnext", loaded.Content.Body);
            Assert.IsTrue(loaded.Content.Append);
            if (kind == PromptResourceKind.Agent)
            {
                Assert.AreEqual(content.Name, loaded.Content.Name);
                Assert.AreEqual(content.Description, loaded.Content.Description);
                Assert.AreEqual("custom", loaded.Content.SystemPromptName);
            }
        }
    }

    [TestMethod]
    public void SaveRetainsLoadedUnicodeEncodingAndBom()
    {
        using var fixture = new Fixture();
        var path = fixture.Store.GetPath(fixture.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "---\r\nname: Unicode\r\n---\r\noriginal\r\n", new UnicodeEncoding(false, true, true));
        var loaded = fixture.Store.Load(fixture.Id);
        var result = fixture.Store.Save(loaded, loaded.Content with { Body = "日本語" });
        Assert.IsFalse(result.IsConflict);
        var saved = fixture.Store.Load(fixture.Id);
        Assert.AreEqual(Encoding.Unicode.CodePage, saved.File.Encoding.CodePage);
        Assert.IsTrue(saved.File.HasByteOrderMark);
        Assert.AreEqual("日本語", saved.Content.Body);
        CollectionAssert.AreEqual(new byte[] { 0xff, 0xfe }, File.ReadAllBytes(path)[..2]);
    }

    [TestMethod]
    public void BuiltInsAndWrongRootProvenanceCannotBeWritten()
    {
        using var fixture = new Fixture();
        var builtIn = new PromptResourceIdentity(PromptResourceScope.BuiltIn, PromptResourceKind.Agent, "default");
        var path = fixture.Store.GetPath(builtIn);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "---\nname: Built in\n---\nOriginal");
        var loaded = fixture.Store.Load(builtIn);
        Assert.Throws<UnauthorizedAccessException>(() => fixture.Store.Create(builtIn, fixture.Content));
        Assert.Throws<UnauthorizedAccessException>(() => fixture.Store.Save(loaded, fixture.Content));
        Assert.Throws<UnauthorizedAccessException>(() => fixture.Store.Delete(loaded));
        Assert.Throws<ArgumentException>(() => fixture.Store.Identify(PromptResourceScope.Global, PromptResourceKind.Agent, path));
        using var other = new Fixture();
        Assert.Throws<ArgumentException>(() => other.Store.Save(loaded, fixture.Content));
        Assert.IsTrue(File.ReadAllText(path).EndsWith("Original", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ScopeSeparationAndCreateCollisionPreservePriorBytes()
    {
        using var fixture = new Fixture();
        var global = fixture.Id;
        var project = global with { Scope = PromptResourceScope.Project };
        fixture.Store.Create(global, fixture.Content);
        fixture.Store.Create(project, fixture.Content with { Body = "project" });
        var before = File.ReadAllBytes(fixture.Store.GetPath(global));
        Assert.IsTrue(fixture.Store.Create(global, fixture.Content with { Body = "collision" }).IsConflict);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(fixture.Store.GetPath(global)));
        Assert.AreEqual("project", fixture.Store.Load(project).Content.Body);
    }

    [TestMethod]
    public void InvalidIdsKindsScopesRootsAndRequiredFieldsAreRejected()
    {
        using var fixture = new Fixture();
        foreach (var id in new[] { "", ".", "..", "../escape", "a/b", "a\\b", "a:b", "a.", "CON", "NUL.txt" })
        {
            Assert.Throws<ArgumentException>(() => fixture.Store.Create(fixture.Id with { Id = id }, fixture.Content), id);
        }
        Assert.Throws<ArgumentException>(() => fixture.Store.GetPath(fixture.Id with { Scope = (PromptResourceScope)99 }));
        Assert.Throws<ArgumentException>(() => fixture.Store.GetPath(fixture.Id with { Kind = (PromptResourceKind)99 }));
        Assert.Throws<ArgumentException>(() => fixture.Store.Create(fixture.Id, fixture.Content with { Name = null }));
        Assert.Throws<ArgumentException>(() => fixture.Store.Create(fixture.Id, fixture.Content with { Body = " " }));
        Assert.Throws<ArgumentException>(() => fixture.Store.Create(fixture.Id, fixture.Content with { SystemPromptName = "../bad" }));
        Assert.Throws<ArgumentException>(() => new PromptResourceStore(fixture.Root, fixture.Root, null, new TextFileCodec()));
        Assert.Throws<ArgumentException>(() => new PromptResourceStore(Path.GetPathRoot(fixture.Root)!, fixture.Root, null, new TextFileCodec()));
        var noProject = new PromptResourceStore(Path.Combine(fixture.Root, "b"), Path.Combine(fixture.Root, "g"), null, new TextFileCodec());
        Assert.Throws<ArgumentException>(() => noProject.GetPath(fixture.Id with { Scope = PromptResourceScope.Project }));
    }

    [TestMethod]
    public void SameMtimeEditsAndExternalDeletionConflictUntilExplicitAcknowledgment()
    {
        using var fixture = new Fixture();
        fixture.Store.Create(fixture.Id, fixture.Content);
        var loaded = fixture.Store.Load(fixture.Id);
        var path = fixture.Store.GetPath(fixture.Id);
        File.WriteAllText(path, "---\nname: External\n---\nexternal");
        File.SetLastWriteTimeUtc(path, loaded.File.LastWriteTimeUtc.UtcDateTime);
        var conflict = fixture.Store.Save(loaded, fixture.Content with { Body = "dirty" });
        Assert.IsTrue(conflict.IsConflict);
        Assert.IsTrue(fixture.Store.Delete(loaded).IsConflict);
        Assert.AreEqual("original", loaded.Content.Body);
        File.WriteAllText(path, "---\nname: External again\n---\nchanged again");
        Assert.IsTrue(fixture.Store.Save(loaded, fixture.Content with { Body = "dirty" }, conflict.CurrentRevision).IsConflict);
        conflict = fixture.Store.Save(loaded, fixture.Content with { Body = "dirty" });
        Assert.IsFalse(fixture.Store.Save(loaded, fixture.Content with { Body = "dirty" }, conflict.CurrentRevision).IsConflict);
        Assert.IsTrue(fixture.Store.Delete(loaded).IsConflict);
        var current = fixture.Store.Load(fixture.Id);
        File.Delete(path);
        Assert.IsTrue(fixture.Store.Save(current, fixture.Content).IsConflict);
        Assert.IsTrue(fixture.Store.Delete(current).IsConflict);
        Assert.Throws<FileNotFoundException>(() => fixture.Store.Load(fixture.Id));
        Assert.IsFalse(fixture.Store.Save(current, fixture.Content, TextFileRevision.Missing).IsConflict);
    }

    [TestMethod]
    public void LinkedFilesAndDirectoriesAreRejectedWithoutTouchingTargets()
    {
        using var fixture = new Fixture();
        var outside = Path.Combine(fixture.Root, "outside");
        Directory.CreateDirectory(outside);
        var target = Path.Combine(outside, "target.md");
        File.WriteAllText(target, "---\nname: Outside\n---\noutside");
        var path = fixture.Store.GetPath(fixture.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try { File.CreateSymbolicLink(path, target); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Inconclusive("Creating task-owned symlinks requires platform permission.");
        }
        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => fixture.Store.Load(fixture.Id));
            Assert.Throws<UnauthorizedAccessException>(() => fixture.Store.Create(fixture.Id, fixture.Content));
        }
        finally { File.Delete(path); }
        fixture.Store.Create(fixture.Id, fixture.Content);
        var loaded = fixture.Store.Load(fixture.Id);
        File.Delete(path);
        File.CreateSymbolicLink(path, target);
        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => fixture.Store.Save(loaded, fixture.Content));
            Assert.Throws<UnauthorizedAccessException>(() => fixture.Store.Delete(loaded));
        }
        finally { File.Delete(path); }
        var directory = Path.GetDirectoryName(path)!;
        Directory.Delete(directory);
        Directory.CreateSymbolicLink(directory, outside);
        try { Assert.Throws<UnauthorizedAccessException>(() => fixture.Store.Create(fixture.Id, fixture.Content)); }
        finally { Directory.Delete(directory); }
        Assert.IsTrue(File.ReadAllText(target).EndsWith("outside", StringComparison.Ordinal));
    }

    [TestMethod]
    public void InvalidUnicodeMetadataCannotBeSilentlyReplacedDuringSerialization()
    {
        using var fixture = new Fixture();
        Assert.Throws<ArgumentException>(() => fixture.Store.Create(fixture.Id, fixture.Content with { Name = "bad\ud800" }));
        Assert.IsFalse(File.Exists(fixture.Store.GetPath(fixture.Id)));
    }

    [TestMethod]
    public void AmbiguousReplaceSystemBodyIsRejectedBeforeWriting()
    {
        using var fixture = new Fixture();
        var id = fixture.Id with { Kind = PromptResourceKind.System };
        fixture.Store.Create(id, fixture.Content);
        var loaded = fixture.Store.Load(id);
        var bytes = File.ReadAllBytes(fixture.Store.GetPath(id));
        Assert.Throws<ArgumentException>(() => fixture.Store.Save(loaded, fixture.Content with { Body = "---\nmode: append\n---\ndifferent mode" }));
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(fixture.Store.GetPath(id)));
    }

    [TestMethod]
    public void MalformedMetadataAndAppendAliasesAreValidated()
    {
        foreach (var metadata in new[] { "mode: invalid", "append: invalid", "append: true\nmode: replace" })
            Assert.Throws<ArgumentException>(() => PromptFileFormat.Parse(PromptResourceKind.Agent, $"---\nname: Custom\n{metadata}\n---\nbody"));
        var inherited = PromptFileFormat.Parse(PromptResourceKind.Agent, "---\nappend: true\n---\nbody");
        Assert.IsTrue(inherited.Append);
        Assert.IsNull(inherited.Name);
        Assert.IsNull(inherited.SystemPromptName);
        Assert.AreEqual(inherited, PromptFileFormat.Parse(PromptResourceKind.Agent, PromptFileFormat.Serialize(PromptResourceKind.Agent, inherited)));
        Assert.Throws<ArgumentException>(() => PromptFileFormat.Parse(PromptResourceKind.Agent, "body without required name"));
        Assert.Throws<ArgumentException>(() => PromptFileFormat.Parse(PromptResourceKind.System, " "));
    }

    [TestMethod]
    public async Task FailureAndCancellationPreserveOriginalSnapshotAndBytes()
    {
        using var fixture = new Fixture();
        fixture.Store.Create(fixture.Id, fixture.Content);
        var loaded = fixture.Store.Load(fixture.Id);
        var path = fixture.Store.GetPath(fixture.Id);
        var bytes = File.ReadAllBytes(path);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Store.SaveAsync(loaded, fixture.Content with { Body = "dirty" }, loaded.File.Revision, cancellation.Token));
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => fixture.Store.Save(loaded, fixture.Content with { Body = "dirty" }));
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
            Assert.AreEqual("original", loaded.Content.Body);
        }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "CodeAlta-PromptCrud-" + Guid.NewGuid().ToString("N"));
        public PromptResourceStore Store { get; }
        public PromptResourceIdentity Id { get; } = new(PromptResourceScope.Global, PromptResourceKind.Agent, "custom");
        public PromptFileContent Content { get; } = new("Custom", null, "default", "original", false);
        public Fixture() => Store = new PromptResourceStore(Path.Combine(Root, "built"), Path.Combine(Root, "global"), Path.Combine(Root, "project"), new TextFileCodec());
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
