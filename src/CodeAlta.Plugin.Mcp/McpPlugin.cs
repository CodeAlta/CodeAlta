using System.Text;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugin.Mcp;

/// <summary>
/// Built-in plugin that inspects Model Context Protocol server configuration for CodeAlta.
/// </summary>
[Plugin("mcp", DisplayName = "MCP", Description = "Connects CodeAlta to configured Model Context Protocol servers.")]
public sealed class McpPlugin : PluginBase
{
    private const string ArgumentsJsonPropertyName = "arguments_json";
    private const int MaxWrapperSchemaDescriptionChars = 2000;

    private readonly McpActivationState _activationState = new();
    private readonly McpManagementService _managementService = new();
    private readonly McpPluginPresentation? _presentation;

    /// <summary>Initializes an MCP backend without terminal presentation.</summary>
    public McpPlugin()
    {
    }

    // The terminal host borrows these exact owners; presentation construction precedes contribution enumeration.
    internal McpPlugin(Func<McpManagementService, McpActivationState, McpPluginPresentation> createPresentation)
    {
        ArgumentNullException.ThrowIfNull(createPresentation);
        _presentation = createPresentation(_managementService, _activationState)
            ?? throw new InvalidOperationException("The MCP presentation factory returned null.");
    }

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
                    Text = CreateStatusLabel(snapshot, _activationState.GetToolCounts(activationScope), _activationState.GetActiveServers(activationScope)),
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
            CreateCommandNode = context => McpCommandFactory.CreateCommand(context, _activationState),
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
            Text = CreateStatusLabel(snapshot, new Dictionary<string, int>(), [])[(label.Length + 1)..],
            Tone = summary.UnavailableServerCount > 0 ? PluginStatusTone.Warning : PluginStatusTone.Info,
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
    }

    /// <inheritdoc />
    public override async ValueTask<PluginBeforeAgentRunResult?> OnBeforeAgentRunAsync(
        PluginBeforeAgentRunContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var projectPath = ResolveProjectPath(context.ProjectPath, context.Services.Workspace.SelectedProjectPath, null);
        var activationScope = ResolveActivationScopeKey(context, projectPath);
        var activeServers = _activationState.GetActiveServers(activationScope);
        if (activeServers.Count == 0)
        {
            return null;
        }

        await using var runtime = new McpRuntimeService();
        var direct = await runtime.ListToolsForServersAsync(new McpRuntimeRequest { ProjectDirectory = projectPath }, activeServers, cancellationToken).ConfigureAwait(false);
        _activationState.UpdateToolCounts(
            activationScope,
            direct.Tools
                .GroupBy(static tool => tool.Server, StringComparer.Ordinal)
                .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.Ordinal));
        if (direct.Tools.Count == 0 && direct.Diagnostics.Count == 0)
        {
            return null;
        }

        return new PluginBeforeAgentRunResult
        {
            AdditionalTools = direct.Tools.Select(tool => CreateAgentTool(tool, projectPath)).ToArray(),
            TemporaryPromptContributions = CreateDirectToolDiagnosticsPrompt(direct.Diagnostics),
        };
    }

    private ValueTask<string?> CreatePromptDiscoveryAsync(PluginSystemPromptContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        var projectPath = ResolveProjectPath(context.ProjectPath, context.Services.Workspace.SelectedProjectPath, null);
        var snapshot = new McpManagementService().RefreshSnapshot(new McpManagementRequest { ProjectDirectory = projectPath });
        if (!snapshot.Policy.DiscoverInPrompt || snapshot.Summary.ConfiguredServerCount == 0)
        {
            return new ValueTask<string?>((string?)null);
        }

        var configuredServers = snapshot.Servers
            .Where(static server => server.State == McpManagementServerState.Configured && server.PolicyEnabled != false)
            .OrderBy(static server => server.Key, StringComparer.Ordinal)
            .ToArray();
        if (configuredServers.Length == 0)
        {
            return new ValueTask<string?>((string?)null);
        }

        var activationScope = ResolveActivationScopeKey(context, projectPath);
        var activeKeys = _activationState.GetActiveServers(activationScope).ToHashSet(StringComparer.Ordinal);
        var configuredKeys = configuredServers.Select(static server => server.Key).ToHashSet(StringComparer.Ordinal);
        var validActiveKeys = activeKeys.Where(configuredKeys.Contains).OrderBy(static server => server, StringComparer.Ordinal).ToArray();
        if (validActiveKeys.Length != activeKeys.Count)
        {
            _activationState.ReplaceActiveServers(activationScope, validActiveKeys);
            activeKeys = validActiveKeys.ToHashSet(StringComparer.Ordinal);
        }

        var activeServers = configuredServers.Where(server => activeKeys.Contains(server.Key)).ToArray();
        var inactiveServers = configuredServers.Where(server => !activeKeys.Contains(server.Key)).ToArray();
        var maxServers = Math.Max(1, snapshot.Policy.PromptMaxServers);
        var builder = new StringBuilder();
        builder.AppendLine("MCP servers:");
        builder.Append("- Active: ");
        AppendServerList(builder, activeServers, maxServers);
        builder.AppendLine();
        builder.Append("- Inactive (`alta mcp activate <id>*`): ");
        AppendServerList(builder, inactiveServers, maxServers);
        builder.AppendLine();
        builder.Append("- Activation adds tools on next user turn.");

        return new ValueTask<string?>(builder.ToString().TrimEnd());
    }

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

    private static AgentToolDefinition CreateAgentTool(McpRuntimeTool tool, string? projectPath)
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
                useArgumentsJsonWrapper,
                invocation,
                cancellationToken).ConfigureAwait(false));
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
        IReadOnlyList<McpManagementServerSnapshot> servers,
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

            builder.Append('`').Append(displayed[index].Key).Append('`');
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
        bool useArgumentsJsonWrapper,
        AgentToolInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (!TryReadArguments(invocation.Arguments, useArgumentsJsonWrapper, out var arguments, out var error))
        {
            return new AgentToolResult(false, [new AgentToolResultItem.Text(error)], error);
        }

        await using var runtime = new McpRuntimeService();
        var diagnostics = new List<McpRuntimeDiagnostic>();
        var result = await runtime.CallToolAsync(
            new McpRuntimeRequest { ProjectDirectory = projectPath },
            server,
            tool,
            arguments,
            diagnostics,
            cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            var message = diagnostics.Count == 0
                ? $"MCP tool '{tool}' on server '{server}' is unavailable."
                : string.Join(Environment.NewLine, diagnostics.Select(static diagnostic => diagnostic.Message));
            return new AgentToolResult(false, [new AgentToolResultItem.Text(message)], message);
        }

        var text = FormatDirectToolResult(result);
        return new AgentToolResult(!result.IsError, [new AgentToolResultItem.Text(text)], result.IsError ? text : null);
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
