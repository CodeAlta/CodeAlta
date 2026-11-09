using CodeAlta.Catalog;
using CodeAlta.Hosting;
using CodeAlta.Tui.ViewModels;

namespace CodeAlta.Tests;

[TestClass]
public sealed class ProviderRequestLimitConfigTests
{
    [TestMethod]
    [DataRow("openai-chat", "api_key = \"key\"\napi_url = \"http://dgx-spark:8000/v1\"")]
    [DataRow("anthropic", "api_key = \"key\"")]
    [DataRow("copilot", "")]
    [DataRow("xai", "")]
    [DataRow("claude-code", "")]
    [DataRow("llama-cpp", "")]
    public void LoadGlobalProviderDefinitions_AcceptsMaxConcurrentRequestsForEveryType(string type, string fields)
    {
        using var temp = TestTempDirectory.Create();
        var path = Path.Combine(temp.Path, "config.toml");
        File.WriteAllText(path, $"[providers.spark]\ntype = \"{type}\"\n{fields}\nmax_concurrent_requests = 2\n");
        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });

        var provider = store.LoadGlobalProviderDefinitions(includeDisabled: true).Single(static value => value.ProviderKey == "spark");
        Assert.AreEqual(2, provider.MaxConcurrentRequests);

        store.SaveGlobalProviderDefinitions([provider]);
        StringAssert.Contains(File.ReadAllText(path), "max_concurrent_requests = 2");
        Assert.AreEqual(2, store.LoadGlobalProviderDefinitions(includeDisabled: true).Single(static value => value.ProviderKey == "spark").MaxConcurrentRequests);
    }

    [TestMethod]
    public void LoadGlobalProviderDefinitions_RefusesALimitOfZero()
    {
        using var temp = TestTempDirectory.Create();
        File.WriteAllText(Path.Combine(temp.Path, "config.toml"), "[providers.spark]\ntype = \"openai-chat\"\napi_key = \"key\"\nmax_concurrent_requests = 0\n");
        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });

        var exception = Assert.ThrowsExactly<InvalidDataException>(() => store.LoadGlobalProviderDefinitions(includeDisabled: true));

        StringAssert.Contains(exception.InnerException?.Message, "max_concurrent_requests must be greater than zero");
    }

    [TestMethod]
    public void LoadGlobalProviderDefinitions_RefusesALimitForAProviderOfAModelServer()
    {
        // The slots of the models of the server decide, and its machine holds their queue for the whole fleet.
        using var temp = TestTempDirectory.Create();
        File.WriteAllText(Path.Combine(temp.Path, "config.toml"), "[providers.coder]\ntype = \"llama-cpp\"\nserver = \"local/coder\"\nmax_concurrent_requests = 2\n");
        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });

        var exception = Assert.ThrowsExactly<InvalidDataException>(() => store.LoadGlobalProviderDefinitions(includeDisabled: true));

        StringAssert.Contains(exception.InnerException?.Message, "the slots of its models decide");
    }

    [TestMethod]
    public void LoadGlobalProviderDefinitions_CodexKeepsItsDefault()
    {
        using var temp = TestTempDirectory.Create();
        File.WriteAllText(Path.Combine(temp.Path, "config.toml"), "[providers.codex]\ntype = \"codex\"\n");
        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });

        Assert.AreEqual(16, store.LoadGlobalProviderDefinitions(includeDisabled: true).Single().MaxConcurrentRequests);
    }

    [TestMethod]
    public void TryCreateProviderRegistration_GivesTheLimitToTheDescriptor()
    {
        using var temp = TestTempDirectory.Create();
        Assert.IsTrue(ConfiguredModelProviderRegistryBuilder.TryCreateProviderRegistration(
            new CodeAltaProviderDocument { ProviderKey = "spark", ProviderType = "openai-chat", ApiKey = "key", ApiUrl = "http://dgx-spark:8000/v1", MaxConcurrentRequests = 2 },
            temp.Path, null, out var limited, out _));
        Assert.IsTrue(ConfiguredModelProviderRegistryBuilder.TryCreateProviderRegistration(
            new CodeAltaProviderDocument { ProviderKey = "open", ProviderType = "openai-chat", ApiKey = "key" },
            temp.Path, null, out var unlimited, out _));

        Assert.AreEqual(2, limited.MaxConcurrentRequests);
        Assert.IsNull(unlimited.MaxConcurrentRequests);
    }

    [TestMethod]
    public void TryCreateProviderRegistration_CodexKeepsItsOwnGuard()
    {
        using var temp = TestTempDirectory.Create();
        Assert.IsTrue(ConfiguredModelProviderRegistryBuilder.TryCreateProviderRegistration(
            new CodeAltaProviderDocument { ProviderKey = "codex", ProviderType = "codex", MaxConcurrentRequests = 4 },
            temp.Path, null, out var descriptor, out _));

        Assert.IsNull(descriptor.MaxConcurrentRequests, "The guard of the ChatGPT account bounds a codex provider.");
    }

    [TestMethod]
    public void TuiEditor_KeepsTheLimitItDoesNotShow()
    {
        var item = ModelProviderEditorItemViewModel.FromDocument(new CodeAltaProviderDocument
        {
            ProviderKey = "spark",
            ProviderType = "openai-chat",
            ApiKey = "key",
            MaxConcurrentRequests = 3,
        });
        item.DisplayName = "Spark";

        Assert.AreEqual(3, item.ToDocument().MaxConcurrentRequests);
    }
}
