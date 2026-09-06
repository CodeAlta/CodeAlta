using System.Globalization;
using CodeAlta.Agent.Xai;
using CodeAlta.Catalog;
using Coordinator = CodeAlta.Tui.App.ProviderFrontendCoordinator;

namespace CodeAlta.Tests;

// Production-connected pre-move cores only, never a coordinator instance. Every factory returns
// an in-memory recording delegate; the only provider objects are inert public display/options records.
// No manager, client, listener, store, runtime, credential data or root discovery is used here.
// SR may read output-directory localization content; assembly-level writerless logging still applies.
[TestClass]
public sealed class ConfiguredXaiAuthenticationTests
{
    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow(" ")]
    [DataRow("Xai")]
    [DataRow("XAI")]
    [DataRow(" xai")]
    [DataRow("xai ")]
    [DataRow("xai-direct")]
    [DataRow("other")]
    public async Task ProviderType_RequiresExactOrdinalXai(string? providerType)
    {
        foreach (var operation in new[] { "browser", "device", "delete", "status" })
        {
            var fake = new RecordingOperations();
            fake.Definition.ProviderType = providerType;
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => fake.InvokeAsync(operation, cancellation.Token));

            Assert.AreEqual("fixture invalid provider", failure.Message);
            CollectionAssert.AreEqual(new[] { "format-invalid" }, fake.Events);
        }
    }

    [TestMethod]
    public async Task Mismatch_UsesSuppliedLocalizedMessageOnlyOnInvalidProvider()
    {
        foreach (var operation in new[] { "browser", "device", "delete", "status" })
        {
            var fake = new RecordingOperations();
            fake.Definition.ProviderType = "wrong";
            fake.InvalidProviderText = () => SR.T("Select an xAI provider first.");

            var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fake.InvokeAsync(operation));

            Assert.AreEqual(SR.T("Select an xAI provider first."), failure.Message);
            CollectionAssert.AreEqual(new[] { "format-invalid" }, fake.Events);
        }
    }

    [TestMethod]
    [DataRow("browser")]
    [DataRow("device")]
    [DataRow("delete")]
    [DataRow("status")]
    public async Task RequiredArguments_ValidateEachInputAndEveryPrecedenceSuffixBeforeTypeOrWork(string operation)
    {
        var arguments = operation == "browser"
            ? new[] { "definition", "onAuthorize", "getStateRootPath", "formatInvalidProvider", "createOperation" }
            : operation == "device"
                ? new[] { "definition", "onDeviceCode", "getStateRootPath", "formatInvalidProvider", "createOperation" }
                : new[] { "definition", "getStateRootPath", "formatInvalidProvider", "createOperation" };
        for (var index = 0; index < arguments.Length; index++)
        {
            foreach (var nullSuffix in new[] { false, true })
            {
                var fake = new RecordingOperations();
                fake.Definition.ProviderType = "wrong";
                for (var missing = index; missing < (nullSuffix ? arguments.Length : index + 1); missing++)
                {
                    fake.SetNullArgument(arguments[missing]);
                }

                var failure = await Assert.ThrowsExactlyAsync<ArgumentNullException>(() => fake.InvokeAsync(operation));

                Assert.AreEqual(arguments[index], failure.ParamName);
                Assert.IsEmpty(fake.Events);
            }
        }
    }

    [TestMethod]
    [DataRow("browser")]
    [DataRow("device")]
    [DataRow("delete")]
    [DataRow("status")]
    public async Task MatchingType_ConstructsThenResolvesRootThenInvokesSelectedOperationImmediately(string operation)
    {
        var fake = new RecordingOperations();
        fake.InvalidProviderText = () => throw new AssertFailedException("Matching type must not format an error.");
        using var cancellation = new CancellationTokenSource();

        var task = fake.InvokeAsync(operation, cancellation.Token);

        Assert.IsTrue(task.IsCompletedSuccessfully);
        Assert.AreSame(operation == "delete" ? null : fake.Result, await task);
        CollectionAssert.AreEqual(new[] { "factory", "root", "operation" }, fake.Events);
        Assert.AreEqual(operation, fake.ObservedFactory);
        Assert.AreEqual(operation, fake.ObservedOperation);
        Assert.AreEqual(cancellation.Token, fake.ObservedToken);
        Assert.AreSame(operation == "browser" ? fake.BrowserCallback : null, fake.ObservedBrowserCallback);
        Assert.AreSame(operation == "device" ? fake.DeviceCallback : null, fake.ObservedDeviceCallback);
        Assert.IsNotNull(fake.ObservedOptions);
        Assert.AreEqual("fixture-provider", fake.ObservedOptions.ProviderKey);
        Assert.AreEqual("inert-root", fake.ObservedOptions.StateRootPath);
        Assert.IsNull(fake.ObservedOptions.BaseUri);
        Assert.IsNull(fake.ObservedOptions.PollingIntervalOverride);
    }

    [TestMethod]
    [DataRow("browser")]
    [DataRow("device")]
    [DataRow("delete")]
    [DataRow("status")]
    public async Task OptionEvaluation_ObservesFactoryChangesAndKeyBeforeRootChanges(string operation)
    {
        var fake = new RecordingOperations();
        fake.OnFactory = () =>
        {
            fake.Definition.ProviderType = "changed-after-type-check";
            fake.Definition.ProviderKey = "key-from-factory";
            fake.Definition.ApiUrl = "https://example.invalid/before-root";
        };
        fake.RootValue = () =>
        {
            fake.Definition.ProviderKey = "key-after-root";
            fake.Definition.ApiUrl = "urn:fixture:after-root";
            return " root-with-spaces ";
        };

        await fake.InvokeAsync(operation);

        CollectionAssert.AreEqual(new[] { "factory", "root", "operation" }, fake.Events);
        Assert.IsNotNull(fake.ObservedOptions);
        Assert.AreEqual("key-from-factory", fake.ObservedOptions.ProviderKey);
        Assert.AreEqual(" root-with-spaces ", fake.ObservedOptions.StateRootPath);
        Assert.AreEqual(new Uri("urn:fixture:after-root"), fake.ObservedOptions.BaseUri);
    }

    [TestMethod]
    [DataRow(null, null, null, null)]
    [DataRow("", "", "", null)]
    [DataRow(" key ", " root ", " ", null)]
    [DataRow(" ", " ", "relative/path", null)]
    [DataRow("fixture", "inert-root", "https://[", null)]
    [DataRow("fixture", "inert-root", "https://example.invalid/api", "https://example.invalid/api")]
    [DataRow("fixture", "inert-root", "file:///inert/fixture", "file:///inert/fixture")]
    [DataRow("fixture", "inert-root", "mailto:fixture@example.invalid", "mailto:fixture@example.invalid")]
    [DataRow("fixture", "inert-root", "urn:fixture:xai", "urn:fixture:xai")]
    public async Task Options_ForwardInertValuesWithoutExtraValidationOrNormalization(
        string? key, string? root, string? apiUrl, string? expectedApiUrl)
    {
        foreach (var operation in new[] { "browser", "device", "delete", "status" })
        {
            var fake = new RecordingOperations();
            // Deliberate runtime nulls: the unconstructed provider owns key and blank-root policy.
            fake.Definition.ProviderKey = key!;
            fake.RootValue = () => root!;
            fake.Definition.ApiUrl = apiUrl;

            await fake.InvokeAsync(operation);

            Assert.IsNotNull(fake.ObservedOptions);
            Assert.AreSame(key, fake.ObservedOptions.ProviderKey);
            Assert.AreSame(root, fake.ObservedOptions.StateRootPath);
            Assert.AreEqual(expectedApiUrl is null ? null : new Uri(expectedApiUrl), fake.ObservedOptions.BaseUri);
            Assert.IsNull(fake.ObservedOptions.PollingIntervalOverride);
            CollectionAssert.AreEqual(new[] { "factory", "root", "operation" }, fake.Events);
        }
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("codealta_oauth")]
    [DataRow("api_key_env")]
    [DataRow("unrecognized-fixture-mode")]
    public async Task OtherSettings_DoNotAffectConfiguredOperations(string? authSource)
    {
        foreach (var operation in new[] { "browser", "device", "delete", "status" })
        {
            foreach (var enabled in new bool?[] { null, false, true })
            {
                var fake = new RecordingOperations();
                fake.Definition.Enabled = enabled;
                fake.Definition.AuthSource = authSource;
                fake.Definition.Model = "unused-fixture-model";
                fake.Definition.AccountId = "unused-fixture-account";
                fake.Definition.GitHubEnterpriseUrl = "unused-fixture-enterprise";
                Assert.IsNull(fake.Definition.ApiKey);
                Assert.IsNull(fake.Definition.ApiKeyEnv);

                await fake.InvokeAsync(operation);

                CollectionAssert.AreEqual(new[] { "factory", "root", "operation" }, fake.Events);
                Assert.IsNotNull(fake.ObservedOptions);
                Assert.IsNull(fake.ObservedOptions.BaseUri);
                Assert.IsNull(fake.ObservedOptions.PollingIntervalOverride);
            }
        }
    }

    [TestMethod]
    [DataRow("null")]
    [DataRow("unknown-expiry")]
    [DataRow("past-expiry")]
    public async Task Status_ReturnsExactNullableOrPastResultWithoutLoginOrExpiryPolicy(string status)
    {
        var fake = new RecordingOperations();
        var result = status == "null" ? null : new XaiDirectLoginResult(
            new Uri("https://example.invalid/cached"),
            status == "past-expiry" ? new DateTimeOffset(2000, 1, 2, 3, 4, 5, TimeSpan.Zero) : null,
            " fixture-scope ");
        fake.StatusBody = (_, _) => ValueTask.FromResult(result);
        fake.BrowserBody = (_, _, _) => throw new AssertFailedException("Status must not log in.");
        fake.DeviceBody = (_, _, _) => throw new AssertFailedException("Status must not log in.");

        Assert.AreSame(result, await fake.InvokeAsync("status"));
        Assert.AreEqual("status", fake.ObservedOperation);
        CollectionAssert.AreEqual(new[] { "factory", "root", "operation" }, fake.Events);
    }

    [TestMethod]
    [DataRow("mismatch")]
    [DataRow("formatter")]
    [DataRow("factory")]
    [DataRow("root")]
    public async Task PreOperationFailures_PreserveIdentityAndPrecedence(string stage)
    {
        foreach (var operation in new[] { "browser", "device", "delete", "status" })
        {
            var fake = new RecordingOperations();
            var failure = new InvalidOperationException("fixture " + stage);
            fake.FailOperations(new InvalidOperationException("later operation failure"), synchronous: true);
            fake.RootValue = () => throw failure;
            if (stage is "mismatch" or "formatter" or "factory")
            {
                fake.OnFactory = () => throw failure;
            }

            if (stage is "mismatch" or "formatter")
            {
                fake.Definition.ProviderType = "wrong";
            }

            if (stage == "formatter")
            {
                fake.InvalidProviderText = () => throw failure;
            }

            var actual = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fake.InvokeAsync(operation));

            if (stage == "mismatch")
            {
                Assert.AreEqual("fixture invalid provider", actual.Message);
                Assert.AreNotSame(failure, actual);
            }
            else
            {
                Assert.AreSame(failure, actual);
            }

            var expected = stage is "mismatch" or "formatter" ? new[] { "format-invalid" }
                : stage == "factory" ? new[] { "factory" } : new[] { "factory", "root" };
            CollectionAssert.AreEqual(expected, fake.Events);
        }
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task OperationFailures_PropagateSynchronousAndFaultedValueTaskIdentity(bool synchronous)
    {
        foreach (var operation in new[] { "browser", "device", "delete", "status" })
        {
            var fake = new RecordingOperations();
            var failure = new InvalidOperationException("fixture operation failure");
            fake.FailOperations(failure, synchronous);

            var actual = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fake.InvokeAsync(operation));

            Assert.AreSame(failure, actual);
            CollectionAssert.AreEqual(new[] { "factory", "root", "operation" }, fake.Events);
        }
    }

    [TestMethod]
    [DataRow("browser")]
    [DataRow("device")]
    [DataRow("delete")]
    [DataRow("status")]
    public async Task PreCanceledToken_ReachesIgnoringFakeWithoutEarlyCancellation(string operation)
    {
        var fake = new RecordingOperations();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await fake.InvokeAsync(operation, cancellation.Token);

        Assert.AreEqual(cancellation.Token, fake.ObservedToken);
        Assert.AreSame(operation == "delete" ? null : fake.Result, result);
        CollectionAssert.AreEqual(new[] { "factory", "root", "operation" }, fake.Events);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task CancellationException_PropagatesIdentityAndOriginalToken(bool synchronous)
    {
        foreach (var operation in new[] { "browser", "device", "delete", "status" })
        {
            var fake = new RecordingOperations();
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var failure = new OperationCanceledException(cancellation.Token);
            fake.FailOperations(failure, synchronous);

            var actual = await Assert.ThrowsExactlyAsync<OperationCanceledException>(
                () => fake.InvokeAsync(operation, cancellation.Token));

            Assert.AreSame(failure, actual);
            Assert.AreEqual(cancellation.Token, actual.CancellationToken);
            CollectionAssert.AreEqual(new[] { "factory", "root", "operation" }, fake.Events);
        }
    }

    [TestMethod]
    [DataRow("browser", false)]
    [DataRow("device", false)]
    [DataRow("delete", false)]
    [DataRow("status", false)]
    [DataRow("browser", true)]
    [DataRow("device", true)]
    [DataRow("delete", true)]
    [DataRow("status", true)]
    public async Task PendingOperation_WaitsForManualCompletionOrCooperativeCancellation(string operation, bool cancel)
    {
        var fake = new RecordingOperations();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        fake.WaitInOperations(entered, release);
        var task = fake.InvokeAsync(operation, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(task.IsCompleted);
            CollectionAssert.AreEqual(new[] { "factory", "root", "operation" }, fake.Events);
            if (cancel)
            {
                cancellation.Cancel();
                var failure = await Assert.ThrowsAsync<OperationCanceledException>(
                    () => task.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.AreEqual(cancellation.Token, failure.CancellationToken);
                Assert.AreEqual(cancellation.Token, fake.ObservedToken);
                CollectionAssert.AreEqual(new[] { "factory", "root", "operation" }, fake.Events);
            }
            else
            {
                release.TrySetResult();
                Assert.AreSame(operation == "delete" ? null : fake.Result, await task.WaitAsync(TimeSpan.FromSeconds(5)));
                CollectionAssert.AreEqual(new[] { "factory", "root", "operation", "operation-completed" }, fake.Events);
            }
        }
        finally
        {
            release.TrySetResult();
            try
            {
                await task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (OperationCanceledException) when (cancel && cancellation.IsCancellationRequested)
            {
                // Observe deliberate cancellation, including when an earlier assertion fails.
            }
        }
    }

    [TestMethod]
    [DataRow("browser")]
    [DataRow("device")]
    public async Task Login_ForwardsDistinctOriginalCallbackPayloadAndProviderChosenToken(string operation)
    {
        var fake = new RecordingOperations();
        using var operationCancellation = new CancellationTokenSource();
        using var callbackCancellation = new CancellationTokenSource();
        callbackCancellation.Cancel();
        fake.BrowserCallback = (actual, token) =>
        {
            Assert.AreEqual("browser", operation);
            Assert.AreSame(fake.Authorization, actual);
            Assert.AreEqual(callbackCancellation.Token, token);
            fake.Events.Add("browser-callback");
            return ValueTask.CompletedTask;
        };
        fake.DeviceCallback = (actual, token) =>
        {
            Assert.AreEqual("device", operation);
            Assert.AreSame(fake.DeviceCode, actual);
            Assert.AreEqual(callbackCancellation.Token, token);
            fake.Events.Add("device-callback");
            return ValueTask.CompletedTask;
        };
        fake.CallLoginCallbacks(callbackCancellation.Token);

        var task = fake.InvokeAsync(operation, operationCancellation.Token);

        Assert.IsTrue(task.IsCompletedSuccessfully);
        Assert.AreSame(fake.Result, await task);
        Assert.AreEqual(operationCancellation.Token, fake.ObservedToken);
        Assert.AreSame(operation == "browser" ? fake.BrowserCallback : null, fake.ObservedBrowserCallback);
        Assert.AreSame(operation == "device" ? fake.DeviceCallback : null, fake.ObservedDeviceCallback);
        CollectionAssert.AreEqual(new[] { "factory", "root", "operation", operation + "-callback", "after-callback" }, fake.Events);
    }

    [TestMethod]
    [DataRow("browser", true)]
    [DataRow("device", true)]
    [DataRow("browser", false)]
    [DataRow("device", false)]
    public async Task Login_CallbackFailurePreventsLaterOperationFailure(string operation, bool synchronous)
    {
        var fake = new RecordingOperations();
        var failure = new InvalidOperationException("fixture callback failure");
        ValueTask FailCallback()
        {
            fake.Events.Add("callback");
            return synchronous ? throw failure : ValueTask.FromException(failure);
        }

        fake.BrowserCallback = (_, _) => FailCallback();
        fake.DeviceCallback = (_, _) => FailCallback();
        fake.CallLoginCallbacks(default, new InvalidOperationException("later operation failure"));

        var actual = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fake.InvokeAsync(operation));

        Assert.AreSame(failure, actual);
        CollectionAssert.AreEqual(new[] { "factory", "root", "operation", "callback" }, fake.Events);
    }

    [TestMethod]
    [DataRow("browser", false)]
    [DataRow("device", false)]
    [DataRow("browser", true)]
    [DataRow("device", true)]
    public async Task Login_WaitsForManuallyReleasedOrCanceledCallback(string operation, bool cancel)
    {
        var fake = new RecordingOperations();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var callbackCancellation = new CancellationTokenSource();
        async ValueTask WaitInCallback(CancellationToken token)
        {
            fake.Events.Add("callback");
            Assert.AreEqual(callbackCancellation.Token, token);
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            fake.Events.Add("callback-completed");
        }

        fake.BrowserCallback = (_, token) => WaitInCallback(token);
        fake.DeviceCallback = (_, token) => WaitInCallback(token);
        fake.CallLoginCallbacks(callbackCancellation.Token);
        var task = fake.InvokeAsync(operation);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(task.IsCompleted);
            CollectionAssert.AreEqual(new[] { "factory", "root", "operation", "callback" }, fake.Events);
            if (cancel)
            {
                callbackCancellation.Cancel();
                var failure = await Assert.ThrowsAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.AreEqual(callbackCancellation.Token, failure.CancellationToken);
                CollectionAssert.AreEqual(new[] { "factory", "root", "operation", "callback" }, fake.Events);
            }
            else
            {
                release.TrySetResult();
                Assert.AreSame(fake.Result, await task.WaitAsync(TimeSpan.FromSeconds(5)));
                CollectionAssert.AreEqual(new[] { "factory", "root", "operation", "callback", "callback-completed", "after-callback" }, fake.Events);
            }
        }
        finally
        {
            release.TrySetResult();
            try
            {
                await task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (OperationCanceledException) when (cancel && callbackCancellation.IsCancellationRequested)
            {
                // Release and observe the callback chain even when an earlier assertion fails.
            }
        }
    }

    [TestMethod]
    public async Task BrowserPrompt_ReportsLocalizedMessageBeforeFakeBrowserAttempt()
    {
        var authorization = new XaiDirectBrowserAuthorization(new Uri("https://example.invalid/authorize"));
        var events = new List<string>();

        await Coordinator.ReportXaiDirectBrowserAuthorization(
            authorization,
            message =>
            {
                Assert.AreEqual(SR.T("Opening xAI login in your browser: {0}. Waiting for authorization...", authorization.AuthorizeUri), message);
                events.Add("report");
            },
            uri =>
            {
                Assert.AreSame(authorization.AuthorizeUri, uri);
                events.Add("browser");
            });

        CollectionAssert.AreEqual(new[] { "report", "browser" }, events);
    }

    [TestMethod]
    public void BrowserPrompt_ReportFailurePreventsBrowserAttempt()
    {
        var failure = new InvalidOperationException("fixture report failure");
        var events = new List<string>();

        var actual = Assert.ThrowsExactly<InvalidOperationException>(() => Coordinator.ReportXaiDirectBrowserAuthorization(
            new XaiDirectBrowserAuthorization(new Uri("https://example.invalid/authorize")),
            _ =>
            {
                events.Add("report");
                throw failure;
            },
            _ => events.Add("browser")));

        Assert.AreSame(failure, actual);
        CollectionAssert.AreEqual(new[] { "report" }, events);
    }

    [TestMethod]
    public async Task DevicePrompt_ReportsLocalizedMessageWithoutBrowserCapability()
    {
        var deviceCode = CreateDeviceCode();
        var messages = new List<string>();

        await Coordinator.ReportXaiDirectDeviceCode(deviceCode, messages.Add);

        CollectionAssert.AreEqual(new[]
        {
            SR.T("Open {0} and enter code {1}. Waiting for xAI authorization...", deviceCode.VerificationUri, deviceCode.UserCode),
        }, messages);
    }

    [TestMethod]
    public void DevicePrompt_ReportFailurePropagatesUnchanged()
    {
        var failure = new InvalidOperationException("fixture report failure");

        var actual = Assert.ThrowsExactly<InvalidOperationException>(
            () => Coordinator.ReportXaiDirectDeviceCode(CreateDeviceCode(), _ => throw failure));

        Assert.AreSame(failure, actual);
    }

    [TestMethod]
    [DataRow(null, false)]
    [DataRow("", false)]
    [DataRow(" \t ", false)]
    [DataRow(" fixture-scope ", false)]
    [DataRow(null, true)]
    [DataRow("", true)]
    [DataRow(" \t ", true)]
    [DataRow(" fixture-scope ", true)]
    public void ResultMessage_PreservesLocalizedScopeAndCurrentCultureExpiryFormatting(string? scope, bool hasExpiry)
    {
        var result = new XaiDirectLoginResult(
            new Uri("https://example.invalid/api"),
            hasExpiry ? new DateTimeOffset(2000, 1, 2, 3, 4, 5, TimeSpan.Zero) : null,
            scope);
        var expectedExpiry = result.ExpiresAt is null ? SR.T("expiry unknown")
            : SR.T("expires {0}", result.ExpiresAt.Value.LocalDateTime.ToString("g", CultureInfo.CurrentCulture));
        var expectedScope = string.IsNullOrWhiteSpace(scope) ? SR.T("scope unknown") : SR.T("scope {0}", scope.Trim());

        foreach (var key in new[] { "xAI login completed", "xAI device login completed", "Authenticated with cached xAI credentials" })
        {
            var prefix = SR.T(key);

            var actual = Coordinator.FormatXaiDirectLoginMessage(prefix, result);

            Assert.AreEqual(SR.T("{0} · API {1} · {2} · {3}.", prefix, result.BaseUri, expectedExpiry, expectedScope), actual);
        }
    }

    private static XaiDirectDeviceCode CreateDeviceCode()
        => new(new Uri("https://example.invalid/device"), "FAKE-DISPLAY-CODE", new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero));

    // Instance-owned, resource-free recording only. Separate typed browser/device paths are intentional.
    private sealed class RecordingOperations
    {
        public RecordingOperations()
        {
            RootCallback = ReadRoot;
            InvalidProviderFormatter = FormatInvalidProvider;
            BrowserFactory = CreateBrowser;
            DeviceFactory = CreateDevice;
            DeleteFactory = CreateDelete;
            StatusFactory = CreateStatus;
            BrowserBody = (_, _, _) => ValueTask.FromResult(Result);
            DeviceBody = (_, _, _) => ValueTask.FromResult(Result);
            DeleteBody = (_, _) => ValueTask.CompletedTask;
            StatusBody = (_, _) => ValueTask.FromResult<XaiDirectLoginResult?>(Result);
        }

        public List<string> Events { get; } = [];
        public CodeAltaProviderDocument Definition { get; } = new() { ProviderKey = "fixture-provider", ProviderType = "xai" };
        public XaiDirectLoginResult Result { get; } = new(new Uri("https://example.invalid/api"), null, null);
        public XaiDirectBrowserAuthorization Authorization { get; } = new(new Uri("https://example.invalid/authorize"));
        public XaiDirectDeviceCode DeviceCode { get; } = CreateDeviceCode();
        public XaiDirectLoginOptions? ObservedOptions { get; private set; }
        public CancellationToken ObservedToken { get; private set; }
        public string? ObservedFactory { get; private set; }
        public string? ObservedOperation { get; private set; }
        public Func<XaiDirectBrowserAuthorization, CancellationToken, ValueTask>? ObservedBrowserCallback { get; private set; }
        public Func<XaiDirectDeviceCode, CancellationToken, ValueTask>? ObservedDeviceCallback { get; private set; }
        public Func<XaiDirectBrowserAuthorization, CancellationToken, ValueTask> BrowserCallback { get; set; } = (_, _) => ValueTask.CompletedTask;
        public Func<XaiDirectDeviceCode, CancellationToken, ValueTask> DeviceCallback { get; set; } = (_, _) => ValueTask.CompletedTask;
        public Func<string> RootValue { get; set; } = () => "inert-root";
        public Func<string> InvalidProviderText { get; set; } = () => "fixture invalid provider";
        public Action? OnFactory { get; set; }
        public Coordinator.XaiDirectBrowserLoginOperation BrowserBody { get; set; }
        public Coordinator.XaiDirectDeviceLoginOperation DeviceBody { get; set; }
        public Coordinator.XaiDirectDeleteCredentialOperation DeleteBody { get; set; }
        public Coordinator.XaiDirectCredentialStatusOperation StatusBody { get; set; }
        private Func<string> RootCallback { get; set; }
        private Func<string> InvalidProviderFormatter { get; set; }
        private Func<Coordinator.XaiDirectBrowserLoginOperation> BrowserFactory { get; set; }
        private Func<Coordinator.XaiDirectDeviceLoginOperation> DeviceFactory { get; set; }
        private Func<Coordinator.XaiDirectDeleteCredentialOperation> DeleteFactory { get; set; }
        private Func<Coordinator.XaiDirectCredentialStatusOperation> StatusFactory { get; set; }
        private bool UseNullDefinition { get; set; }

        public async Task<XaiDirectLoginResult?> InvokeAsync(string operation, CancellationToken cancellationToken = default)
        {
            var definition = UseNullDefinition ? null! : Definition; // Intentional invalid-input case only.
            switch (operation)
            {
                case "browser":
                    return await Coordinator.LoginXaiDirectWithBrowserCoreAsync(definition, RootCallback, InvalidProviderFormatter, BrowserFactory, BrowserCallback, cancellationToken);
                case "device":
                    return await Coordinator.LoginXaiDirectWithDeviceCodeCoreAsync(definition, RootCallback, InvalidProviderFormatter, DeviceFactory, DeviceCallback, cancellationToken);
                case "delete":
                    await Coordinator.DeleteXaiDirectCredentialCoreAsync(definition, RootCallback, InvalidProviderFormatter, DeleteFactory, cancellationToken);
                    return null;
                case "status":
                    return await Coordinator.GetXaiDirectCredentialStatusCoreAsync(definition, RootCallback, InvalidProviderFormatter, StatusFactory, cancellationToken);
                default:
                    throw new AssertFailedException("Unknown fixture operation.");
            }
        }

        public void SetNullArgument(string argument)
        {
            // Null-forgiving assignments deliberately exercise mandatory seam validation.
            switch (argument)
            {
                case "definition": UseNullDefinition = true; break;
                case "onAuthorize": BrowserCallback = null!; break;
                case "onDeviceCode": DeviceCallback = null!; break;
                case "getStateRootPath": RootCallback = null!; break;
                case "formatInvalidProvider": InvalidProviderFormatter = null!; break;
                case "createOperation": BrowserFactory = null!; DeviceFactory = null!; DeleteFactory = null!; StatusFactory = null!; break;
                default: throw new AssertFailedException("Unknown fixture argument.");
            }
        }

        public void FailOperations(Exception failure, bool synchronous)
        {
            BrowserBody = (_, _, _) => synchronous ? throw failure : ValueTask.FromException<XaiDirectLoginResult>(failure);
            DeviceBody = (_, _, _) => synchronous ? throw failure : ValueTask.FromException<XaiDirectLoginResult>(failure);
            DeleteBody = (_, _) => synchronous ? throw failure : ValueTask.FromException(failure);
            StatusBody = (_, _) => synchronous ? throw failure : ValueTask.FromException<XaiDirectLoginResult?>(failure);
        }

        public void WaitInOperations(TaskCompletionSource entered, TaskCompletionSource release)
        {
            async ValueTask WaitAsync(CancellationToken token)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                Events.Add("operation-completed");
            }

            BrowserBody = async (_, _, token) => { await WaitAsync(token); return Result; };
            DeviceBody = async (_, _, token) => { await WaitAsync(token); return Result; };
            DeleteBody = (_, token) => WaitAsync(token);
            StatusBody = async (_, token) => { await WaitAsync(token); return Result; };
        }

        public void CallLoginCallbacks(CancellationToken callbackToken, Exception? afterCallbackFailure = null)
        {
            BrowserBody = async (_, callback, _) =>
            {
                Assert.AreSame(BrowserCallback, callback);
                await callback(Authorization, callbackToken);
                Events.Add("after-callback");
                if (afterCallbackFailure is not null)
                {
                    throw afterCallbackFailure;
                }

                return Result;
            };
            DeviceBody = async (_, callback, _) =>
            {
                Assert.AreSame(DeviceCallback, callback);
                await callback(DeviceCode, callbackToken);
                Events.Add("after-callback");
                if (afterCallbackFailure is not null)
                {
                    throw afterCallbackFailure;
                }

                return Result;
            };
        }

        private string ReadRoot()
        {
            Events.Add("root");
            return RootValue();
        }

        private string FormatInvalidProvider()
        {
            Events.Add("format-invalid");
            return InvalidProviderText();
        }

        private Coordinator.XaiDirectBrowserLoginOperation CreateBrowser()
        {
            Events.Add("factory");
            ObservedFactory = "browser";
            OnFactory?.Invoke();
            return Browser;
        }

        private Coordinator.XaiDirectDeviceLoginOperation CreateDevice()
        {
            Events.Add("factory");
            ObservedFactory = "device";
            OnFactory?.Invoke();
            return Device;
        }

        private Coordinator.XaiDirectDeleteCredentialOperation CreateDelete()
        {
            Events.Add("factory");
            ObservedFactory = "delete";
            OnFactory?.Invoke();
            return Delete;
        }

        private Coordinator.XaiDirectCredentialStatusOperation CreateStatus()
        {
            Events.Add("factory");
            ObservedFactory = "status";
            OnFactory?.Invoke();
            return Status;
        }

        private void Capture(string operation, XaiDirectLoginOptions options, CancellationToken token)
        {
            Events.Add("operation");
            ObservedOperation = operation;
            ObservedOptions = options;
            ObservedToken = token;
        }

        private ValueTask<XaiDirectLoginResult> Browser(
            XaiDirectLoginOptions options, Func<XaiDirectBrowserAuthorization, CancellationToken, ValueTask> callback, CancellationToken token)
        {
            Capture("browser", options, token);
            ObservedBrowserCallback = callback;
            return BrowserBody(options, callback, token);
        }

        private ValueTask<XaiDirectLoginResult> Device(
            XaiDirectLoginOptions options, Func<XaiDirectDeviceCode, CancellationToken, ValueTask> callback, CancellationToken token)
        {
            Capture("device", options, token);
            ObservedDeviceCallback = callback;
            return DeviceBody(options, callback, token);
        }

        private ValueTask Delete(XaiDirectLoginOptions options, CancellationToken token)
        {
            Capture("delete", options, token);
            return DeleteBody(options, token);
        }

        private ValueTask<XaiDirectLoginResult?> Status(XaiDirectLoginOptions options, CancellationToken token)
        {
            Capture("status", options, token);
            return StatusBody(options, token);
        }
    }
}
