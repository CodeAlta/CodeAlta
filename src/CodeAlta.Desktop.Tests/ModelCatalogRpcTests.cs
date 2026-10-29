using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Desktop.Rpc;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class ModelCatalogRpcTests
{
    [TestMethod]
    public async Task StartupInitializesProvidersOnceWithoutInventoryReadsLaunchingWork()
    {
        await using var registry = new ModelProviderRegistry();
        var probes = 0;
        var descriptor = new ModelProviderDescriptor(new("ready"), "Ready");
        registry.RegisterOrReplace(descriptor, () => new LiteralRuntime(descriptor, () => { probes++; return [new AgentModelInfo("model")]; }));
        var initialization = new ModelProviderInitializationService(registry);
        var service = new ModelCatalogService(registry, initialization, "epoch");
        Assert.AreEqual("Unknown", service.Providers(new("epoch")).Providers[0].Availability);
        Assert.AreEqual(0, probes);
        await service.StartInitialization();
        await service.StartInitialization();
        Assert.AreEqual(1, probes);
        Assert.AreEqual("Ready", service.Providers(new("epoch")).Providers[0].Availability);
        await service.DrainAsync();
        Assert.AreEqual("closed", service.Providers(new("epoch")).Status);
    }

    [TestMethod]
    public async Task ExplicitProviderReadProjectsOnlyReportedModelsAndKnownMetadata()
    {
        await using var registry = new ModelProviderRegistry();
        var id = new ModelProviderId("literal-provider");
        var descriptor = new ModelProviderDescriptor(id, new string('P', 300)) { DefaultModelId = "not-a-reported-model" };
        var model = new AgentModelInfo("real-model", "Real model", new string('D', 1200), DefaultReasoningEffort: AgentReasoningEffort.Medium,
            SupportedReasoningEfforts: [AgentReasoningEffort.Low, AgentReasoningEffort.Medium],
            Capabilities: new Dictionary<string, object?> { ["contextWindow"] = 128_000L, ["inputTokenLimit"] = JsonDocument.Parse("64000").RootElement.Clone(),
                ["outputTokenLimit"] = -1, ["supportsToolCall"] = true, ["supportsStructuredOutput"] = false,
                ["inputModalities"] = new[] { "text", "image" },
                [AgentReasoningEffortCapability.CompleteListKey] = JsonDocument.Parse("true").RootElement.Clone() });
        var probes = 0;
        registry.RegisterOrReplace(descriptor, () => new LiteralRuntime(descriptor, () => { probes++; return [model]; }));
        var service = new ModelCatalogService(registry, new ModelProviderInitializationService(registry), "epoch-1");
        var providers = service.Providers(new("epoch-1"));
        Assert.AreEqual("ok", providers.Status);
        Assert.HasCount(1, providers.Providers);
        Assert.AreEqual("Unknown", providers.Providers[0].Availability);
        Assert.AreEqual(256, providers.Providers[0].Name.Length);
        Assert.AreEqual(0, probes, "Provider list must not start a provider or infer a default model.");
        Assert.AreEqual("stale_epoch", service.Providers(new("other")).Status);
        Assert.AreEqual("stale_epoch", (await service.Models(new("other", id.Value), CancellationToken.None)).Status);
        Assert.AreEqual("invalid_request", (await service.Models(new("epoch-1", " bad "), CancellationToken.None)).Status);
        Assert.AreEqual("not_found", (await service.Models(new("epoch-1", "missing"), CancellationToken.None)).Status);
        Assert.AreEqual(0, probes);
        var result = await service.Models(new("epoch-1", id.Value), CancellationToken.None);
        Assert.AreEqual("ok", result.Status);
        Assert.AreEqual("epoch-1", result.Epoch);
        Assert.HasCount(1, result.Models);
        Assert.AreEqual(1, probes);
        var actual = result.Models[0];
        Assert.AreEqual("real-model", actual.Id);
        Assert.AreEqual(1024, actual.Description!.Length);
        Assert.AreEqual(128_000, actual.ContextTokens);
        Assert.AreEqual(64_000, actual.InputTokens);
        Assert.IsNull(actual.OutputTokens, "Invalid capabilities are unknown, not fabricated limits.");
        Assert.AreEqual(true, actual.Reasoning);
        Assert.AreEqual(true, actual.Tools);
        Assert.AreEqual(false, actual.StructuredOutput);
        Assert.AreEqual(true, actual.ImageInput);
        CollectionAssert.AreEqual(new[] { "Low", "Medium" }, actual.Efforts.ToArray());
        Assert.AreEqual("Medium", actual.DefaultEffort);
        // The provider says these efforts are all the model has: the page shows no other one as possible.
        Assert.AreEqual(true, actual.EffortsComplete);
        Assert.IsNull(actual.Deprecated, "A model with no status is not marked.");
    }

    [TestMethod]
    public async Task ContextChoices_ProjectVerifiedSizesAndOnlyMatchingPreferences()
    {
        await using var registry = new ModelProviderRegistry();
        var descriptor = new ModelProviderDescriptor(new("fixture"), "Fixture")
        {
            ContextPreferences = new Dictionary<string, long>(StringComparer.Ordinal)
            {
                ["model"] = 1000000, ["unknown"] = 1000000, ["managed-preferred"] = 1000000,
            },
        };
        AgentModelInfo[] models =
        [
            new("model") { ContextSizes = new(200000, [200000, 1000000]) },
            new("Model") { ContextSizes = new(200000, [200000, 1000000]) },
            new("unknown", Capabilities: new Dictionary<string, object?> { ["contextWindow"] = 200000L }),
            new("managed", Capabilities: new Dictionary<string, object?> { ["contextWindow"] = 999999L })
                { ContextSizes = new(null, [200000, 1000000], IsProviderManaged: true) },
            new("managed-preferred") { ContextSizes = new(null, [200000, 1000000], IsProviderManaged: true) },
        ];
        registry.RegisterOrReplace(descriptor, () => new LiteralRuntime(descriptor, () => models));
        var service = new ModelCatalogService(registry, new ModelProviderInitializationService(registry), "epoch");

        var response = await service.Models(new("epoch", "fixture"), CancellationToken.None);

        CollectionAssert.AreEqual(new decimal[] { 200000, 1000000 }, response.Models.Single(model => model.Id == "model").ContextSizes!.ToArray());
        Assert.AreEqual(1000000m, response.Models.Single(model => model.Id == "model").DefaultContextTokens);
        Assert.AreEqual(200000m, response.Models.Single(model => model.Id == "model").AdvertisedContextTokens,
            "Clearing the provider preference must show the advertised default, not the preference being cleared.");
        Assert.AreEqual(200000m, response.Models.Single(model => model.Id == "Model").DefaultContextTokens);
        Assert.AreEqual(200000m, response.Models.Single(model => model.Id == "unknown").DefaultContextTokens);
        Assert.IsNull(response.Models.Single(model => model.Id == "unknown").ContextSizes);
        var managed = response.Models.Single(model => model.Id == "managed");
        CollectionAssert.AreEqual(new decimal[] { 200000, 1000000 }, managed.ContextSizes!.ToArray());
        Assert.IsNull(managed.AdvertisedContextTokens, "Catalog metadata cannot invent an external agent's default.");
        Assert.IsNull(managed.DefaultContextTokens);
        var preferred = response.Models.Single(model => model.Id == "managed-preferred");
        Assert.IsNull(preferred.AdvertisedContextTokens);
        Assert.AreEqual(1000000m, preferred.DefaultContextTokens, "Chat Default follows a verified preference even when the CLI default is unknown.");
    }

    [TestMethod]
    public async Task DraftContextWindows_KeepCapacitiesSeparateFromInternalBudgets()
    {
        await using var registry = new ModelProviderRegistry();
        var descriptor = new ModelProviderDescriptor(new("fixture"), "Fixture")
        {
            ContextPreferences = new Dictionary<string, long>
            {
                ["preferred"] = 922000, ["invalid-preference"] = 500000, ["managed"] = 1000000,
            },
        };
        var copilot = new AgentModelInfo("copilot", Capabilities: new Dictionary<string, object?>
        {
            ["contextWindow"] = 1050000L, ["inputTokenLimit"] = 922000L, ["outputTokenLimit"] = 128000L,
        }) { ContextSizes = new(272000, [272000, 922000]) { IsInputBudget = true } };
        AgentModelInfo[] models =
        [
            copilot, copilot with { Id = "preferred" }, copilot with { Id = "invalid-preference" },
            new("codex", Capabilities: new Dictionary<string, object?> { ["effectiveContextWindowPercent"] = 95L })
                { ContextSizes = new(272000, [272000, 1050000]) },
            new("reserved", Capabilities: new Dictionary<string, object?> { ["outputTokenLimit"] = 64000L })
                { ContextSizes = new(200000, [200000, 1000000]) },
            new("unpaired", Capabilities: new Dictionary<string, object?> { ["contextWindow"] = 200000L, ["outputTokenLimit"] = 64000L }),
            new("legacy", Capabilities: new Dictionary<string, object?> { ["contextWindow"] = 200000L, ["inputTokenLimit"] = 180000L }),
            new("managed", Capabilities: new Dictionary<string, object?> { ["contextWindow"] = 1000000L })
                { ContextSizes = new(null, [200000, 1000000], IsProviderManaged: true) },
            new("unknown"),
        ];
        registry.RegisterOrReplace(descriptor, () => new LiteralRuntime(descriptor, () => models));
        var service = new ModelCatalogService(registry, new ModelProviderInitializationService(registry), "epoch");

        var response = await service.Models(new("epoch", "fixture"), CancellationToken.None);
        var standard = response.Models.Single(model => model.Id == "copilot");
        Assert.AreEqual(1050000, standard.ContextTokens, "The catalog envelope must not replace the provider's selectable capacities.");
        Assert.AreEqual(272000m, standard.DefaultContextTokens);
        CollectionAssert.AreEqual(new decimal[] { 272000, 922000 }, standard.ContextSizes!.ToArray());
        Assert.AreEqual(922000m, response.Models.Single(model => model.Id == "preferred").DefaultContextTokens);
        Assert.AreEqual(272000m, response.Models.Single(model => model.Id == "invalid-preference").DefaultContextTokens);
        var codex = response.Models.Single(model => model.Id == "codex");
        Assert.AreEqual(272000m, codex.DefaultContextTokens);
        CollectionAssert.AreEqual(new decimal[] { 272000, 1050000 }, codex.ContextSizes!.ToArray());
        var reserved = response.Models.Single(model => model.Id == "reserved");
        Assert.AreEqual(200000m, reserved.DefaultContextTokens);
        CollectionAssert.AreEqual(new decimal[] { 200000, 1000000 }, reserved.ContextSizes!.ToArray());
        Assert.AreEqual(200000m, response.Models.Single(model => model.Id == "unpaired").DefaultContextTokens);
        Assert.AreEqual(200000m, response.Models.Single(model => model.Id == "legacy").DefaultContextTokens);
        var managed = response.Models.Single(model => model.Id == "managed");
        Assert.AreEqual(1000000m, managed.DefaultContextTokens, "A verified preference is displayed without a local output reservation.");
        Assert.IsNull(response.Models.Single(model => model.Id == "unknown").DefaultContextTokens);
    }

    [TestMethod]
    public async Task ADeprecatedModelIsMarked_WithTheDateItRetires()
    {
        await using var registry = new ModelProviderRegistry();
        var id = new ModelProviderId("anthropic");
        var descriptor = new ModelProviderDescriptor(id, "Anthropic");
        var retiresAt = DateTimeOffset.Parse("2027-03-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var models = new[]
        {
            new AgentModelInfo("old-model", Capabilities: new Dictionary<string, object?> { ["status"] = "deprecated", ["retiresAt"] = retiresAt }),
            // models.dev writes the status as text, and a saved list gives it back as JSON.
            new AgentModelInfo("listed-model", Capabilities: new Dictionary<string, object?> { ["status"] = JsonDocument.Parse("\"deprecated\"").RootElement.Clone() }),
            new AgentModelInfo("beta-model", Capabilities: new Dictionary<string, object?> { ["status"] = "beta" }),
        };
        registry.RegisterOrReplace(descriptor, () => new LiteralRuntime(descriptor, () => models));
        var service = new ModelCatalogService(registry, new ModelProviderInitializationService(registry), "epoch");

        var result = await service.Models(new("epoch", id.Value), CancellationToken.None);

        Assert.AreEqual(true, result.Models[0].Deprecated);
        Assert.AreEqual(retiresAt, result.Models[0].RetiresAt);
        Assert.AreEqual(true, result.Models[1].Deprecated);
        Assert.IsNull(result.Models[1].RetiresAt);
        Assert.IsNull(result.Models[2].Deprecated);
    }

    [TestMethod]
    public async Task DisabledFailedAndUnconfiguredRemainExplicitWithoutDefaultInventory()
    {
        await using var registry = new ModelProviderRegistry();
        var disabled = new ModelProviderDescriptor(new("disabled"), "Disabled") { IsEnabled = false, DefaultModelId = "placeholder" };
        registry.RegisterOrReplace(disabled, () => throw new AssertFailedException("Disabled provider must never start."));
        var failed = new ModelProviderDescriptor(new("failure"), "Failure");
        registry.RegisterOrReplace(failed, () => new LiteralRuntime(failed, () => throw new InvalidOperationException("private provider diagnostic")));
        var initialization = new ModelProviderInitializationService(registry);
        var service = new ModelCatalogService(registry, initialization, "epoch");
        var off = await service.Models(new("epoch", "disabled"), CancellationToken.None);
        Assert.AreEqual("unavailable", off.Status);
        Assert.AreEqual("Disabled", off.Availability);
        Assert.IsEmpty(off.Models);
        var testedOff = await service.Probe(new("epoch", "disabled"), CancellationToken.None);
        Assert.AreEqual("ok", testedOff.Status);
        Assert.AreEqual("Disabled", testedOff.Availability);
        var error = await service.Models(new("epoch", "failure"), CancellationToken.None);
        Assert.AreEqual("unavailable", error.Status);
        Assert.AreEqual("Failed", error.Availability);
        Assert.IsEmpty(error.Models);
        Assert.IsFalse(JsonSerializer.Serialize(error).Contains("private provider diagnostic", StringComparison.Ordinal));
        var catalogOnly = new ModelCatalogService();
        Assert.AreEqual("unconfigured", catalogOnly.Providers(new("epoch")).Status);
        Assert.AreEqual("unconfigured", (await catalogOnly.Models(new("epoch", "failure"), CancellationToken.None)).Status);
    }

    [TestMethod]
    public async Task BoundProviderAndModelWindowsReportOmissions()
    {
        await using var registry = new ModelProviderRegistry();
        var models = Enumerable.Range(0, ModelCatalogService.MaximumModels + 7).Select(index => new AgentModelInfo($"model-{index:D4}")).ToArray();
        for (var index = 0; index < 33; index++)
        {
            var descriptor = new ModelProviderDescriptor(new($"provider-{index:D2}"), $"Provider {index:D2}");
            registry.RegisterOrReplace(descriptor, () => new LiteralRuntime(descriptor, () => models));
        }
        var service = new ModelCatalogService(registry, new ModelProviderInitializationService(registry), "epoch");
        var providers = service.Providers(new("epoch"));
        Assert.HasCount(32, providers.Providers);
        Assert.IsTrue(providers.Truncated);
        var result = await service.Models(new("epoch", "provider-00"), CancellationToken.None);
        Assert.HasCount(ModelCatalogService.MaximumModels, result.Models);
        Assert.IsTrue(result.Truncated);
        Assert.AreEqual($"model-{ModelCatalogService.MaximumModels - 1:D4}", result.Models[^1].Id);
    }

    [TestMethod]
    public async Task LargeProviderInventoryIsSentWhole()
    {
        // Azure OpenAI lists 475 models in one page; the ones after the 128th were once left out of every list.
        await using var registry = new ModelProviderRegistry();
        var descriptor = new ModelProviderDescriptor(new("azure-openai"), "Azure OpenAI", "openai-responses");
        var models = Enumerable.Range(0, 475).Select(index => new AgentModelInfo($"model-{index:D3}", $"Model {index}",
            "A model of a large inventory.", SupportedReasoningEfforts: [AgentReasoningEffort.Low, AgentReasoningEffort.High],
            Capabilities: new Dictionary<string, object?> { ["contextWindow"] = 400_000L, ["supportsToolCall"] = true })).ToArray();
        registry.RegisterOrReplace(descriptor, () => new LiteralRuntime(descriptor, () => models));
        var service = new ModelCatalogService(registry, new ModelProviderInitializationService(registry), "epoch");
        var result = await service.Models(new("epoch", descriptor.ProviderId.Value), CancellationToken.None);
        Assert.AreEqual("ok", result.Status);
        Assert.IsFalse(result.Truncated);
        CollectionAssert.AreEqual(models.Select(static model => model.Id).ToArray(), result.Models.Select(static model => model.Id).ToArray());
        Assert.AreEqual(400_000, result.Models[424].ContextTokens);
    }

    [TestMethod]
    public async Task EscapedWorstCaseModelPayloadIsTruncatedBeforeTheBridgeLimit()
    {
        await using var registry = new ModelProviderRegistry();
        var descriptor = new ModelProviderDescriptor(new("literal-provider"), "Literal provider");
        var models = Enumerable.Range(0, ModelCatalogService.MaximumModels).Select(index => new AgentModelInfo($"model-{index:D4}",
            new string('\u0001', 256), new string('\u0001', 1024))).ToArray();
        registry.RegisterOrReplace(descriptor, () => new LiteralRuntime(descriptor, () => models));
        var service = new ModelCatalogService(registry, new ModelProviderInitializationService(registry), "epoch");
        var result = await service.Models(new("epoch", descriptor.ProviderId.Value), CancellationToken.None);
        Assert.AreEqual("ok", result.Status);
        Assert.IsTrue(result.Truncated);
        Assert.IsTrue(result.Models.Count > 0 && result.Models.Count < ModelCatalogService.MaximumModels);
        var wireBytes = JsonSerializer.SerializeToUtf8Bytes(result, DesktopJsonContext.Default.ModelCatalogModelsResponse).Length;
        Assert.IsTrue(wireBytes <= ModelCatalogService.MaximumModelsResponseBytes, $"Model response exceeded its bound: {wireBytes} bytes.");
        Assert.AreEqual($"model-{result.Models.Count - 1:D4}", result.Models[^1].Id);
    }

    [TestMethod]
    public async Task ExplicitProbeRetainsWorkAcrossCallerCancellationAndHostDrain()
    {
        await using var registry = new ModelProviderRegistry();
        var descriptor = new ModelProviderDescriptor(new("selected"), "Selected", "literal-type")
        { IsDefault = true, DefaultModelId = "configured-not-inventory" };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        registry.RegisterOrReplace(descriptor, () => new GatedRuntime(descriptor, entered, release, () => Interlocked.Increment(ref count)));
        var other = new ModelProviderDescriptor(new("other"), "Other");
        registry.RegisterOrReplace(other, () => throw new AssertFailedException("Do not probe other providers."));
        var initialization = new ModelProviderInitializationService(registry);
        var service = new ModelCatalogService(registry, initialization, "epoch");
        var list = service.Providers(new("epoch"));
        Assert.HasCount(2, list.Providers);
        var selected = list.Providers.Single(value => value.Id == "selected");
        Assert.AreEqual("Unknown", selected.Availability);
        Assert.AreEqual("literal-type", selected.Type);
        Assert.AreEqual("configured-not-inventory", selected.DefaultModel);
        Assert.IsTrue(selected.IsDefault);
        Assert.IsNull(selected.ObservedAt);
        Assert.AreEqual(0, count);
        Assert.AreEqual("stale_epoch", (await service.Probe(new("stale", "selected"), default)).Status);
        Assert.AreEqual("not_found", (await service.Probe(new("epoch", "missing"), default)).Status);
        Assert.AreEqual(0, count);
        using var canceled = new CancellationTokenSource();
        var probe = service.Probe(new("epoch", "selected"), canceled.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual("busy", (await service.Probe(new("epoch", "other"), default)).Status);
            canceled.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await probe);
            service.CloseAdmission();
            Assert.AreEqual("closed", (await service.Probe(new("epoch", "selected"), default)).Status);
            var drain = service.DrainAsync();
            Assert.IsFalse(drain.IsCompleted);
            release.TrySetResult();
            await drain.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(1, count);
            Assert.AreEqual("closed", service.Providers(new("epoch")).Status);
            Assert.AreEqual(ModelProviderAvailability.Ready, initialization.CurrentStates.Single(value => value.ProviderId == descriptor.ProviderId).Availability);
        }
        finally { release.TrySetResult(); await service.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [TestMethod]
    public async Task FailedProbeDoesNotDiscloseProviderDiagnostics()
    {
        await using var registry = new ModelProviderRegistry();
        var descriptor = new ModelProviderDescriptor(new("failure"), "Failure");
        registry.RegisterOrReplace(descriptor, () => new LiteralRuntime(descriptor, () =>
            throw new InvalidOperationException("https://private.example/token?secret=never-expose")));
        var service = new ModelCatalogService(registry, new ModelProviderInitializationService(registry), "epoch");
        var result = await service.Probe(new("epoch", "failure"), default);
        Assert.AreEqual("ok", result.Status);
        Assert.AreEqual("Failed", result.Availability);
        Assert.IsNull(result.Reason, "A failure the page has no text for gives no reason.");
        Assert.IsFalse(JsonSerializer.Serialize(result).Contains("private.example", StringComparison.Ordinal));
        Assert.IsFalse(JsonSerializer.Serialize(service.Providers(new("epoch"))).Contains("private.example", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task FailedProbeNamesAKnownReasonWithoutItsMessage()
    {
        await using var registry = new ModelProviderRegistry();
        var signedOut = new ModelProviderDescriptor(new("signed-out"), "Signed out");
        registry.RegisterOrReplace(signedOut, () => new ResultRuntime(signedOut, new ModelProviderProbeResult
        {
            ProviderId = signedOut.ProviderId, Availability = ModelProviderAvailability.Failed,
            StatusMessage = "private provider diagnostic", ErrorCategory = "claude-code-signed-out",
        }));
        var other = new ModelProviderDescriptor(new("other"), "Other");
        registry.RegisterOrReplace(other, () => new ResultRuntime(other, new ModelProviderProbeResult
        {
            ProviderId = other.ProviderId, Availability = ModelProviderAvailability.Failed,
            StatusMessage = "private provider diagnostic", ErrorCategory = "https://private.example/unknown-category",
        }));
        var ready = new ModelProviderDescriptor(new("ready"), "Ready");
        registry.RegisterOrReplace(ready, () => new ResultRuntime(ready, new ModelProviderProbeResult
        {
            ProviderId = ready.ProviderId, Availability = ModelProviderAvailability.Ready, ErrorCategory = "claude-code-signed-out",
        }));
        var service = new ModelCatalogService(registry, new ModelProviderInitializationService(registry), "epoch");

        var result = await service.Probe(new("epoch", "signed-out"), default);
        Assert.AreEqual("Failed", result.Availability);
        Assert.AreEqual("claude-code-signed-out", result.Reason);
        Assert.IsFalse(JsonSerializer.Serialize(result).Contains("private", StringComparison.Ordinal));

        var unknown = await service.Probe(new("epoch", "other"), default);
        Assert.AreEqual("Failed", unknown.Availability);
        Assert.IsNull(unknown.Reason);
        Assert.IsFalse(JsonSerializer.Serialize(unknown).Contains("private", StringComparison.Ordinal));
        Assert.IsNull((await service.Probe(new("epoch", "ready"), default)).Reason, "A provider that is ready has no reason to give.");

        // The list of the providers and the models of one say it too: a signed-out provider is shown as such, not as failed.
        var listed = service.Providers(new("epoch")).Providers.ToDictionary(static provider => provider.Id, static provider => provider.Reason);
        Assert.AreEqual("claude-code-signed-out", listed["signed-out"]);
        Assert.IsNull(listed["other"]);
        Assert.IsNull(listed["ready"]);
        var models = await service.Models(new("epoch", "signed-out"), default);
        Assert.AreEqual(("unavailable", "claude-code-signed-out"), (models.Status, models.Reason));
        Assert.IsFalse(JsonSerializer.Serialize(service.Providers(new("epoch"))).Contains("private", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ProviderProjectionBoundsConfiguredFieldsAndWireWithoutProbing()
    {
        await using var registry = new ModelProviderRegistry();
        for (var index = 0; index < 33; index++)
        {
            var descriptor = new ModelProviderDescriptor(new($"literal-{index:D2}"), new string('\u0001', 1024), new string('\u0001', 1024))
            { DefaultModelId = new string('\u0001', 1024) };
            registry.RegisterOrReplace(descriptor, () => throw new AssertFailedException("Listing must not instantiate a runtime."));
        }
        var service = new ModelCatalogService(registry, new ModelProviderInitializationService(registry), "epoch");
        var result = service.Providers(new("epoch"));
        Assert.HasCount(32, result.Providers);
        Assert.IsTrue(result.Truncated);
        Assert.IsTrue(result.Providers.All(value => value.Name.Length == 256 && value.Type.Length == 256 && value.DefaultModel!.Length == 256));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(result, DesktopJsonContext.Default.ModelCatalogProvidersResponse).Length;
        Assert.IsTrue(bytes < 256 * 1024, $"Provider projection exceeded its bounded wire: {bytes} bytes.");
        Assert.AreEqual("not_found", (await service.Probe(new("epoch", "literal-32"), default)).Status);
    }

    private sealed class GatedRuntime(ModelProviderDescriptor descriptor, TaskCompletionSource entered,
        TaskCompletionSource release, Action count) : IModelProviderSessionRuntime
    {
        public ModelProviderDescriptor Descriptor => descriptor;
        public Task StartAsync(CancellationToken token = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken token = default) => Task.CompletedTask;
        public async Task<ModelProviderProbeResult> ProbeAsync(CancellationToken token = default)
        {
            count(); entered.TrySetResult(); await release.Task;
            return new ModelProviderProbeResult { ProviderId = descriptor.ProviderId };
        }
        public IModelProviderTurnExecutor CreateTurnExecutor() => throw new AssertFailedException("No turns.");
        public Task<IAgentSession> CreateSessionAsync(AgentSessionCreateOptions options, CancellationToken token = default) => throw new AssertFailedException("No sessions.");
        public Task<IAgentSession> ResumeSessionAsync(string id, AgentSessionResumeOptions options, CancellationToken token = default) => throw new AssertFailedException("No sessions.");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ResultRuntime(ModelProviderDescriptor descriptor, ModelProviderProbeResult result) : IModelProviderSessionRuntime
    {
        public ModelProviderDescriptor Descriptor => descriptor;
        public Task StartAsync(CancellationToken token = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken token = default) => Task.CompletedTask;
        public Task<ModelProviderProbeResult> ProbeAsync(CancellationToken token = default) => Task.FromResult(result);
        public IModelProviderTurnExecutor CreateTurnExecutor() => throw new AssertFailedException("No turns.");
        public Task<IAgentSession> CreateSessionAsync(AgentSessionCreateOptions options, CancellationToken token = default) => throw new AssertFailedException("No sessions.");
        public Task<IAgentSession> ResumeSessionAsync(string id, AgentSessionResumeOptions options, CancellationToken token = default) => throw new AssertFailedException("No sessions.");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class LiteralRuntime(ModelProviderDescriptor descriptor, Func<IReadOnlyList<AgentModelInfo>> models) : IModelProviderSessionRuntime
    {
        public ModelProviderDescriptor Descriptor => descriptor;
        public Task StartAsync(CancellationToken token = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken token = default) => Task.CompletedTask;
        public Task<ModelProviderProbeResult> ProbeAsync(CancellationToken token = default)
            => Task.FromResult(new ModelProviderProbeResult { ProviderId = descriptor.ProviderId, Models = models() });
        public IModelProviderTurnExecutor CreateTurnExecutor() => throw new AssertFailedException("No turn execution in model catalog tests.");
        public Task<IAgentSession> CreateSessionAsync(AgentSessionCreateOptions options, CancellationToken token = default)
            => throw new AssertFailedException("No session creation in model catalog tests.");
        public Task<IAgentSession> ResumeSessionAsync(string sessionId, AgentSessionResumeOptions options, CancellationToken token = default)
            => throw new AssertFailedException("No session resume in model catalog tests.");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
