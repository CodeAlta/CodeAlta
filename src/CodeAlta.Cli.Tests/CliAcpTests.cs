using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Text.Json;
using CodeAlta.Acp;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;

namespace CodeAlta.Cli.Tests;

/// <summary><c>altacli acp</c> end to end: the command runs in the test process on temporary roots and a fake provider, and the
/// test is its client, an <see cref="AcpClient"/> over pipes.</summary>
[TestClass]
public sealed class CliAcpTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private static readonly AgentModelInfo[] Models =
    [
        new("fake-model", DisplayName: "Fake Model", SupportedReasoningEfforts: [AgentReasoningEffort.Low, AgentReasoningEffort.High]),
        new("fake-other", DisplayName: "Fake Other"),
    ];

    [TestMethod]
    public async Task Initialize_AdvertisesWhatCodeAltaServes()
    {
        using var root = new CliTestRoot();
        await using var acp = await AcpHarness.StartAsync(root, new FakeProvider());

        var initialize = acp.Client.InitializeResponse;
        Assert.AreEqual(1, initialize.ProtocolVersion.Value.GetInt32());
        Assert.AreEqual("altacli", initialize.AgentInfo!.Name);
        Assert.IsFalse(string.IsNullOrWhiteSpace(initialize.AgentInfo.Version));
        Assert.AreEqual(0, initialize.AuthMethods!.Count, "CodeAlta signs in to its providers itself.");
        var capabilities = initialize.AgentCapabilities;
        Assert.IsTrue(capabilities.LoadSession);
        Assert.IsTrue(capabilities.PromptCapabilities.Image);
        Assert.IsTrue(capabilities.PromptCapabilities.EmbeddedContext);
        Assert.AreNotEqual(true, capabilities.PromptCapabilities.Audio);
        Assert.IsNotNull(capabilities.SessionCapabilities.List);
        Assert.IsNotNull(capabilities.SessionCapabilities.Fork);
        Assert.IsNotNull(capabilities.SessionCapabilities.Close);
        Assert.IsNotNull(capabilities.SessionCapabilities.Resume);
        var meta = capabilities.Meta![CodeAltaAcpExtensions.MetaKey];
        Assert.AreEqual(CodeAltaAcpExtensions.SchemaVersion, meta.GetProperty("schemaVersion").GetInt32());
        var notifications = meta.GetProperty("notifications").EnumerateArray().Select(static item => item.GetString()).ToArray();
        CollectionAssert.AreEquivalent(CodeAltaAcpExtensions.AcpMethods.ToArray(), notifications);

        // A client of a later version is answered with the version the agent speaks.
        var later = await acp.Client.InitializeAsync(new InitializeRequest
        {
            ProtocolVersion = ProtocolVersion.From(7),
            ClientCapabilities = new ClientCapabilities(),
        }).WaitAsync(Timeout);
        Assert.AreEqual(1, later.ProtocolVersion.Value.GetInt32());
    }

    [TestMethod]
    public async Task NewSession_HasTheAgentPromptsAsModes_AndTheModelAndTheEffortAsOptions()
    {
        using var root = new CliTestRoot();
        await using var acp = await AcpHarness.StartAsync(root, new FakeProvider(models: Models), "--model", "fake:fake-model@high");

        var session = await acp.Client.SessionNewAsync(new NewSessionRequest { Cwd = root.ProjectRoot }).WaitAsync(Timeout);

        Assert.IsTrue(Guid.TryParse(session.SessionId.Value, out _));
        Assert.AreEqual("default", session.Modes!.CurrentModeId.Value);
        var modes = session.Modes.AvailableModes.Select(static mode => mode.Id.Value).ToArray();
        CollectionAssert.Contains(modes, "default");
        CollectionAssert.Contains(modes, "plan");
        var options = session.ConfigOptions!;
        CollectionAssert.AreEqual(new[] { "mode", "model", "thought_level" }, options.Select(static option => option.Id).ToArray());
        Assert.AreEqual("default", Option(options, "mode").CurrentValue);
        Assert.AreEqual("mode", Option(options, "mode").Category);
        Assert.AreEqual("fake:fake-model", Option(options, "model").CurrentValue);
        CollectionAssert.IsSubsetOf(new[] { "fake:fake-model", "fake:fake-other" }, Values(Option(options, "model")));
        Assert.AreEqual("high", Option(options, "thought_level").CurrentValue);
        Assert.AreEqual("thought_level", Option(options, "thought_level").Category);
        CollectionAssert.AreEqual(new[] { "low", "high" }, Values(Option(options, "thought_level")));

        var missing = await Assert.ThrowsExactlyAsync<AcpJsonRpcException>(() =>
            acp.Client.SessionNewAsync(new NewSessionRequest { Cwd = Path.Combine(root.Root, "missing") }).WaitAsync(Timeout));
        Assert.AreEqual(AcpErrorCodes.InvalidParams, missing.Code);
        var relative = await Assert.ThrowsExactlyAsync<AcpJsonRpcException>(() => acp.Client.SessionNewAsync(new NewSessionRequest { Cwd = "project" }).WaitAsync(Timeout));
        Assert.AreEqual(AcpErrorCodes.InvalidParams, relative.Code);
    }

    [TestMethod]
    public async Task Prompt_SendsEveryUpdateOfTheRunBeforeItsResponse_ThenEndsTheTurn()
    {
        using var root = new CliTestRoot();
        File.WriteAllText(Path.Combine(root.ProjectRoot, "a.txt"), "content");
        var provider = new FakeProvider();
        provider.Turns.Enqueue(FakeProvider.CallTool("read_file", """{"path":"a.txt"}"""));
        provider.Turns.Enqueue(FakeProvider.Stream("All done.", "Let me think."));
        await using var acp = await AcpHarness.StartAsync(root, provider);
        var sessionId = (await acp.Client.SessionNewAsync(new NewSessionRequest { Cwd = root.ProjectRoot }).WaitAsync(Timeout)).SessionId.Value;

        var response = await acp.PromptAsync(sessionId, "read a.txt");

        Assert.AreEqual("end_turn", response.StopReason.Value.GetString());
        Assert.AreEqual(100UL, response.Usage!.InputTokens);
        Assert.AreEqual(20UL, response.Usage.OutputTokens);
        var meta = response.Meta![CodeAltaAcpExtensions.MetaKey];
        Assert.IsFalse(string.IsNullOrEmpty(meta.GetProperty("runId").GetString()));
        Assert.AreEqual(0.25, meta.GetProperty("cost").GetProperty("amount").GetDouble(), 1e-9);
        // The updates came before the response: they are all there once it is.
        var updates = acp.Updates(sessionId);
        Assert.AreEqual("All done.", Text(updates, "agent_message_chunk"));
        Assert.AreEqual("Let me think.", Text(updates, "agent_thought_chunk"));
        var call = updates.Single(static update => Kind(update) == "tool_call");
        Assert.AreEqual("read_file a.txt", call.GetProperty("title").GetString());
        Assert.AreEqual("read", call.GetProperty("kind").GetString());
        Assert.IsTrue(updates.Any(static update => Kind(update) == "tool_call_update" && AcpJson.GetString(update, "status") == "completed"));
        Assert.IsFalse(updates.Any(static update => Kind(update) == "user_message_chunk"), "The client shows the prompt it sent.");
        Assert.AreEqual("read a.txt", provider.Requests.Last().Conversation.First(static message => message.Role == AgentConversationRole.User).Parts
            .OfType<AgentMessagePart.Text>().First().Value);

        // A provider that streams nothing: its answer is sent whole.
        provider.Turns.Enqueue(FakeProvider.Reply("Not streamed."));
        var before = acp.Updates(sessionId).Count;
        Assert.AreEqual("end_turn", (await acp.PromptAsync(sessionId, "again")).StopReason.Value.GetString());
        Assert.AreEqual("Not streamed.", Text(acp.Updates(sessionId).Skip(before).ToList(), "agent_message_chunk"));

        // A visible session is marked for CodeAlta Desktop.
        Assert.IsNotNull(new SessionMarkerStore(new CatalogOptions { GlobalRoot = root.Profile }).TryRead(sessionId));
    }

    [TestMethod]
    public async Task Prompt_TakesImagesResourcesAndLinks()
    {
        using var root = new CliTestRoot();
        var file = Path.Combine(root.ProjectRoot, "linked.txt");
        File.WriteAllText(file, "linked");
        var provider = new FakeProvider();
        await using var acp = await AcpHarness.StartAsync(root, provider);
        var sessionId = (await acp.Client.SessionNewAsync(new NewSessionRequest { Cwd = root.ProjectRoot }).WaitAsync(Timeout)).SessionId.Value;
        var image = ContentBlock.FromImage(Convert.ToBase64String([0x89, 0x50, 0x4E, 0x47, 1, 2, 3]), "image/png");
        var resource = ContentBlock.FromJsonElement(AcpJson.Parse("""{"type":"resource","resource":{"uri":"file:///notes.md","text":"The notes."}}"""));

        var response = await acp.Client.SessionPromptAsync(new PromptRequest
        {
            SessionId = sessionId,
            Prompt = [ContentBlock.FromText("Look at "), ContentBlock.FromResourceLink(new Uri(file).AbsoluteUri, "linked.txt"), image, resource],
        }).WaitAsync(Timeout);

        Assert.AreEqual("end_turn", response.StopReason.Value.GetString());
        var user = provider.Requests.Last().Conversation.First(static message => message.Role == AgentConversationRole.User);
        var text = string.Join("\n", user.Parts.OfType<AgentMessagePart.Text>().Select(static part => part.Value));
        StringAssert.Contains(text, "Look at [linked.txt](file:///");
        StringAssert.Contains(text, "<resource uri=\"file:///notes.md\">\nThe notes.\n</resource>");
        var saved = Directory.EnumerateFiles(Path.Combine(root.Profile, "sessions"), "*.png", SearchOption.AllDirectories).ToArray();
        Assert.AreEqual(1, saved.Length, "The image is saved beside the journal, as a pasted one is.");

        var audio = ContentBlock.FromJsonElement(AcpJson.Parse("""{"type":"audio","data":"AAAA","mimeType":"audio/wav"}"""));
        var refused = await Assert.ThrowsExactlyAsync<AcpJsonRpcException>(() =>
            acp.Client.SessionPromptAsync(new PromptRequest { SessionId = sessionId, Prompt = [audio] }).WaitAsync(Timeout));
        Assert.AreEqual(AcpErrorCodes.InvalidParams, refused.Code);
    }

    [TestMethod]
    public async Task Cancel_EndsThePromptWithCancelled()
    {
        using var root = new CliTestRoot();
        var provider = new FakeProvider();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.Turns.Enqueue(FakeProvider.WaitThenReply(new TaskCompletionSource().Task, "never", started));
        await using var acp = await AcpHarness.StartAsync(root, provider);
        var sessionId = (await acp.Client.SessionNewAsync(new NewSessionRequest { Cwd = root.ProjectRoot }).WaitAsync(Timeout)).SessionId.Value;

        var prompt = acp.PromptAsync(sessionId, "work");
        await started.Task.WaitAsync(Timeout);
        var busy = await Assert.ThrowsExactlyAsync<AcpJsonRpcException>(() => acp.PromptAsync(sessionId, "more"));
        Assert.AreEqual("session_busy", busy.ErrorData!.Value.GetProperty("kind").GetString(), "One prompt at a time.");
        await acp.Client.CancelSessionAsync(sessionId);
        var response = await prompt;

        Assert.AreEqual("cancelled", response.StopReason.Value.GetString());
        provider.Turns.Enqueue(FakeProvider.Reply("Back."));
        Assert.AreEqual("end_turn", (await acp.PromptAsync(sessionId, "again")).StopReason.Value.GetString(), "The session takes prompts after a cancel.");
    }

    [TestMethod]
    public async Task Cancel_WhileThePromptIsRead_EndsItWithCancelled_AndTheProviderNeverRuns()
    {
        using var root = new CliTestRoot();
        var provider = new FakeProvider();
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var environment = root.Environment(provider) with
        {
            AcpPromptReading = async () =>
            {
                reading.TrySetResult();
                await gate.Task;
            },
        };
        await using var acp = await AcpHarness.StartAsync(root, environment);
        var sessionId = (await acp.Client.SessionNewAsync(new NewSessionRequest { Cwd = root.ProjectRoot }).WaitAsync(Timeout)).SessionId.Value;

        var prompt = acp.PromptAsync(sessionId, "work");
        await reading.Task.WaitAsync(Timeout);
        await acp.Client.CancelSessionAsync(sessionId);
        // Answered after the cancel was handled: the server reads the messages in order.
        await acp.Client.SessionListAsync(new ListSessionsRequest()).WaitAsync(Timeout);
        gate.SetResult();
        var response = await prompt;

        Assert.AreEqual("cancelled", response.StopReason.Value.GetString());
        Assert.AreEqual(0, provider.Requests.Count, "A prompt cancelled before it ran never reaches the provider.");

        // A cancel with no prompt in flight cancels nothing, not even the next prompt.
        await acp.Client.CancelSessionAsync(sessionId);
        await acp.Client.SessionListAsync(new ListSessionsRequest()).WaitAsync(Timeout);
        provider.Turns.Enqueue(FakeProvider.Reply("Back."));
        Assert.AreEqual("end_turn", (await acp.PromptAsync(sessionId, "again")).StopReason.Value.GetString());
    }

    [TestMethod]
    public async Task SetModeAndConfigOptions_ChangeWhatTheNextPromptsRunWith()
    {
        using var root = new CliTestRoot();
        var provider = new FakeProvider(models: Models);
        var other = new FakeProvider("other", "other-model");
        var environment = root.Environment(null) with
        {
            ConfigureModelProviders = registry =>
            {
                provider.Register(registry);
                other.Register(registry);
            },
        };
        await using var acp = await AcpHarness.StartAsync(root, environment, "--model", "fake:fake-model@high");
        var sessionId = (await acp.Client.SessionNewAsync(new NewSessionRequest { Cwd = root.ProjectRoot }).WaitAsync(Timeout)).SessionId.Value;

        await acp.Client.SessionSetModeAsync(new SetSessionModeRequest { SessionId = sessionId, ModeId = "plan" }).WaitAsync(Timeout);
        var update = acp.Updates(sessionId).Last(static update => Kind(update) == "config_option_update");
        Assert.AreEqual("plan", update.GetProperty("configOptions").EnumerateArray().Single(static option => option.GetProperty("id").GetString() == "mode")
            .GetProperty("currentValue").GetString(), "A client of the options learns the mode.");
        var low = await acp.SetOptionAsync(sessionId, "thought_level", "low");
        Assert.AreEqual("low", Option(low.ConfigOptions, "thought_level").CurrentValue);
        Assert.AreEqual("plan", Option(low.ConfigOptions, "mode").CurrentValue);
        await acp.PromptAsync(sessionId, "plan it");
        Assert.AreEqual(AgentReasoningEffort.Low, provider.Requests.Last().ReasoningEffort);

        var back = await acp.SetOptionAsync(sessionId, "mode", "default");
        Assert.AreEqual("default", Option(back.ConfigOptions, "mode").CurrentValue);
        Assert.AreEqual("default", acp.Updates(sessionId).Last(static update => Kind(update) == "current_mode_update").GetProperty("currentModeId").GetString(),
            "A client of the modes learns the mode.");

        var switched = await acp.SetOptionAsync(sessionId, "model", "other:other-model");
        Assert.AreEqual("other:other-model", Option(switched.ConfigOptions, "model").CurrentValue);
        CollectionAssert.Contains(Values(Option(switched.ConfigOptions, "model")), "fake:fake-model", "The models of every provider that is ready are offered.");
        Assert.IsFalse(switched.ConfigOptions.Any(static option => option.Id == "thought_level"), "A model that lists no effort has no option for it.");
        other.Turns.Enqueue(FakeProvider.Reply("From the other provider."));
        await acp.PromptAsync(sessionId, "and now");
        Assert.AreEqual(1, other.Requests.Count, "The next prompt runs on the provider chosen.");

        foreach (var (id, value) in new[] { ("model", "missing:model"), ("thought_level", "loud"), ("color", "blue"), ("mode", "no-such-prompt") })
        {
            var refused = await Assert.ThrowsExactlyAsync<AcpJsonRpcException>(() => acp.SetOptionAsync(sessionId, id, value), $"{id}={value}");
            Assert.AreEqual(AcpErrorCodes.InvalidParams, refused.Code, $"{id}={value}");
        }
    }

    [TestMethod]
    public async Task Load_ReplaysTheHistory_Resume_DoesNot_AndBothTakePrompts()
    {
        using var root = new CliTestRoot();
        File.WriteAllText(Path.Combine(root.ProjectRoot, "a.txt"), "content");
        var provider = new FakeProvider();
        string sessionId;
        await using (var first = await AcpHarness.StartAsync(root, provider))
        {
            sessionId = (await first.Client.SessionNewAsync(new NewSessionRequest { Cwd = root.ProjectRoot }).WaitAsync(Timeout)).SessionId.Value;
            provider.Turns.Enqueue(FakeProvider.CallTool("read_file", """{"path":"a.txt"}"""));
            provider.Turns.Enqueue(FakeProvider.Stream("All done.", "Let me think."));
            await first.PromptAsync(sessionId, "read a.txt");
            Assert.AreEqual(0, await first.CloseInputAsync());
        }

        await using var acp = await AcpHarness.StartAsync(root, provider);
        var loaded = await acp.Client.SessionLoadAsync(new LoadSessionRequest { SessionId = sessionId, Cwd = root.ProjectRoot }).WaitAsync(Timeout);

        Assert.AreEqual("default", loaded.Modes!.CurrentModeId.Value);
        var replay = acp.Updates(sessionId);
        Assert.AreEqual("read a.txt", Text(replay, "user_message_chunk"));
        Assert.AreEqual("All done.", Text(replay, "agent_message_chunk"));
        Assert.AreEqual("Let me think.", Text(replay, "agent_thought_chunk"));
        var kinds = replay.Select(Kind).ToList();
        Assert.IsTrue(kinds.IndexOf("user_message_chunk") < kinds.IndexOf("tool_call") && kinds.IndexOf("tool_call") < kinds.IndexOf("agent_message_chunk"),
            string.Join(", ", kinds));
        Assert.IsTrue(replay.Any(static update => Kind(update) is "tool_call" or "tool_call_update" && AcpJson.GetString(update, "status") == "completed"),
            "A tool call is replayed with its final status.");
        provider.Turns.Enqueue(FakeProvider.Reply("Continued."));
        Assert.AreEqual("end_turn", (await acp.PromptAsync(sessionId, "go on")).StopReason.Value.GetString());
        Assert.IsTrue(provider.Requests.Last().Conversation.Count > 2, "The loaded session goes on with its history.");

        var inUse = await Assert.ThrowsExactlyAsync<AcpJsonRpcException>(() =>
            acp.Client.SessionLoadAsync(new LoadSessionRequest { SessionId = sessionId, Cwd = root.ProjectRoot }).WaitAsync(Timeout));
        Assert.AreEqual("session_in_use", inUse.ErrorData!.Value.GetProperty("kind").GetString());

        await acp.Client.SessionCloseAsync(new CloseSessionRequest { SessionId = sessionId }).WaitAsync(Timeout);
        var count = acp.Updates(sessionId).Count;
        await acp.Client.SessionResumeAsync(new ResumeSessionRequest { SessionId = sessionId, Cwd = root.ProjectRoot }).WaitAsync(Timeout);
        Assert.AreEqual(count, acp.Updates(sessionId).Count, "A resume replays nothing.");
        Assert.AreEqual("end_turn", (await acp.PromptAsync(sessionId, "resumed")).StopReason.Value.GetString());

        var unknown = await Assert.ThrowsExactlyAsync<AcpJsonRpcException>(() =>
            acp.Client.SessionLoadAsync(new LoadSessionRequest { SessionId = Guid.NewGuid().ToString(), Cwd = root.ProjectRoot }).WaitAsync(Timeout));
        Assert.AreEqual(AcpErrorCodes.ResourceNotFound, unknown.Code);
        var closed = await Assert.ThrowsExactlyAsync<AcpJsonRpcException>(() =>
            acp.Client.SessionCloseAsync(new CloseSessionRequest { SessionId = Guid.NewGuid().ToString() }).WaitAsync(Timeout));
        Assert.AreEqual(AcpErrorCodes.ResourceNotFound, closed.Code);
    }

    [TestMethod]
    public async Task Load_WritesTheMarkerOfTheSessionAgain()
    {
        using var root = new CliTestRoot();
        var provider = new FakeProvider();
        var markers = new SessionMarkerStore(new CatalogOptions { GlobalRoot = root.Profile });
        string sessionId;
        await using (var first = await AcpHarness.StartAsync(root, provider))
        {
            sessionId = (await first.Client.SessionNewAsync(new NewSessionRequest { Cwd = root.ProjectRoot }).WaitAsync(Timeout)).SessionId.Value;
            await first.PromptAsync(sessionId, "hello");
            Assert.AreEqual(0, await first.CloseInputAsync());
        }

        Assert.IsTrue(markers.Delete(sessionId), "Desktop lists it and deletes the marker.");
        await using var acp = await AcpHarness.StartAsync(root, provider);
        await acp.Client.SessionLoadAsync(new LoadSessionRequest { SessionId = sessionId, Cwd = root.ProjectRoot }).WaitAsync(Timeout);
        await acp.PromptAsync(sessionId, "again");

        Assert.IsNotNull(markers.TryRead(sessionId), "A loaded session is announced again.");
    }

    [TestMethod]
    public async Task ListAndFork_FindTheSessionsOfAFolder_AndForkOneWithItsHistory()
    {
        using var root = new CliTestRoot();
        var elsewhere = Path.Combine(root.Root, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        var provider = new FakeProvider();
        await using var acp = await AcpHarness.StartAsync(root, provider);
        var here = (await acp.Client.SessionNewAsync(new NewSessionRequest { Cwd = root.ProjectRoot }).WaitAsync(Timeout)).SessionId.Value;
        await acp.PromptAsync(here, "first");
        var there = (await acp.Client.SessionNewAsync(new NewSessionRequest { Cwd = elsewhere }).WaitAsync(Timeout)).SessionId.Value;
        await acp.PromptAsync(there, "second");

        var inFolder = await acp.Client.SessionListAsync(new ListSessionsRequest { Cwd = root.ProjectRoot }).WaitAsync(Timeout);
        var all = await acp.Client.SessionListAsync(new ListSessionsRequest()).WaitAsync(Timeout);

        CollectionAssert.AreEqual(new[] { here }, inFolder.Sessions.Select(static session => session.SessionId.Value).ToArray());
        Assert.AreEqual(root.ProjectRoot, inFolder.Sessions[0].Cwd);
        Assert.IsTrue(DateTimeOffset.TryParse(inFolder.Sessions[0].UpdatedAt, out _));
        CollectionAssert.AreEqual(new[] { there, here }, all.Sessions.Select(static session => session.SessionId.Value).ToArray(), "The newest first.");
        Assert.IsNull(all.NextCursor);
        var cursor = await Assert.ThrowsExactlyAsync<AcpJsonRpcException>(() => acp.Client.SessionListAsync(new ListSessionsRequest { Cursor = "nope" }).WaitAsync(Timeout));
        Assert.AreEqual(AcpErrorCodes.InvalidParams, cursor.Code);

        var fork = await acp.Client.SessionForkAsync(new ForkSessionRequest { SessionId = here, Cwd = root.ProjectRoot }).WaitAsync(Timeout);
        Assert.AreNotEqual(here, fork.SessionId.Value);
        Assert.IsNotNull(fork.ConfigOptions);
        await acp.PromptAsync(fork.SessionId.Value, "in the fork");
        var conversation = provider.Requests.Last().Conversation;
        Assert.IsTrue(conversation.Any(static message => message.Parts.OfType<AgentMessagePart.Text>().Any(static part => part.Value == "first")),
            "The fork starts with the history of its session.");
        Assert.IsNotNull(new SessionMarkerStore(new CatalogOptions { GlobalRoot = root.Profile }).TryRead(fork.SessionId.Value), "The fork is marked as a new session is.");
    }

    [TestMethod]
    public async Task NewSession_ConnectsTheSessionToTheMcpServersItIsGiven_WithoutWritingAConfiguration()
    {
        using var root = new CliTestRoot();
        var log = Path.Combine(root.Root, "mcp.log");
        var provider = new FakeProvider();
        provider.Turns.Enqueue(FakeProvider.CallTool("mcp__tiny__echo", """{"text":"hi"}"""));
        provider.Turns.Enqueue(FakeProvider.Reply("Echoed."));
        // The server is started with dotnet, found on the PATH of the process.
        await using var acp = await AcpHarness.StartAsync(root, root.Environment(provider) with { PluginEnvironment = null }, plugins: true);
        Assert.IsTrue(acp.Client.InitializeResponse.AgentCapabilities.McpCapabilities.Http);
        Assert.IsTrue(acp.Client.InitializeResponse.AgentCapabilities.McpCapabilities.Sse);
        var server = new McpServerStdio
        {
            Name = "tiny",
            Command = "dotnet",
            Args = [Path.Combine(AppContext.BaseDirectory, "CodeAlta.Tests.TinyMcpServer.dll")],
            Env = [new EnvVariable { Name = "MCP_TEST_LOG", Value = log }],
        };

        var sessionId = (await acp.Client.SessionNewAsync(new NewSessionRequest { Cwd = root.ProjectRoot, McpServers = [server] }).WaitAsync(Timeout)).SessionId.Value;
        var response = await acp.PromptAsync(sessionId, "echo hi");

        Assert.AreEqual("end_turn", response.StopReason.Value.GetString());
        CollectionAssert.Contains(provider.Requests.First().Tools.Select(static tool => tool.Spec.Name).ToArray(), "mcp__tiny__echo",
            "The session has the tools of its server from its first prompt.");
        StringAssert.Contains(File.ReadAllText(log), "CALL echo");
        var call = acp.Updates(sessionId).First(static update => Kind(update) == "tool_call");
        StringAssert.StartsWith(call.GetProperty("title").GetString(), "mcp__tiny__echo");
        Assert.IsFalse(File.Exists(Path.Combine(root.ProjectRoot, ".alta", "mcp.json")), "No configuration is written.");
        Assert.IsFalse(File.Exists(Path.Combine(root.Profile, "mcp.json")), "No configuration is written.");

        var invalid = await Assert.ThrowsExactlyAsync<AcpJsonRpcException>(() => acp.Client.SessionNewAsync(new NewSessionRequest
        {
            Cwd = root.ProjectRoot,
            McpServers = [new McpServerHttp { Name = "remote", Url = "not an address" }],
        }).WaitAsync(Timeout));
        Assert.AreEqual(AcpErrorCodes.InvalidParams, invalid.Code);

        // A run that starts no MCP server refuses the servers rather than ignoring them.
        await using var off = await AcpHarness.StartAsync(root, provider);
        Assert.AreNotEqual(true, off.Client.InitializeResponse.AgentCapabilities.McpCapabilities.Http);
        var refused = await Assert.ThrowsExactlyAsync<AcpJsonRpcException>(() =>
            off.Client.SessionNewAsync(new NewSessionRequest { Cwd = root.ProjectRoot, McpServers = [server] }).WaitAsync(Timeout));
        Assert.AreEqual("mcp_off", refused.ErrorData!.Value.GetProperty("kind").GetString());
    }

    [TestMethod]
    public async Task Sessions_OfAFolderThatRequiresAServerThatDoesNotStart_AreRefused()
    {
        using var root = new CliTestRoot();
        var other = Path.Combine(root.Root, "other");
        Directory.CreateDirectory(other);
        var provider = new FakeProvider();
        string existing;
        await using (var first = await AcpHarness.StartAsync(root, provider))
        {
            existing = (await first.Client.SessionNewAsync(new NewSessionRequest { Cwd = other }).WaitAsync(Timeout)).SessionId.Value;
            await first.PromptAsync(existing, "hello");
        }

        // The project of the other folder requires a server no configuration defines; the folder of the command requires nothing.
        Directory.CreateDirectory(Path.Combine(other, ".alta"));
        File.WriteAllText(Path.Combine(other, ".alta", "config.toml"), "[plugins.mcp.servers.ghost]\nrequired = true\n");
        await using var acp = await AcpHarness.StartAsync(root, root.Environment(provider) with { PluginEnvironment = null }, plugins: true);

        await acp.Client.SessionNewAsync(new NewSessionRequest { Cwd = root.ProjectRoot }).WaitAsync(Timeout);
        var created = await Assert.ThrowsExactlyAsync<AcpJsonRpcException>(() => acp.Client.SessionNewAsync(new NewSessionRequest { Cwd = other }).WaitAsync(Timeout));
        Assert.AreEqual("required_unavailable", created.ErrorData!.Value.GetProperty("kind").GetString());
        StringAssert.Contains(created.Message, "'ghost'");
        var loaded = await Assert.ThrowsExactlyAsync<AcpJsonRpcException>(() =>
            acp.Client.SessionLoadAsync(new LoadSessionRequest { SessionId = existing, Cwd = other }).WaitAsync(Timeout));
        Assert.AreEqual("required_unavailable", loaded.ErrorData!.Value.GetProperty("kind").GetString());
        var resumed = await Assert.ThrowsExactlyAsync<AcpJsonRpcException>(() =>
            acp.Client.SessionResumeAsync(new ResumeSessionRequest { SessionId = existing, Cwd = other }).WaitAsync(Timeout));
        Assert.AreEqual("required_unavailable", resumed.ErrorData!.Value.GetProperty("kind").GetString(), "The refused load left the session closed.");
    }

    [TestMethod]
    public async Task ProviderErrors_AreAuthRequiredForASignIn_AndInternalErrorsWithTheirKind()
    {
        using var root = new CliTestRoot();
        await using (var notReady = await AcpHarness.StartAsync(root, new FakeProvider(availability: ModelProviderAvailability.Failed)))
        {
            var refused = await Assert.ThrowsExactlyAsync<AcpJsonRpcException>(() =>
                notReady.Client.SessionNewAsync(new NewSessionRequest { Cwd = root.ProjectRoot }).WaitAsync(Timeout));
            Assert.AreEqual(AcpErrorCodes.AuthRequired, refused.Code);
            StringAssert.Contains(refused.Message, "altacli auth check");
        }

        var provider = new FakeProvider();
        await using var acp = await AcpHarness.StartAsync(root, provider);
        var sessionId = (await acp.Client.SessionNewAsync(new NewSessionRequest { Cwd = root.ProjectRoot }).WaitAsync(Timeout)).SessionId.Value;
        provider.Turns.Enqueue(FakeProvider.Fail(AgentFailureKind.Auth, "Sign in again."));
        var signIn = await Assert.ThrowsExactlyAsync<AcpJsonRpcException>(() => acp.PromptAsync(sessionId, "work"));
        Assert.AreEqual(AcpErrorCodes.AuthRequired, signIn.Code);

        provider.Turns.Enqueue(FakeProvider.Fail(AgentFailureKind.RateLimited, "Too many requests."));
        var failed = await Assert.ThrowsExactlyAsync<AcpJsonRpcException>(() => acp.PromptAsync(sessionId, "work again"));
        Assert.AreEqual(AcpErrorCodes.InternalError, failed.Code);
        StringAssert.Contains(failed.Message, "Too many requests.");
        Assert.AreEqual("rate_limited", failed.ErrorData!.Value.GetProperty("kind").GetString());
        Assert.AreEqual(sessionId, failed.ErrorData.Value.GetProperty("sessionId").GetString());

        var unknown = await Assert.ThrowsExactlyAsync<AcpJsonRpcException>(() => acp.PromptAsync(Guid.NewGuid().ToString(), "who"));
        Assert.AreEqual(AcpErrorCodes.ResourceNotFound, unknown.Code);
    }

    [TestMethod]
    public async Task ClosingStandardInput_CancelsTheRunsClosesTheSessions_AndExitsWithZero()
    {
        using var root = new CliTestRoot();
        var provider = new FakeProvider();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.Turns.Enqueue(FakeProvider.WaitThenReply(new TaskCompletionSource().Task, "never", started));
        await using var acp = await AcpHarness.StartAsync(root, provider);
        var sessionId = (await acp.Client.SessionNewAsync(new NewSessionRequest { Cwd = root.ProjectRoot }).WaitAsync(Timeout)).SessionId.Value;
        var prompt = acp.PromptAsync(sessionId, "work");
        await started.Task.WaitAsync(Timeout);

        var stopwatch = Stopwatch.StartNew();
        Assert.AreEqual(CliExitCodes.Completed, await acp.CloseInputAsync(), acp.Stderr);

        Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(30), "The run is aborted, not waited for.");
        _ = await Task.WhenAny(prompt, Task.Delay(TimeSpan.FromSeconds(1)));
        var events = await new CodeAlta.Agent.Runtime.FileSystemAgentSessionStore(new CodeAlta.Agent.Runtime.AgentRuntimePathLayout(root.Profile)).ReadEventsAsync(sessionId);
        Assert.AreEqual("cancelled", CodeAlta.Orchestration.Runtime.SessionOutcome.From(events, isRunning: false).AbortReason, "The journal records the abort of the run.");
    }

    [TestMethod]
    public async Task Help_IsWrittenOnStandardOutput()
    {
        using var root = new CliTestRoot();

        var help = await root.RunAsync(null, ["acp", "--help"]);

        Assert.AreEqual(0, help.ExitCode, help.Stderr);
        StringAssert.StartsWith(help.Stdout, "Usage: altacli acp [options]");
        StringAssert.Contains(help.Stdout, "\"command\": \"altacli\", \"args\": [\"acp\"]");
    }

    [TestMethod]
    public async Task Executable_AnswersInitializeOverItsStandardStreams_AndExitsWhenTheyClose()
    {
        var executable = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "altacli.exe" : "altacli");
        using var root = new CliTestRoot();
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        // A whole profile of its own, empty: no provider, no plugin, nothing of the user's.
        foreach (var arg in new[] { "acp", "--profile", root.Profile, "--ignore-user-config", "--no-plugins", "--offline" }) start.ArgumentList.Add(arg);
        Directory.CreateDirectory(root.Profile);
        using var process = Process.Start(start)!;
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await using var client = await AcpClient.ConnectAsync(process.StandardOutput.BaseStream, process.StandardInput.BaseStream, new InitializeRequest
            {
                ProtocolVersion = ProtocolVersion.From(1),
                ClientInfo = new Implementation { Name = "test", Version = "1.0.0" },
                ClientCapabilities = new ClientCapabilities(),
            }).WaitAsync(Timeout);
            Assert.AreEqual("altacli", client.InitializeResponse.AgentInfo!.Name);
            Assert.IsTrue(client.InitializeResponse.AgentCapabilities.LoadSession);
            process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(Timeout);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }

        Assert.AreEqual(CliExitCodes.Completed, process.ExitCode, await stderr);
    }

    private static SessionConfigOption Option(IEnumerable<SessionConfigOption> options, string id) => options.Single(option => option.Id == id);

    private static string[] Values(SessionConfigOption option)
        => [.. option.Value.GetProperty("options").EnumerateArray().Select(static value => value.GetProperty("value").GetString()!)];

    private static string? Kind(JsonElement update) => AcpJson.GetString(update, "sessionUpdate");

    private static string Text(IEnumerable<JsonElement> updates, string kind)
        => string.Concat(updates.Where(update => Kind(update) == kind).Select(static update => update.GetProperty("content").GetProperty("text").GetString()));

    /// <summary><c>altacli acp</c> in the test process, and its client over two pipes.</summary>
    private sealed class AcpHarness : IAsyncDisposable
    {
        private readonly Pipe _toAgent;
        private readonly Task<int> _run;
        private readonly StringWriter _stderr;
        private readonly ConcurrentQueue<AcpServerMessage> _messages;

        private AcpHarness(Pipe toAgent, Task<int> run, StringWriter stderr, ConcurrentQueue<AcpServerMessage> messages, AcpClient client)
        {
            _toAgent = toAgent;
            _run = run;
            _stderr = stderr;
            _messages = messages;
            Client = client;
        }

        public AcpClient Client { get; }

        public string Stderr => _stderr.ToString();

        public static Task<AcpHarness> StartAsync(CliTestRoot root, FakeProvider provider, params string[] args)
            => StartAsync(root, root.Environment(provider), args);

        public static Task<AcpHarness> StartAsync(CliTestRoot root, CliEnvironment environment, params string[] args)
            => StartAsync(root, environment, plugins: false, args);

        public static async Task<AcpHarness> StartAsync(CliTestRoot root, CliEnvironment environment, bool plugins, params string[] args)
        {
            ArgumentNullException.ThrowIfNull(root);
            var toAgent = new Pipe();
            var fromAgent = new Pipe();
            var stderr = new StringWriter();
            var streams = new CliStreams
            {
                Stdin = new StringReader(string.Empty),
                StdinStream = toAgent.Reader.AsStream(),
                Stdout = fromAgent.Writer.AsStream(),
                Stderr = TextWriter.Synchronized(stderr),
            };
            var run = Task.Run(async () =>
            {
                try
                {
                    return await CliApplication.RunAsync(["acp", .. plugins ? Array.Empty<string>() : ["--no-plugins"], .. args], streams, environment);
                }
                finally
                {
                    await fromAgent.Writer.CompleteAsync();
                }
            });
            var messages = new ConcurrentQueue<AcpServerMessage>();
            var client = await AcpClient.ConnectAsync(fromAgent.Reader.AsStream(), toAgent.Writer.AsStream(), new InitializeRequest
            {
                ProtocolVersion = ProtocolVersion.From(1),
                ClientInfo = new Implementation { Name = "test", Version = "1.0.0" },
                ClientCapabilities = new ClientCapabilities(),
            }, messages.Enqueue).WaitAsync(Timeout);
            return new AcpHarness(toAgent, run, stderr, messages, client);
        }

        public Task<PromptResponse> PromptAsync(string sessionId, string text)
            => Client.SessionPromptAsync(new PromptRequest { SessionId = sessionId, Prompt = [ContentBlock.FromText(text)] }).WaitAsync(Timeout);

        public Task<SetSessionConfigOptionResponse> SetOptionAsync(string sessionId, string id, string value)
            => Client.SessionSetConfigOptionAsync(SetSessionConfigOptionRequest.FromJsonElement(AcpJson.Build(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("sessionId", sessionId);
                writer.WriteString("configId", id);
                writer.WriteString("value", value);
                writer.WriteEndObject();
            }))).WaitAsync(Timeout);

        /// <summary>The updates of a session the agent sent so far, in order.</summary>
        public List<JsonElement> Updates(string sessionId)
            => [.. _messages.Where(message => message.Method == "session/update" && AcpJson.GetString(message.Params, "sessionId") == sessionId)
                .Select(static message => message.Params.GetProperty("update"))];

        /// <summary>Closes standard input of the agent, as a client that is gone, and waits for its exit code.</summary>
        public async Task<int> CloseInputAsync()
        {
            await _toAgent.Writer.CompleteAsync();
            return await _run.WaitAsync(Timeout);
        }

        public async ValueTask DisposeAsync()
        {
            await _toAgent.Writer.CompleteAsync();
            try
            {
                await _run.WaitAsync(Timeout);
            }
            finally
            {
                await Client.DisposeAsync();
            }
        }
    }
}
