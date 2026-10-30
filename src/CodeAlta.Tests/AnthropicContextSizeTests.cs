using CodeAlta.Agent;
using CodeAlta.Agent.Anthropic;
using CodeAlta.Agent.ModelCatalog;
using CodeAlta.Agent.Runtime;
using CodeAlta.Agent.Runtime.Compaction;

namespace CodeAlta.Tests;

/// <summary>
/// The sizes of context a session of the Anthropic API can use a model with: the standard 200K window of a model that
/// accepts more, as Claude Code offers it, and each price models.dev gives the model past a size of prompt.
/// </summary>
[TestClass]
public sealed class AnthropicContextSizeTests
{
    [TestMethod]
    public async Task AModelOfTheClaudeApiAbove200K_CanKeepTo200K()
    {
        await using var catalog = new ModelsDevCatalogService(CreateDatabase(), new ModelsDevCatalogServiceOptions());
        var models = Enrich(new AnthropicProviderOptions { ProviderKey = "anthropic", ApiKey = "test-key", ModelCatalog = catalog, ModelsDevProviderId = "anthropic" });

        var opus = models.Single(static model => model.Id == "claude-opus-5-5");
        CollectionAssert.AreEqual(new long[] { 200_000, 1_000_000 }, opus.ContextSizes!.ToArray());
        Assert.AreEqual(1_000_000L, opus.GetContextSize(), "the whole input by default");
        var standard = opus.WithContextSize(200_000);
        var budget = AgentTokenBudgetResolver.Resolve(standard, AgentCompactionSettings.Default);
        Assert.AreEqual(200_000L, budget.InputContextLimit);
        Assert.AreEqual(128_000L, budget.MaxOutputTokens, "the output a request asks for does not change");

        // A price that rises past 100,000 tokens is a size too.
        var haiku = models.Single(static model => model.Id == "claude-haiku-5-5");
        CollectionAssert.AreEqual(new long[] { 100_000, 200_000, 1_000_000 }, haiku.ContextSizes!.ToArray());
        Assert.AreEqual(1_000_000L, haiku.GetContextSize());

        // A model of 200K has one size.
        Assert.IsNull(models.Single(static model => model.Id == "claude-sonnet-4-5").ContextSizes);
    }

    [TestMethod]
    public async Task AnotherEndpointOfTheProtocol_HasNoStandardWindow()
    {
        await using var catalog = new ModelsDevCatalogService(CreateDatabase(), new ModelsDevCatalogServiceOptions());
        var models = Enrich(new AnthropicProviderOptions
        {
            ProviderKey = "gateway",
            ApiKey = "test-key",
            BaseUri = new Uri("https://gateway.example/anthropic"),
            ModelCatalog = catalog,
            ModelsDevProviderId = "gateway",
        });

        Assert.IsNull(models.Single(static model => model.Id == "claude-opus-5-5").ContextSizes);
        Assert.IsNull(models.Single(static model => model.Id == "claude-haiku-5-5").ContextSizes, "the prices of Anthropic are not the gateway's");
    }

    private static IReadOnlyList<AgentModelInfo> Enrich(AnthropicProviderOptions provider)
        => AnthropicModelProviderRuntime.EnrichModels(
            provider,
            new ModelProviderRuntimeDescriptor { ProtocolFamily = "anthropic-messages", ProviderKey = provider.ProviderKey, DisplayName = provider.ProviderKey, TransportKind = AgentTransportKind.AnthropicMessages, BaseUri = provider.BaseUri },
            [Listed("claude-opus-5-5", 1_000_000), Listed("claude-haiku-5-5", 1_000_000), Listed("claude-sonnet-4-5", 200_000)]);

    // A model as the Models API lists it.
    private static AgentModelInfo Listed(string id, long maxInputTokens)
        => new(id, Capabilities: new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["maxInputTokens"] = maxInputTokens,
            ["maxTokens"] = 128_000L,
        });

    private static ModelsDevDatabase CreateDatabase()
        => ModelsDevDatabaseJson.Deserialize(
            """
            {
              "anthropic": {
                "id": "anthropic", "name": "Anthropic",
                "models": {
                  "claude-opus-5-5": { "id": "claude-opus-5-5", "name": "Claude Opus 5.5", "limit": { "context": 1000000, "output": 128000 }, "cost": { "input": 4, "output": 20 } },
                  "claude-haiku-5-5": { "id": "claude-haiku-5-5", "name": "Claude Haiku 5.5", "limit": { "context": 1000000, "output": 128000 },
                    "cost": { "input": 0.1, "output": 0.5, "tiers": [{ "input": 0.5, "output": 2.5, "tier": { "type": "context", "size": 100000 } }] } },
                  "claude-sonnet-4-5": { "id": "claude-sonnet-4-5", "name": "Claude Sonnet 4.5", "limit": { "context": 200000, "output": 64000 }, "cost": { "input": 3, "output": 15 } }
                }
              }
            }
            """);
}
