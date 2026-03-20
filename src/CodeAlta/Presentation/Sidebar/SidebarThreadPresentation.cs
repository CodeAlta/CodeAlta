using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Presentation.Styling;

namespace CodeAlta.Presentation.Sidebar;

internal static class SidebarThreadPresentation
{
    public static SidebarAccent ResolveThreadAccent(string? backendId, WorkThreadKind kind)
    {
        if (string.Equals(backendId, AgentBackendIds.Copilot.Value, StringComparison.OrdinalIgnoreCase))
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

    public static string CompactThreadTitle(string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        const int maxLength = 34;
        var normalized = title.Trim();
        return normalized.Length <= maxLength
            ? normalized
            : normalized[..Math.Max(1, maxLength - 1)].TrimEnd() + "…";
    }

    public static string BuildThreadTooltip(WorkThreadDescriptor thread)
    {
        ArgumentNullException.ThrowIfNull(thread);

        if (string.IsNullOrWhiteSpace(thread.LatestSummary))
        {
            return thread.Title;
        }

        return $"{thread.Title}\n\n{thread.LatestSummary}";
    }
}