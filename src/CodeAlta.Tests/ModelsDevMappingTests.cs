using CodeAlta.Agent;
using CodeAlta.Agent.ModelCatalog;
using CodeAlta.Catalog;
using CodeAlta.Hosting;

namespace CodeAlta.Tests;

/// <summary>
/// How a model of a configured provider finds what models.dev knows of it: under its provider, the provider models.dev
/// lists with the same endpoint, or its vendor, and with the price of its vendor when its provider gives none.
/// </summary>
[TestClass]
public sealed class ModelsDevMappingTests
{
    [TestMethod]
    public void AModelIsFound_ByTheNameAfterItsPath_AndByAVersionWrittenTheOtherWay()
    {
        var database = CreateDatabase();

        Assert.IsTrue(database.TryGetModel("google", "models/gemini-2.5-flash", out var gemini));
        Assert.AreEqual("gemini-2.5-flash", gemini.Id);
        Assert.IsTrue(database.TryGetModel("anthropic", "claude-opus-4.8", out var dotted));
        Assert.AreEqual("claude-opus-4-8", dotted.Id);
        Assert.IsTrue(database.TryGetModel("github-copilot", "claude-opus-4-8", out var dashed));
        Assert.AreEqual("claude-opus-4.8", dashed.Id);
        Assert.IsFalse(database.TryGetModel("anthropic", "claude-opus-4.9", out _));
    }

    [TestMethod]
    public void AProviderIsFound_ByItsEndpoint_AndASharedEndpointNeedsItsKey()
    {
        var database = CreateDatabase();

        Assert.IsTrue(database.TryFindProviderByApiUrl(new Uri("https://api.kimi.com/coding/"), null, out var kimi));
        Assert.AreEqual("kimi-plan", kimi);
        // A trailing /v1 and slash do not matter.
        Assert.IsTrue(database.TryFindProviderByApiUrl(new Uri("https://api.z.ai/api/coding/paas/v4/"), null, out var coding));
        Assert.AreEqual("zai-coding-plan", coding);
        // Two providers have the same endpoint: the key of the configured provider chooses, or none is chosen.
        Assert.IsFalse(database.TryFindProviderByApiUrl(new Uri("https://api.minimax.io/anthropic/v1"), null, out _));
        Assert.IsTrue(database.TryFindProviderByApiUrl(new Uri("https://api.minimax.io/anthropic"), "minimax", out var minimax));
        Assert.AreEqual("minimax", minimax);
        Assert.IsFalse(database.TryFindProviderByApiUrl(new Uri("https://api.openai.com/v1"), "openai", out _));
    }

    [TestMethod]
    public async Task AModelItsProviderDoesNotList_IsDescribedByItsVendor()
    {
        await using var catalog = new ModelsDevCatalogService(CreateDatabase(), new ModelsDevCatalogServiceOptions());

        var model = AgentModelMetadataEnricher.EnrichModel(new AgentModelInfo("claude-sonnet-5"), catalog, "github-copilot", overrides: null);

        Assert.AreEqual("anthropic", model.Capabilities?["modelsDevProviderId"]);
        Assert.AreEqual(1_000_000L, model.Capabilities?["contextWindow"]);
        Assert.AreEqual("Claude Sonnet 5", model.DisplayName);
        Assert.AreEqual(3m, model.Capabilities?[AgentModelMetadataEnricher.InputCostKey]);
    }

    [TestMethod]
    public async Task AModelWhoseProviderGivesNoPrice_HasThePriceOfItsVendor_AndItsOwnLimits()
    {
        await using var catalog = new ModelsDevCatalogService(CreateDatabase(), new ModelsDevCatalogServiceOptions());

        var resold = AgentModelMetadataEnricher.EnrichModel(new AgentModelInfo("claude-opus-4.8"), catalog, "github-copilot", overrides: null);
        var own = AgentModelMetadataEnricher.EnrichModel(new AgentModelInfo("claude-opus-4-8"), catalog, "anthropic", overrides: null);

        Assert.AreEqual("github-copilot", resold.Capabilities?["modelsDevProviderId"]);
        Assert.AreEqual(128_000L, resold.Capabilities?["contextWindow"], "The limits are those of the provider that serves it.");
        Assert.AreEqual(5m, resold.Capabilities?[AgentModelMetadataEnricher.InputCostKey]);
        Assert.AreEqual(25m, resold.Capabilities?[AgentModelMetadataEnricher.OutputCostKey]);
        Assert.AreEqual(0.5m, resold.Capabilities?[AgentModelMetadataEnricher.CacheReadCostKey]);
        Assert.AreEqual("anthropic", resold.Capabilities?[AgentModelMetadataEnricher.CostProviderKey]);
        Assert.AreEqual("anthropic", own.Capabilities?[AgentModelMetadataEnricher.CostProviderKey]);
        Assert.AreEqual(1_000_000L, own.Capabilities?["contextWindow"]);
    }

    [TestMethod]
    public async Task AModelOfAPlugin_IsDescribedByTheIdTheCatalogKnows_AndKeepsItsOwn()
    {
        await using var catalog = new ModelsDevCatalogService(CreateDatabase(), new ModelsDevCatalogServiceOptions());
        var variant = new AgentModelInfo("claude-opus-4-8[context=300k,effort=high]", "claude-opus-4-8");

        var described = catalog.Describe(variant, "claude-opus-4-8", catalogProviderId: null);
        var resold = catalog.Describe(new AgentModelInfo("claude-opus-4.8", "Claude Opus 4.8"), catalogModelId: null, "github-copilot");

        Assert.AreEqual(variant.Id, described.Id);
        Assert.AreEqual("Claude Opus 4.8", described.DisplayName, "A name that is only the id is the name of the catalog.");
        Assert.AreEqual(1_000_000L, described.Capabilities?["contextWindow"]);
        Assert.AreEqual(128_000L, resold.Capabilities?["contextWindow"], "The provider that serves it gives its limits.");
        var unknown = new AgentModelInfo("local[fast=true]", "local");
        Assert.AreSame(unknown, catalog.Describe(unknown, "local", null));
    }

    [TestMethod]
    public async Task AModelNoProviderAndNoVendorKnows_IsLeftAsItsProviderGaveIt()
    {
        await using var catalog = new ModelsDevCatalogService(CreateDatabase(), new ModelsDevCatalogServiceOptions());
        var original = new AgentModelInfo("local-model", "Local model");

        Assert.AreSame(original, AgentModelMetadataEnricher.EnrichModel(original, catalog, "openai", overrides: null));
        Assert.AreSame(original, AgentModelMetadataEnricher.EnrichModel(original, catalog, modelsDevProviderId: null, overrides: null));
    }

    [TestMethod]
    public async Task AConfiguredProvider_IsMatchedByItsEndpoint_BeforeItsTypeDefault()
    {
        await using var catalog = new ModelsDevCatalogService(CreateDatabase(), new ModelsDevCatalogServiceOptions());
        using var temp = TestTempDirectory.Create();
        // Kimi for coding speaks the protocol of Anthropic, and is listed by models.dev with its endpoint.
        Assert.IsTrue(ConfiguredModelProviderRegistryBuilder.TryCreateProviderRegistration(
            new CodeAltaProviderDocument
            {
                ProviderKey = "kimi-for-coding",
                ProviderType = "anthropic",
                ApiKey = "test-key",
                ApiUrl = "https://api.kimi.com/coding/",
                SingleModelId = "k3",
            },
            temp.Path,
            catalog,
            out _,
            out var createRuntime));

        await using var runtime = createRuntime();
        var agentRuntime = Assert.IsInstanceOfType<IAgentModelProviderRuntime>(runtime);
        var model = (await agentRuntime.ModelCatalog!.ListModelsAsync(agentRuntime.RuntimeDescriptor).ConfigureAwait(false)).Single();

        Assert.AreEqual("kimi-plan", model.Capabilities?["modelsDevProviderId"]);
        Assert.AreEqual(262_144L, model.Capabilities?["contextWindow"]);
        // The plan gives no price and names the model its own way: its family names the vendor, its name the model.
        Assert.AreEqual("moonshotai", model.Capabilities?[AgentModelMetadataEnricher.CostProviderKey]);
        Assert.AreEqual(0.6m, model.Capabilities?[AgentModelMetadataEnricher.InputCostKey]);
    }

    [TestMethod]
    public async Task AnXaiProvider_IsDescribedByTheXaiProviderOfModelsDev()
    {
        await using var catalog = new ModelsDevCatalogService(CreateDatabase(), new ModelsDevCatalogServiceOptions());
        using var temp = TestTempDirectory.Create();
        Assert.IsTrue(ConfiguredModelProviderRegistryBuilder.TryCreateProviderRegistration(
            new CodeAltaProviderDocument { ProviderKey = "grok", ProviderType = "xai", SingleModelId = "grok-test" },
            temp.Path,
            catalog,
            out _,
            out var createRuntime));

        await using var runtime = createRuntime();
        var agentRuntime = Assert.IsInstanceOfType<IAgentModelProviderRuntime>(runtime);
        var model = (await agentRuntime.ModelCatalog!.ListModelsAsync(agentRuntime.RuntimeDescriptor).ConfigureAwait(false)).Single();

        Assert.AreEqual("xai", model.Capabilities?["modelsDevProviderId"]);
        Assert.AreEqual(2_000_000L, model.Capabilities?["contextWindow"]);
    }

    private static ModelsDevDatabase CreateDatabase()
        => ModelsDevDatabaseJson.Deserialize(
            """
            {
              "anthropic": {
                "id": "anthropic", "name": "Anthropic",
                "models": {
                  "claude-opus-4-8": { "id": "claude-opus-4-8", "name": "Claude Opus 4.8", "limit": { "context": 1000000, "output": 128000 },
                    "cost": { "input": 5, "output": 25, "cache_read": 0.5, "cache_write": 6.25 } },
                  "claude-sonnet-5": { "id": "claude-sonnet-5", "name": "Claude Sonnet 5", "limit": { "context": 1000000, "output": 64000 },
                    "cost": { "input": 3, "output": 15 } }
                }
              },
              "github-copilot": {
                "id": "github-copilot", "name": "GitHub Copilot", "api": "https://api.githubcopilot.com",
                "models": {
                  "claude-opus-4.8": { "id": "claude-opus-4.8", "name": "Claude Opus 4.8", "limit": { "context": 128000, "output": 32000 },
                    "cost": { "input": 0, "output": 0 } }
                }
              },
              "google": {
                "id": "google", "name": "Google",
                "models": { "gemini-2.5-flash": { "id": "gemini-2.5-flash", "name": "Gemini 2.5 Flash" } }
              },
              "kimi-plan": {
                "id": "kimi-plan", "name": "Kimi For Coding", "api": "https://api.kimi.com/coding/v1",
                "models": { "k3": { "id": "k3", "name": "Kimi K3", "family": "kimi-k3", "limit": { "context": 262144, "output": 32768 },
                  "cost": { "input": 0, "output": 0 } } }
              },
              "moonshotai": {
                "id": "moonshotai", "name": "Moonshot AI",
                "models": { "kimi-k3": { "id": "kimi-k3", "name": "Kimi K3", "limit": { "context": 262144, "output": 32768 },
                  "cost": { "input": 0.6, "output": 2.5 } } }
              },
              "zai": { "id": "zai", "name": "Z.AI", "api": "https://api.z.ai/api/paas/v4", "models": {} },
              "zai-coding-plan": { "id": "zai-coding-plan", "name": "Z.AI Coding Plan", "api": "https://api.z.ai/api/coding/paas/v4", "models": {} },
              "minimax": { "id": "minimax", "name": "MiniMax", "api": "https://api.minimax.io/anthropic/v1", "models": {} },
              "minimax-coding-plan": { "id": "minimax-coding-plan", "name": "MiniMax Coding Plan", "api": "https://api.minimax.io/anthropic/v1", "models": {} },
              "openai": { "id": "openai", "name": "OpenAI", "models": {} },
              "xai": {
                "id": "xai", "name": "xAI",
                "models": { "grok-test": { "id": "grok-test", "name": "Grok Test", "limit": { "context": 2000000, "output": 30000 } } }
              }
            }
            """);
}
