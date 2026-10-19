using System.Text;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using Microsoft.Data.Sqlite;

namespace CodeAlta.Tests;

// What a durable write of a session costs: the journal is not read again after an append, the cache keeps the view it
// read, and sessions that write at the same time share their cache writes. What is read must stay what a full read gives.
[TestClass]
public sealed class SessionJournalWritePathTests
{
    [TestMethod]
    public async Task AppendLinesTrackedAsync_ReportsTheStampsAroundTheAppendAndTheBytesItWrote()
    {
        using var temp = TestTempDirectory.Create();
        var path = Path.Combine(temp.Path, "journal.jsonl");
        var file = new AgentSessionJournalFile();
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        JournalAppend first = default, second = default;

        await file.AppendLinesTrackedAsync(path, ["{\"a\":1}"], encoding, append => first = append, CancellationToken.None).ConfigureAwait(false);
        await file.AppendLinesTrackedAsync(path, ["{\"b\":\"é — \U0001F600\"}", "{\"c\":3}"], encoding, append => second = append, CancellationToken.None).ConfigureAwait(false);

        Assert.IsNull(first.Before, "The journal did not exist before its first append.");
        Assert.IsFalse(first.IsContinuous);
        Assert.IsTrue(second.IsContinuous, "Nothing else wrote between the two stamps.");
        Assert.AreEqual(first.After, second.Before);
        Assert.AreEqual(new FileInfo(path).Length, second.After!.Value.Length);
        Assert.AreEqual(second.Before!.Value.Length + second.Written, second.After.Value.Length);
    }

    [TestMethod]
    public void JournalAppend_IsNotContinuousWhenTheJournalGrewByMoreThanTheAppend()
    {
        var before = new JournalStamp(new DateTime(2026, 10, 18, 10, 0, 0, DateTimeKind.Utc), 100);
        var after = new JournalStamp(new DateTime(2026, 10, 18, 10, 0, 1, DateTimeKind.Utc), 150);

        Assert.IsTrue(new JournalAppend(before, after, 50).IsContinuous);
        Assert.IsFalse(new JournalAppend(before, after, 40).IsContinuous, "Another writer appended 10 bytes meanwhile.");
        Assert.IsFalse(new JournalAppend(before, null, 50).IsContinuous, "The journal went away.");
        Assert.IsFalse(new JournalAppend(null, after, 150).IsContinuous, "The journal did not exist before.");
    }

    [TestMethod]
    public async Task Store_AfterAppends_GivesTheProjectionAFreshReadOfTheJournalGives()
    {
        using var temp = TestTempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var store = new SessionViewCatalog(options).JournalStore.CreateSessionStore();
        var summary = CreateSummary("session-advance") with { ParentSessionId = "parent-1", CreatedBySessionId = "creator-1" };

        await store.UpsertSessionAsync(summary).ConfigureAwait(false);
        await store.UpsertStateAsync(CreateState(summary, "resp-1")).ConfigureAwait(false);
        Assert.AreEqual("resp-1", ProviderSessionId(await store.GetSessionAsync(summary.SessionId).ConfigureAwait(false)));
        await store.AppendEventsAsync("openai", "openai", summary.SessionId, [UserMessage(summary, "u1", "hello"), AssistantMessage(summary, "a1", "world")]).ConfigureAwait(false);
        // A summary written without its lineage keeps the lineage the journal records.
        await store.UpsertSessionAsync(summary with { ParentSessionId = null, CreatedBySessionId = null, Title = "Renamed", UpdatedAt = summary.UpdatedAt.AddMinutes(5) }).ConfigureAwait(false);
        await store.UpsertStateAsync(CreateState(summary, "resp-2")).ConfigureAwait(false);
        await store.AppendEventsAsync("openai", "openai", summary.SessionId, [AssistantMessage(summary, "a2", "again")]).ConfigureAwait(false);

        var kept = await store.GetSessionAsync(summary.SessionId).ConfigureAwait(false);
        var fresh = await new FileSystemAgentSessionStore(new AgentRuntimePathLayout(options.StateRoot)).GetSessionAsync(summary.SessionId).ConfigureAwait(false);
        var listed = (await new SessionViewCatalog(options).JournalStore.CreateSessionStore().ListSessionsAsync().ToArrayAsync().ConfigureAwait(false)).Single();

        Assert.IsNotNull(fresh);
        foreach (var session in new[] { kept, listed })
        {
            Assert.IsNotNull(session);
            Assert.AreEqual(fresh.Title(), session.Title());
            Assert.AreEqual("Renamed", session.Title());
            Assert.AreEqual(fresh.ParentSessionId, session.ParentSessionId);
            Assert.AreEqual("parent-1", session.ParentSessionId);
            Assert.AreEqual(fresh.CreatedBySessionId, session.CreatedBySessionId);
            Assert.AreEqual(fresh.UpdatedAt, session.UpdatedAt);
            Assert.AreEqual(ProviderSessionId(fresh), ProviderSessionId(session));
            Assert.AreEqual("resp-2", ProviderSessionId(session));
        }
    }

    [TestMethod]
    public async Task Store_AfterAnAppend_AnswersFromWhatItKeptWithoutReadingTheJournal()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("A journal that can be appended to but not read needs Unix permissions.");
            return;
        }

        await AnswersWithoutReadingAsync().ConfigureAwait(false);
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static async Task AnswersWithoutReadingAsync()
    {
        using var temp = TestTempDirectory.Create();
        var layout = new AgentRuntimePathLayout(temp.Path);
        var store = new FileSystemAgentSessionStore(layout);
        var summary = CreateSummary("session-unreadable");
        await store.UpsertSessionAsync(summary).ConfigureAwait(false);
        await store.UpsertStateAsync(CreateState(summary, "resp-1")).ConfigureAwait(false);
        Assert.AreEqual("SQLite cache test", (await store.GetSessionAsync(summary.SessionId).ConfigureAwait(false))?.Title());

        var path = layout.GetSessionFilePath(summary.SessionId, summary.CreatedAt);
        var mode = File.GetUnixFileMode(path);
        File.SetUnixFileMode(path, UnixFileMode.UserWrite);
        try
        {
            await store.AppendEventsAsync("openai", "openai", summary.SessionId, [AssistantMessage(summary, "a1", "text")]).ConfigureAwait(false);
            await store.UpsertSessionAsync(summary with { Title = "Written while unreadable", UpdatedAt = summary.UpdatedAt.AddMinutes(1) }).ConfigureAwait(false);
            await store.UpsertStateAsync(CreateState(summary, "resp-2")).ConfigureAwait(false);

            var session = await store.GetSessionAsync(summary.SessionId).ConfigureAwait(false);
            Assert.AreEqual("Written while unreadable", session?.Title());
            Assert.AreEqual("resp-2", ProviderSessionId(session));
        }
        finally
        {
            File.SetUnixFileMode(path, mode);
        }
    }

    [TestMethod]
    public async Task Store_AfterAnotherWriterAppended_ReadsTheJournalAgain()
    {
        using var temp = TestTempDirectory.Create();
        var layout = new AgentRuntimePathLayout(temp.Path);
        // Two stores with journal locks of their own, as the runtime of a session and a catalog of the host have.
        var store = new FileSystemAgentSessionStore(layout);
        var other = new FileSystemAgentSessionStore(layout);
        var summary = CreateSummary("session-two-writers");
        await store.UpsertSessionAsync(summary).ConfigureAwait(false);
        Assert.AreEqual("SQLite cache test", (await store.GetSessionAsync(summary.SessionId).ConfigureAwait(false))?.Title());

        await other.UpsertSessionAsync(summary with { Title = "Written by the other store", UpdatedAt = summary.UpdatedAt.AddMinutes(1) }).ConfigureAwait(false);
        await store.AppendEventsAsync("openai", "openai", summary.SessionId, [AssistantMessage(summary, "a1", "text")]).ConfigureAwait(false);

        Assert.AreEqual("Written by the other store", (await store.GetSessionAsync(summary.SessionId).ConfigureAwait(false))?.Title());
    }

    [TestMethod]
    public async Task Cache_KeepsTheViewItReadAcrossAppends_AndReadsItAgainAfterAViewStateIsWritten()
    {
        using var temp = TestTempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var catalog = new SessionViewCatalog(options);
        var store = catalog.JournalStore.CreateSessionStore();
        var summary = CreateSummary("session-view");
        var descriptor = CreateDescriptor(summary);
        await catalog.JournalStore.EnsureHeaderAsync(descriptor).ConfigureAwait(false);
        await store.UpsertSessionAsync(summary).ConfigureAwait(false);
        await catalog.JournalStore.AppendStateAsync(descriptor, new SessionViewLocalState { ModelId = "model-1" }).ConfigureAwait(false);
        await store.AppendEventsAsync("openai", "openai", summary.SessionId, [UserMessage(summary, "u1", "one")]).ConfigureAwait(false);
        await store.AppendEventsAsync("openai", "openai", summary.SessionId, [AssistantMessage(summary, "a1", "two")]).ConfigureAwait(false);

        var path = new AgentRuntimePathLayout(options.StateRoot).GetSessionFilePath(summary.SessionId, summary.CreatedAt);
        var row = ReadRow(options.ApplicationDatabasePath, summary.SessionId);
        Assert.AreEqual("model-1", row.ModelId);
        Assert.AreEqual(new FileInfo(path).Length, row.Length, "The row is of the journal as it is.");
        Assert.AreEqual("project-cache-test", row.ProjectRef);

        await catalog.JournalStore.AppendStateAsync(descriptor, new SessionViewLocalState { ModelId = "model-2" }).ConfigureAwait(false);
        await store.AppendEventsAsync("openai", "openai", summary.SessionId, [AssistantMessage(summary, "a2", "three")]).ConfigureAwait(false);

        row = ReadRow(options.ApplicationDatabasePath, summary.SessionId);
        Assert.AreEqual("model-2", row.ModelId, "The view state written since was read again.");
        Assert.AreEqual(new FileInfo(path).Length, row.Length);
        var listed = (await new SessionViewCatalog(options).JournalStore.CreateSessionStore().ListSessionsAsync().ToArrayAsync().ConfigureAwait(false)).Single();
        Assert.AreEqual("model-2", listed.ViewState?.ModelId);
    }

    [TestMethod]
    public async Task Cache_WritesTheUpsertsOfSessionsThatWriteAtTheSameTime()
    {
        using var temp = TestTempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var catalog = new SessionViewCatalog(options);
        var summaries = Enumerable.Range(0, 24).Select(index => CreateSummary($"session-batch-{index:00}")).ToArray();

        // Each session writes through a store of its own, as each runtime does, all at once and several times.
        await Task.WhenAll(summaries.Select(summary => Task.Run(async () =>
        {
            var store = catalog.JournalStore.CreateSessionStore();
            await store.UpsertSessionAsync(summary).ConfigureAwait(false);
            for (var turn = 0; turn < 5; turn++)
            {
                await store.AppendEventsAsync("openai", "openai", summary.SessionId, [AssistantMessage(summary, $"a{turn}", $"turn {turn}")]).ConfigureAwait(false);
                await store.UpsertSessionAsync(summary with { Title = $"{summary.SessionId} turn {turn}", UpdatedAt = summary.UpdatedAt.AddMinutes(turn + 1) }).ConfigureAwait(false);
            }
        }))).ConfigureAwait(false);

        var layout = new AgentRuntimePathLayout(options.StateRoot);
        foreach (var summary in summaries)
        {
            var row = ReadRow(options.ApplicationDatabasePath, summary.SessionId);
            Assert.AreEqual($"{summary.SessionId} turn 4", row.Title);
            Assert.AreEqual(new FileInfo(layout.GetSessionFilePath(summary.SessionId, summary.CreatedAt)).Length, row.Length);
        }
    }

    private static string? ProviderSessionId(AgentSessionMetadata? session)
        => (session?.Details as RawApiSessionMetadataDetails)?.ProviderSessionId;

    private static (string? Title, string? ModelId, string? ProjectRef, long Length) ReadRow(string databasePath, string sessionId)
        => TryReadRow(databasePath, sessionId) ?? throw new AssertFailedException($"No row for {sessionId}.");

    private static (string? Title, string? ModelId, string? ProjectRef, long Length)? TryReadRow(string databasePath, string sessionId)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT title, local_model_id, project_ref, journal_length FROM session_projection_cache WHERE session_id = $id;";
        command.Parameters.AddWithValue("$id", sessionId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return (reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetInt64(3));
    }

    private static AgentEvent UserMessage(AgentSessionSummary summary, string contentId, string text)
        => new AgentContentCompletedEvent(summary.ProviderId, summary.SessionId, summary.UpdatedAt, new AgentRunId("run-1"),
            AgentContentKind.User, contentId, null, text);

    private static AgentEvent AssistantMessage(AgentSessionSummary summary, string contentId, string text)
        => new AgentContentCompletedEvent(summary.ProviderId, summary.SessionId, summary.UpdatedAt, new AgentRunId("run-1"),
            AgentContentKind.Assistant, contentId, null, text);

    private static AgentSessionSummary CreateSummary(string sessionId)
        => new()
        {
            SessionId = sessionId,
            ProviderId = ModelProviderIds.OpenAIResponses,
            ProtocolFamily = "openai",
            ProviderKey = "openai",
            ModelId = "gpt-5",
            ReasoningEffort = AgentReasoningEffort.Medium,
            AgentPromptId = "default",
            WorkingDirectory = Path.Combine(Path.GetTempPath(), "write-path-tests"),
            Title = "SQLite cache test",
            Summary = "cached summary",
            CreatedAt = DateTimeOffset.Parse("2026-06-18T09:00:00+00:00"),
            UpdatedAt = DateTimeOffset.Parse("2026-06-18T10:00:00+00:00"),
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

internal static class SessionMetadataTestExtensions
{
    public static string? Title(this AgentSessionMetadata session)
        => (session.Details as RawApiSessionMetadataDetails)?.Title;
}
