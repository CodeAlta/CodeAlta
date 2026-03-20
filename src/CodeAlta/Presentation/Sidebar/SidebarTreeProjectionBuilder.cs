using CodeAlta.Catalog;
using CodeAlta.Presentation.Styling;
using CodeAlta.Presentation.Threads;
using XenoAtom.Terminal.UI;

namespace CodeAlta.Presentation.Sidebar;

internal static class SidebarTreeProjectionBuilder
{
    public static SidebarTreeProjection Build(
        IReadOnlyList<ProjectDescriptor> projects,
        IReadOnlyList<WorkThreadDescriptor> threads,
        string globalRoot,
        string? expandedProjectId,
        int maxRecentThreadsPerProject)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(threads);
        ArgumentException.ThrowIfNullOrWhiteSpace(globalRoot);

        return new SidebarTreeProjection(
        [
            CreateGlobalNode(threads, globalRoot, maxRecentThreadsPerProject),
            CreateProjectsNode(projects, threads, expandedProjectId, maxRecentThreadsPerProject),
        ]);
    }

    private static SidebarTreeNodeProjection CreateGlobalNode(
        IReadOnlyList<WorkThreadDescriptor> threads,
        string globalRoot,
        int maxRecentThreadsPerProject)
    {
        var children = threads
            .Where(static item => item.Kind == WorkThreadKind.GlobalThread)
            .OrderByDescending(static item => item.LastActiveAt)
            .Take(maxRecentThreadsPerProject)
            .Select(CreateThreadNode)
            .ToArray();

        return new SidebarTreeNodeProjection(
            Title: "Global",
            Tooltip: globalRoot,
            Icon: NerdFont.MdHomeOutline,
            Accent: SidebarAccent.Global,
            SelectionTarget: SidebarSelectionTarget.Global(),
            IsExpanded: true,
            Children: children);
    }

    private static SidebarTreeNodeProjection CreateProjectsNode(
        IReadOnlyList<ProjectDescriptor> projects,
        IReadOnlyList<WorkThreadDescriptor> threads,
        string? expandedProjectId,
        int maxRecentThreadsPerProject)
    {
        var children = projects
            .OrderBy(static item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(project => CreateProjectNode(project, threads, expandedProjectId, maxRecentThreadsPerProject))
            .ToArray();

        return new SidebarTreeNodeProjection(
            Title: "Projects",
            Tooltip: $"{projects.Count} known projects",
            Icon: NerdFont.MdFolderMultipleOutline,
            Accent: SidebarAccent.Projects,
            SelectionTarget: null,
            IsExpanded: true,
            Children: children);
    }

    private static SidebarTreeNodeProjection CreateProjectNode(
        ProjectDescriptor project,
        IReadOnlyList<WorkThreadDescriptor> threads,
        string? expandedProjectId,
        int maxRecentThreadsPerProject)
    {
        var projectThreads = ThreadScopePresentation.FilterThreadsForProject(threads, project.Id, includeInternal: true)
            .Take(maxRecentThreadsPerProject)
            .Select(CreateThreadNode)
            .ToArray();

        return new SidebarTreeNodeProjection(
            Title: project.DisplayName,
            Tooltip: project.ProjectPath,
            Icon: NerdFont.MdFolderOutline,
            Accent: SidebarAccent.Projects,
            SelectionTarget: SidebarSelectionTarget.Project(project.Id),
            IsExpanded: string.Equals(project.Id, expandedProjectId, StringComparison.OrdinalIgnoreCase),
            Children: projectThreads);
    }

    private static SidebarTreeNodeProjection CreateThreadNode(WorkThreadDescriptor thread)
    {
        ArgumentNullException.ThrowIfNull(thread);

        var icon = thread.Kind switch
        {
            WorkThreadKind.GlobalThread => NerdFont.MdHomeOutline,
            WorkThreadKind.ProjectThread => NerdFont.MdChatProcessingOutline,
            WorkThreadKind.InternalThread => NerdFont.MdAccountCogOutline,
            _ => NerdFont.MdChatProcessingOutline,
        };

        return new SidebarTreeNodeProjection(
            Title: SidebarThreadPresentation.CompactThreadTitle(thread.Title),
            Tooltip: SidebarThreadPresentation.BuildThreadTooltip(thread),
            Icon: icon,
            Accent: SidebarThreadPresentation.ResolveThreadAccent(thread.BackendId, thread.Kind),
            SelectionTarget: SidebarSelectionTarget.Thread(thread.ThreadId),
            IsExpanded: false,
            Children: []);
    }
}