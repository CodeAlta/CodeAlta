using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Orchestration.Hosting;
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

    [TestMethod]
    public async Task OversizedPolicyInEitherScopeIsUnknownAndNormalOverlayMatchesRuntimePolicy()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-mcp-policy-" + Guid.NewGuid().ToString("N"));
        try
        {
            var home = Path.Combine(root, "home");
            var project = Path.Combine(root, "project");
            Write(home, "mcp.json", """
                {"mcpServers":{"shared":{"command":"SECRET_COMMAND"},"global":{"command":"SECRET_GLOBAL"}}}
                """);
            Write(project, "mcp.json", """
                {"mcpServers":{"shared":{"command":"SECRET_OVERRIDE"},"local":{"command":"SECRET_LOCAL"}}}
                """);
            Write(home, "config.toml", "[plugins.mcp]\nenabled = false\n[plugins.mcp.servers.shared]\nenabled = true\n");
            var service = new McpInventoryService((_, _) => Task.FromResult<OwnedMcpScope?>(new(project, "project")), "epoch", home);
            var globalOnly = new McpInventoryService((_, _) => Task.FromResult<OwnedMcpScope?>(new(null, null)), "epoch", home);
            var disabled = await globalOnly.List(new("epoch", "global-session"), CancellationToken.None);
            Assert.AreEqual(false, disabled.Servers.Single(s => s.Name == "shared").Enabled,
                "A server-local true cannot override globally disabled MCP policy.");
            Write(project, "config.toml", "[plugins.mcp]\nenabled = true\n[plugins.mcp.servers.global]\nenabled = false\n");
            var overlay = await service.List(new("epoch", "project-session"), CancellationToken.None);
            Assert.IsFalse(overlay.PolicyReadError);
            Assert.AreEqual(true, overlay.Servers.Single(s => s.Name == "shared").Enabled);
            Assert.AreEqual(false, overlay.Servers.Single(s => s.Name == "global").Enabled);
            Assert.AreEqual(true, overlay.Servers.Single(s => s.Name == "local").Enabled);
            Write(project, "config.toml", "[plugins.mcp]\nenabled = false\n[plugins.mcp.servers.shared]\nenabled = true\n");
            Assert.AreEqual(false, (await service.List(new("epoch", "project-session"), CancellationToken.None))
                .Servers.Single(s => s.Name == "shared").Enabled);

            var huge = "[plugins.mcp]\nenabled = false\n# SECRET_OVERSIZE" + new string(' ', 1024 * 1024);
            Write(home, "config.toml", huge);
            var oversizedGlobal = await service.List(new("epoch", "project-session"), CancellationToken.None);
            Assert.IsTrue(oversizedGlobal.PolicyReadError);
            Assert.IsTrue(oversizedGlobal.Servers.All(s => s.Enabled is null));
            Assert.IsFalse(JsonSerializer.Serialize(oversizedGlobal, DesktopJsonContext.Default.McpInventoryResponse).Contains("SECRET_", StringComparison.Ordinal));
            Write(home, "config.toml", "[plugins.mcp]\nenabled = true\n");
            Write(project, "config.toml", huge);
            var oversizedProject = await service.List(new("epoch", "project-session"), CancellationToken.None);
            Assert.IsTrue(oversizedProject.PolicyReadError);
            Assert.IsTrue(oversizedProject.Servers.All(s => s.Enabled is null));
            Assert.IsFalse(JsonSerializer.Serialize(oversizedProject, DesktopJsonContext.Default.McpInventoryResponse).Contains("SECRET_", StringComparison.Ordinal));
            Assert.IsFalse((await globalOnly.List(new("epoch", "global-session"), CancellationToken.None)).PolicyReadError,
                "A project policy failure cannot affect a global-only session.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task RealOwnedCatalogResolvesExactSessionScopeWithoutClientPathsOrPluginActivation()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-mcp-owned-" + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(root)) throw new InvalidOperationException("Test root already exists.");
        Directory.CreateDirectory(root);
        try
        {
            var global = Path.Combine(root, "catalog");
            var home = Path.Combine(root, "home");
            var projectPath = Path.Combine(root, "project");
            var otherPath = Path.Combine(root, "other");
            var builtin = Path.Combine(root, "builtin");
            foreach (var path in new[] { global, home, projectPath, otherPath, builtin }) Directory.CreateDirectory(path);
            Write(home, "mcp.json", """
                {"mcpServers":{"shared":{"command":"SECRET_GLOBAL"},"global-only":{"command":"SECRET_GLOBAL_ONLY"}}}
                """);
            Write(projectPath, "mcp.json", """
                {"mcpServers":{"shared":{"command":"SECRET_PROJECT"},"project-only":{"command":"SECRET_PROJECT_ONLY"}}}
                """);
            Write(otherPath, "mcp.json", """
                {"mcpServers":{"other-only":{"command":"SECRET_OTHER"}}}
                """);
            var provider = new ModelProviderDescriptor(new("mcp-inventory-fixture"), "Literal fixture") { IsDefault = true };
            await using var host = await CodeAltaHost.CreateAsync(new CodeAltaHostOptions
            {
                GlobalRoot = global, CurrentProjectPath = projectPath, DiscoveryScope = new(home, root), BuiltInSkillRoot = builtin,
                PluginEnvironment = FrozenDictionary<string, string?>.Empty, StartPlugins = false, IsHeadless = true, OwnsLogging = false,
                ConfigureModelProviders = registry => registry.RegisterOrReplace(provider, () => new InventoryRuntime(provider)),
            });
            Assert.IsEmpty(host.PluginRuntime.ActivePlugins);
            var project = await host.ProjectCatalog.UpsertFromPathAsync(projectPath);
            var other = await host.ProjectCatalog.UpsertFromPathAsync(otherPath);
            var projectSession = await host.Commands.CreateDraftSessionAsync(project, provider, "Project draft");
            var otherSession = await host.Commands.CreateDraftSessionAsync(other, provider, "Other draft");
            var globalSession = await host.Commands.CreateDraftSessionAsync(null, provider, "Global draft");
            var rpc = new McpInventoryService(host.Commands, "epoch", home);
            var request = new McpInventoryRequest("epoch", projectSession.SessionId);
            var wire = JsonSerializer.Serialize(request, DesktopJsonContext.Default.McpInventoryRequest);
            Assert.IsFalse(wire.Contains(root, StringComparison.Ordinal));
            Assert.IsFalse(wire.Contains("path", StringComparison.OrdinalIgnoreCase));
            Assert.AreEqual("stale_epoch", (await rpc.List(request with { ExpectedEpoch = "old" }, CancellationToken.None)).Status);
            var selected = await rpc.List(request, CancellationToken.None);
            Assert.AreEqual("ok", selected.Status);
            Assert.AreEqual(project.Id, selected.ProjectId);
            Assert.HasCount(3, selected.Servers);
            Assert.AreEqual("Project", selected.Servers.Single(row => row.Name == "shared").Scope);
            Assert.IsTrue(selected.Servers.Single(row => row.Name == "shared").OverridesGlobal);
            Assert.IsFalse(selected.Servers.Any(row => row.Name == "other-only"));
            Assert.IsFalse(JsonSerializer.Serialize(selected, DesktopJsonContext.Default.McpInventoryResponse).Contains("SECRET_", StringComparison.Ordinal));
            var otherSelected = await rpc.List(request with { SessionId = otherSession.SessionId }, CancellationToken.None);
            Assert.AreEqual(other.Id, otherSelected.ProjectId);
            Assert.IsTrue(otherSelected.Servers.Any(row => row.Name == "other-only"));
            Assert.IsFalse(otherSelected.Servers.Any(row => row.Name == "project-only"));
            var unscoped = await rpc.List(request with { SessionId = globalSession.SessionId }, CancellationToken.None);
            Assert.AreEqual("ok", unscoped.Status);
            Assert.IsNull(unscoped.ProjectId);
            Assert.HasCount(2, unscoped.Servers);
            Assert.IsTrue(unscoped.Servers.All(row => row.Scope == "Global"));
            Assert.AreEqual("unavailable", (await rpc.List(request with { SessionId = Guid.NewGuid().ToString("D") }, CancellationToken.None)).Status);
            Assert.IsTrue(await host.ProjectCatalog.DeleteAsync(project));
            Assert.AreEqual("unavailable", (await rpc.List(request, CancellationToken.None)).Status,
                "Removed project identity cannot be silently converted to global scope or a different project's path.");
            Assert.IsEmpty(host.PluginRuntime.ActivePlugins);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void Write(string directory, string name, string content)
    {
        var path = Path.Combine(directory, ".alta", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private sealed class InventoryRuntime(ModelProviderDescriptor descriptor) : IModelProviderSessionRuntime
    {
        public ModelProviderDescriptor Descriptor => descriptor;
        public Task StartAsync(CancellationToken token = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken token = default) => Task.CompletedTask;
        public Task<ModelProviderProbeResult> ProbeAsync(CancellationToken token = default) => throw new AssertFailedException("Inventory must not probe providers.");
        public IModelProviderTurnExecutor CreateTurnExecutor() => throw new AssertFailedException("Inventory must not start turns.");
        public Task<IAgentSession> CreateSessionAsync(AgentSessionCreateOptions options, CancellationToken token = default)
            => Task.FromResult<IAgentSession>(new InventorySession(descriptor.ProviderId, options.SessionId!, options.WorkingDirectory));
        public Task<IAgentSession> ResumeSessionAsync(string id, AgentSessionResumeOptions options, CancellationToken token = default)
            => Task.FromResult<IAgentSession>(new InventorySession(descriptor.ProviderId, id, options.WorkingDirectory));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class InventorySession(ModelProviderId providerId, string id, string? path) : IAgentSession
    {
        public ModelProviderId ProviderId => providerId;
        public string SessionId => id;
        public string? WorkspacePath => path;
        public async IAsyncEnumerable<AgentEvent> StreamEventsAsync([EnumeratorCancellation] CancellationToken token = default)
        { await Task.CompletedTask; yield break; }
        public IDisposable Subscribe(Action<AgentEvent> handler) => new InventorySubscription();
        public Task<AgentRunId> SendAsync(AgentSendOptions options, CancellationToken token = default) => throw new AssertFailedException("Inventory must not send.");
        public Task AbortAsync(CancellationToken token = default) => Task.CompletedTask;
        public Task<AgentRunId> SteerAsync(AgentSteerOptions options, CancellationToken token = default) => throw new AssertFailedException("Inventory must not steer.");
        public Task CompactAsync(CancellationToken token = default) => throw new AssertFailedException("Inventory must not compact.");
        public Task<IReadOnlyList<AgentEvent>> GetHistoryAsync(CancellationToken token = default) => Task.FromResult<IReadOnlyList<AgentEvent>>([]);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class InventorySubscription : IDisposable { public void Dispose() { } }
}
