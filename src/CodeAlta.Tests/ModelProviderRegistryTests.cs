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
    public async Task Registry_VersionsEachRegistrationOfAProvider()
    {
        await using var registry = new ModelProviderRegistry();
        var descriptor = new ModelProviderDescriptor(new ModelProviderId("Claude"), "Claude", "claude-code");
        var other = new ModelProviderDescriptor(new ModelProviderId("other"), "Other", "test");
        Assert.AreEqual(0, registry.GetRegistrationVersion(new ModelProviderId("claude")), "A provider that is not registered has no version.");

        registry.RegisterOrReplace(descriptor, () => new TestModelProviderRuntime(descriptor));
        var first = registry.GetRegistrationVersion(new ModelProviderId("claude"));
        registry.RegisterOrReplace(other, () => new TestModelProviderRuntime(other));
        await registry.GetOrCreateRuntimeAsync(new ModelProviderId("CLAUDE"));

        Assert.AreNotEqual(0, first);
        Assert.AreEqual(first, registry.GetRegistrationVersion(new ModelProviderId("claude")), "Another provider, or a runtime of this one, is no new registration.");

        registry.RegisterOrReplace(descriptor, () => new TestModelProviderRuntime(descriptor));
        Assert.AreNotEqual(first, registry.GetRegistrationVersion(new ModelProviderId("claude")));

        registry.Unregister(new ModelProviderId("claude"));
        Assert.AreEqual(0, registry.GetRegistrationVersion(new ModelProviderId("claude")));
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

    [TestMethod]
    public async Task Registry_WaitingProviderIsListedButHasNoRuntime()
    {
        await using var registry = new ModelProviderRegistry();
        var descriptor = new ModelProviderDescriptor(new ModelProviderId("anthropic"), "Anthropic", "anthropic") { WaitsFor = "The provider anthropic waits: its vault is locked." };
        registry.RegisterWaiting(descriptor, static _ => Task.FromResult(false));

        Assert.IsTrue(registry.TryGetProvider(new ModelProviderId("anthropic"), out var found));
        Assert.AreSame(descriptor, found);
        var failure = await Assert.ThrowsExactlyAsync<ModelProviderWaitingException>(() => registry.CreateRuntimeAsync(new ModelProviderId("anthropic")).AsTask());
        Assert.AreEqual(descriptor.WaitsFor, failure.Message);
        await Assert.ThrowsExactlyAsync<ModelProviderWaitingException>(() => registry.GetOrCreateRuntimeAsync(new ModelProviderId("anthropic")).AsTask());
        Assert.ThrowsExactly<ArgumentException>(() => registry.RegisterWaiting(descriptor with { WaitsFor = null }, static _ => Task.FromResult(true)));
    }

    [TestMethod]
    public async Task Registry_RunOfAWaitingProviderEndsOnceItIsRegisteredAgain()
    {
        await using var registry = new ModelProviderRegistry();
        var descriptor = new ModelProviderDescriptor(new ModelProviderId("anthropic"), "Anthropic", "anthropic");
        var asked = 0;
        var unlock = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        registry.RegisterWaiting(descriptor with { WaitsFor = "locked" }, _ => { Interlocked.Increment(ref asked); return unlock.Task; });
        await registry.WaitForProviderAsync(new ModelProviderId("other"), CancellationToken.None).WithinAsync(TestTime.Seconds(5));

        var first = registry.WaitForProviderAsync(new ModelProviderId("anthropic"), CancellationToken.None);
        var second = registry.WaitForProviderAsync(new ModelProviderId("ANTHROPIC"), CancellationToken.None);
        unlock.SetResult(true);
        Assert.IsFalse(first.IsCompleted, "A run waits for the provider to be registered again, not only for the vault.");
        registry.RegisterOrReplace(descriptor, () => new TestModelProviderRuntime(descriptor));
        await Task.WhenAll(first, second).WithinAsync(TestTime.Seconds(5));

        Assert.AreEqual(2, asked);
        Assert.IsNotNull(await registry.CreateRuntimeAsync(new ModelProviderId("anthropic")));
        await registry.WaitForProviderAsync(new ModelProviderId("anthropic"), CancellationToken.None).WithinAsync(TestTime.Seconds(5));
        Assert.AreEqual(2, asked, "A provider that runs asks for nothing.");
    }

    [TestMethod]
    public async Task Registry_RunOfAWaitingProviderFailsWithWhatItWaitsFor()
    {
        await using var registry = new ModelProviderRegistry { RegistrationWaitTime = TestTime.Milliseconds(100) };
        var descriptor = new ModelProviderDescriptor(new ModelProviderId("anthropic"), "Anthropic", "anthropic") { WaitsFor = "The vault is locked." };
        var given = false;
        registry.RegisterWaiting(descriptor, _ => Task.FromResult(given));

        var refused = await Assert.ThrowsExactlyAsync<ModelProviderWaitingException>(
            () => registry.WaitForProviderAsync(new ModelProviderId("anthropic"), CancellationToken.None).WithinAsync(TestTime.Seconds(5)));
        Assert.AreEqual("The vault is locked.", refused.Message);
        Assert.AreEqual("anthropic", refused.ProviderKey);

        given = true;
        await Assert.ThrowsExactlyAsync<ModelProviderWaitingException>(
            () => registry.WaitForProviderAsync(new ModelProviderId("anthropic"), CancellationToken.None).WithinAsync(TestTime.Seconds(5)),
            "A provider the host does not register again ends the wait.");

        var removed = registry.WaitForProviderAsync(new ModelProviderId("anthropic"), CancellationToken.None);
        registry.Unregister(new ModelProviderId("anthropic"));
        await removed.WithinAsync(TestTime.Seconds(5));
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
