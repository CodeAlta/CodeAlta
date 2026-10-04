using CodeAlta.Agent;
using CodeAlta.Catalog;

namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed class CatalogStateRootTests
{
    [TestMethod]
    public void StateRoot_DefaultsToTheGlobalRoot()
    {
        var options = new CatalogOptions { GlobalRoot = Path.Combine("tmp", "alta-home") };

        Assert.AreEqual(options.GlobalRoot, options.StateRoot);
        Assert.IsFalse(options.HasSeparateStateRoot);
        Assert.AreEqual(Path.Combine(options.GlobalRoot, "sessions"), options.SessionsRoot);
        Assert.AreEqual(Path.Combine(options.GlobalRoot, "cache", "cache.sqlite3"), options.SessionCacheDatabasePath);
        Assert.AreEqual(Path.Combine(options.GlobalRoot, "ui-state.yaml"), options.UiStatePath);
        Assert.AreEqual(Path.Combine(options.GlobalRoot, "saved_prompts"), options.PromptDraftsRoot);

        options.StateRoot = options.GlobalRoot;
        Assert.IsFalse(options.HasSeparateStateRoot, "naming the global root again is not a separate state root");
    }

    [TestMethod]
    public void SeparateStateRoot_MovesWhatOneInstanceWritesAndKeepsWhatIsShared()
    {
        var global = Path.Combine("tmp", "alta-home");
        var state = Path.Combine(global, "dev");
        var options = new CatalogOptions { GlobalRoot = global, StateRoot = state };

        Assert.IsTrue(options.HasSeparateStateRoot);
        // Written by one instance only.
        Assert.AreEqual(Path.Combine(state, "sessions"), options.SessionsRoot);
        Assert.AreEqual(Path.Combine(state, "cache", "cache.sqlite3"), options.SessionCacheDatabasePath);
        Assert.AreEqual(Path.Combine(state, "ui-state.yaml"), options.UiStatePath);
        Assert.AreEqual(Path.Combine(state, "saved_prompts"), options.PromptDraftsRoot);
        Assert.AreEqual(Path.Combine(state, "threads", "internal"), options.InternalSessionsRoot);
        // Shared with the instance that owns the global root.
        Assert.AreEqual(Path.Combine(global, "config.toml"), options.ConfigPath);
        Assert.AreEqual(Path.Combine(global, "projects"), options.ProjectsRoot);
        Assert.AreEqual(Path.Combine(global, "cache"), options.CacheRoot);
    }

    [TestMethod]
    public async Task SessionJournalsAndTheirCache_AreWrittenUnderTheStateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-state-root-" + Guid.NewGuid().ToString("N"));
        var global = Path.Combine(root, "alta");
        var state = Path.Combine(global, "dev");
        Directory.CreateDirectory(state);
        try
        {
            var options = new CatalogOptions { GlobalRoot = global, StateRoot = state };
            var catalog = new SessionViewCatalog(options);
            var createdAt = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
            var session = new SessionViewDescriptor
            {
                SessionId = "dev-session", Kind = SessionViewKind.GlobalSession, ProviderId = "provider", ProviderKey = "provider",
                WorkingDirectory = global, Title = "Developer session", CreatedAt = createdAt, UpdatedAt = createdAt,
            };

            await catalog.JournalStore.AppendStateAsync(session, new SessionViewLocalState(), CancellationToken.None);

            var journal = Directory.EnumerateFiles(state, "dev-session.jsonl", SearchOption.AllDirectories).Single();
            StringAssert.StartsWith(journal, Path.Combine(state, "sessions"));
            Assert.IsFalse(Directory.Exists(Path.Combine(global, "sessions")), "nothing is written to the shared root's sessions");
            Assert.IsNotNull(await catalog.JournalStore.ReadHeaderAsync("dev-session", createdAt));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch (IOException) { } }
    }
}
