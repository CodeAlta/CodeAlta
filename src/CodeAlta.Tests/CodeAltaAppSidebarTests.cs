using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.LiveTool;
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
    public void Build_CreatesProjectSessionChildrenForMatchingProject()
    {
        var timestamp = DateTimeOffset.Parse("2026-03-29T10:00:00+00:00");
        var project = CreateProject("project-1", "CodeAlta", @"C:\repo");
        var otherProject = CreateProject("project-2", "Other", @"C:\other");
        var visibleSession = CreateSession(
            sessionId: "session-1",
            title: "Recovered session",
            kind: SessionViewKind.ProjectSession,
            projectId: project.Id,
            ProviderId: ModelProviderIds.Codex.Value,
            workingDirectory: project.ProjectPath,
            lastActiveAt: timestamp.AddMinutes(2));
        var internalSession = CreateSession(
            sessionId: "session-2",
            title: "Internal helper",
            kind: SessionViewKind.InternalSession,
            projectId: project.Id,
            ProviderId: ModelProviderIds.Codex.Value,
            workingDirectory: project.ProjectPath,
            lastActiveAt: timestamp.AddMinutes(1));
        var unrelatedSession = CreateSession(
            sessionId: "session-3",
            title: "Other session",
            kind: SessionViewKind.ProjectSession,
            projectId: otherProject.Id,
            ProviderId: ModelProviderIds.Codex.Value,
            workingDirectory: otherProject.ProjectPath,
            lastActiveAt: timestamp);

        var projection = BuildProjection(
            [project, otherProject],
            [unrelatedSession, internalSession, visibleSession],
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
                SidebarSelectionTarget.Session(visibleSession.SessionId),
                SidebarSelectionTarget.Session(internalSession.SessionId),
            },
            projectNode.Children
                .Select(node => node.SelectionTarget!.Value)
                .ToArray());

        Assert.IsFalse(projectNode.Children.Any(node => node.SelectionTarget == SidebarSelectionTarget.Session(unrelatedSession.SessionId)));
    }

    [TestMethod]
    public void Build_NestsSameProjectChildSessionsUnderParent()
    {
        var timestamp = DateTimeOffset.Parse("2026-03-29T10:00:00+00:00");
        var project = CreateProject("project-1", "CodeAlta", @"C:\repo");
        var parent = CreateSession("session-parent", "Parent", SessionViewKind.ProjectSession, project.Id, ModelProviderIds.Codex.Value, project.ProjectPath, timestamp);
        var child = CreateSession("session-child", "Child", SessionViewKind.ProjectSession, project.Id, ModelProviderIds.Codex.Value, project.ProjectPath, timestamp.AddMinutes(1));
        var crossProjectChild = CreateSession("session-cross-project", "Cross project", SessionViewKind.ProjectSession, "project-2", ModelProviderIds.Codex.Value, @"C:\other", timestamp.AddMinutes(2));
        child.ParentSessionId = parent.SessionId;
        crossProjectChild.ParentSessionId = parent.SessionId;

        var projection = BuildProjection(
            [project, CreateProject("project-2", "Other", @"C:\other")],
            [parent, child, crossProjectChild],
            [project.Id, "project-2"],
            nowUtc: timestamp.AddMinutes(3));

        var projectNode = projection.Roots[1].Children.Single(node => node.SelectionTarget == SidebarSelectionTarget.Project(project.Id));
        Assert.AreEqual(1, projectNode.Children.Count);
        var parentNode = projectNode.Children[0];
        Assert.AreEqual(SidebarSelectionTarget.Session(parent.SessionId), parentNode.SelectionTarget);
        Assert.IsTrue(parentNode.IsExpanded);
        Assert.AreEqual(1, parentNode.Children.Count);
        Assert.AreEqual(SidebarSelectionTarget.Session(child.SessionId), parentNode.Children[0].SelectionTarget);

        var otherProjectNode = projection.Roots[1].Children.Single(node => node.SelectionTarget == SidebarSelectionTarget.Project("project-2"));
        Assert.AreEqual(SidebarSelectionTarget.Session(crossProjectChild.SessionId), otherProjectNode.Children[0].SelectionTarget);
        StringAssert.Contains(otherProjectNode.Children[0].Row.StateIconMarkup, NerdFont.MdAlertCircleOutline.ToString());
        StringAssert.Contains(otherProjectNode.Children[0].Row.StateTooltip, "another scope");
    }

    [TestMethod]
    public void Build_LineageAnomaliesRenderAtProjectRootWithDiagnosticMarkers()
    {
        var timestamp = DateTimeOffset.Parse("2026-03-29T10:00:00+00:00");
        var project = CreateProject("project-1", "CodeAlta", @"C:\repo");
        var missingParent = CreateSession("session-missing", "Missing parent", SessionViewKind.ProjectSession, project.Id, ModelProviderIds.Codex.Value, project.ProjectPath, timestamp.AddMinutes(1));
        var cycleParent = CreateSession("session-cycle-parent", "Cycle parent", SessionViewKind.ProjectSession, project.Id, ModelProviderIds.Codex.Value, project.ProjectPath, timestamp.AddMinutes(2));
        var cycleChild = CreateSession("session-cycle-child", "Cycle child", SessionViewKind.ProjectSession, project.Id, ModelProviderIds.Codex.Value, project.ProjectPath, timestamp.AddMinutes(3));
        missingParent.ParentSessionId = "session-no-longer-present";
        cycleParent.ParentSessionId = cycleChild.SessionId;
        cycleChild.ParentSessionId = cycleParent.SessionId;
        var rows = new Dictionary<string, SidebarNodeViewModel>(StringComparer.OrdinalIgnoreCase);

        var projection = SidebarTreeProjectionBuilder.Build(
            [project],
            [missingParent, cycleParent, cycleChild],
            @"C:\global",
            [project.Id],
            new NavigatorSettings { RecentSessionsPerProject = 10 },
            static _ => default,
            static (_, _) => false,
            (nodeId, kind, selectionTarget) => GetOrCreateRow(rows, nodeId, kind, selectionTarget),
            timestamp.AddMinutes(4));

        var projectNode = projection.Roots[1].Children.Single(node => node.SelectionTarget == SidebarSelectionTarget.Project(project.Id));
        CollectionAssert.AreEquivalent(
            new[] { missingParent.SessionId, cycleParent.SessionId, cycleChild.SessionId },
            projectNode.Children.Select(static node => node.SelectionTarget!.Value.SessionId).ToArray());
        Assert.IsTrue(projectNode.Children.All(static node => node.Children.Count == 0));
        StringAssert.Contains(rows[$"session:{missingParent.SessionId}"].StateIconMarkup, NerdFont.MdAlertCircleOutline.ToString());
        StringAssert.Contains(rows[$"session:{missingParent.SessionId}"].StateTooltip, "missing");
        StringAssert.Contains(rows[$"session:{cycleParent.SessionId}"].StateIconMarkup, NerdFont.MdAlertCircleOutline.ToString());
        StringAssert.Contains(rows[$"session:{cycleParent.SessionId}"].StateTooltip, "cycle");
        StringAssert.Contains(rows[$"session:{cycleChild.SessionId}"].StateIconMarkup, NerdFont.MdAlertCircleOutline.ToString());
        StringAssert.Contains(rows[$"session:{cycleChild.SessionId}"].StateTooltip, "cycle");
    }

    [TestMethod]
    public void Build_FiltersArchivedProjectsAndSessions()
    {
        var project = CreateProject("project-1", "CodeAlta", @"C:\repo");
        var archivedProject = CreateProject("project-2", "Archived", @"C:\archive");
        archivedProject.Archived = true;
        var visibleSession = CreateSession("session-1", "Visible", SessionViewKind.ProjectSession, project.Id, ModelProviderIds.Codex.Value, project.ProjectPath, DateTimeOffset.Parse("2026-03-29T12:00:00+00:00"));
        var archivedSession = CreateSession("session-2", "Archived", SessionViewKind.ProjectSession, project.Id, ModelProviderIds.Codex.Value, project.ProjectPath, DateTimeOffset.Parse("2026-03-29T12:01:00+00:00"));
        archivedSession.Status = SessionViewStatus.Archived;

        var projection = BuildProjection(
            [project, archivedProject],
            [visibleSession, archivedSession],
            [project.Id],
            nowUtc: DateTimeOffset.Parse("2026-03-29T12:02:00+00:00"));

        Assert.AreEqual(1, projection.Roots[1].Children.Count);
        Assert.AreEqual(project.Id, projection.Roots[1].Children[0].SelectionTarget?.ProjectId);
        Assert.AreEqual(1, projection.Roots[1].Children[0].Children.Count);
        Assert.AreEqual(visibleSession.SessionId, projection.Roots[1].Children[0].Children[0].SelectionTarget?.SessionId);
    }

    [TestMethod]
    public void Build_GlobalNodeIncludesOpenSessionsAction()
    {
        var globalSession = CreateSession(
            "global-1",
            "Global session",
            SessionViewKind.GlobalSession,
            projectId: null,
            ModelProviderIds.Codex.Value,
            @"C:\global",
            DateTimeOffset.Parse("2026-03-29T12:00:00+00:00"));

        var projection = BuildProjection(
            projects: [],
            sessions: [globalSession],
            expandedProjectIds: [],
            nowUtc: DateTimeOffset.Parse("2026-03-29T12:02:00+00:00"));

        var globalNode = projection.Roots[0];
        Assert.AreEqual(SidebarSelectionTarget.Global(), globalNode.SelectionTarget);
        Assert.AreEqual(1, globalNode.Actions.Count);
        Assert.AreEqual(SidebarRowActionKind.OpenProjectSessions, globalNode.Actions[0].Kind);
    }

    [TestMethod]
    public void Build_ProjectsRootIncludesAlwaysVisibleOpenFolderAction()
    {
        var projection = BuildProjection(
            projects: [],
            sessions: [],
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
        var sessions = new[]
        {
            CreateSession("session-1", "Older", SessionViewKind.ProjectSession, olderProject.Id, ModelProviderIds.Codex.Value, olderProject.ProjectPath, timestamp),
            CreateSession("session-2", "Newer", SessionViewKind.ProjectSession, newerProject.Id, ModelProviderIds.Codex.Value, newerProject.ProjectPath, timestamp.AddDays(1)),
        };

        var projection = BuildProjection(
            [olderProject, newerProject],
            sessions,
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
        var session = CreateSession("session-1", "Recovered session", SessionViewKind.ProjectSession, project.Id, ModelProviderIds.Codex.Value, project.ProjectPath, timestamp);
        var rows = new Dictionary<string, SidebarNodeViewModel>(StringComparer.OrdinalIgnoreCase);

        var first = SidebarTreeProjectionBuilder.Build(
            [project],
            [session],
            @"C:\global",
            [project.Id],
            new NavigatorSettings(),
            static _ => default,
            static (_, _) => false,
            (nodeId, kind, selectionTarget) => GetOrCreateRow(rows, nodeId, kind, selectionTarget),
            timestamp.AddSeconds(30));
        var second = SidebarTreeProjectionBuilder.Build(
            [project],
            [session],
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
    public void ResolveTargetForProjectionChange_PrefersCurrentVisibleSessionSelection()
    {
        var project = CreateProject("project-1", "CodeAlta", @"C:\repo");
        var visibleSession = CreateSession(
            sessionId: "session-1",
            title: "Recovered session",
            kind: SessionViewKind.ProjectSession,
            projectId: project.Id,
            ProviderId: ModelProviderIds.Codex.Value,
            workingDirectory: project.ProjectPath,
            lastActiveAt: DateTimeOffset.Parse("2026-03-29T10:00:00+00:00"));
        var projection = BuildProjection(
            [project],
            [visibleSession],
            [project.Id],
            nowUtc: DateTimeOffset.Parse("2026-03-29T10:05:00+00:00"));
        var currentTarget = SidebarSelectionResolver.ResolveCurrentTarget(
            visibleSession.SessionId,
            project.Id,
            globalScopeSelected: false);

        var selectedTarget = SidebarSelectionResolver.ResolveTargetForProjectionChange(
            SidebarSelectionTarget.Project(project.Id),
            projection,
            currentTarget);

        Assert.AreEqual(SidebarSelectionTarget.Session(visibleSession.SessionId), selectedTarget);
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
            SidebarSelectionTarget.Session("missing-session"));

        Assert.AreEqual(SidebarSelectionTarget.Global(), selectedTarget);
    }

    [TestMethod]
    public void SidebarView_ApplyProjectionBuildsSelectableProjectNode()
    {
        var project = CreateProject("project-1", "CodeAlta", @"C:\repo");
        var session = CreateSession("session-1", "Recovered session", SessionViewKind.ProjectSession, project.Id, ModelProviderIds.Codex.Value, project.ProjectPath, DateTimeOffset.Parse("2026-03-29T10:04:00+00:00"));
        var projection = BuildProjection(
            [project],
            [session],
            [project.Id],
            nowUtc: DateTimeOffset.Parse("2026-03-29T10:05:00+00:00"));
        var view = new SidebarView(new SidebarViewModel(), static () => { }, static () => { }, static () => { }, static () => { }, static _ => { }, static _ => { }, new CapturingSidebarRowCommandDispatcher(), static _ => { });

        view.ApplyProjection(projection);

        Assert.AreEqual(2, view.Tree.Roots.Count);
        var projectsRoot = view.Tree.Roots[1];
        var projectNode = view.Tree.Roots[1].Children[0];
        var sessionNode = projectNode.Children[0];
        AssertSidebarIconStyle(projectsRoot, expectedBasic16Index: 12);
        AssertSidebarIconStyle(projectNode, expectedBasic16Index: 12);
        AssertSidebarIconStyle(sessionNode, expectedBasic16Index: 10);
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
        Assert.AreEqual(2, sessionNode.RightVisuals.Count);
        Assert.AreEqual(TreeNodeRightVisualVisibility.Always, sessionNode.RightVisuals[0].Visibility);
        Assert.AreEqual(TreeNodeRightVisualVisibility.Hover, sessionNode.RightVisuals[1].Visibility);
    }

    [TestMethod]
    public void SidebarView_ApplyProjectionBuildsSelectableGlobalNodeWithOpenSessionsAction()
    {
        var globalSession = CreateSession("global-1", "Recovered global session", SessionViewKind.GlobalSession, null, ModelProviderIds.Codex.Value, @"C:\global", DateTimeOffset.Parse("2026-03-29T10:04:00+00:00"));
        var projection = BuildProjection(
            projects: [],
            sessions: [globalSession],
            expandedProjectIds: [],
            nowUtc: DateTimeOffset.Parse("2026-03-29T10:05:00+00:00"));
        var view = new SidebarView(new SidebarViewModel(), static () => { }, static () => { }, static () => { }, static () => { }, static _ => { }, static _ => { }, new CapturingSidebarRowCommandDispatcher(), static _ => { });

        view.ApplyProjection(projection);

        Assert.AreEqual(2, view.Tree.Roots.Count);
        var globalNode = view.Tree.Roots[0];
        var globalSessionNode = globalNode.Children[0];
        AssertSidebarIconStyle(globalNode, expectedBasic16Index: 3);
        AssertSidebarIconStyle(globalSessionNode, expectedBasic16Index: 3);
        Assert.AreEqual(SidebarSelectionTarget.Global(), globalNode.Data);
        Assert.AreEqual(2, globalNode.RightVisuals.Count);
        Assert.AreEqual(TreeNodeRightVisualVisibility.Always, globalNode.RightVisuals[0].Visibility);
        Assert.AreEqual(TreeNodeRightVisualVisibility.Hover, globalNode.RightVisuals[1].Visibility);
    }

    [TestMethod]
    public void SidebarView_SetCollapsedShowsOnlyNavigatorIconAndExpandButton()
    {
        var view = new SidebarView(new SidebarViewModel(), static () => { }, static () => { }, static () => { }, static () => { }, static _ => { }, static _ => { }, new CapturingSidebarRowCommandDispatcher(), static _ => { });
        var splitter = Assert.IsInstanceOfType<VSplitter>(view.Root);
        var group = Assert.IsInstanceOfType<Group>(view.NavigatorRoot);
        var notesGroup = Assert.IsInstanceOfType<Group>(view.NotesRoot);
        var title = Assert.IsInstanceOfType<Markup>(group.TopLeftText);
        var toggleHost = Assert.IsInstanceOfType<TooltipHost>(group.TopRightText);
        var toggleButton = Assert.IsInstanceOfType<Button>(toggleHost.Content);
        var toggleIcon = Assert.IsInstanceOfType<TextBlock>(toggleButton.Content);
        bool? observedCollapsed = null;
        view.CollapsedChanged += isCollapsed => observedCollapsed = isCollapsed;

        Assert.AreSame(group, splitter.First);
        Assert.AreSame(notesGroup, splitter.Second);
        Assert.AreEqual(0.75, splitter.Ratio);
        Assert.IsNotNull(group.Content);
        StringAssert.Contains(title.Text ?? string.Empty, "Navigator");
        Assert.AreEqual("\u25E8", toggleIcon.Text);

        view.SetCollapsed(true);

        Assert.IsTrue(view.IsCollapsed);
        Assert.AreEqual(true, observedCollapsed);
        Assert.IsNull(group.Content);
        Assert.IsNull(splitter.Second);
        Assert.IsFalse(title.Text?.Contains("Navigator", StringComparison.Ordinal) ?? true);
        Assert.AreEqual("\u25E7", toggleIcon.Text);
    }

    [TestMethod]
    public void SidebarView_NotesGroupUsesMarkdownControlAndUpdatesMarkdown()
    {
        var notesService = new AltaNotesService(static () => "session-notes");
        var caller = new AltaCallerIdentity { Kind = "host", SourceSessionId = "session-notes" };
        notesService.SetMarkdownAsync("# Initial", caller).GetAwaiter().GetResult();
        var view = new SidebarView(new SidebarViewModel(), static () => { }, static () => { }, static () => { }, static () => { }, static _ => { }, static _ => { }, new CapturingSidebarRowCommandDispatcher(), static _ => { }, notesService: notesService);
        var notesGroup = Assert.IsInstanceOfType<Group>(view.NotesRoot);
        var scrollViewer = Assert.IsInstanceOfType<ScrollViewer>(notesGroup.Content);
        var markdown = Assert.IsInstanceOfType<MarkdownControl>(scrollViewer.Content);

        Assert.AreEqual("# Initial", markdown.Markdown);
        Assert.IsFalse(scrollViewer.HorizontalScrollEnabled);
        Assert.IsTrue(markdown.Options.WrapCodeBlocks);

        view.SetNotesMarkdown("- [x] Done");

        Assert.AreEqual("- [x] Done", markdown.Markdown);
    }

    [TestMethod]
    public async Task SidebarView_ClearNotesButtonDoesNotBlockOnAsyncClear()
    {
        var notesService = new BlockingNotesService();
        var view = new SidebarView(new SidebarViewModel(), static () => { }, static () => { }, static () => { }, static () => { }, static _ => { }, static _ => { }, new CapturingSidebarRowCommandDispatcher(), static _ => { }, notesService: notesService);
        var notesGroup = Assert.IsInstanceOfType<Group>(view.NotesRoot);
        var clearButtonHost = Assert.IsInstanceOfType<TooltipHost>(notesGroup.BottomRightText);
        var clearButton = Assert.IsInstanceOfType<Button>(clearButtonHost.Content);
        var clickTask = Task.Run(() => RaiseButtonClick(clearButton));

        try
        {
            var started = await Task.WhenAny(notesService.ClearStarted.Task, Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(false);
            Assert.AreSame(notesService.ClearStarted.Task, started, "The clear button did not invoke the notes service.");

            var completedBeforeClearFinished = await Task.WhenAny(clickTask, Task.Delay(TimeSpan.FromMilliseconds(100))).ConfigureAwait(false);

            Assert.AreSame(clickTask, completedBeforeClearFinished, "The clear button must not synchronously wait for notes persistence on the UI thread.");
        }
        finally
        {
            notesService.ClearRelease.TrySetResult();
            await clickTask.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            await notesService.ClearCompleted.Task.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
    }

    [TestMethod]
    public void CodeAltaShellView_SetSidebarCollapsedShrinksAndRestoresSplitter()
    {
        var view = new CodeAltaShellView(
            new TextBlock("sidebar"),
            new TextBlock("workspace"),
            new TextBlock("command"),
            static _ => { });
        view.SidebarSplitter.Ratio = 0.4d;

        view.SetSidebarCollapsed(true);

        Assert.AreEqual(5, view.SidebarSplitter.MinFirst);
        Assert.AreEqual(0.0d, view.SidebarSplitter.Ratio);

        view.SetSidebarCollapsed(false);

        Assert.AreEqual(24, view.SidebarSplitter.MinFirst);
        Assert.AreEqual(0.4d, view.SidebarSplitter.Ratio);
    }

    [TestMethod]
    public void Build_RunningSessionsPromoteSpinnerStateToSessionAndProjectRows()
    {
        var timestamp = DateTimeOffset.Parse("2026-03-29T10:00:00+00:00");
        var project = CreateProject("project-1", "CodeAlta", @"C:\repo");
        var session = CreateSession("session-1", "Recovered session", SessionViewKind.ProjectSession, project.Id, ModelProviderIds.Codex.Value, project.ProjectPath, timestamp);
        var rows = new Dictionary<string, SidebarNodeViewModel>(StringComparer.OrdinalIgnoreCase);

        SidebarTreeProjectionBuilder.Build(
            [project],
            [session],
            @"C:\global",
            [project.Id],
            new NavigatorSettings(),
            static _ => new SessionVisualState(IsRunning: true, HasPromptDraft: false, HasActiveReminder: false),
            static (_, _) => false,
            (nodeId, kind, selectionTarget) => GetOrCreateRow(rows, nodeId, kind, selectionTarget),
            timestamp.AddMinutes(1));

        Assert.IsTrue(rows[$"project:{project.Id}"].ShowStateSpinner);
        Assert.IsTrue(rows[$"session:{session.SessionId}"].ShowStateSpinner);
        Assert.IsNull(rows[$"session:{session.SessionId}"].StateIconMarkup);
    }

    [TestMethod]
    public void Build_EditedPromptSessionsExposeDraftIconState()
    {
        var timestamp = DateTimeOffset.Parse("2026-03-29T10:00:00+00:00");
        var project = CreateProject("project-1", "CodeAlta", @"C:\repo");
        var session = CreateSession("session-1", "Recovered session", SessionViewKind.ProjectSession, project.Id, ModelProviderIds.Codex.Value, project.ProjectPath, timestamp);
        var rows = new Dictionary<string, SidebarNodeViewModel>(StringComparer.OrdinalIgnoreCase);

        SidebarTreeProjectionBuilder.Build(
            [project],
            [session],
            @"C:\global",
            [project.Id],
            new NavigatorSettings(),
            static _ => new SessionVisualState(IsRunning: false, HasPromptDraft: true, HasActiveReminder: false),
            static (_, _) => false,
            (nodeId, kind, selectionTarget) => GetOrCreateRow(rows, nodeId, kind, selectionTarget),
            timestamp.AddMinutes(1));

        Assert.IsFalse(rows[$"session:{session.SessionId}"].ShowStateSpinner);
        StringAssert.Contains(rows[$"session:{session.SessionId}"].StateIconMarkup, NerdFont.MdSquareEditOutline.ToString());
    }

    [TestMethod]
    public void Build_ActiveReminderSessionsExposeReminderIconState()
    {
        var timestamp = DateTimeOffset.Parse("2026-03-29T10:00:00+00:00");
        var project = CreateProject("project-1", "CodeAlta", @"C:\repo");
        var session = CreateSession("session-1", "Recovered session", SessionViewKind.ProjectSession, project.Id, ModelProviderIds.Codex.Value, project.ProjectPath, timestamp);
        var rows = new Dictionary<string, SidebarNodeViewModel>(StringComparer.OrdinalIgnoreCase);

        SidebarTreeProjectionBuilder.Build(
            [project],
            [session],
            @"C:\global",
            [project.Id],
            new NavigatorSettings(),
            static _ => new SessionVisualState(IsRunning: false, HasPromptDraft: false, HasActiveReminder: true),
            static (_, _) => false,
            (nodeId, kind, selectionTarget) => GetOrCreateRow(rows, nodeId, kind, selectionTarget),
            timestamp.AddMinutes(1));

        Assert.IsFalse(rows[$"session:{session.SessionId}"].ShowStateSpinner);
        StringAssert.Contains(rows[$"session:{session.SessionId}"].StateIconMarkup, NerdFont.MdTimerOutline.ToString());
        StringAssert.Contains(rows[$"project:{project.Id}"].StateIconMarkup, NerdFont.MdTimerOutline.ToString());
        StringAssert.Contains(rows[$"project:{project.Id}"].StateTooltip, "reminder active");
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
    public void SidebarNodeHeaderView_StateIndicatorCanAddIconWhileSpinnerIsActive()
    {
        var row = new SidebarNodeViewModel("session:session-1", SidebarNodeKind.Session, SidebarSelectionTarget.Session("session-1"));
        row.UpdateTitle("Recovered session");
        row.UpdateStateIndicator(iconMarkup: null, showSpinner: true);
        var view = new SidebarNodeHeaderView(row, static _ => { }, static _ => { });

        using var terminalSession = Terminal.Open(new InMemoryTerminalBackend(new TerminalSize(80, 20)), new TerminalOptions { ImplicitStartInput = true }, force: true);
        var app = new TerminalApp(
            view,
            terminalSession.Instance,
            new TerminalAppOptions
            {
                HostKind = TerminalHostKind.Fullscreen,
            });

        InvokeTerminalApp(app, "BeginRun");
        try
        {
            TickTerminalApp(app);
            row.UpdateStateIndicator(NerdFont.MdTimerOutline.ToString(), showSpinner: true, "reminder active");

            TickTerminalApp(app);
        }
        finally
        {
            InvokeTerminalApp(app, "EndRun");
        }
    }

    [TestMethod]
    public void SidebarView_KeyNavigationNotifiesSelectedTargetChanged()
    {
        var project = CreateProject("project-1", "CodeAlta", @"C:\repo");
        var session = CreateSession("session-1", "Recovered session", SessionViewKind.ProjectSession, project.Id, ModelProviderIds.Codex.Value, project.ProjectPath, DateTimeOffset.Parse("2026-03-29T10:04:00+00:00"));
        var projection = BuildProjection(
            [project],
            [session],
            [project.Id],
            nowUtc: DateTimeOffset.Parse("2026-03-29T10:05:00+00:00"));
        var observedTargets = new List<SidebarSelectionTarget?>();
        var view = new SidebarView(new SidebarViewModel(), static () => { }, static () => { }, static () => { }, static () => { }, static _ => { }, static _ => { }, new CapturingSidebarRowCommandDispatcher(), target => observedTargets.Add(target));

        view.ApplyProjection(projection);

        using var terminalSession = Terminal.Open(new InMemoryTerminalBackend(new TerminalSize(80, 20)), new TerminalOptions { ImplicitStartInput = true }, force: true);
        var app = new TerminalApp(
            view.Root,
            terminalSession.Instance,
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

            var backend = (InMemoryTerminalBackend)terminalSession.Instance.Backend;
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
        IReadOnlyList<SessionViewDescriptor> sessions,
        IReadOnlyCollection<string> expandedProjectIds,
        DateTimeOffset nowUtc,
        NavigatorProjectSortMode sortMode = NavigatorProjectSortMode.Name)
    {
        var rows = new Dictionary<string, SidebarNodeViewModel>(StringComparer.OrdinalIgnoreCase);
        return SidebarTreeProjectionBuilder.Build(
            projects,
            sessions,
            @"C:\global",
            expandedProjectIds,
            new NavigatorSettings
            {
                SortMode = sortMode,
                RecentSessionsPerProject = 3,
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

    private static SessionViewDescriptor CreateSession(
        string sessionId,
        string title,
        SessionViewKind kind,
        string? projectId,
        string ProviderId,
        string workingDirectory,
        DateTimeOffset lastActiveAt)
    {
        return new SessionViewDescriptor
        {
            SessionId = sessionId,
            Kind = kind,
            ProviderId = ProviderId,
            ProjectRef = projectId,
            WorkingDirectory = workingDirectory,
            Title = title,
            Status = SessionViewStatus.Active,
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

    private static void RaiseButtonClick(Button button)
    {
        var raiseEventMethod = typeof(Visual).GetMethod("RaiseEvent", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(raiseEventMethod);
        raiseEventMethod.MakeGenericMethod(typeof(ClickEventArgs)).Invoke(button, [Button.ClickEvent, new ClickEventArgs()]);
    }

    private sealed class CapturingSidebarRowCommandDispatcher : ISidebarRowCommandDispatcher
    {
        public SidebarRowCommand? LastCommand { get; private set; }

        public void Dispatch(SidebarRowCommand command)
        {
            LastCommand = command;
        }
    }

    private sealed class BlockingNotesService : IAltaNotesService
    {
        public TaskCompletionSource ClearStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ClearRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ClearCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event EventHandler<AltaNotesChangedEventArgs>? Changed;

        public string GetMarkdown(AltaCallerIdentity caller) => "# Initial";

        public ValueTask SetMarkdownAsync(string markdown, AltaCallerIdentity caller, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public async ValueTask ClearAsync(AltaCallerIdentity caller, CancellationToken cancellationToken = default)
        {
            ClearStarted.TrySetResult();
            await ClearRelease.Task.ConfigureAwait(false);
            Changed?.Invoke(this, new AltaNotesChangedEventArgs("session-notes", string.Empty, caller));
            ClearCompleted.TrySetResult();
        }
    }
}
