using CodeAlta.Plugins.Abstractions;
using CodeAlta.Plugins.Tui;

namespace CodeAlta.Plugin.Git;

internal static class GitTerminalContributions
{
    internal static IEnumerable<PluginPromptEditorContribution> CreatePromptEditorContributions(GitPlugin plugin)
    {
        yield return PluginTui.PromptEditor(
            "Git issue prompt picker",
            host => new GitIssuePromptAttachment(plugin, host),
            "[#] to reference an issue");
    }
}
