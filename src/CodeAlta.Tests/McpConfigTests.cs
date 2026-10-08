using System.Globalization;
using System.Text;
using System.Text.Json;
using CodeAlta.Plugin.Mcp;
using CodeAlta.Plugins.Abstractions;
using CodeAlta.Plugins.Tui;
using XenoAtom.CommandLine;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;

namespace CodeAlta.Tests;

[TestClass]
public sealed class McpConfigTests
{
    // Global MCP configuration, policy and OAuth tokens resolve under this home, never the developer's real ~/.alta.
    private readonly TempDirectory _home = TempDirectory.Create();

    [TestCleanup]
    public void DisposeHome() => _home.Dispose();

    [TestMethod]
    public void Discovery_ParsesSupportedFormatsAndAppliesProjectOverlay()
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
                "global-only": { "command": "node", "args": ["server.js"], "env": { "API_TOKEN": "secret" } },
                "shared": { "command": "global" }
              }
            }
            """);
        File.WriteAllText(
            Path.Combine(project.Path, ".alta", "mcp.json"),
            """
            {
              "servers": {
                "shared": { "type": "stdio", "command": "project", "env": { "SAFE": "value" } },
                "remote": { "type": "sse", "url": "https://example.test/mcp", "headers": { "Authorization": "Bearer token" } }
              }
            }
            """);

        var snapshot = new McpConfigDiscovery().Discover(new McpConfigPathOptions
        {
            UserHomeDirectory = home.Path,
            ProjectDirectory = project.Path,
        });

        Assert.AreEqual(2, snapshot.Sources.Count);
        Assert.AreEqual(McpConfigFlavor.CodeAlta, snapshot.Sources[0].Flavor);
        Assert.AreEqual(McpConfigFlavor.Vscode, snapshot.Sources[1].Flavor);
        Assert.AreEqual(3, snapshot.EffectiveServers.Count);
        Assert.AreEqual(1, snapshot.ShadowedServers.Count);
        var shared = snapshot.EffectiveServers.Single(server => server.Definition.Key == "shared");
        Assert.AreEqual(McpConfigScope.Project, shared.Definition.SourceScope);
        Assert.IsTrue(shared.OverridesGlobal);
        Assert.AreEqual("project", shared.Definition.Command);
        Assert.AreEqual(McpTransportKind.Http, snapshot.EffectiveServers.Single(server => server.Definition.Key == "remote").Definition.Transport);
    }

    [TestMethod]
    public void Discovery_ReportsInvalidMixedTransportWithoutThrowing()
    {
        using var home = TempDirectory.Create();
        Directory.CreateDirectory(Path.Combine(home.Path, ".alta"));
        File.WriteAllText(
            Path.Combine(home.Path, ".alta", "mcp.json"),
            """
            { "mcpServers": { "bad": { "command": "npx", "url": "https://example.test/mcp" } } }
            """);

        var snapshot = new McpConfigDiscovery().Discover(new McpConfigPathOptions { UserHomeDirectory = home.Path });

        Assert.AreEqual(1, snapshot.Sources.Count);
        Assert.IsFalse(snapshot.Sources[0].IsValid);
        StringAssert.Contains(snapshot.Sources[0].Diagnostic!, "both 'command' and 'url'");
        Assert.AreEqual(0, snapshot.EffectiveServers.Count);
    }

    [TestMethod]
    public void Discovery_ReadsTheFilesOfOtherToolsBelowTheFileOfCodeAltaOfTheSameScope()
    {
        using var home = TempDirectory.Create();
        using var project = TempDirectory.Create();
        var projectPath = Path.GetFullPath(project.Path);
        WriteFile(Path.Combine(home.Path, ".alta", "mcp.json"), """{ "mcpServers": { "user": { "command": "alta-user" }, "both-user": { "command": "alta-user" } } }""");
        // GitHub Copilot CLI writes a server it starts as "local", with the tools it may use.
        WriteFile(Path.Combine(home.Path, ".copilot", "mcp-config.json"), """
            { "mcpServers": {
                "both-user": { "type": "local", "command": "copilot-user", "tools": ["*"] },
                "copilot-user": { "type": "local", "command": "npx", "args": ["-y", "server"], "tools": ["*"] },
                "shared": { "type": "http", "url": "https://user.example.test/mcp" } } }
            """);
        WriteFile(Path.Combine(projectPath, ".alta", "mcp.json"), """{ "mcpServers": { "everywhere": { "command": "alta-project" } } }""");
        WriteFile(Path.Combine(projectPath, ".mcp.json"), """
            { "mcpServers": {
                "everywhere": { "command": "common" },
                "shared": { "type": "http", "url": "https://${DOCS_HOST:-docs.example.test}/mcp", "headers": { "Authorization": "Bearer ${DOCS_TOKEN}" } },
                "relative": { "command": "node", "args": ["server.js"], "cwd": "tools" } } }
            """);
        // A project file of GitHub Copilot CLI can be the map of the servers itself.
        WriteFile(Path.Combine(projectPath, ".github", "mcp.json"), """
            { "everywhere": { "command": "copilot" }, "github-only": { "type": "local", "command": "gh-mcp" } }
            """);
        // Visual Studio Code accepts comments and trailing commas, and has variables of its own.
        WriteFile(Path.Combine(projectPath, ".vscode", "mcp.json"), """
            {
              // The servers of this workspace.
              "inputs": [{ "id": "token", "type": "promptString", "password": true }],
              "servers": {
                "everywhere": { "type": "stdio", "command": "vscode" },
                "workspace": {
                  "type": "stdio",
                  "command": "${workspaceFolder}/bin/server",
                  "args": ["--home", "${userHome}", "--name", "${workspaceFolderBasename}", "--key", "${env:API_KEY}"],
                  "env": { "PORT": 8080, "UNSET": null, "TOKEN": "${env:API_TOKEN}" },
                },
                "asks": { "type": "http", "url": "https://example.test/mcp", "headers": { "Authorization": "Bearer ${input:token}" } },
                "from-file": { "type": "stdio", "command": "node", "envFile": "${workspaceFolder}/.env" },
                "other-variable": { "type": "stdio", "command": "node", "args": ["${config:editor.tabSize}"] },
                "bare-name": { "type": "stdio", "command": "node", "args": ["${HOME}"] },
                "no-transport": { "type": "stdio" },
              },
            }
            """);

        var snapshot = new McpConfigDiscovery().Discover(new McpConfigPathOptions { UserHomeDirectory = home.Path, ProjectDirectory = projectPath });

        Assert.AreEqual(2, snapshot.Sources.Count, "The files CodeAlta writes are still the two of its own.");
        Assert.IsTrue(snapshot.Sources.All(static source => source.Origin == McpConfigOrigin.CodeAlta));
        CollectionAssert.AreEqual(
            new[] { (McpConfigScope.Project, McpConfigOrigin.Common), (McpConfigScope.Project, McpConfigOrigin.Copilot), (McpConfigScope.Project, McpConfigOrigin.Vscode), (McpConfigScope.Global, McpConfigOrigin.Copilot) },
            snapshot.ExternalSources.Select(static source => (source.Scope, source.Origin)).ToArray());
        Assert.IsTrue(snapshot.ExternalSources.All(static source => source.IsValid && !source.IsWritable));

        McpServerDefinition Effective(string key) => snapshot.EffectiveServers.Single(server => server.Definition.Key == key).Definition;
        Assert.AreEqual("alta-project", Effective("everywhere").Command, "The file of CodeAlta comes first in its scope.");
        CollectionAssert.AreEqual(
            new[] { "common", "copilot", "vscode" },
            snapshot.ShadowedServers.Where(static server => server.Definition.Key == "everywhere").Select(static server => server.Definition.Command).ToArray(),
            "Then .mcp.json, .github/mcp.json and .vscode/mcp.json.");
        Assert.AreEqual("alta-user", Effective("both-user").Command);
        Assert.AreEqual(McpConfigOrigin.Copilot, snapshot.ShadowedServers.Single(static server => server.Definition.Key == "both-user").Definition.SourceOrigin);
        var shared = snapshot.EffectiveServers.Single(static server => server.Definition.Key == "shared");
        Assert.AreEqual(McpConfigOrigin.Common, shared.Definition.SourceOrigin, "A project comes before the user, whose file it is.");
        Assert.IsTrue(shared.OverridesGlobal);
        Assert.AreEqual("https://${DOCS_HOST:-docs.example.test}/mcp", shared.Definition.Url, "An environment variable is resolved when the server starts.");
        Assert.AreEqual("Bearer ${DOCS_TOKEN}", shared.Definition.Headers["Authorization"]);
        Assert.AreEqual(McpConfigOrigin.Copilot, Effective("copilot-user").SourceOrigin);
        Assert.AreEqual(McpTransportKind.Stdio, Effective("copilot-user").Transport);
        Assert.IsNull(Effective("copilot-user").Cwd, "A server of the user has no project to start in.");
        Assert.AreEqual(McpConfigOrigin.Copilot, Effective("github-only").SourceOrigin);
        Assert.AreEqual(projectPath, Effective("github-only").Cwd, "A server of a project starts in the folder of the project.");
        Assert.AreEqual(Path.Combine(projectPath, "tools"), Effective("relative").Cwd);

        var workspace = Effective("workspace");
        Assert.AreEqual(McpConfigOrigin.Vscode, workspace.SourceOrigin);
        Assert.AreEqual(projectPath + "/bin/server", workspace.Command);
        CollectionAssert.AreEqual(
            new[] { "--home", home.Path, "--name", Path.GetFileName(projectPath), "--key", "${API_KEY}" },
            workspace.Args.ToArray());
        Assert.AreEqual("8080", workspace.Env["PORT"]);
        Assert.AreEqual("${API_TOKEN}", workspace.Env["TOKEN"]);
        Assert.IsFalse(workspace.Env.ContainsKey("UNSET"));

        var skipped = snapshot.ExternalSources.Single(static source => source.Origin == McpConfigOrigin.Vscode).SkippedServers;
        CollectionAssert.AreEquivalent(
            new[]
            {
                ("asks", McpSkipReason.InputVariable), ("from-file", McpSkipReason.EnvironmentFile), ("other-variable", McpSkipReason.UnknownVariable),
                ("bare-name", McpSkipReason.UnknownVariable), ("no-transport", McpSkipReason.Invalid),
            },
            skipped.Select(static server => (server.Key, server.Reason)).ToArray());
        StringAssert.Contains(skipped.Single(static server => server.Key == "asks").Message, "${input:token}");
        Assert.IsFalse(snapshot.EffectiveServers.Any(server => skipped.Any(left => left.Key == server.Definition.Key)));
    }

    [TestMethod]
    public void Discovery_AFileOfAnotherToolThatCannotBeReadIsReportedAndLeavesTheOthersInEffect()
    {
        using var home = TempDirectory.Create();
        using var project = TempDirectory.Create();
        WriteFile(Path.Combine(project.Path, ".mcp.json"), "{ broken");
        WriteFile(Path.Combine(project.Path, ".github", "mcp.json"), """{ "mcpServers": { "docs": { "url": "https://example.test/mcp" } } }""");
        WriteFile(Path.Combine(project.Path, ".vscode", "mcp.json"), """{ "name": "not a list of servers" }""");

        var snapshot = new McpConfigDiscovery().Discover(new McpConfigPathOptions { UserHomeDirectory = home.Path, ProjectDirectory = project.Path });

        CollectionAssert.AreEqual(new[] { false, true, false }, snapshot.ExternalSources.Select(static source => source.IsValid).ToArray());
        Assert.IsFalse(string.IsNullOrWhiteSpace(snapshot.ExternalSources[0].Diagnostic));
        Assert.AreEqual("docs", snapshot.EffectiveServers.Single().Definition.Key);
        Assert.IsTrue(snapshot.Sources.All(static source => !source.Exists), "A file of another tool is no reason to create one of CodeAlta.");
    }

    [TestMethod]
    public void FormatAdapter_AFileOfCodeAltaIsStillReadStrictly()
    {
        Assert.ThrowsExactly<InvalidDataException>(static () => McpConfigFormatAdapter.ParseDocument("""{ "memory": { "command": "npx" } }"""));
        Assert.Throws<JsonException>(static () => McpConfigFormatAdapter.ParseDocument("""
            {
              // A comment would be lost when CodeAlta writes the file again.
              "mcpServers": {}
            }
            """));
        // The stdio type of GitHub Copilot CLI is read in any file.
        var local = McpConfigFormatAdapter.ParseDocument("""{ "mcpServers": { "memory": { "type": "local", "command": "npx" } } }""");
        Assert.AreEqual(McpConfigFlavor.Copilot, local.Flavor);
        Assert.AreEqual(McpTransportKind.Stdio, McpConfigFormatAdapter.ReadServers(local, McpConfigScope.Global, "mcp.json").Single().Transport);
    }

    [TestMethod]
    public void FormatAdapter_DetectsSupportedJsonFlavors()
    {
        var cases = new[]
        {
            new
            {
                Json = """
                { "mcpServers": { "memory": { "command": "npx" } } }
                """,
                Flavor = McpConfigFlavor.CodeAlta,
                RootKey = "mcpServers",
                Transport = McpTransportKind.Stdio,
            },
            new
            {
                Json = """
                { "mcpServers": { "github": { "command": "docker", "tools": ["*"] } } }
                """,
                Flavor = McpConfigFlavor.Copilot,
                RootKey = "mcpServers",
                Transport = McpTransportKind.Stdio,
            },
            new
            {
                Json = """
                { "servers": { "docs": { "type": "sse", "url": "https://example.test/mcp" } } }
                """,
                Flavor = McpConfigFlavor.Vscode,
                RootKey = "servers",
                Transport = McpTransportKind.Http,
            },
            new
            {
                Json = """
                { "mcpServers": { "filesystem": { "type": "stdio", "command": "node" } } }
                """,
                Flavor = McpConfigFlavor.Claude,
                RootKey = "mcpServers",
                Transport = McpTransportKind.Stdio,
            },
            new
            {
                Json = """
                { "mcpServers": { "remote": { "url": "https://example.test/mcp" } } }
                """,
                Flavor = McpConfigFlavor.Intellij,
                RootKey = "mcpServers",
                Transport = McpTransportKind.Http,
            },
        };

        foreach (var item in cases)
        {
            var document = McpConfigFormatAdapter.ParseDocument(item.Json);
            var server = McpConfigFormatAdapter.ReadServers(document, McpConfigScope.Project, "mcp.json").Single();

            Assert.AreEqual(item.Flavor, document.Flavor, item.Json);
            Assert.AreEqual(item.RootKey, document.RootKey, item.Json);
            Assert.AreEqual(item.Transport, server.Transport, item.Json);
        }
    }

    [TestMethod]
    public void FormatAdapter_ParsesOAuthOptionsWithoutSecretsInHeaders()
    {
        var document = McpConfigFormatAdapter.ParseDocument(
            """
            {
              "mcpServers": {
                "docs": {
                  "url": "https://example.test/mcp",
                  "auth": {
                    "type": "oauth",
                    "clientId": "codealta-test",
                    "scopes": ["read", "search"],
                    "redirectUri": "http://127.0.0.1:1456/mcp/oauth/callback"
                  }
                }
              }
            }
            """);

        var server = McpConfigFormatAdapter.ReadServers(document, McpConfigScope.Project, "mcp.json").Single();

        Assert.AreEqual(McpTransportKind.Http, server.Transport);
        Assert.IsNotNull(server.OAuth);
        Assert.IsTrue(server.OAuth.Enabled);
        Assert.AreEqual("codealta-test", server.OAuth.ClientId);
        CollectionAssert.AreEqual(new[] { "read", "search" }, server.OAuth.Scopes.ToArray());
        Assert.AreEqual(0, server.Headers.Count);
    }

    [TestMethod]
    public void FormatAdapter_RemovesOAuthSettingsWhenRenderingStdioServer()
    {
        var document = McpConfigFormatAdapter.ParseDocument(
            """
            {
              "mcpServers": {
                "docs": {
                  "url": "https://example.test/mcp",
                  "auth": { "type": "oauth", "client_secret": "secret-value" }
                }
              }
            }
            """);

        McpConfigFormatAdapter.AddOrUpdateServer(
            document,
            new McpServerDefinition
            {
                Key = "docs",
                Transport = McpTransportKind.Stdio,
                SourceScope = McpConfigScope.Project,
                SourcePath = "mcp.json",
                SourceFlavor = McpConfigFlavor.CodeAlta,
                Command = "node",
            });
        var rendered = McpConfigFormatAdapter.Serialize(document.Root);
        var server = McpConfigFormatAdapter.ReadServers(document, McpConfigScope.Project, "mcp.json").Single();

        Assert.AreEqual(McpTransportKind.Stdio, server.Transport);
        Assert.IsNull(server.OAuth);
        Assert.IsFalse(rendered.Contains("\"auth\"", StringComparison.Ordinal));
        Assert.IsFalse(rendered.Contains("secret-value", StringComparison.Ordinal));
    }

    [TestMethod]
    public void FormatAdapter_RejectsAmbiguousRootKeys()
    {
        var exception = Assert.ThrowsExactly<InvalidDataException>(static () => McpConfigFormatAdapter.ParseDocument(
            """
            { "mcpServers": {}, "servers": {} }
            """));

        StringAssert.Contains(exception.Message, "both 'mcpServers' and 'servers'");
    }

    [TestMethod]
    public async Task Writer_CreatesMissingDefaultFileAndRemovesServer()
    {
        using var project = TempDirectory.Create();
        var path = McpConfigDiscovery.GetProjectConfigPath(project.Path);
        var definition = new McpServerDefinition
        {
            Key = "memory",
            Transport = McpTransportKind.Stdio,
            SourceScope = McpConfigScope.Project,
            SourcePath = path,
            SourceFlavor = McpConfigFlavor.CodeAlta,
            Command = "npx",
            Args = ["-y", "@modelcontextprotocol/server-memory"],
        };
        var writer = new McpConfigWriter();

        var addResult = await writer.AddOrUpdateServerAsync(path, McpConfigScope.Project, definition, CancellationToken.None);

        Assert.IsTrue(addResult.CreatedFile);
        Assert.IsTrue(File.Exists(path));
        using (var document = JsonDocument.Parse(File.ReadAllText(path)))
        {
            var root = document.RootElement;
            Assert.IsTrue(root.TryGetProperty("mcpServers", out var servers));
            Assert.AreEqual("npx", servers.GetProperty("memory").GetProperty("command").GetString());
        }

        var removeResult = await writer.RemoveServerAsync(path, McpConfigScope.Project, "memory", CancellationToken.None);

        Assert.IsTrue(removeResult.Changed);
        using var removedDocument = JsonDocument.Parse(File.ReadAllText(path));
        Assert.IsFalse(removedDocument.RootElement.GetProperty("mcpServers").TryGetProperty("memory", out _));
    }

    [TestMethod]
    public async Task Writer_PreservesUnknownFieldsAndCopilotToolsOnUpdate()
    {
        using var home = TempDirectory.Create();
        var path = McpConfigDiscovery.GetGlobalConfigPath(home.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(
            path,
            """
            {
              "unknownRoot": true,
              "mcpServers": {
                "github": { "command": "old", "tools": ["*"], "unknownServer": 42 }
              }
            }
            """);
        var definition = new McpServerDefinition
        {
            Key = "github",
            Transport = McpTransportKind.Http,
            SourceScope = McpConfigScope.Global,
            SourcePath = path,
            SourceFlavor = McpConfigFlavor.Copilot,
            Url = "https://example.test/mcp",
            Headers = new Dictionary<string, string>(StringComparer.Ordinal) { ["Authorization"] = "Bearer token" },
        };

        await new McpConfigWriter().AddOrUpdateServerAsync(path, McpConfigScope.Global, definition, CancellationToken.None);

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        Assert.IsTrue(root.GetProperty("unknownRoot").GetBoolean());
        var server = root.GetProperty("mcpServers").GetProperty("github");
        Assert.AreEqual(42, server.GetProperty("unknownServer").GetInt32());
        Assert.AreEqual("http", server.GetProperty("type").GetString());
        Assert.AreEqual("*", server.GetProperty("tools")[0].GetString());
        Assert.IsFalse(server.TryGetProperty("command", out _));
    }

    [TestMethod]
    public async Task Writer_PreservesFlavorQuirksAcrossSupportedFormats()
    {
        using var temp = TempDirectory.Create();
        var cases = new[]
        {
            new FlavorWriteCase(
                "codealta",
                """
                { "mcpServers": { "existing": { "command": "old" } }, "rootKeep": true }
                """,
                "mcpServers",
                "http"),
            new FlavorWriteCase(
                "copilot",
                """
                { "mcpServers": { "existing": { "command": "old", "tools": ["*"] } }, "rootKeep": true }
                """,
                "mcpServers",
                "http"),
            new FlavorWriteCase(
                "vscode",
                """
                { "servers": { "existing": { "type": "stdio", "command": "old" } }, "rootKeep": true }
                """,
                "servers",
                "sse"),
            new FlavorWriteCase(
                "claude",
                """
                { "mcpServers": { "existing": { "type": "stdio", "command": "old" } }, "rootKeep": true }
                """,
                "mcpServers",
                "http"),
            new FlavorWriteCase(
                "intellij",
                """
                { "mcpServers": { "existing": { "url": "https://old.example.test/mcp" } }, "rootKeep": true }
                """,
                "mcpServers",
                null),
        };

        foreach (var item in cases)
        {
            var path = Path.Combine(temp.Path, item.Name, ".alta", "mcp.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, item.Json);
            var definition = new McpServerDefinition
            {
                Key = "remote",
                Transport = McpTransportKind.Http,
                SourceScope = McpConfigScope.Project,
                SourcePath = path,
                SourceFlavor = McpConfigFlavor.CodeAlta,
                Url = "https://example.test/mcp",
            };

            await new McpConfigWriter().AddOrUpdateServerAsync(path, McpConfigScope.Project, definition, CancellationToken.None);

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            Assert.IsTrue(root.GetProperty("rootKeep").GetBoolean(), item.Name);
            Assert.IsTrue(root.TryGetProperty(item.RootKey, out var servers), item.Name);
            var server = servers.GetProperty("remote");
            Assert.AreEqual("https://example.test/mcp", server.GetProperty("url").GetString(), item.Name);
            if (item.ExpectedHttpType is null)
            {
                Assert.IsFalse(server.TryGetProperty("type", out _), item.Name);
            }
            else
            {
                Assert.AreEqual(item.ExpectedHttpType, server.GetProperty("type").GetString(), item.Name);
            }

            if (item.Name == "copilot")
            {
                Assert.AreEqual("*", server.GetProperty("tools")[0].GetString(), item.Name);
            }
        }
    }

    [TestMethod]
    public async Task PolicyWriter_CreatesAndUpdatesServerEnabledPolicy()
    {
        using var project = TempDirectory.Create();
        var path = McpPolicyWriter.GetProjectPolicyPath(project.Path);

        var disable = await new McpPolicyWriter().SetServerEnabledAsync(path, McpConfigScope.Project, "docs", enabled: false, CancellationToken.None);
        var disabledPolicy = new McpPolicyLoader().Load(null, path);
        var enable = await new McpPolicyWriter().SetServerEnabledAsync(path, McpConfigScope.Project, "docs", enabled: true, CancellationToken.None);
        var enabledPolicy = new McpPolicyLoader().Load(null, path);

        Assert.IsTrue(disable.CreatedFile);
        Assert.IsFalse(disabledPolicy.Servers["docs"].Enabled!.Value);
        Assert.IsFalse(enable.CreatedFile);
        Assert.IsTrue(enabledPolicy.Servers["docs"].Enabled!.Value);
    }

    [TestMethod]
    public void PolicyLoader_AppliesProjectOverlayForPluginAndServerSettings()
    {
        using var home = TempDirectory.Create();
        using var project = TempDirectory.Create();
        var globalConfig = Path.Combine(home.Path, ".alta", "config.toml");
        var projectConfig = Path.Combine(project.Path, ".alta", "config.toml");
        Directory.CreateDirectory(Path.GetDirectoryName(globalConfig)!);
        Directory.CreateDirectory(Path.GetDirectoryName(projectConfig)!);
        File.WriteAllText(
            globalConfig,
            """
            [plugins.mcp]
            enabled = true
            prompt_max_servers = 5
            direct_exposure = "auto"

            [plugins.mcp.servers.github]
            enabled = true
            disabled_tools = ["delete_repository"]
            """);
        File.WriteAllText(
            projectConfig,
            """
            [plugins.mcp]
            prompt_max_servers = 2

            [plugins.mcp.servers.github]
            enabled = false
            direct_tools = ["create_issue"]
            """);

        var policy = new McpPolicyLoader().Load(globalConfig, projectConfig);

        Assert.IsTrue(policy.Enabled);
        Assert.AreEqual(2, policy.PromptMaxServers);
        var github = policy.Servers["github"];
        Assert.AreEqual(false, github.Enabled);
        CollectionAssert.AreEqual(new[] { "delete_repository" }, github.DisabledTools.ToArray());
        CollectionAssert.AreEqual(new[] { "create_issue" }, github.DirectTools.ToArray());
    }

    [TestMethod]
    public async Task PluginCommand_ListEmitsConfiguredProjectServer()
    {
        using var project = TempDirectory.Create();
        Directory.CreateDirectory(Path.Combine(project.Path, ".alta"));
        File.WriteAllText(
            Path.Combine(project.Path, ".alta", "mcp.json"),
            """
            { "mcpServers": { "memory": { "command": "npx" } } }
            """);
        var plugin = new McpPlugin(createPresentation: null, _home.Path);
        var contribution = plugin.GetAltaCommands().Single();
        var stdout = new StringWriter(CultureInfo.InvariantCulture);
        var stderr = new StringWriter(CultureInfo.InvariantCulture);
        var app = new CommandApp("alta", "test") { contribution.CreateCommandNode(CreateAltaContext(stdout, stderr, project.Path)) };

        var exitCode = await app.RunAsync(["mcp", "list"], new CommandRunConfig { Out = TextWriter.Null, Error = stderr });

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(string.Empty, stderr.ToString());
        StringAssert.Contains(stdout.ToString(), "\"type\":\"alta.mcp.server\"");
        StringAssert.Contains(stdout.ToString(), "\"server\":\"memory\"");
    }

    [TestMethod]
    public async Task PluginCommand_HelpShowsServerCommandGroup()
    {
        var plugin = new McpPlugin(createPresentation: null, _home.Path);
        var contribution = plugin.GetAltaCommands().Single();
        var stdout = new StringWriter(CultureInfo.InvariantCulture);
        var stderr = new StringWriter(CultureInfo.InvariantCulture);
        var app = new CommandApp("alta", "test") { contribution.CreateCommandNode(CreateAltaContext(stdout, stderr, null)) };

        var exitCode = await app.RunAsync(["mcp", "server", "--help"], new CommandRunConfig { Out = stdout, Error = stderr });

        Assert.AreEqual(0, exitCode);
        StringAssert.Contains(stdout.ToString(), "add");
        StringAssert.Contains(stdout.ToString(), "remove");
        StringAssert.Contains(stdout.ToString(), "enable");
        StringAssert.Contains(stdout.ToString(), "disable");
    }

    [TestMethod]
    public async Task PluginCommand_ConfigSourcesReportsSourcesOverlayShadowingAndDefaultWriteScope()
    {
        using var home = TempDirectory.Create();
        using var project = TempDirectory.Create();
        var stdout = new StringWriter(CultureInfo.InvariantCulture);
        var stderr = new StringWriter(CultureInfo.InvariantCulture);
        var context = CreateAltaContext(stdout, stderr, project.Path);
        var app = new CommandApp("alta", "test")
        {
            McpCommandFactory.CreateCommand(context, new McpCommandFactoryOptions { UserHomeDirectory = home.Path }),
        };

        var missingExitCode = await app.RunAsync(["mcp", "config", "sources", "--include-missing"], new CommandRunConfig { Out = TextWriter.Null, Error = stderr });
        var missingRecords = ReadJsonLines(stdout.ToString());

        Assert.AreEqual(0, missingExitCode, stderr.ToString());
        var missingSources = missingRecords.Where(static line => line.GetProperty("type").GetString() == "alta.mcp.config.source").ToArray();
        Assert.AreEqual(2, missingSources.Length);
        var missingGlobal = missingSources.Single(static line => line.GetProperty("scope").GetString() == "global");
        var missingProject = missingSources.Single(static line => line.GetProperty("scope").GetString() == "project");
        Assert.AreEqual(McpConfigDiscovery.GetGlobalConfigPath(home.Path), missingGlobal.GetProperty("path").GetString());
        Assert.AreEqual(McpConfigDiscovery.GetProjectConfigPath(project.Path), missingProject.GetProperty("path").GetString());
        Assert.IsFalse(missingGlobal.GetProperty("exists").GetBoolean());
        Assert.IsFalse(missingProject.GetProperty("exists").GetBoolean());
        Assert.AreEqual("project", missingGlobal.GetProperty("defaultWriteScope").GetString());
        Assert.AreEqual("project", missingProject.GetProperty("defaultWriteScope").GetString());

        stdout.GetStringBuilder().Clear();
        stderr.GetStringBuilder().Clear();
        Directory.CreateDirectory(Path.Combine(home.Path, ".alta"));
        Directory.CreateDirectory(Path.Combine(project.Path, ".alta"));
        File.WriteAllText(
            McpConfigDiscovery.GetGlobalConfigPath(home.Path),
            """
            { "mcpServers": { "shared": { "command": "global" }, "global-only": { "command": "node" } } }
            """);
        File.WriteAllText(
            McpConfigDiscovery.GetProjectConfigPath(project.Path),
            """
            { "servers": { "shared": { "type": "stdio", "command": "project" }, "project-only": { "type": "sse", "url": "https://example.test/mcp" } } }
            """);

        var presentExitCode = await app.RunAsync(["mcp", "config", "sources", "--include-missing"], new CommandRunConfig { Out = TextWriter.Null, Error = stderr });
        var records = ReadJsonLines(stdout.ToString());

        Assert.AreEqual(0, presentExitCode, stderr.ToString());
        var sources = records.Where(static line => line.GetProperty("type").GetString() == "alta.mcp.config.source").ToArray();
        Assert.AreEqual(2, sources.Length);
        var globalSource = sources.Single(static line => line.GetProperty("scope").GetString() == "global");
        var projectSource = sources.Single(static line => line.GetProperty("scope").GetString() == "project");
        Assert.IsTrue(globalSource.GetProperty("exists").GetBoolean());
        Assert.IsTrue(projectSource.GetProperty("exists").GetBoolean());
        Assert.AreEqual("codealta", globalSource.GetProperty("format").GetString());
        Assert.AreEqual("mcpServers", globalSource.GetProperty("rootKey").GetString());
        CollectionAssert.AreEqual(new[] { "global-only", "shared" }, ReadStringArray(globalSource.GetProperty("serverKeys")).ToArray());
        Assert.AreEqual("vscode", projectSource.GetProperty("format").GetString());
        Assert.AreEqual("servers", projectSource.GetProperty("rootKey").GetString());
        CollectionAssert.AreEqual(new[] { "project-only", "shared" }, ReadStringArray(projectSource.GetProperty("serverKeys")).ToArray());
        Assert.AreEqual("project", globalSource.GetProperty("defaultWriteScope").GetString());
        Assert.AreEqual("project", projectSource.GetProperty("defaultWriteScope").GetString());

        var effectiveShared = records.Single(static line => line.GetProperty("type").GetString() == "alta.mcp.config.effective_server" && line.GetProperty("server").GetString() == "shared");
        Assert.AreEqual("project", effectiveShared.GetProperty("sourceScope").GetString());
        Assert.AreEqual("project-overrides-global", effectiveShared.GetProperty("overlay").GetString());
        Assert.AreEqual(McpConfigDiscovery.GetGlobalConfigPath(home.Path), effectiveShared.GetProperty("shadowedGlobalPath").GetString());
        var shadowed = records.Single(static line => line.GetProperty("type").GetString() == "alta.mcp.config.shadowed_server");
        Assert.AreEqual("shared", shadowed.GetProperty("server").GetString());
        Assert.AreEqual("global", shadowed.GetProperty("sourceScope").GetString());
        Assert.AreEqual(McpConfigDiscovery.GetGlobalConfigPath(home.Path), shadowed.GetProperty("sourcePath").GetString());
        Assert.AreEqual("project-overrides-global", shadowed.GetProperty("reason").GetString());

        stdout.GetStringBuilder().Clear();
        stderr.GetStringBuilder().Clear();
        var globalOnlyContext = CreateAltaContext(stdout, stderr, projectPath: null);
        var globalOnlyApp = new CommandApp("alta", "test")
        {
            McpCommandFactory.CreateCommand(globalOnlyContext, new McpCommandFactoryOptions { UserHomeDirectory = home.Path }),
        };

        var globalOnlyExitCode = await globalOnlyApp.RunAsync(["mcp", "config", "sources", "--include-missing"], new CommandRunConfig { Out = TextWriter.Null, Error = stderr });
        var globalOnlySource = ReadJsonLines(stdout.ToString()).Single(static line => line.GetProperty("type").GetString() == "alta.mcp.config.source");

        Assert.AreEqual(0, globalOnlyExitCode, stderr.ToString());
        Assert.AreEqual("global", globalOnlySource.GetProperty("scope").GetString());
        Assert.AreEqual("global", globalOnlySource.GetProperty("defaultWriteScope").GetString());
    }

    [TestMethod]
    public async Task PluginCommand_ServerAddDefaultsToProjectAndCreatesMissingStdioConfig()
    {
        using var project = TempDirectory.Create();
        var plugin = new McpPlugin(createPresentation: null, _home.Path);
        var contribution = plugin.GetAltaCommands().Single();
        var stdout = new StringWriter(CultureInfo.InvariantCulture);
        var stderr = new StringWriter(CultureInfo.InvariantCulture);
        var app = new CommandApp("alta", "test") { contribution.CreateCommandNode(CreateAltaContext(stdout, stderr, project.Path)) };

        var exitCode = await app.RunAsync(
            ["mcp", "server", "add", "memory", "--command", "npx", "--arg", "-y", "--arg", "@modelcontextprotocol/server-memory", "--cwd", "tools", "--env", "API_TOKEN=secret"],
            new CommandRunConfig { Out = TextWriter.Null, Error = stderr });

        Assert.AreEqual(0, exitCode, stderr.ToString());
        var path = McpConfigDiscovery.GetProjectConfigPath(project.Path);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var server = document.RootElement.GetProperty("mcpServers").GetProperty("memory");
        Assert.AreEqual("npx", server.GetProperty("command").GetString());
        Assert.AreEqual("-y", server.GetProperty("args")[0].GetString());
        Assert.AreEqual("@modelcontextprotocol/server-memory", server.GetProperty("args")[1].GetString());
        Assert.AreEqual("tools", server.GetProperty("cwd").GetString());
        Assert.AreEqual("secret", server.GetProperty("env").GetProperty("API_TOKEN").GetString());
        StringAssert.Contains(stdout.ToString(), "\"scope\":\"project\"");
        StringAssert.Contains(stdout.ToString(), "\"createdFile\":true");
    }

    [TestMethod]
    public async Task PluginCommand_ServerAddWritesHttpAndPreservesVscodeFlavor()
    {
        using var project = TempDirectory.Create();
        var path = McpConfigDiscovery.GetProjectConfigPath(project.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ \"servers\": { \"old\": { \"type\": \"stdio\", \"command\": \"node\" } } }");
        var plugin = new McpPlugin(createPresentation: null, _home.Path);
        var contribution = plugin.GetAltaCommands().Single();
        var stdout = new StringWriter(CultureInfo.InvariantCulture);
        var stderr = new StringWriter(CultureInfo.InvariantCulture);
        var app = new CommandApp("alta", "test") { contribution.CreateCommandNode(CreateAltaContext(stdout, stderr, project.Path)) };

        var exitCode = await app.RunAsync(
            ["mcp", "server", "add", "docs", "--url", "https://example.test/mcp", "--header", "Authorization=Bearer token"],
            new CommandRunConfig { Out = TextWriter.Null, Error = stderr });

        Assert.AreEqual(0, exitCode, stderr.ToString());
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var server = document.RootElement.GetProperty("servers").GetProperty("docs");
        Assert.AreEqual("sse", server.GetProperty("type").GetString());
        Assert.AreEqual("https://example.test/mcp", server.GetProperty("url").GetString());
        Assert.AreEqual("Bearer token", server.GetProperty("headers").GetProperty("Authorization").GetString());
        StringAssert.Contains(stdout.ToString(), "\"format\":\"vscode\"");
    }

    [TestMethod]
    public async Task PluginCommand_AuthStatusAndLogoutUseTokenCacheWithoutPrintingSecrets()
    {
        using var home = TempDirectory.Create();
        using var project = TempDirectory.Create();
        Directory.CreateDirectory(Path.Combine(project.Path, ".alta"));
        File.WriteAllText(
            McpConfigDiscovery.GetProjectConfigPath(project.Path),
            """
            { "mcpServers": { "docs": { "url": "https://example.test/mcp", "auth": { "type": "oauth" } } } }
            """);
        var tokenPath = McpOAuthTokenCache.GetTokenPath(home.Path, "docs", "https://example.test/mcp");
        Directory.CreateDirectory(Path.GetDirectoryName(tokenPath)!);
        File.WriteAllText(tokenPath, "{\"access_token\":\"secret-token\",\"token_type\":\"Bearer\"}");
        var stdout = new StringWriter(CultureInfo.InvariantCulture);
        var stderr = new StringWriter(CultureInfo.InvariantCulture);
        var context = CreateAltaContext(stdout, stderr, project.Path);
        var app = new CommandApp("alta", "test")
        {
            McpCommandFactory.CreateCommand(context, new McpCommandFactoryOptions { UserHomeDirectory = home.Path }),
        };

        var statusExitCode = await app.RunAsync(["mcp", "auth", "status", "--server", "docs"], new CommandRunConfig { Out = TextWriter.Null, Error = stderr });
        var statusText = stdout.ToString();
        var status = ReadJsonLines(statusText).Single(static line => line.GetProperty("type").GetString() == "alta.mcp.auth.status");

        Assert.AreEqual(0, statusExitCode, stderr.ToString());
        Assert.IsTrue(status.GetProperty("tokenCached").GetBoolean());
        Assert.IsFalse(statusText.Contains("secret-token", StringComparison.Ordinal));

        stdout.GetStringBuilder().Clear();
        stderr.GetStringBuilder().Clear();
        var logoutExitCode = await app.RunAsync(["mcp", "auth", "logout", "docs"], new CommandRunConfig { Out = TextWriter.Null, Error = stderr });
        var logout = ReadJsonLines(stdout.ToString()).Single(static line => line.GetProperty("type").GetString() == "alta.mcp.auth.logout");

        Assert.AreEqual(0, logoutExitCode, stderr.ToString());
        Assert.IsTrue(logout.GetProperty("removed").GetBoolean());
        Assert.IsFalse(File.Exists(tokenPath));
    }

    [TestMethod]
    public async Task PluginCommand_AuthLoginRejectsStdioServersWithoutStartingThem()
    {
        using var project = TempDirectory.Create();
        Directory.CreateDirectory(Path.Combine(project.Path, ".alta"));
        File.WriteAllText(
            McpConfigDiscovery.GetProjectConfigPath(project.Path),
            """
            { "mcpServers": { "local": { "command": "definitely-not-started-by-auth-login" } } }
            """);
        var stdout = new StringWriter(CultureInfo.InvariantCulture);
        var stderr = new StringWriter(CultureInfo.InvariantCulture);
        var context = CreateAltaContext(stdout, stderr, project.Path);
        var app = new CommandApp("alta", "test")
        {
            McpCommandFactory.CreateCommand(context, new McpCommandFactoryOptions()),
        };

        var exitCode = await app.RunAsync(["mcp", "auth", "login", "local"], new CommandRunConfig { Out = TextWriter.Null, Error = stderr });
        var error = ReadJsonLines(stdout.ToString()).Single(static line => line.GetProperty("type").GetString() == "alta.mcp.error");

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual("unsupported_transport", error.GetProperty("code").GetString());
        Assert.IsTrue(stderr.ToString().Length == 0, stderr.ToString());
    }

    [TestMethod]
    public async Task PluginCommand_ServerRemoveDefaultsToProjectOverlayAndPreservesGlobalServer()
    {
        using var project = TempDirectory.Create();
        var path = McpConfigDiscovery.GetProjectConfigPath(project.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(
            path,
            """
            { "mcpServers": { "globalish": { "command": "keep" }, "overlay": { "command": "remove-me" } } }
            """);
        var plugin = new McpPlugin(createPresentation: null, _home.Path);
        var contribution = plugin.GetAltaCommands().Single();
        var stdout = new StringWriter(CultureInfo.InvariantCulture);
        var stderr = new StringWriter(CultureInfo.InvariantCulture);
        var app = new CommandApp("alta", "test") { contribution.CreateCommandNode(CreateAltaContext(stdout, stderr, project.Path)) };

        var exitCode = await app.RunAsync(["mcp", "server", "remove", "overlay"], new CommandRunConfig { Out = TextWriter.Null, Error = stderr });

        Assert.AreEqual(0, exitCode, stderr.ToString());
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var servers = document.RootElement.GetProperty("mcpServers");
        Assert.IsFalse(servers.TryGetProperty("overlay", out _));
        Assert.AreEqual("keep", servers.GetProperty("globalish").GetProperty("command").GetString());
        StringAssert.Contains(stdout.ToString(), "\"changed\":true");
    }

    [TestMethod]
    public async Task PluginCommand_AServerOfAnotherToolIsListedDisabledAndOverridden_AndItsFileIsNeverWritten()
    {
        using var home = TempDirectory.Create();
        using var project = TempDirectory.Create();
        var shared = Path.Combine(project.Path, ".mcp.json");
        var vscode = Path.Combine(project.Path, ".vscode", "mcp.json");
        WriteFile(shared, """{ "mcpServers": { "docs": { "type": "http", "url": "https://example.test/mcp" } } }""");
        WriteFile(vscode, """{ "servers": { "asks": { "type": "stdio", "command": "node", "args": ["${input:token}"] } } }""");
        var before = (File.ReadAllText(shared), File.ReadAllText(vscode));
        var stdout = new StringWriter(CultureInfo.InvariantCulture);
        var stderr = new StringWriter(CultureInfo.InvariantCulture);
        var app = new CommandApp("alta", "test")
        {
            McpCommandFactory.CreateCommand(CreateAltaContext(stdout, stderr, project.Path), new McpCommandFactoryOptions { UserHomeDirectory = home.Path }),
        };
        async Task<List<JsonElement>> RunAsync(int expectedExitCode, params string[] arguments)
        {
            stdout.GetStringBuilder().Clear();
            Assert.AreEqual(expectedExitCode, await app.RunAsync(["mcp", .. arguments], new CommandRunConfig { Out = TextWriter.Null, Error = stderr }), stderr.ToString());
            return ReadJsonLines(stdout.ToString());
        }

        var listed = (await RunAsync(0, "list")).Single();
        Assert.AreEqual("docs", listed.GetProperty("server").GetString());
        Assert.AreEqual("common", listed.GetProperty("sourceOrigin").GetString());
        Assert.IsTrue(listed.GetProperty("readOnly").GetBoolean());
        Assert.AreEqual(shared, listed.GetProperty("sourcePath").GetString());

        var sources = await RunAsync(0, "config", "sources", "--include-missing");
        var files = sources.Where(static line => line.GetProperty("type").GetString() == "alta.mcp.config.source").ToArray();
        CollectionAssert.AreEquivalent(new[] { "codealta", "codealta", "common", "vscode" }, files.Select(static line => line.GetProperty("origin").GetString()).ToArray());
        Assert.IsTrue(files.Where(static line => line.GetProperty("readOnly").GetBoolean()).All(static line => !line.GetProperty("writable").GetBoolean() && line.GetProperty("exists").GetBoolean()));
        var skipped = sources.Single(static line => line.GetProperty("type").GetString() == "alta.mcp.config.skipped_server");
        Assert.AreEqual("asks", skipped.GetProperty("server").GetString());
        Assert.AreEqual("input_variable", skipped.GetProperty("reason").GetString());
        Assert.AreEqual(1, (await RunAsync(0, "status")).First().GetProperty("skippedServerCount").GetInt32());

        var refused = (await RunAsync(1, "server", "remove", "docs")).Single();
        Assert.AreEqual("read_only_source", refused.GetProperty("code").GetString());
        await RunAsync(0, "server", "disable", "docs");
        Assert.IsFalse(new McpPolicyLoader().Load(null, McpPolicyWriter.GetProjectPolicyPath(project.Path)).Servers["docs"].Enabled!.Value);

        // Adding a server of the same name writes the file of CodeAlta, which comes first.
        await RunAsync(0, "server", "add", "docs", "--url", "https://override.example.test/mcp");
        var overriding = (await RunAsync(0, "list")).Single();
        Assert.AreEqual("codealta", overriding.GetProperty("sourceOrigin").GetString());
        Assert.AreEqual("https://override.example.test/mcp", overriding.GetProperty("url").GetString());
        var shadowed = (await RunAsync(0, "config", "sources")).Single(static line => line.GetProperty("type").GetString() == "alta.mcp.config.shadowed_server");
        Assert.AreEqual("same-scope-source-comes-first", shadowed.GetProperty("reason").GetString());
        Assert.AreEqual(shared, shadowed.GetProperty("sourcePath").GetString());
        Assert.AreEqual(McpConfigDiscovery.GetProjectConfigPath(project.Path), shadowed.GetProperty("overriddenByPath").GetString());

        // Removing it again leaves the one of the other tool in effect.
        await RunAsync(0, "server", "remove", "docs");
        Assert.AreEqual("common", (await RunAsync(0, "list")).Single().GetProperty("sourceOrigin").GetString());
        Assert.AreEqual(before, (File.ReadAllText(shared), File.ReadAllText(vscode)));
    }

    [TestMethod]
    public async Task PluginCommand_ServerDisableAndEnableMutatesPolicyOnly()
    {
        using var project = TempDirectory.Create();
        var mcpPath = McpConfigDiscovery.GetProjectConfigPath(project.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(mcpPath)!);
        File.WriteAllText(mcpPath, "{ \"mcpServers\": { \"docs\": { \"url\": \"https://example.test/mcp\" } } }");
        var originalMcpJson = File.ReadAllText(mcpPath);
        var plugin = new McpPlugin(createPresentation: null, _home.Path);
        var contribution = plugin.GetAltaCommands().Single();
        var stdout = new StringWriter(CultureInfo.InvariantCulture);
        var stderr = new StringWriter(CultureInfo.InvariantCulture);
        var app = new CommandApp("alta", "test") { contribution.CreateCommandNode(CreateAltaContext(stdout, stderr, project.Path)) };

        var disableExitCode = await app.RunAsync(["mcp", "server", "disable", "docs"], new CommandRunConfig { Out = TextWriter.Null, Error = stderr });
        var disabledPolicy = new McpPolicyLoader().Load(null, McpPolicyWriter.GetProjectPolicyPath(project.Path));
        var enableExitCode = await app.RunAsync(["mcp", "server", "enable", "docs"], new CommandRunConfig { Out = TextWriter.Null, Error = stderr });
        var enabledPolicy = new McpPolicyLoader().Load(null, McpPolicyWriter.GetProjectPolicyPath(project.Path));

        Assert.AreEqual(0, disableExitCode, stderr.ToString());
        Assert.AreEqual(0, enableExitCode, stderr.ToString());
        Assert.AreEqual(originalMcpJson, File.ReadAllText(mcpPath));
        Assert.IsFalse(disabledPolicy.Servers["docs"].Enabled!.Value);
        Assert.IsTrue(enabledPolicy.Servers["docs"].Enabled!.Value);
        StringAssert.Contains(stdout.ToString(), "\"type\":\"alta.mcp.server.disable\"");
        StringAssert.Contains(stdout.ToString(), "\"type\":\"alta.mcp.server.enable\"");
    }

    [TestMethod]
    public async Task PluginCommand_StatusRedactsSecretValues()
    {
        const string argumentSecret = "argumentSecretValue12345678901234567890";
        const string querySecret = "querySecretValue123456789012345678901234";
        using var project = TempDirectory.Create();
        Directory.CreateDirectory(Path.Combine(project.Path, ".alta"));
        File.WriteAllText(
            Path.Combine(project.Path, ".alta", "mcp.json"),
            $$"""
            {
              "mcpServers": {
                "stdio": {
                  "command": "npx",
                  "args": ["--token", "{{argumentSecret}}", "--safe=value"],
                  "env": { "API_TOKEN": "env-secret" }
                },
                "remote": {
                  "url": "https://example.test/mcp?token={{querySecret}}&safe=value",
                  "headers": { "Authorization": "Bearer header-secret" }
                }
              }
            }
            """);
        var plugin = new McpPlugin(createPresentation: null, _home.Path);
        var contribution = plugin.GetAltaCommands().Single();
        var stdout = new StringWriter(CultureInfo.InvariantCulture);
        var stderr = new StringWriter(CultureInfo.InvariantCulture);
        var app = new CommandApp("alta", "test") { contribution.CreateCommandNode(CreateAltaContext(stdout, stderr, project.Path)) };

        var exitCode = await app.RunAsync(["mcp", "status"], new CommandRunConfig { Out = TextWriter.Null, Error = stderr });

        Assert.AreEqual(0, exitCode);
        var output = stdout.ToString();
        StringAssert.Contains(output, "[redacted]");
        StringAssert.Contains(output, "safe=value");
        Assert.IsFalse(output.Contains(argumentSecret, StringComparison.Ordinal));
        Assert.IsFalse(output.Contains(querySecret, StringComparison.Ordinal));
        Assert.IsFalse(output.Contains("env-secret", StringComparison.Ordinal));
        Assert.IsFalse(output.Contains("header-secret", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task PluginAndServices_ReadGlobalConfigurationFromTheirHomeDirectory()
    {
        Directory.CreateDirectory(Path.Combine(_home.Path, ".alta"));
        File.WriteAllText(
            Path.Combine(_home.Path, ".alta", "mcp.json"),
            """
            { "mcpServers": { "home-only": { "url": "https://example.test/mcp" } } }
            """);
        using var project = TempDirectory.Create();

        var snapshot = new McpManagementService(_home.Path).RefreshSnapshot(new McpManagementRequest { ProjectDirectory = project.Path });
        var content = await new McpPlugin(createPresentation: null, _home.Path).GetSystemPromptContributions().Single()
            .Content(CreatePromptContext(project.Path), CancellationToken.None);

        CollectionAssert.AreEqual(
            new[] { "home-only" },
            snapshot.Servers.Where(static server => server.State == McpManagementServerState.Configured).Select(static server => server.Key).ToArray());
        Assert.IsNotNull(content);
        StringAssert.Contains(content, "home-only");
    }

    [TestMethod]
    public async Task PromptContribution_IsNullWhenMissingAndMentionsConfiguredProjectServer()
    {
        using var emptyProject = TempDirectory.Create();
        var contribution = new McpPlugin(createPresentation: null, _home.Path).GetSystemPromptContributions().Single();
        var emptyContent = await contribution.Content(CreatePromptContext(emptyProject.Path), CancellationToken.None);
        Assert.IsNull(emptyContent);

        using var project = TempDirectory.Create();
        Directory.CreateDirectory(Path.Combine(project.Path, ".alta"));
        File.WriteAllText(
            Path.Combine(project.Path, ".alta", "mcp.json"),
            """
            { "mcpServers": { "docs": { "url": "https://example.test/mcp" } } }
            """);

        var content = await contribution.Content(CreatePromptContext(project.Path), CancellationToken.None);

        Assert.IsNotNull(content);
        StringAssert.Contains(content, "MCP servers:");
        StringAssert.Contains(content, "docs");
        StringAssert.Contains(content, "Inactive (`alta mcp activate <id>*`): `docs`");
        StringAssert.Contains(content, "Activation registers the tools in the current turn: call them in your next step, without ending the turn.");
        Assert.IsFalse(content.Contains("http/sse", StringComparison.Ordinal));
        Assert.IsFalse(content.Contains("alta mcp tool search", StringComparison.Ordinal));
        Assert.IsFalse(content.Contains("runtime deferred", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void StatusLabel_UsesActivatedToolCountsWhenManagementSnapshotHasNoToolCache()
    {
        using var project = TempDirectory.Create();
        Directory.CreateDirectory(Path.Combine(project.Path, ".alta"));
        File.WriteAllText(
            Path.Combine(project.Path, ".alta", "mcp.json"),
            """
            { "mcpServers": { "docs": { "command": "__missing_codealta_mcp_test_server__" } } }
            """);
        var snapshot = new McpManagementService(_home.Path).RefreshSnapshot(new McpManagementRequest { ProjectDirectory = project.Path });

        var label = McpPlugin.CreateStatusLabel(snapshot, new Dictionary<string, int>(StringComparer.Ordinal) { ["docs"] = 7 }, ["docs"]);

        Assert.AreEqual("MCP 1/1 · active tools 7", label);
    }

    [TestMethod]
    public void StatusLabel_ShowsPendingForActivatedServersBeforeToolEnumeration()
    {
        using var project = TempDirectory.Create();
        Directory.CreateDirectory(Path.Combine(project.Path, ".alta"));
        File.WriteAllText(
            Path.Combine(project.Path, ".alta", "mcp.json"),
            """
            { "mcpServers": { "docs": { "url": "https://example.test/mcp" } } }
            """);
        var snapshot = new McpManagementService(_home.Path).RefreshSnapshot(new McpManagementRequest { ProjectDirectory = project.Path });

        var label = McpPlugin.CreateStatusLabel(snapshot, new Dictionary<string, int>(StringComparer.Ordinal), ["docs"]);

        Assert.AreEqual("MCP 1/1 · tools pending", label);
    }

    [TestMethod]
    public async Task StatusVisual_UpdatesFromBindableStateAfterSessionActivation()
    {
        using var project = TempDirectory.Create();
        Directory.CreateDirectory(Path.Combine(project.Path, ".alta"));
        File.WriteAllText(
            Path.Combine(project.Path, ".alta", "mcp.json"),
            """
            { "mcpServers": { "docs": { "url": "https://example.test/mcp" } } }
            """);
        var plugin = new McpPlugin(McpTerminalContributions.CreatePresentation, _home.Path);
        var statusContribution = plugin.GetUiContributions().OfType<PluginVisualContribution>().Single();
        var visual = statusContribution.CreateVisual!(CreateVisualContext(project.Path, "session-a"));
        Assert.IsNotNull(visual);
        Assert.IsInstanceOfType<Button>(visual);
        var button = (Button)visual;
        Assert.IsInstanceOfType<Markup>(button.Content);
        var markup = (Markup)button.Content!;
        StringAssert.Contains(markup.Text!, "[success]");
        StringAssert.Contains(markup.Text!, "MCP[/]");
        Assert.AreEqual("MCP 1/1 · tools not loaded", ReadStatusPlainText(visual));

        var changedCount = 0;
        void OnValueChanged(Binding binding)
        {
            if (binding.Owner.GetType().IsGenericType && binding.Owner.GetType().GetGenericTypeDefinition() == typeof(State<>))
            {
                changedCount++;
            }
        }

        var stdout = new StringWriter(CultureInfo.InvariantCulture);
        var stderr = new StringWriter(CultureInfo.InvariantCulture);
        var app = new CommandApp("alta", "test")
        {
            plugin.GetAltaCommands().Single().CreateCommandNode(CreateAltaContext(stdout, stderr, project.Path, sourceSessionId: "session-a")),
        };

        BindingManager.Current.ValueChanged += OnValueChanged;
        try
        {
            var exitCode = await app.RunAsync(["mcp", "activate", "docs"], new CommandRunConfig { Out = TextWriter.Null, Error = stderr });
            Assert.AreEqual(0, exitCode, stderr.ToString());
        }
        finally
        {
            BindingManager.Current.ValueChanged -= OnValueChanged;
        }

        Assert.IsTrue(changedCount > 0, "MCP activation should invalidate bindable UI state.");
        Assert.AreEqual("MCP 1/1 · active tools 0", ReadStatusPlainText(statusContribution.CreateVisual!(CreateVisualContext(project.Path, "session-a"))!));
    }

    [TestMethod]
    public void StatusVisual_UpdatesFromBindableStateAfterToolCountsLoad()
    {
        using var project = TempDirectory.Create();
        Directory.CreateDirectory(Path.Combine(project.Path, ".alta"));
        File.WriteAllText(
            Path.Combine(project.Path, ".alta", "mcp.json"),
            """
            { "mcpServers": { "docs": { "url": "https://example.test/mcp" } } }
            """);
        var activationState = new McpActivationState();
        var statusRevision = new State<int>(0);
        activationState.Changed += _ => statusRevision.Value++;
        var visual = McpTerminalContributions.CreateStatusIndicator(
            CreateVisualContext(project.Path, "session-a"),
            new McpManagementService(_home.Path),
            activationState,
            statusRevision);
        Assert.IsNotNull(visual);
        Assert.AreEqual("MCP 1/1 · tools not loaded", ReadStatusPlainText(visual));
        StringAssert.Contains(ReadStatusMarkup(visual)!, "tools [muted]not loaded[/]");

        var scopeKey = McpActivationState.ResolveScopeKey("session-a", project.Path);
        activationState.ActivateServers(scopeKey, ["docs"]);
        Assert.AreEqual(1, statusRevision.Value);
        var pendingVisual = McpTerminalContributions.CreateStatusIndicator(CreateVisualContext(project.Path, "session-a"), new McpManagementService(_home.Path), activationState, statusRevision)!;
        Assert.AreEqual(
            "MCP 1/1 · tools pending",
            ReadStatusPlainText(pendingVisual));
        StringAssert.Contains(ReadStatusMarkup(pendingVisual)!, "tools [warning]pending[/]");

        activationState.UpdateToolCounts(scopeKey, new Dictionary<string, int>(StringComparer.Ordinal) { ["docs"] = 7 });
        Assert.AreEqual(2, statusRevision.Value);
        var activeVisual = McpTerminalContributions.CreateStatusIndicator(CreateVisualContext(project.Path, "session-a"), new McpManagementService(_home.Path), activationState, statusRevision)!;
        Assert.AreEqual(
            "MCP 1/1 · active tools 7",
            ReadStatusPlainText(activeVisual));
        StringAssert.Contains(ReadStatusMarkup(activeVisual)!, "active tools [accent]7[/]");
    }

    private static string? ReadStatusPlainText(Visual visual)
    {
        var markup = ReadStatusMarkup(visual);
        if (markup is null)
        {
            return null;
        }

        var plainText = StripMarkupTags(markup);
        var mcpOffset = plainText.IndexOf("MCP ", StringComparison.Ordinal);
        return mcpOffset >= 0 ? plainText[mcpOffset..] : plainText;
    }

    private static string? ReadStatusMarkup(Visual visual)
    {
        Assert.IsInstanceOfType<Button>(visual);
        var button = (Button)visual;
        Assert.IsInstanceOfType<Markup>(button.Content);
        return ((Markup)button.Content!).Text;
    }

    private static string StripMarkupTags(string markup)
    {
        var builder = new StringBuilder(markup.Length);
        var inTag = false;
        foreach (var c in markup)
        {
            if (c == '[')
            {
                inTag = true;
                continue;
            }

            if (inTag)
            {
                if (c == ']')
                {
                    inTag = false;
                }

                continue;
            }

            builder.Append(c);
        }

        return builder.ToString();
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

    private static PluginVisualContext CreateVisualContext(string projectPath, string? sessionId = null)
        => new()
        {
            Plugin = CreatePluginDescriptor(),
            Services = NoopPluginServices.Create(),
            Scope = PluginScope.Global,
            ProjectPath = projectPath,
            SessionId = sessionId,
            Region = PluginUiRegion.SessionStatus,
            HasInteractiveUi = true,
        };

    private static PluginSystemPromptContext CreatePromptContext(string projectPath)
        => new()
        {
            Plugin = CreatePluginDescriptor(),
            Services = NoopPluginServices.Create(),
            Scope = PluginScope.Global,
            ProjectPath = projectPath,
        };

    private static PluginDescriptor CreatePluginDescriptor()
        => new()
        {
            RuntimeKey = "mcp",
            TypeName = typeof(McpPlugin).FullName!,
            AssemblyName = typeof(McpPlugin).Assembly.GetName().Name!,
            DisplayName = "MCP",
        };

    private static List<JsonElement> ReadJsonLines(string text)
    {
        var values = new List<JsonElement>();
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            using var document = JsonDocument.Parse(line);
            values.Add(document.RootElement.Clone());
        }

        return values;
    }

    private static IEnumerable<string> ReadStringArray(JsonElement element)
        => element.EnumerateArray().Select(static item => item.GetString()!);

    private static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private sealed record FlavorWriteCase(string Name, string Json, string RootKey, string? ExpectedHttpType);

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory()
            => Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CodeAlta.McpConfigTests." + Guid.NewGuid().ToString("N"));

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
