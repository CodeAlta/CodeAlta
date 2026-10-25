using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Catalog.ModelTiers;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

[TestClass]
public sealed class ModelTierSelectionTests
{
    private static ModelTierScope Scope(params string[] standard)
        => ModelTierScope.Of(ModelSettings.Read(new CodeAltaModelSettingsDocument
        {
            Tiers = new CodeAltaModelTiersDocument { Standard = [.. standard] },
        }), space: null, SpaceAssignments.Empty);

    private static ModelProviderDescriptor Provider(string key, bool enabled = true)
        => new(new ModelProviderId(key), key.ToUpperInvariant()) { IsEnabled = enabled };

    private static ModelProviderStateSnapshot State(string key, ModelProviderAvailability availability, params AgentModelInfo[] models)
        => new() { Descriptor = Provider(key), Availability = availability, Models = models };

    [TestMethod]
    public void TakesTheFirstTargetWhoseProviderCanAnswer()
    {
        var scope = Scope("gone:m1", "off:m2", "failed:m3", "ready:other@high", "ready:m4@xhigh,high");
        var providers = new[] { Provider("off", enabled: false), Provider("failed"), Provider("ready") };
        var states = new[]
        {
            State("failed", ModelProviderAvailability.Failed),
            State("ready", ModelProviderAvailability.Ready, new AgentModelInfo("m4", SupportedReasoningEfforts: [AgentReasoningEffort.High])),
        };

        var choice = ModelTierSelection.SelectForNewSession(scope, ModelTier.Standard, states,
            key => providers.FirstOrDefault(provider => provider.ProviderId.Value == key), requestedReasoning: null);

        // gone: not configured; off: disabled; failed: known not to answer; ready:other: a ready provider that does not list it.
        Assert.IsNotNull(choice);
        Assert.AreEqual("ready", choice.ProviderKey);
        Assert.AreEqual("m4", choice.ModelId);
        Assert.AreEqual(AgentReasoningEffort.High, choice.Reasoning, "The first effort of the target that the model supports.");
        Assert.AreEqual(ModelTier.Standard, choice.Tier);
    }

    [TestMethod]
    public void TakesAProviderNotProbedYet_AndTheReasoningAskedFor()
    {
        var scope = Scope("later:m1@low");
        var choice = ModelTierSelection.SelectForNewSession(scope, ModelTier.Standard, [], key => Provider(key), AgentReasoningEffort.Max);

        Assert.IsNotNull(choice);
        Assert.AreEqual("later", choice.ProviderKey);
        Assert.AreEqual(AgentReasoningEffort.Max, choice.Reasoning);
    }

    [TestMethod]
    public void GivesNothingForATierWithoutAUsableTarget()
    {
        Assert.IsNull(ModelTierSelection.SelectForNewSession(Scope("a:m"), ModelTier.Complex, [], key => Provider(key), null));
        Assert.IsNull(ModelTierSelection.SelectForNewSession(Scope("a:m"), ModelTier.Standard, [], static _ => null, null));
    }
}
