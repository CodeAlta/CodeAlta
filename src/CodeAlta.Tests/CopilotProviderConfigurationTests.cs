using CodeAlta.App;
using CodeAlta.Catalog;

namespace CodeAlta.Tests;

[TestClass]
public sealed class CopilotProviderConfigurationTests
{
    [TestMethod]
    public void LoadGlobalProviderDefinitions_CopilotAllowsCliPathAndNpmRegistry()
    {
        using var temp = TestTempDirectory.Create();
        File.WriteAllText(
            Path.Combine(temp.Path, "config.toml"),
            """
            [providers.copilot_cli]
            enabled = true
            cli_path = " C:/tools/copilot.exe "
            npm_registry = " https://registry.example.test/npm/ "
            model = " claude-opus-4.6 "
            """);

        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });
        var providers = store.LoadGlobalProviderDefinitions(includeDisabled: true)
            .ToDictionary(static provider => provider.ProviderKey, StringComparer.OrdinalIgnoreCase);

        var copilot = providers["copilot_cli"];
        Assert.IsFalse(copilot.Enabled);
        Assert.AreEqual("C:/tools/copilot.exe", copilot.CliPath);
        Assert.AreEqual("https://registry.example.test/npm/", copilot.NpmRegistry);
        Assert.AreEqual("claude-opus-4.6", copilot.Model);
    }

    [TestMethod]
    public void LoadGlobalProviderDefinitions_NpmRegistryMustBeHttpOrHttpsAbsoluteUri()
    {
        using var temp = TestTempDirectory.Create();
        File.WriteAllText(
            Path.Combine(temp.Path, "config.toml"),
            """
            [providers.copilot_cli]
            npm_registry = "file:///tmp/npm"
            """);

        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });
        var exception = Assert.ThrowsExactly<InvalidDataException>(() =>
            store.LoadGlobalProviderDefinitions(includeDisabled: true));

        StringAssert.Contains(exception.GetBaseException().Message, "npm_registry");
    }

    [TestMethod]
    public void LoadGlobalProviderDefinitions_CopilotCliSettingsAreRejectedForOtherProviders()
    {
        using var temp = TestTempDirectory.Create();
        File.WriteAllText(
            Path.Combine(temp.Path, "config.toml"),
            """
            [providers.openai]
            type = "openai-chat"
            api_key_env = "OPENAI_API_KEY"
            cli_path = "C:/tools/copilot.exe"
            """);

        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });
        var exception = Assert.ThrowsExactly<InvalidDataException>(() =>
            store.LoadGlobalProviderDefinitions(includeDisabled: true));

        StringAssert.Contains(exception.GetBaseException().Message, "cli_path");
    }

    [TestMethod]
    public void SaveGlobalProviderDefinitions_PersistsCopilotCliSettings()
    {
        using var temp = TestTempDirectory.Create();
        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });

        store.SaveGlobalProviderDefinitions(
        [
            new CodeAltaProviderDocument
            {
                ProviderKey = "copilot_cli",
                Enabled = true,
                ProviderType = "copilot_cli",
                CliPath = "C:/tools/copilot.exe",
                NpmRegistry = "https://registry.example.test/npm/",
            },
        ]);

        var content = File.ReadAllText(Path.Combine(temp.Path, "config.toml"));
        StringAssert.Contains(content, "cli_path = \"C:/tools/copilot.exe\"");
        StringAssert.Contains(content, "npm_registry = \"https://registry.example.test/npm/\"");

        var providers = store.LoadGlobalProviderDefinitions(includeDisabled: true)
            .ToDictionary(static provider => provider.ProviderKey, StringComparer.OrdinalIgnoreCase);
        Assert.AreEqual("C:/tools/copilot.exe", providers["copilot_cli"].CliPath);
        Assert.AreEqual("https://registry.example.test/npm/", providers["copilot_cli"].NpmRegistry);
    }

    [TestMethod]
    public void CreateCopilotBackendOptions_UsesConfiguredCliPathAndRegistry()
    {
        using var temp = TestTempDirectory.Create();
        var cacheRoot = Path.Combine(temp.Path, "cache");
        var options = CodeAltaOwnedServices.CreateCopilotBackendOptions(
            new CodeAltaProviderDocument
            {
                ProviderKey = "copilot_cli",
                ProviderType = "copilot_cli",
                CliPath = " C:/tools/copilot.exe ",
                NpmRegistry = " https://registry.example.test/npm/ ",
            },
            cacheRoot);

        Assert.AreEqual("C:/tools/copilot.exe", options.ClientOptions.CliPath);
        Assert.IsNotNull(options.CliInstallOptions);
        Assert.AreEqual(cacheRoot, options.CliInstallOptions!.LocalRootPath);
        Assert.AreEqual("https://registry.example.test/npm/", options.CliInstallOptions.NpmRegistryUrl);
    }
}
