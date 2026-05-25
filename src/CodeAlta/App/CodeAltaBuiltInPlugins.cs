using CodeAlta.Plugin.GitHub;
using CodeAlta.Plugin.Statistics;
using CodeAlta.Plugins;

namespace CodeAlta.App;

internal static class CodeAltaBuiltInPlugins
{
    public static IReadOnlyList<BuiltInPluginDefinition> All { get; } = CreateRegistry().GetDefinitions();

    private static BuiltInPluginRegistry CreateRegistry()
    {
        var registry = new BuiltInPluginRegistry();
        registry.Add(new BuiltInPluginDefinition
        {
            Id = "github",
            DisplayName = "GitHub",
            Description = "Adds a GitHub issue prompt picker and exposes the GitHub CLI when available.",
            EnabledByDefault = true,
            PluginType = typeof(GitHubPlugin),
            Factory = static () => new GitHubPlugin(),
        });
        registry.Add(new BuiltInPluginDefinition
        {
            Id = "statistics",
            DisplayName = "Statistics",
            Description = "Projects transient per-turn and session statistics from normalized agent events.",
            EnabledByDefault = true,
            PluginType = typeof(StatisticsPlugin),
            Factory = static () => new StatisticsPlugin(),
        });
        return registry;
    }
}
