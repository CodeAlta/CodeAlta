using System.Text.Json;

namespace CodeAlta.Cli.Tests;

/// <summary>The JSON Schema subset of <c>--output-schema</c>: what is checked, and what is refused.</summary>
[TestClass]
public sealed class CliStructuredOutputTests
{
    [TestMethod]
    public void NumericAndStringConstraints_AreChecked()
    {
        var number = Schema("""{"type":"integer","minimum":5,"maximum":10}""");
        Assert.IsNull(CliStructuredOutput.Check("1", number).Json, "1 is below the minimum.");
        Assert.IsNull(CliStructuredOutput.Check("11", number).Json);
        Assert.AreEqual("5", CliStructuredOutput.Check("5", number).Json);
        var exclusive = Schema("""{"type":"number","exclusiveMinimum":0,"exclusiveMaximum":1}""");
        Assert.IsNull(CliStructuredOutput.Check("0", exclusive).Json);
        Assert.AreEqual("0.5", CliStructuredOutput.Check("0.5", exclusive).Json);

        var text = Schema("""{"type":"string","minLength":2,"maxLength":3,"pattern":"^[a-z]+$"}""");
        Assert.IsNull(CliStructuredOutput.Check("\"a\"", text).Json);
        Assert.IsNull(CliStructuredOutput.Check("\"abcd\"", text).Json);
        Assert.IsNull(CliStructuredOutput.Check("\"AB\"", text).Json);
        Assert.AreEqual("\"ab\"", CliStructuredOutput.Check("\"ab\"", text).Json);
        Assert.AreEqual("\"😀😀\"", CliStructuredOutput.Check("\"😀😀\"", Schema("""{"maxLength":2}""")).Json, "Lengths count characters, not UTF-16 units.");

        var list = Schema("""{"type":"array","items":{"type":"integer"},"minItems":1,"maxItems":2}""");
        Assert.IsNull(CliStructuredOutput.Check("[]", list).Json);
        Assert.IsNull(CliStructuredOutput.Check("[1,2,3]", list).Json);
        Assert.AreEqual("[1]", CliStructuredOutput.Check("[1]", list).Json);
    }

    [TestMethod]
    public void Load_RefusesTheKeywordsItDoesNotCheck_NamingThem()
    {
        using var root = new CliTestRoot();
        var path = Path.Combine(root.Root, "schema.json");
        File.WriteAllText(path, """
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "title": "A report",
              "type": "object",
              "properties": {
                "count": { "type": "integer", "description": "How many." },
                "kind": { "$ref": "#/$defs/kind" },
                "tags": { "type": "array", "items": { "allOf": [{ "type": "string" }] } }
              },
              "$defs": { "kind": { "enum": ["a", "b"] } }
            }
            """);

        var (_, error) = CliStructuredOutput.Load(path);

        Assert.IsNotNull(error);
        StringAssert.Contains(error, "$ref");
        StringAssert.Contains(error, "allOf");
        StringAssert.Contains(error, "$defs");
        Assert.IsFalse(error.Contains("description", StringComparison.Ordinal), "An annotation is not a constraint: " + error);
        Assert.IsFalse(error.Contains("title", StringComparison.Ordinal), error);
    }

    [TestMethod]
    public void Load_RefusesAnInvalidPattern()
    {
        using var root = new CliTestRoot();
        var path = Path.Combine(root.Root, "schema.json");
        File.WriteAllText(path, """{"type":"string","pattern":"("}""");

        var (_, error) = CliStructuredOutput.Load(path);

        Assert.IsNotNull(error);
        StringAssert.Contains(error, "pattern");
    }

    [TestMethod]
    public async Task Run_WithASchemaOfUncheckedKeywords_ExitsWithTwo_BeforeTheRun()
    {
        using var root = new CliTestRoot();
        var schema = Path.Combine(root.Root, "schema.json");
        File.WriteAllText(schema, """{"type":"object","properties":{"n":{"type":"integer"}},"oneOf":[{"required":["n"]}]}""");
        var provider = new FakeProvider();

        var run = await root.RunAsync(provider, ["run", "--no-plugins", "--format", "jsonl", "--output-schema", schema, "report"]);

        Assert.AreEqual(CliExitCodes.InvalidUsage, run.ExitCode, run.Stderr);
        StringAssert.Contains(run.Stderr, "oneOf");
        Assert.AreEqual("invalid_usage", CliRunTests.Result(run).GetProperty("status").GetString());
        Assert.AreEqual(0, provider.Requests.Count, "No prompt is sent.");
    }

    private static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
