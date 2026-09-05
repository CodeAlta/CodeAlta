using CodeAlta.Agent;
using CodeAlta.Tui.App;
using CodeAlta.Catalog;
using CodeAlta.Tui.Presentation.Timeline;
using CodeAlta.Tui.Threading;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;

namespace CodeAlta.Tests;

[TestClass]
public sealed class SessionTimelineMarkdownRootTests
{
    [TestMethod]
    public void CreatePendingChatMessage_AssignsLocalFileRootPathToUserAndAssistantMarkdown()
    {
        const string projectRoot = @"C:\code\CodeAlta";

        var pending = ChatTimelineVisualFactory.CreatePendingChatMessage("Open [readme](readme.md).", projectRoot);

        Assert.AreEqual(projectRoot, pending.StreamingMarkdown.Options.LocalFileRootPath);
        Assert.AreEqual(projectRoot, GetMarkdownControl(pending.UserItem).Options.LocalFileRootPath);
    }

    [TestMethod]
    public void SessionTimelinePresenter_SetLocalFileRootPath_UpdatesExistingAndFutureMarkdownControls()
    {
        const string initialProjectRoot = @"C:\code\CodeAlta";
        const string updatedProjectRoot = @"D:\code\CodeAlta";
        var timeline = new SessionTimelinePresenter(new InlineUiDispatcher(), static () => null, initialProjectRoot);

        timeline.FinalizeContent(new AgentContentCompletedEvent(
            ModelProviderIds.Codex,
            "session-1",
            DateTimeOffset.UtcNow,
            null,
            AgentContentKind.User,
            "user-1",
            null,
            "Open [readme](readme.md)."));

        var firstMarkdown = GetMarkdownControl(timeline.Flow.Items[0]);
        Assert.AreEqual(initialProjectRoot, firstMarkdown.Options.LocalFileRootPath);

        timeline.SetLocalFileRootPath(updatedProjectRoot);

        Assert.AreEqual(updatedProjectRoot, firstMarkdown.Options.LocalFileRootPath);

        timeline.FinalizeContent(new AgentContentCompletedEvent(
            ModelProviderIds.Codex,
            "session-1",
            DateTimeOffset.UtcNow,
            null,
            AgentContentKind.Assistant,
            "assistant-1",
            null,
            "Open [guide](doc/development-guide.md)."));

        var secondMarkdown = GetMarkdownControl(timeline.Flow.Items[^1]);
        Assert.AreEqual(updatedProjectRoot, secondMarkdown.Options.LocalFileRootPath);
    }

    [TestMethod]
    public void OpenSessionStateStore_RefreshesTimelineLocalFileRootPathFromProjectMapping()
    {
        var project = new ProjectDescriptor
        {
            Id = "project-1",
            DisplayName = "CodeAlta",
            ProjectPath = @"C:\code\CodeAlta",
        };
        var registry = new OpenSessionStateStore(new SessionStateFactory(
            new InlineUiDispatcher(),
            new SessionTimelineSurface(static () => null),
            new SessionPromptDraftService(static _ => null, static _ => { }),
            new SessionModelProviderPreferenceService(static _ => { }, static (_, _, _, _) => { }),
            new SessionProjectRootResolver(
                () => project,
                projectId => string.Equals(projectId, project.Id, StringComparison.OrdinalIgnoreCase) ? project : null)));
        var session = CreateSession();

        var firstTab = registry.EnsureSessionTab(session);

        Assert.AreEqual(project.ProjectPath, firstTab.Timeline.LocalFileRootPath);

        project.ProjectPath = @"D:\code\CodeAlta";

        var refreshedTab = registry.EnsureSessionTab(session);

        Assert.AreSame(firstTab, refreshedTab);
        Assert.AreEqual(project.ProjectPath, refreshedTab.Timeline.LocalFileRootPath);
    }

    private static MarkdownControl GetMarkdownControl(DocumentFlowItem item)
    {
        Assert.IsInstanceOfType<FlowDocument>(item.Content);
        var document = (FlowDocument)item.Content;
        Assert.AreEqual(1, document.BlockCount);
        Assert.IsInstanceOfType<VisualDocumentFlowBlock>(document.GetBlock(0));
        var group = Assert.IsInstanceOfType<Group>(((VisualDocumentFlowBlock)document.GetBlock(0)).CreateVisual());
        return Assert.IsInstanceOfType<MarkdownControl>(group.Content);
    }

    private static SessionViewDescriptor CreateSession()
    {
        return new SessionViewDescriptor
        {
            SessionId = "session-1",
            Kind = SessionViewKind.ProjectSession,
            ProviderId = ModelProviderIds.Codex.Value,
            ProjectRef = "project-1",
            WorkingDirectory = @"C:\code\CodeAlta",
            Title = "Review startup",
            Status = SessionViewStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            LastActiveAt = DateTimeOffset.UtcNow,
        };
    }

    private sealed class InlineUiDispatcher : IUiDispatcher
    {
        public bool CheckAccess()
            => true;

        public void Post(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            action();
        }

        public Task InvokeAsync(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            action();
            return Task.CompletedTask;
        }

        public Task<T> InvokeAsync<T>(Func<T> action)
        {
            ArgumentNullException.ThrowIfNull(action);
            return Task.FromResult(action());
        }
    }
}
