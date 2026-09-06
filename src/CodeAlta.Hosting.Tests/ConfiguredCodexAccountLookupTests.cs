using CodeAlta.Catalog;
using Authentication = CodeAlta.Hosting.ConfiguredCodexAuthentication;

namespace CodeAlta.Hosting.Tests;

// Original pure-core cases: synthetic documents/strings/metadata and mandatory instance-owned factories.
// Added public null-input cases must fail required guards before the concrete factory; type is NOT a barrier.
// No concrete credential/protocol records, stores, managers, clients, hosts, runtimes or root discovery.
// Fakes establish forwarding, lazy consumption and callback ordering, NOT real storage, JWT resolution
// or credential projection correctness. The once-only raw-label projection is a source-audited change.
// SR may read output-directory localization content; existing assembly writerless logging still applies.
[TestClass]
public sealed class ConfiguredCodexAccountLookupTests
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
    public async Task RequiredArguments_ValidateEachInputAndEveryPrecedenceSuffixBeforeWork(int index, bool nullSuffix)
    {
        var fake = new RecordingLookup();
        var missing = new bool[4];
        for (var position = index; position < (nullSuffix ? missing.Length : index + 1); position++)
        {
            missing[position] = true;
        }

        // Intentional nulls exercise required-object guards, never provider key/root-value validation.
        var failure = await Assert.ThrowsExactlyAsync<ArgumentNullException>(() =>
            Authentication.ReadAccountMetadataAsync(
                missing[0] ? null! : fake.Definition,
                missing[1] ? null! : fake.GetStateRootPath,
                missing[2] ? null! : fake.OnMetadata,
                missing[3] ? null! : fake.CreateOperation,
                CancellationToken.None));

        var names = new[] { "definition", "getStateRootPath", "onMetadata", "createOperation" };
        Assert.AreEqual(names[index], failure.ParamName);
        Assert.IsEmpty(fake.Events);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow(" ")]
    [DataRow("\t")]
    [DataRow("codex")]
    [DataRow("Codex")]
    [DataRow("CODEX")]
    [DataRow(" codex")]
    [DataRow("codex ")]
    [DataRow("codex-subscription")]
    [DataRow("other")]
    public async Task ProviderType_IsNotAnEligibilityGuard(string? providerType)
    {
        var fake = new RecordingLookup();
        fake.Definition.ProviderType = providerType;

        await fake.InvokeAsync();

        Assert.AreSame(fake.Metadata, fake.ObservedMetadata);
        CollectionAssert.AreEqual(new[] { "factory", "operation", "metadata", "completed" }, fake.Events);
    }

    [TestMethod]
    public async Task Core_ForwardsOriginalDefinitionRootCallbackMetadataCallbackAndTokenWithoutConsumingRoot()
    {
        var fake = new RecordingLookup();
        fake.RootValue = () => throw new AssertFailedException("The core must not consume the root.");
        using var cancellation = new CancellationTokenSource();

        var task = fake.InvokeAsync(cancellation.Token);
        try
        {
            Assert.IsTrue(task.IsCompletedSuccessfully);
            await task;
            Assert.AreSame(fake.Definition, fake.ObservedDefinition);
            Assert.AreSame(fake.GetStateRootPath, fake.ObservedRootCallback);
            Assert.AreSame(fake.OnMetadata, fake.ObservedMetadataCallback);
            Assert.AreEqual(cancellation.Token, fake.ObservedToken);
            Assert.AreSame(fake.Metadata, fake.ObservedMetadata);
            CollectionAssert.AreEqual(new[] { "factory", "operation", "metadata", "completed" }, fake.Events);
        }
        finally
        {
            await task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [TestMethod]
    public async Task Factory_ControlsLazyRootAndKeyConsumptionWithoutCoreSnapshots()
    {
        var fake = new RecordingLookup();
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

        CollectionAssert.AreEqual(new[] { "factory", "root", "root", "operation", "metadata", "completed" }, fake.Events);
    }

    [TestMethod]
    [DataRow(null, null)]
    [DataRow("", "")]
    [DataRow(" ", " ")]
    [DataRow("\t", "\t")]
    [DataRow(" key with spaces ", " root with spaces ")]
    public async Task InertKeyAndRoot_AreForwardedWithoutCoreValidationOrNormalization(string? key, string? root)
    {
        var fake = new RecordingLookup();
        // Intentionally violate annotations only with inert fixture values, not actual stores.
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

        CollectionAssert.AreEqual(new[] { "factory", "root", "operation", "metadata", "completed" }, fake.Events);
    }

    [TestMethod]
    [DataRow(null, null)]
    [DataRow(false, "codex_auth_file_readonly")]
    [DataRow(true, "arbitrary source")]
    [DataRow(false, " ")]
    public async Task IrrelevantConfiguration_DoesNotSuppressExplicitLookup(bool? enabled, string? authSource)
    {
        var fake = new RecordingLookup();
        fake.Definition.Enabled = enabled;
        fake.Definition.AuthSource = authSource;
        fake.Definition.AccountId = "synthetic-configured-account";
        fake.Definition.ApiKey = "inert-not-a-credential";
        fake.Definition.ApiKeyEnv = "inert-never-looked-up";
        fake.Definition.ApiUrl = "inert-not-a-uri";
        fake.Definition.Model = "synthetic-model";

        await fake.InvokeAsync();

        Assert.AreSame(fake.Definition, fake.ObservedDefinition);
        Assert.AreSame(fake.Metadata, fake.ObservedMetadata);
        CollectionAssert.AreEqual(new[] { "factory", "operation", "metadata", "completed" }, fake.Events);
    }

    [TestMethod]
    [DataRow("factory")]
    [DataRow("fake-root")]
    [DataRow("synchronous-operation")]
    [DataRow("faulted-operation")]
    [DataRow("callback")]
    public async Task Failures_PreserveIdentityAndStopAtFailingStage(string stage)
    {
        var fake = new RecordingLookup();
        var expected = new InvalidOperationException("synthetic failure");
        string[] expectedEvents;
        switch (stage)
        {
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
                fake.Operation = (_, _) => throw expected;
                expectedEvents = ["factory", "operation"];
                break;
            case "faulted-operation":
                fake.Operation = (_, _) => ValueTask.FromException(expected);
                expectedEvents = ["factory", "operation"];
                break;
            case "callback":
                fake.MetadataAction = _ => throw expected;
                expectedEvents = ["factory", "operation", "metadata"];
                break;
            default:
                throw new AssertFailedException("Unknown synthetic failure stage.");
        }

        var actual = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fake.InvokeAsync());

        Assert.AreSame(expected, actual);
        CollectionAssert.AreEqual(expectedEvents, fake.Events);
    }

    [TestMethod]
    public async Task PrecanceledToken_ReachesIgnoringOperationAndMetadataCallback()
    {
        var fake = new RecordingLookup();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var task = fake.InvokeAsync(cancellation.Token);
        try
        {
            Assert.IsTrue(task.IsCompletedSuccessfully);
            await task;
            Assert.AreEqual(cancellation.Token, fake.ObservedToken);
            Assert.IsTrue(fake.ObservedToken.IsCancellationRequested);
            CollectionAssert.AreEqual(new[] { "factory", "operation", "metadata", "completed" }, fake.Events);
        }
        finally
        {
            await task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [TestMethod]
    public async Task PendingOperation_CooperativelyCancelsWithoutReportingMetadata()
    {
        var fake = new RecordingLookup();
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.Operation = async (onMetadata, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            onMetadata(fake.Metadata);
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
                // Join expected cancellation and release the gate before disposing the source.
            }
        }
    }

    [TestMethod]
    [DataRow("definition")]
    [DataRow("getStateRootPath")]
    [DataRow("onMetadata")]
    [DataRow("all")]
    public async Task PublicRequiredArguments_ReturnFaultedTaskBeforeFactory(string missing)
    {
        // There is no type barrier. Every row must fail a required-object guard before construction.
        var definition = new CodeAltaProviderDocument { ProviderType = "codex", ProviderKey = "synthetic-provider" };
        Func<string> getRoot = () => throw new AssertFailedException("Root callback must not be invoked.");
        Action<CodexAccountMetadata?> onMetadata = _ => throw new AssertFailedException("Metadata callback must not be invoked.");

        var task = Authentication.ReadAccountMetadataAsync(
            missing is "definition" or "all" ? null! : definition,
            missing is "getStateRootPath" or "all" ? null! : getRoot,
            missing is "onMetadata" or "all" ? null! : onMetadata,
            CancellationToken.None);

        Assert.IsTrue(task.IsFaulted);
        var failure = await Assert.ThrowsExactlyAsync<ArgumentNullException>(() => task);
        Assert.AreEqual(missing == "all" ? "definition" : missing, failure.ParamName);
    }

    private sealed class RecordingLookup
    {
        public RecordingLookup()
        {
            GetStateRootPath = () =>
            {
                Events.Add("root");
                return RootValue();
            };
            OnMetadata = metadata =>
            {
                Events.Add("metadata");
                ObservedMetadata = metadata;
                MetadataAction(metadata);
            };
            FactoryBody = (_, _) => InvokeOperation;
            Operation = (onMetadata, _) =>
            {
                onMetadata(Metadata);
                Events.Add("completed");
                return ValueTask.CompletedTask;
            };
        }

        public CodeAltaProviderDocument Definition { get; } = new()
        {
            ProviderType = "codex",
            ProviderKey = "synthetic-provider",
        };

        public List<string> Events { get; } = [];

        public CodexAccountMetadata? Metadata { get; set; } = new("synthetic-id", "raw synthetic label");

        public Func<string> GetStateRootPath { get; }

        public Func<string> RootValue { get; set; } = () => "synthetic-root";

        public Action<CodexAccountMetadata?> OnMetadata { get; }

        public Action<CodexAccountMetadata?> MetadataAction { get; set; } = _ => { };

        public Func<CodeAltaProviderDocument, Func<string>, CodexAccountLookupOperation> FactoryBody { get; set; }

        public CodexAccountLookupOperation Operation { get; set; }

        public CodeAltaProviderDocument? ObservedDefinition { get; private set; }

        public Func<string>? ObservedRootCallback { get; private set; }

        public Action<CodexAccountMetadata?>? ObservedMetadataCallback { get; private set; }

        public CancellationToken ObservedToken { get; private set; }

        public CodexAccountMetadata? ObservedMetadata { get; private set; }

        public CodexAccountLookupOperation CreateOperation(CodeAltaProviderDocument definition, Func<string> getStateRootPath)
        {
            Events.Add("factory");
            ObservedDefinition = definition;
            ObservedRootCallback = getStateRootPath;
            return FactoryBody(definition, getStateRootPath);
        }

        public ValueTask InvokeOperation(Action<CodexAccountMetadata?> onMetadata, CancellationToken cancellationToken)
        {
            Events.Add("operation");
            ObservedMetadataCallback = onMetadata;
            ObservedToken = cancellationToken;
            return Operation(onMetadata, cancellationToken);
        }

        public Task InvokeAsync(CancellationToken cancellationToken = default)
            => Authentication.ReadAccountMetadataAsync(
                Definition, GetStateRootPath, OnMetadata, CreateOperation, cancellationToken);
    }
}
