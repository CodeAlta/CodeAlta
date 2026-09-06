using CodeAlta.Catalog;
using CodeAlta.Tui.App;
using Coordinator = CodeAlta.Tui.App.ProviderFrontendCoordinator;

namespace CodeAlta.Tests;

// Only static production-connected core/display helpers and instance-owned recording factories run.
// Definitions and primitive display strings are synthetic; no credential/device-protocol/PKCE records,
// concrete manager/client/store/coordinator/host, resolver, discovery or authentication filesystem work.
// Fake-controlled projection/root/key consumption proves the seam, not concrete provider behavior.
// Request/report/poll/persist ordering and the concrete credential projection remain source-only evidence.
// SR may read output-directory localization; existing assembly-level writerless logging still applies.
[TestClass]
public sealed class ConfiguredCodexDeviceLoginTests
{
    [TestMethod]
    [DataRow(0, false)]
    [DataRow(1, false)]
    [DataRow(2, false)]
    [DataRow(3, false)]
    [DataRow(4, false)]
    [DataRow(5, false)]
    [DataRow(6, false)]
    [DataRow(0, true)]
    [DataRow(1, true)]
    [DataRow(2, true)]
    [DataRow(3, true)]
    [DataRow(4, true)]
    [DataRow(5, true)]
    [DataRow(6, true)]
    public async Task RequiredArguments_ValidateEachInputAndEveryPrecedenceSuffixBeforeTypeOrWork(int index, bool nullSuffix)
    {
        var fake = new RecordingDeviceLogin();
        fake.Definition.ProviderType = "wrong";
        var missing = new bool[7];
        for (var position = index; position < (nullSuffix ? missing.Length : index + 1); position++)
        {
            missing[position] = true;
        }

        var failure = await Assert.ThrowsExactlyAsync<ArgumentNullException>(() =>
            Coordinator.LoginCodexDeviceCoreAsync(
                missing[0] ? null! : fake.Definition,
                missing[1] ? null! : fake.GetStateRootPath,
                missing[2] ? null! : fake.FormatInvalidProvider,
                missing[3] ? null! : fake.ReportDeviceCode,
                missing[4] ? null! : fake.FormatCompletionPrefix,
                missing[5] ? null! : fake.OnCompleted,
                missing[6] ? null! : fake.CreateOperation,
                CancellationToken.None));

        var names = new[]
        {
            "definition", "getStateRootPath", "formatInvalidProvider", "reportDeviceCode",
            "formatCompletionPrefix", "onCompleted", "createOperation",
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
    public async Task ProviderType_RequiresExactOrdinalCodexBeforeFactoryOrCancellation(string? providerType)
    {
        var fake = new RecordingDeviceLogin();
        fake.Definition.ProviderType = providerType;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fake.InvokeAsync(cancellation.Token));

        Assert.AreEqual("fixture invalid provider", failure.Message);
        CollectionAssert.AreEqual(new[] { "format-invalid" }, fake.Events);
    }

    [TestMethod]
    public async Task Mismatch_UsesSuppliedLocalizedMessage()
    {
        var fake = new RecordingDeviceLogin();
        fake.Definition.ProviderType = "wrong";
        fake.InvalidProviderText = () => SR.T("Select a Codex provider first.");

        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fake.InvokeAsync());

        Assert.AreEqual(SR.T("Select a Codex provider first."), failure.Message);
        CollectionAssert.AreEqual(new[] { "format-invalid" }, fake.Events);
    }

    [TestMethod]
    public async Task MatchingType_ForwardsOriginalReferencesAndTokenWithoutFormattingOrConsumingRoot()
    {
        var fake = new RecordingDeviceLogin();
        fake.InvalidProviderText = () => throw new AssertFailedException("Matching type must not format an error.");
        fake.RootValue = () => throw new AssertFailedException("The core must not consume the root.");
        using var cancellation = new CancellationTokenSource();

        var task = fake.InvokeAsync(cancellation.Token);

        Assert.IsTrue(task.IsCompletedSuccessfully);
        await task;
        Assert.AreSame(fake.Definition, fake.ObservedDefinition);
        Assert.AreSame(fake.GetStateRootPath, fake.ObservedRootCallback);
        Assert.AreSame(fake.ReportDeviceCode, fake.ObservedReportCallback);
        Assert.AreSame(fake.FormatCompletionPrefix, fake.ObservedPrefixCallback);
        Assert.AreSame(fake.OnCompleted, fake.ObservedCompletionCallback);
        Assert.AreEqual(cancellation.Token, fake.ObservedToken);
        CollectionAssert.AreEqual(new[]
        {
            "factory", "operation", "report", "prefix", "project-id", "completion", "operation-completed",
        }, fake.Events);
    }

    [TestMethod]
    public async Task Factory_ControlsRepeatedLazyRootConsumptionAndSeesDefinitionMutation()
    {
        var fake = new RecordingDeviceLogin();
        var rootValue = " first synthetic root ";
        fake.RootValue = () =>
        {
            fake.Definition.ProviderKey = " mutated synthetic key ";
            return rootValue;
        };
        fake.FactoryBody = (definition, getRoot) =>
        {
            Assert.AreEqual(" first synthetic root ", getRoot());
            Assert.AreEqual(" mutated synthetic key ", definition.ProviderKey);
            rootValue = " second synthetic root ";
            Assert.AreEqual(" second synthetic root ", getRoot());
            return fake.InvokeOperation;
        };

        await fake.InvokeAsync();

        CollectionAssert.AreEqual(new[]
        {
            "factory", "root", "root", "operation", "report", "prefix", "project-id", "completion", "operation-completed",
        }, fake.Events);
    }

    [TestMethod]
    [DataRow(null, "synthetic-key")]
    [DataRow("synthetic-root", null)]
    [DataRow("", "")]
    [DataRow(" \t ", " \t ")]
    [DataRow(" raw synthetic root ", " raw synthetic/key ")]
    public async Task RootAndKey_AreNotNormalizedOrValidatedByCore(string? rootValue, string? providerKey)
    {
        var fake = new RecordingDeviceLogin();
        // Null-forgiving assignments deliberately pass invalid inert values only to this fake,
        // never to a concrete constructor, despite the production definition's non-null key contract.
        fake.RootValue = () => rootValue!;
        fake.Definition.ProviderKey = providerKey!;
        fake.FactoryBody = (definition, getRoot) =>
        {
            Assert.AreEqual(rootValue, getRoot());
            Assert.AreEqual(providerKey, definition.ProviderKey);
            return fake.InvokeOperation;
        };

        await fake.InvokeAsync();

        CollectionAssert.AreEqual(new[]
        {
            "factory", "root", "operation", "report", "prefix", "project-id", "completion", "operation-completed",
        }, fake.Events);
    }

    [TestMethod]
    [DataRow(null, null, false)]
    [DataRow(" arbitrary synthetic source ", " configured synthetic id ", true)]
    public async Task IrrelevantConfiguration_DoesNotAddEligibilityOrAccountResolution(string? authSource, string? configuredAccountId, bool enabled)
    {
        var fake = new RecordingDeviceLogin();
        fake.Definition.AuthSource = authSource;
        fake.Definition.AccountId = configuredAccountId;
        fake.Definition.Enabled = enabled;
        string? observedId = null;
        fake.CompletionAction = (_, rawId) => observedId = rawId;

        await fake.InvokeAsync();

        Assert.AreSame(fake.Definition, fake.ObservedDefinition);
        Assert.AreEqual("synthetic-raw-id", observedId);
        CollectionAssert.AreEqual(new[]
        {
            "factory", "operation", "report", "prefix", "project-id", "completion", "operation-completed",
        }, fake.Events);
    }

    [TestMethod]
    [DataRow("not a URI", " synthetic code ")]
    [DataRow(" raw synthetic verification string ", "synthetic-[code]")]
    public async Task Report_UsesProductionHelperWithRawStringsAndExactLocalizedPrompt(string verificationUri, string userCode)
    {
        var fake = new RecordingDeviceLogin();
        fake.VerificationUri = verificationUri;
        fake.UserCode = userCode;
        var messages = new List<string>();
        fake.ReportAction = (uri, code) => Coordinator.ReportCodexDeviceCode(uri, code, messages.Add);

        await fake.InvokeAsync();

        CollectionAssert.AreEqual(new[]
        {
            SR.T("Open {0} and enter code {1}. Waiting for ChatGPT authorization...", verificationUri, userCode),
        }, messages);
    }

    [TestMethod]
    public async Task Completion_LocalizesPrefixBeforeFakeControlledRawIdProjectionAndSynchronousPresentation()
    {
        var fake = new RecordingDeviceLogin();
        fake.RawAccountId = "before";
        fake.PrefixText = () =>
        {
            fake.RawAccountId = " after synthetic id ";
            return SR.T("ChatGPT device-code login completed");
        };
        ProviderTestResult? result = null;
        fake.CompletionAction = (prefix, rawId) => result = Coordinator.FormatCodexDeviceLoginResult(prefix, rawId);

        await fake.InvokeAsync();

        Assert.IsNotNull(result);
        Assert.IsTrue(result.Value.Success);
        Assert.AreEqual(0, result.Value.ModelCount);
        Assert.AreEqual(SR.T("{0} · account/workspace: {1}.", SR.T("ChatGPT device-code login completed"), " after synthetic id "), result.Value.Message);
        CollectionAssert.AreEqual(new[]
        {
            "factory", "operation", "report", "prefix", "project-id", "completion", "operation-completed",
        }, fake.Events);
    }

    [TestMethod]
    [DataRow(null, true)]
    [DataRow("", true)]
    [DataRow(" ", true)]
    [DataRow("\t\r\n", true)]
    [DataRow("synthetic-id", false)]
    [DataRow(" padded synthetic id ", false)]
    public async Task Completion_UsesProductionHelperForEveryRawIdBranch(string? rawAccountId, bool unknown)
    {
        var fake = new RecordingDeviceLogin();
        fake.RawAccountId = rawAccountId;
        fake.PrefixText = () => SR.T("ChatGPT device-code login completed");
        ProviderTestResult? result = null;
        fake.CompletionAction = (prefix, rawId) => result = Coordinator.FormatCodexDeviceLoginResult(prefix, rawId);

        await fake.InvokeAsync();

        Assert.IsNotNull(result);
        Assert.IsTrue(result.Value.Success);
        Assert.AreEqual(0, result.Value.ModelCount);
        Assert.AreEqual(SR.T("{0} · account/workspace: {1}.", SR.T("ChatGPT device-code login completed"), unknown ? SR.T("account/workspace unknown") : rawAccountId), result.Value.Message);
    }

    [TestMethod]
    [DataRow("mismatch")]
    [DataRow("factory")]
    [DataRow("fake-root")]
    [DataRow("report")]
    [DataRow("prefix")]
    [DataRow("completion")]
    [DataRow("synchronous-operation")]
    [DataRow("faulted-operation")]
    public async Task SynchronousFailures_KeepExceptionIdentityAndStopLaterCallbacks(string stage)
    {
        var fake = new RecordingDeviceLogin();
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
            case "report":
                fake.ReportAction = (uri, code) => Coordinator.ReportCodexDeviceCode(uri, code, _ => throw expected);
                expectedEvents = ["factory", "operation", "report"];
                break;
            case "prefix":
                fake.PrefixText = () => throw expected;
                expectedEvents = ["factory", "operation", "report", "prefix"];
                break;
            case "completion":
                fake.CompletionAction = (_, _) => throw expected;
                expectedEvents = ["factory", "operation", "report", "prefix", "project-id", "completion"];
                break;
            case "synchronous-operation":
                fake.Operation = (_, _, _, _) => throw expected;
                expectedEvents = ["factory", "operation"];
                break;
            case "faulted-operation":
                fake.Operation = (_, _, _, _) => ValueTask.FromException(expected);
                expectedEvents = ["factory", "operation"];
                break;
            default:
                throw new AssertFailedException("Unknown synthetic failure stage.");
        }

        var actual = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fake.InvokeAsync());

        Assert.AreSame(expected, actual);
        CollectionAssert.AreEqual(expectedEvents, fake.Events);
    }

    [TestMethod]
    public async Task PrecanceledToken_ReachesIgnoringOperationAndCompletesWithCallbacks()
    {
        var fake = new RecordingDeviceLogin();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var task = fake.InvokeAsync(cancellation.Token);

        Assert.IsTrue(task.IsCompletedSuccessfully);
        await task;
        Assert.AreEqual(cancellation.Token, fake.ObservedToken);
        Assert.IsTrue(fake.ObservedToken.IsCancellationRequested);
        CollectionAssert.AreEqual(new[]
        {
            "factory", "operation", "report", "prefix", "project-id", "completion", "operation-completed",
        }, fake.Events);
    }

    [TestMethod]
    public async Task PrecanceledToken_ReachesCooperativeOperationBeforeCancellation()
    {
        var fake = new RecordingDeviceLogin();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        fake.Operation = (_, _, _, token) => ValueTask.FromCanceled(token);

        var failure = await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => fake.InvokeAsync(cancellation.Token));

        Assert.AreEqual(cancellation.Token, failure.CancellationToken);
        Assert.AreEqual(cancellation.Token, fake.ObservedToken);
        CollectionAssert.AreEqual(new[] { "factory", "operation" }, fake.Events);
    }

    [TestMethod]
    public async Task PendingOperation_ConsumesOriginalDefinitionAndRootOnlyWhenFakeReleasesWork()
    {
        var fake = new RecordingDeviceLogin();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rootValue = "before-root";
        fake.RootValue = () => rootValue;
        fake.FactoryBody = (definition, getRoot) =>
        {
            fake.Operation = async (_, _, _, _) =>
            {
                entered.TrySetResult();
                await release.Task;
                Assert.AreEqual("after-key", definition.ProviderKey);
                Assert.AreEqual("after-root", getRoot());
                fake.Events.Add("operation-completed");
            };
            return fake.InvokeOperation;
        };

        var task = fake.InvokeAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(task.IsCompleted);
            CollectionAssert.AreEqual(new[] { "factory", "operation" }, fake.Events);
            fake.Definition.ProviderKey = "after-key";
            rootValue = "after-root";
            release.TrySetResult();
            await task.WaitAsync(TimeSpan.FromSeconds(5));
            CollectionAssert.AreEqual(new[] { "factory", "operation", "root", "operation-completed" }, fake.Events);
        }
        finally
        {
            release.TrySetResult();
            await task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [TestMethod]
    public async Task PendingOperation_ReportingAndCompletionPresentationDoNotSettleTheOperation()
    {
        var fake = new RecordingDeviceLogin();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowPresentation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var presented = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var messages = new List<string>();
        ProviderTestResult? result = null;
        fake.ReportAction = (uri, code) => Coordinator.ReportCodexDeviceCode(uri, code, messages.Add);
        fake.CompletionAction = (prefix, rawId) => result = Coordinator.FormatCodexDeviceLoginResult(prefix, rawId);
        fake.Operation = async (report, prefix, completed, _) =>
        {
            entered.TrySetResult();
            await allowPresentation.Task;
            fake.Present(report, prefix, completed);
            presented.TrySetResult();
            await allowCompletion.Task;
            fake.Events.Add("operation-completed");
        };

        var task = fake.InvokeAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(task.IsCompleted);
            Assert.IsNull(result);
            Assert.IsEmpty(messages);
            allowPresentation.TrySetResult();
            await presented.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(task.IsCompleted);
            Assert.IsNotNull(result);
            Assert.IsTrue(result.Value.Success);
            Assert.AreEqual(0, result.Value.ModelCount);
            Assert.HasCount(1, messages);
            CollectionAssert.AreEqual(new[] { "factory", "operation", "report", "prefix", "project-id", "completion" }, fake.Events);
            allowCompletion.TrySetResult();
            await task.WaitAsync(TimeSpan.FromSeconds(5));
            CollectionAssert.AreEqual(new[]
            {
                "factory", "operation", "report", "prefix", "project-id", "completion", "operation-completed",
            }, fake.Events);
        }
        finally
        {
            allowPresentation.TrySetResult();
            allowCompletion.TrySetResult();
            await task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [TestMethod]
    [DataRow("operation")]
    [DataRow("report")]
    [DataRow("prefix")]
    [DataRow("completion")]
    public async Task PendingFailures_KeepExceptionIdentityAndAreJoined(string stage)
    {
        var fake = new RecordingDeviceLogin();
        var expected = new InvalidOperationException("pending synthetic failure");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expectedEvents = new List<string> { "factory", "operation" };
        switch (stage)
        {
            case "operation":
                break;
            case "report":
                fake.ReportAction = (_, _) => throw expected;
                expectedEvents.Add("report");
                break;
            case "prefix":
                fake.PrefixText = () => throw expected;
                expectedEvents.AddRange(["report", "prefix"]);
                break;
            case "completion":
                fake.CompletionAction = (_, _) => throw expected;
                expectedEvents.AddRange(["report", "prefix", "project-id", "completion"]);
                break;
            default:
                throw new AssertFailedException("Unknown pending failure stage.");
        }
        fake.Operation = async (report, prefix, completed, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            if (stage == "operation")
            {
                throw expected;
            }

            fake.Present(report, prefix, completed);
        };

        var task = fake.InvokeAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(task.IsCompleted);
            release.TrySetResult();
            var actual = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.AreSame(expected, actual);
            CollectionAssert.AreEqual(expectedEvents, fake.Events);
        }
        finally
        {
            release.TrySetResult();
            try
            {
                await task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (InvalidOperationException failure) when (ReferenceEquals(failure, expected))
            {
                // The expected fault is observed again for cleanup; no started work is abandoned.
            }
        }
    }

    [TestMethod]
    public async Task PendingOperation_CooperativelyCancelsWithOriginalTokenAndJoinsBeforeDisposal()
    {
        var fake = new RecordingDeviceLogin();
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.Operation = async (_, _, _, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        };

        var task = fake.InvokeAsync(cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(task.IsCompleted);
            cancellation.Cancel();
            var failure = await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.AreEqual(cancellation.Token, failure.CancellationToken);
            Assert.AreEqual(cancellation.Token, fake.ObservedToken);
            Assert.IsTrue(task.IsCanceled);
            CollectionAssert.AreEqual(new[] { "factory", "operation" }, fake.Events);
        }
        finally
        {
            cancellation.Cancel();
            release.TrySetResult();
            try
            {
                await task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (OperationCanceledException failure) when (failure.CancellationToken == cancellation.Token)
            {
                // All gates are released and the canceled task is joined before CTS disposal.
            }
        }
    }

    private sealed class RecordingDeviceLogin
    {
        public RecordingDeviceLogin()
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
            ReportDeviceCode = (verificationUri, userCode) =>
            {
                Events.Add("report");
                ReportAction(verificationUri, userCode);
            };
            FormatCompletionPrefix = () =>
            {
                Events.Add("prefix");
                return PrefixText();
            };
            OnCompleted = (prefix, rawAccountId) =>
            {
                Events.Add("completion");
                CompletionAction(prefix, rawAccountId);
            };
            FactoryBody = (_, _) => InvokeOperation;
            Operation = (report, prefix, completed, _) =>
            {
                Present(report, prefix, completed);
                Events.Add("operation-completed");
                return ValueTask.CompletedTask;
            };
        }

        public CodeAltaProviderDocument Definition { get; } = new()
        {
            ProviderType = "codex",
            ProviderKey = "synthetic-provider",
        };

        public List<string> Events { get; } = [];
        public string VerificationUri { get; set; } = "synthetic verification string";
        public string UserCode { get; set; } = "synthetic code";
        public string? RawAccountId { get; set; } = "synthetic-raw-id";
        public Func<string> GetStateRootPath { get; }
        public Func<string> RootValue { get; set; } = () => "synthetic-root";
        public Func<string> FormatInvalidProvider { get; }
        public Func<string> InvalidProviderText { get; set; } = () => "fixture invalid provider";
        public Action<string, string> ReportDeviceCode { get; }
        public Action<string, string> ReportAction { get; set; } = (_, _) => { };
        public Func<string> FormatCompletionPrefix { get; }
        public Func<string> PrefixText { get; set; } = () => "synthetic completion prefix";
        public Action<string, string?> OnCompleted { get; }
        public Action<string, string?> CompletionAction { get; set; } = (_, _) => { };
        public Func<CodeAltaProviderDocument, Func<string>, CodexDeviceLoginOperation> FactoryBody { get; set; }
        public CodexDeviceLoginOperation Operation { get; set; }
        public CodeAltaProviderDocument? ObservedDefinition { get; private set; }
        public Func<string>? ObservedRootCallback { get; private set; }
        public Action<string, string>? ObservedReportCallback { get; private set; }
        public Func<string>? ObservedPrefixCallback { get; private set; }
        public Action<string, string?>? ObservedCompletionCallback { get; private set; }
        public CancellationToken ObservedToken { get; private set; }

        public CodexDeviceLoginOperation CreateOperation(CodeAltaProviderDocument definition, Func<string> getStateRootPath)
        {
            Events.Add("factory");
            ObservedDefinition = definition;
            ObservedRootCallback = getStateRootPath;
            return FactoryBody(definition, getStateRootPath);
        }

        public ValueTask InvokeOperation(
            Action<string, string> reportDeviceCode,
            Func<string> formatCompletionPrefix,
            Action<string, string?> onCompleted,
            CancellationToken cancellationToken)
        {
            Events.Add("operation");
            ObservedReportCallback = reportDeviceCode;
            ObservedPrefixCallback = formatCompletionPrefix;
            ObservedCompletionCallback = onCompleted;
            ObservedToken = cancellationToken;
            return Operation(reportDeviceCode, formatCompletionPrefix, onCompleted, cancellationToken);
        }

        public void Present(Action<string, string> reportDeviceCode, Func<string> formatCompletionPrefix, Action<string, string?> onCompleted)
        {
            reportDeviceCode(VerificationUri, UserCode);
            var prefix = formatCompletionPrefix();
            // Fake-controlled primitive projection, not a constructed credential or provider getter test.
            Events.Add("project-id");
            var rawAccountId = RawAccountId;
            onCompleted(prefix, rawAccountId);
        }

        public Task InvokeAsync(CancellationToken cancellationToken = default)
            => Coordinator.LoginCodexDeviceCoreAsync(
                Definition, GetStateRootPath, FormatInvalidProvider, ReportDeviceCode,
                FormatCompletionPrefix, OnCompleted, CreateOperation, cancellationToken);
    }
}
