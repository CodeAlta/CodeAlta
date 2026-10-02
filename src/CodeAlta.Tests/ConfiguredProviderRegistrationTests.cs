using System.Reflection;
using CodeAlta.Agent;
using CodeAlta.Agent.OpenAI;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Hosting;

namespace CodeAlta.Tests;

[TestClass]
public sealed class ConfiguredProviderRegistrationTests
{
    [TestMethod]
    [DataRow(null, null)]
    [DataRow("default", "default")]
    [DataRow("priority", "priority")]
    [DataRow(" FAST ", "priority")]
    public async Task CodexRegistration_MapsServiceTierWithoutChangingDefaults(string? tier, string? expected)
    {
        using var temp = TestTempDirectory.Create();
        Assert.IsTrue(ConfiguredModelProviderRegistryBuilder.TryCreateProviderRegistration(
            new CodeAltaProviderDocument
            {
                ProviderKey = "codex",
                ProviderType = "codex",
                ServiceTier = tier,
            },
            temp.Path,
            null,
            out _,
            out var createRuntime));
        await using var runtime = createRuntime();
        var executor = Assert.IsInstanceOfType<IAgentModelProviderRuntime>(runtime).CreateTurnExecutor();
        // Inspect the captured options without starting/probing the provider or reading credentials.
        var providerField = executor.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(static field => field.FieldType == typeof(OpenAIProviderOptions));
        var options = Assert.IsInstanceOfType<OpenAIProviderOptions>(providerField.GetValue(executor));
        Assert.IsNotNull(options.CodexSubscription);
        Assert.AreEqual(expected, options.CodexSubscription.ServiceTier);
        Assert.AreEqual("medium", options.CodexSubscription.TextVerbosity);
        Assert.AreEqual("http", options.CodexSubscription.ResponseTransport);
    }

    [TestMethod]
    [DataRow("flex")]
    [DataRow("prioroty")]
    public void CodexOptions_RejectInvalidServiceTier(string tier)
    {
        var options = new OpenAICodexSubscriptionOptions();
        Assert.ThrowsExactly<ArgumentException>(() => options.ServiceTier = tier);
        Assert.IsNull(options.ServiceTier);
    }
}
