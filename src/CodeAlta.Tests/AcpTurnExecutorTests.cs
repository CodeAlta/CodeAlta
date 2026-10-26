using System.Text.Json;
using System.Text.Json.Nodes;
using CodeAlta.Acp;
using CodeAlta.Agent;
using CodeAlta.Agent.Harness;
using CodeAlta.Agent.Runtime;
using CodeAlta.Plugin.Acp;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Tests;

[TestClass]
public sealed class AcpTurnExecutorTests
{
    private static readonly ModelProviderId ProviderId = new("fake-acp");

    [TestMethod]
    public async Task FirstTurn_StartsASessionWithTheToolsOfCodeAlta_AndGivesTheInstructionsWithThePrompt()
    {
        var agent = new AcpFakeAgent();
        await using var fixture = new Fixture(agent);

        var conversation = await fixture.RunAsync([User("hello")]);

        var newSession = agent.RequestsOf("session/new").Single()!;
        var server = newSession["mcpServers"]!.AsArray().Single()!;
        Assert.AreEqual("codealta", server["name"]!.GetValue<string>());
        StringAssert.StartsWith(server["url"]!.GetValue<string>(), "http://127.0.0.1:");
        StringAssert.StartsWith(server["headers"]![0]!["value"]!.GetValue<string>(), "Bearer ");
        Assert.IsNull(newSession["systemPrompt"]);
        var prompt = PromptText(agent.RequestsOf("session/prompt").Single());
        StringAssert.Contains(prompt, AcpPrompts.HarnessOpening);
        StringAssert.Contains(prompt, "The instructions of CodeAlta for this session:\n\nBe brief.");
        StringAssert.Contains(prompt, "the MCP server named `codealta`");
        StringAssert.EndsWith(prompt, "hello");
        Assert.AreEqual("ok", Text(conversation[^1]));
        Assert.AreEqual("first-prompt", fixture.Executor.GetInstructionsChannel("session-1"));
        var state = AcpProviderStateOf(fixture.State);
        Assert.AreEqual("agent-session-1", state.GetProperty("sessionId").GetString());
        Assert.AreEqual(1, state.GetProperty("users").GetInt32());
        Assert.AreEqual(1, state.GetProperty("assistants").GetInt32());
    }

    [TestMethod]
    public async Task AnAgentInTheSandbox_ReachesTheToolsOfCodeAlta_WithTheHomeAndTheVariablesOfItsProvider()
    {
        var agent = new AcpFakeAgent();
        var settings = new AcpAgentSettings("dotnet", [], new Dictionary<string, string> { ["AGENT_TOKEN"] = "token-of-the-test" }, null, null)
        {
            SandboxHosts = ["api.example.com"],
        };
        await using var fixture = new Fixture(agent, settings) { ProcessHost = new TestSandbox() };

        await fixture.RunAsync([User("hello")]);

        var request = agent.Connections.Single().Request;
        Assert.IsNotNull(request.Host, "the agent starts in the sandbox");
        var url = new Uri(agent.RequestsOf("session/new").Single()!["mcpServers"]![0]!["url"]!.GetValue<string>());
        CollectionAssert.AreEqual(new[] { url.Port }, request.Needs.LocalPorts.ToArray(), "its proxy lets it reach the tools of CodeAlta");
        Assert.AreEqual("acp-fake-acp", request.Needs.OwnHome, "it signs in once, in a home of its provider");
        CollectionAssert.Contains(request.Needs.PassedVariables.ToArray(), "AGENT_TOKEN", "a token of its provider signs it in there");
        CollectionAssert.Contains(request.Needs.Hosts.ToArray(), "api.example.com");
    }

    [TestMethod]
    public async Task AnAgentInTheSandboxWithoutMcpOverHttp_IsGivenNoToolsOfCodeAlta()
    {
        var agent = new AcpFakeAgent { McpHttp = false };
        await using var fixture = new Fixture(agent) { ProcessHost = new TestSandbox() };

        await fixture.RunAsync([User("hello")]);

        Assert.IsEmpty(agent.RequestsOf("session/new").Single()!["mcpServers"]!.AsArray(), "the endpoint opened for its port is closed again");
    }

    // A sandbox: the fake factory runs the agent on pipes whatever host the request names.
    private sealed class TestSandbox : IAgentProcessHost
    {
        public string Key => "sandbox-of-the-test";

        public Task<IAgentHostedProcess> StartAsync(AgentProcessLaunch launch, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    [TestMethod]
    public async Task AgentThatTakesASystemPrompt_IsGivenTheInstructionsThere()
    {
        var agent = new AcpFakeAgent { SystemPrompt = true };
        await using var fixture = new Fixture(agent);

        await fixture.RunAsync([User("hello")]);

        var systemPrompt = agent.RequestsOf("session/new").Single()!["systemPrompt"]!;
        Assert.AreEqual("append", systemPrompt["mode"]!.GetValue<string>());
        StringAssert.Contains(systemPrompt["content"]![0]!["text"]!.GetValue<string>(), "Be brief.");
        Assert.IsFalse(PromptText(agent.RequestsOf("session/prompt").Single()).Contains(AcpPrompts.HarnessOpening, StringComparison.Ordinal));
        Assert.AreEqual("acp-system-prompt", fixture.Executor.GetInstructionsChannel("session-1"));
    }

    [TestMethod]
    public async Task ToolOfCodeAlta_IsRunByTheSessionWhenTheAgentCallsItThroughMcp()
    {
        JsonNode? mcpResult = null;
        var agent = new AcpFakeAgent
        {
            OnPrompt = async prompt =>
            {
                await prompt.SendTextAsync("Looking.");
                await prompt.StartToolAsync("call-1", "codealta/alta", "other", new JsonObject { ["args"] = new JsonArray("session", "list") });
                mcpResult = await prompt.CallMcpToolAsync("alta", new JsonObject { ["args"] = new JsonArray("session", "list") });
                await prompt.EndToolAsync("call-1", "two sessions");
                await prompt.SendTextAsync("Done.");
                return "end_turn";
            },
        };
        var runs = new List<JsonElement>();
        await using var fixture = new Fixture(agent)
        {
            Tools = [Tool("alta", invocation =>
            {
                runs.Add(invocation.Arguments.Clone());
                return new AgentToolResult(true, [new AgentToolResultItem.Text("session-1, session-2")]);
            })],
        };

        var conversation = await fixture.RunAsync([User("list the sessions")]);

        var call = conversation[1].Parts.OfType<AgentMessagePart.ToolCall>().Single();
        Assert.AreEqual("Looking.", ((AgentMessagePart.Text)conversation[1].Parts[0]).Value);
        Assert.AreEqual("alta", call.Name);
        Assert.AreEqual("call-1", call.CallId);
        Assert.AreEqual("session", call.Arguments.GetProperty("args")[0].GetString());
        Assert.AreEqual(1, runs.Count, "the tool runs once, for the call the session records");
        Assert.AreEqual("session-1, session-2", mcpResult!["content"]![0]!["text"]!.GetValue<string>());
        Assert.AreEqual("session-1, session-2", ToolResult(conversation[2]));
        Assert.AreEqual("Done.", Text(conversation[^1]));
    }

    [TestMethod]
    public async Task ToolAddedDuringTheRun_IsAnnouncedToTheAgent_ThatListsTheToolsAgain()
    {
        string[]? before = null;
        string[]? after = null;
        JsonNode? announced = null;
        var agent = new AcpFakeAgent
        {
            OnPrompt = async prompt =>
            {
                before = await prompt.ListMcpToolNamesAsync();
                await using var events = await prompt.OpenMcpEventsAsync();
                await prompt.StartToolAsync("call-1", "codealta/alta", "other", new JsonObject { ["args"] = new JsonArray("ui", "activate") });
                await prompt.CallMcpToolAsync("alta", new JsonObject { ["args"] = new JsonArray("ui", "activate") });
                await prompt.EndToolAsync("call-1", "activated");
                announced = await events.ReadMessageAsync(TimeSpan.FromSeconds(10));
                after = await prompt.ListMcpToolNamesAsync();
                return "end_turn";
            },
        };
        // `alta ui activate` registers the UI tools in the running turn.
        var tools = new List<AgentToolDefinition>();
        tools.Add(Tool("alta", _ =>
        {
            tools.Add(Tool("take_screenshot", static _ => new AgentToolResult(true, [new AgentToolResultItem.Text("picture")])));
            return new AgentToolResult(true, [new AgentToolResultItem.Text("activated")]);
        }));
        await using var fixture = new Fixture(agent) { Tools = tools };

        await fixture.RunAsync([User("look at the window")]);

        CollectionAssert.AreEqual(new[] { "alta" }, before);
        Assert.AreEqual("notifications/tools/list_changed", announced?["method"]?.GetValue<string>());
        CollectionAssert.AreEquivalent(new[] { "alta", "take_screenshot" }, after);
    }

    [TestMethod]
    public async Task ToolAddedDuringTheRun_IsAnnouncedWithTheResultOfTheCall_ToAnAgentWithoutAStreamOfEvents()
    {
        // Copilot opens no stream of events: it posts, and takes a response that is a stream of events.
        string[]? notified = null;
        string[]? after = null;
        var agent = new AcpFakeAgent
        {
            OnPrompt = async prompt =>
            {
                await prompt.ListMcpToolNamesAsync();
                await prompt.StartToolAsync("call-1", "codealta/alta", "other", new JsonObject { ["args"] = new JsonArray("ui", "activate") });
                var result = await prompt.CallMcpToolAsync("alta", new JsonObject { ["args"] = new JsonArray("ui", "activate") });
                await prompt.EndToolAsync("call-1", result!["content"]![0]!["text"]!.GetValue<string>());
                notified = [.. prompt.McpNotifications.Select(static message => message["method"]!.GetValue<string>())];
                after = await prompt.ListMcpToolNamesAsync();
                return "end_turn";
            },
        };
        var tools = new List<AgentToolDefinition>();
        tools.Add(Tool("alta", _ =>
        {
            tools.Add(Tool("take_screenshot", static _ => new AgentToolResult(true, [new AgentToolResultItem.Text("picture")])));
            return new AgentToolResult(true, [new AgentToolResultItem.Text("activated")]);
        }));
        await using var fixture = new Fixture(agent) { Tools = tools };

        var conversation = await fixture.RunAsync([User("look at the window")]);

        CollectionAssert.AreEqual(new[] { "notifications/tools/list_changed" }, notified);
        CollectionAssert.AreEquivalent(new[] { "alta", "take_screenshot" }, after);
        Assert.AreEqual("activated", ToolResult(conversation[2]));
    }

    [TestMethod]
    public async Task ToolOfTheAgent_IsShownByItsKind_WithTheResultTheAgentReports()
    {
        var agent = new AcpFakeAgent
        {
            OnPrompt = async prompt =>
            {
                await prompt.StartToolAsync("run-1", "Run dotnet --version", "execute", new JsonObject { ["command"] = "dotnet --version" });
                await prompt.EndToolAsync("run-1", "10.0.100");
                await prompt.StartToolAsync("edit-1", "Edit a.cs", "edit", new JsonObject { ["oldText"] = "a" }, new JsonArray(new JsonObject { ["path"] = "a.cs" }));
                await prompt.EndToolAsync("edit-1", "edited");
                return "end_turn";
            },
        };
        await using var fixture = new Fixture(agent);

        var conversation = await fixture.RunAsync([User("build")]);

        var shell = conversation[1].Parts.OfType<AgentMessagePart.ToolCall>().Single();
        Assert.AreEqual("shell", shell.Name);
        Assert.AreEqual("dotnet --version", shell.Arguments.GetProperty("command").GetString());
        Assert.AreEqual("Run dotnet --version", shell.Arguments.GetProperty("title").GetString());
        Assert.AreEqual("10.0.100", ToolResult(conversation[2]));
        var edit = conversation[3].Parts.OfType<AgentMessagePart.ToolCall>().Single();
        Assert.AreEqual("Edit", edit.Name);
        Assert.AreEqual(Path.Combine(fixture.WorkingDirectory, "a.cs"), edit.Arguments.GetProperty("file_path").GetString());
        Assert.AreEqual("a", edit.Arguments.GetProperty("oldText").GetString());
    }

    [TestMethod]
    public async Task PermissionRequest_IsAnsweredByTheUser_WithTheOptionOfTheDecision()
    {
        JsonNode? outcome = null;
        var agent = new AcpFakeAgent
        {
            OnPrompt = async prompt =>
            {
                await prompt.StartToolAsync("run-1", "Remove the build folder", "execute", new JsonObject { ["command"] = "rm -rf bin" });
                outcome = await prompt.RequestPermissionAsync("run-1", "Remove the build folder", "execute", new JsonObject { ["command"] = "rm -rf bin" });
                await prompt.EndToolAsync("run-1", "refused", success: false);
                return "end_turn";
            },
        };
        AgentPermissionRequest? asked = null;
        await using var fixture = new Fixture(agent)
        {
            OnPermission = (request, _) =>
            {
                asked = request;
                return Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.Deny));
            },
        };

        await fixture.RunAsync([User("clean")]);

        var command = (AgentCommandPermissionRequest)asked!;
        Assert.AreEqual("rm -rf bin", command.Command);
        Assert.AreEqual(fixture.WorkingDirectory, command.WorkingDirectory);
        Assert.AreEqual("selected", outcome!["outcome"]!["outcome"]!.GetValue<string>());
        Assert.AreEqual("no", outcome["outcome"]!["optionId"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task PermissionMode_AnswersWithoutAsking()
    {
        var outcomes = new List<string>();
        var agent = new AcpFakeAgent
        {
            OnPrompt = async prompt =>
            {
                outcomes.Add((await prompt.RequestPermissionAsync("e-1", "Edit a.cs", "edit"))!["outcome"]!["optionId"]!.GetValue<string>());
                outcomes.Add((await prompt.RequestPermissionAsync("r-1", "Read a.cs", "read"))!["outcome"]!["optionId"]!.GetValue<string>());
                return "end_turn";
            },
        };
        var asked = 0;
        await using var fixture = new Fixture(agent)
        {
            PermissionMode = "acceptEdits",
            OnPermission = (_, _) =>
            {
                asked++;
                return Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.Deny));
            },
        };

        await fixture.RunAsync([User("edit")]);

        CollectionAssert.AreEqual(new[] { "yes", "yes" }, outcomes);
        Assert.AreEqual(0, asked);
    }

    [TestMethod]
    public async Task LaterTurn_KeepsTheProcess_AndSendsOnlyTheNewMessage()
    {
        var agent = new AcpFakeAgent();
        await using var fixture = new Fixture(agent);
        var conversation = await fixture.RunAsync([User("one")]);

        conversation.Add(User("two"));
        await fixture.RunAsync(conversation);

        Assert.AreEqual(1, agent.Connections.Count);
        Assert.AreEqual(1, agent.RequestsOf("session/new").Count());
        var second = PromptText(agent.RequestsOf("session/prompt").Last());
        Assert.AreEqual("two", second);
    }

    [TestMethod]
    public async Task NewProcess_LoadsTheRecordedSession_WithoutItsReplay()
    {
        var agent = new AcpFakeAgent();
        List<AgentConversationMessage> conversation;
        AgentSessionState state;
        await using (var first = new Fixture(agent))
        {
            conversation = await first.RunAsync([User("one")]);
            state = first.State;
        }

        await using var fixture = new Fixture(agent) { State = state };
        conversation.Add(User("two"));
        conversation = await fixture.RunAsync(conversation);

        Assert.AreEqual(2, agent.Connections.Count);
        Assert.AreEqual("agent-session-1", agent.RequestsOf("session/load").Single()!["sessionId"]!.GetValue<string>());
        Assert.AreEqual(1, agent.RequestsOf("session/new").Count());
        Assert.AreEqual("ok", Text(conversation[^1]), "the replay of the loaded session is not part of the answer");
        Assert.AreEqual("two", PromptText(agent.RequestsOf("session/prompt").Last()));
    }

    [TestMethod]
    public async Task SessionReadAgainWithoutItsConversation_LoadsTheRecordedSession_WithoutReadingIt()
    {
        var agent = new AcpFakeAgent();
        List<AgentConversationMessage> conversation;
        AgentSessionState state;
        await using (var first = new Fixture(agent))
        {
            conversation = await first.RunAsync([User("one")]);
            state = first.State;
        }

        var loads = 0;
        var earlier = new AgentEarlierConversation(state, _ =>
        {
            loads++;
            return Task.FromResult<IReadOnlyList<AgentConversationMessage>>(conversation);
        });
        await using var fixture = new Fixture(agent) { State = state, Earlier = earlier };
        var resumed = await fixture.RunAsync([User("two")]);
        resumed.Add(User("three"));
        await fixture.RunAsync(resumed);

        Assert.AreEqual(0, loads, "The agent holds the conversation: it is not read.");
        Assert.AreEqual("agent-session-1", agent.RequestsOf("session/load").Single()!["sessionId"]!.GetValue<string>());
        Assert.AreEqual(1, agent.RequestsOf("session/new").Count());
        Assert.AreEqual("three", PromptText(agent.RequestsOf("session/prompt").Last()));
    }

    [TestMethod]
    public async Task AgentThatResumes_IsAskedToResume()
    {
        var agent = new AcpFakeAgent { ResumeSession = true };
        AgentSessionState state;
        List<AgentConversationMessage> conversation;
        await using (var first = new Fixture(agent))
        {
            conversation = await first.RunAsync([User("one")]);
            state = first.State;
        }

        await using var fixture = new Fixture(agent) { State = state };
        conversation.Add(User("two"));
        await fixture.RunAsync(conversation);

        Assert.AreEqual(1, agent.RequestsOf("session/resume").Count());
        Assert.AreEqual(0, agent.RequestsOf("session/load").Count());
    }

    [TestMethod]
    public async Task ConversationOfAnotherProvider_StartsANewSession_ThatIsToldWhatWasSaid()
    {
        var agent = new AcpFakeAgent();
        await using var fixture = new Fixture(agent);

        await fixture.RunAsync([
            User("what is in a.txt?"),
            new(AgentConversationRole.Assistant, [new AgentMessagePart.Text("It holds one line.")]),
            User("and b.txt?"),
        ]);

        var prompt = PromptText(agent.RequestsOf("session/prompt").Single());
        StringAssert.Contains(prompt, HarnessHistory.PreviousConversationOpening);
        StringAssert.Contains(prompt, "It holds one line.");
        StringAssert.EndsWith(prompt, "and b.txt?");
    }

    [TestMethod]
    public async Task SessionContinuedByAnotherProvider_StartsANewSessionWhenItComesBack()
    {
        var agent = new AcpFakeAgent();
        await using var fixture = new Fixture(agent);
        var conversation = await fixture.RunAsync([User("one")]);
        // Another provider answers the next turn: the agent did not see it.
        conversation.Add(User("two"));
        conversation.Add(new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.Text("answered by codex")]));
        conversation.Add(User("three"));

        await using var back = new Fixture(agent) { State = fixture.State };
        await back.RunAsync(conversation);

        Assert.AreEqual(2, agent.RequestsOf("session/new").Count(), "the recorded session lacks a turn: a new one is told the conversation");
        var prompt = PromptText(agent.RequestsOf("session/prompt").Last());
        StringAssert.Contains(prompt, "answered by codex");
        StringAssert.EndsWith(prompt, "three");
    }

    [TestMethod]
    public async Task ModelAndEffort_AreSetThroughTheConfigurationOptions()
    {
        var agent = new AcpFakeAgent();
        await using var fixture = new Fixture(agent) { ModelId = "smart", Effort = AgentReasoningEffort.Medium };

        await fixture.RunAsync([User("hello")]);

        var sets = agent.RequestsOf("session/set_config_option").ToArray();
        Assert.AreEqual("model", sets[0]!["configId"]!.GetValue<string>());
        Assert.AreEqual("smart", sets[0]!["value"]!.GetValue<string>());
        Assert.AreEqual("reasoning", sets[1]!["configId"]!.GetValue<string>());
        Assert.AreEqual("medium", sets[1]!["value"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task UserMessageDuringATurn_IsTheNextPromptOfTheSameRun()
    {
        var agent = new AcpFakeAgent
        {
            OnPrompt = async prompt =>
            {
                if (prompt.Text.EndsWith("steer", StringComparison.Ordinal))
                {
                    await prompt.SendTextAsync("steered");
                    return "end_turn";
                }

                await prompt.StartToolAsync("run-1", "Run tests", "execute", new JsonObject { ["command"] = "dotnet test" });
                await prompt.EndToolAsync("run-1", "passed");
                await prompt.SendTextAsync("first");
                return "end_turn";
            },
        };
        await using var fixture = new Fixture(agent) { BeforeNextTurn = conversation => conversation.Add(User("steer")) };

        var conversation = await fixture.RunAsync([User("go")]);

        Assert.AreEqual(2, agent.RequestsOf("session/prompt").Count());
        Assert.AreEqual("steer", PromptText(agent.RequestsOf("session/prompt").Last()));
        Assert.AreEqual("steered", Text(conversation[^1]));
    }

    [TestMethod]
    public async Task StoppedTurn_CancelsThePromptOfTheAgent_AndTheNextTurnGoesOn()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = new AcpFakeAgent
        {
            OnPrompt = async prompt =>
            {
                if (prompt.Text.EndsWith("again", StringComparison.Ordinal))
                {
                    await prompt.SendTextAsync("back");
                    return "end_turn";
                }

                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, prompt.CancellationToken);
                return "end_turn";
            },
        };
        await using var fixture = new Fixture(agent);
        using var cancellation = new CancellationTokenSource();
        var run = fixture.RunAsync([User("wait")], cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await cancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => run);
        Assert.AreEqual(1, agent.RequestsOf("session/cancel").Count());

        var conversation = await fixture.RunAsync([User("wait"), User("again")]);
        Assert.AreEqual("back", Text(conversation[^1]));
        Assert.AreEqual(1, agent.Connections.Count, "the agent stopped its prompt: its process is kept");
    }

    [TestMethod]
    public async Task AgentThatExits_FailsTheTurnAsUnavailable()
    {
        var agent = new AcpFakeAgent
        {
            OnPrompt = static async prompt =>
            {
                await prompt.SendTextAsync("partial");
                prompt.Connection.Exit();
                await Task.Delay(Timeout.Infinite, prompt.CancellationToken);
                return "end_turn";
            },
        };
        await using var fixture = new Fixture(agent);

        var error = await Assert.ThrowsExactlyAsync<AgentProviderFailureException>(() => fixture.RunAsync([User("hello")]));
        Assert.AreEqual(AgentFailureKind.Unavailable, error.Failure.Kind);
    }

    [TestMethod]
    public async Task AgentThatIsNotSignedIn_FailsTheTurnAsAuth()
    {
        var agent = new AcpFakeAgent { NewSessionError = -32000 };
        await using var fixture = new Fixture(agent);

        var error = await Assert.ThrowsExactlyAsync<AgentProviderFailureException>(() => fixture.RunAsync([User("hello")]));
        Assert.AreEqual(AgentFailureKind.Auth, error.Failure.Kind);
        StringAssert.Contains(error.Message, "is not signed in");
    }

    [TestMethod]
    public async Task Probe_ListsTheModels_AndWhatTheAgentCanDo_InASessionItCloses()
    {
        var agent = new AcpFakeAgent { McpHttp = false };
        await using var fixture = new Fixture(agent);

        var probe = await fixture.Runtime.ProbeAsync();

        Assert.AreEqual(ModelProviderAvailability.Ready, probe.Availability, probe.StatusMessage);
        CollectionAssert.AreEqual(new[] { "fast", "smart" }, probe.Models.Select(static model => model.Id).ToArray());
        Assert.AreEqual("FAST", probe.Models[0].DisplayName);
        CollectionAssert.AreEqual(new[] { AgentReasoningEffort.Low, AgentReasoningEffort.Medium, AgentReasoningEffort.High }, probe.Models[0].SupportedReasoningEfforts!.ToArray());
        Assert.IsFalse(probe.Capabilities.Single(static capability => capability.Id == "codealta-tools").Supported);
        Assert.IsTrue(probe.Capabilities.Single(static capability => capability.Id == "resume").Supported);
        Assert.AreEqual(1, agent.RequestsOf("session/close").Count());
        StringAssert.Contains(probe.StatusMessage, "Fake Agent 0.1.0");

        // The list is kept for the pickers.
        var models = await fixture.Runtime.ListModelsAsync(fixture.Runtime.RuntimeDescriptor);
        Assert.AreEqual(2, models.Count);
    }

    [TestMethod]
    public async Task Probe_ReadsTheEffortsOfEachModel_AndGivesTheAgentBackItsModel()
    {
        // The efforts of a model are known only once it is chosen: Copilot gives other ones to each model.
        var agent = new AcpFakeAgent { Models = ["fast", "smart", "plain", "other-plan"], CurrentModel = "smart" };
        agent.EffortsByModel["fast"] = ["low"];
        agent.EffortsByModel["plain"] = [];
        await using var fixture = new Fixture(agent);

        var probe = await fixture.Runtime.ProbeAsync();
        var byId = probe.Models.ToDictionary(static model => model.Id);

        CollectionAssert.AreEqual(new[] { AgentReasoningEffort.Low }, byId["fast"].SupportedReasoningEfforts!.ToArray());
        CollectionAssert.AreEqual(new[] { AgentReasoningEffort.Low, AgentReasoningEffort.Medium, AgentReasoningEffort.High }, byId["smart"].SupportedReasoningEfforts!.ToArray());
        Assert.AreEqual(0, byId["plain"].SupportedReasoningEfforts!.Count, "A model without the option runs without an effort.");
        Assert.IsTrue(probe.Models.All(AgentReasoningEffortCapability.IsListComplete));
        Assert.AreEqual(AgentReasoningEffort.High, byId["smart"].DefaultReasoningEffort);
        // The session ends on the model and the effort the agent had chosen, which an agent may keep for its next sessions.
        var sets = agent.RequestsOf("session/set_config_option").ToArray();
        Assert.AreEqual("smart", sets[^2]!["value"]!.GetValue<string>());
        Assert.AreEqual("reasoning", sets[^1]!["configId"]!.GetValue<string>());
        Assert.AreEqual("high", sets[^1]!["value"]!.GetValue<string>());

        // A session tells the efforts of its model only: what the probe learned of the others stays.
        await fixture.RunAsync([User("hello")]);
        var models = await fixture.Runtime.ListModelsAsync(fixture.Runtime.RuntimeDescriptor);
        CollectionAssert.AreEqual(new[] { AgentReasoningEffort.Low }, models.Single(static model => model.Id == "fast").SupportedReasoningEfforts!.ToArray());
    }

    [TestMethod]
    public async Task Probe_OfCursor_ChoosesNoModel_AndReadsTheVariantsFromTheirNames()
    {
        var agent = new AcpFakeAgent { Models = ["default[]", "grok-4.7[context=256k,reasoning_effort=high,fast=true]"], Efforts = [] };
        // The id of the registry names the agent; the command is one the test can start.
        var settings = new AcpAgentSettings("dotnet", ["acp"], new Dictionary<string, string>(), "cursor", "2026.10.22");
        var catalog = new RecordingCatalog();
        await using var fixture = new Fixture(agent, settings, AcpAgentQuirks.For(settings), catalog);

        var probe = await fixture.Runtime.ProbeAsync();

        // Cursor keeps the last model chosen for its next sessions, and takes half a minute to go through them.
        Assert.AreEqual(0, agent.RequestsOf("session/set_config_option").Count());
        var grok = probe.Models[1];
        Assert.AreEqual("grok-4.7", grok.DisplayName);
        Assert.AreEqual("256K context · high reasoning · fast", grok.Description);
        CollectionAssert.AreEqual(new[] { AgentReasoningEffort.High }, grok.SupportedReasoningEfforts!.ToArray());
        Assert.AreEqual(256_000L, grok.Capabilities!["contextWindow"]);
        Assert.AreEqual(0, probe.Models[0].SupportedReasoningEfforts!.Count);
        CollectionAssert.Contains(catalog.Lookups, ("grok-4.7[context=256k,reasoning_effort=high,fast=true]", "grok-4.7", (string?)null));
    }

    [TestMethod]
    public async Task Probe_OfCursorThatListsItsModelsByName_ReadsTheEffortsAndTheOptionsOfEach()
    {
        // As Cursor answers a client that says parameterizedModelPicker: the models by their names, and their parameters
        // as options. The thinking of a model comes before its effort, in the same category.
        var agent = new AcpFakeAgent { Models = ["default", "claude-opus-5-5", "composer-2.5", "claude-haiku-4-5"], CurrentModel = "default" };
        agent.EffortsByModel["default"] = [];
        agent.EffortsByModel["composer-2.5"] = [];
        agent.EffortsByModel["claude-haiku-4-5"] = [];
        agent.ParametersByModel["claude-opus-5-5"] = new() { ["thinking"] = ["true", "false"], ["context"] = ["300k", "1m"], ["fast"] = ["false", "true"] };
        agent.ParametersByModel["composer-2.5"] = new() { ["fast"] = ["true", "false"] };
        agent.ParametersByModel["claude-haiku-4-5"] = new() { ["thinking"] = ["true", "false"] };
        var settings = new AcpAgentSettings("dotnet", ["acp"], new Dictionary<string, string>(), "cursor", "2026.10.22");
        await using var fixture = new Fixture(agent, settings, AcpAgentQuirks.For(settings));

        var probe = await fixture.Runtime.ProbeAsync();

        Assert.AreEqual(true, agent.ClientCapabilities?["_meta"]?["parameterizedModelPicker"]?.GetValue<bool>(), "CodeAlta asks Cursor for its models by name");
        var opus = probe.Models.Single(static model => model.Id == "claude-opus-5-5");
        CollectionAssert.AreEqual(new[] { AgentReasoningEffort.Low, AgentReasoningEffort.Medium, AgentReasoningEffort.High }, opus.SupportedReasoningEfforts!.ToArray(),
            "the effort is the option of the category whose values are efforts, not the thinking before it");
        CollectionAssert.AreEqual(new[] { "thinking", "context", "fast" }, opus.Options!.Select(static option => option.Id).ToArray());
        var context = opus.Options!.Single(static option => option.Id == "context");
        Assert.AreEqual("300k", context.Value);
        Assert.IsFalse(context.IsToggle);
        Assert.IsTrue(opus.Options!.Single(static option => option.Id == "fast").IsToggle);
        Assert.AreEqual(300_000L, opus.Capabilities!["contextWindow"], "the context the model runs with");
        CollectionAssert.AreEqual(new[] { "fast" }, probe.Models.Single(static model => model.Id == "composer-2.5").Options!.Select(static option => option.Id).ToArray());
        Assert.IsNull(probe.Models.Single(static model => model.Id == "default").Options, "a model without options offers none");
        // A model whose only control of its reasoning is a toggle has no effort, and the toggle as an option.
        var haiku = probe.Models.Single(static model => model.Id == "claude-haiku-4-5");
        Assert.AreEqual(0, haiku.SupportedReasoningEfforts!.Count);
        CollectionAssert.AreEqual(new[] { "thinking" }, haiku.Options!.Select(static option => option.Id).ToArray());
    }

    [TestMethod]
    public void ModelOptions_AreTheModelConfigOptions_AndAThoughtLevelThatIsNotTheEffort()
    {
        var options = JsonDocument.Parse("""
            [
              { "id": "mode", "category": "mode", "type": "select", "currentValue": "agent", "options": [{ "value": "agent" }, { "value": "plan" }] },
              { "id": "model", "category": "model", "type": "select", "currentValue": "m", "options": [{ "value": "m" }, { "value": "n" }] },
              { "id": "thinking", "category": "thought_level", "type": "select", "currentValue": "true", "options": [{ "value": "true" }, { "value": "false" }] },
              { "id": "effort", "category": "thought_level", "type": "select", "currentValue": "high", "options": [{ "value": "low" }, { "value": "high" }] },
              { "id": "context", "name": "Context", "category": "model_config", "type": "select", "currentValue": "1m", "options": [{ "value": "300k" }, { "value": "1m" }] },
              { "id": "approvals", "category": "_vendor", "type": "select", "currentValue": "ask", "options": [{ "value": "ask" }, { "value": "auto" }] },
              { "id": "speed", "category": "model_config", "type": "boolean", "currentValue": true }
            ]
            """).RootElement;

        Assert.AreEqual("effort", AcpJson.GetString(AcpConfigOptions.EffortOption(options)!.Value, "id"), "the thought level whose values are efforts");
        var read = AcpConfigOptions.ReadModelOptions(options);
        // A setting of another category belongs to the session, and a boolean option is not offered by this client.
        CollectionAssert.AreEqual(new[] { "thinking", "context" }, read.Select(static option => option.Id).ToArray());
        Assert.AreEqual("Context", read[1].Name);
        Assert.AreEqual("thinking", read[0].Name, "an option without a name is named by its id");
    }

    [TestMethod]
    public async Task ModelOption_ChosenInCodeAlta_IsSentBeforeEachTurnOfTheModel_AndKept()
    {
        var agent = new AcpFakeAgent();
        agent.ParametersByModel["smart"] = new() { ["context"] = ["300k", "1m"], ["fast"] = ["false", "true"] };
        await using var fixture = new Fixture(agent) { ModelId = "smart" };
        await fixture.Runtime.ProbeAsync();

        var model = await fixture.Runtime.SetModelOptionAsync("smart", "context", "1m");

        Assert.AreEqual("1m", model!.Options!.Single(static option => option.Id == "context").Value);
        Assert.AreEqual(1_000_000L, model.Capabilities!["contextWindow"]);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => fixture.Runtime.SetModelOptionAsync("smart", "context", "2m"));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => fixture.Runtime.SetModelOptionAsync("smart", "speed", "1m"));
        Assert.IsNull(await fixture.Runtime.SetModelOptionAsync("unknown", "context", "1m"));

        while (agent.ConfigChanges.TryDequeue(out _))
        {
        }

        await fixture.RunAsync([User("hello")]);

        CollectionAssert.AreEqual(new[] { "model=smart", "context=1m" }, agent.ConfigChanges.ToArray(),
            "the model, then the option chosen for it; the one left as the agent has it is not sent");

        // Another runtime of the provider, after a restart, has the choice.
        await using var endpoints = new LoopbackHarnessToolEndpoints();
        var again = new AcpModelProviderRuntime(fixture.Runtime.Descriptor, new AcpRuntimeOptions
        {
            ProviderKey = fixture.Runtime.Descriptor.ProviderId.Value,
            AgentName = "Fake Agent",
            Agent = new AcpAgentSettings("dotnet", [], new Dictionary<string, string>(), null, null),
            ToolEndpoints = endpoints,
            ProbeDirectory = Path.Combine(fixture.WorkingDirectory, ".probe"),
            ConnectionFactory = agent.CreateFactory(),
        }, new AcpModelCacheStore(Path.Combine(fixture.WorkingDirectory, ".models")));
        await using (again)
        {
            var models = await again.ListModelsAsync(again.RuntimeDescriptor);
            Assert.AreEqual("1m", models.Single(static listed => listed.Id == "smart").Options!.Single(static option => option.Id == "context").Value);

            // An option set in another runtime of the provider (another CodeAlta of the profile) reaches the session
            // that runs: the next turn sends it.
            await again.SetModelOptionAsync("smart", "context", "300k");
        }

        while (agent.ConfigChanges.TryDequeue(out _))
        {
        }

        await fixture.RunAsync([User("hello"), User("again")]);
        CollectionAssert.Contains(agent.ConfigChanges.ToArray(), "context=300k", string.Join(", ", agent.ConfigChanges));
    }

    [TestMethod]
    public async Task ModelOption_OfAModelTheAgentDoesNotList_IsNotGivenToTheModelTheSessionRuns()
    {
        var agent = new AcpFakeAgent();
        agent.ParametersByModel["fast"] = new() { ["context"] = ["300k", "1m"] };
        await using var fixture = new Fixture(agent) { ModelId = "gone" };
        new AcpModelCacheStore(Path.Combine(fixture.WorkingDirectory, ".models"))
            .Choose(fixture.Runtime.Descriptor.ProviderId.Value, "gone", "context", "1m", static () => new AcpModelCache());

        await fixture.RunAsync([User("hello")]);

        CollectionAssert.DoesNotContain(agent.ConfigChanges.ToArray(), "context=1m", string.Join(", ", agent.ConfigChanges));
    }

    [TestMethod]
    public async Task SessionOfCursorNamedByAVariant_RunsTheModelOfItsName()
    {
        // A session of an earlier version of CodeAlta names the variant Cursor listed then.
        var agent = new AcpFakeAgent { Models = ["default", "claude-opus-5-5"], Efforts = [] };
        var settings = new AcpAgentSettings("dotnet", ["acp"], new Dictionary<string, string>(), "cursor", "2026.10.22");
        await using var fixture = new Fixture(agent, settings, AcpAgentQuirks.For(settings)) { ModelId = "claude-opus-5-5[context=300k,effort=medium,fast=false]" };

        await fixture.RunAsync([User("hello")]);

        CollectionAssert.Contains(agent.ConfigChanges.ToArray(), "model=claude-opus-5-5");
    }

    [TestMethod]
    public void AgentQuirks_AreChosenByTheRegistryAgent_ElseByTheCommand()
    {
        static AcpAgentSettings Agent(string command, string? id, params string[] arguments) => new(command, arguments, new Dictionary<string, string>(), id, null);

        Assert.IsInstanceOfType<CursorAcpQuirks>(AcpAgentQuirks.For(Agent("C:\\agents\\cursor\\cursor-agent.cmd", null, "acp")));
        Assert.IsInstanceOfType<CursorAcpQuirks>(AcpAgentQuirks.For(Agent("agent", "cursor")));
        Assert.IsInstanceOfType<CopilotAcpQuirks>(AcpAgentQuirks.For(Agent("npx", null, "--yes", "@github/copilot@1.0.103", "--acp")));
        Assert.IsInstanceOfType<CopilotAcpQuirks>(AcpAgentQuirks.For(Agent("npx", "github-copilot-cli")));
        Assert.AreSame(AcpAgentQuirks.Protocol, AcpAgentQuirks.For(Agent("npx", "gemini", "@google/gemini-cli", "--experimental-acp")));
    }

    [TestMethod]
    public void CopilotModels_AreDescribedByGitHubCopilot_WithTheirPremiumRequests()
    {
        var catalog = new RecordingCatalog();
        var listed = new AcpListedModel("claude-opus-5.5", "Claude Opus 5.5", "Claude Opus 5.5",
            JsonDocument.Parse("""{"copilotUsage":"15x","copilotEnablement":"enabled"}""").RootElement.Clone());

        var model = AcpModels.Present(listed, CopilotAcpQuirks.Instance, catalog);

        Assert.AreEqual("Claude Opus 5.5", model.DisplayName);
        Assert.AreEqual("15x premium requests", model.Description, "A description that repeats the name is left out.");
        Assert.IsNull(model.SupportedReasoningEfforts, "Efforts not read yet are not known.");
        CollectionAssert.Contains(catalog.Lookups, ("claude-opus-5.5", (string?)null, (string?)"github-copilot"));
    }

    [TestMethod]
    public void ModelCache_KeepsTheEffortsLearned()
    {
        var listed = new AcpListedModel("fast", "Fast", null, JsonDocument.Parse("""{"copilotUsage":"1x"}""").RootElement.Clone())
        {
            Efforts = [AgentReasoningEffort.Low, AgentReasoningEffort.XHigh],
            DefaultEffort = AgentReasoningEffort.Low,
            Options = [new AgentModelOption("context", "Context", [new("300k", "300K"), new("1m", "1M")], "300k") { Description = "The size of the context" }],
        };
        var choices = new Dictionary<string, IReadOnlyDictionary<string, string>> { ["fast"] = new Dictionary<string, string> { ["context"] = "1m" } };
        var json = JsonSerializer.Serialize(AcpModelCache.From([listed, new AcpListedModel("unknown", null, null, default)], "fast", choices), AcpPluginJsonContext.Default.AcpModelCache);

        var cache = JsonSerializer.Deserialize(json, AcpPluginJsonContext.Default.AcpModelCache)!;
        var read = cache.ToModels();

        CollectionAssert.AreEqual(new[] { AgentReasoningEffort.Low, AgentReasoningEffort.XHigh }, read[0].Efforts!.ToArray());
        Assert.AreEqual(AgentReasoningEffort.Low, read[0].DefaultEffort);
        Assert.AreEqual("1x", AcpJson.GetString(read[0].Meta, "copilotUsage"));
        var option = read[0].Options!.Single();
        Assert.AreEqual(("context", "Context", "300k", "The size of the context"), (option.Id, option.Name, option.Value, option.Description));
        CollectionAssert.AreEqual(new[] { "300k", "1m" }, option.Values.Select(static value => value.Value).ToArray());
        Assert.AreEqual("1m", cache.Choices!["fast"]["context"]);
        Assert.IsNull(read[1].Efforts);
        Assert.IsNull(read[1].Options);
    }

    [TestMethod]
    public async Task AgentWithoutHttpMcp_RunsWithoutTheToolsOfCodeAlta_AndIsToldSo()
    {
        var agent = new AcpFakeAgent { McpHttp = false };
        await using var fixture = new Fixture(agent);

        await fixture.RunAsync([User("hello")]);

        Assert.AreEqual(0, agent.RequestsOf("session/new").Single()!["mcpServers"]!.AsArray().Count);
        StringAssert.Contains(PromptText(agent.RequestsOf("session/prompt").Single()), "are not available in this session");
    }

    [TestMethod]
    public void SignInMethods_GiveTheCommandOfATerminalSignIn()
    {
        var agent = new AcpAgentSettings("npx", ["--yes", "@scope/agent@1.0.0", "--acp"], new Dictionary<string, string>(), "agent", "1.0.0");
        var methods = AcpAccount.CreateMethods(agent,
        [
            JsonDocument.Parse("""{"id":"copilot-login","name":"Log in","_meta":{"terminal-auth":{"command":"C:\\copilot.exe","args":["login"],"label":"Copilot Login"}}}""").RootElement,
            JsonDocument.Parse("""{"id":"term","name":"Terminal","type":"terminal","args":["--login"],"env":{"A":"1"}}""").RootElement,
            JsonDocument.Parse("""{"id":"browser","name":"Browser"}""").RootElement,
        ]);

        CollectionAssert.AreEqual(new[] { "C:\\copilot.exe", "login" }, methods[0].TerminalCommand!.ToArray());
        Assert.AreEqual("Copilot Login", methods[0].Label);
        CollectionAssert.AreEqual(new[] { "npx", "--yes", "@scope/agent@1.0.0", "--login" }, methods[1].TerminalCommand!.ToArray());
        Assert.AreEqual("1", methods[1].TerminalEnvironment!["A"]);
        Assert.IsNull(methods[2].TerminalCommand);
    }

    private static AgentConversationMessage User(string text) => new(AgentConversationRole.User, [new AgentMessagePart.Text(text)]);

    private static string Text(AgentConversationMessage message) => string.Concat(message.Parts.OfType<AgentMessagePart.Text>().Select(static part => part.Value));

    private static string ToolResult(AgentConversationMessage message)
        => string.Concat(message.Parts.OfType<AgentMessagePart.ToolResult>().Single().Result.Items.OfType<AgentToolResultItem.Text>().Select(static item => item.Value));

    private static string PromptText(JsonNode? prompt)
        => string.Join("\n", prompt!["prompt"]!.AsArray().Select(static block => block?["text"]?.GetValue<string>()).Where(static text => text is not null)).ReplaceLineEndings("\n");

    private static JsonElement AcpProviderStateOf(AgentSessionState state) => state.ProviderState!.Value;

    private static AgentToolDefinition Tool(string name, Func<AgentToolInvocation, AgentToolResult> handler)
        => new(new AgentToolSpec(name, $"The tool {name}.", JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone()),
            (invocation, _) => Task.FromResult(handler(invocation)));

    /// <summary>A catalog of models that knows none, and records what it was asked.</summary>
    private sealed class RecordingCatalog : IPluginModelCatalog
    {
        public List<(string ModelId, string? CatalogModelId, string? CatalogProviderId)> Lookups { get; } = [];

        public AgentModelInfo Describe(AgentModelInfo model, string? catalogModelId, string? catalogProviderId)
        {
            lock (Lookups)
            {
                Lookups.Add((model.Id, catalogModelId, catalogProviderId));
            }

            return model;
        }
    }

    /// <summary>An ACP provider runtime on the fake agent, and a driver that runs turns as the agent session does.</summary>
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly LoopbackHarnessToolEndpoints _endpoints = new();

        public Fixture(AcpFakeAgent agent, AcpAgentSettings? settings = null, AcpAgentQuirks? quirks = null, IPluginModelCatalog? catalog = null)
        {
            WorkingDirectory = Path.Combine(Path.GetTempPath(), nameof(AcpTurnExecutorTests), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(WorkingDirectory);
            var options = new AcpRuntimeOptions
            {
                ProviderKey = ProviderId.Value,
                AgentName = "Fake Agent",
                Agent = settings ?? new AcpAgentSettings("dotnet", [], new Dictionary<string, string>(), null, null),
                Quirks = quirks ?? AcpAgentQuirks.Protocol,
                ModelCatalog = catalog,
                ToolEndpoints = _endpoints,
                ProbeDirectory = Path.Combine(WorkingDirectory, ".probe"),
                ConnectionFactory = agent.CreateFactory(),
                CancelTimeout = TimeSpan.FromSeconds(5),
                RequestTimeout = TimeSpan.FromSeconds(20),
            };
            Runtime = new AcpModelProviderRuntime(
                new ModelProviderDescriptor(ProviderId, "Fake Agent", "acp"),
                options,
                new AcpModelCacheStore(Path.Combine(WorkingDirectory, ".models")));
            Executor = (AcpTurnExecutor)Runtime.CreateTurnExecutor();
        }

        public AcpModelProviderRuntime Runtime { get; }

        public AcpTurnExecutor Executor { get; }

        public string WorkingDirectory { get; }

        public AgentSessionState State { get; set; } = new() { SessionId = "session-1", UpdatedAt = DateTimeOffset.UtcNow };

        public AgentEarlierConversation? Earlier { get; init; }

        public IReadOnlyList<AgentToolDefinition> Tools { get; init; } = [];

        public string? ModelId { get; init; }

        public AgentReasoningEffort? Effort { get; init; }

        public string? PermissionMode { get; init; }

        public AgentPermissionRequestHandler OnPermission { get; init; } = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce));

        public Action<List<AgentConversationMessage>>? BeforeNextTurn { get; init; }

        public IAgentProcessHost? ProcessHost { get; init; }

        public async Task<List<AgentConversationMessage>> RunAsync(IReadOnlyList<AgentConversationMessage> start, CancellationToken cancellationToken = default)
        {
            var conversation = new List<AgentConversationMessage>(start);
            Executor.AttachRun(new AgentProviderRunContext("session-1", new AgentRunId("run-1"), OnPermission, null));
            var steered = false;
            while (true)
            {
                var response = await Executor.ExecuteTurnAsync(CreateRequest(conversation), static (_, _) => ValueTask.CompletedTask, cancellationToken);
                conversation.Add(response.AssistantMessage);
                if (response.ProviderState is { } providerState)
                {
                    State = State with { ProviderState = providerState };
                }

                var calls = response.AssistantMessage.Parts.OfType<AgentMessagePart.ToolCall>().ToArray();
                foreach (var call in calls)
                {
                    var registered = Tools.FirstOrDefault(tool => tool.Spec.Name == call.Name);
                    var definition = Executor.ResolveTool("session-1", call, registered) ?? registered
                        ?? throw new InvalidOperationException($"No tool {call.Name}.");
                    var result = await definition.Handler(new AgentToolInvocation(ProviderId, "session-1", call.CallId, call.Name, call.Arguments), cancellationToken);
                    conversation.Add(new AgentConversationMessage(AgentConversationRole.Tool, [new AgentMessagePart.ToolResult(call.CallId, result)]));
                }

                if (calls.Length > 0 && !steered && BeforeNextTurn is { } before)
                {
                    steered = true;
                    before(conversation);
                }

                if (calls.Length == 0 && !response.RequiresProviderFollowUp)
                {
                    return conversation;
                }
            }
        }

        private AgentTurnRequest CreateRequest(IReadOnlyList<AgentConversationMessage> conversation)
            => new()
            {
                Provider = Runtime.RuntimeDescriptor,
                ProviderId = ProviderId,
                SessionId = "session-1",
                RunId = new AgentRunId("run-1"),
                WorkingDirectory = WorkingDirectory,
                DeveloperInstructions = "Be brief.",
                ModelId = ModelId,
                ReasoningEffort = Effort,
                PermissionMode = PermissionMode,
                Conversation = [.. conversation],
                EarlierConversation = Earlier,
                Tools = Tools,
                State = State,
                ProcessHost = ProcessHost,
            };

        public async ValueTask DisposeAsync()
        {
            await Runtime.DisposeAsync();
            await _endpoints.DisposeAsync();
            try
            {
                Directory.Delete(WorkingDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
