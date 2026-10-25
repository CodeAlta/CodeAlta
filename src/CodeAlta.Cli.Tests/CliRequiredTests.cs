using System.Text.Json;

namespace CodeAlta.Cli.Tests;

/// <summary>The plugins and the MCP servers a configuration marks required, and the MCP settings a run reads.</summary>
[TestClass]
public sealed class CliRequiredTests
{
    [TestMethod]
    public async Task RequiredMcpServer_ThatCannotStart_StopsTheRunWithExitCode2()
    {
        using var root = new CliTestRoot();
        // A server of the profile that never starts: the run checks the required ones before its session.
        Directory.CreateDirectory(root.Profile);
        File.WriteAllText(Path.Combine(root.Profile, "mcp.json"), """{ "mcpServers": { "docs": { "command": "codealta-no-such-command" } } }""");
        var provider = new FakeProvider();

        var ghost = await root.RunAsync(provider, ["run", "--format", "jsonl", "-c", "plugins.mcp.servers.ghost.required=true", "hello"]);
        Assert.AreEqual(CliExitCodes.InvalidUsage, ghost.ExitCode, ghost.Stderr);
        var failure = Result(ghost).GetProperty("failure");
        Assert.AreEqual("required_unavailable", failure.GetProperty("kind").GetString());
        StringAssert.Contains(failure.GetProperty("message").GetString(), "'ghost'");
        StringAssert.Contains(failure.GetProperty("message").GetString(), "server_not_found");
        Assert.AreEqual(0, provider.Requests.Count, "No session ran.");

        // -c reaches the MCP policy: the server is turned off by the run, and a required server that is off fails.
        var off = await root.RunAsync(provider, ["run", "-c", "plugins.mcp.servers.docs.required=true", "-c", "plugins.mcp.servers.docs.enabled=false", "hello"]);
        Assert.AreEqual(CliExitCodes.InvalidUsage, off.ExitCode, off.Stderr);
        StringAssert.Contains(off.Stderr, "server_disabled");
        Assert.AreEqual(string.Empty, off.Stdout);

        var broken = await root.RunAsync(provider, ["run", "-c", "plugins.mcp.servers.docs.required=true", "hello"]);
        Assert.AreEqual(CliExitCodes.InvalidUsage, broken.ExitCode, broken.Stderr);
        StringAssert.Contains(broken.Stderr, "'docs'");

        // --no-mcp starts no server, and checks none.
        var noMcp = await root.RunAsync(provider, ["run", "--no-mcp", "-c", "plugins.mcp.servers.ghost.required=true", "hello"]);
        Assert.AreEqual(CliExitCodes.Completed, noMcp.ExitCode, noMcp.Stderr);
    }

    [TestMethod]
    public async Task RequiredPlugin_ThatDoesNotStart_StopsTheRunWithExitCode2()
    {
        using var root = new CliTestRoot();
        var provider = new FakeProvider();

        var missing = await root.RunAsync(provider, ["run", "--no-mcp", "-c", "plugins.codealta-no-such-plugin.required=true", "hello"]);
        Assert.AreEqual(CliExitCodes.InvalidUsage, missing.ExitCode, missing.Stderr);
        StringAssert.Contains(missing.Stderr, "'codealta-no-such-plugin'");

        var git = await root.RunAsync(provider, ["run", "--no-mcp", "-c", "plugins.git.required=true", "hello"]);
        Assert.AreEqual(CliExitCodes.Completed, git.ExitCode, git.Stderr);
        CollectionAssert.Contains(root.LastPlugins, "Git");

        // A command that runs no session does not check: altacli alta mcp is how a required server is looked at.
        var alta = await root.RunAsync(provider, ["alta", "--no-mcp", "-c", "plugins.codealta-no-such-plugin.required=true", "session", "list"]);
        Assert.AreEqual(0, alta.ExitCode, alta.Stderr);
    }

    [TestMethod]
    public async Task AltaMcpCommands_WriteNoConfigurationFile()
    {
        using var root = new CliTestRoot();
        Directory.CreateDirectory(root.Profile);
        File.WriteAllText(Path.Combine(root.Profile, "mcp.json"), """{ "mcpServers": { "docs": { "command": "codealta-no-such-command" } } }""");
        var mcpJson = File.ReadAllText(Path.Combine(root.Profile, "mcp.json"));

        var disable = await root.RunAsync(new FakeProvider(), ["alta", "mcp", "server", "disable", "docs", "--global"]);
        var add = await root.RunAsync(new FakeProvider(), ["alta", "mcp", "server", "add", "other", "--command", "x", "--scope", "global"]);

        StringAssert.Contains(disable.Stdout, "read-only", disable.Stdout + disable.Stderr);
        StringAssert.Contains(add.Stdout, "read-only", add.Stdout + add.Stderr);
        Assert.IsFalse(File.Exists(Path.Combine(root.Profile, "config.toml")));
        Assert.AreEqual(mcpJson, File.ReadAllText(Path.Combine(root.Profile, "mcp.json")));
    }

    private static JsonElement Result(CliRun run)
    {
        var last = JsonDocument.Parse(run.Lines[^1]).RootElement;
        Assert.AreEqual("_codealta/result", last.GetProperty("method").GetString());
        return last.GetProperty("params");
    }
}
