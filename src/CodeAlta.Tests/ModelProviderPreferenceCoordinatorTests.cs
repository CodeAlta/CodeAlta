using CodeAlta.Agent;
using CodeAlta.Tui.App;
using CodeAlta.Tui.App.State;
using CodeAlta.Catalog;
using CodeAlta.Tui.Models;
using CodeAlta.Tui.Presentation.Chat;
using CodeAlta.Tui.Presentation.Timeline;
using CodeAlta.Tui.Threading;
using Views = CodeAlta.Tui.Views;

namespace CodeAlta.Tests;

[TestClass]
public sealed class ModelProviderPreferenceCoordinatorTests
{
    [TestMethod]
    public void ApplyDraftModelProviderPreference_RestoresRememberedProjectDraftPreference()
    {
        using var temp = TempDirectory.Create();
        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });
        var coordinator = new ModelProviderPreferenceCoordinator(store, Views.CodeAltaApp.UiLogger);
        var providerState = new ModelProviderState(new ModelProviderId("zai"), "ZAI");
        providerState.Models.Add(new AgentModelInfo(
            "gpt-5",
            SupportedReasoningEfforts: [AgentReasoningEffort.Low, AgentReasoningEffort.High]));
        providerState.Models.Add(new AgentModelInfo(
            "glm-5.1",
            SupportedReasoningEfforts: [AgentReasoningEffort.Medium, AgentReasoningEffort.High]));

        var projectA = Path.Combine(temp.Path, "project-a");
        var projectB = Path.Combine(temp.Path, "project-b");
        var viewState = new SessionViewViewState();
        coordinator.RememberGlobalModelProviderPreference(
            viewState,
            new ModelProviderId("zai"),
            "glm-5.1",
            AgentReasoningEffort.Medium,
            projectA,
            draftProjectId: "project-a",
            rememberDraftScope: true);
        coordinator.RememberGlobalModelProviderPreference(
            viewState,
            new ModelProviderId("zai"),
            "gpt-5",
            AgentReasoningEffort.High,
            projectB,
            draftProjectId: "project-b",
            rememberDraftScope: true);

        coordinator.ApplyDraftModelProviderPreference(providerState, viewState, projectB, "project-b");
        Assert.AreEqual("gpt-5", providerState.SelectedModelId);
        Assert.AreEqual(AgentReasoningEffort.High, providerState.SelectedReasoningEffort);

        coordinator.ApplyDraftModelProviderPreference(providerState, viewState, projectA, "project-a");

        Assert.AreEqual("glm-5.1", providerState.SelectedModelId);
        Assert.AreEqual(AgentReasoningEffort.Medium, providerState.SelectedReasoningEffort);
    }

    [TestMethod]
    public void ApplyDraftModelProviderPreference_PreservesPersistedModelBeforeModelDiscovery()
    {
        using var temp = TempDirectory.Create();
        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });
        var coordinator = new ModelProviderPreferenceCoordinator(store, Views.CodeAltaApp.UiLogger);
        var viewState = new SessionViewViewState
        {
            ProjectPreferences = new Dictionary<string, SessionViewPreference>(StringComparer.OrdinalIgnoreCase)
            {
                ["project-a"] = new()
                {
                    ProviderKey = "zai",
                    ModelId = "glm-5.1",
                    ReasoningEffort = AgentReasoningEffort.Medium,
                },
            },
        };
        var providerState = new ModelProviderState(new ModelProviderId("zai"), "ZAI");

        coordinator.ApplyDraftModelProviderPreference(providerState, viewState, draftProjectRoot: null, draftProjectId: "project-a");

        Assert.AreEqual("glm-5.1", providerState.SelectedModelId);
        Assert.AreEqual(AgentReasoningEffort.Medium, providerState.SelectedReasoningEffort);

        providerState.Models.Add(new AgentModelInfo("gpt-5"));
        providerState.Models.Add(new AgentModelInfo(
            "glm-5.1",
            SupportedReasoningEfforts: [AgentReasoningEffort.Low, AgentReasoningEffort.Medium]));

        coordinator.ApplyDraftModelProviderPreference(providerState, viewState, draftProjectRoot: null, draftProjectId: "project-a");

        Assert.AreEqual("glm-5.1", providerState.SelectedModelId);
        Assert.AreEqual(AgentReasoningEffort.Medium, providerState.SelectedReasoningEffort);
    }

    [TestMethod]
    public void RememberGlobalModelProviderPreference_PersistsGlobalProjectPreferenceWithoutChangingConfigDefaultProvider()
    {
        using var temp = TempDirectory.Create();
        File.WriteAllText(
            Path.Combine(temp.Path, "config.toml"),
            """
            [providers.zai]
            type = "openai-chat"
            display_name = "ZAI"
            api_key_env = "TEST_API_KEY"
            """);
        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });
        var coordinator = new ModelProviderPreferenceCoordinator(store, Views.CodeAltaApp.UiLogger);
        var viewState = new SessionViewViewState();

        coordinator.RememberGlobalModelProviderPreference(
            viewState,
            new ModelProviderId("zai"),
            "glm-5.1",
            AgentReasoningEffort.High,
            rememberDraftScope: true);

        Assert.IsNull(store.GetEffectiveDefaultProvider());
        Assert.IsTrue(viewState.ProjectPreferences.TryGetValue(ModelProviderPreferenceCoordinator.GlobalProjectPreferenceKey, out var preference));
        Assert.AreEqual("zai", preference.ProviderKey);
        Assert.AreEqual("glm-5.1", preference.ModelId);
        Assert.AreEqual(AgentReasoningEffort.High, preference.ReasoningEffort);
        Assert.IsFalse(File.ReadAllText(Path.Combine(temp.Path, "config.toml")).Contains("default_provider", StringComparison.Ordinal));
    }

    [TestMethod]
    public void RememberGlobalModelProviderPreference_PersistsSessionScopedProjectPreference()
    {
        using var temp = TempDirectory.Create();
        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });
        var coordinator = new ModelProviderPreferenceCoordinator(store, Views.CodeAltaApp.UiLogger);
        var viewState = new SessionViewViewState();

        coordinator.RememberGlobalModelProviderPreference(
            viewState,
            new ModelProviderId("zai"),
            "glm-5.1",
            AgentReasoningEffort.Medium,
            draftProjectRoot: Path.Combine(temp.Path, "project-a"),
            draftProjectId: "project-a",
            rememberDraftScope: false);

        Assert.IsTrue(viewState.ProjectPreferences.TryGetValue("project-a", out var preference));
        Assert.AreEqual("zai", preference.ProviderKey);
        Assert.AreEqual("glm-5.1", preference.ModelId);
        Assert.AreEqual(AgentReasoningEffort.Medium, preference.ReasoningEffort);
    }

    [TestMethod]
    public void ApplySessionPreference_PrefersPersistedSessionPreferenceOverProviderDefaults()
    {
        using var temp = TempDirectory.Create();
        File.WriteAllText(
            Path.Combine(temp.Path, "config.toml"),
            """
            [providers.zai]
            type = "openai-chat"
            display_name = "ZAI"
            api_key_env = "TEST_API_KEY"
            model = "gpt-5"
            reasoning_effort = "high"
            """);

        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });
        var coordinator = new ModelProviderPreferenceCoordinator(store, Views.CodeAltaApp.UiLogger);
        ModelProviderDescriptor[] providerDescriptors =
        [
            new ModelProviderDescriptor(new ModelProviderId("zai"), "ZAI"),
        ];
        var providerStates = ModelProviderPresentation.CreateProviderStates(providerDescriptors);
        providerStates["zai"].Models.Add(
            new AgentModelInfo(
                "gpt-5",
                DisplayName: "GPT-5",
                DefaultReasoningEffort: AgentReasoningEffort.High,
                SupportedReasoningEfforts: [AgentReasoningEffort.Medium, AgentReasoningEffort.High]));
        providerStates["zai"].Models.Add(
            new AgentModelInfo(
                "glm-5.1",
                DisplayName: "GLM-5.1",
                DefaultReasoningEffort: AgentReasoningEffort.Medium,
                SupportedReasoningEfforts: [AgentReasoningEffort.Low, AgentReasoningEffort.Medium]));

        var tab = CreateOpenSessionState("session-1", "zai");
        var viewState = new SessionViewViewState
        {
            SessionPreferences = new Dictionary<string, SessionViewPreference>(StringComparer.OrdinalIgnoreCase)
            {
                ["session-1"] = new SessionViewPreference
                {
                    ModelId = "glm-5.1",
                    ReasoningEffort = AgentReasoningEffort.Medium,
                },
            },
        };

        coordinator.ApplySessionPreference(tab, viewState, sessionProjectRoot: null, providerStates);

        Assert.AreEqual("glm-5.1", tab.ModelId);
        Assert.AreEqual(AgentReasoningEffort.Medium, tab.ReasoningEffort);
    }

    [TestMethod]
    public void ApplySessionPreference_PreservesPersistedModelMissingFromCatalog()
    {
        using var temp = TempDirectory.Create();
        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });
        var coordinator = new ModelProviderPreferenceCoordinator(store, Views.CodeAltaApp.UiLogger);
        ModelProviderDescriptor[] providerDescriptors =
        [
            new ModelProviderDescriptor(new ModelProviderId("zai"), "ZAI"),
        ];
        var providerStates = ModelProviderPresentation.CreateProviderStates(providerDescriptors);
        providerStates["zai"].Models.Add(new AgentModelInfo("gpt-5", DisplayName: "GPT-5"));
        var tab = CreateOpenSessionState("session-1", "zai");
        var viewState = new SessionViewViewState
        {
            SessionPreferences = new Dictionary<string, SessionViewPreference>(StringComparer.OrdinalIgnoreCase)
            {
                ["session-1"] = new SessionViewPreference
                {
                    ModelId = "glm-5.1",
                    ReasoningEffort = AgentReasoningEffort.Medium,
                },
            },
        };

        coordinator.ApplySessionPreference(tab, viewState, sessionProjectRoot: null, providerStates);

        Assert.AreEqual("glm-5.1", tab.ModelId);
        Assert.AreEqual(AgentReasoningEffort.Medium, tab.ReasoningEffort);
    }

    [TestMethod]
    public void ApplySessionPreference_PrefersDescriptorModelOverProviderDefaults()
    {
        using var temp = TempDirectory.Create();
        File.WriteAllText(
            Path.Combine(temp.Path, "config.toml"),
            """
            [providers.zai]
            type = "openai-chat"
            display_name = "ZAI"
            api_key_env = "TEST_API_KEY"
            model = "gpt-5"
            reasoning_effort = "high"
            """);

        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });
        var coordinator = new ModelProviderPreferenceCoordinator(store, Views.CodeAltaApp.UiLogger);
        ModelProviderDescriptor[] providerDescriptors =
        [
            new ModelProviderDescriptor(new ModelProviderId("zai"), "ZAI"),
        ];
        var providerStates = ModelProviderPresentation.CreateProviderStates(providerDescriptors);
        providerStates["zai"].Models.Add(new AgentModelInfo(
            "gpt-5",
            SupportedReasoningEfforts: [AgentReasoningEffort.High]));
        providerStates["zai"].Models.Add(new AgentModelInfo(
            "glm-5.1",
            SupportedReasoningEfforts: [AgentReasoningEffort.Medium]));
        var tab = CreateOpenSessionState("session-1", "zai");
        tab.Session.ModelId = "glm-5.1";
        tab.Session.ReasoningEffort = AgentReasoningEffort.Medium;

        coordinator.ApplySessionPreference(tab, new SessionViewViewState(), sessionProjectRoot: null, providerStates);

        Assert.AreEqual("glm-5.1", tab.ModelId);
        Assert.AreEqual(AgentReasoningEffort.Medium, tab.ReasoningEffort);
    }

    private static OpenSessionState CreateOpenSessionState(string sessionId, string providerKey)
    {
        var session = new SessionViewDescriptor
        {
            SessionId = sessionId,
            Kind = SessionViewKind.ProjectSession,
            ProviderId = providerKey,
            WorkingDirectory = @"C:\code\CodeAlta",
            Title = "Investigate provider defaults",
            Status = SessionViewStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            LastActiveAt = DateTimeOffset.UtcNow,
        };
        var timeline = new SessionTimelinePresenter(new InlineUiDispatcher(), static () => null);
        var tab = new OpenSessionState(session, timeline)
        {
            ProviderId = new ModelProviderId(providerKey),
        };
        return tab;
    }

    private sealed class InlineUiDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;

        public void Post(Action callback)
        {
            ArgumentNullException.ThrowIfNull(callback);
            callback();
        }

        public Task InvokeAsync(Action callback)
        {
            ArgumentNullException.ThrowIfNull(callback);
            callback();
            return Task.CompletedTask;
        }

        public Task<T> InvokeAsync<T>(Func<T> callback)
        {
            ArgumentNullException.ThrowIfNull(callback);
            return Task.FromResult(callback());
        }
    }

    private sealed class TempDirectory(string path) : IDisposable
    {
        public string Path { get; } = path;

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(
                AppContext.BaseDirectory,
                "model-provider-preference-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TempDirectory(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
