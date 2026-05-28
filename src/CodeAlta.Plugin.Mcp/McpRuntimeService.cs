using System.ComponentModel;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeAlta.Plugin.Mcp;

internal sealed record McpRuntimeRequest
{
    public string? ProjectDirectory { get; init; }

    public string? UserHomeDirectory { get; init; }
}

internal sealed record McpRuntimeDiagnostic
{
    public required string Code { get; init; }

    public required string Message { get; init; }

    public string? Server { get; init; }

    public string? Tool { get; init; }

    public string? Transport { get; init; }
}

internal sealed record McpRuntimeTool
{
    public required string Server { get; init; }

    public required string Name { get; init; }

    public required string Alias { get; init; }

    public string? Title { get; init; }

    public string? Description { get; init; }

    public required JsonElement InputSchema { get; init; }

    public JsonElement? OutputSchema { get; init; }

    public bool Enabled { get; init; } = true;

    public string? DisabledReason { get; init; }
}

internal sealed record McpRuntimeToolSearchResult
{
    public IReadOnlyList<McpRuntimeTool> Tools { get; init; } = [];

    public IReadOnlyList<McpRuntimeDiagnostic> Diagnostics { get; init; } = [];
}

internal sealed record McpRuntimeServerTestResult
{
    public required string Server { get; init; }

    public IReadOnlyList<McpRuntimeTool> Tools { get; init; } = [];

    public IReadOnlyList<McpRuntimeDiagnostic> Diagnostics { get; init; } = [];
}

internal sealed record McpRuntimeToolCallResult
{
    public required string Server { get; init; }

    public required string Name { get; init; }

    public required string Alias { get; init; }

    public bool IsError { get; init; }

    public IReadOnlyList<McpRuntimeContentBlock> Content { get; init; } = [];

    public string? ContentText { get; init; }

    public JsonElement? StructuredContent { get; init; }

    public bool Truncated { get; init; }
}

internal sealed record McpRuntimeContentBlock
{
    public required string Type { get; init; }

    public string? Text { get; init; }

    public string? MimeType { get; init; }

    public string? Summary { get; init; }
}

internal sealed class McpRuntimeService : IAsyncDisposable
{
    private readonly McpConfigDiscovery _discovery = new();
    private readonly McpPolicyLoader _policyLoader = new();
    private readonly Dictionary<string, ServerRuntimeState> _servers = new(StringComparer.Ordinal);

    public async ValueTask DisposeAsync()
    {
        foreach (var state in _servers.Values)
        {
            if (state.Client is not null)
            {
                await state.Client.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public async Task<McpRuntimeToolSearchResult> SearchToolsAsync(
        McpRuntimeRequest request,
        string? serverFilter,
        string? query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var context = LoadContext(request);
        var diagnostics = new List<McpRuntimeDiagnostic>();
        var tools = new List<McpRuntimeTool>();
        var matchedServer = false;
        foreach (var server in context.Config.EffectiveServers)
        {
            if (!MatchesServer(server.Definition.Key, serverFilter))
            {
                continue;
            }

            matchedServer = true;
            var state = await GetServerStateAsync(context, server, cancellationToken).ConfigureAwait(false);
            if (state.Diagnostic is not null)
            {
                diagnostics.Add(state.Diagnostic);
                continue;
            }

            foreach (var tool in state.Tools)
            {
                if (!tool.Enabled || !MatchesQuery(tool, query))
                {
                    continue;
                }

                tools.Add(tool);
            }
        }

        if (!matchedServer && !string.IsNullOrWhiteSpace(serverFilter))
        {
            diagnostics.Add(new McpRuntimeDiagnostic
            {
                Code = "server_not_found",
                Server = serverFilter.Trim(),
                Message = $"MCP server '{serverFilter.Trim()}' is not configured in the effective MCP configuration.",
            });
        }

        return new McpRuntimeToolSearchResult
        {
            Tools = tools
                .OrderBy(static tool => tool.Server, StringComparer.Ordinal)
                .ThenBy(static tool => tool.Name, StringComparer.Ordinal)
                .ToArray(),
            Diagnostics = diagnostics,
        };
    }

    public async Task<McpRuntimeToolSearchResult> ListDirectToolsAsync(
        McpRuntimeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var context = LoadContext(request);
        var diagnostics = new List<McpRuntimeDiagnostic>();
        var candidates = new List<DirectToolCandidate>();
        foreach (var server in context.Config.EffectiveServers)
        {
            context.Policy.Servers.TryGetValue(server.Definition.Key, out var serverPolicy);
            var state = await GetServerStateAsync(context, server, cancellationToken).ConfigureAwait(false);
            if (state.Diagnostic is not null)
            {
                diagnostics.Add(state.Diagnostic);
                continue;
            }

            foreach (var tool in state.Tools)
            {
                if (tool.Enabled)
                {
                    candidates.Add(new DirectToolCandidate(tool, serverPolicy));
                }
            }
        }

        var enabledToolCount = candidates.Count;
        return new McpRuntimeToolSearchResult
        {
            Tools = candidates
                .Where(candidate => ShouldExposeDirectTool(context.Policy, candidate.ServerPolicy, candidate.Tool, enabledToolCount))
                .Select(static candidate => candidate.Tool)
                .OrderBy(static tool => tool.Server, StringComparer.Ordinal)
                .ThenBy(static tool => tool.Name, StringComparer.Ordinal)
                .ToArray(),
            Diagnostics = diagnostics,
        };
    }

    public async Task<McpRuntimeServerTestResult> TestServerAsync(
        McpRuntimeRequest request,
        string serverKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverKey);
        var key = serverKey.Trim();
        var context = LoadContext(request);
        var effective = context.Config.EffectiveServers.SingleOrDefault(server => string.Equals(server.Definition.Key, key, StringComparison.Ordinal));
        if (effective is null)
        {
            return new McpRuntimeServerTestResult
            {
                Server = key,
                Diagnostics =
                [
                    new McpRuntimeDiagnostic
                    {
                        Code = "server_not_found",
                        Server = key,
                        Message = $"MCP server '{key}' is not configured in the effective MCP configuration.",
                    },
                ],
            };
        }

        var state = await GetServerStateAsync(context, effective, cancellationToken).ConfigureAwait(false);
        if (state.Diagnostic is not null)
        {
            return new McpRuntimeServerTestResult
            {
                Server = key,
                Diagnostics = [state.Diagnostic],
            };
        }

        return new McpRuntimeServerTestResult
        {
            Server = key,
            Tools = state.Tools
                .OrderBy(static tool => tool.Name, StringComparer.Ordinal)
                .ToArray(),
        };
    }

    public async Task<McpRuntimeTool?> DescribeToolAsync(
        McpRuntimeRequest request,
        string serverKey,
        string toolName,
        IList<McpRuntimeDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentNullException.ThrowIfNull(diagnostics);
        var context = LoadContext(request);
        var state = await ResolveToolStateAsync(context, serverKey.Trim(), toolName.Trim(), diagnostics, cancellationToken).ConfigureAwait(false);
        return state?.Tool;
    }

    public async Task<McpRuntimeToolCallResult?> CallToolAsync(
        McpRuntimeRequest request,
        string serverKey,
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        IList<McpRuntimeDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(diagnostics);
        var context = LoadContext(request);
        var resolved = await ResolveToolStateAsync(context, serverKey.Trim(), toolName.Trim(), diagnostics, cancellationToken).ConfigureAwait(false);
        if (resolved is null)
        {
            return null;
        }

        var timeoutMs = NormalizeTimeout(resolved.ServerPolicy?.ToolTimeoutMs ?? context.Policy.ToolTimeoutMs, 60000);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));
        try
        {
            var result = await resolved.State.Client!.CallToolAsync(resolved.Tool.Name, arguments, cancellationToken: timeout.Token).ConfigureAwait(false);
            return ConvertToolResult(resolved.Tool, result, context.Policy.MaxToolOutputChars);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            diagnostics.Add(new McpRuntimeDiagnostic
            {
                Code = "tool_timeout",
                Server = serverKey.Trim(),
                Tool = toolName.Trim(),
                Message = $"MCP tool '{toolName.Trim()}' on server '{serverKey.Trim()}' did not finish within {timeoutMs} ms.",
            });
            return null;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && IsRuntimeException(ex))
        {
            diagnostics.Add(new McpRuntimeDiagnostic
            {
                Code = "tool_call_failed",
                Server = serverKey.Trim(),
                Tool = toolName.Trim(),
                Message = McpRedactor.RedactValue(null, ex.GetBaseException().Message),
            });
            return null;
        }
    }

    private RuntimeContext LoadContext(McpRuntimeRequest request)
    {
        var projectDirectory = string.IsNullOrWhiteSpace(request.ProjectDirectory) ? null : Path.GetFullPath(request.ProjectDirectory);
        var config = _discovery.Discover(new McpConfigPathOptions
        {
            ProjectDirectory = projectDirectory,
            UserHomeDirectory = request.UserHomeDirectory,
        });
        var globalPolicyPath = McpPolicyWriter.GetGlobalPolicyPath(request.UserHomeDirectory);
        var projectPolicyPath = projectDirectory is null ? null : McpPolicyWriter.GetProjectPolicyPath(projectDirectory);
        var policy = _policyLoader.Load(globalPolicyPath, projectPolicyPath);
        return new RuntimeContext(config, policy, CreateServerAliasPartMap(config.EffectiveServers));
    }

    private async Task<ServerRuntimeState> GetServerStateAsync(RuntimeContext context, McpEffectiveServer effective, CancellationToken cancellationToken)
    {
        var key = effective.Definition.Key;
        var serverAliasPart = context.ServerAliasParts.TryGetValue(key, out var aliasPart) ? aliasPart : SanitizeAliasPart(key);
        var cacheKey = CreateServerCacheKey(effective.Definition, serverAliasPart);
        if (_servers.TryGetValue(cacheKey, out var existing))
        {
            return existing;
        }

        if (!IsServerEnabled(context.Policy, key, out var serverPolicy))
        {
            return Cache(cacheKey, ServerRuntimeState.Failed(new McpRuntimeDiagnostic
            {
                Code = "server_disabled",
                Server = key,
                Message = $"MCP server '{key}' is disabled by policy.",
            }));
        }

        var validation = ValidateRuntimeDefinition(effective.Definition);
        if (validation is not null)
        {
            return Cache(cacheKey, ServerRuntimeState.Failed(validation));
        }

        var timeoutMs = NormalizeTimeout(serverPolicy?.StartupTimeoutMs ?? context.Policy.StartupTimeoutMs, 30000);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));
        try
        {
            var transport = CreateTransport(effective.Definition, timeoutMs);
            var client = await McpClient.CreateAsync(transport, loggerFactory: NullLoggerFactory.Instance, cancellationToken: timeout.Token).ConfigureAwait(false);
            var listedTools = await client.ListToolsAsync(cancellationToken: timeout.Token).ConfigureAwait(false);
            var tools = ConvertTools(key, serverAliasPart, listedTools.ToArray(), context.Policy, serverPolicy);
            return Cache(cacheKey, ServerRuntimeState.Connected(client, tools));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Cache(cacheKey, ServerRuntimeState.Failed(new McpRuntimeDiagnostic
            {
                Code = "server_startup_timeout",
                Server = key,
                Transport = FormatTransport(effective.Definition.Transport),
                Message = $"MCP {FormatTransport(effective.Definition.Transport)} server '{key}' did not finish startup/tool discovery within {timeoutMs} ms.",
            }));
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && IsRuntimeException(ex))
        {
            return Cache(cacheKey, ServerRuntimeState.Failed(CreateUnavailableDiagnostic(effective.Definition, ex)));
        }
    }

    private static IClientTransport CreateTransport(McpServerDefinition definition, int timeoutMs)
    {
        if (definition.Transport == McpTransportKind.Stdio)
        {
            var options = new StdioClientTransportOptions
            {
                Name = definition.Key,
                Command = definition.Command!,
                Arguments = definition.Args.ToArray(),
                WorkingDirectory = definition.Cwd,
                EnvironmentVariables = ExpandDictionary(definition.Env).ToDictionary(static pair => pair.Key, static pair => (string?)pair.Value, StringComparer.Ordinal),
                ShutdownTimeout = CreateStdioShutdownTimeout(timeoutMs),
            };
            return new StdioClientTransport(options, NullLoggerFactory.Instance);
        }

        var headers = ExpandDictionary(definition.Headers).ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
        var httpOptions = new HttpClientTransportOptions
        {
            Name = definition.Key,
            Endpoint = new Uri(definition.Url!, UriKind.Absolute),
            TransportMode = HttpTransportMode.AutoDetect,
            ConnectionTimeout = TimeSpan.FromMilliseconds(timeoutMs),
            AdditionalHeaders = headers,
        };
        return new HttpClientTransport(httpOptions, NullLoggerFactory.Instance);
    }

    private async Task<ResolvedToolState?> ResolveToolStateAsync(RuntimeContext context, string serverKey, string toolName, IList<McpRuntimeDiagnostic> diagnostics, CancellationToken cancellationToken)
    {
        var effective = context.Config.EffectiveServers.SingleOrDefault(server => string.Equals(server.Definition.Key, serverKey, StringComparison.Ordinal));
        if (effective is null)
        {
            diagnostics.Add(new McpRuntimeDiagnostic
            {
                Code = "server_not_found",
                Server = serverKey,
                Message = $"MCP server '{serverKey}' is not configured in the effective MCP configuration.",
            });
            return null;
        }

        context.Policy.Servers.TryGetValue(serverKey, out var serverPolicy);
        var state = await GetServerStateAsync(context, effective, cancellationToken).ConfigureAwait(false);
        if (state.Diagnostic is not null)
        {
            diagnostics.Add(state.Diagnostic);
            return null;
        }

        var tool = state.Tools.SingleOrDefault(candidate => string.Equals(candidate.Name, toolName, StringComparison.Ordinal));
        if (tool is null)
        {
            diagnostics.Add(new McpRuntimeDiagnostic
            {
                Code = "tool_not_found",
                Server = serverKey,
                Tool = toolName,
                Message = $"MCP tool '{toolName}' is not available on server '{serverKey}'.",
            });
            return null;
        }

        if (!tool.Enabled)
        {
            diagnostics.Add(new McpRuntimeDiagnostic
            {
                Code = "tool_disabled",
                Server = serverKey,
                Tool = toolName,
                Message = tool.DisabledReason ?? $"MCP tool '{toolName}' on server '{serverKey}' is disabled by policy.",
            });
            return null;
        }

        return new ResolvedToolState(state, tool, serverPolicy);
    }

    private ServerRuntimeState Cache(string key, ServerRuntimeState state)
    {
        _servers[key] = state;
        return state;
    }

    private static string CreateServerCacheKey(McpServerDefinition definition, string serverAliasPart)
        => string.Join(
            '\0',
            definition.Key,
            serverAliasPart,
            definition.SourcePath,
            definition.Transport.ToString(),
            definition.Command ?? string.Empty,
            definition.Url ?? string.Empty,
            string.Join('\0', definition.Args),
            definition.Cwd ?? string.Empty,
            JoinDictionary(definition.Env),
            JoinDictionary(definition.Headers));

    private static string JoinDictionary(IReadOnlyDictionary<string, string> values)
        => string.Join(
            '\0',
            values
                .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(static pair => string.Concat(pair.Key, "\0", pair.Value)));

    private static McpRuntimeDiagnostic? ValidateRuntimeDefinition(McpServerDefinition definition)
    {
        if (definition.Transport == McpTransportKind.Stdio)
        {
            if (string.IsNullOrWhiteSpace(definition.Command))
            {
                return InvalidTransport(definition, "A stdio MCP server must define a non-empty command.");
            }

            if (!string.IsNullOrWhiteSpace(definition.Url) || definition.Headers.Count > 0)
            {
                return InvalidTransport(definition, "Stdio MCP servers cannot include URL or header fields.");
            }

            if (TryCreateVariableDiagnostic(definition, definition.Env, "environment variable", out var variableDiagnostic))
            {
                return variableDiagnostic;
            }

            return null;
        }

        if (!string.IsNullOrWhiteSpace(definition.Command) || definition.Args.Count > 0 || !string.IsNullOrWhiteSpace(definition.Cwd) || definition.Env.Count > 0)
        {
            return InvalidTransport(definition, "HTTP/SSE MCP servers cannot include stdio command, args, cwd, or env fields.");
        }

        if (string.IsNullOrWhiteSpace(definition.Url))
        {
            return InvalidTransport(definition, "HTTP/SSE MCP servers must define a non-empty URL.");
        }

        if (!Uri.TryCreate(definition.Url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return new McpRuntimeDiagnostic
            {
                Code = "invalid_http_url",
                Server = definition.Key,
                Transport = "http",
                Message = $"Invalid MCP server '{definition.Key}': HTTP/SSE URL must be an absolute http or https URL ({McpRedactor.RedactUrl(definition.Url)}).",
            };
        }

        foreach (var (name, value) in definition.Headers)
        {
            if (!IsValidHttpHeaderName(name))
            {
                return new McpRuntimeDiagnostic
                {
                    Code = "invalid_http_header",
                    Server = definition.Key,
                    Transport = "http",
                    Message = $"Invalid MCP server '{definition.Key}': HTTP header name '{McpRedactor.RedactValue("header", name)}' is not valid.",
                };
            }

            if (TryExpandEnvironmentVariables(value, out var expandedValue, out var missingVariable))
            {
                if (missingVariable is not null)
                {
                    return MissingEnvironmentVariable(definition, "HTTP header", name, missingVariable);
                }
            }

            if (expandedValue.Any(static c => c is '\r' or '\n'))
            {
                return new McpRuntimeDiagnostic
                {
                    Code = "invalid_http_header",
                    Server = definition.Key,
                    Transport = "http",
                    Message = $"Invalid MCP server '{definition.Key}': HTTP header '{McpRedactor.RedactValue("header", name)}' contains an invalid value.",
                };
            }
        }

        return null;
    }

    private static bool TryCreateVariableDiagnostic(
        McpServerDefinition definition,
        IReadOnlyDictionary<string, string> values,
        string fieldKind,
        out McpRuntimeDiagnostic? diagnostic)
    {
        foreach (var (name, value) in values)
        {
            if (TryExpandEnvironmentVariables(value, out _, out var missingVariable) && missingVariable is not null)
            {
                diagnostic = MissingEnvironmentVariable(definition, fieldKind, name, missingVariable);
                return true;
            }
        }

        diagnostic = null;
        return false;
    }

    private static McpRuntimeDiagnostic MissingEnvironmentVariable(McpServerDefinition definition, string fieldKind, string fieldName, string variableName)
        => new()
        {
            Code = "environment_variable_not_found",
            Server = definition.Key,
            Transport = FormatTransport(definition.Transport),
            Message = $"Invalid MCP server '{definition.Key}': {fieldKind} '{McpRedactor.RedactValue(fieldKind, fieldName)}' references environment variable '{variableName}', but it is not set.",
        };

    private static Dictionary<string, string> ExpandDictionary(IReadOnlyDictionary<string, string> values)
    {
        var expanded = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in values)
        {
            _ = TryExpandEnvironmentVariables(value, out var expandedValue, out _);
            expanded[key] = expandedValue;
        }

        return expanded;
    }

    private static bool TryExpandEnvironmentVariables(string value, out string expanded, out string? missingVariable)
    {
        var start = value.IndexOf("${", StringComparison.Ordinal);
        if (start < 0)
        {
            expanded = value;
            missingVariable = null;
            return false;
        }

        var builder = new StringBuilder(value.Length);
        var offset = 0;
        do
        {
            builder.Append(value, offset, start - offset);
            var end = value.IndexOf('}', start + 2);
            if (end < 0)
            {
                builder.Append(value, start, value.Length - start);
                expanded = builder.ToString();
                missingVariable = null;
                return true;
            }

            var variableName = value.Substring(start + 2, end - start - 2);
            if (variableName.Length == 0)
            {
                builder.Append(value, start, end - start + 1);
            }
            else
            {
                var variableValue = Environment.GetEnvironmentVariable(variableName);
                if (variableValue is null)
                {
                    expanded = value;
                    missingVariable = variableName;
                    return true;
                }

                builder.Append(variableValue);
            }

            offset = end + 1;
            start = value.IndexOf("${", offset, StringComparison.Ordinal);
        }
        while (start >= 0);

        builder.Append(value, offset, value.Length - offset);
        expanded = builder.ToString();
        missingVariable = null;
        return true;
    }

    private static bool IsValidHttpHeaderName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        foreach (var c in name)
        {
            if (c <= 32 || c >= 127 || c is '(' or ')' or '<' or '>' or '@' or ',' or ';' or ':' or '\\' or '"' or '/' or '[' or ']' or '?' or '=' or '{' or '}')
            {
                return false;
            }
        }

        return true;
    }

    private static McpRuntimeDiagnostic InvalidTransport(McpServerDefinition definition, string message)
        => new()
        {
            Code = "invalid_transport_fields",
            Server = definition.Key,
            Transport = definition.Transport == McpTransportKind.Stdio ? "stdio" : "http",
            Message = $"Invalid MCP server '{definition.Key}': {message}",
        };

    private static bool IsServerEnabled(McpPolicyOptions policy, string serverKey, out McpServerPolicyOptions? serverPolicy)
    {
        policy.Servers.TryGetValue(serverKey, out serverPolicy);
        return policy.Enabled && (serverPolicy?.Enabled ?? true);
    }

    private static bool ShouldExposeDirectTool(McpPolicyOptions policy, McpServerPolicyOptions? serverPolicy, McpRuntimeTool tool, int enabledToolCount)
    {
        var mode = NormalizeDirectExposure(serverPolicy?.DirectExposure ?? policy.DirectExposure);
        return mode switch
        {
            "none" => false,
            "all" => true,
            "allowlist" => IsExplicitDirectTool(serverPolicy, tool.Name),
            _ => IsExplicitDirectTool(serverPolicy, tool.Name) || enabledToolCount <= Math.Max(0, policy.DirectToolThreshold),
        };
    }

    private static string NormalizeDirectExposure(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "auto";
        }

        var normalized = value.Trim().ToLowerInvariant();
        return normalized is "none" or "allowlist" or "auto" or "all" ? normalized : "auto";
    }

    private static bool IsExplicitDirectTool(McpServerPolicyOptions? serverPolicy, string toolName)
    {
        if (serverPolicy is null)
        {
            return false;
        }

        if (serverPolicy.DirectTools.Count > 0)
        {
            return serverPolicy.DirectTools.Contains(toolName, StringComparer.Ordinal);
        }

        return serverPolicy.AllowedTools.Count > 0 && serverPolicy.AllowedTools.Contains(toolName, StringComparer.Ordinal);
    }

    private static IReadOnlyList<McpRuntimeTool> ConvertTools(string serverKey, string serverAliasPart, IReadOnlyList<McpClientTool> tools, McpPolicyOptions policy, McpServerPolicyOptions? serverPolicy)
    {
        var toolAliasParts = CreateToolAliasPartMap(tools);
        return tools.Select(tool => ConvertTool(serverKey, serverAliasPart, toolAliasParts, tool, policy, serverPolicy)).ToArray();
    }

    private static McpRuntimeTool ConvertTool(
        string serverKey,
        string serverAliasPart,
        IReadOnlyDictionary<string, string> toolAliasParts,
        McpClientTool tool,
        McpPolicyOptions policy,
        McpServerPolicyOptions? serverPolicy)
    {
        var disabledReason = GetToolDisabledReason(serverKey, tool.Name, serverPolicy);
        var toolAliasPart = toolAliasParts.TryGetValue(tool.Name, out var aliasPart) ? aliasPart : SanitizeAliasPart(tool.Name);
        return new McpRuntimeTool
        {
            Server = serverKey,
            Name = tool.Name,
            Alias = CreateAlias(serverAliasPart, toolAliasPart),
            Title = string.IsNullOrWhiteSpace(tool.Title) ? null : tool.Title,
            Description = string.IsNullOrWhiteSpace(tool.Description) ? null : tool.Description,
            InputSchema = tool.JsonSchema.Clone(),
            OutputSchema = tool.ReturnJsonSchema?.Clone(),
            Enabled = disabledReason is null && policy.Enabled,
            DisabledReason = disabledReason,
        };
    }

    private static string? GetToolDisabledReason(string serverKey, string toolName, McpServerPolicyOptions? serverPolicy)
    {
        if (serverPolicy is null)
        {
            return null;
        }

        if (serverPolicy.AllowedTools.Count > 0 && !serverPolicy.AllowedTools.Contains(toolName, StringComparer.Ordinal))
        {
            return $"MCP tool '{toolName}' on server '{serverKey}' is not in the policy allowed_tools list.";
        }

        if (serverPolicy.DisabledTools.Contains(toolName, StringComparer.Ordinal))
        {
            return $"MCP tool '{toolName}' on server '{serverKey}' is disabled by policy.";
        }

        return null;
    }

    private static McpRuntimeToolCallResult ConvertToolResult(McpRuntimeTool tool, CallToolResult result, int maxToolOutputChars)
    {
        var limit = NormalizeTimeout(maxToolOutputChars, 120000);
        var remaining = limit;
        var truncated = false;
        var blocks = new List<McpRuntimeContentBlock>();
        var textBuilder = new StringBuilder();
        foreach (var block in result.Content)
        {
            var converted = ConvertContentBlock(block, ref remaining, ref truncated);
            blocks.Add(converted);
            if (!string.IsNullOrEmpty(converted.Text))
            {
                if (textBuilder.Length > 0)
                {
                    textBuilder.AppendLine();
                }

                textBuilder.Append(converted.Text);
            }
        }

        JsonElement? structuredContent = result.StructuredContent is { } structured
            ? RedactJsonElement(structured, propertyName: null)
            : null;
        if (structuredContent is not null)
        {
            var serialized = JsonSerializer.Serialize(structuredContent.Value);
            if (serialized.Length > remaining)
            {
                structuredContent = null;
                truncated = true;
            }
            else
            {
                remaining -= serialized.Length;
            }
        }

        return new McpRuntimeToolCallResult
        {
            Server = tool.Server,
            Name = tool.Name,
            Alias = tool.Alias,
            IsError = result.IsError == true,
            Content = blocks,
            ContentText = textBuilder.Length == 0 ? null : textBuilder.ToString(),
            StructuredContent = structuredContent,
            Truncated = truncated,
        };
    }

    private static McpRuntimeContentBlock ConvertContentBlock(ContentBlock block, ref int remaining, ref bool truncated)
    {
        var type = string.IsNullOrWhiteSpace(block.Type) ? "unknown" : block.Type;
        if (block is TextContentBlock text)
        {
            return new McpRuntimeContentBlock
            {
                Type = type,
                Text = AppendWithinLimit(McpRedactor.RedactValue(null, text.Text), ref remaining, ref truncated),
            };
        }

        if (block is ImageContentBlock image)
        {
            return new McpRuntimeContentBlock { Type = type, MimeType = image.MimeType, Summary = $"image content omitted ({image.DecodedData.Length} bytes)" };
        }

        if (block is AudioContentBlock audio)
        {
            return new McpRuntimeContentBlock { Type = type, MimeType = audio.MimeType, Summary = $"audio content omitted ({audio.DecodedData.Length} bytes)" };
        }

        if (block is EmbeddedResourceBlock resource)
        {
            return new McpRuntimeContentBlock { Type = type, MimeType = resource.Resource.MimeType, Summary = $"embedded resource omitted ({resource.Resource.Uri})" };
        }

        if (block is ResourceLinkBlock link)
        {
            return new McpRuntimeContentBlock { Type = type, MimeType = link.MimeType, Summary = $"resource link: {link.Uri}" };
        }

        return new McpRuntimeContentBlock { Type = type, Summary = "non-text MCP content omitted" };
    }

    private static JsonElement RedactJsonElement(JsonElement element, string? propertyName)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(RedactJsonValue(element, propertyName)));
        return document.RootElement.Clone();
    }

    private static object? RedactJsonValue(JsonElement element, string? propertyName)
        => element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(
                static property => property.Name,
                property => RedactJsonValue(property.Value, property.Name),
                StringComparer.Ordinal),
            JsonValueKind.Array => element.EnumerateArray().Select(item => RedactJsonValue(item, propertyName)).ToArray(),
            JsonValueKind.String => McpRedactor.RedactValue(propertyName, element.GetString()),
            JsonValueKind.Number => element.Clone(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => element.Clone(),
        };

    private static string AppendWithinLimit(string value, ref int remaining, ref bool truncated)
    {
        if (remaining <= 0)
        {
            truncated = true;
            return string.Empty;
        }

        if (value.Length <= remaining)
        {
            remaining -= value.Length;
            return value;
        }

        truncated = true;
        var slice = value[..remaining];
        remaining = 0;
        return slice;
    }

    private static bool MatchesServer(string server, string? serverFilter)
        => string.IsNullOrWhiteSpace(serverFilter) || string.Equals(server, serverFilter.Trim(), StringComparison.Ordinal);

    private static bool MatchesQuery(McpRuntimeTool tool, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        var value = query.Trim();
        return tool.Name.Contains(value, StringComparison.OrdinalIgnoreCase) ||
               (tool.Title?.Contains(value, StringComparison.OrdinalIgnoreCase) ?? false) ||
               (tool.Description?.Contains(value, StringComparison.OrdinalIgnoreCase) ?? false) ||
               tool.Server.Contains(value, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<string, string> CreateServerAliasPartMap(IReadOnlyList<McpEffectiveServer> servers)
    {
        var result = new Dictionary<string, string>(servers.Count, StringComparer.Ordinal);
        foreach (var group in servers.GroupBy(static server => SanitizeAliasPart(server.Definition.Key), StringComparer.Ordinal))
        {
            var items = group.OrderBy(static server => server.Definition.Key, StringComparer.Ordinal).ToArray();
            foreach (var item in items)
            {
                result[item.Definition.Key] = items.Length == 1
                    ? group.Key
                    : AppendAliasSuffix(group.Key, item.Definition.Key);
            }
        }

        return result;
    }

    private static IReadOnlyDictionary<string, string> CreateToolAliasPartMap(IReadOnlyList<McpClientTool> tools)
    {
        var result = new Dictionary<string, string>(tools.Count, StringComparer.Ordinal);
        foreach (var group in tools.GroupBy(static tool => SanitizeAliasPart(tool.Name), StringComparer.Ordinal))
        {
            var items = group.OrderBy(static tool => tool.Name, StringComparer.Ordinal).ToArray();
            foreach (var item in items)
            {
                result[item.Name] = items.Length == 1
                    ? group.Key
                    : AppendAliasSuffix(group.Key, item.Name);
            }
        }

        return result;
    }

    private static string CreateAlias(string serverAliasPart, string toolAliasPart)
        => $"mcp__{serverAliasPart}__{toolAliasPart}";

    private static string AppendAliasSuffix(string aliasPart, string rawValue)
        => $"{aliasPart}_h{CreateStableHashSuffix(rawValue)}";

    private static string CreateStableHashSuffix(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }

    private static string SanitizeAliasPart(string value)
    {
        var builder = new StringBuilder(value.Length + 1);
        foreach (var c in value)
        {
            builder.Append(char.IsAsciiLetterOrDigit(c) ? c : '_');
        }

        if (builder.Length == 0 || char.IsAsciiDigit(builder[0]))
        {
            builder.Insert(0, '_');
        }

        return builder.ToString();
    }

    private static int NormalizeTimeout(int value, int fallback)
        => value > 0 ? value : fallback;

    private static TimeSpan CreateStdioShutdownTimeout(int startupTimeoutMs)
        => TimeSpan.FromMilliseconds(Math.Clamp(startupTimeoutMs, 1, 500));

    private static bool IsRuntimeException(Exception exception)
        => exception is McpException or IOException or InvalidOperationException or Win32Exception or JsonException or TimeoutException or OperationCanceledException or HttpRequestException;

    private static string FormatTransport(McpTransportKind transport)
        => transport == McpTransportKind.Stdio ? "stdio" : "http";

    private static McpRuntimeDiagnostic CreateUnavailableDiagnostic(McpServerDefinition definition, Exception exception)
    {
        var httpException = FindHttpRequestException(exception);
        if (definition.Transport == McpTransportKind.Http && httpException?.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            return new McpRuntimeDiagnostic
            {
                Code = "server_authentication_failed",
                Server = definition.Key,
                Transport = "http",
                Message = $"MCP HTTP/SSE server '{definition.Key}' rejected the connection with HTTP {(int)httpException.StatusCode.Value}; configure static headers in MCP JSON or complete authentication outside CodeAlta. OAuth UX is not implemented.",
            };
        }

        return new McpRuntimeDiagnostic
        {
            Code = "server_unavailable",
            Server = definition.Key,
            Transport = FormatTransport(definition.Transport),
            Message = $"MCP {FormatTransport(definition.Transport)} server '{definition.Key}' is unavailable: {RedactRuntimeMessage(definition, exception.GetBaseException().Message)}",
        };
    }

    private static string RedactRuntimeMessage(McpServerDefinition definition, string message)
    {
        var redacted = message;
        if (!string.IsNullOrWhiteSpace(definition.Url) && redacted.Contains(definition.Url, StringComparison.Ordinal))
        {
            redacted = redacted.Replace(definition.Url, McpRedactor.RedactUrl(definition.Url) ?? string.Empty, StringComparison.Ordinal);
        }

        foreach (var value in definition.Headers.Values)
        {
            if (!string.IsNullOrEmpty(value) && redacted.Contains(value, StringComparison.Ordinal))
            {
                redacted = redacted.Replace(value, "[redacted]", StringComparison.Ordinal);
            }
        }

        return McpRedactor.RedactValue(null, redacted);
    }

    private static HttpRequestException? FindHttpRequestException(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException!)
        {
            if (current is HttpRequestException httpException)
            {
                return httpException;
            }
        }

        return null;
    }

    private sealed record RuntimeContext(McpConfigSnapshot Config, McpPolicyOptions Policy, IReadOnlyDictionary<string, string> ServerAliasParts);

    private sealed record DirectToolCandidate(McpRuntimeTool Tool, McpServerPolicyOptions? ServerPolicy);

    private sealed record ResolvedToolState(ServerRuntimeState State, McpRuntimeTool Tool, McpServerPolicyOptions? ServerPolicy);

    private sealed class ServerRuntimeState
    {
        private ServerRuntimeState(McpClient? client, IReadOnlyList<McpRuntimeTool> tools, McpRuntimeDiagnostic? diagnostic)
        {
            Client = client;
            Tools = tools;
            Diagnostic = diagnostic;
        }

        public McpClient? Client { get; }

        public IReadOnlyList<McpRuntimeTool> Tools { get; }

        public McpRuntimeDiagnostic? Diagnostic { get; }

        public static ServerRuntimeState Connected(McpClient client, IReadOnlyList<McpRuntimeTool> tools)
            => new(client, tools, null);

        public static ServerRuntimeState Failed(McpRuntimeDiagnostic diagnostic)
            => new(null, [], diagnostic);
    }
}
