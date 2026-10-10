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
        Assert.AreEqual(Path.Combine(options.GlobalRoot, "data", "alta.sqlite3"), options.ApplicationDatabasePath);
        Assert.AreEqual(Path.Combine(options.GlobalRoot, "cache", "cache.sqlite3"), options.LegacySessionCacheDatabasePath);
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
        Assert.AreEqual(Path.Combine(state, "data", "alta.sqlite3"), options.ApplicationDatabasePath);
        Assert.AreEqual(Path.Combine(state, "data", "backups"), options.ApplicationDatabaseBackupRoot);
        Assert.AreEqual(Path.Combine(state, "cache", "cache.sqlite3"), options.LegacySessionCacheDatabasePath);
        Assert.AreEqual(Path.Combine(state, "ui-state.yaml"), options.UiStatePath);
        Assert.AreEqual(Path.Combine(state, "saved_prompts"), options.PromptDraftsRoot);
        Assert.AreEqual(Path.Combine(state, "threads", "internal"), options.InternalSessionsRoot);
        // Shared with the instance that owns the global root.
        Assert.AreEqual(Path.Combine(global, "config.toml"), options.ConfigPath);
        Assert.AreEqual(Path.Combine(global, "projects"), options.ProjectsRoot);
        Assert.AreEqual(Path.Combine(global, "cache"), options.CacheRoot);
    }

    [TestMethod]
    public async Task SeparateStateRoot_StartsFromTheOwnersProjectPreferencesOnce()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-state-seed-" + Guid.NewGuid().ToString("N"));
        var global = Path.Combine(root, "alta");
        var state = Path.Combine(global, "dev");
        Directory.CreateDirectory(state);
        try
        {
            var owner = new SessionViewCatalog(new CatalogOptions { GlobalRoot = global });
            var theirs = new SessionViewViewState { OpenSessionIds = ["their-session"] };
            theirs.ProjectPreferences["project-1"] = new SessionViewPreference { ProviderKey = "codex", ModelId = "model-a", ReasoningEffort = AgentReasoningEffort.High };
            theirs.Navigator.ThemeSchemeName = "Dark Soft";
            await owner.SaveViewStateAsync(theirs);
            var developer = new SessionViewCatalog(new CatalogOptions { GlobalRoot = global, StateRoot = state });

            Assert.IsTrue(await developer.SeedViewStateFromAsync(owner));

            var mine = await developer.LoadViewStateAsync();
            Assert.AreEqual("model-a", mine.ProjectPreferences["project-1"].ModelId);
            Assert.AreEqual("codex", mine.ProjectPreferences["project-1"].ProviderKey);
            Assert.AreEqual("Dark Soft", mine.Navigator.ThemeSchemeName);
            // Sessions of the owner are not this instance's sessions.
            Assert.AreEqual(0, mine.OpenSessionIds.Count);

            // Its own later choices are kept: seeding happens once.
            mine.ProjectPreferences["project-1"].ModelId = "model-b";
            await developer.SaveViewStateAsync(mine);
            Assert.IsFalse(await developer.SeedViewStateFromAsync(owner));
            Assert.AreEqual("model-b", (await developer.LoadViewStateAsync()).ProjectPreferences["project-1"].ModelId);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch (IOException) { } }
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
