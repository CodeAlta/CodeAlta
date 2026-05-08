using CodeAlta.App;

namespace CodeAlta.Presentation.Tabs;

internal sealed record ThreadTabStripProjection(
    IReadOnlyList<ThreadTabStripItemProjection> Tabs,
    string? SelectedTabId);

internal sealed record ThreadTabStripItemProjection(
    string TabId,
    ShellTabKind Kind,
    bool CanClose)
{
    public bool IsDraft => Kind == ShellTabKind.PromptDraft;

    public bool IsFile => Kind == ShellTabKind.Editor;

    public bool IsPlugin => Kind == ShellTabKind.Plugin;
}
