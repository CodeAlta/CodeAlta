using System.Text.Json;
using System.Text.Json.Nodes;
using CodeAlta.Agent;
using CodeAlta.Agent.Harness;
using CodeAlta.Agent.Runtime;
using CodeAlta.Agent.Runtime.Compaction;
using CodeAlta.Plugin.CodexCli;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Tests;

[TestClass]
public sealed class CodexCliTurnExecutorTests
{
    private static readonly ModelProviderId ProviderId = new("codex-test");

    [TestMethod]
    public async Task FirstTurn_StartsAThreadWithTheToolsOfCodeAlta_AndAddsTheInstructions()
    {
        var server = new CodexAppServerFake();
        await using var fixture = new Fixture(server);

        var conversation = await fixture.RunAsync([User("hello")]);

        var start = server.RequestsOf("thread/start").Single()!.AsObject();
        StringAssert.Contains(start["developerInstructions"]!.GetValue<string>(), "The instructions of CodeAlta for this session:\n\nBe brief.");
        StringAssert.Contains(start["developerInstructions"]!.GetValue<string>(), "the MCP server named `codealta`");
        // Codex's own attribution, which a ChatGPT workspace turns on, stays beside the trailer of CodeAlta.
        StringAssert.Contains(start["developerInstructions"]!.GetValue<string>(), "`Co-authored-by: Codex <noreply@openai.com>`");
        StringAssert.Contains(start["developerInstructions"]!.GetValue<string>(), "neither in place of the other");
        Assert.IsFalse(start.ContainsKey("baseInstructions"), "CodeAlta adds to the prompt of Codex and never replaces it.");
        Assert.AreEqual("danger-full-access", start["sandbox"]!.GetValue<string>());
        Assert.AreEqual("untrusted", start["approvalPolicy"]!.GetValue<string>());
        var mcp = start["config"]!["mcp_servers"]!["codealta"]!;
        StringAssert.StartsWith(mcp["url"]!.GetValue<string>(), "http://127.0.0.1:");
        StringAssert.StartsWith(mcp["http_headers"]!["Authorization"]!.GetValue<string>(), "Bearer ");
        Assert.AreEqual("approve", mcp["default_tools_approval_mode"]!.GetValue<string>());
        Assert.IsTrue(mcp["required"]!.GetValue<bool>());
        Assert.AreEqual("live", start["config"]!["web_search"]!.GetValue<string>());
        Assert.AreEqual("untrusted", start["config"]!["projects"]![fixture.WorkingDirectory]!["trust_level"]!.GetValue<string>());
        var turn = server.RequestsOf("turn/start").Single()!;
        Assert.AreEqual("hello", turn["input"]![0]!["text"]!.GetValue<string>());
        Assert.AreEqual("ok", Text(conversation[^1]));
        Assert.AreEqual(CodexCliSession.InstructionsChannel, fixture.Executor.GetInstructionsChannel("session-1"));

        var state = CodexCliProviderState.Read(fixture.State)!;
        Assert.AreEqual("thread-1", state.ThreadId);
        Assert.AreEqual(1, state.Users);
        Assert.AreEqual(1, state.Assistants);
        Assert.AreEqual("turn-1", state.Turns.Single().Id);
        var config = File.ReadAllText(Path.Combine(fixture.Home, "config.toml"));
        StringAssert.Contains(config, "sandbox_mode = \"danger-full-access\"");
        StringAssert.Contains(config, "multi_agent = false");
        Assert.IsFalse(config.Contains("image_generation", StringComparison.Ordinal), "Codex generates images, as it does by default.");
        Assert.AreEqual(fixture.Home, server.Connections.Single().Request.Home);
    }

    [TestMethod]
    public async Task AGeneratedImage_IsTheImageOfTheResultOfItsCall()
    {
        var saved = string.Empty;
        var missing = string.Empty;
        var server = new CodexAppServerFake
        {
            OnTurn = async turn =>
            {
                await turn.GenerateImageAsync("A red circle on white.", saved);
                await turn.GenerateImageAsync("A blue square.", missing);
                await turn.GenerateImageAsync("A refused image.", null);
                await turn.SendTextAsync("Here it is.");
            },
        };
        await using var fixture = new Fixture(server);
        // Where Codex saves an image: generated_images/<thread>/<call>.png in its home.
        saved = Path.Combine(fixture.Home, "generated_images", "thread-1", "ig_1.png");
        missing = Path.Combine(fixture.Home, "generated_images", "thread-1", "ig_2.png");
        Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
        await File.WriteAllBytesAsync(saved, png);

        var conversation = await fixture.RunAsync([User("draw")]);

        var calls = conversation.SelectMany(static message => message.Parts.OfType<AgentMessagePart.ToolCall>()).ToArray();
        CollectionAssert.AreEqual(new[] { "image_generation", "image_generation", "image_generation" }, calls.Select(static call => call.Name).ToArray());
        // Codex tells the call before it has the revised prompt: the result gives it.
        Assert.IsFalse(calls[0].Arguments.TryGetProperty("prompt", out _));
        var results = conversation.SelectMany(static message => message.Parts.OfType<AgentMessagePart.ToolResult>()).Select(static part => part.Result).ToArray();

        // The image is in the result: the session saves its own copy and shows it.
        Assert.IsTrue(results[0].Success);
        Assert.AreEqual($"Generated {saved}\nPrompt: A red circle on white.", ((AgentToolResultItem.Text)results[0].Items[0]).Value);
        var image = (AgentToolResultItem.Image)results[0].Items[1];
        CollectionAssert.AreEqual(png, Convert.FromBase64String(image.Base64Data));
        Assert.AreEqual("image/png", image.MediaType);
        Assert.AreEqual("ig_1.png", image.DisplayName);

        // A file that cannot be read leaves the path alone.
        Assert.IsTrue(results[1].Success);
        Assert.AreEqual($"Generated {missing}\nPrompt: A blue square.", ((AgentToolResultItem.Text)results[1].Items.Single()).Value);

        // A generation the image API refused fails, though Codex gives a failure detail only for a reached limit.
        Assert.IsFalse(results[2].Success);
        Assert.AreEqual("The image could not be generated.", results[2].Error);
        Assert.AreEqual("Here it is.", Text(conversation[^1]));
    }

    [TestMethod]
    public async Task Turn_StreamsTextAndReasoning_RunsCommands_AndCallsTheToolsOfCodeAlta()
    {
        JsonNode? mcpResult = null;
        var server = new CodexAppServerFake
        {
            OnTurn = async turn =>
            {
                await turn.SendReasoningAsync("Thinking about it.");
                await turn.SendTextAsync("Looking.");
                await turn.RunCommandAsync("ls -la", "a.txt\n");
                mcpResult = await turn.CallCodeAltaToolAsync("echo", new JsonObject { ["text"] = "hi" });
                await turn.UpdatePlanAsync(("Look", "completed"), ("Answer", "inProgress"));
                await turn.SendTextAsync("Done.");
            },
        };
        var updates = new List<AgentTurnSessionUpdate>();
        await using var fixture = new Fixture(server)
        {
            Tools = [Tool("echo", invocation => new AgentToolResult(true, [new AgentToolResultItem.Text("echo:" + invocation.Arguments.GetProperty("text").GetString())]))],
            OnSessionUpdate = updates.Add,
        };

        var conversation = await fixture.RunAsync([User("go")]);

        var calls = conversation.SelectMany(static message => message.Parts.OfType<AgentMessagePart.ToolCall>()).ToArray();
        CollectionAssert.AreEqual(new[] { "shell", "echo" }, calls.Select(static call => call.Name).ToArray());
        Assert.AreEqual("ls -la", calls[0].Arguments.GetProperty("command").GetString());
        Assert.AreEqual("a.txt\n", ToolResult(conversation[2]));
        Assert.AreEqual("echo:hi", ToolResult(conversation[4]));
        Assert.AreEqual("echo:hi", mcpResult!["content"]![0]!["text"]!.GetValue<string>());
        Assert.AreEqual("Thinking about it.", conversation[1].Parts.OfType<AgentMessagePart.Reasoning>().Single().Value);
        StringAssert.Contains(Text(conversation[1]), "Looking.");
        Assert.AreEqual("Done.", Text(conversation[^1]));
        Assert.IsTrue(updates.Exists(static update => update.Kind == AgentSessionUpdateKind.PlanUpdated && update.Message!.Contains("- [x] Look", StringComparison.Ordinal)));
        var usage = updates.Last(static update => update.Kind == AgentSessionUpdateKind.UsageUpdated).Usage!;
        Assert.AreEqual(150, usage.Window!.CurrentTokens);
        Assert.AreEqual(400000, usage.Window.TokenLimit);
        Assert.AreEqual(3, CodexCliProviderState.Read(fixture.State)!.Assistants, "each message up to a tool call is one");
    }

    [TestMethod]
    public async Task AWaitOfCodex_IsACallOfTheSession_WithItsDuration()
    {
        var server = new CodexAppServerFake
        {
            OnTurn = async turn =>
            {
                await turn.SleepAsync(30_000);
                await turn.SleepAsync(1_800_000);
                await turn.SendTextAsync("Done.");
            },
        };
        await using var fixture = new Fixture(server);

        var conversation = await fixture.RunAsync([User("wait")]);

        // The session shows the wait while it lasts: a message sent meanwhile steers the turn and ends it.
        var calls = conversation.SelectMany(static message => message.Parts.OfType<AgentMessagePart.ToolCall>()).ToArray();
        CollectionAssert.AreEqual(new[] { "sleep", "sleep" }, calls.Select(static call => call.Name).ToArray());
        Assert.AreEqual(30_000, calls[0].Arguments.GetProperty("duration_ms").GetInt64());
        var results = conversation.SelectMany(static message => message.Parts.OfType<AgentMessagePart.ToolResult>()).Select(static part => part.Result).ToArray();
        Assert.IsTrue(results[0].Success);
        Assert.AreEqual("Waited up to 30 s.", ((AgentToolResultItem.Text)results[0].Items.Single()).Value);
        Assert.AreEqual("Waited up to 30 min.", ((AgentToolResultItem.Text)results[1].Items.Single()).Value);
        Assert.AreEqual("Done.", Text(conversation[^1]));
    }

    [TestMethod]
    public async Task ANewProcess_ResumesTheThread_WithItsTools()
    {
        var server = new CodexAppServerFake();
        AgentSessionState state;
        List<AgentConversationMessage> conversation;
        await using (var first = new Fixture(server))
        {
            conversation = await first.RunAsync([User("one")]);
            state = first.State;
        }

        await using var second = new Fixture(server) { State = state };
        conversation.Add(User("two"));
        await second.RunAsync(conversation);

        var resume = server.RequestsOf("thread/resume").Single()!;
        Assert.AreEqual("thread-1", resume["threadId"]!.GetValue<string>());
        Assert.IsTrue(resume["excludeTurns"]!.GetValue<bool>());
        StringAssert.Contains(resume["developerInstructions"]!.GetValue<string>(), "Be brief.", "Codex does not keep the instructions of a thread: they are given again.");
        Assert.IsNotNull(resume["config"]!["mcp_servers"]!["codealta"], "nor its MCP server");
        Assert.AreEqual("two", server.RequestsOf("turn/start").Last()!["input"]![0]!["text"]!.GetValue<string>());
        Assert.AreEqual(1, server.RequestsOf("thread/start").Count());
    }

    [TestMethod]
    public async Task AThreadCodexNoLongerHas_IsStartedAgain_AndToldTheConversation()
    {
        var server = new CodexAppServerFake();
        var state = new AgentSessionState
        {
            SessionId = "session-1",
            UpdatedAt = DateTimeOffset.UtcNow,
            ProviderState = new CodexCliProviderState("thread-gone", 1, 1, null, []).ToJson(),
        };
        await using var fixture = new Fixture(server) { State = state };

        await fixture.RunAsync([User("one"), Assistant("first answer"), User("two")]);

        Assert.AreEqual(1, server.RequestsOf("thread/resume").Count());
        Assert.AreEqual(1, server.RequestsOf("thread/start").Count());
        var input = server.RequestsOf("turn/start").Single()!["input"]![0]!["text"]!.GetValue<string>();
        StringAssert.Contains(input, HarnessHistory.PreviousConversationOpening);
        StringAssert.Contains(input, "first answer");
        StringAssert.EndsWith(input, "two");
    }

    [TestMethod]
    public async Task AConversationAnotherProviderWrote_StartsAThreadToldWhatWasSaid()
    {
        var server = new CodexAppServerFake();
        await using var fixture = new Fixture(server);

        await fixture.RunAsync([User("one"), Assistant("from claude"), User("two")]);

        var input = server.RequestsOf("turn/start").Single()!["input"]![0]!["text"]!.GetValue<string>();
        StringAssert.StartsWith(input, HarnessHistory.PreviousConversationOpening);
        StringAssert.Contains(input, "from claude");
    }

    [TestMethod]
    public async Task ChangedInstructions_AreSentAsAnUpdateWithTheNextPrompt()
    {
        var server = new CodexAppServerFake();
        await using var fixture = new Fixture(server);
        var conversation = await fixture.RunAsync([User("one")]);

        fixture.DeveloperInstructions = "Be brief.\n\nAnswer in French.";
        conversation.Add(User("two"));
        await fixture.RunAsync(conversation);

        var input = server.RequestsOf("turn/start").Last()!["input"]![0]!["text"]!.GetValue<string>();
        StringAssert.StartsWith(input, "<codealta_instructions_update>");
        StringAssert.Contains(input, "Answer in French.");
        StringAssert.EndsWith(input, "two");
    }

    [TestMethod]
    public async Task DefaultMode_AsksTheUserForACommand_AndDeclinesWhatTheUserRefuses()
    {
        string? accepted = null, refused = null;
        var server = new CodexAppServerFake
        {
            OnTurn = async turn =>
            {
                accepted = await turn.RunCommandAsync("rm a.txt", "removed", askApproval: true);
                refused = await turn.RunCommandAsync("rm b.txt", "removed", askApproval: true);
                await turn.SendTextAsync("done");
            },
        };
        var asked = new List<AgentPermissionRequest>();
        await using var fixture = new Fixture(server)
        {
            OnPermission = (request, _) =>
            {
                asked.Add(request);
                return Task.FromResult(new AgentPermissionDecision(asked.Count == 1 ? AgentPermissionDecisionKind.AllowOnce : AgentPermissionDecisionKind.Deny));
            },
        };

        var conversation = await fixture.RunAsync([User("clean")]);

        Assert.AreEqual(2, asked.Count);
        Assert.AreEqual("rm a.txt", ((AgentCommandPermissionRequest)asked[0]).Command);
        Assert.AreEqual("accept", accepted);
        Assert.AreEqual("decline", refused);
        StringAssert.Contains(ToolResult(conversation[^2]), "declined");
    }

    [TestMethod]
    public async Task AcceptEdits_ChangesFilesWithoutAsking_ButAsksForCommands()
    {
        string? edit = null, command = null;
        var server = new CodexAppServerFake
        {
            OnTurn = async turn =>
            {
                edit = await turn.ChangeFileAsync("src/a.cs");
                command = await turn.RunCommandAsync("make", "built", askApproval: true);
                await turn.SendTextAsync("done");
            },
        };
        var asked = new List<AgentPermissionRequest>();
        await using var fixture = new Fixture(server)
        {
            PermissionMode = "acceptEdits",
            OnPermission = (request, _) =>
            {
                asked.Add(request);
                return Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowForSession));
            },
        };

        var conversation = await fixture.RunAsync([User("edit")]);

        Assert.AreEqual("accept", edit);
        Assert.AreEqual("acceptForSession", command);
        Assert.IsInstanceOfType<AgentCommandPermissionRequest>(asked.Single());
        var editCall = conversation.SelectMany(static message => message.Parts.OfType<AgentMessagePart.ToolCall>()).First();
        Assert.AreEqual("Edit", editCall.Name);
        Assert.AreEqual(Path.GetFullPath(Path.Combine(fixture.WorkingDirectory, "src/a.cs")), editCall.Arguments.GetProperty("file_path").GetString());
    }

    [TestMethod]
    public async Task BypassPermissions_LetsCodexAskNothing_AndAcceptsWhatItStillAsks()
    {
        string? decision = null;
        var server = new CodexAppServerFake
        {
            OnTurn = async turn =>
            {
                decision = await turn.RunCommandAsync("make", "built", askApproval: true);
                await turn.SendTextAsync("done");
            },
        };
        await using var fixture = new Fixture(server)
        {
            PermissionMode = "bypassPermissions",
            OnPermission = static (_, _) => throw new AssertFailedException("bypassPermissions asks nothing."),
        };

        await fixture.RunAsync([User("build")]);

        Assert.AreEqual("never", server.RequestsOf("turn/start").Single()!["approvalPolicy"]!.GetValue<string>());
        Assert.AreEqual("accept", decision);
    }

    [TestMethod]
    public async Task AMessageSentWhileCodexWorks_SteersItsTurn()
    {
        var steered = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new CodexAppServerFake
        {
            OnTurn = async turn =>
            {
                await turn.RunCommandAsync("sleep 1", "slept");
                for (var wait = 0; wait < 200 && turn.Steers.IsEmpty; wait++)
                {
                    await Task.Delay(20);
                }

                turn.Steers.TryDequeue(out var steer);
                steered.TrySetResult(steer!);
                await turn.SendTextAsync("steered");
            },
        };
        await using var fixture = new Fixture(server) { BeforeNextTurn = conversation => conversation.Add(User("also check b")) };

        var conversation = await fixture.RunAsync([User("check a")]);

        var steer = await steered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual("also check b", steer[0]!["text"]!.GetValue<string>());
        Assert.AreEqual(1, server.RequestsOf("turn/start").Count(), "the message went into the running turn");
        Assert.AreEqual("steered", Text(conversation[^1]));
    }

    [TestMethod]
    public async Task ATurnThatTakesNoSteer_GetsTheMessageAsItsNextTurn()
    {
        var server = new CodexAppServerFake
        {
            RefuseSteer = true,
            OnTurn = async turn =>
            {
                if (turn.Text == "first")
                {
                    await turn.RunCommandAsync("ls", "a");
                }

                await turn.SendTextAsync("answer to " + turn.Text);
            },
        };
        await using var fixture = new Fixture(server) { BeforeNextTurn = conversation => conversation.Add(User("second")) };

        var conversation = await fixture.RunAsync([User("first")]);

        CollectionAssert.AreEqual(new[] { "first", "second" }, server.RequestsOf("turn/start").Select(static turn => turn!["input"]![0]!["text"]!.GetValue<string>()).ToArray());
        Assert.AreEqual("answer to second", Text(conversation[^1]));
    }

    [TestMethod]
    public async Task AFailedTurn_IsClassifiedByItsKind()
    {
        var server = new CodexAppServerFake { OnTurn = static _ => throw new CodexFakeTurnFailure("You've hit your usage limit.", "usageLimitExceeded") };
        await using var fixture = new Fixture(server);

        var exception = await Assert.ThrowsExactlyAsync<AgentProviderFailureException>(() => fixture.RunAsync([User("go")]));

        Assert.AreEqual(AgentFailureKind.QuotaExhausted, exception.Failure.Kind);
        StringAssert.Contains(exception.Message, "usage limit");
    }

    [TestMethod]
    public async Task AHomeWithoutASignIn_FailsTheTurnAsASignIn_BeforeAnyThread()
    {
        // Codex would call the OpenAI API without a key and fail with a 401 that says nothing of the sign-in.
        var server = new CodexAppServerFake { SignedIn = false };
        await using var fixture = new Fixture(server);

        var exception = await Assert.ThrowsExactlyAsync<AgentProviderFailureException>(() => fixture.RunAsync([User("hello")]));

        Assert.AreEqual(AgentFailureKind.Auth, exception.Failure.Kind);
        StringAssert.Contains(exception.Message, "is not signed in");
        Assert.IsEmpty(server.RequestsOf("thread/start"));
        Assert.IsEmpty(server.RequestsOf("turn/start"));
    }

    [TestMethod]
    public async Task Compaction_IsTheOneOfCodex()
    {
        var server = new CodexAppServerFake();
        await using var fixture = new Fixture(server);
        var conversation = await fixture.RunAsync([User("one")]);

        var outcome = await fixture.Executor.CompactAsync(fixture.CreateRequest(conversation), CancellationToken.None);

        Assert.IsTrue(outcome.Success, outcome.Message);
        Assert.AreEqual("thread-1", server.RequestsOf("thread/compact/start").Single()!["threadId"]!.GetValue<string>());
        Assert.IsFalse(fixture.Runtime.RuntimeDescriptor.Compaction!.Enabled, "CodeAlta does not compact the sessions of Codex itself");
    }

    [TestMethod]
    public async Task ARewindToTheEndOfATurn_ForksTheThreadThere()
    {
        var server = new CodexAppServerFake();
        AgentSessionState state;
        List<AgentConversationMessage> conversation;
        await using (var first = new Fixture(server))
        {
            conversation = await first.RunAsync([User("one")]);
            var afterOne = first.State;
            conversation.Add(User("two"));
            conversation = await first.RunAsync(conversation);
            // What a rewind to the end of the first answer gives the session: the state of that point.
            state = afterOne with { ProviderBranch = new AgentProviderBranch(AgentProviderBranchKind.Rewind, AtEnd: false) };
        }

        await using var second = new Fixture(server) { State = state };
        await second.RunAsync([.. conversation.Take(2), User("other two")]);

        var fork = server.RequestsOf("thread/fork").Single()!;
        Assert.AreEqual("thread-1", fork["threadId"]!.GetValue<string>());
        Assert.AreEqual("turn-1", fork["lastTurnId"]!.GetValue<string>());
        Assert.AreEqual("other two", server.RequestsOf("turn/start").Last()!["input"]![0]!["text"]!.GetValue<string>());
        Assert.AreEqual("thread-2", CodexCliProviderState.Read(second.State)!.ThreadId);
    }

    [TestMethod]
    public async Task InASandbox_TheProcessStartsThere_AndReachesTheToolsOfCodeAlta()
    {
        var server = new CodexAppServerFake();
        await using var fixture = new Fixture(server) { ProcessHost = new TestSandbox() };

        await fixture.RunAsync([User("hello")]);

        var request = server.Connections.Single().Request;
        Assert.IsNotNull(request.Host);
        var url = new Uri(server.RequestsOf("thread/start").Single()!["config"]!["mcp_servers"]!["codealta"]!["url"]!.GetValue<string>());
        CollectionAssert.AreEqual(new[] { url.Port }, request.LocalPorts.ToArray(), "its proxy lets it reach the tools of CodeAlta");
    }

    [TestMethod]
    public async Task Probe_ReadsTheAccountAndTheModels()
    {
        var server = new CodexAppServerFake();
        await using var fixture = new Fixture(server);

        var probe = await fixture.Runtime.ProbeAsync();

        Assert.AreEqual(ModelProviderAvailability.Ready, probe.Availability, probe.StatusMessage);
        CollectionAssert.AreEqual(new[] { "gpt-test", "gpt-mini" }, probe.Models.Select(static model => model.Id).ToArray());
        CollectionAssert.AreEqual(new[] { AgentReasoningEffort.Low, AgentReasoningEffort.High }, probe.Models[0].SupportedReasoningEfforts!.ToArray(), "ultra has no effort of CodeAlta");
        Assert.AreEqual(AgentReasoningEffort.Medium, probe.Models[0].DefaultReasoningEffort);
        Assert.AreEqual("gpt-test", probe.SelectedModelId);
        StringAssert.Contains(probe.StatusMessage, "Signed in to ChatGPT (Business)");
    }

    [TestMethod]
    public async Task Probe_OfAProviderNotSignedIn_SaysSo()
    {
        var server = new CodexAppServerFake { SignedIn = false };
        await using var fixture = new Fixture(server);

        var probe = await fixture.Runtime.ProbeAsync();

        Assert.AreEqual(ModelProviderAvailability.Failed, probe.Availability);
        Assert.AreEqual(CodexCliFailures.SignedOutCategory, probe.ErrorCategory);
    }

    [TestMethod]
    public async Task Probe_OfACommandNotFound_SaysSo()
    {
        var server = new CodexAppServerFake();
        await using var fixture = new Fixture(server, command: Path.Combine(Path.GetTempPath(), "no-such-codex-" + Guid.NewGuid().ToString("N")));

        var probe = await fixture.Runtime.ProbeAsync();

        Assert.AreEqual(CodexCliFailures.NotFoundCategory, probe.ErrorCategory);
        Assert.IsEmpty(server.Connections);
    }

    [TestMethod]
    public async Task SignIn_ShowsTheAddressOfCodex_AndWaitsForTheEnd()
    {
        foreach (var device in new[] { false, true })
        {
            var server = new CodexAppServerFake { SignedIn = false };
            await using var fixture = new Fixture(server);
            PluginProviderSignInPrompt? shown = null;

            await CodexCliAccount.SignInAsync(fixture.Options, device, (prompt, _) =>
            {
                shown = prompt;
                return ValueTask.CompletedTask;
            }, logger: null, CancellationToken.None);

            Assert.IsTrue(server.SignedIn);
            Assert.AreEqual(device ? "chatgptDeviceCode" : "chatgpt", server.RequestsOf("account/login/start").Single()!["type"]!.GetValue<string>());
            Assert.AreEqual(device ? "ABCD-1234" : null, shown!.UserCode);
            StringAssert.StartsWith(shown.Uri.ToString(), "https://auth.openai.com/");
        }
    }

    [TestMethod]
    public void Settings_RefuseTheHomeOfTheUsersCodex_AndTheVariablesCodeAltaSets()
    {
        var settings = new PluginProviderSettings("codex-cli", "codex-cli", new Dictionary<string, object?>
        {
            ["codex_home"] = "~/.codex",
            ["env"] = new Dictionary<string, object?> { ["CODEX_HOME"] = "/elsewhere", ["HTTPS_PROXY"] = "http://proxy" },
        });

        var problems = CodexCliSettings.Validate(settings);

        Assert.HasCount(2, problems);
        Assert.IsTrue(problems.Any(static problem => problem.Contains("~/.codex", StringComparison.Ordinal)));
        Assert.IsTrue(problems.Any(static problem => problem.Contains("CODEX_HOME", StringComparison.Ordinal)));
        CollectionAssert.AreEqual(new[] { "HTTPS_PROXY" }, CodexCliSettings.Read(settings).Environment.Keys.ToArray());
    }

    private static AgentConversationMessage User(string text) => new(AgentConversationRole.User, [new AgentMessagePart.Text(text)]);

    private static AgentConversationMessage Assistant(string text) => new(AgentConversationRole.Assistant, [new AgentMessagePart.Text(text)]);

    private static string Text(AgentConversationMessage message) => string.Concat(message.Parts.OfType<AgentMessagePart.Text>().Select(static part => part.Value));

    private static string ToolResult(AgentConversationMessage message)
        => string.Concat(message.Parts.OfType<AgentMessagePart.ToolResult>().SelectMany(static part => part.Result.Items.OfType<AgentToolResultItem.Text>()).Select(static item => item.Value));

    private static AgentToolDefinition Tool(string name, Func<AgentToolInvocation, AgentToolResult> handler)
        => new(new AgentToolSpec(name, $"The tool {name}.", JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone()),
            (invocation, _) => Task.FromResult(handler(invocation)));

    // A sandbox: the fake factory runs the server on pipes whatever host the request names.
    private sealed class TestSandbox : IAgentProcessHost
    {
        public string Key => "sandbox-of-the-test";

        public Task<IAgentHostedProcess> StartAsync(AgentProcessLaunch launch, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    /// <summary>A Codex CLI provider runtime on the fake app-server, and a driver that runs turns as the agent session does.</summary>
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly LoopbackHarnessToolEndpoints _endpoints = new();

        public Fixture(CodexAppServerFake server, string command = "dotnet")
        {
            WorkingDirectory = Path.Combine(Path.GetTempPath(), nameof(CodexCliTurnExecutorTests), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(WorkingDirectory);
            Home = Path.Combine(WorkingDirectory, ".codex-home");
            Options = new CodexCliRuntimeOptions
            {
                ProviderKey = ProviderId.Value,
                ProviderName = "Codex",
                Settings = new CodexCliSettings(command, null, new Dictionary<string, string>()),
                Home = Home,
                ToolEndpoints = _endpoints,
                ProbeDirectory = Path.Combine(WorkingDirectory, ".probe"),
                ConnectionFactory = server,
                CancelTimeout = TimeSpan.FromSeconds(5),
                RequestTimeout = TimeSpan.FromSeconds(20),
            };
            Runtime = new CodexCliModelProviderRuntime(new ModelProviderDescriptor(ProviderId, "Codex", "codex-cli"), Options);
            Executor = (CodexCliTurnExecutor)Runtime.CreateTurnExecutor();
        }

        public CodexCliRuntimeOptions Options { get; }

        public CodexCliModelProviderRuntime Runtime { get; }

        public CodexCliTurnExecutor Executor { get; }

        public string WorkingDirectory { get; }

        public string Home { get; }

        public string DeveloperInstructions { get; set; } = "Be brief.";

        public AgentSessionState State { get; set; } = new() { SessionId = "session-1", UpdatedAt = DateTimeOffset.UtcNow };

        public IReadOnlyList<AgentToolDefinition> Tools { get; init; } = [];

        public string? PermissionMode { get; init; }

        public AgentPermissionRequestHandler OnPermission { get; init; } = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce));

        public Action<List<AgentConversationMessage>>? BeforeNextTurn { get; init; }

        public Action<AgentTurnSessionUpdate>? OnSessionUpdate { get; init; }

        public IAgentProcessHost? ProcessHost { get; init; }

        public async Task<List<AgentConversationMessage>> RunAsync(IReadOnlyList<AgentConversationMessage> start, CancellationToken cancellationToken = default)
        {
            var conversation = new List<AgentConversationMessage>(start);
            Executor.AttachRun(new AgentProviderRunContext("session-1", new AgentRunId("run-1"), OnPermission, null));
            var steered = false;
            while (true)
            {
                var response = await Executor.ExecuteTurnAsync(
                    CreateRequest(conversation),
                    static (_, _) => ValueTask.CompletedTask,
                    (update, _) =>
                    {
                        lock (this)
                        {
                            OnSessionUpdate?.Invoke(update);
                        }

                        return ValueTask.CompletedTask;
                    },
                    cancellationToken);
                conversation.Add(response.AssistantMessage);
                if (response.ProviderState is { } providerState)
                {
                    State = State with { ProviderState = providerState, ProviderBranch = null };
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

        public AgentTurnRequest CreateRequest(IReadOnlyList<AgentConversationMessage> conversation)
            => new()
            {
                Provider = Runtime.RuntimeDescriptor,
                ProviderId = ProviderId,
                SessionId = "session-1",
                RunId = new AgentRunId("run-1"),
                WorkingDirectory = WorkingDirectory,
                DeveloperInstructions = DeveloperInstructions,
                PermissionMode = PermissionMode,
                Conversation = [.. conversation],
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
