using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using Microsoft.Data.Sqlite;

namespace CodeAlta.Catalog.Tests;

// The row of a session in the list of sessions holds its last state: the state is read from there, never from the journal,
// and the journal stays the record the row is made again from.
[TestClass]
public sealed class SessionStateRowTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 10, 24, 8, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    [TestMethod]
    public async Task ANewStore_ReadsAndChangesTheStateOfASession_WithoutReadingItsJournal()
    {
        using var root = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = root.Path };
        var session = await CreateSessionAsync(options, "session-row");
        var path = JournalPath(options, session);

        var mutations = new (string Name, Action<SessionViewLocalState> Change)[]
        {
            ("archive", static state => { state.Archived = true; state.ArchivedAt = CreatedAt.AddHours(1); }),
            ("restore", static state => { state.Archived = false; state.ArchivedAt = null; }),
            ("model", static state => { state.ProviderKey = "claude"; state.ModelId = "opus"; state.ReasoningEffort = AgentReasoningEffort.High; }),
            ("tier", static state => state.Tier = "quick"),
            ("title", static state => state.TitleMode = "last_message"),
            ("permission", static state => state.PermissionMode = "plan"),
            ("queue", static state => state.QueuedPrompts.Add(Queued("later", "queued"))),
            ("submitting", static state => state.QueuedPrompts.Single(static prompt => prompt.QueueItemId == "later").State = "submitting"),
            ("drained", static state =>
            {
                var prompt = state.QueuedPrompts.Single(static prompt => prompt.QueueItemId == "later");
                prompt.State = "submitted";
                prompt.RunId = "run-later";
                prompt.DrainedAt = CreatedAt.AddHours(2);
            }),
        };

        foreach (var (name, change) in mutations)
        {
            // What the journal holds is made unreadable, with its stamp kept: a read of the journal finds no state.
            BlankJournal(path);

            // A new store each time, as after a start of the application: nothing is remembered in memory.
            var restarted = new SessionViewCatalog(options).JournalStore;
            var state = await restarted.ReadLatestStateAsync(session.SessionId, session.CreatedAt).WaitAsync(Deadline);
            Assert.IsNotNull(state, name + ": the state is read from the row.");
            Assert.AreEqual(0, restarted.JournalStateReadCount, name);

            change(state);
            var expected = Serialize(SessionViewJournalStore.CompactForJournal(state));
            await restarted.AppendStateAsync(session, state).WaitAsync(Deadline);

            // The journal takes the record: it stays the record of the session.
            AssertSameJson(expected, LastStateRecord(path), name + ": the journal holds the state written.");
            AssertSameJson(expected, ReadRowState(options, session.SessionId), name + ": the row holds the state written.");
            Assert.IsTrue(File.ReadAllBytes(path).AsSpan(0, BlankLength(path)).IndexOfAnyExcept((byte)' ', (byte)'\n', (byte)'\r') < 0,
                name + ": the journal was appended to, never read and written again.");

            BlankJournal(path);
            var read = await new SessionViewCatalog(options).JournalStore.ReadLatestStateAsync(session.SessionId, session.CreatedAt).WaitAsync(Deadline);
            AssertSameJson(expected, Serialize(read!), name + ": the next start reads the state written.");
        }
    }

    [TestMethod]
    public async Task TheRow_HoldsTheLastStateRecordOfTheJournal_AfterEachKindOfWrite()
    {
        using var root = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = root.Path };
        var catalog = new SessionViewCatalog(options);
        var session = await CreateSessionAsync(options, "session-source");
        AssertRowIsTheJournal(options, session, "a state appended");

        // A fork starts with the choices of its source.
        var fork = CreateDescriptor("session-fork");
        fork.ParentSessionId = "session-parent";
        await catalog.JournalStore.ForkAsync(session, fork, beforeRunId: null, activeRunId: null);
        AssertRowIsTheJournal(options, fork, "a fork");

        // An imported conversation starts with the state it is given.
        var imported = CreateDescriptor("session-imported");
        await catalog.JournalStore.ImportAsync(imported, CreateSummary(imported.SessionId), CreateAgentState(imported.SessionId), [],
            new SessionViewLocalState { ProviderKey = "codex", ModelId = "imported-model", ParentSessionId = "session-parent" }, CancellationToken.None);
        AssertRowIsTheJournal(options, imported, "an import");

        // A row projected from its journal, as when the list is made again, holds it too.
        File.Delete(options.ApplicationDatabasePath);
        var listed = await new SessionViewCatalog(options).JournalStore.CreateSessionStore().ListSessionsAsync().ToArrayAsync();
        Assert.HasCount(3, listed);
        foreach (var descriptor in new[] { session, fork, imported })
        {
            AssertRowIsTheJournal(options, descriptor, "a rebuild");
        }
    }

    [TestMethod]
    public async Task ACacheOfVersion6_IsGivenTheColumn_AndEachRowIsFilledOnceFromItsJournal()
    {
        using var root = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = root.Path };
        var session = await CreateSessionAsync(options, "session-v6");
        var path = JournalPath(options, session);
        var expected = LastStateRecord(path);

        // A cache written before the row kept the state: no column, and the version that build recorded. Its rows stay.
        await ExecuteAsync(options, "ALTER TABLE session_projection_cache DROP COLUMN local_state_json; UPDATE app_meta SET version = 6 WHERE owner = 'session_cache';");

        var first = new SessionViewCatalog(options).JournalStore;
        AssertSameJson(expected, Serialize((await first.ReadLatestStateAsync(session.SessionId, session.CreatedAt))!));
        Assert.AreEqual(1, first.JournalStateReadCount, "The first read takes the state from the journal.");
        await using (var database = ApplicationDatabase.Create(options))
        {
            Assert.AreEqual(7, await database.GetVersionAsync("session_cache"));
        }

        AssertSameJson(expected, ReadRowState(options, session.SessionId), "The state read is written to the row.");
        BlankJournal(path);
        var next = new SessionViewCatalog(options).JournalStore;
        AssertSameJson(expected, Serialize((await next.ReadLatestStateAsync(session.SessionId, session.CreatedAt))!));
        Assert.AreEqual(0, next.JournalStateReadCount, "The next start reads it from the row.");
    }

    [TestMethod]
    public async Task AStateWrittenByAnotherProcess_IsReadFromTheJournal()
    {
        using var root = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = root.Path };
        var session = await CreateSessionAsync(options, "session-outside");
        var path = JournalPath(options, session);

        // Another process appends a state: the row is no longer the projection of the journal.
        var record = JsonNode.Parse(File.ReadAllLines(path).Last(static line => line.Contains(SessionViewJournalStore.SessionStateEventType, StringComparison.Ordinal)))!;
        record["raw"]!["model_id"] = "outside-model";
        await File.AppendAllTextAsync(path, record.ToJsonString() + Environment.NewLine);

        var journal = new SessionViewCatalog(options).JournalStore;
        Assert.AreEqual("outside-model", (await journal.ReadLatestStateAsync(session.SessionId, session.CreatedAt))!.ModelId);
        Assert.AreEqual(1, journal.JournalStateReadCount);
        AssertSameJson(LastStateRecord(path), Serialize((await journal.ReadLatestStateAsync(session.SessionId, session.CreatedAt))!));
    }

    [TestMethod]
    public async Task AnAppendOfEventsInANewStore_KeepsTheViewOfTheRow_WithoutReadingTheJournalForIt()
    {
        using var root = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = root.Path };
        var session = await CreateSessionAsync(options, "session-events");
        var expected = ReadRowState(options, session.SessionId);

        var journal = new SessionViewCatalog(options).JournalStore;
        var cache = (SessionJournalSqliteCache)journal.ProjectionCache;
        var store = journal.CreateSessionStore();
        await store.AppendEventsAsync("openai", "openai", session.SessionId,
            [new AgentSessionUpdateEvent(new ModelProviderId("openai"), session.SessionId, CreatedAt, null, AgentSessionUpdateKind.Info, "info")]);

        Assert.AreEqual(0, cache.JournalViewReadCount, "The view of the session is the one of its row.");
        AssertSameJson(expected, ReadRowState(options, session.SessionId));
        Assert.AreEqual("model-1", (await journal.ReadLatestStateAsync(session.SessionId, session.CreatedAt))!.ModelId);
        Assert.AreEqual(0, journal.JournalStateReadCount);
    }

    private static async Task<SessionViewDescriptor> CreateSessionAsync(CatalogOptions options, string sessionId)
    {
        var catalog = new SessionViewCatalog(options);
        var session = CreateDescriptor(sessionId);
        await catalog.JournalStore.EnsureHeaderAsync(session);
        var store = catalog.JournalStore.CreateSessionStore();
        await store.UpsertSessionAsync(CreateSummary(sessionId));
        await store.UpsertStateAsync(CreateAgentState(sessionId));
        await catalog.JournalStore.AppendStateAsync(session, new SessionViewLocalState
        {
            ProviderKey = "openai",
            ModelId = "model-1",
            ReasoningEffort = AgentReasoningEffort.Medium,
            PermissionMode = "acceptEdits",
            ReviewBeforeCommit = true,
            ReviewNoted = false,
            AgentPromptId = "default",
            MessageCount = 4,
            ParentSessionId = "session-parent",
            CreatedBy = new AltaActorProvenance { Kind = "session", SourceSessionId = "session-parent" },
            QueuedPrompts = [Queued("sent", "submitted")],
            PromptProvenance = [new SessionViewPromptProvenance { PromptId = "sent", Kind = "send", Queued = true, CreatedAt = CreatedAt }],
        });
        return session;
    }

    private static SessionViewQueuedPrompt Queued(string id, string state)
        => new() { QueueItemId = id, Kind = "send", Prompt = "Prompt " + id, PromptPreview = "Prompt " + id, State = state, CreatedAt = CreatedAt };

    private static SessionViewDescriptor CreateDescriptor(string sessionId) => new()
    {
        SessionId = sessionId,
        Kind = SessionViewKind.GlobalSession,
        ProviderId = "openai",
        ProviderKey = "openai",
        ParentSessionId = "session-parent",
        WorkingDirectory = Path.GetTempPath(),
        Title = sessionId,
        CreatedAt = CreatedAt,
        UpdatedAt = CreatedAt,
    };

    private static AgentSessionSummary CreateSummary(string sessionId) => new()
    {
        SessionId = sessionId,
        ProviderId = new ModelProviderId("openai"),
        ProtocolFamily = "openai",
        ProviderKey = "openai",
        ModelId = "model-1",
        WorkingDirectory = Path.GetTempPath(),
        Title = sessionId,
        CreatedAt = CreatedAt,
        UpdatedAt = CreatedAt,
    };

    private static AgentSessionState CreateAgentState(string sessionId) => new()
    {
        SessionId = sessionId,
        ProtocolFamily = "openai",
        ProviderKey = "openai",
        UpdatedAt = CreatedAt,
    };

    private static string JournalPath(CatalogOptions options, SessionViewDescriptor session)
        => new AgentRuntimePathLayout(options.StateRoot).GetSessionFilePath(session.SessionId, session.CreatedAt);

    private static void AssertRowIsTheJournal(CatalogOptions options, SessionViewDescriptor session, string what)
        => AssertSameJson(LastStateRecord(JournalPath(options, session)), ReadRowState(options, session.SessionId), what + ": " + session.SessionId);

    // Writes spaces over every byte of the journal but its line ends, and gives it back its time: its stamp is the same.
    private static void BlankJournal(string path)
    {
        var time = File.GetLastWriteTimeUtc(path);
        var bytes = File.ReadAllBytes(path);
        for (var index = 0; index < bytes.Length; index++)
        {
            if (bytes[index] is not ((byte)'\n' or (byte)'\r')) bytes[index] = (byte)' ';
        }

        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, time);
        File.WriteAllText(path + ".blank", bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static int BlankLength(string path)
        => int.Parse(File.ReadAllText(path + ".blank"), System.Globalization.CultureInfo.InvariantCulture);

    private static string LastStateRecord(string path)
    {
        foreach (var line in File.ReadAllLines(path, Encoding.UTF8).Reverse())
        {
            if (!line.Contains(SessionViewJournalStore.SessionStateEventType, StringComparison.Ordinal)) continue;
            using var document = JsonDocument.Parse(line);
            return document.RootElement.GetProperty("raw").GetRawText();
        }

        Assert.Fail("The journal holds no state.");
        return string.Empty;
    }

    private static string ReadRowState(CatalogOptions options, string sessionId)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = options.ApplicationDatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT local_state_json FROM session_projection_cache WHERE session_id = $id;";
        command.Parameters.AddWithValue("$id", sessionId);
        var json = command.ExecuteScalar() as string;
        Assert.IsNotNull(json, "The row holds the state of " + sessionId);
        return json;
    }

    private static string Serialize(SessionViewLocalState state)
        => JsonSerializer.Serialize(state, SessionViewJournalJsonSerializerContext.Default.SessionViewLocalState);

    private static void AssertSameJson(string expected, string actual, string? message = null)
        => Assert.IsTrue(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual)), $"{message}{Environment.NewLine}expected {expected}{Environment.NewLine}actual   {actual}");

    private static async Task ExecuteAsync(CatalogOptions options, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={options.ApplicationDatabasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path) => Path = path;

        public string Path { get; }

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codealta-state-row-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TempDirectory(path);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }
}
