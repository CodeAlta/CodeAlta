using System.Text;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugin.Mcp;

/// <summary>
/// Built-in plugin that inspects Model Context Protocol server configuration for CodeAlta.
/// </summary>
[Plugin("mcp", DisplayName = "MCP", Description = "Connects CodeAlta to configured Model Context Protocol servers.", Frontends = PluginFrontends.All | PluginFrontends.Cli)]
public sealed partial class McpPlugin : PluginBase
{
    private const string ArgumentsJsonPropertyName = "arguments_json";
    private const int MaxWrapperSchemaDescriptionChars = 2000;

    private readonly McpActivationState _activationState = new();
    // What every tool call, prompt build and run start reads: the configuration and the policy, read again when a file changed.
    private readonly McpConfigCache _configCache = new();
    private readonly McpSessionConnections _connections;
    private readonly McpManagementService _managementService;
    private readonly McpPluginPresentation? _presentation;
    // The approvals and the files read of the skills of the servers, for a host that loads them.
    private readonly McpSkillServices? _skills;
    private readonly string? _userHomeDirectory;

    /// <summary>Initializes an MCP backend without terminal presentation.</summary>
    public McpPlugin()
        : this(createPresentation: null, userHomeDirectory: null)
    {
    }

    /// <summary>Initializes an MCP backend without terminal presentation, for a host with these options.</summary>
    /// <param name="options">What the host does with MCP servers beyond their tools.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    public McpPlugin(McpPluginOptions options)
        : this(createPresentation: null, userHomeDirectory: null, options ?? throw new ArgumentNullException(nameof(options)))
    {
    }

    // The terminal host borrows these exact owners; presentation construction precedes contribution enumeration.
    internal McpPlugin(Func<McpManagementService, McpActivationState, McpPluginPresentation> createPresentation)
        : this(createPresentation ?? throw new ArgumentNullException(nameof(createPresentation)), userHomeDirectory: null)
    {
    }

    // A non-null home directory replaces the current user's for global MCP configuration, policy and OAuth tokens.
    internal McpPlugin(Func<McpManagementService, McpActivationState, McpPluginPresentation>? createPresentation, string? userHomeDirectory)
        : this(createPresentation, userHomeDirectory, new McpPluginOptions())
    {
    }

    internal McpPlugin(Func<McpManagementService, McpActivationState, McpPluginPresentation>? createPresentation, string? userHomeDirectory, McpPluginOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _userHomeDirectory = userHomeDirectory;
        ShowsApps = options.ShowsApps;
        LoadsSkills = options.LoadsSkills;
        _connections = new McpSessionConnections(_configCache, options.ShowsApps, options.LoadsSkills);
        // What the presentation saves through it is read again at once by the runtime and the prompt.
        _managementService = new McpManagementService(userHomeDirectory, _configCache, options.ShowsApps, options.LoadsSkills);
        _skills = options.LoadsSkills ? new McpSkillServices(new McpSkillApprovalStore(McpSkillApprovalStore.PathOf(userHomeDirectory)), new McpSkillContentCache()) : null;
        if (createPresentation is not null)
        {
            _presentation = createPresentation(_managementService, _activationState)
                ?? throw new InvalidOperationException("The MCP presentation factory returned null.");
        }
    }

    /// <summary>Gets whether the host shows the views of MCP Apps: the servers are told, and the calls of their tools carry their view.</summary>
    public bool ShowsApps { get; }

    /// <summary>Gets whether the host loads the skills of MCP servers: the servers are told, and their skills are listed and offered.</summary>
    public bool LoadsSkills { get; }

    // The configuration the plugin reads, kept between its requests.
    internal McpConfigCache ConfigCache => _configCache;

    // The management service the presentation borrows.
    internal McpManagementService ManagementService => _managementService;

    /// <inheritdoc />
    public override IEnumerable<PluginCommandContribution> GetCommands()
    {
        if (_presentation is null)
        {
            yield break;
        }

        foreach (var contribution in _presentation.CreateCommands())
        {
            yield return contribution;
        }
    }

    /// <inheritdoc />
    public override IEnumerable<PluginUiContribution> GetUiContributions()
    {
        var status = new PluginContentContribution
        {
            Region = PluginUiRegion.SessionStatus,
            Name = "mcp-status",
            Order = 100,
            CreateContent = context =>
            {
                var projectPath = ResolveProjectPath(context.ProjectPath, context.Services.Workspace.SelectedProjectPath, null);
                var snapshot = ResolveStatusSnapshot(_managementService, projectPath);
                if (!snapshot.Summary.HasConfiguration && snapshot.Summary.ConfiguredServerCount == 0 && snapshot.Summary.InvalidSourceCount == 0)
                {
                    return null;
                }

                var activationScope = ResolveActivationScopeKey(context, projectPath);
                return new PluginRenderResult
                {
                    Text = CreateStatusLabel(snapshot, _activationState.GetToolCounts(activationScope), _activationState.GetUsedServers(activationScope)),
                };
            },
        };
        yield return _presentation is null ? status : _presentation.DecorateStatus(status);
    }

    /// <inheritdoc />
    public override IEnumerable<PluginAltaCommandContribution> GetAltaCommands()
    {
        yield return new PluginAltaCommandContribution
        {
            Path = "mcp",
            Description = "Inspect and manage configured Model Context Protocol servers.",
            Policy = new PluginAltaCommandPolicy
            {
                RequiresInProcessRuntime = true,
                IsMutating = true,
                SupportsCatalogOnlyContext = false,
            },
            CreateCommandNode = context => McpCommandFactory.CreateCommand(
                context,
                new McpCommandFactoryOptions
                {
                    UserHomeDirectory = _userHomeDirectory,
                    Connections = _connections,
                    ConfigCache = _configCache,
                    SessionEnvironment = SessionEnvironmentAsync,
                    SessionProjectDirectory = string.IsNullOrWhiteSpace(context.SourceSessionId)
                        ? null
                        : _activationState.GetProjectDirectory(McpActivationState.ResolveScopeKey(context.SourceSessionId, null)),
                    Skills = _skills,
                },
                _activationState),
        };
    }

    /// <summary>
    /// Describes the servers of a configuration snapshot as a status item, for a host that shows the MCP
    /// status without running this plugin. No server is contacted: the tool part says what the snapshot knows.
    /// </summary>
    /// <param name="snapshot">The configuration snapshot.</param>
    /// <returns>The status item, or <see langword="null"/> when no MCP configuration exists.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is <see langword="null"/>.</exception>
    public static PluginStatusItem? CreateStatus(McpManagementSnapshot snapshot)
        => CreateStatus(snapshot, new Dictionary<string, int>(), []);

    /// <summary>
    /// Describes the servers of a configuration snapshot as a status item for one session of the host that
    /// runs this plugin: the tool part counts the tools of the servers the session activated.
    /// </summary>
    /// <param name="snapshot">The configuration snapshot of the session's project.</param>
    /// <param name="sessionId">The session, or <see langword="null"/> for the project as a whole.</param>
    /// <returns>The status item, or <see langword="null"/> when no MCP configuration exists.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is <see langword="null"/>.</exception>
    public PluginStatusItem? CreateStatus(McpManagementSnapshot snapshot, string? sessionId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var scope = McpActivationState.ResolveScopeKey(sessionId, snapshot.ProjectDirectory);
        return CreateStatus(snapshot, _activationState.GetToolCounts(scope), _activationState.GetUsedServers(scope));
    }

    private static PluginStatusItem? CreateStatus(
        McpManagementSnapshot snapshot,
        IReadOnlyDictionary<string, int> activatedToolCounts,
        IReadOnlyCollection<string> activeServers)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var summary = snapshot.Summary;
        if (!summary.HasConfiguration && summary.ConfiguredServerCount == 0 && summary.InvalidSourceCount == 0)
        {
            return null;
        }

        const string label = "MCP";
        return new PluginStatusItem
        {
            Label = label,
            Text = CreateStatusLabel(snapshot, activatedToolCounts, activeServers)[(label.Length + 1)..],
            Tone = summary.UnavailableServerCount > 0 ? PluginStatusTone.Warning
                : activeServers.Count > 0 ? PluginStatusTone.Success : PluginStatusTone.Info,
        };
    }

    internal static string CreateStatusLabel(
        McpManagementSnapshot snapshot,
        IReadOnlyDictionary<string, int> activatedToolCounts,
        IReadOnlyCollection<string> activeServers)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(activatedToolCounts);
        ArgumentNullException.ThrowIfNull(activeServers);

        var summary = snapshot.Summary;
        var builder = new StringBuilder();
        builder.Append("MCP ")
            .Append(summary.ActiveServerCount)
            .Append('/')
            .Append(summary.ConfiguredServerCount);
        if (summary.UnavailableServerCount > 0)
        {
            builder.Append(" · ")
                .Append(summary.UnavailableServerCount)
                .Append(" unavailable");
        }

        builder.Append(" · ")
            .Append(CreateStatusToolLabel(snapshot, activatedToolCounts, activeServers));
        return builder.ToString();
    }

    private static string CreateStatusToolLabel(
        McpManagementSnapshot snapshot,
        IReadOnlyDictionary<string, int> activatedToolCounts,
        IReadOnlyCollection<string> activeServers)
    {
        var summary = snapshot.Summary;
        if (summary.TotalToolCount > 0 || HasCompletedManagementToolDiscovery(snapshot))
        {
            return $"tools {summary.ExposedToolCount}/{summary.TotalToolCount}";
        }

        var loadedActiveServerCount = activeServers.Count(server => activatedToolCounts.ContainsKey(server));
        if (loadedActiveServerCount > 0)
        {
            return $"active tools {activeServers.Sum(server => activatedToolCounts.TryGetValue(server, out var count) ? count : 0)}";
        }

        return activeServers.Count > 0 ? "tools pending" : "tools not loaded";
    }

    internal static bool HasCompletedManagementToolDiscovery(McpManagementSnapshot snapshot)
        => snapshot.Servers.Any(static server => server.LastTestStatus == McpManagementTestStatus.Succeeded);

    internal static McpManagementSnapshot ResolveStatusSnapshot(McpManagementService managementService, string? projectPath)
    {
        var normalizedProjectPath = string.IsNullOrWhiteSpace(projectPath) ? null : Path.GetFullPath(projectPath);
        var cached = managementService.CachedSnapshot;
        return cached is not null && string.Equals(cached.ProjectDirectory, normalizedProjectPath, StringComparison.OrdinalIgnoreCase)
            ? cached
            : managementService.RefreshSnapshot(new McpManagementRequest { ProjectDirectory = projectPath });
    }

    /// <inheritdoc />
    public override IEnumerable<PluginSystemPromptContribution> GetSystemPromptContributions()
    {
        yield return Prompt.Dynamic(
            PluginPromptChannel.Developer,
            CreatePromptDiscoveryAsync,
            title: "MCP servers",
            kind: PluginPromptPartKind.ToolGuidance,
            order: 50);
        // A host that does not load the skills of the servers does not offer them.
        if (LoadsSkills)
        {
            yield return Prompt.Dynamic(
                PluginPromptChannel.Developer,
                CreateSkillsPromptAsync,
                title: "MCP skills",
                kind: PluginPromptPartKind.SkillAdvertisement,
                order: 52);
        }
    }

    /// <inheritdoc />
    public override async ValueTask<PluginBeforeAgentRunResult?> OnBeforeAgentRunAsync(
        PluginBeforeAgentRunContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var projectPath = ResolveProjectPath(context.ProjectPath, context.Services.Workspace.SelectedProjectPath, null);
        var activationScope = ResolveActivationScopeKey(context, projectPath);
        // An `alta mcp` call of the session's runs is not given their project: it uses this one, and the same servers.
        if (!string.IsNullOrWhiteSpace(context.SessionId))
        {
            _activationState.SetProjectDirectory(activationScope, projectPath);
        }

        // What the session loaded is in its journal: a resumed session, or one of another run of the application, gets it again.
        RestoreLoadedTools(activationScope, context.LoadedTools);
        var usedServers = _activationState.GetUsedServers(activationScope);
        if (usedServers.Count == 0)
        {
            return CachedToolsResult(await CreateCachedToolsAsync(context, projectPath, []).ConfigureAwait(false));
        }

        // A server activated before it left the space of the session is dropped, and forgotten by the session.
        if (context.SpaceId is { } spaceId)
        {
            var policy = LoadPolicy(projectPath);
            if (usedServers.Any(server => !policy.IsServerInSpace(server, spaceId)))
            {
                _activationState.RetainServers(activationScope, server => policy.IsServerInSpace(server, spaceId));
                usedServers = _activationState.GetUsedServers(activationScope);
                if (usedServers.Count == 0)
                {
                    return CachedToolsResult(await CreateCachedToolsAsync(context, projectPath, []).ConfigureAwait(false));
                }
            }
        }

        // The servers are started as the session's own commands are: with the identity of its space.
        var environment = await SessionEnvironmentAsync(context.SessionId, cancellationToken).ConfigureAwait(false);
        var listed = await ListToolsAsync(
            context.SessionId,
            new McpRuntimeRequest { ProjectDirectory = projectPath, UserHomeDirectory = _userHomeDirectory, SpaceId = context.SpaceId, Environment = environment },
            usedServers,
            cancellationToken).ConfigureAwait(false);
        // A server activated as a whole gives all its tools; another only the tools the session loaded.
        var direct = listed with { Tools = listed.Tools.Where(tool => _activationState.IsActive(activationScope, tool.Server, tool.Name)).ToArray() };
        _activationState.UpdateToolCounts(
            activationScope,
            direct.Tools
                .GroupBy(static tool => tool.Server, StringComparer.Ordinal)
                .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.Ordinal));
        var activeTools = direct.Tools.Select(tool => CreateAgentTool(tool, projectPath, _userHomeDirectory, _connections, context.SpaceId, SessionEnvironmentAsync)).ToArray();
        var cached = await CreateCachedToolsAsync(context, projectPath, activeTools).ConfigureAwait(false);
        if (direct.Tools.Count == 0 && direct.Diagnostics.Count == 0)
        {
            return CachedToolsResult(cached);
        }

        return new PluginBeforeAgentRunResult
        {
            AdditionalTools = [.. activeTools, .. cached],
            TemporaryPromptContributions = CreateDirectToolDiagnosticsPrompt(direct.Diagnostics),
        };
    }

    // The tools of the servers a session activated, listed on the session's own connections: the server started for the
    // listing is the one its tool calls use, and a server it keeps connected answers without being started again. Without
    // a session, the listing has a connection of its own, which ends with it.
    private async Task<McpRuntimeToolSearchResult> ListToolsAsync(string? sessionId, McpRuntimeRequest request, IReadOnlyCollection<string> servers, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            try
            {
                return await _connections.ListToolsForServersAsync(sessionId, request, servers, cancellationToken).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // The plugin stopped: the tools are listed as without a session, and their calls say it stopped.
            }
        }

        await using var runtime = _connections.CreateRuntime(_userHomeDirectory);
        return await runtime.ListToolsForServersAsync(request, servers, cancellationToken).ConfigureAwait(false);
    }

    // For a provider that defers tools, the tools of every enabled server of the space that CodeAlta listed before, without
    // starting any: the few the policy keeps loaded, and the others deferred, found with the tool search of the session. A
    // deferred tool starts its server at its first call. The tools the session activated keep their place before them.
    private async ValueTask<IReadOnlyList<AgentToolDefinition>> CreateCachedToolsAsync(PluginBeforeAgentRunContext context, string? projectPath, IReadOnlyList<AgentToolDefinition> activeTools)
    {
        if (!context.ProviderDefersTools)
        {
            return [];
        }

        McpCachedToolsResult cached;
        await using (var runtime = _connections.CreateRuntime(_userHomeDirectory))
        {
            try
            {
                cached = runtime.ListCachedTools(new McpRuntimeRequest { ProjectDirectory = projectPath, SpaceId = context.SpaceId });
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
            {
                return [];
            }
        }

        var active = activeTools.Select(static tool => tool.Spec.Name).ToHashSet(StringComparer.Ordinal);
        AgentToolDefinition Create(McpRuntimeTool tool) => CreateAgentTool(tool, projectPath, _userHomeDirectory, _connections, context.SpaceId, SessionEnvironmentAsync);
        AgentToolDefinition Defer(McpRuntimeTool tool)
        {
            var definition = Create(tool);
            return definition with { Spec = definition.Spec with { Deferred = true, Group = tool.Server } };
        }

        return
        [
            .. cached.Loaded.Where(tool => !active.Contains(tool.Alias)).Select(Create),
            .. cached.Deferred.Where(tool => !active.Contains(tool.Alias)).Select(Defer),
        ];
    }

    private static PluginBeforeAgentRunResult? CachedToolsResult(IReadOnlyList<AgentToolDefinition> tools)
        => tools.Count == 0 ? null : new PluginBeforeAgentRunResult { AdditionalTools = tools };

    private void RestoreLoadedTools(string activationScope, IReadOnlyList<string> loadedTools)
    {
        var servers = new List<string>();
        var tools = new List<(string Server, string Tool)>();
        foreach (var reference in loadedTools)
        {
            if (!McpToolReference.TryParse(reference, out var server, out var tool)) continue;
            if (tool is null) servers.Add(server);
            else tools.Add((server, tool));
        }

        if (servers.Count > 0) _activationState.ActivateServers(activationScope, servers);
        if (tools.Count > 0) _activationState.ActivateTools(activationScope, tools);
    }

    private ValueTask<string?> CreatePromptDiscoveryAsync(PluginSystemPromptContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        var projectPath = ResolveProjectPath(context.ProjectPath, context.Services.Workspace.SelectedProjectPath, null);
        // The servers and the policy as a refresh of the management snapshot reads them, from the files read last unless one
        // changed: a policy that cannot be read is the default one here, as in the snapshot.
        var config = _configCache.Discover(projectPath, _userHomeDirectory);
        McpPolicyOptions snapshotPolicy;
        try
        {
            snapshotPolicy = LoadPolicy(projectPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or Tomlyn.TomlException)
        {
            snapshotPolicy = new McpPolicyOptions();
        }

        if (!snapshotPolicy.DiscoverInPrompt || config.EffectiveServers.Count == 0)
        {
            return new ValueTask<string?>((string?)null);
        }

        // The space first: a server of another space is not offered, and its activation is forgotten below.
        var policy = context.SpaceId is null ? null : LoadPolicy(projectPath);
        // A configured server is one the policy does not turn off: its own `enabled`, or else the one of MCP.
        var configuredServers = config.EffectiveServers
            .Select(static server => server.Definition.Key)
            .Where(key => policy?.IsServerInSpace(key, context.SpaceId) != false)
            .Where(key => snapshotPolicy.Servers.TryGetValue(key, out var serverPolicy) ? serverPolicy.Enabled ?? snapshotPolicy.Enabled : snapshotPolicy.Enabled)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (configuredServers.Length == 0)
        {
            return new ValueTask<string?>((string?)null);
        }

        var activationScope = ResolveActivationScopeKey(context, projectPath);
        var activeKeys = _activationState.GetActiveServers(activationScope).ToHashSet(StringComparer.Ordinal);
        var configuredKeys = configuredServers.ToHashSet(StringComparer.Ordinal);
        if (_activationState.GetUsedServers(activationScope).Any(server => !configuredKeys.Contains(server)))
        {
            _activationState.RetainServers(activationScope, configuredKeys.Contains);
            activeKeys = _activationState.GetActiveServers(activationScope).ToHashSet(StringComparer.Ordinal);
        }

        var activeServers = configuredServers.Where(activeKeys.Contains).ToArray();
        var inactiveServers = configuredServers.Where(server => !activeKeys.Contains(server)).ToArray();
        var maxServers = Math.Max(1, snapshotPolicy.PromptMaxServers);
        var builder = new StringBuilder();
        builder.AppendLine("MCP servers:");
        builder.Append("- Active: ");
        AppendServerList(builder, activeServers, maxServers);
        builder.AppendLine();
        builder.Append("- Inactive (`alta mcp activate <id>*`): ");
        AppendServerList(builder, inactiveServers, maxServers);
        builder.AppendLine();
        builder.Append("- Activation registers the tools in the current turn: call them in your next step, without ending the turn.");

        return new ValueTask<string?>(builder.ToString().TrimEnd());
    }

    private McpPolicyOptions LoadPolicy(string? projectPath)
        => _configCache.LoadPolicy(McpPolicyWriter.GetGlobalPolicyPath(_userHomeDirectory),
            string.IsNullOrWhiteSpace(projectPath) ? null : McpPolicyWriter.GetProjectPolicyPath(projectPath));

    internal static string ResolveActivationScopeKey(PluginOperationContext context, string? projectPath)
        => McpActivationState.ResolveScopeKey(
            string.IsNullOrWhiteSpace(context.SessionId) ? context.Services.Sessions.SelectedSessionId : context.SessionId,
            projectPath);

    internal static string? ResolveProjectPath(string? primary, string? secondary, string? fallback)
    {
        if (!string.IsNullOrWhiteSpace(primary))
        {
            return primary;
        }

        if (!string.IsNullOrWhiteSpace(secondary))
        {
            return secondary;
        }

        return string.IsNullOrWhiteSpace(fallback) ? null : fallback;
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        // The servers the sessions kept connected end with the plugin, not with the process.
        await _connections.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    internal static AgentToolDefinition CreateAgentTool(McpRuntimeTool tool, string? projectPath, string? userHomeDirectory, McpSessionConnections? connections,
        string? spaceId = null, Func<string?, CancellationToken, ValueTask<AgentProcessEnvironment>>? environment = null)
    {
        var description = CreateToolDescription(tool);
        var useArgumentsJsonWrapper = RequiresArgumentsJsonWrapper(tool.InputSchema);
        var inputSchema = useArgumentsJsonWrapper
            ? CreateArgumentsJsonWrapperSchema(tool)
            : tool.InputSchema.Clone();
        return new AgentToolDefinition(
            new AgentToolSpec(tool.Alias, description, inputSchema),
            async (invocation, cancellationToken) => await InvokeDirectToolAsync(
                tool.Server,
                tool.Name,
                projectPath,
                userHomeDirectory,
                useArgumentsJsonWrapper,
                connections,
                invocation,
                spaceId,
                environment,
                cancellationToken).ConfigureAwait(false));
    }

    // The variables of the identity of a session; none for no session, or outside a host.
    private async ValueTask<AgentProcessEnvironment> SessionEnvironmentAsync(string? sessionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return AgentProcessEnvironment.Empty;
        IPluginIdentityService identities;
        try { identities = Services.Identity; }
        catch (InvalidOperationException) { return AgentProcessEnvironment.Empty; }
        return (await identities.GetIdentityAsync(sessionId, cancellationToken).ConfigureAwait(false)).Environment;
    }

    internal static bool RequiresArgumentsJsonWrapper(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var hasProperties = schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object;
        if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in required.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String &&
                    item.GetString() is { Length: > 0 } requiredName &&
                    (!hasProperties || !properties.TryGetProperty(requiredName, out _)))
                {
                    return true;
                }
            }
        }

        if (schema.TryGetProperty("additionalProperties", out var additionalProperties) &&
            (additionalProperties.ValueKind == JsonValueKind.True ||
             additionalProperties.ValueKind == JsonValueKind.Object ||
             additionalProperties.ValueKind == JsonValueKind.Array))
        {
            return true;
        }

        if (schema.TryGetProperty("patternProperties", out var patternProperties) && patternProperties.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        if (IsObjectSchema(schema) && (!hasProperties || !properties.EnumerateObject().Any()) && !HasClosedAdditionalProperties(schema))
        {
            return true;
        }

        foreach (var property in schema.EnumerateObject())
        {
            if (property.NameEquals("properties") && property.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var childProperty in property.Value.EnumerateObject())
                {
                    if (RequiresArgumentsJsonWrapper(childProperty.Value))
                    {
                        return true;
                    }
                }

                continue;
            }

            if (property.NameEquals("items"))
            {
                if (RequiresArgumentsJsonWrapper(property.Value))
                {
                    return true;
                }

                continue;
            }

            if (property.NameEquals("anyOf") || property.NameEquals("oneOf") || property.NameEquals("allOf"))
            {
                if (property.Value.ValueKind == JsonValueKind.Array &&
                    property.Value.EnumerateArray().Any(RequiresArgumentsJsonWrapper))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsObjectSchema(JsonElement schema)
    {
        if (!schema.TryGetProperty("type", out var type))
        {
            return false;
        }

        return type.ValueKind switch
        {
            JsonValueKind.String => string.Equals(type.GetString(), "object", StringComparison.Ordinal),
            JsonValueKind.Array => type.EnumerateArray().Any(static item =>
                item.ValueKind == JsonValueKind.String && string.Equals(item.GetString(), "object", StringComparison.Ordinal)),
            _ => false,
        };
    }

    private static bool HasClosedAdditionalProperties(JsonElement schema)
        => schema.TryGetProperty("additionalProperties", out var additionalProperties) && additionalProperties.ValueKind == JsonValueKind.False;

    private static JsonElement CreateArgumentsJsonWrapperSchema(McpRuntimeTool tool)
    {
        var description = CreateArgumentsJsonWrapperDescription(tool);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "object");
            writer.WriteStartObject("properties");
            writer.WriteStartObject(ArgumentsJsonPropertyName);
            writer.WriteString("type", "string");
            writer.WriteString("description", description);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteStartArray("required");
            writer.WriteStringValue(ArgumentsJsonPropertyName);
            writer.WriteEndArray();
            writer.WriteBoolean("additionalProperties", false);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private static string CreateArgumentsJsonWrapperDescription(McpRuntimeTool tool)
    {
        var schemaText = CreateRedactedSchemaText(tool.InputSchema);
        if (schemaText.Length > MaxWrapperSchemaDescriptionChars)
        {
            schemaText = schemaText[..MaxWrapperSchemaDescriptionChars] + "…";
        }

        return "JSON object string containing the MCP tool arguments to pass through unchanged. " +
               $"Use this compatibility wrapper because MCP tool '{tool.Name}' on server '{tool.Server}' uses a schema that cannot be faithfully represented as an OpenAI strict tool schema. " +
               "The string value must parse to a JSON object matching the original MCP input schema. " +
               "Original MCP input schema: " + schemaText;
    }

    private static string CreateRedactedSchemaText(JsonElement schema)
        => JsonSerializer.Serialize(RedactSchemaValue(schema, propertyName: null));

    private static object? RedactSchemaValue(JsonElement element, string? propertyName)
        => element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(
                static property => property.Name,
                property => RedactSchemaValue(property.Value, property.Name),
                StringComparer.Ordinal),
            JsonValueKind.Array => element.EnumerateArray().Select(item => RedactSchemaValue(item, propertyName)).ToArray(),
            JsonValueKind.String => McpRedactor.RedactValue(propertyName, element.GetString()),
            JsonValueKind.Number => element.Clone(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => element.Clone(),
        };

    private static void AppendServerList(
        StringBuilder builder,
        IReadOnlyList<string> servers,
        int maxServers)
    {
        if (servers.Count == 0)
        {
            builder.Append("(none)");
            return;
        }

        var displayed = servers.Take(maxServers).ToArray();
        for (var index = 0; index < displayed.Length; index++)
        {
            if (index > 0)
            {
                builder.Append(", ");
            }

            builder.Append('`').Append(displayed[index]).Append('`');
        }

        if (servers.Count > displayed.Length)
        {
            builder.Append(", …(+");
            builder.Append((servers.Count - displayed.Length).ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(')');
        }
    }

    private static string CreateToolDescription(McpRuntimeTool tool)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(tool.Title))
        {
            builder.Append(tool.Title.Trim()).Append(". ");
        }

        if (!string.IsNullOrWhiteSpace(tool.Description))
        {
            builder.Append(tool.Description.Trim()).Append(" ");
        }

        builder.Append("MCP tool '").Append(tool.Name).Append("' on server '").Append(tool.Server).Append("'.");
        return builder.ToString();
    }

    private static async Task<AgentToolResult> InvokeDirectToolAsync(
        string server,
        string tool,
        string? projectPath,
        string? userHomeDirectory,
        bool useArgumentsJsonWrapper,
        McpSessionConnections? connections,
        AgentToolInvocation invocation,
        string? spaceId,
        Func<string?, CancellationToken, ValueTask<AgentProcessEnvironment>>? environment,
        CancellationToken cancellationToken)
    {
        if (!TryReadArguments(invocation.Arguments, useArgumentsJsonWrapper, out var arguments, out var error))
        {
            return new AgentToolResult(false, [new AgentToolResultItem.Text(error)], error);
        }

        var diagnostics = new List<McpRuntimeDiagnostic>();
        var request = new McpRuntimeRequest
        {
            ProjectDirectory = projectPath, UserHomeDirectory = userHomeDirectory, SpaceId = spaceId,
            Environment = environment is null ? null : await environment(invocation.SessionId, cancellationToken).ConfigureAwait(false),
        };
        McpRuntimeToolCallResult? result;
        if (connections is not null && !string.IsNullOrWhiteSpace(invocation.SessionId))
        {
            // The session's server stays connected from one call to the next: it keeps its state, and
            // the call does not pay for a start.
            try
            {
                result = await connections.CallToolAsync(invocation.SessionId, request, server, tool, arguments, diagnostics, cancellationToken).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                var stopped = $"MCP tool '{tool}' on server '{server}' is unavailable: the MCP plugin was stopped.";
                return new AgentToolResult(false, [new AgentToolResultItem.Text(stopped)], stopped);
            }
        }
        else
        {
            await using var runtime = connections?.CreateRuntime(userHomeDirectory) ?? new McpRuntimeService(userHomeDirectory);
            result = await runtime.CallToolAsync(request, server, tool, arguments, diagnostics, cancellationToken).ConfigureAwait(false);
        }

        if (result is null)
        {
            var message = diagnostics.Count == 0
                ? $"MCP tool '{tool}' on server '{server}' is unavailable."
                : string.Join(Environment.NewLine, diagnostics.Select(static diagnostic => diagnostic.Message));
            return new AgentToolResult(false, [new AgentToolResultItem.Text(message)], message);
        }

        var text = FormatDirectToolResult(result);
        // The images of the result go to the model with its text: a screenshot tool is of no use without them.
        var images = result.Content
            .Where(static block => block.ImageBase64 is not null)
            .Select((block, index) => (AgentToolResultItem)new AgentToolResultItem.Image(
                block.ImageBase64!,
                string.IsNullOrWhiteSpace(block.MimeType) ? "image/png" : block.MimeType,
                index == 0 ? $"{server}-{tool}" : $"{server}-{tool}-{index + 1}"));
        return new AgentToolResult(!result.IsError, [new AgentToolResultItem.Text(text), .. images], result.IsError ? text : null)
        {
            // A host that shows views gets the view of the call with it; the model never sees it.
            App = connections?.AdvertiseApps == true && result.App is { } app
                ? new AgentToolAppResult(server, tool, app.ResourceUri, app.StructuredContent, app.Meta, app.Content)
                : null,
        };
    }

    private static bool TryReadArguments(JsonElement element, bool useArgumentsJsonWrapper, out IReadOnlyDictionary<string, object?> arguments, out string error)
    {
        if (useArgumentsJsonWrapper)
        {
            return TryReadWrappedArguments(element, out arguments, out error);
        }

        return TryReadObjectArguments(element, out arguments, out error);
    }

    private static bool TryReadWrappedArguments(JsonElement element, out IReadOnlyDictionary<string, object?> arguments, out string error)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
            error = $"MCP direct tool compatibility arguments must be a JSON object containing an '{ArgumentsJsonPropertyName}' string.";
            return false;
        }

        if (!element.TryGetProperty(ArgumentsJsonPropertyName, out var argumentsJson))
        {
            arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
            error = $"MCP direct tool compatibility arguments must include an '{ArgumentsJsonPropertyName}' string.";
            return false;
        }

        if (argumentsJson.ValueKind != JsonValueKind.String)
        {
            arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
            error = $"MCP direct tool compatibility argument '{ArgumentsJsonPropertyName}' must be a JSON string containing an object.";
            return false;
        }

        var json = argumentsJson.GetString();
        if (string.IsNullOrWhiteSpace(json))
        {
            arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
            error = $"MCP direct tool compatibility argument '{ArgumentsJsonPropertyName}' must contain a JSON object.";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
                error = $"MCP direct tool compatibility argument '{ArgumentsJsonPropertyName}' must parse to a JSON object.";
                return false;
            }

            return TryReadObjectArguments(document.RootElement, out arguments, out error);
        }
        catch (JsonException ex)
        {
            arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
            error = $"MCP direct tool compatibility argument '{ArgumentsJsonPropertyName}' must contain valid JSON: {ex.Message}";
            return false;
        }
    }

    private static bool TryReadObjectArguments(JsonElement element, out IReadOnlyDictionary<string, object?> arguments, out string error)
    {
        if (element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
            error = string.Empty;
            return true;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
            error = "MCP direct tool arguments must be a JSON object.";
            return false;
        }

        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            values[property.Name] = property.Value.Clone();
        }

        arguments = values;
        error = string.Empty;
        return true;
    }

    private static string FormatDirectToolResult(McpRuntimeToolCallResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.ContentText))
        {
            return result.Truncated ? result.ContentText + "\n[truncated]" : result.ContentText;
        }

        if (result.StructuredContent is { } structured)
        {
            var json = JsonSerializer.Serialize(structured);
            return result.Truncated ? json + "\n[truncated]" : json;
        }

        var summaries = result.Content
            .Select(static block => block.Summary)
            .Where(static summary => !string.IsNullOrWhiteSpace(summary))
            .ToArray();
        if (summaries.Length > 0)
        {
            var text = string.Join(Environment.NewLine, summaries);
            return result.Truncated ? text + "\n[truncated]" : text;
        }

        return result.Truncated ? "[truncated]" : "(empty MCP response)";
    }

    private static IReadOnlyList<PluginSystemPromptContribution> CreateDirectToolDiagnosticsPrompt(IReadOnlyList<McpRuntimeDiagnostic> diagnostics)
    {
        if (diagnostics.Count == 0)
        {
            return [];
        }

        var content = string.Join(Environment.NewLine, diagnostics.Take(5).Select(static diagnostic => "- " + diagnostic.Message));
        if (diagnostics.Count > 5)
        {
            content += Environment.NewLine + "- Additional MCP direct-tool diagnostics omitted; run `alta mcp status` or `alta mcp tool search` for details.";
        }

        return
        [
            new PluginSystemPromptContribution
            {
                Channel = PluginPromptChannel.Developer,
                Content = (_, _) => new ValueTask<string?>("Some MCP direct tools were unavailable for this run:\n" + content),
                Title = "MCP direct-tool diagnostics",
                Kind = PluginPromptPartKind.ToolGuidance,
                Order = 51,
            },
        ];
    }
}
