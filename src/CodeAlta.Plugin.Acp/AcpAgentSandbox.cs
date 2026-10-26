namespace CodeAlta.Plugin.Acp;

/// <summary>
/// What an agent CodeAlta knows needs in the sandbox of a session besides what every agent gets there: the hosts of
/// its vendor, and the variables that make it work there. An agent it does not know runs with the hosts its provider
/// lists (<see cref="AcpAgentSettings.SandboxHosts"/>).
/// </summary>
/// <param name="Hosts">The hosts of its vendor.</param>
/// <param name="Environment">Variables set for it in the sandbox, unless its provider sets them.</param>
internal sealed record AcpAgentSandbox(IReadOnlyList<string> Hosts, IReadOnlyDictionary<string, string> Environment)
{
    private static readonly AcpAgentSandbox Unknown = new([], new Dictionary<string, string>());

    // It does not update itself there: the package would land in its home of the sandbox, from hosts it does not reach.
    private static readonly AcpAgentSandbox Copilot = new(
        ["api.githubcopilot.com", "*.githubcopilot.com", "api.github.com", "github.com"],
        new Dictionary<string, string> { ["COPILOT_AUTO_UPDATE"] = "false" });

    // Its MCP client uses the proxy only when Node is told to; the system's credential store is out of reach.
    private static readonly AcpAgentSandbox Cursor = new(
        ["api2.cursor.sh", "*.cursor.sh", "cursor.com"],
        new Dictionary<string, string> { ["AGENT_CLI_CREDENTIAL_STORE"] = "memory", ["NODE_USE_ENV_PROXY"] = "1" });

    private static readonly AcpAgentSandbox Grok = new(
        ["cli-chat-proxy.grok.com", "grok.com", "auth.x.ai", "accounts.x.ai", "api.x.ai"],
        new Dictionary<string, string>());

    /// <summary>Gets what an agent needs, by its id in the ACP registry, else by its command line.</summary>
    /// <param name="agent">The settings of the agent.</param>
    public static AcpAgentSandbox Of(AcpAgentSettings agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        var id = agent.AgentId ?? string.Empty;
        var line = agent.CommandLine;
        if (id.Equals("github-copilot-cli", StringComparison.OrdinalIgnoreCase) || line.Contains("@github/copilot", StringComparison.OrdinalIgnoreCase)) return Copilot;
        if (CursorAcpQuirks.Runs(agent)) return Cursor;
        if (id.Equals("grok-build", StringComparison.OrdinalIgnoreCase) || line.Contains("@xai-official/grok", StringComparison.OrdinalIgnoreCase)) return Grok;
        return Unknown;
    }
}
