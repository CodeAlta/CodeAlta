using System.Reflection;
using CodeAlta.Agent.Runtime;
using CodeAlta.CodexAppServer;
using CodeAlta.CodexAppServer.Protocol;
using XenoAtom.Logging;

namespace CodeAlta.Plugin.CodexCli;

/// <summary>What an app-server process is started with.</summary>
/// <param name="Command">The executable and its arguments.</param>
/// <param name="Home">The <c>CODEX_HOME</c> of the provider.</param>
/// <param name="Environment">The variables of the provider and of the session.</param>
/// <param name="WorkingDirectory">Its working directory.</param>
/// <param name="InitializeTimeout">How long it has to answer <c>initialize</c>.</param>
internal sealed record CodexCliConnectionRequest(
    CodexAppServerCommand Command,
    string Home,
    IReadOnlyDictionary<string, string> Environment,
    string WorkingDirectory,
    TimeSpan InitializeTimeout)
{
    /// <summary>Gets what starts the process in the sandbox of the session; <see langword="null"/> for a child of CodeAlta.</summary>
    public IAgentProcessHost? Host { get; init; }

    /// <summary>Gets the ports of this computer the process reaches in a sandbox: the MCP server of the tools of CodeAlta.</summary>
    public IReadOnlyList<int> LocalPorts { get; init; } = [];
}

/// <summary>Starts an app-server and initializes the protocol with it; tests connect a scripted one instead.</summary>
internal interface ICodexCliConnectionFactory
{
    /// <summary>Starts the app-server; each of its messages is given to <paramref name="messageHandler"/> on its reading loop.</summary>
    /// <exception cref="CodexConnectionClosedException">The app-server stopped before it answered.</exception>
    /// <exception cref="TimeoutException">The app-server did not answer <c>initialize</c> in time.</exception>
    Task<CodexAppServerClient> StartAsync(CodexCliConnectionRequest request, Action<CodexServerMessage> messageHandler, CancellationToken cancellationToken);
}

/// <summary>Starts the process of an app-server: a child of CodeAlta, or a process in the sandbox of the session.</summary>
internal sealed class CodexCliProcessConnectionFactory(Logger? logger) : ICodexCliConnectionFactory
{
    /// <summary>The hosts of OpenAI the app-server reaches in a sandbox: the ChatGPT sign-in and its refresh, and the API.</summary>
    public static IReadOnlyList<string> VendorHosts { get; } = ["chatgpt.com", "*.chatgpt.com", "auth.openai.com", "api.openai.com"];

    // Variables of the environment of CodeAlta that would tell Codex it runs inside another agent, or bill a key
    // instead of the account the user signed in with.
    private static readonly string[] RemovedVariables =
        ["CLAUDECODE", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_SESSION_ID", "CLAUDE_CODE_SSE_PORT", "CLAUDE_AGENT_SDK_CLIENT_APP", "OPENAI_API_KEY", "CODEX_API_KEY", "CODEX_SQLITE_HOME"];

    public async Task<CodexAppServerClient> StartAsync(CodexCliConnectionRequest request, Action<CodexServerMessage> messageHandler, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(messageHandler);
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var name in RemovedVariables)
        {
            environment[name] = null;
        }

        foreach (var (name, value) in request.Environment)
        {
            environment[name] = value;
        }

        environment["CODEX_HOME"] = request.Home;
        CodexAppServerProcess process;
        if (request.Host is { } host)
        {
            var launch = new AgentProcessLaunch(request.Command.FileName, request.Command.StartArguments, request.WorkingDirectory, environment)
            {
                ReadOnlyFolders = ReadOnlyFoldersOf(request.Command),
                ReadWriteFolders = [request.Home],
                ProtectedPaths = [.. CodexCliHomeConfig.ProtectedEntries.Select(entry => Path.Combine(request.Home, entry))],
                Hosts = VendorHosts,
                LocalPorts = request.LocalPorts,
            };
            var hosted = await host.StartAsync(launch, cancellationToken).ConfigureAwait(false);
            process = new CodexAppServerProcess(Path.GetFileNameWithoutExtension(request.Command.FileName), hosted.StandardOutput, hosted.StandardInput, hosted.StandardError,
                hosted.Exited, hosted.DisposeAsync, logger);
        }
        else
        {
            if (request.Command.PathDirectory is { } pathDirectory && !environment.ContainsKey("PATH"))
            {
                // What the package puts on the PATH of its commands (rg), before the PATH of CodeAlta.
                environment["PATH"] = pathDirectory + Path.PathSeparator + System.Environment.GetEnvironmentVariable("PATH");
            }

            process = CodexAppServerProcess.Start(new CodexAppServerStartInfo
            {
                FileName = request.Command.FileName,
                Arguments = request.Command.StartArguments,
                WorkingDirectory = request.WorkingDirectory,
                Environment = environment,
            }, logger);
        }

        return await CodexAppServerClient.ConnectAsync(process, CodexCliClientInfo.CreateInitialize(), messageHandler, request.InitializeTimeout, logger, cancellationToken)
            .ConfigureAwait(false);
    }

    // The package of the pinned app-server (its executable and its tools), else the folder of the program the user
    // named and, for a link (Homebrew, npm), the folder of what it points to.
    internal static IReadOnlyList<string> ReadOnlyFoldersOf(CodexAppServerCommand command)
    {
        var folders = new List<string>();
        var directory = Path.GetDirectoryName(command.FileName);
        if (command.IsPinned && directory is not null && Path.GetDirectoryName(directory) is { } package)
        {
            folders.Add(package);
        }
        else if (directory is not null)
        {
            folders.Add(directory);
            try
            {
                if (File.ResolveLinkTarget(command.FileName, returnFinalTarget: true) is { } target && Path.GetDirectoryName(target.FullName) is { } targetDirectory)
                {
                    // An npm link points into its package, whose native binary is in a folder beside it.
                    folders.Add(Path.GetDirectoryName(targetDirectory) ?? targetDirectory);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        return [.. folders.Distinct(StringComparer.Ordinal)];
    }
}

/// <summary>What CodeAlta tells the app-server of itself.</summary>
internal static class CodexCliClientInfo
{
    /// <summary>The name of the client: Codex sends it as the <c>originator</c> of its requests. CodeAlta never presents itself as Codex.</summary>
    public const string Name = "codealta";

    /// <summary>The version of CodeAlta.</summary>
    public static string Version { get; } = typeof(CodexCliClientInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "1.0.0";

    /// <summary>Creates the parameters of <c>initialize</c>: no experimental surface.</summary>
    public static InitializeParams CreateInitialize()
        => new()
        {
            ClientInfo = new ClientInfo { Name = Name, Title = "CodeAlta", Version = Version },
            Capabilities = new InitializeCapabilities { ExperimentalApi = false },
        };
}
