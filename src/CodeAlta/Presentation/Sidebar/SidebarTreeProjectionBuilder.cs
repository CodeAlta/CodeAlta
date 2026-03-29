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
        NavigatorSettings settings,
        Func<string, SidebarNodeKind, SidebarSelectionTarget?, SidebarNodeViewModel> getOrCreateRow,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(threads);
        ArgumentException.ThrowIfNullOrWhiteSpace(globalRoot);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(getOrCreateRow);

        return new SidebarTreeProjection(
        [
            CreateGlobalNode(threads, settings, getOrCreateRow, nowUtc),
            CreateProjectsNode(projects, threads, expandedProjectId, settings, getOrCreateRow, nowUtc),
        ]);
    }

    private static SidebarTreeNodeProjection CreateGlobalNode(
        IReadOnlyList<WorkThreadDescriptor> threads,
        NavigatorSettings settings,
        Func<string, SidebarNodeKind, SidebarSelectionTarget?, SidebarNodeViewModel> getOrCreateRow,
        DateTimeOffset nowUtc)
    {
        var visibleThreads = threads
            .Where(static item => item.Status != WorkThreadStatus.Archived)
            .Where(static item => item.Kind == WorkThreadKind.GlobalThread)
            .OrderByDescending(static item => item.LastActiveAt)
            .ToArray();
        var row = getOrCreateRow("global", SidebarNodeKind.Global, SidebarSelectionTarget.Global());
        row.UpdateTitle("Global");
        row.UpdateActivity(visibleThreads.FirstOrDefault()?.LastActiveAt, nowUtc);

        var children = visibleThreads
            .Take(settings.RecentThreadsPerProject)
            .Select(thread => CreateThreadNode(thread, getOrCreateRow, nowUtc))
            .ToArray();

        return new SidebarTreeNodeProjection(
            row.NodeId,
            SidebarNodeKind.Global,
            row,
            NerdFont.MdHomeOutline,
            SidebarAccent.Global,
            SidebarSelectionTarget.Global(),
            true,
            children);
    }

    private static SidebarTreeNodeProjection CreateProjectsNode(
        IReadOnlyList<ProjectDescriptor> projects,
        IReadOnlyList<WorkThreadDescriptor> threads,
        string? expandedProjectId,
        NavigatorSettings settings,
        Func<string, SidebarNodeKind, SidebarSelectionTarget?, SidebarNodeViewModel> getOrCreateRow,
        DateTimeOffset nowUtc)
    {
        var row = getOrCreateRow("projects", SidebarNodeKind.ProjectsRoot, null);
        row.UpdateTitle("Projects");
        row.UpdateActivity(activityAtUtc: null, nowUtc);

        var visibleProjects = projects
            .Where(static project => !project.Archived)
            .ToArray();
        var orderedProjects = settings.SortMode == NavigatorProjectSortMode.Date
            ? OrderProjectsByDate(visibleProjects, threads)
            : OrderProjectsByName(visibleProjects);

        var children = orderedProjects
            .Select(project => CreateProjectNode(project, threads, expandedProjectId, settings, getOrCreateRow, nowUtc))
            .ToArray();

        return new SidebarTreeNodeProjection(
            row.NodeId,
            SidebarNodeKind.ProjectsRoot,
            row,
            NerdFont.MdFolderMultipleOutline,
            SidebarAccent.Projects,
            null,
            true,
            children);
    }

    private static SidebarTreeNodeProjection CreateProjectNode(
        ProjectDescriptor project,
        IReadOnlyList<WorkThreadDescriptor> threads,
        string? expandedProjectId,
        NavigatorSettings settings,
        Func<string, SidebarNodeKind, SidebarSelectionTarget?, SidebarNodeViewModel> getOrCreateRow,
        DateTimeOffset nowUtc)
    {
        var projectThreads = ThreadScopePresentation.FilterThreadsForProject(threads, project.Id, includeInternal: true)
            .Where(static thread => thread.Status != WorkThreadStatus.Archived)
            .ToArray();
        var row = getOrCreateRow($"project:{project.Id}", SidebarNodeKind.Project, SidebarSelectionTarget.Project(project.Id));
        row.UpdateTitle(project.DisplayName);
        row.UpdateActivity(projectThreads.FirstOrDefault()?.LastActiveAt, nowUtc);

        var children = projectThreads
            .Take(settings.RecentThreadsPerProject)
            .Select(thread => CreateThreadNode(thread, getOrCreateRow, nowUtc))
            .ToArray();

        return new SidebarTreeNodeProjection(
            row.NodeId,
            SidebarNodeKind.Project,
            row,
            NerdFont.MdFolderOutline,
            SidebarAccent.Projects,
            SidebarSelectionTarget.Project(project.Id),
            string.Equals(project.Id, expandedProjectId, StringComparison.OrdinalIgnoreCase),
            children);
    }

    private static SidebarTreeNodeProjection CreateThreadNode(
        WorkThreadDescriptor thread,
        Func<string, SidebarNodeKind, SidebarSelectionTarget?, SidebarNodeViewModel> getOrCreateRow,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentNullException.ThrowIfNull(getOrCreateRow);

        var icon = thread.Kind switch
        {
            WorkThreadKind.GlobalThread => NerdFont.MdHomeOutline,
            WorkThreadKind.ProjectThread => NerdFont.MdChatProcessingOutline,
            WorkThreadKind.InternalThread => NerdFont.MdAccountCogOutline,
            _ => NerdFont.MdChatProcessingOutline,
        };
        var row = getOrCreateRow($"thread:{thread.ThreadId}", SidebarNodeKind.Thread, SidebarSelectionTarget.Thread(thread.ThreadId));
        row.UpdateTitle(SidebarThreadPresentation.CompactThreadTitle(thread.Title));
        row.UpdateActivity(thread.LastActiveAt, nowUtc);

        return new SidebarTreeNodeProjection(
            row.NodeId,
            SidebarNodeKind.Thread,
            row,
            icon,
            SidebarThreadPresentation.ResolveThreadAccent(thread.BackendId, thread.Kind),
            SidebarSelectionTarget.Thread(thread.ThreadId),
            false,
            []);
    }

    private static IEnumerable<ProjectDescriptor> OrderProjectsByName(IEnumerable<ProjectDescriptor> projects)
    {
        return projects
            .OrderBy(static project => project.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static project => project.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static project => project.Id, StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<ProjectDescriptor> OrderProjectsByDate(
        IEnumerable<ProjectDescriptor> projects,
        IReadOnlyList<WorkThreadDescriptor> threads,
        bool includeInternal = true)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(threads);

        return projects
            .Select(project => new
            {
                Project = project,
                LastActiveAt = ThreadScopePresentation.FilterThreadsForProject(threads, project.Id, includeInternal)
                    .Where(static thread => thread.Status != WorkThreadStatus.Archived)
                    .Select(static thread => (DateTimeOffset?)thread.LastActiveAt)
                    .Max(),
            })
            .OrderByDescending(static item => item.LastActiveAt.HasValue)
            .ThenByDescending(static item => item.LastActiveAt)
            .ThenBy(static item => item.Project.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.Project.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.Project.Id, StringComparer.OrdinalIgnoreCase)
            .Select(static item => item.Project);
    }
}
