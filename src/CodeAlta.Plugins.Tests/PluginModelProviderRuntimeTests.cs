using CodeAlta.Agent;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugins.Tests;

[TestClass]
public sealed class PluginModelProviderRuntimeTests
{
    [TestMethod]
    public async Task ModelProviderTypes_AreListedForAllPlugins_AndForOnePluginByItsId()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "CodeAlta-provider-types-" + Guid.NewGuid().ToString("N"))).FullName;
        await using var runtime = new PluginRuntimeManager();
        try
        {
            await runtime.StartAsync(new PluginRuntimeManagerOptions
            {
                GlobalRoot = root,
                BuiltIns =
                [
                    new BuiltInPluginDefinition { Id = "agents", DisplayName = "Agents", PluginType = typeof(AgentsPlugin), Factory = static () => new AgentsPlugin() },
                    new BuiltInPluginDefinition { Id = "other", DisplayName = "Other", PluginType = typeof(OtherPlugin), Factory = static () => new OtherPlugin() },
                ],
            });

            CollectionAssert.AreEqual(new[] { "fake-agent" }, runtime.GetModelProviderTypes().Select(static type => type.ProviderType).ToArray());
            CollectionAssert.AreEqual(new[] { "fake-agent" }, runtime.GetModelProviderTypes("Agents").Select(static type => type.ProviderType).ToArray());
            Assert.AreEqual(0, runtime.GetModelProviderTypes("other").Count, "A plugin that gives no provider type.");
            Assert.AreEqual(0, runtime.GetModelProviderTypes("missing").Count);
            Assert.ThrowsExactly<ArgumentException>(() => runtime.GetModelProviderTypes(" "));
        }
        finally
        {
            await runtime.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>A plugin that gives a provider type.</summary>
    public sealed class AgentsPlugin : PluginBase
    {
        /// <inheritdoc />
        public override IEnumerable<PluginModelProviderContribution> GetModelProviders()
        {
            yield return new PluginModelProviderContribution
            {
                ProviderType = "fake-agent",
                DisplayName = "Fake agent",
                CreateRuntime = static _ => throw new NotSupportedException("Not created in this test."),
            };
        }
    }

    /// <summary>A plugin that gives none.</summary>
    public sealed class OtherPlugin : PluginBase;
}
