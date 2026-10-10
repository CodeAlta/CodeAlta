using CodeAlta.Agent;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

/// <summary>Mailbox-only run binding fixtures; no host, store, provider or tool is created.</summary>
[TestClass]
public sealed class SessionOwnedRunBindingTests
{
    [TestMethod]
    public Task OwnedRunBinding_IsOneTimeAndDoesNotCancelUnrelatedExecution() => Fixture.Run(async f =>
    {
        var first = await f.CreateExecution();
        var second = await f.CreateExecution();
        var a = f.NewSource();
        var b = f.NewSource();
        Assert.IsTrue(await f.Wait(f.Permissions.BindOwnedRunAsync(first, new("a"), a.Token).AsTask()));
        Assert.IsFalse(await f.Wait(f.Permissions.BindOwnedRunAsync(first, new("b"), b.Token).AsTask()));
        Assert.IsTrue(await f.Wait(f.Permissions.BindOwnedRunAsync(second, new("b"), b.Token).AsTask()));
        var firstCallback = f.Permissions.CreateOwnedCommandHandler(first);
        var secondCallback = f.Permissions.CreateOwnedCommandHandler(second);
        var wrong = f.Keep(firstCallback(f.Request("wrong", "b"), CancellationToken.None));
        Assert.AreEqual(AgentPermissionDecisionKind.Deny, (await f.Wait(wrong)).Kind);
        var pendingA = f.Keep(firstCallback(f.Request("first", null), CancellationToken.None));
        var pendingB = f.Keep(secondCallback(f.Request("second", "b"), CancellationToken.None));
        var page = await f.Wait(f.Permissions.ListOwnedCommandsAsync("session", CancellationToken.None).AsTask());
        Assert.HasCount(2, page.Entries);
        var handleA = page.Entries.Single(entry => entry.Handle.OperationId == first.OperationId).Handle;
        var handleB = page.Entries.Single(entry => entry.Handle.OperationId == second.OperationId).Handle;
        var cancelA = f.Keep(a.CancelAsync());
        Assert.IsFalse(await f.Wait(f.Permissions.ResolveOwnedCommandAsync(handleA, AgentPermissionDecisionKind.AllowOnce, CancellationToken.None).AsTask()));
        await f.Wait(cancelA);
        Assert.AreEqual(AgentPermissionDecisionKind.Cancel, (await f.Wait(pendingA)).Kind);
        Assert.IsFalse(pendingB.IsCompleted);
        Assert.IsTrue(await f.Wait(f.Permissions.ResolveOwnedCommandAsync(handleB, AgentPermissionDecisionKind.AllowOnce, CancellationToken.None).AsTask()));
        Assert.AreEqual(AgentPermissionDecisionKind.AllowOnce, (await f.Wait(pendingB)).Kind);
        await f.Wait(f.Permissions.CloseOwnedExecutionAsync(first));
        Assert.AreEqual(AgentPermissionDecisionKind.Deny, (await f.Wait(f.Keep(firstCallback(f.Request("late", "a"), CancellationToken.None)))).Kind);
        await f.Wait(f.Keep(b.CancelAsync()));
        Assert.AreEqual(AgentPermissionDecisionKind.AllowOnce, (await f.Wait(pendingB)).Kind);
    });

    [TestMethod]
    public Task OwnedRunBinding_RejectsUnattachedClosedAndAlreadyCancelledBindings() => Fixture.Run(async f =>
    {
        var token = f.NewSource();
        var execution = await f.Wait(f.Permissions.CreateOwnedExecutionAsync(Guid.NewGuid(), "session", CancellationToken.None).AsTask());
        Assert.IsNotNull(execution);
        Assert.IsFalse(await f.Wait(f.Permissions.BindOwnedRunAsync(execution, new("run"), token.Token).AsTask()));
        var attached = await f.CreateExecution();
        await f.Wait(f.Keep(token.CancelAsync()));
        Assert.IsFalse(await f.Wait(f.Permissions.BindOwnedRunAsync(attached, new("run"), token.Token).AsTask()));
        await f.Wait(f.Permissions.CloseOwnedExecutionAsync(attached));
        var fresh = f.NewSource();
        Assert.IsFalse(await f.Wait(f.Permissions.BindOwnedRunAsync(attached, new("run"), fresh.Token).AsTask()));
    });

    [TestMethod]
    public Task OwnedReview_PresentsAFileChangeByItsRoot_AndStillRefusesWhatItCannotShowWhole() => Fixture.Run(async f =>
    {
        var execution = await f.CreateExecution();
        var callback = f.Permissions.CreateOwnedCommandHandler(execution);

        // A file change is reviewed like a command: it carries the root it asks to write under, and nothing of
        // the shape of a command.
        var pending = f.Keep(callback(f.FileChange("edit", "inert-directory"), CancellationToken.None));
        var entry = (await f.Wait(f.Permissions.ListOwnedCommandsAsync("session", CancellationToken.None).AsTask())).Entries.Single();
        Assert.AreEqual("fileChange", entry.Request.Kind);
        Assert.AreEqual("inert-directory", entry.Request.GrantRoot);
        Assert.IsNull(entry.Request.Command);
        Assert.IsNull(entry.Request.WorkingDirectory);
        Assert.IsTrue(await f.Wait(f.Permissions.ResolveOwnedCommandAsync(entry.Handle, AgentPermissionDecisionKind.AllowOnce, CancellationToken.None).AsTask()));
        Assert.AreEqual(AgentPermissionDecisionKind.AllowOnce, (await f.Wait(pending)).Kind);

        // A file change that does not say where it would write is not presented, and a command that carries
        // more than the review shows whole is still refused rather than shown as less than it is.
        Assert.AreEqual(AgentPermissionDecisionKind.Deny,
            (await f.Wait(f.Keep(callback(f.FileChange("rootless", null), CancellationToken.None)))).Kind);
        Assert.AreEqual(AgentPermissionDecisionKind.Deny,
            (await f.Wait(f.Keep(callback(f.Request("rich", null) with { ProposedExecPolicyAmendment = ["inert"] }, CancellationToken.None)))).Kind);
    });

    [TestMethod]
    public Task OwnedPermission_KeepsTheApprovalPolicyItsSendStartedWith() => Fixture.Run(async f =>
    {
        // The user turns the review on and off while the host runs: the next send follows, with no restart, and
        // a send that runs keeps what it started with. These executions review nothing, so the automatic-approval
        // policy alone answers each request.
        f.AutoApprove = true;
        var started = f.Permissions.CreateOwnedCommandHandler(await f.CreateExecution(reviewCommands: false));
        Assert.AreEqual(AgentPermissionDecisionKind.AllowOnce,
            (await f.Wait(f.Keep(started(f.Request("approved", null), CancellationToken.None)))).Kind);

        f.AutoApprove = false;
        Assert.AreEqual(AgentPermissionDecisionKind.AllowOnce,
            (await f.Wait(f.Keep(started(f.Request("still-approved", null), CancellationToken.None)))).Kind,
            "A send that started unreviewed is not denied when the review is turned on: it is approved until it ends.");
        var next = f.Permissions.CreateOwnedCommandHandler(await f.CreateExecution(reviewCommands: false));
        Assert.AreEqual(AgentPermissionDecisionKind.Deny,
            (await f.Wait(f.Keep(next(f.Request("refused", null), CancellationToken.None)))).Kind);
    });

    [TestMethod]
    public Task OwnedPermission_OfASendWithoutExecution_KeepsTheApprovalPolicyItStartedWith() => Fixture.Run(async f =>
    {
        // A send that reviews nothing and asks the user nothing has no owned execution: its default decision is
        // fixed when it starts all the same, so turning the review on while it runs does not deny what it does.
        f.AutoApprove = true;
        var started = f.Permissions.CreateSendDefaultPermissionHandler(null);
        f.AutoApprove = false;
        Assert.AreEqual(AgentPermissionDecisionKind.AllowOnce,
            (await f.Wait(f.Keep(started(f.Request("still-approved", null), CancellationToken.None)))).Kind);
        Assert.AreEqual(AgentPermissionDecisionKind.Deny,
            (await f.Wait(f.Keep(f.Permissions.CreateSendDefaultPermissionHandler(null)(f.Request("refused", null), CancellationToken.None)))).Kind);
        // The policy of a session the send started with decides over the host.
        Assert.AreEqual(AgentPermissionDecisionKind.AllowOnce,
            (await f.Wait(f.Keep(f.Permissions.CreateSendDefaultPermissionHandler(SessionPermissionPolicy.Approve)(f.Request("bypassed", null), CancellationToken.None)))).Kind);
    });

    private sealed class Fixture
    {
        private readonly object _gate = new();
        private readonly List<Task> _work = [];
        private readonly List<CancellationTokenSource> _sources = [];
        private readonly List<Exception> _failures = [];
        private Task? _lifetime;
        /// <summary>The host's automatic-approval policy, read again for every request.</summary>
        internal bool AutoApprove { get; set; }
        internal SessionPermissionService Permissions { get; }
        private readonly OwnedProviderEventForwarding.Attachment _attachment = new(new OwnedProviderEventForwarding(), 1,
            new("session", "inert-handle"), static () => Task.CompletedTask, static () => Task.CompletedTask);
        internal Fixture() => Permissions = new SessionPermissionService(() => AutoApprove);
        internal Task Keep(Task task) { lock (_gate) _work.Add(task); return task; }
        internal Task<T> Keep<T>(Task<T> task) { Keep((Task)task); return task; }
        internal Task Wait(Task task) => Keep(Keep(task).WaitAsync(TimeSpan.FromSeconds(5)));
        internal Task<T> Wait<T>(Task<T> task) => Keep(Keep(task).WaitAsync(TimeSpan.FromSeconds(5)));
        internal CancellationTokenSource NewSource()
        {
            var source = new CancellationTokenSource();
            lock (_gate) _sources.Add(source);
            return source;
        }
        internal Task<SessionPermissionService.OwnedPermissionExecution> CreateExecution() => CreateExecution(reviewCommands: true);
        internal async Task<SessionPermissionService.OwnedPermissionExecution> CreateExecution(bool reviewCommands)
        {
            var execution = await Wait(Permissions.CreateOwnedExecutionAsync(Guid.NewGuid(), "session", CancellationToken.None, reviewCommands, false).AsTask());
            Assert.IsNotNull(execution);
            Assert.IsTrue(await Wait(Permissions.BindOwnedExecutionAsync(execution, Guid.NewGuid(), _attachment, new("inert")).AsTask()));
            return execution;
        }
        internal AgentCommandPermissionRequest Request(string interaction, string? run) => new(new("inert"), "session", DateTimeOffset.UtcNow,
            run is null ? null : new AgentRunId(run), interaction, null, "inert text only", "inert-directory", null, "fixture", null, null, null);
        internal AgentFileChangePermissionRequest FileChange(string interaction, string? grantRoot) => new(new("inert"), "session",
            DateTimeOffset.UtcNow, null, interaction, grantRoot, "fixture");
        internal static async Task Run(Func<Fixture, Task> body)
        {
            var f = new Fixture();
            f._lifetime = f.RunOwned(body);
            try { await f._lifetime.WaitAsync(TimeSpan.FromSeconds(30)); }
            catch (Exception ex) { lock (f._gate) f._failures.Add(ex); }
            Exception[] failures;
            lock (f._gate) failures = [.. f._failures];
            if (failures.Length > 0)
            {
                var error = new AggregateException("Mailbox fixture failed; unconfirmed lifetime remains retained.", failures);
                error.Data["RetainedFixture"] = f;
                throw error;
            }
        }
        private async Task RunOwned(Func<Fixture, Task> body)
        {
            try { await body(this); }
            catch (Exception ex) { lock (_gate) _failures.Add(ex); }
            finally
            {
                CancellationTokenSource[] sources;
                lock (_gate) sources = [.. _sources];
                foreach (var source in sources) _ = Keep(source.CancelAsync());
                _ = Keep(Permissions.DisposeAsync().AsTask());
                Task[] tasks;
                lock (_gate) tasks = [.. _work];
                await Task.WhenAll(tasks.Distinct().Select(Join));
                Task[] lateTasks;
                lock (_gate) lateTasks = _work.Except(tasks).ToArray();
                await Task.WhenAll(lateTasks.Select(Join));
                foreach (var source in sources) source.Dispose();
                _attachment.Cancellation.Dispose();
            }
        }
        private async Task Join(Task task)
        {
            try { await task.ConfigureAwait(false); }
            catch (Exception ex) { lock (_gate) _failures.Add(ex); }
        }
    }
}
