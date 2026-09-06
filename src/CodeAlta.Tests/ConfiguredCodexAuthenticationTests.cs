using CodeAlta.Catalog;
using CodeAlta.Tui.App;
using Coordinator = CodeAlta.Tui.App.ProviderFrontendCoordinator;
using Authentication = CodeAlta.Hosting.ConfiguredCodexAuthentication;
using AuthenticationOperation = CodeAlta.Hosting.CodexAuthenticationTestOperation;

namespace CodeAlta.Tests;

// Static seam/presentation helpers and mandatory instance-owned recording operations only.
// Synthetic constructor/home/context events describe the FAKE, not provider protocol qualification.
// No concrete provider, coordinator, host, manager, client, store, credential, context or metadata
// construction, discovery, browser/listener/process execution or real authentication.
// Raw completion IDs stand for provider-resolved context IDs, not raw credential IDs.
// SR may read output-directory localization; existing writerless logging remains, not zero I/O.
[TestClass]
public sealed class ConfiguredCodexAuthenticationTests
{
    [TestMethod]
    [DataRow("mismatch")]
    [DataRow("factory")]
    [DataRow("fake-root")]
    [DataRow("synthetic-home")]
    [DataRow("synchronous-operation")]
    [DataRow("faulted-operation")]
    [DataRow("completion")]
    public async Task SynchronousFailures_KeepIdentityAndStopLaterCallbacks(string stage)
    {
        var fake = new RecordingAuthenticationTest();
        var expected = new InvalidOperationException("synthetic failure");
        string[] events;
        switch (stage)
        {
            case "mismatch":
                fake.Definition.ProviderType = "wrong";
                fake.InvalidProviderText = () => throw expected;
                events = ["format-invalid"];
                break;
            case "factory":
                fake.FactoryBody = (_, _) => throw expected;
                events = ["factory"];
                break;
            case "fake-root":
                fake.FactoryBody = fake.RecordConstruction;
                fake.RootValue = () => throw expected;
                events = ["factory", "root"];
                break;
            case "synthetic-home":
                fake.FactoryBody = fake.RecordConstruction;
                fake.SyntheticHomeAction = () => throw expected;
                events = ["factory", "root", "synthetic-store", "synthetic-http-oauth", "synthetic-key", "synthetic-source", "synthetic-account", "synthetic-home"];
                break;
            case "synchronous-operation":
                fake.Operation = (_, _) => throw expected;
                events = ["factory", "operation"];
                break;
            case "faulted-operation":
                fake.Operation = (_, _) => ValueTask.FromException(expected);
                events = ["factory", "operation"];
                break;
            case "completion":
                fake.CompletionAction = rawId =>
                {
                    var result = Coordinator.FormatCodexAuthenticationResult(rawId);
                    Assert.IsTrue(result.Success);
                    throw expected;
                };
                events = ["factory", "operation", "synthetic-context", "synthetic-project-id", "completion"];
                break;
            default:
                throw new AssertFailedException("Unknown synthetic stage.");
        }

        var route = InvokeAsync(fake);
        await ObserveAsync(route, expected);
        CollectionAssert.AreEqual(events, fake.Events);
    }

    [TestMethod]
    public async Task PendingOperation_DelaysMetadataPresentation()
    {
        var fake = new RecordingAuthenticationTest();
        var gate = NewGate();
        var wait = gate.Task;
        fake.WaitBody = _ => wait;
        ProviderTestResult? result = null;
        fake.CompletionAction = rawId => result = Coordinator.FormatCodexAuthenticationResult(rawId);
        Task? route = null;
        try
        {
            route = InvokeAsync(fake);
            Assert.IsFalse(route.IsCompleted);
            Assert.IsFalse(wait.IsCompleted);
            Assert.IsNull(result);
            CollectionAssert.AreEqual(new[] { "factory", "operation", "synthetic-context" }, fake.Events);
            gate.SetResult();
            await ObserveAsync(route);
            Assert.IsNotNull(result);
            Assert.IsTrue(result.Value.Success);
            Assert.AreEqual(0, result.Value.ModelCount);
            CollectionAssert.AreEqual(new[] { "factory", "operation", "synthetic-context", "synthetic-project-id", "completion" }, fake.Events);
        }
        finally
        {
            gate.TrySetResult();
            await JoinBothAsync(route, wait);
        }
    }

    [TestMethod]
    [DataRow(null, true)]
    [DataRow("", true)]
    [DataRow(" ", true)]
    [DataRow("\t\r\n", true)]
    [DataRow("synthetic-resolved-id", false)]
    [DataRow(" padded resolved id ", false)]
    public async Task Completion_UsesEveryRawIdBranchAndExactLocalizedResult(string? rawAccountId, bool blank)
    {
        var fake = new RecordingAuthenticationTest();
        fake.RawAccountId = rawAccountId;
        ProviderTestResult? result = null;
        fake.CompletionAction = rawId => result = Coordinator.FormatCodexAuthenticationResult(rawId);
        var route = InvokeAsync(fake);
        await ObserveAsync(route);
        Assert.AreEqual(rawAccountId, fake.CompletedId);
        Assert.IsNotNull(result);
        Assert.IsTrue(result.Value.Success);
        Assert.AreEqual(0, result.Value.ModelCount);
        Assert.AreEqual(SR.T("Authenticated without sending a model turn · account/workspace: {0}.",
            blank ? SR.T("no account/workspace id in token") : rawAccountId), result.Value.Message);
        CollectionAssert.AreEqual(new[] { "factory", "operation", "synthetic-context", "synthetic-project-id", "completion" }, fake.Events);
    }

    [TestMethod]
    public async Task CompletionPresentation_DoesNotSettlePendingOperation()
    {
        var fake = new RecordingAuthenticationTest();
        var gate = NewGate();
        var wait = gate.Task;
        ProviderTestResult? result = null;
        fake.CompletionAction = rawId => result = Coordinator.FormatCodexAuthenticationResult(rawId);
        fake.Operation = async (complete, _) =>
        {
            complete("synthetic resolved id");
            await wait;
        };
        Task? route = null;
        try
        {
            route = InvokeAsync(fake);
            Assert.IsNotNull(result);
            Assert.IsTrue(result.Value.Success);
            Assert.IsFalse(route.IsCompleted);
            Assert.IsFalse(wait.IsCompleted);
            CollectionAssert.AreEqual(new[] { "factory", "operation", "completion" }, fake.Events);
            gate.SetResult();
            await ObserveAsync(route);
        }
        finally
        {
            gate.TrySetResult();
            await JoinBothAsync(route, wait);
        }
    }

    // Hosting seam binding; presentation above remains TUI-owned, with the complete shared suffix below.
    private static Task InvokeAsync(RecordingAuthenticationTest fake, CancellationToken token = default)
        => Authentication.TestCodexAuthenticationCoreAsync(
            fake.Definition, fake.GetStateRootPath, fake.FormatInvalidProvider,
            fake.OnAuthenticated, fake.CreateOperation, token);

    // BEGIN mechanically movable helper suffix (including helpers unused by either later half).
    private static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

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

    private static async Task JoinBothAsync(Task? route, Task wait, Exception? expected = null, bool canceled = false, CancellationToken? expectedToken = null)
    {
        try
        {
            if (route is not null)
            {
                await ObserveAsync(route, expected, canceled, expectedToken);
            }
        }
        finally
        {
            // Fixture ownership only: independently join the gate even if route observation fails.
            await ObserveAsync(wait, expected, canceled, expectedToken);
        }
    }

    private sealed class RecordingAuthenticationTest
    {
        public RecordingAuthenticationTest()
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
            OnAuthenticated = rawId =>
            {
                Events.Add("completion");
                CompletedId = rawId;
                CompletionAction(rawId);
            };
            FactoryBody = (_, _) => InvokeOperation;
            Operation = RunOperationAsync;
        }

        public CodeAltaProviderDocument Definition { get; } = new() { ProviderType = "codex", ProviderKey = "synthetic-provider" };
        public List<string> Events { get; } = [];
        public Func<string> GetStateRootPath { get; }
        public Func<string> RootValue { get; set; } = () => "synthetic-root";
        public Func<string> FormatInvalidProvider { get; }
        public Func<string> InvalidProviderText { get; set; } = () => "fixture invalid provider";
        public Action<string?> OnAuthenticated { get; }
        public Action<string?> CompletionAction { get; set; } = _ => { };
        public Action SyntheticHomeAction { get; set; } = () => { };
        public Func<CancellationToken, Task> WaitBody { get; set; } = _ => Task.CompletedTask;
        public Func<CodeAltaProviderDocument, Func<string>, AuthenticationOperation> FactoryBody { get; set; }
        public AuthenticationOperation Operation { get; set; }
        public string? RawAccountId { get; set; } = "synthetic-resolved-id";
        public string? CompletedId { get; private set; }
        public CodeAltaProviderDocument? ObservedDefinition { get; private set; }
        public Func<string>? ObservedRootCallback { get; private set; }
        public Action<string?>? ObservedCompletionCallback { get; private set; }
        public CancellationToken ObservedToken { get; private set; }
        public CancellationToken ObservedWaitToken { get; private set; }
        public string? ObservedRoot { get; private set; }
        public string? ObservedKey { get; private set; }
        public string? ObservedSource { get; private set; }
        public string? ObservedConfiguredAccountId { get; private set; }

        public AuthenticationOperation CreateOperation(CodeAltaProviderDocument definition, Func<string> getStateRootPath)
        {
            Events.Add("factory");
            ObservedDefinition = definition;
            ObservedRootCallback = getStateRootPath;
            return FactoryBody(definition, getStateRootPath);
        }

        public AuthenticationOperation RecordConstruction(CodeAltaProviderDocument definition, Func<string> root)
        {
            // Inert, fake-controlled constructor/default/home sequencing. No store validation,
            // real constructor, environment discovery, import, refresh, persistence or protocol coverage.
            ObservedRoot = root();
            Events.Add("synthetic-store");
            Events.Add("synthetic-http-oauth");
            Events.Add("synthetic-key");
            ObservedKey = definition.ProviderKey;
            Events.Add("synthetic-source");
            ObservedSource = definition.AuthSource ?? "codealta_oauth";
            Events.Add("synthetic-account");
            ObservedConfiguredAccountId = definition.AccountId;
            Events.Add("synthetic-home");
            SyntheticHomeAction();
            Events.Add("synthetic-manager");
            return InvokeOperation;
        }

        public ValueTask InvokeOperation(Action<string?> onAuthenticated, CancellationToken token)
        {
            Events.Add("operation");
            ObservedCompletionCallback = onAuthenticated;
            ObservedToken = token;
            return Operation(onAuthenticated, token);
        }

        public async ValueTask RunOperationAsync(Action<string?> onAuthenticated, CancellationToken token)
        {
            Events.Add("synthetic-context");
            ObservedWaitToken = token;
            var wait = WaitBody(token);
            await wait;
            Events.Add("synthetic-project-id");
            var rawId = RawAccountId;
            onAuthenticated(rawId);
        }
    }
}
