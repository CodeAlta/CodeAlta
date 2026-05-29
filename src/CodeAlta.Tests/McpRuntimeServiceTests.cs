using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Plugin.Mcp;
using CodeAlta.Plugins.Abstractions;
using XenoAtom.CommandLine;

namespace CodeAlta.Tests;

[TestClass]
public sealed class McpRuntimeServiceTests
{
    [TestMethod]
    public async Task SearchDescribeAndCall_UseStdioAndApplyToolPolicy()
    {
        using var project = TempDirectory.Create();
        WriteTinyServerConfig(project.Path, "tiny", logPath: null);
        WriteProjectPolicy(
            project.Path,
            """
            [plugins.mcp]
            max_tool_output_chars = 12

            [plugins.mcp.servers.tiny]
            disabled_tools = ["disabled"]
            """);
        await using var service = new McpRuntimeService();
        var request = new McpRuntimeRequest { ProjectDirectory = project.Path };

        var search = await service.SearchToolsAsync(request, serverFilter: null, query: null, CancellationToken.None);
        var disabledDiagnostics = new List<McpRuntimeDiagnostic>();
        var disabled = await service.DescribeToolAsync(request, "tiny", "disabled", disabledDiagnostics, CancellationToken.None);
        var callDiagnostics = new List<McpRuntimeDiagnostic>();
        var call = await service.CallToolAsync(
            request,
            "tiny",
            "echo",
            new Dictionary<string, object?> { ["text"] = JsonDocument.Parse("\"hello\"").RootElement.Clone() },
            callDiagnostics,
            CancellationToken.None);
        var secretDiagnostics = new List<McpRuntimeDiagnostic>();
        var secret = await service.CallToolAsync(request, "tiny", "secret", new Dictionary<string, object?>(), secretDiagnostics, CancellationToken.None);

        Assert.AreEqual(0, search.Diagnostics.Count);
        CollectionAssert.Contains(search.Tools.Select(static tool => tool.Name).ToArray(), "echo");
        CollectionAssert.DoesNotContain(search.Tools.Select(static tool => tool.Name).ToArray(), "disabled");
        Assert.IsNull(disabled);
        Assert.AreEqual("tool_disabled", disabledDiagnostics.Single().Code);
        Assert.IsNotNull(call);
        Assert.AreEqual("echo", call.Name);
        Assert.AreEqual("mcp__tiny__echo", call.Alias);
        Assert.AreEqual("echo:hello", call.ContentText);
        Assert.IsNotNull(secret);
        Assert.AreEqual("[redacted]", secret.ContentText);
        Assert.AreEqual(0, callDiagnostics.Count);
        Assert.AreEqual(0, secretDiagnostics.Count);
    }

    [TestMethod]
    public async Task SearchDescribeAndCall_ApplyAllowedToolsPolicy()
    {
        using var project = TempDirectory.Create();
        WriteTinyServerConfig(project.Path, "tiny", logPath: null);
        WriteProjectPolicy(
            project.Path,
            """
            [plugins.mcp.servers.tiny]
            allowed_tools = ["echo"]
            """);
        await using var service = new McpRuntimeService();
        var request = new McpRuntimeRequest { ProjectDirectory = project.Path };

        var search = await service.SearchToolsAsync(request, serverFilter: "tiny", query: null, CancellationToken.None);
        var describeDiagnostics = new List<McpRuntimeDiagnostic>();
        var describedSecret = await service.DescribeToolAsync(request, "tiny", "secret", describeDiagnostics, CancellationToken.None);
        var callDiagnostics = new List<McpRuntimeDiagnostic>();
        var calledSecret = await service.CallToolAsync(request, "tiny", "secret", new Dictionary<string, object?>(), callDiagnostics, CancellationToken.None);
        var allowedDiagnostics = new List<McpRuntimeDiagnostic>();
        var allowedCall = await service.CallToolAsync(
            request,
            "tiny",
            "echo",
            new Dictionary<string, object?> { ["text"] = JsonDocument.Parse("\"ok\"").RootElement.Clone() },
            allowedDiagnostics,
            CancellationToken.None);

        Assert.AreEqual(0, search.Diagnostics.Count, string.Join(Environment.NewLine, search.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        CollectionAssert.AreEqual(new[] { "echo" }, search.Tools.Select(static tool => tool.Name).ToArray());
        Assert.IsNull(describedSecret);
        Assert.AreEqual("tool_disabled", describeDiagnostics.Single().Code);
        StringAssert.Contains(describeDiagnostics.Single().Message, "allowed_tools");
        Assert.IsNull(calledSecret);
        Assert.AreEqual("tool_disabled", callDiagnostics.Single().Code);
        Assert.IsNotNull(allowedCall);
        Assert.AreEqual("echo:ok", allowedCall.ContentText);
        Assert.AreEqual(0, allowedDiagnostics.Count);
    }

    [TestMethod]
    public async Task ListDirectTools_AppliesDirectExposurePolicy()
    {
        using var project = TempDirectory.Create();
        WriteTinyServerConfig(project.Path, "tiny", logPath: null);
        WriteProjectPolicy(
            project.Path,
            """
            [plugins.mcp]
            direct_exposure = "allowlist"

            [plugins.mcp.servers.tiny]
            direct_tools = ["echo"]
            disabled_tools = ["secret"]
            """);
        await using var service = new McpRuntimeService();

        var direct = await service.ListDirectToolsAsync(new McpRuntimeRequest { ProjectDirectory = project.Path }, CancellationToken.None);

        Assert.AreEqual(0, direct.Diagnostics.Count, string.Join(Environment.NewLine, direct.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        CollectionAssert.AreEqual(new[] { "mcp__tiny__echo" }, direct.Tools.Select(static tool => tool.Alias).ToArray());
    }

    [TestMethod]
    public async Task ListDirectTools_AutoUsesExplicitDirectToolsWhenOverThreshold()
    {
        using var project = TempDirectory.Create();
        WriteTinyServerConfig(project.Path, "tiny", logPath: null);
        WriteProjectPolicy(
            project.Path,
            """
            [plugins.mcp]
            direct_exposure = "auto"
            direct_tool_threshold = 1

            [plugins.mcp.servers.tiny]
            direct_tools = ["echo"]
            """);
        await using var service = new McpRuntimeService();

        var direct = await service.ListDirectToolsAsync(new McpRuntimeRequest { ProjectDirectory = project.Path }, CancellationToken.None);

        Assert.AreEqual(0, direct.Diagnostics.Count, string.Join(Environment.NewLine, direct.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        CollectionAssert.AreEqual(new[] { "echo" }, direct.Tools.Select(static tool => tool.Name).ToArray());
    }

    [TestMethod]
    public async Task PluginBeforeAgentRun_ExposesAndInvokesDirectMcpTool()
    {
        using var project = TempDirectory.Create();
        WriteTinyServerConfig(project.Path, "tiny", logPath: null);
        WriteProjectPolicy(
            project.Path,
            """
            [plugins.mcp]
            direct_exposure = "allowlist"

            [plugins.mcp.servers.tiny]
            direct_tools = ["echo"]
            allowed_tools = ["echo"]
            """);
        var plugin = new McpPlugin();
        var inactiveContext = new PluginBeforeAgentRunContext
        {
            Plugin = CreatePluginDescriptor(),
            Services = NoopPluginServices.Create(),
            ProjectPath = project.Path,
        };

        Assert.IsNull(await plugin.OnBeforeAgentRunAsync(inactiveContext, CancellationToken.None));

        var contribution = plugin.GetAltaCommands().Single();
        var stdout = new StringWriter(CultureInfo.InvariantCulture);
        var stderr = new StringWriter(CultureInfo.InvariantCulture);
        var app = new CommandApp("alta", "test") { contribution.CreateCommandNode(CreateAltaContext(stdout, stderr, project.Path)) };
        var activateExitCode = await app.RunAsync(["mcp", "activate", "tiny"], new CommandRunConfig { Out = TextWriter.Null, Error = stderr });
        Assert.AreEqual(0, activateExitCode, stderr.ToString());

        var context = new PluginBeforeAgentRunContext
        {
            Plugin = CreatePluginDescriptor(),
            Services = NoopPluginServices.Create(),
            ProjectPath = project.Path,
        };

        var before = await plugin.OnBeforeAgentRunAsync(context, CancellationToken.None);

        Assert.IsNotNull(before);
        var tool = before.AdditionalTools.Single();
        Assert.AreEqual("mcp__tiny__echo", tool.Spec.Name);
        Assert.AreEqual("object", tool.Spec.InputSchema.GetProperty("type").GetString());
        Assert.IsTrue(tool.Spec.InputSchema.GetProperty("properties").TryGetProperty("text", out _));
        using var arguments = JsonDocument.Parse("{\"text\":\"hi\"}");
        var result = await tool.Handler(
            new AgentToolInvocation(new ModelProviderId("test"), "session", "call", tool.Spec.Name, arguments.RootElement.Clone()),
            CancellationToken.None);
        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual("echo:hi", Assert.IsInstanceOfType<AgentToolResultItem.Text>(result.Items.Single()).Value);

        var prompt = await plugin.GetSystemPromptContributions().Single().Content(
            new PluginSystemPromptContext
            {
                Plugin = CreatePluginDescriptor(),
                Services = NoopPluginServices.Create(),
                ProjectPath = project.Path,
            },
            CancellationToken.None);
        Assert.IsNotNull(prompt);
        StringAssert.Contains(prompt, "- Active: `tiny`");
        StringAssert.Contains(prompt, "- Inactive (`alta mcp activate <id>*`): (none)");
    }

    [TestMethod]
    public async Task Activate_UsesCallerSourceSessionForNextRunScope()
    {
        using var project = TempDirectory.Create();
        WriteTinyServerConfig(project.Path, "tiny", logPath: null);
        var plugin = new McpPlugin();
        var contribution = plugin.GetAltaCommands().Single();
        var stdout = new StringWriter(CultureInfo.InvariantCulture);
        var stderr = new StringWriter(CultureInfo.InvariantCulture);
        var app = new CommandApp("alta", "test") { contribution.CreateCommandNode(CreateAltaContext(stdout, stderr, project.Path, sourceSessionId: "session-a")) };

        var activateExitCode = await app.RunAsync(["mcp", "activate", "tiny"], new CommandRunConfig { Out = TextWriter.Null, Error = stderr });

        Assert.AreEqual(0, activateExitCode, stderr.ToString());
        var otherSession = await plugin.OnBeforeAgentRunAsync(
            new PluginBeforeAgentRunContext
            {
                Plugin = CreatePluginDescriptor(),
                Services = NoopPluginServices.Create(),
                ProjectPath = project.Path,
                SessionId = "session-b",
            },
            CancellationToken.None);
        Assert.IsNull(otherSession, "Activation from an agent caller must not fall back to the project scope for a different session.");

        var sameSession = await plugin.OnBeforeAgentRunAsync(
            new PluginBeforeAgentRunContext
            {
                Plugin = CreatePluginDescriptor(),
                Services = NoopPluginServices.Create(),
                ProjectPath = project.Path,
                SessionId = "session-a",
            },
            CancellationToken.None);
        Assert.IsNotNull(sameSession);
        Assert.IsTrue(sameSession.AdditionalTools.Any(static tool => tool.Spec.Name == "mcp__tiny__echo"));

        var prompt = await plugin.GetSystemPromptContributions().Single().Content(
            new PluginSystemPromptContext
            {
                Plugin = CreatePluginDescriptor(),
                Services = NoopPluginServices.Create(),
                ProjectPath = project.Path,
                SessionId = "session-a",
            },
            CancellationToken.None);
        Assert.IsNotNull(prompt);
        StringAssert.Contains(prompt, "- Active: `tiny`");
        StringAssert.Contains(prompt, "- Inactive (`alta mcp activate <id>*`): (none)");
    }

    [TestMethod]
    public async Task Search_UsesProjectOverlayAndDoesNotConnectShadowedGlobalServer()
    {
        using var home = TempDirectory.Create();
        using var project = TempDirectory.Create();
        var globalLog = Path.Combine(home.Path, "global.log");
        var projectLog = Path.Combine(project.Path, "project.log");
        WriteTinyServerConfig(home.Path, "shared", globalLog, global: true);
        WriteTinyServerConfig(project.Path, "shared", projectLog);
        await using var service = new McpRuntimeService();

        var search = await service.SearchToolsAsync(new McpRuntimeRequest { UserHomeDirectory = home.Path, ProjectDirectory = project.Path }, null, null, CancellationToken.None);

        Assert.AreEqual(0, search.Diagnostics.Count);
        Assert.IsTrue(search.Tools.Any(static tool => tool.Server == "shared" && tool.Name == "echo"));
        Assert.IsTrue(File.Exists(projectLog), "The project overlay server should be contacted.");
        Assert.IsFalse(File.Exists(globalLog), "The shadowed global server should not be contacted.");
    }

    [TestMethod]
    public async Task Search_FailedServerDoesNotSuppressWorkingServer()
    {
        using var project = TempDirectory.Create();
        Directory.CreateDirectory(Path.Combine(project.Path, ".alta"));
        File.WriteAllText(
            McpConfigDiscovery.GetProjectConfigPath(project.Path),
            JsonSerializer.Serialize(new
            {
                mcpServers = new Dictionary<string, object>
                {
                    ["tiny"] = CreateTinyServerDefinition(new Dictionary<string, string>(StringComparer.Ordinal)),
                    ["unavailable"] = new { url = "ftp://example.test/mcp" },
                },
            }));
        WriteProjectPolicy(
            project.Path,
            """
            [plugins.mcp]
            startup_timeout_ms = 1000
            """);
        await using var service = new McpRuntimeService();

        var search = await service.SearchToolsAsync(new McpRuntimeRequest { ProjectDirectory = project.Path }, serverFilter: null, query: null, CancellationToken.None);

        Assert.IsTrue(search.Tools.Any(static tool => tool.Server == "tiny" && tool.Name == "echo"));
        var diagnostic = search.Diagnostics.Single(static item => item.Server == "unavailable");
        Assert.AreEqual("invalid_http_url", diagnostic.Code);
        Assert.IsFalse(search.Tools.Any(static tool => tool.Server == "unavailable"));
    }

    [TestMethod]
    public async Task Search_SlowUnavailableServerDoesNotSuppressWorkingServer()
    {
        using var project = TempDirectory.Create();
        Directory.CreateDirectory(Path.Combine(project.Path, ".alta"));
        File.WriteAllText(
            McpConfigDiscovery.GetProjectConfigPath(project.Path),
            JsonSerializer.Serialize(new
            {
                mcpServers = new Dictionary<string, object>
                {
                    ["slow"] = CreateTinyServerDefinition(new Dictionary<string, string> { ["MCP_TEST_DELAY_MS"] = "5000" }),
                    ["tiny"] = CreateTinyServerDefinition(new Dictionary<string, string>(StringComparer.Ordinal)),
                },
            }));
        WriteProjectPolicy(
            project.Path,
            """
            [plugins.mcp]
            startup_timeout_ms = 100

            [plugins.mcp.servers.tiny]
            startup_timeout_ms = 5000
            """);
        await using var service = new McpRuntimeService();

        var search = await service.SearchToolsAsync(new McpRuntimeRequest { ProjectDirectory = project.Path }, serverFilter: null, query: null, CancellationToken.None);

        Assert.IsTrue(search.Tools.Any(static tool => tool.Server == "tiny" && tool.Name == "echo"));
        Assert.IsFalse(search.Tools.Any(static tool => tool.Server == "slow"));
        Assert.AreEqual("server_startup_timeout", search.Diagnostics.Single(static diagnostic => diagnostic.Server == "slow").Code);
    }

    [TestMethod]
    public async Task Search_RefreshesCacheWhenEffectiveServerEnvironmentChanges()
    {
        using var project = TempDirectory.Create();
        await using var service = new McpRuntimeService();
        var request = new McpRuntimeRequest { ProjectDirectory = project.Path };
        WriteTinyServerConfig(project.Path, "tiny", logPath: null, extraEnv: new Dictionary<string, string> { ["MCP_TEST_EXTRA_TOOL"] = "first" });

        var first = await service.SearchToolsAsync(request, serverFilter: "tiny", query: null, CancellationToken.None);
        WriteTinyServerConfig(project.Path, "tiny", logPath: null, extraEnv: new Dictionary<string, string> { ["MCP_TEST_EXTRA_TOOL"] = "second" });
        var second = await service.SearchToolsAsync(request, serverFilter: "tiny", query: null, CancellationToken.None);

        Assert.IsTrue(first.Tools.Any(static tool => tool.Name == "first"));
        Assert.IsFalse(first.Tools.Any(static tool => tool.Name == "second"));
        Assert.IsTrue(second.Tools.Any(static tool => tool.Name == "second"));
        Assert.IsFalse(second.Tools.Any(static tool => tool.Name == "first"));
    }

    [TestMethod]
    public async Task Search_ExpandsEnvironmentVariableReferencesInStdioEnvironment()
    {
        using var project = TempDirectory.Create();
        var variableName = "CODEALTA_TEST_MCP_EXTRA_TOOL_" + Guid.NewGuid().ToString("N");
        var originalValue = Environment.GetEnvironmentVariable(variableName);
        try
        {
            Environment.SetEnvironmentVariable(variableName, "from_env");
            WriteTinyServerConfig(project.Path, "tiny", logPath: null, extraEnv: new Dictionary<string, string> { ["MCP_TEST_EXTRA_TOOL"] = "${" + variableName + "}" });
            await using var service = new McpRuntimeService();

            var search = await service.SearchToolsAsync(new McpRuntimeRequest { ProjectDirectory = project.Path }, serverFilter: "tiny", query: null, CancellationToken.None);

            Assert.AreEqual(0, search.Diagnostics.Count, string.Join(Environment.NewLine, search.Diagnostics.Select(static diagnostic => diagnostic.Message)));
            Assert.IsTrue(search.Tools.Any(static tool => tool.Name == "from_env"));
            Assert.IsFalse(search.Tools.Any(tool => tool.Name.Contains(variableName, StringComparison.Ordinal)));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, originalValue);
        }
    }

    [TestMethod]
    public async Task Search_ReturnsDiagnosticWhenEnvironmentVariableReferenceIsMissing()
    {
        using var project = TempDirectory.Create();
        var variableName = "CODEALTA_TEST_MCP_MISSING_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variableName, null);
        WriteTinyServerConfig(project.Path, "tiny", logPath: null, extraEnv: new Dictionary<string, string> { ["MCP_TEST_EXTRA_TOOL"] = "${" + variableName + "}" });
        await using var service = new McpRuntimeService();

        var search = await service.SearchToolsAsync(new McpRuntimeRequest { ProjectDirectory = project.Path }, serverFilter: "tiny", query: null, CancellationToken.None);

        Assert.AreEqual(0, search.Tools.Count);
        var diagnostic = search.Diagnostics.Single();
        Assert.AreEqual("environment_variable_not_found", diagnostic.Code);
        StringAssert.Contains(diagnostic.Message, variableName);
    }

    [TestMethod]
    public async Task Search_ReturnsStartupTimeoutDiagnostic()
    {
        using var project = TempDirectory.Create();
        WriteTinyServerConfig(project.Path, "slow", logPath: null, extraEnv: new Dictionary<string, string> { ["MCP_TEST_DELAY_MS"] = "5000" });
        WriteProjectPolicy(
            project.Path,
            """
            [plugins.mcp]
            startup_timeout_ms = 100
            """);
        await using var service = new McpRuntimeService();

        var search = await service.SearchToolsAsync(new McpRuntimeRequest { ProjectDirectory = project.Path }, "slow", null, CancellationToken.None);

        Assert.AreEqual(0, search.Tools.Count);
        Assert.AreEqual("server_startup_timeout", search.Diagnostics.Single().Code);
    }

    [TestMethod]
    public async Task Search_ReturnsDisabledServerDiagnosticWithoutConnecting()
    {
        using var project = TempDirectory.Create();
        var logPath = Path.Combine(project.Path, "disabled.log");
        WriteTinyServerConfig(project.Path, "disabled-server", logPath);
        WriteProjectPolicy(
            project.Path,
            """
            [plugins.mcp.servers.disabled-server]
            enabled = false
            """);
        await using var service = new McpRuntimeService();

        var search = await service.SearchToolsAsync(new McpRuntimeRequest { ProjectDirectory = project.Path }, "disabled-server", null, CancellationToken.None);

        Assert.AreEqual(0, search.Tools.Count);
        Assert.AreEqual("server_disabled", search.Diagnostics.Single().Code);
        Assert.IsFalse(File.Exists(logPath), "Disabled servers should not be connected for tool discovery.");
    }

    [TestMethod]
    public async Task SearchDescribeAndCall_AssignStableAliasesWhenSanitizedNamesCollide()
    {
        using var project = TempDirectory.Create();
        Directory.CreateDirectory(Path.Combine(project.Path, ".alta"));
        var extraEnv = new Dictionary<string, string> { ["MCP_TEST_EXTRA_TOOLS"] = "a-b|a_b" };
        File.WriteAllText(
            McpConfigDiscovery.GetProjectConfigPath(project.Path),
            JsonSerializer.Serialize(new
            {
                mcpServers = new Dictionary<string, object>
                {
                    ["srv-a"] = CreateTinyServerDefinition(extraEnv),
                    ["srv_a"] = CreateTinyServerDefinition(extraEnv),
                },
            }));
        var request = new McpRuntimeRequest { ProjectDirectory = project.Path };
        await using var firstService = new McpRuntimeService();
        await using var secondService = new McpRuntimeService();

        var first = await firstService.SearchToolsAsync(request, serverFilter: null, query: null, CancellationToken.None);
        var second = await secondService.SearchToolsAsync(request, serverFilter: null, query: null, CancellationToken.None);
        var describeDiagnostics = new List<McpRuntimeDiagnostic>();
        var described = await firstService.DescribeToolAsync(request, "srv-a", "a-b", describeDiagnostics, CancellationToken.None);
        var callDiagnostics = new List<McpRuntimeDiagnostic>();
        var call = await firstService.CallToolAsync(request, "srv_a", "a_b", new Dictionary<string, object?>(), callDiagnostics, CancellationToken.None);

        Assert.AreEqual(0, first.Diagnostics.Count, string.Join(Environment.NewLine, first.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        Assert.AreEqual(0, second.Diagnostics.Count, string.Join(Environment.NewLine, second.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        CollectionAssert.AreEqual(
            first.Tools.Select(static tool => tool.Alias).OrderBy(static alias => alias, StringComparer.Ordinal).ToArray(),
            second.Tools.Select(static tool => tool.Alias).OrderBy(static alias => alias, StringComparer.Ordinal).ToArray(),
            "Collision suffixes must be deterministic across runtime instances.");
        Assert.AreEqual(first.Tools.Count, first.Tools.Select(static tool => tool.Alias).Distinct(StringComparer.Ordinal).Count(), "Every qualified alias should be unique after suffixing collisions.");
        var aliasByTool = first.Tools.ToDictionary(static tool => (tool.Server, tool.Name), static tool => tool.Alias);
        Assert.AreNotEqual("mcp__srv_a__echo", aliasByTool[("srv-a", "echo")]);
        Assert.AreNotEqual("mcp__srv_a__echo", aliasByTool[("srv_a", "echo")]);
        Assert.AreNotEqual(aliasByTool[("srv-a", "echo")], aliasByTool[("srv_a", "echo")]);
        Assert.AreNotEqual("mcp__srv_a__a_b", aliasByTool[("srv-a", "a-b")]);
        Assert.AreNotEqual("mcp__srv_a__a_b", aliasByTool[("srv-a", "a_b")]);
        Assert.AreNotEqual(aliasByTool[("srv-a", "a-b")], aliasByTool[("srv-a", "a_b")]);
        Assert.IsTrue(aliasByTool[("srv-a", "a-b")].Contains("_h", StringComparison.Ordinal));
        Assert.IsNotNull(described);
        Assert.AreEqual("a-b", described.Name, "Raw tool names should remain available for describe routing.");
        Assert.AreEqual(aliasByTool[("srv-a", "a-b")], described.Alias);
        Assert.AreEqual(0, describeDiagnostics.Count);
        Assert.IsNotNull(call);
        Assert.AreEqual("a_b", call.Name, "Raw tool names should remain available for call routing.");
        Assert.AreEqual("extra:a_b", call.ContentText);
        Assert.AreEqual(aliasByTool[("srv_a", "a_b")], call.Alias);
        Assert.AreEqual(0, callDiagnostics.Count);
    }

    [TestMethod]
    public async Task CallTool_ConvertsRichResultsAndTruncatesOutput()
    {
        using var project = TempDirectory.Create();
        WriteTinyServerConfig(project.Path, "tiny", logPath: null, extraEnv: new Dictionary<string, string> { ["MCP_TEST_RICH_TOOLS"] = "1" });
        WriteProjectPolicy(
            project.Path,
            """
            [plugins.mcp]
            max_tool_output_chars = 1000
            """);
        var request = new McpRuntimeRequest { ProjectDirectory = project.Path };
        await using var service = new McpRuntimeService();

        var richDiagnostics = new List<McpRuntimeDiagnostic>();
        var rich = await service.CallToolAsync(request, "tiny", "rich", new Dictionary<string, object?>(), richDiagnostics, CancellationToken.None);
        var errorDiagnostics = new List<McpRuntimeDiagnostic>();
        var error = await service.CallToolAsync(request, "tiny", "error", new Dictionary<string, object?>(), errorDiagnostics, CancellationToken.None);

        Assert.IsNotNull(rich);
        Assert.IsFalse(rich.IsError);
        Assert.IsFalse(rich.Truncated);
        Assert.AreEqual("alpha", rich.ContentText);
        Assert.AreEqual("ok", rich.StructuredContent?.GetProperty("visible").GetString());
        Assert.AreEqual("[redacted]", rich.StructuredContent?.GetProperty("apiToken").GetString());
        Assert.IsTrue(rich.Content.Any(static block => block.Type == "image" && block.MimeType == "image/png" && block.Summary!.Contains("image content omitted", StringComparison.Ordinal)));
        Assert.IsTrue(rich.Content.Any(static block => block.Type == "audio" && block.MimeType == "audio/wav" && block.Summary!.Contains("audio content omitted", StringComparison.Ordinal)));
        Assert.IsTrue(rich.Content.Any(static block => block.Type == "resource_link" && block.Summary!.Contains("resource link:", StringComparison.Ordinal)));
        Assert.IsTrue(rich.Content.Any(static block => block.Type == "resource" && block.Summary!.Contains("embedded resource omitted", StringComparison.Ordinal)));
        Assert.AreEqual(0, richDiagnostics.Count);
        Assert.IsNotNull(error);
        Assert.IsTrue(error.IsError);
        Assert.AreEqual("failure text", error.ContentText);
        Assert.AreEqual(0, errorDiagnostics.Count);

        WriteProjectPolicy(
            project.Path,
            """
            [plugins.mcp]
            max_tool_output_chars = 8
            """);
        await using var truncatingService = new McpRuntimeService();
        var truncationDiagnostics = new List<McpRuntimeDiagnostic>();
        var truncated = await truncatingService.CallToolAsync(request, "tiny", "long", new Dictionary<string, object?>(), truncationDiagnostics, CancellationToken.None);

        Assert.IsNotNull(truncated);
        Assert.AreEqual("abcdefgh", truncated.ContentText);
        Assert.IsTrue(truncated.Truncated);
        Assert.IsNull(truncated.StructuredContent, "Structured content should be omitted when the output budget has already been consumed.");
        Assert.AreEqual(0, truncationDiagnostics.Count);
    }

    [TestMethod]
    public async Task Search_StdioServerStderrDoesNotImplyFailure()
    {
        using var project = TempDirectory.Create();
        WriteTinyServerConfig(project.Path, "tiny", logPath: null, extraEnv: new Dictionary<string, string> { ["MCP_TEST_STDERR"] = "diagnostic stderr from test server" });
        await using var service = new McpRuntimeService();

        var search = await service.SearchToolsAsync(new McpRuntimeRequest { ProjectDirectory = project.Path }, "tiny", null, CancellationToken.None);

        Assert.AreEqual(0, search.Diagnostics.Count, string.Join(Environment.NewLine, search.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        Assert.IsTrue(search.Tools.Any(static tool => tool.Name == "echo"));
    }

    [TestMethod]
    public async Task PluginCommand_ToolCallInvokesConfiguredStdioServer()
    {
        using var project = TempDirectory.Create();
        WriteTinyServerConfig(project.Path, "tiny", logPath: null);
        var plugin = new McpPlugin();
        var contribution = plugin.GetAltaCommands().Single();
        var stdout = new StringWriter(CultureInfo.InvariantCulture);
        var stderr = new StringWriter(CultureInfo.InvariantCulture);
        var app = new CommandApp("alta", "test") { contribution.CreateCommandNode(CreateAltaContext(stdout, stderr, project.Path)) };

        var exitCode = await app.RunAsync(
            ["mcp", "tool", "call", "--server", "tiny", "--tool", "echo", "--arguments", "{\"text\":\"hi\"}"],
            new CommandRunConfig { Out = TextWriter.Null, Error = stderr });

        Assert.AreEqual(0, exitCode, stderr.ToString());
        var output = stdout.ToString();
        StringAssert.Contains(output, "\"type\":\"alta.mcp.tool.call\"");
        StringAssert.Contains(output, "\"server\":\"tiny\"");
        StringAssert.Contains(output, "\"tool\":\"echo\"");
        StringAssert.Contains(output, "echo:hi");
    }

    [TestMethod]
    public async Task HttpServer_SearchDescribeCallAndPluginCommandUseStaticHeaders()
    {
        using var project = TempDirectory.Create();
        await using var server = TinyHttpMcpServer.Start(requiredHeaderName: "Authorization", requiredHeaderValue: "Bearer secret-token-value");
        Directory.CreateDirectory(Path.Combine(project.Path, ".alta"));
        File.WriteAllText(
            McpConfigDiscovery.GetProjectConfigPath(project.Path),
            JsonSerializer.Serialize(new
            {
                mcpServers = new Dictionary<string, object>
                {
                    ["remote"] = new
                    {
                        url = server.Endpoint,
                        headers = new Dictionary<string, string> { ["Authorization"] = "Bearer secret-token-value" },
                    },
                },
            }));
        await using var service = new McpRuntimeService();
        var request = new McpRuntimeRequest { ProjectDirectory = project.Path };

        var search = await service.SearchToolsAsync(request, "remote", null, CancellationToken.None);
        var describeDiagnostics = new List<McpRuntimeDiagnostic>();
        var describe = await service.DescribeToolAsync(request, "remote", "echo", describeDiagnostics, CancellationToken.None);
        var callDiagnostics = new List<McpRuntimeDiagnostic>();
        var call = await service.CallToolAsync(
            request,
            "remote",
            "echo",
            new Dictionary<string, object?> { ["text"] = JsonDocument.Parse("\"hi\"").RootElement.Clone() },
            callDiagnostics,
            CancellationToken.None);
        var plugin = new McpPlugin();
        var contribution = plugin.GetAltaCommands().Single();
        var stdout = new StringWriter(CultureInfo.InvariantCulture);
        var stderr = new StringWriter(CultureInfo.InvariantCulture);
        var app = new CommandApp("alta", "test") { contribution.CreateCommandNode(CreateAltaContext(stdout, stderr, project.Path)) };

        var exitCode = await app.RunAsync(["mcp", "tool", "search", "--server", "remote"], new CommandRunConfig { Out = TextWriter.Null, Error = stderr });

        Assert.AreEqual(0, search.Diagnostics.Count, string.Join(Environment.NewLine, search.Diagnostics.Select(static diagnostic => diagnostic.Message)) + Environment.NewLine + server.ErrorSummary);
        Assert.IsTrue(search.Tools.Any(static tool => tool.Server == "remote" && tool.Name == "echo" && tool.Alias == "mcp__remote__echo"));
        Assert.IsNotNull(describe);
        Assert.AreEqual("echo", describe.Name);
        Assert.AreEqual(0, describeDiagnostics.Count);
        Assert.IsNotNull(call);
        Assert.AreEqual("http:hi", call.ContentText);
        Assert.AreEqual(0, callDiagnostics.Count);
        Assert.IsTrue(server.SawHeader("Authorization", "Bearer secret-token-value"), "Configured static HTTP headers should be sent to the MCP server.");
        Assert.AreEqual(0, exitCode, stderr.ToString());
        StringAssert.Contains(stdout.ToString(), "\"type\":\"alta.mcp.tool\"");
        StringAssert.Contains(stdout.ToString(), "\"server\":\"remote\"");
        Assert.IsFalse(stdout.ToString().Contains("secret-token-value", StringComparison.Ordinal), "CLI diagnostics must not echo configured header values.");
    }

    [TestMethod]
    public async Task HttpServer_ExpandsEnvironmentVariableReferencesInHeaders()
    {
        using var project = TempDirectory.Create();
        await using var server = TinyHttpMcpServer.Start(requiredHeaderName: "Authorization", requiredHeaderValue: "Bearer secret-token-value");
        var variableName = "CODEALTA_TEST_MCP_HTTP_TOKEN_" + Guid.NewGuid().ToString("N");
        var originalValue = Environment.GetEnvironmentVariable(variableName);
        try
        {
            Environment.SetEnvironmentVariable(variableName, "secret-token-value");
            Directory.CreateDirectory(Path.Combine(project.Path, ".alta"));
            File.WriteAllText(
                McpConfigDiscovery.GetProjectConfigPath(project.Path),
                JsonSerializer.Serialize(new
                {
                    mcpServers = new Dictionary<string, object>
                    {
                        ["remote"] = new
                        {
                            url = server.Endpoint,
                            headers = new Dictionary<string, string> { ["Authorization"] = "Bearer ${" + variableName + "}" },
                        },
                    },
                }));
            await using var service = new McpRuntimeService();

            var search = await service.SearchToolsAsync(new McpRuntimeRequest { ProjectDirectory = project.Path }, "remote", null, CancellationToken.None);

            Assert.AreEqual(0, search.Diagnostics.Count, string.Join(Environment.NewLine, search.Diagnostics.Select(static diagnostic => diagnostic.Message)) + Environment.NewLine + server.ErrorSummary);
            Assert.IsTrue(search.Tools.Any(static tool => tool.Name == "echo"));
            Assert.IsTrue(server.SawHeader("Authorization", "Bearer secret-token-value"), "Environment variable references in HTTP headers should be expanded before connecting.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, originalValue);
        }
    }

    [TestMethod]
    public async Task HttpServer_ValidationAndAuthenticationDiagnosticsAreRedacted()
    {
        using var project = TempDirectory.Create();
        Directory.CreateDirectory(Path.Combine(project.Path, ".alta"));
        File.WriteAllText(
            McpConfigDiscovery.GetProjectConfigPath(project.Path),
            """
            { "mcpServers": { "remote": { "url": "ftp://example.test/mcp?token=secret-value", "headers": { "Authorization": "Bearer secret-token-value" } } } }
            """);
        await using var service = new McpRuntimeService();

        var invalid = await service.SearchToolsAsync(new McpRuntimeRequest { ProjectDirectory = project.Path }, "remote", null, CancellationToken.None);

        Assert.AreEqual(0, invalid.Tools.Count);
        Assert.AreEqual("invalid_http_url", invalid.Diagnostics.Single().Code);
        Assert.IsFalse(invalid.Diagnostics.Single().Message.Contains("secret-value", StringComparison.Ordinal));
        Assert.IsFalse(invalid.Diagnostics.Single().Message.Contains("secret-token-value", StringComparison.Ordinal));

        await using var authServer = TinyHttpMcpServer.Start(requiredHeaderName: "Authorization", requiredHeaderValue: "Bearer expected-token");
        File.WriteAllText(
            McpConfigDiscovery.GetProjectConfigPath(project.Path),
            JsonSerializer.Serialize(new
            {
                mcpServers = new Dictionary<string, object>
                {
                    ["remote"] = new
                    {
                        url = authServer.Endpoint + "?token=secret-value",
                        headers = new Dictionary<string, string> { ["Authorization"] = "Bearer wrong-secret-token-value" },
                    },
                },
            }));
        await using var authService = new McpRuntimeService();

        var unauthorized = await authService.SearchToolsAsync(new McpRuntimeRequest { ProjectDirectory = project.Path }, "remote", null, CancellationToken.None);

        Assert.AreEqual(0, unauthorized.Tools.Count);
        Assert.AreEqual("server_authentication_failed", unauthorized.Diagnostics.Single().Code);
        Assert.IsFalse(unauthorized.Diagnostics.Single().Message.Contains("secret-value", StringComparison.Ordinal));
        Assert.IsFalse(unauthorized.Diagnostics.Single().Message.Contains("wrong-secret-token-value", StringComparison.Ordinal));
    }

    private static void WriteTinyServerConfig(string root, string server, string? logPath, bool global = false, IReadOnlyDictionary<string, string>? extraEnv = null)
    {
        var path = global ? McpConfigDiscovery.GetGlobalConfigPath(root) : McpConfigDiscovery.GetProjectConfigPath(root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(logPath))
        {
            env["MCP_TEST_LOG"] = logPath;
        }

        if (extraEnv is not null)
        {
            foreach (var (key, value) in extraEnv)
            {
                env[key] = value;
            }
        }

        var definition = new
        {
            mcpServers = new Dictionary<string, object>
            {
                [server] = CreateTinyServerDefinition(env),
            },
        };
        File.WriteAllText(path, JsonSerializer.Serialize(definition));
    }

    private static object CreateTinyServerDefinition(IReadOnlyDictionary<string, string> env)
        => new
        {
            command = "dotnet",
            args = new[] { TinyServerAssemblyPath },
            env,
        };

    private static void WriteProjectPolicy(string projectPath, string content)
    {
        var path = McpPolicyWriter.GetProjectPolicyPath(projectPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static PluginAltaCommandContext CreateAltaContext(TextWriter stdout, TextWriter stderr, string? projectPath, string? sourceSessionId = null)
        => new()
        {
            Plugin = CreatePluginDescriptor(),
            Services = NoopPluginServices.Create(),
            Scope = PluginScope.Global,
            CorrelationId = "corr-1",
            WorkingDirectory = projectPath,
            SourceSessionId = sourceSessionId,
            Stdin = TextReader.Null,
            Stdout = stdout,
            Stderr = stderr,
        };

    private static PluginDescriptor CreatePluginDescriptor()
        => new()
        {
            RuntimeKey = "mcp",
            TypeName = typeof(McpPlugin).FullName!,
            AssemblyName = typeof(McpPlugin).Assembly.GetName().Name!,
            DisplayName = "MCP",
        };

    private static string TinyServerAssemblyPath
        => Path.Combine(AppContext.BaseDirectory, "CodeAlta.Tests.TinyMcpServer.dll");

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory()
            => Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CodeAlta.McpRuntimeServiceTests." + Guid.NewGuid().ToString("N"));

        public string Path { get; }

        public static TempDirectory Create()
        {
            var directory = new TempDirectory();
            Directory.CreateDirectory(directory.Path);
            return directory;
        }

        public void Dispose()
        {
            if (!Directory.Exists(Path))
            {
                return;
            }

            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    internal sealed class TinyHttpMcpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _shutdown = new();
        private readonly Task _acceptLoop;
        private readonly object _syncRoot = new();
        private readonly List<IReadOnlyDictionary<string, string>> _requests = [];
        private readonly List<string> _errors = [];
        private readonly string? _requiredHeaderName;
        private readonly string? _requiredHeaderValue;

        private TinyHttpMcpServer(string? requiredHeaderName, string? requiredHeaderValue)
        {
            _requiredHeaderName = requiredHeaderName;
            _requiredHeaderValue = requiredHeaderValue;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Endpoint = $"http://127.0.0.1:{port}/mcp";
            _acceptLoop = Task.Run(AcceptLoopAsync);
        }

        public string Endpoint { get; }

        public static TinyHttpMcpServer Start(string? requiredHeaderName = null, string? requiredHeaderValue = null)
            => new(requiredHeaderName, requiredHeaderValue);

        public bool SawHeader(string name, string value)
        {
            lock (_syncRoot)
            {
                return _requests.Any(headers => headers.TryGetValue(name, out var actual) && string.Equals(actual, value, StringComparison.Ordinal));
            }
        }

        public string ErrorSummary
        {
            get
            {
                lock (_syncRoot)
                {
                    return string.Join(Environment.NewLine, _errors);
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _shutdown.CancelAsync();
            _listener.Stop();
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }

            _shutdown.Dispose();
        }

        private async Task AcceptLoopAsync()
        {
            while (!_shutdown.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_shutdown.Token).ConfigureAwait(false);
                _ = Task.Run(() => HandleClientAsync(client, _shutdown.Token));
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
        {
            try
            {
                using var acceptedClient = client;
                using var stream = acceptedClient.GetStream();
                var requestLine = await ReadLineAsync(stream, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(requestLine))
                {
                    return;
                }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            while (true)
            {
                var line = await ReadLineAsync(stream, cancellationToken).ConfigureAwait(false);
                if (line.Length == 0)
                {
                    break;
                }

                var separator = line.IndexOf(':', StringComparison.Ordinal);
                if (separator > 0)
                {
                    headers[line[..separator]] = line[(separator + 1)..].Trim();
                }
            }

            lock (_syncRoot)
            {
                _requests.Add(new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(_requiredHeaderName) &&
                (!headers.TryGetValue(_requiredHeaderName, out var actualHeader) || !string.Equals(actualHeader, _requiredHeaderValue, StringComparison.Ordinal)))
            {
                await WriteResponseAsync(stream, 401, "Unauthorized", "{\"error\":\"unauthorized\"}", cancellationToken).ConfigureAwait(false);
                return;
            }

            var requestParts = requestLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (requestParts.Length < 2 || !requestParts[1].StartsWith("/mcp", StringComparison.Ordinal))
            {
                await WriteResponseAsync(stream, 404, "Not Found", "{}", cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!string.Equals(requestParts[0], "POST", StringComparison.OrdinalIgnoreCase))
            {
                await WriteResponseAsync(stream, 200, "OK", "{}", cancellationToken).ConfigureAwait(false);
                return;
            }

            var body = headers.TryGetValue("Transfer-Encoding", out var transferEncoding) && transferEncoding.Contains("chunked", StringComparison.OrdinalIgnoreCase)
                ? await ReadChunkedBodyAsync(stream, cancellationToken).ConfigureAwait(false)
                : await ReadContentLengthBodyAsync(stream, headers, cancellationToken).ConfigureAwait(false);
            var response = CreateJsonRpcResponse(body, out var isNotification);
            if (isNotification)
            {
                await WriteResponseAsync(stream, 202, "Accepted", string.Empty, cancellationToken).ConfigureAwait(false);
                return;
            }

                await WriteResponseAsync(stream, 200, "OK", response, cancellationToken, includeSessionId: true).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not ObjectDisposedException)
            {
                lock (_syncRoot)
                {
                    _errors.Add(ex.ToString());
                }
            }
        }

        private static string CreateJsonRpcResponse(string body, out bool isNotification)
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            isNotification = !root.TryGetProperty("id", out var id);
            if (isNotification)
            {
                return string.Empty;
            }

            var method = root.GetProperty("method").GetString();
            return method switch
            {
                "initialize" => "{\"jsonrpc\":\"2.0\",\"id\":" + id.GetRawText() + ",\"result\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{\"tools\":{\"listChanged\":false}},\"serverInfo\":{\"name\":\"codealta-http-mcp\",\"version\":\"1.0.0\"}}}",
                "tools/list" => "{\"jsonrpc\":\"2.0\",\"id\":" + id.GetRawText() + ",\"result\":{\"tools\":[{\"name\":\"echo\",\"title\":\"Echo\",\"description\":\"Echo input text over HTTP.\",\"inputSchema\":{\"type\":\"object\",\"properties\":{\"text\":{\"type\":\"string\"}}}}]}}",
                "tools/call" => CreateToolCallResponse(id, root),
                _ => "{\"jsonrpc\":\"2.0\",\"id\":" + id.GetRawText() + ",\"error\":{\"code\":-32601,\"message\":\"Unknown method\"}}",
            };
        }

        private static string CreateToolCallResponse(JsonElement id, JsonElement root)
        {
            var text = root.GetProperty("params").GetProperty("arguments").GetProperty("text").GetString() ?? string.Empty;
            var payload = JsonSerializer.Serialize("http:" + text);
            return "{\"jsonrpc\":\"2.0\",\"id\":" + id.GetRawText() + ",\"result\":{\"content\":[{\"type\":\"text\",\"text\":" + payload + "}],\"structuredContent\":{\"echoed\":" + JsonSerializer.Serialize(text) + "}}}";
        }

        private static async Task<string> ReadLineAsync(NetworkStream stream, CancellationToken cancellationToken)
        {
            var bytes = new List<byte>();
            var buffer = new byte[1];
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (buffer[0] == (byte)'\n')
                {
                    break;
                }

                if (buffer[0] != (byte)'\r')
                {
                    bytes.Add(buffer[0]);
                }
            }

            return Encoding.ASCII.GetString(bytes.ToArray());
        }

        private static async Task<string> ReadContentLengthBodyAsync(NetworkStream stream, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken)
        {
            headers.TryGetValue("Content-Length", out var lengthText);
            _ = int.TryParse(lengthText, CultureInfo.InvariantCulture, out var contentLength);
            return await ReadBodyAsync(stream, contentLength, cancellationToken).ConfigureAwait(false);
        }

        private static async Task<string> ReadChunkedBodyAsync(NetworkStream stream, CancellationToken cancellationToken)
        {
            using var buffer = new MemoryStream();
            while (true)
            {
                var sizeLine = await ReadLineAsync(stream, cancellationToken).ConfigureAwait(false);
                var extensionIndex = sizeLine.IndexOf(';', StringComparison.Ordinal);
                var sizeText = extensionIndex < 0 ? sizeLine : sizeLine[..extensionIndex];
                var size = int.Parse(sizeText, System.Globalization.NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                if (size == 0)
                {
                    _ = await ReadLineAsync(stream, cancellationToken).ConfigureAwait(false);
                    break;
                }

                var chunk = new byte[size];
                var offset = 0;
                while (offset < size)
                {
                    var read = await stream.ReadAsync(chunk.AsMemory(offset, size - offset), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    offset += read;
                }

                await buffer.WriteAsync(chunk.AsMemory(0, offset), cancellationToken).ConfigureAwait(false);
                _ = await ReadLineAsync(stream, cancellationToken).ConfigureAwait(false);
            }

            return Encoding.UTF8.GetString(buffer.ToArray());
        }

        private static async Task<string> ReadBodyAsync(NetworkStream stream, int contentLength, CancellationToken cancellationToken)
        {
            var buffer = new byte[contentLength];
            var offset = 0;
            while (offset < contentLength)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(offset, contentLength - offset), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                offset += read;
            }

            return Encoding.UTF8.GetString(buffer, 0, offset);
        }

        private static async Task WriteResponseAsync(NetworkStream stream, int statusCode, string reasonPhrase, string body, CancellationToken cancellationToken, bool includeSessionId = false)
        {
            var bodyBytes = Encoding.UTF8.GetBytes(body);
            var builder = new StringBuilder();
            builder.Append("HTTP/1.1 ").Append(statusCode).Append(' ').Append(reasonPhrase).Append("\r\n");
            builder.Append("Content-Type: application/json\r\n");
            builder.Append("Content-Length: ").Append(bodyBytes.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            builder.Append("Connection: close\r\n");
            if (includeSessionId)
            {
                builder.Append("mcp-session-id: codealta-test-session\r\n");
            }

            builder.Append("\r\n");
            var headerBytes = Encoding.ASCII.GetBytes(builder.ToString());
            await stream.WriteAsync(headerBytes, cancellationToken).ConfigureAwait(false);
            if (bodyBytes.Length > 0)
            {
                await stream.WriteAsync(bodyBytes, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
