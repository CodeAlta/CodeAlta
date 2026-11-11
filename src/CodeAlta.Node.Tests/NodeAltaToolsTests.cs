using System.Text.Json;
using System.Text.Json.Nodes;
using CodeAlta.Agent;
using CodeAlta.LiveTool;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Node.Tests;

/// <summary>The agent tools of the sessions of a node: the <c>alta</c> tool and Code Mode, as CodeAlta CLI gives them, without asks.</summary>
[TestClass]
public sealed class NodeAltaToolsTests
{
    private const string SessionId = "node-session-1";

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Sessions_GetTheAltaTool_AndCodeMode()
    {
        using var root = new NodeTestRoot();
        await using var node = await NodeHost.StartAsync(root.Options(), TestContext.CancellationToken);

        var names = Tools(node, root).Select(static tool => tool.Spec.Name).ToArray();

        CollectionAssert.Contains(names, "alta");
        CollectionAssert.Contains(names, "exec");
        CollectionAssert.Contains(names, "wait");
    }

    [TestMethod]
    public async Task AltaSessionList_WorksFromANodeSession()
    {
        using var root = new NodeTestRoot();
        await using var node = await NodeHost.StartAsync(root.Options(), TestContext.CancellationToken);

        var result = await CallAltaAsync(node, root, ["session", "list"]);

        Assert.IsTrue(result.Success, Text(result));
        StringAssert.Contains(Text(result), "alta.result");
    }

    [TestMethod]
    public async Task Ask_IsNotOffered_ButRemindersAre()
    {
        using var root = new NodeTestRoot();
        await using var node = await NodeHost.StartAsync(root.Options(), TestContext.CancellationToken);

        var ask = await CallAltaAsync(node, root, ["ask", "--stdin"], stdin: """{"questions":[{"title":"Go","question":"Go on?"}]}""");
        Assert.IsFalse(ask.Success, "Nobody answers a question on a node: alta ask is not offered.");

        var help = await node.Alta.InvokeAsync(["--help"], cancellationToken: TestContext.CancellationToken);
        Assert.AreEqual(AltaExitCodes.Success, help.ExitCode, help.Transcript);
        Assert.IsFalse(help.Stdout.Contains("\n  ask ", StringComparison.Ordinal), help.Stdout);
        Assert.IsFalse(help.Stdout.Contains("\n  review ", StringComparison.Ordinal), help.Stdout);
        StringAssert.Contains(help.Stdout, "\n  reminder ", "A node outlives the turns of its sessions: it keeps reminders.");

        var reminders = await CallAltaAsync(node, root, ["reminder", "list"]);
        Assert.IsTrue(reminders.Success, Text(reminders));
    }

    // An agent of the node that edited config.toml does not wait for the watch of the file: alta provider refresh applies the
    // providers changed in it, then tests them; a file that does not read is refused and nothing is tested.
    [TestMethod]
    public async Task ProviderRefresh_AppliesTheConfigurationOfTheNode()
    {
        var token = TestContext.CancellationToken;
        using var root = new NodeTestRoot();
        await using var node = await NodeHost.StartAsync(root.Options(), token);
        var path = node.Host.CatalogOptions.ConfigPath;

        var early = await node.Alta.InvokeAsync(["provider", "refresh"], caller: AltaCallerIdentity.Cli, cancellationToken: token);
        Assert.AreEqual(AltaExitCodes.Failure, early.ExitCode, "the providers of the node are not started yet");
        StringAssert.Contains(early.Transcript, "provider.configurationFailed");

        var providers = new NodeProviders(node.Host, null);
        using var watch = providers.WatchConfiguration();
        node.ProviderConfiguration!.Providers = providers;
        File.WriteAllText(path, "[providers.local]\ntype = \"openai-chat\"\napi_url = \"http://127.0.0.1:9/v1\"\napi_key = \"test-key\"\n");

        var refreshed = await node.Alta.InvokeAsync(["provider", "refresh"], caller: AltaCallerIdentity.Cli, cancellationToken: token);

        Assert.AreEqual(AltaExitCodes.Success, refreshed.ExitCode, refreshed.Transcript);
        var records = refreshed.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(static line => JsonDocument.Parse(line).RootElement).ToArray();
        Assert.IsTrue(records.Any(static record => record.GetProperty("type").GetString() == "alta.provider.state" && record.GetProperty("providerKey").GetString() == "local"),
            "the provider written by hand is tested at once: " + refreshed.Stdout);
        var summary = records.Single(static record => record.GetProperty("type").GetString() == "alta.provider.refreshed");
        Assert.IsTrue(summary.GetProperty("configurationApplied").GetBoolean());

        File.WriteAllText(path, "[providers.local]\ntype = \"openai-chat\"\napi_key \"sk-secret-0123456789\"\n");
        var broken = await node.Alta.InvokeAsync(["provider", "refresh"], caller: AltaCallerIdentity.Cli, cancellationToken: token);
        Assert.AreEqual(AltaExitCodes.Failure, broken.ExitCode);
        StringAssert.Contains(broken.Transcript, "line 3");
        Assert.IsFalse(broken.Transcript.Contains("sk-secret", StringComparison.Ordinal), broken.Transcript);
        Assert.IsFalse(broken.Stdout.Contains("alta.provider.state", StringComparison.Ordinal), "nothing is tested");
    }

    [TestMethod]
    public async Task ScriptQuestions_AreNotAnswered()
    {
        using var root = new NodeTestRoot();
        await using var node = await NodeHost.StartAsync(root.Options(), TestContext.CancellationToken);
        var exec = Tools(node, root).Single(static tool => tool.Spec.Name == "exec");

        var arguments = new JsonObject
        {
            ["code"] = """return await ask("Go on?").then((answer) => `answered: ${answer}`, (e) => `${e.code}`);""",
        };
        var result = await exec.Handler(new AgentToolInvocation(new ModelProviderId("scripted"), SessionId, "call-exec", "exec", Element(arguments)),
            TestContext.CancellationToken);

        Assert.IsTrue(result.Success, Text(result));
        StringAssert.Contains(Text(result), "unavailable", "ask() tells the script that nobody can be asked.");
        Assert.IsFalse(Text(result).Contains("answered:", StringComparison.Ordinal), Text(result));
    }

    [TestMethod]
    public void AltaOptions_AreNotThoseOfTheCommandLine()
    {
        using var root = new NodeTestRoot();
        var options = NodeHost.CreateAltaOptions(root.Profile());

        Assert.AreSame(AltaCommandRegistryOptions.Node, options.Registry);
        Assert.IsTrue(options.OwnsReminders);
        Assert.IsNull(options.CodeMode, "Code Mode follows the configuration of the profile.");
        foreach (var allowed in new[] { "reminder", "automation", "session", "job", "notes", "task", "plan", "project", "skill", "model" })
        {
            Assert.IsTrue(options.Registry.AllowsBuiltInRoot(allowed), allowed);
        }

        foreach (var excluded in new[] { "ask", "review", "window", "canvas", "terminal", "editor", "diff" })
        {
            Assert.IsFalse(options.Registry.AllowsBuiltInRoot(excluded), excluded);
        }
    }

    private static IReadOnlyList<AgentToolDefinition> Tools(NodeHost node, NodeTestRoot root)
    {
        var sessionTools = node.Host.Commands.SessionTools;
        Assert.IsNotNull(sessionTools, "The node gives its sessions their tools.");
        return sessionTools(new OwnedSessionToolRequest(SessionId, null, root.ProjectRoot, "scripted"));
    }

    private async Task<AgentToolResult> CallAltaAsync(NodeHost node, NodeTestRoot root, string[] args, string? stdin = null)
    {
        var alta = Tools(node, root).Single(static tool => tool.Spec.Name == "alta");
        var arguments = new JsonObject { ["args"] = new JsonArray([.. args.Select(static arg => (JsonNode?)JsonValue.Create(arg))]) };
        if (stdin is not null) arguments["stdin"] = stdin;
        return await alta.Handler(new AgentToolInvocation(new ModelProviderId("scripted"), SessionId, "call-alta", "alta", Element(arguments)),
            TestContext.CancellationToken);
    }

    private static JsonElement Element(JsonObject value)
    {
        using var document = JsonDocument.Parse(value.ToJsonString());
        return document.RootElement.Clone();
    }

    private static string Text(AgentToolResult result)
        => string.Join('\n', result.Items.OfType<AgentToolResultItem.Text>().Select(static item => item.Value)) + (result.Error is { } error ? "\n" + error : "");
}
