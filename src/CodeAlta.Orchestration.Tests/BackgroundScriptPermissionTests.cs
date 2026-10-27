using CodeAlta.Agent;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

/// <summary>
/// The approval of a call a script makes after the send that started it ended (the script went on in the background): it
/// was denied once the send closed; it is asked on the session as a request outside a send is.
/// </summary>
[TestClass]
public sealed class BackgroundScriptPermissionTests
{
    private static AgentCommandPermissionRequest Command() => new(new("inert"), "session", DateTimeOffset.UnixEpoch, null, "call-1/1", null, "make", "inert", null, "fixture", null, null, null);

    [TestMethod]
    public async Task ARequestDuringItsSend_IsShownInTheTurn_AfterTakingItsTurnAmongTheQuestionsOfItsRun()
    {
        var owner = new SessionPermissionService();
        var forwarding = new OwnedProviderEventForwarding();
        var attachment = forwarding.RegisterAttachment("session", "inert", static () => Task.CompletedTask, static () => Task.CompletedTask);
        try
        {
            attachment.CloseOwnedPermissions = () => owner.InvalidateOwnedAttachmentAsync(attachment);
        }
        finally
        {
            attachment.CompleteSetup();
        }

        var policy = SessionPermissionPolicy.Review;
        owner.SessionPolicy = _ => policy;
        var waits = 0;
        var turn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        owner.WaitingForUser = async (request, token) =>
        {
            Interlocked.Increment(ref waits);
            // The turn of the run goes to a question asked before: the request is not shown until it ends.
            await turn.Task.WaitAsync(token);
            return null;
        };
        try
        {
            var execution = await owner.CreateOwnedExecutionAsync(Guid.NewGuid(), "session", CancellationToken.None, SessionPermissionPolicy.Review, enableUserInput: false);
            Assert.IsNotNull(execution);
            Assert.IsTrue(await owner.BindOwnedExecutionAsync(execution, Guid.NewGuid(), attachment, new("inert")));
            using var run = new CancellationTokenSource();
            await owner.CreateOwnedRunLifecycle(execution).StartedAsync(new("run"), run.Token);
            var handler = owner.CreateOwnedCommandHandler(execution);

            var asked = handler(Command(), CancellationToken.None);
            await Task.Delay(100);
            Assert.AreEqual(0, (await owner.ListOwnedCommandsAsync("session", CancellationToken.None)).Entries.Count, "it waits for its turn");
            turn.SetResult();
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            SessionOwnedPermissionPage page;
            while ((page = await owner.ListOwnedCommandsAsync("session", CancellationToken.None)).Entries.Count == 0)
            {
                Assert.IsLessThan(deadline, DateTime.UtcNow, "the request was not shown during its send");
                await Task.Delay(20);
            }

            Assert.IsTrue(await owner.ResolveOwnedCommandAsync(page.Entries[0].Handle, AgentPermissionDecisionKind.AllowOnce, CancellationToken.None));
            Assert.AreEqual(AgentPermissionDecisionKind.AllowOnce, (await asked.WaitAsync(TimeSpan.FromSeconds(10))).Kind);
            Assert.AreEqual(1, waits);

            // A request the session grants waits for nobody.
            policy = SessionPermissionPolicy.Approve;
            Assert.AreEqual(AgentPermissionDecisionKind.AllowOnce, (await handler(Command(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10))).Kind);
            Assert.AreEqual(1, waits);
            await owner.CloseOwnedExecutionAsync(execution);
        }
        finally
        {
            await forwarding.RetireAsync(attachment);
            await owner.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task ARequestAfterItsSendEnded_WaitsForTheUserOnTheSession_InsteadOfBeingDenied()
    {
        var owner = new SessionPermissionService();
        var forwarding = new OwnedProviderEventForwarding();
        var attachment = forwarding.RegisterAttachment("session", "inert", static () => Task.CompletedTask, static () => Task.CompletedTask);
        try
        {
            attachment.CloseOwnedPermissions = () => owner.InvalidateOwnedAttachmentAsync(attachment);
        }
        finally
        {
            attachment.CompleteSetup();
        }

        var runtime = Guid.NewGuid();
        var policy = SessionPermissionPolicy.Review;
        owner.SessionPolicy = _ => policy;
        owner.SessionReviewTarget = _ => new(runtime, attachment, new("inert"));
        var waits = 0;
        owner.WaitingForUser = (request, _) =>
        {
            Assert.AreEqual("call-1/1", request.InteractionId);
            Interlocked.Increment(ref waits);
            return ValueTask.FromResult<IDisposable?>(null);
        };
        try
        {
            var execution = await owner.CreateOwnedExecutionAsync(Guid.NewGuid(), "session", CancellationToken.None, SessionPermissionPolicy.Review, enableUserInput: false);
            Assert.IsNotNull(execution);
            Assert.IsTrue(await owner.BindOwnedExecutionAsync(execution, runtime, attachment, new("inert")));
            using var run = new CancellationTokenSource();
            await owner.CreateOwnedRunLifecycle(execution).StartedAsync(new("run"), run.Token);
            // The send ends, its run with it; the script of one of its calls goes on and asks.
            await owner.CloseOwnedExecutionAsync(execution);
            var handler = owner.CreateOwnedCommandHandler(execution);

            var asked = handler(Command(), CancellationToken.None);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            SessionOwnedPermissionPage page;
            while ((page = await owner.ListOwnedCommandsAsync("session", CancellationToken.None)).Entries.Count == 0)
            {
                Assert.IsFalse(asked.IsCompleted, $"the request ended without asking: {(asked.IsCompleted ? asked.Result.Kind : default)}");
                Assert.IsLessThan(deadline, DateTime.UtcNow, "the request was not shown");
                await Task.Delay(20);
            }

            CollectionAssert.Contains((await owner.ListWaitingSessionsAsync()).ToArray(), "session", "the session waits for the user");
            Assert.IsTrue(await owner.ResolveOwnedCommandAsync(page.Entries[0].Handle, AgentPermissionDecisionKind.AllowOnce, CancellationToken.None));
            Assert.AreEqual(AgentPermissionDecisionKind.AllowOnce, (await asked.WaitAsync(TimeSpan.FromSeconds(10))).Kind);
            Assert.AreEqual(1, waits, "the request took its turn among the questions of its run");

            // A session that approves everything approves it without asking, as during the send.
            policy = SessionPermissionPolicy.Approve;
            Assert.AreEqual(AgentPermissionDecisionKind.AllowOnce, (await handler(Command(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10))).Kind);
            Assert.AreEqual(1, waits);
        }
        finally
        {
            await forwarding.RetireAsync(attachment);
            await owner.DisposeAsync();
        }
    }
}
