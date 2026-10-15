using CodeAlta.Agent;
using CodeAlta.Catalog.ModelTiers;

namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed class ModelTierResolverTests
{
    private static readonly ModelTierSettings Tiers = ModelSettings.Read(new CodeAltaModelSettingsDocument
    {
        Tiers = new CodeAltaModelTiersDocument
        {
            Complex = ["claude-code:opus@xhigh,high", "codex:gpt-6.1@xhigh"],
            Standard = ["claude-code:opus@medium", "codex:gpt-6.1@medium"],
            Quick = ["claude-code:haiku"],
        },
    }).Tiers;

    [TestMethod]
    public void Resolve_TakesTheFirstReadyTargetWhoseProviderKnowsTheModel()
    {
        var states = new[]
        {
            State("claude-code", ModelProviderAvailability.Failed, "opus"),
            State("codex", ModelProviderAvailability.Ready, "gpt-6.1"),
        };

        var choice = ModelTierResolver.Resolve(Tiers, ModelTier.Complex, states)!;
        Assert.AreEqual("codex", choice.ProviderKey);
        Assert.AreEqual("gpt-6.1", choice.ModelId);
        Assert.AreEqual(AgentReasoningEffort.XHigh, choice.Reasoning);
        Assert.AreEqual("Codex", choice.ProviderDisplayName);
        Assert.AreEqual("GPT-6.1", choice.ModelDisplayName);

        Assert.IsNull(ModelTierResolver.Resolve(Tiers, ModelTier.Quick, states));
        Assert.IsNull(ModelTierResolver.Resolve(Tiers, ModelTier.Complex, [State("codex", ModelProviderAvailability.Ready, "gpt-5")]));
        Assert.IsNull(ModelTierResolver.Resolve(Tiers, ModelTier.Complex, states, isProviderAllowed: static key => key != "codex"));
    }

    [TestMethod]
    public void Resolve_TakesTheFirstReasoningTheModelSupports()
    {
        var states = new[] { State("claude-code", ModelProviderAvailability.Ready, "opus", [AgentReasoningEffort.Low, AgentReasoningEffort.High]) };
        Assert.AreEqual(AgentReasoningEffort.High, ModelTierResolver.Resolve(Tiers, ModelTier.Complex, states)!.Reasoning);

        // A provider that lists no model is taken at its word.
        Assert.AreEqual("haiku", ModelTierResolver.Resolve(Tiers, ModelTier.Quick, [new ModelProviderStateSnapshot
        {
            Descriptor = new ModelProviderDescriptor(new ModelProviderId("claude-code"), "Claude Code"),
            Availability = ModelProviderAvailability.Ready,
        }])!.ModelId);
    }

    [TestMethod]
    public void Candidates_ListEachTargetWithEachReasoningInOrder()
    {
        var candidates = ModelTierResolver.Candidates(Tiers, ModelTier.Complex).Select(static candidate => candidate.Format()).ToArray();
        CollectionAssert.AreEqual(new[] { "claude-code:opus@xhigh", "claude-code:opus@high", "codex:gpt-6.1@xhigh" }, candidates);
        Assert.AreEqual("claude-code:haiku", ModelTierResolver.Candidates(Tiers, ModelTier.Quick).Single().Format());
    }

    [TestMethod]
    public void DescribeForAgents_NamesTheFirstTargetOfEachTier()
    {
        Assert.AreEqual(
            "`complex` (claude-code:opus@xhigh, then 1 more), `standard` (claude-code:opus@medium, then 1 more), `quick` (claude-code:haiku)",
            ModelTierResolver.DescribeForAgents(Tiers));
        Assert.IsNull(ModelTierResolver.DescribeForAgents(ModelTierSettings.Empty));
    }

    [TestMethod]
    public void Scope_TakesTheTiersOfTheSpace_AndOnlyItsProviders()
    {
        var assignments = SpaceAssignments.Parse("[providers.codex]\nspaces = [\"personal\"]\n");
        var business = new SpaceDescriptor { Id = "business", Name = "Business" };
        var global = ModelTierScope.Of(Usual, business, assignments);
        Assert.IsFalse(global.FromSpace);
        Assert.AreSame(Tiers, global.Tiers);
        Assert.IsFalse(global.IsProviderAvailable("codex"));
        Assert.IsTrue(global.IsProviderAvailable("claude-code"));
        Assert.IsTrue(ModelTierScope.Of(Usual, new SpaceDescriptor { Id = "personal", Name = "Personal" }, assignments).IsProviderAvailable("codex"));

        var states = new[] { State("claude-code", ModelProviderAvailability.Failed, "opus"), State("codex", ModelProviderAvailability.Ready, "gpt-6.1") };
        Assert.IsNull(ModelTierResolver.Resolve(global, ModelTier.Complex, states), "Codex is not a provider of Business.");
        Assert.AreEqual("`complex` (claude-code:opus@xhigh), `standard` (claude-code:opus@medium), `quick` (claude-code:haiku)",
            ModelTierResolver.DescribeForAgents(global.Tiers, global.IsProviderAvailable));

        business.Settings = new SpaceSettings(new SpaceNewSessions(null, SpaceModelTiers.From(ModelSettings.Read(new CodeAltaModelSettingsDocument
        {
            Tiers = new CodeAltaModelTiersDocument { Quick = ["claude-code:haiku@low"] },
        }).Tiers)));
        var own = ModelTierScope.Of(Usual, business, assignments);
        Assert.IsTrue(own.FromSpace);
        Assert.AreEqual(0, own.Tiers.Complex.Count, "The tiers of a space replace the global ones.");
        Assert.AreEqual("haiku", own.Tiers.Quick.Single().ModelId);
    }

    [TestMethod]
    public void Scope_TakesTheFallbackOfTheSpace_OnlyWithItsOwnTiers()
    {
        var assignments = SpaceAssignments.Parse("[providers.work-claude]\nspaces = [\"business\"]\n");
        var spaceTiers = SpaceModelTiers.From(ModelSettings.Read(new CodeAltaModelSettingsDocument
        {
            Tiers = new CodeAltaModelTiersDocument { Standard = ["work-claude:opus@high", "codex:gpt-6.1@high"] },
        }).Tiers);
        var on = new ModelFallbackSettings(true, OnQuota: true, OnUnavailable: false);

        // The usual settings: the default space and a space without tiers of its own take the global tiers and fallback.
        foreach (var space in new[] { SpaceDescriptor.CreateDefault(), new SpaceDescriptor { Id = "business", Name = "Business" }, null })
        {
            var usual = ModelTierScope.Of(Usual, space, assignments);
            Assert.IsFalse(usual.FromSpace);
            Assert.IsFalse(usual.FallbackFromSpace);
            Assert.AreEqual(Usual.Fallback, usual.Fallback);
        }

        // Tiers of its own and a fallback of its own: the space's, with its own provider.
        var business = new SpaceDescriptor { Id = "business", Name = "Business", Settings = new SpaceSettings(new SpaceNewSessions(null, spaceTiers, on)) };
        var scope = ModelTierScope.Of(Usual, business, assignments);
        Assert.IsTrue(scope.FromSpace);
        Assert.IsTrue(scope.FallbackFromSpace);
        Assert.AreEqual(on, scope.Fallback);
        Assert.IsTrue(scope.IsProviderAvailable("work-claude"));
        Assert.IsFalse(ModelTierScope.Of(Usual, SpaceDescriptor.CreateDefault(), assignments).IsProviderAvailable("work-claude"));

        // Tiers of its own without a fallback (a file of 1.9.0): the global fallback, as before.
        business.Settings = new SpaceSettings(new SpaceNewSessions(null, spaceTiers));
        scope = ModelTierScope.Of(Usual, business, assignments);
        Assert.IsTrue(scope.FromSpace);
        Assert.IsFalse(scope.FallbackFromSpace);
        Assert.AreEqual(Usual.Fallback, scope.Fallback);

        // A fallback without tiers of its own is not read: the space has the usual settings.
        business.Settings = new SpaceSettings(new SpaceNewSessions(null, null, on));
        scope = ModelTierScope.Of(Usual, business, assignments);
        Assert.IsFalse(scope.FromSpace);
        Assert.AreEqual(Usual.Fallback, scope.Fallback);
    }

    [TestMethod]
    public void ConfigOf190_GivesEverySpaceTheSameTiersAndFallbackAsBefore()
    {
        // A [models] table as CodeAlta 1.9.0 writes it.
        var root = Path.Combine(Path.GetTempPath(), "CodeAlta-tiers-" + Guid.NewGuid().ToString("N"));
        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = root });
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(store.ConfigPath, """
                [models.tiers]
                complex = ["claude-code:opus@xhigh,high", "codex:gpt-6.1@xhigh"]
                standard = ["claude-code:opus@medium"]

                [models.fallback]
                enabled = true
                on = ["quota"]

                [providers.codex]
                type = "codex"
                spaces = ["business"]
                """);
            var settings = store.LoadGlobalModelSettings();
            var assignments = store.LoadGlobalSpaceAssignments();
            foreach (var space in new[] { SpaceDescriptor.CreateDefault(), new SpaceDescriptor { Id = "business", Name = "Business" } })
            {
                var scope = ModelTierScope.Of(settings, space, assignments);
                Assert.IsFalse(scope.FromSpace, space.Id);
                Assert.AreSame(settings.Tiers, scope.Tiers);
                Assert.AreEqual(new ModelFallbackSettings(true, OnQuota: true, OnUnavailable: false), scope.Fallback, space.Id);
            }

            Assert.IsTrue(ModelTierScope.Of(settings, new SpaceDescriptor { Id = "business", Name = "Business" }, assignments).IsProviderAvailable("codex"));
            Assert.IsFalse(ModelTierScope.Of(settings, SpaceDescriptor.CreateDefault(), assignments).IsProviderAvailable("codex"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static readonly ModelSettings Usual = new(Tiers, new ModelFallbackSettings(true, OnQuota: true, OnUnavailable: true));

    private static ModelProviderStateSnapshot State(string key, ModelProviderAvailability availability, string model, IReadOnlyList<AgentReasoningEffort>? reasoning = null)
        => new()
        {
            Descriptor = new ModelProviderDescriptor(new ModelProviderId(key), key == "codex" ? "Codex" : "Claude Code"),
            Availability = availability,
            Models = [new AgentModelInfo(model, model == "gpt-6.1" ? "GPT-6.1" : null, SupportedReasoningEfforts: reasoning)],
        };
}
