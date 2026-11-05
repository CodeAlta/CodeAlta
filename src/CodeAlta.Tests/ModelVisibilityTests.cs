using CodeAlta.Agent;

namespace CodeAlta.Tests;

[TestClass]
public sealed class ModelVisibilityTests
{
    [TestMethod]
    public void Parse_ReadsTheThreeKindsOfRule_AndWritesThemBack()
    {
        var anyCase = ModelRule.Parse("  GPT-5.4 ");
        Assert.AreEqual(ModelRuleMatch.AnyCase, anyCase.Match);
        Assert.AreEqual("GPT-5.4", anyCase.Format());
        Assert.IsTrue(anyCase.Matches("gpt-5.4"));

        var exact = ModelRule.Parse("Exact: Grok-4");
        Assert.AreEqual(ModelRuleMatch.Exact, exact.Match);
        Assert.AreEqual("exact:Grok-4", exact.Format());
        Assert.IsTrue(exact.Matches("Grok-4"));
        Assert.IsFalse(exact.Matches("grok-4"), "exact: keeps the case.");

        var regex = ModelRule.Parse("regex:^claude-");
        Assert.AreEqual(ModelRuleMatch.Regex, regex.Match);
        Assert.AreEqual("regex:^claude-", regex.Format());
        Assert.IsTrue(regex.Matches("claude-sonnet-4"));
        Assert.IsFalse(regex.Matches("x-claude-"));
    }

    [TestMethod]
    public void Parse_RefusesAnEmptyRule_AndARegularExpressionThatDoesNotRead()
    {
        Assert.ThrowsExactly<ArgumentException>(() => ModelRule.Parse("regex:  "));
        var ex = Assert.ThrowsExactly<ArgumentException>(() => ModelVisibility.Parse(["regex:("], null));
        StringAssert.Contains(ex.Message, "'(' is not a valid regular expression");
    }

    [TestMethod]
    public void ParseValid_LeavesOutTheRulesThatDoNotRead()
    {
        var visibility = ModelVisibility.ParseValid(["regex:(", "grok-4"], [" "], out var refused);

        CollectionAssert.AreEqual(new[] { "regex:(" }, refused.ToArray());
        Assert.AreEqual(1, visibility.Hide.Count);
        Assert.AreEqual(0, visibility.Show.Count);
        Assert.IsFalse(visibility.Shows("GROK-4"));
    }

    [TestMethod]
    public void Apply_HidesEveryModel_ButTheFewShownAgain_InTheirOrder()
    {
        var visibility = ModelVisibility.Parse(["regex:.*"], ["gpt-5.4", "regex:^claude-"]);
        AgentModelInfo[] models = [new("grok-4"), new("claude-opus"), new("gpt-5.4"), new("kimi"), new("claude-haiku")];

        var (shown, hidden) = visibility.Apply(models);

        CollectionAssert.AreEqual(new[] { "claude-opus", "gpt-5.4", "claude-haiku" }, shown.Select(static model => model.Id).ToArray());
        CollectionAssert.AreEqual(new[] { "grok-4", "kimi" }, hidden.Select(static model => model.Id).ToArray());
    }

    [TestMethod]
    public void Apply_WithoutHideRules_ShowsEveryModel_EvenWithShowRules()
    {
        AgentModelInfo[] models = [new("a"), new("b")];

        Assert.AreSame(ModelVisibility.All, ModelVisibility.Parse(null, [" "]));
        var (shown, hidden) = ModelVisibility.Parse(null, ["a"]).Apply(models);

        Assert.AreSame(models, shown);
        Assert.AreEqual(0, hidden.Count);
    }
}
