using CodeAlta.Catalog;
using Authentication = CodeAlta.Hosting.ConfiguredCodexAuthentication;

namespace CodeAlta.Hosting.Tests;

// Original cases use only the static production-connected core and instance-owned recording factories.
// Added public cases fail required-null guards before any concrete factory; no public success is executed.
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
            Authentication.LoginWithDeviceCodeAsync(
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

    [TestMethod]
    [DataRow("definition")]
    [DataRow("getStateRootPath")]
    [DataRow("formatInvalidProvider")]
    [DataRow("reportDeviceCode")]
    [DataRow("formatCompletionPrefix")]
    [DataRow("onCompleted")]
    [DataRow("all")]
    public async Task PublicRequiredArguments_ReturnFaultedTaskBeforeFactory(string missing)
    {
        // Required-null objects are the first barrier; this nonmatching type is secondary ONLY.
        // Never execute this public route with only invalid type/key/root values.
        var definition = new CodeAltaProviderDocument { ProviderType = "not-codex", ProviderKey = "synthetic-provider" };
        Func<string> getStateRootPath = () => throw new AssertFailedException("Public guard must precede root selection.");
        Func<string> formatInvalidProvider = () => throw new AssertFailedException("Public guard must precede type formatting.");
        Action<string, string> reportDeviceCode = (_, _) => throw new AssertFailedException("Public guard must precede reporting.");
        Func<string> formatCompletionPrefix = () => throw new AssertFailedException("Public guard must precede prefix formatting.");
        Action<string, string?> onCompleted = (_, _) => throw new AssertFailedException("Public guard must precede completion.");

        var task = Authentication.LoginWithDeviceCodeAsync(
            missing is "definition" or "all" ? null! : definition,
            missing is "getStateRootPath" or "all" ? null! : getStateRootPath,
            missing is "formatInvalidProvider" or "all" ? null! : formatInvalidProvider,
            missing is "reportDeviceCode" or "all" ? null! : reportDeviceCode,
            missing is "formatCompletionPrefix" or "all" ? null! : formatCompletionPrefix,
            missing is "onCompleted" or "all" ? null! : onCompleted,
            CancellationToken.None);

        Assert.IsTrue(task.IsFaulted);
        var failure = await Assert.ThrowsExactlyAsync<ArgumentNullException>(() => task);
        Assert.AreEqual(missing == "all" ? "definition" : missing, failure.ParamName);
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
            => Authentication.LoginWithDeviceCodeAsync(
                Definition, GetStateRootPath, FormatInvalidProvider, ReportDeviceCode,
                FormatCompletionPrefix, OnCompleted, CreateOperation, cancellationToken);
    }
}
