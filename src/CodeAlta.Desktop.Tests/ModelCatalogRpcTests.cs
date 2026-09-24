using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Desktop.Rpc;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class ModelCatalogRpcTests
{
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
                ["inputModalities"] = new[] { "text", "image" } });
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
    }

    [TestMethod]
    public async Task DisabledFailedAndUnconfiguredRemainExplicitWithoutDefaultInventory()
    {
        await using var registry = new ModelProviderRegistry();
        var disabled = new ModelProviderDescriptor(new("disabled"), "Disabled") { IsEnabled = false, DefaultModelId = "placeholder" };
        registry.RegisterOrReplace(disabled, () => throw new AssertFailedException("Disabled provider must never start."));
        var failed = new ModelProviderDescriptor(new("failure"), "Failure");
        registry.RegisterOrReplace(failed, () => new LiteralRuntime(failed, () => throw new InvalidOperationException("private provider diagnostic")));
        var service = new ModelCatalogService(registry, new ModelProviderInitializationService(registry), "epoch");
        var off = await service.Models(new("epoch", "disabled"), CancellationToken.None);
        Assert.AreEqual("unavailable", off.Status);
        Assert.AreEqual("Disabled", off.Availability);
        Assert.IsEmpty(off.Models);
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
        var models = Enumerable.Range(0, 135).Select(index => new AgentModelInfo($"model-{index:D3}")).ToArray();
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
        Assert.HasCount(128, result.Models);
        Assert.IsTrue(result.Truncated);
        Assert.AreEqual("model-127", result.Models[^1].Id);
    }

    [TestMethod]
    public async Task EscapedWorstCaseModelPayloadIsTruncatedBeforeTheBridgeLimit()
    {
        await using var registry = new ModelProviderRegistry();
        var descriptor = new ModelProviderDescriptor(new("literal-provider"), "Literal provider");
        var models = Enumerable.Range(0, 128).Select(index => new AgentModelInfo($"model-{index:D3}",
            new string('\u0001', 256), new string('\u0001', 1024))).ToArray();
        registry.RegisterOrReplace(descriptor, () => new LiteralRuntime(descriptor, () => models));
        var service = new ModelCatalogService(registry, new ModelProviderInitializationService(registry), "epoch");
        var result = await service.Models(new("epoch", descriptor.ProviderId.Value), CancellationToken.None);
        Assert.AreEqual("ok", result.Status);
        Assert.IsTrue(result.Truncated);
        Assert.IsTrue(result.Models.Count > 0 && result.Models.Count < 128);
        var wireBytes = JsonSerializer.SerializeToUtf8Bytes(result, DesktopJsonContext.Default.ModelCatalogModelsResponse).Length;
        Assert.IsTrue(wireBytes <= ModelCatalogService.MaximumModelsResponseBytes, $"Model response exceeded its bound: {wireBytes} bytes.");
        Assert.AreEqual($"model-{result.Models.Count - 1:D3}", result.Models[^1].Id);
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
