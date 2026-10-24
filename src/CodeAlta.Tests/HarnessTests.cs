using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Harness;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Tests;

[TestClass]
public sealed class HarnessTests
{
    private static readonly ModelProviderId Provider = new("fake-agent");

    [TestMethod]
    public async Task McpToolServer_ListsTheToolsAndRunsACall()
    {
        var tool = CreateTool("alta", static (invocation, _) =>
            Task.FromResult(new AgentToolResult(true, [new AgentToolResultItem.Text($"ran {invocation.Arguments.GetProperty("args")[0].GetString()}")])));
        HarnessMcpToolCall? received = null;
        var server = new HarnessMcpToolServer(
            "codealta",
            () => new Dictionary<string, AgentToolDefinition> { ["alta"] = tool },
            (call, cancellationToken) =>
            {
                received = call;
                return tool.Handler(new AgentToolInvocation(Provider, "s1", "c1", call.Name, call.Arguments), cancellationToken);
            });

        var initialize = await server.HandleAsync(Parse("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26"}}"""), CancellationToken.None);
        Assert.AreEqual("2025-03-26", initialize!.Value.GetProperty("result").GetProperty("protocolVersion").GetString());
        Assert.IsFalse(server.IsInitialized);

        Assert.IsNull(await server.HandleAsync(Parse("""{"jsonrpc":"2.0","method":"notifications/initialized"}"""), CancellationToken.None));
        Assert.IsTrue(server.IsInitialized);

        var list = await server.HandleAsync(Parse("""{"jsonrpc":"2.0","id":"two","method":"tools/list"}"""), CancellationToken.None);
        Assert.AreEqual("two", list!.Value.GetProperty("id").GetString());
        var listed = list.Value.GetProperty("result").GetProperty("tools")[0];
        Assert.AreEqual("alta", listed.GetProperty("name").GetString());
        Assert.AreEqual("object", listed.GetProperty("inputSchema").GetProperty("type").GetString());

        var call = await server.HandleAsync(
            Parse("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"alta","arguments":{"args":["session"]},"_meta":{"progressToken":7}}}"""),
            CancellationToken.None);
        var result = call!.Value.GetProperty("result");
        Assert.IsFalse(result.GetProperty("isError").GetBoolean());
        Assert.AreEqual("ran session", result.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.AreEqual(7, received!.Meta!.Value.GetProperty("progressToken").GetInt32());

        var unknown = await server.HandleAsync(Parse("""{"jsonrpc":"2.0","id":4,"method":"resources/list"}"""), CancellationToken.None);
        Assert.AreEqual(-32601, unknown!.Value.GetProperty("error").GetProperty("code").GetInt32());
    }

    [TestMethod]
    public async Task McpToolServer_ReportsAFailingToolAsAnError()
    {
        var server = new HarnessMcpToolServer(
            "codealta",
            static () => new Dictionary<string, AgentToolDefinition>(),
            static (_, _) => throw new InvalidOperationException("boom"));

        var call = await server.HandleAsync(Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"alta","arguments":{}}}"""), CancellationToken.None);

        var result = call!.Value.GetProperty("result");
        Assert.IsTrue(result.GetProperty("isError").GetBoolean());
        StringAssert.Contains(result.GetProperty("content")[0].GetProperty("text").GetString(), "boom");
    }

    [TestMethod]
    public async Task ToolCallBridge_RunsACodeAltaToolWhenTheHarnessCallsItThroughMcp()
    {
        var runs = 0;
        var tool = CreateTool("alta", (_, _) =>
        {
            runs++;
            return Task.FromResult(new AgentToolResult(true, [new AgentToolResultItem.Text("listed")]));
        });
        var bridge = new HarnessToolCallBridge();
        var arguments = Parse("""{"args":["session","list"]}""");
        bridge.Register("call-1", "alta", arguments, isCodeAltaTool: true);
        var toolCall = new AgentMessagePart.ToolCall("call-1", "alta", arguments);

        var definition = bridge.Resolve(toolCall, tool, "A tool run by the agent.", static () => Task.CompletedTask)!;
        var sessionRun = definition.Handler(new AgentToolInvocation(Provider, "s1", "call-1", "alta", arguments), CancellationToken.None);
        Assert.IsFalse(sessionRun.IsCompleted);

        var harnessCall = await bridge.CallFromMcpAsync(
            new HarnessMcpToolCall("alta", Parse("""{"args":["session","list"]}"""), null),
            _ => tool,
            Provider,
            "s1",
            CancellationToken.None);

        Assert.AreEqual("listed", ((AgentToolResultItem.Text)harnessCall.Items[0]).Value);
        Assert.AreSame(harnessCall, await sessionRun);
        Assert.AreEqual(1, runs);
        Assert.IsTrue(bridge.WhenStarted("call-1")!.IsCompleted);
    }

    [TestMethod]
    public async Task ToolCallBridge_MatchesByArgumentsFirstThenByName()
    {
        var tool = CreateTool("alta", static (invocation, _) =>
            Task.FromResult(new AgentToolResult(true, [new AgentToolResultItem.Text(invocation.ToolCallId)])));
        var bridge = new HarnessToolCallBridge();
        bridge.Register("first", "alta", Parse("""{"args":["a"]}"""), isCodeAltaTool: true);
        bridge.Register("second", "alta", Parse("""{"args":["b"]}"""), isCodeAltaTool: true);
        var first = bridge.Resolve(new AgentMessagePart.ToolCall("first", "alta", Parse("""{"args":["a"]}""")), tool, "x", static () => Task.CompletedTask)!;
        var second = bridge.Resolve(new AgentMessagePart.ToolCall("second", "alta", Parse("""{"args":["b"]}""")), tool, "x", static () => Task.CompletedTask)!;
        var firstRun = first.Handler(new AgentToolInvocation(Provider, "s1", "first", "alta", Parse("""{"args":["a"]}""")), CancellationToken.None);
        var secondRun = second.Handler(new AgentToolInvocation(Provider, "s1", "second", "alta", Parse("""{"args":["b"]}""")), CancellationToken.None);

        // The harness calls the second one first: its arguments say which call it is.
        var answerB = await bridge.CallFromMcpAsync(new HarnessMcpToolCall("alta", Parse("""{"args":["b"]}"""), null), _ => tool, Provider, "s1", CancellationToken.None);
        // Arguments the harness rewrote match the oldest call of the tool left.
        var answerA = await bridge.CallFromMcpAsync(new HarnessMcpToolCall("alta", Parse("""{"args":["A"]}"""), null), _ => tool, Provider, "s1", CancellationToken.None);

        Assert.AreEqual("second", ((AgentToolResultItem.Text)answerB.Items[0]).Value);
        Assert.AreEqual("first", ((AgentToolResultItem.Text)answerA.Items[0]).Value);
        await Task.WhenAll(firstRun, secondRun);
    }

    [TestMethod]
    public async Task ToolCallBridge_RunsAnUnmatchedMcpCallByItself()
    {
        var tool = CreateTool("alta", static (_, _) => Task.FromResult(new AgentToolResult(true, [new AgentToolResultItem.Text("detached")])));
        var bridge = new HarnessToolCallBridge();

        var result = await bridge.CallFromMcpAsync(new HarnessMcpToolCall("alta", Parse("{}"), null), _ => tool, Provider, "s1", CancellationToken.None);
        var missing = await bridge.CallFromMcpAsync(new HarnessMcpToolCall("nope", Parse("{}"), null), _ => null, Provider, "s1", CancellationToken.None);

        Assert.AreEqual("detached", ((AgentToolResultItem.Text)result.Items[0]).Value);
        Assert.IsFalse(missing.Success);
    }

    [TestMethod]
    public async Task ToolCallBridge_WaitsForTheResultOfAnExternalToolAndShowsItsOutput()
    {
        var bridge = new HarnessToolCallBridge();
        bridge.Register("run-1", "shell", Parse("""{"command":"dotnet --version"}"""), isCodeAltaTool: false);
        await bridge.ReportProgressAsync("run-1", "10.0.1", CancellationToken.None);
        var definition = bridge.Resolve(new AgentMessagePart.ToolCall("run-1", "shell", Parse("{}")), registered: null, "A tool run by the agent.", static () => Task.CompletedTask)!;
        var progress = new List<string>();
        var run = definition.Handler(
            new AgentToolInvocation(Provider, "s1", "run-1", "shell", Parse("{}"), (update, _) =>
            {
                progress.Add(update.Delta!);
                return ValueTask.CompletedTask;
            }),
            CancellationToken.None);

        Assert.IsTrue(bridge.Complete("run-1", new AgentToolResult(true, [new AgentToolResultItem.Text("done")])));
        var result = await run;

        Assert.AreEqual("shell", definition.Spec.Name);
        Assert.AreEqual("done", ((AgentToolResultItem.Text)result.Items[0]).Value);
        CollectionAssert.AreEqual(new[] { "10.0.1" }, progress);
        Assert.IsFalse(bridge.HasOutstandingCalls);
    }

    [TestMethod]
    public async Task ToolCallBridge_FailsOutstandingCallsWhenTheHarnessStops()
    {
        var bridge = new HarnessToolCallBridge();
        bridge.Register("run-1", "shell", Parse("{}"), isCodeAltaTool: false);
        var definition = bridge.Resolve(new AgentMessagePart.ToolCall("run-1", "shell", Parse("{}")), null, "x", static () => Task.CompletedTask)!;
        var run = definition.Handler(new AgentToolInvocation(Provider, "s1", "run-1", "shell", Parse("{}")), CancellationToken.None);

        bridge.FailOutstanding("The agent stopped.");

        var result = await run;
        Assert.IsFalse(result.Success);
        Assert.AreEqual("The agent stopped.", result.Error);
        bridge.ForgetCompleted();
        Assert.IsNull(bridge.Find("run-1"));
    }

    [TestMethod]
    public void SanitizeName_KeepsWhatAToolNameCanHold()
    {
        Assert.AreEqual("Read_file", HarnessToolCallBridge.SanitizeName("Read file"));
        Assert.AreEqual("tool", HarnessToolCallBridge.SanitizeName("  "));
        Assert.AreEqual("tool", HarnessToolCallBridge.SanitizeName("…"));
        Assert.AreEqual(64, HarnessToolCallBridge.SanitizeName(new string('a', 100)).Length);
    }

    [TestMethod]
    public void Instructions_UpdateNamesWhereTheyWereGiven()
    {
        var update = HarnessInstructions.CreateUpdate("First part.\n\nSecond part.", "First part.\n\nThird part.", "the CodeAlta block of your first prompt");

        StringAssert.Contains(update, "it updates the CodeAlta block of your first prompt, and earlier notes");
        StringAssert.Contains(update, "- Second part.");
        StringAssert.Contains(update, "Third part.");
        Assert.AreEqual(16, HarnessInstructions.Hash("x").Length);
    }

    [TestMethod]
    public void History_RendersTheEarlierConversationWithoutThePromptThatStarts()
    {
        AgentConversationMessage[] conversation =
        [
            new(AgentConversationRole.User, [new AgentMessagePart.Text("Hello")]),
            new(AgentConversationRole.Assistant, [new AgentMessagePart.Text("Hi"), new AgentMessagePart.ToolCall("c1", "read_file", Parse("""{"path":"a.cs"}"""))]),
            new(AgentConversationRole.Tool, [new AgentMessagePart.ToolResult("c1", new AgentToolResult(true, [new AgentToolResultItem.Text("content")]))]),
            new(AgentConversationRole.User, [new AgentMessagePart.Text("Next")]),
        ];

        var history = HarnessHistory.CreatePreviousConversation(HarnessHistory.WithoutTrailingUserMessages(conversation))!;

        StringAssert.StartsWith(history, HarnessHistory.PreviousConversationOpening);
        StringAssert.Contains(history, "[assistant called tool read_file]");
        StringAssert.Contains(history, "[tool result]\ncontent");
        Assert.IsFalse(history.Contains("Next", StringComparison.Ordinal));
        Assert.IsNull(HarnessHistory.CreatePreviousConversation([]));
    }

    private static AgentToolDefinition CreateTool(string name, AgentToolHandler handler)
        => new(new AgentToolSpec(name, "A tool.", Parse("""{"type":"object"}""")), handler);

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
