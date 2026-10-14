using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed class SessionViewJournalMoveTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 10, 10, 9, 0, 0, TimeSpan.Zero);
    private static readonly SessionViewMove Target = new("target", @"C:\code\target", null, null, null);

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

        await journal.MoveSessionAsync(session, new("target-project", @"C:\code\target", @"C:\code\target-tree", "Moved title", "Moved summary"));

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
        Assert.AreEqual("Moved summary", summary.Summary);
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

        await journal.MoveSessionAsync(session, new("target-project", @"C:\code\target", WorktreeDirectory: null, Title: null, Summary: null));

        Assert.AreEqual("Saved title", (await journal.ReadHeaderAsync(session.SessionId, CreatedAt))!.Title);
        var summary = await journal.CreateSessionStore().GetSessionSummaryAsync(session.SessionId);
        Assert.AreEqual("Saved title", summary!.Title);
        Assert.IsNull(summary.WorktreeDirectory);
    }

    [TestMethod]
    public async Task MoveSessionAsync_MovesASessionThatNeverRan()
    {
        using var root = TempDirectory.Create();
        var journal = new SessionViewCatalog(new CatalogOptions { GlobalRoot = root.Path }).JournalStore;
        var session = await CreateSessionAsync(journal, worktree: null, ran: false);

        await journal.MoveSessionAsync(session, Target);

        Assert.AreEqual("target", (await journal.ReadHeaderAsync(session.SessionId, CreatedAt))!.ProjectRef);
        var state = await journal.CreateSessionStore().GetStateAsync(session.SessionId);
        Assert.IsNotNull(state);
        Assert.IsNull(state.ProviderSessionId, "A session that never ran has no conversation to continue.");
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
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => journal.MoveSessionAsync(session, Target));
        CollectionAssert.AreEqual(queued, await File.ReadAllBytesAsync(path), "A refused move leaves the journal as it was.");
        await journal.AppendStateAsync(session, new SessionViewLocalState { ProviderKey = "codex" });

        var turn = new AgentActivityEvent(new("codex"), session.SessionId, CreatedAt, new("run"),
            AgentActivityKind.Turn, AgentActivityPhase.Started, "turn", null, "Turn", null);
        await store.AppendEventsAsync("codex", "codex", session.SessionId, [turn]);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => journal.MoveSessionAsync(session, Target));

        var global = new SessionViewDescriptor { SessionId = session.SessionId, Kind = SessionViewKind.GlobalSession,
            ProviderId = "codex", WorkingDirectory = root.Path, CreatedAt = CreatedAt };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => journal.MoveSessionAsync(global, Target));
        Assert.AreEqual(@"C:\code\source", (await journal.ReadHeaderAsync(session.SessionId, CreatedAt))!.WorkingDirectory);
    }

    [TestMethod]
    public async Task MoveAndSwitch_ReadTheSummaryOf18ThatNamesTheProviderKeyAsFamily_WithTheFamilyOfItsRuntime_Only()
    {
        // CodeAlta 1.8 attached a session of a custom-keyed provider by writing its key as the family of the summary;
        // the provider state kept the family of the runtime.
        using var root = TempDirectory.Create();
        var journal = new SessionViewCatalog(new CatalogOptions { GlobalRoot = root.Path }).JournalStore;
        var session = await CreateSessionAsync(journal, worktree: null, provider: "neuralwatt", summaryFamily: "neuralwatt", stateFamily: "openai-chat");
        var path = Directory.EnumerateFiles(root.Path, "*.jsonl", SearchOption.AllDirectories).Single();
        var before = await File.ReadAllBytesAsync(path);

        // Without the family of its runtime, or with another one, the session still needs recovery.
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => journal.MoveSessionAsync(session, Target));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => journal.MoveSessionAsync(session, Target with { ProviderFamily = "openai-responses" }));
        var other = new ModelProviderDescriptor(new ModelProviderId("codex"), "Codex", "codex");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => journal.SelectProviderAsync(session, "neuralwatt", other, carryOver: null, providerFamily: null));
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(path), "A refused move or switch leaves the journal as it was.");

        await journal.MoveSessionAsync(session, Target with { ProviderFamily = "openai-chat" });
        var store = journal.CreateSessionStore();
        Assert.AreEqual("openai-chat", (await store.GetSessionSummaryAsync(session.SessionId))!.ProtocolFamily, "The move repairs the summary.");
        Assert.AreEqual("opaque-provider", (await store.GetStateAsync(session.SessionId))!.ProviderSessionId);

        // A switch reads such a summary the same way.
        using var second = TempDirectory.Create();
        var switched = new SessionViewCatalog(new CatalogOptions { GlobalRoot = second.Path }).JournalStore;
        var stale = await CreateSessionAsync(switched, worktree: null, provider: "neuralwatt", summaryFamily: "neuralwatt", stateFamily: "openai-chat");
        await switched.SelectProviderAsync(stale, "neuralwatt", other, carryOver: null, providerFamily: "openai-chat");
        Assert.AreEqual("codex", (await switched.CreateSessionStore().GetSessionSummaryAsync(stale.SessionId))!.ProviderKey);
    }

    [TestMethod]
    public async Task Move_RefusesAnyOtherDisagreementOnTheFamily()
    {
        using var root = TempDirectory.Create();
        var journal = new SessionViewCatalog(new CatalogOptions { GlobalRoot = root.Path }).JournalStore;
        // The summary names another family than the key: that is not what 1.8 wrote.
        var session = await CreateSessionAsync(journal, worktree: null, provider: "neuralwatt", summaryFamily: "openai-responses", stateFamily: "openai-chat");

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => journal.MoveSessionAsync(session, Target with { ProviderFamily = "openai-chat" }));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => journal.MoveSessionAsync(session, Target with { ProviderFamily = "openai-responses" }));
    }

    private static async Task<SessionViewDescriptor> CreateSessionAsync(SessionViewJournalStore journal, string? worktree, bool ran = true,
        string provider = "codex", string? summaryFamily = null, string? stateFamily = null)
    {
        var session = new SessionViewDescriptor
        {
            SessionId = "moved-session",
            Kind = SessionViewKind.ProjectSession,
            ProviderId = provider,
            ProviderKey = provider,
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
        await store.UpsertSessionAsync(new() { SessionId = session.SessionId, ProviderId = new(provider), ProviderKey = provider,
            ProtocolFamily = summaryFamily ?? provider, WorkingDirectory = session.WorkingDirectory, WorktreeDirectory = worktree, Title = "Saved title",
            CreatedAt = CreatedAt, UpdatedAt = CreatedAt });
        if (ran) await store.UpsertStateAsync(new() { SessionId = session.SessionId, ProviderKey = provider, ProtocolFamily = stateFamily ?? provider,
            ProviderSessionId = "opaque-provider", UpdatedAt = CreatedAt });
        await journal.AppendStateAsync(session, new SessionViewLocalState { ProviderKey = provider, PermissionMode = "acceptEdits" });
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
