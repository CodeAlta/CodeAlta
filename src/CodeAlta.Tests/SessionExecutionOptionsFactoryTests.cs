using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.App;
using CodeAlta.App.Context;
using CodeAlta.App.State;
using CodeAlta.Catalog;
using CodeAlta.LiveTool;
using CodeAlta.Models;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Threading;

namespace CodeAlta.Tests;

[TestClass]
public sealed class SessionExecutionOptionsFactoryTests
{
    [TestMethod]
    public async Task BuildPreferredExecutionOptions_GlobalScopeDoesNotInheritSelectedProjectForAltaTool()
    {
        using var temp = TestTempDirectory.Create();
        var project = CreateProject("project-a", Path.Combine(temp.Path, "project-a"));
        Directory.CreateDirectory(project.ProjectPath);
        var factory = CreateFactory(temp.Path, project);

        var options = factory.BuildPreferredExecutionOptions(ModelProviderIds.Codex, temp.Path, []);

        var caller = await InvokeToolStatusAsync(options).ConfigureAwait(false);
        Assert.AreEqual("agent", caller.GetProperty("kind").GetString());
        Assert.IsFalse(caller.TryGetProperty("sourceProjectId", out _), "Global coordinator tools must not be project-scoped just because a project is selected in the UI.");
    }

    [TestMethod]
    public async Task BuildPreferredExecutionOptions_ProjectScopeKeepsSelectedProjectForAltaTool()
    {
        using var temp = TestTempDirectory.Create();
        var project = CreateProject("project-a", Path.Combine(temp.Path, "project-a"));
        Directory.CreateDirectory(project.ProjectPath);
        var factory = CreateFactory(temp.Path, project);

        var options = factory.BuildPreferredExecutionOptions(ModelProviderIds.Codex, project.ProjectPath, [project.ProjectPath]);

        var caller = await InvokeToolStatusAsync(options).ConfigureAwait(false);
        Assert.AreEqual(project.Id, caller.GetProperty("sourceProjectId").GetString());
    }

    [TestMethod]
    public async Task BuildPreferredExecutionOptions_UsesDeferredSourceSessionProviderForAltaTool()
    {
        using var temp = TestTempDirectory.Create();
        var project = CreateProject("project-a", Path.Combine(temp.Path, "project-a"));
        Directory.CreateDirectory(project.ProjectPath);
        var factory = CreateFactory(temp.Path, project);
        string? createdSessionId = null;

        var options = factory.BuildPreferredExecutionOptions(
            ModelProviderIds.Codex,
            project.ProjectPath,
            [project.ProjectPath],
            () => createdSessionId);
        createdSessionId = "canonical-session-id";

        var caller = await InvokeToolStatusAsync(options).ConfigureAwait(false);
        Assert.AreEqual(createdSessionId, caller.GetProperty("sourceSessionId").GetString());
        Assert.AreEqual(project.Id, caller.GetProperty("sourceProjectId").GetString());
    }

    [TestMethod]
    public void BuildPreferredExecutionOptions_UsesCanonicalProviderStateForDraftSessionCreation()
    {
        using var temp = TestTempDirectory.Create();
        var catalogOptions = new CatalogOptions { GlobalRoot = temp.Path };
        var uiDispatcher = new InlineUiDispatcher();
        var sessionState = TestSessionStateServices.CreateCoordinator(
            new ProjectCatalog(catalogOptions),
            new SessionViewCatalog(catalogOptions),
            uiDispatcher,
            new ShellStateStore(uiDispatcher));
        sessionState.ApplyInitialCatalogState(new ShellSessionStateCoordinator.InitialCatalogState([], [], new SessionViewViewState()));
        var selection = new SessionSelectionContext(
            sessionState,
            static (_, _) => Task.CompletedTask,
            static _ => false);
        var providerState = new ModelProviderState(ModelProviderIds.Codex, "Codex")
        {
            Availability = ModelProviderAvailability.Ready,
            SelectedModelId = "gpt-selected",
            SelectedReasoningEffort = AgentReasoningEffort.High,
        };
        providerState.Models.Add(new AgentModelInfo(
            "gpt-wrong",
            SupportedReasoningEfforts: [AgentReasoningEffort.Low]));
        providerState.Models.Add(new AgentModelInfo(
            "gpt-selected",
            SupportedReasoningEfforts: [AgentReasoningEffort.High]));
        var factory = new SessionExecutionOptionsFactory(
            catalogOptions,
            new Dictionary<string, ModelProviderState>(StringComparer.Ordinal)
            {
                [ModelProviderIds.Codex.Value] = providerState,
            },
            selection,
            new SessionPermissionRequestCoordinator(selection, CreateCommandContext(uiDispatcher), uiDispatcher),
            new SessionUserInputRequestCoordinator(selection, CreateCommandContext(uiDispatcher)));

        var options = factory.BuildPreferredExecutionOptions(ModelProviderIds.Codex, temp.Path, []);

        Assert.AreEqual("gpt-selected", options.Model);
        Assert.AreEqual(AgentReasoningEffort.High, options.ReasoningEffort);
    }

    [TestMethod]
    public void BuildPreferredExecutionOptions_AllowsProviderBeforeStateCatalogSyncs()
    {
        using var temp = TestTempDirectory.Create();
        var project = CreateProject("project-a", Path.Combine(temp.Path, "project-a"));
        Directory.CreateDirectory(project.ProjectPath);
        var factory = CreateFactory(temp.Path, project);
        var ProviderId = new ModelProviderId("gemini");

        var options = factory.BuildPreferredExecutionOptions(new ModelProviderId(ProviderId.Value), temp.Path, []);

        Assert.AreEqual(ProviderId.Value, options.ProviderId.Value);
        Assert.AreEqual("gemini", options.ProviderKey);
        Assert.IsNull(options.Model);
        Assert.IsNull(options.ReasoningEffort);
    }

    [TestMethod]
    public void BuildExecutionOptions_UsesOpenTabProviderSelection()
    {
        using var temp = TestTempDirectory.Create();
        var catalogOptions = new CatalogOptions { GlobalRoot = temp.Path };
        var uiDispatcher = new InlineUiDispatcher();
        var sessionState = TestSessionStateServices.CreateCoordinator(
            new ProjectCatalog(catalogOptions),
            new SessionViewCatalog(catalogOptions),
            uiDispatcher,
            new ShellStateStore(uiDispatcher));
        var session = CreateSession("session-1", "openai", temp.Path);
        sessionState.ApplyInitialCatalogState(new ShellSessionStateCoordinator.InitialCatalogState([], [session], new SessionViewViewState()));
        sessionState.OpenSession(session.SessionId);
        var tab = sessionState.FindOpenSession(session.SessionId);
        Assert.IsNotNull(tab);
        tab.ProviderId = new ModelProviderId("anthropic");
        tab.ModelId = "claude-sonnet-4";
        tab.ReasoningEffort = AgentReasoningEffort.High;
        session.ProviderId = "openai";
        session.ProviderKey = "openai";
        var selection = new SessionSelectionContext(
            sessionState,
            static (_, _) => Task.CompletedTask,
            static _ => false);
        var commandContext = CreateCommandContext(uiDispatcher);
        var factory = new SessionExecutionOptionsFactory(
            catalogOptions,
            new Dictionary<string, ModelProviderState>(StringComparer.OrdinalIgnoreCase),
            selection,
            new SessionPermissionRequestCoordinator(selection, commandContext, uiDispatcher),
            new SessionUserInputRequestCoordinator(selection, commandContext));

        var options = factory.BuildExecutionOptions(session, tab);

        Assert.AreEqual("anthropic", options.ProviderId.Value);
        Assert.AreEqual("anthropic", options.ProviderKey);
        Assert.AreEqual("claude-sonnet-4", options.Model);
        Assert.AreEqual(AgentReasoningEffort.High, options.ReasoningEffort);
    }

    [TestMethod]
    public async Task BuildPreferredExecutionOptions_AddsAltaToolForAnyProviderWhenServicesAreAvailable()
    {
        using var temp = TestTempDirectory.Create();
        var project = CreateProject("project-a", Path.Combine(temp.Path, "project-a"));
        Directory.CreateDirectory(project.ProjectPath);
        var factory = CreateFactory(temp.Path, project);
        var providerId = new ModelProviderId("gemma4-12b");

        var options = factory.BuildPreferredExecutionOptions(providerId, project.ProjectPath, [project.ProjectPath]);

        Assert.AreEqual(providerId.Value, options.ProviderId.Value);
        var caller = await InvokeToolStatusAsync(options).ConfigureAwait(false);
        Assert.AreEqual(project.Id, caller.GetProperty("sourceProjectId").GetString());
    }

    private static SessionExecutionOptionsFactory CreateFactory(string globalRoot, ProjectDescriptor selectedProject)
    {
        var catalogOptions = new CatalogOptions { GlobalRoot = globalRoot };
        var uiDispatcher = new InlineUiDispatcher();
        var sessionState = TestSessionStateServices.CreateCoordinator(
            new ProjectCatalog(catalogOptions),
            new SessionViewCatalog(catalogOptions),
            uiDispatcher,
            new ShellStateStore(uiDispatcher));
        sessionState.ApplyInitialCatalogState(new ShellSessionStateCoordinator.InitialCatalogState(
            [selectedProject],
            [],
            new SessionViewViewState()));
        sessionState.SelectProjectScope(selectedProject.Id);

        var selection = new SessionSelectionContext(
            sessionState,
            static (_, _) => Task.CompletedTask,
            static _ => false);
        var services = new AltaServiceCollection();
        var commandContext = CreateCommandContext(uiDispatcher);
        return new SessionExecutionOptionsFactory(
            catalogOptions,
            new Dictionary<string, ModelProviderState>(StringComparer.Ordinal)
            {
                [ModelProviderIds.Codex.Value] = new ModelProviderState(ModelProviderIds.Codex, "Codex")
                {
                    Availability = ModelProviderAvailability.Ready,
                    SelectedModelId = "gpt-test",
                },
            },
            selection,
            new SessionPermissionRequestCoordinator(selection, commandContext, uiDispatcher),
            new SessionUserInputRequestCoordinator(selection, commandContext),
            null,
            services);
    }

    private static ShellSessionCommandContext CreateCommandContext(IUiDispatcher uiDispatcher)
        => new(
            new DelegatingSessionLifecycleCommandPort(
                static _ => Task.FromResult<SessionViewDescriptor?>(null),
                static _ => Task.FromResult<SessionViewDescriptor?>(null),
                static () => Task.CompletedTask),
            new SessionCommandUiPort(
                uiDispatcher,
                static () => false,
                static () => true,
                static () => { },
                static () => { },
                static () => { },
                static () => { },
                static (_, action, _) => action()),
            new PromptSessionPort(
                uiDispatcher,
                static () => true,
                static () => { },
                static _ => { },
                static () => [],
                static _ => { }),
            static () => new PromptSessionId("test-prompt"),
            new ShellStatusPort(
                uiDispatcher,
                static (_, _, _) => { },
                static (_, _, _, _) => { }));

    private static async Task<JsonElement> InvokeToolStatusAsync(SessionExecutionOptions options)
    {
        Assert.IsNotNull(options.Tools);
        var tool = options.Tools.Single(static candidate => string.Equals(candidate.Spec.Name, "alta", StringComparison.Ordinal));
        using var arguments = JsonDocument.Parse("""
            {"args":["tool","status"]}
            """);
        var result = await tool.Handler(
                new AgentToolInvocation(
                    ModelProviderIds.Codex,
                    "provider-session",
                    "tool-call",
                    "alta",
                    arguments.RootElement),
                CancellationToken.None)
            .ConfigureAwait(false);

        Assert.IsTrue(result.Success, result.Error);
        var text = ((AgentToolResultItem.Text)result.Items.Single()).Value;
        var statusLine = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.Contains("\"type\":\"alta.tool.status\"", StringComparison.Ordinal));
        using var status = JsonDocument.Parse(statusLine);
        return status.RootElement.GetProperty("caller").Clone();
    }

    private static ProjectDescriptor CreateProject(string id, string path)
        => new()
        {
            Id = id,
            Slug = id,
            Name = id,
            DisplayName = id,
            ProjectPath = path,
            DefaultBranch = "main",
        };

    private static SessionViewDescriptor CreateSession(string sessionId, string providerKey, string workingDirectory)
        => new()
        {
            SessionId = sessionId,
            Kind = SessionViewKind.GlobalSession,
            ProviderId = providerKey,
            ProviderKey = providerKey,
            WorkingDirectory = workingDirectory,
            Title = sessionId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            LastActiveAt = DateTimeOffset.UtcNow,
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
