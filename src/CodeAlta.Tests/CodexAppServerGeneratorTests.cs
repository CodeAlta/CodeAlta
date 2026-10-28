using System.Text.Json.Nodes;
using CodeAlta.CodexAppServer;
using CodeAlta.CodexAppServer.Generator;

namespace CodeAlta.Tests;

[TestClass]
public sealed class CodexAppServerGeneratorTests
{
    [TestMethod]
    public async Task Generator_KeepsOnlyTheTypesTheMethodsReach()
    {
        using var temp = TestTempDirectory.Create();
        var (schema, map) = WriteSchema(temp.Path);

        var methods = CodexMethodMap.Load(map, schema);
        var definitions = await SchemaWalker.LoadDefinitionsAsync(Path.Combine(schema, "codex_app_server_protocol.v2.schemas.json"), "Test.Protocol", methods.RootTypes);

        var names = definitions.Select(static definition => definition.Name).ToHashSet(StringComparer.Ordinal);
        CollectionAssert.IsSubsetOf(new[] { "DoParams", "DoResponse", "Kind", "Item", "NoteNotification" }, names.ToArray());
        Assert.IsFalse(names.Contains("Unused"), "A type no method reaches is left out.");
    }

    [TestMethod]
    public async Task Generator_EmitsTolerantEnumsAndUnions()
    {
        using var temp = TestTempDirectory.Create();
        var (schema, map) = WriteSchema(temp.Path);
        var methods = CodexMethodMap.Load(map, schema);
        var definitions = await SchemaWalker.LoadDefinitionsAsync(Path.Combine(schema, "codex_app_server_protocol.v2.schemas.json"), "Test.Protocol", methods.RootTypes);

        var files = new CSharpEmitter(definitions, "Test.Protocol").EmitAll(definitions)["Test.Protocol"].ToDictionary(static file => file.FileName, static file => file.Content);

        var kind = files["Kind.gen.cs"];
        StringAssert.Contains(kind, "UnrecognizedValue = -1");
        StringAssert.Contains(kind, "\"b-c\" => Kind.BC");
        StringAssert.Contains(kind, "_ => Kind.UnrecognizedValue");
        var item = files["Item.gen.cs"];
        StringAssert.Contains(item, "IgnoreUnrecognizedTypeDiscriminators = true");
        StringAssert.Contains(item, "public partial record Item");
        Assert.IsFalse(item.Contains("abstract", StringComparison.Ordinal), "An unknown kind reads as the base type, which must not be abstract.");
        // "openai/form" and "openaiForm" make the same name: the second is numbered.
        StringAssert.Contains(item, "typeof(OpenaiForm), typeDiscriminator: \"openai/form\"");
        StringAssert.Contains(item, "typeof(OpenaiForm2), typeDiscriminator: \"openaiForm\"");
    }

    [TestMethod]
    public void MethodEmitter_NamesTheMethodsAndParsesTheNotifications()
    {
        using var temp = TestTempDirectory.Create();
        var (schema, map) = WriteSchema(temp.Path);
        var methods = CodexMethodMap.Load(map, schema);

        var client = CodexMethodEmitter.EmitClientMethods(methods, "Test", "Test.Protocol", "TestContext");
        var messages = CodexMethodEmitter.EmitServerMessages(methods, "Test", "Test.Protocol", "TestContext");

        StringAssert.Contains(client, "public Task<DoResponse> XDoItAsync(DoParams parameters, CancellationToken cancellationToken = default)");
        StringAssert.Contains(client, "SendRequestAsync(\"x/do_it\", parameters, TestContext.Default.DoParams, TestContext.Default.DoResponse, cancellationToken)");
        StringAssert.Contains(messages, "public const string XNote = \"x/note\";");
        StringAssert.Contains(messages, "\"x/note\" => parameters.Deserialize(TestContext.Default.NoteNotification),");
        Assert.AreEqual("AccountRateLimitsRead", CodexMethodEmitter.ToMethodName("account/rateLimits/read"));
    }

    [TestMethod]
    public void MethodMap_FailsOnAMethodTheSchemaNoLongerHas()
    {
        using var temp = TestTempDirectory.Create();
        var (schema, map) = WriteSchema(temp.Path);
        var json = JsonNode.Parse(File.ReadAllText(map))!;
        json["requests"]!["x/gone"] = "DoResponse";
        File.WriteAllText(map, json.ToJsonString());

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => CodexMethodMap.Load(map, schema));
        StringAssert.Contains(exception.Message, "x/gone");
    }

    [TestMethod]
    public void CheckedInTypes_ComeFromThePinnedRelease()
    {
        // The generated protocol and the downloaded app-server must be the same release: bump them together.
        // Read at run time: the generated constants would otherwise be folded into the assertion.
        string? Constant(string name) => typeof(CodexAppServerProtocol).GetField(name)!.GetRawConstantValue() as string;
        Assert.AreEqual(CodexPinnedRelease.Tag, Constant(nameof(CodexAppServerProtocol.SourceRef)));
        Assert.AreEqual(40, Constant(nameof(CodexAppServerProtocol.SourceCommit))?.Length);
    }

    [TestMethod]
    public void CheckedInMethodMap_NamesEveryMethodTheClientParses()
    {
        var map = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "codex-app-server-methods.json")))!;
        foreach (var notification in map["notifications"]!.AsArray())
        {
            var method = notification!.GetValue<string>();
            Assert.IsNotNull(typeof(CodexMethods).GetField(CodexMethodEmitter.ToMethodName(method)), method);
        }
    }

    // A small schema in the layout of the one Codex checks in: a v2 bundle, and the lists of the messages.
    private static (string SchemaDirectory, string MapFile) WriteSchema(string root)
    {
        var schema = Path.Combine(root, "schema");
        Directory.CreateDirectory(schema);
        var bundle = new JsonObject
        {
            ["$schema"] = "http://json-schema.org/draft-07/schema#",
            ["title"] = "CodexAppServerProtocolV2",
            ["type"] = "object",
            ["definitions"] = new JsonObject
            {
                ["DoParams"] = Object(("kind", Ref("Kind")), ("item", Ref("Item"))),
                ["DoResponse"] = Object(("ok", new JsonObject { ["type"] = "boolean" })),
                ["NoteNotification"] = Object(("text", new JsonObject { ["type"] = "string" })),
                ["Kind"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("a", "b-c") },
                ["Item"] = new JsonObject
                {
                    ["oneOf"] = new JsonArray(Variant("openai/form"), Variant("openaiForm"), Variant("plain")),
                },
                ["Unused"] = Object(("x", new JsonObject { ["type"] = "string" })),
            },
        };
        File.WriteAllText(Path.Combine(schema, "codex_app_server_protocol.v2.schemas.json"), bundle.ToJsonString());
        File.WriteAllText(Path.Combine(schema, "ClientRequest.json"), MessageList("ClientRequest", ("x/do_it", Ref("DoParams"))).ToJsonString());
        File.WriteAllText(Path.Combine(schema, "ServerNotification.json"), MessageList("ServerNotification", ("x/note", Ref("NoteNotification"))).ToJsonString());
        File.WriteAllText(Path.Combine(schema, "ServerRequest.json"), MessageList("ServerRequest").ToJsonString());
        var map = Path.Combine(root, "methods.json");
        File.WriteAllText(map, new JsonObject
        {
            ["requests"] = new JsonObject { ["x/do_it"] = "DoResponse" },
            ["notifications"] = new JsonArray("x/note"),
            ["serverRequests"] = new JsonObject(),
        }.ToJsonString());
        return (schema, map);
    }

    private static JsonObject Ref(string name) => new() { ["$ref"] = $"#/definitions/{name}" };

    private static JsonObject Object(params (string Name, JsonObject Schema)[] properties)
    {
        var props = new JsonObject();
        foreach (var (name, schema) in properties)
        {
            props[name] = schema;
        }

        return new JsonObject { ["type"] = "object", ["properties"] = props, ["required"] = new JsonArray([.. properties.Select(static property => (JsonNode)property.Name)]) };
    }

    private static JsonObject Variant(string tag) => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["type"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(tag) },
            ["id"] = new JsonObject { ["type"] = "string" },
        },
        ["required"] = new JsonArray("type", "id"),
    };

    private static JsonObject MessageList(string title, params (string Method, JsonObject Params)[] methods)
    {
        var variants = new JsonArray();
        foreach (var (method, parameters) in methods)
        {
            variants.Add(new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["method"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(method) },
                    ["params"] = parameters,
                },
                ["required"] = new JsonArray("method"),
            });
        }

        return new JsonObject { ["$schema"] = "http://json-schema.org/draft-07/schema#", ["title"] = title, ["oneOf"] = variants };
    }
}
