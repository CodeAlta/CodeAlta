using CodeAlta.Tui.Threading;
using System.Reflection;
using System.Text;
using CodeAlta.Agent;
using CodeAlta.Tui.App;
using CodeAlta.Tui.Models;
using CodeAlta.Tui.Presentation.Timeline;
using CodeAlta.Tui.Views;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;

namespace CodeAlta.Tests;

[TestClass]
public sealed class ToolCallPresenterTests
{
    [TestMethod]
    public void TryHandleContent_AccumulatesOutputStateWithoutReprocessingWholeBuffer()
    {
        var presenter = CreatePresenter();
        var timestamp = DateTimeOffset.UtcNow;

        presenter.TryHandleContent(new AgentContentDeltaEvent(
            ModelProviderIds.Codex,
            "session-1",
            timestamp,
            null,
            AgentContentKind.CommandOutput,
            "tool-1",
            null,
            "first line\r"));
        presenter.TryHandleContent(new AgentContentDeltaEvent(
            ModelProviderIds.Codex,
            "session-1",
            timestamp.AddMilliseconds(10),
            null,
            AgentContentKind.CommandOutput,
            "tool-1",
            null,
            "\nsecond line\r\nthird line"));

        var entry = GetToolCallEntry(presenter, "tool-1");

        Assert.AreEqual("first line\nsecond line\nthird line", entry.OutputBuffer.ToString());
        Assert.AreEqual(3, entry.OutputLineCount);
        Assert.AreEqual(Encoding.UTF8.GetByteCount(entry.OutputBuffer.ToString()), entry.OutputByteCount);
        Assert.AreEqual("third line", entry.OutputPreview);
        Assert.AreEqual(ToolCallDisplayStatus.Running, entry.Status);
    }

    [TestMethod]
    public void TryHandleActivity_MaintainsCachedGroupStatusCountsAcrossTransitions()
    {
        var presenter = CreatePresenter();
        var timestamp = DateTimeOffset.UtcNow;

        presenter.TryHandleActivity(new AgentActivityEvent(
            ModelProviderIds.Codex,
            "session-1",
            timestamp,
            null,
            AgentActivityKind.ToolCall,
            AgentActivityPhase.Requested,
            "tool-1",
            null,
            "shell_command",
            null));
        presenter.TryHandleActivity(new AgentActivityEvent(
            ModelProviderIds.Codex,
            "session-1",
            timestamp.AddMilliseconds(5),
            null,
            AgentActivityKind.ToolCall,
            AgentActivityPhase.Started,
            "tool-2",
            null,
            "shell_command",
            null));
        presenter.TryHandleActivity(new AgentActivityEvent(
            ModelProviderIds.Codex,
            "session-1",
            timestamp.AddMilliseconds(10),
            null,
            AgentActivityKind.ToolCall,
            AgentActivityPhase.Completed,
            "tool-1",
            null,
            "shell_command",
            null));
        presenter.TryHandleActivity(new AgentActivityEvent(
            ModelProviderIds.Codex,
            "session-1",
            timestamp.AddMilliseconds(15),
            null,
            AgentActivityKind.ToolCall,
            AgentActivityPhase.Failed,
            "tool-2",
            null,
            "shell_command",
            null));

        var firstEntry = GetToolCallEntry(presenter, "tool-1");
        var group = firstEntry.Group ?? throw new AssertFailedException("Expected tool call group.");

        Assert.AreEqual(0, group.PendingCount);
        Assert.AreEqual(0, group.RunningCount);
        Assert.AreEqual(1, group.CompletedCount);
        Assert.AreEqual(1, group.FailedCount);
        Assert.AreEqual(0, group.CanceledCount);
        Assert.AreEqual(2, group.ToolCalls.Count);
    }

    [TestMethod]
    public void TryHandleActivity_OnExistingToolCallDoesNotForceTailScroll()
    {
        var flow = new DocumentFlow { FollowTail = false };
        var presenter = CreatePresenter(flow);
        var timestamp = DateTimeOffset.UtcNow;

        presenter.TryHandleActivity(new AgentActivityEvent(
            ModelProviderIds.Codex,
            "session-1",
            timestamp,
            null,
            AgentActivityKind.ToolCall,
            AgentActivityPhase.Requested,
            "tool-1",
            null,
            "shell_command",
            null));

        flow.FollowTail = false;

        presenter.TryHandleActivity(new AgentActivityEvent(
            ModelProviderIds.Codex,
            "session-1",
            timestamp.AddMilliseconds(5),
            null,
            AgentActivityKind.ToolCall,
            AgentActivityPhase.Progressed,
            "tool-1",
            null,
            "shell_command",
            null));

        Assert.IsFalse(flow.FollowTail);
    }

    private static ToolCallPresenter CreatePresenter(DocumentFlow? flow = null)
    {
        flow ??= new DocumentFlow();
        return new(
            flow,
            new InlineUiDispatcher(),
            static _ => { },
            static () => (Rectangle?)null);
    }

    private static ToolCallEntryState GetToolCallEntry(ToolCallPresenter presenter, string toolCallId)
    {
        var field = typeof(ToolCallPresenter).GetField("_toolCalls", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field);
        var toolCalls = field.GetValue(presenter) as Dictionary<string, ToolCallEntryState>;
        Assert.IsNotNull(toolCalls);
        Assert.IsTrue(toolCalls.TryGetValue(toolCallId, out var entry));
        return entry ?? throw new AssertFailedException($"Expected tool call entry '{toolCallId}'.");
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
