using CodeAlta.Catalog;
using CodeAlta.Hosting;
using CodeAlta.Tui.App;
using Authentication = CodeAlta.Hosting.ConfiguredCodexAuthentication;
using Coordinator = CodeAlta.Tui.App.ProviderFrontendCoordinator;

namespace CodeAlta.Tests;

// Static core/helpers and instance-owned recording operations only; no coordinator/provider execution.
// Synthetic begin/wait events describe recording behavior, NOT real Begin/listener/protocol coverage.
// No credential, browser-context, PKCE or protocol records, concrete clients/stores/hosts/processes,
// resolver, profile/environment discovery or credential I/O. URI values are inert display examples.
// Production may abandon its wait after a callback failure; fixtures independently retain and join theirs.
// SR may read output-directory localization; existing writerless logging remains, not zero I/O.
[TestClass]
public sealed class ConfiguredCodexBrowserLoginTests
{
    [TestMethod]
    [DataRow("mismatch")]
    [DataRow("factory")]
    [DataRow("fake-root")]
    [DataRow("begin")]
    [DataRow("start-wait")]
    [DataRow("synchronous-operation")]
    [DataRow("faulted-operation")]
    [DataRow("report")]
    [DataRow("opener")]
    [DataRow("prefix")]
    [DataRow("completion")]
    public async Task SynchronousFailures_KeepIdentityAndStopLaterCallbacks(string stage)
    {
        var fake = new RecordingBrowserLogin();
        var expected = new InvalidOperationException("synthetic failure");
        string[] expectedEvents;
        switch (stage)
        {
            case "mismatch":
                fake.Definition.ProviderType = "wrong";
                fake.InvalidProviderText = () => throw expected;
                expectedEvents = ["format-invalid"];
                break;
            case "factory":
                fake.FactoryBody = (_, _) => throw expected;
                expectedEvents = ["factory"];
                break;
            case "fake-root":
                fake.RootValue = () => throw expected;
                fake.FactoryBody = (_, getRoot) =>
                {
                    getRoot();
                    return fake.InvokeOperation;
                };
                expectedEvents = ["factory", "root"];
                break;
            case "begin":
                fake.BeginAction = _ => throw expected;
                expectedEvents = ["factory", "operation", "synthetic-begin"];
                break;
            case "start-wait":
                // A genuinely synchronous fake call throw, not an async provider bind-failure claim.
                fake.WaitBody = _ => throw expected;
                expectedEvents = ["factory", "operation", "synthetic-begin", "synthetic-start-wait"];
                break;
            case "synchronous-operation":
                fake.Operation = (_, _, _, _, _) => throw expected;
                expectedEvents = ["factory", "operation"];
                break;
            case "faulted-operation":
                fake.Operation = (_, _, _, _, _) => ValueTask.FromException(expected);
                expectedEvents = ["factory", "operation"];
                break;
            case "report":
                fake.ReportAction = uri => Coordinator.ReportCodexBrowserAuthorization(uri, _ => throw expected);
                expectedEvents = ["factory", "operation", "synthetic-begin", "synthetic-start-wait", "report"];
                break;
            case "opener":
                // Arbitrary callback failure only: real TryOpenBrowser suppresses ordinary launch errors.
                fake.OpenAction = _ => throw expected;
                expectedEvents = ["factory", "operation", "synthetic-begin", "synthetic-start-wait", "report", "open"];
                break;
            case "prefix":
                fake.PrefixText = () => throw expected;
                expectedEvents = ["factory", "operation", "synthetic-begin", "synthetic-start-wait", "report", "open", "prefix"];
                break;
            case "completion":
                fake.CompletionAction = (_, _) => throw expected;
                expectedEvents = ["factory", "operation", "synthetic-begin", "synthetic-start-wait", "report", "open", "prefix", "synthetic-project-id", "completion"];
                break;
            default:
                throw new AssertFailedException("Unknown synthetic stage.");
        }

        var actual = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fake.InvokeAsync().WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.AreSame(expected, actual);
        CollectionAssert.AreEqual(expectedEvents, fake.Events);
    }

    [TestMethod]
    [DataRow("report", "success")]
    [DataRow("report", "fault")]
    [DataRow("report", "cancel")]
    [DataRow("opener", "success")]
    [DataRow("opener", "fault")]
    [DataRow("opener", "cancel")]
    public async Task PendingCallbackFailure_LeavesFakeWaitUnjoinedUntilFixtureCleanup(string callback, string outcome)
    {
        var fake = new RecordingBrowserLogin();
        var callbackFailure = new InvalidOperationException("synthetic callback failure");
        var waitFailure = new InvalidOperationException("synthetic wait failure");
        using var cancellation = new CancellationTokenSource();
        var gate = NewGate();
        var wait = gate.Task; // Retained before the route can abandon it.
        fake.WaitBody = _ => wait;
        SetCallbackFailure(fake, callback, callbackFailure);
        Task? route = null;
        try
        {
            route = fake.InvokeAsync();
            await ObserveAsync(route, callbackFailure);
            Assert.IsFalse(wait.IsCompleted);
            AssertCallbackEvents(fake, callback);
        }
        finally
        {
            if (outcome == "fault")
            {
                gate.TrySetException(waitFailure);
            }
            else if (outcome == "cancel")
            {
                cancellation.Cancel();
                gate.TrySetCanceled(cancellation.Token);
            }
            else
            {
                gate.TrySetResult();
            }

            // Fixture-only joining: do not remedy production's intentional missing join.
            await JoinBothAsync(route, wait, callbackFailure, false, outcome == "fault" ? waitFailure : null, outcome == "cancel");
        }
    }

    [TestMethod]
    public async Task PendingWait_DelaysCompletionPresentation()
    {
        var fake = new RecordingBrowserLogin();
        var gate = NewGate();
        var wait = gate.Task;
        fake.WaitBody = _ => wait;
        Task? route = null;
        try
        {
            route = fake.InvokeAsync();
            Assert.IsFalse(route.IsCompleted);
            Assert.IsFalse(wait.IsCompleted);
            CollectionAssert.AreEqual(new[] { "factory", "operation", "synthetic-begin", "synthetic-start-wait", "report", "open" }, fake.Events);
            gate.SetResult();
            await ObserveAsync(route);
            CollectionAssert.AreEqual(new[]
            {
                "factory", "operation", "synthetic-begin", "synthetic-start-wait", "report", "open", "prefix", "synthetic-project-id", "completion",
            }, fake.Events);
        }
        finally
        {
            gate.TrySetResult();
            await JoinBothAsync(route, wait);
        }
    }

    [TestMethod]
    public async Task CompletionPresentation_DoesNotSettlePendingOperation()
    {
        var fake = new RecordingBrowserLogin();
        var gate = NewGate();
        var wait = gate.Task;
        var presented = false;
        fake.CompletionAction = (prefix, rawId) =>
        {
            var result = Coordinator.FormatCodexBrowserLoginResult(prefix, rawId);
            Assert.IsTrue(result.Success);
            presented = true;
        };
        fake.Operation = async (_, _, prefix, complete, _) =>
        {
            complete(prefix(), "synthetic id");
            await wait;
        };
        Task? route = null;
        try
        {
            route = fake.InvokeAsync();
            Assert.IsTrue(presented);
            Assert.IsFalse(wait.IsCompleted);
            Assert.IsFalse(route.IsCompleted);
            gate.SetResult();
            await ObserveAsync(route);
            CollectionAssert.AreEqual(new[] { "factory", "operation", "prefix", "completion" }, fake.Events);
        }
        finally
        {
            gate.TrySetResult();
            await JoinBothAsync(route, wait);
        }
    }

    [TestMethod]
    [DataRow("https://example.invalid/synthetic-login", UriKind.Absolute)]
    [DataRow("synthetic-relative-login", UriKind.Relative)]
    public async Task Report_UsesOriginalUriAndExactLocalizedMessage(string value, UriKind kind)
    {
        var fake = new RecordingBrowserLogin();
        fake.AuthorizationUri = new Uri(value, kind);
        var messages = new List<string>();
        fake.ReportAction = uri => Coordinator.ReportCodexBrowserAuthorization(uri, messages.Add);

        await ObserveAsync(fake.InvokeAsync());

        Assert.AreSame(fake.AuthorizationUri, fake.ReportedUri);
        Assert.AreSame(fake.AuthorizationUri, fake.OpenedUri);
        CollectionAssert.AreEqual(new[] { SR.T("Open ChatGPT login in your browser: {0}", fake.AuthorizationUri) }, messages);
    }

    [TestMethod]
    public async Task Completion_LocalizesPrefixBeforeFakeIdProjection()
    {
        var fake = new RecordingBrowserLogin();
        fake.RawAccountId = "before prefix";
        fake.PrefixText = () =>
        {
            fake.RawAccountId = " after synthetic id ";
            return SR.T("ChatGPT browser login completed");
        };
        ProviderTestResult? result = null;
        fake.CompletionAction = (prefix, rawId) => result = Coordinator.FormatCodexBrowserLoginResult(prefix, rawId);

        await ObserveAsync(fake.InvokeAsync());

        Assert.IsNotNull(result);
        Assert.IsTrue(result.Value.Success);
        Assert.AreEqual(0, result.Value.ModelCount);
        Assert.AreEqual(SR.T("{0} · account/workspace: {1}.", SR.T("ChatGPT browser login completed"), " after synthetic id "), result.Value.Message);
        CollectionAssert.AreEqual(new[]
        {
            "factory", "operation", "synthetic-begin", "synthetic-start-wait", "report", "open", "prefix", "synthetic-project-id", "completion",
        }, fake.Events);
    }

    [TestMethod]
    [DataRow(null, true)]
    [DataRow("", true)]
    [DataRow(" ", true)]
    [DataRow("\t\r\n", true)]
    [DataRow("synthetic-id", false)]
    [DataRow(" padded synthetic id ", false)]
    public async Task Completion_UsesEveryRawIdBranch(string? rawAccountId, bool unknown)
    {
        var fake = new RecordingBrowserLogin();
        fake.RawAccountId = rawAccountId;
        fake.PrefixText = () => SR.T("ChatGPT browser login completed");
        ProviderTestResult? result = null;
        fake.CompletionAction = (prefix, rawId) => result = Coordinator.FormatCodexBrowserLoginResult(prefix, rawId);

        await ObserveAsync(fake.InvokeAsync());

        Assert.IsNotNull(result);
        Assert.IsTrue(result.Value.Success);
        Assert.AreEqual(0, result.Value.ModelCount);
        Assert.AreEqual(SR.T("{0} · account/workspace: {1}.", SR.T("ChatGPT browser login completed"), unknown ? SR.T("account/workspace unknown") : rawAccountId), result.Value.Message);
    }

    // Complete fixture-only helper suffix is intentionally suitable for mechanical full-copy.
    private static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static Task CompletedWait(string outcome, Exception failure, CancellationToken token)
        => outcome switch
        {
            "success" => Task.CompletedTask,
            "fault" => Task.FromException(failure),
            "cancel" => Task.FromCanceled(token),
            _ => throw new AssertFailedException("Unknown synthetic outcome."),
        };

    private static void SetCallbackFailure(RecordingBrowserLogin fake, string callback, Exception failure)
    {
        if (callback == "report")
        {
            // Shared recording helper must remain frontend-free for the mechanical Hosting split.
            // Production report-helper failures are covered separately by SynchronousFailures.
            fake.ReportAction = _ => throw failure;
        }
        else
        {
            fake.OpenAction = _ => throw failure;
        }
    }

    private static void AssertCallbackEvents(RecordingBrowserLogin fake, string callback)
        => CollectionAssert.AreEqual(callback == "report"
            ? new[] { "factory", "operation", "synthetic-begin", "synthetic-start-wait", "report" }
            : new[] { "factory", "operation", "synthetic-begin", "synthetic-start-wait", "report", "open" }, fake.Events);

    private static async Task ObserveAsync(Task task, Exception? expected = null, bool canceled = false, CancellationToken? expectedToken = null)
    {
        if (canceled)
        {
            var actual = await Assert.ThrowsAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.IsTrue(task.IsCanceled);
            if (expectedToken is { } token)
            {
                Assert.AreEqual(token, actual.CancellationToken);
            }
        }
        else if (expected is not null)
        {
            var actual = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.AreSame(expected, actual);
        }
        else
        {
            await task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task JoinBothAsync(
        Task? route, Task wait, Exception? routeFailure = null, bool routeCanceled = false,
        Exception? waitFailure = null, bool waitCanceled = false)
    {
        try
        {
            if (route is not null)
            {
                await ObserveAsync(route, routeFailure, routeCanceled);
            }
        }
        finally
        {
            // Always observe the independently owned wait, even when route observation fails.
            await ObserveAsync(wait, waitFailure, waitCanceled);
        }
    }

    private sealed class RecordingBrowserLogin
    {
        public RecordingBrowserLogin()
        {
            GetStateRootPath = () =>
            {
                Events.Add("root");
                return RootValue();
            };
            FormatInvalidProvider = () =>
            {
                Events.Add("format-invalid");
                return InvalidProviderText();
            };
            ReportAuthorization = uri =>
            {
                Events.Add("report");
                ReportedUri = uri;
                ReportAction(uri);
            };
            OpenBrowser = uri =>
            {
                Events.Add("open");
                OpenedUri = uri;
                OpenAction(uri);
            };
            FormatCompletionPrefix = () =>
            {
                Events.Add("prefix");
                return PrefixText();
            };
            OnCompleted = (prefix, rawId) =>
            {
                Events.Add("completion");
                CompletionAction(prefix, rawId);
            };
            FactoryBody = (_, _) => InvokeOperation;
            Operation = RunOperationAsync;
        }

        public CodeAltaProviderDocument Definition { get; } = new()
        {
            ProviderType = "codex",
            ProviderKey = "synthetic-provider",
        };

        public List<string> Events { get; } = [];
        public Uri AuthorizationUri { get; set; } = new("https://example.invalid/synthetic-login");
        public string? RawAccountId { get; set; } = "synthetic-raw-id";
        public Func<string> GetStateRootPath { get; }
        public Func<string> RootValue { get; set; } = () => "synthetic-root";
        public Func<string> FormatInvalidProvider { get; }
        public Func<string> InvalidProviderText { get; set; } = () => "fixture invalid provider";
        public Action<Uri> ReportAuthorization { get; }
        public Action<Uri> ReportAction { get; set; } = _ => { };
        public Action<Uri> OpenBrowser { get; }
        public Action<Uri> OpenAction { get; set; } = _ => { };
        public Func<string> FormatCompletionPrefix { get; }
        public Func<string> PrefixText { get; set; } = () => "synthetic completion prefix";
        public Action<string, string?> OnCompleted { get; }
        public Action<string, string?> CompletionAction { get; set; } = (_, _) => { };
        public Action<string?> BeginAction { get; set; } = _ => { };
        public Func<CancellationToken, Task> WaitBody { get; set; } = _ => Task.CompletedTask;
        public Func<CodeAltaProviderDocument, Func<string>, CodexBrowserLoginOperation> FactoryBody { get; set; }
        public CodexBrowserLoginOperation Operation { get; set; }
        public CodeAltaProviderDocument? ObservedDefinition { get; private set; }
        public Func<string>? ObservedRootCallback { get; private set; }
        public Action<Uri>? ObservedReportCallback { get; private set; }
        public Action<Uri>? ObservedOpenCallback { get; private set; }
        public Func<string>? ObservedPrefixCallback { get; private set; }
        public Action<string, string?>? ObservedCompletionCallback { get; private set; }
        public CancellationToken ObservedToken { get; private set; }
        public CancellationToken ObservedWaitToken { get; private set; }
        public string? ObservedConfiguredAccountId { get; private set; }
        public Uri? ReportedUri { get; private set; }
        public Uri? OpenedUri { get; private set; }

        public CodexBrowserLoginOperation CreateOperation(CodeAltaProviderDocument definition, Func<string> getStateRootPath)
        {
            Events.Add("factory");
            ObservedDefinition = definition;
            ObservedRootCallback = getStateRootPath;
            return FactoryBody(definition, getStateRootPath);
        }

        public ValueTask InvokeOperation(
            Action<Uri> reportAuthorization,
            Action<Uri> openBrowser,
            Func<string> formatCompletionPrefix,
            Action<string, string?> onCompleted,
            CancellationToken cancellationToken)
        {
            Events.Add("operation");
            ObservedReportCallback = reportAuthorization;
            ObservedOpenCallback = openBrowser;
            ObservedPrefixCallback = formatCompletionPrefix;
            ObservedCompletionCallback = onCompleted;
            ObservedToken = cancellationToken;
            return Operation(reportAuthorization, openBrowser, formatCompletionPrefix, onCompleted, cancellationToken);
        }

        public async ValueTask RunOperationAsync(
            Action<Uri> reportAuthorization,
            Action<Uri> openBrowser,
            Func<string> formatCompletionPrefix,
            Action<string, string?> onCompleted,
            CancellationToken cancellationToken)
        {
            // Recording behavior, not a provider implementation or proof of concrete sequence coverage.
            Events.Add("synthetic-begin");
            ObservedConfiguredAccountId = Definition.AccountId;
            BeginAction(ObservedConfiguredAccountId);
            Events.Add("synthetic-start-wait");
            ObservedWaitToken = cancellationToken;
            var waitTask = WaitBody(cancellationToken);
            reportAuthorization(AuthorizationUri);
            openBrowser(AuthorizationUri);
            await waitTask;
            var prefix = formatCompletionPrefix();
            Events.Add("synthetic-project-id");
            var rawAccountId = RawAccountId;
            onCompleted(prefix, rawAccountId);
        }

        public Task InvokeAsync(CancellationToken cancellationToken = default)
            => Authentication.LoginWithBrowserAsync(
                Definition, GetStateRootPath, FormatInvalidProvider, ReportAuthorization, OpenBrowser,
                FormatCompletionPrefix, OnCompleted, CreateOperation, cancellationToken);
    }
}
