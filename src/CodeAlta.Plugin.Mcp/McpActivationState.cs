namespace CodeAlta.Plugin.Mcp;

internal sealed class McpActivationState
{
    private readonly object _syncRoot = new();
    private readonly Dictionary<string, McpActivationScopeState> _scopes = new(StringComparer.Ordinal);

    internal event Action<string>? Changed;

    public IReadOnlyList<string> GetActiveServers(string scopeKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);
        lock (_syncRoot)
        {
            return _scopes.TryGetValue(scopeKey, out var state)
                ? state.ActiveServers.OrderBy(static server => server, StringComparer.Ordinal).ToArray()
                : [];
        }
    }

    // The tools activated one by one, on servers that are not active as a whole: (server, tool name on the server).
    public IReadOnlyList<(string Server, string Tool)> GetActiveTools(string scopeKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);
        lock (_syncRoot)
        {
            return _scopes.TryGetValue(scopeKey, out var state)
                ? state.ActiveTools.Where(tool => !state.ActiveServers.Contains(tool.Server))
                    .OrderBy(static tool => tool.Server, StringComparer.Ordinal).ThenBy(static tool => tool.Tool, StringComparer.Ordinal).ToArray()
                : [];
        }
    }

    // The servers the session uses: those active as a whole and those with a tool activated.
    public IReadOnlyList<string> GetUsedServers(string scopeKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);
        lock (_syncRoot)
        {
            return _scopes.TryGetValue(scopeKey, out var state)
                ? state.ActiveServers.Concat(state.ActiveTools.Select(static tool => tool.Server)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()
                : [];
        }
    }

    // Whether a session has this tool: its server is active, or the tool is.
    public bool IsActive(string scopeKey, string server, string tool)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);
        lock (_syncRoot)
        {
            return _scopes.TryGetValue(scopeKey, out var state) && (state.ActiveServers.Contains(server) || state.ActiveTools.Contains((server, tool)));
        }
    }

    public IReadOnlyList<(string Server, string Tool)> ActivateTools(string scopeKey, IEnumerable<(string Server, string Tool)> tools)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);
        ArgumentNullException.ThrowIfNull(tools);
        var changed = false;
        lock (_syncRoot)
        {
            var state = GetOrCreateScope(scopeKey);
            foreach (var (server, tool) in tools)
            {
                if (!string.IsNullOrWhiteSpace(server) && !string.IsNullOrWhiteSpace(tool))
                {
                    changed |= state.ActiveTools.Add((server.Trim(), tool.Trim()));
                }
            }
        }

        if (changed)
        {
            Changed?.Invoke(scopeKey);
        }

        return GetActiveTools(scopeKey);
    }

    public IReadOnlyDictionary<string, int> GetToolCounts(string scopeKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);
        lock (_syncRoot)
        {
            return _scopes.TryGetValue(scopeKey, out var state)
                ? new Dictionary<string, int>(state.ToolCounts, StringComparer.Ordinal)
                : new Dictionary<string, int>(StringComparer.Ordinal);
        }
    }

    public IReadOnlyList<string> ActivateServers(string scopeKey, IEnumerable<string> serverKeys)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);
        ArgumentNullException.ThrowIfNull(serverKeys);
        IReadOnlyList<string> activeServers;
        var changed = false;
        lock (_syncRoot)
        {
            var state = GetOrCreateScope(scopeKey);
            foreach (var serverKey in serverKeys)
            {
                if (!string.IsNullOrWhiteSpace(serverKey))
                {
                    changed |= state.ActiveServers.Add(serverKey.Trim());
                }
            }

            activeServers = state.ActiveServers.OrderBy(static server => server, StringComparer.Ordinal).ToArray();
        }

        if (changed)
        {
            Changed?.Invoke(scopeKey);
        }

        return activeServers;
    }

    // Forgets the servers, and the tools of the servers, a session may no longer use (removed, or out of its space).
    public void RetainServers(string scopeKey, Func<string, bool> keep)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);
        ArgumentNullException.ThrowIfNull(keep);
        var changed = false;
        lock (_syncRoot)
        {
            if (!_scopes.TryGetValue(scopeKey, out var state))
            {
                return;
            }

            changed |= state.ActiveServers.RemoveWhere(server => !keep(server)) > 0;
            changed |= state.ActiveTools.RemoveWhere(tool => !keep(tool.Server)) > 0;
            foreach (var serverKey in state.ToolCounts.Keys.Where(server => !keep(server)).ToArray())
            {
                changed |= state.ToolCounts.Remove(serverKey);
            }
        }

        if (changed)
        {
            Changed?.Invoke(scopeKey);
        }
    }

    public void UpdateToolCounts(string scopeKey, IReadOnlyDictionary<string, int> counts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);
        ArgumentNullException.ThrowIfNull(counts);
        var changed = false;
        lock (_syncRoot)
        {
            var state = GetOrCreateScope(scopeKey);
            foreach (var serverKey in state.ActiveServers.Concat(state.ActiveTools.Select(static tool => tool.Server)).Distinct(StringComparer.Ordinal))
            {
                var count = counts.TryGetValue(serverKey, out var value) ? value : 0;
                if (!state.ToolCounts.TryGetValue(serverKey, out var previous) || previous != count)
                {
                    state.ToolCounts[serverKey] = count;
                    changed = true;
                }
            }
        }

        if (changed)
        {
            Changed?.Invoke(scopeKey);
        }
    }

    // The project folder the runs of a session start their servers for: its activations use the same one.
    public void SetProjectDirectory(string scopeKey, string? projectDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);
        lock (_syncRoot)
        {
            GetOrCreateScope(scopeKey).ProjectDirectory = string.IsNullOrWhiteSpace(projectDirectory) ? null : projectDirectory;
        }
    }

    public string? GetProjectDirectory(string scopeKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);
        lock (_syncRoot)
        {
            return _scopes.TryGetValue(scopeKey, out var state) ? state.ProjectDirectory : null;
        }
    }

    // Holds the entry a skill of a server was loaded from: its files are read against it until it is loaded again.
    public void HoldSkill(string scopeKey, McpHeldSkill held)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);
        ArgumentNullException.ThrowIfNull(held);
        lock (_syncRoot)
        {
            GetOrCreateScope(scopeKey).HeldSkills[(held.Server, held.Entry.Uri)] = held;
        }
    }

    // The skills of a server a session loaded, with the entries they were loaded from.
    public IReadOnlyList<McpHeldSkill> GetHeldSkills(string scopeKey, string server)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);
        lock (_syncRoot)
        {
            return _scopes.TryGetValue(scopeKey, out var state)
                ? state.HeldSkills.Values.Where(held => string.Equals(held.Server, server, StringComparison.Ordinal))
                    .OrderBy(static held => held.Entry.Uri, StringComparer.Ordinal).ToArray()
                : [];
        }
    }

    // Lets a skill go: it changed on its server, and is to be loaded again.
    public void ReleaseSkill(string scopeKey, string server, string uri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);
        lock (_syncRoot)
        {
            if (_scopes.TryGetValue(scopeKey, out var state)) state.HeldSkills.Remove((server, uri));
        }
    }

    public static string ResolveScopeKey(string? sessionId, string? projectPath)
    {
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            return "session:" + sessionId.Trim();
        }

        if (!string.IsNullOrWhiteSpace(projectPath))
        {
            return "project:" + Path.GetFullPath(projectPath.Trim());
        }

        return "global";
    }

    private McpActivationScopeState GetOrCreateScope(string scopeKey)
    {
        if (!_scopes.TryGetValue(scopeKey, out var state))
        {
            state = new McpActivationScopeState();
            _scopes.Add(scopeKey, state);
        }

        return state;
    }

    private sealed class McpActivationScopeState
    {
        public HashSet<string> ActiveServers { get; } = new(StringComparer.Ordinal);

        public HashSet<(string Server, string Tool)> ActiveTools { get; } = [];

        public Dictionary<string, int> ToolCounts { get; } = new(StringComparer.Ordinal);

        public string? ProjectDirectory { get; set; }

        public Dictionary<(string Server, string Uri), McpHeldSkill> HeldSkills { get; } = [];
    }
}

// A skill of a server a session loaded: the entry it was loaded from, against which its files are read, and the bytes
// read of it, which bound a skill whose server generates its files.
internal sealed class McpHeldSkill(string server, McpSkillEntry entry)
{
    private long _bytesRead;

    public string Server { get; } = server;

    public McpSkillEntry Entry { get; } = entry;

    public long BytesRead => Interlocked.Read(ref _bytesRead);

    // Counts bytes read of the skill; false, without counting them, when they would go over the limit of a skill.
    public bool TryCount(long bytes)
    {
        while (true)
        {
            var read = Interlocked.Read(ref _bytesRead);
            if (read + bytes > McpSkills.MaximumTotalBytes) return false;
            if (Interlocked.CompareExchange(ref _bytesRead, read + bytes, read) == read) return true;
        }
    }
}
