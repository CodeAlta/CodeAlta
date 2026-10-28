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
                  "gpt-6-astra": { "id": "gpt-6-astra", "name": "GPT-6 Astra", "family": "gpt-astra", "limit": { "context": 1050000, "input": 922000, "output": 128000 } },
                  "gpt-6.1-sol": { "id": "gpt-6.1-sol", "name": "GPT-6.1 Sol", "family": "gpt-sol", "limit": { "context": 1050000, "input": 922000, "output": 128000 } },
                  "gpt-5.5": { "id": "gpt-5.5", "name": "GPT-5.5", "family": "gpt", "limit": { "context": 1050000, "input": 922000, "output": 128000 } }
                }
              }
            }
            """);
}
