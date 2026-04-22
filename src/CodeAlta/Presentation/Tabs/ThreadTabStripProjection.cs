namespace CodeAlta.Presentation.Tabs
{
    internal sealed record ThreadTabStripProjection(
        IReadOnlyList<ThreadTabStripItemProjection> Tabs,
        string? SelectedTabId);

    internal sealed record ThreadTabStripItemProjection(
        string TabId,
        bool IsDraft,
        bool IsFile = false);
}
