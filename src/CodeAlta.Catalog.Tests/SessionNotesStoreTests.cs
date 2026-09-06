using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Catalog.Tests;

// Only owned temporary journals/catalog roots. No runtime, provider, profile, or plugin startup.
[TestClass]
public sealed class SessionNotesStoreTests
{
    [TestMethod]
    public async Task AppendNotes_PreservesValidFinalRecordWithoutNewline()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        var store = fixture.Catalog.JournalStore.CreateSessionStore();
        await store.AppendNotesAsync(Note("previous"), () => Task.CompletedTask);
        var text = await File.ReadAllTextAsync(fixture.Path);
        await File.WriteAllTextAsync(fixture.Path, text.TrimEnd('\r', '\n'));
        var before = await File.ReadAllBytesAsync(fixture.Path);
        var notifications = 0;

        await store.AppendNotesAsync(Note("next"), () => { notifications++; return Task.CompletedTask; });

        var after = await File.ReadAllBytesAsync(fixture.Path);
        CollectionAssert.AreEqual(before, after[..before.Length], "Every existing byte must be preserved.");
        var events = await store.ReadEventsAsync("notes");
        CollectionAssert.AreEqual(new[] { "previous", "next" }, events.OfType<AgentNotesEvent>().Select(note => note.Markdown).ToArray());
        Assert.AreEqual("next", (await store.ReadLatestNotesAsync("notes"))!.Markdown);
        Assert.AreEqual(1, notifications);
    }

    [TestMethod]
    [DataRow("{\"$type\":\"notes\",\"markdown\":\"truncated", false)]
    [DataRow("{\"$type\":\"notes\",\"markdown\":\"truncated", true)]
    [DataRow("not-json", false)]
    [DataRow("not-json", true)]
    public async Task AppendNotes_RefusesMalformedTailWithoutChangingUserBytes(string tail, bool newline)
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        var store = fixture.Catalog.JournalStore.CreateSessionStore();
        await store.AppendNotesAsync(Note("retained"), () => Task.CompletedTask);
        await File.AppendAllTextAsync(fixture.Path, tail + (newline ? "\n" : string.Empty));
        var before = await File.ReadAllBytesAsync(fixture.Path);
        var notified = false;

        await Assert.ThrowsAsync<JsonException>(() => store.AppendNotesAsync(Note("wrong"),
            () => { notified = true; return Task.CompletedTask; }));

        Assert.IsFalse(notified);
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(fixture.Path));
    }

    [TestMethod]
    public async Task AppendNotes_RefusesMixingUtf8IntoBomDetectedLegacyEncoding()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        var store = fixture.Catalog.JournalStore.CreateSessionStore();
        await store.AppendNotesAsync(Note("retained"), () => Task.CompletedTask);
        var text = await File.ReadAllTextAsync(fixture.Path);
        await File.WriteAllTextAsync(fixture.Path, text, System.Text.Encoding.Unicode);
        Assert.AreEqual("retained", (await store.ReadLatestNotesAsync("notes"))!.Markdown);
        var before = await File.ReadAllBytesAsync(fixture.Path);
        var notified = false;
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.AppendNotesAsync(Note("wrong"),
            () => { notified = true; return Task.CompletedTask; }));
        Assert.IsFalse(notified);
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(fixture.Path));
    }

    [TestMethod]
    public async Task LatestNotes_UsesCanonicalJournalOrderAndExistingRecoverySemantics()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        var store = fixture.Catalog.JournalStore.CreateSessionStore();
        Assert.IsNull(await store.ReadLatestNotesAsync("notes"));
        var first = Note("first") with { Timestamp = DateTimeOffset.MaxValue };
        var clear = Note(string.Empty) with { Kind = AgentNotesUpdateKind.Cleared, Timestamp = DateTimeOffset.MinValue };
        var other = new AgentSessionUpdateEvent(new ModelProviderId("test"), "notes", DateTimeOffset.UtcNow, null, AgentSessionUpdateKind.ModelChanged, "non-notes");
        await store.AppendEventsAsync("test", "test", "notes", [first, other, clear, other]);
        Assert.AreEqual(clear, await store.ReadLatestNotesAsync("notes"));
        var empty = Note(string.Empty);
        await store.AppendNotesAsync(empty, () => Task.CompletedTask);
        Assert.AreEqual(AgentNotesUpdateKind.Set, (await store.ReadLatestNotesAsync("notes"))!.Kind);
        var exact = Note(" \r\n# Unicode Ω 😀\n\n  ");
        await store.AppendNotesAsync(exact, () => Task.CompletedTask);
        var reconstructed = new SessionViewCatalog(new CatalogOptions { GlobalRoot = fixture.Root }).JournalStore.CreateSessionStore();
        Assert.AreEqual(exact, await reconstructed.ReadLatestNotesAsync("notes"));
    }

    [TestMethod]
    public async Task LatestNotes_RetainsCanonicalTrailingRecordToleranceButRejectsMalformedMiddle()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        var store = fixture.Catalog.JournalStore.CreateSessionStore();
        await store.AppendNotesAsync(Note("retained"), () => Task.CompletedTask);
        await File.AppendAllTextAsync(fixture.Path, "{partial");
        Assert.AreEqual("retained", (await store.ReadLatestNotesAsync("notes"))!.Markdown);
        await File.AppendAllTextAsync(fixture.Path, "\n{}\n");
        await Assert.ThrowsAsync<JsonException>(() => store.ReadLatestNotesAsync("notes"));
    }

    [TestMethod]
    public async Task InvalidNotesPayload_DoesNotBecomeAnEmptyDocument()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        var store = fixture.Catalog.JournalStore.CreateSessionStore();
        await store.AppendNotesAsync(Note("retained"), () => Task.CompletedTask);
        // Deliberately corrupt persisted data; the production event contract is non-nullable.
        await File.AppendAllTextAsync(fixture.Path, Note(null!).ToJson() + "\n");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.ReadLatestNotesAsync("notes"));
    }

    [TestMethod]
    public async Task SharedJournalGate_CanceledReadAndWriteLeaveBytesAndFeedbackUnchanged()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        var a = fixture.Catalog.JournalStore.CreateSessionStore();
        var b = fixture.Catalog.JournalStore.CreateSessionStore();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var initial = a.AppendNotesAsync(Note("retained"), async () => { entered.SetResult(); await release.Task; });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var before = await File.ReadAllBytesAsync(fixture.Path);
        using var cancellation = new CancellationTokenSource();
        var feedback = false;
        var read = b.ReadLatestNotesAsync("notes", cancellation.Token);
        var write = b.AppendNotesAsync(Note("wrong"), () => { feedback = true; return Task.CompletedTask; }, cancellation.Token);
        try
        {
            Assert.IsFalse(read.IsCompleted);
            Assert.IsFalse(write.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => read);
            await Assert.ThrowsAsync<OperationCanceledException>(() => write);
        }
        finally { release.TrySetResult(); }
        await initial.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(feedback);
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(fixture.Path));
    }

    [TestMethod]
    public async Task DeleteBetweenLookupAndWrite_IsVisibleAndNeverRecreatesJournal()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        var store = fixture.Catalog.JournalStore.CreateSessionStore();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = store.AppendNotesAsync(Note("first"), async () => { entered.SetResult(); await release.Task; });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var feedback = false;
        var pending = store.AppendNotesAsync(Note("wrong"), () => { feedback = true; return Task.CompletedTask; });
        Assert.IsFalse(pending.IsCompleted);
        File.Delete(fixture.Path);
        release.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<IOException>(() => pending);
        Assert.IsFalse(feedback);
        Assert.IsFalse(File.Exists(fixture.Path));
    }

    [TestMethod]
    public async Task ObserverFailure_AfterCommitIsExplicitAndRecordRemainsReadable()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        var store = fixture.Catalog.JournalStore.CreateSessionStore();
        var error = await Assert.ThrowsExactlyAsync<AgentNotesCommittedException>(() => store.AppendNotesAsync(Note("committed"),
            () => throw new IOException("Observed failure")));
        StringAssert.Contains(error.Message, "were committed");
        Assert.IsInstanceOfType<IOException>(error.InnerException);
        Assert.AreEqual("committed", (await store.ReadLatestNotesAsync("notes"))!.Markdown);
    }

    [TestMethod]
    public async Task CancellationDuringFileOpenRetry_DoesNotChangeNotesOrNotify()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        var store = fixture.Catalog.JournalStore.CreateSessionStore();
        await store.AppendNotesAsync(Note("retained"), () => Task.CompletedTask);
        var before = await File.ReadAllBytesAsync(fixture.Path);
        var feedback = false;
        using (var locked = new FileStream(fixture.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            using var cancellation = new CancellationTokenSource();
            var read = store.ReadLatestNotesAsync("notes", cancellation.Token);
            Assert.IsFalse(read.IsCompleted, "The owned file lock should delay reading.");
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => read);
            using var writeCancellation = new CancellationTokenSource();
            var write = store.AppendNotesAsync(Note("wrong"), () => { feedback = true; return Task.CompletedTask; }, writeCancellation.Token);
            Assert.IsFalse(write.IsCompleted, "The owned file lock should delay write admission.");
            writeCancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => write);
        }

        Assert.IsFalse(feedback);
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(fixture.Path));
        Assert.AreEqual("retained", (await store.ReadLatestNotesAsync("notes"))!.Markdown);
    }

    [TestMethod]
    public async Task UnknownSessionAndPreCanceledOperations_DoNotCreateFiles()
    {
        using var fixture = new Fixture();
        var store = fixture.Catalog.JournalStore.CreateSessionStore();
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReadLatestNotesAsync("missing"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AppendNotesAsync(Note("unknown"), () => Task.CompletedTask));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => store.ReadLatestNotesAsync("notes", cancellation.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => store.AppendNotesAsync(Note("unknown"), () => Task.CompletedTask, cancellation.Token));
        Assert.AreEqual(0, Directory.GetFiles(fixture.Root, "*.jsonl", SearchOption.AllDirectories).Length);
    }

    private static AgentNotesEvent Note(string text) => new(new ModelProviderId("test"), "notes", DateTimeOffset.UtcNow, null, AgentNotesUpdateKind.Set, text);

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CodeAlta-notes-store-" + Guid.NewGuid().ToString("N"));
        public SessionViewCatalog Catalog { get; }
        public string Path => new AgentRuntimePathLayout(Root).GetSessionFilePath("notes", DateTimeOffset.Parse("2026-09-01T00:00:00Z"));
        public Fixture()
        {
            Directory.CreateDirectory(Root);
            Catalog = new SessionViewCatalog(new CatalogOptions { GlobalRoot = Root });
        }
        public async Task SeedAsync()
        {
            var createdAt = DateTimeOffset.Parse("2026-09-01T00:00:00Z");
            await Catalog.JournalStore.EnsureHeaderAsync(new SessionViewDescriptor
            {
                SessionId = "notes", ProviderId = "test", ProviderKey = "test", WorkingDirectory = Root,
                CreatedAt = createdAt, Title = "Owned notes fixture", Kind = SessionViewKind.GlobalSession,
            });
            await Catalog.JournalStore.CreateSessionStore().UpsertSessionAsync(new AgentSessionSummary
            {
                SessionId = "notes", ProviderId = new ModelProviderId("test"), ProviderKey = "test", ProtocolFamily = "test",
                WorkingDirectory = Root, CreatedAt = createdAt, UpdatedAt = createdAt, Title = "Owned notes fixture",
            });
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
