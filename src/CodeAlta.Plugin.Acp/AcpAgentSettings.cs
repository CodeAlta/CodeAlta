using CodeAlta.Acp;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugin.Acp;

/// <summary>
/// What starts the agent of an ACP provider, from its settings (<c>[providers.&lt;key&gt;]</c> with <c>type = "acp"</c>).
/// </summary>
/// <param name="Command">The executable: a command on <c>PATH</c> (<c>npx</c>) or a full path.</param>
/// <param name="Arguments">Its arguments.</param>
/// <param name="Environment">The environment variables set for it.</param>
/// <param name="AgentId">The id of the agent in the ACP registry, or <see langword="null"/> for an agent added by hand.</param>
/// <param name="AgentVersion">The version installed from the registry, or <see langword="null"/>.</param>
internal sealed record AcpAgentSettings(
    string Command,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment,
    string? AgentId,
    string? AgentVersion)
{
    public const string ProviderType = "acp";
    public const string CommandKey = "command";
    public const string ArgumentsKey = "arguments";
    public const string EnvironmentKey = "env";
    public const string AgentKey = "agent";
    public const string AgentVersionKey = "agent_version";
    public const string DistributionKey = "distribution";
    public const string SandboxHostsKey = "sandbox_hosts";

    /// <summary>Reads the settings of a provider; the command is required.</summary>
    /// <exception cref="InvalidOperationException">The provider names no command.</exception>
    public static AcpAgentSettings Read(PluginProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var command = settings.GetString(CommandKey)
            ?? throw new InvalidOperationException($"The ACP provider '{settings.ProviderKey}' names no command.");
        return new AcpAgentSettings(
            command,
            settings.GetList(ArgumentsKey),
            settings.GetMap(EnvironmentKey),
            settings.GetString(AgentKey),
            settings.GetString(AgentVersionKey))
        {
            SandboxHosts = settings.GetList(SandboxHostsKey),
        };
    }

    /// <summary>Gets the hosts the agent reaches in the sandbox of a session besides those CodeAlta knows for it: the service of its vendor.</summary>
    public IReadOnlyList<string> SandboxHosts { get; init; } = [];

    /// <summary>Checks the settings of a provider.</summary>
    public static IReadOnlyList<string> Validate(PluginProviderSettings settings)
        => settings.GetString(CommandKey) is null ? [$"The ACP provider '{settings.ProviderKey}' names no command."] : [];

    /// <summary>Gets the name of the executable, without its folder and extension, as the window names the agent.</summary>
    public string CommandName => Path.GetFileNameWithoutExtension(Command);

    /// <summary>Gets whether the agent runs through a package runner, which may download it at its first start.</summary>
    public bool UsesPackageRunner => CommandName is "npx" or "uvx" or "bunx" or "pnpx";

    /// <summary>Gets the command line as it is shown.</summary>
    public string CommandLine
        => string.Join(' ', [Command, .. Arguments.Select(static argument => argument.Contains(' ', StringComparison.Ordinal) ? $"\"{argument}\"" : argument)]);

    /// <summary>Returns the full path of the executable, or <see langword="null"/> when it is not found.</summary>
    public string? ResolveExecutable() => AcpCommandLocator.FindCommandPath(Command);
}
