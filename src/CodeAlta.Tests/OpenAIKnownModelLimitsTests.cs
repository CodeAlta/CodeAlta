using CodeAlta.Agent;
using CodeAlta.Agent.ModelCatalog;
using CodeAlta.Agent.OpenAI;
using CodeAlta.Agent.Runtime;
using CodeAlta.Agent.Runtime.Compaction;

namespace CodeAlta.Tests;

/// <summary>
/// The context a session of a GPT-6 model keeps to on OpenAI's endpoints: the 272,000 input tokens it is priced for,
/// not the 922,000 models.dev gives, and a compaction at 90% of it, as Codex does.
/// </summary>
[TestClass]
public sealed class OpenAIKnownModelLimitsTests
{
    [TestMethod]
    public async Task AGpt6ModelOfTheOpenAIApi_KeepsTo272000InputTokens_AndCompactsAt90Percent()
    {
        await using var catalog = new ModelsDevCatalogService(CreateDatabase(), new ModelsDevCatalogServiceOptions());
        var models = await ListAsync(new OpenAIProviderOptions
        {
            ProviderKey = "openai",
            ApiKey = "test-key",
            ModelCatalog = catalog,
            ModelsDevProviderId = "openai",
            ModelListAsync = static _ => Task.FromResult<IReadOnlyList<AgentModelInfo>>([new("gpt-6-astra"), new("gpt-6.1-sol"), new("gpt-5.5")]),
        }).ConfigureAwait(false);

        foreach (var id in new[] { "gpt-6-astra", "gpt-6.1-sol" })
        {
            var model = models.Single(model => model.Id == id);
            var budget = AgentTokenBudgetResolver.Resolve(model, AgentCompactionSettings.Default);
            Assert.AreEqual(272_000L, budget.InputContextLimit, id);
            Assert.AreEqual(400_000L, budget.TotalContextEnvelope, id);
            Assert.AreEqual(128_000L, budget.MaxOutputTokens, id);
            // Codex's auto-compact limit for these models: 90% of 272,000.
            Assert.AreEqual(244_800L, AgentTokenBudgetResolver.ResolveCompactionThreshold(budget.InputContextLimit!.Value, model, AgentCompactionSettings.Default), id);
            // The cap of one tool result is a share of the same limit.
            Assert.AreEqual(27_200L, (long)(budget.InputContextLimit.Value * AgentCompactionSettings.DefaultPostCompactionTargetRatio), id);
        }

        // Another family keeps what models.dev gives, and the ratio of the settings.
        var gpt55 = models.Single(static model => model.Id == "gpt-5.5");
        var gpt55Budget = AgentTokenBudgetResolver.Resolve(gpt55, AgentCompactionSettings.Default);
        Assert.AreEqual(922_000L, gpt55Budget.InputContextLimit);
        Assert.AreEqual(875_900L, AgentTokenBudgetResolver.ResolveCompactionThreshold(922_000, gpt55, AgentCompactionSettings.Default));
    }

    [TestMethod]
    public async Task AnotherEndpoint_AndAnOverrideOfTheUser_KeepTheirLimits()
    {
        await using var catalog = new ModelsDevCatalogService(CreateDatabase(), new ModelsDevCatalogServiceOptions());
        var proxied = await ListAsync(new OpenAIProviderOptions
        {
            ProviderKey = "proxy",
            ApiKey = "test-key",
            BaseUri = new Uri("https://llm-proxy.example.test/v1"),
            ModelCatalog = catalog,
            ModelsDevProviderId = "openai",
            ModelListAsync = static _ => Task.FromResult<IReadOnlyList<AgentModelInfo>>([new("gpt-6-astra")]),
        }).ConfigureAwait(false);
        Assert.AreEqual(922_000L, AgentTokenBudgetResolver.Resolve(proxied.Single(), AgentCompactionSettings.Default).InputContextLimit);

        var overridden = await ListAsync(new OpenAIProviderOptions
        {
            ProviderKey = "openai",
            ApiKey = "test-key",
            ModelCatalog = catalog,
            ModelsDevProviderId = "openai",
            ModelOverrides = new Dictionary<string, AgentModelOverride>
            {
                ["gpt-6-astra"] = new() { InputTokenLimit = 500_000 },
            },
            ModelListAsync = static _ => Task.FromResult<IReadOnlyList<AgentModelInfo>>([new("gpt-6-astra")]),
        }).ConfigureAwait(false);
        Assert.AreEqual(500_000L, AgentTokenBudgetResolver.Resolve(overridden.Single(), AgentCompactionSettings.Default).InputContextLimit);
        Assert.IsNull(overridden.Single().ContextSizes, "the limit the user set is the size of the model");
    }

    // A session of the API can use a model at the price it keeps to by default and at the whole input it accepts.
    [TestMethod]
    public async Task AModelOfTheOpenAIApi_CanBeUsedAtEachOfItsPrices()
    {
        await using var catalog = new ModelsDevCatalogService(CreateDatabase(), new ModelsDevCatalogServiceOptions());
        var models = await ListAsync(new OpenAIProviderOptions
        {
            ProviderKey = "openai",
            ApiKey = "test-key",
            ModelCatalog = catalog,
            ModelsDevProviderId = "openai",
            ModelListAsync = static _ => Task.FromResult<IReadOnlyList<AgentModelInfo>>([new("gpt-6-astra"), new("gpt-6.1-sol"), new("gpt-5.5"), new("gpt-4.1")]),
        }).ConfigureAwait(false);

        // The known limit of GPT-6 is its default size, under the input models.dev gives.
        var astra = models.Single(static model => model.Id == "gpt-6-astra");
        CollectionAssert.AreEqual(new long[] { 272_000, 922_000 }, astra.ContextSizes!.ToArray());
        Assert.AreEqual(272_000L, astra.GetContextSize());
        var larger = astra.WithContextSize(922_000);
        var budget = AgentTokenBudgetResolver.Resolve(larger, AgentCompactionSettings.Default);
        Assert.AreEqual(922_000L, budget.InputContextLimit);
        Assert.AreEqual(829_800L, AgentTokenBudgetResolver.ResolveCompactionThreshold(922_000, larger, AgentCompactionSettings.Default));
        // Without a price tier, the known limit still gives both sizes.
        CollectionAssert.AreEqual(new long[] { 272_000, 922_000 }, models.Single(static model => model.Id == "gpt-6.1-sol").ContextSizes!.ToArray());

        // A model whose price rises past 272,000 keeps its whole input by default, and can keep to the lower price.
        var gpt55 = models.Single(static model => model.Id == "gpt-5.5");
        CollectionAssert.AreEqual(new long[] { 272_000, 922_000 }, gpt55.ContextSizes!.ToArray());
        Assert.AreEqual(922_000L, gpt55.GetContextSize());
        Assert.AreEqual(272_000L, gpt55.WithContextSize(272_000).GetContextSize());

        // One price, one size.
        Assert.IsNull(models.Single(static model => model.Id == "gpt-4.1").ContextSizes);
    }

    // The prices of models.dev are those of the provider it is listed for, not of another that resells the model.
    [TestMethod]
    public async Task ThePriceTiersOfTheVendor_AreNotThoseOfAnotherProvider()
    {
        await using var catalog = new ModelsDevCatalogService(CreateDatabase(), new ModelsDevCatalogServiceOptions());

        var resold = AgentModelMetadataEnricher.EnrichModel(new AgentModelInfo("gpt-5.5"), catalog, "some-gateway", overrides: null, contextPriceTiers: true);
        Assert.AreEqual("openai", resold.Capabilities!["modelsDevProviderId"]);
        Assert.IsNull(resold.ContextSizes);

        var own = AgentModelMetadataEnricher.EnrichModel(new AgentModelInfo("gpt-5.5"), catalog, "openai", overrides: null, contextPriceTiers: true);
        CollectionAssert.AreEqual(new long[] { 272_000, 922_000 }, own.ContextSizes!.ToArray());
        Assert.IsNull(AgentModelMetadataEnricher.EnrichModel(new AgentModelInfo("gpt-5.5"), catalog, "openai", overrides: null).ContextSizes, "only where the provider asks");

        // A provider's own sizes are kept.
        var listed = new AgentModelInfo("gpt-5.5") { ContextSizes = [500_000, 922_000] };
        CollectionAssert.AreEqual(new long[] { 500_000, 922_000 }, AgentModelMetadataEnricher.EnrichModel(listed, catalog, "openai", overrides: null, contextPriceTiers: true).ContextSizes!.ToArray());
    }

    [TestMethod]
    public void TheRatioOfTheModel_OnlyLowersTheRatioOfTheSettings()
    {
        var model = new AgentModelInfo("gpt-6-astra", Capabilities: new Dictionary<string, object?>
        {
            [AgentTokenBudgetResolver.CompactionRatioCapabilityKey] = 0.90,
        });

        Assert.AreEqual(80_000L, AgentTokenBudgetResolver.ResolveCompactionThreshold(100_000, model, AgentCompactionSettings.Default with { Ratio = 0.80 }));
        Assert.AreEqual(90_000L, AgentTokenBudgetResolver.ResolveCompactionThreshold(100_000, model, AgentCompactionSettings.Default with { Ratio = 0.98 }));
        Assert.AreEqual(95_000L, AgentTokenBudgetResolver.ResolveCompactionThreshold(100_000, modelInfo: null, AgentCompactionSettings.Default));
    }

    [TestMethod]
    [DataRow("gpt-6", true)]
    [DataRow("gpt-6-astra", true)]
    [DataRow("GPT-6.1-sol", true)]
    [DataRow("gpt-60", false)]
    [DataRow("gpt-5.5", false)]
    [DataRow("openai/gpt-6-astra", false)]
    public void TheTable_MatchesTheFamilyUpToASeparator(string modelId, bool expected)
        => Assert.AreEqual(expected, AgentModelKnownLimit.Find(OpenAIKnownModelLimits.Table, modelId) is not null);

    private static Task<IReadOnlyList<AgentModelInfo>> ListAsync(OpenAIProviderOptions provider)
        => OpenAIProviderSdkFactory.ListModelsAsync(
            provider,
            new ModelProviderRuntimeDescriptor { ProtocolFamily = "openai", ProviderKey = provider.ProviderKey, DisplayName = provider.ProviderKey, TransportKind = AgentTransportKind.OpenAIResponses },
            CancellationToken.None);

    internal static ModelsDevDatabase CreateDatabase()
        => ModelsDevDatabaseJson.Deserialize(
            """
            {
              "openai": {
                "id": "openai", "name": "OpenAI",
                "models": {
                  "gpt-6-astra": { "id": "gpt-6-astra", "name": "GPT-6 Astra", "family": "gpt-astra", "limit": { "context": 1050000, "input": 922000, "output": 128000 },
                    "cost": { "input": 10, "output": 50, "tiers": [{ "input": 20, "output": 75, "tier": { "type": "context", "size": 272000 } }] } },
                  "gpt-6.1-sol": { "id": "gpt-6.1-sol", "name": "GPT-6.1 Sol", "family": "gpt-sol", "limit": { "context": 1050000, "input": 922000, "output": 128000 } },
                  "gpt-5.6-sol": { "id": "gpt-5.6-sol", "name": "GPT-5.6 Sol", "family": "gpt-sol", "limit": { "context": 1050000, "input": 922000, "output": 128000 } },
                  "gpt-5.5": { "id": "gpt-5.5", "name": "GPT-5.5", "family": "gpt", "limit": { "context": 1050000, "input": 922000, "output": 128000 },
                    "cost": { "input": 5, "output": 30, "tiers": [{ "input": 10, "output": 45, "tier": { "type": "context", "size": 272000 } }] } },
                  "gpt-4.1": { "id": "gpt-4.1", "name": "GPT-4.1", "family": "gpt", "limit": { "context": 1047576, "output": 32768 }, "cost": { "input": 2, "output": 8 } }
                }
              }
            }
            """);
}
