using System.Text;
using CodeAlta.Plugins.Abstractions;
using CodeAlta.Plugins.Tui;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Styling;
using static CodeAlta.Plugin.Mcp.McpPlugin;

namespace CodeAlta.Plugin.Mcp;

internal sealed class McpTerminalContributions
{
    private readonly McpManagementService _managementService;
    private readonly McpActivationState _activationState;
    private readonly State<int> _statusRevision = new(0);

    private static readonly PluginKeyBinding ManageServersKeyBinding = new(
        new PluginKeyGesture('G', PluginKeyModifiers.Ctrl),
        new PluginKeyGesture('Y', PluginKeyModifiers.Ctrl));

    private McpTerminalContributions(McpManagementService managementService, McpActivationState activationState)
    {
        _managementService = managementService;
        _activationState = activationState;
        _activationState.Changed += _ => IncrementStatusRevision();
    }

    internal static McpPluginPresentation CreatePresentation(McpManagementService managementService, McpActivationState activationState)
    {
        var presentation = new McpTerminalContributions(managementService, activationState);
        return new McpPluginPresentation(presentation.CreateCommands, presentation.DecorateStatus);
    }

    private PluginContentContribution DecorateStatus(PluginContentContribution content)
        => McpTerminalPresentation.DecorateStatus(content, context => CreateStatusIndicator(context, _managementService, _activationState, _statusRevision));

    private IEnumerable<PluginCommandContribution> CreateCommands()
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
            Handler = (context, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                ShowManagementDialog(context, _managementService);
                return new ValueTask<PluginCommandResult>(PluginCommandResult.Handled);
            },
        };
    }

    private static void ShowManagementDialog(PluginOperationContext context, McpManagementService managementService, Visual? focusTarget = null)
    {
        var projectPath = ResolveProjectPath(context.ProjectPath, context.Services.Workspace.SelectedProjectPath, null);
        new McpServersDialog(
            managementService,
            () => new McpManagementRequest { ProjectDirectory = projectPath },
            static (_, _) => Task.CompletedTask,
            () => PluginDialogLayout.ResolveDialogBounds(focusTarget),
            () => focusTarget)
            .Show();
    }

    internal static Visual? CreateStatusIndicator(PluginVisualContext context, McpManagementService managementService, McpActivationState activationState, State<int> statusRevision)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(managementService);
        ArgumentNullException.ThrowIfNull(activationState);
        ArgumentNullException.ThrowIfNull(statusRevision);

        var projectPath = ResolveProjectPath(context.ProjectPath, context.Services.Workspace.SelectedProjectPath, null);
        var snapshot = ResolveStatusSnapshot(managementService, projectPath);
        if (!snapshot.Summary.HasConfiguration && snapshot.Summary.ConfiguredServerCount == 0 && snapshot.Summary.InvalidSourceCount == 0)
        {
            return null;
        }

        var activationScope = ResolveActivationScopeKey(context, projectPath);
        var button = new Button(new Markup(() =>
            {
                _ = statusRevision.Value;
                return CreateStatusVisualState(managementService, activationState, activationScope, projectPath).Markup;
            })
            {
                Wrap = false,
                IsSelectable = false,
            })
            .Tone(() =>
            {
                _ = statusRevision.Value;
                return CreateStatusVisualState(managementService, activationState, activationScope, projectPath).Tone;
            });
        button.Click(() => ShowManagementDialog(context, managementService, button));
        return button;
    }

    private void IncrementStatusRevision()
    {
        var dispatcher = _statusRevision.Dispatcher;
        McpTerminalPresentation.IncrementRevision(dispatcher.CheckAccess, () => _statusRevision.Value++, action => dispatcher.Post(action));
    }

    private static McpStatusVisualState CreateStatusVisualState(
        McpManagementService managementService,
        McpActivationState activationState,
        string activationScope,
        string? projectPath)
    {
        var currentSnapshot = ResolveStatusSnapshot(managementService, projectPath);
        var activeServers = activationState.GetActiveServers(activationScope);
        return new McpStatusVisualState(
            CreateStatusMarkup(currentSnapshot, activationState.GetToolCounts(activationScope), activeServers),
            currentSnapshot.Summary.UnavailableServerCount > 0 ? ControlTone.Warning : ControlTone.Default);
    }

    private readonly record struct McpStatusVisualState(string Markup, ControlTone Tone);

    internal static string CreateStatusMarkup(
        McpManagementSnapshot snapshot,
        IReadOnlyDictionary<string, int> activatedToolCounts,
        IReadOnlyCollection<string> activeServers)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(activatedToolCounts);
        ArgumentNullException.ThrowIfNull(activeServers);

        var summary = snapshot.Summary;
        var serverTone = summary.ConfiguredServerCount > 0 ? "success" : "muted";
        var activeServerTone = summary.ActiveServerCount > 0 ? serverTone : "muted";
        var builder = new StringBuilder();
        builder.Append('[')
            .Append(serverTone)
            .Append(']')
            .Append(McpTerminalIcons.MdServerNetwork)
            .Append(" MCP[/] [")
            .Append(activeServerTone)
            .Append(']')
            .Append(summary.ActiveServerCount)
            .Append("[/][muted]/")
            .Append(summary.ConfiguredServerCount)
            .Append("[/]");
        if (summary.UnavailableServerCount > 0)
        {
            builder.Append(" · [warning]")
                .Append(summary.UnavailableServerCount)
                .Append(" unavailable[/]");
        }

        builder.Append(" · ")
            .Append(CreateStatusToolMarkup(snapshot, activatedToolCounts, activeServers));
        return builder.ToString();
    }

    private static string CreateStatusToolMarkup(
        McpManagementSnapshot snapshot,
        IReadOnlyDictionary<string, int> activatedToolCounts,
        IReadOnlyCollection<string> activeServers)
    {
        var summary = snapshot.Summary;
        if (summary.TotalToolCount > 0 || HasCompletedManagementToolDiscovery(snapshot))
        {
            return $"tools [accent]{summary.ExposedToolCount}[/][muted]/{summary.TotalToolCount}[/]";
        }

        var loadedActiveServerCount = activeServers.Count(server => activatedToolCounts.ContainsKey(server));
        if (loadedActiveServerCount > 0)
        {
            return $"active tools [accent]{activeServers.Sum(server => activatedToolCounts.TryGetValue(server, out var count) ? count : 0)}[/]";
        }

        return activeServers.Count > 0 ? "tools [warning]pending[/]" : "tools [muted]not loaded[/]";
    }

}

// Stateless seams have no native field initializers; tests do not construct the presentation above.
internal static class McpTerminalPresentation
{
    internal static PluginVisualContribution DecorateStatus(PluginContentContribution content, Func<PluginVisualContext, Visual?> createVisual)
        => new()
        {
            Region = content.Region,
            Name = content.Name,
            Order = content.Order,
            CreateContent = content.CreateContent,
            CreateVisual = createVisual,
        };

    internal static void IncrementRevision(Func<bool> checkAccess, Action increment, Action<Action> post)
    {
        if (checkAccess())
        {
            increment();
            return;
        }

        try
        {
            post(increment);
        }
        catch (InvalidOperationException)
        {
            // No interactive TerminalApp is attached. The in-memory activation state is still current,
            // and the next UI composition will read it directly.
        }
    }
}
