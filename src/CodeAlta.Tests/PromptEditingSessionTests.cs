using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime.SystemPrompts;
using CodeAlta.Tui.Views;

namespace CodeAlta.Tests;

[TestClass]
public sealed class PromptEditingSessionTests
{
    [TestMethod]
    public void SaveFailureRetainsDirtyAcknowledgmentAndRetryNotifiesExactlyOnce()
    {
        using var fixture = new Fixture();
        fixture.Store.Create(fixture.Id, fixture.Content);
        var loaded = fixture.Store.Load(fixture.Id);
        var editor = new PromptEditingSession(fixture.Store, loaded);
        var dirty = fixture.Content with { Body = "dirty" };
        File.WriteAllText(fixture.Store.GetPath(fixture.Id), "---\nname: External\n---\nnewer");
        var success = 0;
        var failures = 0;
        Assert.IsFalse(editor.Save(dirty, () => success++, _ => failures++));
        Assert.AreSame(loaded, editor.Snapshot);
        Assert.AreEqual("dirty", dirty.Body);
        Assert.AreEqual(0, success);
        Assert.AreEqual(1, failures);
        var observed = editor.ConflictRevision;
        Assert.IsNotNull(observed);
        // Cancel is no operation, and another ordinary Save must not silently rebaseline.
        Assert.IsFalse(editor.Save(dirty, () => success++, _ => failures++));
        Assert.AreEqual(0, success);
        Assert.IsTrue(editor.Save(dirty, () => success++, _ => failures++, observed));
        Assert.AreEqual(1, success);
        Assert.AreEqual(2, failures);
        Assert.AreEqual("dirty", editor.Snapshot.Content.Body);
    }

    [TestMethod]
    public void SharedParserLeavesGlobalProjectPrecedenceAndAppendInheritanceIntact()
    {
        using var fixture = new Fixture();
        var builtPath = fixture.Store.GetPath(fixture.Id with { Scope = PromptResourceScope.BuiltIn });
        Directory.CreateDirectory(Path.GetDirectoryName(builtPath)!);
        File.WriteAllText(builtPath, "---\nname: Built\nsystem: base\n---\nbuilt");
        fixture.Store.Create(fixture.Id, fixture.Content);
        var projectId = fixture.Id with { Scope = PromptResourceScope.Project };
        fixture.Store.Create(projectId, new PromptFileContent(null, null, null, "project", true));
        var catalog = new AgentPromptCatalog(new FileSystemPromptContentLocator(fixture.App));
        var query = new AgentPromptCatalogQuery
        {
            AppBaseDirectory = fixture.App,
            UserProfileRoot = Path.Combine(fixture.Root, "profile"),
            UserCodeAltaRoot = Path.Combine(fixture.Root, "global"),
            ProjectRoot = Path.Combine(fixture.Root, "project"),
            ProjectPromptResourcesTrusted = true,
        };
        var prompts = catalog.ListPrompts(query);
        Assert.AreEqual(3, prompts.Count);
        Assert.IsTrue(prompts.Single(x => x.IsBuiltIn).IsShadowed);
        Assert.IsFalse(prompts.Single(x => x.SourceKind == AgentPromptSourceKind.UserGlobal).IsShadowed);
        Assert.AreEqual(PromptCompositionMode.Append, prompts.Single(x => x.SourceKind == AgentPromptSourceKind.Project).Mode);
        Assert.IsNull(fixture.Store.Load(projectId).Content.Name);
        var effective = catalog.ListEffectivePrompts(query).Single();
        Assert.AreEqual("Custom", effective.DisplayName);
        Assert.AreEqual("default", effective.SystemPromptName);
        Assert.AreEqual("original" + Environment.NewLine + Environment.NewLine + "project", effective.Body);
        var systemGlobal = fixture.Id with { Kind = PromptResourceKind.System };
        fixture.Store.Create(systemGlobal, fixture.Content);
        fixture.Store.Create(systemGlobal with { Scope = PromptResourceScope.Project }, fixture.Content with { Body = "project system" });
        var systems = catalog.ListSystemPrompts(query);
        Assert.IsTrue(systems.Single(x => x.SourceKind == AgentPromptSourceKind.UserGlobal).IsShadowed);
        Assert.IsFalse(systems.Single(x => x.SourceKind == AgentPromptSourceKind.Project).IsShadowed);
    }

    [TestMethod]
    public void DeleteConflictDoesNotNotifyOrAcknowledgeUntilConfirmedRetry()
    {
        using var fixture = new Fixture();
        fixture.Store.Create(fixture.Id, fixture.Content);
        var loaded = fixture.Store.Load(fixture.Id);
        var editor = new PromptEditingSession(fixture.Store, loaded);
        File.WriteAllText(fixture.Store.GetPath(fixture.Id), "---\nname: External\n---\nnewer");
        var success = 0;
        Assert.IsFalse(editor.Delete(() => success++, _ => { }));
        var observed = editor.ConflictRevision;
        Assert.IsNotNull(observed);
        Assert.AreSame(loaded, editor.Snapshot);
        Assert.AreEqual(0, success);
        Assert.IsTrue(File.Exists(fixture.Store.GetPath(fixture.Id)));
        Assert.IsTrue(editor.Delete(() => success++, _ => Assert.Fail(), observed));
        Assert.AreEqual(1, success);
        Assert.IsFalse(File.Exists(fixture.Store.GetPath(fixture.Id)));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "CodeAlta-PromptEditor-" + Guid.NewGuid().ToString("N"));
        public string App => Path.Combine(Root, "app");
        public PromptResourceStore Store { get; }
        public PromptResourceIdentity Id { get; } = new(PromptResourceScope.Global, PromptResourceKind.Agent, "custom");
        public PromptFileContent Content { get; } = new("Custom", null, "default", "original", false);
        public Fixture() => Store = new PromptResourceStore(Path.Combine(App, "content", "prompts"), Path.Combine(Root, "global", "prompts"), Path.Combine(Root, "project", ".alta", "prompts"), new TextFileCodec());
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
