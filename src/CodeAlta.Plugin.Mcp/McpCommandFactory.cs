using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAlta.Plugins.Abstractions;
using XenoAtom.CommandLine;
using Command = XenoAtom.CommandLine.Command;

namespace CodeAlta.Plugin.Mcp;

internal sealed record McpCommandFactoryOptions
{
    public string? UserHomeDirectory { get; init; }
}

internal static class McpCommandFactory
{
    public static Command CreateCommand(PluginAltaCommandContext context)
        => CreateCommand(context, new McpCommandFactoryOptions());

    public static Command CreateCommand(PluginAltaCommandContext context, McpActivationState activationState)
        => CreateCommand(context, new McpCommandFactoryOptions(), activationState);

    internal static Command CreateCommand(PluginAltaCommandContext context, McpCommandFactoryOptions options)
        => CreateCommand(context, options, activationState: null);

    private static Command CreateCommand(PluginAltaCommandContext context, McpCommandFactoryOptions options, McpActivationState? activationState)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);
        var command = Group("mcp", "Inspect and manage configured Model Context Protocol servers.");
        if (activationState is not null)
        {
            command.Add(CreateActivateCommand(context, options, activationState));
        }

        command.Add(CreateListCommand(context, options));
        command.Add(CreateStatusCommand(context, options));
        command.Add(CreateConfigCommand(context, options));
        command.Add(CreateServerCommand(context, options));
        command.Add(CreateToolCommand(context, options));
        AddHelpText(
            command,
            "Configuration commands read fixed CodeAlta MCP config paths: project .alta/mcp.json and global ~/.alta/mcp.json.",
            "Tool commands lazily connect to configured stdio and HTTP/SSE MCP servers with bounded startup and tool-call timeouts.",
            "Examples: `alta mcp list`; `alta mcp tool search`; `alta mcp tool describe --server memory --tool read_graph`; `alta mcp tool call --server memory --tool echo --arguments {\"text\":\"hi\"}`.");
        return command;
    }

    private static Command CreateActivateCommand(PluginAltaCommandContext context, McpCommandFactoryOptions options, McpActivationState activationState)
    {
        var serverKeys = new List<string>();
        var command = Leaf("activate", "Activate configured MCP servers for the current session so their tools are registered on agent runs.");
        command.Add("<id>*", "MCP server ids to activate.", value =>
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                serverKeys.Add(value.Trim());
            }
        });
        command.Add((_, _) =>
        {
            if (serverKeys.Count == 0)
            {
                return ValueTask.FromResult(WriteError(context, "missing_server", "Specify at least one MCP server id to activate."));
            }

            var snapshot = Discover(context, options);
            var configured = snapshot.EffectiveServers
                .Select(static server => server.Definition.Key)
                .ToHashSet(StringComparer.Ordinal);
            var missing = serverKeys.Where(key => !configured.Contains(key)).Distinct(StringComparer.Ordinal).OrderBy(static key => key, StringComparer.Ordinal).ToArray();
            if (missing.Length > 0)
            {
                return ValueTask.FromResult(WriteError(context, "server_not_found", "MCP server(s) are not configured: " + string.Join(", ", missing)));
            }

            var projectDirectory = ResolveProjectDirectory(context);
            var scopeKey = ResolveActivationScopeKey(context, projectDirectory);
            var activated = activationState.ActivateServers(scopeKey, serverKeys);
            WriteRecord(context.Stdout, new
            {
                type = "alta.mcp.activate",
                version = 1,
                correlationId = context.CorrelationId,
                activatedServers = serverKeys.Distinct(StringComparer.Ordinal).OrderBy(static key => key, StringComparer.Ordinal).ToArray(),
                activeServers = activated,
                note = "MCP tools from active servers are registered on the next agent run as mcp__<server>__<tool>.",
            });
            return ValueTask.FromResult(0);
        });
        AddHelpText(command, "Examples: `alta mcp activate memory`; `alta mcp activate github docs`.");
        return command;
    }

    private static Command CreateListCommand(PluginAltaCommandContext context, McpCommandFactoryOptions options)
    {
        var command = Leaf("list", "List configured MCP servers without connecting to them.");
        command.Add((_, _) =>
        {
            var snapshot = Discover(context, options);
            foreach (var server in snapshot.EffectiveServers)
            {
                WriteRecord(context.Stdout, CreateServerRecord("alta.mcp.server", context, server));
            }

            return ValueTask.FromResult(0);
        });
        return command;
    }

    private static Command CreateToolCommand(PluginAltaCommandContext context, McpCommandFactoryOptions options)
    {
        var command = Group("tool", "Search, describe, and call tools from configured MCP servers.");
        command.Add(CreateToolSearchCommand(context, options));
        command.Add(CreateToolDescribeCommand(context, options));
        command.Add(CreateToolCallCommand(context, options));
        AddHelpText(
            command,
            "Tool commands lazily connect to stdio and HTTP/SSE MCP servers, list tools with a bounded startup timeout, and apply MCP policy filters.",
            "HTTP/SSE servers use headers from MCP JSON config; header values may reference environment variables with ${NAME}. OAuth and interactive auth flows are not implemented.");
        return command;
    }

    private static Command CreateToolSearchCommand(PluginAltaCommandContext context, McpCommandFactoryOptions options)
    {
        string? server = null;
        string? query = null;
        var command = Leaf("search", "Search enabled tools from configured MCP servers.");
        command.Add("server=", "Only search one MCP server key.", value => server = value);
        command.Add("query=", "Case-insensitive text to match server, tool name, title, or description.", value => query = value);
        command.Add(async (_, _) =>
        {
            await using var runtime = new McpRuntimeService();
            var result = await runtime.SearchToolsAsync(CreateRuntimeRequest(context, options), server, query, context.CancellationToken).ConfigureAwait(false);
            foreach (var diagnostic in result.Diagnostics)
            {
                WriteRecord(context.Stdout, CreateDiagnosticRecord(context, diagnostic));
            }

            foreach (var tool in result.Tools)
            {
                WriteRecord(context.Stdout, CreateToolRecord("alta.mcp.tool", context, tool));
            }

            WriteRecord(context.Stdout, new
            {
                type = "alta.mcp.tool.search",
                version = 1,
                correlationId = context.CorrelationId,
                server,
                query,
                matchingToolCount = result.Tools.Count,
                diagnosticCount = result.Diagnostics.Count,
            });
            return 0;
        });
        AddHelpText(command, "Examples: `alta mcp tool search`; `alta mcp tool search --server memory --query graph`.");
        return command;
    }

    private static Command CreateToolDescribeCommand(PluginAltaCommandContext context, McpCommandFactoryOptions options)
    {
        string? server = null;
        string? tool = null;
        var command = Leaf("describe", "Describe one enabled MCP tool.");
        command.Add("server=", "MCP server key.", value => server = value);
        command.Add("tool=", "MCP tool name on the server.", value => tool = value);
        command.Add(async (_, _) =>
        {
            var serverKey = RequireServerKey(server);
            var toolName = RequireToolName(tool);
            var diagnostics = new List<McpRuntimeDiagnostic>();
            await using var runtime = new McpRuntimeService();
            var result = await runtime.DescribeToolAsync(CreateRuntimeRequest(context, options), serverKey, toolName, diagnostics, context.CancellationToken).ConfigureAwait(false);
            foreach (var diagnostic in diagnostics)
            {
                WriteRecord(context.Stdout, CreateDiagnosticRecord(context, diagnostic));
            }

            if (result is null)
            {
                return 1;
            }

            WriteRecord(context.Stdout, CreateToolRecord("alta.mcp.tool.describe", context, result));
            return 0;
        });
        AddHelpText(command, "Example: `alta mcp tool describe --server memory --tool read_graph`.");
        return command;
    }

    private static Command CreateToolCallCommand(PluginAltaCommandContext context, McpCommandFactoryOptions options)
    {
        string? server = null;
        string? tool = null;
        string? argumentsJson = null;
        var command = Leaf("call", "Call one enabled MCP tool with a JSON object argument payload.");
        command.Add("server=", "MCP server key.", value => server = value);
        command.Add("tool=", "MCP tool name on the server.", value => tool = value);
        command.Add("arguments=", "JSON object with tool arguments. Defaults to {}.", value => argumentsJson = value);
        command.Add(async (_, _) =>
        {
            var serverKey = RequireServerKey(server);
            var toolName = RequireToolName(tool);
            if (!TryParseArguments(argumentsJson, out var arguments, out var errorMessage))
            {
                return WriteError(context, "invalid_arguments", errorMessage!);
            }

            var diagnostics = new List<McpRuntimeDiagnostic>();
            await using var runtime = new McpRuntimeService();
            var result = await runtime.CallToolAsync(CreateRuntimeRequest(context, options), serverKey, toolName, arguments, diagnostics, context.CancellationToken).ConfigureAwait(false);
            foreach (var diagnostic in diagnostics)
            {
                WriteRecord(context.Stdout, CreateDiagnosticRecord(context, diagnostic));
            }

            if (result is null)
            {
                return 1;
            }

            WriteRecord(context.Stdout, new
            {
                type = "alta.mcp.tool.call",
                version = 1,
                correlationId = context.CorrelationId,
                server = result.Server,
                tool = result.Name,
                alias = result.Alias,
                isError = result.IsError,
                contentText = result.ContentText,
                content = result.Content,
                structuredContent = result.StructuredContent,
                truncated = result.Truncated,
            });
            return result.IsError ? 2 : 0;
        });
        AddHelpText(command, "Example: `alta mcp tool call --server memory --tool echo --arguments {\"text\":\"hi\"}`.");
        return command;
    }

    private static Command CreateStatusCommand(PluginAltaCommandContext context, McpCommandFactoryOptions options)
    {
        string? serverFilter = null;
        var command = Leaf("status", "Show MCP configuration status without connecting to servers.");
        command.Add("server=", "Filter by MCP server key.", value => serverFilter = value);
        command.Add((_, _) =>
        {
            var snapshot = Discover(context, options);
            var effective = snapshot.EffectiveServers
                .Where(item => string.IsNullOrWhiteSpace(serverFilter) || string.Equals(item.Definition.Key, serverFilter, StringComparison.Ordinal))
                .ToArray();
            WriteRecord(context.Stdout, new
            {
                type = "alta.mcp.status",
                version = 1,
                correlationId = context.CorrelationId,
                configuredServerCount = snapshot.EffectiveServers.Count,
                matchingServerCount = effective.Length,
                invalidSourceCount = snapshot.Sources.Count(static source => source.Exists && !source.IsValid),
                shadowedGlobalServerCount = snapshot.ShadowedGlobalServers.Count,
                defaultWriteScope = FormatScope(snapshot.DefaultWriteScope),
                connectionRuntime = "not_started",
            });
            foreach (var source in snapshot.Sources.Where(static source => source.Exists && !source.IsValid))
            {
                WriteRecord(context.Stdout, CreateSourceRecord("alta.mcp.config.source", context, source, snapshot));
            }

            foreach (var server in effective)
            {
                WriteRecord(context.Stdout, CreateServerRecord("alta.mcp.status.server", context, server));
            }

            return ValueTask.FromResult(0);
        });
        return command;
    }

    private static Command CreateConfigCommand(PluginAltaCommandContext context, McpCommandFactoryOptions options)
    {
        var command = Group("config", "Inspect MCP configuration files.");
        command.Add(CreateConfigSourcesCommand(context, options));
        return command;
    }

    private static Command CreateConfigSourcesCommand(PluginAltaCommandContext context, McpCommandFactoryOptions options)
    {
        var includeMissing = false;
        var scope = "all";
        var command = Leaf("sources", "Show fixed global/project MCP config sources and overlay diagnostics.");
        command.Add("include-missing", "Include missing fixed config files in output.", value => includeMissing = value is not null);
        command.Add("scope=", "Source scope to show: all, project, or global.", value => scope = ValidateScope(value));
        command.Add((_, _) =>
        {
            var snapshot = Discover(context, options);
            foreach (var source in snapshot.Sources.Where(source => ShouldEmitSource(source, includeMissing, scope)))
            {
                WriteRecord(context.Stdout, CreateSourceRecord("alta.mcp.config.source", context, source, snapshot));
            }

            foreach (var effective in snapshot.EffectiveServers.Where(item => ShouldEmitEffective(item, scope)))
            {
                WriteRecord(context.Stdout, new
                {
                    type = "alta.mcp.config.effective_server",
                    version = 1,
                    correlationId = context.CorrelationId,
                    server = effective.Definition.Key,
                    sourceScope = FormatScope(effective.Definition.SourceScope),
                    sourcePath = effective.Definition.SourcePath,
                    overlay = effective.OverridesGlobal ? "project-overrides-global" : FormatScope(effective.Definition.SourceScope),
                    shadowedGlobalPath = effective.ShadowedGlobalDefinition?.SourcePath,
                });
            }

            foreach (var shadowed in snapshot.ShadowedGlobalServers.Where(server => scope is "all" or "global"))
            {
                WriteRecord(context.Stdout, new
                {
                    type = "alta.mcp.config.shadowed_server",
                    version = 1,
                    correlationId = context.CorrelationId,
                    server = shadowed.Key,
                    sourceScope = "global",
                    sourcePath = shadowed.SourcePath,
                    reason = "project-overrides-global",
                });
            }

            return ValueTask.FromResult(0);
        });
        AddHelpText(command, "Examples: `alta mcp config sources --include-missing`; `alta mcp config sources --scope project`.");
        return command;
    }

    private static Command CreateServerCommand(PluginAltaCommandContext context, McpCommandFactoryOptions options)
    {
        var command = Group("server", "Add, remove, enable, and disable MCP server definitions.");
        command.Add(CreateServerAddCommand(context, options));
        command.Add(CreateServerRemoveCommand(context, options));
        command.Add(CreateServerEnableCommand(context, options));
        command.Add(CreateServerDisableCommand(context, options));
        AddHelpText(
            command,
            "Add/remove update only fixed MCP JSON config files. Enable/disable updates CodeAlta TOML policy and preserves JSON server definitions.",
            "Examples:",
            "  `alta mcp server add memory --command npx --arg -y --arg @modelcontextprotocol/server-memory`",
            "  `alta mcp server add docs --url https://example.test/mcp --header Authorization=Bearer... --scope project`",
            "  `alta mcp server remove memory --scope project`",
            "  `alta mcp server disable docs`.");
        return command;
    }

    private static Command CreateServerAddCommand(PluginAltaCommandContext context, McpCommandFactoryOptions options)
    {
        string? serverKey = null;
        string? commandValue = null;
        string? url = null;
        string? cwd = null;
        string? scopeValue = null;
        var args = new List<string>();
        var envValues = new List<string>();
        var headerValues = new List<string>();
        var command = Leaf("add", "Add or update an MCP server in the selected JSON config scope.");
        command.Add("<server>", "MCP server key.", value => serverKey = value);
        command.Add("command=", "Stdio command to launch.", value => commandValue = value);
        command.Add("url=", "HTTP/SSE MCP endpoint URL.", value => url = value);
        command.Add("arg=", "Argument for a stdio server. Repeat for multiple arguments.", value => AddRequired(args, value, "--arg"));
        command.Add("cwd=", "Working directory for a stdio server.", value => cwd = value);
        command.Add("env=", "Environment entry KEY=VALUE for a stdio server. Repeat for multiple entries.", value => AddRequired(envValues, value, "--env"));
        command.Add("header=", "HTTP header KEY=VALUE for a remote server. Repeat for multiple entries.", value => AddRequired(headerValues, value, "--header"));
        command.Add("scope=", "Write scope: project or global. Defaults to project when available, otherwise global.", value => scopeValue = ValidateWriteScope(value));
        command.Add(async (_, _) =>
        {
            var key = RequireServerKey(serverKey);
            if (string.IsNullOrWhiteSpace(commandValue) == string.IsNullOrWhiteSpace(url))
            {
                return WriteError(context, "invalid_transport", "Specify exactly one of --command or --url.");
            }

            if (!string.IsNullOrWhiteSpace(commandValue) && headerValues.Count > 0)
            {
                return WriteError(context, "invalid_transport_fields", "--header can only be used with --url MCP servers.");
            }

            if (!string.IsNullOrWhiteSpace(url) && (args.Count > 0 || envValues.Count > 0 || !string.IsNullOrWhiteSpace(cwd)))
            {
                return WriteError(context, "invalid_transport_fields", "--arg, --env, and --cwd can only be used with --command MCP servers.");
            }

            var target = ResolveJsonWriteTarget(context, options, scopeValue, requireExplicitGlobalWhenProjectExists: false, serverKey: null);
            if (target.ErrorMessage is not null)
            {
                return WriteError(context, "invalid_scope", target.ErrorMessage);
            }

            try
            {
                var definition = new McpServerDefinition
                {
                    Key = key,
                    Transport = string.IsNullOrWhiteSpace(url) ? McpTransportKind.Stdio : McpTransportKind.Http,
                    SourceScope = target.Scope,
                    SourcePath = target.Path,
                    SourceFlavor = McpConfigFlavor.CodeAlta,
                    Command = string.IsNullOrWhiteSpace(commandValue) ? null : commandValue,
                    Args = args.ToArray(),
                    Cwd = string.IsNullOrWhiteSpace(cwd) ? null : cwd,
                    Env = ParseAssignments(envValues, "--env"),
                    Url = string.IsNullOrWhiteSpace(url) ? null : url,
                    Headers = ParseAssignments(headerValues, "--header"),
                };
                var result = await new McpConfigWriter().AddOrUpdateServerAsync(target.Path, target.Scope, definition, context.CancellationToken).ConfigureAwait(false);
                WriteRecord(context.Stdout, new
                {
                    type = "alta.mcp.server.add",
                    version = 1,
                    correlationId = context.CorrelationId,
                    server = key,
                    scope = FormatScope(result.Scope),
                    path = result.Path,
                    format = FormatFlavor(result.Flavor),
                    transport = definition.Transport == McpTransportKind.Stdio ? "stdio" : "http",
                    createdFile = result.CreatedFile,
                    changed = result.Changed,
                });
                return 0;
            }
            catch (Exception ex) when (IsUserConfigException(ex))
            {
                return WriteError(context, "config_write_failed", ex.Message);
            }
        });
        return command;
    }

    private static Command CreateServerRemoveCommand(PluginAltaCommandContext context, McpCommandFactoryOptions options)
    {
        string? serverKey = null;
        string? scopeValue = null;
        var command = Leaf("remove", "Remove an MCP server from one JSON config scope.");
        command.Add("<server>", "MCP server key.", value => serverKey = value);
        command.Add("scope=", "Write scope: project or global. Defaults to the project overlay entry when present.", value => scopeValue = ValidateWriteScope(value));
        command.Add(async (_, _) =>
        {
            var key = RequireServerKey(serverKey);
            var target = ResolveJsonWriteTarget(context, options, scopeValue, requireExplicitGlobalWhenProjectExists: true, key);
            if (target.ErrorMessage is not null)
            {
                return WriteError(context, "invalid_scope", target.ErrorMessage);
            }

            try
            {
                var result = await new McpConfigWriter().RemoveServerAsync(target.Path, target.Scope, key, context.CancellationToken).ConfigureAwait(false);
                WriteRecord(context.Stdout, new
                {
                    type = "alta.mcp.server.remove",
                    version = 1,
                    correlationId = context.CorrelationId,
                    server = key,
                    scope = FormatScope(result.Scope),
                    path = result.Path,
                    format = FormatFlavor(result.Flavor),
                    changed = result.Changed,
                });
                return 0;
            }
            catch (Exception ex) when (IsUserConfigException(ex))
            {
                return WriteError(context, "config_write_failed", ex.Message);
            }
        });
        return command;
    }

    private static Command CreateServerEnableCommand(PluginAltaCommandContext context, McpCommandFactoryOptions options)
        => CreateServerPolicyCommand(context, options, "enable", true, "Enable an MCP server in CodeAlta TOML policy while preserving its JSON definition.");

    private static Command CreateServerDisableCommand(PluginAltaCommandContext context, McpCommandFactoryOptions options)
        => CreateServerPolicyCommand(context, options, "disable", false, "Disable an MCP server in CodeAlta TOML policy while preserving its JSON definition.");

    private static Command CreateServerPolicyCommand(PluginAltaCommandContext context, McpCommandFactoryOptions options, string name, bool enabled, string description)
    {
        string? serverKey = null;
        string? scopeValue = null;
        var global = false;
        var command = Leaf(name, description);
        command.Add("<server>", "MCP server key.", value => serverKey = value);
        command.Add("scope=", "Policy scope: project or global. Defaults to project when available, otherwise global.", value => scopeValue = ValidateWriteScope(value));
        command.Add("global", "Write global ~/.alta/config.toml policy.", value => global = value is not null);
        command.Add(async (_, _) =>
        {
            var key = RequireServerKey(serverKey);
            if (global && string.Equals(scopeValue, "project", StringComparison.OrdinalIgnoreCase))
            {
                return WriteError(context, "invalid_scope", "Use either --global or --scope project, not both.");
            }

            var target = ResolvePolicyWriteTarget(context, options, global ? "global" : scopeValue);
            if (target.ErrorMessage is not null)
            {
                return WriteError(context, "invalid_scope", target.ErrorMessage);
            }

            try
            {
                var result = await new McpPolicyWriter().SetServerEnabledAsync(target.Path, target.Scope, key, enabled, context.CancellationToken).ConfigureAwait(false);
                WriteRecord(context.Stdout, new
                {
                    type = enabled ? "alta.mcp.server.enable" : "alta.mcp.server.disable",
                    version = 1,
                    correlationId = context.CorrelationId,
                    server = key,
                    enabled = result.Enabled,
                    scope = FormatScope(result.Scope),
                    path = result.Path,
                    createdFile = result.CreatedFile,
                    changed = result.Changed,
                });
                return 0;
            }
            catch (Exception ex) when (IsUserConfigException(ex))
            {
                return WriteError(context, "policy_write_failed", ex.Message);
            }
        });
        return command;
    }

    private static McpConfigSnapshot Discover(PluginAltaCommandContext context, McpCommandFactoryOptions options)
    {
        var projectPath = ResolveProjectDirectory(context);
        return new McpConfigDiscovery().Discover(new McpConfigPathOptions { UserHomeDirectory = options.UserHomeDirectory, ProjectDirectory = projectPath });
    }

    private static string? ResolveProjectDirectory(PluginAltaCommandContext context)
        => McpPlugin.ResolveProjectPath(
            context.Services.Workspace.SelectedProjectPath,
            context.ScopeProjectPath,
            context.WorkingDirectory);

    private static string ResolveActivationScopeKey(PluginAltaCommandContext context, string? projectDirectory)
        => McpActivationState.ResolveScopeKey(
            string.IsNullOrWhiteSpace(context.SourceSessionId) ? context.Services.Sessions.SelectedSessionId : context.SourceSessionId,
            projectDirectory);

    private static McpRuntimeRequest CreateRuntimeRequest(PluginAltaCommandContext context, McpCommandFactoryOptions options)
        => new() { UserHomeDirectory = options.UserHomeDirectory, ProjectDirectory = ResolveProjectDirectory(context) };

    private static McpWriteTarget ResolveJsonWriteTarget(
        PluginAltaCommandContext context,
        McpCommandFactoryOptions options,
        string? scopeValue,
        bool requireExplicitGlobalWhenProjectExists,
        string? serverKey)
    {
        var projectDirectory = ResolveProjectDirectory(context);
        var snapshot = Discover(context, options);
        var scope = ResolveJsonWriteScope(scopeValue, projectDirectory, snapshot, requireExplicitGlobalWhenProjectExists, serverKey);
        if (scope.ErrorMessage is not null)
        {
            return new McpWriteTarget(McpConfigScope.Global, string.Empty, scope.ErrorMessage);
        }

        var path = scope.Scope == McpConfigScope.Project
            ? McpConfigDiscovery.GetProjectConfigPath(projectDirectory!)
            : McpConfigDiscovery.GetGlobalConfigPath(options.UserHomeDirectory);
        return new McpWriteTarget(scope.Scope, path, null);
    }

    private static McpScopeResolution ResolveJsonWriteScope(
        string? scopeValue,
        string? projectDirectory,
        McpConfigSnapshot snapshot,
        bool requireExplicitGlobalWhenProjectExists,
        string? serverKey)
    {
        if (!string.IsNullOrWhiteSpace(scopeValue))
        {
            var explicitScope = ParseScope(scopeValue);
            if (explicitScope == McpConfigScope.Project && string.IsNullOrWhiteSpace(projectDirectory))
            {
                return new McpScopeResolution(McpConfigScope.Project, "Project scope requires an invocation project directory.");
            }

            return new McpScopeResolution(explicitScope, null);
        }

        if (!string.IsNullOrWhiteSpace(projectDirectory))
        {
            if (!string.IsNullOrWhiteSpace(serverKey))
            {
                var projectHasServer = snapshot.Sources.Any(source => source.Scope == McpConfigScope.Project && source.IsValid && source.Servers.Any(server => string.Equals(server.Key, serverKey, StringComparison.Ordinal)));
                if (projectHasServer)
                {
                    return new McpScopeResolution(McpConfigScope.Project, null);
                }

                var globalHasServer = snapshot.Sources.Any(source => source.Scope == McpConfigScope.Global && source.IsValid && source.Servers.Any(server => string.Equals(server.Key, serverKey, StringComparison.Ordinal)));
                if (globalHasServer && requireExplicitGlobalWhenProjectExists)
                {
                    return new McpScopeResolution(McpConfigScope.Global, "The server exists only in global MCP config. Use --scope global to remove it from inside a project.");
                }
            }

            return new McpScopeResolution(McpConfigScope.Project, null);
        }

        return new McpScopeResolution(McpConfigScope.Global, null);
    }

    private static McpWriteTarget ResolvePolicyWriteTarget(PluginAltaCommandContext context, McpCommandFactoryOptions options, string? scopeValue)
    {
        var projectDirectory = ResolveProjectDirectory(context);
        var scope = string.IsNullOrWhiteSpace(scopeValue)
            ? string.IsNullOrWhiteSpace(projectDirectory) ? McpConfigScope.Global : McpConfigScope.Project
            : ParseScope(scopeValue);
        if (scope == McpConfigScope.Project && string.IsNullOrWhiteSpace(projectDirectory))
        {
            return new McpWriteTarget(McpConfigScope.Project, string.Empty, "Project policy scope requires an invocation project directory.");
        }

        var path = scope == McpConfigScope.Project
            ? McpPolicyWriter.GetProjectPolicyPath(projectDirectory!)
            : McpPolicyWriter.GetGlobalPolicyPath(options.UserHomeDirectory);
        return new McpWriteTarget(scope, path, null);
    }

    private static bool ShouldEmitSource(McpConfigSource source, bool includeMissing, string scope)
        => (includeMissing || source.Exists) &&
           (scope == "all" || string.Equals(scope, FormatScope(source.Scope), StringComparison.OrdinalIgnoreCase));

    private static bool ShouldEmitEffective(McpEffectiveServer effective, string scope)
        => scope == "all" || string.Equals(scope, FormatScope(effective.Definition.SourceScope), StringComparison.OrdinalIgnoreCase);

    private static object CreateServerRecord(string type, PluginAltaCommandContext context, McpEffectiveServer server)
    {
        var definition = server.Definition;
        return new
        {
            type,
            version = 1,
            correlationId = context.CorrelationId,
            server = definition.Key,
            transport = definition.Transport == McpTransportKind.Stdio ? "stdio" : "http",
            status = "configured",
            sourceScope = FormatScope(definition.SourceScope),
            sourcePath = definition.SourcePath,
            sourceFormat = FormatFlavor(definition.SourceFlavor),
            overlay = server.OverridesGlobal ? "project-overrides-global" : FormatScope(definition.SourceScope),
            command = definition.Transport == McpTransportKind.Stdio ? definition.Command : null,
            args = definition.Transport == McpTransportKind.Stdio && definition.Args.Count > 0 ? McpRedactor.RedactArguments(definition.Args) : null,
            cwd = definition.Cwd,
            env = definition.Env.Count > 0 ? McpRedactor.RedactDictionary(definition.Env) : null,
            url = definition.Transport == McpTransportKind.Http ? McpRedactor.RedactUrl(definition.Url) : null,
            headers = definition.Headers.Count > 0 ? McpRedactor.RedactDictionary(definition.Headers) : null,
            shadowedGlobalPath = server.ShadowedGlobalDefinition?.SourcePath,
        };
    }

    private static object CreateToolRecord(string type, PluginAltaCommandContext context, McpRuntimeTool tool)
        => new
        {
            type,
            version = 1,
            correlationId = context.CorrelationId,
            server = tool.Server,
            tool = tool.Name,
            alias = tool.Alias,
            title = tool.Title,
            description = tool.Description,
            enabled = tool.Enabled,
            disabledReason = tool.DisabledReason,
            inputSchema = tool.InputSchema,
            outputSchema = tool.OutputSchema,
        };

    private static object CreateDiagnosticRecord(PluginAltaCommandContext context, McpRuntimeDiagnostic diagnostic)
        => new
        {
            type = "alta.mcp.tool.diagnostic",
            version = 1,
            correlationId = context.CorrelationId,
            code = diagnostic.Code,
            message = diagnostic.Message,
            server = diagnostic.Server,
            tool = diagnostic.Tool,
            transport = diagnostic.Transport,
        };

    private static object CreateSourceRecord(string type, PluginAltaCommandContext context, McpConfigSource source, McpConfigSnapshot snapshot)
        => new
        {
            type,
            version = 1,
            correlationId = context.CorrelationId,
            scope = FormatScope(source.Scope),
            path = source.Path,
            exists = source.Exists,
            directoryExists = source.DirectoryExists,
            writable = source.IsWritable,
            valid = source.IsValid,
            format = source.Flavor is { } flavor ? FormatFlavor(flavor) : null,
            rootKey = source.RootKey,
            serverKeys = source.Servers.Select(static server => server.Key).OrderBy(static key => key, StringComparer.Ordinal).ToArray(),
            diagnostic = source.Diagnostic,
            defaultWriteScope = FormatScope(snapshot.DefaultWriteScope),
            wouldCreateParentDirectory = !source.DirectoryExists,
        };

    private static string ValidateScope(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "all";
        }

        var normalized = value.Trim().ToLowerInvariant();
        return normalized is "all" or "project" or "global"
            ? normalized
            : throw new CommandOptionException("Scope must be 'all', 'project', or 'global'.", "--scope");
    }

    private static string ValidateWriteScope(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Trim().ToLowerInvariant();
        return normalized is "project" or "global"
            ? normalized
            : throw new CommandOptionException("Scope must be 'project' or 'global'.", "--scope");
    }

    private static McpConfigScope ParseScope(string value)
        => string.Equals(value, "project", StringComparison.OrdinalIgnoreCase) ? McpConfigScope.Project : McpConfigScope.Global;

    private static string RequireServerKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new CommandOptionException("A non-empty MCP server key is required.", "<server>");
        }

        return value.Trim();
    }

    private static string RequireToolName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new CommandOptionException("A non-empty MCP tool name is required.", "--tool");
        }

        return value.Trim();
    }

    private static bool TryParseArguments(string? json, out IReadOnlyDictionary<string, object?> arguments, out string? errorMessage)
    {
        arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
        errorMessage = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                errorMessage = "--arguments must be a JSON object.";
                return false;
            }

            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                result[property.Name] = property.Value.Clone();
            }

            arguments = result;
            return true;
        }
        catch (JsonException ex)
        {
            errorMessage = $"--arguments must be valid JSON: {ex.Message}";
            return false;
        }
    }

    private static void AddRequired(List<string> values, string? value, string optionName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new CommandOptionException($"{optionName} requires a non-empty value.", optionName);
        }

        values.Add(value);
    }

    private static IReadOnlyDictionary<string, string> ParseAssignments(IEnumerable<string> values, string optionName)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            var separatorIndex = value.IndexOf('=', StringComparison.Ordinal);
            if (separatorIndex <= 0)
            {
                throw new CommandOptionException($"{optionName} values must use KEY=VALUE syntax.", optionName);
            }

            var key = value[..separatorIndex].Trim();
            if (key.Length == 0)
            {
                throw new CommandOptionException($"{optionName} values must include a non-empty key.", optionName);
            }

            result[key] = value[(separatorIndex + 1)..];
        }

        return result;
    }

    private static int WriteError(PluginAltaCommandContext context, string code, string message)
    {
        WriteRecord(context.Stdout, new
        {
            type = "alta.mcp.error",
            version = 1,
            correlationId = context.CorrelationId,
            code,
            message,
        });
        return 1;
    }

    private static bool IsUserConfigException(Exception exception)
        => exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException;

    private static string FormatScope(McpConfigScope scope)
        => scope == McpConfigScope.Project ? "project" : "global";

    private static string FormatFlavor(McpConfigFlavor flavor)
        => flavor switch
        {
            McpConfigFlavor.CodeAlta => "codealta",
            McpConfigFlavor.Copilot => "copilot",
            McpConfigFlavor.Vscode => "vscode",
            McpConfigFlavor.Claude => "claude",
            McpConfigFlavor.Intellij => "intellij",
            _ => flavor.ToString().ToLowerInvariant(),
        };

    private static Command Group(string name, string description)
        => new(name, description)
        {
            new CommandUsage(),
            new HelpOption(),
        };

    private static Command Leaf(string name, string description)
        => new(name, description)
        {
            new CommandUsage(),
            new HelpOption(),
        };

    private static void AddHelpText(Command command, params string[] lines)
    {
        command.Add("");
        foreach (var line in lines)
        {
            command.Add(line);
        }
    }

    private static void WriteRecord(TextWriter writer, object record)
    {
        writer.Write(JsonSerializer.Serialize(record, CreateJsonOptions()));
        writer.WriteLine();
    }

    private static JsonSerializerOptions CreateJsonOptions()
        => new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

    private readonly record struct McpWriteTarget(McpConfigScope Scope, string Path, string? ErrorMessage);

    private readonly record struct McpScopeResolution(McpConfigScope Scope, string? ErrorMessage);
}
