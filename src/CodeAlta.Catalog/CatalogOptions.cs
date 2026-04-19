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
    /// Gets the ACP root path under the global catalog.
    /// </summary>
    public string AcpRoot => Path.Combine(GlobalRoot, "acp");

    /// <summary>
    /// Gets the ACP registry cache root.
    /// </summary>
    public string AcpRegistryRoot => Path.Combine(AcpRoot, "registry");

    /// <summary>
    /// Gets the ACP download cache root.
    /// </summary>
    public string AcpDownloadsRoot => Path.Combine(AcpRoot, "downloads");

    /// <summary>
    /// Gets the ACP install root.
    /// </summary>
    public string AcpInstallsRoot => Path.Combine(AcpRoot, "installs");

    /// <summary>
    /// Gets the ACP manifest root.
    /// </summary>
    public string AcpManifestsRoot => Path.Combine(AcpRoot, "manifests");

    /// <summary>
    /// Gets the ACP state root.
    /// </summary>
    public string AcpStateRoot => Path.Combine(AcpRoot, "state");

    /// <summary>
    /// Gets the local runtime root path under the global catalog.
    /// </summary>
    [Obsolete("Use CacheRoot for caches or the dedicated roots such as SessionsRoot and PromptDraftsRoot.")]
    public string LocalRoot => CacheRoot;

    /// <summary>
    /// Gets the machine-local cache root.
    /// </summary>
    public string CacheRoot => Path.Combine(GetLegacyGlobalRoot(), "cache");

    /// <summary>
    /// Gets the session journals root path under the global catalog.
    /// </summary>
    public string SessionsRoot => Path.Combine(GlobalRoot, "sessions");

    /// <summary>
    /// Gets the saved prompt drafts root path under the global catalog.
    /// </summary>
    public string PromptDraftsRoot => Path.Combine(GlobalRoot, "saved_prompts");

    /// <summary>
    /// Gets the thread view state path.
    /// </summary>
    public string UiStatePath => Path.Combine(GlobalRoot, "ui-state.yaml");

    /// <summary>
    /// Gets the legacy machine-local runtime root path under the legacy catalog.
    /// </summary>
    [Obsolete("Use CacheRoot. The machine root path was renamed to cache.")]
    public string MachineRoot => CacheRoot;

    /// <summary>
    /// Gets the internal thread linkage root path under the global catalog.
    /// </summary>
    public string InternalThreadsRoot => Path.Combine(GlobalRoot, "threads", "internal");

    private string GetLegacyGlobalRoot()
    {
        var fullGlobalRoot = Path.GetFullPath(GlobalRoot);
        var parent = Path.GetDirectoryName(fullGlobalRoot);
        return string.Equals(
                Path.GetFileName(fullGlobalRoot),
                ".alta",
                StringComparison.OrdinalIgnoreCase) &&
               !string.IsNullOrWhiteSpace(parent)
            ? Path.Combine(parent, ".codealta")
            : Path.Combine(fullGlobalRoot, ".codealta");
    }
}
