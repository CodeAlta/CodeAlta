using CodeAlta.Agent;
using CodeAlta.App;
using CodeAlta.Models;
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
        => new(new InlineUiDispatcher(), () => true, static () => null);

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
