using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed class SessionViewJournalMoveTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 10, 10, 9, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task MoveSessionAsync_RewritesHeaderSummaryAndState_AndKeepsHistory()
    {
        using var root = TempDirectory.Create();
        var journal = new SessionViewCatalog(new CatalogOptions { GlobalRoot = root.Path }).JournalStore;
        var session = await CreateSessionAsync(journal, worktree: Path.Combine(root.Path, "tree"));
        var store = journal.CreateSessionStore();
        var turn = new AgentActivityEvent(new("codex"), session.SessionId, CreatedAt, new("run"),
            AgentActivityKind.Turn, AgentActivityPhase.Started, "turn", null, "Turn", null);
        await store.AppendEventsAsync("codex", "codex", session.SessionId, [turn, turn with { Phase = AgentActivityPhase.Completed }]);

        await journal.MoveSessionAsync(session, "target-project", @"C:\code\target", @"C:\code\target-tree", "Moved title");

        var header = await journal.ReadHeaderAsync(session.SessionId, CreatedAt);
        Assert.IsNotNull(header);
        Assert.AreEqual("target-project", header.ProjectRef);
        Assert.AreEqual(@"C:\code\target", header.WorkingDirectory);
        Assert.AreEqual("Moved title", header.Title);
        Assert.AreEqual(SessionViewKind.ProjectSession, header.Kind);
        Assert.AreEqual("parent", header.ParentSessionId, "Who created the session and under which parent does not change.");
        Assert.AreEqual(CreatedAt, header.CreatedAt);

        var fresh = journal.CreateSessionStore();
        var summary = await fresh.GetSessionSummaryAsync(session.SessionId);
        Assert.IsNotNull(summary);
        Assert.AreEqual(@"C:\code\target", summary.WorkingDirectory);
        Assert.AreEqual(@"C:\code\target-tree", summary.WorktreeDirectory);
        Assert.AreEqual("Moved title", summary.Title);
        Assert.AreEqual("opaque-provider", (await fresh.GetStateAsync(session.SessionId))!.ProviderSessionId,
            "The provider continuation is kept: the conversation goes on in the new folder.");
        Assert.AreEqual("acceptEdits", (await journal.ReadLatestStateAsync(session.SessionId, CreatedAt))!.PermissionMode);
        var history = await fresh.ReadHistoryPageAsync(session.SessionId, null, CancellationToken.None);
        Assert.AreEqual(2, history.Entries.Select(entry => entry.Event).OfType<AgentActivityEvent>().Count(activity => activity.Kind == AgentActivityKind.Turn));
    }

    [TestMethod]
    public async Task MoveSessionAsync_WithoutTitle_KeepsTheSavedTitle_AndDropsTheWorktree()
    {
        using var root = TempDirectory.Create();
        var journal = new SessionViewCatalog(new CatalogOptions { GlobalRoot = root.Path }).JournalStore;
        var session = await CreateSessionAsync(journal, worktree: Path.Combine(root.Path, "tree"));

        await journal.MoveSessionAsync(session, "target-project", @"C:\code\target", worktreeDirectory: null, title: null);

        Assert.AreEqual("Saved title", (await journal.ReadHeaderAsync(session.SessionId, CreatedAt))!.Title);
        var summary = await journal.CreateSessionStore().GetSessionSummaryAsync(session.SessionId);
        Assert.AreEqual("Saved title", summary!.Title);
        Assert.IsNull(summary.WorktreeDirectory);
    }

    [TestMethod]
    public async Task MoveSessionAsync_RefusesUnfinishedTurnsQueuedWorkAndGlobalSessions()
    {
        using var root = TempDirectory.Create();
        var journal = new SessionViewCatalog(new CatalogOptions { GlobalRoot = root.Path }).JournalStore;
        var session = await CreateSessionAsync(journal, worktree: null);
        var store = journal.CreateSessionStore();
        var path = Directory.EnumerateFiles(root.Path, "*.jsonl", SearchOption.AllDirectories).Single();

        await journal.AppendStateAsync(session, new SessionViewLocalState { QueuedPrompts = [new()
            { QueueItemId = "keep", Kind = "prompt", Prompt = "Later", State = "queued", CreatedAt = CreatedAt }] });
        var queued = await File.ReadAllBytesAsync(path);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => journal.MoveSessionAsync(session, "target", @"C:\code\target", null, null));
        CollectionAssert.AreEqual(queued, await File.ReadAllBytesAsync(path), "A refused move leaves the journal as it was.");
        await journal.AppendStateAsync(session, new SessionViewLocalState { ProviderKey = "codex" });

        var turn = new AgentActivityEvent(new("codex"), session.SessionId, CreatedAt, new("run"),
            AgentActivityKind.Turn, AgentActivityPhase.Started, "turn", null, "Turn", null);
        await store.AppendEventsAsync("codex", "codex", session.SessionId, [turn]);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => journal.MoveSessionAsync(session, "target", @"C:\code\target", null, null));

        var global = new SessionViewDescriptor { SessionId = session.SessionId, Kind = SessionViewKind.GlobalSession,
            ProviderId = "codex", WorkingDirectory = root.Path, CreatedAt = CreatedAt };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => journal.MoveSessionAsync(global, "target", @"C:\code\target", null, null));
        Assert.AreEqual(@"C:\code\source", (await journal.ReadHeaderAsync(session.SessionId, CreatedAt))!.WorkingDirectory);
    }

    private static async Task<SessionViewDescriptor> CreateSessionAsync(SessionViewJournalStore journal, string? worktree)
    {
        var session = new SessionViewDescriptor
        {
            SessionId = "moved-session",
            Kind = SessionViewKind.ProjectSession,
            ProviderId = "codex",
            ProviderKey = "codex",
            ProjectRef = "source-project",
            ParentSessionId = "parent",
            WorkingDirectory = @"C:\code\source",
            Title = "Saved title",
            Status = SessionViewStatus.Active,
            CreatedAt = CreatedAt,
            UpdatedAt = CreatedAt,
            LastActiveAt = CreatedAt,
        };
        await journal.EnsureHeaderAsync(session);
        var store = journal.CreateSessionStore();
        await store.UpsertSessionAsync(new() { SessionId = session.SessionId, ProviderId = new("codex"), ProviderKey = "codex",
            ProtocolFamily = "codex", WorkingDirectory = session.WorkingDirectory, WorktreeDirectory = worktree, Title = "Saved title",
            CreatedAt = CreatedAt, UpdatedAt = CreatedAt });
        await store.UpsertStateAsync(new() { SessionId = session.SessionId, ProviderKey = "codex", ProtocolFamily = "codex",
            ProviderSessionId = "opaque-provider", UpdatedAt = CreatedAt });
        await journal.AppendStateAsync(session, new SessionViewLocalState { ProviderKey = "codex", PermissionMode = "acceptEdits" });
        return session;
    }

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path) => Path = path;

        public string Path { get; }

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"CodeAlta.Tests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TempDirectory(path);
        }

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
