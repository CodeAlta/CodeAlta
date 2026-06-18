using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using Microsoft.Data.Sqlite;

namespace CodeAlta.Tests;

[TestClass]
public sealed class SessionJournalSqliteCacheTests
{
    [TestMethod]
    public async Task ListSessionsAsync_RebuildsMissingDatabaseAndUsesHotCacheWithoutParsingJournal()
    {
        using var temp = TestTempDirectory.Create();
        var options = CreateOptions(temp.Path);
        var catalog = new SessionViewCatalog(options);
        var store = catalog.JournalStore.CreateSessionStore();
        var session = CreateSummary("session-hot", updatedAt: "2026-06-18T10:00:00+00:00");
        await store.UpsertSessionAsync(session).ConfigureAwait(false);
        await store.UpsertStateAsync(CreateState(session, "resp_hot")).ConfigureAwait(false);
        File.Delete(options.SessionCacheDatabasePath);

        var rebuiltCatalog = new SessionViewCatalog(options);
        var rebuiltStore = rebuiltCatalog.JournalStore.CreateSessionStore();
        var rebuilt = await rebuiltStore.ListSessionsAsync().ToArrayAsync().ConfigureAwait(false);
        Assert.AreEqual(1, rebuilt.Length);
        Assert.AreEqual("session-hot", rebuilt[0].SessionId);
        Assert.IsTrue(File.Exists(options.SessionCacheDatabasePath));

        var journalPath = new AgentRuntimePathLayout(temp.Path).GetSessionFilePath(session.SessionId, session.CreatedAt);
        await File.AppendAllTextAsync(journalPath, Environment.NewLine + "{not-json" + Environment.NewLine).ConfigureAwait(false);

        var hotCatalog = new SessionViewCatalog(options);
        var hotStore = hotCatalog.JournalStore.CreateSessionStore();
        var hot = await hotStore.ListSessionsAsync().ToArrayAsync().ConfigureAwait(false);

        Assert.AreEqual(1, hot.Length, "A healthy hot cache should not parse the changed journal before reconciliation.");
        Assert.AreEqual("resp_hot", ((RawApiSessionMetadataDetails?)hot[0].Details)?.ProviderSessionId);
    }

    [TestMethod]
    public async Task ListSessionsAsync_RecreatesCorruptDatabaseFromJournals()
    {
        using var temp = TestTempDirectory.Create();
        var options = CreateOptions(temp.Path);
        var session = CreateSummary("session-corrupt-db", updatedAt: "2026-06-18T11:00:00+00:00");
        var uncachedStore = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(temp.Path));
        await uncachedStore.UpsertSessionAsync(session).ConfigureAwait(false);
        Directory.CreateDirectory(options.CacheRoot);
        await File.WriteAllTextAsync(options.SessionCacheDatabasePath, "not a sqlite database").ConfigureAwait(false);

        var catalog = new SessionViewCatalog(options);
        var sessions = await catalog.JournalStore.CreateSessionStore().ListSessionsAsync().ToArrayAsync().ConfigureAwait(false);

        Assert.AreEqual(1, sessions.Length);
        Assert.AreEqual("session-corrupt-db", sessions[0].SessionId);
    }

    [TestMethod]
    public async Task ListSessionsAsync_ThrowsWhenDatabaseIsLocked()
    {
        using var temp = TestTempDirectory.Create();
        var options = CreateOptions(temp.Path);
        var catalog = new SessionViewCatalog(options);
        _ = await catalog.JournalStore.CreateSessionStore().ListSessionsAsync().ToArrayAsync().ConfigureAwait(false);

        await using (var lockConnection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = options.SessionCacheDatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            DefaultTimeout = 1,
            Pooling = false,
        }.ToString()))
        {
            await lockConnection.OpenAsync().ConfigureAwait(false);
            await using var lockCommand = lockConnection.CreateCommand();
            lockCommand.CommandText = "BEGIN EXCLUSIVE;";
            await lockCommand.ExecuteNonQueryAsync().ConfigureAwait(false);

            var lockedCatalog = new SessionViewCatalog(options);
            await Assert.ThrowsExactlyAsync<AgentSessionCacheLockedException>(async () =>
                _ = await lockedCatalog.JournalStore.CreateSessionStore().ListSessionsAsync().ToArrayAsync().ConfigureAwait(false)).ConfigureAwait(false);

            await using var rollbackCommand = lockConnection.CreateCommand();
            rollbackCommand.CommandText = "ROLLBACK;";
            await rollbackCommand.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        SqliteConnection.ClearAllPools();
        Assert.IsTrue(File.Exists(options.SessionCacheDatabasePath));
    }

    [TestMethod]
    public async Task ListSessionsAsync_PrunesRowsWhoseJournalWasDeleted()
    {
        using var temp = TestTempDirectory.Create();
        var options = CreateOptions(temp.Path);
        var catalog = new SessionViewCatalog(options);
        var store = catalog.JournalStore.CreateSessionStore();
        var session = CreateSummary("session-stale", updatedAt: "2026-06-18T12:00:00+00:00");
        await store.UpsertSessionAsync(session).ConfigureAwait(false);
        Assert.AreEqual(1, (await store.ListSessionsAsync().ToArrayAsync().ConfigureAwait(false)).Length);

        var journalPath = new AgentRuntimePathLayout(temp.Path).GetSessionFilePath(session.SessionId, session.CreatedAt);
        File.Delete(journalPath);

        var afterDelete = await new SessionViewCatalog(options).JournalStore.CreateSessionStore().ListSessionsAsync().ToArrayAsync().ConfigureAwait(false);
        var secondRead = await new SessionViewCatalog(options).JournalStore.CreateSessionStore().ListSessionsAsync().ToArrayAsync().ConfigureAwait(false);

        Assert.AreEqual(0, afterDelete.Length);
        Assert.AreEqual(0, secondRead.Length, "The stale cache row should be deleted, not just filtered once.");
    }

    [TestMethod]
    public async Task ReconcileCacheAsync_ImportsExternalJournalAdditionsAndChanges()
    {
        using var temp = TestTempDirectory.Create();
        var options = CreateOptions(temp.Path);
        var layout = new AgentRuntimePathLayout(temp.Path);
        var cachedCatalog = new SessionViewCatalog(options);
        var cachedStore = cachedCatalog.JournalStore.CreateSessionStore();
        var original = CreateSummary("session-original", updatedAt: "2026-06-18T13:00:00+00:00");
        await cachedStore.UpsertSessionAsync(original).ConfigureAwait(false);
        Assert.AreEqual(1, (await cachedStore.ListSessionsAsync().ToArrayAsync().ConfigureAwait(false)).Length);

        var externalStore = new FileSystemAgentSessionStore(layout);
        var external = CreateSummary("session-external", updatedAt: "2026-06-18T13:05:00+00:00") with
        {
            Summary = "external first summary",
        };
        await externalStore.UpsertSessionAsync(external).ConfigureAwait(false);

        var beforeReconcile = await new SessionViewCatalog(options).JournalStore.CreateSessionStore().ListSessionsAsync().ToArrayAsync().ConfigureAwait(false);
        Assert.AreEqual(1, beforeReconcile.Length);

        var reconcileStore = new SessionViewCatalog(options).JournalStore.CreateSessionStore();
        var result = await reconcileStore.ReconcileCacheAsync().ConfigureAwait(false);
        Assert.IsTrue(result.Changed);

        var afterAdd = await new SessionViewCatalog(options).JournalStore.CreateSessionStore().ListSessionsAsync().ToArrayAsync().ConfigureAwait(false);
        CollectionAssert.AreEquivalent(new[] { "session-original", "session-external" }, afterAdd.Select(static item => item.SessionId).ToArray());

        var changed = external with
        {
            UpdatedAt = DateTimeOffset.Parse("2026-06-18T13:10:00+00:00"),
            Summary = "external changed summary",
        };
        await externalStore.UpsertSessionAsync(changed).ConfigureAwait(false);
        var changeResult = await reconcileStore.ReconcileCacheAsync().ConfigureAwait(false);
        var afterChange = await new SessionViewCatalog(options).JournalStore.CreateSessionStore().ListSessionsAsync().ToArrayAsync().ConfigureAwait(false);

        Assert.IsTrue(changeResult.Changed);
        Assert.AreEqual("external changed summary", afterChange.Single(static item => item.SessionId == "session-external").Summary);
    }

    [TestMethod]
    public async Task WriteThrough_ProjectsLocalStateLineageModelReasoningAndDelete()
    {
        using var temp = TestTempDirectory.Create();
        var options = CreateOptions(temp.Path);
        var catalog = new SessionViewCatalog(options);
        var store = catalog.JournalStore.CreateSessionStore();
        var session = CreateSummary("session-write-through", updatedAt: "2026-06-18T14:00:00+00:00") with
        {
            ModelId = "gpt-5.4",
            ReasoningEffort = AgentReasoningEffort.High,
            AgentPromptId = "default",
            ParentSessionId = "agent-parent",
            CreatedBySessionId = "agent-creator",
            CreatedByRunId = new AgentRunId("run-create"),
        };
        await store.UpsertSessionAsync(session).ConfigureAwait(false);
        await store.UpsertStateAsync(CreateState(session, "resp_write")).ConfigureAwait(false);
        var descriptor = CreateDescriptor(session);
        var createdBy = new AltaActorProvenance
        {
            Kind = "agent",
            SourceSessionId = "local-creator",
            CreatedAt = DateTimeOffset.Parse("2026-06-18T14:01:00+00:00"),
        };
        await catalog.JournalStore.AppendStateAsync(
                descriptor,
                new SessionViewLocalState
                {
                    ProviderKey = "local-provider",
                    ModelId = "local-model",
                    ReasoningEffort = AgentReasoningEffort.Low,
                    AgentPromptId = "local-prompt",
                    Archived = true,
                    MessageCount = 42,
                    ParentSessionId = "local-parent",
                    CreatedBy = createdBy,
                })
            .ConfigureAwait(false);

        var metadata = (await new SessionViewCatalog(options).JournalStore.CreateSessionStore().ListSessionsAsync().ToArrayAsync().ConfigureAwait(false)).Single();
        Assert.AreEqual(AgentReasoningEffort.High, metadata.ReasoningEffort);
        Assert.AreEqual("gpt-5.4", metadata.ModelId);
        Assert.AreEqual("agent-parent", metadata.ParentSessionId);
        Assert.AreEqual("agent-creator", metadata.CreatedBySessionId);
        Assert.AreEqual(new AgentRunId("run-create"), metadata.CreatedByRunId);
        Assert.AreEqual("resp_write", ((RawApiSessionMetadataDetails?)metadata.Details)?.ProviderSessionId);
        Assert.IsNotNull(metadata.ViewState);
        Assert.AreEqual("local-provider", metadata.ViewState.ProviderKey);
        Assert.AreEqual("local-model", metadata.ViewState.ModelId);
        Assert.AreEqual(AgentReasoningEffort.Low, metadata.ViewState.ReasoningEffort);
        Assert.AreEqual("local-prompt", metadata.ViewState.AgentPromptId);
        Assert.IsTrue(metadata.ViewState.Archived);
        Assert.AreEqual(42, metadata.ViewState.MessageCount);
        Assert.AreEqual("local-parent", metadata.ViewState.ParentSessionId);
        StringAssert.Contains(metadata.ViewState.CreatedByJson, "local-creator");

        Assert.IsTrue(await store.DeleteSessionAsync(session.SessionId).ConfigureAwait(false));
        Assert.AreEqual(0, (await new SessionViewCatalog(options).JournalStore.CreateSessionStore().ListSessionsAsync().ToArrayAsync().ConfigureAwait(false)).Length);
    }

    [TestMethod]
    public async Task ListSessionsAsync_ToleratesCorruptJournalsDuringRebuild()
    {
        using var temp = TestTempDirectory.Create();
        var options = CreateOptions(temp.Path);
        var layout = new AgentRuntimePathLayout(temp.Path);
        var corruptPath = layout.GetSessionFilePath("session-corrupt-journal", DateTimeOffset.Parse("2026-06-18T15:00:00+00:00"));
        Directory.CreateDirectory(Path.GetDirectoryName(corruptPath)!);
        await File.WriteAllTextAsync(corruptPath, "not-json" + Environment.NewLine + "also-not-json").ConfigureAwait(false);

        var sessions = await new SessionViewCatalog(options).JournalStore.CreateSessionStore().ListSessionsAsync().ToArrayAsync().ConfigureAwait(false);

        Assert.AreEqual(0, sessions.Length);
        Assert.IsTrue(File.Exists(options.SessionCacheDatabasePath));
    }

    private static CatalogOptions CreateOptions(string globalRoot)
        => new() { GlobalRoot = globalRoot };

    private static AgentSessionSummary CreateSummary(string sessionId, string updatedAt)
        => new()
        {
            SessionId = sessionId,
            ProviderId = ModelProviderIds.OpenAIResponses,
            ProtocolFamily = "openai",
            ProviderKey = "openai",
            ModelId = "gpt-5",
            ReasoningEffort = AgentReasoningEffort.Medium,
            AgentPromptId = "default",
            WorkingDirectory = @"C:\repo\sqlite-cache-tests",
            Title = "SQLite cache test",
            Summary = "cached summary",
            CreatedAt = DateTimeOffset.Parse("2026-06-18T09:00:00+00:00"),
            UpdatedAt = DateTimeOffset.Parse(updatedAt),
        };

    private static AgentSessionState CreateState(AgentSessionSummary summary, string providerSessionId)
        => new()
        {
            SessionId = summary.SessionId,
            ProtocolFamily = summary.ProtocolFamily,
            ProviderKey = summary.ProviderKey,
            ProviderSessionId = providerSessionId,
            UpdatedAt = summary.UpdatedAt.AddMinutes(1),
        };

    private static SessionViewDescriptor CreateDescriptor(AgentSessionSummary summary)
        => new()
        {
            SessionId = summary.SessionId,
            Kind = SessionViewKind.ProjectSession,
            ProviderId = summary.ProviderId.Value,
            ProviderKey = summary.ProviderKey,
            ProjectRef = "project-cache-test",
            ParentSessionId = summary.ParentSessionId,
            WorkingDirectory = summary.WorkingDirectory!,
            Title = summary.Title!,
            Status = SessionViewStatus.Active,
            CreatedAt = summary.CreatedAt,
            UpdatedAt = summary.UpdatedAt,
            LastActiveAt = summary.UpdatedAt,
            StartedAt = summary.CreatedAt,
            LatestSummary = summary.Summary,
            ModelId = summary.ModelId,
            ReasoningEffort = summary.ReasoningEffort,
            AgentPromptId = summary.AgentPromptId,
        };
}
