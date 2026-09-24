using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Catalog.Tests;

// Real cached store and exclusively owned disposable journal root; no profile/provider startup.
[TestClass]
public sealed class SessionHistoryTailStoreTests
{
    [TestMethod]
    public async Task Tail_ResolvesCachedSessionAndPagesBackFromLatestUserPrompt()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        var store = fixture.Catalog.JournalStore.CreateSessionStore();
        var lines = Enumerable.Range(0, 1205).Select(i => new AgentContentCompletedEvent(new("test"), "runtime", DateTimeOffset.UnixEpoch, null,
            i == 1204 ? AgentContentKind.User : AgentContentKind.Assistant, i.ToString(), null,
            i == 1204 ? "latest user prompt" : "earlier").ToJson() + "\n");
        await File.AppendAllTextAsync(fixture.Path, string.Concat(lines));

        var first = await store.ReadHistoryTailPageAsync("selected", null, CancellationToken.None);
        Assert.AreEqual("latest user prompt", ((AgentContentCompletedEvent)first.Entries[^1].Event).Content);
        Assert.IsNotNull(first.Next);
        var second = await store.ReadHistoryTailPageAsync("selected", first.Next, CancellationToken.None);
        Assert.AreEqual(100, second.Entries.Count);
        Assert.IsTrue(second.Entries[^1].Offset < first.Entries[0].Offset);
        await File.AppendAllTextAsync(fixture.Path, " \n");
        Assert.AreEqual("history_changed", (await Assert.ThrowsExactlyAsync<AgentSessionHistoryException>(() =>
            store.ReadHistoryTailPageAsync("selected", first.Next, CancellationToken.None))).Code);
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CodeAlta-history-tail-" + Guid.NewGuid().ToString("N"));
        internal SessionViewCatalog Catalog { get; }
        internal string Path => new AgentRuntimePathLayout(Root).GetSessionFilePath("selected", DateTimeOffset.Parse("2026-09-01T00:00:00Z"));

        internal Fixture()
        {
            Directory.CreateDirectory(Root);
            Catalog = new SessionViewCatalog(new CatalogOptions { GlobalRoot = Root });
        }

        internal async Task SeedAsync()
        {
            var created = DateTimeOffset.Parse("2026-09-01T00:00:00Z");
            await Catalog.JournalStore.EnsureHeaderAsync(new SessionViewDescriptor
            {
                SessionId = "selected", ProviderId = "test", ProviderKey = "test", WorkingDirectory = Root,
                CreatedAt = created, Title = "Disposable history", Kind = SessionViewKind.GlobalSession,
            });
            await Catalog.JournalStore.CreateSessionStore().UpsertSessionAsync(new AgentSessionSummary
            {
                SessionId = "selected", ProviderId = new ModelProviderId("test"), ProviderKey = "test", ProtocolFamily = "test",
                WorkingDirectory = Root, CreatedAt = created, UpdatedAt = created, Title = "Disposable history",
            });
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
