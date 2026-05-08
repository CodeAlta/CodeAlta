using CodeAlta.App;

namespace CodeAlta.Presentation.Tabs;

internal static class ThreadTabStripProjectionBuilder
{
    public static ThreadTabStripProjection Build(IReadOnlyList<ShellTabSnapshot> shellTabs)
    {
        ArgumentNullException.ThrowIfNull(shellTabs);

        var workspaceTabCount = shellTabs.Count(static tab => IsWorkspaceTab(tab.Kind));
        var tabs = new List<ThreadTabStripItemProjection>(workspaceTabCount);
        string? selectedTabId = null;
        foreach (var shellTab in shellTabs)
        {
            if (!IsWorkspaceTab(shellTab.Kind))
            {
                continue;
            }

            var canClose = shellTab.CanClose;
            if (shellTab.Kind == ShellTabKind.PromptDraft && workspaceTabCount == 1)
            {
                canClose = false;
            }

            tabs.Add(new ThreadTabStripItemProjection(shellTab.TabId.Value, shellTab.Kind, canClose));
            if (shellTab.IsSelected)
            {
                selectedTabId = shellTab.TabId.Value;
            }
        }

        return new ThreadTabStripProjection(tabs, selectedTabId ?? tabs.FirstOrDefault()?.TabId);
    }

    private static bool IsWorkspaceTab(ShellTabKind kind)
        => kind is ShellTabKind.PromptDraft or ShellTabKind.Thread or ShellTabKind.Editor or ShellTabKind.Plugin;
}
