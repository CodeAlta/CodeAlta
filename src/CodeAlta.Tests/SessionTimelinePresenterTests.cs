using CodeAlta.Catalog;
using CodeAlta.Tui.Threading;
using CodeAlta.Agent;
using CodeAlta.Tui.App;
using CodeAlta.Tui.Models;
using CodeAlta.Tui.Presentation.Prompting;
using CodeAlta.Tui.Presentation.Timeline;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;

namespace CodeAlta.Tests;

[TestClass]
public sealed class SessionTimelinePresenterTests
{
    [TestMethod]
    public async Task CreateTruncatedHistoryState_CanBeCreatedFromWorkerSession()
    {
        var state = await Task.Run(() => SessionTimelinePresenter.CreateTruncatedHistoryState(3, static () => { }));

        Assert.IsNotNull(state);
        Assert.IsInstanceOfType<Rule>(state.Rule);
        Assert.IsInstanceOfType<Button>(state.Rule.CenterLabel);
        Assert.AreEqual(3, state.OmittedMessageCount);
    }

    [TestMethod]
    public void BuildInitialSessionHistoryItems_PrependsTruncatedHistoryBeforeFirstLoadedPrompt()
    {
        var pending = ChatTimelineVisualFactory.CreatePendingChatMessage("hello");
        var followup = ChatTimelineVisualFactory.CreatePendingChatMessage("assistant");
        var truncatedHistory = SessionTimelinePresenter.CreateTruncatedHistoryState(3, static () => { });

        var items = SessionTimelinePresenter.BuildInitialSessionHistoryItems(
            [pending.UserItem, followup.AssistantItem],
            truncatedHistory.Item);

        Assert.AreEqual(3, items.Count);
        Assert.AreSame(truncatedHistory.Item.Content, items[0].Content);
        Assert.AreSame(pending.UserItem.Content, items[1].Content);
        Assert.AreSame(followup.AssistantItem.Content, items[2].Content);
    }

    [TestMethod]
    public void BuildInitialSessionHistoryItems_LeavesRenderedItemsUnchangedWhenNoMarkerExists()
    {
        var pending = ChatTimelineVisualFactory.CreatePendingChatMessage("hello");
        var followup = ChatTimelineVisualFactory.CreatePendingChatMessage("assistant");

        var items = SessionTimelinePresenter.BuildInitialSessionHistoryItems(
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

        var content = SessionTimelinePresenter.ResolveCompletedContent(string.Empty, buffer);

        Assert.AreEqual("Streaming assistant reply", content);
    }

    [TestMethod]
    public void ResolveCompletedContent_PrefersCompletedPayloadWhenPresent()
    {
        var buffer = new System.Text.StringBuilder("Older delta text");

        var content = SessionTimelinePresenter.ResolveCompletedContent("Final assistant reply", buffer);

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
            ModelProviderIds.Codex,
            "session-1",
            timestamp,
            null,
            AgentContentKind.Assistant,
            "assistant-1",
            null,
            "Streaming reply");
        var completed = new AgentContentCompletedEvent(
            ModelProviderIds.Codex,
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
        Assert.AreEqual("Streaming reply", GetOnlyMarkdown(presenter).Markdown);
    }

    [TestMethod]
    public void FinalizeContent_ReusesEquivalentStreamingItemWhenCompletedContentIdDiffers()
    {
        var presenter = CreatePresenter();
        var timestamp = DateTimeOffset.UtcNow;
        var runId = new AgentRunId("run-1");
        var delta = new AgentContentDeltaEvent(
            ModelProviderIds.Codex,
            "session-1",
            timestamp,
            runId,
            AgentContentKind.Assistant,
            "assistant-stream",
            null,
            "Streaming ");
        var completed = new AgentContentCompletedEvent(
            ModelProviderIds.Codex,
            "session-1",
            timestamp.AddSeconds(1),
            runId,
            AgentContentKind.Assistant,
            "assistant-final",
            null,
            "Streaming assistant reply");

        presenter.AppendContent(delta);
        presenter.FinalizeContent(completed);

        Assert.AreEqual(1, presenter.Flow.Items.Count);
        Assert.AreEqual("Streaming assistant reply", GetOnlyMarkdown(presenter).Markdown);
    }

    [TestMethod]
    public void AppendContent_IgnoresLateDeltaAfterAuthoritativeCompletedContent()
    {
        var presenter = CreatePresenter();
        var timestamp = DateTimeOffset.UtcNow;
        var runId = new AgentRunId("run-1");
        var delta = new AgentContentDeltaEvent(
            ModelProviderIds.Codex,
            "session-1",
            timestamp,
            runId,
            AgentContentKind.Assistant,
            "assistant-stream",
            null,
            "Streaming ");
        var completed = new AgentContentCompletedEvent(
            ModelProviderIds.Codex,
            "session-1",
            timestamp.AddSeconds(1),
            runId,
            AgentContentKind.Assistant,
            "assistant-final",
            null,
            "Streaming assistant reply");
        var lateDelta = delta with { Timestamp = timestamp.AddSeconds(2), Delta = "assistant reply" };

        presenter.AppendContent(delta);
        presenter.FinalizeContent(completed);
        presenter.AppendContent(lateDelta);

        Assert.AreEqual(1, presenter.Flow.Items.Count);
        Assert.AreEqual("Streaming assistant reply", GetOnlyMarkdown(presenter).Markdown);
    }

    [TestMethod]
    public void DiscardDraftContent_RemovesMatchingDraftTimelineItem()
    {
        var presenter = CreatePresenter();
        var timestamp = DateTimeOffset.UtcNow;
        var delta = new AgentContentDeltaEvent(
            ModelProviderIds.Codex,
            "session-1",
            timestamp,
            null,
            AgentContentKind.Assistant,
            "assistant-1",
            null,
            "Partial draft",
            CreateJsonDetails("""{"attemptId":"run-1:1","draft":true}"""));
        var update = new AgentSessionUpdateEvent(
            ModelProviderIds.Codex,
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
    public void FinalizeContent_KeepsStreamedReasoningHeadingAfterPlaceholderArrives()
    {
        var presenter = CreatePresenter();
        var timestamp = DateTimeOffset.UtcNow;
        var delta = new AgentContentDeltaEvent(
            ModelProviderIds.Codex,
            "session-1",
            timestamp,
            new AgentRunId("run-1"),
            AgentContentKind.Reasoning,
            "reasoning-1",
            null,
            "**Checking tests**",
            CreateJsonDetails("""{"summaryIndex":0}"""));
        var placeholderDelta = delta with
        {
            Timestamp = timestamp.AddMilliseconds(500),
            Delta = "\n\n<!-- -->",
        };
        var completed = new AgentContentCompletedEvent(
            ModelProviderIds.Codex,
            "session-1",
            timestamp.AddSeconds(1),
            new AgentRunId("run-1"),
            AgentContentKind.Reasoning,
            "reasoning-1",
            null,
            "**Checking tests**\n\n<!-- -->",
            CreateJsonDetails("""{"summaryParts":["**Checking tests**\n\n<!-- -->"]}"""));

        presenter.AppendContent(delta);
        presenter.AppendContent(placeholderDelta);
        Assert.AreEqual(1, presenter.Flow.Items.Count);
        Assert.AreEqual(string.Empty, GetOnlyMarkdown(presenter).Markdown);
        Assert.IsTrue(CodeAlta.Tui.Presentation.Formatting.ChatMarkdownFormatter.ShouldDisplayCompletedContent(completed));

        presenter.FinalizeContent(completed);

        Assert.AreEqual(1, presenter.Flow.Items.Count);
        Assert.AreEqual(string.Empty, GetOnlyMarkdown(presenter).Markdown);
    }

    [TestMethod]
    public void AppendContent_RemovesPlaceholderDeltaBeforeReasoningHeading()
    {
        var presenter = CreatePresenter();
        var timestamp = DateTimeOffset.UtcNow;
        var delta = new AgentContentDeltaEvent(
            ModelProviderIds.Codex,
            "session-1",
            timestamp,
            new AgentRunId("run-1"),
            AgentContentKind.Reasoning,
            "reasoning-1",
            null,
            "<!-- -->",
            CreateJsonDetails("""{"summaryIndex":0}"""));

        presenter.AppendContent(delta);
        presenter.AppendContent(delta with
        {
            Timestamp = timestamp.AddMilliseconds(500),
            Delta = "**Planning Codex model inspection**",
        });

        Assert.AreEqual(1, presenter.Flow.Items.Count);
        Assert.AreEqual(string.Empty, GetOnlyMarkdown(presenter).Markdown);
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
        var presenter = new SessionTimelinePresenter(dispatcher, static () => null);
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
        var presenter = new SessionTimelinePresenter(dispatcher, static () => null);
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
    public void StaleDeferredProjection_DoesNotSplitSequentialToolCallGroup()
    {
        var dispatcher = new DeferringUiDispatcher();
        var presenter = new SessionTimelinePresenter(dispatcher, static () => null);
        var timestamp = DateTimeOffset.UtcNow;
        var shouldApplyProjection = true;

        presenter.ToolCalls.TryHandleActivity(CreateToolActivity("tool-1", timestamp));
        presenter.UpsertPluginProjection(
            "projection-1",
            timestamp.AddMilliseconds(1),
            "Projection that is stale before it reaches the UI.",
            shouldApply: () => shouldApplyProjection);
        shouldApplyProjection = false;
        presenter.ToolCalls.TryHandleActivity(CreateToolActivity("tool-2", timestamp.AddMilliseconds(2)));

        dispatcher.DrainPostedActions();

        Assert.AreEqual(1, presenter.Flow.Items.Count);
    }

    [TestMethod]
    public void Reset_ClearsTimelineItemsAndResetsAssistantTracking()
    {
        var presenter = CreatePresenter();
        var timestamp = DateTimeOffset.UtcNow;
        var delta = new AgentContentDeltaEvent(
            ModelProviderIds.Codex,
            "session-1",
            timestamp,
            null,
            AgentContentKind.Assistant,
            "assistant-1",
            null,
            "Streaming reply");
        var completed = new AgentContentCompletedEvent(
            ModelProviderIds.Codex,
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

    private static SessionTimelinePresenter CreatePresenter()
        => new(new InlineUiDispatcher(), static () => null);

    private static System.Text.Json.JsonElement CreateJsonDetails(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static MarkdownControl GetOnlyMarkdown(SessionTimelinePresenter presenter)
    {
        var document = Assert.IsInstanceOfType<FlowDocument>(presenter.Flow.Items.Single().Content);
        var block = Assert.IsInstanceOfType<VisualDocumentFlowBlock>(document.GetBlock(0));
        var group = Assert.IsInstanceOfType<Group>(block.CreateVisual());
        return group.EnumerateVisualsDepthFirst().OfType<MarkdownControl>().Single();
    }

    private static void RenderCompletedContent(
        SessionTimelinePresenter presenter,
        AgentContentKind kind,
        string contentId,
        string content)
        => presenter.FinalizeContent(new AgentContentCompletedEvent(
            ModelProviderIds.Codex,
            "session-1",
            DateTimeOffset.UtcNow,
            null,
            kind,
            contentId,
            null,
            content));

    private static AgentActivityEvent CreateToolActivity(string activityId, DateTimeOffset timestamp)
        => new(
            ModelProviderIds.Codex,
            "session-1",
            timestamp,
            null,
            AgentActivityKind.ToolCall,
            AgentActivityPhase.Started,
            activityId,
            null,
            "read_file",
            null);

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

    private sealed class DeferringUiDispatcher : IUiDispatcher
    {
        private readonly Queue<Action> _postedActions = new();

        public bool CheckAccess()
            => false;

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
