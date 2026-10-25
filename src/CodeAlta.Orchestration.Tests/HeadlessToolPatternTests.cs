using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

/// <summary>Tools a session does without by patterns over their names: built-in, MCP, plugin and provider web tools.</summary>
[TestClass]
public sealed class HeadlessToolPatternTests
{
    [TestMethod]
    public void Patterns_MatchNamesWithStars_WithoutRegardToCase_AndWebNamesTheWebTools()
    {
        Assert.IsTrue(AgentToolPatterns.IsDisabled("mcp__github__create_issue", ["mcp__*"]));
        Assert.IsTrue(AgentToolPatterns.IsDisabled("mcp__github__create_issue", ["mcp__*__create_*"]));
        Assert.IsTrue(AgentToolPatterns.IsDisabled("Shell_Command", ["shell_command"]));
        Assert.IsFalse(AgentToolPatterns.IsDisabled("shell_command", ["shell"]));
        Assert.IsFalse(AgentToolPatterns.IsDisabled("read_file", []));
        CollectionAssert.AreEquivalent(new[] { "webget", AgentToolPatterns.WebSearchToolName, AgentToolPatterns.WebFetchToolName },
            AgentToolPatterns.Expand(AgentToolPatterns.KnownToolNames, [AgentToolPatterns.WebAlias]).ToArray());
        Assert.IsNull(AgentToolPatterns.Validate("mcp__*"));
        Assert.IsNotNull(AgentToolPatterns.Validate(" "));
        Assert.IsNotNull(AgentToolPatterns.Validate("a b"));
    }

    [TestMethod]
    public async Task DisabledPatterns_KeepTheToolsTheyNameFromTheModel_AndChildrenInheritThem()
    {
        await using var root = new HeadlessTestRoot();
        var provider = new ScriptedTurnProvider();
        var host = await root.CreateHostAsync(provider, readOnlyGlobalState: true);
        var id = Guid.CreateVersion7().ToString();
        host.RuntimeService.SetDisabledToolPatterns(id, [AgentToolPatterns.WebAlias, "shell_*", "mcp__*", "extra_*"]);
        var options = HeadlessTestRoot.Options(provider, root.ProjectRoot, [Tool("extra_tool"), Tool("mcp__server__lookup"), Tool("kept_tool")]);

        var session = await host.RuntimeService.CreateProjectSessionAsync(host.CurrentProject, options, "Patterns", null, null, null, null, sessionId: id);
        await host.RuntimeService.SendAsync(session, options, new AgentSendOptions { Input = AgentInput.Text("hello") });

        Assert.IsTrue(provider.Requests.TryDequeue(out var request));
        var offered = request.Tools.Select(static tool => tool.Spec.Name).ToArray();
        CollectionAssert.Contains(offered, "kept_tool");
        CollectionAssert.Contains(offered, "read_file");
        foreach (var name in new[] { "webget", "shell_command", "extra_tool", "mcp__server__lookup" })
            CollectionAssert.DoesNotContain(offered, name, name + " is not offered.");
        // The provider is told which of its own tools the session does without.
        Assert.IsTrue(request.DisabledTools!.Contains(AgentToolPatterns.WebSearchToolName));
        Assert.IsTrue(request.DisabledTools.Contains("shell_command"));
        Assert.IsTrue((await host.RuntimeService.GetDisabledToolsAsync(id)).Contains("webget"));

        var child = await host.RuntimeService.CreateProjectSessionAsync(host.CurrentProject, HeadlessTestRoot.Options(provider, root.ProjectRoot), "Child",
            parentSessionId: id, createdBy: null);
        CollectionAssert.AreEquivalent(host.RuntimeService.GetDisabledToolPatterns(id).ToArray(), host.RuntimeService.GetDisabledToolPatterns(child.SessionId).ToArray(),
            "A child is never given more tools than its parent.");

        // Without patterns, the next prompt attaches the session again with its usual tools.
        host.RuntimeService.SetDisabledToolPatterns(id, null);
        await host.RuntimeService.SendAsync(session, options, new AgentSendOptions { Input = AgentInput.Text("again") });
        Assert.IsTrue(provider.Requests.TryDequeue(out var second));
        CollectionAssert.Contains(second.Tools.Select(static tool => tool.Spec.Name).ToArray(), "webget");
        CollectionAssert.Contains(second.Tools.Select(static tool => tool.Spec.Name).ToArray(), "extra_tool");
    }

    private static AgentToolDefinition Tool(string name)
        => new(new AgentToolSpec(name, "A tool of the test.", JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone()),
            static (_, _) => Task.FromResult(new AgentToolResult(true, [])));
}
