using System.Runtime.ExceptionServices;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;
using CodeAlta.Tui.Plugins;

namespace CodeAlta.Tests;

/// <summary>Inert selection tests; no terminal controls, runtime, plugin, or services are constructed.</summary>
[TestClass]
public sealed class TerminalPluginContributionAdapterTests
{
    [TestMethod]
    public void Selection_RejectsMissingFallbackBeforeNativeCallbacks()
    {
        Func<object, object?> native = _ => throw new AssertFailedException("Native invoked.");
        Func<object> context = () => throw new AssertFailedException("Context invoked.");
        var error = Assert.ThrowsExactly<ArgumentNullException>(() => TerminalPluginContributionAdapter.SelectContent(true, true, new object(), native, null!, context));
        Assert.AreEqual("createPortable", error.ParamName);
        var contextError = Assert.ThrowsExactly<ArgumentNullException>(() => TerminalPluginContributionAdapter.SelectContent<object, object>(true, true, new object(), native, _ => null, null!));
        Assert.AreEqual("createContext", contextError.ParamName);
        var rendererError = Assert.ThrowsExactly<ArgumentNullException>(() => TerminalPluginContributionAdapter.SelectRenderer<object, object>(true,
            (_, _) => throw new AssertFailedException("Native renderer invoked."), null!, new object(), CancellationToken.None));
        Assert.AreEqual("renderPortable", rendererError.ParamName);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void Selection_UsesTerminalOnlyWhenExplicitlySupported(bool terminalContribution, bool supported)
    {
        var native = new object();
        var portable = new object();
        var context = new object();
        var contextCalls = 0;
        Assert.IsFalse(new PluginAdapterOperationOptions().SupportsTerminalVisuals);
        var result = TerminalPluginContributionAdapter.SelectContent(supported, terminalContribution, native,
            _ => throw new AssertFailedException("Direct native must win."),
            actual => { Assert.AreSame(context, actual); return portable; },
            () => { contextCalls++; return context; });
        Assert.AreSame(terminalContribution && supported ? native : portable, result);
        Assert.AreEqual(terminalContribution && supported ? 0 : 1, contextCalls);
    }

    [TestMethod]
    [DataRow("direct")]
    [DataRow("factory")]
    [DataRow("null")]
    public void Selection_PreservesDirectFactoryAndNullPrecedence(string scenario)
    {
        var expected = new object();
        var context = new object();
        var contextCalls = 0;
        var factoryCalls = 0;
        var result = TerminalPluginContributionAdapter.SelectContent(true, true, scenario == "direct" ? expected : null,
            actual =>
            {
                factoryCalls++;
                Assert.AreSame(context, actual);
                return scenario == "null" ? null : expected;
            }, _ => throw new AssertFailedException("Native null must not fall back."),
            () => { contextCalls++; return context; });
        Assert.AreSame(scenario == "null" ? null : expected, result);
        Assert.AreEqual(scenario == "direct" ? 0 : 1, factoryCalls);
        Assert.AreEqual(factoryCalls, contextCalls);
        var absentContextCalls = 0;
        var absent = TerminalPluginContributionAdapter.SelectContent<object, object>(true, true, null, null,
            _ => throw new AssertFailedException("Absent native factory must not fall back."),
            () => { absentContextCalls++; return context; });
        Assert.IsNull(absent);
        Assert.AreEqual(0, absentContextCalls);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Selection_DoesNotFallbackAfterNativeFailure(bool cancellation)
    {
        Exception failure = cancellation ? new OperationCanceledException() : new InvalidOperationException("native");
        Exception? observed = null;
        try
        {
            TerminalPluginContributionAdapter.SelectContent<object, object>(true, true, null, _ => throw failure,
                _ => throw new AssertFailedException("Failure fallback."), () => new object());
        }
        catch (Exception error) { observed = error; }
        Assert.AreSame(failure, observed);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Selection_UsesPortableMarkdownOrText(bool markdown)
    {
        var expected = markdown ? PluginRenderResult.FromMarkdown("**portable**") : new PluginRenderResult { Text = "portable" };
        var context = new object();
        var result = TerminalPluginContributionAdapter.SelectContent<object, PluginRenderResult>(false, false, null,
            _ => throw new AssertFailedException("Unsupported native invoked."),
            actual => { Assert.AreSame(context, actual); return expected; }, () => context);
        Assert.AreSame(expected, result);
        Assert.IsNull(TerminalPluginContributionAdapter.SelectContent<object, PluginRenderResult>(false, false, null, null, _ => null, () => context));
    }

    [TestMethod]
    public Task RendererSelection_ForwardsOriginalDelegateContextTokenAndTask()
        => RunAsync(async tasks =>
        {
            var gate = tasks.Gate();
            var context = new object();
            var token = new CancellationToken(true);
            var calls = 0;
            Func<object, CancellationToken, ValueTask<object?>> native = (actualContext, actualToken) =>
            {
                calls++;
                Assert.AreSame(context, actualContext);
                Assert.AreEqual(token, actualToken);
                return new ValueTask<object?>(gate.Task);
            };
            var caller = tasks.Track(TerminalPluginContributionAdapter.SelectRenderer(true, native,
                (_, _) => throw new AssertFailedException("Portable invoked."), context, token).AsTask());
            Assert.AreSame(gate.Task, caller);
            Assert.AreEqual(1, calls);
            Assert.IsFalse(caller.IsCompleted);
            var expected = new object();
            gate.SetResult(expected);
            Assert.AreSame(expected, await tasks.WaitAsync(caller));
        });

    [TestMethod]
    [DataRow("success")]
    [DataRow("synchronous")]
    [DataRow("fault")]
    [DataRow("canceled")]
    [DataRow("faulted-oce")]
    public Task RendererSelection_PreservesOutcomes(string outcome)
        => RunAsync(async tasks =>
        {
            var token = new CancellationToken(true);
            var expected = new object();
            Exception failure = outcome == "faulted-oce" ? new OperationCanceledException(token) : new InvalidOperationException("renderer");
            var original = tasks.Track(outcome switch
            {
                "fault" or "faulted-oce" => Task.FromException<object?>(failure),
                "canceled" => Task.FromCanceled<object?>(token),
                _ => Task.FromResult<object?>(expected),
            });
            foreach (var supported in new[] { false, true })
            {
                var calls = 0;
                Func<object, CancellationToken, ValueTask<object?>> selected = (_, actualToken) =>
                {
                    calls++;
                    Assert.AreEqual(token, actualToken);
                    return outcome == "synchronous" ? throw failure : new ValueTask<object?>(original);
                };
                Task<object?>? caller = null;
                Exception? observed = null;
                try
                {
                    caller = tasks.Track(TerminalPluginContributionAdapter.SelectRenderer(supported,
                        supported ? selected : (_, _) => throw new AssertFailedException("Native invoked."),
                        supported ? (_, _) => throw new AssertFailedException("Portable invoked.") : selected, new object(), token).AsTask());
                    Assert.AreSame(original, caller);
                    Assert.AreSame(expected, await tasks.WaitAsync(caller));
                }
                catch (Exception error) { observed = error; }
                Assert.AreEqual(1, calls);
                if (outcome == "success") Assert.IsNull(observed);
                else if (outcome == "canceled") Assert.IsInstanceOfType<OperationCanceledException>(observed);
                else Assert.AreSame(failure, observed);
                if (outcome == "synchronous") Assert.IsNull(caller);
            }
            var absent = tasks.Track(TerminalPluginContributionAdapter.SelectRenderer<object, object>(true,
                (_, _) => ValueTask.FromResult<object?>(null), (_, _) => throw new AssertFailedException("Native null fallback."), new object(), token).AsTask());
            Assert.IsNull(await tasks.WaitAsync(absent));
            var portable = tasks.Track(TerminalPluginContributionAdapter.SelectRenderer<object, object>(true,
                null, (_, _) => ValueTask.FromResult<object?>(null), new object(), token).AsTask());
            Assert.IsNull(await tasks.WaitAsync(portable));
        });

    private static async Task RunAsync(Func<RetainedTasks, Task> body)
    {
        var tasks = new RetainedTasks();
        Exception? primary = null;
        var cleanup = new List<Exception>();
        try { await tasks.Track(body(tasks)); }
        catch (Exception error) { primary = error; }
        finally
        {
            var cleanupTask = tasks.FinishAsync();
            try { cleanup.AddRange(await cleanupTask); }
            catch (Exception error) { cleanup.Add(error); }
        }
        if (primary is not null && cleanup.Count != 0) throw new AggregateException("Body and cleanup failed.", [primary, .. cleanup]);
        if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
        if (cleanup.Count != 0) throw new AggregateException(cleanup);
    }

    private sealed class RetainedTasks
    {
        private readonly List<Task> _tasks = [];
        private readonly List<TaskCompletionSource<object?>> _gates = [];
        public T Track<T>(T task) where T : Task { _tasks.Add(task); return task; }
        public TaskCompletionSource<object?> Gate()
        {
            var gate = new TaskCompletionSource<object?>();
            _gates.Add(gate);
            Track(gate.Task);
            return gate;
        }
        public Task<T> WaitAsync<T>(Task<T> task) => Track(task.WaitAsync(TimeSpan.FromSeconds(5)));
        public async Task<List<Exception>> FinishAsync()
        {
            foreach (var gate in _gates) gate.TrySetResult(null);
            var observers = _tasks.Select(ObserveAsync).ToArray();
            var group = Task.WhenAll(observers);
            await group;
            return observers.Select(static observer => observer.Result).OfType<Exception>().ToList();
        }
        private static async Task<Exception?> ObserveAsync(Task original)
        {
            try { await original.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException error) { return error; }
            catch (Exception) { _ = original.Exception; }
            return null;
        }
    }
}
