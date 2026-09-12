using System.Collections.Frozen;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Desktop.Tests;

/// <summary>Source-audited, explicit-root queue RPC fixtures; no real provider or native execution.</summary>
[TestClass]
public sealed class SessionQueueRpcTests
{
    [TestMethod]
    public void RoutesRejectBeforeAdmission()
    {
        var calls = 0;
        var rpc = new SessionOperationsService("epoch", _ => throw new AssertFailedException(), _ => throw new AssertFailedException(),
            _ => throw new AssertFailedException(), _ => throw new AssertFailedException(), _ => throw new AssertFailedException(),
            _ => { calls++; throw new InvalidOperationException("private fixture detail"); },
            _ => { calls++; throw new InvalidOperationException("private fixture detail"); });
        var request = new SessionQueueRequest("epoch", "key", "session", "abcdefab-1234-5678-9abc-abcdefabcdef", "1", " exact \n");
        var cancel = new SessionCancelQueueRequest("epoch", "cancel", request.ExpectedRuntimeInstanceId);
        Assert.AreEqual("unconfigured", new SessionOperationsService().Queue(request, default).Status);
        Assert.AreEqual("unconfigured", new SessionOperationsService().CancelQueue(cancel, default).Status);
        Assert.AreEqual("stale_epoch", rpc.Queue(request with { ExpectedEpoch = "old" }, default).Status);
        Assert.AreEqual("stale_epoch", rpc.CancelQueue(cancel with { ExpectedEpoch = "old" }, default).Status);
        foreach (var invalid in new[] { request with { ClientRequestId = " padded " }, request with { SessionId = "\ud800" },
            request with { ClientRequestId = new string('x', 257) }, request with { SessionId = new string('x', 257) },
            request with { Text = " " }, request with { Text = "\ud800" }, request with { Text = new string('x', 32769) },
            request with { ExpectedRuntimeInstanceId = request.ExpectedRuntimeInstanceId.ToUpperInvariant() },
            request with { ExpectedRuntimeInstanceId = Guid.Empty.ToString("D") }, request with { ExpectedAttachmentGeneration = "0" },
            request with { ExpectedAttachmentGeneration = "01" }, request with { ExpectedAttachmentGeneration = "+1" },
            request with { ExpectedAttachmentGeneration = "9223372036854775808" } })
            Assert.AreEqual("invalid_request", rpc.Queue(invalid, default).Status);
        foreach (var invalid in new[] { cancel with { ClientRequestId = " padded " }, cancel with { TargetOperationId = "bad" },
            cancel with { TargetOperationId = cancel.TargetOperationId.ToUpperInvariant() }, cancel with { TargetOperationId = Guid.Empty.ToString("D") } })
            Assert.AreEqual("invalid_request", rpc.CancelQueue(invalid, default).Status);
        Assert.ThrowsExactly<OperationCanceledException>(() => rpc.Queue(request, new CancellationToken(true)));
        Assert.ThrowsExactly<OperationCanceledException>(() => rpc.CancelQueue(cancel, new CancellationToken(true)));
        Assert.AreEqual(0, calls);
        Assert.AreEqual(new SessionAdmission("admission_failed", "epoch", null), rpc.Queue(request, default));
        Assert.AreEqual(new SessionAdmission("admission_failed", "epoch", null), rpc.CancelQueue(cancel, default));
        Assert.AreEqual(2, calls);
        rpc.CloseAdmission();
        Assert.AreEqual("closed", rpc.Queue(request, default).Status);
        Assert.AreEqual("closed", rpc.CancelQueue(cancel, default).Status);
        Assert.AreEqual(2, calls);
    }

    [TestMethod]
    public void WireBoundsAndPhaseValidation()
    {
        var escaped = new string('\u0001', 256);
        var code = new string('\u0001', 64);
        var id = "abcdefab-1234-5678-9abc-abcdefabcdef";
        var pending = new SessionReceiptView(escaped, escaped, id, null, "Queue", "pending", null, null, null)
            { QueueInsertion = new("pending", null, null) };
        var inserted = pending with { QueueInsertion = new("terminal", true, "queue_accepted") };
        var refused = pending with { QueueInsertion = new("terminal", false, code) };
        var complete = inserted with { State = "terminal", Outcome = "Completed", Code = "queue_dispatched", RunId = escaped };
        var failed = refused with { State = "terminal", Outcome = "Failed", Code = "queue_cleanup_failed" };
        var cancellation = pending with { Kind = "CancelQueue", TargetOperationId = "abcdefab-1234-5678-9abc-abcdefabcdee", QueueInsertion = null };
        foreach (var valid in new[] { pending, inserted, refused, complete, failed, cancellation,
            cancellation with { State = "terminal", Outcome = "Completed", Code = "queue_cancellation_signalled" },
            cancellation with { State = "terminal", Outcome = "Failed", Code = "queue_cancel_failed" } })
            Assert.AreEqual("ok", SessionOperationsService.ProjectPage("epoch", [valid], null).Status);
        foreach (var invalid in new[] { pending with { QueueInsertion = null }, pending with { State = "terminal", Outcome = "Failed", Code = "queue_failed" },
            pending with { QueueInsertion = new("pending", false, null) }, pending with { QueueInsertion = new("terminal", true, "queue_failed") },
            pending with { QueueInsertion = new("terminal", false, new string('x', 65)) }, complete with { Code = "queue_accepted" },
            cancellation with { QueueInsertion = new("pending", null, null) }, cancellation with { TargetOperationId = null },
            cancellation with { TargetOperationId = id }, cancellation with { State = "terminal", Outcome = "Cancelled", Code = "queue_cancelled" },
            pending with { TargetOperationId = id }, pending with { Outcome = "Failed" }, pending with { Code = "queue_failed" } })
            Assert.AreEqual("wire_limit", SessionOperationsService.ProjectPage("epoch", [invalid], null).Status);
        // Actual source-generated serialization, including escaped maxima and a separate framing allowance.
        foreach (var maximum in new[] { complete, refused with { State = "terminal", Outcome = "Failed", Code = code },
            cancellation with { State = "terminal", Outcome = "Failed", Code = code } })
        {
            var page = SessionOperationsService.ProjectPage(code, Enumerable.Repeat(maximum, 64).ToArray(), 64);
            Assert.AreEqual("ok", page.Status);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(page, DesktopJsonContext.Default.SessionReceiptPage);
            Assert.IsTrue(bytes.Length + 8192 <= 448 * 1024);
            Assert.IsTrue(JsonSerializer.SerializeToUtf8Bytes(maximum, DesktopJsonContext.Default.SessionReceiptView).Length <= 6400);
        }
        var request = new SessionQueueRequest(code, escaped, escaped, id, long.MaxValue.ToString(CultureInfo.InvariantCulture), new string('\u0001', 32768));
        var inbound = JsonSerializer.SerializeToUtf8Bytes(request, DesktopJsonContext.Default.SessionQueueRequest);
        Assert.IsTrue(inbound.Length + 8192 <= 208 * 1024);
        Assert.AreEqual(request, JsonSerializer.Deserialize(inbound, DesktopJsonContext.Default.SessionQueueRequest));
        var cancel = new SessionCancelQueueRequest(code, escaped, id);
        Assert.IsTrue(JsonSerializer.SerializeToUtf8Bytes(cancel, DesktopJsonContext.Default.SessionCancelQueueRequest).Length + 8192 <= 208 * 1024);
    }

    [TestMethod]
    public Task ReservationInsertionDispatchAreSeparate() => Fixture.Run(async f =>
    {
        var target = await f.Prepare();
        await f.State(AgentSessionUpdateKind.Info, new("earlier"));
        var held = f.HoldTools();
        var ensure = f.Keep(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.Options(held)));
        await f.Ready(held.Entered.Task);
        var request = f.Request(target);
        var receipt = f.Accept(request);
        var reserved = f.Row(receipt);
        Assert.AreEqual(new SessionQueueInsertionView("pending", null, null), reserved.QueueInsertion);
        Assert.AreEqual("pending", reserved.State);
        held.Release.TrySetResult();
        await f.Wait(ensure); await f.Wait(receipt.QueueInsertion!);
        Assert.AreEqual(new SessionQueueInsertionView("terminal", true, "queue_accepted"), f.Row(receipt).QueueInsertion);
        Assert.AreEqual("pending", f.Row(receipt).State);
        var journal = await f.Wait(f.Journal.ReadLatestStateAsync(f.Session.SessionId, f.Session.CreatedAt));
        Assert.IsNotNull(journal); Assert.IsFalse(journal.QueuedPrompts.Any(value => value.Prompt == request.Text));
        var script = f.AddScript();
        await f.State(AgentSessionUpdateKind.Idle); await f.Ready(script.Started.Task);
        Assert.IsFalse(receipt.Completion.IsCompleted);
        Assert.AreEqual(AgentInput.Text(request.Text).Items[0], script.Options!.Input.Items[0]);
        script.Release.TrySetResult(); await f.Wait(receipt.Completion);
        Assert.AreEqual("queue_dispatched", f.Row(receipt).Code);
        Assert.AreEqual("Completed", f.Row(receipt).Outcome);
        Assert.IsTrue(script.RegistrationDisposed && script.ForwardingDisposed);
    });

    [TestMethod]
    public async Task RefusedInsertionSettlesBothResults()
    {
        await Fixture.Run(async f =>
        {
            var target = await f.Prepare();
            var receipt = f.Accept(f.Request(target) with { ExpectedAttachmentGeneration = "9223372036854775807" });
            await f.Wait(receipt.QueueInsertion!); await f.Wait(receipt.Completion);
            Assert.AreEqual(new SessionQueueInsertionView("terminal", false, "queue_target_unavailable"), f.Row(receipt).QueueInsertion);
            Assert.AreEqual("queue_target_unavailable", f.Row(receipt).Code);
            Assert.AreEqual(0, f.Provider.Sends);
        });
        await Fixture.Run(async f =>
        {
            var target = await f.Prepare(); var script = f.AddScript();
            script.Failure = new OperationCanceledException("private unsolicited provider failure");
            var request = f.Request(target); var receipt = f.Accept(request);
            await f.Wait(receipt.QueueInsertion!); await f.Ready(script.Started.Task);
            script.Release.TrySetResult(); await f.Wait(receipt.Completion);
            Assert.AreEqual("Failed", f.Row(receipt).Outcome); Assert.AreEqual("queue_failed", f.Row(receipt).Code);
            Assert.AreEqual(true, f.Row(receipt).QueueInsertion!.Accepted);
            Assert.AreEqual("replay", f.Rpc.Queue(request, default).Status); Assert.AreEqual(1, f.Provider.Sends);
        });
    }

    [TestMethod]
    public Task ReplayConflictCapacityAndClosure() => Fixture.Run(async f =>
    {
        var target = await f.Prepare(); await f.State(AgentSessionUpdateKind.Info, new("earlier"));
        var request = f.Request(target); var receipt = f.Accept(request); await f.Wait(receipt.QueueInsertion!);
        Assert.AreEqual(receipt.OperationId.ToString("D"), f.Rpc.Queue(request, default).Receipt!.OperationId);
        Assert.AreEqual("replay", f.Rpc.Queue(request, default).Status);
        Assert.AreEqual("conflict", f.Rpc.Queue(request with { Text = "changed" }, default).Status);
        Assert.AreEqual("conflict", f.Rpc.Send(new("epoch", request.ClientRequestId, request.SessionId, "text"), default).Status);
        Assert.AreEqual("busy", f.Rpc.Queue(request with { ClientRequestId = "busy" }, default).Status);
        var cancel = f.Cancel(receipt, "cancel"); await f.Wait(cancel.Completion); await f.Wait(receipt.Completion);
        Assert.AreEqual("capacity", f.Rpc.Queue(request with { ClientRequestId = "full" }, default).Status);
        await f.Wait(f.Commands.DisposeAsync().AsTask());
        Assert.AreEqual("replay", f.Rpc.Queue(request, default).Status);
        Assert.AreEqual("closed", f.Rpc.Queue(request with { ClientRequestId = "closed" }, default).Status);
        f.Rpc.CloseAdmission(); Assert.AreEqual("closed", f.Rpc.Queue(request, default).Status);
        Assert.AreEqual(2, f.Rpc.Receipts(new("epoch", 0)).Rows.Length);
    }, capacity: 2);

    [TestMethod]
    public Task WaitingCancelNeverTargetsAnotherOperation() => Fixture.Run(async f =>
    {
        var target = await f.Prepare(); await f.State(AgentSessionUpdateKind.Info, new("unrelated"));
        var first = f.Accept(f.Request(target)); await f.Wait(first.QueueInsertion!);
        Assert.AreEqual("unknowntarget", f.Rpc.CancelQueue(new("epoch", "unknown", Guid.NewGuid().ToString("D")), default).Status);
        var cancel = f.Cancel(first, "cancel"); await f.Wait(cancel.Completion); await f.Wait(first.Completion);
        Assert.AreEqual(first.OperationId.ToString("D"), f.Row(cancel).TargetOperationId);
        Assert.IsNull(f.Row(cancel).QueueInsertion); Assert.AreEqual("queue_cancelled", f.Row(first).Code);
        var second = f.Accept(f.Request(target)); await f.Wait(second.QueueInsertion!);
        Assert.AreEqual("replay", f.Rpc.CancelQueue(new("epoch", "cancel", first.OperationId.ToString("D")), default).Status);
        Assert.AreEqual("conflict", f.Rpc.CancelQueue(new("epoch", "cancel", second.OperationId.ToString("D")), default).Status);
        Assert.IsFalse(second.Completion.IsCompleted);
        Assert.AreEqual("unrelated", (await f.Wait(f.Runtime.GetCurrentStateAsync(f.Session.SessionId))).Entry!.ActiveRunId);
        Assert.AreEqual(0, f.Provider.Aborts); Assert.AreEqual(0, f.Provider.Sends);
        await f.Wait(f.Cancel(second, "second-cancel").Completion);
    });

    [TestMethod]
    public Task ClaimedCancelRetainsReviewAndTraversal() => Fixture.Run(async f =>
    {
        var target = await f.Prepare(); var script = f.AddScript(holdCancellation: true);
        var receipt = f.Accept(f.Request(target)); await f.Wait(receipt.QueueInsertion!); await f.Ready(script.Started.Task);
        var handler = script.Options!.OnPermissionRequest!;
        var accepted = f.Keep(handler(f.Permission("accepted", script.RunId), CancellationToken.None));
        var page = await f.Wait(f.Runtime.Permissions.ListOwnedCommandsAsync(f.Session.SessionId, default).AsTask());
        var handle = page.Entries.Single().Handle; Assert.AreEqual(receipt.OperationId, handle.OperationId);
        Assert.IsTrue(await f.Wait(f.Runtime.Permissions.ResolveOwnedCommandAsync(handle, AgentPermissionDecisionKind.AllowOnce, default).AsTask()));
        Assert.AreEqual(AgentPermissionDecisionKind.AllowOnce, (await f.Wait(accepted)).Kind);
        var pending = f.Keep(handler(f.Permission("pending", null), CancellationToken.None));
        var pendingPage = await f.Wait(f.Runtime.Permissions.ListOwnedCommandsAsync(f.Session.SessionId, default).AsTask());
        var pendingHandle = pendingPage.Entries.Single().Handle;
        var cancel = f.Cancel(receipt, "cancel"); await f.Ready(script.CancellationEntered.Task);
        Assert.IsFalse(receipt.Completion.IsCompleted); Assert.IsFalse(cancel.Completion.IsCompleted);
        Assert.IsFalse(await f.Wait(f.Runtime.Permissions.ResolveOwnedCommandAsync(pendingHandle, AgentPermissionDecisionKind.AllowOnce, default).AsTask()));
        Assert.AreEqual(AgentPermissionDecisionKind.Cancel, (await f.Wait(pending)).Kind);
        Assert.AreEqual(AgentPermissionDecisionKind.AllowOnce, (await f.Wait(accepted)).Kind);
        script.Release.TrySetResult(); Assert.IsFalse(receipt.Completion.IsCompleted);
        script.ReleaseCancellation.TrySetResult(); await f.Wait(receipt.Completion); await f.Wait(cancel.Completion);
        Assert.AreEqual("queue_cancellation_signalled", f.Row(cancel).Code);
        Assert.IsTrue(script.RegistrationDisposed && script.ForwardingDisposed); Assert.AreEqual(0, f.Provider.Aborts);
        var terminal = f.Cancel(receipt, "terminal"); await f.Wait(terminal.Completion);
        Assert.AreEqual("already_terminal", f.Row(terminal).Code);
    }, review: true);

    [TestMethod]
    public Task CallerWaitAndShutdownRetainOriginalWork() => Fixture.Run(async f =>
    {
        var target = await f.Prepare(); var script = f.AddScript(holdCancellation: true);
        var receipt = f.Accept(f.Request(target)); await f.Wait(receipt.QueueInsertion!); await f.Ready(script.Started.Task);
        var caller = f.Source(); await f.Wait(caller.CancelAsync());
        var wait = f.Keep(receipt.Completion.WaitAsync(caller.Token)); await f.Cancelled(wait);
        Assert.IsFalse(script.Token.IsCancellationRequested);
        var close = f.Keep(f.Commands.DisposeAsync().AsTask()); await f.Ready(script.CancellationEntered.Task);
        Assert.IsFalse(close.IsCompleted); Assert.IsFalse(receipt.Completion.IsCompleted);
        script.Release.TrySetResult(); script.ReleaseCancellation.TrySetResult();
        await f.Wait(receipt.Completion); await f.Wait(close);
        Assert.IsTrue(script.RegistrationDisposed && script.ForwardingDisposed); Assert.AreEqual(0, f.Provider.EarlyDisposals);
    });

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class HeldTools : IReadOnlyList<AgentToolDefinition>
    {
        internal TaskCompletionSource Entered { get; } = Gate();
        internal TaskCompletionSource Release { get; } = Gate();
        public int Count { get { Entered.TrySetResult(); Release.Task.GetAwaiter().GetResult(); return 0; } }
        public AgentToolDefinition this[int index] => throw new ArgumentOutOfRangeException(nameof(index));
        public IEnumerator<AgentToolDefinition> GetEnumerator() => Enumerable.Empty<AgentToolDefinition>().GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    // Dedicated queue lifetime adapted from SessionOwnedQueueTests, not a provider-wide release gate.
    private sealed class Fixture
    {
        private readonly object _gate = new();
        private readonly List<Task> _work = [];
        private readonly HashSet<Task> _expectedCancellation = [];
        private readonly List<Exception> _failures = [];
        private readonly List<CancellationTokenSource> _sources = [];
        private readonly List<HeldTools> _tools = [];
        private readonly string _root = Path.Combine(Path.GetTempPath(), "CodeAlta-desktop-queue-" + Guid.NewGuid().ToString("N"));
        private Task? _lifetime;
        private CodeAltaHost? _host;
        private Task? _hostClose;
        private bool _cleaning;
        private readonly TaskCompletionSource _cleanupStarted = Gate();
        private readonly SessionInstructionProcessor _instructions = static (_, _) => ValueTask.FromResult(new SessionInstructionProcessingResult
        {
            SystemMessage = "Inert queue fixture instructions", DeveloperInstructions = "No tools or native execution",
            Transformations = [new() { PluginRuntimeKey = "fixture", RuntimeContributionKey = "fixture", Stage = "fixture", Disposition = "unchanged" }],
        });
        internal Provider Provider { get; }
        private Fixture() { Provider = new(task => Keep(task)); }
        internal SessionViewDescriptor Session { get; private set; } = null!;
        internal SessionViewJournalStore Journal { get; private set; } = null!;
        internal SessionOperationsService Rpc { get; private set; } = null!;
        internal SessionRuntimeService Runtime => _host!.RuntimeService;
        internal OwnedSessionCommandService Commands => _host!.Commands;
        internal static async Task Run(Func<Fixture, Task> body, int capacity = 256, bool review = false)
        {
            var f = new Fixture(); f._lifetime = f.RunOwned(body, capacity, review);
            try { await f._lifetime.WaitAsync(TimeSpan.FromSeconds(40)); }
            catch (Exception ex) { lock (f._gate) f._failures.Add(ex); f.BeginCleanup(); }
            Exception[] failures; lock (f._gate) failures = [.. f._failures];
            if (failures.Length != 0)
            {
                var error = new AggregateException("Queue RPC fixture/root retained at " + f._root, failures);
                error.Data["RetainedFixture"] = f; throw error;
            }
        }
        private async Task RunOwned(Func<Fixture, Task> body, int capacity, bool review)
        {
            try
            {
                await Keep(Setup(capacity, review));
                bool run; lock (_gate) run = !_cleaning;
                if (run) await Keep(body(this));
            }
            catch (Exception ex) { lock (_gate) _failures.Add(ex); }
            finally
            {
                BeginCleanup(); await _cleanupStarted.Task;
                Task[] tasks; lock (_gate) tasks = [.. _work];
                await Task.WhenAll(tasks.Distinct().Select(Join));
                Task[] late; lock (_gate) late = _work.Except(tasks).ToArray();
                await Task.WhenAll(late.Select(Join));
                CancellationTokenSource[] sources; lock (_gate) sources = [.. _sources];
                foreach (var source in sources) source.Dispose();
                // Root is intentionally retained even on success. A timeout never proves termination.
            }
        }
        private void BeginCleanup()
        {
            HeldTools[] tools; CancellationTokenSource[] sources; bool close;
            lock (_gate)
            {
                if (_cleaning) return;
                _cleaning = true; tools = [.. _tools]; sources = [.. _sources]; close = _host is not null;
            }
            try
            {
                foreach (var hold in tools) hold.Release.TrySetResult();
                Provider.ReleaseAll();
                foreach (var source in sources) Initiate(source.CancelAsync);
                if (close) Initiate(CloseHost);
            }
            finally { _cleanupStarted.TrySetResult(); }
        }
        private Task CloseHost()
        {
            var launch = Gate(); Task task;
            lock (_gate)
            {
                if (_hostClose is not null) return _hostClose;
                var host = _host!; task = Keep(Dispose()); _hostClose = task;
                async Task Dispose() { await launch.Task; await host.DisposeAsync(); }
            }
            launch.TrySetResult(); return task;
        }
        private void CheckOpen() { lock (_gate) if (_cleaning) throw new InvalidOperationException("Fixture is closing."); }
        private async Task Join(Task task)
        {
            try { await task.ConfigureAwait(false); }
            catch (OperationCanceledException) when (task.IsCanceled && _expectedCancellation.Contains(task)) { }
            catch (Exception ex) { lock (_gate) _failures.Add(ex); }
        }
        private void Initiate(Func<Task> start) { try { _ = Keep(start()); } catch (Exception ex) { _ = Keep(Task.FromException(ex)); } }
        internal Task Keep(Task task) { lock (_gate) _work.Add(task); return task; }
        internal Task<T> Keep<T>(Task<T> task) { Keep((Task)task); return task; }
        internal Task Wait(Task task) => Keep(Keep(task).WaitAsync(TimeSpan.FromSeconds(5)));
        internal Task<T> Wait<T>(Task<T> task) => Keep(Keep(task).WaitAsync(TimeSpan.FromSeconds(5)));
        internal Task Ready(Task task) => Keep(task.WaitAsync(TimeSpan.FromSeconds(5)));
        internal async Task Cancelled(Task task) { await Assert.ThrowsAsync<OperationCanceledException>(() => task); _expectedCancellation.Add(task); }
        internal CancellationTokenSource Source() { lock (_gate) { CheckOpen(); var source = new CancellationTokenSource(); _sources.Add(source); return source; } }
        internal HeldTools HoldTools() { lock (_gate) { CheckOpen(); var tools = new HeldTools(); _tools.Add(tools); return tools; } }
        internal Script AddScript(bool holdCancellation = false) { CheckOpen(); return Provider.Add(holdCancellation); }
        internal SessionExecutionOptions Options(IReadOnlyList<AgentToolDefinition>? tools = null) => new()
        {
            ProviderId = Provider.Descriptor.ProviderId, ProviderKey = Provider.Descriptor.ProviderId.Value,
            WorkingDirectory = Session.WorkingDirectory, ProjectRoots = [Session.WorkingDirectory!], Model = "fixture-model", Tools = tools,
            InstructionProcessor = _instructions,
            OnPermissionRequest = Runtime.Permissions.OwnedDefaultPermissionHandler, OnUserInputRequest = Runtime.Permissions.OwnedDefaultUserInputHandler,
        };
        internal async Task<SessionRuntimeCurrentState> Prepare()
        {
            await Wait(Runtime.EnsureCoordinatorSessionAsync(Session, Options())); return await Wait(Runtime.GetCurrentStateAsync(Session.SessionId));
        }
        internal SessionQueueRequest Request(SessionRuntimeCurrentState state) => new("epoch", Guid.NewGuid().ToString("N"), Session.SessionId,
            state.RuntimeInstanceId.ToString("D"), (state.Entry?.AttachmentGeneration ?? 1).ToString(CultureInfo.InvariantCulture), " exact volatile text \n");
        internal OwnedSessionCommandReceipt Accept(SessionQueueRequest request)
        {
            CheckOpen(); var admission = Rpc.Queue(request, default);
            // Real-owner exact replay only; retained before assertions so failed adapter assertions cannot lose work.
            var replay = Commands.AdmitQueue(new(request.ClientRequestId, request.SessionId, Guid.ParseExact(request.ExpectedRuntimeInstanceId, "D"),
                long.Parse(request.ExpectedAttachmentGeneration, CultureInfo.InvariantCulture), request.Text));
            if (replay.Receipt is { } retained) { _ = Keep(retained.Completion); if (retained.QueueInsertion is { } insertion) _ = Keep(insertion); }
            Assert.AreEqual("accepted", admission.Status); Assert.AreEqual(OwnedSessionCommandAdmissionKind.Replay, replay.Kind);
            Assert.AreEqual(replay.Receipt!.OperationId.ToString("D"), admission.Receipt!.OperationId); return replay.Receipt;
        }
        internal OwnedSessionCommandReceipt Cancel(OwnedSessionCommandReceipt target, string key)
        {
            CheckOpen(); var admission = Rpc.CancelQueue(new("epoch", key, target.OperationId.ToString("D")), default);
            var replay = Commands.AdmitCancelQueue(new(key, target.OperationId));
            if (replay.Receipt is { } retained) _ = Keep(retained.Completion);
            Assert.AreEqual("accepted", admission.Status); Assert.AreEqual(OwnedSessionCommandAdmissionKind.Replay, replay.Kind); return replay.Receipt!;
        }
        internal SessionReceiptView Row(OwnedSessionCommandReceipt receipt)
        {
            var page = Rpc.Receipts(new("epoch", 0)); Assert.AreEqual("ok", page.Status);
            return page.Rows.Single(value => value.OperationId == receipt.OperationId.ToString("D"));
        }
        internal AgentCommandPermissionRequest Permission(string id, AgentRunId? run) => new(Provider.Descriptor.ProviderId, Session.SessionId,
            DateTimeOffset.UtcNow, run, id, null, "inert text", Session.WorkingDirectory!, null, "fixture", null, null, null);
        internal async Task State(AgentSessionUpdateKind kind, AgentRunId? run = null)
        {
            CheckOpen(); var source = Source(); var marker = Guid.NewGuid().ToString("N"); var observation = Keep(Observe());
            try { Provider.Latest.Emit(kind, run, marker); await Wait(observation); }
            finally { var cancel = Keep(source.CancelAsync()); await Keep(Task.WhenAll(observation, cancel)); }
            async Task Observe()
            {
                await foreach (var item in Runtime.StreamEventsAsync(source.Token))
                    if (item is SessionAgentEvent { Event: AgentSessionUpdateEvent update } && update.Message == marker) return;
                Assert.Fail("Missing uniquely tagged committed state.");
            }
        }
        private async Task Setup(int capacity, bool review)
        {
            for (var node = new DirectoryInfo(Path.GetDirectoryName(_root)!); node is not null; node = node.Parent)
                if ((node.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Reparse fixture ancestry.");
            if (Directory.Exists(_root) || File.Exists(_root)) throw new IOException("Fixture root already exists.");
            var home = Path.Combine(_root, "home"); var global = Path.Combine(_root, "global");
            var project = Path.Combine(_root, "project"); var builtin = Path.Combine(_root, "builtin");
            foreach (var path in new[] { home, global, project, builtin }) Directory.CreateDirectory(path);
            Directory.CreateDirectory(Path.Combine(builtin, ".git"));
            File.WriteAllText(Path.Combine(builtin, ".git", "config"), "[core]\nignorecase = true\nexcludesfile = fixture.ignore\n");
            File.WriteAllText(Path.Combine(builtin, ".git", "fixture.ignore"), "");
            var catalog = new CatalogOptions { GlobalRoot = global };
            var projectId = (await Keep(new ProjectCatalog(catalog).UpsertFromPathAsync(project))).Id; Journal = new(catalog);
            Session = new() { SessionId = Guid.CreateVersion7().ToString(), Kind = SessionViewKind.ProjectSession, ProjectRef = projectId,
                ProviderId = Provider.Descriptor.ProviderId.Value, ProviderKey = Provider.Descriptor.ProviderId.Value,
                WorkingDirectory = project, Title = "Queue RPC fixture", ModelId = "fixture-model", AgentPromptId = "default", CreatedAt = DateTimeOffset.UtcNow };
            await Keep(Journal.EnsureHeaderAsync(Session)); var store = Journal.CreateSessionStore();
            await Keep(store.UpsertSessionAsync(new AgentSessionSummary { SessionId = Session.SessionId, ProviderId = Provider.Descriptor.ProviderId,
                ProviderKey = Provider.Descriptor.ProviderId.Value, WorkingDirectory = project, Title = Session.Title,
                ModelId = Session.ModelId, AgentPromptId = "default", CreatedAt = Session.CreatedAt, UpdatedAt = Session.CreatedAt }));
            await Keep(Journal.AppendStateAsync(Session, new() { ProviderKey = Session.ProviderKey, ModelId = Session.ModelId, AgentPromptId = "default" }));
            var readback = await Keep(store.GetSessionAsync(Session.SessionId)); Assert.IsNotNull(readback); Assert.AreEqual(Session.SessionId, readback.SessionId);
            var host = await Keep(CodeAltaHost.CreateAsync(new() { GlobalRoot = global, CurrentProjectPath = project,
                DiscoveryScope = new(home, _root), BuiltInSkillRoot = builtin, PluginEnvironment = FrozenDictionary<string, string?>.Empty,
                StartPlugins = false, OwnsLogging = false, IsHeadless = true, ReviewOwnedCommandPermissions = review,
                OwnedCommandReceiptCapacity = capacity, ConfigureModelProviders = registry => registry.RegisterOrReplace(Provider.Descriptor, Provider.CreateRuntime) }));
            bool close; lock (_gate) { _host = host; Rpc = new(host.Commands, "epoch"); close = _cleaning; }
            if (close) await Keep(CloseHost());
        }
    }

    private sealed class Script
    {
        internal Exception? Failure { get; set; }
        internal TaskCompletionSource Started { get; } = Gate();
        internal TaskCompletionSource Release { get; } = Gate();
        internal TaskCompletionSource CancellationEntered { get; } = Gate();
        internal TaskCompletionSource ReleaseCancellation { get; } = Gate();
        internal AgentSendOptions? Options { get; set; }
        internal CancellationToken Token { get; set; }
        internal AgentRunId RunId { get; } = new(Guid.NewGuid().ToString("N"));
        internal bool RegistrationDisposed { get; set; }
        internal bool ForwardingDisposed { get; set; }
    }
    private sealed class Provider(Action<Task> retain)
    {
        private readonly object _gate = new();
        private readonly List<Script> _scripts = [];
        private readonly List<Session> _sessions = [];
        private readonly List<Task> _originals = [];
        private int _sends, _aborts, _active, _earlyDisposals;
        private bool _cleaning;
        internal int Sends => Volatile.Read(ref _sends);
        internal int Aborts => Volatile.Read(ref _aborts);
        internal int EarlyDisposals => Volatile.Read(ref _earlyDisposals);
        internal Session Latest { get { lock (_gate) return _sessions[^1]; } }
        internal ModelProviderDescriptor Descriptor { get; } = new(new("desktop-queue-fixture"), "Queue RPC fixture") { DefaultModelId = "fixture-model" };
        private void Retain(Task task) => retain(task);
        private void RetainProvider(Task<AgentRunId> original, Script script, CancellationToken token)
        {
            lock (_gate) _originals.Add(original);
            retain(Observe());
            async Task Observe()
            {
                try { await original.ConfigureAwait(false); }
                catch (Exception ex) when (ReferenceEquals(ex, script.Failure) || ex is OperationCanceledException && token.IsCancellationRequested) { }
            }
        }
        internal IModelProviderRuntime CreateRuntime() => new Runtime(this);
        internal Script Add(bool holdCancellation)
        {
            var script = new Script(); if (!holdCancellation) script.ReleaseCancellation.TrySetResult();
            lock (_gate) { _scripts.Add(script); if (_cleaning) { script.Release.TrySetResult(); script.ReleaseCancellation.TrySetResult(); } }
            return script;
        }
        internal void ReleaseAll()
        {
            lock (_gate) { _cleaning = true; foreach (var script in _scripts) { script.Release.TrySetResult(); script.ReleaseCancellation.TrySetResult(); } }
        }
        private sealed class Runtime(Provider owner) : IModelProviderSessionRuntime
        {
            public ModelProviderDescriptor Descriptor => owner.Descriptor;
            public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task<ModelProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("Probe forbidden.");
            public IModelProviderTurnExecutor CreateTurnExecutor() => throw new InvalidOperationException("Executor forbidden.");
            public Task<IAgentSession> CreateSessionAsync(AgentSessionCreateOptions options, CancellationToken cancellationToken = default) => Create(options.SessionId!, options);
            public Task<IAgentSession> ResumeSessionAsync(string sessionId, AgentSessionResumeOptions options, CancellationToken cancellationToken = default) => Create(sessionId, options);
            private Task<IAgentSession> Create(string id, AgentSessionCreateOptions options)
            {
                Assert.IsTrue(options.InstructionsAlreadyComposed);
                var session = new Session(owner, id); lock (owner._gate) owner._sessions.Add(session); return Task.FromResult<IAgentSession>(session);
            }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
        internal sealed class Session(Provider owner, string id) : IAgentSession
        {
            private Action<AgentEvent>? _handler;
            public ModelProviderId ProviderId => owner.Descriptor.ProviderId;
            public string SessionId => id;
            public string? WorkspacePath => null;
            public async IAsyncEnumerable<AgentEvent> StreamEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
            { await Task.CompletedTask; yield break; }
            public IDisposable Subscribe(Action<AgentEvent> handler) { _handler = handler; return new Subscription(this); }
            internal void Emit(AgentSessionUpdateKind kind, AgentRunId? run, string marker) => _handler?.Invoke(new AgentSessionUpdateEvent(ProviderId, id, DateTimeOffset.UtcNow, run, kind, marker));
            public Task<AgentRunId> SendAsync(AgentSendOptions send, CancellationToken cancellationToken = default)
            {
                Script script; lock (owner._gate) script = owner._scripts[owner._sends++];
                var original = SendOwnedAsync(script, send, cancellationToken);
                owner.RetainProvider(original, script, cancellationToken); return original;
            }
            private async Task<AgentRunId> SendOwnedAsync(Script script, AgentSendOptions send, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref owner._active);
                using var source = new CancellationTokenSource();
                var forwarding = cancellationToken.UnsafeRegister(static state => ((CancellationTokenSource)state!).Cancel(), source);
                var registration = source.Token.Register(() => { script.CancellationEntered.TrySetResult(); script.ReleaseCancellation.Task.GetAwaiter().GetResult(); });
                try
                {
                    script.Options = send; script.Token = source.Token;
                    if (send.RunLifecycle is { } lifecycle) await lifecycle.StartedAsync(script.RunId, source.Token);
                    script.Started.TrySetResult(); await script.Release.Task;
                    Emit(AgentSessionUpdateKind.Idle, null, "script-complete"); source.Token.ThrowIfCancellationRequested();
                    if (script.Failure is { } failure) throw failure; return script.RunId;
                }
                finally
                {
                    try { if (send.RunLifecycle is { } lifecycle) await lifecycle.ClosingAsync(script.RunId); }
                    finally
                    {
                        var registrationDisposal = registration.DisposeAsync().AsTask(); var forwardingDisposal = forwarding.DisposeAsync().AsTask();
                        owner.Retain(registrationDisposal); owner.Retain(forwardingDisposal);
                        await Task.WhenAll(registrationDisposal, forwardingDisposal);
                        script.RegistrationDisposed = true; script.ForwardingDisposed = true; Interlocked.Decrement(ref owner._active);
                    }
                }
            }
            public Task AbortAsync(CancellationToken cancellationToken = default) { Interlocked.Increment(ref owner._aborts); return Task.CompletedTask; }
            public Task<AgentRunId> SteerAsync(AgentSteerOptions steer, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Steer forbidden.");
            public Task CompactAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("Compact forbidden.");
            public Task<IReadOnlyList<AgentEvent>> GetHistoryAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AgentEvent>>([]);
            public ValueTask DisposeAsync() { if (Volatile.Read(ref owner._active) != 0) Interlocked.Increment(ref owner._earlyDisposals); return ValueTask.CompletedTask; }
            private sealed class Subscription(Session session) : IDisposable { public void Dispose() => session._handler = null; }
        }
    }
}
