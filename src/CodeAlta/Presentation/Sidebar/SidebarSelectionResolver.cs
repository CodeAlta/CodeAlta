namespace CodeAlta.Presentation.Sidebar;

internal static class SidebarSelectionResolver
{
    public static SidebarSelectionTarget ResolveCurrentTarget(
        string? selectedThreadId,
        string? selectedProjectId,
        bool globalScopeSelected)
    {
        if (!string.IsNullOrWhiteSpace(selectedThreadId))
        {
            return SidebarSelectionTarget.Thread(selectedThreadId);
        }

        if (globalScopeSelected || string.IsNullOrWhiteSpace(selectedProjectId))
        {
            return SidebarSelectionTarget.Global();
        }

        return SidebarSelectionTarget.Project(selectedProjectId);
    }

    public static SidebarSelectionTarget ResolveTargetForProjectionChange(
        SidebarSelectionTarget? previousTarget,
        SidebarTreeProjection? projection,
        SidebarSelectionTarget currentTarget)
    {
        if (previousTarget is { } target &&
            projection is not null &&
            projection.ContainsTarget(target))
        {
            return target;
        }

        return currentTarget;
    }
}