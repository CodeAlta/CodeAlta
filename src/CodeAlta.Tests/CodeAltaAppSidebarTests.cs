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
        var project = CreateProject("project-1", "CodeAlta", @"C:\repo");
        var otherProject = CreateProject("project-2", "Other", @"C:\other");
        var visibleThread = CreateThread(
            threadId: "thread-1",
            title: "Recovered thread",
            kind: WorkThreadKind.ProjectThread,
            projectId: project.Id,
            backendId: AgentBackendIds.Codex.Value,
            workingDirectory: project.ProjectPath);
        var internalThread = CreateThread(
            threadId: "thread-2",
            title: "Internal helper",
            kind: WorkThreadKind.InternalThread,
            projectId: project.Id,
            backendId: AgentBackendIds.Codex.Value,
            workingDirectory: project.ProjectPath);
        var unrelatedThread = CreateThread(
            threadId: "thread-3",
            title: "Other thread",
            kind: WorkThreadKind.ProjectThread,
            projectId: otherProject.Id,
            backendId: AgentBackendIds.Codex.Value,
            workingDirectory: otherProject.ProjectPath);

        var projection = SidebarTreeProjectionBuilder.Build(
            [project, otherProject],
            [unrelatedThread, internalThread, visibleThread],
            @"C:\global",
            project.Id,
            maxRecentThreadsPerProject: 3);

        Assert.AreEqual(2, projection.Roots.Count);
        var projectsRoot = projection.Roots[1];
        Assert.AreEqual("Projects", projectsRoot.Title);
        Assert.AreEqual(2, projectsRoot.Children.Count);

        var projectNode = projectsRoot.Children[0];
        Assert.AreEqual(project.DisplayName, projectNode.Title);
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
    public void ResolveTargetForProjectionChange_PreservesExplicitProjectSelectionWhenCurrentThreadIsVisible()
    {
        var project = CreateProject("project-1", "CodeAlta", @"C:\repo");
        var visibleThread = CreateThread(
            threadId: "thread-1",
            title: "Recovered thread",
            kind: WorkThreadKind.ProjectThread,
            projectId: project.Id,
            backendId: AgentBackendIds.Codex.Value,
            workingDirectory: project.ProjectPath);
        var projection = SidebarTreeProjectionBuilder.Build(
            [project],
            [visibleThread],
            @"C:\global",
            project.Id,
            maxRecentThreadsPerProject: 3);
        var currentTarget = SidebarSelectionResolver.ResolveCurrentTarget(
            visibleThread.ThreadId,
            project.Id,
            globalScopeSelected: false);

        var selectedTarget = SidebarSelectionResolver.ResolveTargetForProjectionChange(
            SidebarSelectionTarget.Project(project.Id),
            projection,
            currentTarget);

        Assert.AreEqual(SidebarSelectionTarget.Project(project.Id), selectedTarget);
    }

    [TestMethod]
    public void SidebarView_ApplyProjectionBuildsSelectableProjectNode()
    {
        var project = CreateProject("project-1", "CodeAlta", @"C:\repo");
        var projection = SidebarTreeProjectionBuilder.Build(
            [project],
            [],
            @"C:\global",
            project.Id,
            maxRecentThreadsPerProject: 3);
        var view = new SidebarView(new SidebarViewModel(), static () => { });

        view.ApplyProjection(projection);

        Assert.AreEqual(2, view.Tree.Roots.Count);
        var projectNode = view.Tree.Roots[1].Children[0];
        Assert.AreEqual(SidebarSelectionTarget.Project(project.Id), projectNode.Data);
        Assert.IsTrue(projectNode.IsExpanded);
        Assert.IsTrue(projection.ContainsTarget(SidebarSelectionTarget.Project(project.Id)));
    }

    private static ProjectDescriptor CreateProject(string id, string displayName, string projectPath)
    {
        return new ProjectDescriptor
        {
            Id = id,
            Slug = displayName.ToLowerInvariant(),
            DisplayName = displayName,
            ProjectPath = projectPath,
        };
    }

    private static WorkThreadDescriptor CreateThread(
        string threadId,
        string title,
        WorkThreadKind kind,
        string? projectId,
        string backendId,
        string workingDirectory)
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
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            UpdatedAt = DateTimeOffset.UtcNow,
            LastActiveAt = DateTimeOffset.UtcNow,
        };
    }
}
