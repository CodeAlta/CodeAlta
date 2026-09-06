using CodeAlta.Catalog;
using CodeAlta.Hosting;
using CodeAlta.Tui.App;
using Authentication = CodeAlta.Hosting.ConfiguredCodexAuthentication;
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
