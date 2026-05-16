using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Presentation.Styling;
using XenoAtom.Ansi;
using XenoAtom.Terminal.UI;

namespace CodeAlta.Presentation.Sidebar;

internal static class SidebarThreadPresentation
{
    public static SidebarAccent ResolveThreadAccent(string? providerKey, WorkThreadKind kind)
    {
        if (string.Equals(providerKey, AgentBackendIds.Copilot.Value, StringComparison.OrdinalIgnoreCase))
        {
            return SidebarAccent.CopilotThread;
        }

        return kind switch
        {
            WorkThreadKind.GlobalThread => SidebarAccent.Global,
            WorkThreadKind.ProjectThread => SidebarAccent.ProjectThread,
            WorkThreadKind.InternalThread => SidebarAccent.InternalThread,
            _ => SidebarAccent.Fallback,
        };
    }

    public static string ResolveProviderDisplayName(string? providerKey, string? displayName = null)
    {
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            return displayName.Trim();
        }

        if (string.Equals(providerKey, AgentBackendIds.Copilot.Value, StringComparison.OrdinalIgnoreCase))
        {
            return "Copilot CLI";
        }

        if (string.Equals(providerKey, AgentBackendIds.Codex.Value, StringComparison.OrdinalIgnoreCase))
        {
            return "Codex CLI";
        }

        if (!string.IsNullOrWhiteSpace(providerKey) &&
            providerKey.StartsWith("acp:", StringComparison.OrdinalIgnoreCase))
        {
            return FormatProviderToken(providerKey["acp:".Length..]);
        }

        return string.IsNullOrWhiteSpace(providerKey)
            ? "Unknown"
            : providerKey.Trim();
    }

    public static string BuildProviderMarkup(string? providerKey, string? displayName, WorkThreadKind kind)
    {
        var accent = ResolveThreadAccent(providerKey, kind);
        return $"[{UiPalette.GetSidebarAccentMarkup(accent)}]{NerdFont.MdCircleSmall}[/] {AnsiMarkup.Escape(ResolveProviderDisplayName(providerKey, displayName))}";
    }

    public static string BuildEditedPromptIconMarkup(SidebarAccent accent)
        => $"[{UiPalette.GetSidebarAccentMarkup(accent)}]{NerdFont.MdSquareEditOutline}[/]";

    private static string FormatProviderToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return "ACP";
        }

        var normalized = token.Trim()
            .Replace('_', ' ')
            .Replace('-', ' ');
        return string.Join(
            ' ',
            normalized
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(static part => char.ToUpperInvariant(part[0]) + part[1..]));
    }
}
