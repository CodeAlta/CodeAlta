using CodeAlta.Plugin.Mcp;
using CodeAlta.Plugins.Abstractions;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Styling;

namespace CodeAlta.App;

internal static class McpStatusIndicatorComposer
{
    public static Visual? Compose(
        McpManagementService managementService,
        Func<string?> getProjectDirectory,
        Action openMcpServers,
        PluginHostBridge? pluginHostBridge)
    {
        ArgumentNullException.ThrowIfNull(managementService);
        ArgumentNullException.ThrowIfNull(getProjectDirectory);
        ArgumentNullException.ThrowIfNull(openMcpServers);

        var mcpIndicator = BuildMcpStatusIndicator(managementService, getProjectDirectory, openMcpServers);
        var pluginStatus = ShellPluginFooterComposer.ComposeRegion(pluginHostBridge, PluginUiRegion.SessionStatus);
        if (mcpIndicator is null)
        {
            return pluginStatus;
        }

        if (pluginStatus is null)
        {
            return mcpIndicator;
        }

        return new HStack(mcpIndicator, pluginStatus) { Spacing = 2 };
    }

    private static Visual? BuildMcpStatusIndicator(
        McpManagementService managementService,
        Func<string?> getProjectDirectory,
        Action openMcpServers)
    {
        var projectDirectory = getProjectDirectory();
        var snapshot = managementService.CachedSnapshot;
        if (snapshot is null || !string.Equals(snapshot.ProjectDirectory, projectDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!snapshot.Summary.HasConfiguration && snapshot.Summary.ConfiguredServerCount == 0 && snapshot.Summary.InvalidSourceCount == 0)
        {
            return null;
        }

        var label = snapshot.Summary.UnavailableServerCount > 0
            ? $"MCP {snapshot.Summary.ActiveServerCount}/{snapshot.Summary.ConfiguredServerCount} · {snapshot.Summary.UnavailableServerCount} unavailable · tools {snapshot.Summary.ExposedToolCount}/{snapshot.Summary.TotalToolCount}"
            : $"MCP {snapshot.Summary.ActiveServerCount}/{snapshot.Summary.ConfiguredServerCount} · tools {snapshot.Summary.ExposedToolCount}/{snapshot.Summary.TotalToolCount}";
        return new Button(label)
            .Tone(snapshot.Summary.UnavailableServerCount > 0 ? ControlTone.Warning : ControlTone.Default)
            .Click(openMcpServers);
    }
}
