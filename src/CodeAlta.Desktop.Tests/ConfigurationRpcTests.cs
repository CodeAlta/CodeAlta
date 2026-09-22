using CodeAlta.Agent;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Plugins;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class ConfigurationRpcTests
{
    [TestMethod]
    public async Task Snapshot_ProjectsConfiguredProvidersWithoutStartingThem()
    {
        await using var providers = new ModelProviderRegistry();
        providers.RegisterOrReplace(new ModelProviderDescriptor(new ModelProviderId("zeta"), "Zeta", "openai-chat")
        {
            IsEnabled = false,
            DefaultModelId = "zeta-1",
        }, static () => throw new AssertFailedException("Snapshot must not start a provider runtime."));
        providers.RegisterOrReplace(new ModelProviderDescriptor(new ModelProviderId("alpha"), "Alpha", "anthropic")
        {
            IsDefault = true,
            DefaultReasoningEffort = AgentReasoningEffort.High,
        }, static () => throw new AssertFailedException("Snapshot must not start a provider runtime."));
        await using var plugins = new PluginRuntimeManager();

        var result = new ConfigurationService(providers, plugins).Snapshot(new ConfigurationRequest());

        Assert.IsTrue(result.ProviderRuntimeAvailable);
        Assert.IsTrue(result.PluginRuntimeAvailable);
        Assert.HasCount(2, result.Providers);
        Assert.AreEqual("alpha", result.Providers[0].Id);
        Assert.AreEqual("anthropic", result.Providers[0].Type);
        Assert.IsTrue(result.Providers[0].IsDefault);
        Assert.AreEqual("High", result.Providers[0].DefaultReasoning);
        Assert.AreEqual("zeta-1", result.Providers[1].DefaultModel);
        Assert.IsFalse(result.Providers[1].Enabled);
        Assert.IsEmpty(result.Plugins);
        Assert.IsFalse(result.ProvidersTruncated);
        Assert.IsFalse(result.PluginsTruncated);
    }

    [TestMethod]
    public void Snapshot_ReportsUnavailableCatalogOnlyServices()
    {
        var result = new ConfigurationService().Snapshot(new ConfigurationRequest());

        Assert.IsFalse(result.ProviderRuntimeAvailable);
        Assert.IsFalse(result.PluginRuntimeAvailable);
        Assert.IsEmpty(result.Providers);
        Assert.IsEmpty(result.Plugins);
        Assert.IsFalse(result.ProvidersTruncated);
        Assert.IsFalse(result.PluginsTruncated);
    }
}
