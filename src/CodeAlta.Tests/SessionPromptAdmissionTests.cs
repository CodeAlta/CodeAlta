using CodeAlta.Tui.App;

namespace CodeAlta.Tests;

[TestClass]
public sealed class SessionPromptAdmissionTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ClearFailure_DoesNotAdmitAndRetryAdmitsOnce(bool enqueue)
    {
        var failClear = true;
        var queued = 0;
        var dispatched = 0;
        var cleared = false;

        void ClearInput()
        {
            if (failClear)
            {
                throw new IOException("Draft deletion conflicted");
            }

            cleared = true;
        }

        void EnqueuePrompt()
        {
            Assert.IsTrue(cleared);
            queued++;
        }

        Task DispatchPrompt()
        {
            Assert.IsTrue(cleared);
            dispatched++;
            return Task.CompletedTask;
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            await Assert.ThrowsAsync<IOException>(() => SessionCommandCoordinator.ClearInputAndAdmitPromptAsync(
                ClearInput, enqueue, EnqueuePrompt, DispatchPrompt));
            Assert.AreEqual(0, queued);
            Assert.AreEqual(0, dispatched);
            Assert.IsFalse(cleared);
        }

        failClear = false;
        await SessionCommandCoordinator.ClearInputAndAdmitPromptAsync(ClearInput, enqueue, EnqueuePrompt, DispatchPrompt);
        Assert.AreEqual(enqueue ? 1 : 0, queued);
        Assert.AreEqual(enqueue ? 0 : 1, dispatched);
    }

    [TestMethod]
    public async Task AdmissionCompletion_DoesNotRunAnotherThrowingClearAfterAcceptance()
    {
        var clearCount = 0;
        var accepted = false;
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var send = SessionCommandCoordinator.ClearInputAndAdmitPromptAsync(
            () =>
            {
                clearCount++;
                if (accepted)
                {
                    throw new IOException("A post-admission clear would falsely report send failure");
                }
            },
            false,
            () => Assert.Fail("This prompt should dispatch, not queue."),
            () =>
            {
                accepted = true;
                return complete.Task;
            });

        Assert.IsTrue(accepted);
        Assert.IsFalse(send.IsCompleted);
        complete.SetResult();
        await send;
        Assert.AreEqual(1, clearCount);
    }
}
