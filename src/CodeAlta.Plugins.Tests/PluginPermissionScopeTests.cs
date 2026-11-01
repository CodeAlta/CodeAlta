using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugins.Tests;

/// <summary>The permission requests a plugin gets: those the host gives it by its key, and none from a host that does not tell plugins apart.</summary>
[TestClass]
public sealed class PluginPermissionScopeTests
{
    [TestMethod]
    public async Task ABuiltIn_GetsWhatTheHostGivesItsKey()
    {
        var permissions = new ScopedPermissions();
        var plugin = await StartAsync(new HostServices(permissions));

        Assert.AreSame(permissions, plugin.Permissions, "the host gives this built-in its permission requests");
        Assert.AreEqual(("builtin:approver", true), permissions.AskedFor.Single());
    }

    [TestMethod]
    public async Task AHostThatDoesNotTellPluginsApart_GivesNoPermissionRequests()
    {
        var plugin = await StartAsync(new HostServices(new PlainPermissions()));

        Assert.AreSame(NoopPluginPermissionService.Instance, plugin.Permissions);
    }

    [TestMethod]
    public async Task ThePermissionRequests_AreNotPassedOnAsTheyAre_EvenFromAHostThatTellsPluginsApart()
    {
        var permissions = new RefusingPermissions();
        var plugin = await StartAsync(new HostServices(permissions));

        Assert.AreSame(NoopPluginPermissionService.Instance, plugin.Permissions, "what the host gives this key, not the service of the host");
    }

    private static async Task<ApproverPlugin> StartAsync(IPluginServices services)
    {
        var root = Directory.CreateTempSubdirectory("CodeAlta-plugin-permissions-").FullName;
        var plugin = new ApproverPlugin();
        var runtime = new PluginRuntimeManager();
        try
        {
            await runtime.StartAsync(new PluginRuntimeManagerOptions
            {
                GlobalRoot = root,
                Services = services,
                BuiltIns = [new BuiltInPluginDefinition { Id = "approver", DisplayName = "Approver", PluginType = typeof(ApproverPlugin), Factory = () => plugin }],
            });
            return plugin;
        }
        finally
        {
            await runtime.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>A built-in that reads its permission requests when it starts.</summary>
    public sealed class ApproverPlugin : PluginBase
    {
        public IPluginPermissionService? Permissions { get; private set; }

        public override ValueTask OnActivatedAsync(CancellationToken cancellationToken = default)
        {
            Permissions = Services.Permissions;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class HostServices(IPluginPermissionService permissions) : IPluginServices
    {
        private readonly IPluginServices _inner = NoopPluginServices.Create();

        public XenoAtom.Logging.Logger Logger => _inner.Logger;

        public IPluginUiService Ui => _inner.Ui;

        public IPluginStateStore State => _inner.State;

        public IPluginWorkspaceService Workspace => _inner.Workspace;

        public IPluginSessionService Sessions => _inner.Sessions;

        public IPluginPromptService Prompts => _inner.Prompts;

        public IPluginAgentService Agents => _inner.Agents;

        public IPluginTaskService Tasks => _inner.Tasks;

        public IPluginAltaService Alta => _inner.Alta;

        public IPluginPermissionService Permissions => permissions;
    }

    private class PlainPermissions : IPluginPermissionService
    {
        public bool HasPermissions => true;

        public ValueTask<IReadOnlyList<PluginPermissionSummary>> ListWaitingAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<PluginPermissionSummary>>([]);

        public ValueTask<PluginPermissionPage> ListAsync(string sessionId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new PluginPermissionPage([], false));

        public ValueTask<PluginPermissionOutcome> ResolveAsync(string sessionId, string requestId, PluginPermissionDecision decision, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new PluginPermissionOutcome("resolved"));
    }

    private sealed class ScopedPermissions : PlainPermissions, IPluginPermissionRuntimeService
    {
        internal List<(string Key, bool BuiltIn)> AskedFor { get; } = [];

        public IPluginPermissionService ForPlugin(string pluginRuntimeKey, bool builtIn)
        {
            AskedFor.Add((pluginRuntimeKey, builtIn));
            return this;
        }
    }

    private sealed class RefusingPermissions : PlainPermissions, IPluginPermissionRuntimeService
    {
        public IPluginPermissionService ForPlugin(string pluginRuntimeKey, bool builtIn) => NoopPluginPermissionService.Instance;
    }
}
