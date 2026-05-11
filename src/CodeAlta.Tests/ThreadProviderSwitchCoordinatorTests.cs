using CodeAlta.Agent;
using CodeAlta.App;
using CodeAlta.App.State;
using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Threading;

namespace CodeAlta.Tests;

[TestClass]
public sealed class ThreadProviderSwitchCoordinatorTests
{
    [TestMethod]
    public async Task SwitchThreadProviderAsync_UpdatesProviderWithoutRekeyingThreadOrTouchingSessionStore()
    {
        using var temp = TempDirectory.Create();
        WriteProviderConfig(temp.Path);
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var backendStates = CreateBackendStates();
        var updatedThreads = new List<WorkThreadDescriptor>();
        var detachedThreadIds = new List<string>();
        var persisted = false;
        var coordinator = new ThreadProviderSwitchCoordinator(
            new CodeAltaConfigStore(options),
            backendStates,
            tab =>
            {
                tab.ModelId = "claude-sonnet-4";
                tab.ReasoningEffort = AgentReasoningEffort.High;
                return Task.CompletedTask;
            },
            threadId =>
            {
                detachedThreadIds.Add(threadId);
                return Task.FromResult(true);
            },
            updatedThreads.Add,
            () =>
            {
                persisted = true;
                return Task.CompletedTask;
            });
        var createdAt = DateTimeOffset.Parse("2026-04-19T10:00:00+00:00");
        var thread = CreateThread("codex_subscription:019e1584", "codex_subscription", "019e1584", createdAt);
        var tabState = CreateTabState(thread, "codex_subscription", "gpt-5.5");

        var switched = await coordinator.SwitchThreadProviderAsync(
            thread,
            tabState,
            new AgentBackendId("anthropic")).ConfigureAwait(false);

        Assert.IsTrue(switched);
        Assert.AreEqual("codex_subscription:019e1584", thread.ThreadId, "Switching providers must not rekey the open thread/tab.");
        Assert.AreEqual("anthropic", thread.BackendId);
        Assert.AreEqual("anthropic", thread.ProviderKey);
        Assert.AreEqual(string.Empty, thread.BackendSessionId, "The next run should start a target-provider session without resuming the previous provider session.");
        Assert.AreEqual("anthropic", tabState.BackendId.Value);
        Assert.AreEqual("claude-sonnet-4", tabState.ModelId);
        Assert.AreEqual(AgentReasoningEffort.High, tabState.ReasoningEffort);
        Assert.IsNull(tabState.Usage);
        CollectionAssert.AreEqual(new[] { "codex_subscription:019e1584" }, detachedThreadIds);
        CollectionAssert.AreEqual(new[] { thread }, updatedThreads);
        Assert.IsTrue(persisted);
    }

    [TestMethod]
    public async Task SwitchThreadProviderAsync_AllowsNativeSourceWithoutReadingHistory()
    {
        using var temp = TempDirectory.Create();
        WriteProviderConfig(temp.Path);
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var coordinator = new ThreadProviderSwitchCoordinator(
            new CodeAltaConfigStore(options),
            CreateBackendStates(includeNative: true),
            static tab =>
            {
                tab.ModelId = "claude-sonnet-4";
                return Task.CompletedTask;
            },
            static _ => Task.FromResult(true),
            static _ => { },
            static () => Task.CompletedTask);
        var createdAt = DateTimeOffset.Parse("2026-04-19T10:00:00+00:00");
        var thread = CreateThread("codex:native-session", AgentBackendIds.Codex.Value, "native-session", createdAt);
        var tabState = CreateTabState(thread, AgentBackendIds.Codex.Value, "gpt-5");

        var switched = await coordinator.SwitchThreadProviderAsync(
            thread,
            tabState,
            new AgentBackendId("anthropic")).ConfigureAwait(false);

        Assert.IsTrue(switched);
        Assert.AreEqual("codex:native-session", thread.ThreadId);
        Assert.AreEqual("anthropic", thread.BackendId);
        Assert.AreEqual(string.Empty, thread.BackendSessionId);
        Assert.AreEqual("anthropic", tabState.BackendId.Value);
    }

    [TestMethod]
    public async Task SwitchThreadProviderAsync_UpdatesVisibleProviderBeforeDetachingSession()
    {
        using var temp = TempDirectory.Create();
        WriteProviderConfig(temp.Path);
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var createdAt = DateTimeOffset.Parse("2026-04-19T10:00:00+00:00");
        var thread = CreateThread("openai:session-1", "openai", "session-1", createdAt);
        var tabState = CreateTabState(thread, "openai", "gpt-4.1");
        var observedTargetDuringDetach = false;
        var coordinator = new ThreadProviderSwitchCoordinator(
            new CodeAltaConfigStore(options),
            CreateBackendStates(),
            static _ => Task.CompletedTask,
            _ =>
            {
                observedTargetDuringDetach =
                    string.Equals(thread.BackendId, "anthropic", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(tabState.BackendId.Value, "anthropic", StringComparison.OrdinalIgnoreCase);
                return Task.FromResult(true);
            },
            static _ => { },
            static () => Task.CompletedTask);

        var switched = await coordinator.SwitchThreadProviderAsync(
            thread,
            tabState,
            new AgentBackendId("anthropic")).ConfigureAwait(false);

        Assert.IsTrue(switched);
        Assert.IsTrue(observedTargetDuringDetach, "Refreshes raised while the previous session is detached must not restore the old provider selection.");
    }

    [TestMethod]
    public void CanSwitchThreadProvider_RejectsNativeTargets()
    {
        using var temp = TempDirectory.Create();
        WriteProviderConfig(temp.Path);
        var coordinator = new ThreadProviderSwitchCoordinator(
            new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path }),
            CreateBackendStates(includeNative: true),
            static _ => Task.CompletedTask,
            static _ => Task.FromResult(true),
            static _ => { },
            static () => Task.CompletedTask);
        var thread = CreateThread("openai:session-1", "openai", "session-1", DateTimeOffset.UtcNow);
        var tabState = CreateTabState(thread, "openai", "gpt-4.1");

        Assert.IsTrue(coordinator.CanSelectThreadProvider(thread, tabState));
        Assert.IsFalse(coordinator.CanSwitchThreadProvider(thread, tabState, AgentBackendIds.Codex));
    }

    private static void WriteProviderConfig(string root)
    {
        File.WriteAllText(
            Path.Combine(root, "config.toml"),
            """
            [providers.openai]
            type = "openai-chat"
            api_key_env = "OPENAI_API_KEY"

            [providers.codex_subscription]
            type = "openai-codex-subscription"
            model = "gpt-5.5"
            experimental = true

            [providers.anthropic]
            type = "anthropic"
            api_key_env = "ANTHROPIC_API_KEY"
            """);
    }

    private static Dictionary<string, ChatBackendState> CreateBackendStates(bool includeNative = false)
    {
        var states = new Dictionary<string, ChatBackendState>(StringComparer.OrdinalIgnoreCase)
        {
            ["openai"] = ReadyState("openai", "OpenAI"),
            ["codex_subscription"] = ReadyState("codex_subscription", "Codex ChatGPT subscription"),
            ["anthropic"] = ReadyState("anthropic", "Anthropic"),
        };
        if (includeNative)
        {
            states[AgentBackendIds.Codex.Value] = ReadyState(AgentBackendIds.Codex.Value, "Codex");
        }

        return states;
    }

    private static ChatBackendState ReadyState(string backendId, string displayName)
        => new(new AgentBackendId(backendId), displayName)
        {
            Availability = ChatBackendAvailability.Ready,
        };

    private static WorkThreadDescriptor CreateThread(string threadId, string backendId, string backendSessionId, DateTimeOffset timestamp)
        => new()
        {
            ThreadId = threadId,
            Kind = WorkThreadKind.ProjectThread,
            BackendId = backendId,
            ProviderKey = backendId,
            BackendSessionId = backendSessionId,
            ProjectRef = "project-1",
            WorkingDirectory = @"C:\repo",
            Title = "Review startup",
            Status = WorkThreadStatus.Active,
            CreatedAt = timestamp,
            UpdatedAt = timestamp,
            LastActiveAt = timestamp,
            StartedAt = timestamp,
        };

    private static OpenThreadState CreateTabState(WorkThreadDescriptor thread, string backendId, string modelId)
        => new(thread, new Presentation.Timeline.ThreadTimelinePresenter(
            new InlineUiDispatcher(),
            static () => null,
            localFileRootPath: null))
        {
            BackendId = new AgentBackendId(backendId),
            ModelId = modelId,
            Usage = new AgentSessionUsage(
                Window: new AgentWindowUsageSnapshot(1200, 8000, 3, "Old usage"),
                Scope: AgentUsageScope.ThreadTotal,
                Source: AgentUsageSource.RecoveredHistory,
                UpdatedAt: DateTimeOffset.UtcNow,
                Details: null),
        };

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
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CodeAltaTests", Guid.NewGuid().ToString("N"));
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
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
