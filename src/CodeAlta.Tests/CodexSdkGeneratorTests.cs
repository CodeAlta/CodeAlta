extern alias CodexSdkGenerator;

using NJsonSchema;
using CSharpEmitter = CodexSdkGenerator::CodeAlta.CodexSdk.Generator.CSharpEmitter;
using OutputDirectoryCleaner = CodexSdkGenerator::CodeAlta.CodexSdk.Generator.OutputDirectoryCleaner;
using SchemaWalker = CodexSdkGenerator::CodeAlta.CodexSdk.Generator.SchemaWalker;
using TypeDef = CodexSdkGenerator::CodeAlta.CodexSdk.Generator.TypeDef;

namespace CodeAlta.Tests;

[TestClass]
public sealed class CodexSdkGeneratorTests
{
    [TestMethod]
    public async Task OutputDirectoryCleaner_CanDeleteReadOnlyFiles()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"CodeAlta.Tests.{Guid.NewGuid():N}");
        var output = Path.Combine(root, "generated");
        Directory.CreateDirectory(output);

        var filePath = Path.Combine(output, "read-only.gen.cs");
        await File.WriteAllTextAsync(filePath, "// test").ConfigureAwait(false);
        File.SetAttributes(filePath, FileAttributes.ReadOnly);

        try
        {
            await OutputDirectoryCleaner.CleanAsync(output).ConfigureAwait(false);
            Assert.IsFalse(Directory.Exists(output));
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    [TestMethod]
    public async Task SchemaWalker_AddsAliasWhenRootRefTargetsOnlyExistInV2()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"CodeAlta.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var schemaPath = Path.Combine(root, "schema.json");

        await File.WriteAllTextAsync(
                schemaPath,
                """
                {
                  "$schema": "http://json-schema.org/draft-07/schema#",
                  "title": "Test",
                  "type": "object",
                  "definitions": {
                    "v2": {
                      "ThreadId": { "type": "string" }
                    },
                    "Foo": {
                      "type": "object",
                      "properties": {
                        "thread_id": { "$ref": "#/definitions/ThreadId" }
                      }
                    }
                  }
                }
                """)
            .ConfigureAwait(false);

        try
        {
            var defs = await SchemaWalker.LoadDefinitionsAsync(
                    schemaPath,
                    "CodeAlta.CodexSdk")
                .ConfigureAwait(false);

            Assert.IsTrue(defs.Any(x => x.CsNamespace == "CodeAlta.CodexSdk.V2" && x.Name == "ThreadId"));
            Assert.IsTrue(defs.Any(x => x.CsNamespace == "CodeAlta.CodexSdk" && x.Name == "ThreadId"));
            Assert.IsTrue(defs.Any(x => x.Name == "Foo"));
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    [TestMethod]
    public async Task SchemaWalker_FlatV2Bundle_UsesRootNamespaceForTypes()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"CodeAlta.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var schemaPath = Path.Combine(root, "codex_app_server_protocol.v2.schemas.json");

        await File.WriteAllTextAsync(
                schemaPath,
                """
                {
                  "$schema": "http://json-schema.org/draft-07/schema#",
                  "title": "Test",
                  "type": "object",
                  "definitions": {
                    "Foo": { "type": "string" },
                    "Bar": {
                      "type": "object",
                      "properties": {
                        "foo": { "$ref": "#/definitions/Foo" }
                      }
                    }
                  }
                }
                """)
            .ConfigureAwait(false);

        try
        {
            var defs = await SchemaWalker.LoadDefinitionsAsync(
                    schemaPath,
                    "CodeAlta.CodexSdk")
                .ConfigureAwait(false);

            Assert.IsTrue(defs.Any(x => x.CsNamespace == "CodeAlta.CodexSdk" && x.Name == "Foo"));
            Assert.IsTrue(defs.Any(x => x.CsNamespace == "CodeAlta.CodexSdk" && x.Name == "Bar"));
            Assert.IsFalse(defs.Any(x => x.CsNamespace == "CodeAlta.CodexSdk.V2"));
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    [TestMethod]
    public async Task SchemaWalker_FlatV2Bundle_MergesSupplementalFragments_WithoutLegacyServerRequests()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"CodeAlta.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var schemaPath = Path.Combine(root, "codex_app_server_protocol.v2.schemas.json");
        var v1Dir = Path.Combine(root, "v1");
        Directory.CreateDirectory(v1Dir);

        await File.WriteAllTextAsync(
                schemaPath,
                """
                {
                  "$schema": "http://json-schema.org/draft-07/schema#",
                  "title": "Test",
                  "type": "object",
                  "definitions": {
                    "Existing": { "type": "string" }
                  }
                }
                """)
            .ConfigureAwait(false);

        await File.WriteAllTextAsync(
                Path.Combine(v1Dir, "InitializeResponse.json"),
                """
                {
                  "$schema": "http://json-schema.org/draft-07/schema#",
                  "title": "InitializeResponse",
                  "type": "object",
                  "required": ["userAgent"],
                  "properties": {
                    "userAgent": { "type": "string" }
                  }
                }
                """)
            .ConfigureAwait(false);

        await File.WriteAllTextAsync(
                Path.Combine(root, "ServerRequest.json"),
                """
                {
                  "$schema": "http://json-schema.org/draft-07/schema#",
                  "title": "ServerRequest",
                  "oneOf": [
                    {
                      "type": "object",
                      "required": ["id", "method", "params"],
                      "properties": {
                        "id": { "$ref": "#/definitions/RequestId" },
                        "method": {
                          "type": "string",
                          "enum": ["item/tool/call"]
                        },
                        "params": { "$ref": "#/definitions/DynamicToolCallParams" }
                      }
                    },
                    {
                      "type": "object",
                      "required": ["id", "method", "params"],
                      "properties": {
                        "id": { "$ref": "#/definitions/RequestId" },
                        "method": {
                          "type": "string",
                          "enum": ["applyPatchApproval"]
                        },
                        "params": { "$ref": "#/definitions/ApplyPatchApprovalParams" }
                      }
                    },
                    {
                      "type": "object",
                      "required": ["id", "method", "params"],
                      "properties": {
                        "id": { "$ref": "#/definitions/RequestId" },
                        "method": {
                          "type": "string",
                          "enum": ["mcpServer/elicitation/request"]
                        },
                        "params": { "$ref": "#/definitions/McpServerElicitationRequestParams" }
                      }
                    }
                  ],
                  "definitions": {
                    "RequestId": {
                      "anyOf": [
                        { "type": "string" },
                        { "type": "integer", "format": "int64" }
                      ]
                    },
                    "DynamicToolCallParams": {
                      "type": "object",
                      "required": ["tool"],
                      "properties": {
                        "tool": { "type": "string" }
                      }
                    },
                    "ApplyPatchApprovalParams": {
                      "type": "object",
                      "required": ["legacy"],
                      "properties": {
                        "legacy": { "type": "boolean" }
                      }
                    },
                    "McpServerElicitationRequestParams": {
                      "type": "object",
                      "required": ["threadId"],
                      "properties": {
                        "threadId": { "type": "string" }
                      }
                    }
                  }
                }
                """)
            .ConfigureAwait(false);

        await File.WriteAllTextAsync(
                Path.Combine(root, "DynamicToolCallResponse.json"),
                """
                {
                  "$schema": "http://json-schema.org/draft-07/schema#",
                  "title": "DynamicToolCallResponse",
                  "type": "object",
                  "required": ["contentItems", "success"],
                  "properties": {
                    "contentItems": {
                      "type": "array",
                      "items": { "$ref": "#/definitions/DynamicToolCallOutputContentItem" }
                    },
                    "success": { "type": "boolean" }
                  },
                  "definitions": {
                    "DynamicToolCallOutputContentItem": {
                      "type": "object",
                      "required": ["type"],
                      "properties": {
                        "type": { "type": "string" }
                      }
                    }
                  }
                }
                """)
            .ConfigureAwait(false);

        try
        {
            var defs = await SchemaWalker.LoadDefinitionsAsync(
                    schemaPath,
                    "CodeAlta.CodexSdk")
                .ConfigureAwait(false);

            Assert.IsTrue(defs.Any(x => x.CsNamespace == "CodeAlta.CodexSdk" && x.Name == "InitializeResponse"));
            Assert.IsTrue(defs.Any(x => x.CsNamespace == "CodeAlta.CodexSdk" && x.Name == "ServerRequest"));
            Assert.IsTrue(defs.Any(x => x.CsNamespace == "CodeAlta.CodexSdk" && x.Name == "DynamicToolCallResponse"));
            Assert.IsTrue(defs.Any(x => x.CsNamespace == "CodeAlta.CodexSdk" && x.Name == "McpServerElicitationRequestParams"));
            Assert.IsFalse(defs.Any(x => x.Name == "ApplyPatchApprovalParams"));
            Assert.IsFalse(defs.Any(x => x.CsNamespace == "CodeAlta.CodexSdk.V2"));
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    [TestMethod]
    public async Task SchemaWalker_FlatV2Bundle_DiscoversAdditionalExtractedFragments()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"CodeAlta.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var schemaPath = Path.Combine(root, "codex_app_server_protocol.v2.schemas.json");
        var v2Dir = Path.Combine(root, "v2");
        Directory.CreateDirectory(v2Dir);

        await File.WriteAllTextAsync(
                schemaPath,
                """
                {
                  "$schema": "http://json-schema.org/draft-07/schema#",
                  "title": "CodexAppServerProtocolV2",
                  "type": "object",
                  "definitions": {
                    "Existing": { "type": "string" }
                  }
                }
                """)
            .ConfigureAwait(false);

        await File.WriteAllTextAsync(
                Path.Combine(root, "ClientNotification.json"),
                """
                {
                  "$schema": "http://json-schema.org/draft-07/schema#",
                  "title": "ClientNotification",
                  "oneOf": [
                    {
                      "type": "object",
                      "required": ["method"],
                      "properties": {
                        "method": {
                          "type": "string",
                          "enum": ["initialized"]
                        }
                      }
                    }
                  ]
                }
                """)
            .ConfigureAwait(false);

        await File.WriteAllTextAsync(
                Path.Combine(root, "FuzzyFileSearchResponse.json"),
                """
                {
                  "$schema": "http://json-schema.org/draft-07/schema#",
                  "title": "FuzzyFileSearchResponse",
                  "type": "object",
                  "required": ["files"],
                  "properties": {
                    "files": {
                      "type": "array",
                      "items": { "$ref": "#/definitions/FuzzyFileSearchResult" }
                    }
                  },
                  "definitions": {
                    "FuzzyFileSearchResult": {
                      "type": "object",
                      "required": ["path"],
                      "properties": {
                        "path": { "type": "string" }
                      }
                    }
                  }
                }
                """)
            .ConfigureAwait(false);

        await File.WriteAllTextAsync(
                Path.Combine(v2Dir, "SkillsRemoteReadParams.json"),
                """
                {
                  "$schema": "http://json-schema.org/draft-07/schema#",
                  "title": "SkillsRemoteReadParams",
                  "type": "object",
                  "properties": {
                    "enabled": { "type": "boolean" }
                  }
                }
                """)
            .ConfigureAwait(false);

        try
        {
            var defs = await SchemaWalker.LoadDefinitionsAsync(
                    schemaPath,
                    "CodeAlta.CodexSdk")
                .ConfigureAwait(false);

            Assert.IsTrue(defs.Any(x => x.CsNamespace == "CodeAlta.CodexSdk" && x.Name == "Existing"));
            Assert.IsTrue(defs.Any(x => x.CsNamespace == "CodeAlta.CodexSdk" && x.Name == "ClientNotification"));
            Assert.IsTrue(defs.Any(x => x.CsNamespace == "CodeAlta.CodexSdk" && x.Name == "FuzzyFileSearchResponse"));
            Assert.IsTrue(defs.Any(x => x.CsNamespace == "CodeAlta.CodexSdk" && x.Name == "FuzzyFileSearchResult"));
            Assert.IsTrue(defs.Any(x => x.CsNamespace == "CodeAlta.CodexSdk" && x.Name == "SkillsRemoteReadParams"));
            Assert.IsFalse(defs.Any(x => x.CsNamespace == "CodeAlta.CodexSdk.V2"));
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    [TestMethod]
    public void EmitSerializerContext_DictionaryWithNullablePrimitive_UsesPrimitiveTypeAndSanitizedPropertyName()
    {
        var schema = new JsonSchema();
        var defs = new List<TypeDef>
        {
            new(
                "Dummy",
                "CodeAlta.CodexSdk",
                schema,
                "#/definitions/Dummy"),
        };

        var emitter = new CSharpEmitter(defs, "CodeAlta.CodexSdk");
        var trackCollectionType = typeof(CSharpEmitter).GetMethod("TrackCollectionType", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.IsNotNull(trackCollectionType);

        trackCollectionType.Invoke(emitter, ["Dictionary<string, string?>", "CodeAlta.CodexSdk"]);

        var contextCode = emitter.EmitSerializerContext("CodexJsonSerializerContext");

        StringAssert.Contains(
            contextCode,
            "[JsonSerializable(typeof(Dictionary<string, string?>), TypeInfoPropertyName = \"DictionarystringstringNullable\")]");
        Assert.IsFalse(contextCode.Contains("CodeAlta.CodexSdk.string?", StringComparison.Ordinal));
        Assert.IsFalse(contextCode.Contains("Dictionarystringstring?", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task EmitType_TaggedUnionWithSharedProperties_EmitsBaseProperties()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"CodeAlta.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var schemaPath = Path.Combine(root, "schema.json");

        await File.WriteAllTextAsync(
                schemaPath,
                """
                {
                  "$schema": "http://json-schema.org/draft-07/schema#",
                  "title": "Test",
                  "type": "object",
                  "definitions": {
                    "McpElicitationSchema": {
                      "type": "object",
                      "properties": {
                        "type": { "type": "string" },
                        "properties": {
                          "type": "object",
                          "additionalProperties": true
                        }
                      }
                    },
                    "McpServerElicitationRequestParams": {
                      "type": "object",
                      "required": ["serverName", "threadId"],
                      "properties": {
                        "serverName": { "type": "string" },
                        "threadId": { "type": "string" },
                        "turnId": { "type": ["string", "null"] }
                      },
                      "oneOf": [
                        {
                          "type": "object",
                          "required": ["message", "mode", "requestedSchema"],
                          "properties": {
                            "mode": { "type": "string", "enum": ["form"] },
                            "message": { "type": "string" },
                            "requestedSchema": { "$ref": "#/definitions/McpElicitationSchema" }
                          }
                        },
                        {
                          "type": "object",
                          "required": ["elicitationId", "message", "mode", "url"],
                          "properties": {
                            "mode": { "type": "string", "enum": ["url"] },
                            "elicitationId": { "type": "string" },
                            "message": { "type": "string" },
                            "url": { "type": "string" }
                          }
                        }
                      ]
                    }
                  }
                }
                """)
            .ConfigureAwait(false);

        try
        {
            var defs = await SchemaWalker.LoadDefinitionsAsync(
                    schemaPath,
                    "CodeAlta.CodexSdk")
                .ConfigureAwait(false);
            var def = defs.Single(static x => x.Name == "McpServerElicitationRequestParams");
            var emitter = new CSharpEmitter(defs, "CodeAlta.CodexSdk");

            var code = emitter.EmitType(def);

            Assert.IsNotNull(code);
            StringAssert.Contains(code, "public string ServerName { get; set; } = string.Empty;");
            StringAssert.Contains(code, "public string ThreadId { get; set; } = string.Empty;");
            StringAssert.Contains(code, "public string? TurnId { get; set; }");
            StringAssert.Contains(code, "public sealed partial record Form : McpServerElicitationRequestParams");
            StringAssert.Contains(code, "public sealed partial record Url : McpServerElicitationRequestParams");
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    [TestMethod]
    public async Task EmitType_AnyOfWithRefs_EmitsRawJsonConverterWrapper()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"CodeAlta.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var schemaPath = Path.Combine(root, "schema.json");

        await File.WriteAllTextAsync(
                schemaPath,
                """
                {
                  "$schema": "http://json-schema.org/draft-07/schema#",
                  "title": "Test",
                  "type": "object",
                  "definitions": {
                    "A": {
                      "type": "object",
                      "properties": {
                        "kind": { "type": "string" },
                        "value": { "type": "string" }
                      }
                    },
                    "B": {
                      "type": "object",
                      "properties": {
                        "items": {
                          "type": "array",
                          "items": { "type": "string" }
                        },
                        "title": { "type": "string" }
                      }
                    },
                    "Primitive": {
                      "anyOf": [
                        { "$ref": "#/definitions/A" },
                        { "$ref": "#/definitions/B" }
                      ]
                    }
                  }
                }
                """)
            .ConfigureAwait(false);

        try
        {
            var defs = await SchemaWalker.LoadDefinitionsAsync(
                    schemaPath,
                    "CodeAlta.CodexSdk")
                .ConfigureAwait(false);
            var def = defs.Single(static x => x.Name == "Primitive");
            var emitter = new CSharpEmitter(defs, "CodeAlta.CodexSdk");

            var code = emitter.EmitType(def);

            Assert.IsNotNull(code);
            StringAssert.Contains(code, "JsonConverter(typeof(PrimitiveJsonConverter))");
            StringAssert.Contains(code, "public partial record struct Primitive");
            StringAssert.Contains(code, "public JsonElement Value { get; set; }");
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    [TestMethod]
    public async Task EmitType_ReferencedUnionProperty_UsesNamedReferencedType()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"CodeAlta.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var schemaPath = Path.Combine(root, "schema.json");

        await File.WriteAllTextAsync(
                schemaPath,
                """
                {
                  "$schema": "http://json-schema.org/draft-07/schema#",
                  "title": "Test",
                  "type": "object",
                  "definitions": {
                    "AgentMessageContent": {
                      "oneOf": [
                        {
                          "type": "object",
                          "required": ["text", "type"],
                          "properties": {
                            "text": { "type": "string" },
                            "type": {
                              "type": "string",
                              "enum": ["text"]
                            }
                          },
                          "title": "TextAgentMessageContent"
                        }
                      ]
                    },
                    "TurnItem": {
                      "type": "object",
                      "required": ["content"],
                      "properties": {
                        "content": {
                          "type": "array",
                          "items": { "$ref": "#/definitions/AgentMessageContent" }
                        }
                      }
                    }
                  }
                }
                """)
            .ConfigureAwait(false);

        try
        {
            var defs = await SchemaWalker.LoadDefinitionsAsync(
                    schemaPath,
                    "CodeAlta.CodexSdk")
                .ConfigureAwait(false);
            var def = defs.Single(static x => x.Name == "TurnItem");
            var emitter = new CSharpEmitter(defs, "CodeAlta.CodexSdk");

            var code = emitter.EmitType(def);

            Assert.IsNotNull(code);
            StringAssert.Contains(code, "public List<AgentMessageContent> Content { get; set; } = [];");
            Assert.IsFalse(code.Contains("List<JsonElement>", StringComparison.Ordinal));
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}
