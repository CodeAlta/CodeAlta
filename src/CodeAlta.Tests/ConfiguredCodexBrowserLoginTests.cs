using CodeAlta.Catalog;
using CodeAlta.Tui.App;
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
    [DataRow(0, false)]
    [DataRow(1, false)]
    [DataRow(2, false)]
    [DataRow(3, false)]
    [DataRow(4, false)]
    [DataRow(5, false)]
    [DataRow(6, false)]
    [DataRow(7, false)]
    [DataRow(0, true)]
    [DataRow(1, true)]
    [DataRow(2, true)]
    [DataRow(3, true)]
    [DataRow(4, true)]
    [DataRow(5, true)]
    [DataRow(6, true)]
    [DataRow(7, true)]
    public async Task RequiredArguments_ValidateEachInputAndEveryPrecedenceSuffix(int index, bool nullSuffix)
    {
        var fake = new RecordingBrowserLogin();
        fake.Definition.ProviderType = "wrong";
        var missing = new bool[8];
        for (var position = index; position < (nullSuffix ? missing.Length : index + 1); position++)
        {
            missing[position] = true;
        }

        var failure = await Assert.ThrowsExactlyAsync<ArgumentNullException>(() =>
            Coordinator.LoginCodexBrowserCoreAsync(
                missing[0] ? null! : fake.Definition,
                missing[1] ? null! : fake.GetStateRootPath,
                missing[2] ? null! : fake.FormatInvalidProvider,
                missing[3] ? null! : fake.ReportAuthorization,
                missing[4] ? null! : fake.OpenBrowser,
                missing[5] ? null! : fake.FormatCompletionPrefix,
                missing[6] ? null! : fake.OnCompleted,
                missing[7] ? null! : fake.CreateOperation,
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));

        var names = new[]
        {
            "definition", "getStateRootPath", "formatInvalidProvider", "reportAuthorization",
            "openBrowser", "formatCompletionPrefix", "onCompleted", "createOperation",
        };
        Assert.AreEqual(names[index], failure.ParamName);
        Assert.IsEmpty(fake.Events);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow(" ")]
    [DataRow("\t")]
    [DataRow("Codex")]
    [DataRow("CODEX")]
    [DataRow(" codex")]
    [DataRow("codex ")]
    [DataRow("codex-subscription")]
    [DataRow("other")]
    public async Task ProviderType_RequiresExactOrdinalCodex(string? providerType)
    {
        var fake = new RecordingBrowserLogin();
        fake.Definition.ProviderType = providerType;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fake.InvokeAsync(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.AreEqual("fixture invalid provider", failure.Message);
        CollectionAssert.AreEqual(new[] { "format-invalid" }, fake.Events);
    }

    [TestMethod]
    public async Task Mismatch_UsesSuppliedLocalizedMessage()
    {
        var fake = new RecordingBrowserLogin();
        fake.Definition.ProviderType = "wrong";
        fake.InvalidProviderText = () => SR.T("Select a Codex provider first.");

        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fake.InvokeAsync().WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.AreEqual(SR.T("Select a Codex provider first."), failure.Message);
        CollectionAssert.AreEqual(new[] { "format-invalid" }, fake.Events);
    }

    [TestMethod]
    public async Task MatchingType_ForwardsOriginalReferencesAndTokenWithoutConsumingRoot()
    {
        var fake = new RecordingBrowserLogin();
        fake.RootValue = () => throw new AssertFailedException("Core must not consume root.");
        fake.InvalidProviderText = () => throw new AssertFailedException("Matching type must not format mismatch.");
        using var cancellation = new CancellationTokenSource();

        var task = fake.InvokeAsync(cancellation.Token);

        try
        {
            Assert.IsTrue(task.IsCompletedSuccessfully);
        }
        finally
        {
            await ObserveAsync(task);
        }
        Assert.AreSame(fake.Definition, fake.ObservedDefinition);
        Assert.AreSame(fake.GetStateRootPath, fake.ObservedRootCallback);
        Assert.AreSame(fake.ReportAuthorization, fake.ObservedReportCallback);
        Assert.AreSame(fake.OpenBrowser, fake.ObservedOpenCallback);
        Assert.AreSame(fake.FormatCompletionPrefix, fake.ObservedPrefixCallback);
        Assert.AreSame(fake.OnCompleted, fake.ObservedCompletionCallback);
        Assert.AreEqual(cancellation.Token, fake.ObservedToken);
        Assert.AreEqual(cancellation.Token, fake.ObservedWaitToken);
        Assert.AreSame(fake.AuthorizationUri, fake.ReportedUri);
        Assert.AreSame(fake.AuthorizationUri, fake.OpenedUri);
        CollectionAssert.AreEqual(new[]
        {
            "factory", "operation", "synthetic-begin", "synthetic-start-wait", "report", "open", "prefix", "synthetic-project-id", "completion",
        }, fake.Events);
    }

    [TestMethod]
    public async Task Factory_ControlsLazyRootKeyAndConfiguredAccountConsumption()
    {
        var fake = new RecordingBrowserLogin();
        var rootValue = " first synthetic root ";
        fake.RootValue = () =>
        {
            fake.Definition.ProviderKey = " changed synthetic key ";
            fake.Definition.AccountId = " changed synthetic account ";
            return rootValue;
        };
        fake.FactoryBody = (definition, getRoot) =>
        {
            Assert.AreEqual(" first synthetic root ", getRoot());
            Assert.AreEqual(" changed synthetic key ", definition.ProviderKey);
            rootValue = " second synthetic root ";
            Assert.AreEqual(" second synthetic root ", getRoot());
            return fake.InvokeOperation;
        };

        await ObserveAsync(fake.InvokeAsync());

        Assert.AreEqual(" changed synthetic account ", fake.ObservedConfiguredAccountId);
        CollectionAssert.AreEqual(new[]
        {
            "factory", "root", "root", "operation", "synthetic-begin", "synthetic-start-wait", "report", "open", "prefix", "synthetic-project-id", "completion",
        }, fake.Events);
    }

    [TestMethod]
    [DataRow(null, "synthetic-key")]
    [DataRow("synthetic-root", null)]
    [DataRow("", "")]
    [DataRow(" \t ", " \t ")]
    [DataRow(" raw synthetic root ", " raw synthetic/key ")]
    public async Task RootAndKey_AreNotNormalizedByCore(string? rootValue, string? providerKey)
    {
        var fake = new RecordingBrowserLogin();
        // Intentional invalid synthetic values despite non-null annotations; only the fake sees them.
        fake.RootValue = () => rootValue!;
        fake.Definition.ProviderKey = providerKey!;
        fake.FactoryBody = (definition, getRoot) =>
        {
            Assert.AreEqual(rootValue, getRoot());
            Assert.AreEqual(providerKey, definition.ProviderKey);
            return fake.InvokeOperation;
        };

        await ObserveAsync(fake.InvokeAsync());

        CollectionAssert.AreEqual(new[]
        {
            "factory", "root", "operation", "synthetic-begin", "synthetic-start-wait", "report", "open", "prefix", "synthetic-project-id", "completion",
        }, fake.Events);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow(" \t ")]
    [DataRow("synthetic-account")]
    [DataRow(" padded synthetic account ")]
    public async Task ConfiguredAccountId_IsForwardedRawAtBeginAfterFactory(string? accountId)
    {
        var fake = new RecordingBrowserLogin();
        fake.Definition.AccountId = "before factory";
        fake.FactoryBody = (definition, _) =>
        {
            definition.AccountId = accountId;
            return fake.InvokeOperation;
        };

        await ObserveAsync(fake.InvokeAsync());

        // This is fake-controlled consumption; provider URI query trimming remains source-only.
        Assert.AreEqual(accountId, fake.ObservedConfiguredAccountId);
        Assert.AreSame(fake.Definition, fake.ObservedDefinition);
    }

    [TestMethod]
    [DataRow(null, null)]
    [DataRow("unused", false)]
    [DataRow(" arbitrary synthetic source ", true)]
    public async Task IrrelevantConfiguration_DoesNotAddEligibility(string? authSource, bool? enabled)
    {
        var fake = new RecordingBrowserLogin();
        fake.Definition.AuthSource = authSource;
        fake.Definition.Enabled = enabled;

        await ObserveAsync(fake.InvokeAsync());

        CollectionAssert.AreEqual(new[]
        {
            "factory", "operation", "synthetic-begin", "synthetic-start-wait", "report", "open", "prefix", "synthetic-project-id", "completion",
        }, fake.Events);
    }

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
    [DataRow("success")]
    [DataRow("fault")]
    [DataRow("cancel")]
    public async Task AlreadyCompletedWait_IsObservedAfterReportingAndOpening(string outcome)
    {
        var fake = new RecordingBrowserLogin();
        var failure = new InvalidOperationException("synthetic wait failure");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        // Independently retained before invocation, including faulted/canceled tasks.
        var wait = CompletedWait(outcome, failure, cancellation.Token);
        fake.WaitBody = _ => wait;
        Task? route = null;
        try
        {
            route = fake.InvokeAsync();
            await ObserveAsync(route, outcome == "fault" ? failure : null, outcome == "cancel");
            CollectionAssert.AreEqual(outcome == "success"
                ? new[] { "factory", "operation", "synthetic-begin", "synthetic-start-wait", "report", "open", "prefix", "synthetic-project-id", "completion" }
                : new[] { "factory", "operation", "synthetic-begin", "synthetic-start-wait", "report", "open" }, fake.Events);
        }
        finally
        {
            await JoinBothAsync(route, wait, outcome == "fault" ? failure : null, outcome == "cancel",
                outcome == "fault" ? failure : null, outcome == "cancel");
        }
    }

    [TestMethod]
    [DataRow("report")]
    [DataRow("opener")]
    public async Task CallbackFailure_WinsOverAlreadyFaultedWait(string callback)
    {
        var fake = new RecordingBrowserLogin();
        var callbackFailure = new InvalidOperationException("synthetic callback failure");
        var waitFailure = new InvalidOperationException("synthetic wait failure");
        var wait = Task.FromException(waitFailure);
        fake.WaitBody = _ => wait;
        SetCallbackFailure(fake, callback, callbackFailure);
        Task? route = null;
        try
        {
            route = fake.InvokeAsync();
            await ObserveAsync(route, callbackFailure);
            AssertCallbackEvents(fake, callback);
        }
        finally
        {
            await JoinBothAsync(route, wait, callbackFailure, false, waitFailure, false);
        }
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
    [DataRow(false)]
    [DataRow(true)]
    public async Task PrecanceledToken_ReachesIgnoringAndCooperativeWait(bool cooperative)
    {
        var fake = new RecordingBrowserLogin();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var wait = cooperative ? Task.FromCanceled(cancellation.Token) : Task.CompletedTask;
        fake.WaitBody = token =>
        {
            Assert.AreEqual(cancellation.Token, token);
            return wait;
        };
        Task? route = null;
        try
        {
            route = fake.InvokeAsync(cancellation.Token);
            await ObserveAsync(route, canceled: cooperative, expectedToken: cooperative ? cancellation.Token : null);
            Assert.AreEqual(cancellation.Token, fake.ObservedToken);
            Assert.AreEqual(cancellation.Token, fake.ObservedWaitToken);
            CollectionAssert.AreEqual(cooperative
                ? new[] { "factory", "operation", "synthetic-begin", "synthetic-start-wait", "report", "open" }
                : new[] { "factory", "operation", "synthetic-begin", "synthetic-start-wait", "report", "open", "prefix", "synthetic-project-id", "completion" }, fake.Events);
        }
        finally
        {
            await JoinBothAsync(route, wait, routeCanceled: cooperative, waitCanceled: cooperative);
        }
    }

    [TestMethod]
    public async Task PendingWait_CooperativelyCancelsWithOriginalToken()
    {
        var fake = new RecordingBrowserLogin();
        using var cancellation = new CancellationTokenSource();
        var gate = NewGate();
        var wait = gate.Task;
        using var registration = cancellation.Token.Register(() => gate.TrySetCanceled(cancellation.Token));
        fake.WaitBody = token =>
        {
            Assert.AreEqual(cancellation.Token, token);
            return wait;
        };
        Task? route = null;
        try
        {
            route = fake.InvokeAsync(cancellation.Token);
            Assert.IsFalse(route.IsCompleted);
            Assert.IsFalse(wait.IsCompleted);
            cancellation.Cancel();
            await ObserveAsync(route, canceled: true, expectedToken: cancellation.Token);
            CollectionAssert.AreEqual(new[] { "factory", "operation", "synthetic-begin", "synthetic-start-wait", "report", "open" }, fake.Events);
        }
        finally
        {
            cancellation.Cancel();
            await JoinBothAsync(route, wait, routeCanceled: true, waitCanceled: true);
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
            => Coordinator.LoginCodexBrowserCoreAsync(
                Definition, GetStateRootPath, FormatInvalidProvider, ReportAuthorization, OpenBrowser,
                FormatCompletionPrefix, OnCompleted, CreateOperation, cancellationToken);
    }
}
