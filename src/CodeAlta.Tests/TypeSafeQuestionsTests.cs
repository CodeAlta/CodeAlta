using System.Text.Json.Nodes;
using CodeAlta.Agent.TypeSafe;

namespace CodeAlta.Tests;

/// <summary>Tests of <see cref="TypeSafeQuestions"/>: the rules of the System One API checked before a request is sent.</summary>
[TestClass]
public sealed class TypeSafeQuestionsTests
{
    [TestMethod]
    public void AMixOfTheThreeTypes_IsValid()
    {
        var questions = Parse("""
            {
              "flaky": { "type": "noul", "instructions": "The failure comes from timing." },
              "meant": { "type": "noul", "instructions": { "claim": "The change is safe" }, "criteria": { "true": "safe", "false": "unsafe" } },
              "half": { "type": "noul", "instructions": "One meaning only.", "criteria": { "true": "yes" } },
              "area": { "type": "choice", "instructions": "Where the defect is", "criteria": { "parser": "Parsing", "io": null } },
              "risk": { "type": "score", "instructions": ["How risky", "the change is"], "criteria": ["none", "low", "high"] }
            }
            """);

        foreach (var state in new JsonNode[] { JsonValue.Create("the diff"), new JsonObject { ["diff"] = "+ line" }, new JsonArray("a", "b") })
        {
            Assert.IsTrue(TypeSafeQuestions.TryValidate(state, questions, out var error), error);
            Assert.IsNull(error);
        }
    }

    [TestMethod]
    public void ChoiceAndScore_AcceptTheirLimits()
    {
        var options = new JsonObject();
        for (var index = 0; index < TypeSafeQuestions.MaximumChoiceOptions; index++) options[$"o{index}"] = null;
        var levels = new JsonArray();
        for (var index = 0; index < TypeSafeQuestions.MaximumScoreLevels; index++) levels.Add($"level {index}");
        var questions = new JsonObject
        {
            ["one"] = new JsonObject { ["type"] = "choice", ["instructions"] = "Pick", ["criteria"] = new JsonObject { ["only"] = "The one option" } },
            ["many"] = new JsonObject { ["type"] = "choice", ["instructions"] = "Pick", ["criteria"] = options },
            ["two"] = new JsonObject { ["type"] = "score", ["instructions"] = "Rate", ["criteria"] = new JsonArray("low", "high") },
            ["ten"] = new JsonObject { ["type"] = "score", ["instructions"] = "Rate", ["criteria"] = levels },
        };

        Assert.IsTrue(TypeSafeQuestions.TryValidate(JsonValue.Create("state"), questions, out var error), error);
    }

    [TestMethod]
    [DataRow("null", "`state` is required")]
    [DataRow("\"  \"", "`state` is empty")]
    [DataRow("42", "`state` is a text, an object or an array")]
    [DataRow("true", "`state` is a text, an object or an array")]
    public void TheState_IsAText_AnObjectOrAnArray(string state, string expected)
    {
        Assert.IsFalse(TypeSafeQuestions.TryValidate(JsonNode.Parse(state), Parse("""{"q": {"type": "noul", "instructions": "Judge."}}"""), out var error));
        StringAssert.Contains(error, expected);
    }

    [TestMethod]
    [DataRow("null")]
    [DataRow("{}")]
    [DataRow("[]")]
    [DataRow("\"questions\"")]
    public void TheQuestions_AreAnObjectOfAtLeastOne(string questions)
    {
        Assert.IsFalse(TypeSafeQuestions.TryValidate(JsonValue.Create("state"), JsonNode.Parse(questions), out var error));
        StringAssert.Contains(error, "`questions` is required");
    }

    [TestMethod]
    [DataRow("""{"q": "noul"}""", "Question `q` is an object")]
    [DataRow("""{"q": {"type": "noul", "instructions": "Judge.", "model": "jev"}}""", "Question `q` has an unknown field `model`")]
    [DataRow("""{"q": {"type": "noul"}}""", "Question `q` needs `instructions`")]
    [DataRow("""{"q": {"type": "noul", "instructions": "  "}}""", "Question `q` needs `instructions`")]
    [DataRow("""{"q": {"type": "noul", "instructions": {}}}""", "Question `q` needs `instructions`")]
    [DataRow("""{"q": {"type": "noul", "instructions": null}}""", "Question `q` needs `instructions`")]
    [DataRow("""{"q": {"instructions": "Judge."}}""", "Question `q` has the type `(none)`")]
    [DataRow("""{"q": {"type": "maybe", "instructions": "Judge."}}""", "Question `q` has the type `maybe`")]
    [DataRow("""{"q": {"type": 3, "instructions": "Judge."}}""", "Question `q` has the type `(none)`")]
    [DataRow("""{"q": {"type": "noul", "instructions": "Judge.", "criteria": {"yes": "true"}}}""", "Question `q`: the `criteria` of a noul")]
    [DataRow("""{"q": {"type": "noul", "instructions": "Judge.", "criteria": ["true", "false"]}}""", "Question `q`: the `criteria` of a noul")]
    [DataRow("""{"q": {"type": "choice", "instructions": "Pick."}}""", "Question `q`: the `criteria` of a choice")]
    [DataRow("""{"q": {"type": "choice", "instructions": "Pick.", "criteria": {}}}""", "Question `q`: the `criteria` of a choice")]
    [DataRow("""{"q": {"type": "choice", "instructions": "Pick.", "criteria": ["a", "b"]}}""", "Question `q`: the `criteria` of a choice")]
    [DataRow("""{"q": {"type": "choice", "instructions": "Pick.", "criteria": {" ": "blank"}}}""", "Question `q`: the `criteria` of a choice")]
    [DataRow("""{"q": {"type": "score", "instructions": "Rate."}}""", "Question `q`: the `criteria` of a score")]
    [DataRow("""{"q": {"type": "score", "instructions": "Rate.", "criteria": ["only"]}}""", "Question `q`: the `criteria` of a score")]
    [DataRow("""{"q": {"type": "score", "instructions": "Rate.", "criteria": ["low", " "]}}""", "Question `q`: the `criteria` of a score")]
    [DataRow("""{"q": {"type": "score", "instructions": "Rate.", "criteria": {"1": "low", "2": "high"}}}""", "Question `q`: the `criteria` of a score")]
    public void EachRule_IsRefused_NamingTheQuestion(string questions, string expected)
    {
        Assert.IsFalse(TypeSafeQuestions.TryValidate(JsonValue.Create("state"), JsonNode.Parse(questions), out var error));
        StringAssert.Contains(error, expected);
    }

    [TestMethod]
    public void TooManyOptionsOrLevels_AreRefused()
    {
        var options = new JsonObject();
        for (var index = 0; index <= TypeSafeQuestions.MaximumChoiceOptions; index++) options[$"o{index}"] = null;
        var levels = new JsonArray();
        for (var index = 0; index <= TypeSafeQuestions.MaximumScoreLevels; index++) levels.Add($"level {index}");

        Assert.IsFalse(TypeSafeQuestions.TryValidate(JsonValue.Create("state"),
            new JsonObject { ["pick"] = new JsonObject { ["type"] = "choice", ["instructions"] = "Pick", ["criteria"] = options } }, out var choice));
        StringAssert.Contains(choice, "Question `pick`");
        StringAssert.Contains(choice, "1 to 255 options");
        Assert.IsFalse(TypeSafeQuestions.TryValidate(JsonValue.Create("state"),
            new JsonObject { ["rate"] = new JsonObject { ["type"] = "score", ["instructions"] = "Rate", ["criteria"] = levels } }, out var score));
        StringAssert.Contains(score, "Question `rate`");
        StringAssert.Contains(score, "2 to 10 levels");
    }

    [TestMethod]
    public void TheFirstInvalidQuestion_IsNamed_AmongValidOnes()
    {
        var questions = Parse("""
            {
              "fine": { "type": "noul", "instructions": "Judge." },
              "broken": { "type": "score", "instructions": "Rate.", "criteria": ["one"] },
              "later": { "type": "unknown", "instructions": "Judge." }
            }
            """);

        Assert.IsFalse(TypeSafeQuestions.TryValidate(JsonValue.Create("state"), questions, out var error));
        StringAssert.Contains(error, "`broken`");
        Assert.IsFalse(error!.Contains("`fine`", StringComparison.Ordinal) || error.Contains("`later`", StringComparison.Ordinal), error);
    }

    [TestMethod]
    public void ABlankQuestionId_IsRefused()
    {
        Assert.IsFalse(TypeSafeQuestions.TryValidate(JsonValue.Create("state"), Parse("""{" ": {"type": "noul", "instructions": "Judge."}}"""), out var error));
        StringAssert.Contains(error, "question id is empty");
    }

    private static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();
}
