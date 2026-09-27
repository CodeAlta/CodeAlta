using CodeAlta.Agent;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Plugins;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class ConfigurationRpcTests
{
    [TestMethod]
    public async Task Snapshot_NeverTruncatesProviderIdentityAndRemainsBoundedWithoutFactories()
    {
        await using var providers = new ModelProviderRegistry();
        providers.RegisterOrReplace(new(new(new string('x', 257)), "First"),
            () => throw new AssertFailedException("Inventory cannot create a runtime."));
        for (var index = 0; index < 35; index++)
            providers.RegisterOrReplace(new(new($"provider-{index:00}"), $"Provider {index:00}"),
                () => throw new AssertFailedException("Inventory cannot create a runtime."));
        var result = new ConfigurationService(providers).Snapshot(new());
        Assert.IsTrue(result.ProvidersTruncated);
        Assert.HasCount(31, result.Providers);
        Assert.IsFalse(result.Providers.Any(provider => provider.Id == new string('x', 256)));
        Assert.IsTrue(result.Providers.All(provider => providers.ListProviders().Any(value => value.ProviderId.Value == provider.Id)));
    }

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

    [TestMethod]
    public void Snapshot_BrowserOnlyStartupDoesNotRequireCatalogRoot()
    {
        // The explicit --data-root mode forwards a null CatalogRoot to this constructor.
        var result = new ConfigurationService(catalogRoot: null).Snapshot(new ConfigurationRequest());

        Assert.IsFalse(result.ProviderRuntimeAvailable);
        Assert.IsFalse(result.PluginRuntimeAvailable);
        Assert.IsEmpty(result.Providers);
        Assert.IsEmpty(result.Plugins);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("relative")]
    public void Snapshot_NonNullCatalogRootStillRequiresAnAbsoluteDirectory(string root) =>
        Assert.ThrowsExactly<ArgumentException>(() => new ConfigurationService(root));

    [TestMethod]
    public void Snapshot_ReadsCatalogConfigurationWithoutStartingProvidersOrPlugins()
    {
        var root = Path.Combine(Path.GetTempPath(), $"CodeAlta-desktop-configuration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "config.toml"), """
                [chat]
                default_provider = "alpha"

                [providers.alpha]
                type = "openai"
                display_name = "Alpha"
                model = "alpha-1"
                reasoning_effort = "high"

                [providers.off]
                type = "anthropic"
                enabled = false

                [plugins.mcp]
                enabled = true
                """);

            var result = new ConfigurationService(root).Snapshot(new ConfigurationRequest());

            Assert.IsFalse(result.ProviderRuntimeAvailable);
            Assert.IsFalse(result.PluginRuntimeAvailable);
            Assert.HasCount(2, result.Providers);
            Assert.AreEqual("alpha", result.Providers[0].Id);
            Assert.AreEqual("alpha-1", result.Providers[0].DefaultModel);
            Assert.IsTrue(result.Providers[0].IsDefault);
            Assert.IsFalse(result.Providers[1].Enabled);
            Assert.HasCount(1, result.Plugins);
            Assert.AreEqual("mcp", result.Plugins[0].Id);
            Assert.AreEqual("Configured", result.Plugins[0].State);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
