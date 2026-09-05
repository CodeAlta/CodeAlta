using CodeAlta.Tui.App;
using CodeAlta.Tui.Models;
using CodeAlta.Tui.Presentation.Shell;

namespace CodeAlta.Tests;

[TestClass]
public sealed class UiTaskDiagnosticsTests
{
    [TestMethod]
    public async Task ObserveAsync_ReportsUnexpectedExceptionsToStatus()
    {
        string? message = null;
        var busy = true;
        var tone = StatusTone.Info;

        await UiTaskDiagnostics.ObserveAsync(
            Task.FromException(new InvalidOperationException("boom")),
            "submit the current prompt",
            (statusMessage, statusBusy, statusTone) =>
            {
                message = statusMessage;
                busy = statusBusy;
                tone = statusTone;
            });

        Assert.IsNotNull(message);
        StringAssert.Contains(message, "submit the current prompt");
        StringAssert.Contains(message, "boom");
        Assert.IsFalse(busy);
        Assert.AreEqual(StatusTone.Error, tone);
    }

    [TestMethod]
    public async Task ObserveAsync_TaskFactory_PreservesUiSynchronizationContextForUiWorkflows()
    {
        var previousContext = SynchronizationContext.Current;
        var inlineContext = new InlineSynchronizationContext();
        var resumed = false;
        try
        {
            SynchronizationContext.SetSynchronizationContext(inlineContext);

            var task = UiTaskDiagnostics.ObserveAsync(
                async () =>
                {
                    await Task.Yield();
                    resumed = true;
                },
                "resume test operation",
                static (_, _, _) => { });
            SynchronizationContext.SetSynchronizationContext(previousContext);
            await task;
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        Assert.IsTrue(resumed);
        Assert.AreEqual(1, inlineContext.PostCount);
    }

    [TestMethod]
    public async Task ObserveAsync_TaskFactory_DropsContinuationsAfterTerminalDispatcherDetaches()
    {
        var previousContext = SynchronizationContext.Current;
        var terminatingContext = new TerminatingSynchronizationContext();
        try
        {
            SynchronizationContext.SetSynchronizationContext(terminatingContext);

            var task = UiTaskDiagnostics.ObserveAsync(
                async () =>
                {
                    await Task.Yield();
                },
                "resume after terminal exit",
                static (_, _, _) => { });
            SynchronizationContext.SetSynchronizationContext(previousContext);

            var completed = await Task.WhenAny(task, Task.Delay(50)).ConfigureAwait(false);

            Assert.AreNotSame(task, completed);
            Assert.AreEqual(1, terminatingContext.PostCount);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    private sealed class InlineSynchronizationContext : SynchronizationContext
    {
        public int PostCount { get; private set; }

        public override void Post(SendOrPostCallback d, object? state)
        {
            PostCount++;
            d(state);
        }
    }

    private sealed class TerminatingSynchronizationContext : SynchronizationContext
    {
        public int PostCount { get; private set; }

        public override void Post(SendOrPostCallback d, object? state)
        {
            PostCount++;
            throw new InvalidOperationException("Dispatcher is not attached to a running TerminalApp.");
        }
    }
}
