using System.Text.Json;
using CodeAlta.Catalog;

namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed class SessionStateCompactionTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 10, 9, 8, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task StateRecordSize_DoesNotGrowWithTheNumberOfHandledPrompts()
    {
        using var root = TempDirectory.Create();
        var catalog = new SessionViewCatalog(new CatalogOptions { GlobalRoot = root.Path });
        var session = CreateSession();
        var state = new SessionViewLocalState { ProviderKey = "codex" };
        var longText = new string('x', 8000);
        var sizes = new List<int>();
        for (var index = 0; index < 500; index++)
        {
            var id = "queue-" + index;
            state.QueuedPrompts.Add(new() { QueueItemId = id, Kind = "send", Prompt = longText, PromptPreview = longText[..160], State = "queued", CreatedAt = CreatedAt });
            state.PromptProvenance.Add(new() { PromptId = id, Kind = "send", Queued = true, PromptPreview = longText[..160], CreatedAt = CreatedAt });
            await catalog.JournalStore.AppendStateAsync(session, state);
            state.QueuedPrompts[^1].State = "submitted";
            state.QueuedPrompts[^1].RunId = "run-" + index;
            await catalog.JournalStore.AppendStateAsync(session, state);
            if (index is 149 or 499)
            {
                sizes.Add(LastStateRecordLength(root.Path));
            }
        }

        // The in-memory state of the caller is not changed by the write.
        Assert.AreEqual(longText, state.QueuedPrompts[0].Prompt);
        Assert.AreEqual(500, state.QueuedPrompts.Count);
        Assert.AreEqual(sizes[0], sizes[1], sizes[0] / 20, $"record sizes {sizes[0]} and {sizes[1]}");
        Assert.IsLessThan(60_000, sizes[1]);

        var read = await catalog.JournalStore.ReadLatestStateAsync(session.SessionId, session.CreatedAt);
        Assert.IsNotNull(read);
        Assert.HasCount(SessionViewJournalStore.MaxHandledQueuedPromptRecords, read.QueuedPrompts);
        Assert.IsTrue(read.QueuedPrompts.All(static prompt => prompt.State == "submitted" && prompt.RunId is not null && prompt.Prompt.Length == 160));
        Assert.AreEqual("queue-499", read.QueuedPrompts[^1].QueueItemId);
        Assert.HasCount(SessionViewJournalStore.MaxPromptProvenanceRecords, read.PromptProvenance);
        Assert.AreEqual("queue-499", read.PromptProvenance[^1].PromptId);
    }

    [TestMethod]
    public async Task PendingQueuedPrompts_SurviveWriteAndReloadWithTheirFullText()
    {
        using var root = TempDirectory.Create();
        var catalog = new SessionViewCatalog(new CatalogOptions { GlobalRoot = root.Path });
        var session = CreateSession();
        var state = new SessionViewLocalState();
        var pendingText = new string('p', 5000);
        for (var index = 0; index < 300; index++)
        {
            state.QueuedPrompts.Add(new() { QueueItemId = "done-" + index, Kind = "send", Prompt = "text " + index, PromptPreview = "text " + index, State = "submitted", CreatedAt = CreatedAt });
            state.PromptProvenance.Add(new() { PromptId = "done-" + index, Kind = "send", Queued = true, CreatedAt = CreatedAt });
        }

        // The oldest entries are pending: they are neither dropped nor stripped, nor is their provenance.
        state.QueuedPrompts.Insert(0, new() { QueueItemId = "pending-queued", Kind = "send", Prompt = pendingText, PromptPreview = "p", State = "queued", CreatedAt = CreatedAt });
        state.QueuedPrompts.Insert(1, new() { QueueItemId = "pending-submitting", Kind = "send", Prompt = pendingText, PromptPreview = "p", State = "submitting", CreatedAt = CreatedAt });
        state.PromptProvenance.Insert(0, new() { PromptId = "pending-queued", Kind = "send", Queued = true, CreatedAt = CreatedAt });
        await catalog.JournalStore.AppendStateAsync(session, state);

        // A fresh store, as after a restart.
        var restarted = new SessionViewCatalog(new CatalogOptions { GlobalRoot = root.Path });
        var read = await restarted.JournalStore.ReadLatestStateAsync(session.SessionId, session.CreatedAt);
        Assert.IsNotNull(read);
        var queued = read.QueuedPrompts.Single(static prompt => prompt.QueueItemId == "pending-queued");
        Assert.AreEqual(pendingText, queued.Prompt);
        Assert.AreEqual(pendingText, read.QueuedPrompts.Single(static prompt => prompt.QueueItemId == "pending-submitting").Prompt);
        Assert.IsTrue(read.PromptProvenance.Any(static entry => entry.PromptId == "pending-queued"));
        Assert.HasCount(2 + SessionViewJournalStore.MaxHandledQueuedPromptRecords, read.QueuedPrompts);
    }

    [TestMethod]
    public async Task FailedPromptKeepsItsErrorAndTheOtherStateFieldsAreWritten()
    {
        using var root = TempDirectory.Create();
        var catalog = new SessionViewCatalog(new CatalogOptions { GlobalRoot = root.Path });
        var session = CreateSession();
        var state = new SessionViewLocalState { ProviderKey = "codex", ModelId = "m", AgentPromptId = "plan", PermissionMode = "ask", MessageCount = 3 };
        state.QueuedPrompts.Add(new() { QueueItemId = "q", Kind = "send", Prompt = "full text of a failed prompt", State = "failed", LastError = "boom", CreatedAt = CreatedAt });
        await catalog.JournalStore.AppendStateAsync(session, state);

        var read = await new SessionViewCatalog(new CatalogOptions { GlobalRoot = root.Path }).JournalStore.ReadLatestStateAsync(session.SessionId, session.CreatedAt);
        Assert.IsNotNull(read);
        Assert.AreEqual("boom", read.QueuedPrompts.Single().LastError);
        Assert.AreEqual("failed", read.QueuedPrompts.Single().State);
        Assert.AreEqual("plan", read.AgentPromptId);
        Assert.AreEqual("ask", read.PermissionMode);
        Assert.AreEqual(3, read.MessageCount);
    }

    [TestMethod]
    public async Task OldRecordsWithManyFullQueuedPromptsStillRead()
    {
        using var root = TempDirectory.Create();
        var catalog = new SessionViewCatalog(new CatalogOptions { GlobalRoot = root.Path });
        var session = CreateSession();
        await catalog.JournalStore.AppendStateAsync(session, new SessionViewLocalState());
        var path = Directory.EnumerateFiles(root.Path, "*.jsonl", SearchOption.AllDirectories).Single();
        var old = new SessionViewLocalState { ProviderKey = "codex" };
        for (var index = 0; index < 250; index++)
        {
            old.QueuedPrompts.Add(new() { QueueItemId = "q" + index, Kind = "send", Prompt = "old full text " + index, State = "submitted", CreatedAt = CreatedAt });
        }

        // An old-format record: the whole list, written without the compaction. The state is the object of the
        // last record that has a provider_key.
        var records = File.ReadAllLines(path);
        var node = System.Text.Json.Nodes.JsonNode.Parse(records[^1])!.AsObject();
        var rawProperty = node.First(static pair => pair.Value is System.Text.Json.Nodes.JsonObject value && value.ContainsKey("queued_prompts")).Key;
        node[rawProperty] = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(old));
        File.WriteAllLines(path, [.. records[..^1], node.ToJsonString()]);

        var read = await new SessionViewCatalog(new CatalogOptions { GlobalRoot = root.Path }).JournalStore.ReadLatestStateAsync(session.SessionId, session.CreatedAt);
        Assert.IsNotNull(read);
        Assert.HasCount(250, read.QueuedPrompts);
        Assert.AreEqual("old full text 7", read.QueuedPrompts[7].Prompt);
    }

    private static SessionViewDescriptor CreateSession() => new()
    {
        SessionId = "compaction-session",
        Kind = SessionViewKind.GlobalSession,
        ProviderId = "codex",
        ProviderKey = "codex",
        WorkingDirectory = Path.GetTempPath(),
        Title = "Compaction",
        CreatedAt = CreatedAt,
        UpdatedAt = CreatedAt,
    };

    private static int LastStateRecordLength(string root)
    {
        var path = Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories).Single();
        return File.ReadAllLines(path)[^1].Length;
    }

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path) => Path = path;

        public string Path { get; }

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codealta-state-compaction-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TempDirectory(path);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }
}
