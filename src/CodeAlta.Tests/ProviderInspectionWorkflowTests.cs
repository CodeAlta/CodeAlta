using System.Collections;
using CodeAlta.Agent;
using CodeAlta.Agent.ModelCatalog;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Tui.App;
using CodeAlta.Tui.Models;

namespace CodeAlta.Tests;

// Only the real static cached helpers and production-connected fallback cores are called.
// Every core call explicitly supplies an in-memory fake factory. No coordinator, config store,
// registry, concrete provider, metadata service, credentials, filesystem root or host is created.
[TestClass]
public sealed class ProviderInspectionWorkflowTests
{
    private const string StateRootPath = "provider-inspection-fixture-root";

    [TestMethod]
    [DataRow(ModelProviderAvailability.Unknown)]
    [DataRow(ModelProviderAvailability.Probing)]
    [DataRow(ModelProviderAvailability.Ready)]
    [DataRow(ModelProviderAvailability.Disabled)]
    [DataRow(ModelProviderAvailability.Unsupported)]
    [DataRow(ModelProviderAvailability.Failed)]
    public void CachedAvailability_PreservesDifferentTestAndListPolicies(ModelProviderAvailability availability)
    {
        var definition = CreateDefinition();
        definition.SortModels = true;
        var state = CreateState(availability);
        var states = new Dictionary<string, ModelProviderState>(StringComparer.OrdinalIgnoreCase)
        {
            [definition.ProviderKey] = state,
        };

        var testHandled = ProviderFrontendCoordinator.TryBuildActiveProviderTestResult(definition, states, out var test);
        var listHandled = ProviderFrontendCoordinator.TryBuildActiveProviderModelListResult(definition, states, out var list);

        Assert.AreEqual(availability is ModelProviderAvailability.Ready or ModelProviderAvailability.Probing
            or ModelProviderAvailability.Failed or ModelProviderAvailability.Unsupported, testHandled);
        Assert.AreEqual(availability == ModelProviderAvailability.Ready, listHandled);
        if (availability == ModelProviderAvailability.Ready)
        {
            Assert.IsTrue(test.Success);
            Assert.AreEqual(2, test.ModelCount);
            Assert.AreEqual(SR.T("Using active model provider · {0} model(s) discovered.", 2), test.Message);
            Assert.IsTrue(list.Success);
            Assert.AreEqual(SR.T("Using active model provider · {0} model(s) available.", 2), list.Message);
            Assert.AreSame(state.Models, list.Models);
            CollectionAssert.AreEqual(new[] { "z-model", "a-model" }, list.Models.Select(model => model.Id).ToArray());
        }
        else
        {
            Assert.AreEqual(default, list);
            if (testHandled)
            {
                Assert.IsFalse(test.Success);
                Assert.AreEqual(state.StatusMessage, test.Message);
                Assert.AreEqual(0, test.ModelCount); // Cached non-ready models are not counted.
            }
            else
            {
                Assert.AreEqual(default, test);
            }
        }
    }

    [TestMethod]
    public void MissingCachedState_FallsThroughForBothOperations()
    {
        var states = new Dictionary<string, ModelProviderState>(StringComparer.OrdinalIgnoreCase);
        Assert.IsFalse(ProviderFrontendCoordinator.TryBuildActiveProviderTestResult(CreateDefinition(), states, out var test));
        Assert.IsFalse(ProviderFrontendCoordinator.TryBuildActiveProviderModelListResult(CreateDefinition(), states, out var list));
        Assert.AreEqual(default, test);
        Assert.AreEqual(default, list);
    }

    [TestMethod]
    [DataRow(false, "fixture", true)]
    [DataRow(false, "FIXTURE", false)]
    [DataRow(true, "FIXTURE", true)]
    [DataRow(true, "other", false)]
    public void CachedLookup_UsesOnlyKeyAndSuppliedComparerDespiteChangedDisabledSettings(
        bool ignoreCase, string key, bool expectedHandled)
    {
        var definition = CreateDefinition();
        definition.ProviderKey = key;
        definition.ProviderType = "changed-fixture-type";
        definition.ApiUrl = "https://changed.invalid";
        definition.ApiKey = "changed-literal-fake-key";
        definition.Enabled = false;
        definition.SortModels = true;
        var state = CreateState(ModelProviderAvailability.Ready);
        var states = new Dictionary<string, ModelProviderState>(ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
        {
            ["fixture"] = state,
        };

        Assert.AreEqual(expectedHandled, ProviderFrontendCoordinator.TryBuildActiveProviderTestResult(definition, states, out var test));
        Assert.AreEqual(expectedHandled, ProviderFrontendCoordinator.TryBuildActiveProviderModelListResult(definition, states, out var list));
        if (expectedHandled)
        {
            Assert.IsTrue(test.Success);
            Assert.IsTrue(list.Success);
            Assert.AreSame(state.Models, list.Models);
            Assert.AreEqual("z-model", list.Models[0].Id);
        }
        else
        {
            Assert.AreEqual(default, test);
            Assert.AreEqual(default, list);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RejectedFactory_FormatsOnlyInvalidSettingsEvenWithCanceledToken(bool listModels)
    {
        var events = new List<string>();
        var definition = CreateDefinition();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var calls = 0;
        bool Reject(CodeAltaProviderDocument actual, string root, ModelsDevCatalogService? catalog, out IModelProviderRuntime runtime)
        {
            calls++;
            Assert.AreSame(definition, actual);
            Assert.AreEqual(StateRootPath, root);
            Assert.IsNull(catalog);
            events.Add("factory");
            runtime = null!;
            return false;
        }

        var result = await InvokeAsync(listModels, definition, Reject,
            () => { events.Add("invalid"); return listModels ? "invalid list settings" : "invalid test settings"; },
            _ => throw new AssertFailedException("Rejected settings must not format success."), cancellation.Token);

        Assert.IsFalse(result.Success);
        Assert.AreEqual(listModels ? "invalid list settings" : "invalid test settings", result.Message);
        Assert.AreEqual(0, result.ModelCount);
        if (listModels)
        {
            Assert.AreEqual(0, result.Models!.Count);
        }
        Assert.AreEqual(1, calls);
        CollectionAssert.AreEqual(new[] { "factory", "invalid" }, events);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Factory_ForwardsBorrowedArgumentsOnceAndOnlyProbesThenFormatsThenDisposes(bool listModels)
    {
        var events = new List<string>();
        var runtime = new FakeRuntime(events) { Models = [new("one"), new("two")] };
        var definition = CreateDefinition();
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        bool Create(CodeAltaProviderDocument actual, string root, ModelsDevCatalogService? catalog, out IModelProviderRuntime created)
        {
            calls++;
            Assert.AreSame(definition, actual);
            Assert.AreEqual(StateRootPath, root);
            Assert.IsNull(catalog);
            events.Add("factory");
            created = runtime;
            return true;
        }

        var result = await InvokeAsync(listModels, definition, Create,
            () => throw new AssertFailedException("Valid factory must not format invalid settings."),
            count => { events.Add($"format:{count}"); return $"found {count}"; }, cancellation.Token);

        Assert.IsTrue(result.Success);
        Assert.AreEqual("found 2", result.Message);
        Assert.AreEqual(2, result.ModelCount);
        if (listModels)
        {
            Assert.AreSame(runtime.Models, result.Models);
        }
        Assert.AreEqual(1, calls);
        Assert.AreEqual(cancellation.Token, runtime.ReceivedToken);
        Assert.AreEqual(1, runtime.ProbeCount);
        Assert.AreEqual(1, runtime.DisposeCount);
        CollectionAssert.AreEqual(new[] { "factory", "probe", "format:2", "dispose" }, events);
    }

    [TestMethod]
    [DataRow(false, ModelProviderAvailability.Unknown)]
    [DataRow(false, ModelProviderAvailability.Probing)]
    [DataRow(false, ModelProviderAvailability.Ready)]
    [DataRow(false, ModelProviderAvailability.Disabled)]
    [DataRow(false, ModelProviderAvailability.Unsupported)]
    [DataRow(false, ModelProviderAvailability.Failed)]
    [DataRow(true, ModelProviderAvailability.Unknown)]
    [DataRow(true, ModelProviderAvailability.Probing)]
    [DataRow(true, ModelProviderAvailability.Ready)]
    [DataRow(true, ModelProviderAvailability.Disabled)]
    [DataRow(true, ModelProviderAvailability.Unsupported)]
    [DataRow(true, ModelProviderAvailability.Failed)]
    public async Task ReturnedProbe_IsSuccessfulRegardlessOfAvailabilityAndDiagnostics(bool listModels, ModelProviderAvailability availability)
    {
        var runtime = new FakeRuntime([]) { Availability = availability, Models = [new("model")] };
        var result = await InvokeWithFakeAsync(listModels, CreateDefinition(), runtime);
        Assert.IsTrue(result.Success);
        Assert.AreEqual("found 1", result.Message);
        Assert.AreEqual(1, result.ModelCount);
        Assert.AreEqual(1, runtime.ProbeCount);
        Assert.AreEqual(1, runtime.DisposeCount);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task EmptyProbe_IsSuccessful(bool listModels)
    {
        var runtime = new FakeRuntime([]);
        var result = await InvokeWithFakeAsync(listModels, CreateDefinition(), runtime);
        Assert.IsTrue(result.Success);
        Assert.AreEqual("found 0", result.Message);
        Assert.AreEqual(0, result.ModelCount);
        Assert.AreEqual(1, runtime.DisposeCount);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ModelList_OptionalSortingIsStableAndDoesNotMutateInput(bool? sortModels)
    {
        var models = new List<AgentModelInfo>
        {
            new("b", "Shared"), new("a", "shared", "first tie"), new("A", "SHARED", "second tie"),
            new("z", "Alpha"), new("beta"), new("empty", ""),
        };
        var original = models.ToArray();
        var runtime = new FakeRuntime([]) { Models = models };
        var definition = CreateDefinition();
        definition.SortModels = sortModels;

        var result = await InvokeWithFakeAsync(true, definition, runtime);

        Assert.IsNotNull(result.Models);
        CollectionAssert.AreEqual(original, models);
        if (sortModels == true)
        {
            Assert.AreNotSame(models, result.Models);
            var expected = new[] { original[5], original[3], original[4], original[1], original[2], original[0] };
            CollectionAssert.AreEqual(expected, result.Models.ToArray());
            for (var index = 0; index < expected.Length; index++)
            {
                Assert.AreSame(expected[index], result.Models[index]);
            }
        }
        else
        {
            Assert.AreSame(models, result.Models);
        }
        Assert.AreEqual(1, runtime.DisposeCount);
    }

    [TestMethod]
    public async Task Test_DoesNotEnumerateOrSortModelsEvenWhenRequested()
    {
        var failure = new InvalidOperationException("enumeration forbidden");
        var runtime = new FakeRuntime([]) { Models = new ThrowingEnumerationModels(failure) };
        var definition = CreateDefinition();
        definition.SortModels = true;
        var result = await InvokeWithFakeAsync(false, definition, runtime);
        Assert.IsTrue(result.Success);
        Assert.AreEqual(2, result.ModelCount);
        Assert.AreEqual(1, runtime.DisposeCount);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task Cancellation_IsOnlyForwardedAndIgnoredProbeCanSucceed(bool listModels, bool preCanceled)
    {
        using var cancellation = new CancellationTokenSource();
        if (preCanceled)
        {
            cancellation.Cancel();
        }
        var runtime = new FakeRuntime([]);
        runtime.ProbeAction = token =>
        {
            Assert.AreEqual(cancellation.Token, token);
            cancellation.Cancel();
            return Task.FromResult(runtime.CreateProbeResult()); // Deliberately ignores cancellation.
        };

        var result = await InvokeWithFakeAsync(listModels, CreateDefinition(), runtime, cancellation.Token);

        Assert.IsTrue(result.Success);
        Assert.AreEqual(1, runtime.ProbeCount);
        Assert.AreEqual(cancellation.Token, runtime.ReceivedToken);
        Assert.AreEqual(1, runtime.DisposeCount);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task CooperativeCancellation_PropagatesTokenAndDisposes(bool listModels, bool preCanceled)
    {
        using var cancellation = new CancellationTokenSource();
        if (preCanceled)
        {
            cancellation.Cancel();
        }
        var runtime = new FakeRuntime([]);
        runtime.ProbeAction = token =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<ModelProviderProbeResult>(token);
        };
        var operation = InvokeWithFakeAsync(listModels, CreateDefinition(), runtime, cancellation.Token);

        var failure = await Assert.ThrowsAsync<OperationCanceledException>(() => operation);

        Assert.AreEqual(cancellation.Token, failure.CancellationToken);
        Assert.IsTrue(operation.IsCanceled);
        Assert.AreEqual(1, runtime.ProbeCount);
        Assert.AreEqual(1, runtime.DisposeCount);
    }

    [TestMethod]
    [DataRow(false, "factory")]
    [DataRow(true, "factory")]
    [DataRow(false, "probe")]
    [DataRow(true, "probe")]
    [DataRow(false, "probe-sync")]
    [DataRow(true, "probe-sync")]
    [DataRow(false, "success-format")]
    [DataRow(true, "success-format")]
    [DataRow(false, "invalid-format")]
    [DataRow(true, "invalid-format")]
    public async Task Failure_PropagatesSameExceptionAndDisposesOnlyAcquiredRuntime(bool listModels, string stage)
    {
        var failure = new InvalidOperationException(stage);
        var runtime = new FakeRuntime([]);
        if (stage == "probe")
        {
            runtime.ProbeAction = _ => Task.FromException<ModelProviderProbeResult>(failure);
        }
        else if (stage == "probe-sync")
        {
            runtime.ProbeAction = _ => throw failure;
        }
        var calls = 0;
        bool Create(CodeAltaProviderDocument definition, string root, ModelsDevCatalogService? catalog, out IModelProviderRuntime created)
        {
            calls++;
            if (stage == "factory")
            {
                throw failure;
            }
            created = stage == "invalid-format" ? null! : runtime;
            return stage != "invalid-format";
        }

        var actual = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => InvokeAsync(
            listModels, CreateDefinition(), Create, () => throw failure,
            _ => stage == "success-format" ? throw failure : "success", CancellationToken.None));

        Assert.AreSame(failure, actual);
        Assert.AreEqual(1, calls);
        var acquired = stage is "probe" or "probe-sync" or "success-format";
        Assert.AreEqual(acquired ? 1 : 0, runtime.ProbeCount);
        Assert.AreEqual(acquired ? 1 : 0, runtime.DisposeCount);
    }

    [TestMethod]
    public async Task SortingFailure_PropagatesAndDisposesRuntime()
    {
        var failure = new InvalidOperationException("enumeration failed");
        var runtime = new FakeRuntime([]) { Models = new ThrowingEnumerationModels(failure) };
        var definition = CreateDefinition();
        definition.SortModels = true;
        var actual = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => InvokeWithFakeAsync(true, definition, runtime));
        Assert.AreSame(failure, actual);
        Assert.AreEqual(1, runtime.DisposeCount);
    }

    [TestMethod]
    [DataRow(false, "success", false)]
    [DataRow(true, "success", false)]
    [DataRow(false, "probe", false)]
    [DataRow(true, "probe", false)]
    [DataRow(false, "format", false)]
    [DataRow(true, "format", false)]
    [DataRow(false, "cancel", false)]
    [DataRow(true, "cancel", false)]
    [DataRow(false, "success", true)]
    [DataRow(true, "success", true)]
    [DataRow(false, "probe", true)]
    [DataRow(true, "probe", true)]
    [DataRow(false, "format", true)]
    [DataRow(true, "format", true)]
    [DataRow(false, "cancel", true)]
    [DataRow(true, "cancel", true)]
    public async Task DisposalFailure_OverridesSuccessProbeFormattingAndCancellation(bool listModels, string pending, bool faultedValueTask)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var disposalFailure = new InvalidOperationException("disposal failed");
        var pendingFailure = new ArgumentException("pending failure");
        var runtime = new FakeRuntime([])
        {
            DisposeAction = () => faultedValueTask ? ValueTask.FromException(disposalFailure) : throw disposalFailure,
        };
        runtime.ProbeAction = token => pending switch
        {
            "probe" => Task.FromException<ModelProviderProbeResult>(pendingFailure),
            "cancel" => Task.FromCanceled<ModelProviderProbeResult>(token),
            _ => Task.FromResult(runtime.CreateProbeResult()),
        };

        var actual = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => InvokeWithFakeAsync(
            listModels, CreateDefinition(), runtime, cancellation.Token,
            _ => pending == "format" ? throw pendingFailure : "success"));

        Assert.AreSame(disposalFailure, actual);
        Assert.AreEqual(1, runtime.ProbeCount);
        Assert.AreEqual(1, runtime.DisposeCount);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PendingProbe_IsAwaitedBeforeFormattingAndDisposal(bool listModels)
    {
        var events = new List<string>();
        var releaseProbe = new TaskCompletionSource<ModelProviderProbeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new FakeRuntime(events) { ProbeAction = _ => releaseProbe.Task };
        var operation = InvokeWithFakeAsync(listModels, CreateDefinition(), runtime, CancellationToken.None,
            _ => { events.Add("format"); return "success"; });
        try
        {
            Assert.IsFalse(operation.IsCompleted);
            Assert.AreEqual(1, runtime.ProbeCount);
            Assert.AreEqual(0, runtime.DisposeCount);
            CollectionAssert.AreEqual(new[] { "probe" }, events);
        }
        finally
        {
            releaseProbe.TrySetResult(runtime.CreateProbeResult());
            await operation.WaitAsync(TimeSpan.FromSeconds(5));
        }
        CollectionAssert.AreEqual(new[] { "probe", "format", "dispose" }, events);
        Assert.AreEqual(1, runtime.DisposeCount);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AsynchronousDisposal_IsAwaitedAfterSuccessFormattingBeforeReturn(bool listModels)
    {
        var events = new List<string>();
        var releaseDisposal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new FakeRuntime(events) { DisposeAction = () => new ValueTask(releaseDisposal.Task) };
        var operation = InvokeWithFakeAsync(listModels, CreateDefinition(), runtime, CancellationToken.None,
            _ => { events.Add("format"); return "success"; });
        try
        {
            Assert.IsFalse(operation.IsCompleted);
            Assert.AreEqual(1, runtime.DisposeCount);
            CollectionAssert.AreEqual(new[] { "probe", "format", "dispose" }, events);
        }
        finally
        {
            releaseDisposal.TrySetResult();
            await operation.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.AreEqual(1, runtime.DisposeCount);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DisposalMutation_PreservesAlreadyFormattedCountAndBorrowedListSemantics(bool listModels)
    {
        var models = new List<AgentModelInfo> { new("one"), new("two") };
        var runtime = new FakeRuntime([])
        {
            Models = models,
            DisposeAction = () => { models.Clear(); return ValueTask.CompletedTask; },
        };

        var result = await InvokeWithFakeAsync(listModels, CreateDefinition(), runtime);

        Assert.AreEqual("found 2", result.Message);
        Assert.AreEqual(listModels ? 0 : 2, result.ModelCount);
        if (listModels)
        {
            Assert.AreSame(models, result.Models);
        }
        Assert.AreEqual(1, runtime.DisposeCount);
    }

    private static CodeAltaProviderDocument CreateDefinition()
        => new() { ProviderKey = "fixture", ProviderType = "fixture-only", ApiKey = "literal-fake-key" };

    private static ModelProviderState CreateState(ModelProviderAvailability availability)
    {
        var state = new ModelProviderState(new ModelProviderId("fixture"), "Fixture")
        {
            Availability = availability,
            StatusMessage = "Existing state message, unchanged.",
        };
        state.Models.Add(new AgentModelInfo("z-model"));
        state.Models.Add(new AgentModelInfo("a-model"));
        return state;
    }

    private static Task<InspectionResult> InvokeWithFakeAsync(
        bool listModels, CodeAltaProviderDocument definition, FakeRuntime runtime,
        CancellationToken cancellationToken = default, Func<int, string>? formatSuccess = null)
    {
        bool Create(CodeAltaProviderDocument document, string root, ModelsDevCatalogService? catalog, out IModelProviderRuntime created)
        {
            created = runtime;
            return true;
        }

        return InvokeAsync(listModels, definition, Create,
            () => throw new AssertFailedException("An explicitly supplied fake runtime must be accepted."),
            formatSuccess ?? (count => $"found {count}"), cancellationToken);
    }

    private static async Task<InspectionResult> InvokeAsync(
        bool listModels, CodeAltaProviderDocument definition,
        ProviderFrontendCoordinator.TryCreateProviderRuntime factory,
        Func<string> formatInvalidSettings, Func<int, string> formatSuccess, CancellationToken cancellationToken)
    {
        if (listModels)
        {
            var result = await ProviderFrontendCoordinator.ListProviderModelsCoreAsync(
                definition, StateRootPath, null, factory, formatInvalidSettings, formatSuccess, cancellationToken);
            return new(result.Success, result.Message, result.ModelCount, result.Models);
        }

        var test = await ProviderFrontendCoordinator.TestProviderCoreAsync(
            definition, StateRootPath, null, factory, formatInvalidSettings, formatSuccess, cancellationToken);
        return new(test.Success, test.Message, test.ModelCount, null);
    }

    private readonly record struct InspectionResult(bool Success, string Message, int ModelCount, IReadOnlyList<AgentModelInfo>? Models);

    private sealed class FakeRuntime(List<string> events) : IModelProviderRuntime
    {
        public ModelProviderDescriptor Descriptor { get; } = new(new ModelProviderId("fixture"), "Fixture");
        public IReadOnlyList<AgentModelInfo> Models { get; init; } = [];
        public ModelProviderAvailability Availability { get; init; } = ModelProviderAvailability.Ready;
        public Func<CancellationToken, Task<ModelProviderProbeResult>>? ProbeAction { get; set; }
        public Func<ValueTask>? DisposeAction { get; init; }
        public int ProbeCount { get; private set; }
        public int DisposeCount { get; private set; }
        public CancellationToken ReceivedToken { get; private set; }

        public ModelProviderProbeResult CreateProbeResult()
            => new()
            {
                ProviderId = Descriptor.ProviderId,
                Availability = Availability,
                Models = Models,
                StatusMessage = "Probe diagnostic must not replace workflow success.",
                ErrorCategory = "fixture-error-category",
            };

        public Task<ModelProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
        {
            events.Add("probe");
            ProbeCount++;
            ReceivedToken = cancellationToken;
            return ProbeAction?.Invoke(cancellationToken) ?? Task.FromResult(CreateProbeResult());
        }

        public ValueTask DisposeAsync()
        {
            events.Add("dispose");
            DisposeCount++;
            return DisposeAction?.Invoke() ?? ValueTask.CompletedTask;
        }

        public Task StartAsync(CancellationToken cancellationToken = default) => throw new AssertFailedException("Start is forbidden.");
        public Task StopAsync(CancellationToken cancellationToken = default) => throw new AssertFailedException("Stop is forbidden.");
        public IModelProviderTurnExecutor CreateTurnExecutor() => throw new AssertFailedException("Turns are forbidden.");
    }

    private sealed class ThrowingEnumerationModels(Exception failure) : IReadOnlyList<AgentModelInfo>
    {
        public int Count => 2;
        public AgentModelInfo this[int index] => throw failure;
        public IEnumerator<AgentModelInfo> GetEnumerator() => throw failure;
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
