using CodeAlta.CodeMode;

namespace CodeAlta.CodeMode.Tests;

[TestClass]
public sealed class CodeModeDeclarationTests
{
    [TestMethod]
    public void Schema_RendersRequiredOptionalEnumsAndTags()
    {
        var type = CodeModeDeclarations.RenderType("""
            {
              "type": "object",
              "properties": {
                "path": { "type": "string", "description": "File to read" },
                "offset": { "type": "integer", "minimum": 1 },
                "mode": { "enum": ["fast", "full"] },
                "tags": { "type": "array", "items": { "type": "string" } }
              },
              "required": ["path"]
            }
            """);

        StringAssert.Contains(type, "/** File to read */");
        StringAssert.Contains(type, "path: string;");
        StringAssert.Contains(type, "offset?: number; // @integer @minimum 1");
        StringAssert.Contains(type, "mode?: \"fast\" | \"full\";");
        StringAssert.Contains(type, "tags?: string[];");
    }

    [TestMethod]
    public void RecursiveReferences_AreBounded()
    {
        var type = CodeModeDeclarations.RenderType("""
            { "$defs": { "node": { "type": "object", "properties": { "children": { "type": "array", "items": { "$ref": "#/$defs/node" } } } } },
              "$ref": "#/$defs/node" }
            """);

        StringAssert.Contains(type, "unknown");
        Assert.IsLessThan(CodeModeDeclarations.MaxSchemaCharacters, type.Length);
    }

    [TestMethod]
    public void Api_RendersNamespaces()
    {
        var api = new CodeModeApi(
        [
            new CodeModeFunction("tools.read_file") { Description = "Read a file.", InputSchemaJson = """{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}""", ResultType = "string" },
            new CodeModeFunction("alta.session.create") { Description = "Create a session.", Annotations = ["mutating"], ResultType = "SessionCreated" },
            new CodeModeFunction("mcp.my-server.list") ,
        ]);

        StringAssert.Contains(api.Declarations, "declare const tools: {");
        StringAssert.Contains(api.Declarations, "read_file(args: {");
        StringAssert.Contains(api.Declarations, "}): Promise<string>;");
        StringAssert.Contains(api.Declarations, "/** Create a session. @mutating */");
        StringAssert.Contains(api.Declarations, "\"my-server\": {");
    }

    [TestMethod]
    public void Api_RefusesReservedNamesAndClashes()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new CodeModeApi([new CodeModeFunction("console.log")]));
        Assert.ThrowsExactly<ArgumentException>(() => new CodeModeApi([new CodeModeFunction("tools")]));
        Assert.ThrowsExactly<ArgumentException>(() => new CodeModeApi([new CodeModeFunction("tools.a"), new CodeModeFunction("tools.a.b")]));
    }

    [TestMethod]
    public void Names_BecomeIdentifiers()
    {
        Assert.AreEqual("my_tool", CodeModeNames.ToIdentifier("my-tool"));
        Assert.AreEqual("_2fa", CodeModeNames.ToIdentifier("2fa"));
        Assert.AreEqual("setAgent", CodeModeNames.ToCamelCase("set_agent"));
        Assert.AreEqual("noParent", CodeModeNames.ToCamelCase("no-parent"));
    }

    [TestMethod]
    public void JsonTruncation_KeepsValidJsonAndItsShape()
    {
        var json = "{\"items\":[" + string.Join(",", Enumerable.Range(0, 500).Select(i => $"{{\"id\":{i},\"text\":\"{new string('x', 200)}\"}}")) + "],\"total\":500}";

        var cut = CodeModeJsonTruncation.Truncate(json, 4_000);

        Assert.IsLessThanOrEqualTo(4_000, cut.Length);
        using var document = System.Text.Json.JsonDocument.Parse(cut);
        Assert.AreEqual(500, document.RootElement.GetProperty("total").GetInt32());
        StringAssert.Contains(cut, "more items cut");
    }
}
