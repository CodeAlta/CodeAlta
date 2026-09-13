using CodeAlta.Agent;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

/// <summary>Root-free input mailbox and actual forwarding-lifetime ownership; no provider or tools.</summary>
[TestClass]
public sealed class OwnedSessionUserInputTests
{
    [TestMethod]
    public async Task Capabilities_AreIndependentAndLegacyCommandsRemainUnbound()
    {
        foreach (var review in new[] { false, true })
        foreach (var input in new[] { false, true })
            await Fixture.Run(async f =>
            {
                var execution = await f.Execution(review, input);
                var command = f.Keep(() => f.Owner.CreateOwnedCommandHandler(execution)(f.Command(), default));
                var commands = await f.Keep(() => f.Owner.ListOwnedCommandsAsync("session", default).AsTask());
                Assert.AreEqual(review ? 1 : 0, commands.Entries.Count);
                if (review) Assert.IsTrue(await f.Keep(() => f.Owner.ResolveOwnedCommandAsync(commands.Entries[0].Handle, AgentPermissionDecisionKind.Deny, default).AsTask()));
                Assert.AreEqual(AgentPermissionDecisionKind.Deny, (await command).Kind);
                var unbound = f.Keep(() => f.Owner.CreateOwnedUserInputHandler(execution)(f.Request(), default));
                await Assert.ThrowsAsync<OperationCanceledException>(() => unbound);
                await f.Start(execution);
                var pending = f.Keep(() => f.Owner.CreateOwnedUserInputHandler(execution)(f.Request(), default));
                var page = await f.Page();
                Assert.AreEqual(input ? 1 : 0, page.Entries.Count);
                if (input) Assert.IsTrue(await f.Keep(() => f.Owner.CancelOwnedUserInputAsync(page.Entries[0].Handle, default).AsTask()));
                await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
            });
    }

    [TestMethod]
    public Task ExactAttempt_NullRunImmutableFormAndLiteralAnswer() => Fixture.Run(async f =>
    {
        var execution = await f.Execution(false, true); await f.Start(execution);
        var prompts = new List<AgentUserInputPrompt> { new("q", "Question", AllowFreeform: true) };
        var pending = f.Keep(() => f.Owner.CreateOwnedUserInputHandler(execution)(f.Request() with { Form = new(prompts) }, default));
        var page = await f.Page(); var entry = page.Entries.Single();
        prompts.Clear(); Assert.AreEqual(1, entry.Form.Prompts.Count); Assert.IsNull(entry.Handle.RunId);
        Assert.ThrowsExactly<NotSupportedException>(() => ((IList<AgentUserInputPrompt>)entry.Form.Prompts).Clear());
        Assert.IsFalse(await f.Keep(() => f.Owner.ResolveOwnedUserInputAsync(entry.Handle with { RunId = "run" }, [new("q", "wrong")], default).AsTask()));
        Assert.IsFalse(await f.Keep(() => f.Owner.ResolveOwnedUserInputAsync(entry.Handle, [new("q", "a"), new("q", "b")], default).AsTask()));
        Assert.IsTrue(await f.Keep(() => f.Owner.ResolveOwnedUserInputAsync(entry.Handle, [new("q", "literal\r\n😀  ")], default).AsTask()));
        Assert.AreEqual("literal\r\n😀  ", (await pending).Answers["q"]);
        Assert.IsFalse(await f.Keep(() => f.Owner.CancelOwnedUserInputAsync(entry.Handle, default).AsTask()));
        var next = f.Keep(() => f.Owner.CreateOwnedUserInputHandler(execution)(f.Request(), default));
        var nextHandle = (await f.Page()).Entries.Single().Handle;
        Assert.AreNotEqual(entry.Handle.AttemptId, nextHandle.AttemptId);
        Assert.IsTrue(await f.Keep(() => f.Owner.ResolveOwnedUserInputAsync(nextHandle, [new("q", "")], default).AsTask()));
        Assert.AreEqual("", (await next).Answers["q"]);
    });

    [TestMethod]
    public Task UnsupportedFormsAndWrongBindings_CancelWithoutPublishing() => Fixture.Run(async f =>
    {
        var execution = await f.Execution(false, true); await f.Start(execution);
        var handler = f.Owner.CreateOwnedUserInputHandler(execution);
        foreach (var request in new[] { f.Request() with { SessionId = "wrong" }, f.Request() with { ProviderId = new("wrong") },
            f.Request() with { RunId = new("wrong") }, f.Request() with { Form = new([]) },
            f.Request() with { Form = new([new("q", "secret", IsSecret: true)]) },
            f.Request() with { Form = new([new("q", "no choice", AllowFreeform: false)]) },
            f.Request() with { Form = new([new("q", "a"), new("q", "b")]) },
            f.Request() with { Form = new([new("q", "\ud800")]) },
            f.Request() with { Form = new([new("q", new string('x', 1025))]) } })
        {
            var task = f.Keep(() => handler(request, default));
            await Assert.ThrowsAsync<OperationCanceledException>(() => task);
        }
        Assert.AreEqual(0, (await f.Page()).Entries.Count);
        await f.Keep(() => f.Owner.CloseOwnedExecutionAsync(execution));
        var late = f.Keep(() => handler(f.Request(), default));
        await Assert.ThrowsAsync<OperationCanceledException>(() => late);
    });

    [TestMethod]
    public Task SharedCapacity_AndChoiceValidation() => Fixture.Run(async f =>
    {
        var execution = await f.Execution(true, true); await f.Start(execution);
        var command = f.Keep(() => f.Owner.CreateOwnedCommandHandler(execution)(f.Command(), default));
        var tasks = new List<Task<AgentUserInputResponse>>();
        for (var i = 0; i < 3; i++) tasks.Add(f.Keep(() => f.Owner.CreateOwnedUserInputHandler(execution)(f.Request() with { Form = new([new("q", "choose", Options: [new("Yes"), new("No")], AllowFreeform: false)]) }, default)));
        var refused = f.Keep(() => f.Owner.CreateOwnedUserInputHandler(execution)(f.Request(), default));
        await Assert.ThrowsAsync<OperationCanceledException>(() => refused);
        var page = await f.Page(); Assert.AreEqual(3, page.Entries.Count);
        var handle = page.Entries[0].Handle;
        Assert.IsFalse(await f.Keep(() => f.Owner.ResolveOwnedUserInputAsync(handle, [new("q", "yes")], default).AsTask()));
        Assert.IsTrue(await f.Keep(() => f.Owner.ResolveOwnedUserInputAsync(handle, [new("q", "Yes")], default).AsTask()));
        await f.Keep(() => f.Owner.CloseOwnedExecutionAsync(execution));
        Assert.AreEqual(AgentPermissionDecisionKind.Cancel, (await command).Kind);
        await Task.WhenAll(tasks.Select(async task => { try { await task; } catch (OperationCanceledException) { } }));
        Assert.IsTrue(tasks.All(task => task.IsCompleted));
    });

    [TestMethod]
    public async Task CancellationAndClosure_InvalidateOriginalNotLaterExecution()
    {
        foreach (var origin in new[] { "request", "run", "operation", "attachment", "closing", "dispose" })
            await Fixture.Run(async f =>
            {
                var execution = await f.Execution(false, true); await f.Start(execution);
                var pending = f.Keep(() => f.Owner.CreateOwnedUserInputHandler(execution)(f.Request(), f.RequestCancellation.Token));
                var handle = (await f.Page()).Entries.Single().Handle;
                var close = origin switch
                {
                    "request" => f.Keep(() => f.RequestCancellation.CancelAsync()),
                    "run" => f.Keep(() => f.RunCancellation.CancelAsync()),
                    "operation" => f.Keep(() => f.OperationCancellation.CancelAsync()),
                    "attachment" => f.Keep(() => f.Forwarding.RetireAsync(f.Attachment)),
                    "closing" => f.Keep(() => f.Owner.CreateOwnedRunLifecycle(execution).ClosingAsync(new("run"))),
                    _ => f.Keep(() => f.Owner.DisposeAsync().AsTask()),
                };
                await close;
                await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
                Assert.IsFalse(await f.Keep(() => f.Owner.ResolveOwnedUserInputAsync(handle, [new("q", "late")], default).AsTask()));
            });
    }

    [TestMethod]
    public Task ResolveCancelCompeteOnce_AndOriginalCannotTargetLaterRun() => Fixture.Run(async f =>
    {
        var old = await f.Execution(false, true); await f.Start(old);
        var callback = f.Owner.CreateOwnedUserInputHandler(old);
        var pending = f.Keep(() => callback(f.Request(), default)); var handle = (await f.Page()).Entries.Single().Handle;
        var resolve = f.Keep(() => f.Owner.ResolveOwnedUserInputAsync(handle, [new("q", "literal")], default).AsTask());
        var cancel = f.Keep(() => f.Owner.CancelOwnedUserInputAsync(handle, default).AsTask());
        Assert.IsTrue(await resolve); Assert.IsFalse(await cancel); Assert.AreEqual("literal", (await pending).Answers["q"]);
        var second = f.Keep(() => callback(f.Request() with { RunId = new("run") }, default)); var secondHandle = (await f.Page()).Entries.Single().Handle;
        var cancelFirst = f.Keep(() => f.Owner.CancelOwnedUserInputAsync(secondHandle, default).AsTask());
        var resolveSecond = f.Keep(() => f.Owner.ResolveOwnedUserInputAsync(secondHandle, [new("q", "late")], default).AsTask());
        Assert.IsTrue(await cancelFirst); Assert.IsFalse(await resolveSecond); await Assert.ThrowsAsync<OperationCanceledException>(() => second);
        await f.Keep(() => f.Owner.CloseOwnedExecutionAsync(old));
        var next = await f.Execution(false, true); await f.Start(next);
        var nextTask = f.Keep(() => f.Owner.CreateOwnedUserInputHandler(next)(f.Request(), default)); var nextHandle = (await f.Page()).Entries.Single().Handle;
        var stale = f.Keep(() => callback(f.Request(), default)); await Assert.ThrowsAsync<OperationCanceledException>(() => stale);
        foreach (var forged in new[] { nextHandle with { OperationId = old.OperationId }, nextHandle with { RuntimeInstanceId = Guid.NewGuid() },
            nextHandle with { AttachmentGeneration = nextHandle.AttachmentGeneration + 1 }, nextHandle with { SessionId = "other" },
            nextHandle with { InteractionId = "other" }, nextHandle with { AttemptId = handle.AttemptId } })
            Assert.IsFalse(await f.Keep(() => f.Owner.CancelOwnedUserInputAsync(forged, default).AsTask()));
        Assert.IsTrue(await f.Keep(() => f.Owner.CancelOwnedUserInputAsync(nextHandle, default).AsTask())); await Assert.ThrowsAsync<OperationCanceledException>(() => nextTask);
    });

    [TestMethod]
    public Task GlobalCapacityExecutionLimitAndPaging() => Fixture.Run(async f =>
    {
        var executions = new List<SessionPermissionService.OwnedPermissionExecution>();
        for (var i = 0; i < 64; i++) executions.Add(await f.Execution(false, true));
        Assert.IsNull(await f.Keep(() => f.Owner.CreateOwnedExecutionAsync(Guid.NewGuid(), "session", default, false, true).AsTask()));
        for (var i = 0; i < 32; i++)
        {
            await f.Start(executions[i]);
            for (var j = 0; j < 4; j++) _ = f.Keep(() => f.Owner.CreateOwnedUserInputHandler(executions[i])(f.Request(), default));
        }
        var page = await f.Page(); Assert.AreEqual(4, page.Entries.Count); Assert.IsTrue(page.HasMore);
        await f.Start(executions[32]); var refused = f.Keep(() => f.Owner.CreateOwnedUserInputHandler(executions[32])(f.Request(), default));
        await Assert.ThrowsAsync<OperationCanceledException>(() => refused);
        Assert.AreEqual(4, (await f.Page()).Entries.Count);
    });

    [TestMethod]
    public void FormBoundariesAndDuplicateAnswers_AreCompleteAndOrdinal()
    {
        var accepted = new AgentUserInputForm([new("Q", "Question", Options: [new("YES"), new("yes")], AllowFreeform: false)]);
        var snapshot = OwnedUserInputValidation.Snapshot(accepted); Assert.IsNotNull(snapshot);
        Assert.IsNotNull(OwnedUserInputValidation.Answers(snapshot, [new("Q", "YES")]));
        Assert.IsNull(OwnedUserInputValidation.Answers(snapshot, [new("q", "YES")]));
        Assert.IsNull(OwnedUserInputValidation.Answers(snapshot, [new("Q", "Yes")]));
        foreach (var form in new AgentUserInputForm[] {
            new(Enumerable.Range(0, 9).Select(i => new AgentUserInputPrompt(i.ToString(), "question")).ToArray()),
            new([new("q", "question", Options: Enumerable.Range(0, 9).Select(i => new AgentUserInputOption(i.ToString())).ToArray())]),
            new([new("q", "question", Options: [new("a"), new("a")])]), new([new("q", "question", Options: [new(" ")])]),
            new([new(new string('x', 129), "question")]), new([new("q", "question", Header: new string('x', 129))]),
            new([new("q", "question", Options: [new(new string('x', 257))])]),
            new([new("q", "question", Options: [new("a", new string('x', 513))])]),
            new(Enumerable.Range(0, 8).Select(i => new AgentUserInputPrompt(i.ToString(), new string('x', 1024))).ToArray()) })
            Assert.IsNull(OwnedUserInputValidation.Snapshot(form));
        var freeform = OwnedUserInputValidation.Snapshot(new([new("q", "question")]))!;
        Assert.IsNotNull(OwnedUserInputValidation.Answers(freeform, [new("q", new string('x', 2048))]));
        Assert.IsNull(OwnedUserInputValidation.Answers(freeform, [new("q", new string('x', 2049))]));
        Assert.IsNull(OwnedUserInputValidation.Answers(freeform, [new("q", "\ud800")]));
    }

    [TestMethod]
    public void MutableCollectionBounds_AreSampledOnceBeforeCopying()
    {
        var options = new List<AgentUserInputOption> { new("choice", "description") };
        var source = new OnceCount<AgentUserInputPrompt>([new("q", "Question", Options: new OnceCount<AgentUserInputOption>(options))]);
        var snapshot = OwnedUserInputValidation.Snapshot(new(source)); Assert.IsNotNull(snapshot);
        options.Clear(); Assert.AreEqual("choice", snapshot.Prompts[0].Options![0].Label);
        Assert.IsNotNull(OwnedUserInputValidation.Answers(snapshot, new OnceCount<SessionOwnedUserInputAnswer>([new("q", "literal")])));
    }

    private sealed class OnceCount<T>(IReadOnlyList<T> source) : IReadOnlyList<T>
    {
        private bool _read;
        public int Count { get { if (_read) throw new AssertFailedException("Count resampled before allocation."); _read = true; return source.Count; } }
        public T this[int index] => source[index];
        public IEnumerator<T> GetEnumerator() => throw new AssertFailedException("Unbounded enumeration forbidden.");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [TestMethod]
    public async Task RetirementAndDisposal_WithExternalRunCancellationTraversalHeld()
    {
        // This gate holds the fixture's external run-token callback, NOT DeliverInputAsync's
        // internal registration-disposal continuation. No claim about that continuation is made.
        foreach (var dispose in new[] { false, true }) await Fixture.Run(async f =>
        {
            var execution = await f.Execution(false, true); await f.Start(execution);
            var pending = f.Keep(() => f.Owner.CreateOwnedUserInputHandler(execution)(f.Request(), default));
            var handle = (await f.Page()).Entries.Single().Handle;
            var gate = f.GateRunCancellation();
            var traversal = f.Keep(() => f.RunCancellation.CancelAsync());
            await gate.Entered.Task;
            Assert.IsFalse(traversal.IsCompleted);
            var closing = dispose ? f.Keep(() => f.Owner.DisposeAsync().AsTask()) : f.Keep(() => f.Forwarding.RetireAsync(f.Attachment));
            Assert.IsFalse(await f.Keep(() => f.Owner.ResolveOwnedUserInputAsync(handle, [new("q", "late")], default).AsTask()));
            gate.Release.Set(); await traversal; await closing;
            await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        });
    }

    private sealed class Fixture
    {
        internal SessionPermissionService Owner { get; } = new();
        internal OwnedProviderEventForwarding Forwarding { get; } = new();
        private OwnedProviderEventForwarding.Attachment? _attachment;
        internal OwnedProviderEventForwarding.Attachment Attachment => _attachment ?? throw new InvalidOperationException("Setup not published.");
        internal CancellationTokenSource RunCancellation { get; } = new();
        internal CancellationTokenSource OperationCancellation { get; } = new();
        internal CancellationTokenSource RequestCancellation { get; } = new();
        private readonly List<Task> _work = [];
        private readonly List<CancellationGate> _gates = [];
        private readonly TaskCompletionSource _stopLaunch = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _setupSettled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task _stop = Task.CompletedTask, _stopObserver = Task.CompletedTask;
        private Task? _lifetime, _observer;
        private readonly Guid _runtime = Guid.NewGuid();
        private Fixture() { }
        private void Setup()
        {
            var attachment = Forwarding.RegisterAttachment("session", "inert", static () => Task.CompletedTask, static () => Task.CompletedTask);
            try
            {
                attachment.CloseOwnedPermissions = () => Owner.InvalidateOwnedAttachmentAsync(attachment);
                _attachment = attachment;
            }
            finally { attachment.CompleteSetup(); }
        }
        internal CancellationGate GateRunCancellation()
        {
            var gate = new CancellationGate();
            lock (_gates) { _gates.Add(gate); if (_stopLaunch.Task.IsCompleted) gate.Release.Set(); }
            gate.Registration = RunCancellation.Token.UnsafeRegister(static state =>
            { var value = (CancellationGate)state!; value.Entered.TrySetResult(); value.Release.Wait(); }, gate);
            return gate;
        }
        internal Task Keep(Func<Task> start) => Keep(async () => { await start(); return true; });
        internal Task<T> Keep<T>(Func<Task<T>> start)
        {
            var acquisition = new TaskCompletionSource<Task<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var observer = Observe(acquisition.Task); _work.Add(observer);
            try { acquisition.SetResult(start()); } catch (Exception ex) { acquisition.SetException(ex); }
            return observer;
        }
        private static async Task<T> Observe<T>(Task<Task<T>> task) => await await task;
        internal AgentUserInputRequest Request() => new(new("inert"), "session", DateTimeOffset.UnixEpoch, null, "interaction", new([new("q", "Question")]));
        internal AgentCommandPermissionRequest Command() => new(new("inert"), "session", DateTimeOffset.UnixEpoch, null, "command", null, "not executed", "inert", null, "fixture", null, null, null);
        internal Task<SessionOwnedUserInputPage> Page() => Keep(() => Owner.ListOwnedUserInputsAsync("session", default).AsTask());
        internal async Task<SessionPermissionService.OwnedPermissionExecution> Execution(bool review, bool input)
        {
            var execution = await Keep(() => Owner.CreateOwnedExecutionAsync(Guid.NewGuid(), "session", OperationCancellation.Token, review, input).AsTask());
            Assert.IsNotNull(execution);
            Assert.IsTrue(await Keep(() => Owner.BindOwnedExecutionAsync(execution, _runtime, Attachment, new("inert")).AsTask()));
            return execution;
        }
        internal Task Start(SessionPermissionService.OwnedPermissionExecution execution) => Keep(() => Owner.CreateOwnedRunLifecycle(execution).StartedAsync(new("run"), RunCancellation.Token));
        internal static async Task Run(Func<Fixture, Task> body)
        {
            var f = new Fixture(); var launch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f._stop = f.Stop(); f._stopObserver = Settle(f._stop);
            f._lifetime = f.Core(body, launch.Task); f._observer = Settle(f._lifetime); launch.SetResult();
            try
            {
                await f._lifetime.WaitAsync(TimeSpan.FromSeconds(15)); await f._observer;
                foreach (var gate in f._gates) { await gate.Registration.DisposeAsync(); gate.Release.Dispose(); }
                f.RunCancellation.Dispose(); f.OperationCancellation.Dispose(); f.RequestCancellation.Dispose();
            }
            catch (Exception ex) { ex.Data["RetainedFixture"] = f; throw; }
            finally { f._stopLaunch.TrySetResult(); }
        }
        private async Task Core(Func<Fixture, Task> body, Task launch)
        {
            await launch; Exception? primary = null;
            try { Setup(); _setupSettled.TrySetResult(); await Keep(() => body(this)); }
            catch (Exception ex) { primary = ex; throw; }
            finally
            {
                _setupSettled.TrySetResult(); _stopLaunch.TrySetResult(); await Task.WhenAll(_work.Select(Settle));
                await _stopObserver;
                var failures = _work.Append(_stop).Where(t => t.Exception is not null).SelectMany(t => t.Exception!.Flatten().InnerExceptions).Where(e => e is not OperationCanceledException).Distinct().ToList();
                if (primary is not null && !failures.Contains(primary)) failures.Insert(0, primary);
                if (failures.Count > 0) throw new AggregateException("Mailbox fixture originals or cleanup failed.", failures);
            }
        }
        private async Task Stop()
        {
            await _stopLaunch.Task;
            lock (_gates) foreach (var gate in _gates) gate.Release.Set();
            var a = RunCancellation.CancelAsync(); var b = OperationCancellation.CancelAsync(); var c = RequestCancellation.CancelAsync();
            var close = Owner.DisposeAsync().AsTask(); var forwarding = CloseForwarding();
            var all = Task.WhenAll(a, b, c, close, forwarding);
            try { await all; } catch { throw all.Exception ?? new AggregateException(new TaskCanceledException(all)); }
        }
        private async Task CloseForwarding()
        {
            await _setupSettled.Task;
            await Forwarding.CloseAsync(() => Owner.DisposeAsync().AsTask(), static () => Task.CompletedTask, static () => { });
        }
        private static async Task Settle(Task task) { try { await task; } catch { /* Original result remains retained and asserted. */ } }
        internal sealed class CancellationGate
        {
            internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal ManualResetEventSlim Release { get; } = new();
            internal CancellationTokenRegistration Registration;
        }
    }
}
