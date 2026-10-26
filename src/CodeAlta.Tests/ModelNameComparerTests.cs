using CodeAlta.Agent;

namespace CodeAlta.Tests;

[TestClass]
public sealed class ModelNameComparerTests
{
    [TestMethod]
    public void Sort_GroupsByName_WithTheHighestVersionFirst()
    {
        // The models of a multi-vendor agent, in the agent's own order (#220).
        string[] listed =
        [
            "GPT-5", "Claude Fable 5", "Gemini 3.8 Flash", "GPT-5.1 Codex", "Claude Fable 5.1", "Gemini 4 Argon", "Grok Code",
            "GPT-5.1", "Claude Fable 5.1 Thinking", "Grok 4", "Auto", "Gemini 3.10 Pro", "Gemini 3.8 Pro",
        ];

        var sorted = ModelNameComparer.Sort(listed.Select(static name => new AgentModelInfo(name.ToLowerInvariant().Replace(' ', '-'), name)));

        CollectionAssert.AreEqual(new[]
        {
            "Auto",
            "Claude Fable 5.1", "Claude Fable 5.1 Thinking", "Claude Fable 5",
            "Gemini 4 Argon", "Gemini 3.10 Pro", "Gemini 3.8 Flash", "Gemini 3.8 Pro",
            "GPT-5.1", "GPT-5.1 Codex", "GPT-5",
            "Grok 4", "Grok Code",
        }, sorted.Select(static model => model.DisplayName).ToArray());
    }

    [TestMethod]
    [DataRow("claude-sonnet-4-5-20250929", "claude-sonnet-4-5-20250514")]
    [DataRow("o4-mini", "o3")]
    [DataRow("gpt-5.10", "gpt-5.9")]
    [DataRow("gpt-5.1", "gpt-5")]
    [DataRow("model-0099999999999999999999", "model-98")]
    [DataRow("Opus", "opus 4")]
    [DataRow("gpt-5", "GPT 5 mini")]
    public void Compare_PutsTheFirstNameFirst(string first, string second)
    {
        Assert.IsLessThan(0, ModelNameComparer.Instance.Compare(first, second));
        Assert.IsGreaterThan(0, ModelNameComparer.Instance.Compare(second, first));
    }

    [TestMethod]
    public void Compare_ReadsCaseAndPunctuationAsTheSameName()
    {
        Assert.AreEqual(0, ModelNameComparer.Instance.Compare("GPT-5.1 Codex", "gpt 5.1 codex"));
        Assert.AreEqual(0, ModelNameComparer.Instance.Compare("", " - "));
        Assert.IsLessThan(0, ModelNameComparer.Instance.Compare(null, ""));
    }

    [TestMethod]
    public void ModelOrdering_ReadsWritesAndAppliesTheOrders()
    {
        Assert.IsTrue(ModelOrdering.TryParse(" NAME_version ", out var order));
        Assert.AreEqual(ModelOrder.NameVersion, order);
        Assert.IsFalse(ModelOrdering.TryParse("newest", out _));
        Assert.IsFalse(ModelOrdering.TryParse(null, out _));
        CollectionAssert.AreEqual(new[] { "provider", "name", "name_version" }, Enum.GetValues<ModelOrder>().Select(ModelOrdering.Format).ToArray());

        Assert.AreEqual(ModelOrder.NameVersion, ModelOrdering.Resolve("name_version", sortModels: false, ModelOrder.Provider));
        Assert.AreEqual(ModelOrder.Name, ModelOrdering.Resolve(null, sortModels: true, ModelOrder.Provider));
        Assert.AreEqual(ModelOrder.Provider, ModelOrdering.Resolve("newest", sortModels: false, ModelOrder.NameVersion));
        Assert.AreEqual(ModelOrder.NameVersion, ModelOrdering.Resolve("newest", sortModels: null, ModelOrder.NameVersion));

        AgentModelInfo[] models = [new("gpt-5", "GPT-5"), new("gpt-5.1", "GPT-5.1"), new("auto", "Auto")];
        Assert.AreSame(models, ModelOrdering.Apply(models, ModelOrder.Provider));
        CollectionAssert.AreEqual(new[] { "auto", "gpt-5", "gpt-5.1" }, ModelOrdering.Apply(models, ModelOrder.Name).Select(static model => model.Id).ToArray());
        CollectionAssert.AreEqual(new[] { "auto", "gpt-5.1", "gpt-5" }, ModelOrdering.Apply(models, ModelOrder.NameVersion).Select(static model => model.Id).ToArray());
    }

    [TestMethod]
    public void Sort_KeepsTheOrderOfTheModelsOfTheSameName_ThenTheirIds()
    {
        AgentModelInfo[] models = [new("b", "Shared"), new("a", "shared"), new("A", "SHARED"), new("beta")];

        var sorted = ModelNameComparer.Sort(models);

        CollectionAssert.AreEqual(new[] { "beta", "a", "A", "b" }, sorted.Select(static model => model.Id).ToArray());
        Assert.AreEqual("b", models[0].Id, "The input is not changed.");
    }
}
