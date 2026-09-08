using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugin.Mcp;

/// <summary>Host-owned presentation factories borrowing the backend's management and activation owners.</summary>
internal sealed record McpPluginPresentation(
    Func<IEnumerable<PluginCommandContribution>> CreateCommands,
    Func<PluginContentContribution, PluginContentContribution> DecorateStatus);
