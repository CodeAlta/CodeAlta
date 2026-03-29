using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Presentation.Sidebar;
using CodeAlta.ViewModels;
using CodeAlta.Views;

namespace CodeAlta.Tests;

[TestClass]
public sealed class CodeAltaAppSidebarTests
{
    [TestMethod]
    public void Build_CreatesProjectThreadChildrenForMatchingProject()
    {
        var timestamp = DateTimeOffset.Parse("2026-03-29T10:00:00+00:00");
        var project = CreateProject("project-1", "CodeAlta", @"C:\repo");
        var otherProject = CreateProject("project-2", "Other", @"C:\other");
        var visibleThread = CreateThread(
            threadId: "thread-1",
            title: "Recovered thread",
            kind: WorkThreadKind.ProjectThread,
            projectId: project.Id,
            backendId: AgentBackendIds.Codex.Value,
            workingDirectory: project.ProjectPath,
            lastActiveAt: timestamp.AddMinutes(2));
        var internalThread = CreateThread(
            threadId: "thread-2",
            title: "Internal helper",
            kind: WorkThreadKind.InternalThread,
            projectId: project.Id,
            backendId: AgentBackendIds.Codex.Value,
            workingDirectory: project.ProjectPath,
            lastActiveAt: timestamp.AddMinutes(1));
        var unrelatedThread = CreateThread(
            threadId: "thread-3",
            title: "Other thread",
            kind: WorkThreadKind.ProjectThread,
            projectId: otherProject.Id,
            backendId: AgentBackendIds.Codex.Value,
            workingDirectory: otherProject.ProjectPath,
            lastActiveAt: timestamp);

        var projection = BuildProjection(
            [project, otherProject],
            [unrelatedThread, internalThread, visibleThread],
            project.Id,
            nowUtc: timestamp.AddMinutes(3));

        Assert.AreEqual(2, projection.Roots.Count);
        var projectsRoot = projection.Roots[1];
        Assert.AreEqual("Projects", projectsRoot.Row.Title);
        Assert.AreEqual(2, projectsRoot.Children.Count);

        var projectNode = projectsRoot.Children[0];
        Assert.AreEqual(project.DisplayName, projectNode.Row.Title);
        Assert.AreEqual("1 min ago", projectNode.Row.RelativeActivityText);
        Assert.AreEqual(SidebarSelectionTarget.Project(project.Id), projectNode.SelectionTarget);
        Assert.IsTrue(projectNode.IsExpanded);
        Assert.AreEqual(2, projectNode.Children.Count);
        CollectionAssert.AreEquivalent(
            new SidebarSelectionTarget[]
            {
                SidebarSelectionTarget.Thread(visibleThread.ThreadId),
                SidebarSelectionTarget.Thread(internalThread.ThreadId),
            },
            projectNode.Children
                .Select(node => node.SelectionTarget!.Value)
                .ToArray());

        Assert.IsFalse(projectNode.Children.Any(node => node.SelectionTarget == SidebarSelectionTarget.Thread(unrelatedThread.ThreadId)));
    }

    [TestMethod]
    public void Build_FiltersArchivedProjectsAndThreads()
    {
        var project = CreateProject("project-1", "CodeAlta", @"C:\repo");
        var archivedProject = CreateProject("project-2", "Archived", @"C:\archive");
        archivedProject.Archived = true;
        var visibleThread = CreateThread("thread-1", "Visible", WorkThreadKind.ProjectThread, project.Id, AgentBackendIds.Codex.Value, project.ProjectPath, DateTimeOffset.Parse("2026-03-29T12:00:00+00:00"));
        var archivedThread = CreateThread("thread-2", "Archived", WorkThreadKind.ProjectThread, project.Id, AgentBackendIds.Codex.Value, project.ProjectPath, DateTimeOffset.Parse("2026-03-29T12:01:00+00:00"));
        archivedThread.Status = WorkThreadStatus.Archived;

        var projection = BuildProjection(
            [project, archivedProject],
            [visibleThread, archivedThread],
            project.Id,
            nowUtc: DateTimeOffset.Parse("2026-03-29T12:02:00+00:00"));

        Assert.AreEqual(1, projection.Roots[1].Children.Count);
        Assert.AreEqual(project.Id, projection.Roots[1].Children[0].SelectionTarget?.ProjectId);
        Assert.AreEqual(1, projection.Roots[1].Children[0].Children.Count);
        Assert.AreEqual(visibleThread.ThreadId, projection.Roots[1].Children[0].Children[0].SelectionTarget?.ThreadId);
    }

    [TestMethod]
    public void Build_SortsProjectsByDateWhenConfigured()
    {
        var timestamp = DateTimeOffset.Parse("2026-03-29T10:00:00+00:00");
        var olderProject = CreateProject("project-1", "Alpha", @"C:\alpha");
        var newerProject = CreateProject("project-2", "Zulu", @"C:\zulu");
        var threads = new[]
        {
            CreateThread("thread-1", "Older", WorkThreadKind.ProjectThread, olderProject.Id, AgentBackendIds.Codex.Value, olderProject.ProjectPath, timestamp),
            CreateThread("thread-2", "Newer", WorkThreadKind.ProjectThread, newerProject.Id, AgentBackendIds.Codex.Value, newerProject.ProjectPath, timestamp.AddDays(1)),
        };

        var projection = BuildProjection(
            [olderProject, newerProject],
            threads,
            expandedProjectId: newerProject.Id,
            nowUtc: timestamp.AddDays(1).AddMinutes(2),
            sortMode: NavigatorProjectSortMode.Date);

        CollectionAssert.AreEqual(
            new[] { newerProject.DisplayName, olderProject.DisplayName },
            projection.Roots[1].Children.Select(static node => node.Row.Title).ToArray());
    }

    [TestMethod]
    public void Build_UpdatesRelativeTimeTextWithoutChangingStructure()
    {
        var timestamp = DateTimeOffset.Parse("2026-03-29T10:00:00+00:00");
        var project = CreateProject("project-1", "CodeAlta", @"C:\repo");
        var thread = CreateThread("thread-1", "Recovered thread", WorkThreadKind.ProjectThread, project.Id, AgentBackendIds.Codex.Value, project.ProjectPath, timestamp);
        var rows = new Dictionary<string, SidebarNodeViewModel>(StringComparer.OrdinalIgnoreCase);

        var first = SidebarTreeProjectionBuilder.Build(
            [project],
            [thread],
            @"C:\global",
            project.Id,
            new NavigatorSettings(),
            (nodeId, kind, selectionTarget) => GetOrCreateRow(rows, nodeId, kind, selectionTarget),
            timestamp.AddSeconds(30));
        var second = SidebarTreeProjectionBuilder.Build(
            [project],
            [thread],
            @"C:\global",
            project.Id,
            new NavigatorSettings(),
            (nodeId, kind, selectionTarget) => GetOrCreateRow(rows, nodeId, kind, selectionTarget),
            timestamp.AddMinutes(1).AddSeconds(5));

        Assert.AreEqual(first, second);
        Assert.AreEqual("1 min ago", second.Roots[1].Children[0].Row.RelativeActivityText);
    }

    [TestMethod]
    public void ResolveTargetForProjectionChange_PrefersCurrentVisibleThreadSelection()
    {
        var project = CreateProject("project-1", "CodeAlta", @"C:\repo");
        var visibleThread = CreateThread(
            threadId: "thread-1",
            title: "Recovered thread",
            kind: WorkThreadKind.ProjectThread,
            projectId: project.Id,
            backendId: AgentBackendIds.Codex.Value,
            workingDirectory: project.ProjectPath,
            lastActiveAt: DateTimeOffset.Parse("2026-03-29T10:00:00+00:00"));
        var projection = BuildProjection(
            [project],
            [visibleThread],
            project.Id,
            nowUtc: DateTimeOffset.Parse("2026-03-29T10:05:00+00:00"));
        var currentTarget = SidebarSelectionResolver.ResolveCurrentTarget(
            visibleThread.ThreadId,
            project.Id,
            globalScopeSelected: false);

        var selectedTarget = SidebarSelectionResolver.ResolveTargetForProjectionChange(
            SidebarSelectionTarget.Project(project.Id),
            projection,
            currentTarget);

        Assert.AreEqual(SidebarSelectionTarget.Thread(visibleThread.ThreadId), selectedTarget);
    }

    [TestMethod]
    public void ResolveTargetForProjectionChange_FallsBackToGlobalWhenCurrentTargetIsMissing()
    {
        var project = CreateProject("project-1", "CodeAlta", @"C:\repo");
        var projection = BuildProjection(
            [project],
            [],
            project.Id,
            nowUtc: DateTimeOffset.Parse("2026-03-29T10:05:00+00:00"));

        var selectedTarget = SidebarSelectionResolver.ResolveTargetForProjectionChange(
            previousTarget: null,
            projection,
            SidebarSelectionTarget.Thread("missing-thread"));

        Assert.AreEqual(SidebarSelectionTarget.Global(), selectedTarget);
    }

    [TestMethod]
    public void SidebarView_ApplyProjectionBuildsSelectableProjectNode()
    {
        var project = CreateProject("project-1", "CodeAlta", @"C:\repo");
        var projection = BuildProjection(
            [project],
            [],
            project.Id,
            nowUtc: DateTimeOffset.Parse("2026-03-29T10:05:00+00:00"));
        var view = new SidebarView(new SidebarViewModel(), static () => { }, static () => { }, static () => { }, static _ => { });

        view.ApplyProjection(projection);

        Assert.AreEqual(2, view.Tree.Roots.Count);
        var projectNode = view.Tree.Roots[1].Children[0];
        Assert.AreEqual(SidebarSelectionTarget.Project(project.Id), projectNode.Data);
        Assert.IsTrue(projectNode.IsExpanded);
        Assert.IsTrue(projection.ContainsTarget(SidebarSelectionTarget.Project(project.Id)));
        Assert.AreEqual(1, projectNode.RightVisuals.Count);
    }

    [TestMethod]
    public void SidebarRelativeTimeFormatter_UsesExpectedBuckets()
    {
        var activity = DateTimeOffset.Parse("2026-03-29T10:00:00+00:00");

        Assert.AreEqual("just now", SidebarRelativeTimeFormatter.Format(activity, activity.AddSeconds(10)).RelativeText);
        Assert.AreEqual("1 min ago", SidebarRelativeTimeFormatter.Format(activity, activity.AddMinutes(1)).RelativeText);
        Assert.AreEqual("1 h ago", SidebarRelativeTimeFormatter.Format(activity, activity.AddHours(1)).RelativeText);
        Assert.AreEqual("1 day ago", SidebarRelativeTimeFormatter.Format(activity, activity.AddDays(1)).RelativeText);
        Assert.AreEqual("1 month ago", SidebarRelativeTimeFormatter.Format(activity, activity.AddDays(40)).RelativeText);
        Assert.AreEqual("1 year ago", SidebarRelativeTimeFormatter.Format(activity, activity.AddDays(370)).RelativeText);
    }

    private static SidebarTreeProjection BuildProjection(
        IReadOnlyList<ProjectDescriptor> projects,
        IReadOnlyList<WorkThreadDescriptor> threads,
        string? expandedProjectId,
        DateTimeOffset nowUtc,
        NavigatorProjectSortMode sortMode = NavigatorProjectSortMode.Name)
    {
        var rows = new Dictionary<string, SidebarNodeViewModel>(StringComparer.OrdinalIgnoreCase);
        return SidebarTreeProjectionBuilder.Build(
            projects,
            threads,
            @"C:\global",
            expandedProjectId,
            new NavigatorSettings
            {
                SortMode = sortMode,
                RecentThreadsPerProject = 3,
            },
            (nodeId, kind, selectionTarget) => GetOrCreateRow(rows, nodeId, kind, selectionTarget),
            nowUtc);
    }

    private static SidebarNodeViewModel GetOrCreateRow(
        Dictionary<string, SidebarNodeViewModel> rows,
        string nodeId,
        SidebarNodeKind kind,
        SidebarSelectionTarget? selectionTarget)
    {
        if (rows.TryGetValue(nodeId, out var existing))
        {
            return existing;
        }

        var created = new SidebarNodeViewModel(nodeId, kind, selectionTarget);
        rows.Add(nodeId, created);
        return created;
    }

    private static ProjectDescriptor CreateProject(string id, string displayName, string projectPath)
    {
        return new ProjectDescriptor
        {
            Id = id,
            Slug = displayName.ToLowerInvariant(),
            Name = displayName,
            DisplayName = displayName,
            ProjectPath = projectPath,
            DefaultBranch = "main",
        };
    }

    private static WorkThreadDescriptor CreateThread(
        string threadId,
        string title,
        WorkThreadKind kind,
        string? projectId,
        string backendId,
        string workingDirectory,
        DateTimeOffset lastActiveAt)
    {
        return new WorkThreadDescriptor
        {
            ThreadId = threadId,
            Kind = kind,
            BackendId = backendId,
            BackendSessionId = $"session-{threadId}",
            ProjectRef = projectId,
            WorkingDirectory = workingDirectory,
            Title = title,
            Status = WorkThreadStatus.Active,
            CreatedAt = lastActiveAt.AddMinutes(-2),
            UpdatedAt = lastActiveAt,
            LastActiveAt = lastActiveAt,
        };
    }
}
