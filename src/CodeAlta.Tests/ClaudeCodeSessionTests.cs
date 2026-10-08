using System.Text.Json;
using System.Text.Json.Nodes;
using CodeAlta.Agent;
using CodeAlta.Agent.Claude;

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
        // A turn the CLI starts by itself after a run is not shown: coming back later takes a reminder.
        StringAssert.Contains(appended, "is not shown to the user");
        Assert.IsTrue(appended.IndexOf("Be brief.", StringComparison.Ordinal) > appended.IndexOf("`alta session`", StringComparison.Ordinal));

        // No tool of Claude Code is named: which ones a version has is its own business.
        foreach (var tool in new[] { "Glob", "Grep", "WebFetch" })
        {
            Assert.IsFalse(appended.Contains(tool, StringComparison.Ordinal), tool);
        }
        Assert.AreEqual("codealta", initialize.GetProperty("sdkMcpServers")[0].GetString());
        Assert.AreEqual("codealta_pre_edit", initialize.GetProperty("hooks").GetProperty("PreToolUse")[0].GetProperty("hookCallbackIds")[0].GetString());
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
                ["callback_id"] = "codealta_pre_edit",
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

    private static ClaudeCodeFakeProcess SessionProcess(ClaudeCodeFakeCli cli)
        => cli.Processes.Last(static process => process.Launch.Arguments.Contains("--mcp-config"));

    private static async Task<IAgentSession> CreateSessionAsync(
        ClaudeCodeModelProviderRuntime runtime,
        TestTempDirectory directory,
        AgentPermissionRequestHandler? onPermission = null,
        IReadOnlyList<AgentToolDefinition>? tools = null,
        string? model = null,
        AgentReasoningEffort? reasoningEffort = null,
        string? developerInstructions = null)
        => await runtime.CreateSessionAsync(new AgentSessionCreateOptions
        {
            ProviderKey = runtime.Descriptor.ProviderId.Value,
            WorkingDirectory = directory.Path,
            Model = model,
            ReasoningEffort = reasoningEffort,
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
