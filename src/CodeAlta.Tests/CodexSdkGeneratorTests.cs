using CodeAlta.CodexSdk.Generator;

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
}
