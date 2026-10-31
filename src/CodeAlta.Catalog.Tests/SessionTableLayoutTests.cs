using System.Text.Json;
using System.Text.Json.Nodes;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using Microsoft.Data.Sqlite;

namespace CodeAlta.Catalog.Tests;

/// <summary>
/// The list of sessions keeps short values only: the last answer as a preview, kept once, and no state JSON. The state of the
/// provider and the last state record of a session are in the detail table, read for that session alone.
/// </summary>
[TestClass]
public sealed class SessionTableLayoutTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly string LongAnswer = "The answer starts here. " + new string('a', 4000);

    [TestMethod]
    public async Task TheListTable_KeepsShortValues_AndOneSessionGetsItsStatesFromTheDetailTable()
    {
        using var root = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = root.Path };
        var descriptor = await CreateSessionAsync(options, "session-layout");

        AssertListTableHoldsNoState(options);
        var (summary, summaryJson) = ReadSummaryColumns(options, "session-layout");
        Assert.IsLessThanOrEqualTo(SessionJournalSqliteCache.MaximumSummaryPreviewLength, summary.Length);
        StringAssert.StartsWith(summary, "The answer starts here.");
        Assert.IsNull(JsonNode.Parse(summaryJson)!["summary"], "The answer is kept once, in the summary column.");

        // The list gives the preview and the session of the provider; one session gives the whole state of its provider.
        var store = new SessionViewCatalog(options).JournalStore.CreateSessionStore();
        var listed = (await store.ListSessionsAsync().ToArrayAsync()).Single();
        Assert.AreEqual(summary, listed.Summary);
        Assert.AreEqual("provider-session", ((RawApiSessionMetadataDetails)listed.Details!).ProviderSessionId);
        var state = await store.GetStateAsync(descriptor.SessionId);
        Assert.AreEqual("resume-token", state!.ProviderState!.Value.GetProperty("resume").GetString());
        Assert.AreEqual("hash-1", state.InstructionHash);
    }

    [TestMethod]
    public async Task ACacheOfVersion7_MovesItsStatesToTheDetailTable_AndTrimsThemWithoutReadingAJournal()
    {
        using var root = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = root.Path };
        var descriptor = await CreateSessionAsync(options, "session-v7");

        // The layout of version 7: the states in the row, the whole answer twice, and a last state record of an older journal
        // that holds every handled prompt whole.
        var handled = Enumerable.Range(0, 60).Select(static index => new SessionViewQueuedPrompt
        {
            QueueItemId = "prompt-" + index,
            Kind = "prompt",
            Prompt = new string('p', 2000),
            PromptPreview = "Prompt " + index,
            State = "submitted",
            CreatedAt = CreatedAt,
        }).ToList();
        var oldState = JsonSerializer.Serialize(
            new SessionViewLocalState { ProviderKey = "openai", ModelId = "model-7", QueuedPrompts = handled },
            SessionViewJournalJsonSerializerContext.Default.SessionViewLocalState);
        var oldSummaryJson = JsonNode.Parse(ReadSummaryColumns(options, "session-v7").SummaryJson)!;
        oldSummaryJson["summary"] = LongAnswer;
        await ExecuteAsync(options, """
            ALTER TABLE session_projection_cache ADD COLUMN state_json TEXT;
            ALTER TABLE session_projection_cache ADD COLUMN local_state_json TEXT;
            UPDATE session_projection_cache
            SET state_json = (SELECT state_json FROM session_projection_cache_detail d WHERE d.session_id = session_projection_cache.session_id),
                local_state_json = $state,
                summary = $answer,
                summary_json = $summary_json;
            DROP TABLE session_projection_cache_detail;
            UPDATE app_meta SET version = 7 WHERE owner = 'session_cache';
            """, ("$state", oldState), ("$answer", LongAnswer), ("$summary_json", oldSummaryJson.ToJsonString()));

        var journal = new SessionViewCatalog(options).JournalStore;
        var store = journal.CreateSessionStore();
        var listed = (await store.ListSessionsAsync().ToArrayAsync()).Single();
        await using (var database = ApplicationDatabase.Create(options))
        {
            Assert.AreEqual(11, await database.GetVersionAsync("session_cache"));
        }

        AssertListTableHoldsNoState(options);
        Assert.IsLessThanOrEqualTo(SessionJournalSqliteCache.MaximumSummaryPreviewLength, listed.Summary!.Length);
        Assert.IsNull(JsonNode.Parse(ReadSummaryColumns(options, "session-v7").SummaryJson)!["summary"]);

        // The state record was trimmed as the journal keeps it, and is read from the detail table, not from the journal.
        var trimmed = ReadDetail(options, "session-v7").LocalState!;
        Assert.IsLessThan(oldState.Length / 10, trimmed.Length, trimmed);
        var latest = await journal.ReadLatestStateAsync(descriptor.SessionId, descriptor.CreatedAt);
        Assert.AreEqual(0, journal.JournalStateReadCount);
        Assert.AreEqual("model-7", latest!.ModelId);
        Assert.HasCount(SessionViewJournalStore.MaxHandledQueuedPromptRecords, latest.QueuedPrompts);
        Assert.AreEqual("resume-token", (await store.GetStateAsync(descriptor.SessionId))!.ProviderState!.Value.GetProperty("resume").GetString());
    }

    private static async Task<SessionViewDescriptor> CreateSessionAsync(CatalogOptions options, string sessionId)
    {
        var catalog = new SessionViewCatalog(options);
        var session = new SessionViewDescriptor
        {
            SessionId = sessionId,
            Kind = SessionViewKind.GlobalSession,
            ProviderId = "openai",
            ProviderKey = "openai",
            WorkingDirectory = Path.GetTempPath(),
            Title = sessionId,
            CreatedAt = CreatedAt,
            UpdatedAt = CreatedAt,
        };
        await catalog.JournalStore.EnsureHeaderAsync(session);
        var store = catalog.JournalStore.CreateSessionStore();
        await store.UpsertSessionAsync(new AgentSessionSummary
        {
            SessionId = sessionId,
            ProviderId = new ModelProviderId("openai"),
            ProtocolFamily = "openai",
            ProviderKey = "openai",
            ModelId = "model-1",
            WorkingDirectory = Path.GetTempPath(),
            Title = sessionId,
            Summary = LongAnswer,
            CreatedAt = CreatedAt,
            UpdatedAt = CreatedAt,
        });
        using var providerState = JsonDocument.Parse("""{"resume":"resume-token"}""");
        await store.UpsertStateAsync(new AgentSessionState
        {
            SessionId = sessionId,
            ProtocolFamily = "openai",
            ProviderKey = "openai",
            ProviderSessionId = "provider-session",
            InstructionHash = "hash-1",
            ProviderState = providerState.RootElement.Clone(),
            UpdatedAt = CreatedAt,
        });
        await catalog.JournalStore.AppendStateAsync(session, new SessionViewLocalState { ProviderKey = "openai", ModelId = "model-7" });
        return session;
    }

    private static void AssertListTableHoldsNoState(CatalogOptions options)
    {
        using var connection = Open(options);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info('session_projection_cache') WHERE name IN ('state_json', 'local_state_json');";
        Assert.IsNull(command.ExecuteScalar(), "The list table holds no state JSON.");
    }

    private static (string Summary, string SummaryJson) ReadSummaryColumns(CatalogOptions options, string sessionId)
    {
        using var connection = Open(options);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT summary, summary_json FROM session_projection_cache WHERE session_id = $id;";
        command.Parameters.AddWithValue("$id", sessionId);
        using var reader = command.ExecuteReader();
        Assert.IsTrue(reader.Read());
        return (reader.GetString(0), reader.GetString(1));
    }

    private static (string? State, string? LocalState) ReadDetail(CatalogOptions options, string sessionId)
    {
        using var connection = Open(options);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT state_json, local_state_json FROM session_projection_cache_detail WHERE session_id = $id;";
        command.Parameters.AddWithValue("$id", sessionId);
        using var reader = command.ExecuteReader();
        Assert.IsTrue(reader.Read(), "The session has a detail row.");
        return (reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    private static SqliteConnection Open(CatalogOptions options)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = options.ApplicationDatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static async Task ExecuteAsync(CatalogOptions options, string sql, params (string Name, string Value)[] parameters)
    {
        await using var connection = new SqliteConnection($"Data Source={options.ApplicationDatabasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path) => Path = path;

        public string Path { get; }

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codealta-table-layout-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TempDirectory(path);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }
}
