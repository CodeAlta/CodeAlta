using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Agent.Harness;
using CodeAlta.CodexAppServer;
using CodeAlta.Plugins.Abstractions;
using XenoAtom.Logging;

namespace CodeAlta.Plugin.CodexCli;

/// <summary>What the runtime of a Codex CLI provider runs with.</summary>
internal sealed class CodexCliRuntimeOptions
{
    /// <summary>Gets the key of the provider.</summary>
    public required string ProviderKey { get; init; }

    /// <summary>Gets the name of the provider, as the messages name it.</summary>
    public required string ProviderName { get; init; }

    /// <summary>Gets the settings of the provider.</summary>
    public required CodexCliSettings Settings { get; init; }

    /// <summary>Gets the <c>CODEX_HOME</c> of the provider.</summary>
    public required string Home { get; init; }

    /// <summary>Gets the service that serves the tools of a session to Codex.</summary>
    public required IHarnessToolEndpoints ToolEndpoints { get; init; }

    /// <summary>Gets the folder the app-server is started in to read the account and the models, without a session.</summary>
    public required string ProbeDirectory { get; init; }

    /// <summary>Gets what downloads the pinned app-server, or <see langword="null"/> when only a command the user named runs.</summary>
    public CodexAppServerInstaller? Installer { get; init; }

    /// <summary>Gets what starts the app-server; tests give a scripted one.</summary>
    public required ICodexCliConnectionFactory ConnectionFactory { get; init; }

    /// <summary>Gets how long the process of an idle session is kept; the next turn starts it again.</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Gets how long the app-server has to answer a request that is not a turn.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Gets how long a stopped turn has to end before its process is stopped.</summary>
    public TimeSpan CancelTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Gets how long the app-server has to start.</summary>
    public TimeSpan InitializeTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Gets how long a call of a tool of CodeAlta may take, as Codex is told it.</summary>
    public TimeSpan ToolTimeout { get; init; } = TimeSpan.FromHours(4);

    /// <summary>Gets the logger, or <see langword="null"/>.</summary>
    public Logger? Logger { get; init; }

    /// <summary>Gets what describes the models by the catalog of models of the host, or <see langword="null"/>.</summary>
    public IPluginModelCatalog? ModelCatalog { get; init; }

    /// <summary>Gets the models the probe and the sessions learned last.</summary>
    public CodexCliProfile Profile { get; } = new();

    /// <summary>
    /// Gets the program that starts the app-server: the one the settings name, else the pinned package, downloaded
    /// the first time it is needed.
    /// </summary>
    /// <param name="progress">Where the progress of a download goes, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">A token to cancel the download.</param>
    /// <exception cref="AgentProviderFailureException">The program was not found, or could not be downloaded.</exception>
    public async Task<CodexAppServerCommand> ResolveCommandAsync(IProgress<CodexInstallProgress>? progress, CancellationToken cancellationToken)
    {
        if (Settings.Command is { } command)
        {
            return CodexAppServerCommand.FromUserCommand(command)
                ?? throw new AgentProviderFailureException(
                    $"{command} was not found: {ProviderName} cannot start. Install Codex, or clear its command in Settings > Providers to use the version CodeAlta downloads.",
                    new AgentFailureInfo(AgentFailureKind.Unavailable)) { Data = { ["category"] = CodexCliFailures.NotFoundCategory } };
        }

        if (Installer is null)
        {
            throw new AgentProviderFailureException($"{ProviderName} has no command, and no Codex can be downloaded here.", new AgentFailureInfo(AgentFailureKind.Unavailable))
            {
                Data = { ["category"] = CodexCliFailures.NotFoundCategory },
            };
        }

        try
        {
            return CodexAppServerCommand.FromInstallation(await Installer.EnsureInstalledAsync(progress, cancellationToken).ConfigureAwait(false));
        }
        catch (CodexInstallException exception)
        {
            throw new AgentProviderFailureException(exception.Message, new AgentFailureInfo(AgentFailureKind.Unavailable), exception)
            {
                Data = { ["category"] = CodexCliFailures.NotFoundCategory },
            };
        }
    }
}

/// <summary>What a runtime learned of Codex: its models and its version, shared by its sessions and its probe.</summary>
internal sealed class CodexCliProfile
{
    private readonly Lock _gate = new();
    private IReadOnlyList<AgentModelInfo>? _models;
    private string? _version;

    public IReadOnlyList<AgentModelInfo>? Models
    {
        get { lock (_gate) return _models; }
        set { lock (_gate) _models = value; }
    }

    public string? Version
    {
        get { lock (_gate) return _version; }
        set { lock (_gate) _version = value; }
    }
}
