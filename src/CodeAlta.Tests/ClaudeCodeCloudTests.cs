using System.Text.Json.Nodes;
using CodeAlta.Agent;
using CodeAlta.Agent.Claude;
using CodeAlta.Agent.Runtime;
using Transcript = CodeAlta.Tests.ClaudeCodeExternalTurnsTests.Transcript;

namespace CodeAlta.Tests;

/// <summary>
/// The cloud sessions of Claude Code through its documented commands: how they are started and what their screen says,
/// how a message is sent, and how a session is brought back into a conversation of this computer, whose turns made in
/// the cloud are added to the session.
/// </summary>
[TestClass]
public sealed class ClaudeCodeCloudTests
{
    private const string CloudId = "session_01QK5u8xRwDmhdvgs5BDTzPp";
    private const string Conversation = "6f1d2b7e-1111-4c3a-9d2e-0a1b2c3d4e5f";

    [TestMethod]
    public void TheScreenOfAStart_GivesTheSessionItsTitleAndItsAddress_AlsoWhenTheTerminalWrappedTheLink()
    {
        string[] rows =
        [
            "PS C:\\repo> claude --cloud \"Reply with exactly: cloud-ok-C.\"",
            "Created cloud session: Cloud ok C",
            "View: https://claude.ai/code/session_01QK5u8x",
            "RwDmhdvgs5BDTzPp?from=cli&m=0",
            "Resume with: claude --teleport session_01QK5u8xRwDmhdvgs5BDTzPp",
            "[process exited with code 0]",
        ];
        var screen = ClaudeCodeCloud.ReadScreen(rows);
        Assert.IsTrue(screen.Created);
        Assert.AreEqual(CloudId, screen.SessionId);
        Assert.AreEqual("Cloud ok C", screen.Title);
        Assert.IsNull(screen.Note);
        Assert.AreEqual($"https://claude.ai/code/{CloudId}", ClaudeCodeCloud.UrlOf(CloudId));
        Assert.IsFalse(ClaudeCodeCloud.Asks(rows));
    }

    [TestMethod]
    public void TheScreenOfAStart_SaysWhenClaudeCodeAsks_WhenItFailed_AndWhereTheSessionStarts()
    {
        Assert.IsTrue(ClaudeCodeCloud.Asks(["Quick safety check: Is this a project you created or one you trust?", " ❯ No, exit", "   Yes, I trust this folder", " Enter to confirm · Esc to cancel"]));

        var failed = ClaudeCodeCloud.ReadScreen(["PS C:\\repo> claude --cloud x", "Error: Cloud sessions are disabled by your organization's policy.", "Contact your organization admin.", "[process exited with code 1]"]);
        Assert.IsFalse(failed.Created);
        Assert.AreEqual("Error: Cloud sessions are disabled by your organization's policy.\nContact your organization admin.", failed.Message);

        var noted = ClaudeCodeCloud.ReadScreen(["Branch feat/x is not on GitHub: the session starts from main.", "", "Created cloud session: Fix it", $"View: https://claude.ai/code/{CloudId}"]);
        Assert.IsTrue(noted.Created);
        Assert.AreEqual("Branch feat/x is not on GitHub: the session starts from main.", noted.Note);
    }

    [TestMethod]
    public void TheIdentityOfAStartedSession_IsTheOneOfItsAddress_NeverAWordOfItsTitle()
    {
        // A title that names another session, and the next row glued to the end of the address.
        var titled = ClaudeCodeCloud.ReadScreen(["Created cloud session: Investigate session_01AAAAAAAAAA", $"View: https://claude.ai/code/{CloudId}",
            $"Resume with: claude --teleport {CloudId}"]);
        Assert.AreEqual(CloudId, titled.SessionId);
        var resumeOnly = ClaudeCodeCloud.ReadScreen(["Created cloud session: Fix it", $"Resume with: claude --teleport {CloudId}"]);
        Assert.AreEqual(CloudId, resumeOnly.SessionId);
        // An address and a resume line that disagree name no session: the start is not taken for one it did not create.
        var mixed = ClaudeCodeCloud.ReadScreen(["Created cloud session: Fix it", $"View: https://claude.ai/code/{CloudId}", "Resume with: claude --teleport session_01BBBBBBBBBB"]);
        Assert.IsFalse(mixed.Created);
        Assert.IsNull(mixed.SessionId);
        Assert.IsFalse(ClaudeCodeCloud.ReadScreen(["Created cloud session: session_01AAAAAAAAAA"]).Created, "A title alone names no session.");
    }

    [TestMethod]
    public void ALinkOrAnIdentifier_NamesACloudSession_AndNothingElseDoes()
    {
        Assert.AreEqual(CloudId, ClaudeCodeCloud.FindCloudSessionId(CloudId));
        Assert.AreEqual(CloudId, ClaudeCodeCloud.FindCloudSessionId($" https://claude.ai/code/{CloudId}?from=cli&m=0 "));
        Assert.AreEqual("cse_01ABCDEFGHJK", ClaudeCodeCloud.FindCloudSessionId("cse_01ABCDEFGHJK"));
        Assert.IsNull(ClaudeCodeCloud.FindCloudSessionId("https://example.com/code/" + CloudId), "Only a link of claude.ai.");
        Assert.IsNull(ClaudeCodeCloud.FindCloudSessionId("session_; rm -rf /"));
        Assert.IsNull(ClaudeCodeCloud.FindCloudSessionId(""));
        Assert.ThrowsExactly<ArgumentException>(() => ClaudeCodeCloud.UrlOf("session_x y"));
    }

    [TestMethod]
    public void TheCommands_AreTheDocumentedOnes_WithTheAccountOfTheProvider_AndWithoutAnApiKey()
    {
        var options = new ClaudeCodeModelProviderRuntimeOptions
        {
            ProviderKey = "claude",
            ConfigDirectory = "/accounts/max",
            EnvironmentVariables = new Dictionary<string, string> { ["CLAUDE_CODE_OAUTH_TOKEN"] = "from-the-provider" },
        };
        var start = ClaudeCodeCloud.StartLaunch("/bin/claude", options, "/repo", "-fix the bug");
        CollectionAssert.AreEqual(new[] { "--cloud=-fix the bug" }, start.Arguments.ToArray(), "The task is bound to the option: one with a dash stays the task.");
        Assert.AreEqual("/repo", start.WorkingDirectory);
        Assert.AreEqual(options.ResolveConfigDirectory(), start.Environment[ClaudeCodeProjectFolders.ConfigDirectoryVariable], "The account folder of the provider.");
        Assert.IsNull(start.Environment[ClaudeCodeApiKey.Variable], "A cloud session runs on the claude.ai account: no API key.");
        Assert.IsNull(start.Environment["CLAUDECODE"], "Not a child of the Claude Code session CodeAlta may run in.");
        Assert.IsNull(start.Environment["CLAUDE_CODE_CHILD_SESSION"]);
        Assert.AreEqual("from-the-provider", start.Environment["CLAUDE_CODE_OAUTH_TOKEN"]);
        var keyed = new ClaudeCodeModelProviderRuntimeOptions
        {
            ProviderKey = "claude",
            EnvironmentVariables = new Dictionary<string, string> { [ClaudeCodeApiKey.Variable] = "sk-ant-from-the-provider" },
        };
        foreach (var launch in new[] { ClaudeCodeCloud.StartLaunch("/bin/claude", keyed, "/repo", "x"), ClaudeCodeCloud.SendLaunch("/bin/claude", keyed, "/repo", CloudId),
                     ClaudeCodeCloud.TeleportLaunch("/bin/claude", keyed, "/repo", CloudId, Conversation) })
            Assert.IsNull(launch.Environment[ClaudeCodeApiKey.Variable], "Not even a key the provider sets.");
        Assert.IsFalse(start.Environment.ContainsKey("CLAUDE_AGENT_SDK_CLIENT_APP"), "Not a process of the Agent SDK.");

        CollectionAssert.AreEqual(new[] { "-p", $"--cloud={CloudId}", "--output-format", "json" },
            ClaudeCodeCloud.SendLaunch("/bin/claude", options, "/repo", CloudId).Arguments.ToArray());
        CollectionAssert.AreEqual(new[] { "-p", "/cost", "--teleport", CloudId, $"--session-id={Conversation}", "--output-format", "json" },
            ClaudeCodeCloud.TeleportLaunch("/bin/claude", options, "/repo", CloudId, Conversation).Arguments.ToArray());
        Assert.AreEqual($"claude --teleport {CloudId} --session-id {Conversation}", ClaudeCodeCloud.TeleportCommand(CloudId, Conversation));
        Assert.ThrowsExactly<ArgumentException>(() => ClaudeCodeCloud.SendLaunch("/bin/claude", options, "/repo", "--help"));
        Assert.ThrowsExactly<ArgumentException>(() => ClaudeCodeCloud.StartLaunch("/bin/claude", options, "/repo", new string('x', ClaudeCodeCloud.MaxTaskLength + 1)));
    }

    [TestMethod]
    public void WhatASendAnswers_IsReadIntoWhatTheUserIsTold()
    {
        Assert.AreEqual("ok", ClaudeCodeCloud.ReadSend(new(0, $"{{\"ok\":true,\"session_id\":\"{CloudId}\",\"url\":\"https://claude.ai/code/{CloudId}\"}}", "")).Status);
        var archived = ClaudeCodeCloud.ReadSend(new(1, $"{{\"ok\":false,\"session_id\":\"{CloudId}\",\"error\":\"cloud session {CloudId} is archived and cannot accept new messages\"}}", "Error: …"));
        Assert.AreEqual("archived", archived.Status);
        StringAssert.Contains(archived.Message, "archived");
        Assert.AreEqual("not_found", ClaudeCodeCloud.ReadSend(new(1, "{\"ok\":false,\"error\":\"invalid session ID: must be a cse_… or session_… tagged ID\"}", "")).Status);
        Assert.AreEqual("needs_claude_ai", ClaudeCodeCloud.ReadSend(new(1, "", "Error: Cloud sessions need a claude.ai sign-in. Run `claude auth login`.")).Status);
        Assert.AreEqual("policy", ClaudeCodeCloud.ReadSend(new(1, "", "Error: Cloud sessions are disabled by your organization's policy.")).Status);
        Assert.AreEqual("failed", ClaudeCodeCloud.ReadSend(new(null, "", "")).Status);
    }

    [TestMethod]
    public void WhatATeleportAnswers_SaysWhetherItWorked_AndThatItMadeNoModelTurn()
    {
        var (ok, turns, message) = ClaudeCodeCloud.ReadTeleport(new(0,
            "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"num_turns\":0,\"total_cost_usd\":0,\"session_id\":\"" + Conversation + "\"}", ""));
        Assert.IsTrue(ok);
        Assert.AreEqual(0, turns);
        Assert.IsNull(message);
        var silent = ClaudeCodeCloud.ReadTeleport(new(1, "", ""));
        Assert.IsFalse(silent.Ok);
        Assert.AreEqual("Claude Code did not bring the session back (exit code 1).", silent.Message);
    }

    [TestMethod]
    public void ACloudSessionBroughtBack_GivesTheTurnsMadeInTheCloud_MarkedSo_WithoutWhatTheBringBackWrote()
    {
        using var directory = TestTempDirectory.Create();
        var transcript = Transcript.For(directory, Conversation);
        transcript.Write(TeleportedLines(transcript));

        var read = ClaudeCodeExternalTurns.ReadBroughtBack(directory.Path, Conversation, directory.Path, 0, 0, fromTerminal: false, CancellationToken.None);
        Assert.AreEqual(AgentExternalTurnsStatus.Read, read.Status);
        Assert.AreEqual(2, read.Turns.Count, "The two turns of the cloud: not the /usage of the bring-back.");
        Assert.IsTrue(read.Turns.All(static turn => turn.Origin == AgentExternalTurnOrigin.Cloud));
        Assert.AreEqual("Reply with exactly: cloud-ok-C.", ((AgentMessagePart.Text)read.Turns[0].Messages[0].Parts[0]).Value);
        Assert.AreEqual("cloud-ok-D", ((AgentMessagePart.Text)read.Turns[1].Messages[^1].Parts[0]).Value);

        var after = ClaudeCodeProviderState.Read(read.ProviderStateAfter!(2, 2, 2))!;
        Assert.AreEqual(Conversation, after.SessionId);
        Assert.AreEqual((2, 2), (after.Users, after.Assistants));
        Assert.IsTrue(after.Imported, "The conversation was never given the instructions of CodeAlta.");
        Assert.IsTrue(after.Leaf is "l2" or "l3", $"The end of the file, not {after.Leaf}: the lines of the bring-back are never added.");
        Assert.IsNotNull(after.Position);
        var between = ClaudeCodeProviderState.Read(read.ProviderStateAfter!(1, 1, 1))!;
        Assert.AreEqual("a1", between.Leaf, "Between two turns: where a fork at the second one resumes.");

        // After /cost, nothing more is ever read from it.
        Assert.AreEqual(AgentExternalTurnsStatus.Unchanged,
            ClaudeCodeExternalTurns.Read(directory.Path, after, directory.Path, CancellationToken.None).Status);
    }

    [TestMethod]
    public void ACloudSessionBroughtBackInATerminal_IsFollowedFromTheContinuation_SoThatTheTurnsOfTheTerminalComeNext()
    {
        using var directory = TestTempDirectory.Create();
        var transcript = Transcript.For(directory, Conversation);
        var lines = TeleportedLines(transcript).Take(6).ToList();
        lines.Add(transcript.User("t1", "m1", "What did you do?", "cli"));
        lines.Add(transcript.Assistant("t2", "t1", "msg_t", ClaudeCodeExternalTurnsTests.Text("I answered twice."), "cli"));
        transcript.Write([.. lines]);

        var read = ClaudeCodeExternalTurns.ReadBroughtBack(directory.Path, Conversation, directory.Path, 0, 0, fromTerminal: true, CancellationToken.None);
        Assert.AreEqual(2, read.Turns.Count);
        var after = ClaudeCodeProviderState.Read(read.ProviderStateAfter!(2, 2, 2))!;
        Assert.AreEqual("a2", after.Leaf, "The last entry of the cloud before the continuation.");

        var next = ClaudeCodeExternalTurns.Read(directory.Path, after, directory.Path, CancellationToken.None);
        Assert.AreEqual(AgentExternalTurnsStatus.Read, next.Status);
        Assert.AreEqual(AgentExternalTurnOrigin.Terminal, next.Turns.Single().Origin, "The turn of the terminal, after the cloud.");
    }

    [TestMethod]
    public void ATranscriptWithoutTheContinuationEntry_IsNotBroughtBack_NorAMissingOne()
    {
        using var directory = TestTempDirectory.Create();
        var transcript = Transcript.For(directory, Conversation);
        // Claude Code is still writing it, or left it unfinished: what is there is not taken for the whole conversation.
        transcript.Write([.. TeleportedLines(transcript).Take(4)]);
        Assert.AreEqual(AgentExternalTurnsStatus.Missing,
            ClaudeCodeExternalTurns.ReadBroughtBack(directory.Path, Conversation, directory.Path, 0, 0, false, CancellationToken.None).Status);
        transcript.Write([]);
        Assert.AreEqual(AgentExternalTurnsStatus.Missing,
            ClaudeCodeExternalTurns.ReadBroughtBack(directory.Path, Conversation, directory.Path, 0, 0, false, CancellationToken.None).Status, "An empty one neither.");
        Assert.AreEqual(AgentExternalTurnsStatus.Missing,
            ClaudeCodeExternalTurns.ReadBroughtBack(directory.Path, Guid.NewGuid().ToString(), directory.Path, 0, 0, false, CancellationToken.None).Status);
        Assert.ThrowsExactly<ArgumentException>(() => ClaudeCodeExternalTurns.ReadBroughtBack(directory.Path, "..\\x", directory.Path, 0, 0, false, CancellationToken.None));
    }

    [TestMethod]
    public async Task TheExecutor_BringsASessionBackWithTheDocumentedCommand_AndChecksThatTheConversationWasWritten()
    {
        using var directory = TestTempDirectory.Create();
        var launches = new List<(ClaudeCodeLaunch Launch, string? Input)>();
        var writes = true;
        var options = new ClaudeCodeModelProviderRuntimeOptions
        {
            ProviderKey = "claude",
            ResolveCli = static () => new ClaudeCodeCliResolution("/fake/bin/claude", null),
            GetEnvironmentVariable = name => name == ClaudeCodeProjectFolders.ConfigDirectoryVariable ? directory.Path : null,
            RunCommand = (launch, input, _, _) =>
            {
                launches.Add((launch, input));
                if (launch.Arguments.Contains("--teleport") && writes)
                {
                    var transcript = Transcript.For(directory, Conversation);
                    transcript.Write(TeleportedLines(transcript));
                    return Task.FromResult(new ClaudeCodeCommandResult(0, "{\"type\":\"result\",\"is_error\":false,\"num_turns\":0}", ""));
                }

                return Task.FromResult(launch.Arguments.Contains("--teleport")
                    ? new ClaudeCodeCommandResult(0, "{\"type\":\"result\",\"is_error\":false,\"num_turns\":0}", "")
                    : new ClaudeCodeCommandResult(0, $"{{\"ok\":true,\"session_id\":\"{CloudId}\"}}", ""));
            },
        };
        await using var runtime = new ClaudeCodeModelProviderRuntime(options);
        var cloud = (IAgentProviderCloudSessions)runtime.CreateTurnExecutor();
        Assert.IsTrue(runtime.Descriptor.SupportsCloudSessions);

        Assert.AreEqual("ok", (await cloud.SendToCloudSessionAsync(CloudId, "Also add a test.", directory.Path, CancellationToken.None)).Status);
        Assert.AreEqual("Also add a test.", launches[^1].Input, "The message goes on the input, not on the command line.");

        Assert.AreEqual("ok", (await cloud.BringCloudSessionBackAsync(CloudId, Conversation, directory.Path, CancellationToken.None)).Status);
        Assert.AreEqual(directory.Path, launches[^1].Launch.WorkingDirectory);
        Assert.AreEqual(2, cloud.ReadBroughtBackTurns(Conversation, directory.Path, 0, 0, false, CancellationToken.None).Turns.Count);

        writes = false;
        var other = Guid.NewGuid().ToString();
        var missing = await cloud.BringCloudSessionBackAsync(CloudId, other, directory.Path, CancellationToken.None);
        Assert.AreEqual("failed", missing.Status, "Claude Code said it ended well but wrote nothing: that is not a success.");

        var start = await cloud.StartCloudSessionAsync(new AgentCloudStartRequest(directory.Path, "Fix it", "Repo"), null, null, CancellationToken.None);
        Assert.AreEqual("terminal_unavailable", start.Status, "Without a terminal of the host, Claude Code would refuse.");
    }

    [TestMethod]
    public async Task TheExecutor_StartsACloudSessionInTheTerminalOfTheHost_AndKeepsWhatItCreatedEvenWhenItWasEnded()
    {
        var options = new ClaudeCodeModelProviderRuntimeOptions { ProviderKey = "claude", ResolveCli = static () => new ClaudeCodeCliResolution("/fake/bin/claude", null) };
        await using var runtime = new ClaudeCodeModelProviderRuntime(options);
        var cloud = (IAgentProviderCloudSessions)runtime.CreateTurnExecutor();
        var terminal = new ScriptedTerminal(["Created cloud session: Fix it", $"View: https://claude.ai/code/{CloudId}?from=cli"], 0, canceled: false);
        var created = await cloud.StartCloudSessionAsync(new AgentCloudStartRequest("/repo", "Fix it", "Repo"), terminal, null, CancellationToken.None);
        Assert.AreEqual(("created", CloudId, "Fix it"), (created.Status, created.SessionId, created.Title));
        Assert.AreEqual($"https://claude.ai/code/{CloudId}", created.Url);
        CollectionAssert.AreEqual(new[] { "--cloud=Fix it" }, terminal.Program!.Arguments.ToArray());
        Assert.AreEqual("Claude Code: Repo", terminal.Program.Title);
        Assert.IsTrue(terminal.Asks!(["Enter to confirm"]), "The terminal is told what a question of Claude Code looks like.");

        var late = await cloud.StartCloudSessionAsync(new AgentCloudStartRequest("/repo", "Fix it", "Repo"),
            new ScriptedTerminal(["Created cloud session: Fix it", $"View: https://claude.ai/code/{CloudId}"], null, canceled: true), null, CancellationToken.None);
        Assert.AreEqual("created", late.Status, "A session Claude Code created before it was ended is kept: it is not started twice.");

        var failed = await cloud.StartCloudSessionAsync(new AgentCloudStartRequest("/repo", "Fix it", "Repo"),
            new ScriptedTerminal(["Error: Cloud sessions need a claude.ai sign-in."], 1, canceled: false), null, CancellationToken.None);
        Assert.AreEqual("failed", failed.Status);
        Assert.AreEqual("Error: Cloud sessions need a claude.ai sign-in.", failed.Message);
        Assert.AreEqual("term-1", failed.TerminalId);
    }

    [TestMethod]
    public void TheProbe_SaysWhetherTheAccountCanRunCloudSessions()
    {
        Assert.IsTrue(ClaudeCodeModelProviderRuntime.CloudCapability("max")!.Supported);
        Assert.IsTrue(ClaudeCodeModelProviderRuntime.CloudCapability("Pro")!.Supported);
        Assert.IsFalse(ClaudeCodeModelProviderRuntime.CloudCapability("API key")!.Supported);
        Assert.IsFalse(ClaudeCodeModelProviderRuntime.CloudCapability("bedrock")!.Supported);
        Assert.IsNull(ClaudeCodeModelProviderRuntime.CloudCapability(null), "Nothing is said when the inspection names nothing.");
    }

    // A transcript as Claude Code 2.1.309 wrote it after `claude -p /cost --teleport <id> --session-id <uuid>`: the two
    // turns of the cloud, the continuation entry, then what the local command wrote.
    private static string[] TeleportedLines(Transcript transcript)
        =>
        [
            transcript.User("u1", null, "Reply with exactly: cloud-ok-C.", "sdk-cli"),
            transcript.Assistant("a1", "u1", "msg_1", ClaudeCodeExternalTurnsTests.Text("cloud-ok-C"), "sdk-cli"),
            transcript.User("u2", "a1", "Now reply with exactly: cloud-ok-D.", "sdk-cli"),
            transcript.Assistant("a2", "u2", "msg_2", ClaudeCodeExternalTurnsTests.Text("cloud-ok-D"), "sdk-cli"),
            Meta(transcript, "m1", "a2", "This session is being continued from another machine. Application state may have changed."),
            Transcript.Line(new JsonObject { ["type"] = "system", ["subtype"] = "informational", ["content"] = "Session resumed", ["uuid"] = "s1", ["parentUuid"] = "m1", ["isSidechain"] = false, ["sessionId"] = Conversation }),
            Meta(transcript, "l1", "s1", "<local-command-caveat>The command below was run by the user locally.</local-command-caveat>"),
            transcript.User("l2", "l1", "<command-name>/usage</command-name>\n<command-message>usage</command-message>", "sdk-cli"),
            Transcript.Line(new JsonObject { ["type"] = "system", ["subtype"] = "local_command", ["content"] = "<local-command-stdout>Current session: 41% used</local-command-stdout>", ["uuid"] = "l3", ["parentUuid"] = "l2", ["isSidechain"] = false, ["sessionId"] = Conversation }),
        ];

    private static string Meta(Transcript transcript, string uuid, string parent, string text)
    {
        var line = JsonNode.Parse(transcript.User(uuid, parent, text, "sdk-cli"))!.AsObject();
        line["isMeta"] = true;
        return line.ToJsonString();
    }

    private sealed class ScriptedTerminal(string[] rows, int? exitCode, bool canceled) : IAgentInteractiveTerminal
    {
        public AgentInteractiveProgram? Program { get; private set; }

        public Func<IReadOnlyList<string>, bool>? Asks { get; private set; }

        public Task<AgentInteractiveOutcome> RunAsync(AgentInteractiveProgram program, Func<IReadOnlyList<string>, bool> asks, Action? onShown, CancellationToken cancellationToken)
        {
            Program = program;
            Asks = asks;
            return Task.FromResult(new AgentInteractiveOutcome(rows, exitCode, canceled, exitCode is 0 ? null : "term-1", false));
        }
    }
}
