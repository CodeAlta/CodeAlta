using System.Globalization;
using CodeAlta.Agent.Copilot;
using CodeAlta.Catalog;
using Coordinator = CodeAlta.Tui.App.ProviderFrontendCoordinator;

namespace CodeAlta.Tests;

// Pre-move production cores, never a coordinator instance. All factories below return in-memory
// recording delegates: no concrete manager/client/store/runtime, credential data, or path discovery.
[TestClass]
public sealed class ConfiguredCopilotAuthenticationTests
{
    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow(" ")]
    [DataRow("Copilot")]
    [DataRow("COPILOT")]
    [DataRow(" copilot")]
    [DataRow("copilot ")]
    [DataRow("copilot-cli")]
    [DataRow("other")]
    public async Task ProviderType_RequiresExactOrdinalCopilot(string? providerType)
    {
        foreach (var operation in new[] { "login", "delete", "status" })
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
        foreach (var operation in new[] { "login", "delete", "status" })
        {
            var fake = new RecordingOperations();
            fake.Definition.ProviderType = "wrong";
            fake.InvalidProviderText = () => SR.T("Select a Copilot provider first.");

            var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fake.InvokeAsync(operation));

            Assert.AreEqual(SR.T("Select a Copilot provider first."), failure.Message);
            CollectionAssert.AreEqual(new[] { "format-invalid" }, fake.Events);
        }
    }

    [TestMethod]
    [DataRow("login")]
    [DataRow("delete")]
    [DataRow("status")]
    public async Task RequiredArguments_AreValidatedBeforeTypeCheckOrAnyWork(string operation)
    {
        var arguments = operation == "login"
            ? new[] { "definition", "onDeviceCode", "getStateRootPath", "formatInvalidProvider", "createOperation" }
            : new[] { "definition", "getStateRootPath", "formatInvalidProvider", "createOperation" };
        foreach (var argument in arguments)
        {
            var fake = new RecordingOperations();
            fake.Definition.ProviderType = "wrong";
            fake.SetNullArgument(argument);

            var failure = await Assert.ThrowsExactlyAsync<ArgumentNullException>(() => fake.InvokeAsync(operation));

            Assert.AreEqual(argument, failure.ParamName);
            Assert.IsEmpty(fake.Events);
        }
    }

    [TestMethod]
    [DataRow("login")]
    [DataRow("delete")]
    [DataRow("status")]
    public async Task NullDefinition_WinsOverAllOtherMissingArguments(string operation)
    {
        var fake = new RecordingOperations();
        foreach (var argument in new[] { "definition", "onDeviceCode", "getStateRootPath", "formatInvalidProvider", "createOperation" })
        {
            fake.SetNullArgument(argument);
        }

        var failure = await Assert.ThrowsExactlyAsync<ArgumentNullException>(() => fake.InvokeAsync(operation));

        Assert.AreEqual("definition", failure.ParamName);
        Assert.IsEmpty(fake.Events);
    }

    [TestMethod]
    public async Task LoginCallbackValidation_PrecedesTypeCheckAndOtherSeamArguments()
    {
        var fake = new RecordingOperations();
        fake.Definition.ProviderType = "wrong";
        foreach (var argument in new[] { "onDeviceCode", "getStateRootPath", "formatInvalidProvider", "createOperation" })
        {
            fake.SetNullArgument(argument);
        }

        var failure = await Assert.ThrowsExactlyAsync<ArgumentNullException>(() => fake.InvokeAsync("login"));

        Assert.AreEqual("onDeviceCode", failure.ParamName);
        Assert.IsEmpty(fake.Events);
    }

    [TestMethod]
    [DataRow("login")]
    [DataRow("delete")]
    [DataRow("status")]
    public async Task MatchingType_ConstructsThenResolvesRootThenInvokesExactlyOnce(string operation)
    {
        var fake = new RecordingOperations();
        fake.InvalidProviderText = () => throw new AssertFailedException("Matching type must not format an error.");
        using var cancellation = new CancellationTokenSource();

        var result = await fake.InvokeAsync(operation, cancellation.Token);

        CollectionAssert.AreEqual(new[] { "factory", "root", "operation" }, fake.Events);
        Assert.AreEqual(cancellation.Token, fake.ObservedToken);
        Assert.AreSame(operation == "delete" ? null : fake.Result, result);
        if (operation == "login")
        {
            Assert.AreSame(fake.Callback, fake.ObservedCallback);
        }

        Assert.IsNotNull(fake.ObservedOptions);
        Assert.AreEqual("fixture-provider", fake.ObservedOptions.ProviderKey);
        Assert.AreEqual("inert-root", fake.ObservedOptions.StateRootPath);
        Assert.IsNull(fake.ObservedOptions.DeviceFlowClientId);
        Assert.IsNull(fake.ObservedOptions.PollingIntervalOverride);
        // The bound target is disposable only to observe accidental new ownership. Neither disposal
        // method (nor any start/stop protocol) belongs to these operation-only cores.
    }

    [TestMethod]
    [DataRow("login")]
    [DataRow("delete")]
    [DataRow("status")]
    public async Task OptionEvaluation_ObservesFactoryChangesAndKeyBeforeRootChanges(string operation)
    {
        var fake = new RecordingOperations();
        fake.OnFactory = () =>
        {
            fake.Definition.ProviderType = "changed-after-type-check";
            fake.Definition.ProviderKey = "key-from-factory";
            fake.Definition.GitHubEnterpriseUrl = "enterprise-before-root";
            fake.Definition.ApiUrl = "https://before-root.invalid";
        };
        fake.RootValue = () =>
        {
            fake.Definition.ProviderKey = "key-after-root";
            fake.Definition.GitHubEnterpriseUrl = " enterprise-after-root ";
            fake.Definition.ApiUrl = "urn:fixture:after-root";
            return " root-with-spaces ";
        };

        await fake.InvokeAsync(operation);

        CollectionAssert.AreEqual(new[] { "factory", "root", "operation" }, fake.Events);
        Assert.IsNotNull(fake.ObservedOptions);
        Assert.AreEqual("key-from-factory", fake.ObservedOptions.ProviderKey);
        Assert.AreEqual(" root-with-spaces ", fake.ObservedOptions.StateRootPath);
        Assert.AreEqual(" enterprise-after-root ", fake.ObservedOptions.EnterpriseDomain);
        Assert.AreEqual(new Uri("urn:fixture:after-root"), fake.ObservedOptions.BaseUri);
    }

    [TestMethod]
    [DataRow(null, null, null, null, null)]
    [DataRow("", "", "", "", null)]
    [DataRow(" key ", " root ", " enterprise ", " ", null)]
    [DataRow(" ", " ", " ", "relative/path", null)]
    [DataRow("fixture", "inert-root", null, "https://endpoint.invalid/path", "https://endpoint.invalid/path")]
    [DataRow("fixture", "inert-root", " enterprise.invalid ", "file:///inert/fixture", "file:///inert/fixture")]
    [DataRow("fixture", "inert-root", "https://enterprise.invalid/path", "mailto:fixture@example.invalid", "mailto:fixture@example.invalid")]
    [DataRow("fixture", "inert-root", "enterprise.invalid", "urn:fixture:copilot", "urn:fixture:copilot")]
    public async Task Options_ForwardInertValuesWithoutExtraValidationOrNormalization(
        string? key, string? root, string? enterprise, string? apiUrl, string? expectedApiUrl)
    {
        foreach (var operation in new[] { "login", "delete", "status" })
        {
            var fake = new RecordingOperations();
            // Deliberately exercise runtime nulls despite the caller's non-null annotations. The
            // provider, which is never constructed here, owns key validation and blank-root policy.
            fake.Definition.ProviderKey = key!;
            fake.RootValue = () => root!;
            fake.Definition.GitHubEnterpriseUrl = enterprise;
            fake.Definition.ApiUrl = apiUrl;

            await fake.InvokeAsync(operation);

            Assert.IsNotNull(fake.ObservedOptions);
            Assert.AreSame(key, fake.ObservedOptions.ProviderKey);
            Assert.AreSame(root, fake.ObservedOptions.StateRootPath);
            Assert.AreSame(enterprise, fake.ObservedOptions.EnterpriseDomain);
            Assert.AreEqual(expectedApiUrl is null ? null : new Uri(expectedApiUrl), fake.ObservedOptions.BaseUri);
            Assert.IsNull(fake.ObservedOptions.DeviceFlowClientId);
            Assert.IsNull(fake.ObservedOptions.PollingIntervalOverride);
            CollectionAssert.AreEqual(new[] { "factory", "root", "operation" }, fake.Events);
        }
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("github_device_flow")]
    [DataRow("github_token_env")]
    [DataRow("copilot_token_env")]
    [DataRow("unrecognized-fixture-mode")]
    public async Task EnabledAndAuthMode_DoNotAffectConfiguredOperations(string? authSource)
    {
        foreach (var operation in new[] { "login", "delete", "status" })
        {
            foreach (var enabled in new bool?[] { null, false, true })
            {
                var fake = new RecordingOperations();
                fake.Definition.Enabled = enabled;
                fake.Definition.AuthSource = authSource;
                fake.Definition.Model = "unused-fixture-model";
                Assert.IsNull(fake.Definition.ApiKey);
                Assert.IsNull(fake.Definition.ApiKeyEnv);
                Assert.IsNull(fake.Definition.GitHubTokenEnv);
                Assert.IsNull(fake.Definition.CopilotTokenEnv);

                await fake.InvokeAsync(operation);

                CollectionAssert.AreEqual(new[] { "factory", "root", "operation" }, fake.Events);
                Assert.IsNotNull(fake.ObservedOptions);
                Assert.IsNull(fake.ObservedOptions.BaseUri);
            }
        }
    }

    [TestMethod]
    public async Task Status_ReturnsNullWithoutInventingResultOrInvokingLogin()
    {
        var fake = new RecordingOperations
        {
            StatusBody = (_, _) => ValueTask.FromResult<CopilotDirectLoginResult?>(null),
            LoginBody = (_, _, _) => throw new AssertFailedException("Status must not log in."),
        };

        Assert.IsNull(await fake.InvokeAsync("status"));
        CollectionAssert.AreEqual(new[] { "factory", "root", "operation" }, fake.Events);
    }

    [TestMethod]
    [DataRow("mismatch")]
    [DataRow("formatter")]
    [DataRow("factory")]
    [DataRow("root")]
    public async Task PreOperationFailures_PreserveIdentityAndPrecedence(string stage)
    {
        foreach (var operation in new[] { "login", "delete", "status" })
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
        foreach (var operation in new[] { "login", "delete", "status" })
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
    [DataRow("login")]
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
        foreach (var operation in new[] { "login", "delete", "status" })
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
    [DataRow("login")]
    [DataRow("delete")]
    [DataRow("status")]
    public async Task PendingOperation_IsAwaitedUntilManualCompletion(string operation)
    {
        var fake = new RecordingOperations();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.WaitInOperations(entered, release);
        var task = fake.InvokeAsync(operation);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(task.IsCompleted);
            CollectionAssert.AreEqual(new[] { "factory", "root", "operation" }, fake.Events);
            release.TrySetResult();

            var result = await task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.AreSame(operation == "delete" ? null : fake.Result, result);
            CollectionAssert.AreEqual(new[] { "factory", "root", "operation", "operation-completed" }, fake.Events);
        }
        finally
        {
            release.TrySetResult();
            await task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [TestMethod]
    [DataRow("login")]
    [DataRow("delete")]
    [DataRow("status")]
    public async Task PendingOperation_CooperativelyCancelsWithOriginalToken(string operation)
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
            cancellation.Cancel();

            var failure = await Assert.ThrowsAsync<OperationCanceledException>(
                () => task.WaitAsync(TimeSpan.FromSeconds(5)));

            Assert.AreEqual(cancellation.Token, failure.CancellationToken);
            Assert.AreEqual(cancellation.Token, fake.ObservedToken);
            CollectionAssert.AreEqual(new[] { "factory", "root", "operation" }, fake.Events);
        }
        finally
        {
            cancellation.Cancel();
            release.TrySetResult();
            try
            {
                await task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (OperationCanceledException)
            {
                // Observe the deliberately canceled task even when an earlier assertion fails.
            }
        }
    }

    [TestMethod]
    public async Task Login_ForwardsOriginalCallbackAndAllowsProviderChosenCallbackToken()
    {
        var fake = new RecordingOperations();
        var deviceCode = CreateDeviceCode();
        using var operationCancellation = new CancellationTokenSource();
        using var callbackCancellation = new CancellationTokenSource();
        callbackCancellation.Cancel();
        fake.Callback = (actual, token) =>
        {
            fake.Events.Add("callback");
            Assert.AreSame(deviceCode, actual);
            Assert.AreEqual(callbackCancellation.Token, token);
            return ValueTask.CompletedTask;
        };
        fake.LoginBody = async (_, callback, token) =>
        {
            Assert.AreSame(fake.Callback, callback);
            Assert.AreEqual(operationCancellation.Token, token);
            await callback(deviceCode, callbackCancellation.Token);
            fake.Events.Add("after-callback");
            return fake.Result;
        };

        Assert.AreSame(fake.Result, await fake.InvokeAsync("login", operationCancellation.Token));
        CollectionAssert.AreEqual(new[] { "factory", "root", "operation", "callback", "after-callback" }, fake.Events);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Login_CallbackFailurePreventsLaterOperationFailure(bool synchronous)
    {
        var fake = new RecordingOperations();
        var failure = new InvalidOperationException("fixture callback failure");
        fake.Callback = (_, _) =>
        {
            fake.Events.Add("callback");
            return synchronous ? throw failure : ValueTask.FromException(failure);
        };
        fake.LoginBody = async (_, callback, token) =>
        {
            await callback(CreateDeviceCode(), token);
            fake.Events.Add("after-callback");
            throw new InvalidOperationException("later operation failure");
        };

        var actual = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fake.InvokeAsync("login"));

        Assert.AreSame(failure, actual);
        CollectionAssert.AreEqual(new[] { "factory", "root", "operation", "callback" }, fake.Events);
    }

    [TestMethod]
    public async Task Login_WaitsForManuallyReleasedCallbackBeforeReturningResult()
    {
        var fake = new RecordingOperations();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.Callback = async (_, _) =>
        {
            fake.Events.Add("callback");
            entered.TrySetResult();
            await release.Task;
            fake.Events.Add("callback-completed");
        };
        fake.LoginBody = async (_, callback, token) =>
        {
            await callback(CreateDeviceCode(), token);
            fake.Events.Add("operation-completed");
            return fake.Result;
        };
        var task = fake.InvokeAsync("login");
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(task.IsCompleted);
            CollectionAssert.AreEqual(new[] { "factory", "root", "operation", "callback" }, fake.Events);
            release.TrySetResult();

            Assert.AreSame(fake.Result, await task.WaitAsync(TimeSpan.FromSeconds(5)));
            CollectionAssert.AreEqual(new[] { "factory", "root", "operation", "callback", "callback-completed", "operation-completed" }, fake.Events);
        }
        finally
        {
            release.TrySetResult();
            await task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [TestMethod]
    public async Task BrowserPrompt_ReportsLocalizedMessageBeforeFakeBrowserAttempt()
    {
        var deviceCode = CreateDeviceCode();
        var events = new List<string>();
        var expected = SR.T("Opening Copilot login in your browser. Enter code {0} at {1}. Waiting for authorization...", deviceCode.UserCode, deviceCode.VerificationUri);

        await Coordinator.ReportCopilotDirectBrowserDeviceCode(
            deviceCode,
            message =>
            {
                Assert.AreEqual(expected, message);
                events.Add("report");
            },
            uri =>
            {
                Assert.AreSame(deviceCode.VerificationUri, uri);
                events.Add("browser");
            });

        CollectionAssert.AreEqual(new[] { "report", "browser" }, events);
    }

    [TestMethod]
    public void BrowserPrompt_ReportFailurePreventsBrowserAttempt()
    {
        var failure = new InvalidOperationException("fixture report failure");
        var events = new List<string>();

        var actual = Assert.ThrowsExactly<InvalidOperationException>(() => Coordinator.ReportCopilotDirectBrowserDeviceCode(
            CreateDeviceCode(),
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

        await Coordinator.ReportCopilotDirectDeviceCode(deviceCode, messages.Add);

        CollectionAssert.AreEqual(new[]
        {
            SR.T("Open {0} and enter code {1}. Waiting for Copilot authorization...", deviceCode.VerificationUri, deviceCode.UserCode),
        }, messages);
    }

    [TestMethod]
    public void DevicePrompt_ReportFailurePropagatesUnchanged()
    {
        var failure = new InvalidOperationException("fixture report failure");

        var actual = Assert.ThrowsExactly<InvalidOperationException>(
            () => Coordinator.ReportCopilotDirectDeviceCode(CreateDeviceCode(), _ => throw failure));

        Assert.AreSame(failure, actual);
    }

    [TestMethod]
    [DataRow(null, false)]
    [DataRow("", false)]
    [DataRow(" ", false)]
    [DataRow(" enterprise.invalid ", false)]
    [DataRow(null, true)]
    [DataRow(" enterprise.invalid ", true)]
    public void ResultMessage_PreservesEnterpriseAndCurrentCultureExpiryFormatting(string? enterprise, bool hasExpiry)
    {
        var result = new CopilotDirectLoginResult(
            new Uri("https://endpoint.invalid"),
            hasExpiry ? new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero) : null,
            enterprise);
        var expectedExpiry = result.ExpiresAt is null ? SR.T("expiry unknown")
            : SR.T("expires {0}", result.ExpiresAt.Value.LocalDateTime.ToString("g", CultureInfo.CurrentCulture));
        var expectedEnterprise = string.IsNullOrWhiteSpace(enterprise) ? "GitHub.com" : enterprise.Trim();

        foreach (var key in new[] { "Copilot login completed", "Copilot device login completed", "Authenticated with cached Copilot credentials" })
        {
            var prefix = SR.T(key);

            var actual = Coordinator.FormatCopilotDirectLoginMessage(prefix, result);

            Assert.AreEqual(SR.T("{0} · {1} · API {2} · {3}.", prefix, expectedEnterprise, result.BaseUri, expectedExpiry), actual);
        }
    }

    private static CopilotDirectDeviceCode CreateDeviceCode()
        => new(new Uri("https://verification.invalid/device"), "FAKE-CODE", new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero));

    // This fake has no resources. Its disposal methods only record unexpected ownership changes.
    private sealed class RecordingOperations : IDisposable, IAsyncDisposable
    {
        public RecordingOperations()
        {
            RootCallback = ReadRoot;
            InvalidProviderFormatter = FormatInvalidProvider;
            LoginFactory = CreateLogin;
            DeleteFactory = CreateDelete;
            StatusFactory = CreateStatus;
            LoginBody = (_, _, _) => ValueTask.FromResult(Result);
            DeleteBody = (_, _) => ValueTask.CompletedTask;
            StatusBody = (_, _) => ValueTask.FromResult<CopilotDirectLoginResult?>(Result);
        }

        public List<string> Events { get; } = [];
        public CodeAltaProviderDocument Definition { get; } = new() { ProviderKey = "fixture-provider", ProviderType = "copilot" };
        public CopilotDirectLoginResult Result { get; } = new(new Uri("https://endpoint.invalid"), null, null);
        public CopilotDirectLoginOptions? ObservedOptions { get; private set; }
        public CancellationToken ObservedToken { get; private set; }
        public Func<CopilotDirectDeviceCode, CancellationToken, ValueTask>? ObservedCallback { get; private set; }
        public Func<CopilotDirectDeviceCode, CancellationToken, ValueTask> Callback { get; set; } = (_, _) => ValueTask.CompletedTask;
        public Func<string> RootValue { get; set; } = () => "inert-root";
        public Func<string> InvalidProviderText { get; set; } = () => "fixture invalid provider";
        public Action? OnFactory { get; set; }
        public Coordinator.CopilotDirectLoginOperation LoginBody { get; set; }
        public Coordinator.CopilotDirectDeleteCredentialOperation DeleteBody { get; set; }
        public Coordinator.CopilotDirectCredentialStatusOperation StatusBody { get; set; }
        private Func<string> RootCallback { get; set; }
        private Func<string> InvalidProviderFormatter { get; set; }
        private Func<Coordinator.CopilotDirectLoginOperation> LoginFactory { get; set; }
        private Func<Coordinator.CopilotDirectDeleteCredentialOperation> DeleteFactory { get; set; }
        private Func<Coordinator.CopilotDirectCredentialStatusOperation> StatusFactory { get; set; }
        private bool UseNullDefinition { get; set; }

        public async Task<CopilotDirectLoginResult?> InvokeAsync(string operation, CancellationToken cancellationToken = default)
        {
            var definition = UseNullDefinition ? null! : Definition; // Intentional invalid-input case only.
            switch (operation)
            {
                case "login":
                    return await Coordinator.LoginCopilotDirectCoreAsync(definition, RootCallback, InvalidProviderFormatter, LoginFactory, Callback, cancellationToken);
                case "delete":
                    await Coordinator.DeleteCopilotDirectCredentialCoreAsync(definition, RootCallback, InvalidProviderFormatter, DeleteFactory, cancellationToken);
                    return null;
                case "status":
                    return await Coordinator.GetCopilotDirectCredentialStatusCoreAsync(definition, RootCallback, InvalidProviderFormatter, StatusFactory, cancellationToken);
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
                case "onDeviceCode": Callback = null!; break;
                case "getStateRootPath": RootCallback = null!; break;
                case "formatInvalidProvider": InvalidProviderFormatter = null!; break;
                case "createOperation": LoginFactory = null!; DeleteFactory = null!; StatusFactory = null!; break;
                default: throw new AssertFailedException("Unknown fixture argument.");
            }
        }

        public void FailOperations(Exception failure, bool synchronous)
        {
            LoginBody = (_, _, _) => synchronous ? throw failure : ValueTask.FromException<CopilotDirectLoginResult>(failure);
            DeleteBody = (_, _) => synchronous ? throw failure : ValueTask.FromException(failure);
            StatusBody = (_, _) => synchronous ? throw failure : ValueTask.FromException<CopilotDirectLoginResult?>(failure);
        }

        public void WaitInOperations(TaskCompletionSource entered, TaskCompletionSource release)
        {
            async ValueTask WaitAsync(CancellationToken token)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                Events.Add("operation-completed");
            }

            LoginBody = async (_, _, token) => { await WaitAsync(token); return Result; };
            DeleteBody = (_, token) => WaitAsync(token);
            StatusBody = async (_, token) => { await WaitAsync(token); return Result; };
        }

        public void Dispose() => Events.Add("dispose");
        public ValueTask DisposeAsync()
        {
            Events.Add("dispose-async");
            return ValueTask.CompletedTask;
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

        private Coordinator.CopilotDirectLoginOperation CreateLogin()
        {
            Events.Add("factory");
            OnFactory?.Invoke();
            return Login;
        }

        private Coordinator.CopilotDirectDeleteCredentialOperation CreateDelete()
        {
            Events.Add("factory");
            OnFactory?.Invoke();
            return Delete;
        }

        private Coordinator.CopilotDirectCredentialStatusOperation CreateStatus()
        {
            Events.Add("factory");
            OnFactory?.Invoke();
            return Status;
        }

        private void Capture(CopilotDirectLoginOptions options, CancellationToken token)
        {
            Events.Add("operation");
            ObservedOptions = options;
            ObservedToken = token;
        }

        private ValueTask<CopilotDirectLoginResult> Login(
            CopilotDirectLoginOptions options, Func<CopilotDirectDeviceCode, CancellationToken, ValueTask> callback, CancellationToken token)
        {
            Capture(options, token);
            ObservedCallback = callback;
            return LoginBody(options, callback, token);
        }

        private ValueTask Delete(CopilotDirectLoginOptions options, CancellationToken token)
        {
            Capture(options, token);
            return DeleteBody(options, token);
        }

        private ValueTask<CopilotDirectLoginResult?> Status(CopilotDirectLoginOptions options, CancellationToken token)
        {
            Capture(options, token);
            return StatusBody(options, token);
        }
    }
}
