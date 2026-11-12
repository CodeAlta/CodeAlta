using CodeAlta.Agent;
using CodeAlta.Agent.Harness;
using CodeAlta.Catalog;
using CodeAlta.Hosting;
using CodeAlta.Plugin.CodexCli;

namespace CodeAlta.Tests;

[TestClass]
public sealed class CodexCliModelOrderTests
{
    [TestMethod]
    [DataRow("", ModelOrder.NameVersion)]
    [DataRow("models_order = \"provider\"", ModelOrder.Provider)]
    [DataRow("sort_models = false", ModelOrder.Provider)]
    [DataRow("models_order = \"name\"", ModelOrder.Name)]
    public async Task Registration_DefaultsToVersionOrder_AndHonorsAnExplicitOrder(string setting, ModelOrder expected)
    {
        var root = Directory.CreateTempSubdirectory("codealta-codex-model-order-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "config.toml"), $"[providers.codex-cli]\ntype = \"codex-cli\"\n{setting}\n");
            var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = root });
            await using var endpoints = new LoopbackHarnessToolEndpoints();
            var types = new PluginModelProviderTypes(endpoints);
            var plugin = new CodexCliPlugin();
            types.Attach(() => plugin.GetModelProviders().ToArray());

            var descriptor = types.RegisterOrReplace(new ModelProviderRegistry(), store, root, includeDisabled: false).Single();

            Assert.AreEqual(expected, descriptor.ModelOrder,
                "The desktop and phone use this order to decide whether older models go under Other models.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
