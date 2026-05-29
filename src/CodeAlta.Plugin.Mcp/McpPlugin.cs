using System.Text;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Plugins.Abstractions;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Input;
using XenoAtom.Terminal.UI.Styling;

namespace CodeAlta.Plugin.Mcp;

/// <summary>
/// Built-in plugin that inspects Model Context Protocol server configuration for CodeAlta.
/// </summary>
[Plugin("mcp", DisplayName = "MCP", Description = "Connects CodeAlta to configured Model Context Protocol servers.")]
public sealed class McpPlugin : PluginBase
{
    private readonly McpActivationState _activationState = new();

    private static readonly PluginKeyBinding ManageServersKeyBinding = new()
    {
        DisplayText = "Ctrl+G Ctrl+Y",
        Sequence = new KeySequence(
            new KeyGesture(TerminalChar.CtrlG, TerminalModifiers.Ctrl),
            new KeyGesture(TerminalChar.CtrlY, TerminalModifiers.Ctrl)),
    };

    /// <inheritdoc />
    public override IEnumerable<PluginCommandContribution> GetCommands()
    {
        yield return new PluginCommandContribution
        {
            Name = "mcp",
            Label = "MCP Servers",
            Description = "Inspect and manage configured Model Context Protocol servers.",
            Placement = PluginCommandPlacement.ShellRoot | PluginCommandPlacement.PromptEditor | PluginCommandPlacement.WorkspaceRoot,
            SearchText = "model context protocol servers tools",
            KeyBinding = ManageServersKeyBinding,
            Availability = PluginCommandAvailability.InteractiveUi,
            Handler = static (context, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                ShowManagementDialog(context);
                return new ValueTask<PluginCommandResult>(PluginCommandResult.Handled);
            },
        };
    }

    /// <inheritdoc />
    public override IEnumerable<PluginUiContribution> GetUiContributions()
    {
        yield return new PluginVisualContribution
        {
            Region = PluginUiRegion.SessionStatus,
            Name = "mcp-status",
            Order = 100,
            CreateVisual = static context => CreateStatusIndicator(context),
        };
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

    private static void ShowManagementDialog(PluginOperationContext context, Visual? focusTarget = null)
    {
        var projectPath = ResolveProjectPath(context.ProjectPath, context.Services.Workspace.SelectedProjectPath, null);
        new McpServersDialog(
            new McpManagementService(),
            () => new McpManagementRequest { ProjectDirectory = projectPath },
            static (_, _) => Task.CompletedTask,
            () => PluginDialogLayout.ResolveDialogBounds(focusTarget),
            () => focusTarget)
            .Show();
    }

    private static Visual? CreateStatusIndicator(PluginVisualContext context)
    {
        var projectPath = ResolveProjectPath(context.ProjectPath, context.Services.Workspace.SelectedProjectPath, null);
        var snapshot = new McpManagementService().RefreshSnapshot(new McpManagementRequest { ProjectDirectory = projectPath });
        if (!snapshot.Summary.HasConfiguration && snapshot.Summary.ConfiguredServerCount == 0 && snapshot.Summary.InvalidSourceCount == 0)
        {
            return null;
        }

        var label = snapshot.Summary.UnavailableServerCount > 0
            ? $"MCP {snapshot.Summary.ActiveServerCount}/{snapshot.Summary.ConfiguredServerCount} · {snapshot.Summary.UnavailableServerCount} unavailable · tools {snapshot.Summary.ExposedToolCount}/{snapshot.Summary.TotalToolCount}"
            : $"MCP {snapshot.Summary.ActiveServerCount}/{snapshot.Summary.ConfiguredServerCount} · tools {snapshot.Summary.ExposedToolCount}/{snapshot.Summary.TotalToolCount}";
        var button = new Button(label)
            .Tone(snapshot.Summary.UnavailableServerCount > 0 ? ControlTone.Warning : ControlTone.Default);
        button.Click(() => ShowManagementDialog(context, button));
        return button;
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
        return new AgentToolDefinition(
            new AgentToolSpec(tool.Alias, description, tool.InputSchema.Clone()),
            async (invocation, cancellationToken) => await InvokeDirectToolAsync(tool.Server, tool.Name, projectPath, invocation, cancellationToken).ConfigureAwait(false));
    }

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
        AgentToolInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (!TryReadArguments(invocation.Arguments, out var arguments, out var error))
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

    private static bool TryReadArguments(JsonElement element, out IReadOnlyDictionary<string, object?> arguments, out string error)
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
