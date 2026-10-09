using System.Text.Json;
using System.Text.Json.Nodes;
using CodeAlta.Agent;
using CodeAlta.Agent.Claude;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Tests;

/// <summary>
/// Runs sessions of CodeAlta against a scripted Claude Code CLI: what the CLI writes becomes the events and the
/// journal of the session, and what the CLI asks is answered by the host of the session.
/// </summary>
[TestClass]
public sealed class ClaudeCodeSessionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [TestMethod]
    public async Task TextTurn_StreamsAndRecordsTheAnswer()
    {
        using var directory = TestTempDirectory.Create();
        var cli = new ClaudeCodeFakeCli();
        await using var runtime = new ClaudeCodeModelProviderRuntime(cli.CreateOptions());
        await using var session = await CreateSessionAsync(runtime, directory, model: "sonnet", reasoningEffort: AgentReasoningEffort.High);
        var events = Collect(session);

        await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("hello") }).WaitAsync(Timeout);

        var process = SessionProcess(cli);
        CollectionAssert.IsSubsetOf(
            new[] { "-p", "--input-format", "--output-format", "stream-json", "--verbose", "--include-partial-messages", "--permission-prompt-tool", "stdio", "--model", "sonnet", "--effort", "high" },
            process.Launch.Arguments.ToArray());
        Assert.IsTrue(process.Launch.Arguments.Any(static argument => argument.StartsWith("--session-id=", StringComparison.Ordinal)));
        Assert.AreEqual(directory.Path, process.Launch.WorkingDirectory);
        Assert.AreEqual("hello", ClaudeCodeFakeProcess.UserText(process.UserMessages.Single()));
        Assert.IsFalse(
            process.Launch.Arguments.Any(static argument => argument.StartsWith("--disallowedTools", StringComparison.Ordinal)),
            "A session without the live tool has no child sessions to delegate to: Claude Code keeps its subagents.");

        var delta = events.Snapshot().OfType<AgentContentDeltaEvent>().Single(static e => e.Kind == AgentContentKind.Assistant);
        var completed = events.Snapshot().OfType<AgentContentCompletedEvent>().Single(static e => e.Kind == AgentContentKind.Assistant);
        Assert.AreEqual("ok", delta.Delta);
        Assert.AreEqual("ok", completed.Content);
        Assert.AreEqual(delta.ContentId, completed.ContentId, "The final text replaces the streamed one.");
        Assert.IsTrue(events.Snapshot().OfType<AgentSessionUpdateEvent>().Any(static e => e.Kind == AgentSessionUpdateKind.Idle));

        var usage = events.Snapshot().OfType<AgentSessionUpdateEvent>().Last(static e => e.Usage?.Window is not null).Usage!;
        Assert.AreEqual(200_000, usage.Window!.TokenLimit);
        Assert.IsTrue(usage.Window.CurrentTokens >= 2412, "The context holds what the last request read and wrote.");
    }

    [TestMethod]
    public async Task Initialize_AppendsTheInstructionsOfCodeAltaAndRegistersItsTools()
    {
        using var directory = TestTempDirectory.Create();
        var cli = new ClaudeCodeFakeCli();
        await using var runtime = new ClaudeCodeModelProviderRuntime(cli.CreateOptions());
        var alta = new AgentToolDefinition(
            new AgentToolSpec("alta", "The live tool.", JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone()),
            static (_, _) => Task.FromResult(new AgentToolResult(true, [])));
        await using var session = await CreateSessionAsync(runtime, directory, tools: [alta], developerInstructions: "# Agent Prompt\nBe brief.");

        await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("hello") }).WaitAsync(Timeout);

        var process = SessionProcess(cli);
        var initialize = process.InitializeRequest;
        var appended = initialize.GetProperty("appendSystemPrompt").GetString()!;
        StringAssert.Contains(appended, "driven by CodeAlta");
        StringAssert.Contains(appended, "mcp__codealta__alta");
        StringAssert.Contains(appended, "never a shell command");
        StringAssert.Contains(appended, "Be brief.");

        // What CodeAlta has its own mechanism for is said once, for every agent prompt: which one to use.
        StringAssert.Contains(appended, "`alta ask`");
        StringAssert.Contains(appended, "`alta session`");
        // What the model delegates is a child session, which the user sees: the CLI starts without its own
        // subagent tool, under its name and the one older versions gave it, and without the one that runs agents
        // from a script, and the model is told so.
        CollectionAssert.Contains(process.Launch.Arguments.ToArray(), "--disallowedTools=Agent,Task,Workflow");
        StringAssert.Contains(appended, "`alta session` for everything you delegate");
        StringAssert.Contains(appended, "no subagent tool of Claude Code");
        Assert.IsFalse(appended.Contains("stay yours", StringComparison.Ordinal));
        // A turn the CLI starts by itself after a run is shown while the session is open: what has to bring the
        // agent back in any case takes a reminder.
        StringAssert.Contains(appended, "starts a turn the user sees as a run of the session");
        StringAssert.Contains(appended, "`alta reminder`");
        // A command CodeAlta runs itself is shown to the user and brings its result back: it is preferred for what takes long.
        StringAssert.Contains(appended, "`alta job start` is run by CodeAlta itself");
        Assert.IsTrue(appended.IndexOf("Be brief.", StringComparison.Ordinal) > appended.IndexOf("`alta session`", StringComparison.Ordinal));

        // No tool of Claude Code is named: which ones a version has is its own business.
        foreach (var tool in new[] { "Glob", "Grep", "WebFetch" })
        {
            Assert.IsFalse(appended.Contains(tool, StringComparison.Ordinal), tool);
        }
        Assert.AreEqual("codealta", initialize.GetProperty("sdkMcpServers")[0].GetString());
        // The CLI tells before it runs a tool, whichever it is: no matcher narrows the hook.
        var preTool = initialize.GetProperty("hooks").GetProperty("PreToolUse")[0];
        Assert.AreEqual("codealta_pre_tool", preTool.GetProperty("hookCallbackIds")[0].GetString());
        Assert.IsFalse(preTool.TryGetProperty("matcher", out _));
        Assert.IsFalse(process.Launch.Arguments.Contains("--system-prompt"), "The system prompt of Claude Code stays its own.");
        Assert.IsFalse(process.Launch.Arguments.Contains("--bare"), "No authentication method of the CLI is restricted.");

        // The variables of a Claude Code session CodeAlta was started from are not passed on; nothing else is removed.
        Assert.IsTrue(process.Launch.Environment.TryGetValue("CLAUDECODE", out var claudeCode) && claudeCode is null);
        Assert.IsFalse(process.Launch.Environment.Keys.Any(static name => name.StartsWith("ANTHROPIC_", StringComparison.Ordinal)));
        StringAssert.StartsWith(process.Launch.Environment["CLAUDE_AGENT_SDK_CLIENT_APP"], "codealta");
    }

    [TestMethod]
    public async Task CliTool_AsksThePermissionOfCodeAltaAndShowsTheResult()
    {
        using var directory = TestTempDirectory.Create();
        var cli = new ClaudeCodeFakeCli();
        JsonElement? decision = null;
        cli.OnUserMessage = async (process, user) =>
        {
            var input = new JsonObject { ["command"] = "echo hi", ["description"] = "Print hi" };
            process.EmitInit();
            process.EmitBlockStart("msg_1", 0, "tool_use");
            process.EmitAssistant("msg_1", new JsonArray(ClaudeCodeFakeProcess.ToolUseBlock("toolu_1", "Bash", input)));
            process.EmitMessageStop("tool_use");
            decision = await process.AskPermissionAsync("Bash", input, "toolu_1");
            process.EmitToolResult("toolu_1", "hi");
            process.EmitTextStream("msg_2", 0, "done");
            process.EmitAssistant("msg_2", new JsonArray(ClaudeCodeFakeProcess.TextBlock("done")));
            process.EmitMessageStop();
            process.EmitResult("done", user);
        };
        var permissions = new List<AgentPermissionRequest>();
        await using var runtime = new ClaudeCodeModelProviderRuntime(cli.CreateOptions());
        await using var session = await CreateSessionAsync(runtime, directory, onPermission: (request, _) =>
        {
            lock (permissions)
            {
                permissions.Add(request);
            }

            return Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce));
        });
        var events = Collect(session);

        await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("run it") }).WaitAsync(Timeout);

        var permission = Assert.IsInstanceOfType<AgentCommandPermissionRequest>(permissions.Single());
        Assert.AreEqual("echo hi", permission.Command);
        Assert.AreEqual("Print hi", permission.Reason);
        Assert.AreEqual(directory.Path, permission.WorkingDirectory);
        Assert.AreEqual("toolu_1", permission.InteractionId);
        Assert.AreEqual("allow", decision!.Value.GetProperty("behavior").GetString());
        Assert.AreEqual("echo hi", decision.Value.GetProperty("updatedInput").GetProperty("command").GetString());

        var activities = events.Snapshot().OfType<AgentActivityEvent>().Where(static e => e.ActivityId == "toolu_1").ToArray();
        CollectionAssert.AreEqual(
            new[] { AgentActivityPhase.Requested, AgentActivityPhase.Started, AgentActivityPhase.Completed },
            activities.Select(static e => e.Phase).ToArray());
        Assert.IsTrue(activities.All(static e => e.Name == "Bash"));
        var output = events.Snapshot().OfType<AgentContentCompletedEvent>().Single(static e => e.Kind == AgentContentKind.ToolOutput);
        Assert.AreEqual("hi", output.Content);
        Assert.AreEqual("done", events.Snapshot().OfType<AgentContentCompletedEvent>().Single(static e => e.Kind == AgentContentKind.Assistant).Content);
    }

    [TestMethod]
    public async Task DeniedPermission_IsAnsweredAsADenial()
    {
        using var directory = TestTempDirectory.Create();
        var cli = new ClaudeCodeFakeCli();
        JsonElement? decision = null;
        cli.OnUserMessage = async (process, user) =>
        {
            var input = new JsonObject { ["command"] = "rm -rf /" };
            process.EmitInit();
            process.EmitAssistant("msg_1", new JsonArray(ClaudeCodeFakeProcess.ToolUseBlock("toolu_1", "Bash", input)));
            decision = await process.AskPermissionAsync("Bash", input, "toolu_1");
            process.EmitToolResult("toolu_1", "Permission denied.", isError: true);
            process.EmitAssistant("msg_2", new JsonArray(ClaudeCodeFakeProcess.TextBlock("I did not run it.")));
            process.EmitResult("I did not run it.", user);
        };
        await using var runtime = new ClaudeCodeModelProviderRuntime(cli.CreateOptions());
        await using var session = await CreateSessionAsync(
            runtime,
            directory,
            onPermission: static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.Deny)));
        var events = Collect(session);

        await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("run it") }).WaitAsync(Timeout);

        Assert.AreEqual("deny", decision!.Value.GetProperty("behavior").GetString());
        Assert.IsTrue(events.Snapshot().OfType<AgentActivityEvent>().Any(static e => e.ActivityId == "toolu_1" && e.Phase == AgentActivityPhase.Failed));
    }

    [TestMethod]
    public async Task CancelledPermission_StopsTheTurnWithoutAFailure()
    {
        using var directory = TestTempDirectory.Create();
        var cli = new ClaudeCodeFakeCli();
        JsonElement? decision = null;
        var turns = 0;
        cli.OnUserMessage = async (process, user) =>
        {
            if (++turns == 2)
            {
                process.EmitResult("API Error: overloaded", user, isError: true, subtype: "error_during_execution");
                return;
            }

            var input = new JsonObject { ["command"] = "echo light > light.txt" };
            process.EmitInit();
            process.EmitAssistant("msg_1", new JsonArray(ClaudeCodeFakeProcess.ToolUseBlock("toolu_1", "Bash", input)));
            decision = await process.AskPermissionAsync("Bash", input, "toolu_1");
            process.EmitToolResult("toolu_1", "The user doesn't want to proceed with this tool use. The tool use was rejected.", isError: true);
            // What Claude Code writes for the turn it stopped on the interrupt of the answer.
            process.Emit(new JsonObject
            {
                ["type"] = "result",
                ["subtype"] = "error_during_execution",
                ["is_error"] = true,
                ["session_id"] = process.SessionId,
                ["errors"] = new JsonArray("[ede_diagnostic] result_type=user last_content_type=n/a stop_reason=tool_use"),
                ["user_message_uuids"] = new JsonArray(user.GetProperty("uuid").GetString()),
            });
            process.Emit(new JsonObject { ["type"] = "system", ["subtype"] = "session_state_changed", ["state"] = "idle", ["session_id"] = process.SessionId });
        };
        await using var runtime = new ClaudeCodeModelProviderRuntime(cli.CreateOptions());
        await using var session = await CreateSessionAsync(
            runtime,
            directory,
            onPermission: static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.Cancel)));
        var events = Collect(session);

        await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("run it") }).WaitAsync(Timeout);

        Assert.AreEqual("deny", decision!.Value.GetProperty("behavior").GetString());
        Assert.IsTrue(decision.Value.GetProperty("interrupt").GetBoolean());
        Assert.IsTrue(events.Snapshot().OfType<AgentActivityEvent>().Any(static e => e.ActivityId == "toolu_1" && e.Phase == AgentActivityPhase.Failed));
        Assert.IsFalse(events.Snapshot().OfType<AgentErrorEvent>().Any(), "The turn the user stopped did not fail.");

        // The cancel is the one of its own turn: a failure of the next is one.
        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("again") }).WaitAsync(Timeout));
        StringAssert.Contains(failure.Message, "overloaded");
    }

    [TestMethod]
    public async Task EditTool_WaitsForTheSessionBeforeItEditsSoThatTheChangeIsShown()
    {
        using var directory = TestTempDirectory.Create();
        var file = Path.Combine(directory.Path, "a.txt");
        await File.WriteAllTextAsync(file, "one\n");
        var cli = new ClaudeCodeFakeCli();
        cli.OnUserMessage = async (process, user) =>
        {
            var input = new JsonObject { ["file_path"] = file, ["old_string"] = "one", ["new_string"] = "two" };
            process.EmitInit();
            // No end of message is written: the hook is what tells that the call is complete.
            process.EmitAssistant("msg_1", new JsonArray(ClaudeCodeFakeProcess.ToolUseBlock("toolu_1", "Edit", input)));
            var hook = await process.RequestAsync(new JsonObject
            {
                ["subtype"] = "hook_callback",
                ["callback_id"] = "codealta_pre_tool",
                ["tool_use_id"] = "toolu_1",
                ["input"] = new JsonObject { ["hook_event_name"] = "PreToolUse", ["tool_name"] = "Edit", ["tool_use_id"] = "toolu_1" },
            });
            Assert.AreEqual("success", hook.GetProperty("subtype").GetString());
            await File.WriteAllTextAsync(file, "two\n");
            process.EmitToolResult("toolu_1", "The file was updated.");
            process.EmitAssistant("msg_2", new JsonArray(ClaudeCodeFakeProcess.TextBlock("edited")));
            process.EmitResult("edited", user);
        };
        await using var runtime = new ClaudeCodeModelProviderRuntime(cli.CreateOptions());
        await using var session = await CreateSessionAsync(runtime, directory);
        var events = Collect(session);

        await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("edit it") }).WaitAsync(Timeout);

        var completed = events.Snapshot().OfType<AgentActivityEvent>().Single(static e => e.ActivityId == "toolu_1" && e.Phase == AgentActivityPhase.Completed);
        var diff = completed.Details!.Value.GetProperty("diff").GetString()!;
        StringAssert.Contains(diff, "-one");
        StringAssert.Contains(diff, "+two");
        Assert.AreEqual(Path.GetFullPath(file), completed.Details.Value.GetProperty("modifiedFiles")[0].GetString());
        Assert.IsTrue(events.Snapshot().OfType<AgentSessionUpdateEvent>().Any(static e => e.Kind == AgentSessionUpdateKind.DiffUpdated));
    }

    [TestMethod]
    public async Task EditsOfOneMessage_EachWaitForTheSessionWhileTheModelStillWritesTheMessage()
    {
        using var directory = TestTempDirectory.Create();
        var file = Path.Combine(directory.Path, "a.txt");
        await File.WriteAllTextAsync(file, "one\n");
        var cli = new ClaudeCodeFakeCli();
        cli.OnUserMessage = async (process, user) =>
        {
            process.EmitInit();
            // The CLI runs a call as soon as the model wrote it: it asks for the first edit while the model
            // still writes the second.
            process.EmitBlockStart("msg_1", 0, "tool_use");
            process.EmitAssistant("msg_1", new JsonArray(ClaudeCodeFakeProcess.ToolUseBlock("toolu_1", "Edit", new JsonObject { ["file_path"] = file, ["old_string"] = "one", ["new_string"] = "two" })));
            var first = process.BeginRequest(PreToolHook("toolu_1", "Edit"));
            process.EmitBlockStart("msg_1", 1, "tool_use");
            process.EmitAssistant("msg_1", new JsonArray(ClaudeCodeFakeProcess.ToolUseBlock("toolu_2", "Edit", new JsonObject { ["file_path"] = file, ["old_string"] = "two", ["new_string"] = "three" })));
            await Task.Delay(200);
            Assert.IsFalse(first.Response.IsCompleted, "The session of CodeAlta has not read the file yet: it is given the call with the whole message.");
            process.EmitMessageStop("tool_use");
            await first.Response.WaitAsync(Timeout);
            await File.WriteAllTextAsync(file, "two\n");
            process.EmitToolResult("toolu_1", "The file was updated.");

            // The CLI runs the edits of a message one after the other.
            await process.RequestAsync(PreToolHook("toolu_2", "Edit"));
            await File.WriteAllTextAsync(file, "three\n");
            process.EmitToolResult("toolu_2", "The file was updated.");
            process.EmitAssistant("msg_2", new JsonArray(ClaudeCodeFakeProcess.TextBlock("edited")));
            process.EmitResult("edited", user);
        };
        await using var runtime = new ClaudeCodeModelProviderRuntime(cli.CreateOptions());
        await using var session = await CreateSessionAsync(runtime, directory);
        var events = Collect(session);

        await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("edit it") }).WaitAsync(Timeout);

        // Each edit shows its own change: the file was read right before it, not when the message arrived.
        string Diff(string callId) => events.Snapshot().OfType<AgentActivityEvent>()
            .Single(e => e.ActivityId == callId && e.Phase == AgentActivityPhase.Completed).Details!.Value.GetProperty("diff").GetString()!;
        StringAssert.Contains(Diff("toolu_1"), "-one");
        StringAssert.Contains(Diff("toolu_1"), "+two");
        Assert.IsFalse(Diff("toolu_1").Contains("three", StringComparison.Ordinal));
        StringAssert.Contains(Diff("toolu_2"), "-two");
        StringAssert.Contains(Diff("toolu_2"), "+three");
        Assert.IsFalse(Diff("toolu_2").Contains("one", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ToolCallsTheCliRunsAtTheSameTime_AreEachShownFromWhenTheyStart()
    {
        using var directory = TestTempDirectory.Create();
        var cli = new ClaudeCodeFakeCli();
        var events = new EventLog();
        cli.OnUserMessage = async (process, user) =>
        {
            process.EmitInit();
            // One message of the model: a file to read, two subagents, a command. The CLI runs a call as soon as
            // the model wrote it, and gives the result of the first while the model still writes the others.
            process.EmitBlockStart("msg_1", 0, "tool_use");
            process.EmitAssistant("msg_1", new JsonArray(ClaudeCodeFakeProcess.ToolUseBlock("toolu_read", "Read", new JsonObject { ["file_path"] = "a.txt" })));
            await process.RequestAsync(PreToolHook("toolu_read", "Read"));
            process.EmitToolResult("toolu_read", "the file");
            process.EmitBlockStart("msg_1", 1, "tool_use");
            process.EmitAssistant("msg_1", new JsonArray(ClaudeCodeFakeProcess.ToolUseBlock("toolu_one", "Agent", new JsonObject { ["prompt"] = "one" })));
            await process.RequestAsync(PreToolHook("toolu_one", "Agent"));
            process.EmitBlockStart("msg_1", 2, "tool_use");
            process.EmitAssistant("msg_1", new JsonArray(ClaudeCodeFakeProcess.ToolUseBlock("toolu_two", "Agent", new JsonObject { ["prompt"] = "two" })));
            await process.RequestAsync(PreToolHook("toolu_two", "Agent"));
            process.EmitBlockStart("msg_1", 3, "tool_use");
            process.EmitAssistant("msg_1", new JsonArray(ClaudeCodeFakeProcess.ToolUseBlock("toolu_build", "Bash", new JsonObject { ["command"] = "build" })));
            process.EmitMessageStop("tool_use");

            // The two subagents run at the same time: both are shown as running, and what the second writes is
            // shown while the first still runs.
            await events.WaitForAsync(Phase("toolu_one", AgentActivityPhase.Started), Timeout);
            await events.WaitForAsync(Phase("toolu_two", AgentActivityPhase.Started), Timeout);
            process.EmitAssistant("msg_sub", new JsonArray(ClaudeCodeFakeProcess.TextBlock("the second looks around")), parentToolUseId: "toolu_two");
            await events.WaitForAsync(static e => e is AgentContentDeltaEvent { Kind: AgentContentKind.ToolOutput, ParentActivityId: "toolu_two" }, Timeout);
            Assert.IsFalse(events.Snapshot().Any(Phase("toolu_build", AgentActivityPhase.Started)), "The CLI runs the command after the subagents: it has not started.");

            // The second ends before the first.
            process.EmitToolResult("toolu_two", "two done");
            await events.WaitForAsync(Phase("toolu_two", AgentActivityPhase.Completed), Timeout);
            Assert.IsFalse(events.Snapshot().Any(Phase("toolu_one", AgentActivityPhase.Completed)));
            process.EmitToolResult("toolu_one", "one done");
            await process.RequestAsync(PreToolHook("toolu_build", "Bash"));
            process.EmitToolResult("toolu_build", "built");
            process.EmitAssistant("msg_2", new JsonArray(ClaudeCodeFakeProcess.TextBlock("all done")));
            process.EmitResult("all done", user);
        };
        await using var runtime = new ClaudeCodeModelProviderRuntime(cli.CreateOptions());
        await using var session = await CreateSessionAsync(runtime, directory);
        session.Subscribe(events.Add);

        await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("go") }).WaitAsync(Timeout);

        // The calls of the message stay together: all are known before the first is shown as running.
        var activities = events.Snapshot().OfType<AgentActivityEvent>().Select(static e => (e.ActivityId, e.Phase)).ToList();
        var firstStarted = activities.FindIndex(static activity => activity.Phase == AgentActivityPhase.Started);
        CollectionAssert.AreEqual(
            new[] { "toolu_read", "toolu_one", "toolu_two", "toolu_build" },
            activities.Take(firstStarted).Where(static activity => activity.Phase == AgentActivityPhase.Requested).Select(static activity => activity.ActivityId).ToArray());
        Assert.IsTrue(
            activities.IndexOf(("toolu_build", AgentActivityPhase.Started)) > activities.IndexOf(("toolu_one", AgentActivityPhase.Completed)),
            "The command is shown as running when the CLI runs it.");
        var outputs = events.Snapshot().OfType<AgentContentCompletedEvent>().Where(static e => e.Kind == AgentContentKind.ToolOutput)
            .ToDictionary(static e => e.ParentActivityId!, static e => e.Content);
        Assert.AreEqual("the file", outputs["toolu_read"]);
        Assert.AreEqual("one done", outputs["toolu_one"]);
        Assert.AreEqual("two done", outputs["toolu_two"]);
        Assert.AreEqual("built", outputs["toolu_build"]);
        Assert.AreEqual("all done", events.Snapshot().OfType<AgentContentCompletedEvent>().Single(static e => e.Kind == AgentContentKind.Assistant).Content);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ToolOfCodeAlta_IsRunByTheSessionAndItsResultGoesBackToTheCli(bool withToolUseId)
    {
        using var directory = TestTempDirectory.Create();
        var cli = new ClaudeCodeFakeCli();
        JsonElement? listed = null;
        JsonElement? result = null;
        cli.OnUserMessage = async (process, user) =>
        {
            var input = new JsonObject { ["text"] = "hi" };
            process.EmitInit();
            listed = (await process.McpAsync("tools/list")).GetProperty("result");
            process.EmitBlockStart("msg_1", 0, "tool_use");
            process.EmitAssistant("msg_1", new JsonArray(ClaudeCodeFakeProcess.ToolUseBlock("toolu_1", "mcp__codealta__echo_tool", input)));
            process.EmitMessageStop("tool_use");
            result = await process.CallMcpToolAsync("echo_tool", input, withToolUseId ? "toolu_1" : null);
            process.EmitToolResult("toolu_1", "echo:hi");
            process.EmitAssistant("msg_2", new JsonArray(ClaudeCodeFakeProcess.TextBlock("done")));
            process.EmitResult("done", user);
        };
        var invocations = 0;
        var echo = new AgentToolDefinition(
            new AgentToolSpec("echo_tool", "Echoes a text.", JsonDocument.Parse("""{"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}""").RootElement.Clone()),
            (invocation, _) =>
            {
                Interlocked.Increment(ref invocations);
                return Task.FromResult(new AgentToolResult(true, [new AgentToolResultItem.Text("echo:" + invocation.Arguments.GetProperty("text").GetString())]));
            });
        await using var runtime = new ClaudeCodeModelProviderRuntime(cli.CreateOptions());
        await using var session = await CreateSessionAsync(runtime, directory, tools: [echo]);
        var events = Collect(session);

        await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("echo") }).WaitAsync(Timeout);

        var names = listed!.Value.GetProperty("tools").EnumerateArray().Select(static tool => tool.GetProperty("name").GetString()).ToArray();
        CollectionAssert.Contains(names, "echo_tool");
        CollectionAssert.DoesNotContain(names, "read_file", "Claude Code has its own file tools.");
        CollectionAssert.DoesNotContain(names, "shell_command");
        Assert.AreEqual(1, invocations);
        Assert.AreEqual("echo:hi", result!.Value.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.IsFalse(result.Value.GetProperty("isError").GetBoolean());

        var activities = events.Snapshot().OfType<AgentActivityEvent>().Where(static e => e.ActivityId == "toolu_1").ToArray();
        Assert.IsTrue(activities.All(static e => e.Name == "echo_tool"), "The session knows the tool by its own name.");
        Assert.AreEqual(AgentActivityPhase.Completed, activities[^1].Phase);
    }

    [TestMethod]
    public async Task Question_IsAskedThroughTheFormOfCodeAlta()
    {
        using var directory = TestTempDirectory.Create();
        var cli = new ClaudeCodeFakeCli();
        JsonElement? decision = null;
        cli.OnUserMessage = async (process, user) =>
        {
            var input = new JsonObject
            {
                ["questions"] = new JsonArray(new JsonObject
                {
                    ["question"] = "Which one?",
                    ["header"] = "Choice",
                    ["options"] = new JsonArray(new JsonObject { ["label"] = "A", ["description"] = "First" }, new JsonObject { ["label"] = "B" }),
                }),
            };
            process.EmitInit();
            process.EmitAssistant("msg_1", new JsonArray(ClaudeCodeFakeProcess.ToolUseBlock("toolu_1", "AskUserQuestion", input)));
            decision = await process.AskPermissionAsync("AskUserQuestion", input, "toolu_1");
            process.EmitToolResult("toolu_1", "The user answered B.");
            process.EmitAssistant("msg_2", new JsonArray(ClaudeCodeFakeProcess.TextBlock("B it is")));
            process.EmitResult("B it is", user);
        };
        AgentUserInputRequest? asked = null;
        await using var runtime = new ClaudeCodeModelProviderRuntime(cli.CreateOptions());
        await using var session = await CreateSessionAsync(runtime, directory);

        await session.SendAsync(new AgentSendOptions
        {
            Input = AgentInput.Text("ask me"),
            EnableUserInputTool = true,
            OnUserInputRequest = (request, _) =>
            {
                asked = request;
                return Task.FromResult(new AgentUserInputResponse(new Dictionary<string, string> { [request.Form.Prompts[0].Id] = "B" }));
            },
        }).WaitAsync(Timeout);

        Assert.IsNotNull(asked);
        Assert.AreEqual("Which one?", asked.Form.Prompts[0].Question);
        Assert.AreEqual("Choice", asked.Form.Prompts[0].Header);
        CollectionAssert.AreEqual(new[] { "A", "B" }, asked.Form.Prompts[0].Options!.Select(static option => option.Label).ToArray());
        Assert.AreEqual("allow", decision!.Value.GetProperty("behavior").GetString());
        Assert.AreEqual("B", decision.Value.GetProperty("updatedInput").GetProperty("answers").GetProperty("Which one?").GetString());
    }

    [TestMethod]
    public async Task PlanModeOfClaudeCode_IsNotEnteredNorLeftWithAnApprovalNobodyGave()
    {
        using var directory = TestTempDirectory.Create();
        var cli = new ClaudeCodeFakeCli();
        JsonElement? enter = null;
        JsonElement? exit = null;
        cli.OnUserMessage = async (process, user) =>
        {
            process.EmitInit();
            process.EmitAssistant("msg_1", new JsonArray(ClaudeCodeFakeProcess.ToolUseBlock("toolu_1", "EnterPlanMode", new JsonObject())));
            enter = await process.RequestAsync(new JsonObject
            {
                ["subtype"] = "hook_callback",
                ["callback_id"] = "codealta_plan_mode",
                ["tool_use_id"] = "toolu_1",
                ["input"] = new JsonObject { ["hook_event_name"] = "PreToolUse", ["tool_name"] = "EnterPlanMode", ["tool_use_id"] = "toolu_1" },
            });
            process.EmitToolResult("toolu_1", "refused", isError: true);
            process.EmitAssistant("msg_2", new JsonArray(ClaudeCodeFakeProcess.ToolUseBlock("toolu_2", "ExitPlanMode", new JsonObject { ["plan"] = "Do it." })));
            exit = await process.AskPermissionAsync("ExitPlanMode", new JsonObject { ["plan"] = "Do it." }, "toolu_2");
            process.EmitToolResult("toolu_2", "refused", isError: true);
            process.EmitAssistant("msg_3", new JsonArray(ClaudeCodeFakeProcess.TextBlock("no plan mode")));
            process.EmitResult("no plan mode", user);
        };
        // The session was put in the plan mode of Claude Code by the configuration of the provider.
        var options = cli.CreateOptions();
        await using var runtime = new ClaudeCodeModelProviderRuntime(new ClaudeCodeModelProviderRuntimeOptions
        {
            ProviderKey = options.ProviderKey,
            TransportFactory = cli,
            ResolveCli = options.ResolveCli,
            GetEnvironmentVariable = options.GetEnvironmentVariable,
            PermissionMode = "plan",
        });
        await using var session = await CreateSessionAsync(runtime, directory);

        await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("plan it") }).WaitAsync(Timeout);

        // CodeAlta has a plan mode of its own, which the user sees: the one of Claude Code is not entered.
        var hooks = SessionProcess(cli).InitializeRequest.GetProperty("hooks").GetProperty("PreToolUse");
        Assert.AreEqual("EnterPlanMode", hooks[1].GetProperty("matcher").GetString());
        Assert.AreEqual("codealta_plan_mode", hooks[1].GetProperty("hookCallbackIds")[0].GetString());
        var decision = enter!.Value.GetProperty("response").GetProperty("hookSpecificOutput");
        Assert.AreEqual("PreToolUse", decision.GetProperty("hookEventName").GetString());
        Assert.AreEqual("deny", decision.GetProperty("permissionDecision").GetString());
        StringAssert.Contains(decision.GetProperty("permissionDecisionReason").GetString(), "set_agent");

        // Leaving it tells the model that the user approved its plan: nobody did, in a session that was made to plan.
        Assert.AreEqual("deny", exit!.Value.GetProperty("behavior").GetString());
        StringAssert.Contains(exit.Value.GetProperty("message").GetString(), "permission_mode");
    }

    [TestMethod]
    public async Task PlanModeOfClaudeCode_CanBeLeftWhenTheSessionWasNotMadeToPlan()
    {
        using var directory = TestTempDirectory.Create();
        var cli = new ClaudeCodeFakeCli();
        JsonElement? exit = null;
        cli.OnUserMessage = async (process, user) =>
        {
            process.EmitInit();
            process.EmitAssistant("msg_1", new JsonArray(ClaudeCodeFakeProcess.ToolUseBlock("toolu_1", "ExitPlanMode", new JsonObject { ["plan"] = "Do it." })));
            exit = await process.AskPermissionAsync("ExitPlanMode", new JsonObject { ["plan"] = "Do it." }, "toolu_1");
            process.EmitToolResult("toolu_1", "left");
            process.EmitAssistant("msg_2", new JsonArray(ClaudeCodeFakeProcess.TextBlock("done")));
            process.EmitResult("done", user);
        };
        await using var runtime = new ClaudeCodeModelProviderRuntime(cli.CreateOptions());
        await using var session = await CreateSessionAsync(runtime, directory);

        await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("go") }).WaitAsync(Timeout);

        // A model that got there another way is not kept in a mode in which it can change nothing.
        Assert.AreEqual("allow", exit!.Value.GetProperty("behavior").GetString());
    }

    [TestMethod]
    [DataRow(null, "plan", "deny")]
    [DataRow("plan", "default", "allow")]
    public async Task PlanModeOfClaudeCode_IsLeftOrNot_ByThePermissionModeOfTheSession(string? providerMode, string sessionMode, string behavior)
    {
        using var directory = TestTempDirectory.Create();
        var cli = new ClaudeCodeFakeCli();
        JsonElement? exit = null;
        cli.OnUserMessage = async (process, user) =>
        {
            process.EmitInit();
            process.EmitAssistant("msg_1", new JsonArray(ClaudeCodeFakeProcess.ToolUseBlock("toolu_1", "ExitPlanMode", new JsonObject { ["plan"] = "Do it." })));
            exit = await process.AskPermissionAsync("ExitPlanMode", new JsonObject { ["plan"] = "Do it." }, "toolu_1");
            process.EmitToolResult("toolu_1", "refused", isError: true);
            process.EmitAssistant("msg_2", new JsonArray(ClaudeCodeFakeProcess.TextBlock("the plan")));
            process.EmitResult("the plan", user);
        };
        // The mode of the session is the one of its turns, whatever the provider is configured with.
        var options = cli.CreateOptions();
        await using var runtime = new ClaudeCodeModelProviderRuntime(new ClaudeCodeModelProviderRuntimeOptions
        {
            ProviderKey = options.ProviderKey,
            TransportFactory = cli,
            ResolveCli = options.ResolveCli,
            PermissionMode = providerMode,
        });
        await using var session = await CreateSessionAsync(runtime, directory, permissionMode: sessionMode);

        await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("plan it") }).WaitAsync(Timeout);

        Assert.AreEqual(sessionMode, SessionProcess(cli).LaunchPermissionMode);
        Assert.AreEqual(behavior, exit!.Value.GetProperty("behavior").GetString());
        if (behavior == "deny")
        {
            StringAssert.Contains(exit.Value.GetProperty("message").GetString(), "permission mode");
        }
    }

    [TestMethod]
    public async Task QuestionOfARunThatCannotAsk_IsRefusedWithTheWayCodeAltaAsks()
    {
        using var directory = TestTempDirectory.Create();
        var cli = new ClaudeCodeFakeCli();
        JsonElement? decision = null;
        cli.OnUserMessage = async (process, user) =>
        {
            var input = new JsonObject { ["questions"] = new JsonArray(new JsonObject { ["question"] = "Which one?" }) };
            process.EmitInit();
            process.EmitAssistant("msg_1", new JsonArray(ClaudeCodeFakeProcess.ToolUseBlock("toolu_1", "AskUserQuestion", input)));
            decision = await process.AskPermissionAsync("AskUserQuestion", input, "toolu_1");
            process.EmitToolResult("toolu_1", "refused", isError: true);
            process.EmitAssistant("msg_2", new JsonArray(ClaudeCodeFakeProcess.TextBlock("I could not ask")));
            process.EmitResult("I could not ask", user);
        };
        await using var runtime = new ClaudeCodeModelProviderRuntime(cli.CreateOptions());
        var alta = new AgentToolDefinition(
            new AgentToolSpec("alta", "The live tool.", JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone()),
            static (_, _) => Task.FromResult(new AgentToolResult(true, [])));
        await using var session = await CreateSessionAsync(runtime, directory, tools: [alta]);

        // The desktop application does not offer live questions: it asks with `alta ask`.
        await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("ask me"), EnableUserInputTool = false }).WaitAsync(Timeout);

        Assert.AreEqual("deny", decision!.Value.GetProperty("behavior").GetString());
        var message = decision.Value.GetProperty("message").GetString()!;
        StringAssert.Contains(message, "mcp__codealta__alta");
        StringAssert.Contains(message, "ask");
        Assert.IsFalse(decision.Value.TryGetProperty("interrupt", out _));
    }

    [TestMethod]
    public async Task SignedOutCli_FailsTheTurnWithTheWayToSignIn()
    {
        using var directory = TestTempDirectory.Create();
        var cli = new ClaudeCodeFakeCli();
        cli.OnUserMessage = (process, user) =>
        {
            // What Claude Code 2.1.289 writes when it has no credentials.
            process.EmitInit();
            process.EmitAssistant("6702496d", new JsonArray(ClaudeCodeFakeProcess.TextBlock("Not logged in · Please run /login")), error: "authentication_failed", model: "<synthetic>");
            process.EmitResult("Not logged in · Please run /login", user, isError: true);
            return Task.CompletedTask;
        };
        await using var runtime = new ClaudeCodeModelProviderRuntime(cli.CreateOptions());
        await using var session = await CreateSessionAsync(runtime, directory);
        var events = Collect(session);

        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("hello") }).WaitAsync(Timeout));

        StringAssert.Contains(failure.Message, "not signed in");
        StringAssert.Contains(failure.Message, "/login");
        Assert.IsTrue(events.Snapshot().OfType<AgentErrorEvent>().Any());
        Assert.IsFalse(events.Snapshot().OfType<AgentContentCompletedEvent>().Any(static e => e.Kind == AgentContentKind.Assistant), "The error is not an answer of the model.");
    }

    [TestMethod]
    public async Task ApiKeyWithoutAnAnswer_StopsTheTurnBeforeTheCliStarts()
    {
        using var directory = TestTempDirectory.Create();
        var cli = new ClaudeCodeFakeCli();
        await using var runtime = new ClaudeCodeModelProviderRuntime(WithApiKey(cli, ClaudeCodeApiKeyPolicy.FollowClaudeCode, savedAnswers: null));
        await using var session = await CreateSessionAsync(runtime, directory);

        // Started here, the CLI would bill the key without asking: nothing is started until the user said.
        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("hello") }).WaitAsync(Timeout));

        StringAssert.Contains(failure.Message, "ANTHROPIC_API_KEY");
        StringAssert.Contains(failure.Message, "anthropic_api_key");
        Assert.IsFalse(cli.Processes.Any(static process => process.Launch.Arguments.Contains("--mcp-config")), "No session process was started.");
    }

    [TestMethod]
    [DataRow(ClaudeCodeApiKeyPolicy.FollowClaudeCode, """{"customApiKeyResponses":{"approved":[],"rejected":["0123456789abcdefKLMN"]}}""", false)]
    [DataRow(ClaudeCodeApiKeyPolicy.FollowClaudeCode, """{"customApiKeyResponses":{"approved":["0123456789abcdefKLMN"],"rejected":[]}}""", true)]
    [DataRow(ClaudeCodeApiKeyPolicy.Ignore, """{"customApiKeyResponses":{"approved":["0123456789abcdefKLMN"],"rejected":[]}}""", false)]
    [DataRow(ClaudeCodeApiKeyPolicy.Use, null, true)]
    public async Task ApiKey_IsGivenToTheCliAsDecided(ClaudeCodeApiKeyPolicy policy, string? savedAnswers, bool given)
    {
        using var directory = TestTempDirectory.Create();
        var cli = new ClaudeCodeFakeCli();
        await using var runtime = new ClaudeCodeModelProviderRuntime(WithApiKey(cli, policy, savedAnswers));
        await using var session = await CreateSessionAsync(runtime, directory);

        await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("hello") }).WaitAsync(Timeout);

        // The process inherits the key unless it is removed from its environment.
        var environment = SessionProcess(cli).Launch.Environment;
        Assert.AreEqual(!given, environment.TryGetValue("ANTHROPIC_API_KEY", out var value) && value is null);
    }

    [TestMethod]
    public async Task TurnThatFailsAfterAnAnswer_KeepsTheAnswerAndReportsTheFailure()
    {
        using var directory = TestTempDirectory.Create();
        var cli = new ClaudeCodeFakeCli();
        cli.OnUserMessage = static (process, user) =>
        {
            process.EmitInit();
            process.EmitAssistant("msg_1", new JsonArray(ClaudeCodeFakeProcess.TextBlock("Half of the work is done.")));
            process.EmitResult(null, user, isError: true, subtype: "error_max_turns");
            return Task.CompletedTask;
        };
        await using var runtime = new ClaudeCodeModelProviderRuntime(cli.CreateOptions());
        await using var session = await CreateSessionAsync(runtime, directory);
        var events = Collect(session);

        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("do it all") }).WaitAsync(Timeout));

        StringAssert.Contains(failure.Message, "turn limit");
        Assert.AreEqual(
            "Half of the work is done.",
            events.Snapshot().OfType<AgentContentCompletedEvent>().Single(static e => e.Kind == AgentContentKind.Assistant).Content);
    }

    [TestMethod]
    public async Task UnknownAndMalformedOutput_IsSkipped()
    {
        using var directory = TestTempDirectory.Create();
        var cli = new ClaudeCodeFakeCli();
        cli.OnUserMessage = (process, user) =>
        {
            process.EmitRaw("[SandboxDebug] not a message");
            process.EmitRaw("{ this is not json");
            process.EmitRaw("");
            process.Emit(new JsonObject { ["type"] = "a_message_of_a_later_version", ["payload"] = new JsonArray(1, 2) });
            process.Emit(new JsonObject { ["type"] = "system", ["subtype"] = "a_later_subtype" });
            process.Emit(new JsonObject { ["type"] = "command_lifecycle", ["state"] = "started" });
            process.EmitInit();
            process.EmitStreamEvent(new JsonObject { ["type"] = "a_later_stream_event" });
            process.EmitAssistant("msg_1", new JsonArray(
                new JsonObject { ["type"] = "a_later_block" },
                ClaudeCodeFakeProcess.TextBlock("still here")));
            process.EmitResult("still here", user);
            return Task.CompletedTask;
        };
        await using var runtime = new ClaudeCodeModelProviderRuntime(cli.CreateOptions());
        await using var session = await CreateSessionAsync(runtime, directory);
        var events = Collect(session);

        await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("hello") }).WaitAsync(Timeout);

        Assert.AreEqual("still here", events.Snapshot().OfType<AgentContentCompletedEvent>().Single(static e => e.Kind == AgentContentKind.Assistant).Content);
    }

    [TestMethod]
    public async Task SubagentOutput_IsShownUnderTheToolThatStartedIt()
    {
        using var directory = TestTempDirectory.Create();
        var cli = new ClaudeCodeFakeCli();
        cli.OnUserMessage = async (process, user) =>
        {
            process.EmitInit();
            process.EmitAssistant("msg_1", new JsonArray(ClaudeCodeFakeProcess.ToolUseBlock("toolu_1", "Agent", new JsonObject { ["prompt"] = "look around" })));
            process.EmitMessageStop("tool_use");
            // The subagent asks a permission for a tool call the session does not know: it is answered all the same.
            var decision = await process.AskPermissionAsync("Bash", new JsonObject { ["command"] = "ls" }, "toolu_sub_1");
            Assert.AreEqual("allow", decision.GetProperty("behavior").GetString());
            process.EmitAssistant("msg_sub", new JsonArray(
                ClaudeCodeFakeProcess.TextBlock("subagent text"),
                ClaudeCodeFakeProcess.ToolUseBlock("toolu_sub_1", "Bash", new JsonObject { ["command"] = "ls" })), parentToolUseId: "toolu_1");
            process.EmitToolResult("toolu_sub_1", "a.txt", parentToolUseId: "toolu_1");
            process.EmitToolResult("toolu_1", "The subagent found a.txt.");
            process.EmitAssistant("msg_2", new JsonArray(ClaudeCodeFakeProcess.TextBlock("found it")));
            process.EmitResult("found it", user);
        };
        await using var runtime = new ClaudeCodeModelProviderRuntime(cli.CreateOptions());
        await using var session = await CreateSessionAsync(runtime, directory);
        var events = Collect(session);

        await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("explore") }).WaitAsync(Timeout);

        var assistant = events.Snapshot().OfType<AgentContentCompletedEvent>().Where(static e => e.Kind == AgentContentKind.Assistant).Select(static e => e.Content).ToArray();
        CollectionAssert.AreEqual(new[] { "found it" }, assistant);
        Assert.IsFalse(events.Snapshot().OfType<AgentActivityEvent>().Any(static e => e.ActivityId == "toolu_sub_1"), "The tool calls of a subagent are its own.");
        var progress = string.Concat(events.Snapshot().OfType<AgentContentDeltaEvent>().Where(static e => e.Kind == AgentContentKind.ToolOutput && e.ParentActivityId == "toolu_1").Select(static e => e.Delta));
        StringAssert.Contains(progress, "subagent text");
        StringAssert.Contains(progress, "[Bash] ls");
    }

    [TestMethod]
    public async Task Abort_InterruptsTheCliAndKeepsTheConversation()
    {
        using var directory = TestTempDirectory.Create();
        var cli = new ClaudeCodeFakeCli();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var turn = 0;
        cli.OnUserMessage = async (process, user) =>
        {
            if (Interlocked.Increment(ref turn) == 1)
            {
                process.EmitInit();
                process.EmitAssistant("msg_1", new JsonArray(ClaudeCodeFakeProcess.ToolUseBlock("toolu_1", "Bash", new JsonObject { ["command"] = "sleep 600" })));
                process.EmitMessageStop("tool_use");
                started.TrySetResult();
                await process.Interrupted;
                return;
            }

            process.EmitTextTurn("msg_2", "second", user);
        };
        await using var runtime = new ClaudeCodeModelProviderRuntime(cli.CreateOptions());
        await using var session = await CreateSessionAsync(runtime, directory);
        var events = Collect(session);

        var send = session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("wait") });
        await started.Task.WaitAsync(Timeout);
        await events.WaitForAsync(static e => e is AgentActivityEvent { ActivityId: "toolu_1", Phase: AgentActivityPhase.Started }, Timeout);
        await session.AbortAsync().WaitAsync(Timeout);
        await Assert.ThrowsAsync<OperationCanceledException>(() => send.WaitAsync(Timeout));

        var process = SessionProcess(cli);
        Assert.IsTrue(process.Interrupted.IsCompleted);
        Assert.IsFalse(process.IsDisposed, "An interrupted CLI that went idle again is kept.");

        // The next message goes to the same process: nothing of the interrupted turn is mistaken for its answer.
        await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("again") }).WaitAsync(Timeout);
        Assert.AreSame(process, SessionProcess(cli));
        Assert.AreEqual("second", events.Snapshot().OfType<AgentContentCompletedEvent>().Last(static e => e.Kind == AgentContentKind.Assistant).Content);
    }

    [TestMethod]
    public async Task CliThatDoesNotStop_IsStoppedAndResumedByTheNextTurn()
    {
        using var directory = TestTempDirectory.Create();
        var cli = new ClaudeCodeFakeCli { AnswerInterrupt = false };
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cli.OnUserMessage = async (process, user) =>
        {
            if (process.ResumedSessionId is not null)
            {
                process.EmitTextTurn("msg_2", "resumed", user);
                return;
            }

            process.EmitInit();
            process.EmitAssistant("msg_1", new JsonArray(ClaudeCodeFakeProcess.ToolUseBlock("toolu_1", "Bash", new JsonObject { ["command"] = "sleep 600" })));
            process.EmitMessageStop("tool_use");
            started.TrySetResult();
            await process.Interrupted;
        };
        await using var runtime = new ClaudeCodeModelProviderRuntime(cli.CreateOptions());
        await using var session = await CreateSessionAsync(runtime, directory);
        var events = Collect(session);

        var send = session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("wait") });
        await started.Task.WaitAsync(Timeout);
        await events.WaitForAsync(static e => e is AgentActivityEvent { ActivityId: "toolu_1", Phase: AgentActivityPhase.Started }, Timeout);
        await session.AbortAsync().WaitAsync(Timeout);
        await Assert.ThrowsAsync<OperationCanceledException>(() => send.WaitAsync(Timeout));

        var first = SessionProcess(cli);
        Assert.IsTrue(first.IsDisposed);

        await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("again") }).WaitAsync(Timeout);

        var second = SessionProcess(cli);
        Assert.AreNotSame(first, second);
        Assert.AreEqual(first.SessionId, second.ResumedSessionId);
    }

    [TestMethod]
    public async Task ResumedSession_ResumesTheTranscriptOfTheCli()
    {
        using var directory = TestTempDirectory.Create();
        var cli = new ClaudeCodeFakeCli();
        string sessionId;
        string claudeSessionId;
        await using (var runtime = new ClaudeCodeModelProviderRuntime(cli.CreateOptions()))
        await using (var session = await CreateSessionAsync(runtime, directory))
        {
            await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("first") }).WaitAsync(Timeout);
            sessionId = session.SessionId;
            claudeSessionId = SessionProcess(cli).SessionId;
        }

        await using var resumedRuntime = new ClaudeCodeModelProviderRuntime(cli.CreateOptions());
        await using var resumed = await resumedRuntime.ResumeSessionAsync(sessionId, CreateResumeOptions(directory));
        await resumed.SendAsync(new AgentSendOptions { Input = AgentInput.Text("second") }).WaitAsync(Timeout);

        var process = SessionProcess(cli);
        Assert.AreEqual(claudeSessionId, process.ResumedSessionId);
        Assert.AreEqual("second", ClaudeCodeFakeProcess.UserText(process.UserMessages.Single()), "The CLI already holds the conversation.");
    }

    [TestMethod]
    public async Task MissingTranscript_StartsAgainWithTheRecordedConversationAsContext()
    {
        using var directory = TestTempDirectory.Create();
        var cli = new ClaudeCodeFakeCli();
        string sessionId;
        await using (var runtime = new ClaudeCodeModelProviderRuntime(cli.CreateOptions()))
        await using (var session = await CreateSessionAsync(runtime, directory))
        {
            await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("first question") }).WaitAsync(Timeout);
            sessionId = session.SessionId;
        }

        cli.FailResume = true;
        await using var resumedRuntime = new ClaudeCodeModelProviderRuntime(cli.CreateOptions());
        await using var resumed = await resumedRuntime.ResumeSessionAsync(sessionId, CreateResumeOptions(directory));
        await resumed.SendAsync(new AgentSendOptions { Input = AgentInput.Text("second question") }).WaitAsync(Timeout);

        var process = SessionProcess(cli);
        Assert.IsNull(process.ResumedSessionId);
        var sent = ClaudeCodeFakeProcess.UserText(process.UserMessages.Single());
        StringAssert.Contains(sent, "<codealta_previous_conversation>");
        StringAssert.Contains(sent, "first question");
        StringAssert.Contains(sent, "ok");
        StringAssert.EndsWith(sent, "second question");
    }

    [TestMethod]
    public async Task CliThatExits_FailsTheTurnWithItsErrorOutput()
    {
        using var directory = TestTempDirectory.Create();
        var cli = new ClaudeCodeFakeCli();
        cli.OnUserMessage = (process, _) =>
        {
            process.EmitInit();
            process.Exit(3);
            return Task.CompletedTask;
        };
        await using var runtime = new ClaudeCodeModelProviderRuntime(cli.CreateOptions());
        await using var session = await CreateSessionAsync(runtime, directory);

        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("hello") }).WaitAsync(Timeout));

        StringAssert.Contains(failure.Message, "exited with code 3");
    }

    [TestMethod]
    public async Task Compaction_IsDoneByTheCli()
    {
        using var directory = TestTempDirectory.Create();
        var cli = new ClaudeCodeFakeCli();
        cli.OnUserMessage = (process, user) =>
        {
            if (ClaudeCodeFakeProcess.UserText(user) == "/compact")
            {
                process.Emit(new JsonObject { ["type"] = "system", ["subtype"] = "status", ["status"] = "compacting", ["session_id"] = process.SessionId });
                process.Emit(new JsonObject
                {
                    ["type"] = "system",
                    ["subtype"] = "compact_boundary",
                    ["session_id"] = process.SessionId,
                    ["compact_metadata"] = new JsonObject { ["trigger"] = "manual", ["pre_tokens"] = 50000, ["post_tokens"] = 4000 },
                });
                process.EmitResult(string.Empty, user);
                return Task.CompletedTask;
            }

            process.EmitTextTurn("msg_1", "ok", user);
            return Task.CompletedTask;
        };
        await using var runtime = new ClaudeCodeModelProviderRuntime(cli.CreateOptions());
        await using var session = await CreateSessionAsync(runtime, directory);
        await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("hello") }).WaitAsync(Timeout);
        var events = Collect(session);

        var outcome = await ((IAgentCompactionOutcomeProvider)session).CompactWithOutcomeAsync().WaitAsync(Timeout);

        Assert.IsNotNull(outcome);
        Assert.IsTrue(outcome.Success);
        Assert.AreEqual(50000, outcome.PreCompactionTokens);
        Assert.AreEqual(4000, outcome.PostCompactionTokens);
        var completed = events.Snapshot().OfType<AgentSessionUpdateEvent>().Single(static e => e.Kind == AgentSessionUpdateKind.CompactionCompleted);
        Assert.AreEqual(4000, completed.Usage!.Window!.CurrentTokens);
        Assert.AreEqual(2, SessionProcess(cli).UserMessages.Count, "The command went to the process of the session.");
    }

    [TestMethod]
    public async Task SteeringMessage_IsSentToTheRunningCli()
    {
        using var directory = TestTempDirectory.Create();
        var cli = new ClaudeCodeFakeCli();
        var toolStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var steered = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var messages = 0;
        cli.OnUserMessage = async (process, user) =>
        {
            if (Interlocked.Increment(ref messages) == 2)
            {
                // A message sent during a turn has its own turn after it.
                steered.TrySetResult(user);
                return;
            }

            process.EmitInit();
            process.EmitAssistant("msg_1", new JsonArray(ClaudeCodeFakeProcess.ToolUseBlock("toolu_1", "Bash", new JsonObject { ["command"] = "build" })));
            process.EmitMessageStop("tool_use");
            toolStarted.TrySetResult();
            await release.Task;
            process.EmitToolResult("toolu_1", "built");
            var second = await steered.Task;
            process.EmitAssistant("msg_2", new JsonArray(ClaudeCodeFakeProcess.TextBlock("built")));
            process.EmitMessageStop();
            process.EmitResult("built", user, idle: false);
            process.EmitAssistant("msg_3", new JsonArray(ClaudeCodeFakeProcess.TextBlock("and tested")));
            process.EmitMessageStop();
            process.EmitResult("and tested", second);
        };
        await using var runtime = new ClaudeCodeModelProviderRuntime(cli.CreateOptions());
        await using var session = await CreateSessionAsync(runtime, directory);
        var events = Collect(session);

        var send = session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("build") });
        await toolStarted.Task.WaitAsync(Timeout);
        await events.WaitForAsync(static e => e is AgentActivityEvent { ActivityId: "toolu_1", Phase: AgentActivityPhase.Started }, Timeout);
        await session.SteerAsync(new AgentSteerOptions { Input = AgentInput.Text("also test") }).WaitAsync(Timeout);
        release.TrySetResult();
        await send.WaitAsync(Timeout);

        Assert.AreEqual("also test", ClaudeCodeFakeProcess.UserText((await steered.Task).Clone()));
        var answers = events.Snapshot().OfType<AgentContentCompletedEvent>().Where(static e => e.Kind == AgentContentKind.Assistant).Select(static e => e.Content).ToArray();
        CollectionAssert.AreEqual(new[] { "built", "and tested" }, answers);
    }

    [TestMethod]
    public async Task TasksTheCliRunsInTheBackground_AreToldToTheSession_DuringARunAndOutsideOne()
    {
        using var directory = TestTempDirectory.Create();
        var cli = new ClaudeCodeFakeCli
        {
            // The order of a real CLI: the list of its tasks, then which call started the new one, then the turn goes on.
            OnUserMessage = (process, user) =>
            {
                process.Emit(BackgroundTasks(process,
                    new JsonObject { ["task_id"] = "b1", ["run_id"] = "r1", ["task_type"] = "local_bash", ["description"] = "Run the tests" },
                    new JsonObject { ["task_id"] = "h1", ["task_type"] = "local_agent", ["description"] = "Housekeeping", ["ambient"] = true }));
                process.Emit(new JsonObject
                {
                    ["type"] = "system", ["subtype"] = "task_started", ["task_id"] = "b1", ["run_id"] = "r1", ["tool_use_id"] = "toolu_bg",
                    ["description"] = "Run the tests", ["is_backgrounded"] = true, ["task_type"] = "local_bash", ["session_id"] = process.SessionId,
                });
                process.EmitTextTurn("msg_1", "started", user);
                return Task.CompletedTask;
            },
        };
        await using var runtime = new ClaudeCodeModelProviderRuntime(cli.CreateOptions());
        await using var session = await CreateSessionAsync(runtime, directory);
        var tasks = (IAgentBackgroundTaskProvider)session;
        Assert.IsEmpty(tasks.BackgroundTasks);
        Assert.IsFalse(await tasks.StopBackgroundTaskAsync("b1").WaitAsync(Timeout), "Nothing runs yet: nothing is asked.");
        var events = Collect(session);

        await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("run the tests in the background") }).WaitAsync(Timeout);

        // The task is told with the call that started it; what the CLI runs for its own housekeeping is not.
        await events.WaitForAsync(static e => e is AgentBackgroundTasksEvent { Tasks: [{ ToolCallId: "toolu_bg" }] }, Timeout);
        var task = tasks.BackgroundTasks.Single();
        Assert.AreEqual(("b1", "command", "Run the tests", "toolu_bg", (string?)null), (task.TaskId, task.Kind, task.Description, task.ToolCallId, task.ParentTaskId));
        Assert.IsTrue(task.StartedAt > DateTimeOffset.UtcNow.AddMinutes(-1));
        var told = events.Snapshot().OfType<AgentBackgroundTasksEvent>().ToArray();
        // The tasks go on outside the runs: their event names no run, which would show the session as running.
        Assert.IsTrue(told.All(static e => e.RunId is null && e.Ended.Count == 0 && e.Tasks.All(static item => item.TaskId == "b1")));
        // They are the state of a process that runs now: the history of the session does not keep them.
        Assert.IsFalse((await session.GetHistoryAsync()).OfType<AgentBackgroundTasksEvent>().Any());

        // The turn ended and the task goes on. It is stopped: the CLI is asked, says that it was killed, then lists what is left.
        var process = SessionProcess(cli);
        Assert.IsTrue(await tasks.StopBackgroundTaskAsync("b1").WaitAsync(Timeout));
        Assert.AreEqual("b1", process.Received.Single(static message => message.GetProperty("type").GetString() == "control_request"
            && message.GetProperty("request").GetProperty("subtype").GetString() == "stop_task").GetProperty("request").GetProperty("task_id").GetString());
        // The task is no longer going on from the moment its end is told.
        await events.WaitForAsync(static e => e is AgentBackgroundTasksEvent { Tasks.Count: 0, Ended: [{ TaskId: "b1", ToolCallId: "toolu_bg", Outcome: AgentBackgroundTaskOutcome.Stopped }] }, Timeout);
        // Its notice came too, and is not a second end; the list that no longer has it changes nothing that is shown.
        process.Emit(BackgroundTasks(process, new JsonObject { ["task_id"] = "h1", ["task_type"] = "local_agent", ["ambient"] = true }));
        await process.McpAsync("ping").WaitAsync(Timeout);
        Assert.IsEmpty(tasks.BackgroundTasks);
        Assert.AreEqual(1, events.Snapshot().OfType<AgentBackgroundTasksEvent>().Sum(static e => e.Ended.Count));
        // Once it ended, the task is never told as going on again by what the CLI still says of that end.
        var afterEnd = events.Snapshot().OfType<AgentBackgroundTasksEvent>().SkipWhile(static e => e.Ended.Count == 0).ToArray();
        Assert.IsFalse(afterEnd.Any(static e => e.Tasks.Any(static item => item.TaskId == "b1")));

        // No run is going on: a task that the CLI starts and that fails is told all the same, with what the CLI says of it.
        process.Emit(BackgroundTasks(process, new JsonObject { ["task_id"] = "w1", ["task_type"] = "watch", ["description"] = "Watch the build" }));
        await process.McpAsync("ping").WaitAsync(Timeout);
        await events.WaitForAsync(static e => e is AgentBackgroundTasksEvent { Tasks: [{ TaskId: "w1", Kind: "watch", ToolCallId: null }] }, Timeout);
        process.Emit(new JsonObject { ["type"] = "system", ["subtype"] = "task_notification", ["task_id"] = "w1", ["status"] = "failed", ["summary"] = "The watch failed", ["session_id"] = process.SessionId });
        process.Emit(BackgroundTasks(process));
        await process.McpAsync("ping").WaitAsync(Timeout);
        await events.WaitForAsync(static e => e is AgentBackgroundTasksEvent { Tasks.Count: 0, Ended: [{ TaskId: "w1", Outcome: AgentBackgroundTaskOutcome.Failed, Summary: "The watch failed" }] }, Timeout);
        Assert.IsEmpty(tasks.BackgroundTasks);
        // None of this was a run of the session.
        Assert.IsTrue(events.Snapshot().OfType<AgentBackgroundTasksEvent>().All(static e => e.RunId is null));
    }

    private static JsonObject BackgroundTasks(ClaudeCodeFakeProcess process, params JsonObject[] tasks)
        => new() { ["type"] = "system", ["subtype"] = "background_tasks_changed", ["tasks"] = new JsonArray([.. tasks]), ["session_id"] = process.SessionId };

    [TestMethod]
    public async Task TurnTheCliStartsByItself_IsShownAsARunOfTheSession()
    {
        using var directory = TestTempDirectory.Create();
        var cli = new ClaudeCodeFakeCli();
        var permissions = new List<AgentPermissionRequest>();
        await using var runtime = new ClaudeCodeModelProviderRuntime(cli.CreateOptions());
        await using var session = await CreateSessionAsync(runtime, directory, onPermission: (request, _) =>
        {
            lock (permissions)
            {
                permissions.Add(request);
            }

            return Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce));
        });
        var events = Collect(session);
        // What orders the runs of a session starts the run the provider asks for: here the test does.
        var runs = (IAgentProviderInitiatedRuns)session;
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = runs.OnProviderInitiatedRun(() => asked.TrySetResult());
        var firstRun = await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("run the tests in the background") }).WaitAsync(Timeout);
        Assert.IsNull(runs.TakeProviderInitiatedRun());

        // The background command ends after the turn: the CLI says which one, and starts a turn by itself in
        // which it asks before it runs a command.
        var process = SessionProcess(cli);
        var input = new JsonObject { ["command"] = "cat results.txt", ["description"] = "Read the results" };
        process.Emit(new JsonObject
        {
            ["type"] = "system",
            ["subtype"] = "task_notification",
            ["task_id"] = "b1",
            ["status"] = "completed",
            ["summary"] = "Background command \"Run the tests\" completed (exit code 0)",
            ["session_id"] = process.SessionId,
        });
        process.Emit(new JsonObject { ["type"] = "system", ["subtype"] = "session_state_changed", ["state"] = "running", ["session_id"] = process.SessionId });
        process.EmitBlockStart("msg_bg", 0, "tool_use");
        process.EmitAssistant("msg_bg", new JsonArray(ClaudeCodeFakeProcess.ToolUseBlock("toolu_bg", "Bash", input)));
        process.EmitMessageStop("tool_use");
        var decision = process.AskPermissionAsync("Bash", input, "toolu_bg");
        await asked.Task.WaitAsync(Timeout);
        lock (permissions)
        {
            Assert.AreEqual(0, permissions.Count, "The run that shows the turn answers what the CLI asks in it.");
        }

        var options = runs.TakeProviderInitiatedRun();
        Assert.IsNotNull(options);
        var run = session.SendAsync(options);
        Assert.AreEqual("allow", (await decision.WaitAsync(Timeout)).GetProperty("behavior").GetString());
        process.EmitToolResult("toolu_bg", "3 passed");
        process.EmitTextStream("msg_bg2", 0, "The tests pass.");
        process.EmitAssistant("msg_bg2", new JsonArray(ClaudeCodeFakeProcess.TextBlock("The tests pass.")));
        process.EmitMessageStop();
        process.EmitResult("The tests pass.");
        var secondRun = await run.WaitAsync(Timeout);

        Assert.AreNotEqual(firstRun, secondRun);
        var shown = events.Snapshot().Where(e => e.RunId == secondRun).ToArray();
        // The run has the message that says what started it, in the place of a prompt. The CLI was not sent it.
        Assert.AreEqual(
            "Claude Code started a turn by itself: Background command \"Run the tests\" completed (exit code 0)",
            shown.OfType<AgentContentCompletedEvent>().Single(static e => e.Kind == AgentContentKind.User).Content);
        Assert.AreEqual(1, process.UserMessages.Count);
        CollectionAssert.AreEqual(
            new[] { AgentActivityPhase.Requested, AgentActivityPhase.Started, AgentActivityPhase.Completed },
            shown.OfType<AgentActivityEvent>().Where(static e => e.ActivityId == "toolu_bg").Select(static e => e.Phase).ToArray());
        Assert.AreEqual("3 passed", shown.OfType<AgentContentCompletedEvent>().Single(static e => e.Kind == AgentContentKind.ToolOutput).Content);
        Assert.AreEqual("The tests pass.", shown.OfType<AgentContentCompletedEvent>().Single(static e => e.Kind == AgentContentKind.Assistant).Content);
        Assert.IsTrue(shown.OfType<AgentSessionUpdateEvent>().Any(static e => e.Kind == AgentSessionUpdateKind.Idle));
        lock (permissions)
        {
            Assert.AreEqual("cat results.txt", Assert.IsInstanceOfType<AgentCommandPermissionRequest>(permissions.Single()).Command);
        }

        Assert.IsNull(runs.TakeProviderInitiatedRun(), "The turn was shown: no other run is started for it.");
    }

    [TestMethod]
    public async Task TurnTheCliStartsByItself_IsStartedAsARunByTheHost()
    {
        // The whole composition of an application without a window: the hub starts the run the provider asks for,
        // and the session runtime forwards it as it does a run that was sent.
        using var directory = TestTempDirectory.Create();
        var globalRoot = Path.Combine(directory.Path, "global");
        var projectRoot = Path.Combine(directory.Path, "project");
        Directory.CreateDirectory(globalRoot);
        Directory.CreateDirectory(projectRoot);
        var cli = new ClaudeCodeFakeCli();
        var options = cli.CreateOptions();
        var providerId = new ModelProviderId(options.ProviderKey);
        await using var host = await CodeAltaHost.CreateAsync(
            new CodeAltaHostOptions
            {
                GlobalRoot = globalRoot,
                CurrentProjectPath = projectRoot,
                IsHeadless = true,
                HasInteractiveUi = false,
                StartPlugins = false,
                ConfigureModelProviders = registry => registry.RegisterOrReplace(
                    ClaudeCodeModelProviderRuntime.CreateDescriptor(options),
                    () => new ClaudeCodeModelProviderRuntime(options)),
            });
        var execution = new SessionExecutionOptions
        {
            ProviderId = providerId,
            ProviderKey = providerId.Value,
            WorkingDirectory = projectRoot,
            ProjectRoots = [projectRoot],
            Model = "sonnet",
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
        };
        var events = new EventLog();
        using var streaming = new CancellationTokenSource();
        var stream = Task.Run(async () =>
        {
            await foreach (var runtimeEvent in host.RuntimeService.StreamEventsAsync(streaming.Token))
            {
                if (runtimeEvent is SessionAgentEvent { Event: var agentEvent })
                {
                    events.Add(agentEvent);
                }
            }
        });

        var session = await host.RuntimeService.CreateProjectSessionAsync(host.CurrentProject, execution, title: "Claude Code", CancellationToken.None);
        var firstRun = await host.RuntimeService.SendAsync(
            session,
            execution,
            new AgentSendOptions { Input = AgentInput.Text("run the tests in the background") },
            CancellationToken.None).WaitAsync(Timeout);
        await events.WaitForAsync(e => e is AgentSessionUpdateEvent { Kind: AgentSessionUpdateKind.Idle } && e.RunId == firstRun, Timeout);
        Assert.IsFalse(await host.RuntimeService.HasActiveRunAsync(session).WaitAsync(Timeout));

        var process = SessionProcess(cli);
        process.Emit(new JsonObject { ["type"] = "system", ["subtype"] = "session_state_changed", ["state"] = "running", ["session_id"] = process.SessionId });
        process.EmitTextStream("msg_bg", 0, "The tests pass.");
        process.EmitAssistant("msg_bg", new JsonArray(ClaudeCodeFakeProcess.TextBlock("The tests pass.")));
        process.EmitMessageStop();
        process.EmitResult("The tests pass.");

        await events.WaitForAsync(e => e is AgentSessionUpdateEvent { Kind: AgentSessionUpdateKind.Idle } && e.RunId is { } runId && runId != firstRun, Timeout);
        await streaming.CancelAsync();
        await stream.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        var shown = events.Snapshot().Where(e => e.RunId is { } runId && runId != firstRun).ToArray();
        Assert.AreEqual(1, shown.Select(static e => e.RunId).Distinct().Count());
        Assert.AreEqual("Claude Code started a turn by itself.", shown.OfType<AgentContentCompletedEvent>().Single(static e => e.Kind == AgentContentKind.User).Content);
        Assert.AreEqual("The tests pass.", shown.OfType<AgentContentCompletedEvent>().Single(static e => e.Kind == AgentContentKind.Assistant).Content);
        Assert.AreEqual(1, process.UserMessages.Count);
    }

    [TestMethod]
    public async Task TasksTheCliRunsInTheBackground_AreInTheRuntimeStateOfTheSession_WhichStopsThem()
    {
        // The whole composition of an application without a window: what the window reads of a session, and what it asks to stop.
        using var directory = TestTempDirectory.Create();
        var globalRoot = Path.Combine(directory.Path, "global");
        var projectRoot = Path.Combine(directory.Path, "project");
        Directory.CreateDirectory(globalRoot);
        Directory.CreateDirectory(projectRoot);
        var cli = new ClaudeCodeFakeCli();
        var options = cli.CreateOptions();
        var providerId = new ModelProviderId(options.ProviderKey);
        await using var host = await CodeAltaHost.CreateAsync(
            new CodeAltaHostOptions
            {
                GlobalRoot = globalRoot,
                CurrentProjectPath = projectRoot,
                IsHeadless = true,
                HasInteractiveUi = false,
                StartPlugins = false,
                ConfigureModelProviders = registry => registry.RegisterOrReplace(
                    ClaudeCodeModelProviderRuntime.CreateDescriptor(options),
                    () => new ClaudeCodeModelProviderRuntime(options)),
            });
        var execution = new SessionExecutionOptions
        {
            ProviderId = providerId,
            ProviderKey = providerId.Value,
            WorkingDirectory = projectRoot,
            ProjectRoots = [projectRoot],
            Model = "sonnet",
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
        };
        var session = await host.RuntimeService.CreateProjectSessionAsync(host.CurrentProject, execution, title: "Claude Code", CancellationToken.None);
        await host.RuntimeService.SendAsync(session, execution, new AgentSendOptions { Input = AgentInput.Text("run the tests in the background") }, CancellationToken.None).WaitAsync(Timeout);
        async Task<SessionRuntimeCurrentEntry> StateAsync(Func<SessionRuntimeCurrentEntry, bool> reached)
        {
            using var limit = new CancellationTokenSource(Timeout);
            while (true)
            {
                if ((await host.RuntimeService.GetCurrentStateAsync(session.SessionId, limit.Token)).Entry is { } entry && reached(entry)) return entry;
                await Task.Delay(20, limit.Token);
            }
        }

        await StateAsync(static entry => entry.ActiveRunId is null);
        Assert.IsFalse(await host.RuntimeService.StopBackgroundTaskAsync(session.SessionId, "b1").WaitAsync(Timeout), "A task that is not listed is not asked to stop.");

        // The turn is over and a command of it goes on.
        var process = SessionProcess(cli);
        process.Emit(BackgroundTasks(process, new JsonObject { ["task_id"] = "b1", ["task_type"] = "local_bash", ["description"] = "Run the tests" }));
        process.Emit(new JsonObject { ["type"] = "system", ["subtype"] = "task_started", ["task_id"] = "b1", ["tool_use_id"] = "toolu_bg", ["task_type"] = "local_bash", ["session_id"] = process.SessionId });
        var working = await StateAsync(static entry => entry.BackgroundTasks is [{ ToolCallId: "toolu_bg" }]);
        var task = working.BackgroundTasks.Single();
        Assert.AreEqual(("b1", "command", "Run the tests", (AgentBackgroundTaskOutcome?)null), (task.TaskId, task.Kind, task.Description, task.Outcome));
        Assert.IsNotNull(task.StartedAt);
        // A task in the background is not a run: the session takes a prompt, and is not shown as thinking.
        Assert.IsNull(working.ActiveRunId);
        Assert.IsFalse(await host.RuntimeService.HasActiveRunAsync(session).WaitAsync(Timeout));

        // It is stopped by the identity the state lists. Its end is kept for the call that started it.
        Assert.IsTrue(await host.RuntimeService.StopBackgroundTaskAsync(session.SessionId, "b1").WaitAsync(Timeout));
        process.Emit(BackgroundTasks(process));
        var stopped = (await StateAsync(static entry => entry.BackgroundTasks is [{ Outcome: not null }])).BackgroundTasks.Single();
        Assert.AreEqual(("b1", "command", "toolu_bg", AgentBackgroundTaskOutcome.Stopped, (DateTimeOffset?)null), (stopped.TaskId, stopped.Kind, stopped.ToolCallId, stopped.Outcome, stopped.StartedAt));
        Assert.IsFalse(await host.RuntimeService.StopBackgroundTaskAsync(session.SessionId, "b1").WaitAsync(Timeout), "A task that ended is not asked to stop.");
        Assert.AreEqual(1, process.Received.Count(static message => message.GetProperty("type").GetString() == "control_request"
            && message.GetProperty("request").GetProperty("subtype").GetString() == "stop_task"));

        // The same call starts a task again: it is shown as going on, and its previous end is forgotten.
        process.Emit(BackgroundTasks(process, new JsonObject { ["task_id"] = "b2", ["task_type"] = "local_bash", ["description"] = "Run the tests again" }));
        process.Emit(new JsonObject { ["type"] = "system", ["subtype"] = "task_started", ["task_id"] = "b2", ["tool_use_id"] = "toolu_bg", ["session_id"] = process.SessionId });
        var again = await StateAsync(static entry => entry.BackgroundTasks is [{ TaskId: "b2", ToolCallId: "toolu_bg" }]);
        Assert.IsNull(again.BackgroundTasks.Single().Outcome);
        // A task that runs to its end leaves nothing: the call that started it says that it completed.
        process.Emit(new JsonObject { ["type"] = "system", ["subtype"] = "task_notification", ["task_id"] = "b2", ["status"] = "completed", ["summary"] = "Done", ["tool_use_id"] = "toolu_bg", ["session_id"] = process.SessionId });
        process.Emit(BackgroundTasks(process));
        await StateAsync(static entry => entry.BackgroundTasks.Count == 0);
    }

    // What the CLI asks before it runs a tool call of the main conversation.
    private static JsonObject PreToolHook(string toolUseId, string toolName)
        => new()
        {
            ["subtype"] = "hook_callback",
            ["callback_id"] = "codealta_pre_tool",
            ["tool_use_id"] = toolUseId,
            ["input"] = new JsonObject { ["hook_event_name"] = "PreToolUse", ["tool_name"] = toolName, ["tool_use_id"] = toolUseId },
        };

    private static Func<AgentEvent, bool> Phase(string callId, AgentActivityPhase phase)
        => e => e is AgentActivityEvent activity && activity.ActivityId == callId && activity.Phase == phase;

    // The options of a provider of that CLI started with an API key in its environment, and the answers Claude Code saved.
    private static ClaudeCodeModelProviderRuntimeOptions WithApiKey(ClaudeCodeFakeCli cli, ClaudeCodeApiKeyPolicy policy, string? savedAnswers)
    {
        var options = cli.CreateOptions();
        return new ClaudeCodeModelProviderRuntimeOptions
        {
            ProviderKey = options.ProviderKey,
            TransportFactory = cli,
            ResolveCli = options.ResolveCli,
            ApiKeyPolicy = policy,
            GetEnvironmentVariable = static name => name == "ANTHROPIC_API_KEY" ? "sk-ant-api03-xxxx0123456789abcdefKLMN" : null,
            ReadClaudeConfig = () => savedAnswers,
        };
    }

    private static ClaudeCodeFakeProcess SessionProcess(ClaudeCodeFakeCli cli)
        => cli.Processes.Last(static process => process.Launch.Arguments.Contains("--mcp-config"));

    private static async Task<IAgentSession> CreateSessionAsync(
        ClaudeCodeModelProviderRuntime runtime,
        TestTempDirectory directory,
        AgentPermissionRequestHandler? onPermission = null,
        IReadOnlyList<AgentToolDefinition>? tools = null,
        string? model = null,
        AgentReasoningEffort? reasoningEffort = null,
        string? developerInstructions = null,
        string? permissionMode = null)
        => await runtime.CreateSessionAsync(new AgentSessionCreateOptions
        {
            ProviderKey = runtime.Descriptor.ProviderId.Value,
            WorkingDirectory = directory.Path,
            Model = model,
            ReasoningEffort = reasoningEffort,
            PermissionMode = permissionMode,
            DeveloperInstructions = developerInstructions,
            Tools = tools,
            OnPermissionRequest = onPermission ?? (static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce))),
        });

    private static AgentSessionResumeOptions CreateResumeOptions(TestTempDirectory directory)
        => new()
        {
            ProviderKey = "claude-code",
            WorkingDirectory = directory.Path,
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
        };

    private static EventLog Collect(IAgentSession session)
    {
        var log = new EventLog();
        session.Subscribe(log.Add);
        return log;
    }

    private sealed class EventLog
    {
        private readonly List<AgentEvent> _events = [];
        private readonly List<(Func<AgentEvent, bool> Match, TaskCompletionSource Completion)> _waiters = [];

        public void Add(AgentEvent @event)
        {
            lock (_events)
            {
                _events.Add(@event);
                foreach (var (match, completion) in _waiters)
                {
                    if (match(@event))
                    {
                        completion.TrySetResult();
                    }
                }
            }
        }

        public AgentEvent[] Snapshot()
        {
            lock (_events)
            {
                return [.. _events];
            }
        }

        public Task WaitForAsync(Func<AgentEvent, bool> match, TimeSpan timeout)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_events)
            {
                if (_events.Exists(e => match(e)))
                {
                    return Task.CompletedTask;
                }

                _waiters.Add((match, completion));
            }

            return completion.Task.WaitAsync(timeout);
        }
    }
}
