using CodeAlta.Plugins.Abstractions;
using CodeAlta.Plugins.Tui;

namespace CodeAlta.Plugin.GitHub;

internal static class GitHubTerminalContributions
{
    internal static IEnumerable<PluginPromptEditorContribution> CreatePromptEditorContributions(GitHubPlugin plugin)
    {
        yield return PluginTui.PromptEditor(
            "GitHub issue prompt picker",
            host => new GitHubIssuePromptAttachment(plugin, host),
            "[#] to reference a GitHub issue");
    }
}
