using CodeAlta.Tui.App;

namespace CodeAlta.Tui.Presentation.Tabs;

internal static class SessionTabStripProjectionBuilder
{
    public static SessionTabStripProjection Build(IReadOnlyList<ShellTabSnapshot> shellTabs)
    {
        ArgumentNullException.ThrowIfNull(shellTabs);

        var workspaceTabCount = shellTabs.Count(static tab => IsWorkspaceTab(tab.Kind));
        var tabs = new List<SessionTabStripItemProjection>(workspaceTabCount);
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

            tabs.Add(new SessionTabStripItemProjection(shellTab.TabId.Value, shellTab.Kind, canClose));
            if (shellTab.IsSelected)
            {
                selectedTabId = shellTab.TabId.Value;
            }
        }

        return new SessionTabStripProjection(tabs, selectedTabId ?? tabs.FirstOrDefault()?.TabId);
    }

    private static bool IsWorkspaceTab(ShellTabKind kind)
        => kind is ShellTabKind.PromptDraft or ShellTabKind.Session or ShellTabKind.Editor or ShellTabKind.Plugin;
}
