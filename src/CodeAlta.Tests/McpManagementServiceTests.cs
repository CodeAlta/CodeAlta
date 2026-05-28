using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using CodeAlta.App;
using CodeAlta.Plugin.Mcp;
using CodeAlta.Views;
using XenoAtom.Terminal;
using XenoAtom.Terminal.Backends;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Hosting;

namespace CodeAlta.Tests;

[TestClass]
public sealed class McpManagementServiceTests
{
    [TestMethod]
    public void RefreshSnapshot_ReportsConfiguredDisabledInvalidMissingAndShadowedStates()
    {
        using var home = TempDirectory.Create();
        using var project = TempDirectory.Create();
        Directory.CreateDirectory(Path.Combine(home.Path, ".alta"));
        Directory.CreateDirectory(Path.Combine(project.Path, ".alta"));
        File.WriteAllText(
            Path.Combine(home.Path, ".alta", "mcp.json"),
            """
            {
              "mcpServers": {
                "shared": { "command": "global" },
                "global-only": { "command": "npx", "args": ["--token", "secret-value"] }
              }
            }
            """);
        File.WriteAllText(
            Path.Combine(project.Path, ".alta", "mcp.json"),
            """
            {
              "servers": {
                "shared": { "type": "stdio", "command": "project", "env": { "API_TOKEN": "abc123" } },
                "remote": { "type": "sse", "url": "https://example.test/mcp?token=secret", "headers": { "Authorization": "Bearer token" } }
              }
            }
            """);
        File.WriteAllText(
            Path.Combine(project.Path, ".alta", "config.toml"),
            """
            [plugins.mcp.servers.remote]
            enabled = false
            disabled_tools = ["danger"]
            """);
        using var invalidProject = TempDirectory.Create();
        Directory.CreateDirectory(Path.Combine(invalidProject.Path, ".alta"));
        File.WriteAllText(Path.Combine(invalidProject.Path, ".alta", "mcp.json"), "{ invalid json");

        var service = new McpManagementService();
        var snapshot = service.RefreshSnapshot(new McpManagementRequest
        {
            UserHomeDirectory = home.Path,
            ProjectDirectory = project.Path,
        });
        var invalidSnapshot = service.RefreshSnapshot(new McpManagementRequest
        {
            UserHomeDirectory = home.Path,
            ProjectDirectory = invalidProject.Path,
        });
        var missingSnapshot = service.RefreshSnapshot(new McpManagementRequest
        {
            UserHomeDirectory = Path.Combine(home.Path, "missing-home"),
        });

        Assert.AreEqual(3, snapshot.Summary.ConfiguredServerCount);
        Assert.AreEqual(1, snapshot.Summary.ShadowedServerCount);
        Assert.IsTrue(snapshot.Servers.Any(server => server.Key == "shared" && server.State == McpManagementServerState.Configured && server.OverridesGlobal));
        Assert.IsTrue(snapshot.Servers.Any(server => server.Key == "shared" && server.State == McpManagementServerState.Shadowed));
        var remote = snapshot.Servers.Single(server => server.Key == "remote");
        Assert.AreEqual(McpManagementServerState.Disabled, remote.State);
        Assert.AreEqual("https://example.test/mcp?token=[redacted]", remote.Url);
        Assert.AreEqual("[redacted]", remote.Headers["Authorization"]);
        CollectionAssert.Contains(remote.DisabledTools.ToArray(), "danger");
        var shared = snapshot.Servers.First(server => server.Key == "shared" && server.State == McpManagementServerState.Configured);
        Assert.AreEqual("[redacted]", shared.Env["API_TOKEN"]);
        Assert.IsTrue(invalidSnapshot.Servers.Any(server => server.State == McpManagementServerState.InvalidConfig));
        Assert.IsTrue(missingSnapshot.Servers.Any(server => server.State == McpManagementServerState.MissingConfig));
    }

    [TestMethod]
    public async Task SetServerEnabledAsync_WritesPolicyAndRefreshesSnapshot()
    {
        using var project = TempDirectory.Create();
        Directory.CreateDirectory(Path.Combine(project.Path, ".alta"));
        File.WriteAllText(
            Path.Combine(project.Path, ".alta", "mcp.json"),
            """
            { "mcpServers": { "memory": { "command": "npx" } } }
            """);
        var service = new McpManagementService();
        var request = new McpManagementRequest { ProjectDirectory = project.Path };

        var result = await service.SetServerEnabledAsync("memory", enabled: false, McpManagementScope.Project, request, CancellationToken.None);

        Assert.AreEqual(McpManagementScope.Project, result.Scope);
        Assert.IsTrue(File.Exists(Path.Combine(project.Path, ".alta", "config.toml")));
        var snapshot = service.CachedSnapshot;
        Assert.IsNotNull(snapshot);
        Assert.AreEqual(McpManagementServerState.Disabled, snapshot.Servers.Single(server => server.Key == "memory").State);
    }

    [TestMethod]
    public async Task AddOrUpdateServerAsync_WritesJsonConfigAndRefreshesSnapshot()
    {
        using var project = TempDirectory.Create();
        var service = new McpManagementService();
        var request = new McpManagementRequest { ProjectDirectory = project.Path };

        var result = await service.AddOrUpdateServerAsync(
            new McpManagementServerEdit
            {
                Key = "docs",
                Transport = McpManagementTransport.Http,
                Url = "https://example.test/mcp",
                Headers = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Authorization"] = "Bearer ${DOCS_TOKEN}",
                },
            },
            McpManagementScope.Project,
            request: request);

        Assert.AreEqual(McpManagementScope.Project, result.Scope);
        Assert.IsTrue(File.Exists(Path.Combine(project.Path, ".alta", "mcp.json")));
        var server = service.CachedSnapshot!.Servers.Single(server => server.Key == "docs");
        Assert.AreEqual(McpManagementTransport.Http, server.Transport);
        Assert.AreEqual("https://example.test/mcp", server.Url);
        Assert.AreEqual("[redacted]", server.Headers["Authorization"]);
    }

    [TestMethod]
    public async Task RemoveServerAsync_RemovesJsonConfigAndRefreshesSnapshot()
    {
        using var project = TempDirectory.Create();
        WriteTinyServerConfig(project.Path, "tiny", extraEnv: null);
        var service = new McpManagementService();
        var request = new McpManagementRequest { ProjectDirectory = project.Path };
        service.RefreshSnapshot(request);

        var result = await service.RemoveServerAsync("tiny", McpManagementScope.Project, request);

        Assert.IsTrue(result.Changed);
        Assert.IsFalse(service.CachedSnapshot!.Servers.Any(server => server.Key == "tiny" && server.State != McpManagementServerState.MissingConfig));
    }

    [TestMethod]
    public void StatusIndicator_UsesOnlyCachedSnapshotData()
    {
        using var project = TempDirectory.Create();
        Directory.CreateDirectory(Path.Combine(project.Path, ".alta"));
        File.WriteAllText(
            Path.Combine(project.Path, ".alta", "mcp.json"),
            """
            { "mcpServers": { "memory": { "command": "npx" } } }
            """);
        var service = new McpManagementService();

        var beforeRefresh = McpStatusIndicatorComposer.Compose(service, () => project.Path, () => { }, pluginHostBridge: null);
        service.RefreshSnapshot(new McpManagementRequest { ProjectDirectory = project.Path });
        var afterRefresh = McpStatusIndicatorComposer.Compose(service, () => project.Path, () => { }, pluginHostBridge: null);

        Assert.IsNull(beforeRefresh);
        Assert.IsNotNull(afterRefresh);
    }

    [TestMethod]
    public void DialogMarkup_RendersSnapshotStatesAndSummary()
    {
        var snapshot = new McpManagementSnapshot
        {
            Policy = new McpManagementPolicySnapshot { GlobalPath = "global.toml" },
            Summary = new McpManagementSummary
            {
                HasConfiguration = true,
                ConfiguredServerCount = 2,
                ActiveServerCount = 1,
                InvalidSourceCount = 1,
                ShadowedServerCount = 1,
            },
        };
        var configured = new McpManagementServerSnapshot
        {
            Key = "memory",
            DisplayName = "memory",
            State = McpManagementServerState.Configured,
            Transport = McpManagementTransport.Stdio,
            SourceScope = McpManagementScope.Project,
        };
        var invalid = configured with
        {
            Key = "project-config",
            DisplayName = "project MCP config",
            State = McpManagementServerState.InvalidConfig,
            Transport = null,
            SourceScope = McpManagementScope.Project,
        };

        var summary = McpServersDialog.BuildSummaryMarkup(snapshot);
        var configuredMarkup = McpServersDialog.BuildServerListItemMarkup(configured, selected: true);
        var invalidMarkup = McpServersDialog.BuildServerListItemMarkup(invalid, selected: false);

        StringAssert.Contains(summary, "Servers:[/] 1/2");
        StringAssert.Contains(summary, "Invalid sources: 1");
        StringAssert.Contains(configuredMarkup, "memory");
        StringAssert.Contains(configuredMarkup, "configured · stdio · project");
        StringAssert.Contains(invalidMarkup, "invalid config");
    }

    [TestMethod]
    public void DialogMarkup_RendersToolsTabTitleWithLoadedToolCount()
    {
        var server = new McpManagementServerSnapshot
        {
            Key = "github",
            DisplayName = "github",
            State = McpManagementServerState.Configured,
            Tools = Enumerable.Range(0, 46)
                .Select(index => new McpManagementToolSnapshot
                {
                    Name = $"tool-{index}",
                    Alias = $"mcp__github__tool_{index}",
                    Availability = "available",
                })
                .ToArray(),
        };

        Assert.AreEqual("Tools (46)", McpServersDialog.BuildToolsTabTitle(server));
    }

    [TestMethod]
    public void DialogMarkup_ParsesEditableAssignmentFields()
    {
        var values = McpServersDialog.ParseAssignments("Authorization=Bearer ${TOKEN}; X-Test=value=with-equals", "Headers");

        Assert.AreEqual("Bearer ${TOKEN}", values["Authorization"]);
        Assert.AreEqual("value=with-equals", values["X-Test"]);
        CollectionAssert.AreEqual(new[] { "-y", "@server/package" }, McpServersDialog.ParseList("-y; @server/package").ToArray());
    }

    [TestMethod]
    public async Task TestServerAsync_UpdatesSnapshotWithStdioToolRowsAndPolicyState()
    {
        using var project = TempDirectory.Create();
        WriteTinyServerConfig(project.Path, "tiny", extraEnv: null);
        WriteProjectPolicy(
            project.Path,
            """
            [plugins.mcp.servers.tiny]
            disabled_tools = ["disabled"]
            """);
        var service = new McpManagementService();
        var request = new McpManagementRequest { ProjectDirectory = project.Path };

        var result = await service.TestServerAsync("tiny", request, CancellationToken.None);

        Assert.AreEqual(McpManagementTestStatus.Succeeded, result.Status);
        Assert.AreEqual(3, result.Tools.Count);
        Assert.IsTrue(result.Tools.Any(static tool => tool.Name == "echo" && tool.Enabled && tool.Alias == "mcp__tiny__echo"));
        Assert.IsTrue(result.Tools.Any(static tool => tool.Name == "disabled" && !tool.Enabled && tool.Availability == "disabled_by_policy"));
        var snapshot = service.CachedSnapshot;
        Assert.IsNotNull(snapshot);
        var server = snapshot.Servers.Single(server => server.Key == "tiny");
        Assert.AreEqual(2, server.ExposedToolCount);
        Assert.AreEqual(3, server.TotalToolCount);
        Assert.AreEqual(McpManagementTestStatus.Succeeded, server.LastTestStatus);
        StringAssert.Contains(McpServersDialog.BuildToolsMarkup(server), "mcp__tiny__echo");
        StringAssert.Contains(McpServersDialog.BuildToolsMarkup(server), "disabled_by_policy");
    }

    [TestMethod]
    public async Task TestServerAsync_UpdatesSnapshotWithCollisionSuffixedAliases()
    {
        using var project = TempDirectory.Create();
        WriteTinyServerConfig(project.Path, "tiny", new Dictionary<string, string> { ["MCP_TEST_EXTRA_TOOLS"] = "a-b|a_b" });
        var service = new McpManagementService();

        var result = await service.TestServerAsync("tiny", new McpManagementRequest { ProjectDirectory = project.Path }, CancellationToken.None);

        Assert.AreEqual(McpManagementTestStatus.Succeeded, result.Status);
        var server = service.CachedSnapshot!.Servers.Single(server => server.Key == "tiny");
        var aliases = server.Tools.Select(static tool => tool.Alias).ToArray();
        Assert.AreEqual(aliases.Length, aliases.Distinct(StringComparer.Ordinal).Count());
        var dashedAlias = server.Tools.Single(static tool => tool.Name == "a-b").Alias;
        var underscoredAlias = server.Tools.Single(static tool => tool.Name == "a_b").Alias;
        Assert.AreNotEqual("mcp__tiny__a_b", dashedAlias);
        Assert.AreNotEqual("mcp__tiny__a_b", underscoredAlias);
        Assert.AreNotEqual(dashedAlias, underscoredAlias);
        StringAssert.Contains(McpServersDialog.BuildToolsMarkup(server), dashedAlias);
        StringAssert.Contains(McpServersDialog.BuildToolsMarkup(server), underscoredAlias);
    }

    [TestMethod]
    public async Task TestServerAsync_UpdatesSnapshotWithHttpToolRows()
    {
        using var project = TempDirectory.Create();
        await using var httpServer = McpRuntimeServiceTests.TinyHttpMcpServer.Start(requiredHeaderName: "Authorization", requiredHeaderValue: "Bearer secret-token-value");
        Directory.CreateDirectory(Path.Combine(project.Path, ".alta"));
        File.WriteAllText(
            McpConfigDiscovery.GetProjectConfigPath(project.Path),
            JsonSerializer.Serialize(new
            {
                mcpServers = new Dictionary<string, object>
                {
                    ["remote"] = new
                    {
                        url = httpServer.Endpoint,
                        headers = new Dictionary<string, string> { ["Authorization"] = "Bearer secret-token-value" },
                    },
                },
            }));
        var service = new McpManagementService();

        var result = await service.TestServerAsync("remote", new McpManagementRequest { ProjectDirectory = project.Path }, CancellationToken.None);

        Assert.AreEqual(McpManagementTestStatus.Succeeded, result.Status);
        Assert.AreEqual(1, result.Tools.Count);
        Assert.IsTrue(httpServer.SawHeader("Authorization", "Bearer secret-token-value"));
        var server = service.CachedSnapshot!.Servers.Single(server => server.Key == "remote");
        Assert.AreEqual(McpManagementTransport.Http, server.Transport);
        Assert.AreEqual(McpManagementTestStatus.Succeeded, server.LastTestStatus);
        Assert.AreEqual(1, server.ExposedToolCount);
        StringAssert.Contains(McpServersDialog.BuildToolsMarkup(server), "mcp__remote__echo");
    }

    [TestMethod]
    public async Task SetToolEnabledAsync_WritesDisabledToolsAndRefreshesRuntimePolicy()
    {
        using var project = TempDirectory.Create();
        WriteTinyServerConfig(project.Path, "tiny", extraEnv: null);
        var jsonPath = McpConfigDiscovery.GetProjectConfigPath(project.Path);
        var originalJson = File.ReadAllText(jsonPath);
        var service = new McpManagementService();
        var request = new McpManagementRequest { ProjectDirectory = project.Path };
        await service.TestServerAsync("tiny", request, CancellationToken.None);

        var disabledResult = await service.SetToolEnabledAsync("tiny", "echo", enabled: false, McpManagementScope.Project, request, CancellationToken.None);
        await using var disabledRuntime = new McpRuntimeService();
        var disabledSearch = await disabledRuntime.SearchToolsAsync(new McpRuntimeRequest { ProjectDirectory = project.Path }, "tiny", null, CancellationToken.None);
        var disabledDescribeDiagnostics = new List<McpRuntimeDiagnostic>();
        var disabledDescribe = await disabledRuntime.DescribeToolAsync(new McpRuntimeRequest { ProjectDirectory = project.Path }, "tiny", "echo", disabledDescribeDiagnostics, CancellationToken.None);
        var disabledCallDiagnostics = new List<McpRuntimeDiagnostic>();
        var disabledCall = await disabledRuntime.CallToolAsync(
            new McpRuntimeRequest { ProjectDirectory = project.Path },
            "tiny",
            "echo",
            new Dictionary<string, object?> { ["text"] = JsonDocument.Parse("\"hi\"").RootElement.Clone() },
            disabledCallDiagnostics,
            CancellationToken.None);

        Assert.AreEqual(McpManagementScope.Project, disabledResult.Scope);
        Assert.IsTrue(disabledResult.Changed);
        CollectionAssert.Contains(disabledResult.DisabledTools.ToArray(), "echo");
        Assert.AreEqual(originalJson, File.ReadAllText(jsonPath), "Tool policy mutations must preserve MCP JSON server definitions.");
        var disabledPolicy = File.ReadAllText(McpPolicyWriter.GetProjectPolicyPath(project.Path));
        StringAssert.Contains(disabledPolicy, "disabled_tools");
        StringAssert.Contains(disabledPolicy, "echo");
        var disabledServer = service.CachedSnapshot!.Servers.Single(server => server.Key == "tiny");
        Assert.IsTrue(disabledServer.DisabledTools.Contains("echo", StringComparer.Ordinal));
        Assert.IsFalse(disabledServer.Tools.Single(tool => tool.Name == "echo").Enabled);
        Assert.AreEqual(2, disabledServer.ExposedToolCount);
        Assert.AreEqual(3, disabledServer.TotalToolCount);
        StringAssert.Contains(McpServersDialog.BuildToolsMarkup(disabledServer), "disabled_by_policy");
        Assert.IsFalse(disabledSearch.Tools.Any(static tool => tool.Name == "echo"));
        Assert.IsNull(disabledDescribe);
        Assert.AreEqual("tool_disabled", disabledDescribeDiagnostics.Single().Code);
        Assert.IsNull(disabledCall);
        Assert.AreEqual("tool_disabled", disabledCallDiagnostics.Single().Code);

        var enabledResult = await service.SetToolEnabledAsync("tiny", "echo", enabled: true, McpManagementScope.Project, request, CancellationToken.None);
        await using var enabledRuntime = new McpRuntimeService();
        var enabledSearch = await enabledRuntime.SearchToolsAsync(new McpRuntimeRequest { ProjectDirectory = project.Path }, "tiny", null, CancellationToken.None);

        Assert.IsTrue(enabledResult.Changed);
        Assert.IsFalse(enabledResult.DisabledTools.Contains("echo", StringComparer.Ordinal));
        Assert.AreEqual(originalJson, File.ReadAllText(jsonPath), "Re-enabling tools must preserve MCP JSON server definitions.");
        var enabledPolicy = File.ReadAllText(McpPolicyWriter.GetProjectPolicyPath(project.Path));
        Assert.IsFalse(enabledPolicy.Contains("disabled_tools", StringComparison.Ordinal));
        var enabledServer = service.CachedSnapshot!.Servers.Single(server => server.Key == "tiny");
        Assert.IsFalse(enabledServer.DisabledTools.Contains("echo", StringComparer.Ordinal));
        Assert.IsTrue(enabledServer.Tools.Single(tool => tool.Name == "echo").Enabled);
        Assert.AreEqual(3, enabledServer.ExposedToolCount);
        Assert.IsTrue(enabledSearch.Tools.Any(static tool => tool.Name == "echo"));
    }

    [TestMethod]
    public async Task TestServerAsync_ReportsInvalidHttpWithoutNetworkAccess()
    {
        using var project = TempDirectory.Create();
        Directory.CreateDirectory(Path.Combine(project.Path, ".alta"));
        File.WriteAllText(
            McpConfigDiscovery.GetProjectConfigPath(project.Path),
            """
            { "mcpServers": { "remote": { "url": "ftp://example.invalid/mcp?token=secret-value" } } }
            """);
        var service = new McpManagementService();

        var result = await service.TestServerAsync("remote", new McpManagementRequest { ProjectDirectory = project.Path }, CancellationToken.None);

        Assert.AreEqual(McpManagementTestStatus.Failed, result.Status);
        Assert.AreEqual(0, result.Tools.Count);
        Assert.IsTrue(result.Diagnostics.Any(static diagnostic => diagnostic.Contains("http or https", StringComparison.Ordinal)));
        Assert.IsTrue(result.Diagnostics.All(static diagnostic => !diagnostic.Contains("secret-value", StringComparison.Ordinal)));
        var server = service.CachedSnapshot!.Servers.Single(server => server.Key == "remote");
        Assert.AreEqual(McpManagementTestStatus.Failed, server.LastTestStatus);
        StringAssert.Contains(McpServersDialog.BuildToolsMarkup(server), "failed");
    }

    [TestMethod]
    public async Task TestServerAsync_ReportsTimeoutThroughManagementSnapshot()
    {
        using var project = TempDirectory.Create();
        WriteTinyServerConfig(project.Path, "slow", new Dictionary<string, string> { ["MCP_TEST_DELAY_MS"] = "5000" });
        WriteProjectPolicy(
            project.Path,
            """
            [plugins.mcp]
            startup_timeout_ms = 100
            """);
        var service = new McpManagementService();

        var result = await service.TestServerAsync("slow", new McpManagementRequest { ProjectDirectory = project.Path }, CancellationToken.None);

        Assert.AreEqual(McpManagementTestStatus.TimedOut, result.Status);
        Assert.AreEqual(0, result.Tools.Count);
        var server = service.CachedSnapshot!.Servers.Single(server => server.Key == "slow");
        Assert.AreEqual(McpManagementTestStatus.TimedOut, server.LastTestStatus);
        StringAssert.Contains(McpServersDialog.BuildToolsMarkup(server), "timed out");
    }

    [TestMethod]
    public async Task TestServerAsync_ReportsCancellation()
    {
        using var project = TempDirectory.Create();
        WriteTinyServerConfig(project.Path, "tiny", extraEnv: null);
        var service = new McpManagementService();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var result = await service.TestServerAsync("tiny", new McpManagementRequest { ProjectDirectory = project.Path }, cancellation.Token);

        Assert.AreEqual(McpManagementTestStatus.Canceled, result.Status);
        var server = service.CachedSnapshot!.Servers.Single(server => server.Key == "tiny");
        Assert.AreEqual(McpManagementTestStatus.Canceled, server.LastTestStatus);
    }

    [TestMethod]
    public void Dialog_TestServerButtonKeepsRowsAfterCompletion()
    {
        using var home = TempDirectory.Create();
        using var project = TempDirectory.Create();
        var globalPath = McpConfigDiscovery.GetGlobalConfigPath(home.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(globalPath)!);
        File.WriteAllText(globalPath, "{ \"mcpServers\": {} }");
        var projectPath = McpConfigDiscovery.GetProjectConfigPath(project.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(projectPath)!);
        File.WriteAllText(
            projectPath,
            """
            { "mcpServers": { "remote": { "url": "ftp://example.invalid/mcp" } } }
            """);
        var service = new McpManagementService();
        var useProjectRequest = true;
        var dialog = new McpServersDialog(
            service,
            () => new McpManagementRequest { ProjectDirectory = useProjectRequest ? project.Path : null, UserHomeDirectory = home.Path },
            static (_, _) => Task.CompletedTask,
            static () => null,
            static () => null);
        var root = new DocumentFlow();
        using var session = Terminal.Open(new InMemoryTerminalBackend(new TerminalSize(100, 30)), new TerminalOptions { ImplicitStartInput = true }, force: true);
        var app = new TerminalApp(
            root,
            session.Instance,
            new TerminalAppOptions
            {
                HostKind = TerminalHostKind.Fullscreen,
            });

        InvokeTerminalApp(app, "BeginRun");
        try
        {
            dialog.Show();
            TickTerminalApp(app);
            var row = GetDialogRows(dialog).Single(row => GetDialogRowEntry(row).Key == "remote");
            var detailBeforeTest = GetCurrentDetailVisual(dialog);
            Assert.IsInstanceOfType<TabControl>(detailBeforeTest);
            useProjectRequest = false;

            var testServerButton = FindDialogButton(dialog, "Test Server");
            app.Focus(testServerButton);
            ((InMemoryTerminalBackend)session.Instance.Backend).PushEvent(new TerminalKeyEvent { Key = TerminalKey.Enter });
            PumpTerminalUntil(app, () => GetDialogRowEntry(row).LastTestStatus != McpManagementTestStatus.NotRun, TimeSpan.FromSeconds(10));

            var rows = GetDialogRows(dialog);
            Assert.IsTrue(rows.Count > 0);
            var updatedRow = rows.Single(row => GetDialogRowEntry(row).Key == "remote");
            Assert.AreSame(row, updatedRow, "Test Server should update the selected row in place instead of replacing the sidebar item.");
            var remote = GetDialogRowEntry(updatedRow);
            Assert.AreEqual(McpManagementTestStatus.Failed, remote.LastTestStatus);
            for (var i = 0; i < 20; i++)
            {
                TickTerminalApp(app);
            }

            Assert.AreEqual(rows.Count, GetRenderedDialogRowCount(dialog), "The sidebar should still have rendered rows after the Test Server result is applied.");
            Assert.AreSame(detailBeforeTest, GetCurrentDetailVisual(dialog), "The Test Server result should not rebuild the selected details tab control.");
            Assert.IsInstanceOfType<TabControl>(GetCurrentDetailVisual(dialog), "The details pane should keep rendering the selected server tabs after Test Server completes.");
        }
        finally
        {
            InvokeTerminalApp(app, "EndRun");
        }
    }

    [TestMethod]
    public void Dialog_CloseCancelsInFlightTestServer()
    {
        using var project = TempDirectory.Create();
        WriteTinyServerConfig(project.Path, "slow", new Dictionary<string, string> { ["MCP_TEST_DELAY_MS"] = "5000" });
        WriteProjectPolicy(
            project.Path,
            """
            [plugins.mcp]
            startup_timeout_ms = 10000
            """);
        var service = new McpManagementService();
        var dialog = new McpServersDialog(
            service,
            () => new McpManagementRequest { ProjectDirectory = project.Path },
            static (_, _) => Task.CompletedTask,
            static () => null,
            static () => null);
        var root = new DocumentFlow();
        using var session = Terminal.Open(new InMemoryTerminalBackend(new TerminalSize(100, 30)), new TerminalOptions { ImplicitStartInput = true }, force: true);
        var app = new TerminalApp(
            root,
            session.Instance,
            new TerminalAppOptions
            {
                HostKind = TerminalHostKind.Fullscreen,
            });

        InvokeTerminalApp(app, "BeginRun");
        try
        {
            dialog.Show();
            TickTerminalApp(app);
            var row = GetDialogRows(dialog).Single(row => GetDialogRowEntry(row).Key == "slow");

            var testTask = InvokeTestServerAsync(dialog, row);
            InvokeDialogClose(dialog);
            PumpTerminalUntilCompleted(app, testTask, TimeSpan.FromSeconds(3));

            Assert.IsNull(GetDialogVisual(dialog).App, "Closing the dialog should detach it while the in-flight Test Server operation is canceled.");
        }
        finally
        {
            InvokeTerminalApp(app, "EndRun");
        }
    }

    [TestMethod]
    public void Dialog_AppShutdownCancelsInFlightTestServer()
    {
        using var project = TempDirectory.Create();
        WriteTinyServerConfig(project.Path, "slow", new Dictionary<string, string> { ["MCP_TEST_DELAY_MS"] = "5000" });
        WriteProjectPolicy(
            project.Path,
            """
            [plugins.mcp]
            startup_timeout_ms = 10000
            """);
        var service = new McpManagementService();
        var dialog = new McpServersDialog(
            service,
            () => new McpManagementRequest { ProjectDirectory = project.Path },
            static (_, _) => Task.CompletedTask,
            static () => null,
            static () => null);
        var root = new DocumentFlow();
        using var session = Terminal.Open(new InMemoryTerminalBackend(new TerminalSize(100, 30)), new TerminalOptions { ImplicitStartInput = true }, force: true);
        var app = new TerminalApp(
            root,
            session.Instance,
            new TerminalAppOptions
            {
                HostKind = TerminalHostKind.Fullscreen,
            });
        var ended = false;

        InvokeTerminalApp(app, "BeginRun");
        try
        {
            dialog.Show();
            TickTerminalApp(app);
            var row = GetDialogRows(dialog).Single(row => GetDialogRowEntry(row).Key == "slow");

            var testTask = InvokeTestServerAsync(dialog, row);
            InvokeTerminalApp(app, "EndRun");
            ended = true;

            Assert.IsTrue(testTask.Wait(TimeSpan.FromSeconds(3)), "App shutdown should cancel the in-flight MCP Test Server operation promptly.");
            testTask.GetAwaiter().GetResult();
        }
        finally
        {
            if (!ended)
            {
                InvokeTerminalApp(app, "EndRun");
            }
        }
    }

    [TestMethod]
    public void Dialog_ToolsTabKeepsDetailPaneVisibleAfterSuccessfulTestServer()
    {
        using var home = TempDirectory.Create();
        using var project = TempDirectory.Create();
        WriteTinyServerConfig(project.Path, "tiny", extraEnv: null);
        var service = new McpManagementService();
        var dialog = new McpServersDialog(
            service,
            () => new McpManagementRequest { ProjectDirectory = project.Path, UserHomeDirectory = home.Path },
            static (_, _) => Task.CompletedTask,
            static () => null,
            static () => null);
        var root = new DocumentFlow();
        using var session = Terminal.Open(new InMemoryTerminalBackend(new TerminalSize(120, 35)), new TerminalOptions { ImplicitStartInput = true }, force: true);
        var app = new TerminalApp(
            root,
            session.Instance,
            new TerminalAppOptions
            {
                HostKind = TerminalHostKind.Fullscreen,
            });

        InvokeTerminalApp(app, "BeginRun");
        try
        {
            dialog.Show();
            TickTerminalApp(app);
            var row = GetDialogRows(dialog).Single(row => GetDialogRowEntry(row).Key == "tiny");

            var testServerButton = FindDialogButton(dialog, "Test Server");
            app.Focus(testServerButton);
            ((InMemoryTerminalBackend)session.Instance.Backend).PushEvent(new TerminalKeyEvent { Key = TerminalKey.Enter });
            PumpTerminalUntil(app, () => GetDialogRowEntry(row).LastTestStatus == McpManagementTestStatus.Succeeded, TimeSpan.FromSeconds(10));

            var detailPane = Assert.IsInstanceOfType<TabControl>(GetCurrentDetailVisual(dialog));
            for (var i = 0; i < 5; i++)
            {
                TickTerminalApp(app);
            }

            Assert.AreSame(detailPane, GetCurrentDetailVisual(dialog), "The Test Server completion should not replace the selected details tab control before the user changes tabs.");
            app.Focus(detailPane);
            ((InMemoryTerminalBackend)session.Instance.Backend).PushEvent(new TerminalKeyEvent { Key = TerminalKey.Right });
            TickTerminalApp(app);
            for (var i = 0; i < 5; i++)
            {
                TickTerminalApp(app);
            }

            var currentDetailPane = GetCurrentDetailVisual(dialog);
            Assert.AreSame(detailPane, currentDetailPane, "Switching to the Tools tab should keep the selected details tab control attached.");
            Assert.AreEqual(1, detailPane.SelectedIndex);
            Assert.IsTrue(detailPane.Bounds.Width > 0 && detailPane.Bounds.Height > 0, "The detail tab control should remain arranged after selecting Tools.");
            var toolsScrollViewer = detailPane.EnumerateVisualsDepthFirst().OfType<ScrollViewer>().SingleOrDefault(viewer => viewer.Content is DataGridControl);
            Assert.IsNotNull(toolsScrollViewer, "The Tools tab should host the DataGrid in a scroll viewer so wide tool columns can scroll horizontally.");
            Assert.IsTrue(toolsScrollViewer.HorizontalScrollEnabled);
            StringAssert.Contains(((InMemoryTerminalBackend)session.Instance.Backend).GetOutText(), "mcp__tiny__echo");
        }
        finally
        {
            InvokeTerminalApp(app, "EndRun");
        }
    }

    private static void WriteTinyServerConfig(string root, string server, IReadOnlyDictionary<string, string>? extraEnv)
    {
        var path = McpConfigDiscovery.GetProjectConfigPath(root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var definition = new
        {
            mcpServers = new Dictionary<string, object>
            {
                [server] = new
                {
                    command = "dotnet",
                    args = new[] { TinyServerAssemblyPath },
                    env = extraEnv ?? new Dictionary<string, string>(),
                },
            },
        };
        File.WriteAllText(path, JsonSerializer.Serialize(definition));
    }

    private static void WriteProjectPolicy(string projectPath, string content)
    {
        var path = McpPolicyWriter.GetProjectPolicyPath(projectPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static IReadOnlyList<object> GetDialogRows(McpServersDialog dialog)
    {
        var field = typeof(McpServersDialog).GetField("_servers", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field);
        return ((IEnumerable)field.GetValue(dialog)!).Cast<object>().ToArray();
    }

    private static McpManagementServerSnapshot GetDialogRowEntry(object row)
    {
        var property = row.GetType().GetProperty("Entry", BindingFlags.Instance | BindingFlags.Public);
        Assert.IsNotNull(property);
        return (McpManagementServerSnapshot)property.GetValue(row)!;
    }

    private static Button FindDialogButton(McpServersDialog dialog, string text)
    {
        return GetDialogVisual(dialog).EnumerateVisualsDepthFirst()
            .OfType<Button>()
            .Single(button => button.Content is TextBlock textBlock && string.Equals(textBlock.Text, text, StringComparison.Ordinal));
    }

    private static Visual GetDialogVisual(McpServersDialog dialog)
        => (Visual)typeof(McpServersDialog).GetField("_dialog", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog)!;

    private static void InvokeDialogClose(McpServersDialog dialog)
    {
        var method = typeof(McpServersDialog).GetMethod("Close", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(method);
        method.Invoke(dialog, null);
    }

    private static int GetRenderedDialogRowCount(McpServersDialog dialog)
    {
        var serverList = typeof(McpServersDialog).GetField("_serverList", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog)!;
        var itemVisuals = serverList.GetType().GetField("_itemVisuals", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(serverList)!;
        return (int)itemVisuals.GetType().GetProperty("Count")!.GetValue(itemVisuals)!;
    }

    private static Visual GetCurrentDetailVisual(McpServersDialog dialog)
    {
        var detailHost = typeof(McpServersDialog).GetField("_detailHost", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog)!;
        return (Visual)detailHost.GetType().GetField("_currentChild", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(detailHost)!;
    }

    private static Task InvokeTestServerAsync(McpServersDialog dialog, object row)
    {
        var method = typeof(McpServersDialog).GetMethod("TestServerAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(method);
        return (Task)method.Invoke(dialog, [row])!;
    }

    private static void PumpTerminalUntil(TerminalApp app, Func<bool> predicate, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!predicate() && stopwatch.Elapsed < timeout)
        {
            TickTerminalApp(app);
            Thread.Sleep(10);
        }

        TickTerminalApp(app);
        Assert.IsTrue(predicate(), "Timed out waiting for the MCP dialog condition.");
    }

    private static void PumpTerminalUntilCompleted(TerminalApp app, Task task, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!task.IsCompleted && stopwatch.Elapsed < timeout)
        {
            TickTerminalApp(app);
            Thread.Sleep(10);
        }

        TickTerminalApp(app);
        Assert.IsTrue(task.IsCompleted, "Timed out waiting for the MCP Test Server dialog operation to complete.");
    }

    private static void TickTerminalApp(TerminalApp app)
    {
        var tickMethod = typeof(TerminalApp).GetMethod("Tick", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(tickMethod);
        tickMethod.Invoke(app, [null]);
    }

    private static void InvokeTerminalApp(TerminalApp app, string methodName)
    {
        var method = typeof(TerminalApp).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(method);
        method.Invoke(app, null);
    }

    private static string TinyServerAssemblyPath
        => Path.Combine(AppContext.BaseDirectory, "CodeAlta.Tests.TinyMcpServer.dll");

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory()
            => Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CodeAlta.McpManagementServiceTests." + Guid.NewGuid().ToString("N"));

        public string Path { get; }

        public static TempDirectory Create()
        {
            var directory = new TempDirectory();
            Directory.CreateDirectory(directory.Path);
            return directory;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
