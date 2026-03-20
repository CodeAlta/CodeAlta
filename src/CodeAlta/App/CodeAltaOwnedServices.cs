using CodeAlta.Agent;
using CodeAlta.Agent.Codex;
using CodeAlta.Agent.Copilot;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Roles;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Persistence;
using XenoAtom.Logging;

namespace CodeAlta.App;

internal sealed class CodeAltaOwnedServices : IAsyncDisposable
{
    private readonly bool _ownsLogging;
    private readonly CodeAltaDb _db;

    private CodeAltaOwnedServices(
        bool ownsLogging,
        CodeAltaDb db,
        CatalogOptions catalogOptions,
        ProjectCatalog projectCatalog,
        WorkThreadCatalog threadCatalog,
        AgentHub agentHub,
        WorkThreadRuntimeService runtimeService)
    {
        _ownsLogging = ownsLogging;
        _db = db;
        CatalogOptions = catalogOptions;
        ProjectCatalog = projectCatalog;
        ThreadCatalog = threadCatalog;
        AgentHub = agentHub;
        RuntimeService = runtimeService;
    }

    public CatalogOptions CatalogOptions { get; }

    public ProjectCatalog ProjectCatalog { get; }

    public WorkThreadCatalog ThreadCatalog { get; }

    public AgentHub AgentHub { get; }

    public WorkThreadRuntimeService RuntimeService { get; }

    public static async Task<CodeAltaOwnedServices> CreateAsync(CancellationToken cancellationToken)
    {
        var homeRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codealta");
        Directory.CreateDirectory(homeRoot);
        var ownsLogging = CodeAltaLogging.Initialize(homeRoot);

        var machineRoot = Path.Combine(homeRoot, "machine");
        Directory.CreateDirectory(machineRoot);

        var db = new CodeAltaDb(
            new CodeAltaDbOptions
            {
                DatabasePath = Path.Combine(machineRoot, "codealta.db"),
            });
        await db.InitializeAsync(cancellationToken).ConfigureAwait(false);

        var agentRepository = new AgentRepository(db);
        var catalogOptions = new CatalogOptions
        {
            GlobalRoot = homeRoot,
        };
        var projectCatalog = new ProjectCatalog(catalogOptions);
        await projectCatalog.UpsertFromPathAsync(Environment.CurrentDirectory, cancellationToken).ConfigureAwait(false);

        var threadCatalog = new WorkThreadCatalog(catalogOptions);
        var roleProfileStore = new RoleProfileStore();
        var instructionTemplateProvider = new AgentInstructionTemplateProvider();

        var backendFactory = new AgentBackendFactory();
        backendFactory.RegisterCodex(new CodexAgentBackendOptions());
        backendFactory.RegisterCopilot(new CopilotAgentBackendOptions());

        var agentHub = new AgentHub(backendFactory, agentRepository);
        var runtimeService = new WorkThreadRuntimeService(
            agentHub,
            projectCatalog,
            threadCatalog,
            roleProfileStore,
            instructionTemplateProvider,
            catalogOptions);

        return new CodeAltaOwnedServices(
            ownsLogging,
            db,
            catalogOptions,
            projectCatalog,
            threadCatalog,
            agentHub,
            runtimeService);
    }

    public async ValueTask DisposeAsync()
    {
        await RuntimeService.DisposeAsync().ConfigureAwait(false);
        await AgentHub.DisposeAsync().ConfigureAwait(false);

        GC.KeepAlive(_db);

        if (_ownsLogging)
        {
            LogManager.Shutdown();
        }
    }
}