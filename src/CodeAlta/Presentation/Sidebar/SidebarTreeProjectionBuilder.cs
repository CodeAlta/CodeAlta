using CodeAlta.Catalog;
using CodeAlta.Models;
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
        IReadOnlyCollection<string> expandedProjectIds,
        NavigatorSettings settings,
        Func<string, ThreadVisualState> getThreadVisualState,
        Func<string, SidebarNodeKind, SidebarSelectionTarget?, SidebarNodeViewModel> getOrCreateRow,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(threads);
        ArgumentException.ThrowIfNullOrWhiteSpace(globalRoot);
        ArgumentNullException.ThrowIfNull(expandedProjectIds);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(getThreadVisualState);
        ArgumentNullException.ThrowIfNull(getOrCreateRow);

        return new SidebarTreeProjection(
            [
                CreateGlobalNode(threads, settings, getThreadVisualState, getOrCreateRow, nowUtc),
                CreateProjectsNode(projects, threads, expandedProjectIds, settings, getThreadVisualState, getOrCreateRow, nowUtc),
            ]);
    }

    private static SidebarTreeNodeProjection CreateGlobalNode(
        IReadOnlyList<WorkThreadDescriptor> threads,
        NavigatorSettings settings,
        Func<string, ThreadVisualState> getThreadVisualState,
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
        row.UpdateStateIndicator(iconMarkup: null, visibleThreads.Any(thread => getThreadVisualState(thread.ThreadId).IsRunning));

        var children = visibleThreads
            .CreateThreadHierarchy(
                settings.RecentThreadsPerProject,
                threads.Where(static thread => thread.Status != WorkThreadStatus.Archived).ToArray())
            .Select(node => CreateThreadNode(node, getThreadVisualState, getOrCreateRow, nowUtc))
            .ToArray();

        return new SidebarTreeNodeProjection(
            row.NodeId,
            SidebarNodeKind.Global,
            row,
            NerdFont.MdHomeOutline,
            SidebarAccent.Global,
            SidebarSelectionTarget.Global(),
            true,
            CreateGlobalActions(),
            children);
    }

    private static SidebarTreeNodeProjection CreateProjectsNode(
        IReadOnlyList<ProjectDescriptor> projects,
        IReadOnlyList<WorkThreadDescriptor> threads,
        IReadOnlyCollection<string> expandedProjectIds,
        NavigatorSettings settings,
        Func<string, ThreadVisualState> getThreadVisualState,
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
            .Select(project => CreateProjectNode(project, threads, expandedProjectIds, settings, getThreadVisualState, getOrCreateRow, nowUtc))
            .ToArray();

        return new SidebarTreeNodeProjection(
            row.NodeId,
            SidebarNodeKind.ProjectsRoot,
            row,
            NerdFont.MdFolderMultipleOutline,
            SidebarAccent.Projects,
            null,
            true,
            CreateProjectsRootActions(),
            children);
    }

    private static SidebarTreeNodeProjection CreateProjectNode(
        ProjectDescriptor project,
        IReadOnlyList<WorkThreadDescriptor> threads,
        IReadOnlyCollection<string> expandedProjectIds,
        NavigatorSettings settings,
        Func<string, ThreadVisualState> getThreadVisualState,
        Func<string, SidebarNodeKind, SidebarSelectionTarget?, SidebarNodeViewModel> getOrCreateRow,
        DateTimeOffset nowUtc)
    {
        var projectThreads = ThreadScopePresentation.FilterThreadsForProject(threads, project.Id, includeInternal: true)
            .Where(static thread => thread.Status != WorkThreadStatus.Archived)
            .ToArray();
        var row = getOrCreateRow($"project:{project.Id}", SidebarNodeKind.Project, SidebarSelectionTarget.Project(project.Id));
        row.UpdateTitle(project.DisplayName);
        row.UpdateActivity(projectThreads.OrderByDescending(static thread => thread.LastActiveAt).FirstOrDefault()?.LastActiveAt, nowUtc);
        row.UpdateStateIndicator(iconMarkup: null, projectThreads.Any(thread => getThreadVisualState(thread.ThreadId).IsRunning));

        var children = projectThreads
            .CreateThreadHierarchy(
                settings.RecentThreadsPerProject,
                threads.Where(static thread => thread.Status != WorkThreadStatus.Archived).ToArray())
            .Select(node => CreateThreadNode(node, getThreadVisualState, getOrCreateRow, nowUtc))
            .ToArray();

        return new SidebarTreeNodeProjection(
            row.NodeId,
            SidebarNodeKind.Project,
            row,
            NerdFont.MdFolderOutline,
            SidebarAccent.Projects,
            SidebarSelectionTarget.Project(project.Id),
            expandedProjectIds.Contains(project.Id, StringComparer.OrdinalIgnoreCase),
            CreateProjectActions(),
            children);
    }

    private static SidebarTreeNodeProjection CreateThreadNode(
        ThreadHierarchyNode node,
        Func<string, ThreadVisualState> getThreadVisualState,
        Func<string, SidebarNodeKind, SidebarSelectionTarget?, SidebarNodeViewModel> getOrCreateRow,
        DateTimeOffset nowUtc)
    {
        var thread = node.Thread;
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentNullException.ThrowIfNull(getThreadVisualState);
        ArgumentNullException.ThrowIfNull(getOrCreateRow);

        var icon = thread.Kind switch
        {
            WorkThreadKind.GlobalThread => NerdFont.MdHomeOutline,
            WorkThreadKind.ProjectThread => NerdFont.MdChatProcessingOutline,
            WorkThreadKind.InternalThread => NerdFont.MdAccountCogOutline,
            _ => NerdFont.MdChatProcessingOutline,
        };
        var row = getOrCreateRow($"thread:{thread.ThreadId}", SidebarNodeKind.Thread, SidebarSelectionTarget.Thread(thread.ThreadId));
        row.UpdateTitle(thread.Title);
        row.UpdateActivity(thread.LastActiveAt, nowUtc);
        var visualState = getThreadVisualState(thread.ThreadId);
        var accent = SidebarThreadPresentation.ResolveThreadAccent(thread.BackendId, thread.Kind);
        var stateTooltip = ResolveLineageDiagnosticTooltip(node.LineageDiagnostic, thread);
        row.UpdateStateIndicator(
            visualState.IsRunning
                ? null
                : BuildThreadStateIconMarkup(visualState, accent, node.LineageDiagnostic),
            visualState.IsRunning,
            visualState.IsRunning ? null : stateTooltip);

        var childNodes = node.Children
            .Select(child => CreateThreadNode(child, getThreadVisualState, getOrCreateRow, nowUtc))
            .ToArray();

        return new SidebarTreeNodeProjection(
            row.NodeId,
            SidebarNodeKind.Thread,
            row,
            icon,
            accent,
            SidebarSelectionTarget.Thread(thread.ThreadId),
            childNodes.Length > 0,
            CreateThreadActions(),
            childNodes);
    }

    private static IReadOnlyList<ThreadHierarchyNode> CreateThreadHierarchy(
        this IReadOnlyList<WorkThreadDescriptor> threads,
        int rootLimit,
        IReadOnlyList<WorkThreadDescriptor> lineageThreads)
    {
        ArgumentNullException.ThrowIfNull(lineageThreads);

        var byId = threads
            .Where(static thread => !string.IsNullOrWhiteSpace(thread.ThreadId))
            .GroupBy(static thread => thread.ThreadId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.OrdinalIgnoreCase);
        var lineageById = lineageThreads
            .Where(static thread => !string.IsNullOrWhiteSpace(thread.ThreadId))
            .GroupBy(static thread => thread.ThreadId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.OrdinalIgnoreCase);
        var diagnosticsById = byId.ToDictionary(
            static pair => pair.Key,
            pair => GetLineageDiagnostic(pair.Value, lineageById),
            StringComparer.OrdinalIgnoreCase);
        var children = threads
            .Where(thread => IsHierarchyChild(thread, diagnosticsById))
            .GroupBy(static thread => thread.ParentThreadId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                group => group
                    .OrderByDescending(child => GetSubtreeLastActiveAt(child, byId, diagnosticsById))
                    .ThenBy(static child => child.Title, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);

        return threads
            .Where(thread => !IsHierarchyChild(thread, diagnosticsById))
            .OrderByDescending(thread => GetSubtreeLastActiveAt(thread, byId, diagnosticsById))
            .ThenBy(static thread => thread.Title, StringComparer.OrdinalIgnoreCase)
            .Take(rootLimit)
            .Select(thread => BuildHierarchyNode(thread, children, diagnosticsById, new HashSet<string>(StringComparer.OrdinalIgnoreCase)))
            .ToArray();
    }

    private static ThreadHierarchyNode BuildHierarchyNode(
        WorkThreadDescriptor thread,
        IReadOnlyDictionary<string, WorkThreadDescriptor[]> children,
        IReadOnlyDictionary<string, ThreadLineageDiagnostic> diagnosticsById,
        HashSet<string> visiting)
    {
        if (!visiting.Add(thread.ThreadId))
        {
            return new ThreadHierarchyNode(thread, [], ThreadLineageDiagnostic.Cycle);
        }

        var childNodes = children.TryGetValue(thread.ThreadId, out var directChildren)
            ? directChildren
                .Select(child => BuildHierarchyNode(child, children, diagnosticsById, visiting))
                .ToArray()
            : [];
        visiting.Remove(thread.ThreadId);
        return new ThreadHierarchyNode(thread, childNodes, diagnosticsById.GetValueOrDefault(thread.ThreadId));
    }

    private static bool IsHierarchyChild(
        WorkThreadDescriptor thread,
        IReadOnlyDictionary<string, ThreadLineageDiagnostic> diagnosticsById)
        => !string.IsNullOrWhiteSpace(thread.ParentThreadId) &&
           diagnosticsById.TryGetValue(thread.ThreadId, out var diagnostic) &&
           diagnostic == ThreadLineageDiagnostic.None;

    private static ThreadLineageDiagnostic GetLineageDiagnostic(
        WorkThreadDescriptor thread,
        IReadOnlyDictionary<string, WorkThreadDescriptor> byId)
    {
        if (string.IsNullOrWhiteSpace(thread.ParentThreadId))
        {
            return ThreadLineageDiagnostic.None;
        }

        if (!byId.TryGetValue(thread.ParentThreadId, out var parent))
        {
            return ThreadLineageDiagnostic.MissingParent;
        }

        if (!string.Equals(parent.ProjectRef, thread.ProjectRef, StringComparison.OrdinalIgnoreCase))
        {
            return ThreadLineageDiagnostic.CrossProjectParent;
        }

        return HasLineageCycle(thread, byId) ? ThreadLineageDiagnostic.Cycle : ThreadLineageDiagnostic.None;
    }

    private static bool HasLineageCycle(
        WorkThreadDescriptor thread,
        IReadOnlyDictionary<string, WorkThreadDescriptor> byId)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { thread.ThreadId };
        var current = thread.ParentThreadId;
        while (!string.IsNullOrWhiteSpace(current) && byId.TryGetValue(current, out var parent))
        {
            if (!visited.Add(parent.ThreadId))
            {
                return true;
            }

            current = parent.ParentThreadId;
        }

        return false;
    }

    private static DateTimeOffset GetSubtreeLastActiveAt(
        WorkThreadDescriptor thread,
        IReadOnlyDictionary<string, WorkThreadDescriptor> byId,
        IReadOnlyDictionary<string, ThreadLineageDiagnostic> diagnosticsById)
    {
        var latest = thread.LastActiveAt;
        foreach (var child in byId.Values.Where(candidate =>
                     string.Equals(candidate.ParentThreadId, thread.ThreadId, StringComparison.OrdinalIgnoreCase) &&
                     IsHierarchyChild(candidate, diagnosticsById)))
        {
            var childLatest = GetSubtreeLastActiveAt(child, byId, diagnosticsById);
            if (childLatest > latest)
            {
                latest = childLatest;
            }
        }

        return latest;
    }

    private static string? BuildThreadStateIconMarkup(
        ThreadVisualState visualState,
        SidebarAccent accent,
        ThreadLineageDiagnostic lineageDiagnostic)
    {
        if (lineageDiagnostic != ThreadLineageDiagnostic.None)
        {
            return BuildLineageDiagnosticIconMarkup();
        }

        return visualState.HasPromptDraft
            ? SidebarThreadPresentation.BuildEditedPromptIconMarkup(accent)
            : null;
    }

    private static string BuildLineageDiagnosticIconMarkup()
        => $"[{UiPalette.GetStatusToneMarkup(StatusTone.Warning)}]{NerdFont.MdAlertCircleOutline}[/]";

    private static string? ResolveLineageDiagnosticTooltip(ThreadLineageDiagnostic diagnostic, WorkThreadDescriptor thread)
    {
        return diagnostic switch
        {
            ThreadLineageDiagnostic.MissingParent => $"Parent session '{thread.ParentThreadId}' is missing; rendering this session at the project root.",
            ThreadLineageDiagnostic.CrossProjectParent => $"Parent session '{thread.ParentThreadId}' belongs to another scope; rendering this session at the project root while preserving provenance.",
            ThreadLineageDiagnostic.Cycle => "Session parent lineage contains a cycle; rendering this session at the project root.",
            _ => null,
        };
    }

    private enum ThreadLineageDiagnostic
    {
        None,
        MissingParent,
        CrossProjectParent,
        Cycle,
    }

    private sealed record ThreadHierarchyNode(WorkThreadDescriptor Thread, IReadOnlyList<ThreadHierarchyNode> Children, ThreadLineageDiagnostic LineageDiagnostic);

    private static IReadOnlyList<SidebarRowActionDescriptor> CreateProjectActions()
        =>
        [
            new SidebarRowActionDescriptor(SidebarRowActionKind.OpenProjectThreads, NerdFont.MdFormatListBulleted, "Show all project threads"),
            new SidebarRowActionDescriptor(SidebarRowActionKind.OpenProjectDetails, NerdFont.MdInformationOutline, "Show project details"),
            new SidebarRowActionDescriptor(SidebarRowActionKind.DeleteProject, NerdFont.MdTrashCanOutline, "Delete project"),
        ];

    private static IReadOnlyList<SidebarRowActionDescriptor> CreateProjectsRootActions()
        => [new SidebarRowActionDescriptor(SidebarRowActionKind.OpenFolder, NerdFont.MdPlus, "Open folder", SidebarRowActionVisibility.Always)];

    private static IReadOnlyList<SidebarRowActionDescriptor> CreateGlobalActions()
        => [new SidebarRowActionDescriptor(SidebarRowActionKind.OpenProjectThreads, NerdFont.MdFormatListBulleted, "Show all global threads")];

    private static IReadOnlyList<SidebarRowActionDescriptor> CreateThreadActions()
        => [new SidebarRowActionDescriptor(SidebarRowActionKind.DeleteThread, NerdFont.MdTrashCanOutline, "Delete thread")];

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
