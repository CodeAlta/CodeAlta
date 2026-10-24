using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Harness;
using XenoAtom.Logging;

namespace CodeAlta.Plugin.Acp;

/// <summary>What the runtime of an ACP provider runs with.</summary>
internal sealed class AcpRuntimeOptions
{
    /// <summary>Gets the key of the provider.</summary>
    public required string ProviderKey { get; init; }

    /// <summary>Gets the name of the agent, as the instructions and the messages name it.</summary>
    public required string AgentName { get; init; }

    /// <summary>Gets what starts the agent.</summary>
    public required AcpAgentSettings Agent { get; init; }

    /// <summary>Gets the service that serves the tools of a session to the agent.</summary>
    public required IHarnessToolEndpoints ToolEndpoints { get; init; }

    /// <summary>Gets the folder the agent is started in to read its models, without a session.</summary>
    public required string ProbeDirectory { get; init; }

    /// <summary>Gets what starts the agent; tests give a scripted one.</summary>
    public IAcpConnectionFactory ConnectionFactory { get; init; } = AcpProcessConnectionFactory.Instance;

    /// <summary>Gets how long the process of an idle session is kept; the next turn starts it again.</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Gets how long the agent has to answer a request that is not a prompt.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Gets how long a stopped turn has to end before its process is stopped.</summary>
    public TimeSpan CancelTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Gets how long the agent has to start; longer through a package runner, which may download it first.</summary>
    public TimeSpan InitializeTimeout => Agent.UsesPackageRunner ? TimeSpan.FromMinutes(10) : TimeSpan.FromMinutes(2);

    /// <summary>Gets the logger, or <see langword="null"/>.</summary>
    public Logger? Logger { get; init; }

    /// <summary>Gets what the runtime learned of the agent: shared by its sessions and its probe.</summary>
    public AcpAgentProfile Profile { get; } = new();
}

/// <summary>What a runtime learned of its agent: its capabilities and its models, from its last start.</summary>
internal sealed class AcpAgentProfile
{
    private readonly Lock _gate = new();
    private AcpAgentCapabilities? _capabilities;
    private IReadOnlyList<AgentModelInfo>? _models;
    private string? _currentModel;

    public AcpAgentCapabilities? Capabilities
    {
        get { lock (_gate) return _capabilities; }
        set { lock (_gate) _capabilities = value; }
    }

    public IReadOnlyList<AgentModelInfo>? Models
    {
        get { lock (_gate) return _models; }
    }

    public string? CurrentModel
    {
        get { lock (_gate) return _currentModel; }
    }

    public void SetModels(IReadOnlyList<AgentModelInfo> models, string? currentModel)
    {
        lock (_gate)
        {
            _models = models;
            _currentModel = currentModel;
        }
    }
}

/// <summary>What an agent said it can do when it was initialized.</summary>
/// <param name="AgentName">The name the agent gives itself, or <see langword="null"/>.</param>
/// <param name="AgentVersion">Its version, or <see langword="null"/>.</param>
/// <param name="LoadSession">Whether it loads a session (<c>session/load</c>).</param>
/// <param name="ResumeSession">Whether it resumes a session without replaying it (<c>session/resume</c>).</param>
/// <param name="CloseSession">Whether it closes a session (<c>session/close</c>).</param>
/// <param name="SystemPrompt">Whether it takes a system prompt from its client (<c>systemPrompt</c>).</param>
/// <param name="McpHttp">Whether it connects to an MCP server over HTTP.</param>
/// <param name="Images">Whether a prompt can hold images.</param>
/// <param name="AuthMethods">The ways to sign in it lists.</param>
internal sealed record AcpAgentCapabilities(
    string? AgentName,
    string? AgentVersion,
    bool LoadSession,
    bool ResumeSession,
    bool CloseSession,
    bool SystemPrompt,
    bool McpHttp,
    bool Images,
    IReadOnlyList<JsonElement> AuthMethods)
{
    /// <summary>Reads the capabilities of an initialize response.</summary>
    public static AcpAgentCapabilities Read(JsonElement initialize)
    {
        AcpJson.TryGetObject(initialize, "agentCapabilities", out var agent);
        AcpJson.TryGetObject(agent, "sessionCapabilities", out var session);
        AcpJson.TryGetObject(agent, "mcpCapabilities", out var mcp);
        AcpJson.TryGetObject(agent, "promptCapabilities", out var prompt);
        AcpJson.TryGetObject(initialize, "agentInfo", out var info);
        var methods = AcpJson.TryGetArray(initialize, "authMethods", out var array)
            ? array.EnumerateArray().Where(static method => method.ValueKind == JsonValueKind.Object).Select(static method => method.Clone()).ToArray()
            : [];
        return new AcpAgentCapabilities(
            AcpJson.GetString(info, "title") ?? AcpJson.GetString(info, "name"),
            AcpJson.GetString(info, "version"),
            AcpJson.GetBoolean(agent, "loadSession"),
            AcpJson.HasCapability(session, "resume"),
            AcpJson.HasCapability(session, "close"),
            AcpJson.HasCapability(session, "systemPrompt"),
            AcpJson.GetBoolean(mcp, "http"),
            AcpJson.GetBoolean(prompt, "image"),
            methods);
    }
}
