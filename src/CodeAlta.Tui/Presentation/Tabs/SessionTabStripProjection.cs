using CodeAlta.Tui.App;

namespace CodeAlta.Tui.Presentation.Tabs;

internal sealed record SessionTabStripProjection(
    IReadOnlyList<SessionTabStripItemProjection> Tabs,
    string? SelectedTabId);

internal sealed record SessionTabStripItemProjection(
    string TabId,
    ShellTabKind Kind,
    bool CanClose)
{
    public bool IsDraft => Kind == ShellTabKind.PromptDraft;

    public bool IsFile => Kind == ShellTabKind.Editor;

    public bool IsPlugin => Kind == ShellTabKind.Plugin;
}
