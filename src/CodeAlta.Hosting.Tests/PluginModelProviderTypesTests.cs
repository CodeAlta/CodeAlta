using CodeAlta.Agent;
using CodeAlta.Agent.Harness;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Hosting.Tests;

[TestClass]
public sealed class PluginModelProviderTypesTests
{
    private const string Config = """
[chat]
default_provider = "agent-one"

[providers.codex]
type = "codex"
enabled = false

[providers.agent-one]
type = "fake-agent"
display_name = "Agent One"
model = "fast"
reasoning_effort = "high"
permission_mode = "acceptEdits"
command = "agent-one"

[providers.agent-off]
type = "fake-agent"
enabled = false

[providers.agent-broken]
type = "fake-agent"
command = ""

[providers.future]
type = "future-provider"
""";

    [TestMethod]
    public async Task RegisterOrReplace_RegistersTheProvidersOfAContributedType()
    {
        using var root = new TempRoot(Config);
        await using var endpoints = new LoopbackHarnessToolEndpoints();
        var types = new PluginModelProviderTypes(endpoints);
        PluginModelProviderContext? created = null;
        var contribution = CreateContribution(context =>
        {
            created = context;
            return new FakeRuntime(context.Descriptor);
        });
        types.Attach(() => [contribution]);
        var registry = new ModelProviderRegistry();

        var registered = types.RegisterOrReplace(registry, root.Store, root.Path, includeDisabled: false);

        var descriptor = registered.Single();
        Assert.AreEqual("agent-one", descriptor.ProviderId.Value);
        Assert.AreEqual("Agent One", descriptor.DisplayName);
        Assert.AreEqual("fake-agent", descriptor.ProviderType);
        Assert.IsTrue(descriptor.IsDefault);
        Assert.AreEqual("fast", descriptor.DefaultModelId);
        Assert.AreEqual(AgentReasoningEffort.High, descriptor.DefaultReasoningEffort);
        Assert.AreEqual("acceptEdits", descriptor.DefaultPermissionMode);
        Assert.AreEqual(types.Signature, types.RegisteredSignature);
        Assert.IsNull(created, "the runtime is created at its first use");

        var runtime = await registry.GetOrCreateRuntimeAsync(descriptor.ProviderId);
        Assert.IsInstanceOfType<FakeRuntime>(runtime);
        Assert.AreEqual("agent-one", created!.Settings.GetString("command"));
        Assert.AreSame(endpoints, created.ToolEndpoints);
        Assert.AreEqual(root.Path, created.StateRootPath);

        var withDisabled = types.RegisterOrReplace(new ModelProviderRegistry(), root.Store, root.Path, includeDisabled: true);
        CollectionAssert.AreEquivalent(new[] { "agent-one", "agent-off" }, withDisabled.Select(static provider => provider.ProviderId.Value).ToArray());
        Assert.IsFalse(withDisabled.Single(static provider => provider.ProviderId.Value == "agent-off").IsEnabled);
    }

    [TestMethod]
    public async Task Builder_RegistersPluginProvidersAfterTheBuiltInOnes_AndNothingWithoutThePlugin()
    {
        using var root = new TempRoot(Config);
        await using var endpoints = new LoopbackHarnessToolEndpoints();
        var types = new PluginModelProviderTypes(endpoints);

        Assert.AreEqual(0, ConfiguredModelProviderRegistryBuilder.RegisterConfiguredProviders(new ModelProviderRegistry(), root.Store, root.Path, null, types).Count);

        var contribution = CreateContribution(static context => new FakeRuntime(context.Descriptor));
        types.Attach(() => [contribution]);
        var registered = ConfiguredModelProviderRegistryBuilder.RegisterConfiguredProviders(new ModelProviderRegistry(), root.Store, root.Path, null, types);

        CollectionAssert.AreEqual(new[] { "agent-one" }, registered.Select(static provider => provider.ProviderId.Value).ToArray());
        CollectionAssert.AreEqual(new[] { "default", "acceptEdits", "bypassPermissions" }, ConfiguredModelProviderRegistryBuilder.GetPermissionModes("FAKE-AGENT ", types).ToArray());
        Assert.AreEqual(0, ConfiguredModelProviderRegistryBuilder.GetPermissionModes("future-provider", types).Count);
    }

    [TestMethod]
    public void StoppingProviders_AreThoseOfAPluginThatStopsOrIsBuiltAgain()
    {
        using var root = new TempRoot(Config);
        var types = new PluginModelProviderTypes(new LoopbackHarnessToolEndpoints());
        var running = CreateContribution(static context => new FakeRuntime(context.Descriptor));
        types.Attach(() => [running]);
        Assert.AreEqual(0, types.GetStoppingProviders().Count, "Nothing is registered yet.");
        types.RegisterOrReplace(new ModelProviderRegistry(), root.Store, root.Path, includeDisabled: false);

        // The providers a plugin gives, the ones its sessions run with.
        CollectionAssert.AreEqual(new[] { "agent-one" }, types.GetProvidersOf([running]).ToArray());
        Assert.AreEqual(0, types.GetProvidersOf([CreateContribution(static context => new FakeRuntime(context.Descriptor))]).Count);
        Assert.AreEqual(0, types.GetStoppingProviders().Count, "The plugin runs on.");

        // Built again: another contribution of the same type replaces the runtimes of its providers.
        var rebuilt = CreateContribution(static context => new FakeRuntime(context.Descriptor));
        types.Attach(() => [rebuilt]);
        CollectionAssert.AreEqual(new[] { "agent-one" }, types.GetStoppingProviders().ToArray());
        types.RegisterOrReplace(new ModelProviderRegistry(), root.Store, root.Path, includeDisabled: false);
        Assert.AreEqual(0, types.GetStoppingProviders().Count);

        // Stopped: its providers go.
        types.Attach(static () => []);
        CollectionAssert.AreEqual(new[] { "agent-one" }, types.GetStoppingProviders().ToArray());
        types.RegisterOrReplace(new ModelProviderRegistry(), root.Store, root.Path, includeDisabled: false);
        Assert.AreEqual(0, types.GetStoppingProviders().Count);
        Assert.AreEqual(0, types.GetProvidersOf([running]).Count);
    }

    [TestMethod]
    public void Signature_ChangesWithAnotherContribution()
    {
        var types = new PluginModelProviderTypes(new LoopbackHarnessToolEndpoints());
        var first = CreateContribution(static context => new FakeRuntime(context.Descriptor));
        types.Attach(() => [first]);
        var before = types.Signature;
        types.Attach(() => [first]);
        Assert.AreEqual(before, types.Signature);
        var second = CreateContribution(static context => new FakeRuntime(context.Descriptor));
        types.Attach(() => [second]);
        Assert.AreNotEqual(before, types.Signature);
        Assert.AreSame(second, types.Find("Fake-Agent"));
        Assert.IsNull(types.Find("other"));
    }

    private static PluginModelProviderContribution CreateContribution(Func<PluginModelProviderContext, IAgentModelProviderRuntime> create)
        => new()
        {
            ProviderType = "fake-agent",
            DisplayName = "Fake agent",
            PermissionModes = ["default", "acceptEdits", "bypassPermissions"],
            Settings = [new PluginProviderSetting("command", "Command", PluginProviderSettingKind.Text)],
            Validate = static settings => settings.Values.ContainsKey("command") && settings.GetString("command") is null ? ["The command is empty."] : [],
            CreateRuntime = create,
        };

    private sealed class TempRoot : IDisposable
    {
        public TempRoot(string config)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), nameof(PluginModelProviderTypesTests), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            var options = new CatalogOptions { GlobalRoot = Path };
            File.WriteAllText(options.ConfigPath, config);
            Store = new CodeAltaConfigStore(options);
        }

        public string Path { get; }

        public CodeAltaConfigStore Store { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class FakeRuntime(ModelProviderDescriptor descriptor) : IAgentModelProviderRuntime
    {
        public ModelProviderDescriptor Descriptor { get; } = descriptor;

        public ModelProviderRuntimeDescriptor RuntimeDescriptor { get; } = new()
        {
            ProtocolFamily = "fake-agent",
            ProviderKey = descriptor.ProviderId.Value,
            DisplayName = descriptor.DisplayName,
            TransportKind = AgentTransportKind.OpenAIResponses,
        };

        public IModelProviderModelCatalog? ModelCatalog => null;

        public AgentRuntimeProviderRegistration CreateProviderRegistration() => throw new NotSupportedException();

        public IModelProviderTurnExecutor CreateTurnExecutor() => throw new NotSupportedException();

        public Task<ModelProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ModelProviderProbeResult { ProviderId = Descriptor.ProviderId });

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
