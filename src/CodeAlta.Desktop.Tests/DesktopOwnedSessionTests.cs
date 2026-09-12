using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Desktop;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Desktop.Tests;

/// <summary>Unadmitted fixtures. Only the two named real methods create fresh owned roots and a fake-only host.</summary>
[TestClass]
public sealed class DesktopOwnedSessionTests
{
    [TestMethod]
    public void OwnedFlags_RequireCompleteExplicitConsentBeforeAcquisition()
    {
        var root = OperatingSystem.IsWindows() ? @"Q:\owned" : "/owned";
        var args = new[] { "--data-root", root + "/browser", "--catalog-root", root + "/copy", "--allow-catalog-cache",
            "--allow-owned-host", "--project-root", root + "/project", "--discovery-home", root + "/home",
            "--instruction-root", root, "--builtin-skill-root", root + "/builtin" };
        bool Exists(string path) => !path.EndsWith("browser", StringComparison.Ordinal);
        Assert.IsTrue(DesktopCommandLine.TryParse(args, Exists, _ => false, out var options, out _));
        Assert.IsNotNull(options!.Owned);
        Assert.IsFalse(DesktopCommandLine.TryParse(args.Where(value => value != "--allow-owned-host").ToArray(), Exists, _ => false, out _, out _));
        Assert.IsFalse(DesktopCommandLine.TryParse(args[..^2], Exists, _ => false, out _, out _));
        Assert.IsFalse(DesktopCommandLine.TryParse([.. args, "--allow-owned-host"], Exists, _ => false, out _, out _));
    }

    [TestMethod]
    public void LegacyBranches_DoNotComposeOwnedHost()
    {
        var root = OperatingSystem.IsWindows() ? @"Q:\literal" : "/literal";
        Assert.IsTrue(DesktopCommandLine.TryParse(["--data-root", root], _ => false, _ => false, out var options, out _));
        Assert.IsNull(options!.Owned);
        var disabled = new SessionOperationsService();
        Assert.AreEqual("unconfigured", disabled.Send(new("epoch", "key", "session", "text"), CancellationToken.None).Status);
        Assert.AreEqual("unconfigured", disabled.Receipts(new("epoch", 0)).Status);
    }

    [TestMethod]
    public void StaleEpoch_RejectsBeforeOwnerAdmission()
    {
        var service = new SessionOperationsService("current", _ => throw new AssertFailedException("Admission called"),
            _ => throw new AssertFailedException("Abort called"));
        Assert.AreEqual("stale_epoch", service.Send(new("old", "key", "session", "text"), CancellationToken.None).Status);
        Assert.AreEqual("stale_epoch", service.Abort(new("old", "key", Guid.NewGuid().ToString("D")), CancellationToken.None).Status);
        var canonical = "abcdefab-1234-5678-9abc-abcdefabcdef";
        foreach (var target in new[] { " " + canonical, canonical + " ", new string(' ', 7000) + canonical, canonical.ToUpperInvariant() })
            Assert.AreEqual("invalid_request", service.Abort(new("current", "key", target), CancellationToken.None).Status);
        foreach (var text in new[] { new string('x', 32769), "\ud800", "\udc00", "x\ud800x" })
            Assert.AreEqual("invalid_request", service.Send(new("current", "key", "session", text), CancellationToken.None).Status);
        foreach (var identity in new[] { new string('x', 257), "\ud800", "\udc00" })
        {
            Assert.AreEqual("invalid_request", service.Send(new("current", identity, "session", "text"), CancellationToken.None).Status);
            Assert.AreEqual("invalid_request", service.Send(new("current", "key", identity, "text"), CancellationToken.None).Status);
        }
    }

    [TestMethod]
    public Task Admission_RetainsReceiptAndPreservesOwnerReplay() => RealFixture.RunAsync(async f =>
    {
        var service = new SessionOperationsService(f.Host.Commands, "fixture-epoch");
        var request = new SessionSendRequest("fixture-epoch", "same", f.SessionId, "exact text");
        var accepted = service.Send(request, CancellationToken.None);
        // Retain the owner's replay completion before any assertion or page projection can fail.
        var receipt = f.Retain(f.Host.Commands.AdmitSend(new("same", f.SessionId, "exact text")));
        var page = service.Receipts(new("fixture-epoch", 0));
        Assert.AreEqual("accepted", accepted.Status);
        Assert.HasCount(1, page.Rows);
        Assert.AreEqual(accepted.Receipt!.OperationId, page.Rows[0].OperationId);
        Assert.AreEqual("replay", service.Send(request, CancellationToken.None).Status);
        Assert.AreEqual("conflict", service.Send(request with { Text = "changed" }, CancellationToken.None).Status);
        await f.Ready(receipt);
        f.Provider.Release.TrySetResult();
        await f.Wait(receipt.Completion);
        Assert.AreEqual("Completed", service.Receipts(new("fixture-epoch", 0)).Rows[0].Outcome);
    });

    [TestMethod]
    public void ReceiptPages_BoundWorstCaseGeneratedJson()
    {
        var escaped = new string('\u0001', 256);
        var row = new SessionReceiptView(escaped, escaped, Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
            "Send", "terminal", "Completed", new string('\u0001', 64), escaped);
        var epoch = new string('\u0001', 64);
        var page = SessionOperationsService.ProjectPage(epoch, Enumerable.Repeat(row, 64).ToArray(), 64);
        Assert.AreEqual("ok", page.Status);
        Assert.HasCount(64, page.Rows);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(page, DesktopJsonContext.Default.SessionReceiptPage);
        // NeoRpcHost.SendSuccessResultAsync emits neoastra/kind/id/ok/value. The default
        // request ID bound is 128 ASCII units; allow six-byte escaping plus 128 syntax bytes.
        const int resultEnvelope = 128 * 6 + 128;
        Assert.IsTrue(bytes.Length + resultEnvelope <= 64 * 6400 + 8192);
        Assert.IsTrue(bytes.Length + resultEnvelope <= 448 * 1024);
        var request = new SessionSendRequest(epoch, escaped, escaped, new string('\u0001', 32768));
        var requestBytes = JsonSerializer.SerializeToUtf8Bytes(request, DesktopJsonContext.Default.SessionSendRequest);
        // An 8192-byte allowance covers request ID, method, generated contract hash and syntax.
        Assert.IsTrue(requestBytes.Length + 8192 <= 208 * 1024);
        var unicode = row with { SessionId = string.Concat(Enumerable.Repeat("\ud83d\ude00", 128)) };
        Assert.AreEqual("ok", SessionOperationsService.ProjectPage(epoch, [unicode], null).Status);
        foreach (var invalid in new[] { row with { OperationId = " " + row.OperationId }, row with { TargetOperationId = row.TargetOperationId + new string(' ', 7000) },
            row with { OperationId = "ABCDEFAB-1234-5678-9ABC-ABCDEFABCDEF" }, row with { SessionId = escaped + "x" },
            row with { SessionId = "\ud800" }, row with { Code = "\udc00" }, row with { RunId = escaped + "x" } })
            Assert.AreEqual("wire_limit", SessionOperationsService.ProjectPage(epoch, [invalid], null).Status);
        Assert.AreEqual("wire_limit", SessionOperationsService.ProjectPage("epoch", Enumerable.Repeat(row, 65).ToArray(), null).Status);
    }

    [TestMethod]
    public void SteerRpc_RejectsBeforeAdmissionAndBoundsGeneratedJson()
    {
        var service = new SessionOperationsService("current", _ => throw new AssertFailedException("Send called"),
            _ => throw new AssertFailedException("Abort called"), _ => throw new AssertFailedException("Steer called"));
        var request = new SessionSteerRequest("current", "key", "session", "abcdefab-1234-5678-9abc-abcdefabcdef", "1", "run", "text");
        Assert.AreEqual("unconfigured", new SessionOperationsService().Steer(request, CancellationToken.None).Status);
        Assert.AreEqual("stale_epoch", service.Steer(request with { ExpectedEpoch = "old" }, CancellationToken.None).Status);
        foreach (var invalid in new[] { request with { ExpectedRuntimeInstanceId = request.ExpectedRuntimeInstanceId.ToUpperInvariant() },
            request with { ExpectedRuntimeInstanceId = Guid.Empty.ToString("D") }, request with { ExpectedAttachmentGeneration = "01" },
            request with { ExpectedAttachmentGeneration = "0" }, request with { ExpectedAttachmentGeneration = "9223372036854775808" },
            request with { ExpectedRunId = "" }, request with { ExpectedRunId = " run" }, request with { Text = "\ud800" },
            request with { Text = new string('x', 32769) }, request with { ClientRequestId = new string('x', 257) } })
            Assert.AreEqual("invalid_request", service.Steer(invalid, CancellationToken.None).Status);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => service.Steer(request, cancelled.Token));
        service.CloseAdmission();
        Assert.AreEqual("closed", service.Steer(request, CancellationToken.None).Status);
        var escaped = new string('\u0001', 256);
        var maximum = request with { ExpectedEpoch = new string('\u0001', 64), ClientRequestId = escaped, SessionId = escaped,
            ExpectedRunId = escaped, ExpectedAttachmentGeneration = long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture), Text = new string('\u0001', 32768) };
        Assert.IsTrue(JsonSerializer.SerializeToUtf8Bytes(maximum, DesktopJsonContext.Default.SessionSteerRequest).Length + 8192 <= 208 * 1024);
        var row = new SessionReceiptView("key", "session", Guid.NewGuid().ToString("D"), null, "Steer", "terminal", "Completed", null, "run");
        Assert.AreEqual("ok", SessionOperationsService.ProjectPage("epoch", [row], null).Status);
    }

    [TestMethod]
    public Task SteerRpc_ActualOwnedRouteRetainsReplayAndKind() => RealFixture.RunAsync(async f =>
    {
        var service = new SessionOperationsService(f.Host.Commands, "fixture-epoch");
        service.Send(new("fixture-epoch", "send", f.SessionId, "text"), CancellationToken.None);
        var send = f.Retain(f.Host.Commands.AdmitSend(new("send", f.SessionId, "text")));
        await f.Ready(send);
        f.Provider.Release.TrySetResult();
        await f.Wait(send.Completion);
        var state = await f.Wait(f.Keep(f.Host.RuntimeService.GetCurrentStateAsync(f.SessionId)));
        var request = new SessionSteerRequest("fixture-epoch", "steer", f.SessionId, state.RuntimeInstanceId.ToString("D"),
            state.Entry!.AttachmentGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture), state.Entry.ActiveRunId!, "exact text");
        var admitted = service.Steer(request, CancellationToken.None);
        var steer = f.Retain(f.Host.Commands.AdmitSteer(new("steer", f.SessionId, state.RuntimeInstanceId,
            state.Entry.AttachmentGeneration, request.ExpectedRunId, request.Text)));
        Assert.AreEqual("accepted", admitted.Status);
        Assert.AreEqual("replay", service.Steer(request, CancellationToken.None).Status);
        Assert.AreEqual("conflict", service.Steer(request with { Text = "changed" }, CancellationToken.None).Status);
        Assert.AreEqual("conflict", service.Send(new("fixture-epoch", "steer", f.SessionId, "exact text"), CancellationToken.None).Status);
        Assert.AreEqual(OwnedSessionCommandOutcome.Completed, (await f.Wait(steer.Completion)).Outcome);
        var row = service.Receipts(new("fixture-epoch", 0)).Rows.Single(value => value.ClientRequestId == "steer");
        Assert.AreEqual("Steer", row.Kind);
        Assert.AreEqual(request.ExpectedRunId, row.RunId);
    });

    [TestMethod]
    public void CompactRpc_RejectsBeforeAdmissionAndBoundsGeneratedJson()
    {
        var service = new SessionOperationsService("current", _ => throw new AssertFailedException("Send called"),
            _ => throw new AssertFailedException("Abort called"), null, _ => throw new AssertFailedException("Compact called"));
        var request = new SessionCompactRequest("current", "key", "session", "abcdefab-1234-5678-9abc-abcdefabcdef", "1");
        Assert.AreEqual("unconfigured", new SessionOperationsService().Compact(request, CancellationToken.None).Status);
        Assert.AreEqual("stale_epoch", service.Compact(request with { ExpectedEpoch = "old" }, CancellationToken.None).Status);
        foreach (var invalid in new[] { request with { ExpectedRuntimeInstanceId = request.ExpectedRuntimeInstanceId.ToUpperInvariant() },
            request with { ExpectedRuntimeInstanceId = Guid.Empty.ToString("D") }, request with { ExpectedAttachmentGeneration = "01" },
            request with { ExpectedAttachmentGeneration = "0" }, request with { ExpectedAttachmentGeneration = "9223372036854775808" },
            request with { SessionId = " session" }, request with { SessionId = "\ud800" }, request with { ClientRequestId = new string('x', 257) } })
            Assert.AreEqual("invalid_request", service.Compact(invalid, CancellationToken.None).Status);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => service.Compact(request, cancelled.Token));
        service.CloseAdmission();
        Assert.AreEqual("closed", service.Compact(request, CancellationToken.None).Status);
        var escaped = new string('\u0001', 256);
        var maximum = request with { ExpectedEpoch = new string('\u0001', 64), ClientRequestId = escaped, SessionId = escaped,
            ExpectedAttachmentGeneration = long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        Assert.IsTrue(JsonSerializer.SerializeToUtf8Bytes(maximum, DesktopJsonContext.Default.SessionCompactRequest).Length + 8192 <= 16 * 1024);
        var row = new SessionReceiptView("key", "session", Guid.NewGuid().ToString("D"), null, "Compact", "terminal", "Completed", null, null);
        Assert.AreEqual("ok", SessionOperationsService.ProjectPage("epoch", [row], null).Status);
    }

    [TestMethod]
    public Task CompactRpc_ActualOwnedRouteRetainsReplayAndKind() => RealFixture.RunAsync(async f =>
    {
        var service = new SessionOperationsService(f.Host.Commands, "fixture-epoch");
        service.Send(new("fixture-epoch", "send", f.SessionId, "text"), CancellationToken.None);
        var send = f.Retain(f.Host.Commands.AdmitSend(new("send", f.SessionId, "text")));
        await f.Ready(send);
        f.Provider.Release.TrySetResult();
        await f.Wait(send.Completion);
        var marker = Guid.NewGuid().ToString("N");
        f.Provider.EmitIdle!(marker);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await f.Keep(Committed());
        async Task Committed()
        {
            await foreach (var value in f.Host.RuntimeService.StreamEventsAsync(deadline.Token))
                if (value is SessionAgentEvent { Event: AgentSessionUpdateEvent update }
                    && update.Kind == AgentSessionUpdateKind.Idle && update.Message == marker) return;
            Assert.Fail("Idle marker was not committed.");
        }
        var state = await f.Wait(f.Keep(f.Host.RuntimeService.GetCurrentStateAsync(f.SessionId)));
        var request = new SessionCompactRequest("fixture-epoch", "compact", f.SessionId, state.RuntimeInstanceId.ToString("D"),
            state.Entry!.AttachmentGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var admitted = service.Compact(request, CancellationToken.None);
        var compact = f.Retain(f.Host.Commands.AdmitCompact(new("compact", f.SessionId, state.RuntimeInstanceId, state.Entry.AttachmentGeneration)));
        Assert.AreEqual("accepted", admitted.Status);
        Assert.AreEqual("replay", service.Compact(request, CancellationToken.None).Status);
        Assert.AreEqual("conflict", service.Compact(request with { ExpectedAttachmentGeneration = "999" }, CancellationToken.None).Status);
        Assert.AreEqual("conflict", service.Send(new("fixture-epoch", "compact", f.SessionId, "text"), CancellationToken.None).Status);
        Assert.AreEqual(OwnedSessionCommandOutcome.Completed, (await f.Wait(compact.Completion)).Outcome);
        Assert.AreEqual(1, f.Provider.Compactions);
        var row = service.Receipts(new("fixture-epoch", 0)).Rows.Single(value => value.ClientRequestId == "compact");
        Assert.AreEqual("Compact", row.Kind);
        Assert.IsNull(row.RunId);
    });

    [TestMethod]
    public async Task Shutdown_RetainsLeaseUntilAllOwnedWorkIsConfirmed()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wait = gate.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.IsFalse(DesktopApplication.CanReleaseOwnedLease(Task.CompletedTask, gate.Task, true));
            Assert.IsFalse(DesktopApplication.CanReleaseOwnedLease(Task.CompletedTask, Task.CompletedTask, false));
            gate.TrySetResult();
            await wait;
            Assert.IsTrue(DesktopApplication.CanReleaseOwnedLease(Task.CompletedTask, gate.Task, true));
        }
        finally { gate.TrySetResult(); await wait; }
    }

    [TestMethod]
    public Task OwnedRpc_UsesRealHostCachedStoreAndFakeProvider() => RealFixture.RunAsync(async f =>
    {
        var workspace = new WorkspaceService(f.Host.WorkspaceReads);
        var snapshotTask = f.Keep(workspace.SnapshotAsync(new(), CancellationToken.None));
        var snapshot = await f.Wait(snapshotTask);
        Assert.IsTrue(snapshot.Sessions.Any(session => session.Id == f.SessionId));
        var historyTask = f.Keep(workspace.HistoryAsync(new(f.SessionId, null), CancellationToken.None));
        var history = await f.Wait(historyTask);
        Assert.AreEqual("ok", history.Status);
        var service = new SessionOperationsService(f.Host.Commands, "fixture-epoch");
        var admission = service.Send(new("fixture-epoch", "send", f.SessionId, "text"), CancellationToken.None);
        var receipt = f.Retain(f.Host.Commands.AdmitSend(new("send", f.SessionId, "text")));
        Assert.AreEqual("accepted", admission.Status);
        await f.Ready(receipt);
        f.Provider.Release.TrySetResult();
        await f.Wait(receipt.Completion);
        Assert.AreEqual("Completed", service.Receipts(new("fixture-epoch", 0)).Rows.Single().Outcome);
    });

    private sealed class RealFixture
    {
        private readonly object _gate = new();
        private readonly List<Task> _work = [], _observers = [], _cleanup = [];
        private readonly List<Exception> _failures = [];
        private readonly string _root = Path.Combine(Path.GetTempPath(), "CodeAlta-desktop-owned-" + Guid.NewGuid().ToString("N"));
        private Task? _disposal;
        private bool _ownsRoot;
        private volatile bool _closing;
        private CodeAltaHost? _host;
        internal CodeAltaHost Host => _host ?? throw new InvalidOperationException("No fixture host.");
        internal string SessionId { get; } = Guid.CreateVersion7().ToString();
        internal FakeProvider Provider { get; } = new();

        internal Task Keep(Task task)
        {
            lock (_gate) { _work.Add(task); _observers.Add(ObserveAsync(task)); }
            return task;
        }
        internal Task<T> Keep<T>(Task<T> task) { Keep((Task)task); return task; }
        private async Task ObserveAsync(Task task)
        {
            try { await task.ConfigureAwait(false); }
            catch (Exception ex) { lock (_gate) _failures.Add(ex); }
        }
        internal Task Wait(Task task) => Keep(task.WaitAsync(TimeSpan.FromSeconds(5)));
        internal Task<T> Wait<T>(Task<T> task) => Keep(task.WaitAsync(TimeSpan.FromSeconds(5)));
        internal OwnedSessionCommandReceipt Retain(OwnedSessionCommandAdmission admission)
        {
            if (admission.Receipt is not null) Keep(admission.Receipt.Completion);
            Assert.IsNotNull(admission.Receipt);
            return admission.Receipt;
        }
        internal Task Ready(OwnedSessionCommandReceipt receipt) => Keep(ReadyCoreAsync(receipt));
        private async Task ReadyCoreAsync(OwnedSessionCommandReceipt receipt)
        {
            var race = Keep(Task.WhenAny(Provider.Started.Task, receipt.Completion));
            await Wait(race);
            if (Provider.Started.Task.IsCompletedSuccessfully) return;
            var result = await Wait(receipt.Completion);
            Assert.Fail($"Send readiness: Outcome={result.Outcome}; Code={result.Code}");
        }
        private Task DisposeHost()
        {
            lock (_gate) return _disposal ??= Keep(Host.DisposeAsync().AsTask());
        }

        internal static async Task RunAsync(Func<RealFixture, Task> body)
        {
            var f = new RealFixture();
            Task? setup = null;
            Task? bodyTask = null;
            try
            {
                setup = f.Keep(f.SetupAsync());
                await f.Wait(setup);
                bodyTask = f.Keep(body(f));
                await f.Wait(bodyTask);
            }
            catch (Exception ex) { lock (f._gate) f._failures.Add(ex); }
            finally
            {
                Task[] available;
                lock (f._gate)
                {
                    f._closing = true;
                    f.Provider.Release.TrySetResult();
                    if (f._host is not null) _ = f.DisposeHost();
                    available = [.. f._work, .. f._observers];
                }
                // Start every available setup/body/read/disposal/observer deadline before awaiting any.
                var availableJoin = f.DrainAsync(available);
                var confirmed = await availableJoin;
                // A timed-out producer may still add work. Its root remains retained, not finalized.
                if ((setup is null || setup.IsCompleted) && (bodyTask is null || bodyTask.IsCompleted))
                {
                    Task[] snapshot;
                    lock (f._gate) snapshot = [.. f._work, .. f._observers];
                    var finalJoin = f.DrainAsync(snapshot);
                    confirmed &= await finalJoin;
                    lock (f._gate)
                        if (confirmed && f._failures.Count == 0 && f._ownsRoot) Directory.Delete(f._root, recursive: true);
                }
            }
            Exception[] failures;
            lock (f._gate) failures = [.. f._failures];
            if (failures.Length != 0) throw new AggregateException("Fixture failed; root retained: " + f._root, failures);
        }

        private async Task<bool> DrainAsync(IEnumerable<Task> tasks)
        {
            var waits = tasks.Distinct().Select(task => task.WaitAsync(TimeSpan.FromSeconds(5))).ToArray();
            lock (_gate) _cleanup.AddRange(waits);
            var confirmed = true;
            foreach (var wait in waits)
            {
                try { await wait.ConfigureAwait(false); }
                catch (Exception ex) { lock (_gate) _failures.Add(ex); confirmed = false; }
            }
            return confirmed;
        }

        private async Task SetupAsync()
        {
            for (var parent = new DirectoryInfo(Path.GetDirectoryName(_root)!); parent is not null; parent = parent.Parent)
                if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Reparse ancestry.");
            if (Directory.Exists(_root) || File.Exists(_root)) throw new InvalidOperationException("Fixture root exists.");
            Directory.CreateDirectory(_root);
            _ownsRoot = true;
            var global = Path.Combine(_root, "global");
            var projectPath = Path.Combine(_root, "project");
            var home = Path.Combine(_root, "home");
            var builtin = Path.Combine(_root, "builtin");
            foreach (var path in new[] { global, projectPath, home, builtin }) Directory.CreateDirectory(path);
            Directory.CreateDirectory(Path.Combine(builtin, ".git"));
            File.WriteAllText(Path.Combine(builtin, ".git", "config"), "[core]\nignorecase = true\nexcludesfile = fixture.ignore\n");
            File.WriteAllText(Path.Combine(builtin, ".git", "fixture.ignore"), "");
            Directory.CreateDirectory(Path.Combine(builtin, "fixture-builtin"));
            File.WriteAllText(Path.Combine(builtin, "fixture-builtin", "SKILL.md"), "---\nname: fixture-builtin\ndescription: Owned fixture.\n---\nFixture only.\n");
            var options = new CatalogOptions { GlobalRoot = global };
            var projectTask = Keep(new ProjectCatalog(options).UpsertFromPathAsync(projectPath, CancellationToken.None));
            var project = await projectTask;
            var session = new SessionViewDescriptor
            {
                SessionId = SessionId, Kind = SessionViewKind.ProjectSession, ProjectRef = project.Id,
                ProviderId = "owned-fixture", ProviderKey = "owned-fixture", ModelId = "fixture-model", AgentPromptId = "default",
                WorkingDirectory = projectPath, Title = "Owned fixture", CreatedAt = new(2026, 9, 8, 0, 0, 0, TimeSpan.Zero),
            };
            var journal = new SessionViewJournalStore(options);
            var header = Keep(journal.EnsureHeaderAsync(session, CancellationToken.None));
            await header;
            var store = journal.CreateSessionStore();
            var summary = Keep(store.UpsertSessionAsync(new AgentSessionSummary
            {
                SessionId = SessionId, ProviderId = Provider.Descriptor.ProviderId, ProviderKey = "owned-fixture",
                ModelId = session.ModelId, AgentPromptId = session.AgentPromptId, ReasoningEffort = session.ReasoningEffort,
                WorkingDirectory = projectPath, Title = session.Title, CreatedAt = session.CreatedAt, UpdatedAt = session.CreatedAt,
            }, CancellationToken.None));
            await summary;
            var append = Keep(journal.AppendStateAsync(session, new SessionViewLocalState
            {
                ProviderKey = session.ProviderKey, ModelId = session.ModelId, ReasoningEffort = session.ReasoningEffort, AgentPromptId = session.AgentPromptId,
            }, CancellationToken.None));
            await append;
            var read = Keep(store.GetSessionAsync(SessionId, CancellationToken.None));
            var metadata = await read;
            Assert.IsNotNull(metadata);
            Assert.AreEqual(SessionId, metadata.SessionId);
            Assert.AreEqual(projectPath, metadata.WorkspacePath);
            Assert.AreEqual("owned-fixture", metadata.ProviderKey);
            Assert.AreEqual("fixture-model", metadata.ModelId);
            Assert.AreEqual("default", metadata.AgentPromptId);
            Assert.IsNotNull(metadata.ViewState);
            Assert.AreEqual("owned-fixture", metadata.ViewState.ProviderKey);
            Assert.AreEqual("fixture-model", metadata.ViewState.ModelId);
            Assert.AreEqual("default", metadata.ViewState.AgentPromptId);
            Assert.AreEqual(metadata.ReasoningEffort, metadata.ViewState.ReasoningEffort);
            var creation = Keep(CodeAltaHost.CreateAsync(new CodeAltaHostOptions
            {
                GlobalRoot = global, CurrentProjectPath = projectPath, DiscoveryScope = new(home, _root), BuiltInSkillRoot = builtin,
                PluginEnvironment = FrozenDictionary<string, string?>.Empty, StartPlugins = false, OwnsLogging = false, IsHeadless = true,
                ConfigureModelProviders = registry => registry.RegisterOrReplace(Provider.Descriptor, () => new FakeRuntime(Provider)),
            }, CancellationToken.None));
            var host = await creation;
            lock (_gate) _host = host;
            if (_closing) await DisposeHost();
        }
    }

    private sealed class FakeProvider
    {
        internal Action<string>? EmitIdle { get; set; }
        internal int Compactions { get; set; }
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ModelProviderDescriptor Descriptor { get; } = new(new ModelProviderId("owned-fixture"), "Owned fixture") { DefaultModelId = "fixture-model" };
    }

    private sealed class FakeRuntime(FakeProvider provider) : IModelProviderSessionRuntime
    {
        public ModelProviderDescriptor Descriptor => provider.Descriptor;
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ModelProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("No probes.");
        public IModelProviderTurnExecutor CreateTurnExecutor() => throw new InvalidOperationException("No turn executor.");
        public Task<IAgentSession> CreateSessionAsync(AgentSessionCreateOptions options, CancellationToken cancellationToken = default)
            => Task.FromResult<IAgentSession>(new FakeSession(provider, options.SessionId!, options.WorkingDirectory));
        public Task<IAgentSession> ResumeSessionAsync(string sessionId, AgentSessionResumeOptions options, CancellationToken cancellationToken = default)
            => Task.FromResult<IAgentSession>(new FakeSession(provider, sessionId, options.WorkingDirectory));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeSession(FakeProvider provider, string sessionId, string? cwd) : IAgentSession, IAgentIdleCompactionProvider
    {
        public ModelProviderId ProviderId => provider.Descriptor.ProviderId;
        public string SessionId => sessionId;
        public string? WorkspacePath => cwd;
        public async IAsyncEnumerable<AgentEvent> StreamEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        { await Task.CompletedTask; yield break; }
        public IDisposable Subscribe(Action<AgentEvent> handler)
        {
            provider.EmitIdle = marker =>
            {
                handler(new AgentSessionUpdateEvent(ProviderId, SessionId, DateTimeOffset.UtcNow, null, AgentSessionUpdateKind.Idle, marker));
            };
            return new Subscription(() => provider.EmitIdle = null);
        }
        public async Task<AgentRunId> SendAsync(AgentSendOptions options, CancellationToken cancellationToken = default)
        {
            provider.Started.TrySetResult();
            await provider.Release.Task;
            cancellationToken.ThrowIfCancellationRequested();
            return new("fixture-run");
        }
        public Task AbortAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<AgentRunId> SteerAsync(AgentSteerOptions options, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (options.ExpectedRunId?.Value != "fixture-run") throw new InvalidOperationException("Wrong fixture run.");
            return Task.FromResult(new AgentRunId("fixture-run"));
        }
        public Task CompactAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("No compaction.");
        public Task<AgentCompactionOutcome?> TryCompactWhenIdleAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            provider.Compactions++;
            return Task.FromResult<AgentCompactionOutcome?>(new(true, "inert completed compaction"));
        }
        public Task<IReadOnlyList<AgentEvent>> GetHistoryAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AgentEvent>>([]);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        private sealed class Subscription(Action dispose) : IDisposable { public void Dispose() => dispose(); }
    }
}
