namespace CodeAlta.Presentation.Tabs;

internal static class ThreadTabStripProjectionBuilder
{
    public static ThreadTabStripProjection Build(
        IReadOnlyList<string> openThreadIds,
        IReadOnlySet<string> availableThreadIds,
        bool draftTabOpen,
        string draftTabId,
        string? selectedThreadId,
        IReadOnlyList<string>? openFileTabIds = null,
        string? selectedTabIdOverride = null)
    {
        ArgumentNullException.ThrowIfNull(openThreadIds);
        ArgumentNullException.ThrowIfNull(availableThreadIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(draftTabId);

        var tabs = new List<ThreadTabStripItemProjection>(openThreadIds.Count + (draftTabOpen ? 1 : 0) + (openFileTabIds?.Count ?? 0));
        foreach (var threadId in openThreadIds)
        {
            if (!availableThreadIds.Contains(threadId))
            {
                continue;
            }

            tabs.Add(new ThreadTabStripItemProjection(threadId, IsDraft: false));
        }

        if (draftTabOpen)
        {
            tabs.Add(new ThreadTabStripItemProjection(draftTabId, IsDraft: true));
        }

        if (openFileTabIds is not null)
        {
            foreach (var fileTabId in openFileTabIds)
            {
                if (string.IsNullOrWhiteSpace(fileTabId))
                {
                    continue;
                }

                tabs.Add(new ThreadTabStripItemProjection(fileTabId, IsDraft: false, IsFile: true));
            }
        }

        var selectedTabId = !string.IsNullOrWhiteSpace(selectedTabIdOverride) &&
                            tabs.Any(tab => string.Equals(tab.TabId, selectedTabIdOverride, StringComparison.OrdinalIgnoreCase))
            ? selectedTabIdOverride
            : string.IsNullOrWhiteSpace(selectedThreadId)
                ? (draftTabOpen ? draftTabId : tabs.FirstOrDefault()?.TabId)
                : tabs.Any(tab => string.Equals(tab.TabId, selectedThreadId, StringComparison.OrdinalIgnoreCase))
                    ? selectedThreadId
                    : tabs.FirstOrDefault()?.TabId;

        return new ThreadTabStripProjection(tabs, selectedTabId);
    }
}
