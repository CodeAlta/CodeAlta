using System.Runtime.ExceptionServices;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugins.Tests;

/// <summary>Inert tests of the actual content/renderer traversals; no runtime or services.</summary>
[TestClass]
public sealed class PluginUiContentRoutingTests
{
    [TestMethod]
    [DataRow("registrations")]
    [DataRow("getActive")]
    [DataRow("materialize")]
    [DataRow("all")]
    public void ContentRoute_RejectsMissingInputs(string missing)
    {
        Func<PluginContributionRegistration, object?> active = _ => throw new AssertFailedException("Active callback.");
        Func<PluginContributionRegistration, PluginContentContribution, object, object?> materialize = (_, _, _) => throw new AssertFailedException("Materializer.");
        var error = Assert.ThrowsExactly<ArgumentNullException>(() => PluginUiContentRouting.CreateContent(
            missing is "registrations" or "all" ? null! : [], PluginUiRegion.SessionFooter, null,
            missing is "getActive" or "all" ? null! : active,
            missing is "materialize" or "all" ? null! : materialize));
        Assert.AreEqual(missing == "all" ? "registrations" : missing, error.ParamName);
    }

    [TestMethod]
    [DataRow("headless")]
    [DataRow("noninteractive")]
    [DataRow("region")]
    [DataRow("inactive")]
    [DataRow("ordered")]
    public void ContentRoute_PreservesEligibilityAndOrder(string scenario)
    {
        var first = Content();
        var second = Content();
        var entries = new[] { Entry(first, "first"), Entry(second, "second") };
        var visited = new List<string>();
        var activeCalls = 0;
        var options = scenario switch
        {
            "headless" => new PluginAdapterOperationOptions { IsHeadless = true, HasInteractiveUi = true },
            "noninteractive" => new PluginAdapterOperationOptions(),
            _ => null, // Null options preserve the original interactive traversal.
        };
        var result = PluginUiContentRouting.CreateContent(entries,
            scenario == "region" ? PluginUiRegion.CommandBar : PluginUiRegion.SessionFooter, options,
            _ => { activeCalls++; return scenario == "inactive" ? null : new object(); },
            (entry, _, _) => { visited.Add(entry.Handle.NaturalName!); return entry; });
        if (scenario == "ordered") CollectionAssert.AreEqual(new[] { "first", "second" }, visited);
        else Assert.AreEqual(0, result.Count);
        Assert.AreEqual(scenario is "ordered" or "inactive" ? 2 : 0, activeCalls);
    }

    [TestMethod]
    public void ContentRoute_ForwardsOriginalValues()
    {
        var content = Content();
        var entry = Entry(content);
        var owner = new object();
        var result = new object();
        var values = PluginUiContentRouting.CreateContent([entry], content.Region, null,
            actual => { Assert.AreSame(entry, actual); return owner; },
            (actual, contribution, active) =>
            {
                Assert.AreSame(entry, actual);
                Assert.AreSame(content, contribution);
                Assert.AreSame(owner, active);
                return result;
            });
        Assert.AreSame(result, values.Single());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ContentRoute_PropagatesCallbackFailures(bool cancellation)
    {
        Exception expected = cancellation ? new OperationCanceledException() : new InvalidOperationException("content");
        Exception? observed = null;
        try
        {
            PluginUiContentRouting.CreateContent<object, object>([Entry(Content())], PluginUiRegion.SessionFooter, null,
                _ => new object(), (_, _, _) => throw expected);
        }
        catch (Exception error) { observed = error; }
        Assert.AreSame(expected, observed);
    }

    [TestMethod]
    [DataRow("registrations")]
    [DataRow("createContext")]
    [DataRow("render")]
    [DataRow("reportFailure")]
    [DataRow("all")]
    public void RendererRoute_RejectsMissingInputs(string missing)
    {
        Func<PluginContributionRegistration, (object Owner, PluginRendererContext Context)?> context = _ => throw new AssertFailedException("Context.");
        Func<PluginRendererContribution, PluginRendererContext, CancellationToken, ValueTask<object?>> render = (_, _, _) => throw new AssertFailedException("Render.");
        Func<PluginContributionRegistration, object, Exception, PluginRuntimeDiagnostic> report = (_, _, _) => throw new AssertFailedException("Report.");
        var error = Assert.ThrowsExactly<ArgumentNullException>(() => PluginUiContentRouting.RenderAsync(
            missing is "registrations" or "all" ? null! : [], PluginUiRegion.SessionFooter, null, null,
            missing is "createContext" or "all" ? null! : context,
            missing is "render" or "all" ? null! : render,
            missing is "reportFailure" or "all" ? null! : report, CancellationToken.None));
        Assert.AreEqual(missing == "all" ? "registrations" : missing, error.ParamName);
    }

    [TestMethod]
    [DataRow("wildcard")]
    [DataRow("case")]
    [DataRow("nonmatch")]
    [DataRow("null")]
    [DataRow("ordered")]
    public Task RendererRoute_PreservesFilteringAndResults(string scenario)
        => RunAsync(async tasks =>
        {
            var target = scenario switch { "wildcard" => "  ", "case" => "TARGET", "nonmatch" => "other", _ => "target" };
            var entries = new[] { Entry(Renderer(target), "first"), Entry(Renderer(target), "second") };
            var contexts = new List<PluginRendererContext>();
            var original = tasks.Track(Task.FromResult<object?>(scenario == "null" ? null : new object()));
            var caller = tasks.Track(PluginUiContentRouting.RenderAsync<object, object>(entries, PluginUiRegion.SessionFooter, "target", null,
                entry => { var context = Context(entry.Handle.NaturalName); contexts.Add(context); return (entry, context); },
                (_, _, _) => new ValueTask<object?>(original), (_, _, _) => throw new AssertFailedException("Unexpected report."), CancellationToken.None).AsTask());
            var result = await tasks.WaitAsync(caller);
            Assert.AreEqual(scenario == "nonmatch" ? 0 : 2, contexts.Count);
            Assert.AreEqual(scenario is "nonmatch" or "null" ? 0 : 2, result.Results.Count);
            Assert.IsTrue(contexts.All(static context => !context.IsValid));
            if (scenario == "ordered") CollectionAssert.AreEqual(new[] { "first", "second" }, contexts.Select(static context => context.Target).ToArray());

            // These branches are part of the same mandatory renderer traversal, not registry substitutes.
            foreach (var options in new[] { new PluginAdapterOperationOptions(), new PluginAdapterOperationOptions { IsHeadless = true, HasInteractiveUi = true } })
            {
                var bypass = tasks.Track(PluginUiContentRouting.RenderAsync<object, object>(entries, PluginUiRegion.SessionFooter, "target", options,
                    _ => throw new AssertFailedException("Bypass context."), (_, _, _) => throw new AssertFailedException("Bypass renderer."),
                    (_, _, _) => throw new AssertFailedException("Bypass report."), CancellationToken.None).AsTask());
                Assert.AreEqual(0, (await tasks.WaitAsync(bypass)).Results.Count);
            }
            var filtered = tasks.Track(PluginUiContentRouting.RenderAsync<object, object>(entries, PluginUiRegion.CommandBar, "target", null,
                _ => throw new AssertFailedException("Wrong-region context."), (_, _, _) => throw new AssertFailedException("Wrong-region renderer."),
                (_, _, _) => throw new AssertFailedException("Wrong-region report."), CancellationToken.None).AsTask());
            Assert.AreEqual(0, (await tasks.WaitAsync(filtered)).Results.Count);
            var inactive = tasks.Track(PluginUiContentRouting.RenderAsync<object, object>(entries, PluginUiRegion.SessionFooter, "target", null,
                _ => null, (_, _, _) => throw new AssertFailedException("Inactive renderer."),
                (_, _, _) => throw new AssertFailedException("Inactive report."), CancellationToken.None).AsTask());
            Assert.AreEqual(0, (await tasks.WaitAsync(inactive)).Results.Count);
        });

    [TestMethod]
    [DataRow("success")]
    [DataRow("fault")]
    [DataRow("canceled")]
    [DataRow("faulted-oce")]
    public Task RendererRoute_PreservesExceptionAndInvalidationPolicy(string outcome)
        => RunAsync(async tasks =>
        {
            var token = new CancellationToken(true);
            Exception failure = outcome == "faulted-oce" ? new OperationCanceledException(token) : new InvalidOperationException("renderer");
            var original = tasks.Track(outcome switch
            {
                "fault" or "faulted-oce" => Task.FromException<object?>(failure),
                "canceled" => Task.FromCanceled<object?>(token),
                _ => Task.FromResult<object?>(null),
            });
            var context = Context();
            var reports = new List<Exception>();
            var caller = tasks.Track(PluginUiContentRouting.RenderAsync<object, object>([Entry(Renderer())], PluginUiRegion.SessionFooter, null, null,
                _ => (new object(), context), (_, _, _) => new ValueTask<object?>(original),
                (_, _, error) => { reports.Add(error); return Diagnostic(); }, token).AsTask());
            Exception? observed = null;
            try { await tasks.WaitAsync(caller); }
            catch (Exception error) { observed = error; }
            Assert.AreEqual(outcome != "success", context.IsValid);
            Assert.AreEqual(outcome == "fault" ? 1 : 0, reports.Count);
            if (outcome is "success" or "fault") Assert.IsNull(observed);
            else if (outcome == "faulted-oce") Assert.AreSame(failure, observed);
            else Assert.IsInstanceOfType<OperationCanceledException>(observed);
            if (reports.Count != 0) Assert.AreSame(failure, reports[0]);

            if (outcome == "fault")
            {
                var expected = new object();
                var successor = tasks.Track(Task.FromResult<object?>(expected));
                var firstContext = Context("first");
                var secondContext = Context("second");
                var continued = tasks.Track(PluginUiContentRouting.RenderAsync<object, object>(
                    [Entry(Renderer(), "first"), Entry(Renderer(), "second")], PluginUiRegion.SessionFooter, null, null,
                    entry => (entry, entry.Handle.NaturalName == "first" ? firstContext : secondContext),
                    (_, actual, _) => new ValueTask<object?>(ReferenceEquals(actual, firstContext) ? original : successor),
                    (_, _, error) => { Assert.AreSame(failure, error); return Diagnostic(); }, CancellationToken.None).AsTask());
                var continuedResult = await tasks.WaitAsync(continued);
                Assert.AreSame(expected, continuedResult.Results.Single());
                Assert.AreEqual(1, continuedResult.Diagnostics.Count);
                Assert.IsTrue(firstContext.IsValid);
                Assert.IsFalse(secondContext.IsValid);
            }

            // Context creation must remain outside the callback catch; it cannot become a diagnostic.
            var contextFailure = new InvalidOperationException("context");
            var failedContext = tasks.Track(PluginUiContentRouting.RenderAsync<object, object>([Entry(Renderer())], PluginUiRegion.SessionFooter, null, null,
                _ => throw contextFailure, (_, _, _) => throw new AssertFailedException("Renderer after context failure."),
                (_, _, _) => throw new AssertFailedException("Context failure was caught."), CancellationToken.None).AsTask());
            Exception? contextObserved = null;
            try { await tasks.WaitAsync(failedContext); }
            catch (Exception error) { contextObserved = error; }
            Assert.AreSame(contextFailure, contextObserved);

            var sequence = new List<string>();
            var reportingFailure = new InvalidOperationException("report");
            var reporting = tasks.Track(PluginUiContentRouting.RenderAsync<object, object>([Entry(Renderer(), "first"), Entry(Renderer(), "second")], PluginUiRegion.SessionFooter, null, null,
                _ => (new object(), Context()), (_, _, _) => { sequence.Add("render"); throw failure is OperationCanceledException ? contextFailure : failure; },
                (_, _, _) => { sequence.Add("report"); throw reportingFailure; }, CancellationToken.None).AsTask());
            Exception? reportObserved = null;
            try { await tasks.WaitAsync(reporting); }
            catch (Exception error) { reportObserved = error; }
            Assert.AreSame(reportingFailure, reportObserved);
            CollectionAssert.AreEqual(new[] { "render", "report" }, sequence);
        });

    [TestMethod]
    public Task RendererRoute_JoinsOriginalAndPreservesToken()
        => RunAsync(async tasks =>
        {
            var gate = tasks.Gate();
            var token = new CancellationToken(true);
            var payload = new object();
            var context = Context("original", payload);
            var contribution = Renderer();
            var owner = new object();
            var caller = tasks.Track(PluginUiContentRouting.RenderAsync<object, object>([Entry(contribution)], contribution.Region, null, null,
                _ => (owner, context), (actual, actualContext, actualToken) =>
                {
                    Assert.AreSame(contribution, actual);
                    Assert.AreSame(context, actualContext);
                    Assert.AreSame(payload, actualContext.Payload);
                    Assert.AreEqual(token, actualToken);
                    return new ValueTask<object?>(gate.Task);
                }, (_, _, _) => throw new AssertFailedException("Unexpected report."), token).AsTask());
            Assert.IsFalse(caller.IsCompleted);
            Assert.IsTrue(context.IsValid);
            var expected = new object();
            gate.SetResult(expected);
            Assert.AreSame(expected, (await tasks.WaitAsync(caller)).Results.Single());
            Assert.IsFalse(context.IsValid);
        });

    private static PluginContentContribution Content() => new() { Region = PluginUiRegion.SessionFooter, CreateContent = _ => null };
    private static PluginRendererContribution Renderer(string? target = null) => new() { Region = PluginUiRegion.SessionFooter, Target = target, Renderer = (_, _) => ValueTask.FromResult<PluginRenderResult?>(null) };
    // Deliberately unavailable runtime references: these literal operation DTOs never consult services or plugin metadata.
    private static PluginRendererContext Context(string? target = null, object? payload = null) => new() { Plugin = null!, Services = null!, Target = target, Payload = payload };
    private static PluginRuntimeDiagnostic Diagnostic() => new() { Severity = PluginDiagnosticSeverity.Warning, Source = PluginRuntimeDiagnosticSource.Callback, Message = "inert" };
    private static PluginContributionRegistration Entry(object contribution, string name = "inert") => new()
    {
        Contribution = contribution, Scope = PluginScope.Global,
        Handle = PluginContributionHandle.Create("inert", "inert", PluginPoint.Ui, name, 0, 0),
    };

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
            catch (TimeoutException error) { return error; } // Never rehabilitate a timed-out observation.
            catch (Exception) { _ = original.Exception; } // Expected original fault/cancellation; no observer cancellation token.
            return null;
        }
    }
}
