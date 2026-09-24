using System.Text.Json;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class McpInventoryRpcTests
{
    [TestMethod]
    public async Task ScopedOverlayOnlyExposesSafeInventoryAndNoRuntimeClaims()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-mcp-inventory-" + Guid.NewGuid().ToString("N"));
        try
        {
            var home = Path.Combine(root, "home");
            var project = Path.Combine(root, "project");
            Write(home, "mcp.json", """
                {"mcpServers":{"shared":{"command":"GLOBAL_SECRET","args":["ARG_SECRET"]},"global-only":{"command":"SAFE_SECRET"},
                "https://SECRET_KEY.invalid/?token=private":{"command":"SHOULD_NOT_SHOW"}}}
                """);
            Write(project, "mcp.json", """
                {"servers":{"shared":{"type":"stdio","command":"PROJECT_SECRET","env":{"KEY":"ENV_SECRET"}},
                "remote":{"type":"sse","url":"https://URL_SECRET/?token=SECRET","headers":{"Authorization":"HEADER_SECRET"}}}}
                """);
            Write(project, "config.toml", "[plugins.mcp.servers.remote]\nenabled = false\n");
            var calls = 0;
            var service = new McpInventoryService((id, _) => {
                calls++;
                return Task.FromResult<OwnedMcpScope?>(id switch {
                    "one" => new(project, "project-id"), "two" => new(null, null), _ => null,
                });
            }, "epoch", home);
            Assert.AreEqual("stale_epoch", (await service.List(new("old", "one"), CancellationToken.None)).Status);
            Assert.AreEqual("invalid_request", (await service.List(new("epoch", " one"), CancellationToken.None)).Status);
            Assert.AreEqual(0, calls);
            var response = await service.List(new("epoch", "one"), CancellationToken.None);
            Assert.AreEqual("ok", response.Status);
            Assert.AreEqual("project-id", response.ProjectId);
            Assert.HasCount(3, response.Servers);
            Assert.AreEqual(1, response.Omitted);
            Assert.IsTrue(response.Servers.Single(s => s.Name == "shared").OverridesGlobal);
            Assert.AreEqual("Project", response.Servers.Single(s => s.Name == "shared").Scope);
            Assert.AreEqual(false, response.Servers.Single(s => s.Name == "remote").Enabled);
            Assert.AreEqual("Http", response.Servers.Single(s => s.Name == "remote").Transport);
            var json = JsonSerializer.Serialize(response, DesktopJsonContext.Default.McpInventoryResponse);
            foreach (var secret in new[] { root, "GLOBAL_SECRET", "ARG_SECRET", "SAFE_SECRET", "PROJECT_SECRET", "ENV_SECRET", "URL_SECRET", "HEADER_SECRET", "SECRET_KEY", "SHOULD_NOT_SHOW" })
                Assert.IsFalse(json.Contains(secret, StringComparison.Ordinal), secret);
            Assert.IsFalse(Directory.EnumerateFiles(Path.Combine(home, ".alta")).Any(f => Path.GetFileName(f).Contains("write-test")));
            var global = await service.List(new("epoch", "two"), CancellationToken.None);
            Assert.IsNull(global.ProjectId);
            Assert.HasCount(2, global.Servers);
            Assert.IsTrue(global.Servers.All(s => s.Scope == "Global"));
            Assert.AreEqual("unavailable", (await service.List(new("epoch", "missing"), CancellationToken.None)).Status);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task MalformedScopeAndPolicyRemainExplicitWithoutDiagnostics()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-mcp-inventory-" + Guid.NewGuid().ToString("N"));
        try
        {
            var home = Path.Combine(root, "home");
            var project = Path.Combine(root, "project");
            Write(home, "mcp.json", "{\"mcpServers\":{\"safe\":{\"command\":\"TOKEN_SECRET\"}}}");
            Write(project, "mcp.json", "{ malformed SECRET_CONFIG");
            Write(project, "config.toml", "[plugins.mcp\nSECRET_POLICY");
            var service = new McpInventoryService((_, _) => Task.FromResult<OwnedMcpScope?>(new(project, "id")), "epoch", home);
            var response = await service.List(new("epoch", "one"), CancellationToken.None);
            Assert.AreEqual("ok", response.Status);
            CollectionAssert.Contains(response.Sources.ToArray(), "Project: read_error");
            Assert.IsTrue(response.PolicyReadError);
            Assert.HasCount(1, response.Servers);
            Assert.IsNull(response.Servers[0].Enabled);
            var json = JsonSerializer.Serialize(response, DesktopJsonContext.Default.McpInventoryResponse);
            Assert.IsFalse(json.Contains("SECRET_", StringComparison.Ordinal));
            Assert.IsFalse(json.Contains(root, StringComparison.Ordinal));
            Write(project, "mcp.json", new string(' ', 1024 * 1024 + 1) + "SECRET_OVERSIZE");
            var oversized = await service.List(new("epoch", "one"), CancellationToken.None);
            CollectionAssert.Contains(oversized.Sources.ToArray(), "Project: read_error");
            Assert.HasCount(1, oversized.Servers);
            var fault = new McpInventoryService((_, _) => throw new IOException("SECRET_FAILURE"), "epoch", home);
            Assert.AreEqual("read_failed", (await fault.List(new("epoch", "one"), CancellationToken.None)).Status);
            Assert.AreEqual("unconfigured", (await new McpInventoryService().List(new("epoch", "one"), CancellationToken.None)).Status);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void Write(string directory, string name, string content)
    {
        var path = Path.Combine(directory, ".alta", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
