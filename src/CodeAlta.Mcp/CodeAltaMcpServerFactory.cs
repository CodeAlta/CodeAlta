using CodeAlta.DotNet;
using CodeAlta.Mcp.Tools;
using CodeAlta.Persistence;
using CodeAlta.Search;
using CodeAlta.Workspaces;
using CodeAlta.Workspaces.Bootstrap;
using CodeAlta.Workspaces.Roles;
using CodeAlta.Workspaces.Skills;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CodeAlta.Mcp;

/// <summary>
/// Creates configured in-process MCP server instances.
/// </summary>
public sealed class CodeAltaMcpServerFactory
{
    private readonly IServiceProvider _rootServices;
    private readonly McpSessionRegistry _sessionRegistry;
    private readonly CodeAltaMcpOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="CodeAltaMcpServerFactory"/> class.
    /// </summary>
    /// <param name="rootServices">Root service provider containing CodeAlta services.</param>
    /// <param name="sessionRegistry">Session registry.</param>
    /// <param name="options">MCP options.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="rootServices"/>, <paramref name="sessionRegistry"/>, or <paramref name="options"/> is <see langword="null"/>.
    /// </exception>
    public CodeAltaMcpServerFactory(
        IServiceProvider rootServices,
        McpSessionRegistry sessionRegistry,
        CodeAltaMcpOptions options)
    {
        ArgumentNullException.ThrowIfNull(rootServices);
        ArgumentNullException.ThrowIfNull(sessionRegistry);
        ArgumentNullException.ThrowIfNull(options);

        _rootServices = rootServices;
        _sessionRegistry = sessionRegistry;
        _options = options;
    }

    /// <summary>
    /// Creates a new MCP server session bound to the provided streams.
    /// </summary>
    /// <param name="input">Server input stream (client-to-server).</param>
    /// <param name="output">Server output stream (server-to-client).</param>
    /// <returns>The created MCP server session.</returns>
    /// <exception cref="ArgumentNullException">Thrown when required streams are <see langword="null"/>.</exception>
    public CodeAltaMcpServerSession Create(Stream input, Stream output)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        var sessionId = _sessionRegistry.Register();
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddSingleton(_options);
        serviceCollection.AddSingleton(_sessionRegistry);

        AddForwardedService<TaskRepository>(serviceCollection);
        AddForwardedService<ArtifactStore>(serviceCollection);
        AddForwardedService<ArtifactRepository>(serviceCollection);
        AddForwardedService<AgentRepository>(serviceCollection);
        AddForwardedService<Indexer>(serviceCollection);
        AddForwardedService<SearchService>(serviceCollection);
        AddForwardedService<WorkspaceCatalog>(serviceCollection);
        AddForwardedService<WorkspaceResolver>(serviceCollection);
        AddForwardedService<RoleProfileStore>(serviceCollection);
        AddForwardedService<SkillCatalog>(serviceCollection);
        AddForwardedService<GitService>(serviceCollection);
        AddForwardedService<GlobalRepoBootstrapper>(serviceCollection);
        AddForwardedService<GlobalRepoSyncService>(serviceCollection);
        AddForwardedService<WorkspaceBootstrapPlanner>(serviceCollection);
        AddForwardedService<WorkspaceBootstrapper>(serviceCollection);
        AddForwardedService<DotNetWorkspaceService>(serviceCollection);
        AddForwardedService<SymbolIndexService>(serviceCollection);
        AddForwardedService<DotNetContextProvider>(serviceCollection);
        AddForwardedService<DotNetIndexService>(serviceCollection);
        AddForwardedService<DotNetDiagnosticsService>(serviceCollection);

        serviceCollection.AddSingleton<TasksTools>();
        serviceCollection.AddSingleton<ArtifactsTools>();
        serviceCollection.AddSingleton<SearchTools>();
        serviceCollection.AddSingleton<WorkspacesTools>();
        serviceCollection.AddSingleton<AgentsTools>();
        serviceCollection.AddSingleton<RolesTools>();
        serviceCollection.AddSingleton<SkillsTools>();
        serviceCollection.AddSingleton<BootstrapTools>();
        serviceCollection.AddSingleton<DotNetTools>();

        serviceCollection
            .AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation
                {
                    Name = _options.ServerName,
                    Version = _options.ServerVersion,
                };
            })
            .WithStreamServerTransport(input, output)
            .WithTools(
            [
                typeof(TasksTools),
                typeof(ArtifactsTools),
                typeof(SearchTools),
                typeof(WorkspacesTools),
                typeof(AgentsTools),
                typeof(RolesTools),
                typeof(SkillsTools),
                typeof(BootstrapTools),
                typeof(DotNetTools),
            ]);

        var scopedServices = serviceCollection.BuildServiceProvider();
        var server = scopedServices.GetRequiredService<McpServer>();
        return new CodeAltaMcpServerSession(
            sessionId,
            server,
            scopedServices,
            _sessionRegistry);
    }

    private void AddForwardedService<T>(IServiceCollection services)
        where T : class
    {
        services.AddSingleton(_rootServices.GetRequiredService<T>());
    }
}

/// <summary>
/// Represents a created MCP server instance and its scoped service provider.
/// </summary>
public sealed class CodeAltaMcpServerSession : IAsyncDisposable
{
    private readonly IServiceProvider _services;
    private readonly McpSessionRegistry _sessionRegistry;
    private bool _disposed;

    internal CodeAltaMcpServerSession(
        Guid sessionId,
        McpServer server,
        IServiceProvider services,
        McpSessionRegistry sessionRegistry)
    {
        SessionId = sessionId;
        Server = server;
        _services = services;
        _sessionRegistry = sessionRegistry;
    }

    /// <summary>
    /// Gets the logical MCP session identifier.
    /// </summary>
    public Guid SessionId { get; }

    /// <summary>
    /// Gets the underlying MCP server.
    /// </summary>
    public McpServer Server { get; }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _sessionRegistry.Unregister(SessionId);

        await Server.DisposeAsync().ConfigureAwait(false);

        switch (_services)
        {
            case IAsyncDisposable asyncDisposable:
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                break;

            case IDisposable disposable:
                disposable.Dispose();
                break;
        }
    }
}
