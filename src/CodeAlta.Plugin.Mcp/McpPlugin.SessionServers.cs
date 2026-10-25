using System.Text;

namespace CodeAlta.Plugin.Mcp;

/// <summary>
/// An MCP server a host was given for one session, such as one of the <c>mcpServers</c> of a client of the Agent Client
/// Protocol: a command to start (stdio), or the address of a server (streamable HTTP, with a fallback to SSE).
/// </summary>
public sealed record McpSessionServer
{
    /// <summary>Gets the name of the server, which its key and the names of its tools are made of.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the command that starts a stdio server; null for a server at <see cref="Url"/>.</summary>
    public string? Command { get; init; }

    /// <summary>Gets the arguments of <see cref="Command"/>.</summary>
    public IReadOnlyList<string> Args { get; init; } = [];

    /// <summary>Gets the environment variables <see cref="Command"/> is started with.</summary>
    public IReadOnlyDictionary<string, string> Env { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Gets the address of an HTTP or SSE server; null for a stdio server.</summary>
    public string? Url { get; init; }

    /// <summary>Gets the headers of the requests to <see cref="Url"/>.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
}

public sealed partial class McpPlugin
{
    // The session each server given in memory belongs to, by key.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _sessionServerOwners = new(StringComparer.Ordinal);

    /// <summary>
    /// Adds MCP servers for one session, in memory: no configuration file is written. They are activated for that session,
    /// which is given their tools from its next run on, and they stay until the plugin is disposed. They are servers of the
    /// process: the other sessions see them as servers to activate (<c>alta mcp activate</c>). A value written
    /// <c>${NAME}</c> is read as the variable <c>NAME</c> of the environment, as in a configuration file.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="projectDirectory">The project of the session, whose configured servers keep their names; null for none.</param>
    /// <param name="servers">The servers.</param>
    /// <returns>
    /// The key of each server, in order: its name, made of letters, digits, <c>-</c> and <c>_</c>, followed by the beginning
    /// of the session id when the name is taken by a configured server or a server of another session.
    /// </returns>
    /// <exception cref="ArgumentException">The session is blank, or a server has no name, both or neither of a command and an address, or an address that is not absolute.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="servers"/> is null.</exception>
    public IReadOnlyList<string> AddSessionServers(string sessionId, string? projectDirectory, IReadOnlyList<McpSessionServer> servers)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(servers);
        if (servers.Count == 0) return [];
        foreach (var server in servers)
        {
            ArgumentNullException.ThrowIfNull(server, nameof(servers));
            ArgumentException.ThrowIfNullOrWhiteSpace(server.Name, nameof(servers));
            var hasCommand = !string.IsNullOrWhiteSpace(server.Command);
            var hasUrl = !string.IsNullOrWhiteSpace(server.Url);
            if (hasCommand == hasUrl) throw new ArgumentException($"The MCP server '{server.Name}' needs either a command or an address.", nameof(servers));
            if (hasUrl && !Uri.TryCreate(server.Url, UriKind.Absolute, out _)) throw new ArgumentException($"The address of the MCP server '{server.Name}' is not absolute.", nameof(servers));
        }

        // The keys of the configured servers, and of the servers of the other sessions; those of this session are replaced.
        var taken = _configCache.Discover(projectDirectory, _userHomeDirectory).EffectiveServers
            .Where(static server => server.Definition.SourcePath != McpConfigDiscovery.SessionServersPath)
            .Select(static server => server.Definition.Key)
            .Concat(_sessionServerOwners.Where(owner => !string.Equals(owner.Value, sessionId, StringComparison.OrdinalIgnoreCase)).Select(static owner => owner.Key))
            .ToHashSet(StringComparer.Ordinal);
        var suffix = "-" + new string([.. sessionId.Where(char.IsAsciiLetterOrDigit).Take(8)]);
        var definitions = new List<McpServerDefinition>(servers.Count);
        foreach (var server in servers)
        {
            var name = KeyOf(server.Name);
            var key = taken.Contains(name) ? name + suffix : name;
            for (var index = 2; taken.Contains(key) || definitions.Any(definition => definition.Key == key); index++) key = $"{name}{suffix}-{index}";
            definitions.Add(new McpServerDefinition
            {
                Key = key,
                Transport = string.IsNullOrWhiteSpace(server.Url) ? McpTransportKind.Stdio : McpTransportKind.Http,
                SourceScope = McpConfigScope.Project,
                SourcePath = McpConfigDiscovery.SessionServersPath,
                SourceFlavor = McpConfigFlavor.CodeAlta,
                SourceOrigin = McpConfigOrigin.Common,
                Command = string.IsNullOrWhiteSpace(server.Command) ? null : server.Command,
                Args = [.. server.Args],
                // A stdio server starts in the folder of its session.
                Cwd = string.IsNullOrWhiteSpace(server.Command) || string.IsNullOrWhiteSpace(projectDirectory) ? null : Path.GetFullPath(projectDirectory),
                Env = new Dictionary<string, string>(server.Env, StringComparer.Ordinal),
                Url = string.IsNullOrWhiteSpace(server.Url) ? null : server.Url,
                Headers = new Dictionary<string, string>(server.Headers, StringComparer.Ordinal),
            });
        }

        var keys = definitions.Select(static definition => definition.Key).ToArray();
        foreach (var key in keys) _sessionServerOwners[key] = sessionId;
        _configCache.AddSessionServers(definitions);
        var scope = McpActivationState.ResolveScopeKey(sessionId, null);
        if (!string.IsNullOrWhiteSpace(projectDirectory)) _activationState.SetProjectDirectory(scope, Path.GetFullPath(projectDirectory));
        _activationState.ActivateServers(scope, keys);
        return keys;
    }

    // A key of letters, digits, - and _.
    private static string KeyOf(string name)
    {
        var key = new StringBuilder(name.Length);
        foreach (var character in name.Trim()) key.Append(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' ? character : '-');
        var text = key.ToString().Trim('-');
        return text.Length == 0 ? "server" : text.Length <= 64 ? text : text[..64];
    }
}
