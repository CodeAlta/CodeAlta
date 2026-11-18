using CodeAlta.Agent;
using CodeAlta.CodeMode;
using CodeAlta.Orchestration.Runtime.Scripts;

namespace CodeAlta.Orchestration.Tests;

/// <summary>The lifetime of a script handle: from admission through delivery or release of its session.</summary>
[TestClass]
public sealed class CodeModeRunRegistryTests
{
    [TestMethod]
    public async Task Admission_PublishesAPendingCompletion_EvenBeforeTheScriptStarts()
    {
        var run = new CodeModeRunHandle("run-1", "s", null, 100, (_, _) => Task.CompletedTask);
        var published = run.Completion;
        Assert.IsTrue(run.Running);
        Assert.IsFalse(published.IsCompleted);
        run.Stop("test"); // A stop between admission and the execution of the script.
        run.Start(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new(CodeModeRunStatus.Completed, TimeSpan.Zero);
        });

        Assert.AreSame(published, run.Completion);
        Assert.AreEqual(CodeModeRunStatus.Cancelled, (await published.WithinAsync(TestDeadlines.Hang)).Status);
    }

    [TestMethod]
    public async Task Shutdown_JoinsTheFinalizer_AfterTheScriptHasReturnedItsResult()
    {
        var writingEnd = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = new CodeModeRunRegistry(null)
        {
            Journal = async (_, record) =>
            {
                if (record.Kind != AgentWorkflowEventKind.RunEnded) return;
                writingEnd.SetResult();
                await release.Task;
            },
        };
        var run = runs.TryStart("s", "Quick", 100, (_, _) => Task.FromResult(new CodeModeRunResult(CodeModeRunStatus.Completed, TimeSpan.Zero)), out _)!;
        await writingEnd.Task.WithinAsync(TestDeadlines.Hang);
        Assert.IsTrue(run.Completion.IsCompleted);
        var closing = runs.DisposeAsync().AsTask();
        try
        {
            Assert.AreNotSame(closing, await Task.WhenAny(closing, Task.Delay(100)), "shutdown returned with the finalizer still writing");
        }
        finally
        {
            release.SetResult();
            await closing.WithinAsync(TestDeadlines.Hang);
        }
    }
}
