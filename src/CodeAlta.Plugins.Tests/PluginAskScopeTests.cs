using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugins.Tests;

/// <summary>The asks a plugin gets: those the host gives it by its key, and none from a host that does not tell plugins apart.</summary>
[TestClass]
public sealed class PluginAskScopeTests
{
    [TestMethod]
    public async Task ABuiltIn_GetsWhatTheHostGivesItsKey()
    {
        var asks = new ScopedAsks();
        var plugin = await StartAsync(new HostServices(asks));

        Assert.AreSame(asks, plugin.Asks, "the host gives this built-in its asks");
        Assert.AreEqual(("builtin:asker", true), asks.AskedFor.Single());
    }

    [TestMethod]
    public async Task AHostThatDoesNotTellPluginsApart_GivesNoAsks()
    {
        var plugin = await StartAsync(new HostServices(new PlainAsks()));

        Assert.AreSame(NoopPluginAskService.Instance, plugin.Asks);
    }

    private static async Task<AskerPlugin> StartAsync(IPluginServices services)
    {
        var root = Directory.CreateTempSubdirectory("CodeAlta-plugin-asks-").FullName;
        var plugin = new AskerPlugin();
        var runtime = new PluginRuntimeManager();
        try
        {
            await runtime.StartAsync(new PluginRuntimeManagerOptions
            {
                GlobalRoot = root,
                Services = services,
                BuiltIns = [new BuiltInPluginDefinition { Id = "asker", DisplayName = "Asker", PluginType = typeof(AskerPlugin), Factory = () => plugin }],
            });
            return plugin;
        }
        finally
        {
            await runtime.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>A built-in that reads its asks when it starts.</summary>
    public sealed class AskerPlugin : PluginBase
    {
        public IPluginAskService? Asks { get; private set; }

        public override ValueTask OnActivatedAsync(CancellationToken cancellationToken = default)
        {
            Asks = Services.Asks;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class HostServices(IPluginAskService asks) : IPluginServices
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

        public IPluginAskService Asks => asks;
    }

    private class PlainAsks : IPluginAskService
    {
        public bool HasAsks => true;

        public ValueTask<IReadOnlyList<PluginAskSummary>> ListAskingAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<PluginAskSummary>>([]);

        public ValueTask<PluginAsk?> GetPendingAsync(string sessionId, CancellationToken cancellationToken = default) => ValueTask.FromResult<PluginAsk?>(null);

        public ValueTask<PluginAskOutcome> AnswerAsync(PluginAskReference ask, IReadOnlyList<PluginAskAnswer> answers, PluginAskReviewAnswer? review, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new PluginAskOutcome("admitted"));

        public ValueTask<PluginAskOutcome> CancelAsync(PluginAskReference ask, CancellationToken cancellationToken = default) => ValueTask.FromResult(new PluginAskOutcome("cancelled"));
    }

    private sealed class ScopedAsks : PlainAsks, IPluginAskRuntimeService
    {
        internal List<(string Key, bool BuiltIn)> AskedFor { get; } = [];

        public IPluginAskService ForPlugin(string pluginRuntimeKey, bool builtIn)
        {
            AskedFor.Add((pluginRuntimeKey, builtIn));
            return this;
        }
    }
}
