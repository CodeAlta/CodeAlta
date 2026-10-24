using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Catalog.ModelTiers;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

[TestClass]
public sealed class SessionRuntimeArchiveTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 10, 10, 9, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task SetSessionArchivedAsync_ArchivesTheSessionWithWhatItStarted_KeepsItsStateAndRestoresIt()
    {
        await using var f = await Fixture.CreateAsync();
        await f.CreateSessionAsync("root", permissionMode: "plan");
        await f.CreateSessionAsync("child", parent: "root");
        await f.CreateSessionAsync("grandchild", parent: "child");
        await f.CreateSessionAsync("other");
        var before = DateTimeOffset.UtcNow;

        var archived = await f.Runtime.SetSessionArchivedAsync("root", archived: true, withChildren: true);

        Assert.AreEqual("ok", archived.Status);
        CollectionAssert.AreEqual(new[] { "root", "child", "grandchild" }, archived.Changed.ToArray());
        foreach (var id in new[] { "root", "child", "grandchild" })
        {
            var local = (await f.Journal.ReadLatestStateAsync(id, CreatedAt))!;
            Assert.IsTrue(local.Archived, id);
            Assert.IsTrue(local.ArchivedAt >= before, "The time it was archived is kept.");
            Assert.AreEqual(SessionViewStatus.Archived, (await f.Runtime.ResolveOwnedSessionAsync(id, CancellationToken.None))!.Status);
        }

        Assert.AreEqual("plan", (await f.Journal.ReadLatestStateAsync("root", CreatedAt))!.PermissionMode, "What the session chose stays.");
        Assert.IsFalse((await f.Journal.ReadLatestStateAsync("other", CreatedAt))!.Archived);
        var header = (await f.Journal.ReadHeaderAsync("root", CreatedAt))!;
        Assert.AreEqual(f.Project.Id, header.ProjectRef, "An archived session keeps its project.");
        Assert.AreEqual("business", header.SpaceId, "And its space.");

        // Restored alone, the session comes back and the sessions below it stay archived.
        var restored = await f.Runtime.SetSessionArchivedAsync("root", archived: false, withChildren: false);
        Assert.AreEqual("ok", restored.Status);
        CollectionAssert.AreEqual(new[] { "root" }, restored.Changed.ToArray());
        var root = (await f.Journal.ReadLatestStateAsync("root", CreatedAt))!;
        Assert.IsFalse(root.Archived);
        Assert.IsNull(root.ArchivedAt);
        Assert.AreNotEqual(SessionViewStatus.Archived, (await f.Runtime.ResolveOwnedSessionAsync("root", CancellationToken.None))!.Status);
        Assert.IsTrue((await f.Journal.ReadLatestStateAsync("child", CreatedAt))!.Archived);

        // With its children, the rest comes back; a session that is not archived is left as it is.
        restored = await f.Runtime.SetSessionArchivedAsync("root", archived: false, withChildren: true);
        CollectionAssert.AreEqual(new[] { "child", "grandchild" }, restored.Changed.ToArray());
        Assert.IsFalse((await f.Journal.ReadLatestStateAsync("grandchild", CreatedAt))!.Archived);
    }

    [TestMethod]
    public async Task SetSessionArchivedAsync_AloneLeavesTheSessionsBelowIt()
    {
        await using var f = await Fixture.CreateAsync();
        await f.CreateSessionAsync("root");
        await f.CreateSessionAsync("child", parent: "root");

        var result = await f.Runtime.SetSessionArchivedAsync("root", archived: true, withChildren: false);

        Assert.AreEqual("ok", result.Status);
        CollectionAssert.AreEqual(new[] { "root" }, result.Changed.ToArray());
        Assert.IsFalse((await f.Journal.ReadLatestStateAsync("child", CreatedAt))!.Archived);
    }

    [TestMethod]
    public async Task SetSessionArchivedAsync_RefusesASessionAtWorkOrBelowIt_AndChangesNothing()
    {
        await using var f = await Fixture.CreateAsync();
        await f.CreateSessionAsync("root");
        await f.CreateSessionAsync("child", parent: "root");
        await f.CreateSessionAsync("queued", queued: true);

        Assert.AreEqual("session_missing", (await f.Runtime.SetSessionArchivedAsync("missing", archived: true, withChildren: false)).Status);
        // A session below one archived alone would report to a session nobody sees.
        var busy = await f.Runtime.SetOwnedSessionArchivedAsync("root", archived: true, withChildren: false, static id => id == "child");
        Assert.AreEqual("session_in_use", busy.Status);
        Assert.AreEqual("child", busy.BlockingSessionId);
        Assert.AreEqual(0, busy.Changed.Count);
        var queued = await f.Runtime.SetSessionArchivedAsync("queued", archived: true, withChildren: false);
        Assert.AreEqual("queue_pending", queued.Status);
        foreach (var id in new[] { "root", "child", "queued" })
            Assert.IsFalse((await f.Journal.ReadLatestStateAsync(id, CreatedAt))!.Archived, id);
    }

    [TestMethod]
    public async Task LocalStateWrittenFromADescriptor_DoesNotArchiveOrRestoreTheSession()
    {
        await using var f = await Fixture.CreateAsync();
        await f.CreateSessionAsync("kept");
        await f.CreateSessionAsync("archived");
        await f.Runtime.SetSessionArchivedAsync("archived", archived: true, withChildren: false);

        // The descriptor of an attachment that ended says Archived; one read before an archive says Active.
        var ended = (await f.Runtime.ResolveOwnedSessionAsync("kept", CancellationToken.None))!;
        ended.Status = SessionViewStatus.Archived;
        await f.Runtime.PersistSessionLocalStateAsync(ended);
        var stale = (await f.Runtime.ResolveOwnedSessionAsync("archived", CancellationToken.None))!;
        stale.Status = SessionViewStatus.Active;
        await f.Runtime.PersistSessionLocalStateAsync(stale);

        Assert.IsFalse((await f.Journal.ReadLatestStateAsync("kept", CreatedAt))!.Archived);
        Assert.IsTrue((await f.Journal.ReadLatestStateAsync("archived", CreatedAt))!.Archived);
    }

    [TestMethod]
    public async Task ARuntimeAfterARestart_ArchivesAndChangesASession_FromItsRow_WithoutReadingItsJournal()
    {
        await using var f = await Fixture.CreateAsync();
        await f.CreateSessionAsync("root", permissionMode: "plan");
        await f.CreateSessionAsync("child", parent: "root");
        // As at a start of the application: a runtime and a list of sessions that remember nothing in memory.
        await using var runtime = f.CreateRuntime();

        // What the journals hold is made unreadable, with their stamps kept: a read of one finds no state, and what is
        // written back after such a read loses what the session chose (its permission mode, its tier, its queue).
        f.BlankJournals();
        var archived = await runtime.SetSessionArchivedAsync("root", archived: true, withChildren: true);
        Assert.AreEqual("ok", archived.Status);
        CollectionAssert.AreEqual(new[] { "root", "child" }, archived.Changed.ToArray());
        var root = f.LastStateRecord("root");
        Assert.IsTrue(root.GetProperty("archived").GetBoolean());
        Assert.AreEqual("plan", root.GetProperty("permission_mode").GetString(), "What the session chose is written back with the archive.");
        Assert.AreEqual("root", f.LastStateRecord("child").GetProperty("parent_session_id").GetString());

        f.BlankJournals();
        Assert.AreEqual("ok", (await runtime.SetSessionArchivedAsync("root", archived: false, withChildren: true)).Status);
        Assert.IsFalse(f.LastStateRecord("root").GetProperty("archived").GetBoolean());

        f.BlankJournals();
        Assert.IsTrue(await runtime.SetSessionTierAsync("root", ModelTier.Quick));
        Assert.AreEqual(ModelTier.Quick, await runtime.GetSessionTierAsync("root"));
        Assert.AreEqual("plan", f.LastStateRecord("root").GetProperty("permission_mode").GetString());

        f.BlankJournals();
        var session = (await runtime.ResolveOwnedSessionAsync("root", CancellationToken.None))!;
        await runtime.SetOwnedPermissionModeAsync(session, "acceptEdits");
        root = f.LastStateRecord("root");
        Assert.AreEqual("acceptEdits", root.GetProperty("permission_mode").GetString());
        Assert.AreEqual("quick", root.GetProperty("tier").GetString());

        f.BlankJournals();
        var queued = await runtime.QueuePromptAsync(session, "later", "send", submittedBy: null);
        root = f.LastStateRecord("root");
        Assert.AreEqual("quick", root.GetProperty("tier").GetString());
        Assert.AreEqual(queued.QueueItemId, root.GetProperty("queued_prompts").EnumerateArray()
            .Single(static prompt => prompt.GetProperty("state").GetString() == "queued").GetProperty("queue_item_id").GetString());

        // The queue that waits is read from the row: the session cannot be archived.
        f.BlankJournals();
        Assert.AreEqual("queue_pending", (await runtime.SetSessionArchivedAsync("root", archived: true, withChildren: false)).Status);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TempDirectory _temp;
        private readonly AgentHub _hub;

        private Fixture(TempDirectory temp, AgentHub hub, SessionRuntimeService runtime, SessionViewJournalStore journal, ProjectDescriptor project,
            CatalogOptions options)
        {
            _temp = temp;
            _hub = hub;
            Runtime = runtime;
            Journal = journal;
            Project = project;
            Options = options;
        }

        public SessionRuntimeService Runtime { get; }

        public SessionViewJournalStore Journal { get; }

        public ProjectDescriptor Project { get; }

        public CatalogOptions Options { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var temp = new TempDirectory();
            var hub = new AgentHub(new ModelProviderRegistry(), temp.Path);
            var options = new CatalogOptions { GlobalRoot = Path.Combine(temp.Path, "alta") };
            var sessionViewCatalog = new SessionViewCatalog(options);
            var projects = new ProjectCatalog(options);
            var project = await projects.UpsertFromPathAsync(Directory.CreateDirectory(Path.Combine(temp.Path, "project")).FullName);
            var runtime = new SessionRuntimeService(hub, new AgentSessionCatalog(sessionViewCatalog.JournalStore.CreateSessionStore()), projects,
                sessionViewCatalog, new AgentInstructionTemplateProvider(catalogOptions: options), options);
            return new Fixture(temp, hub, runtime, sessionViewCatalog.JournalStore, project, options);
        }

        // Another runtime on the same folders, with a list of sessions of its own, as after a start of the application.
        public SessionRuntimeService CreateRuntime()
        {
            var sessionViewCatalog = new SessionViewCatalog(Options);
            return new SessionRuntimeService(_hub, new AgentSessionCatalog(sessionViewCatalog.JournalStore.CreateSessionStore()), new ProjectCatalog(Options),
                sessionViewCatalog, new AgentInstructionTemplateProvider(catalogOptions: Options), Options);
        }

        // Writes spaces over every byte of each journal but its line ends, and gives it back its time: its stamp is the same.
        public void BlankJournals()
        {
            foreach (var path in Directory.EnumerateFiles(new AgentRuntimePathLayout(Options.StateRoot).SessionsRootPath, "*.jsonl", SearchOption.AllDirectories))
            {
                var time = File.GetLastWriteTimeUtc(path);
                var bytes = File.ReadAllBytes(path);
                for (var index = 0; index < bytes.Length; index++)
                {
                    if (bytes[index] is not ((byte)'\n' or (byte)'\r')) bytes[index] = (byte)' ';
                }

                File.WriteAllBytes(path, bytes);
                File.SetLastWriteTimeUtc(path, time);
            }
        }

        // The last state record of the journal of a session, as it was appended.
        public System.Text.Json.JsonElement LastStateRecord(string sessionId)
        {
            var path = new AgentRuntimePathLayout(Options.StateRoot).GetSessionFilePath(sessionId, CreatedAt);
            var line = File.ReadAllLines(path).Last(static line => line.Contains(SessionViewJournalStore.SessionStateEventType, StringComparison.Ordinal));
            using var document = System.Text.Json.JsonDocument.Parse(line);
            return document.RootElement.GetProperty("raw").Clone();
        }

        public async Task CreateSessionAsync(string id, string? parent = null, string? permissionMode = null, bool queued = false)
        {
            var session = new SessionViewDescriptor
            {
                SessionId = id, Kind = SessionViewKind.ProjectSession, ProviderId = "codex", ProviderKey = "codex", ProjectRef = Project.Id,
                ParentSessionId = parent, WorkingDirectory = Project.ProjectPath, Title = id, SpaceId = "business",
                Status = SessionViewStatus.Active, CreatedAt = CreatedAt, UpdatedAt = CreatedAt, LastActiveAt = CreatedAt,
            };
            await Journal.EnsureHeaderAsync(session);
            var store = Journal.CreateSessionStore();
            await store.UpsertSessionAsync(new() { SessionId = id, ProviderId = new("codex"), ProviderKey = "codex", ProtocolFamily = "codex",
                WorkingDirectory = Project.ProjectPath, Title = id, ParentSessionId = parent, CreatedAt = CreatedAt, UpdatedAt = CreatedAt });
            var local = new SessionViewLocalState { ProviderKey = "codex", ParentSessionId = parent, PermissionMode = permissionMode };
            if (queued) local.QueuedPrompts.Add(new SessionViewQueuedPrompt { QueueItemId = "q1", Kind = "prompt", Prompt = "later", State = "queued" });
            await Journal.AppendStateAsync(session, local);
        }

        public async ValueTask DisposeAsync()
        {
            await Runtime.DisposeAsync();
            await _hub.DisposeAsync();
            _temp.Dispose();
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codealta-archive-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup for temporary test files.
            }
        }
    }
}
