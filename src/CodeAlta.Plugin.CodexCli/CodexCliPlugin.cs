using CodeAlta.Agent;
using CodeAlta.Agent.Harness;
using CodeAlta.CodexAppServer;
using CodeAlta.Plugins.Abstractions;
using XenoAtom.Logging;

namespace CodeAlta.Plugin.CodexCli;

/// <summary>
/// Runs OpenAI's Codex as a model provider (<c>type = "codex-cli"</c>): the standalone <c>codex-app-server</c> of the
/// pinned Codex release, signed in with Codex's own ChatGPT sign-in. Codex keeps its model loop and its coding tools;
/// CodeAlta gives it its instructions and its tools through an MCP server of the session, answers its approvals, and
/// runs it in the sandbox of a sandboxed session.
/// </summary>
[Plugin(PluginId, DisplayName = "Codex CLI", Description = "Runs OpenAI's Codex app-server as a model provider, signed in with Codex's own ChatGPT sign-in.", Frontends = PluginFrontends.All | PluginFrontends.Cli)]
public sealed class CodexCliPlugin : PluginBase
{
    /// <summary>The id of the plugin in the configuration.</summary>
    public const string PluginId = "codex-cli";

    private CodexAppServerInstaller? _installer;
    private string? _userDataDirectory;

    /// <summary>Initializes a new instance of the <see cref="CodexCliPlugin"/> class.</summary>
    public CodexCliPlugin()
    {
    }

    /// <summary>Gets or initializes the folder of the data of CodeAlta, for tests; the host's by default.</summary>
    internal string UserDataDirectory
    {
        get => _userDataDirectory ??= Context.Host.UserDataDirectory;
        init => _userDataDirectory = value;
    }

    /// <summary>Gets what starts the app-server; tests give a scripted one.</summary>
    internal ICodexCliConnectionFactory? ConnectionFactory { get; init; }

    /// <summary>Gets the folder of the pinned app-server in the cache (<c>~/.alta/cache/codex-cli</c>), shared by the normal and the developer instance.</summary>
    internal string CacheRoot => Path.Combine(UserDataDirectory, "cache", "codex-cli");

    internal CodexAppServerInstaller Installer => _installer ??= new CodexAppServerInstaller(CacheRoot);

    internal new Logger? Logger => base.Logger;

    private string ProbeDirectory => Path.Combine(CacheRoot, "probe");

    /// <inheritdoc />
    public override IEnumerable<PluginModelProviderContribution> GetModelProviders()
    {
        yield return new PluginModelProviderContribution
        {
            ProviderType = CodexCliSettings.ProviderType,
            DisplayName = "Codex CLI",
            Tag = "Codex",
            PermissionModes = CodexCliSession.PermissionModes,
            Settings =
            [
                new PluginProviderSetting(CodexCliSettings.CommandKey, "Command", PluginProviderSettingKind.Path)
                {
                    Description = $"Leave empty to run Codex {CodexPinnedRelease.Version}, which CodeAlta downloads and is tested with. Else a codex or codex-app-server you installed.",
                    Placeholder = $"Codex {CodexPinnedRelease.Version}",
                    Advanced = true,
                },
                new PluginProviderSetting(CodexCliSettings.HomeKey, "Codex home", PluginProviderSettingKind.Path)
                {
                    Description = "The CODEX_HOME of this provider: its sign-in and its threads. Empty for a folder of CodeAlta of its own. Several providers are several accounts.",
                    Placeholder = "~/.alta/plugin-data/codex-cli/<provider key>",
                    Advanced = true,
                },
                new PluginProviderSetting(CodexCliSettings.EnvironmentKey, "Environment", PluginProviderSettingKind.Map)
                {
                    Description = "Variables set for Codex, also in a sandbox.",
                    Advanced = true,
                },
            ],
            Validate = CodexCliSettings.Validate,
            Describe = DescribeAsync,
            CreateRuntime = CreateRuntime,
            Account = new CodexCliAccount(this),
            Catalog = new CodexCliCatalog(() => Installer),
        };
    }

    /// <inheritdoc />
    public override ValueTask OnDeactivatingAsync(CancellationToken cancellationToken = default)
    {
        _installer?.Dispose();
        _installer = null;
        return ValueTask.CompletedTask;
    }

    /// <summary>Creates the options of a provider.</summary>
    internal CodexCliRuntimeOptions CreateOptions(PluginProviderSettings settings, IHarnessToolEndpoints? toolEndpoints, Logger? logger, IPluginModelCatalog? catalog)
    {
        var codex = CodexCliSettings.Read(settings);
        return new CodexCliRuntimeOptions
        {
            ProviderKey = settings.ProviderKey,
            ProviderName = settings.DisplayName,
            Settings = codex,
            Home = codex.ResolveHome(UserDataDirectory, settings.ProviderKey),
            ToolEndpoints = toolEndpoints ?? NoToolEndpoints.Instance,
            ProbeDirectory = ProbeDirectory,
            Installer = Installer,
            ConnectionFactory = ConnectionFactory ?? new CodexCliProcessConnectionFactory(logger),
            Logger = logger,
            ModelCatalog = catalog,
        };
    }

    private IAgentModelProviderRuntime CreateRuntime(PluginModelProviderContext context)
        => new CodexCliModelProviderRuntime(context.Descriptor, CreateOptions(context.Settings, context.ToolEndpoints, context.Logger, context.ModelCatalog));

    private Task<PluginProviderDescription?> DescribeAsync(PluginProviderSettings settings, CancellationToken cancellationToken)
    {
        if (CodexCliSettings.Validate(settings).Count > 0)
        {
            return Task.FromResult<PluginProviderDescription?>(null);
        }

        var codex = CodexCliSettings.Read(settings);
        var home = codex.ResolveHome(UserDataDirectory, settings.ProviderKey);
        if (codex.Command is { } command)
        {
            return Task.FromResult<PluginProviderDescription?>(CodexAppServerCommand.FromUserCommand(command) is { } found
                ? new PluginProviderDescription(string.Join(' ', [found.FileName, .. found.Arguments]), $"Its home is {home}. CodeAlta is tested with Codex {CodexPinnedRelease.Version}.")
                : new PluginProviderDescription(command, $"{command} was not found."));
        }

        var installed = Installer.Installed;
        return Task.FromResult<PluginProviderDescription?>(new PluginProviderDescription(
            $"Codex {CodexPinnedRelease.Version}",
            installed is null ? $"CodeAlta downloads it the first time it is needed. Its home is {home}." : $"Downloaded by CodeAlta. Its home is {home}."));
    }

    // A provider created for its sign-in only, which no session runs.
    private sealed class NoToolEndpoints : IHarnessToolEndpoints
    {
        public static NoToolEndpoints Instance { get; } = new();

        public ValueTask<HarnessToolEndpoint> OpenAsync(HarnessMcpToolServer server, CancellationToken cancellationToken)
            => throw new InvalidOperationException("The tools of a session are not served outside a session.");
    }
}
