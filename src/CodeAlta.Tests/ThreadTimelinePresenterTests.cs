using CodeAlta.Threading;
using CodeAlta.Agent;
using CodeAlta.App;
using CodeAlta.Models;
using CodeAlta.Presentation.Prompting;
using CodeAlta.Presentation.Timeline;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;

namespace CodeAlta.Tests;

[TestClass]
public sealed class ThreadTimelinePresenterTests
{
    [TestMethod]
    public async Task CreateTruncatedHistoryState_CanBeCreatedFromWorkerThread()
    {
        var state = await Task.Run(() => ThreadTimelinePresenter.CreateTruncatedHistoryState(3, static () => { }));

        Assert.IsNotNull(state);
        Assert.IsInstanceOfType<Rule>(state.Rule);
        Assert.IsInstanceOfType<Button>(state.Rule.CenterLabel);
        Assert.AreEqual(3, state.OmittedMessageCount);
    }

    [TestMethod]
    public void BuildInitialThreadHistoryItems_PrependsTruncatedHistoryBeforeFirstLoadedPrompt()
    {
        var pending = ChatTimelineVisualFactory.CreatePendingChatMessage("hello");
        var followup = ChatTimelineVisualFactory.CreatePendingChatMessage("assistant");
        var truncatedHistory = ThreadTimelinePresenter.CreateTruncatedHistoryState(3, static () => { });

        var items = ThreadTimelinePresenter.BuildInitialThreadHistoryItems(
            [pending.UserItem, followup.AssistantItem],
            truncatedHistory.Item);

        Assert.AreEqual(3, items.Count);
        Assert.AreSame(truncatedHistory.Item.Content, items[0].Content);
        Assert.AreSame(pending.UserItem.Content, items[1].Content);
        Assert.AreSame(followup.AssistantItem.Content, items[2].Content);
    }

    [TestMethod]
    public void BuildInitialThreadHistoryItems_LeavesRenderedItemsUnchangedWhenNoMarkerExists()
    {
        var pending = ChatTimelineVisualFactory.CreatePendingChatMessage("hello");
        var followup = ChatTimelineVisualFactory.CreatePendingChatMessage("assistant");

        var items = ThreadTimelinePresenter.BuildInitialThreadHistoryItems(
            [pending.UserItem, followup.AssistantItem],
            truncatedHistoryItem: null);

        Assert.AreEqual(2, items.Count);
        Assert.AreSame(pending.UserItem.Content, items[0].Content);
        Assert.AreSame(followup.AssistantItem.Content, items[1].Content);
    }

    [TestMethod]
    public void CreateImageAttachmentDialogBottom_IncludesFullAttachmentPath()
    {
        var fullPath = @"C:\code\CodeAlta\.alta\attachments\prompt-image.png";
        var bottom = ChatTimelineVisualFactory.CreateImageAttachmentDialogBottom(
            new PromptImageAttachmentReference("Prompt image", fullPath, "image/png"),
            new Button("Close"));

        var pathText = bottom.EnumerateVisualsDepthFirst()
            .OfType<TextBlock>()
            .Single(static textBlock => textBlock.Text?.StartsWith("Path: ", StringComparison.Ordinal) == true);

        Assert.AreEqual($"Path: {fullPath}", pathText.Text);
        Assert.IsTrue(pathText.Wrap);
    }

    [TestMethod]
    public void ResolveCompletedContent_PreservesBufferedDeltaWhenCompletedPayloadIsEmpty()
    {
        var buffer = new System.Text.StringBuilder("Streaming assistant reply");

        var content = ThreadTimelinePresenter.ResolveCompletedContent(string.Empty, buffer);

        Assert.AreEqual("Streaming assistant reply", content);
    }

    [TestMethod]
    public void ResolveCompletedContent_PrefersCompletedPayloadWhenPresent()
    {
        var buffer = new System.Text.StringBuilder("Older delta text");

        var content = ThreadTimelinePresenter.ResolveCompletedContent("Final assistant reply", buffer);

        Assert.AreEqual("Final assistant reply", content);
    }

    [TestMethod]
    public void CreateTruncatedHistoryItem_TracksLoadableStateUntilReplacement()
    {
        var presenter = CreatePresenter();

        _ = presenter.CreateTruncatedHistoryItem(3, static () => { });

        Assert.IsTrue(presenter.HasLoadableTruncatedHistory);

        presenter.ReplaceTruncatedHistoryLoadButton();

        Assert.IsFalse(presenter.HasLoadableTruncatedHistory);
    }

    [TestMethod]
    public void AppendContent_CreatesTimelineItemAndPreventsEmptyAssistantCompletionFromBeingSkipped()
    {
        var presenter = CreatePresenter();
        var timestamp = DateTimeOffset.UtcNow;
        var delta = new AgentContentDeltaEvent(
            AgentBackendIds.Codex,
            "session-1",
            timestamp,
            null,
            AgentContentKind.Assistant,
            "assistant-1",
            null,
            "Streaming reply");
        var completed = new AgentContentCompletedEvent(
            AgentBackendIds.Codex,
            "session-1",
            timestamp,
            null,
            AgentContentKind.Assistant,
            "assistant-1",
            null,
            string.Empty);

        presenter.AppendContent(delta);

        Assert.AreEqual(1, presenter.Flow.Items.Count);
        Assert.IsFalse(presenter.ShouldSkipEmptyAssistantCompletion(completed));

        presenter.FinalizeContent(completed);

        Assert.AreEqual(1, presenter.Flow.Items.Count);
    }

    [TestMethod]
    public void DiscardDraftContent_RemovesMatchingDraftTimelineItem()
    {
        var presenter = CreatePresenter();
        var timestamp = DateTimeOffset.UtcNow;
        var delta = new AgentContentDeltaEvent(
            AgentBackendIds.Codex,
            "session-1",
            timestamp,
            null,
            AgentContentKind.Assistant,
            "assistant-1",
            null,
            "Partial draft",
            CreateJsonDetails("""{"attemptId":"run-1:1","draft":true}"""));
        var update = new AgentSessionUpdateEvent(
            AgentBackendIds.Codex,
            "session-1",
            timestamp.AddSeconds(1),
            null,
            AgentSessionUpdateKind.Reconnecting,
            "Reconnecting to ChatGPT/Codex... 1/5",
            Details: CreateJsonDetails("""{"provider":"codex","discardDraft":true,"draftAttemptId":"run-1:1"}"""));

        presenter.AppendContent(delta);

        Assert.AreEqual(1, presenter.Flow.Items.Count);

        presenter.DiscardDraftContent(update);

        Assert.AreEqual(0, presenter.Flow.Items.Count);
    }

    [TestMethod]
    public void OptimisticUserPrompt_PreventsDuplicateTimelineItemsWhenEchoArrives()
    {
        var presenter = CreatePresenter();
        var timestamp = DateTimeOffset.UtcNow;

        presenter.RenderOptimisticUserPrompt("First prompt", timestamp);

        var skippedDelta = presenter.TryConsumeOptimisticUserEcho(AgentContentKind.User, "user-1", timestamp.AddSeconds(1), completed: false);
        var skippedCompletion = presenter.TryConsumeOptimisticUserEcho(AgentContentKind.User, "user-1", timestamp.AddSeconds(2), completed: true);

        Assert.IsTrue(skippedDelta);
        Assert.IsTrue(skippedCompletion);
        Assert.AreEqual(1, presenter.Flow.Items.Count);
    }

    [TestMethod]
    public void RollbackOptimisticUserPrompt_RemovesPromptFromTimeline()
    {
        var presenter = CreatePresenter();

        presenter.RenderOptimisticUserPrompt("First prompt", DateTimeOffset.UtcNow);
        Assert.AreEqual(1, presenter.Flow.Items.Count);

        presenter.RollbackOptimisticUserPrompt();

        Assert.AreEqual(0, presenter.Flow.Items.Count);
    }

    [TestMethod]
    public void RollbackOptimisticUserPrompt_BeforeFailureLeavesOnlyFailureTimelineItem()
    {
        var presenter = CreatePresenter();

        presenter.RenderOptimisticUserPrompt("First prompt", DateTimeOffset.UtcNow);
        presenter.RollbackOptimisticUserPrompt();
        presenter.RenderFailure("Failed to send prompt: test failure");

        Assert.AreEqual(1, presenter.Flow.Items.Count);
        var document = Assert.IsInstanceOfType<FlowDocument>(presenter.Flow.Items[0].Content);
        var block = Assert.IsInstanceOfType<VisualDocumentFlowBlock>(document.GetBlock(0));
        var group = Assert.IsInstanceOfType<Group>(block.CreateVisual());
        var markdown = group.EnumerateVisualsDepthFirst().OfType<MarkdownControl>().Single();
        Assert.AreEqual("Failed to send prompt: test failure", markdown.Markdown);
    }

    [TestMethod]
    public void RenderOptimisticUserPrompt_DoesNotForceTimelineToTail()
    {
        var presenter = CreatePresenter();
        presenter.Flow.FollowTail = false;

        presenter.RenderOptimisticUserPrompt("First prompt", DateTimeOffset.UtcNow);

        Assert.IsFalse(presenter.Flow.FollowTail);
    }

    [TestMethod]
    public void RenderOptimisticUserPrompt_ChecksFollowTailThroughDispatcher()
    {
        var dispatcher = new MarshallingUiDispatcher();
        var presenter = new ThreadTimelinePresenter(dispatcher, static () => null);
        var invocationsAfterConstruction = dispatcher.InvokeCount;

        presenter.RenderOptimisticUserPrompt("First prompt", DateTimeOffset.UtcNow);

        Assert.IsTrue(dispatcher.InvokeCount > invocationsAfterConstruction);
    }

    [TestMethod]
    public void UpsertInteraction_ReusesExistingTimelineItem()
    {
        var presenter = CreatePresenter();
        var timestamp = DateTimeOffset.UtcNow;

        presenter.UpsertInteraction(
            "interaction-1",
            timestamp,
            "Need approval",
            null,
            ChatTimelineTone.Interaction,
            "Action Required",
            "Permission Request");
        presenter.UpsertInteraction(
            "interaction-1",
            timestamp.AddSeconds(1),
            null,
            "Allowed once",
            ChatTimelineTone.Interaction,
            "Action Required",
            "Permission Request");

        Assert.AreEqual(1, presenter.Flow.Items.Count);
    }

    [TestMethod]
    public void BufferedHistoryFlush_PrependsTruncatedHistoryMarker()
    {
        var presenter = CreatePresenter();
        var truncatedHistoryItem = presenter.CreateTruncatedHistoryItem(2, static () => { });

        presenter.BeginBufferedHistoryLoad();
        presenter.AddStatus(DateTimeOffset.UtcNow, "Notice", ChatTimelineTone.Notice, headerOverride: "Notice");
        presenter.CompleteInitialBufferedHistory(truncatedHistoryItem);

        Assert.AreEqual(0, presenter.Flow.Items.Count);

        presenter.FlushBufferedHistoryItems();

        Assert.AreEqual(2, presenter.Flow.Items.Count);
        Assert.AreSame(truncatedHistoryItem.Content, presenter.Flow.Items[0].Content);
    }

    [TestMethod]
    public void BufferedHistoryFlush_IsIdempotent()
    {
        var presenter = CreatePresenter();

        presenter.BeginBufferedHistoryLoad();
        presenter.AddStatus(DateTimeOffset.UtcNow, "Notice", ChatTimelineTone.Notice, headerOverride: "Notice");

        presenter.FlushBufferedHistoryItems();
        presenter.FlushBufferedHistoryItems();

        Assert.AreEqual(1, presenter.Flow.Items.Count);
    }

    [TestMethod]
    public void BufferedHistoryFlush_CompletesBufferedMode()
    {
        var presenter = CreatePresenter();

        presenter.BeginBufferedHistoryLoad();
        presenter.AddStatus(DateTimeOffset.UtcNow, "First", ChatTimelineTone.Notice, headerOverride: "Notice");
        presenter.FlushBufferedHistoryItems();
        presenter.AddStatus(DateTimeOffset.UtcNow, "Second", ChatTimelineTone.Notice, headerOverride: "Notice");

        Assert.AreEqual(2, presenter.Flow.Items.Count);
    }

    [TestMethod]
    public void RevealTail_EnablesFollowTailMode()
    {
        var presenter = CreatePresenter();
        presenter.Flow.FollowTail = false;

        presenter.RevealTail();

        Assert.IsTrue(presenter.Flow.FollowTail);
    }

    [TestMethod]
    public void RevealTail_PostsThroughDispatcherBeforeChangingFollowTail()
    {
        var dispatcher = new QueueingUiDispatcher();
        var presenter = new ThreadTimelinePresenter(dispatcher, static () => null);
        presenter.Flow.FollowTail = false;

        presenter.RevealTail();

        Assert.IsFalse(presenter.Flow.FollowTail);

        dispatcher.DrainPostedActions();

        Assert.IsTrue(presenter.Flow.FollowTail);
    }

    [TestMethod]
    public void ScrollToPreviousMessage_FirstPressSelectsLastNavigableMessage()
    {
        var presenter = CreatePresenter();
        RenderCompletedContent(presenter, AgentContentKind.User, "user-1", "First prompt");
        RenderCompletedContent(presenter, AgentContentKind.Assistant, "assistant-1", "Assistant reply");

        presenter.ScrollToPreviousMessage();

        Assert.AreEqual(1, presenter.MessageNavigationIndex);
        Assert.IsFalse(presenter.Flow.FollowTail);

        presenter.ScrollToPreviousMessage();

        Assert.AreEqual(0, presenter.MessageNavigationIndex);
        Assert.IsFalse(presenter.Flow.FollowTail);
    }

    [TestMethod]
    public void ScrollToNextMessage_FromLastNavigableMessageReturnsToTail()
    {
        var presenter = CreatePresenter();
        RenderCompletedContent(presenter, AgentContentKind.User, "user-1", "First prompt");
        RenderCompletedContent(presenter, AgentContentKind.Assistant, "assistant-1", "Assistant reply");

        presenter.ScrollToPreviousMessage();
        presenter.ScrollToNextMessage();

        Assert.IsNull(presenter.MessageNavigationIndex);
        Assert.IsTrue(presenter.Flow.FollowTail);
    }

    [TestMethod]
    public void ScrollToFirstAndLastMessage_JumpToBounds()
    {
        var presenter = CreatePresenter();
        RenderCompletedContent(presenter, AgentContentKind.User, "user-1", "First prompt");
        RenderCompletedContent(presenter, AgentContentKind.Assistant, "assistant-1", "Assistant reply");

        presenter.ScrollToFirstMessage();

        Assert.AreEqual(0, presenter.MessageNavigationIndex);
        Assert.IsFalse(presenter.Flow.FollowTail);

        presenter.ScrollToLastMessage();

        Assert.IsNull(presenter.MessageNavigationIndex);
        Assert.IsTrue(presenter.Flow.FollowTail);
    }

    [TestMethod]
    public void MessageNavigation_IgnoresNonUserAssistantTimelineItems()
    {
        var presenter = CreatePresenter();

        presenter.AddStatus(DateTimeOffset.UtcNow, "Working", ChatTimelineTone.Activity, "Activity");

        Assert.IsFalse(presenter.HasNavigableMessages);
        presenter.ScrollToPreviousMessage();
        Assert.IsNull(presenter.MessageNavigationIndex);
    }

    [TestMethod]
    public void Reset_ClearsTimelineItemsAndResetsAssistantTracking()
    {
        var presenter = CreatePresenter();
        var timestamp = DateTimeOffset.UtcNow;
        var delta = new AgentContentDeltaEvent(
            AgentBackendIds.Codex,
            "session-1",
            timestamp,
            null,
            AgentContentKind.Assistant,
            "assistant-1",
            null,
            "Streaming reply");
        var completed = new AgentContentCompletedEvent(
            AgentBackendIds.Codex,
            "session-1",
            timestamp,
            null,
            AgentContentKind.Assistant,
            "assistant-1",
            null,
            string.Empty);

        presenter.AppendContent(delta);
        _ = presenter.CreateTruncatedHistoryItem(2, static () => { });

        Assert.IsFalse(presenter.ShouldSkipEmptyAssistantCompletion(completed));
        Assert.IsTrue(presenter.HasLoadableTruncatedHistory);
        Assert.AreEqual(1, presenter.Flow.Items.Count);

        presenter.Reset();

        Assert.AreEqual(0, presenter.Flow.Items.Count);
        Assert.IsTrue(presenter.ShouldSkipEmptyAssistantCompletion(completed));
        Assert.IsFalse(presenter.HasLoadableTruncatedHistory);
    }

    private static ThreadTimelinePresenter CreatePresenter()
        => new(new InlineUiDispatcher(), static () => null);

    private static System.Text.Json.JsonElement CreateJsonDetails(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static void RenderCompletedContent(
        ThreadTimelinePresenter presenter,
        AgentContentKind kind,
        string contentId,
        string content)
        => presenter.FinalizeContent(new AgentContentCompletedEvent(
            AgentBackendIds.Codex,
            "session-1",
            DateTimeOffset.UtcNow,
            null,
            kind,
            contentId,
            null,
            content));

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

    private sealed class QueueingUiDispatcher : IUiDispatcher
    {
        private readonly Queue<Action> _postedActions = new();

        public bool CheckAccess()
            => true;

        public void Post(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            _postedActions.Enqueue(action);
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

        public void DrainPostedActions()
        {
            while (_postedActions.Count > 0)
            {
                _postedActions.Dequeue()();
            }
        }
    }

    private sealed class MarshallingUiDispatcher : IUiDispatcher
    {
        public int InvokeCount { get; private set; }

        public bool CheckAccess()
            => false;

        public void Post(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
        }

        public Task InvokeAsync(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            InvokeCount++;
            action();
            return Task.CompletedTask;
        }

        public Task<T> InvokeAsync<T>(Func<T> action)
        {
            ArgumentNullException.ThrowIfNull(action);
            InvokeCount++;
            return Task.FromResult(action());
        }
    }
}
