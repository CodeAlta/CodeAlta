using CodeAlta.Agent;
using CodeAlta.Tui.App;
using CodeAlta.Tui.App.State;
using CodeAlta.Catalog;
using CodeAlta.Tui.Models;
using CodeAlta.Tui.Threading;
using Presentation = CodeAlta.Tui.Presentation;

namespace CodeAlta.Tests;

[TestClass]
public sealed class SessionProviderSwitchCoordinatorTests
{
    [TestMethod]
    public async Task SwitchSessionProviderAsync_UpdatesProviderWithoutRekeyingSessionOrTouchingSessionStore()
    {
        var providerStates = CreateProviderStates();
        var updatedSessions = new List<SessionViewDescriptor>();
        var detachedSessionIds = new List<string>();
        var persisted = false;
        var coordinator = new SessionProviderSwitchCoordinator(
            providerStates,
            tab =>
            {
                tab.ModelId = "claude-sonnet-4";
                tab.ReasoningEffort = AgentReasoningEffort.High;
                return Task.CompletedTask;
            },
            sessionId =>
            {
                detachedSessionIds.Add(sessionId);
                return Task.FromResult(true);
            },
            updatedSessions.Add,
            () =>
            {
                persisted = true;
                return Task.CompletedTask;
            });
        var createdAt = DateTimeOffset.Parse("2026-04-19T10:00:00+00:00");
        var session = CreateSession("019e1584", "codex", createdAt);
        var tabState = CreateTabState(session, "codex", "gpt-5.5");

        var switched = await coordinator.SwitchSessionProviderAsync(
            session,
            tabState,
            new ModelProviderId("anthropic")).ConfigureAwait(false);

        Assert.IsTrue(switched);
        Assert.AreEqual("019e1584", session.SessionId, "Switching providers must not rekey the open session/tab.");
        Assert.AreEqual("anthropic", session.ProviderId);
        Assert.AreEqual("anthropic", session.ProviderKey);
        Assert.AreEqual("anthropic", tabState.ProviderId.Value);
        Assert.AreEqual("claude-sonnet-4", tabState.ModelId);
        Assert.AreEqual(AgentReasoningEffort.High, tabState.ReasoningEffort);
        Assert.IsNull(tabState.Usage);
        CollectionAssert.AreEqual(new[] { "019e1584" }, detachedSessionIds);
        CollectionAssert.AreEqual(new[] { session }, updatedSessions);
        Assert.IsTrue(persisted);
    }

    [TestMethod]
    public async Task SwitchSessionProviderAsync_AllowsNativeSourceWithoutReadingHistory()
    {
        var coordinator = new SessionProviderSwitchCoordinator(
            CreateProviderStates(includeNative: true),
            static tab =>
            {
                tab.ModelId = "claude-sonnet-4";
                return Task.CompletedTask;
            },
            static _ => Task.FromResult(true),
            static _ => { },
            static () => Task.CompletedTask);
        var createdAt = DateTimeOffset.Parse("2026-04-19T10:00:00+00:00");
        var session = CreateSession("native-session", ModelProviderIds.Codex.Value, createdAt);
        var tabState = CreateTabState(session, ModelProviderIds.Codex.Value, "gpt-5");

        var switched = await coordinator.SwitchSessionProviderAsync(
            session,
            tabState,
            new ModelProviderId("anthropic")).ConfigureAwait(false);

        Assert.IsTrue(switched);
        Assert.AreEqual("native-session", session.SessionId);
        Assert.AreEqual("anthropic", session.ProviderId);
        Assert.AreEqual("anthropic", tabState.ProviderId.Value);
    }

    [TestMethod]
    public async Task SwitchSessionProviderAsync_AllowsUnavailableSourceProvider()
    {
        var coordinator = new SessionProviderSwitchCoordinator(
            CreateProviderStates(),
            static tab =>
            {
                tab.ModelId = "claude-sonnet-4";
                return Task.CompletedTask;
            },
            static _ => Task.FromResult(true),
            static _ => { },
            static () => Task.CompletedTask);
        var createdAt = DateTimeOffset.Parse("2026-04-19T10:00:00+00:00");
        var session = CreateSession("session-1", "disabled-provider", createdAt);
        var tabState = CreateTabState(session, "disabled-provider", "old-model");

        Assert.IsTrue(coordinator.CanSelectSessionProvider(session, tabState));

        var switched = await coordinator.SwitchSessionProviderAsync(
            session,
            tabState,
            new ModelProviderId("anthropic")).ConfigureAwait(false);

        Assert.IsTrue(switched);
        Assert.AreEqual("anthropic", session.ProviderId);
        Assert.AreEqual("anthropic", session.ProviderKey);
        Assert.AreEqual("anthropic", tabState.ProviderId.Value);
        Assert.AreEqual("claude-sonnet-4", tabState.ModelId);
    }

    [TestMethod]
    public async Task SwitchSessionProviderAsync_DropsPreviousProviderModelWhenTargetHasNoModels()
    {
        var coordinator = new SessionProviderSwitchCoordinator(
            CreateProviderStates(includeNative: true),
            static tab =>
            {
                tab.ModelId = "old-provider-model";
                tab.ReasoningEffort = AgentReasoningEffort.High;
                return Task.CompletedTask;
            },
            static _ => Task.FromResult(true),
            static _ => { },
            static () => Task.CompletedTask);
        var createdAt = DateTimeOffset.Parse("2026-04-19T10:00:00+00:00");
        var session = CreateSession("session-1", "openai", createdAt);
        session.ModelId = "old-provider-model";
        session.ReasoningEffort = AgentReasoningEffort.High;
        var tabState = CreateTabState(session, "openai", "old-provider-model");

        var switched = await coordinator.SwitchSessionProviderAsync(
            session,
            tabState,
            ModelProviderIds.Codex).ConfigureAwait(false);

        Assert.IsTrue(switched);
        Assert.AreEqual(ModelProviderIds.Codex.Value, session.ProviderId);
        Assert.IsNull(tabState.ModelId);
        Assert.IsNull(session.ModelId);
        Assert.IsNull(tabState.ReasoningEffort);
        Assert.IsNull(session.ReasoningEffort);
    }

    [TestMethod]
    public async Task SwitchSessionProviderAsync_UpdatesVisibleProviderBeforeDetachingSession()
    {
        var createdAt = DateTimeOffset.Parse("2026-04-19T10:00:00+00:00");
        var session = CreateSession("session-1", "openai", createdAt);
        var tabState = CreateTabState(session, "openai", "gpt-4.1");
        var observedTargetDuringDetach = false;
        var coordinator = new SessionProviderSwitchCoordinator(
            CreateProviderStates(),
            static _ => Task.CompletedTask,
            _ =>
            {
                observedTargetDuringDetach =
                    string.Equals(session.ProviderId, "anthropic", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(tabState.ProviderId.Value, "anthropic", StringComparison.OrdinalIgnoreCase);
                return Task.FromResult(true);
            },
            static _ => { },
            static () => Task.CompletedTask);

        var switched = await coordinator.SwitchSessionProviderAsync(
            session,
            tabState,
            new ModelProviderId("anthropic")).ConfigureAwait(false);

        Assert.IsTrue(switched);
        Assert.IsTrue(observedTargetDuringDetach, "Refreshes raised while the previous session is detached must not restore the old provider selection.");
    }

    [TestMethod]
    public async Task SwitchSessionProviderAsync_DropsPreviousProviderModelSelection()
    {
        var createdAt = DateTimeOffset.Parse("2026-04-19T10:00:00+00:00");
        var session = CreateSession("session-1", "openai", createdAt);
        session.ModelId = "gpt-4.1";
        var tabState = CreateTabState(session, "openai", "gpt-4.1");
        var coordinator = new SessionProviderSwitchCoordinator(
            CreateProviderStates(),
            tab =>
            {
                tab.ModelId = "gpt-4.1";
                tab.ReasoningEffort = AgentReasoningEffort.High;
                return Task.CompletedTask;
            },
            static _ => Task.FromResult(true),
            static _ => { },
            static () => Task.CompletedTask);

        var switched = await coordinator.SwitchSessionProviderAsync(
            session,
            tabState,
            new ModelProviderId("anthropic")).ConfigureAwait(false);

        Assert.IsTrue(switched);
        Assert.AreEqual("claude-sonnet-4", tabState.ModelId);
        Assert.AreEqual("claude-sonnet-4", session.ModelId);
        Assert.AreEqual(AgentReasoningEffort.Medium, tabState.ReasoningEffort);
        Assert.AreEqual(AgentReasoningEffort.Medium, session.ReasoningEffort);
    }

    [TestMethod]
    public void CanSwitchSessionProvider_AllowsDirectCodexTarget()
    {
        var coordinator = new SessionProviderSwitchCoordinator(
            CreateProviderStates(includeNative: true),
            static _ => Task.CompletedTask,
            static _ => Task.FromResult(true),
            static _ => { },
            static () => Task.CompletedTask);
        var session = CreateSession("session-1", "openai", DateTimeOffset.UtcNow);
        var tabState = CreateTabState(session, "openai", "gpt-4.1");

        Assert.IsTrue(coordinator.CanSelectSessionProvider(session, tabState));
        Assert.IsTrue(coordinator.CanSwitchSessionProvider(session, tabState, ModelProviderIds.Codex));
    }

    private static Dictionary<string, ModelProviderState> CreateProviderStates(bool includeNative = false)
    {
        var states = new Dictionary<string, ModelProviderState>(StringComparer.OrdinalIgnoreCase)
        {
            ["openai"] = ReadyState("openai", "OpenAI"),
            ["codex"] = ReadyState("codex", "Codex ChatGPT subscription"),
            ["anthropic"] = ReadyState("anthropic", "Anthropic"),
        };
        if (includeNative)
        {
            states[ModelProviderIds.Codex.Value] = ReadyState(ModelProviderIds.Codex.Value, "Codex");
        }

        return states;
    }

    private static ModelProviderState ReadyState(string ProviderId, string displayName)
    {
        var state = new ModelProviderState(new ModelProviderId(ProviderId), displayName)
        {
            Availability = ModelProviderAvailability.Ready,
        };
        if (string.Equals(ProviderId, "anthropic", StringComparison.OrdinalIgnoreCase))
        {
            state.Models.Add(new AgentModelInfo(
                "claude-sonnet-4",
                "Claude Sonnet 4",
                SupportedReasoningEfforts: [AgentReasoningEffort.Low, AgentReasoningEffort.Medium]));
            state.SelectedModelId = "claude-sonnet-4";
            state.SelectedReasoningEffort = AgentReasoningEffort.Medium;
        }
        else if (string.Equals(ProviderId, "openai", StringComparison.OrdinalIgnoreCase))
        {
            state.Models.Add(new AgentModelInfo("gpt-4.1", "GPT-4.1"));
            state.SelectedModelId = "gpt-4.1";
        }

        return state;
    }

    private static SessionViewDescriptor CreateSession(string sessionId, string ProviderId, DateTimeOffset timestamp)
        => new()
        {
            SessionId = sessionId,
            Kind = SessionViewKind.ProjectSession,
            ProviderId = ProviderId,
            ProviderKey = ProviderId,
            ProjectRef = "project-1",
            WorkingDirectory = @"C:\repo",
            Title = "Review startup",
            Status = SessionViewStatus.Active,
            CreatedAt = timestamp,
            UpdatedAt = timestamp,
            LastActiveAt = timestamp,
            StartedAt = timestamp,
        };

    private static OpenSessionState CreateTabState(SessionViewDescriptor session, string ProviderId, string modelId)
        => new(session, new Presentation.Timeline.SessionTimelinePresenter(
            new InlineUiDispatcher(),
            static () => null,
            localFileRootPath: null))
        {
            ProviderId = new ModelProviderId(ProviderId),
            ModelId = modelId,
            Usage = new AgentSessionUsage(
                Window: new AgentWindowUsageSnapshot(1200, 8000, 3, "Old usage"),
                Scope: AgentUsageScope.SessionTotal,
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
}
