using System.Net;
using CodeAlta.Agent;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugin.Acp;

/// <summary>
/// Runs the agents of the Agent Client Protocol (ACP) as model providers: any agent of the ACP registry, installed in
/// one step, or one configured by hand (<c>type = "acp"</c>). An agent runs its own loop and tools, with the
/// instructions of CodeAlta and its tools through an MCP server of the session.
/// </summary>
[Plugin(PluginId, DisplayName = "ACP", Description = "Runs the agents of the Agent Client Protocol (ACP) as model providers.", Frontends = PluginFrontends.All | PluginFrontends.Cli)]
public sealed class AcpPlugin : PluginBase
{
    /// <summary>The id of the plugin in the configuration.</summary>
    public const string PluginId = "acp";

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly Uri? _registryUri;
    private string? _cacheRoot;
    private AcpRegistryCatalog? _catalog;
    private AcpModelCacheStore? _modelCache;

    /// <summary>Initializes a new instance of the <see cref="AcpPlugin"/> class.</summary>
    public AcpPlugin()
        : this(httpClient: null, registryUri: null)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="AcpPlugin"/> class with another client and registry, for tests.</summary>
    /// <param name="httpClient">The client of the registry and the downloads, or <see langword="null"/> for one of its own.</param>
    /// <param name="registryUri">The address of the registry, or <see langword="null"/> for the ACP registry.</param>
    internal AcpPlugin(HttpClient? httpClient, Uri? registryUri)
    {
        _httpClient = httpClient ?? new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromMinutes(10),
        };
        _ownsHttpClient = httpClient is null;
        _registryUri = registryUri;
    }

    /// <summary>Gets the folder of the cache of the plugin (<c>~/.alta/cache/acp</c>), shared by the normal and the developer instance.</summary>
    internal string CacheRoot => _cacheRoot ??= Path.Combine(Context.Host.UserDataDirectory, "cache", "acp");

    internal IAcpConnectionFactory ConnectionFactory { get; init; } = AcpProcessConnectionFactory.Instance;

    private AcpRegistryCatalog Catalog => _catalog ??= new AcpRegistryCatalog(CacheRoot, _httpClient, _registryUri, Logger);

    private AcpModelCacheStore ModelCache => _modelCache ??= new AcpModelCacheStore(Path.Combine(CacheRoot, "models"));

    private string ProbeDirectory => Path.Combine(CacheRoot, "probe");

    /// <inheritdoc />
    public override IEnumerable<PluginModelProviderContribution> GetModelProviders()
    {
        yield return new PluginModelProviderContribution
        {
            ProviderType = AcpAgentSettings.ProviderType,
            DisplayName = "ACP agent",
            Tag = "ACP",
            PermissionModes = AcpAgentSession.PermissionModes,
            Settings =
            [
                new PluginProviderSetting(AcpAgentSettings.CommandKey, "Command", PluginProviderSettingKind.Path)
                {
                    Description = "The executable of the agent: a command on PATH, such as npx, or a full path.",
                    Placeholder = "npx",
                },
                new PluginProviderSetting(AcpAgentSettings.ArgumentsKey, "Arguments", PluginProviderSettingKind.List)
                {
                    Description = "What starts the agent in ACP mode, such as --acp.",
                },
                new PluginProviderSetting(AcpAgentSettings.EnvironmentKey, "Environment", PluginProviderSettingKind.Map)
                {
                    Advanced = true,
                },
                new PluginProviderSetting(AcpAgentSettings.AgentKey, "Registry agent", PluginProviderSettingKind.Text) { ReadOnly = true, Advanced = true },
                new PluginProviderSetting(AcpAgentSettings.AgentVersionKey, "Version", PluginProviderSettingKind.Text) { ReadOnly = true, Advanced = true },
            ],
            Validate = AcpAgentSettings.Validate,
            Describe = DescribeAsync,
            GetIconSvg = ReadIcon,
            CreateRuntime = CreateRuntime,
            Account = new AcpAccount(ProbeDirectory, ConnectionFactory),
            Catalog = Catalog,
        };
    }

    /// <inheritdoc />
    public override ValueTask OnDeactivatingAsync(CancellationToken cancellationToken = default)
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    // The icon of the agent of the registry a provider runs, as the catalog kept it.
    private string? ReadIcon(PluginProviderSettings settings)
    {
        if (settings.GetString(AcpAgentSettings.AgentKey) is not { } agentId)
        {
            return null;
        }

        var path = Path.Combine(CacheRoot, "icons", new string([.. agentId.Select(static character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' ? character : '_')]) + ".svg");
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private IAgentModelProviderRuntime CreateRuntime(PluginModelProviderContext context)
    {
        var agent = AcpAgentSettings.Read(context.Settings);
        var options = new AcpRuntimeOptions
        {
            ProviderKey = context.Settings.ProviderKey,
            AgentName = context.Settings.DisplayName,
            Agent = agent,
            ToolEndpoints = context.ToolEndpoints,
            ProbeDirectory = ProbeDirectory,
            ConnectionFactory = ConnectionFactory,
            Logger = context.Logger,
            Quirks = AcpAgentQuirks.For(agent),
            ModelCatalog = context.ModelCatalog,
        };
        return new AcpModelProviderRuntime(context.Descriptor, options, ModelCache);
    }

    private static Task<PluginProviderDescription?> DescribeAsync(PluginProviderSettings settings, CancellationToken cancellationToken)
    {
        if (AcpAgentSettings.Validate(settings).Count > 0)
        {
            return Task.FromResult<PluginProviderDescription?>(null);
        }

        var agent = AcpAgentSettings.Read(settings);
        string? detail = null;
        if (agent.ResolveExecutable() is null)
        {
            detail = agent.UsesPackageRunner
                ? $"{agent.CommandName} is not on PATH: install {(agent.CommandName == "uvx" ? "uv" : "Node.js")}."
                : $"{agent.Command} was not found.";
        }
        else if (agent.UsesPackageRunner)
        {
            detail = $"{agent.CommandName} fetches the agent at its first start.";
        }

        return Task.FromResult<PluginProviderDescription?>(new PluginProviderDescription(agent.CommandLine, detail));
    }
}
