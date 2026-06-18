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

    /// <summary>
    /// Gets the default checkout root path under the global catalog.
    /// </summary>
    public string CheckoutsRoot => Path.Combine(GlobalRoot, "checkouts");

    /// <summary>
    /// Gets the projects root path under the global catalog.
    /// </summary>
    public string ProjectsRoot => Path.Combine(GlobalRoot, "projects");

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
    /// Gets the shared machine-local SQLite cache database path.
    /// </summary>
    public string SessionCacheDatabasePath => Path.Combine(CacheRoot, "cache.sqlite3");

    /// <summary>
    /// Gets the session journals root path under the global catalog.
    /// </summary>
    public string SessionsRoot => Path.Combine(GlobalRoot, "sessions");

    /// <summary>
    /// Gets the saved prompt drafts root path under the global catalog.
    /// </summary>
    public string PromptDraftsRoot => Path.Combine(GlobalRoot, "saved_prompts");

    /// <summary>
    /// Gets the session view state path.
    /// </summary>
    public string UiStatePath => Path.Combine(GlobalRoot, "ui-state.yaml");

    /// <summary>
    /// Gets the legacy machine-agent runtime root path under the legacy catalog.
    /// </summary>
    [Obsolete("Use CacheRoot. The machine root path was renamed to cache.")]
    public string MachineRoot => CacheRoot;

    /// <summary>
    /// Gets the internal session linkage root path under the global catalog.
    /// </summary>
    // Compatibility: keep the persisted legacy threads/internal directory loadable.
    public string InternalSessionsRoot => Path.Combine(GlobalRoot, "threads", "internal");
}
