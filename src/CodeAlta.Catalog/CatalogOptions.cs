namespace CodeAlta.Catalog;

/// <summary>
/// Options describing the global CodeAlta catalog layout.
/// </summary>
public sealed class CatalogOptions
{
    /// <summary>
    /// Gets or sets the path to the portable CodeAlta root.
    /// </summary>
    public string GlobalRoot { get; set; } = string.Empty;

    private string? _stateRoot;

    /// <summary>
    /// Gets or sets the root of the state one running instance alone writes: sessions, the session cache,
    /// view state, prompt drafts and internal session links. When unset, it is <see cref="GlobalRoot"/>.
    /// </summary>
    /// <remarks>
    /// A second instance on the same <see cref="GlobalRoot"/> (the developer instance) sets a separate
    /// state root: it shares configuration, providers, prompts, skills and the project catalog, and keeps
    /// what two processes must not write together under this root.
    /// </remarks>
    public string StateRoot
    {
        get => string.IsNullOrWhiteSpace(_stateRoot) ? GlobalRoot : _stateRoot;
        set => _stateRoot = value;
    }

    /// <summary>
    /// Gets whether this instance keeps its state apart from <see cref="GlobalRoot"/>, and therefore leaves
    /// the files it shares with the instance that owns that root as they are at startup.
    /// </summary>
    public bool HasSeparateStateRoot => !string.IsNullOrWhiteSpace(_stateRoot)
        && !string.Equals(Path.GetFullPath(_stateRoot), Path.GetFullPath(GlobalRoot),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>
    /// Gets or sets whether the stores built from these options leave the shared files of <see cref="GlobalRoot"/> as they
    /// are, for a process that runs beside CodeAlta Desktop or TUI (a headless run): the project catalog
    /// (<see cref="ProjectCatalog.IsReadOnly"/>), the spaces (<see cref="SpaceCatalog.IsReadOnly"/>) and the global prompts
    /// (<see cref="PromptResourceStore.IsGlobalReadOnly"/>) refuse to write. Default false.
    /// </summary>
    public bool ReadOnlyGlobalState { get; set; }

    private CodeAltaConfigOptions _config = CodeAltaConfigOptions.Default;

    /// <summary>
    /// Gets or sets how the stores built from these options read and write the configuration of the user: the file as
    /// it is by default; overlays, a strict reading or no write for a process such as the command line.
    /// </summary>
    /// <exception cref="ArgumentNullException">The value set is <see langword="null"/>.</exception>
    public CodeAltaConfigOptions Config
    {
        get => _config;
        set => _config = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// Gets the default checkout root path under the global catalog.
    /// </summary>
    public string CheckoutsRoot => Path.Combine(GlobalRoot, "checkouts");

    /// <summary>
    /// Gets the folder the git worktrees CodeAlta creates go to by default: one folder for each project, and in
    /// it one folder for each worktree.
    /// </summary>
    public string WorktreesRoot => Path.Combine(GlobalRoot, "worktrees");

    /// <summary>
    /// Gets the projects root path under the global catalog.
    /// </summary>
    public string ProjectsRoot => Path.Combine(GlobalRoot, "projects");

    /// <summary>
    /// Gets the folder of the spaces under the global catalog: one Markdown file for each space. The
    /// projects of a space are named by the project files, not by this folder.
    /// </summary>
    public string SpacesRoot => Path.Combine(GlobalRoot, "spaces");

    /// <summary>
    /// Gets the folder of the labels under the global catalog: one Markdown file for each label. The labels of a
    /// project are named by the project file, not by this folder.
    /// </summary>
    public string LabelsRoot => Path.Combine(GlobalRoot, "labels");

    /// <summary>
    /// Gets the machine configuration root path under the global catalog.
    /// </summary>
    public string MachinesRoot => Path.Combine(GlobalRoot, "machines");

    /// <summary>
    /// Gets the agents root path under the global catalog.
    /// </summary>
    public string AgentsRoot => Path.Combine(GlobalRoot, "agents");

    /// <summary>
    /// Gets the global user configuration path.
    /// </summary>
    public string ConfigPath => Path.Combine(GlobalRoot, "config.toml");

    /// <summary>
    /// Gets the agent runtime root path under the global catalog.
    /// </summary>
    [Obsolete("Use CacheRoot for caches or the dedicated roots such as SessionsRoot and PromptDraftsRoot.")]
    public string LocalRoot => CacheRoot;

    /// <summary>
    /// Gets the machine-local cache root.
    /// </summary>
    public string CacheRoot => Path.Combine(GlobalRoot, "cache");

    /// <summary>
    /// Gets the folder of speech to prompt under <see cref="CacheRoot"/>: the speech models and the engine that
    /// runs them, downloaded on first use and shared by the normal and the developer instance.
    /// </summary>
    public string SpeechRoot => Path.Combine(CacheRoot, "speech");

    /// <summary>
    /// Gets the folder of the native runtimes under <see cref="CacheRoot"/>, one folder per runtime, version and
    /// platform (<c>sherpa-onnx/&lt;version&gt;/&lt;rid&gt;</c>), shared by speech and search and by the normal and the
    /// developer instance.
    /// </summary>
    public string RuntimeRoot => Path.Combine(CacheRoot, "runtime");

    /// <summary>
    /// Gets the folder of the Claude Code CLI CodeAlta pins under <see cref="CacheRoot"/>: one folder per version and
    /// platform, downloaded on first use and shared by the normal and the developer instance.
    /// </summary>
    public string ClaudeCodeRoot => Path.Combine(CacheRoot, "claude-code");

    /// <summary>
    /// Gets the path of the SQLite database of the application, under <see cref="StateRoot"/>: the one file of
    /// this instance that holds the list of sessions and the tables of the plugins.
    /// </summary>
    public string ApplicationDatabasePath => Path.Combine(StateRoot, "data", "alta.sqlite3");

    /// <summary>
    /// Gets the folder of the copies of the application database, beside it.
    /// </summary>
    public string ApplicationDatabaseBackupRoot => Path.Combine(StateRoot, "data", "backups");

    /// <summary>
    /// Gets the path of the search index of this instance, beside the application database under <see cref="StateRoot"/>:
    /// a file that can be deleted and is built again from the journals and files, and is never backed up.
    /// </summary>
    public string SearchDatabasePath => Path.Combine(StateRoot, "data", "search.sqlite3");

    /// <summary>
    /// Gets the folder of search in the shared cache under <see cref="CacheRoot"/>: the embedding models, downloaded
    /// when search by meaning is turned on and shared by the normal and the developer instance.
    /// </summary>
    public string SearchCacheRoot => Path.Combine(CacheRoot, "search");

    /// <summary>
    /// Gets the path the session list database had before the application database existed, under
    /// <see cref="StateRoot"/>. The application database takes the file over from there at its first start.
    /// </summary>
    public string LegacySessionCacheDatabasePath => Path.Combine(StateRoot, "cache", "cache.sqlite3");

    /// <summary>
    /// Gets the path the session list database had before the application database existed.
    /// </summary>
    [Obsolete("The database is no longer a cache: use ApplicationDatabasePath, or LegacySessionCacheDatabasePath for the file it replaced.", error: false)]
    public string SessionCacheDatabasePath => LegacySessionCacheDatabasePath;

    /// <summary>
    /// Gets the session journals root path, under <see cref="StateRoot"/>.
    /// </summary>
    public string SessionsRoot => Path.Combine(StateRoot, "sessions");

    /// <summary>
    /// Gets the folder of the session leases, under <see cref="SessionsRoot"/>: one <c>&lt;session-id&gt;.lease</c> for each
    /// session a process runs (<see cref="SessionLeaseStore"/>).
    /// </summary>
    public string SessionLeasesRoot => Path.Combine(SessionsRoot, CodeAlta.Agent.Runtime.AgentRuntimePathLayout.SessionLeasesFolderName);

    /// <summary>
    /// Gets the folder of the session markers, under <see cref="SessionsRoot"/>: one <c>&lt;session-id&gt;.json</c> for each
    /// session another process wrote and the instance has still to list (<see cref="SessionMarkerStore"/>).
    /// </summary>
    public string SessionMarkersRoot => Path.Combine(SessionsRoot, CodeAlta.Agent.Runtime.AgentRuntimePathLayout.SessionMarkersFolderName);

    /// <summary>
    /// Gets the saved prompt drafts root path, under <see cref="StateRoot"/>.
    /// </summary>
    public string PromptDraftsRoot => Path.Combine(StateRoot, "saved_prompts");

    /// <summary>
    /// Gets the session view state path, under <see cref="StateRoot"/>.
    /// </summary>
    public string UiStatePath => Path.Combine(StateRoot, "ui-state.yaml");

    /// <summary>
    /// Gets the legacy machine-agent runtime root path under the legacy catalog.
    /// </summary>
    [Obsolete("Use CacheRoot. The machine root path was renamed to cache.")]
    public string MachineRoot => CacheRoot;

    /// <summary>
    /// Gets the internal session linkage root path under the global catalog.
    /// </summary>
    // Compatibility: keep the persisted legacy threads/internal directory loadable.
    public string InternalSessionsRoot => Path.Combine(StateRoot, "threads", "internal");
}
