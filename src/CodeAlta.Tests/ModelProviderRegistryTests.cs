using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Tests;

[TestClass]
public sealed class ModelProviderRegistryTests
{
    [TestMethod]
    public void ModelProviderDescriptor_NormalizesProviderFields()
    {
        var descriptor = new ModelProviderDescriptor(new ModelProviderId("  openai-main  "), "  OpenAI Main  ", "  openai-chat  ")
        {
            BaseUri = new Uri("https://api.example.test/v1"),
            IsDefault = true,
            DefaultModelId = "gpt-test",
        };

        Assert.AreEqual("openai-main", descriptor.ProviderId.Value);
        Assert.AreEqual("OpenAI Main", descriptor.DisplayName);
        Assert.AreEqual("openai-chat", descriptor.ProviderType);
        Assert.AreEqual("openai-main", descriptor.ProviderId.Value);
        Assert.AreEqual("gpt-test", descriptor.DefaultModelId);
        Assert.IsTrue(descriptor.IsDefault);
    }

    [TestMethod]
    public void ModelProviderDescriptor_UsesProviderIdFallbacks()
    {
        var descriptor = new ModelProviderDescriptor(new ModelProviderId("provider-a"), " ", null);

        Assert.AreEqual("provider-a", descriptor.DisplayName);
        Assert.AreEqual("provider-a", descriptor.ProviderType);
    }

    [TestMethod]
    public async Task Registry_LooksUpProvidersCaseInsensitivelyAndReusesRuntimeAsync()
    {
        await using var registry = new ModelProviderRegistry();
        var descriptor = new ModelProviderDescriptor(new ModelProviderId("OpenAI-Main"), "OpenAI", "openai-chat");
        var createCount = 0;
        registry.RegisterOrReplace(descriptor, () =>
        {
            createCount++;
            return new TestModelProviderRuntime(descriptor);
        });

        Assert.IsTrue(registry.TryGetProvider(new ModelProviderId("openai-main"), out var found));
        Assert.AreSame(descriptor, found);
        CollectionAssert.AreEqual(new[] { descriptor }, registry.ListProviders().ToArray());

        var first = await registry.GetOrCreateRuntimeAsync(new ModelProviderId("openai-main"));
        var second = await registry.GetOrCreateRuntimeAsync(new ModelProviderId("OPENAI-MAIN"));

        Assert.AreSame(first, second);
        Assert.AreEqual(1, createCount);
        Assert.AreEqual("OpenAI-Main", first.Descriptor.ProviderId.Value);
    }

    [TestMethod]
    public void Registry_FiltersDisabledProvidersByDefault()
    {
        var registry = new ModelProviderRegistry();
        var enabled = new ModelProviderDescriptor(new ModelProviderId("enabled"), "Enabled", "test");
        var disabled = new ModelProviderDescriptor(new ModelProviderId("disabled"), "Disabled", "test")
        {
            IsEnabled = false,
        };
        registry.RegisterOrReplace(enabled, () => new TestModelProviderRuntime(enabled));
        registry.RegisterOrReplace(disabled, () => new TestModelProviderRuntime(disabled));

        CollectionAssert.AreEqual(new[] { enabled }, registry.ListProviders().ToArray());
        CollectionAssert.AreEqual(new[] { disabled, enabled }, registry.ListProviders(includeDisabled: true).ToArray());
    }

    private sealed class TestModelProviderRuntime(ModelProviderDescriptor descriptor) : IModelProviderRuntime
    {
        public ModelProviderDescriptor Descriptor { get; } = descriptor;

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<ModelProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ModelProviderProbeResult
            {
                ProviderId = Descriptor.ProviderId,
            });

        public IModelProviderTurnExecutor CreateTurnExecutor() => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
