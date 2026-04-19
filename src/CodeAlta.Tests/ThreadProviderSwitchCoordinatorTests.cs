using CodeAlta.Agent;
using CodeAlta.Agent.LocalRuntime;
using CodeAlta.App;
using CodeAlta.App.State;
using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Threading;
using XenoAtom.Terminal.UI;

namespace CodeAlta.Tests;

[TestClass]
public sealed class ThreadProviderSwitchCoordinatorTests
{
    [TestMethod]
    public async Task SwitchThreadProviderAsync_ClonesLocalRuntimeSessionAndRekeysThread()
    {
        using var temp = TempDirectory.Create();
        File.WriteAllText(
            Path.Combine(temp.Path, "config.toml"),
            """
            [providers.openai]
            type = "openai-chat"
            api_key_env = "OPENAI_API_KEY"

            [providers.anthropic]
            type = "anthropic"
            api_key_env = "ANTHROPIC_API_KEY"
            """);

        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var configStore = new CodeAltaConfigStore(options);
        var threadCatalog = new WorkThreadCatalog(options);
        var backendStates = new Dictionary<string, ChatBackendState>(StringComparer.OrdinalIgnoreCase)
        {
            ["openai"] = new ChatBackendState(new AgentBackendId("openai"), "OpenAI")
            {
                Availability = ChatBackendAvailability.Ready,
            },
            ["anthropic"] = new ChatBackendState(new AgentBackendId("anthropic"), "Anthropic")
            {
                Availability = ChatBackendAvailability.Ready,
            },
        };

        var store = new FileSystemLocalAgentSessionStore(
            new LocalAgentRuntimePathLayout(options.GlobalRoot));
        var createdAt = DateTimeOffset.Parse("2026-04-19T10:00:00+00:00");
        var sourceSummary = new LocalAgentSessionSummary
        {
            SessionId = "session-1",
            BackendId = new AgentBackendId("openai"),
            ProtocolFamily = "openai-chat",
            ProviderKey = "openai",
            ModelId = "gpt-4.1",
            WorkingDirectory = @"C:\repo",
            Title = "Review startup",
            Summary = "Latest summary",
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        };
        var sourceState = new LocalAgentSessionState
        {
            SessionId = "session-1",
            ProtocolFamily = "openai-chat",
            ProviderKey = "openai",
            ProviderSessionId = "resp_123",
            ProviderState = System.Text.Json.JsonDocument.Parse("""{"conversation_id":"abc"}""").RootElement.Clone(),
            UpdatedAt = createdAt,
        };
        var sourceHistory =
            new AgentEvent[]
            {
                new AgentContentCompletedEvent(
                    new AgentBackendId("openai"),
                    "session-1",
                    createdAt,
                    null,
                    AgentContentKind.User,
                    "user-1",
                    null,
                    "Review startup"),
            };
        await store.UpsertSessionAsync(sourceSummary).ConfigureAwait(false);
        await store.UpsertStateAsync(sourceState).ConfigureAwait(false);
        await store.AppendEventsAsync("openai-chat", "openai", "session-1", sourceHistory).ConfigureAwait(false);

        var coordinator = new ThreadProviderSwitchCoordinator(
            options,
            threadCatalog,
            configStore,
            backendStates,
            tab =>
            {
                tab.ModelId = "claude-sonnet-4";
                tab.ReasoningEffort = AgentReasoningEffort.High;
                return Task.CompletedTask;
            },
            threadId =>
            {
                Assert.AreEqual("openai:session-1", threadId);
                return Task.FromResult(true);
            },
            (oldThreadId, updatedThread) =>
            {
                Assert.AreEqual("openai:session-1", oldThreadId);
                Assert.AreEqual("anthropic:session-1", updatedThread.ThreadId);
            },
            static () => Task.CompletedTask);

        var thread = new WorkThreadDescriptor
        {
            ThreadId = "openai:session-1",
            Kind = WorkThreadKind.ProjectThread,
            BackendId = "openai",
            ProviderKey = "openai",
            BackendSessionId = "session-1",
            ProjectRef = "project-1",
            WorkingDirectory = @"C:\repo",
            Title = "Review startup",
            Status = WorkThreadStatus.Active,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            LastActiveAt = createdAt,
            StartedAt = createdAt,
        };
        var tabState = new OpenThreadState(thread, new Presentation.Timeline.ThreadTimelinePresenter(
            new InlineUiDispatcher(),
            static () => true,
            static () => null,
            localFileRootPath: null))
        {
            BackendId = new AgentBackendId("openai"),
            ModelId = "gpt-4.1",
            ReasoningEffort = AgentReasoningEffort.Medium,
            Usage = new AgentSessionUsage(
                Window: new AgentWindowUsageSnapshot(1200, 8000, 3, "Old usage"),
                Scope: AgentUsageScope.ThreadTotal,
                Source: AgentUsageSource.RecoveredHistory,
                UpdatedAt: createdAt,
                Details: null),
        };

        var switched = await coordinator.SwitchThreadProviderAsync(
            thread,
            tabState,
            new AgentBackendId("anthropic")).ConfigureAwait(false);

        Assert.IsTrue(switched);
        Assert.AreEqual("anthropic", thread.BackendId);
        Assert.AreEqual("anthropic", thread.ProviderKey);
        Assert.AreEqual("anthropic:session-1", thread.ThreadId);
        Assert.AreEqual("anthropic", tabState.BackendId.Value);
        Assert.AreEqual("claude-sonnet-4", tabState.ModelId);
        Assert.AreEqual(AgentReasoningEffort.High, tabState.ReasoningEffort);
        Assert.IsNull(tabState.Usage);

        var clonedSummary = await store.GetSessionAsync("anthropic-messages", "anthropic", "session-1").ConfigureAwait(false);
        Assert.IsNotNull(clonedSummary);
        Assert.AreEqual("anthropic", clonedSummary.BackendId.Value);
        Assert.AreEqual("anthropic-messages", clonedSummary.ProtocolFamily);
        Assert.AreEqual("anthropic", clonedSummary.ProviderKey);
        Assert.AreEqual("claude-sonnet-4", clonedSummary.ModelId);
        Assert.IsNull(clonedSummary.Usage);

        var clonedState = await store.GetStateAsync("anthropic-messages", "anthropic", "session-1").ConfigureAwait(false);
        Assert.IsNotNull(clonedState);
        Assert.IsNull(clonedState.ProviderSessionId);
        Assert.IsNull(clonedState.ProviderState);
        Assert.IsNull(clonedState.Usage);

        var clonedHistory = await store.ReadEventsAsync("anthropic-messages", "anthropic", "session-1").ConfigureAwait(false);
        Assert.AreEqual(2, clonedHistory.Count);
        Assert.IsInstanceOfType<AgentSessionUpdateEvent>(clonedHistory[^1]);
        Assert.IsNull(await store.GetSessionAsync("openai-chat", "openai", "session-1").ConfigureAwait(false));
    }

    private sealed class InlineUiDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;

        public void Post(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            action();
        }

        public Task InvokeAsync(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            action();
            return Task.CompletedTask;
        }

        public Task<T> InvokeAsync<T>(Func<T> action)
        {
            ArgumentNullException.ThrowIfNull(action);
            return Task.FromResult(action());
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"CodeAlta.Tests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TempDirectory(path);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}
