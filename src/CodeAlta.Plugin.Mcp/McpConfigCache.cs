namespace CodeAlta.Plugin.Mcp;

/// <summary>
/// The MCP configuration files and the MCP policy of <c>config.toml</c>, read again only when one of their files changed:
/// what asks for them often (every MCP tool call, every build of the prompt, the start of every run) does not read and
/// parse up to eight files each time.
/// </summary>
/// <remarks>
/// A file is known by its stamp, taken before it is read: whether it exists, its size and the time it was last written,
/// and for a file of CodeAlta whether its folder exists. Another stamp reads the files again at the next request, as
/// every request did before. A file written less than <see cref="SettleTime"/> ago is read again at each request until
/// it is older: on a file system that keeps coarse times, a second write of the same size within that time could keep
/// the same stamp. The plugin's own writes call <see cref="Invalidate"/>. What cannot be read or parsed is never kept:
/// the next request reads it again and fails as it did before. Thread-safe; what it returns is shared and never changed.
/// </remarks>
internal sealed class McpConfigCache
{
    /// <summary>How old the last write of a file must be for its stamp to tell a later write apart.</summary>
    internal static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(3);

    // A plugin that reads more projects than this starts its cache again: it does not grow without bound.
    private const int MaximumEntries = 64;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry<McpConfigSnapshot>> _configs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Entry<McpPolicyOptions>> _policies = new(StringComparer.Ordinal);
    private readonly McpConfigDiscovery _discovery = new();
    private readonly McpPolicyLoader _policyLoader;
    private readonly TimeProvider _time;
    // The servers given in memory for sessions, with every discovery.
    private IReadOnlyList<McpServerDefinition> _sessionServers = [];
    private long _generation;
    private int _configReads;
    private int _policyReads;

    public McpConfigCache()
        : this(TimeProvider.System)
    {
    }

    // The policy of the user's configuration as the host reads it (McpPluginOptions.GlobalConfigContent); null reads the file.
    public McpConfigCache(Func<string?>? globalConfigContent)
        : this(TimeProvider.System, globalConfigContent)
    {
    }

    internal McpConfigCache(TimeProvider time, Func<string?>? globalConfigContent = null)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
        _policyLoader = new McpPolicyLoader(globalConfigContent);
    }

    /// <summary>Gets the loader of the policy, which reads the user's configuration as the host does: what reads the policy without the cache uses it too.</summary>
    internal McpPolicyLoader PolicyLoader => _policyLoader;

    /// <summary>How many times the MCP configuration files were read.</summary>
    internal int ConfigReads => Volatile.Read(ref _configReads);

    /// <summary>How many times the policy files were read.</summary>
    internal int PolicyReads => Volatile.Read(ref _policyReads);

    /// <summary>
    /// Discovers the MCP servers as the runtime reads them: the files of CodeAlta are read whole, and nothing probes
    /// whether they can be written (the sources say they cannot).
    /// </summary>
    public McpConfigSnapshot Discover(string? projectDirectory, McpHome home)
    {
        var project = string.IsNullOrWhiteSpace(projectDirectory) ? null : Path.GetFullPath(projectDirectory);
        var files = McpConfigDiscovery.GetSourceFiles(project, home);
        var stamps = new FileStamp[files.Count];
        var now = _time.GetUtcNow().UtcDateTime;
        var settled = true;
        for (var index = 0; index < files.Count; index++)
        {
            stamps[index] = StampOf(files[index].Path, files[index].Origin == McpConfigOrigin.CodeAlta, now, ref settled);
        }

        return Get(_configs, string.Concat(project, "\n", home.Key), stamps, settled, () =>
        {
            Interlocked.Increment(ref _configReads);
            return _discovery.Discover(new McpConfigPathOptions
            {
                ProjectDirectory = project,
                Home = home,
                ProbeWritability = false,
                BoundedRead = false,
                SessionServers = SessionServers,
            });
        });
    }

    /// <summary>Gets the servers given in memory for sessions, which every discovery has.</summary>
    public IReadOnlyList<McpServerDefinition> SessionServers => Volatile.Read(ref _sessionServers);

    /// <summary>Adds servers given in memory for sessions, or replaces those of the same keys; the next discoveries have them.</summary>
    public void AddSessionServers(IReadOnlyList<McpServerDefinition> servers)
    {
        ArgumentNullException.ThrowIfNull(servers);
        lock (_gate)
        {
            var keys = servers.Select(static server => server.Key).ToHashSet(StringComparer.Ordinal);
            _sessionServers = [.. _sessionServers.Where(server => !keys.Contains(server.Key)), .. servers];
        }

        Invalidate();
    }

    /// <summary>Loads the MCP policy of the global and the project <c>config.toml</c>, as <see cref="McpPolicyLoader.Load"/> does.</summary>
    /// <exception cref="Tomlyn.TomlException">A file is not valid TOML.</exception>
    /// <exception cref="IOException">A file cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">A file cannot be read.</exception>
    public McpPolicyOptions LoadPolicy(string? globalPolicyPath, string? projectPolicyPath)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var settled = true;
        FileStamp[] stamps = [StampOf(globalPolicyPath, withFolder: false, now, ref settled), StampOf(projectPolicyPath, withFolder: false, now, ref settled)];
        return Get(_policies, string.Concat(globalPolicyPath, "\n", projectPolicyPath), stamps, settled, () =>
        {
            Interlocked.Increment(ref _policyReads);
            return _policyLoader.Load(globalPolicyPath, projectPolicyPath);
        });
    }

    /// <summary>Forgets what was read: the next request reads the files again, whatever their stamps say.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _generation++;
            _configs.Clear();
            _policies.Clear();
        }
    }

    private T Get<T>(Dictionary<string, Entry<T>> entries, string key, FileStamp[] stamps, bool settled, Func<T> read)
        where T : class
    {
        long generation;
        lock (_gate)
        {
            if (settled && entries.TryGetValue(key, out var entry) && entry.Stamps.AsSpan().SequenceEqual(stamps))
            {
                return entry.Value;
            }

            generation = _generation;
        }

        // Read outside the lock. What cannot be read or parsed throws here, and nothing is kept.
        var value = read();
        if (!settled)
        {
            return value;
        }

        lock (_gate)
        {
            // Invalidated while it was read: what was read may be a file before the plugin changed it.
            if (generation == _generation)
            {
                if (!entries.ContainsKey(key) && entries.Count >= MaximumEntries)
                {
                    entries.Clear();
                }

                entries[key] = new Entry<T>(stamps, value);
            }
        }

        return value;
    }

    // The stamp of a file, taken before it is read; a file whose state cannot be read, or that was written a moment ago, is
    // not settled: it is read again at the next request.
    private static FileStamp StampOf(string? path, bool withFolder, DateTime now, ref bool settled)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return default;
        }

        try
        {
            var folder = Path.GetDirectoryName(path);
            var folderExists = withFolder && !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder);
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return new FileStamp(false, 0, 0, folderExists);
            }

            var writtenAt = info.LastWriteTimeUtc;
            // A time in the future, from another clock or a file copied with its time, is not trusted either.
            if (writtenAt > now - SettleTime)
            {
                settled = false;
            }

            return new FileStamp(true, info.Length, writtenAt.Ticks, folderExists);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            settled = false;
            return default;
        }
    }

    private readonly record struct FileStamp(bool Exists, long Length, long WrittenAt, bool FolderExists);

    private sealed record Entry<T>(FileStamp[] Stamps, T Value);
}
