using CodeAlta.Agent;
using CodeAlta.App;
using CodeAlta.App.State;
using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Presentation.Chat;
using CodeAlta.Presentation.Timeline;
using CodeAlta.Threading;

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
        var backendState = new ChatBackendState(new AgentBackendId("zai"), "ZAI");
        backendState.Models.Add(new AgentModelInfo(
            "gpt-5",
            SupportedReasoningEfforts: [AgentReasoningEffort.Low, AgentReasoningEffort.High]));
        backendState.Models.Add(new AgentModelInfo(
            "glm-5.1",
            SupportedReasoningEfforts: [AgentReasoningEffort.Medium, AgentReasoningEffort.High]));

        var projectA = Path.Combine(temp.Path, "project-a");
        var projectB = Path.Combine(temp.Path, "project-b");
        var viewState = new WorkThreadViewState();
        coordinator.RememberGlobalModelProviderPreference(
            viewState,
            new AgentBackendId("zai"),
            "glm-5.1",
            AgentReasoningEffort.Medium,
            projectA,
            draftProjectId: "project-a",
            rememberDraftScope: true);
        coordinator.RememberGlobalModelProviderPreference(
            viewState,
            new AgentBackendId("zai"),
            "gpt-5",
            AgentReasoningEffort.High,
            projectB,
            draftProjectId: "project-b",
            rememberDraftScope: true);

        coordinator.ApplyDraftModelProviderPreference(backendState, viewState, projectB, "project-b");
        Assert.AreEqual("gpt-5", backendState.SelectedModelId);
        Assert.AreEqual(AgentReasoningEffort.High, backendState.SelectedReasoningEffort);

        coordinator.ApplyDraftModelProviderPreference(backendState, viewState, projectA, "project-a");

        Assert.AreEqual("glm-5.1", backendState.SelectedModelId);
        Assert.AreEqual(AgentReasoningEffort.Medium, backendState.SelectedReasoningEffort);
    }

    [TestMethod]
    public void ApplyDraftModelProviderPreference_PreservesPersistedModelBeforeModelDiscovery()
    {
        using var temp = TempDirectory.Create();
        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });
        var coordinator = new ModelProviderPreferenceCoordinator(store, Views.CodeAltaApp.UiLogger);
        var viewState = new WorkThreadViewState
        {
            ProjectPreferences = new Dictionary<string, WorkThreadPreference>(StringComparer.OrdinalIgnoreCase)
            {
                ["project-a"] = new()
                {
                    ProviderKey = "zai",
                    ModelId = "glm-5.1",
                    ReasoningEffort = AgentReasoningEffort.Medium,
                },
            },
        };
        var backendState = new ChatBackendState(new AgentBackendId("zai"), "ZAI");

        coordinator.ApplyDraftModelProviderPreference(backendState, viewState, draftProjectRoot: null, draftProjectId: "project-a");

        Assert.AreEqual("glm-5.1", backendState.SelectedModelId);
        Assert.AreEqual(AgentReasoningEffort.Medium, backendState.SelectedReasoningEffort);

        backendState.Models.Add(new AgentModelInfo("gpt-5"));
        backendState.Models.Add(new AgentModelInfo(
            "glm-5.1",
            SupportedReasoningEfforts: [AgentReasoningEffort.Low, AgentReasoningEffort.Medium]));

        coordinator.ApplyDraftModelProviderPreference(backendState, viewState, draftProjectRoot: null, draftProjectId: "project-a");

        Assert.AreEqual("glm-5.1", backendState.SelectedModelId);
        Assert.AreEqual(AgentReasoningEffort.Medium, backendState.SelectedReasoningEffort);
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
        var viewState = new WorkThreadViewState();

        coordinator.RememberGlobalModelProviderPreference(
            viewState,
            new AgentBackendId("zai"),
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
    public void RememberGlobalModelProviderPreference_PersistsThreadScopedProjectPreference()
    {
        using var temp = TempDirectory.Create();
        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });
        var coordinator = new ModelProviderPreferenceCoordinator(store, Views.CodeAltaApp.UiLogger);
        var viewState = new WorkThreadViewState();

        coordinator.RememberGlobalModelProviderPreference(
            viewState,
            new AgentBackendId("zai"),
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
    public void ApplyThreadPreference_PrefersPersistedThreadPreferenceOverProviderDefaults()
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
        AgentBackendDescriptor[] backendDescriptors =
        [
            new AgentBackendDescriptor(new AgentBackendId("zai"), "ZAI"),
        ];
        var backendStates = ChatBackendPresentation.CreateBackendStates(backendDescriptors);
        backendStates["zai"].Models.Add(
            new AgentModelInfo(
                "gpt-5",
                DisplayName: "GPT-5",
                DefaultReasoningEffort: AgentReasoningEffort.High,
                SupportedReasoningEfforts: [AgentReasoningEffort.Medium, AgentReasoningEffort.High]));
        backendStates["zai"].Models.Add(
            new AgentModelInfo(
                "glm-5.1",
                DisplayName: "GLM-5.1",
                DefaultReasoningEffort: AgentReasoningEffort.Medium,
                SupportedReasoningEfforts: [AgentReasoningEffort.Low, AgentReasoningEffort.Medium]));

        var tab = CreateOpenThreadState("thread-1", "zai");
        var viewState = new WorkThreadViewState
        {
            ThreadPreferences = new Dictionary<string, WorkThreadPreference>(StringComparer.OrdinalIgnoreCase)
            {
                ["thread-1"] = new WorkThreadPreference
                {
                    ModelId = "glm-5.1",
                    ReasoningEffort = AgentReasoningEffort.Medium,
                },
            },
        };

        coordinator.ApplyThreadPreference(tab, viewState, threadProjectRoot: null, backendStates);

        Assert.AreEqual("glm-5.1", tab.ModelId);
        Assert.AreEqual(AgentReasoningEffort.Medium, tab.ReasoningEffort);
    }

    [TestMethod]
    public void ApplyThreadPreference_PreservesPersistedModelMissingFromCatalog()
    {
        using var temp = TempDirectory.Create();
        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });
        var coordinator = new ModelProviderPreferenceCoordinator(store, Views.CodeAltaApp.UiLogger);
        AgentBackendDescriptor[] backendDescriptors =
        [
            new AgentBackendDescriptor(new AgentBackendId("zai"), "ZAI"),
        ];
        var backendStates = ChatBackendPresentation.CreateBackendStates(backendDescriptors);
        backendStates["zai"].Models.Add(new AgentModelInfo("gpt-5", DisplayName: "GPT-5"));
        var tab = CreateOpenThreadState("thread-1", "zai");
        var viewState = new WorkThreadViewState
        {
            ThreadPreferences = new Dictionary<string, WorkThreadPreference>(StringComparer.OrdinalIgnoreCase)
            {
                ["thread-1"] = new WorkThreadPreference
                {
                    ModelId = "glm-5.1",
                    ReasoningEffort = AgentReasoningEffort.Medium,
                },
            },
        };

        coordinator.ApplyThreadPreference(tab, viewState, threadProjectRoot: null, backendStates);

        Assert.AreEqual("glm-5.1", tab.ModelId);
        Assert.AreEqual(AgentReasoningEffort.Medium, tab.ReasoningEffort);
    }

    [TestMethod]
    public void ApplyThreadPreference_PrefersDescriptorModelOverProviderDefaults()
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
        AgentBackendDescriptor[] backendDescriptors =
        [
            new AgentBackendDescriptor(new AgentBackendId("zai"), "ZAI"),
        ];
        var backendStates = ChatBackendPresentation.CreateBackendStates(backendDescriptors);
        backendStates["zai"].Models.Add(new AgentModelInfo(
            "gpt-5",
            SupportedReasoningEfforts: [AgentReasoningEffort.High]));
        backendStates["zai"].Models.Add(new AgentModelInfo(
            "glm-5.1",
            SupportedReasoningEfforts: [AgentReasoningEffort.Medium]));
        var tab = CreateOpenThreadState("thread-1", "zai");
        tab.Thread.ModelId = "glm-5.1";
        tab.Thread.ReasoningEffort = AgentReasoningEffort.Medium;

        coordinator.ApplyThreadPreference(tab, new WorkThreadViewState(), threadProjectRoot: null, backendStates);

        Assert.AreEqual("glm-5.1", tab.ModelId);
        Assert.AreEqual(AgentReasoningEffort.Medium, tab.ReasoningEffort);
    }

    private static OpenThreadState CreateOpenThreadState(string threadId, string providerKey)
    {
        var thread = new WorkThreadDescriptor
        {
            ThreadId = threadId,
            Kind = WorkThreadKind.ProjectThread,
            BackendId = providerKey,
            WorkingDirectory = @"C:\code\CodeAlta",
            Title = "Investigate provider defaults",
            Status = WorkThreadStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            LastActiveAt = DateTimeOffset.UtcNow,
        };
        var timeline = new ThreadTimelinePresenter(new InlineUiDispatcher(), static () => null);
        var tab = new OpenThreadState(thread, timeline)
        {
            BackendId = new AgentBackendId(providerKey),
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
                "chat-backend-preference-tests",
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
