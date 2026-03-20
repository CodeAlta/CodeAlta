namespace CodeAlta.Presentation.Tabs;

internal static class ThreadTabStripProjectionBuilder
{
    public static ThreadTabStripProjection Build(
        IReadOnlyList<string> openThreadIds,
        IReadOnlySet<string> availableThreadIds,
        bool draftTabOpen,
        string draftTabId,
        string? selectedThreadId)
    {
        ArgumentNullException.ThrowIfNull(openThreadIds);
        ArgumentNullException.ThrowIfNull(availableThreadIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(draftTabId);

        var tabs = new List<ThreadTabStripItemProjection>(openThreadIds.Count + (draftTabOpen ? 1 : 0));
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

        var selectedTabId = string.IsNullOrWhiteSpace(selectedThreadId)
            ? (draftTabOpen ? draftTabId : null)
            : tabs.Any(tab => string.Equals(tab.TabId, selectedThreadId, StringComparison.OrdinalIgnoreCase))
                ? selectedThreadId
                : null;

        return new ThreadTabStripProjection(tabs, selectedTabId);
    }
}