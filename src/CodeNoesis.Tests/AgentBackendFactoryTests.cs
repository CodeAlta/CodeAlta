using CodeNoesis.Agent;
using CodeNoesis.Agent.Codex;
using CodeNoesis.Agent.Copilot;

namespace CodeNoesis.Tests;

[TestClass]
public sealed class AgentBackendFactoryTests
{
    [TestMethod]
    public async Task RegisterAndCreate_Works()
    {
        var factory = new AgentBackendFactory();
        factory.Register("test", () => new TestBackend("test"));

        Assert.IsTrue(factory.IsRegistered("test"));

        var registered = factory.ListRegisteredBackends();
        Assert.AreEqual(1, registered.Count);
        Assert.AreEqual("test", registered[0].Value);

        var backend = factory.Create("test");
        Assert.IsInstanceOfType<TestBackend>(backend);

        await backend.DisposeAsync().ConfigureAwait(false);
    }

    [TestMethod]
    public void Register_ThrowsForDuplicateId()
    {
        var factory = new AgentBackendFactory();
        factory.Register("test", () => new TestBackend("test"));

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            factory.Register("test", () => new TestBackend("test")));
    }

    [TestMethod]
    public async Task RegisterOrReplace_OverridesExistingFactory()
    {
        var factory = new AgentBackendFactory();
        factory.Register("test", () => new TestBackend("test", "first"));
        factory.RegisterOrReplace("test", () => new TestBackend("test", "second"));

        var backend = factory.Create("test");
        Assert.AreEqual("second", backend.DisplayName);

        await backend.DisposeAsync().ConfigureAwait(false);
    }

    [TestMethod]
    public void TryRegisterAndTryCreate_ReturnsFalseWhenMissing()
    {
        var factory = new AgentBackendFactory();

        Assert.IsTrue(factory.TryRegister("test", () => new TestBackend("test")));
        Assert.IsFalse(factory.TryRegister("test", () => new TestBackend("test")));
        Assert.IsFalse(factory.TryCreate("missing", out _));
    }

    [TestMethod]
    public void Create_ThrowsForUnknownId()
    {
        var factory = new AgentBackendFactory();

        Assert.ThrowsExactly<KeyNotFoundException>(() => factory.Create("unknown"));
    }

    [TestMethod]
    public void Create_ThrowsForMismatchedBackendId()
    {
        var factory = new AgentBackendFactory();
        factory.Register("expected", () => new TestBackend("actual"));

        Assert.ThrowsExactly<InvalidOperationException>(() => factory.Create("expected"));
    }

    [TestMethod]
    public async Task AdapterExtensions_RegisterCodexAndCopilot()
    {
        var factory = new AgentBackendFactory();
        factory.RegisterCodex(new CodexAgentBackendOptions());
        factory.RegisterCopilot(new CopilotAgentBackendOptions());

        Assert.IsTrue(factory.IsRegistered(AgentBackendIds.Codex));
        Assert.IsTrue(factory.IsRegistered(AgentBackendIds.Copilot));

        var codexBackend = factory.Create(AgentBackendIds.Codex);
        var copilotBackend = factory.Create(AgentBackendIds.Copilot);

        Assert.IsInstanceOfType<CodexAgentBackend>(codexBackend);
        Assert.IsInstanceOfType<CopilotAgentBackend>(copilotBackend);

        await codexBackend.DisposeAsync().ConfigureAwait(false);
        await copilotBackend.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class TestBackend : IAgentBackend
    {
        public TestBackend(string backendId, string? displayName = null)
        {
            ArgumentNullException.ThrowIfNull(backendId);
            BackendId = new AgentBackendId(backendId);
            DisplayName = displayName ?? backendId;
        }

        public AgentBackendId BackendId { get; }

        public string DisplayName { get; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<AgentModelInfo>>([]);
        }

        public Task<IReadOnlyList<AgentSessionMetadata>> ListSessionsAsync(
            AgentSessionListFilter? filter = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<AgentSessionMetadata>>([]);
        }

        public Task<IAgentSession> CreateSessionAsync(
            AgentSessionCreateOptions options,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IAgentSession> ResumeSessionAsync(
            string sessionId,
            AgentSessionResumeOptions options,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
