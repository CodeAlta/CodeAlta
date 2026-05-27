using CodeAlta.Agent.Runtime;

namespace CodeAlta.Tests;

[TestClass]
public sealed class AgentSessionJournalFileTests
{
    [TestMethod]
    public async Task WithPathLockAsync_SamePathWaitsForCurrentOwner()
    {
        using var temp = TestTempDirectory.Create();
        var journalFile = new AgentSessionJournalFile();
        var path = Path.Combine(temp.Path, "session.jsonl");
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var firstTask = journalFile.WithPathLockAsync(
            path,
            async () =>
            {
                firstEntered.SetResult();
                await releaseFirst.Task.ConfigureAwait(false);
            },
            CancellationToken.None);

        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);

        var secondTask = journalFile.WithPathLockAsync(
            path,
            () =>
            {
                secondEntered.SetResult();
                return Task.CompletedTask;
            },
            CancellationToken.None);

        await Task.Delay(100).ConfigureAwait(false);
        Assert.IsFalse(secondEntered.Task.IsCompleted);

        releaseFirst.SetResult();
        await Task.WhenAll(firstTask, secondTask).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        Assert.IsTrue(secondEntered.Task.IsCompleted);
    }

    [TestMethod]
    public async Task WithPathLockAsync_DifferentPathsRunConcurrently()
    {
        using var temp = TestTempDirectory.Create();
        var journalFile = new AgentSessionJournalFile();
        var firstPath = Path.Combine(temp.Path, "session-1.jsonl");
        var secondPath = Path.Combine(temp.Path, "session-2.jsonl");
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var firstTask = journalFile.WithPathLockAsync(
            firstPath,
            async () =>
            {
                firstEntered.SetResult();
                await releaseFirst.Task.ConfigureAwait(false);
            },
            CancellationToken.None);

        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);

        var secondTask = journalFile.WithPathLockAsync(
            secondPath,
            () => Task.CompletedTask,
            CancellationToken.None);

        await secondTask.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        Assert.IsFalse(firstTask.IsCompleted);

        releaseFirst.SetResult();
        await firstTask.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
    }
}
