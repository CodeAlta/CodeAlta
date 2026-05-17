using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Presentation.Sidebar;
using CodeAlta.ViewModels;
using CodeAlta.Views;
using XenoAtom.Terminal;
using XenoAtom.Terminal.Backends;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Hosting;
using XenoAtom.Terminal.UI.Input;
using XenoAtom.Terminal.UI.Layout;
using System.Reflection;

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
            [project.Id],
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
    public void Build_NestsSameProjectChildThreadsUnderParent()
    {
        var timestamp = DateTimeOffset.Parse("2026-03-29T10:00:00+00:00");
        var project = CreateProject("project-1", "CodeAlta", @"C:\repo");
        var parent = CreateThread("thread-parent", "Parent", WorkThreadKind.ProjectThread, project.Id, AgentBackendIds.Codex.Value, project.ProjectPath, timestamp);
        var child = CreateThread("thread-child", "Child", WorkThreadKind.ProjectThread, project.Id, AgentBackendIds.Codex.Value, project.ProjectPath, timestamp.AddMinutes(1));
        var crossProjectChild = CreateThread("thread-cross-project", "Cross project", WorkThreadKind.ProjectThread, "project-2", AgentBackendIds.Codex.Value, @"C:\other", timestamp.AddMinutes(2));
        child.ParentThreadId = parent.ThreadId;
        crossProjectChild.ParentThreadId = parent.ThreadId;

        var projection = BuildProjection(
            [project, CreateProject("project-2", "Other", @"C:\other")],
            [parent, child, crossProjectChild],
            [project.Id, "project-2"],
            nowUtc: timestamp.AddMinutes(3));

        var projectNode = projection.Roots[1].Children.Single(node => node.SelectionTarget == SidebarSelectionTarget.Project(project.Id));
        Assert.AreEqual(1, projectNode.Children.Count);
        var parentNode = projectNode.Children[0];
        Assert.AreEqual(SidebarSelectionTarget.Thread(parent.ThreadId), parentNode.SelectionTarget);
        Assert.IsTrue(parentNode.IsExpanded);
        Assert.AreEqual(1, parentNode.Children.Count);
        Assert.AreEqual(SidebarSelectionTarget.Thread(child.ThreadId), parentNode.Children[0].SelectionTarget);

        var otherProjectNode = projection.Roots[1].Children.Single(node => node.SelectionTarget == SidebarSelectionTarget.Project("project-2"));
        Assert.AreEqual(SidebarSelectionTarget.Thread(crossProjectChild.ThreadId), otherProjectNode.Children[0].SelectionTarget);
        StringAssert.Contains(otherProjectNode.Children[0].Row.StateIconMarkup, NerdFont.MdAlertCircleOutline.ToString());
        StringAssert.Contains(otherProjectNode.Children[0].Row.StateTooltip, "another scope");
    }

    [TestMethod]
    public void Build_LineageAnomaliesRenderAtProjectRootWithDiagnosticMarkers()
    {
        var timestamp = DateTimeOffset.Parse("2026-03-29T10:00:00+00:00");
        var project = CreateProject("project-1", "CodeAlta", @"C:\repo");
        var missingParent = CreateThread("thread-missing", "Missing parent", WorkThreadKind.ProjectThread, project.Id, AgentBackendIds.Codex.Value, project.ProjectPath, timestamp.AddMinutes(1));
        var cycleParent = CreateThread("thread-cycle-parent", "Cycle parent", WorkThreadKind.ProjectThread, project.Id, AgentBackendIds.Codex.Value, project.ProjectPath, timestamp.AddMinutes(2));
        var cycleChild = CreateThread("thread-cycle-child", "Cycle child", WorkThreadKind.ProjectThread, project.Id, AgentBackendIds.Codex.Value, project.ProjectPath, timestamp.AddMinutes(3));
        missingParent.ParentThreadId = "thread-no-longer-present";
        cycleParent.ParentThreadId = cycleChild.ThreadId;
        cycleChild.ParentThreadId = cycleParent.ThreadId;
        var rows = new Dictionary<string, SidebarNodeViewModel>(StringComparer.OrdinalIgnoreCase);

        var projection = SidebarTreeProjectionBuilder.Build(
            [project],
            [missingParent, cycleParent, cycleChild],
            @"C:\global",
            [project.Id],
            new NavigatorSettings { RecentThreadsPerProject = 10 },
            static _ => default,
            static (_, _) => false,
            (nodeId, kind, selectionTarget) => GetOrCreateRow(rows, nodeId, kind, selectionTarget),
            timestamp.AddMinutes(4));

        var projectNode = projection.Roots[1].Children.Single(node => node.SelectionTarget == SidebarSelectionTarget.Project(project.Id));
        CollectionAssert.AreEquivalent(
            new[] { missingParent.ThreadId, cycleParent.ThreadId, cycleChild.ThreadId },
            projectNode.Children.Select(static node => node.SelectionTarget!.Value.ThreadId).ToArray());
        Assert.IsTrue(projectNode.Children.All(static node => node.Children.Count == 0));
        StringAssert.Contains(rows[$"thread:{missingParent.ThreadId}"].StateIconMarkup, NerdFont.MdAlertCircleOutline.ToString());
        StringAssert.Contains(rows[$"thread:{missingParent.ThreadId}"].StateTooltip, "missing");
        StringAssert.Contains(rows[$"thread:{cycleParent.ThreadId}"].StateIconMarkup, NerdFont.MdAlertCircleOutline.ToString());
        StringAssert.Contains(rows[$"thread:{cycleParent.ThreadId}"].StateTooltip, "cycle");
        StringAssert.Contains(rows[$"thread:{cycleChild.ThreadId}"].StateIconMarkup, NerdFont.MdAlertCircleOutline.ToString());
        StringAssert.Contains(rows[$"thread:{cycleChild.ThreadId}"].StateTooltip, "cycle");
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
            [project.Id],
            nowUtc: DateTimeOffset.Parse("2026-03-29T12:02:00+00:00"));

        Assert.AreEqual(1, projection.Roots[1].Children.Count);
        Assert.AreEqual(project.Id, projection.Roots[1].Children[0].SelectionTarget?.ProjectId);
        Assert.AreEqual(1, projection.Roots[1].Children[0].Children.Count);
        Assert.AreEqual(visibleThread.ThreadId, projection.Roots[1].Children[0].Children[0].SelectionTarget?.ThreadId);
    }

    [TestMethod]
    public void Build_GlobalNodeIncludesOpenThreadsAction()
    {
        var globalThread = CreateThread(
            "global-1",
            "Global thread",
            WorkThreadKind.GlobalThread,
            projectId: null,
            AgentBackendIds.Codex.Value,
            @"C:\global",
            DateTimeOffset.Parse("2026-03-29T12:00:00+00:00"));

        var projection = BuildProjection(
            projects: [],
            threads: [globalThread],
            expandedProjectIds: [],
            nowUtc: DateTimeOffset.Parse("2026-03-29T12:02:00+00:00"));

        var globalNode = projection.Roots[0];
        Assert.AreEqual(SidebarSelectionTarget.Global(), globalNode.SelectionTarget);
        Assert.AreEqual(1, globalNode.Actions.Count);
        Assert.AreEqual(SidebarRowActionKind.OpenProjectThreads, globalNode.Actions[0].Kind);
    }

    [TestMethod]
    public void Build_ProjectsRootIncludesAlwaysVisibleOpenFolderAction()
    {
        var projection = BuildProjection(
            projects: [],
            threads: [],
            expandedProjectIds: [],
            nowUtc: DateTimeOffset.Parse("2026-03-29T12:02:00+00:00"));

        var projectsRoot = projection.Roots[1];
        Assert.AreEqual("Projects", projectsRoot.Row.Title);
        Assert.AreEqual(1, projectsRoot.Actions.Count);
        Assert.AreEqual(SidebarRowActionKind.OpenFolder, projectsRoot.Actions[0].Kind);
        Assert.AreEqual(SidebarRowActionVisibility.Always, projectsRoot.Actions[0].Visibility);
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
            expandedProjectIds: [newerProject.Id],
            nowUtc: timestamp.AddDays(1).AddMinutes(2),
            sortMode: NavigatorProjectSortMode.Date);

        CollectionAssert.AreEqual(
            new[] { newerProject.DisplayName, olderProject.DisplayName },
            projection.Roots[1].Children.Select(static node => node.Row.Title).ToArray());
    }

    [TestMethod]
    public void Build_CanExpandMultipleProjects()
    {
        var timestamp = DateTimeOffset.Parse("2026-03-29T10:00:00+00:00");
        var project1 = CreateProject("project-1", "Alpha", @"C:\alpha");
        var project2 = CreateProject("project-2", "Beta", @"C:\beta");

        var projection = BuildProjection(
            [project1, project2],
            [],
            [project1.Id, project2.Id],
            nowUtc: timestamp);

        Assert.IsTrue(projection.Roots[1].Children[0].IsExpanded);
        Assert.IsTrue(projection.Roots[1].Children[1].IsExpanded);
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
            [project.Id],
            new NavigatorSettings(),
            static _ => default,
            static (_, _) => false,
            (nodeId, kind, selectionTarget) => GetOrCreateRow(rows, nodeId, kind, selectionTarget),
            timestamp.AddSeconds(30));
        var second = SidebarTreeProjectionBuilder.Build(
            [project],
            [thread],
            @"C:\global",
            [project.Id],
            new NavigatorSettings(),
            static _ => default,
            static (_, _) => false,
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
            [project.Id],
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
            [project.Id],
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
        var thread = CreateThread("thread-1", "Recovered thread", WorkThreadKind.ProjectThread, project.Id, AgentBackendIds.Codex.Value, project.ProjectPath, DateTimeOffset.Parse("2026-03-29T10:04:00+00:00"));
        var projection = BuildProjection(
            [project],
            [thread],
            [project.Id],
            nowUtc: DateTimeOffset.Parse("2026-03-29T10:05:00+00:00"));
        var view = new SidebarView(new SidebarViewModel(), static () => { }, static () => { }, static () => { }, static () => { }, static _ => { }, static _ => { }, new CapturingSidebarRowCommandDispatcher(), static _ => { });

        view.ApplyProjection(projection);

        Assert.AreEqual(2, view.Tree.Roots.Count);
        var projectsRoot = view.Tree.Roots[1];
        var projectNode = view.Tree.Roots[1].Children[0];
        var threadNode = projectNode.Children[0];
        AssertSidebarIconStyle(projectsRoot, expectedBasic16Index: 12);
        AssertSidebarIconStyle(projectNode, expectedBasic16Index: 12);
        AssertSidebarIconStyle(threadNode, expectedBasic16Index: 10);
        Assert.AreEqual(1, projectsRoot.RightVisuals.Count);
        Assert.AreEqual(TreeNodeRightVisualVisibility.Always, projectsRoot.RightVisuals[0].Visibility);
        Assert.AreEqual(SidebarSelectionTarget.Project(project.Id), projectNode.Data);
        Assert.IsTrue(projectNode.IsExpanded);
        Assert.IsTrue(projection.ContainsTarget(SidebarSelectionTarget.Project(project.Id)));
        Assert.AreEqual(4, projectNode.RightVisuals.Count);
        Assert.AreEqual(TreeNodeRightVisualVisibility.Always, projectNode.RightVisuals[0].Visibility);
        Assert.AreEqual(TreeNodeRightVisualVisibility.Hover, projectNode.RightVisuals[1].Visibility);
        Assert.AreEqual(TreeNodeRightVisualVisibility.Hover, projectNode.RightVisuals[2].Visibility);
        Assert.AreEqual(TreeNodeRightVisualVisibility.Hover, projectNode.RightVisuals[3].Visibility);
        Assert.AreEqual(2, threadNode.RightVisuals.Count);
        Assert.AreEqual(TreeNodeRightVisualVisibility.Always, threadNode.RightVisuals[0].Visibility);
        Assert.AreEqual(TreeNodeRightVisualVisibility.Hover, threadNode.RightVisuals[1].Visibility);
    }

    [TestMethod]
    public void SidebarView_ApplyProjectionBuildsSelectableGlobalNodeWithOpenThreadsAction()
    {
        var globalThread = CreateThread("global-1", "Recovered global thread", WorkThreadKind.GlobalThread, null, AgentBackendIds.Codex.Value, @"C:\global", DateTimeOffset.Parse("2026-03-29T10:04:00+00:00"));
        var projection = BuildProjection(
            projects: [],
            threads: [globalThread],
            expandedProjectIds: [],
            nowUtc: DateTimeOffset.Parse("2026-03-29T10:05:00+00:00"));
        var view = new SidebarView(new SidebarViewModel(), static () => { }, static () => { }, static () => { }, static () => { }, static _ => { }, static _ => { }, new CapturingSidebarRowCommandDispatcher(), static _ => { });

        view.ApplyProjection(projection);

        Assert.AreEqual(2, view.Tree.Roots.Count);
        var globalNode = view.Tree.Roots[0];
        var globalThreadNode = globalNode.Children[0];
        AssertSidebarIconStyle(globalNode, expectedBasic16Index: 3);
        AssertSidebarIconStyle(globalThreadNode, expectedBasic16Index: 3);
        Assert.AreEqual(SidebarSelectionTarget.Global(), globalNode.Data);
        Assert.AreEqual(2, globalNode.RightVisuals.Count);
        Assert.AreEqual(TreeNodeRightVisualVisibility.Always, globalNode.RightVisuals[0].Visibility);
        Assert.AreEqual(TreeNodeRightVisualVisibility.Hover, globalNode.RightVisuals[1].Visibility);
    }

    [TestMethod]
    public void Build_RunningThreadsPromoteSpinnerStateToThreadAndProjectRows()
    {
        var timestamp = DateTimeOffset.Parse("2026-03-29T10:00:00+00:00");
        var project = CreateProject("project-1", "CodeAlta", @"C:\repo");
        var thread = CreateThread("thread-1", "Recovered thread", WorkThreadKind.ProjectThread, project.Id, AgentBackendIds.Codex.Value, project.ProjectPath, timestamp);
        var rows = new Dictionary<string, SidebarNodeViewModel>(StringComparer.OrdinalIgnoreCase);

        SidebarTreeProjectionBuilder.Build(
            [project],
            [thread],
            @"C:\global",
            [project.Id],
            new NavigatorSettings(),
            static _ => new ThreadVisualState(IsRunning: true, HasPromptDraft: false),
            static (_, _) => false,
            (nodeId, kind, selectionTarget) => GetOrCreateRow(rows, nodeId, kind, selectionTarget),
            timestamp.AddMinutes(1));

        Assert.IsTrue(rows[$"project:{project.Id}"].ShowStateSpinner);
        Assert.IsTrue(rows[$"thread:{thread.ThreadId}"].ShowStateSpinner);
        Assert.IsNull(rows[$"thread:{thread.ThreadId}"].StateIconMarkup);
    }

    [TestMethod]
    public void Build_EditedPromptThreadsExposeDraftIconState()
    {
        var timestamp = DateTimeOffset.Parse("2026-03-29T10:00:00+00:00");
        var project = CreateProject("project-1", "CodeAlta", @"C:\repo");
        var thread = CreateThread("thread-1", "Recovered thread", WorkThreadKind.ProjectThread, project.Id, AgentBackendIds.Codex.Value, project.ProjectPath, timestamp);
        var rows = new Dictionary<string, SidebarNodeViewModel>(StringComparer.OrdinalIgnoreCase);

        SidebarTreeProjectionBuilder.Build(
            [project],
            [thread],
            @"C:\global",
            [project.Id],
            new NavigatorSettings(),
            static _ => new ThreadVisualState(IsRunning: false, HasPromptDraft: true),
            static (_, _) => false,
            (nodeId, kind, selectionTarget) => GetOrCreateRow(rows, nodeId, kind, selectionTarget),
            timestamp.AddMinutes(1));

        Assert.IsFalse(rows[$"thread:{thread.ThreadId}"].ShowStateSpinner);
        StringAssert.Contains(rows[$"thread:{thread.ThreadId}"].StateIconMarkup, NerdFont.MdSquareEditOutline.ToString());
    }

    [TestMethod]
    public void Build_EditedProjectDraftExposesDraftIconStateOnProjectRow()
    {
        var timestamp = DateTimeOffset.Parse("2026-03-29T10:00:00+00:00");
        var project = CreateProject("project-1", "CodeAlta", @"C:\repo");
        var rows = new Dictionary<string, SidebarNodeViewModel>(StringComparer.OrdinalIgnoreCase);

        SidebarTreeProjectionBuilder.Build(
            [project],
            [],
            @"C:\global",
            [project.Id],
            new NavigatorSettings(),
            static _ => default,
            (projectId, isGlobal) => !isGlobal && string.Equals(projectId, "project-1", StringComparison.OrdinalIgnoreCase),
            (nodeId, kind, selectionTarget) => GetOrCreateRow(rows, nodeId, kind, selectionTarget),
            timestamp.AddMinutes(1));

        Assert.IsFalse(rows[$"project:{project.Id}"].ShowStateSpinner);
        StringAssert.Contains(rows[$"project:{project.Id}"].StateIconMarkup, NerdFont.MdSquareEditOutline.ToString());
        StringAssert.Contains(rows[$"project:{project.Id}"].StateTooltip, "Project draft");
    }

    [TestMethod]
    public void SidebarView_F2CommandStartsInlineProjectRename()
    {
        var project = CreateProject("project-1", "CodeAlta", @"C:\repo");
        var projection = BuildProjection(
            [project],
            [],
            [project.Id],
            nowUtc: DateTimeOffset.Parse("2026-03-29T10:05:00+00:00"));
        var renameCount = 0;
        var view = new SidebarView(new SidebarViewModel(), static () => { }, static () => { }, static () => { }, () => renameCount++, static _ => { }, static _ => { }, new CapturingSidebarRowCommandDispatcher(), static _ => { });

        view.ApplyProjection(projection);
        view.Tree.Measure(new LayoutConstraints(0, 80, 0, 20));
        view.Tree.Arrange(new Rectangle(0, 0, 80, 20));
        Assert.IsTrue(view.TrySelectTarget(SidebarSelectionTarget.Project(project.Id)));

        var renameCommand = view.Tree.Commands.Single(command => command.Gesture == new KeyGesture(TerminalKey.F2));
        renameCommand.Execute(view.Tree);

        Assert.AreEqual(1, renameCount);
    }

    [TestMethod]
    public void SidebarView_KeyNavigationNotifiesSelectedTargetChanged()
    {
        var project = CreateProject("project-1", "CodeAlta", @"C:\repo");
        var thread = CreateThread("thread-1", "Recovered thread", WorkThreadKind.ProjectThread, project.Id, AgentBackendIds.Codex.Value, project.ProjectPath, DateTimeOffset.Parse("2026-03-29T10:04:00+00:00"));
        var projection = BuildProjection(
            [project],
            [thread],
            [project.Id],
            nowUtc: DateTimeOffset.Parse("2026-03-29T10:05:00+00:00"));
        var observedTargets = new List<SidebarSelectionTarget?>();
        var view = new SidebarView(new SidebarViewModel(), static () => { }, static () => { }, static () => { }, static () => { }, static _ => { }, static _ => { }, new CapturingSidebarRowCommandDispatcher(), target => observedTargets.Add(target));

        view.ApplyProjection(projection);

        using var session = Terminal.Open(new InMemoryTerminalBackend(new TerminalSize(80, 20)), new TerminalOptions { ImplicitStartInput = true }, force: true);
        var app = new TerminalApp(
            view.Root,
            session.Instance,
            new TerminalAppOptions
            {
                HostKind = TerminalHostKind.Fullscreen,
                EnableMouse = true,
                MouseMode = TerminalMouseMode.Move,
            });

        InvokeTerminalApp(app, "BeginRun");
        try
        {
            TickTerminalApp(app);

            var backend = (InMemoryTerminalBackend)session.Instance.Backend;
            backend.PushEvent(new TerminalKeyEvent { Key = TerminalKey.Down });
            backend.PushEvent(new TerminalKeyEvent { Key = TerminalKey.Down });
            TickTerminalApp(app);

            Assert.AreEqual(SidebarSelectionTarget.Project(project.Id), observedTargets.Last());
        }
        finally
        {
            InvokeTerminalApp(app, "EndRun");
        }
    }

    [TestMethod]
    public void SidebarNodeViewModel_InlineRenameBlocksWhitespaceCommit()
    {
        var row = new SidebarNodeViewModel("project:project-1", SidebarNodeKind.Project, SidebarSelectionTarget.Project("project-1"));
        row.UpdateTitle("CodeAlta");
        row.BeginInlineEdit();
        row.InlineEditText = "   ";

        Assert.IsFalse(row.TryGetInlineEditValue(out _));
        Assert.IsTrue(row.IsInlineEditing);
        Assert.IsNotNull(row.InlineEditValidationMessage);
    }

    [TestMethod]
    public void SidebarNodeViewModel_InlineRenameCanCommitAndCancel()
    {
        var row = new SidebarNodeViewModel("project:project-1", SidebarNodeKind.Project, SidebarSelectionTarget.Project("project-1"));
        row.UpdateTitle("CodeAlta");
        row.BeginInlineEdit();
        row.InlineEditText = "CodeAlta UI";

        Assert.IsTrue(row.TryGetInlineEditValue(out var displayName));
        Assert.AreEqual("CodeAlta UI", displayName);

        row.CancelInlineEdit();

        Assert.IsFalse(row.IsInlineEditing);
        Assert.AreEqual("CodeAlta", row.InlineEditText);
        Assert.IsNull(row.InlineEditValidationMessage);
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
        IReadOnlyCollection<string> expandedProjectIds,
        DateTimeOffset nowUtc,
        NavigatorProjectSortMode sortMode = NavigatorProjectSortMode.Name)
    {
        var rows = new Dictionary<string, SidebarNodeViewModel>(StringComparer.OrdinalIgnoreCase);
        return SidebarTreeProjectionBuilder.Build(
            projects,
            threads,
            @"C:\global",
            expandedProjectIds,
            new NavigatorSettings
            {
                SortMode = sortMode,
                RecentThreadsPerProject = 3,
            },
            static _ => default,
            static (_, _) => false,
            (nodeId, kind, selectionTarget) => GetOrCreateRow(rows, nodeId, kind, selectionTarget),
            nowUtc);
    }

    private static void AssertSidebarIconStyle(TreeNode node, byte expectedBasic16Index)
    {
        Assert.IsNotNull(node.IconStyle);
        Assert.IsTrue(node.IconStyle.Value.TryGetForeground(out var foreground));
        Assert.AreEqual(ColorKind.Basic16, foreground.Kind);
        Assert.AreEqual(expectedBasic16Index, foreground.Index);
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
            ProjectRef = projectId,
            WorkingDirectory = workingDirectory,
            Title = title,
            Status = WorkThreadStatus.Active,
            CreatedAt = lastActiveAt.AddMinutes(-2),
            UpdatedAt = lastActiveAt,
            LastActiveAt = lastActiveAt,
        };
    }

    private static void TickTerminalApp(TerminalApp app)
    {
        var tickMethod = typeof(TerminalApp).GetMethod("Tick", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(tickMethod);
        tickMethod.Invoke(app, [null]);
    }

    private static void InvokeTerminalApp(TerminalApp app, string methodName)
    {
        var method = typeof(TerminalApp).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(method);
        method.Invoke(app, null);
    }

    private sealed class CapturingSidebarRowCommandDispatcher : ISidebarRowCommandDispatcher
    {
        public SidebarRowCommand? LastCommand { get; private set; }

        public void Dispatch(SidebarRowCommand command)
        {
            LastCommand = command;
        }
    }
}
