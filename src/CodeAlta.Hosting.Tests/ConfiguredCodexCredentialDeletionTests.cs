using CodeAlta.Catalog;
using Authentication = CodeAlta.Hosting.ConfiguredCodexAuthentication;

namespace CodeAlta.Hosting.Tests;

// Original cases invoke only the static production-connected core, never a coordinator instance.
// Added public null-input cases fail required guards before the deferred production factory.
// Definitions/strings are synthetic; mandatory recording factories return token-only fake operations.
// No provider credential/protocol objects, managers, clients, stores, runtime, host or root discovery.
// These fakes establish forwarding and lazy-consumption control, NOT concrete constructor ordering
// or real store null/blank-root validation. The production construction expression is source-compared.
// SR may read output-directory localization content; assembly-level writerless logging still applies.
[TestClass]
public sealed class ConfiguredCodexCredentialDeletionTests
{
    [TestMethod]
    [DataRow(0, false)]
    [DataRow(1, false)]
    [DataRow(2, false)]
    [DataRow(3, false)]
    [DataRow(0, true)]
    [DataRow(1, true)]
    [DataRow(2, true)]
    [DataRow(3, true)]
    public async Task RequiredArguments_ValidateEachInputAndEveryPrecedenceSuffixBeforeTypeOrWork(int index, bool nullSuffix)
    {
        var fake = new RecordingDeletion();
        fake.Definition.ProviderType = "wrong";
        var missing = new bool[4];
        for (var position = index; position < (nullSuffix ? missing.Length : index + 1); position++)
        {
            missing[position] = true;
        }

        var failure = await Assert.ThrowsExactlyAsync<ArgumentNullException>(() =>
            Authentication.DeleteCredentialAsync(
                missing[0] ? null! : fake.Definition,
                missing[1] ? null! : fake.GetStateRootPath,
                missing[2] ? null! : fake.FormatInvalidProvider,
                missing[3] ? null! : fake.CreateOperation,
                CancellationToken.None));

        var names = new[] { "definition", "getStateRootPath", "formatInvalidProvider", "createOperation" };
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
        var fake = new RecordingDeletion();
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
        var fake = new RecordingDeletion();
        fake.Definition.ProviderType = "wrong";
        fake.InvalidProviderText = () => SR.T("Select a Codex provider first.");

        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fake.InvokeAsync());

        Assert.AreEqual(SR.T("Select a Codex provider first."), failure.Message);
        CollectionAssert.AreEqual(new[] { "format-invalid" }, fake.Events);
    }

    [TestMethod]
    public async Task MatchingType_ForwardsOriginalReferencesAndTokenWithoutFormattingOrConsumingRoot()
    {
        var fake = new RecordingDeletion();
        fake.InvalidProviderText = () => throw new AssertFailedException("Matching type must not format an error.");
        fake.RootValue = () => throw new AssertFailedException("The core must not consume the root.");
        using var cancellation = new CancellationTokenSource();

        var task = fake.InvokeAsync(cancellation.Token);

        Assert.IsTrue(task.IsCompletedSuccessfully);
        await task;
        Assert.AreSame(fake.Definition, fake.ObservedDefinition);
        Assert.AreSame(fake.GetStateRootPath, fake.ObservedRootCallback);
        Assert.AreEqual(cancellation.Token, fake.ObservedToken);
        CollectionAssert.AreEqual(new[] { "factory", "operation" }, fake.Events);
    }

    [TestMethod]
    public async Task Factory_ControlsRepeatedLazyRootConsumptionAndSeesDefinitionMutation()
    {
        var fake = new RecordingDeletion();
        var rootValue = " first synthetic root ";
        fake.RootValue = () =>
        {
            fake.Definition.ProviderKey = " mutated synthetic key ";
            return rootValue;
        };
        fake.FactoryBody = (definition, getRoot) =>
        {
            Assert.AreEqual("synthetic-provider", definition.ProviderKey);
            Assert.AreEqual(" first synthetic root ", getRoot());
            Assert.AreEqual(" mutated synthetic key ", definition.ProviderKey);
            rootValue = "second synthetic root";
            Assert.AreEqual("second synthetic root", getRoot());
            return fake.InvokeOperation;
        };

        await fake.InvokeAsync();

        CollectionAssert.AreEqual(new[] { "factory", "root", "root", "operation" }, fake.Events);
    }

    [TestMethod]
    [DataRow(null, null)]
    [DataRow(false, "codex_cli")]
    [DataRow(true, "arbitrary source")]
    [DataRow(false, " ")]
    public async Task IrrelevantConfiguration_DoesNotSuppressExplicitDeletion(bool? enabled, string? authSource)
    {
        var fake = new RecordingDeletion();
        fake.Definition.Enabled = enabled;
        fake.Definition.AuthSource = authSource;
        fake.Definition.AccountId = "synthetic-account";
        fake.Definition.ApiKey = "inert-not-a-credential";
        fake.Definition.ApiKeyEnv = "inert-never-looked-up";
        fake.Definition.ApiUrl = "inert-not-a-uri";
        fake.Definition.Model = "synthetic-model";

        await fake.InvokeAsync();

        Assert.AreSame(fake.Definition, fake.ObservedDefinition);
        CollectionAssert.AreEqual(new[] { "factory", "operation" }, fake.Events);
    }

    [TestMethod]
    [DataRow(null, null)]
    [DataRow("", "")]
    [DataRow(" ", " ")]
    [DataRow("\t", "\t")]
    [DataRow(" key with spaces ", " root with spaces ")]
    public async Task InertKeyAndRoot_AreForwardedWithoutCoreValidationOrNormalization(string? key, string? root)
    {
        var fake = new RecordingDeletion();
        // Intentionally violate non-null annotations only on inert fixture values, not real stores.
        fake.Definition.ProviderKey = key!;
        fake.RootValue = () => root!;
        fake.FactoryBody = (definition, getRoot) =>
        {
            Assert.AreSame(fake.Definition, definition);
            Assert.AreEqual(key, definition.ProviderKey);
            Assert.AreEqual(root, getRoot());
            return fake.InvokeOperation;
        };

        await fake.InvokeAsync();

        CollectionAssert.AreEqual(new[] { "factory", "root", "operation" }, fake.Events);
    }

    [TestMethod]
    [DataRow("formatter")]
    [DataRow("factory")]
    [DataRow("fake-root")]
    [DataRow("synchronous-operation")]
    [DataRow("faulted-operation")]
    public async Task Failures_PreserveIdentityAndStopAtFailingStage(string stage)
    {
        var fake = new RecordingDeletion();
        var expected = new InvalidOperationException("synthetic failure");
        string[] expectedEvents;
        switch (stage)
        {
            case "formatter":
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
            case "synchronous-operation":
                fake.Operation = _ => throw expected;
                expectedEvents = ["factory", "operation"];
                break;
            case "faulted-operation":
                fake.Operation = _ => ValueTask.FromException(expected);
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
    public async Task PrecanceledToken_ReachesIgnoringOperationAndCompletesImmediately()
    {
        var fake = new RecordingDeletion();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var task = fake.InvokeAsync(cancellation.Token);

        Assert.IsTrue(task.IsCompletedSuccessfully);
        await task;
        Assert.AreEqual(cancellation.Token, fake.ObservedToken);
        Assert.IsTrue(fake.ObservedToken.IsCancellationRequested);
        CollectionAssert.AreEqual(new[] { "factory", "operation" }, fake.Events);
    }

    [TestMethod]
    public async Task PendingOperation_IsAwaitedAndCanConsumeOriginalDefinitionAndRootAfterMutation()
    {
        var fake = new RecordingDeletion();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rootValue = "before";
        fake.RootValue = () => rootValue;
        fake.FactoryBody = (definition, getRoot) =>
        {
            fake.Operation = async _ =>
            {
                entered.TrySetResult();
                await release.Task;
                Assert.AreEqual("after-key", definition.ProviderKey);
                Assert.AreEqual("after-root", getRoot());
                fake.Events.Add("completed");
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
            CollectionAssert.AreEqual(new[] { "factory", "operation", "root", "completed" }, fake.Events);
        }
        finally
        {
            release.TrySetResult();
            await task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [TestMethod]
    public async Task PendingOperation_CooperativelyCancelsWithOriginalToken()
    {
        var fake = new RecordingDeletion();
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.Operation = async token =>
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
            var failure = await Assert.ThrowsExactlyAsync<TaskCanceledException>(
                () => task.WaitAsync(TimeSpan.FromSeconds(5)));
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
                // The expected canceled task has been joined; all gates are released before disposal.
            }
        }
    }

    [TestMethod]
    [DataRow("definition")]
    [DataRow("getStateRootPath")]
    [DataRow("formatInvalidProvider")]
    [DataRow("all")]
    public async Task PublicRequiredArguments_ReturnFaultedTaskBeforeFactory(string missing)
    {
        // Null required objects are the first barrier; a mismatched synthetic type is the second.
        // Never probe this public entry point with only an invalid key/root value.
        var definition = new CodeAltaProviderDocument { ProviderType = "not-codex", ProviderKey = "synthetic-provider" };
        Func<string> getRoot = () => throw new AssertFailedException("Root callback must not be invoked.");
        Func<string> formatInvalid = () => throw new AssertFailedException("Formatter must not be invoked.");

        var task = Authentication.DeleteCredentialAsync(
            missing is "definition" or "all" ? null! : definition,
            missing is "getStateRootPath" or "all" ? null! : getRoot,
            missing is "formatInvalidProvider" or "all" ? null! : formatInvalid,
            CancellationToken.None);

        Assert.IsTrue(task.IsFaulted);
        var failure = await Assert.ThrowsExactlyAsync<ArgumentNullException>(() => task);
        Assert.AreEqual(missing == "all" ? "definition" : missing, failure.ParamName);
    }

    private sealed class RecordingDeletion
    {
        public RecordingDeletion()
        {
            GetStateRootPath = () =>
            {
                Events.Add("root");
                return RootValue();
            };
            FactoryBody = (_, _) => InvokeOperation;
        }

        public CodeAltaProviderDocument Definition { get; } = new()
        {
            ProviderType = "codex",
            ProviderKey = "synthetic-provider",
        };

        public List<string> Events { get; } = [];

        public Func<string> GetStateRootPath { get; }

        public Func<string> RootValue { get; set; } = () => "synthetic-root";

        public Func<string> InvalidProviderText { get; set; } = () => "fixture invalid provider";

        public Func<CodeAltaProviderDocument, Func<string>, CodexSubscriptionDeleteCredentialOperation> FactoryBody { get; set; }

        public CodexSubscriptionDeleteCredentialOperation Operation { get; set; } = _ => ValueTask.CompletedTask;

        public CodeAltaProviderDocument? ObservedDefinition { get; private set; }

        public Func<string>? ObservedRootCallback { get; private set; }

        public CancellationToken ObservedToken { get; private set; }

        public string FormatInvalidProvider()
        {
            Events.Add("format-invalid");
            return InvalidProviderText();
        }

        public CodexSubscriptionDeleteCredentialOperation CreateOperation(CodeAltaProviderDocument definition, Func<string> getStateRootPath)
        {
            Events.Add("factory");
            ObservedDefinition = definition;
            ObservedRootCallback = getStateRootPath;
            return FactoryBody(definition, getStateRootPath);
        }

        public ValueTask InvokeOperation(CancellationToken cancellationToken)
        {
            Events.Add("operation");
            ObservedToken = cancellationToken;
            return Operation(cancellationToken);
        }

        public Task InvokeAsync(CancellationToken cancellationToken = default)
            => Authentication.DeleteCredentialAsync(
                Definition, GetStateRootPath, FormatInvalidProvider, CreateOperation, cancellationToken);
    }
}
