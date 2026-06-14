using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Presentation.Styling;
using XenoAtom.Ansi;
using XenoAtom.Terminal.UI;

namespace CodeAlta.Presentation.Sidebar;

internal static class SidebarSessionPresentation
{
    public static SidebarAccent ResolveSessionAccent(string? providerKey, SessionViewKind kind)
    {
        _ = providerKey;
        return kind switch
        {
            SessionViewKind.GlobalSession => SidebarAccent.Global,
            SessionViewKind.ProjectSession => SidebarAccent.ProjectSession,
            SessionViewKind.InternalSession => SidebarAccent.InternalSession,
            _ => SidebarAccent.Fallback,
        };
    }

    public static string ResolveProviderDisplayName(string? providerKey, string? displayName = null)
    {
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            return displayName.Trim();
        }

        if (string.Equals(providerKey, ModelProviderIds.Copilot.Value, StringComparison.OrdinalIgnoreCase))
        {
            return "Copilot";
        }

        if (string.Equals(providerKey, ModelProviderIds.Codex.Value, StringComparison.OrdinalIgnoreCase))
        {
            return "Codex";
        }

        return string.IsNullOrWhiteSpace(providerKey)
            ? "Unknown"
            : providerKey.Trim();
    }

    public static string BuildProviderMarkup(string? providerKey, string? displayName, SessionViewKind kind)
    {
        var accent = ResolveSessionAccent(providerKey, kind);
        return $"[{UiPalette.GetSidebarAccentMarkup(accent)}]{TerminalIcons.MdCircleSmall}[/] {AnsiMarkup.Escape(ResolveProviderDisplayName(providerKey, displayName))}";
    }

    public static string BuildEditedPromptIconMarkup(SidebarAccent accent)
        => $"[{UiPalette.GetSidebarAccentMarkup(accent)}]{TerminalIcons.MdSquareEditOutline}[/]";

    public static string BuildReminderIconMarkup(SidebarAccent accent)
        => $"[{UiPalette.GetSidebarAccentMarkup(accent)}]{TerminalIcons.MdTimerOutline}[/]";
}
