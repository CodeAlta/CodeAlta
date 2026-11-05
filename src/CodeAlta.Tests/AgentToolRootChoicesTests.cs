using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeAlta.Agent;
using CodeAlta.Agent.Anthropic;
using CodeAlta.Agent.Runtime.Tools;

namespace CodeAlta.Tests;

// The Anthropic API refuses a tool whose input_schema has anyOf, oneOf or allOf at its root, as Copilot does with an HTTP
// 400 without detail (normalizeToolSchema of vscode-copilot-chat removes them for every model), and Claude Code merges
// such a schema or skips its tool ("the Anthropic API does not accept"). An MCP server may declare one: one such tool
// would fail every turn of a session. Its branches are merged into one object that keeps every argument.
[TestClass]
public sealed class AgentToolRootChoicesTests
{
    internal const string ChoiceSchema =
        """
        {
          "type": "object",
          "description": "Finds an issue.",
          "anyOf": [
            { "type": "object", "properties": { "id": { "type": "string" }, "kind": { "const": "id" } }, "required": ["id", "kind"] },
            { "$ref": "#/$defs/byKey" }
          ],
          "$defs": {
            "byKey": {
              "type": "object",
              "properties": { "key": { "type": "string" }, "kind": { "const": "key" } },
              "required": ["key", "kind"],
              "additionalProperties": false
            }
          }
        }
        """;

    // What a merged ChoiceSchema must be wherever it is sent.
    internal static void AssertMergedChoice(JsonElement schema)
    {
        var text = schema.GetRawText();
        foreach (var keyword in new[] { "anyOf", "oneOf", "allOf" })
        {
            Assert.IsFalse(schema.TryGetProperty(keyword, out _), text);
        }

        Assert.AreEqual("object", schema.GetProperty("type").GetString(), text);
        var properties = schema.GetProperty("properties");
        CollectionAssert.AreEquivalent(new[] { "id", "kind", "key" }, properties.EnumerateObject().Select(static property => property.Name).ToArray(), text);
        Assert.AreEqual("string", properties.GetProperty("key").GetProperty("type").GetString(), text);
        // The two branches give the property two definitions: the merged one takes either value.
        CollectionAssert.AreEqual(
            new[] { "id", "key" },
            properties.GetProperty("kind").GetProperty("anyOf").EnumerateArray().Select(static branch => branch.GetProperty("const").GetString()).ToArray(),
            text);
        // Only what every branch requires is required.
        CollectionAssert.AreEqual(new[] { "kind" }, schema.GetProperty("required").EnumerateArray().Select(static name => name.GetString()).ToArray(), text);
        var description = schema.GetProperty("description").GetString()!;
        StringAssert.StartsWith(description, "Finds an issue.", text);
        StringAssert.Contains(description, "(id, kind) or (key, kind)", text);
    }

    [TestMethod]
    public void AChoiceAtTheRoot_IsMergedIntoOneObject()
    {
        using var schema = JsonDocument.Parse(ChoiceSchema);

        Assert.IsTrue(AgentToolRootChoices.TryMerge(schema.RootElement, out var merged));

        AssertMergedChoice(merged);
        // The definitions stay for the references inside the properties.
        Assert.IsTrue(merged.TryGetProperty("$defs", out _));
        // The root stays open: a branch that was closed only closed its own choice.
        Assert.IsFalse(merged.TryGetProperty("additionalProperties", out _));
    }

    [TestMethod]
    public void AllOf_KeepsWhatEachPartRequires_AndTheRootItsOwnProperties()
    {
        using var schema = JsonDocument.Parse(
            """
            {
              "type": "object",
              "properties": { "path": { "type": "string" } },
              "required": ["path"],
              "additionalProperties": false,
              "allOf": [
                { "properties": { "line": { "type": "integer" } }, "required": ["line"] },
                { "properties": { "path": { "type": "string", "minLength": 1 }, "column": { "type": "integer" } } },
                { "required": ["column"] }
              ]
            }
            """);

        Assert.IsTrue(AgentToolRootChoices.TryMerge(schema.RootElement, out var merged));

        Assert.IsFalse(merged.TryGetProperty("allOf", out _));
        var properties = merged.GetProperty("properties");
        CollectionAssert.AreEqual(new[] { "path", "line", "column" }, properties.EnumerateObject().Select(static property => property.Name).ToArray());
        // The root's own definition always applies: it is kept as the root wrote it.
        Assert.AreEqual("""{"type":"string"}""", properties.GetProperty("path").GetRawText());
        CollectionAssert.AreEquivalent(new[] { "path", "line", "column" }, merged.GetProperty("required").EnumerateArray().Select(static name => name.GetString()).ToArray());
        Assert.IsFalse(merged.GetProperty("additionalProperties").GetBoolean());
        Assert.IsFalse(merged.TryGetProperty("description", out _), "Every part applies: there is no choice to describe.");
    }

    [TestMethod]
    public void OneOf_WithoutType_AndNestedChoices_KeepsEveryArgument()
    {
        using var schema = JsonDocument.Parse(
            """
            {
              "oneOf": [
                { "properties": { "url": { "type": "string" } }, "required": ["url"] },
                { "anyOf": [
                    { "properties": { "file": { "type": "string" } }, "required": ["file", "encoding"] },
                    { "properties": { "data": { "type": "string" } }, "required": ["data", "encoding"] }
                  ],
                  "properties": { "encoding": { "type": "string" } } }
              ]
            }
            """);

        Assert.IsTrue(AgentToolRootChoices.TryMerge(schema.RootElement, out var merged));

        Assert.AreEqual("object", merged.GetProperty("type").GetString());
        CollectionAssert.AreEquivalent(
            new[] { "url", "file", "data", "encoding" },
            merged.GetProperty("properties").EnumerateObject().Select(static property => property.Name).ToArray());
        Assert.IsFalse(merged.TryGetProperty("required", out _), "No name is required by both choices.");
        StringAssert.Contains(merged.GetProperty("description").GetString(), "exactly one of");
    }

    [TestMethod]
    public void ASchemaWithoutAChoiceAtItsRoot_IsLeftAsItIs()
    {
        using var schema = JsonDocument.Parse(
            """{"type":"object","properties":{"value":{"anyOf":[{"type":"string"},{"type":"integer"}]}}}""");

        Assert.IsFalse(AgentToolRootChoices.TryMerge(schema.RootElement, out var merged));
        Assert.AreEqual(schema.RootElement.GetRawText(), merged.GetRawText());
    }

    [TestMethod]
    public async Task Anthropic_SendsAChoiceAtTheRootMerged()
    {
        using var schema = JsonDocument.Parse(ChoiceSchema);
        var tools = new[]
        {
            new AgentToolDefinition(
                new AgentToolSpec("mcp__tracker__findIssue", "Finds an issue.", schema.RootElement.Clone()),
                static (_, _) => throw new InvalidOperationException("The tool is not called.")),
        };
        var bodies = new List<JsonNode>();
        var stream = AnthropicStopReasonTests.Stream(
            AnthropicStopReasonTests.TextBlock(0, "Done."),
            """{"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":1}}""");
        var executor = AnthropicModelProviderRuntime.CreateTurnExecutor(new AnthropicProviderOptions
        {
            ProviderKey = "anthropic",
            ApiKey = "test-key",
            BaseUri = new Uri("https://api.anthropic.test"),
            HttpClient = new HttpClient(new CapturingHandler(bodies, stream)),
        });

        _ = await executor.ExecuteTurnAsync(AnthropicStopReasonTests.CreateRequest(tools), static (_, _) => ValueTask.CompletedTask).ConfigureAwait(false);

        var tool = bodies.Single()["tools"]!.AsArray().Single()!;
        Assert.AreEqual("mcp__tracker__findIssue", (string?)tool["name"]);
        using var sent = JsonDocument.Parse(tool["input_schema"]!.ToJsonString());
        AssertMergedChoice(sent.RootElement);
    }

    private sealed class CapturingHandler(List<JsonNode> bodies, string stream) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            bodies.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false))!);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(stream, Encoding.UTF8, "text/event-stream") };
        }
    }
}
