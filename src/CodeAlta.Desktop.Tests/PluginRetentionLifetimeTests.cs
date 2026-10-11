using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Desktop;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class PluginRetentionLifetimeTests
{
    [TestMethod]
    public async Task PublishedPrompt_CanceledWaiterRetainsClaimUntilDelayedDownstreamHandoff()
    {
        var admission = new Admission();
        using var ui = new DesktopPluginUi(admission);
        ui.Enter(new DesktopPluginScope { SessionId = "old-session" });
        var seen = new List<PluginUiEvent>();
        using var watch = ui.Watch(seen.Add);
        using var cancellation = new CancellationTokenSource();
        var prompt = ui.EnqueuePromptAsync("later", cancellation.Token).AsTask();
        var request = seen.Single();
        Assert.AreEqual("old-session", admission.LastSession);
        Assert.AreEqual(1, admission.Claims);
        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => prompt);
        Assert.AreEqual("close", seen[^1].Kind);
        Assert.AreEqual(1, admission.Claims, "A canceled caller is not proof that the page's captured worker stopped.");
        var reloaded = new List<PluginUiEvent>();
        using var reconnect = ui.Watch(reloaded.Add);
        Assert.AreEqual("close", reloaded.Single().Kind, "Never replay a canceled prompt as runnable work.");

        // The original page finishes its delayed enqueue only after downstream ownership is acquired.
        using (var downstream = admission.TryProtectSession("old-session"))
        {
            Assert.IsNotNull(downstream);
            Assert.IsTrue(ui.Respond(Answer(request)));
            Assert.AreEqual(1, admission.Claims, "The handoff overlaps the downstream queue owner.");
        }
        Assert.AreEqual(0, admission.Claims);
        Assert.IsFalse(ui.Respond(Answer(request)));
    }

    [TestMethod]
    public async Task PublishedPrompt_ReconnectDoesNotRunCapturedWorkTwice()
    {
        var admission = new Admission();
        using var ui = new DesktopPluginUi(admission);
        var first = new List<PluginUiEvent>();
        using var watch = ui.Watch(first.Add);
        var prompt = ui.SendPromptAsync("once").AsTask();
        var second = new List<PluginUiEvent>();
        using var reconnect = ui.Watch(second.Add);
        Assert.AreEqual(0, second.Count);
        Assert.AreEqual(1, admission.Claims);
        Assert.IsTrue(ui.Respond(Answer(first.Single())));
        await prompt;
        Assert.AreEqual(0, admission.Claims);
    }

    [TestMethod]
    public async Task UnpublishedPrompt_CancellationIsConclusive_AndRefusalHasNoVisibleEffects()
    {
        var admission = new Admission();
        using var ui = new DesktopPluginUi(admission);
        using var cancellation = new CancellationTokenSource();
        var prompt = ui.SendPromptAsync("no page", cancellation.Token).AsTask();
        Assert.AreEqual(1, admission.Claims);
        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => prompt);
        Assert.AreEqual(0, admission.Claims);
        var seen = new List<PluginUiEvent>();
        using var watch = ui.Watch(seen.Add);
        Assert.IsFalse(seen.Any(static item => item.Kind == "prompt"));
        seen.Clear();
        admission.Retiring = true;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => ui.SendPromptAsync("refused").AsTask());
        Assert.AreEqual(0, seen.Count);
        Assert.AreEqual(0, admission.Claims);
    }

    [TestMethod]
    public async Task BrokerDisposal_DoesNotReleasePublishedPromptBeforePageAcknowledges()
    {
        var admission = new Admission();
        var ui = new DesktopPluginUi(admission);
        var seen = new List<PluginUiEvent>();
        using var watch = ui.Watch(seen.Add);
        var prompt = ui.TrySteerAsync("held").AsTask();
        var request = seen.Single();
        ui.Dispose();
        Assert.IsFalse(await prompt);
        Assert.AreEqual(1, admission.Claims);
        Assert.IsTrue(ui.Respond(Answer(request, cancelled: true)));
        Assert.AreEqual(0, admission.Claims);
    }

    [TestMethod]
    public async Task DialogAction_OriginalCallbackRetainsClaimThroughRpcAndDialogCancellation()
    {
        var admission = new Admission();
        using var ui = new DesktopPluginUi(admission);
        var seen = new List<PluginUiEvent>();
        using var watch = ui.Watch(seen.Add);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var dialog = ui.ShowDialogForResultAsync(PluginUi.HtmlDialog("held", "<b>test</b>") with
        {
            OnAction = async (_, _) =>
            {
                calls++;
                entered.SetResult();
                await finish.Task;
                return PluginDialogActionResult.KeepOpen;
            },
        }).AsTask();
        var request = seen.Single();
        admission.Retiring = true;
        Assert.AreEqual("retiring", (await ui.ActionAsync(request.RequestId, "test", null, null, default)).Status);
        Assert.AreEqual(0, calls);
        admission.Retiring = false;
        using var rpc = new CancellationTokenSource();
        var action = ui.ActionAsync(request.RequestId, "test", null, null, rpc.Token).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        rpc.Cancel();
        ui.Respond(Answer(request, cancelled: true));
        await dialog;
        Assert.IsFalse(action.IsCompleted);
        Assert.AreEqual(1, admission.Claims);
        finish.SetResult();
        await action.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(0, admission.Claims);
    }

    [TestMethod]
    public async Task Command_RefusesBeforeCallback_OriginalWorkerOutlivesRpcAndShutdownCancellation()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodeAlta-plugin-retention-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var admission = new Admission();
        using var ui = new DesktopPluginUi(admission);
        await using var runtime = new PluginRuntimeManager();
        var plugin = new HeldPlugin();
        PluginUiService? service = null;
        try
        {
            var projects = new ProjectCatalog(new CatalogOptions { GlobalRoot = root });
            await runtime.StartAsync(new PluginRuntimeManagerOptions
            {
                GlobalRoot = root,
                Frontend = PluginFrontends.Desktop,
                SessionRetentionAdmission = admission,
                BuiltIns = [new BuiltInPluginDefinition { Id = "held", DisplayName = "Held", PluginType = typeof(HeldPlugin), Factory = () => plugin }],
            });
            service = new PluginUiService(projects, runtime, ui, "epoch");
            var command = (await service.ContributionsAsync(new("epoch", null), default)).Commands.Single();
            var request = new PluginUiInvokeRequest("epoch", command.Id, null, "old-session", false, null);
            admission.Retiring = true;
            Assert.AreEqual("retiring", (await service.InvokeCommandAsync(request, default)).Status);
            Assert.AreEqual(0, plugin.Calls);
            admission.Retiring = false;
            using var rpc = new CancellationTokenSource();
            Assert.AreEqual("started", (await service.InvokeCommandAsync(request, rpc.Token)).Status);
            await plugin.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            rpc.Cancel();
            var close = service.CloseAsync();
            Assert.IsFalse(close.IsCompleted);
            Assert.AreEqual(1, admission.Claims);
            plugin.Finish.SetResult();
            await close.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(0, admission.Claims);
        }
        finally
        {
            plugin.Finish.TrySetResult();
            if (service is not null) await service.CloseAsync();
            await runtime.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow("ordinary")]
    [DataRow("agent")]
    [DataRow("plugin")]
    [DataRow("agent-multi")]
    [DataRow("plugin-multi")]
    public async Task DialogAction_FaultReleasesOnlyWithConfirmedDependencyTermination(string kind)
    {
        var admission = new Admission();
        using var ui = new DesktopPluginUi(admission);
        var seen = new List<PluginUiEvent>();
        using var watch = ui.Watch(seen.Add);
        var dialog = ui.ShowDialogForResultAsync(PluginUi.HtmlDialog("fault", "<b>test</b>") with
        {
            OnAction = (_, _) => kind.EndsWith("-multi", StringComparison.Ordinal)
                ? new ValueTask<PluginDialogActionResult>(FaultedOriginal<PluginDialogActionResult>(kind))
                : throw Failure(kind),
        }).AsTask();
        var request = seen.Single();
        Assert.AreEqual("failed", (await ui.ActionAsync(request.RequestId, "test", null, null, default)).Status);
        ui.Respond(Answer(request, cancelled: true));
        await dialog;
        ui.Dispose();
        Assert.AreEqual(kind == "ordinary" ? 0 : 1, admission.Claims);
    }

    [TestMethod]
    [DataRow("ordinary")]
    [DataRow("agent")]
    [DataRow("plugin")]
    [DataRow("agent-multi")]
    [DataRow("plugin-multi")]
    public async Task Command_FaultReleasesOnlyWithConfirmedDependencyTermination(string kind)
    {
        var root = Path.Combine(Path.GetTempPath(), "CodeAlta-command-fault-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var admission = new Admission();
        using var ui = new DesktopPluginUi(admission);
        await using var runtime = new PluginRuntimeManager();
        var plugin = new HeldPlugin
        {
            Failure = Failure(kind),
            OriginalFailure = kind.EndsWith("-multi", StringComparison.Ordinal) ? FaultedOriginal<PluginCommandResult>(kind) : null,
        };
        PluginUiService? service = null;
        try
        {
            await runtime.StartAsync(new PluginRuntimeManagerOptions
            {
                GlobalRoot = root,
                SessionRetentionAdmission = admission,
                BuiltIns = [new BuiltInPluginDefinition { Id = "held", DisplayName = "Held", PluginType = typeof(HeldPlugin), Factory = () => plugin }],
            });
            service = new PluginUiService(new ProjectCatalog(new CatalogOptions { GlobalRoot = root }), runtime, ui, "epoch");
            var command = (await service.ContributionsAsync(new("epoch", null), default)).Commands.Single();
            Assert.AreEqual("started", (await service.InvokeCommandAsync(new("epoch", command.Id, null, null, false, null), default)).Status);
            await plugin.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            plugin.Finish.SetResult();
            await service.CloseAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(kind == "ordinary" ? 0 : 1, admission.Claims);
        }
        finally
        {
            plugin.Finish.TrySetResult();
            if (service is not null) await service.CloseAsync();
            await runtime.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    private static Exception Failure(string kind)
    {
        Exception failure = kind.Replace("-multi", "", StringComparison.Ordinal) switch
        {
            "agent" => new AgentDependencyRetentionException("test", "release", [new Exception("release")], new object()),
            "plugin" => new PluginEventDependencyException(new Exception("creation"), new Exception("barrier"), new object()),
            _ => new InvalidOperationException("ordinary"),
        };
        return new AggregateException(new Exception("first ordinary failure"), new InvalidOperationException("wrapper", failure));
    }

    private static Task<T> FaultedOriginal<T>(string kind)
    {
        var original = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        original.SetException([new InvalidOperationException("ordinary first task failure"), Failure(kind)]);
        return original.Task;
    }

    private static PluginUiAnswer Answer(PluginUiEvent request, bool cancelled = false)
        => new(request.RequestId, null, cancelled, null, null, null);

    /// <summary>A synthetic command that deliberately ignores its cancellation token.</summary>
    public sealed class HeldPlugin : PluginBase
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }
        public Exception? Failure { get; init; }
        public Task<PluginCommandResult>? OriginalFailure { get; init; }
        public override IEnumerable<PluginCommandContribution> GetCommands()
        {
            if (OriginalFailure is { } original)
            {
                yield return Command.Shell("held", "Synthetic multiply-faulted original", (_, _) =>
                {
                    Calls++;
                    Entered.SetResult();
                    return new ValueTask<PluginCommandResult>(original);
                });
                yield break;
            }
            yield return Command.Shell("held", "Synthetic held worker", async (_, _) =>
            {
                Calls++;
                Entered.SetResult();
                await Finish.Task;
                if (Failure is not null) throw Failure;
                return PluginCommandResult.Handled;
            });
        }
    }

    private sealed class Admission : ISessionRetentionAdmission
    {
        private int _claims;
        public int Claims => Volatile.Read(ref _claims);
        public string? LastSession { get; private set; }
        public bool Retiring { get; set; }
        public IDisposable? TryProtectSession(string sessionId)
        {
            LastSession = sessionId;
            return TryProtectAll();
        }
        public IDisposable? TryProtectAll()
        {
            if (Retiring) return null;
            Interlocked.Increment(ref _claims);
            return new Claim(this);
        }
        private sealed class Claim(Admission owner) : IDisposable
        {
            private int _disposed;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0) Interlocked.Decrement(ref owner._claims);
            }
        }
    }
}
