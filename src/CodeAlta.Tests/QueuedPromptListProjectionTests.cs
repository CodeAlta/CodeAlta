using CodeAlta.Agent;
using CodeAlta.App.State;
using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Presentation.Prompting;
using CodeAlta.Presentation.Timeline;
using CodeAlta.Threading;

namespace CodeAlta.Tests;

[TestClass]
public sealed class QueuedPromptListProjectionTests
{
    [TestMethod]
    public void Build_ReturnsEmptyProjection_WhenNoThreadIsSelected()
    {
        var projection = QueuedPromptListProjectionBuilder.Build(tab: null);

        Assert.IsFalse(projection.HasItems);
        Assert.AreEqual(0, projection.Items.Count);
    }

    [TestMethod]
    public void Build_NormalizesPreviewTextAndPreservesQueueCount()
    {
        var tab = CreateOpenThreadState();
        tab.QueuedPrompts.Add(new QueuedThreadPrompt("  First line\r\n\r\nSecond line  ", remainingCount: 3));

        var projection = QueuedPromptListProjectionBuilder.Build(tab);

        Assert.IsTrue(projection.HasItems);
        Assert.IsTrue(projection.HasQueuedPrompts);
        Assert.AreEqual(1, projection.Items.Count);
        Assert.AreEqual(PromptStripItemKind.QueuedPrompt, projection.Items[0].Kind);
        Assert.AreEqual("First line Second line", projection.Items[0].PreviewText);
        Assert.AreEqual("First line\r\n\r\nSecond line", projection.Items[0].Text);
        Assert.AreEqual(3, projection.Items[0].RemainingCount);
    }

    [TestMethod]
    public void Build_RendersPendingSteersBeforeQueuedPrompts()
    {
        var tab = CreateOpenThreadState();
        tab.PendingSteers.Add(new PendingSteerPrompt("  steer this next  "));
        tab.QueuedPrompts.Add(new QueuedThreadPrompt("queued prompt", remainingCount: 2));

        var projection = QueuedPromptListProjectionBuilder.Build(tab);

        Assert.AreEqual(2, projection.Items.Count);
        Assert.AreEqual(PromptStripItemKind.PendingSteer, projection.Items[0].Kind);
        Assert.AreEqual("steer this next", projection.Items[0].Text);
        Assert.IsNull(projection.Items[0].RemainingCount);
        Assert.AreEqual(PromptStripItemKind.QueuedPrompt, projection.Items[1].Kind);
        Assert.AreEqual(2, projection.Items[1].RemainingCount);
        Assert.IsTrue(projection.HasQueuedPrompts);
    }

    private static OpenThreadState CreateOpenThreadState()
    {
        var thread = new WorkThreadDescriptor
        {
            ThreadId = "thread-1",
            Kind = WorkThreadKind.ProjectThread,
            BackendId = AgentBackendIds.Codex.Value,
            BackendSessionId = "backend-thread-1",
            ProjectRef = "project-1",
            WorkingDirectory = @"C:\code\CodeAlta",
            Title = "Review startup",
            Status = WorkThreadStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            LastActiveAt = DateTimeOffset.UtcNow,
        };

        var timeline = new ThreadTimelinePresenter(new InlineUiDispatcher(), () => true, static () => null);
        return new OpenThreadState(thread, timeline);
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
