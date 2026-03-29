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
        var currentProjection = projection;
        if (currentProjection is null)
        {
            return currentTarget;
        }

        if (currentProjection.ContainsTarget(currentTarget))
        {
            return currentTarget;
        }

        if (previousTarget is { } target &&
            currentProjection.ContainsTarget(target))
        {
            return target;
        }

        foreach (var root in currentProjection.Roots)
        {
            if (root.SelectionTarget is { } selectionTarget)
            {
                return selectionTarget;
            }
        }

        return SidebarSelectionTarget.Global();
    }
}
