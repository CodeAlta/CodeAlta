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
        var global = ModelTierScope.Of(Tiers, business, assignments);
        Assert.IsFalse(global.FromSpace);
        Assert.AreSame(Tiers, global.Tiers);
        Assert.IsFalse(global.IsProviderAvailable("codex"));
        Assert.IsTrue(global.IsProviderAvailable("claude-code"));
        Assert.IsTrue(ModelTierScope.Of(Tiers, new SpaceDescriptor { Id = "personal", Name = "Personal" }, assignments).IsProviderAvailable("codex"));

        var states = new[] { State("claude-code", ModelProviderAvailability.Failed, "opus"), State("codex", ModelProviderAvailability.Ready, "gpt-6.1") };
        Assert.IsNull(ModelTierResolver.Resolve(global, ModelTier.Complex, states), "Codex is not a provider of Business.");
        Assert.AreEqual("`complex` (claude-code:opus@xhigh), `standard` (claude-code:opus@medium), `quick` (claude-code:haiku)",
            ModelTierResolver.DescribeForAgents(global.Tiers, global.IsProviderAvailable));

        business.Settings = new SpaceSettings(new SpaceNewSessions(null, SpaceModelTiers.From(ModelSettings.Read(new CodeAltaModelSettingsDocument
        {
            Tiers = new CodeAltaModelTiersDocument { Quick = ["claude-code:haiku@low"] },
        }).Tiers)));
        var own = ModelTierScope.Of(Tiers, business, assignments);
        Assert.IsTrue(own.FromSpace);
        Assert.AreEqual(0, own.Tiers.Complex.Count, "The tiers of a space replace the global ones.");
        Assert.AreEqual("haiku", own.Tiers.Quick.Single().ModelId);
    }

    private static ModelProviderStateSnapshot State(string key, ModelProviderAvailability availability, string model, IReadOnlyList<AgentReasoningEffort>? reasoning = null)
        => new()
        {
            Descriptor = new ModelProviderDescriptor(new ModelProviderId(key), key == "codex" ? "Codex" : "Claude Code"),
            Availability = availability,
            Models = [new AgentModelInfo(model, model == "gpt-6.1" ? "GPT-6.1" : null, SupportedReasoningEfforts: reasoning)],
        };
}
