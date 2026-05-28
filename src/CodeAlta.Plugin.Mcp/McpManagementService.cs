namespace CodeAlta.Plugin.Mcp;

/// <summary>
/// Describes the MCP configuration scope used by management snapshots.
/// </summary>
public enum McpManagementScope
{
    /// <summary>Global user configuration.</summary>
    Global,

    /// <summary>Project-local configuration.</summary>
    Project,
}

/// <summary>
/// Describes the detected MCP JSON configuration format.
/// </summary>
public enum McpManagementConfigFormat
{
    /// <summary>CodeAlta-native <c>mcpServers</c> format.</summary>
    CodeAlta,

    /// <summary>GitHub Copilot MCP configuration format.</summary>
    Copilot,

    /// <summary>Visual Studio Code MCP configuration format.</summary>
    Vscode,

    /// <summary>Claude MCP configuration format.</summary>
    Claude,

    /// <summary>IntelliJ MCP configuration format.</summary>
    Intellij,
}

/// <summary>
/// Describes an MCP server transport in a management snapshot.
/// </summary>
public enum McpManagementTransport
{
    /// <summary>Standard-input/standard-output process transport.</summary>
    Stdio,

    /// <summary>HTTP, Streamable HTTP, or SSE transport.</summary>
    Http,
}

/// <summary>
/// Describes the management state of an MCP server or configuration row.
/// </summary>
public enum McpManagementServerState
{
    /// <summary>The server is configured and enabled by policy.</summary>
    Configured,

    /// <summary>The server is configured but disabled by policy.</summary>
    Disabled,

    /// <summary>The fixed configuration file for a scope is missing.</summary>
    MissingConfig,

    /// <summary>A configuration source exists but could not be parsed or normalized.</summary>
    InvalidConfig,

    /// <summary>A global server definition is shadowed by a project definition with the same key.</summary>
    Shadowed,
}

/// <summary>
/// Describes the latest dialog/server accessibility test result.
/// </summary>
public enum McpManagementTestStatus
{
    /// <summary>No test has run for this snapshot row.</summary>
    NotRun,

    /// <summary>The server connected and listed tools successfully.</summary>
    Succeeded,

    /// <summary>The server uses a transport whose runtime support is unavailable.</summary>
    Unsupported,

    /// <summary>The server did not finish startup or tool discovery before the configured timeout.</summary>
    TimedOut,

    /// <summary>The test was canceled by the caller.</summary>
    Canceled,

    /// <summary>The server test failed before tools could be listed.</summary>
    Failed,
}

/// <summary>
/// Supplies inputs for an MCP management snapshot refresh.
/// </summary>
public sealed record McpManagementRequest
{
    /// <summary>Gets the project directory whose <c>.alta</c> configuration should be included.</summary>
    public string? ProjectDirectory { get; init; }

    /// <summary>Gets the user home directory override, primarily for tests.</summary>
    public string? UserHomeDirectory { get; init; }
}

/// <summary>
/// Describes one fixed MCP JSON configuration source.
/// </summary>
public sealed record McpManagementConfigSourceSnapshot
{
    /// <summary>Gets the source scope.</summary>
    public required McpManagementScope Scope { get; init; }

    /// <summary>Gets the fixed file path for the source.</summary>
    public required string Path { get; init; }

    /// <summary>Gets a value indicating whether the file exists.</summary>
    public bool Exists { get; init; }

    /// <summary>Gets a value indicating whether the parent directory exists.</summary>
    public bool DirectoryExists { get; init; }

    /// <summary>Gets a value indicating whether the source is currently writable or creatable.</summary>
    public bool IsWritable { get; init; }

    /// <summary>Gets the detected JSON format, when a valid file exists.</summary>
    public McpManagementConfigFormat? Format { get; init; }

    /// <summary>Gets the root server collection key, when detected.</summary>
    public string? RootKey { get; init; }

    /// <summary>Gets a value indicating whether the source parsed successfully.</summary>
    public bool IsValid { get; init; } = true;

    /// <summary>Gets a redacted diagnostic for invalid or inaccessible configuration.</summary>
    public string? Diagnostic { get; init; }

    /// <summary>Gets configured server keys found in this source.</summary>
    public IReadOnlyList<string> ServerKeys { get; init; } = [];
}

/// <summary>
/// Describes merged MCP TOML policy state for management UI.
/// </summary>
public sealed record McpManagementPolicySnapshot
{
    /// <summary>Gets the global policy path.</summary>
    public required string GlobalPath { get; init; }

    /// <summary>Gets the project policy path, when a project is selected.</summary>
    public string? ProjectPath { get; init; }

    /// <summary>Gets a value indicating whether MCP support is enabled by merged policy.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Gets a value indicating whether servers should connect on startup when runtime support exists.</summary>
    public bool ConnectOnStartup { get; init; } = true;

    /// <summary>Gets the configured startup timeout in milliseconds.</summary>
    public int StartupTimeoutMs { get; init; }

    /// <summary>Gets the configured tool-call timeout in milliseconds.</summary>
    public int ToolTimeoutMs { get; init; }

    /// <summary>Gets the maximum tool output character budget.</summary>
    public int MaxToolOutputChars { get; init; }

    /// <summary>Gets a value indicating whether MCP discovery appears in prompt guidance.</summary>
    public bool DiscoverInPrompt { get; init; }

    /// <summary>Gets the maximum server rows to include in prompt guidance.</summary>
    public int PromptMaxServers { get; init; } = 10;

    /// <summary>Gets the maximum per-server tool names to include in prompt guidance.</summary>
    public int PromptMaxTools { get; init; } = 20;

    /// <summary>Gets the direct exposure policy value.</summary>
    public string DirectExposure { get; init; } = "auto";

    /// <summary>Gets the preferred write scope policy value.</summary>
    public string PreferredWriteScope { get; init; } = "project";

    /// <summary>Gets a diagnostic when policy could not be loaded.</summary>
    public string? Diagnostic { get; init; }
}

/// <summary>
/// Describes one MCP tool row discovered for management UI display.
/// </summary>
public sealed record McpManagementToolSnapshot
{
    /// <summary>Gets the raw MCP tool name used for protocol calls.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the optional MCP tool title.</summary>
    public string? Title { get; init; }

    /// <summary>Gets the optional MCP tool description.</summary>
    public string? Description { get; init; }

    /// <summary>Gets the CodeAlta-qualified routing alias.</summary>
    public required string Alias { get; init; }

    /// <summary>Gets a value indicating whether the tool is enabled by current policy.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Gets the latest availability label for the management table.</summary>
    public required string Availability { get; init; }

    /// <summary>Gets the policy or runtime diagnostic explaining unavailable state, when present.</summary>
    public string? Diagnostic { get; init; }
}

/// <summary>
/// Describes one MCP server or configuration state row for management UI.
/// </summary>
public sealed record McpManagementServerSnapshot
{
    /// <summary>Gets the server key or synthetic configuration row key.</summary>
    public required string Key { get; init; }

    /// <summary>Gets the display name for the row.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Gets the row state.</summary>
    public required McpManagementServerState State { get; init; }

    /// <summary>Gets a short state reason.</summary>
    public string? StateReason { get; init; }

    /// <summary>Gets the transport kind for configured servers.</summary>
    public McpManagementTransport? Transport { get; init; }

    /// <summary>Gets the source scope.</summary>
    public McpManagementScope? SourceScope { get; init; }

    /// <summary>Gets the source JSON path.</summary>
    public string? SourcePath { get; init; }

    /// <summary>Gets the source JSON format.</summary>
    public McpManagementConfigFormat? SourceFormat { get; init; }

    /// <summary>Gets a value indicating whether this project server overrides a global server.</summary>
    public bool OverridesGlobal { get; init; }

    /// <summary>Gets the shadowed global source path, when present.</summary>
    public string? ShadowedGlobalPath { get; init; }

    /// <summary>Gets the redacted stdio command.</summary>
    public string? Command { get; init; }

    /// <summary>Gets redacted stdio arguments.</summary>
    public IReadOnlyList<string> Args { get; init; } = [];

    /// <summary>Gets the configured working directory.</summary>
    public string? Cwd { get; init; }

    /// <summary>Gets redacted environment variables.</summary>
    public IReadOnlyDictionary<string, string> Env { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Gets the redacted remote endpoint URL.</summary>
    public string? Url { get; init; }

    /// <summary>Gets redacted remote headers.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Gets whether the server is enabled by effective policy.</summary>
    public bool? PolicyEnabled { get; init; }

    /// <summary>Gets whether the server is required by policy.</summary>
    public bool? PolicyRequired { get; init; }

    /// <summary>Gets the server direct exposure policy.</summary>
    public string? DirectExposure { get; init; }

    /// <summary>Gets directly exposed tool names from policy.</summary>
    public IReadOnlyList<string> DirectTools { get; init; } = [];

    /// <summary>Gets allowed tool names from policy.</summary>
    public IReadOnlyList<string> AllowedTools { get; init; } = [];

    /// <summary>Gets disabled tool names from policy.</summary>
    public IReadOnlyList<string> DisabledTools { get; init; } = [];

    /// <summary>Gets the server startup timeout override.</summary>
    public int? StartupTimeoutMs { get; init; }

    /// <summary>Gets the server tool timeout override.</summary>
    public int? ToolTimeoutMs { get; init; }

    /// <summary>Gets the cached exposed tool count. This remains zero until a runtime test updates the cached snapshot.</summary>
    public int ExposedToolCount { get; init; }

    /// <summary>Gets the cached total tool count. This remains zero until a runtime test updates the cached snapshot.</summary>
    public int TotalToolCount { get; init; }

    /// <summary>Gets discovered tool rows for the most recent server test in this snapshot.</summary>
    public IReadOnlyList<McpManagementToolSnapshot> Tools { get; init; } = [];

    /// <summary>Gets the latest server accessibility test status.</summary>
    public McpManagementTestStatus LastTestStatus { get; init; } = McpManagementTestStatus.NotRun;

    /// <summary>Gets the latest server accessibility test completion time, when available.</summary>
    public DateTimeOffset? LastTestedAt { get; init; }

    /// <summary>Gets row diagnostics.</summary>
    public IReadOnlyList<string> Diagnostics { get; init; } = [];
}

/// <summary>
/// Summarizes an MCP management snapshot.
/// </summary>
public sealed record McpManagementSummary
{
    /// <summary>Gets a value indicating whether any MCP JSON or TOML configuration exists.</summary>
    public bool HasConfiguration { get; init; }

    /// <summary>Gets the number of effective configured servers.</summary>
    public int ConfiguredServerCount { get; init; }

    /// <summary>Gets the number of effective configured servers enabled by policy.</summary>
    public int ActiveServerCount { get; init; }

    /// <summary>Gets the number of disabled or invalid server/configuration rows.</summary>
    public int UnavailableServerCount { get; init; }

    /// <summary>Gets the number of invalid JSON sources.</summary>
    public int InvalidSourceCount { get; init; }

    /// <summary>Gets the number of missing fixed JSON sources.</summary>
    public int MissingSourceCount { get; init; }

    /// <summary>Gets the number of shadowed global server definitions.</summary>
    public int ShadowedServerCount { get; init; }

    /// <summary>Gets the cached exposed tool count. This remains zero until a runtime test updates the cached snapshot.</summary>
    public int ExposedToolCount { get; init; }

    /// <summary>Gets the cached total tool count. This remains zero until a runtime test updates the cached snapshot.</summary>
    public int TotalToolCount { get; init; }
}

/// <summary>
/// Describes an editable MCP server JSON definition for management UI saves.
/// </summary>
public sealed record McpManagementServerEdit
{
    /// <summary>Gets the server key.</summary>
    public required string Key { get; init; }

    /// <summary>Gets the server transport.</summary>
    public required McpManagementTransport Transport { get; init; }

    /// <summary>Gets the stdio command for <see cref="McpManagementTransport.Stdio" /> servers.</summary>
    public string? Command { get; init; }

    /// <summary>Gets stdio arguments for <see cref="McpManagementTransport.Stdio" /> servers.</summary>
    public IReadOnlyList<string> Args { get; init; } = [];

    /// <summary>Gets the stdio working directory for <see cref="McpManagementTransport.Stdio" /> servers.</summary>
    public string? Cwd { get; init; }

    /// <summary>Gets stdio environment variables for <see cref="McpManagementTransport.Stdio" /> servers.</summary>
    public IReadOnlyDictionary<string, string> Env { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Gets the HTTP/SSE endpoint URL for <see cref="McpManagementTransport.Http" /> servers.</summary>
    public string? Url { get; init; }

    /// <summary>Gets HTTP headers for <see cref="McpManagementTransport.Http" /> servers.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// Describes an MCP JSON configuration mutation result.
/// </summary>
public sealed record McpManagementConfigMutationResult
{
    /// <summary>Gets the written path.</summary>
    public required string Path { get; init; }

    /// <summary>Gets the written scope.</summary>
    public required McpManagementScope Scope { get; init; }

    /// <summary>Gets the server key.</summary>
    public required string Server { get; init; }

    /// <summary>Gets the JSON config flavor that was written.</summary>
    public required string Format { get; init; }

    /// <summary>Gets a value indicating whether the file was created.</summary>
    public bool CreatedFile { get; init; }

    /// <summary>Gets a value indicating whether content changed.</summary>
    public bool Changed { get; init; }
}

/// <summary>
/// Captures MCP configuration and policy state for management UI and command surfaces without connecting to servers.
/// </summary>
public sealed record McpManagementSnapshot
{
    /// <summary>Gets the selected project directory, when available.</summary>
    public string? ProjectDirectory { get; init; }

    /// <summary>Gets the fixed JSON configuration sources.</summary>
    public IReadOnlyList<McpManagementConfigSourceSnapshot> Sources { get; init; } = [];

    /// <summary>Gets server and configuration rows.</summary>
    public IReadOnlyList<McpManagementServerSnapshot> Servers { get; init; } = [];

    /// <summary>Gets merged policy state.</summary>
    public required McpManagementPolicySnapshot Policy { get; init; }

    /// <summary>Gets the default write scope for new JSON servers.</summary>
    public McpManagementScope DefaultWriteScope { get; init; }

    /// <summary>Gets summary counts.</summary>
    public required McpManagementSummary Summary { get; init; }

    /// <summary>Gets the snapshot creation time.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Describes a management service mutation result.
/// </summary>
public sealed record McpManagementMutationResult
{
    /// <summary>Gets the written path.</summary>
    public required string Path { get; init; }

    /// <summary>Gets the written scope.</summary>
    public required McpManagementScope Scope { get; init; }

    /// <summary>Gets the server key.</summary>
    public required string Server { get; init; }

    /// <summary>Gets whether the server was enabled.</summary>
    public required bool Enabled { get; init; }

    /// <summary>Gets a value indicating whether the file was created.</summary>
    public bool CreatedFile { get; init; }

    /// <summary>Gets a value indicating whether content changed.</summary>
    public bool Changed { get; init; }
}

/// <summary>
/// Describes the result of testing one MCP server from the management service.
/// </summary>
public sealed record McpManagementServerTestResult
{
    /// <summary>Gets the tested MCP server key.</summary>
    public required string Server { get; init; }

    /// <summary>Gets the test status.</summary>
    public required McpManagementTestStatus Status { get; init; }

    /// <summary>Gets a value indicating whether the server connected and listed tools.</summary>
    public bool Succeeded => Status == McpManagementTestStatus.Succeeded;

    /// <summary>Gets discovered tool rows.</summary>
    public IReadOnlyList<McpManagementToolSnapshot> Tools { get; init; } = [];

    /// <summary>Gets redacted test diagnostics.</summary>
    public IReadOnlyList<string> Diagnostics { get; init; } = [];

    /// <summary>Gets the test completion time.</summary>
    public DateTimeOffset CompletedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Describes a per-tool enablement policy mutation result.
/// </summary>
public sealed record McpManagementToolMutationResult
{
    /// <summary>Gets the written path.</summary>
    public required string Path { get; init; }

    /// <summary>Gets the written scope.</summary>
    public required McpManagementScope Scope { get; init; }

    /// <summary>Gets the server key.</summary>
    public required string Server { get; init; }

    /// <summary>Gets the raw MCP tool name.</summary>
    public required string Tool { get; init; }

    /// <summary>Gets whether the tool was enabled.</summary>
    public required bool Enabled { get; init; }

    /// <summary>Gets the disabled tool names remaining in the written policy scope.</summary>
    public IReadOnlyList<string> DisabledTools { get; init; } = [];

    /// <summary>Gets a value indicating whether the policy file was created.</summary>
    public bool CreatedFile { get; init; }

    /// <summary>Gets a value indicating whether content changed.</summary>
    public bool Changed { get; init; }
}

/// <summary>
/// Builds cached MCP management snapshots, applies focused policy mutations, and runs explicit server tests on request.
/// </summary>
public sealed class McpManagementService
{
    private readonly object _syncRoot = new();
    private readonly McpConfigDiscovery _discovery = new();
    private readonly McpConfigWriter _configWriter = new();
    private readonly McpPolicyLoader _policyLoader = new();
    private readonly McpPolicyWriter _policyWriter = new();
    private McpManagementSnapshot? _cachedSnapshot;
    private McpManagementRequest _lastRequest = new();

    /// <summary>Gets the last refreshed snapshot, or <see langword="null" /> when none has been loaded.</summary>
    public McpManagementSnapshot? CachedSnapshot
    {
        get
        {
            lock (_syncRoot)
            {
                return _cachedSnapshot;
            }
        }
    }

    /// <summary>
    /// Refreshes the management snapshot from fixed MCP JSON and TOML policy files.
    /// </summary>
    /// <param name="request">The refresh request.</param>
    /// <returns>The refreshed snapshot.</returns>
    public McpManagementSnapshot RefreshSnapshot(McpManagementRequest? request = null)
    {
        request ??= new McpManagementRequest();
        var projectDirectory = NormalizeProjectDirectory(request.ProjectDirectory);
        var configSnapshot = _discovery.Discover(new McpConfigPathOptions
        {
            ProjectDirectory = projectDirectory,
            UserHomeDirectory = request.UserHomeDirectory,
        });
        var globalPolicyPath = McpPolicyWriter.GetGlobalPolicyPath(request.UserHomeDirectory);
        var projectPolicyPath = projectDirectory is null ? null : McpPolicyWriter.GetProjectPolicyPath(projectDirectory);
        var (policy, policyDiagnostic) = LoadPolicy(globalPolicyPath, projectPolicyPath);
        var sources = configSnapshot.Sources.Select(MapSource).ToArray();
        var servers = BuildServers(configSnapshot, policy, sources);
        var snapshot = new McpManagementSnapshot
        {
            ProjectDirectory = projectDirectory,
            Sources = sources,
            Servers = servers,
            Policy = MapPolicy(policy, globalPolicyPath, projectPolicyPath, policyDiagnostic),
            DefaultWriteScope = MapScope(configSnapshot.DefaultWriteScope),
            Summary = BuildSummary(configSnapshot, sources, servers, globalPolicyPath, projectPolicyPath),
            CreatedAt = DateTimeOffset.UtcNow,
        };

        lock (_syncRoot)
        {
            _lastRequest = request with { ProjectDirectory = projectDirectory };
            _cachedSnapshot = snapshot;
        }

        return snapshot;
    }

    /// <summary>
    /// Adds or updates one MCP server JSON definition in the selected configuration scope and refreshes the cached snapshot.
    /// </summary>
    /// <param name="definition">The editable server definition to save.</param>
    /// <param name="scope">The JSON configuration scope to write, or <see langword="null" /> to use the default.</param>
    /// <param name="originalKey">The previous server key when renaming or moving an existing server.</param>
    /// <param name="originalScope">The previous JSON configuration scope when renaming or moving an existing server.</param>
    /// <param name="request">Optional path request used to resolve scope paths.</param>
    /// <param name="cancellationToken">A token that cancels the write.</param>
    /// <returns>The mutation result.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="definition" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException">Thrown when the definition is invalid or project scope has no project directory.</exception>
    /// <exception cref="IOException">Thrown when the JSON configuration file cannot be written.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when the JSON configuration file cannot be written.</exception>
    public async Task<McpManagementConfigMutationResult> AddOrUpdateServerAsync(
        McpManagementServerEdit definition,
        McpManagementScope? scope = null,
        string? originalKey = null,
        McpManagementScope? originalScope = null,
        McpManagementRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        request ??= CachedSnapshot is null ? _lastRequest : _lastRequest with { ProjectDirectory = CachedSnapshot.ProjectDirectory };
        var projectDirectory = NormalizeProjectDirectory(request.ProjectDirectory);
        var targetScope = ResolveWriteScope(scope, projectDirectory, nameof(scope));
        var targetPath = GetJsonConfigPath(targetScope, projectDirectory, request.UserHomeDirectory);
        var normalized = ToServerDefinition(definition, targetScope, targetPath);
        var result = await _configWriter.AddOrUpdateServerAsync(targetPath, MapScope(targetScope), normalized, cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(originalKey) &&
            (!string.Equals(originalKey.Trim(), normalized.Key, StringComparison.Ordinal) || originalScope != targetScope))
        {
            var sourceScope = originalScope ?? targetScope;
            var sourcePath = GetJsonConfigPath(sourceScope, projectDirectory, request.UserHomeDirectory);
            await _configWriter.RemoveServerAsync(sourcePath, MapScope(sourceScope), originalKey.Trim(), cancellationToken).ConfigureAwait(false);
        }

        RefreshSnapshot(new McpManagementRequest { ProjectDirectory = projectDirectory, UserHomeDirectory = request.UserHomeDirectory });
        return new McpManagementConfigMutationResult
        {
            Path = result.Path,
            Scope = MapScope(result.Scope),
            Server = normalized.Key,
            Format = FormatFlavor(result.Flavor),
            CreatedFile = result.CreatedFile,
            Changed = result.Changed,
        };
    }

    /// <summary>
    /// Removes one MCP server JSON definition from the selected configuration scope and refreshes the cached snapshot.
    /// </summary>
    /// <param name="serverKey">The server key to remove.</param>
    /// <param name="scope">The JSON configuration scope to write, or <see langword="null" /> to use the default.</param>
    /// <param name="request">Optional path request used to resolve scope paths.</param>
    /// <param name="cancellationToken">A token that cancels the write.</param>
    /// <returns>The mutation result.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="serverKey" /> is empty or project scope has no project directory.</exception>
    /// <exception cref="IOException">Thrown when the JSON configuration file cannot be written.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when the JSON configuration file cannot be written.</exception>
    public async Task<McpManagementConfigMutationResult> RemoveServerAsync(
        string serverKey,
        McpManagementScope? scope = null,
        McpManagementRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverKey);
        request ??= CachedSnapshot is null ? _lastRequest : _lastRequest with { ProjectDirectory = CachedSnapshot.ProjectDirectory };
        var projectDirectory = NormalizeProjectDirectory(request.ProjectDirectory);
        var targetScope = ResolveWriteScope(scope, projectDirectory, nameof(scope));
        var targetPath = GetJsonConfigPath(targetScope, projectDirectory, request.UserHomeDirectory);
        var result = await _configWriter.RemoveServerAsync(targetPath, MapScope(targetScope), serverKey.Trim(), cancellationToken).ConfigureAwait(false);
        RefreshSnapshot(new McpManagementRequest { ProjectDirectory = projectDirectory, UserHomeDirectory = request.UserHomeDirectory });
        return new McpManagementConfigMutationResult
        {
            Path = result.Path,
            Scope = MapScope(result.Scope),
            Server = serverKey.Trim(),
            Format = FormatFlavor(result.Flavor),
            CreatedFile = result.CreatedFile,
            Changed = result.Changed,
        };
    }

    /// <summary>
    /// Sets a server enabled flag in the selected policy scope and refreshes the cached snapshot.
    /// </summary>
    /// <param name="serverKey">The server key.</param>
    /// <param name="enabled">Whether the server should be enabled.</param>
    /// <param name="scope">The policy scope to write, or <see langword="null" /> to use the default.</param>
    /// <param name="request">Optional path request used to resolve scope paths.</param>
    /// <param name="cancellationToken">A token that cancels the write.</param>
    /// <returns>The mutation result.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="serverKey" /> is empty or project scope has no project directory.</exception>
    /// <exception cref="IOException">Thrown when the policy file cannot be written.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when the policy file cannot be written.</exception>
    public async Task<McpManagementMutationResult> SetServerEnabledAsync(
        string serverKey,
        bool enabled,
        McpManagementScope? scope = null,
        McpManagementRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverKey);
        request ??= CachedSnapshot is null ? _lastRequest : new McpManagementRequest
        {
            ProjectDirectory = CachedSnapshot.ProjectDirectory,
        };
        var projectDirectory = NormalizeProjectDirectory(request.ProjectDirectory);
        var targetScope = scope ?? (projectDirectory is null ? McpManagementScope.Global : McpManagementScope.Project);
        if (targetScope == McpManagementScope.Project && projectDirectory is null)
        {
            throw new ArgumentException("Project policy scope requires a project directory.", nameof(scope));
        }

        var path = targetScope == McpManagementScope.Project
            ? McpPolicyWriter.GetProjectPolicyPath(projectDirectory!)
            : McpPolicyWriter.GetGlobalPolicyPath(request.UserHomeDirectory);
        var result = await _policyWriter.SetServerEnabledAsync(path, MapScope(targetScope), serverKey.Trim(), enabled, cancellationToken).ConfigureAwait(false);
        RefreshSnapshot(new McpManagementRequest { ProjectDirectory = projectDirectory, UserHomeDirectory = request.UserHomeDirectory });
        return new McpManagementMutationResult
        {
            Path = result.Path,
            Scope = MapScope(result.Scope),
            Server = result.Server,
            Enabled = result.Enabled,
            CreatedFile = result.CreatedFile,
            Changed = result.Changed,
        };
    }

    /// <summary>
    /// Sets a tool enabled flag in the selected policy scope by writing or removing a <c>disabled_tools</c> entry.
    /// </summary>
    /// <param name="serverKey">The server key.</param>
    /// <param name="toolName">The raw MCP tool name.</param>
    /// <param name="enabled">Whether the tool should be enabled.</param>
    /// <param name="scope">The policy scope to write, or <see langword="null" /> to use the default.</param>
    /// <param name="request">Optional path request used to resolve scope paths.</param>
    /// <param name="cancellationToken">A token that cancels the write.</param>
    /// <returns>The mutation result.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="serverKey" /> or <paramref name="toolName" /> is empty, or project scope has no project directory.</exception>
    /// <exception cref="IOException">Thrown when the policy file cannot be written.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when the policy file cannot be written.</exception>
    public async Task<McpManagementToolMutationResult> SetToolEnabledAsync(
        string serverKey,
        string toolName,
        bool enabled,
        McpManagementScope? scope = null,
        McpManagementRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        request ??= CachedSnapshot is null ? _lastRequest : new McpManagementRequest
        {
            ProjectDirectory = CachedSnapshot.ProjectDirectory,
        };
        var projectDirectory = NormalizeProjectDirectory(request.ProjectDirectory);
        var targetScope = scope ?? (projectDirectory is null ? McpManagementScope.Global : McpManagementScope.Project);
        if (targetScope == McpManagementScope.Project && projectDirectory is null)
        {
            throw new ArgumentException("Project policy scope requires a project directory.", nameof(scope));
        }

        var path = targetScope == McpManagementScope.Project
            ? McpPolicyWriter.GetProjectPolicyPath(projectDirectory!)
            : McpPolicyWriter.GetGlobalPolicyPath(request.UserHomeDirectory);
        var previousTools = GetCachedTools(serverKey.Trim());
        var previousStatus = GetCachedTestStatus(serverKey.Trim());
        var previousTestedAt = GetCachedTestedAt(serverKey.Trim());
        var result = await _policyWriter.SetToolEnabledAsync(path, MapScope(targetScope), serverKey.Trim(), toolName.Trim(), enabled, cancellationToken).ConfigureAwait(false);
        var snapshot = RefreshSnapshot(new McpManagementRequest { ProjectDirectory = projectDirectory, UserHomeDirectory = request.UserHomeDirectory });
        if (previousTools.Count > 0)
        {
            UpdateCachedSnapshotWithTools(serverKey.Trim(), previousTools, previousStatus, previousTestedAt);
        }

        return new McpManagementToolMutationResult
        {
            Path = result.Path,
            Scope = MapScope(result.Scope),
            Server = result.Server,
            Tool = result.Tool,
            Enabled = result.Enabled,
            DisabledTools = snapshot.Servers.FirstOrDefault(server => string.Equals(server.Key, result.Server, StringComparison.Ordinal))?.DisabledTools ?? result.DisabledTools,
            CreatedFile = result.CreatedFile,
            Changed = result.Changed,
        };
    }

    /// <summary>
    /// Tests connectivity for one configured MCP server and updates the cached management snapshot with discovered tools.
    /// </summary>
    /// <param name="serverKey">The server key.</param>
    /// <param name="request">Optional path request used to resolve configuration and policy paths.</param>
    /// <param name="cancellationToken">A token that cancels the test.</param>
    /// <returns>The server test result.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="serverKey" /> is empty.</exception>
    public async Task<McpManagementServerTestResult> TestServerAsync(
        string serverKey,
        McpManagementRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverKey);
        request ??= _lastRequest;
        var projectDirectory = NormalizeProjectDirectory(request.ProjectDirectory);
        var normalizedRequest = request with { ProjectDirectory = projectDirectory };
        var snapshot = CachedSnapshot;
        if (snapshot is null || !string.Equals(snapshot.ProjectDirectory, projectDirectory, StringComparison.Ordinal))
        {
            RefreshSnapshot(normalizedRequest);
        }

        McpManagementServerTestResult result;
        try
        {
            await using var runtime = new McpRuntimeService();
            var runtimeResult = await runtime.TestServerAsync(
                new McpRuntimeRequest
                {
                    ProjectDirectory = projectDirectory,
                    UserHomeDirectory = normalizedRequest.UserHomeDirectory,
                },
                serverKey.Trim(),
                cancellationToken).ConfigureAwait(false);
            result = MapTestResult(runtimeResult);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            result = new McpManagementServerTestResult
            {
                Server = serverKey.Trim(),
                Status = McpManagementTestStatus.Canceled,
                Diagnostics = [$"MCP server '{serverKey.Trim()}' test was canceled."],
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or Tomlyn.TomlException)
        {
            result = new McpManagementServerTestResult
            {
                Server = serverKey.Trim(),
                Status = McpManagementTestStatus.Failed,
                Diagnostics = [McpRedactor.RedactValue(null, ex.GetBaseException().Message)],
            };
        }

        UpdateCachedSnapshotWithTestResult(result);
        return result;
    }

    private static string? NormalizeProjectDirectory(string? projectDirectory)
        => string.IsNullOrWhiteSpace(projectDirectory) ? null : Path.GetFullPath(projectDirectory);

    private IReadOnlyList<McpManagementToolSnapshot> GetCachedTools(string serverKey)
    {
        lock (_syncRoot)
        {
            return _cachedSnapshot?.Servers.FirstOrDefault(server => string.Equals(server.Key, serverKey, StringComparison.Ordinal))?.Tools ?? [];
        }
    }

    private McpManagementTestStatus GetCachedTestStatus(string serverKey)
    {
        lock (_syncRoot)
        {
            return _cachedSnapshot?.Servers.FirstOrDefault(server => string.Equals(server.Key, serverKey, StringComparison.Ordinal))?.LastTestStatus ?? McpManagementTestStatus.NotRun;
        }
    }

    private DateTimeOffset? GetCachedTestedAt(string serverKey)
    {
        lock (_syncRoot)
        {
            return _cachedSnapshot?.Servers.FirstOrDefault(server => string.Equals(server.Key, serverKey, StringComparison.Ordinal))?.LastTestedAt;
        }
    }

    private void UpdateCachedSnapshotWithTestResult(McpManagementServerTestResult result)
    {
        UpdateCachedSnapshotWithTools(result.Server, result.Tools, result.Status, result.CompletedAt, result.Diagnostics);
    }

    private void UpdateCachedSnapshotWithTools(
        string serverKey,
        IReadOnlyList<McpManagementToolSnapshot> tools,
        McpManagementTestStatus status,
        DateTimeOffset? testedAt,
        IReadOnlyList<string>? diagnostics = null)
    {
        lock (_syncRoot)
        {
            if (_cachedSnapshot is null)
            {
                return;
            }

            var servers = _cachedSnapshot.Servers
                .Select(server =>
                {
                    if (!string.Equals(server.Key, serverKey, StringComparison.Ordinal))
                    {
                        return server;
                    }

                    var appliedTools = ApplyToolPolicy(server, tools);
                    return server with
                    {
                        Tools = appliedTools,
                        ExposedToolCount = appliedTools.Count(static tool => tool.Enabled),
                        TotalToolCount = tools.Count,
                        LastTestStatus = status,
                        LastTestedAt = testedAt,
                        Diagnostics = MergeDiagnostics(server.Diagnostics, diagnostics ?? []),
                    };
                })
                .ToArray();
            _cachedSnapshot = _cachedSnapshot with
            {
                Servers = servers,
                Summary = BuildSummaryFromServers(_cachedSnapshot.Summary, servers),
            };
        }
    }

    private static IReadOnlyList<McpManagementToolSnapshot> ApplyToolPolicy(McpManagementServerSnapshot server, IReadOnlyList<McpManagementToolSnapshot> tools)
        => tools.Select(tool => ApplyToolPolicy(server, tool)).ToArray();

    private static McpManagementToolSnapshot ApplyToolPolicy(McpManagementServerSnapshot server, McpManagementToolSnapshot tool)
    {
        var disabledReason = GetToolDisabledReason(server, tool.Name);
        return tool with
        {
            Enabled = disabledReason is null && server.PolicyEnabled != false,
            Availability = disabledReason is null && server.PolicyEnabled != false ? "available" : "disabled_by_policy",
            Diagnostic = disabledReason,
        };
    }

    private static string? GetToolDisabledReason(McpManagementServerSnapshot server, string toolName)
    {
        if (server.PolicyEnabled == false)
        {
            return $"MCP server '{server.Key}' is disabled by policy.";
        }

        if (server.AllowedTools.Count > 0 && !server.AllowedTools.Contains(toolName, StringComparer.Ordinal))
        {
            return $"MCP tool '{toolName}' on server '{server.Key}' is not in the policy allowed_tools list.";
        }

        if (server.DisabledTools.Contains(toolName, StringComparer.Ordinal))
        {
            return $"MCP tool '{toolName}' on server '{server.Key}' is disabled by policy.";
        }

        return null;
    }

    private static IReadOnlyList<string> MergeDiagnostics(IReadOnlyList<string> existing, IReadOnlyList<string> latest)
        => latest.Count == 0 ? existing : existing.Concat(latest).Distinct(StringComparer.Ordinal).ToArray();

    private static McpManagementSummary BuildSummaryFromServers(McpManagementSummary current, IReadOnlyList<McpManagementServerSnapshot> servers)
        => current with
        {
            ExposedToolCount = servers.Sum(static server => server.ExposedToolCount),
            TotalToolCount = servers.Sum(static server => server.TotalToolCount),
        };

    private static McpManagementServerTestResult MapTestResult(McpRuntimeServerTestResult result)
    {
        var diagnostics = result.Diagnostics.Select(static diagnostic => McpRedactor.RedactValue(null, diagnostic.Message)).ToArray();
        return new McpManagementServerTestResult
        {
            Server = result.Server,
            Status = MapTestStatus(result.Diagnostics),
            Tools = result.Tools.Select(MapTool).ToArray(),
            Diagnostics = diagnostics,
        };
    }

    private static McpManagementTestStatus MapTestStatus(IReadOnlyList<McpRuntimeDiagnostic> diagnostics)
    {
        if (diagnostics.Count == 0)
        {
            return McpManagementTestStatus.Succeeded;
        }

        if (diagnostics.Any(static diagnostic => string.Equals(diagnostic.Code, "unsupported_transport", StringComparison.Ordinal)))
        {
            return McpManagementTestStatus.Unsupported;
        }

        if (diagnostics.Any(static diagnostic => string.Equals(diagnostic.Code, "server_startup_timeout", StringComparison.Ordinal)))
        {
            return McpManagementTestStatus.TimedOut;
        }

        return McpManagementTestStatus.Failed;
    }

    private static McpManagementToolSnapshot MapTool(McpRuntimeTool tool)
        => new()
        {
            Name = tool.Name,
            Title = tool.Title,
            Description = tool.Description,
            Alias = tool.Alias,
            Enabled = tool.Enabled,
            Availability = tool.Enabled ? "available" : "disabled_by_policy",
            Diagnostic = tool.DisabledReason,
        };

    private (McpPolicyOptions Policy, string? Diagnostic) LoadPolicy(string globalPolicyPath, string? projectPolicyPath)
    {
        try
        {
            return (_policyLoader.Load(globalPolicyPath, projectPolicyPath), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or Tomlyn.TomlException)
        {
            return (new McpPolicyOptions(), ex.GetBaseException().Message);
        }
    }

    private static IReadOnlyList<McpManagementServerSnapshot> BuildServers(
        McpConfigSnapshot configSnapshot,
        McpPolicyOptions policy,
        IReadOnlyList<McpManagementConfigSourceSnapshot> sources)
    {
        var rows = new List<McpManagementServerSnapshot>();
        foreach (var source in sources.Where(static source => !source.Exists || !source.IsValid))
        {
            rows.Add(new McpManagementServerSnapshot
            {
                Key = $"{FormatScope(source.Scope)}-config",
                DisplayName = $"{FormatScope(source.Scope)} MCP config",
                State = source.Exists ? McpManagementServerState.InvalidConfig : McpManagementServerState.MissingConfig,
                StateReason = source.Exists ? "Invalid JSON configuration" : "Config file is missing",
                SourceScope = source.Scope,
                SourcePath = source.Path,
                SourceFormat = source.Format,
                Diagnostics = string.IsNullOrWhiteSpace(source.Diagnostic) ? [] : [source.Diagnostic!],
            });
        }

        foreach (var server in configSnapshot.EffectiveServers)
        {
            rows.Add(MapServer(server, policy, McpManagementServerState.Configured));
        }

        foreach (var shadowed in configSnapshot.ShadowedGlobalServers)
        {
            rows.Add(MapShadowedServer(shadowed, policy));
        }

        return rows
            .OrderBy(static row => GetStateSort(row.State))
            .ThenBy(static row => row.Key, StringComparer.Ordinal)
            .ToArray();
    }

    private static McpManagementServerSnapshot MapServer(McpEffectiveServer effective, McpPolicyOptions policy, McpManagementServerState defaultState)
    {
        var definition = effective.Definition;
        policy.Servers.TryGetValue(definition.Key, out var serverPolicy);
        var policyEnabled = serverPolicy?.Enabled ?? policy.Enabled;
        var state = policyEnabled ? defaultState : McpManagementServerState.Disabled;
        return new McpManagementServerSnapshot
        {
            Key = definition.Key,
            DisplayName = definition.Key,
            State = state,
            StateReason = state == McpManagementServerState.Disabled ? "Disabled by policy" : "Configured; runtime connection not started",
            Transport = MapTransport(definition.Transport),
            SourceScope = MapScope(definition.SourceScope),
            SourcePath = definition.SourcePath,
            SourceFormat = MapFormat(definition.SourceFlavor),
            OverridesGlobal = effective.OverridesGlobal,
            ShadowedGlobalPath = effective.ShadowedGlobalDefinition?.SourcePath,
            Command = definition.Transport == McpTransportKind.Stdio ? definition.Command : null,
            Args = definition.Transport == McpTransportKind.Stdio ? McpRedactor.RedactArguments(definition.Args) : [],
            Cwd = definition.Cwd,
            Env = definition.Env.Count > 0 ? McpRedactor.RedactDictionary(definition.Env) : new Dictionary<string, string>(StringComparer.Ordinal),
            Url = definition.Transport == McpTransportKind.Http ? McpRedactor.RedactUrl(definition.Url) : null,
            Headers = definition.Headers.Count > 0 ? McpRedactor.RedactDictionary(definition.Headers) : new Dictionary<string, string>(StringComparer.Ordinal),
            PolicyEnabled = policyEnabled,
            PolicyRequired = serverPolicy?.Required,
            DirectExposure = serverPolicy?.DirectExposure ?? policy.DirectExposure,
            DirectTools = serverPolicy?.DirectTools ?? [],
            AllowedTools = serverPolicy?.AllowedTools ?? [],
            DisabledTools = serverPolicy?.DisabledTools ?? [],
            StartupTimeoutMs = serverPolicy?.StartupTimeoutMs,
            ToolTimeoutMs = serverPolicy?.ToolTimeoutMs,
            Diagnostics = effective.OverridesGlobal ? [$"Project config overrides global definition at {effective.ShadowedGlobalDefinition?.SourcePath}."] : [],
        };
    }

    private static McpManagementServerSnapshot MapShadowedServer(McpServerDefinition definition, McpPolicyOptions policy)
    {
        policy.Servers.TryGetValue(definition.Key, out var serverPolicy);
        return new McpManagementServerSnapshot
        {
            Key = definition.Key,
            DisplayName = definition.Key,
            State = McpManagementServerState.Shadowed,
            StateReason = "Shadowed by project config",
            Transport = MapTransport(definition.Transport),
            SourceScope = MapScope(definition.SourceScope),
            SourcePath = definition.SourcePath,
            SourceFormat = MapFormat(definition.SourceFlavor),
            Command = definition.Transport == McpTransportKind.Stdio ? definition.Command : null,
            Args = definition.Transport == McpTransportKind.Stdio ? McpRedactor.RedactArguments(definition.Args) : [],
            Cwd = definition.Cwd,
            Env = definition.Env.Count > 0 ? McpRedactor.RedactDictionary(definition.Env) : new Dictionary<string, string>(StringComparer.Ordinal),
            Url = definition.Transport == McpTransportKind.Http ? McpRedactor.RedactUrl(definition.Url) : null,
            Headers = definition.Headers.Count > 0 ? McpRedactor.RedactDictionary(definition.Headers) : new Dictionary<string, string>(StringComparer.Ordinal),
            PolicyEnabled = serverPolicy?.Enabled ?? policy.Enabled,
            PolicyRequired = serverPolicy?.Required,
            DirectExposure = serverPolicy?.DirectExposure ?? policy.DirectExposure,
            DirectTools = serverPolicy?.DirectTools ?? [],
            AllowedTools = serverPolicy?.AllowedTools ?? [],
            DisabledTools = serverPolicy?.DisabledTools ?? [],
            Diagnostics = ["This global definition is ignored because a project server uses the same key."],
        };
    }

    private static McpManagementSummary BuildSummary(
        McpConfigSnapshot configSnapshot,
        IReadOnlyList<McpManagementConfigSourceSnapshot> sources,
        IReadOnlyList<McpManagementServerSnapshot> servers,
        string globalPolicyPath,
        string? projectPolicyPath)
    {
        var configured = servers.Count(static server => server.State is McpManagementServerState.Configured or McpManagementServerState.Disabled);
        var active = servers.Count(static server => server.State == McpManagementServerState.Configured && server.PolicyEnabled != false);
        var unavailable = servers.Count(static server => server.State is McpManagementServerState.Disabled or McpManagementServerState.InvalidConfig);
        return new McpManagementSummary
        {
            HasConfiguration = sources.Any(static source => source.Exists) || File.Exists(globalPolicyPath) || (!string.IsNullOrWhiteSpace(projectPolicyPath) && File.Exists(projectPolicyPath)),
            ConfiguredServerCount = configSnapshot.EffectiveServers.Count,
            ActiveServerCount = active,
            UnavailableServerCount = unavailable,
            InvalidSourceCount = sources.Count(static source => source.Exists && !source.IsValid),
            MissingSourceCount = sources.Count(static source => !source.Exists),
            ShadowedServerCount = configSnapshot.ShadowedGlobalServers.Count,
            ExposedToolCount = 0,
            TotalToolCount = 0,
        };
    }

    private static McpManagementConfigSourceSnapshot MapSource(McpConfigSource source)
        => new()
        {
            Scope = MapScope(source.Scope),
            Path = source.Path,
            Exists = source.Exists,
            DirectoryExists = source.DirectoryExists,
            IsWritable = source.IsWritable,
            Format = source.Flavor is { } flavor ? MapFormat(flavor) : null,
            RootKey = source.RootKey,
            IsValid = source.IsValid,
            Diagnostic = source.Diagnostic,
            ServerKeys = source.Servers.Select(static server => server.Key).OrderBy(static key => key, StringComparer.Ordinal).ToArray(),
        };

    private static McpManagementPolicySnapshot MapPolicy(McpPolicyOptions policy, string globalPath, string? projectPath, string? diagnostic)
        => new()
        {
            GlobalPath = globalPath,
            ProjectPath = projectPath,
            Enabled = policy.Enabled,
            ConnectOnStartup = policy.ConnectOnStartup,
            StartupTimeoutMs = policy.StartupTimeoutMs,
            ToolTimeoutMs = policy.ToolTimeoutMs,
            MaxToolOutputChars = policy.MaxToolOutputChars,
            DiscoverInPrompt = policy.DiscoverInPrompt,
            PromptMaxServers = policy.PromptMaxServers,
            PromptMaxTools = policy.PromptMaxTools,
            DirectExposure = policy.DirectExposure,
            PreferredWriteScope = policy.PreferredWriteScope,
            Diagnostic = diagnostic,
        };

    private static int GetStateSort(McpManagementServerState state)
        => state switch
        {
            McpManagementServerState.InvalidConfig => 0,
            McpManagementServerState.Configured => 1,
            McpManagementServerState.Disabled => 2,
            McpManagementServerState.Shadowed => 3,
            McpManagementServerState.MissingConfig => 4,
            _ => 9,
        };

    private static McpManagementScope ResolveWriteScope(McpManagementScope? scope, string? projectDirectory, string parameterName)
    {
        var targetScope = scope ?? (projectDirectory is null ? McpManagementScope.Global : McpManagementScope.Project);
        if (targetScope == McpManagementScope.Project && projectDirectory is null)
        {
            throw new ArgumentException("Project JSON config scope requires a project directory.", parameterName);
        }

        return targetScope;
    }

    private static string GetJsonConfigPath(McpManagementScope scope, string? projectDirectory, string? userHomeDirectory)
        => scope == McpManagementScope.Project
            ? McpConfigDiscovery.GetProjectConfigPath(projectDirectory ?? throw new ArgumentException("Project JSON config scope requires a project directory.", nameof(projectDirectory)))
            : McpConfigDiscovery.GetGlobalConfigPath(userHomeDirectory);

    private static McpServerDefinition ToServerDefinition(McpManagementServerEdit edit, McpManagementScope scope, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(edit.Key, nameof(edit));
        var key = edit.Key.Trim();
        var isStdio = edit.Transport == McpManagementTransport.Stdio;
        var command = string.IsNullOrWhiteSpace(edit.Command) ? null : edit.Command.Trim();
        var url = string.IsNullOrWhiteSpace(edit.Url) ? null : edit.Url.Trim();
        if (isStdio && string.IsNullOrWhiteSpace(command))
        {
            throw new ArgumentException("A stdio MCP server requires a command.", nameof(edit));
        }

        if (!isStdio && string.IsNullOrWhiteSpace(url))
        {
            throw new ArgumentException("An HTTP/SSE MCP server requires a URL.", nameof(edit));
        }

        return new McpServerDefinition
        {
            Key = key,
            Transport = isStdio ? McpTransportKind.Stdio : McpTransportKind.Http,
            SourceScope = MapScope(scope),
            SourcePath = path,
            SourceFlavor = McpConfigFlavor.CodeAlta,
            Command = isStdio ? command : null,
            Args = isStdio ? edit.Args.Where(static value => !string.IsNullOrWhiteSpace(value)).Select(static value => value.Trim()).ToArray() : [],
            Cwd = isStdio && !string.IsNullOrWhiteSpace(edit.Cwd) ? edit.Cwd.Trim() : null,
            Env = isStdio ? TrimDictionary(edit.Env) : new Dictionary<string, string>(StringComparer.Ordinal),
            Url = isStdio ? null : url,
            Headers = isStdio ? new Dictionary<string, string>(StringComparer.Ordinal) : TrimDictionary(edit.Headers),
        };
    }

    private static IReadOnlyDictionary<string, string> TrimDictionary(IReadOnlyDictionary<string, string> values)
        => values
            .Where(static item => !string.IsNullOrWhiteSpace(item.Key))
            .ToDictionary(static item => item.Key.Trim(), static item => item.Value, StringComparer.Ordinal);

    private static McpManagementScope MapScope(McpConfigScope scope)
        => scope == McpConfigScope.Project ? McpManagementScope.Project : McpManagementScope.Global;

    private static McpConfigScope MapScope(McpManagementScope scope)
        => scope == McpManagementScope.Project ? McpConfigScope.Project : McpConfigScope.Global;

    private static McpManagementConfigFormat MapFormat(McpConfigFlavor flavor)
        => flavor switch
        {
            McpConfigFlavor.Copilot => McpManagementConfigFormat.Copilot,
            McpConfigFlavor.Vscode => McpManagementConfigFormat.Vscode,
            McpConfigFlavor.Claude => McpManagementConfigFormat.Claude,
            McpConfigFlavor.Intellij => McpManagementConfigFormat.Intellij,
            _ => McpManagementConfigFormat.CodeAlta,
        };

    private static McpManagementTransport MapTransport(McpTransportKind transport)
        => transport == McpTransportKind.Http ? McpManagementTransport.Http : McpManagementTransport.Stdio;

    private static string FormatFlavor(McpConfigFlavor flavor)
        => flavor switch
        {
            McpConfigFlavor.Copilot => "copilot",
            McpConfigFlavor.Vscode => "vscode",
            McpConfigFlavor.Claude => "claude",
            McpConfigFlavor.Intellij => "intellij",
            _ => "codealta",
        };

    private static string FormatScope(McpManagementScope scope)
        => scope == McpManagementScope.Project ? "project" : "global";
}
