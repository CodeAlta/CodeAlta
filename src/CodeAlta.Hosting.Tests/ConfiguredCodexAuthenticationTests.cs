using CodeAlta.Catalog;
using Coordinator = CodeAlta.Hosting.ConfiguredCodexAuthentication;
using AuthenticationOperation = CodeAlta.Hosting.CodexAuthenticationTestOperation;

namespace CodeAlta.Hosting.Tests;

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
    [DataRow(0, "definition")]
    [DataRow(1, "getStateRootPath")]
    [DataRow(2, "formatInvalidProvider")]
    [DataRow(3, "onAuthenticated")]
    [DataRow(4, "definition")]
    public async Task PublicEntry_RequiredNullGuardsPrecedeEveryConcreteFactory(int missing, string parameter)
    {
        var definition = new CodeAltaProviderDocument { ProviderType = "wrong", ProviderKey = "synthetic-provider" };
        Func<string> root = () => throw new AssertFailedException("Public guard must precede root discovery.");
        Func<string> mismatch = () => throw new AssertFailedException("Public guard must precede mismatch formatting.");
        Action<string?> completion = _ => throw new AssertFailedException("Public guard must precede completion.");
        // Every row lacks a required object. Wrong type and throwing callbacks are secondary only.
        var route = ConfiguredCodexAuthentication.TestAuthenticationAsync(
            missing is 0 or 4 ? null! : definition,
            missing is 1 or 4 ? null! : root,
            missing is 2 or 4 ? null! : mismatch,
            missing is 3 or 4 ? null! : completion,
            default);
        var alreadyFaulted = route.IsFaulted;
        var actual = await Assert.ThrowsExactlyAsync<ArgumentNullException>(() => route.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsTrue(alreadyFaulted);
        Assert.AreEqual(parameter, actual.ParamName);
    }

    [TestMethod]
    [DataRow(0, false, "definition")]
    [DataRow(1, false, "getStateRootPath")]
    [DataRow(2, false, "formatInvalidProvider")]
    [DataRow(3, false, "onAuthenticated")]
    [DataRow(4, false, "createOperation")]
    [DataRow(0, true, "definition")]
    [DataRow(1, true, "getStateRootPath")]
    [DataRow(2, true, "formatInvalidProvider")]
    [DataRow(3, true, "onAuthenticated")]
    [DataRow(4, true, "createOperation")]
    public async Task RequiredArguments_ValidateEachInputAndEveryPrecedenceSuffix(int first, bool suffix, string parameter)
    {
        var fake = new RecordingAuthenticationTest();
        fake.Definition.ProviderType = "wrong";
        bool Missing(int index) => suffix ? index >= first : index == first;
        var route = Coordinator.TestCodexAuthenticationCoreAsync(
            Missing(0) ? null! : fake.Definition,
            Missing(1) ? null! : fake.GetStateRootPath,
            Missing(2) ? null! : fake.FormatInvalidProvider,
            Missing(3) ? null! : fake.OnAuthenticated,
            Missing(4) ? null! : fake.CreateOperation,
            default);
        var actual = await Assert.ThrowsExactlyAsync<ArgumentNullException>(() => route.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(parameter, actual.ParamName);
        Assert.AreEqual(0, fake.Events.Count);
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
    [DataRow("codex\t")]
    [DataRow("other")]
    public async Task ProviderType_RequiresExactOrdinalCodex(string? providerType)
    {
        var fake = new RecordingAuthenticationTest();
        fake.Definition.ProviderType = providerType;
        var route = InvokeAsync(fake);
        var actual = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => route.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual("fixture invalid provider", actual.Message);
        CollectionAssert.AreEqual(new[] { "format-invalid" }, fake.Events);
    }

    [TestMethod]
    public async Task Mismatch_UsesSuppliedLocalizedMessage()
    {
        var fake = new RecordingAuthenticationTest();
        fake.Definition.ProviderType = "wrong";
        fake.InvalidProviderText = () => "Sélection synthétique · invalid provider";
        var route = InvokeAsync(fake);
        var actual = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => route.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual("Sélection synthétique · invalid provider", actual.Message);
        CollectionAssert.AreEqual(new[] { "format-invalid" }, fake.Events);
    }

    [TestMethod]
    public async Task MatchingType_ForwardsOriginalReferencesAndTokenWithoutConsumingRoot()
    {
        var fake = new RecordingAuthenticationTest();
        using var cancellation = new CancellationTokenSource();
        fake.RootValue = () => throw new AssertFailedException("The core must not consume the root.");
        var route = InvokeAsync(fake, cancellation.Token);
        await ObserveAsync(route);
        Assert.AreSame(fake.Definition, fake.ObservedDefinition);
        Assert.AreSame(fake.GetStateRootPath, fake.ObservedRootCallback);
        Assert.AreSame(fake.OnAuthenticated, fake.ObservedCompletionCallback);
        Assert.AreEqual(cancellation.Token, fake.ObservedToken);
        CollectionAssert.AreEqual(new[] { "factory", "operation", "synthetic-context", "synthetic-project-id", "completion" }, fake.Events);
    }

    [TestMethod]
    public async Task Factory_ControlsLazyRootKeySourceAccountAndSyntheticHomeOrdering()
    {
        var fake = new RecordingAuthenticationTest();
        fake.FactoryBody = (definition, root) =>
        {
            // Mutating the original after core selection proves no snapshot/pre-reading by the core.
            definition.ProviderKey = " changed key ";
            definition.AuthSource = " changed source ";
            definition.AccountId = " changed configured id ";
            fake.RootValue = () => " changed root ";
            return fake.RecordConstruction(definition, root);
        };
        fake.SyntheticHomeAction = () =>
        {
            Assert.AreEqual(" changed key ", fake.ObservedKey);
            Assert.AreEqual(" changed source ", fake.ObservedSource);
            Assert.AreEqual(" changed configured id ", fake.ObservedConfiguredAccountId);
            fake.Definition.AccountId = "after fake constructor read";
        };
        var route = InvokeAsync(fake);
        await ObserveAsync(route);
        Assert.AreEqual(" changed root ", fake.ObservedRoot);
        Assert.AreEqual(" changed configured id ", fake.ObservedConfiguredAccountId);
        CollectionAssert.AreEqual(new[]
        {
            "factory", "root", "synthetic-store", "synthetic-http-oauth", "synthetic-key",
            "synthetic-source", "synthetic-account", "synthetic-home", "synthetic-manager",
            "operation", "synthetic-context", "synthetic-project-id", "completion",
        }, fake.Events);
    }

    [TestMethod]
    [DataRow(null, null)]
    [DataRow("", "")]
    [DataRow(" \t", " \t")]
    [DataRow("synthetic-root", "synthetic-key")]
    [DataRow(" padded root ", " padded key ")]
    public async Task RootAndKey_AreNotNormalizedByCore(string? root, string? key)
    {
        var fake = new RecordingAuthenticationTest();
        fake.RootValue = () => root!;
        fake.Definition.ProviderKey = key!; // Deliberately probe unvalidated input without a concrete constructor.
        fake.FactoryBody = fake.RecordConstruction;
        var route = InvokeAsync(fake);
        await ObserveAsync(route);
        Assert.AreEqual(root, fake.ObservedRoot);
        Assert.AreEqual(key, fake.ObservedKey);
        Assert.IsTrue(fake.Events.Contains("synthetic-home"));
        Assert.IsTrue(fake.Events.Contains("completion"));
    }

    [TestMethod]
    [DataRow(null, "codealta_oauth")]
    [DataRow("", "")]
    [DataRow(" \t", " \t")]
    [DataRow("codealta_oauth", "codealta_oauth")]
    [DataRow("codex_auth_import", "codex_auth_import")]
    [DataRow("codex_auth_file_readonly", "codex_auth_file_readonly")]
    [DataRow("CODEALTA_OAUTH", "CODEALTA_OAUTH")]
    [DataRow(" codealta_oauth ", " codealta_oauth ")]
    [DataRow("unknown", "unknown")]
    public async Task AuthSource_UsesNullOnlyDefaultAndOtherwiseRemainsRaw(string? source, string expected)
    {
        var fake = new RecordingAuthenticationTest();
        fake.Definition.AuthSource = source;
        fake.FactoryBody = fake.RecordConstruction;
        var route = InvokeAsync(fake);
        await ObserveAsync(route);
        Assert.AreEqual(source, fake.ObservedDefinition!.AuthSource);
        Assert.AreEqual(expected, fake.ObservedSource);
        Assert.IsTrue(fake.Events.IndexOf("synthetic-home") < fake.Events.IndexOf("synthetic-manager"));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow(" \t")]
    [DataRow("synthetic-configured-id")]
    [DataRow(" padded configured id ")]
    public async Task ConfiguredAccountId_IsConsumedRawDuringFactory(string? accountId)
    {
        var fake = new RecordingAuthenticationTest();
        fake.Definition.AccountId = accountId;
        fake.FactoryBody = fake.RecordConstruction;
        fake.Operation = (complete, _) =>
        {
            fake.Definition.AccountId = "later mutation";
            complete("synthetic resolved id");
            return ValueTask.CompletedTask;
        };
        var route = InvokeAsync(fake);
        await ObserveAsync(route);
        Assert.AreEqual(accountId, fake.ObservedConfiguredAccountId);
        Assert.AreEqual("synthetic resolved id", fake.CompletedId);
        Assert.IsTrue(fake.Events.IndexOf("synthetic-account") < fake.Events.IndexOf("operation"));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Enabled_DoesNotAddEligibility(bool? enabled)
    {
        var fake = new RecordingAuthenticationTest();
        fake.Definition.Enabled = enabled;
        var route = InvokeAsync(fake);
        await ObserveAsync(route);
        Assert.AreEqual(enabled, fake.ObservedDefinition!.Enabled);
        Assert.IsTrue(fake.Events.Contains("completion"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PrecanceledToken_ReachesIgnoringAndCooperativeOperation(bool cooperative)
    {
        var fake = new RecordingAuthenticationTest();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        fake.WaitBody = token => cooperative ? Task.FromCanceled(token) : Task.CompletedTask;
        var route = InvokeAsync(fake, cancellation.Token);
        await ObserveAsync(route, canceled: cooperative, expectedToken: cancellation.Token);
        Assert.AreEqual(cancellation.Token, fake.ObservedToken);
        Assert.AreEqual(cancellation.Token, fake.ObservedWaitToken);
        Assert.AreEqual(!cooperative, fake.Events.Contains("completion"));
        Assert.AreEqual("factory", fake.Events[0]);
    }

    [TestMethod]
    public async Task PendingOperation_CooperativelyCancelsWithOriginalToken()
    {
        var fake = new RecordingAuthenticationTest();
        using var cancellation = new CancellationTokenSource();
        var gate = NewGate();
        var wait = gate.Task;
        fake.WaitBody = _ => wait;
        using var registration = cancellation.Token.Register(() => gate.TrySetCanceled(cancellation.Token));
        Task? route = null;
        try
        {
            route = InvokeAsync(fake, cancellation.Token);
            Assert.IsFalse(route.IsCompleted);
            cancellation.Cancel();
            await ObserveAsync(route, canceled: true, expectedToken: cancellation.Token);
            Assert.AreEqual(cancellation.Token, fake.ObservedToken);
            Assert.AreEqual(cancellation.Token, fake.ObservedWaitToken);
            Assert.IsFalse(fake.Events.Contains("completion"));
        }
        finally
        {
            cancellation.Cancel();
            gate.TrySetCanceled(cancellation.Token);
            await JoinBothAsync(route, wait, canceled: true, expectedToken: cancellation.Token);
        }
    }

    [TestMethod]
    public async Task PendingOperationFailure_KeepsIdentity()
    {
        var fake = new RecordingAuthenticationTest();
        var expected = new InvalidOperationException("synthetic pending failure");
        var gate = NewGate();
        var wait = gate.Task;
        fake.WaitBody = _ => wait;
        Task? route = null;
        try
        {
            route = InvokeAsync(fake);
            Assert.IsFalse(route.IsCompleted);
            gate.SetException(expected);
            await ObserveAsync(route, expected);
            Assert.IsFalse(fake.Events.Contains("completion"));
        }
        finally
        {
            gate.TrySetException(expected);
            await JoinBothAsync(route, wait, expected);
        }
    }

    // Hosting binding; the Coordinator alias preserves the original required-argument method verbatim.
    private static Task InvokeAsync(RecordingAuthenticationTest fake, CancellationToken token = default)
        => Coordinator.TestCodexAuthenticationCoreAsync(
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
