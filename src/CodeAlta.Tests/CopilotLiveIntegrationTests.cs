using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Copilot;
using CodeAlta.DotNet;
using CodeAlta.Mcp;
using CodeAlta.Orchestration.Mcp;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Persistence;
using CodeAlta.Search;
using CodeAlta.Workspaces;
using CodeAlta.Workspaces.Bootstrap;
using CodeAlta.Workspaces.Roles;
using CodeAlta.Workspaces.Skills;
using Microsoft.Extensions.DependencyInjection;

namespace CodeAlta.Tests;

[TestClass]
public sealed class CopilotLiveIntegrationTests
{
    private const string LiveCopilotTestsEnvironmentVariable = "CODEALTA_RUN_LIVE_COPILOT_TESTS";

    [TestMethod]
    [TestCategory("LiveCopilot")]
    public async Task CopilotAgentBackend_LivePrompt_WithDottedTool_ProducesAssistantContent()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(LiveCopilotTestsEnvironmentVariable), "1", StringComparison.Ordinal))
        {
            Assert.Inconclusive(
                $"Set {LiveCopilotTestsEnvironmentVariable}=1 to run live Copilot integration tests.");
        }

        await using var backend = new CopilotAgentBackend(new CopilotAgentBackendOptions());
        var toolSchema = JsonDocument.Parse("""{"type":"object","properties":{"value":{"type":"string"}}}""").RootElement.Clone();
        IAgentSession session;
        try
        {
            session = await backend.CreateSessionAsync(
                    new AgentSessionCreateOptions
                    {
                        Streaming = true,
                        WorkingDirectory = Environment.CurrentDirectory,
                        Tools =
                        [
                            new AgentToolDefinition(
                                new AgentToolSpec("codealta.tasks.create", "Creates a task", toolSchema),
                                static (_, _) => Task.FromResult<AgentToolResult>(
                                    new(
                                        true,
                                        [new AgentToolResultItem.Text("ok")])))
                        ],
                        OnPermissionRequest = static (_, _) =>
                            Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
                    })
                .ConfigureAwait(false);
        }
        catch (FileNotFoundException ex)
        {
            Assert.Inconclusive($"Copilot executable was not found: {ex.Message}");
            return;
        }

        await using var asyncSession = session;

        var assistantContent = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var errorEvent = new TaskCompletionSource<AgentErrorEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = asyncSession.Subscribe(@event =>
        {
            switch (@event)
            {
                case AgentContentCompletedEvent message when message.Kind == AgentContentKind.Assistant && !string.IsNullOrWhiteSpace(message.Content):
                    assistantContent.TrySetResult(message.Content);
                    break;
                case AgentErrorEvent error:
                    errorEvent.TrySetResult(error);
                    break;
            }
        });

        _ = await asyncSession.SendAsync(
                new AgentSendOptions
                {
                    Input = AgentInput.Text("Reply with exactly the word pong.")
                })
            .ConfigureAwait(false);

        var timeoutTask = Task.Delay(TimeSpan.FromMinutes(2));
        var completedTask = await Task.WhenAny(assistantContent.Task, errorEvent.Task, timeoutTask).ConfigureAwait(false);

        if (completedTask == assistantContent.Task)
        {
            var content = await assistantContent.Task.ConfigureAwait(false);
            Assert.IsFalse(string.IsNullOrWhiteSpace(content));
            return;
        }

        if (completedTask == errorEvent.Task)
        {
            var error = await errorEvent.Task.ConfigureAwait(false);
            Assert.Fail($"Copilot returned an error event instead of content: {error.Message}");
        }

        Assert.Fail("No assistant content was received from Copilot within the timeout.");
    }

    [TestMethod]
    [TestCategory("LiveCopilot")]
    public async Task CopilotChatConnection_LiveProjectPrompt_ProducesApprovalsToolsAndSummary()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(LiveCopilotTestsEnvironmentVariable), "1", StringComparison.Ordinal))
        {
            Assert.Inconclusive(
                $"Set {LiveCopilotTestsEnvironmentVariable}=1 to run live Copilot integration tests.");
        }

        const string projectPath = @"C:\code\Tomlyn";
        if (!Directory.Exists(projectPath))
        {
            Assert.Inconclusive($"The live project path '{projectPath}' does not exist on this machine.");
        }

        using var temp = TempDirectory.Create();
        var db = await CreateDbAsync(temp.Path).ConfigureAwait(false);
        var repository = new AgentRepository(db);
        var backendFactory = new AgentBackendFactory();
        backendFactory.RegisterCopilot(new CopilotAgentBackendOptions());

        await using var hub = new AgentHub(backendFactory, repository);
        var receivedEvents = new List<AgentEvent>();
        await using var connection = new ChatAgentConnection(hub, receivedEvents.Add);

        AgentId agentId;
        try
        {
            agentId = await connection.EnsureConnectedAsync(
                    AgentBackendIds.Copilot,
                    projectPath,
                    model: "gpt-5-mini",
                    reasoningEffort: null,
                    tools: null,
                    permissionRequestHandler: static (_, _) =>
                        Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
                    userInputRequestHandler: null)
                .ConfigureAwait(false);
        }
        catch (FileNotFoundException ex)
        {
            Assert.Inconclusive($"Copilot executable was not found: {ex.Message}");
            return;
        }

        var assistantMessages = new List<string>();
        var permissionRequestSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var toolActivitySeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var idleSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errorEvent = new TaskCompletionSource<AgentErrorEvent>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var subscription = await hub.SubscribeSessionEventsAsync(
                agentId,
                @event =>
                {
                    switch (@event)
                    {
                        case AgentPermissionRequest:
                            permissionRequestSeen.TrySetResult();
                            break;
                        case AgentActivityEvent activity when activity.Kind is AgentActivityKind.ToolCall or AgentActivityKind.McpToolCall:
                            toolActivitySeen.TrySetResult();
                            break;
                        case AgentContentCompletedEvent message when
                            message.Kind == AgentContentKind.Assistant &&
                            !string.IsNullOrWhiteSpace(message.Content):
                            lock (assistantMessages)
                            {
                                assistantMessages.Add(message.Content);
                            }
                            break;
                        case AgentSessionUpdateEvent { Kind: AgentSessionUpdateKind.Idle }:
                            idleSeen.TrySetResult();
                            break;
                        case AgentErrorEvent error:
                            errorEvent.TrySetResult(error);
                            break;
                    }
                })
            .ConfigureAwait(false);

        _ = await hub.RunAsync(
                agentId,
                new AgentSendOptions
                {
                    Input = AgentInput.Text("Could you tell me a bit more about the project `C:\\code\\Tomlyn`?")
                })
            .ConfigureAwait(false);

        var timeoutTask = Task.Delay(TimeSpan.FromMinutes(2));
        var completedTask = await Task.WhenAny(
                Task.WhenAll(permissionRequestSeen.Task, toolActivitySeen.Task, idleSeen.Task),
                errorEvent.Task,
                timeoutTask)
            .ConfigureAwait(false);

        if (completedTask == errorEvent.Task)
        {
            var error = await errorEvent.Task.ConfigureAwait(false);
            Assert.Fail($"Copilot emitted an error event: {error.Message}");
        }

        if (completedTask == timeoutTask)
        {
            Assert.Fail(
                $"Timed out waiting for approval/tool/idle events. Received: {string.Join(", ", receivedEvents.Select(static e => e.GetType().Name))}");
        }

        string[] messages;
        lock (assistantMessages)
        {
            messages = assistantMessages.ToArray();
        }

        Assert.IsTrue(messages.Length > 0, "Expected at least one assistant message.");
        Assert.IsTrue(
            messages.Any(static message => message.Contains("Tomlyn", StringComparison.OrdinalIgnoreCase)),
            $"Expected a final assistant summary mentioning Tomlyn. Messages: {string.Join(" || ", messages)}");
    }

    [TestMethod]
    [TestCategory("LiveCopilot")]
    public async Task CopilotChatConnection_LiveProjectPrompt_DeniedApprovalStillSurfacesPermissionRequest()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(LiveCopilotTestsEnvironmentVariable), "1", StringComparison.Ordinal))
        {
            Assert.Inconclusive(
                $"Set {LiveCopilotTestsEnvironmentVariable}=1 to run live Copilot integration tests.");
        }

        const string projectPath = @"C:\code\Tomlyn";
        if (!Directory.Exists(projectPath))
        {
            Assert.Inconclusive($"The live project path '{projectPath}' does not exist on this machine.");
        }

        using var temp = TempDirectory.Create();
        var db = await CreateDbAsync(temp.Path).ConfigureAwait(false);
        var repository = new AgentRepository(db);
        var backendFactory = new AgentBackendFactory();
        backendFactory.RegisterCopilot(new CopilotAgentBackendOptions());

        await using var hub = new AgentHub(backendFactory, repository);
        var receivedEvents = new List<AgentEvent>();
        await using var connection = new ChatAgentConnection(hub, receivedEvents.Add);

        AgentId agentId;
        try
        {
            agentId = await connection.EnsureConnectedAsync(
                    AgentBackendIds.Copilot,
                    projectPath,
                    model: "gpt-5-mini",
                    reasoningEffort: null,
                    tools: null,
                    permissionRequestHandler: static (_, _) =>
                        Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.Deny)),
                    userInputRequestHandler: null)
                .ConfigureAwait(false);
        }
        catch (FileNotFoundException ex)
        {
            Assert.Inconclusive($"Copilot executable was not found: {ex.Message}");
            return;
        }

        var permissionRequestSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var idleSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errorEvent = new TaskCompletionSource<AgentErrorEvent>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var subscription = await hub.SubscribeSessionEventsAsync(
                agentId,
                @event =>
                {
                    switch (@event)
                    {
                        case AgentPermissionRequest:
                            permissionRequestSeen.TrySetResult();
                            break;
                        case AgentSessionUpdateEvent { Kind: AgentSessionUpdateKind.Idle }:
                            idleSeen.TrySetResult();
                            break;
                        case AgentErrorEvent error:
                            errorEvent.TrySetResult(error);
                            break;
                    }
                })
            .ConfigureAwait(false);

        _ = await hub.RunAsync(
                agentId,
                new AgentSendOptions
                {
                    Input = AgentInput.Text("Could you tell me a bit more about the project `C:\\code\\Tomlyn`?")
                })
            .ConfigureAwait(false);

        var timeoutTask = Task.Delay(TimeSpan.FromMinutes(2));
        var completedTask = await Task.WhenAny(
                Task.WhenAll(permissionRequestSeen.Task, idleSeen.Task),
                errorEvent.Task,
                timeoutTask)
            .ConfigureAwait(false);

        if (completedTask == errorEvent.Task)
        {
            var error = await errorEvent.Task.ConfigureAwait(false);
            Assert.Fail($"Copilot emitted an error event: {error.Message}");
        }

        if (completedTask == timeoutTask)
        {
            Assert.Fail(
                $"Timed out waiting for permission request and idle. Received: {string.Join(", ", receivedEvents.Select(static e => e.GetType().Name))}");
        }
    }

    [TestMethod]
    [TestCategory("LiveCopilot")]
    public async Task CopilotChatConnection_LiveProjectPrompt_WithMcpToolBridge_AutoAnswersUserInputAndCompletes()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(LiveCopilotTestsEnvironmentVariable), "1", StringComparison.Ordinal))
        {
            Assert.Inconclusive(
                $"Set {LiveCopilotTestsEnvironmentVariable}=1 to run live Copilot integration tests.");
        }

        const string projectPath = @"C:\code\Tomlyn";
        if (!Directory.Exists(projectPath))
        {
            Assert.Inconclusive($"The live project path '{projectPath}' does not exist on this machine.");
        }

        using var temp = TempDirectory.Create();
        await using var toolHarness = await ToolBridgeHarness.CreateAsync(temp.Path).ConfigureAwait(false);
        var db = await CreateDbAsync(temp.Path).ConfigureAwait(false);
        var repository = new AgentRepository(db);
        var backendFactory = new AgentBackendFactory();
        backendFactory.RegisterCopilot(new CopilotAgentBackendOptions());

        await using var hub = new AgentHub(backendFactory, repository);
        var receivedEvents = new List<AgentEvent>();
        await using var connection = new ChatAgentConnection(hub, receivedEvents.Add);

        AgentId agentId;
        try
        {
            var tools = await toolHarness.Bridge.GetToolsAsync().ConfigureAwait(false);
            agentId = await connection.EnsureConnectedAsync(
                    AgentBackendIds.Copilot,
                    projectPath,
                    model: "gpt-5-mini",
                    reasoningEffort: null,
                    tools,
                    permissionRequestHandler: static (_, _) =>
                        Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
                    userInputRequestHandler: static (request, _) =>
                        Task.FromResult(CodeAltaTerminalUi.CreateChatUserInputResponse(request, autoApprove: true)))
                .ConfigureAwait(false);
        }
        catch (FileNotFoundException ex)
        {
            Assert.Inconclusive($"Copilot executable was not found: {ex.Message}");
            return;
        }

        var assistantMessages = new List<string>();
        var idleSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errorEvent = new TaskCompletionSource<AgentErrorEvent>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var subscription = await hub.SubscribeSessionEventsAsync(
                agentId,
                @event =>
                {
                    switch (@event)
                    {
                        case AgentContentCompletedEvent message when
                            message.Kind == AgentContentKind.Assistant &&
                            !string.IsNullOrWhiteSpace(message.Content):
                            lock (assistantMessages)
                            {
                                assistantMessages.Add(message.Content);
                            }
                            break;
                        case AgentSessionUpdateEvent { Kind: AgentSessionUpdateKind.Idle }:
                            idleSeen.TrySetResult();
                            break;
                        case AgentErrorEvent error:
                            errorEvent.TrySetResult(error);
                            break;
                    }
                })
            .ConfigureAwait(false);

        _ = await hub.RunAsync(
                agentId,
                new AgentSendOptions
                {
                    Input = AgentInput.Text("Could you tell me a bit more about the project `C:\\code\\Tomlyn`?")
                })
            .ConfigureAwait(false);

        var timeoutTask = Task.Delay(TimeSpan.FromMinutes(2));
        var completedTask = await Task.WhenAny(
                Task.WhenAll(idleSeen.Task),
                errorEvent.Task,
                timeoutTask)
            .ConfigureAwait(false);

        if (completedTask == errorEvent.Task)
        {
            var error = await errorEvent.Task.ConfigureAwait(false);
            Assert.Fail($"Copilot emitted an error event: {error.Message}");
        }

        if (completedTask == timeoutTask)
        {
            Assert.Fail(
                $"Timed out waiting for assistant content and idle with MCP tools. Received: {string.Join(", ", receivedEvents.Select(static e => e.GetType().Name))}");
        }

        string[] messages;
        lock (assistantMessages)
        {
            messages = assistantMessages.ToArray();
        }

        Assert.IsTrue(
            messages.Any(static message => message.Contains("Tomlyn", StringComparison.OrdinalIgnoreCase)),
            $"Expected an assistant summary mentioning Tomlyn. Messages: {string.Join(" || ", messages)}. Events: {string.Join(", ", receivedEvents.Select(static e => e.GetType().Name))}");
    }

    [TestMethod]
    [TestCategory("LiveCopilot")]
    public async Task CopilotChatConnection_LiveProjectPrompt_WithUiWorkingDirectory_AutoApproveAvoidsToolRejection()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(LiveCopilotTestsEnvironmentVariable), "1", StringComparison.Ordinal))
        {
            Assert.Inconclusive(
                $"Set {LiveCopilotTestsEnvironmentVariable}=1 to run live Copilot integration tests.");
        }

        const string projectPath = @"C:\code\Tomlyn";
        const string uiWorkingDirectory = @"C:\code\CodeAlta\src\CodeAlta";
        if (!Directory.Exists(projectPath) || !Directory.Exists(uiWorkingDirectory))
        {
            Assert.Inconclusive($"Required directories are missing: '{projectPath}' or '{uiWorkingDirectory}'.");
        }

        using var temp = TempDirectory.Create();
        await using var toolHarness = await ToolBridgeHarness.CreateAsync(temp.Path).ConfigureAwait(false);
        var db = await CreateDbAsync(temp.Path).ConfigureAwait(false);
        var repository = new AgentRepository(db);
        var backendFactory = new AgentBackendFactory();
        backendFactory.RegisterCopilot(new CopilotAgentBackendOptions());

        await using var hub = new AgentHub(backendFactory, repository);
        var receivedEvents = new List<AgentEvent>();
        await using var connection = new ChatAgentConnection(hub, receivedEvents.Add);

        AgentId agentId;
        try
        {
            var tools = await toolHarness.Bridge.GetToolsAsync().ConfigureAwait(false);
            agentId = await connection.EnsureConnectedAsync(
                    AgentBackendIds.Copilot,
                    uiWorkingDirectory,
                    model: "gpt-5.1-codex-mini",
                    reasoningEffort: null,
                    tools,
                    permissionRequestHandler: static (_, _) =>
                        Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
                    userInputRequestHandler: static (request, _) =>
                        Task.FromResult(CodeAltaTerminalUi.CreateChatUserInputResponse(request, autoApprove: true)))
                .ConfigureAwait(false);
        }
        catch (FileNotFoundException ex)
        {
            Assert.Inconclusive($"Copilot executable was not found: {ex.Message}");
            return;
        }

        var assistantMessages = new List<string>();
        var userInputRequests = new List<AgentUserInputRequest>();
        var idleSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errorEvent = new TaskCompletionSource<AgentErrorEvent>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var subscription = await hub.SubscribeSessionEventsAsync(
                agentId,
                @event =>
                {
                    switch (@event)
                    {
                        case AgentUserInputRequest request:
                            lock (userInputRequests)
                            {
                                userInputRequests.Add(request);
                            }

                            break;
                        case AgentContentCompletedEvent content when
                            content.Kind == AgentContentKind.Assistant &&
                            !string.IsNullOrWhiteSpace(content.Content):
                            lock (assistantMessages)
                            {
                                assistantMessages.Add(content.Content);
                            }

                            break;
                        case AgentSessionUpdateEvent { Kind: AgentSessionUpdateKind.Idle }:
                            idleSeen.TrySetResult();
                            break;
                        case AgentErrorEvent error:
                            errorEvent.TrySetResult(error);
                            break;
                    }
                })
            .ConfigureAwait(false);

        _ = await hub.RunAsync(
                agentId,
                new AgentSendOptions
                {
                    Input = AgentInput.Text("Could you tell me a bit more about the project `C:\\code\\Tomlyn`?")
                })
            .ConfigureAwait(false);

        var timeoutTask = Task.Delay(TimeSpan.FromMinutes(2));
        var completedTask = await Task.WhenAny(idleSeen.Task, errorEvent.Task, timeoutTask).ConfigureAwait(false);

        if (completedTask == timeoutTask)
        {
            Assert.Fail($"Timed out. Events: {string.Join(", ", receivedEvents.Select(static e => e.GetType().Name))}");
        }

        if (completedTask == errorEvent.Task)
        {
            var error = await errorEvent.Task.ConfigureAwait(false);
            Assert.Fail($"Copilot emitted an error event: {error.Message}");
        }

        AgentUserInputRequest[] requests;
        string[] messages;
        lock (userInputRequests)
        {
            requests = userInputRequests.ToArray();
        }

        lock (assistantMessages)
        {
            messages = assistantMessages.ToArray();
        }

        if (requests.Length > 0)
        {
            Assert.IsTrue(
                requests.All(static request => request.Form.Prompts.Count > 0),
                "Expected any ask_user requests to carry at least one prompt.");
        }

        Assert.IsFalse(
            messages.Any(static message => message.Contains("rejected this tool call", StringComparison.OrdinalIgnoreCase)),
            $"Did not expect a rejected-tool assistant message. Messages: {string.Join(" || ", messages)}");
        Assert.IsTrue(
            messages.Any(static message => message.Contains("Tomlyn", StringComparison.OrdinalIgnoreCase) ||
                                           message.Contains(@"C:\code", StringComparison.OrdinalIgnoreCase)),
            $"Expected assistant follow-up after auto-answering ask_user. Messages: {string.Join(" || ", messages)}");
    }

    private static async Task<CodeAltaDb> CreateDbAsync(string rootPath)
    {
        var dbPath = Path.Combine(rootPath, "state", "db", "codealta.db");
        var db = new CodeAltaDb(new CodeAltaDbOptions { DatabasePath = dbPath });
        await db.InitializeAsync().ConfigureAwait(false);
        return db;
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

    private sealed class ToolBridgeHarness : IAsyncDisposable
    {
        private readonly ServiceProvider _services;

        private ToolBridgeHarness(ServiceProvider services, McpToolBridge bridge)
        {
            _services = services;
            Bridge = bridge;
        }

        public McpToolBridge Bridge { get; }

        public static async Task<ToolBridgeHarness> CreateAsync(string rootPath)
        {
            var db = new CodeAltaDb(new CodeAltaDbOptions
            {
                DatabasePath = Path.Combine(rootPath, "state", "db", "codealta.db"),
            });
            await db.InitializeAsync().ConfigureAwait(false);

            var taskRepository = new TaskRepository(db);
            var artifactStore = new ArtifactStore();
            var artifactRepository = new ArtifactRepository(db);
            var agentRepository = new AgentRepository(db);
            var documentIndexStore = new DocumentIndexStore(db);
            var embeddingManager = new EmbeddingModelManager(new HashEmbedder());
            var indexingQueue = new IndexingQueue();
            var indexer = new Indexer(indexingQueue, documentIndexStore, embeddingManager);
            var searchService = new SearchService(documentIndexStore, embeddingManager);

            var workspaceCatalog = new WorkspaceCatalog(
                new WorkspaceCatalogOptions
                {
                    GlobalRepoRoot = Path.Combine(rootPath, "repo"),
                });
            var workspaceResolver = new WorkspaceResolver(workspaceCatalog);

            var dotNetWorkspaceService = new DotNetWorkspaceService();
            var symbolIndexService = new SymbolIndexService();
            var dotNetContextProvider = new DotNetContextProvider(dotNetWorkspaceService, symbolIndexService);
            var dotNetOptions = new DotNetOptions
            {
                ArtifactRoot = string.Empty,
            };
            var dotNetIndexService = new DotNetIndexService(
                dotNetWorkspaceService,
                symbolIndexService,
                artifactStore,
                artifactRepository,
                indexer,
                dotNetOptions);
            var dotNetDiagnosticsService = new DotNetDiagnosticsService(
                artifactStore,
                artifactRepository,
                indexer,
                dotNetOptions);

            var serviceCollection = new ServiceCollection();
            serviceCollection.AddSingleton(taskRepository);
            serviceCollection.AddSingleton(artifactStore);
            serviceCollection.AddSingleton(artifactRepository);
            serviceCollection.AddSingleton(agentRepository);
            serviceCollection.AddSingleton(indexer);
            serviceCollection.AddSingleton(searchService);
            serviceCollection.AddSingleton(workspaceCatalog);
            serviceCollection.AddSingleton(workspaceResolver);
            serviceCollection.AddSingleton(new RoleProfileStore());
            serviceCollection.AddSingleton(new SkillCatalog());
            serviceCollection.AddSingleton(new GitService());
            serviceCollection.AddSingleton(sp => new GlobalRepoBootstrapper(sp.GetRequiredService<GitService>()));
            serviceCollection.AddSingleton(sp => new GlobalRepoSyncService(sp.GetRequiredService<GitService>()));
            serviceCollection.AddSingleton(new WorkspaceBootstrapPlanner());
            serviceCollection.AddSingleton(sp =>
                new WorkspaceBootstrapper(
                    sp.GetRequiredService<WorkspaceBootstrapPlanner>(),
                    sp.GetRequiredService<GitService>()));
            serviceCollection.AddSingleton(dotNetWorkspaceService);
            serviceCollection.AddSingleton(symbolIndexService);
            serviceCollection.AddSingleton(dotNetContextProvider);
            serviceCollection.AddSingleton(dotNetIndexService);
            serviceCollection.AddSingleton(dotNetDiagnosticsService);
            serviceCollection.AddSingleton(new CodeAltaMcpOptions
            {
                ServerName = "CodeAlta",
                ServerVersion = "0.1.0-tests",
                ArtifactRoot = Path.Combine(rootPath, "artifacts"),
            });
            serviceCollection.AddSingleton(new McpSessionRegistry());

            var services = serviceCollection.BuildServiceProvider();
            var factory = new CodeAltaMcpServerFactory(
                services,
                services.GetRequiredService<McpSessionRegistry>(),
                services.GetRequiredService<CodeAltaMcpOptions>());
            var bridge = new McpToolBridge(factory);
            return new ToolBridgeHarness(services, bridge);
        }

        public async ValueTask DisposeAsync()
        {
            await Bridge.DisposeAsync().ConfigureAwait(false);
            await _services.DisposeAsync().ConfigureAwait(false);
        }
    }
}
