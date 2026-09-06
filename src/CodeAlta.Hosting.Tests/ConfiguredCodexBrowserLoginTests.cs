using CodeAlta.Catalog;
using Authentication = CodeAlta.Hosting.ConfiguredCodexAuthentication;

namespace CodeAlta.Hosting.Tests;

// Static core/helpers and instance-owned recording operations only; no coordinator/provider execution.
// Synthetic begin/wait events describe recording behavior, NOT real Begin/listener/protocol coverage.
// No credential, browser-context, PKCE or protocol records, concrete clients/stores/hosts/processes,
// resolver, profile/environment discovery or credential I/O. URI values are inert display examples.
// Production may abandon its wait after a callback failure; fixtures independently retain and join theirs.
// SR may read output-directory localization; existing writerless logging remains, not zero I/O.
// Public cases fail required-null guards before any concrete factory; mismatch is a secondary barrier only.
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
            Authentication.LoginWithBrowserAsync(
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
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(5)]
    [DataRow(6)]
    [DataRow(7)]
    public async Task PublicRequiredArguments_FaultBeforeConcreteFactory(int index)
    {
        // Seven individual required-null barriers plus all-null; never a public success/type-only probe.
        var definition = new CodeAltaProviderDocument { ProviderType = "synthetic-nonmatching", ProviderKey = "synthetic-key" };
        Func<string> root = () => throw new AssertFailedException("Public null guard must precede root consumption.");
        Func<string> mismatch = () => throw new AssertFailedException("Required-null guard must precede mismatch formatting.");
        Action<Uri> report = _ => throw new AssertFailedException("Public null guard must precede reporting.");
        Action<Uri> opener = _ => throw new AssertFailedException("Public null guard must precede opening.");
        Func<string> prefix = () => throw new AssertFailedException("Public null guard must precede prefix formatting.");
        Action<string, string?> completion = (_, _) => throw new AssertFailedException("Public null guard must precede completion.");
        var task = Authentication.LoginWithBrowserAsync(
            index is 0 or 7 ? null! : definition,
            index is 1 or 7 ? null! : root,
            index is 2 or 7 ? null! : mismatch,
            index is 3 or 7 ? null! : report,
            index is 4 or 7 ? null! : opener,
            index is 5 or 7 ? null! : prefix,
            index is 6 or 7 ? null! : completion,
            CancellationToken.None);
        try
        {
            Assert.IsTrue(task.IsFaulted);
        }
        finally
        {
            var failure = await Assert.ThrowsExactlyAsync<ArgumentNullException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
            var names = new[]
            {
                "definition", "getStateRootPath", "formatInvalidProvider", "reportAuthorization",
                "openBrowser", "formatCompletionPrefix", "onCompleted", "definition",
            };
            Assert.AreEqual(names[index], failure.ParamName);
        }
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
