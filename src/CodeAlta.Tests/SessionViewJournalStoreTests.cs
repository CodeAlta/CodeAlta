using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;

namespace CodeAlta.Tests;

[TestClass]
public sealed class SessionViewJournalStoreTests
{
    [TestMethod]
    public async Task ListHeadersAsync_RetriesWhenJournalIsTemporarilyLocked()
    {
        using var temp = TestTempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var store = new SessionViewJournalStore(options);
        var createdAt = DateTimeOffset.Parse("2026-07-12T10:00:00+00:00");
        var session = new SessionViewDescriptor
        {
            SessionId = "session-sharing-violation",
            Kind = SessionViewKind.GlobalSession,
            ProviderId = ModelProviderIds.OpenAIResponses.Value,
            ProviderKey = ModelProviderIds.OpenAIResponses.Value,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            LastActiveAt = createdAt,
        };
        await store.EnsureHeaderAsync(session).ConfigureAwait(false);
        var path = new AgentRuntimePathLayout(temp.Path).GetSessionFilePath(session.SessionId, session.CreatedAt);

        await using var exclusiveStream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, bufferSize: 4096, useAsync: true);
        var listTask = store.ListHeadersAsync();

        await Task.Delay(100).ConfigureAwait(false);
        Assert.IsFalse(listTask.IsCompleted);

        await exclusiveStream.DisposeAsync().ConfigureAwait(false);
        var headers = await listTask.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        Assert.AreEqual(session.SessionId, headers.Single().SessionId);
    }

    [TestMethod]
    public async Task ReadLatestStateAsync_ReadTwice_KeepsEveryValueOfTheState()
    {
        using var temp = TestTempDirectory.Create();
        var store = new SessionViewJournalStore(new CatalogOptions { GlobalRoot = temp.Path });
        var createdAt = DateTimeOffset.Parse("2026-07-12T11:00:00+00:00");
        var session = new SessionViewDescriptor
        {
            SessionId = "session-state-read-twice",
            Kind = SessionViewKind.GlobalSession,
            ProviderId = ModelProviderIds.OpenAIResponses.Value,
            ProviderKey = ModelProviderIds.OpenAIResponses.Value,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            LastActiveAt = createdAt,
        };
        await store.AppendStateAsync(session, new SessionViewLocalState
        {
            ProviderKey = "openai",
            ModelId = "gpt-5",
            ReasoningEffort = AgentReasoningEffort.High,
            PermissionMode = "plan",
            AgentPromptId = "plan",
            Archived = true,
            MessageCount = 3,
            ParentSessionId = "session-parent",
        }).ConfigureAwait(false);

        // The first read parses the journal; the next ones answer from the state kept in memory.
        for (var read = 0; read < 3; read++)
        {
            var state = await store.ReadLatestStateAsync(session.SessionId, session.CreatedAt).ConfigureAwait(false);

            Assert.IsNotNull(state);
            Assert.AreEqual("openai", state.ProviderKey, $"read {read}");
            Assert.AreEqual("gpt-5", state.ModelId, $"read {read}");
            Assert.AreEqual(AgentReasoningEffort.High, state.ReasoningEffort, $"read {read}");
            Assert.AreEqual("plan", state.PermissionMode, $"read {read}");
            Assert.AreEqual("plan", state.AgentPromptId, $"read {read}");
            Assert.IsTrue(state.Archived, $"read {read}");
            Assert.AreEqual(3, state.MessageCount, $"read {read}");
            Assert.AreEqual("session-parent", state.ParentSessionId, $"read {read}");
        }
    }
}
