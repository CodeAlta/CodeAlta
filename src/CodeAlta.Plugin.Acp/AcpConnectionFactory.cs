using System.Reflection;
using System.Text.Json;
using CodeAlta.Acp;

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
    TimeSpan InitializeTimeout);

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
        return AcpClient.StartAsync(
            new AcpClientOptions
            {
                ProcessOptions = new AcpProcessOptions
                {
                    FileName = request.FileName,
                    Arguments = request.Arguments,
                    WorkingDirectory = request.WorkingDirectory,
                    EnvironmentVariables = request.Environment,
                    RemovedEnvironmentVariables = RemovedVariables,
                },
                ClientInfo = AcpClientInfo.Implementation,
                ClientCapabilities = AcpClientInfo.CreateCapabilities(),
                InitializeTimeout = request.InitializeTimeout,
                MessageHandler = messageHandler,
            },
            cancellationToken);
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
    /// tools, and asks before it runs them); a sign-in in a terminal; the notices and compactions the agent reports.
    /// Cursor offers its models with their parameters (effort, context, fast) as options of their own, the way the
    /// protocol names them, to a client that says <c>parameterizedModelPicker</c>; else one fixed variant of each model.
    /// </summary>
    public static ClientCapabilities CreateCapabilities()
        => new()
        {
            Fs = new FileSystemCapabilities { ReadTextFile = false, WriteTextFile = false },
            Terminal = false,
            Auth = new AuthCapabilities { Terminal = true },
            Session = new ClientSessionCapabilities
            {
                Compaction = new CompactionCapabilities(),
                Notices = new NoticeCapabilities(),
            },
            Meta = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["parameterizedModelPicker"] = JsonSerializer.SerializeToElement(true, AcpPluginJsonContext.Default.Boolean),
            },
        };

    /// <summary>The initialize request of a client connected to streams, as <see cref="AcpProcessConnectionFactory"/> sends it.</summary>
    public static InitializeRequest CreateInitializeRequest()
        => new()
        {
            ClientInfo = Implementation,
            ClientCapabilities = CreateCapabilities(),
            ProtocolVersion = new ProtocolVersion { Value = JsonSerializer.SerializeToElement(1, AcpPluginJsonContext.Default.Int32) },
        };
}
