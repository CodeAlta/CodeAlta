using CodeAlta.Catalog;
using CodeAlta.Hosting;
using CodeAlta.Tui.App;
using Authentication = CodeAlta.Hosting.ConfiguredCodexAuthentication;
using Coordinator = CodeAlta.Tui.App.ProviderFrontendCoordinator;

namespace CodeAlta.Tests;

// Retained presentation/mixed cases use the Hosting internal core and TUI formatter, never public wrappers.
// All documents, strings and non-secret metadata are synthetic; mandatory factories are instance-owned.
// No concrete credential/protocol records, stores, managers, clients, hosts, runtimes or root discovery.
// Fakes establish forwarding, lazy consumption and callback ordering, NOT real storage, JWT resolution
// or credential projection correctness. The once-only raw-label projection is a source-audited change.
// SR may read output-directory localization content; existing assembly writerless logging still applies.
[TestClass]
public sealed class ConfiguredCodexAccountLookupTests
{
    [TestMethod]
    [DataRow("null")]
    [DataRow("missing-id")]
    [DataRow("present-id")]
    public async Task Metadata_IsForwardedAndFormattedSynchronouslyBeforeOperationCompletion(string kind)
    {
        var fake = new RecordingLookup();
        fake.Metadata = kind switch
        {
            "null" => null,
            "missing-id" => new CodexAccountMetadata(null, " raw synthetic label "),
            "present-id" => new CodexAccountMetadata("synthetic-id", " raw synthetic label "),
            _ => throw new AssertFailedException("Unknown synthetic metadata kind."),
        };
        ProviderTestResult? formatted = null;
        fake.MetadataAction = metadata =>
        {
            Assert.AreSame(fake.Metadata, metadata);
            formatted = Coordinator.FormatCodexAccountMetadataResult(metadata);
            fake.Events.Add("formatted");
        };
        fake.Operation = (onMetadata, _) =>
        {
            onMetadata(fake.Metadata);
            Assert.IsNotNull(formatted);
            Assert.AreEqual(kind != "null", formatted.Value.Success);
            Assert.AreEqual(kind == "present-id" ? 1 : 0, formatted.Value.ModelCount);
            fake.Events.Add("completed");
            return ValueTask.CompletedTask;
        };

        await fake.InvokeAsync();

        CollectionAssert.AreEqual(new[] { "factory", "operation", "metadata", "formatted", "completed" }, fake.Events);
    }

    [TestMethod]
    public void Formatter_NullMetadataReturnsLocalizedLoginRequiredFailure()
    {
        var result = Coordinator.FormatCodexAccountMetadataResult(null);

        Assert.IsFalse(result.Success);
        Assert.AreEqual(SR.T("Login required before account/workspace metadata can be listed."), result.Message);
        Assert.AreEqual(0, result.ModelCount);
    }

    [TestMethod]
    [DataRow(null, null)]
    [DataRow("", "")]
    [DataRow(" ", " ")]
    [DataRow("\t", "\t")]
    [DataRow(null, " raw label ")]
    [DataRow(" ", "raw-label")]
    [DataRow("", "raw\tlabel")]
    public void Formatter_MissingIdPreservesLabelFallbackAndSuccessWithZeroCount(string? accountId, string? accountLabel)
    {
        var result = Coordinator.FormatCodexAccountMetadataResult(new CodexAccountMetadata(accountId, accountLabel));

        var expectedLabel = string.IsNullOrWhiteSpace(accountLabel) ? SR.T("ChatGPT account/workspace") : accountLabel;
        Assert.IsTrue(result.Success);
        Assert.AreEqual(SR.T("{0}: token did not expose an account/workspace id; enter one in Account/Workspace Id if required.", expectedLabel), result.Message);
        Assert.AreEqual(0, result.ModelCount);
    }

    [TestMethod]
    [DataRow("id", null)]
    [DataRow("id", "")]
    [DataRow("id", " ")]
    [DataRow("id", "\t")]
    [DataRow("id", " raw label ")]
    [DataRow(" id ", "raw-label")]
    [DataRow("id", "raw\tlabel")]
    public void Formatter_PresentIdPreservesRawValuesAndSuccessWithOneCount(string accountId, string? accountLabel)
    {
        var result = Coordinator.FormatCodexAccountMetadataResult(new CodexAccountMetadata(accountId, accountLabel));

        var expectedLabel = string.IsNullOrWhiteSpace(accountLabel) ? SR.T("ChatGPT account/workspace") : accountLabel;
        Assert.IsTrue(result.Success);
        Assert.AreEqual(SR.T("{0}: {1}", expectedLabel, accountId), result.Message);
        Assert.AreEqual(1, result.ModelCount);
    }

    [TestMethod]
    public async Task PendingOperation_CanReadOriginalDefinitionAfterMutationAndReportBeforeCompletion()
    {
        var fake = new RecordingLookup();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowMetadata = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.Definition.AccountId = "before-id";
        fake.FactoryBody = (definition, _) =>
        {
            fake.Operation = async (onMetadata, _) =>
            {
                entered.TrySetResult();
                await allowMetadata.Task;
                // A fake-controlled late read, not a test of the concrete store/resolver projection.
                onMetadata(new CodexAccountMetadata(definition.AccountId, " raw label "));
                reported.TrySetResult();
                await allowCompletion.Task;
                fake.Events.Add("completed");
            };
            return fake.InvokeOperation;
        };
        ProviderTestResult? formatted = null;
        fake.MetadataAction = metadata =>
        {
            formatted = Coordinator.FormatCodexAccountMetadataResult(metadata);
            fake.Events.Add("formatted");
        };

        var task = fake.InvokeAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(task.IsCompleted);
            CollectionAssert.AreEqual(new[] { "factory", "operation" }, fake.Events);
            fake.Definition.AccountId = "after-id";
            allowMetadata.TrySetResult();
            await reported.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(task.IsCompleted);
            Assert.IsNotNull(formatted);
            Assert.AreEqual(SR.T("{0}: {1}", " raw label ", "after-id"), formatted.Value.Message);
            CollectionAssert.AreEqual(new[] { "factory", "operation", "metadata", "formatted" }, fake.Events);
            allowCompletion.TrySetResult();
            await task.WaitAsync(TimeSpan.FromSeconds(5));
            CollectionAssert.AreEqual(new[] { "factory", "operation", "metadata", "formatted", "completed" }, fake.Events);
        }
        finally
        {
            allowMetadata.TrySetResult();
            allowCompletion.TrySetResult();
            await task.WaitAsync(TimeSpan.FromSeconds(5));
        }
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
