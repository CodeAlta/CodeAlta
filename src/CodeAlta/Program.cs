using CodeAlta.DotNet;
using CodeAlta.Mcp;
using CodeAlta.Orchestration;
using CodeAlta.Persistence;
using CodeAlta.Search;
using CodeAlta.Workspaces;
using CodeAlta.Workspaces.Bootstrap;
using CodeAlta.Workspaces.Roles;
using CodeAlta.Workspaces.Skills;
using Microsoft.Extensions.DependencyInjection;

var cancellationTokenSource = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellationTokenSource.Cancel();
};

await using var app = await TerminalHost.CreateAsync(cancellationTokenSource.Token).ConfigureAwait(false);
await app.RunAsync(cancellationTokenSource.Token).ConfigureAwait(false);

internal sealed class TerminalHost : IAsyncDisposable
{
    private readonly IServiceProvider _mcpServices;
    private readonly CodeAltaMcpServerFactory _mcpFactory;
    private readonly WorkspaceCatalog _workspaceCatalog;
    private readonly WorkspaceResolver _workspaceResolver;
    private readonly TaskRepository _taskRepository;
    private readonly SearchService _searchService;
    private readonly DotNetIndexService _dotNetIndexService;
    private readonly DotNetDiagnosticsService _dotNetDiagnosticsService;
    private readonly Indexer _indexer;

    private TerminalHost(
        IServiceProvider mcpServices,
        CodeAltaMcpServerFactory mcpFactory,
        WorkspaceCatalog workspaceCatalog,
        WorkspaceResolver workspaceResolver,
        TaskRepository taskRepository,
        SearchService searchService,
        DotNetIndexService dotNetIndexService,
        DotNetDiagnosticsService dotNetDiagnosticsService,
        Indexer indexer)
    {
        _mcpServices = mcpServices;
        _mcpFactory = mcpFactory;
        _workspaceCatalog = workspaceCatalog;
        _workspaceResolver = workspaceResolver;
        _taskRepository = taskRepository;
        _searchService = searchService;
        _dotNetIndexService = dotNetIndexService;
        _dotNetDiagnosticsService = dotNetDiagnosticsService;
        _indexer = indexer;
    }

    public static async Task<TerminalHost> CreateAsync(CancellationToken cancellationToken)
    {
        var homeRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codealta");
        Directory.CreateDirectory(homeRoot);

        var db = new CodeAltaDb(
            new CodeAltaDbOptions
            {
                DatabasePath = Path.Combine(homeRoot, "state", "db", "codealta.db"),
            });
        await db.InitializeAsync(cancellationToken).ConfigureAwait(false);

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
                GlobalRepoRoot = Path.Combine(homeRoot, "repo"),
            });
        var workspaceResolver = new WorkspaceResolver(workspaceCatalog);

        var dotNetWorkspaceService = new DotNetWorkspaceService();
        var symbolIndexService = new SymbolIndexService();
        var dotNetContextProvider = new DotNetContextProvider(dotNetWorkspaceService, symbolIndexService);
        var dotNetOptions = new DotNetOptions
        {
            ArtifactRoot = Path.Combine(homeRoot, "knowledge", "dotnet"),
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

        var mcpOptions = new CodeAltaMcpOptions
        {
            ServerName = "CodeAlta",
            ServerVersion = "0.1.0-preview",
            ArtifactRoot = Path.Combine(homeRoot, "artifacts"),
        };

        var mcpServiceCollection = new ServiceCollection();
        mcpServiceCollection.AddSingleton(taskRepository);
        mcpServiceCollection.AddSingleton(artifactStore);
        mcpServiceCollection.AddSingleton(artifactRepository);
        mcpServiceCollection.AddSingleton(agentRepository);
        mcpServiceCollection.AddSingleton(indexer);
        mcpServiceCollection.AddSingleton(searchService);
        mcpServiceCollection.AddSingleton(workspaceCatalog);
        mcpServiceCollection.AddSingleton(workspaceResolver);
        mcpServiceCollection.AddSingleton(new RoleProfileStore());
        mcpServiceCollection.AddSingleton(new SkillCatalog());
        mcpServiceCollection.AddSingleton(new GitService());
        mcpServiceCollection.AddSingleton(sp => new GlobalRepoBootstrapper(sp.GetRequiredService<GitService>()));
        mcpServiceCollection.AddSingleton(sp => new GlobalRepoSyncService(sp.GetRequiredService<GitService>()));
        mcpServiceCollection.AddSingleton(new WorkspaceBootstrapPlanner());
        mcpServiceCollection.AddSingleton(sp =>
            new WorkspaceBootstrapper(
                sp.GetRequiredService<WorkspaceBootstrapPlanner>(),
                sp.GetRequiredService<GitService>()));
        mcpServiceCollection.AddSingleton(dotNetWorkspaceService);
        mcpServiceCollection.AddSingleton(symbolIndexService);
        mcpServiceCollection.AddSingleton(dotNetContextProvider);
        mcpServiceCollection.AddSingleton(dotNetIndexService);
        mcpServiceCollection.AddSingleton(dotNetDiagnosticsService);
        mcpServiceCollection.AddSingleton(mcpOptions);
        mcpServiceCollection.AddSingleton(new McpSessionRegistry());
        var mcpServices = mcpServiceCollection.BuildServiceProvider();

        var mcpFactory = new CodeAltaMcpServerFactory(
            mcpServices,
            mcpServices.GetRequiredService<McpSessionRegistry>(),
            mcpServices.GetRequiredService<CodeAltaMcpOptions>());

        return new TerminalHost(
            mcpServices,
            mcpFactory,
            workspaceCatalog,
            workspaceResolver,
            taskRepository,
            searchService,
            dotNetIndexService,
            dotNetDiagnosticsService,
            indexer);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            RenderMenu();
            var choice = Console.ReadLine()?.Trim();
            if (string.IsNullOrWhiteSpace(choice))
            {
                continue;
            }

            if (choice is "0" or "q" or "Q" or "exit")
            {
                return;
            }

            try
            {
                switch (choice)
                {
                    case "1":
                        await ListWorkspacesAsync(cancellationToken).ConfigureAwait(false);
                        break;
                    case "2":
                        await ResolveScopeAsync(cancellationToken).ConfigureAwait(false);
                        break;
                    case "3":
                        await ListTasksAsync(cancellationToken).ConfigureAwait(false);
                        break;
                    case "4":
                        await CreateTaskAsync(cancellationToken).ConfigureAwait(false);
                        break;
                    case "5":
                        await SearchAsync(cancellationToken).ConfigureAwait(false);
                        break;
                    case "6":
                        await RefreshDotNetIndexAsync(cancellationToken).ConfigureAwait(false);
                        break;
                    case "7":
                        await RunDotNetDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
                        break;
                    case "8":
                        await McpHealthCheckAsync(cancellationToken).ConfigureAwait(false);
                        break;
                    default:
                        Console.WriteLine("Unknown option.");
                        break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.WriteLine($"Operation failed: {ex.Message}");
            }

            Console.WriteLine();
            Console.WriteLine("Press Enter to continue...");
            Console.ReadLine();
        }
    }

    public ValueTask DisposeAsync()
    {
        switch (_mcpServices)
        {
            case IAsyncDisposable asyncDisposable:
                return asyncDisposable.DisposeAsync();
            case IDisposable disposable:
                disposable.Dispose();
                return ValueTask.CompletedTask;
            default:
                return ValueTask.CompletedTask;
        }
    }

    private void RenderMenu()
    {
        Console.Clear();
        Console.WriteLine("CodeAlta Terminal Host");
        Console.WriteLine("=====================");
        Console.WriteLine($"Index Queue Depth: {_indexer.Status.QueueDepth}");
        Console.WriteLine();
        Console.WriteLine("1. List Workspaces");
        Console.WriteLine("2. Resolve Scope");
        Console.WriteLine("3. List Tasks");
        Console.WriteLine("4. Create Task");
        Console.WriteLine("5. Search Query");
        Console.WriteLine("6. Refresh .NET Index");
        Console.WriteLine("7. Run .NET Diagnostics");
        Console.WriteLine("8. MCP Health Check");
        Console.WriteLine("0. Exit");
        Console.WriteLine();
        Console.Write("Select: ");
    }

    private async Task ListWorkspacesAsync(CancellationToken cancellationToken)
    {
        var workspaces = await _workspaceCatalog.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (workspaces.Count == 0)
        {
            Console.WriteLine("No workspaces were discovered.");
            return;
        }

        foreach (var workspace in workspaces)
        {
            Console.WriteLine($"- {workspace.Key} ({workspace.DisplayName}) projects={workspace.Projects.Count}");
        }
    }

    private async Task ResolveScopeAsync(CancellationToken cancellationToken)
    {
        Console.Write("Scope kind (global|workspace|project): ");
        var kind = Console.ReadLine()?.Trim()?.ToLowerInvariant();
        var selector = kind switch
        {
            "global" => ScopeSelector.Global(),
            "workspace" => ScopeSelector.Workspace(ReadRequired("Workspace key")),
            "project" => ScopeSelector.Project(ReadRequired("Project key")),
            _ => throw new ArgumentException("Invalid scope kind."),
        };

        var resolutions = await _workspaceResolver.ResolveAsync(selector, cancellationToken: cancellationToken).ConfigureAwait(false);
        foreach (var resolution in resolutions)
        {
            Console.WriteLine($"Workspace: {resolution.Workspace.Key}");
            foreach (var project in resolution.Projects)
            {
                Console.WriteLine($"  - {project.Project.Key} => {project.CheckoutPath}");
            }
        }
    }

    private async Task ListTasksAsync(CancellationToken cancellationToken)
    {
        var tasks = await _taskRepository.ListAsync(limit: 20, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (tasks.Count == 0)
        {
            Console.WriteLine("No tasks.");
            return;
        }

        foreach (var task in tasks)
        {
            Console.WriteLine($"- {task.TaskId} [{task.Status}] {task.Title}");
        }
    }

    private async Task CreateTaskAsync(CancellationToken cancellationToken)
    {
        var title = ReadRequired("Task title");
        var workspaceId = ReadOptional("Workspace id (optional)");
        var projectId = ReadOptional("Project id (optional)");

        var task = await _taskRepository.CreateAsync(
            new CreateTaskRequest
            {
                Title = title,
                WorkspaceId = workspaceId,
                ProjectId = projectId,
            },
            cancellationToken).ConfigureAwait(false);

        Console.WriteLine($"Created task: {task.TaskId}");
    }

    private async Task SearchAsync(CancellationToken cancellationToken)
    {
        var queryText = ReadRequired("Search text");
        var results = await _searchService.QueryHybridAsync(
            new SearchQuery
            {
                Text = queryText,
                Limit = 10,
                PrefilterLimit = 20,
            },
            cancellationToken).ConfigureAwait(false);

        if (results.Count == 0)
        {
            Console.WriteLine("No results.");
            return;
        }

        foreach (var result in results)
        {
            Console.WriteLine($"- {result.Title ?? result.SourceId}");
            Console.WriteLine($"  {result.LinkUri}");
            if (!string.IsNullOrWhiteSpace(result.Snippet))
            {
                Console.WriteLine($"  {result.Snippet}");
            }
        }
    }

    private async Task RefreshDotNetIndexAsync(CancellationToken cancellationToken)
    {
        var repoPath = ReadRequired("Repository path");
        var refresh = await _dotNetIndexService.RefreshIndexAsync(
            repoPath,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        Console.WriteLine(
            $"Index refreshed. symbols={refresh.SymbolCount} indexedDocuments={refresh.IndexedDocumentCount} graphArtifact={refresh.ProjectGraphArtifactId}");
    }

    private async Task RunDotNetDiagnosticsAsync(CancellationToken cancellationToken)
    {
        var targetPath = ReadRequired("Path to repo/solution/project");
        var result = await _dotNetDiagnosticsService.RunBuildAsync(targetPath, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        Console.WriteLine($"Diagnostics complete. success={result.Success} exitCode={result.ExitCode}");
        Console.WriteLine($"Artifact: {result.ArtifactPath}");
    }

    private async Task McpHealthCheckAsync(CancellationToken cancellationToken)
    {
        await using var connection = await InProcessMcpConnection.CreateAsync(_mcpFactory, cancellationToken)
            .ConfigureAwait(false);
        var tools = await connection.Client.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"MCP tools available: {tools.Count}");
        foreach (var tool in tools.Take(10))
        {
            Console.WriteLine($"- {tool.Name}");
        }
    }

    private static string ReadRequired(string label)
    {
        while (true)
        {
            Console.Write($"{label}: ");
            var value = Console.ReadLine()?.Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }
    }

    private static string? ReadOptional(string label)
    {
        Console.Write($"{label}: ");
        var value = Console.ReadLine()?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
