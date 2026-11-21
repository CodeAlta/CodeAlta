using System.Reflection;
using System.Text.Json;
using CodeAlta.Acp;
using CodeAlta.Agent.Runtime;
using CodeAlta.Agent.Telemetry;

namespace CodeAlta.Plugin.Acp;

/// <summary>What an agent process is started with.</summary>
/// <param name="FileName">The full path of the executable.</param>
/// <param name="Arguments">Its arguments.</param>
/// <param name="Environment">The environment variables set for it.</param>
/// <param name="WorkingDirectory">Its working directory, or <see langword="null"/>.</param>
/// <param name="InitializeTimeout">How long it has to answer <c>initialize</c>.</param>
internal sealed record AcpConnectionRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment,
    string? WorkingDirectory,
    TimeSpan InitializeTimeout)
{
    /// <summary>Gets what starts the agent elsewhere than as a child of CodeAlta (the sandbox of the session); null for a child.</summary>
    public IAgentProcessHost? Host { get; init; }

    /// <summary>Gets what the agent needs from the computer when <see cref="Host"/> starts it.</summary>
    public AcpAgentNeeds Needs { get; init; } = AcpAgentNeeds.None;

    /// <summary>Gets whether the client advertises form-based <c>elicitation/create</c> support for this connection.</summary>
    public bool EnableElicitationForm { get; init; }
}

/// <summary>
/// What an agent needs from the computer when it runs in a sandbox: a home folder of its own, where it signs in and
/// keeps its state, its folders, its hosts and the variables of its provider.
/// </summary>
/// <param name="ReadOnlyFolders">The folders it reads besides the system and the developer tools: its package, its runtime.</param>
/// <param name="Hosts">The hosts of its vendor.</param>
/// <param name="PassedVariables">The variables of its provider (a token, a key among them), passed with their values.</param>
/// <param name="Environment">Variables set for it in the sandbox, unless its provider sets them.</param>
/// <param name="OwnHome">The name of its home folder in the sandbox, kept between sessions: the key of its provider.</param>
internal sealed record AcpAgentNeeds(
    IReadOnlyList<string> ReadOnlyFolders,
    IReadOnlyList<string> Hosts,
    IReadOnlyList<string> PassedVariables,
    IReadOnlyDictionary<string, string> Environment,
    string? OwnHome)
{
    public static AcpAgentNeeds None { get; } = new([], [], [], new Dictionary<string, string>(), null);

    /// <summary>Gets the ports of this computer it reaches through the proxy of the sandbox: the MCP server of CodeAlta.</summary>
    public IReadOnlyList<int> LocalPorts { get; init; } = [];
}

/// <summary>Starts an agent and initializes the protocol with it; tests connect a scripted agent instead.</summary>
internal interface IAcpConnectionFactory
{
    /// <summary>Starts the agent; each of its messages is given to <paramref name="messageHandler"/> on its reading loop.</summary>
    Task<AcpClient> StartAsync(AcpConnectionRequest request, Action<AcpServerMessage> messageHandler, CancellationToken cancellationToken);
}

/// <summary>Starts the process of an agent.</summary>
internal sealed class AcpProcessConnectionFactory : IAcpConnectionFactory
{
    public static AcpProcessConnectionFactory Instance { get; } = new();

    // Variables of the environment of CodeAlta that would tell an agent it runs inside another agent.
    private static readonly string[] RemovedVariables =
        ["CLAUDECODE", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_SESSION_ID", "CLAUDE_CODE_SSE_PORT", "CLAUDE_AGENT_SDK_CLIENT_APP"];

    public Task<AcpClient> StartAsync(AcpConnectionRequest request, Action<AcpServerMessage> messageHandler, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(messageHandler);
        if (request.Host is { } host)
        {
            return StartHostedAsync(host, request, messageHandler, cancellationToken);
        }

        return AcpClient.StartAsync(
            new AcpClientOptions
            {
                ProcessOptions = new AcpProcessOptions
                {
                    FileName = request.FileName,
                    Arguments = request.Arguments,
                    WorkingDirectory = request.WorkingDirectory,
                    EnvironmentVariables = request.Environment,
                    // The traces of CodeAlta cover the runs of the agent: it does not report them a second time.
                    RemovedEnvironmentVariables = [.. RemovedVariables, .. OpenTelemetryEnvironment.VariablesHiddenFromAgents()],
                },
                ClientInfo = AcpClientInfo.Implementation,
                ClientCapabilities = AcpClientInfo.CreateCapabilities(request.EnableElicitationForm),
                InitializeTimeout = request.InitializeTimeout,
                MessageHandler = messageHandler,
            },
            cancellationToken);
    }

    // The agent in the sandbox of its session: the host starts it with what it needs, and the client reads and writes
    // the streams of that process.
    private static async Task<AcpClient> StartHostedAsync(IAgentProcessHost host, AcpConnectionRequest request, Action<AcpServerMessage> messageHandler,
        CancellationToken cancellationToken)
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (name, value) in request.Needs.Environment)
        {
            environment[name] = value;
        }

        foreach (var (name, value) in request.Environment)
        {
            environment[name] = value;
        }

        foreach (var name in RemovedVariables)
        {
            environment[name] = null;
        }

        OpenTelemetryEnvironment.HideFromAgent(environment);
        var launch = new AgentProcessLaunch(request.FileName, request.Arguments, request.WorkingDirectory, environment)
        {
            ReadOnlyFolders = request.Needs.ReadOnlyFolders,
            Hosts = request.Needs.Hosts,
            PassedVariables = request.Needs.PassedVariables,
            LocalPorts = request.Needs.LocalPorts,
            OwnHome = request.Needs.OwnHome,
        };
        var process = await host.StartAsync(launch, cancellationToken).ConfigureAwait(false);
        var hosted = new AcpHostedProcess(Path.GetFileNameWithoutExtension(request.FileName), process.StandardOutput, process.StandardInput, process.StandardError,
            process.Exited, process.DisposeAsync);
        return await AcpClient.ConnectAsync(hosted, AcpClientInfo.CreateInitializeRequest(request.EnableElicitationForm), messageHandler, request.InitializeTimeout, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>What CodeAlta tells an agent of itself.</summary>
internal static class AcpClientInfo
{
    public static Implementation Implementation { get; } = new()
    {
        Name = "codealta",
        Title = "CodeAlta",
        Version = typeof(AcpClientInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "1.0.0",
    };

    /// <summary>
    /// The capabilities of CodeAlta as a client: no file system and no terminal of its own (the agent uses its own
    /// tools, and asks before it runs them); a sign-in in a terminal; structured plans, boolean config options, notices
    /// and compactions; and form elicitation when the session can present live questions to the user.
    /// Cursor offers its models with their parameters (effort, context, fast) as options of their own, the way the
    /// protocol names them, to a client that says <c>parameterizedModelPicker</c>; else one fixed variant of each model.
    /// </summary>
    /// <param name="elicitationForm">Whether to advertise <c>elicitation.form</c>.</param>
    public static ClientCapabilities CreateCapabilities(bool elicitationForm = false)
        => new()
        {
            Fs = new FileSystemCapabilities { ReadTextFile = false, WriteTextFile = false },
            Terminal = false,
            Auth = new AuthCapabilities { Terminal = true },
            Elicitation = elicitationForm ? new ElicitationCapabilities { Form = new ElicitationFormCapabilities() } : null,
            Plan = new PlanCapabilities(),
            Session = new ClientSessionCapabilities
            {
                Compaction = new CompactionCapabilities(),
                ConfigOptions = new SessionConfigOptionsCapabilities { Boolean = new BooleanConfigOptionCapabilities() },
                Notices = new NoticeCapabilities(),
            },
            Meta = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["parameterizedModelPicker"] = JsonSerializer.SerializeToElement(true, AcpPluginJsonContext.Default.Boolean),
            },
        };

    /// <summary>The initialize request of a client connected to streams, as <see cref="AcpProcessConnectionFactory"/> sends it.</summary>
    /// <param name="elicitationForm">Whether to advertise <c>elicitation.form</c>.</param>
    public static InitializeRequest CreateInitializeRequest(bool elicitationForm = false)
        => new()
        {
            ClientInfo = Implementation,
            ClientCapabilities = CreateCapabilities(elicitationForm),
            ProtocolVersion = new ProtocolVersion { Value = JsonSerializer.SerializeToElement(1, AcpPluginJsonContext.Default.Int32) },
        };
}
