using System.Text.Json.Nodes;
using CodeAlta.Agent;
using CodeAlta.Agent.Claude;
using CodeAlta.Agent.Runtime;
using Transcript = CodeAlta.Tests.ClaudeCodeExternalTurnsTests.Transcript;

namespace CodeAlta.Tests;

/// <summary>
/// The Claude Code sessions a project can import: those VS Code or a terminal started in its folder, which VS Code lists
/// too and no session of CodeAlta continues; and what an import reads of one.
/// </summary>
[TestClass]
public sealed class ClaudeCodeExternalConversationsTests
{
    private const string VsCode = "0f5c2c1e-7a0e-4a52-9d55-2a8d2b1c0e01";
    private const string Terminal = "0f5c2c1e-7a0e-4a52-9d55-2a8d2b1c0e02";
    private const string Programmatic = "0f5c2c1e-7a0e-4a52-9d55-2a8d2b1c0e03";
    private const string Daemon = "0f5c2c1e-7a0e-4a52-9d55-2a8d2b1c0e04";
    private const string Sidechain = "0f5c2c1e-7a0e-4a52-9d55-2a8d2b1c0e05";
    private const string Continued = "0f5c2c1e-7a0e-4a52-9d55-2a8d2b1c0e06";
    private const string Known = "0f5c2c1e-7a0e-4a52-9d55-2a8d2b1c0e07";
    private const string OtherFolder = "0f5c2c1e-7a0e-4a52-9d55-2a8d2b1c0e08";
    private const string Empty = "0f5c2c1e-7a0e-4a52-9d55-2a8d2b1c0e09";
    private const string Untitled = "0f5c2c1e-7a0e-4a52-9d55-2a8d2b1c0e10";
    private const string Continuation = "0f5c2c1e-7a0e-4a52-9d55-2a8d2b1c0e11";

    [TestMethod]
    public void List_GivesWhatVsCodeLists_StartedInTheFolder_AndUnknownToCodeAlta_NewestFirst()
    {
        using var directory = TestTempDirectory.Create();
        var (root, project, folder) = Layout(directory);
        var written = new DateTime(2026, 10, 14, 8, 0, 0, DateTimeKind.Utc);
        void Write(string id, Func<Transcript, string[]> lines, int minutes, string? cwd = null)
        {
            var transcript = new Transcript(Path.Combine(folder, id + ".jsonl"), id, cwd ?? project);
            transcript.Write(lines(transcript));
            File.SetLastWriteTimeUtc(Path.Combine(folder, id + ".jsonl"), written.AddMinutes(minutes));
        }

        Write(VsCode, t => [t.User("u1", null, "Fix the parser."), t.Assistant("a1", "u1", "m1", Text("Fixed.")), Title(VsCode, "Fix the parser of quoted keys")], 1);
        Write(Terminal, t => [t.User("u1", null, "Review the release.", "cli"), t.Assistant("a1", "u1", "m1", Text("Reviewed."), "cli")], 2);
        // Started by a program (CodeAlta's own sessions are such): VS Code does not list them.
        Write(Programmatic, t => [t.User("u1", null, "From the SDK.", "sdk-cli"), t.Assistant("a1", "u1", "m1", Text("Done."), "sdk-cli")], 3);
        Write(Daemon, t => [With(t.User("u1", null, "In the background."), "sessionKind", "daemon"), t.Assistant("a1", "u1", "m1", Text("Done."))], 4);
        Write(Sidechain, t => [With(t.User("u1", null, "A subagent."), "isSidechain", true), t.Assistant("a1", "u1", "m1", Text("Done."))], 5);
        Write(Continuation, t => [t.User("u1", null, "Go on."), t.Assistant("a1", "u1", "m1", Text("Going on."))], 0);
        Write(Continued, t => [t.User("u1", null, "Before."), t.Assistant("a1", "u1", "m1", Text("Done.")),
            Transcript.Line(new JsonObject { ["type"] = "continued-in", ["sessionId"] = Continued, ["continuedInSessionId"] = Continuation })], 6);
        Write(Known, t => [t.User("u1", null, "Continued in CodeAlta."), t.Assistant("a1", "u1", "m1", Text("Done."))], 7);
        // A folder whose name is the same (the name keeps only letters and digits of the path) is not the folder.
        Write(OtherFolder, t => [t.User("u1", null, "Elsewhere."), t.Assistant("a1", "u1", "m1", Text("Done."))], 8, project + "-other");
        Write(Untitled, _ => [Transcript.Line(new JsonObject { ["type"] = "mode", ["mode"] = "default", ["sessionId"] = Untitled })], 9);
        File.WriteAllText(Path.Combine(folder, Empty + ".jsonl"), string.Empty);
        File.WriteAllText(Path.Combine(folder, VsCode + "_HOST_2026-10-14_Conflict.jsonl"), File.ReadAllText(Path.Combine(folder, VsCode + ".jsonl")));
        File.WriteAllText(Path.Combine(folder, "notes.jsonl"), File.ReadAllText(Path.Combine(folder, VsCode + ".jsonl")));

        var conversations = new ClaudeCodeExternalConversations(root, null);
        var all = conversations.List(project, id => id == Known, 0, 10, CancellationToken.None);
        // Continuation is listed: what continues another transcript is a session of its own; the one it continues is not.
        CollectionAssert.AreEqual(new[] { Terminal, VsCode, Continuation }, all.Conversations.Select(static conversation => conversation.ConversationId).ToArray());
        Assert.AreEqual(3, all.Total);
        var vsCode = all.Conversations[1];
        Assert.AreEqual("Fix the parser of quoted keys", vsCode.Title);
        Assert.AreEqual(AgentExternalTurnOrigin.VsCode, vsCode.Origin);
        Assert.AreEqual(new DateTimeOffset(written.AddMinutes(1)), vsCode.LastChangedAt);
        Assert.AreEqual(AgentExternalTurnOrigin.Terminal, all.Conversations[0].Origin);
        Assert.AreEqual("Review the release.", all.Conversations[0].Title, "Without a title, the first prompt.");
        Assert.IsFalse(vsCode.IsHeld);

        var first = conversations.List(project, id => id == Known, 0, 1, CancellationToken.None);
        CollectionAssert.AreEqual(new[] { Terminal }, first.Conversations.Select(static conversation => conversation.ConversationId).ToArray());
        Assert.AreEqual(3, first.Total, "The total counts those beyond the page.");
        // A page further on lists what follows, in the same order, with the same total; one past the end lists nothing.
        var next = conversations.List(project, id => id == Known, 1, 5, CancellationToken.None);
        CollectionAssert.AreEqual(new[] { VsCode, Continuation }, next.Conversations.Select(static conversation => conversation.ConversationId).ToArray());
        Assert.AreEqual(3, next.Total);
        var past = conversations.List(project, id => id == Known, 3, 5, CancellationToken.None);
        Assert.AreEqual((0, 3), (past.Conversations.Count, past.Total));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => conversations.List(project, static _ => false, -1, 5, CancellationToken.None));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => conversations.List(project, static _ => false, 0, 0, CancellationToken.None));

        // A transcript that changed is read again; one that did not is not.
        File.AppendAllText(Path.Combine(folder, Terminal + ".jsonl"), Title(Terminal, "Review the release notes") + "\n");
        Assert.AreEqual("Review the release notes", conversations.List(project, static _ => false, 0, 10, CancellationToken.None).Conversations[0].Title);

        Assert.AreEqual(0, conversations.List(Path.Combine(directory.Path, "no-project"), static _ => false, 0, 10, CancellationToken.None).Total);
        Assert.AreEqual(0, new ClaudeCodeExternalConversations(Path.Combine(directory.Path, "no-claude"), null)
            .List(project, static _ => false, 0, 10, CancellationToken.None).Total, "No configuration folder: nothing.");
    }

    [TestMethod]
    public void List_SaysWhichSessionsAnotherClaudeProcessHolds_AndWhatItDoes()
    {
        using var directory = TestTempDirectory.Create();
        var (root, project, folder) = Layout(directory);
        foreach (var id in new[] { VsCode, Terminal, Continuation })
        {
            var transcript = new Transcript(Path.Combine(folder, id + ".jsonl"), id, project);
            transcript.Write(transcript.User("u1", null, "Hello."), transcript.Assistant("a1", "u1", "m1", Text("Hi.")));
        }

        var cli = new ClaudeCodeFakeCli { ConfigRoot = root };
        // A VS Code panel merely open on the session is idle; a terminal that works on it is busy.
        cli.HoldConversation(4242, VsCode);
        cli.HoldConversation(4243, Terminal, entrypoint: "cli", status: "busy");
        var conversations = new ClaudeCodeExternalConversations(root, new ClaudeCodeLiveSessions(root, new ClaudeCodeOwnProcesses(cli.Probe), cli.Probe, TimeProvider.System));
        var held = conversations.List(project, static _ => false, 0, 10, CancellationToken.None).Conversations
            .ToDictionary(static c => c.ConversationId, static c => (c.IsHeld, c.HolderStatus));
        Assert.AreEqual((true, "idle"), held[VsCode]);
        Assert.AreEqual((true, "busy"), held[Terminal]);
        Assert.AreEqual((false, (string?)null), held[Continuation]);
    }

    [TestMethod]
    public void Describe_GivesTheCommandAndTheVsCodeAddress_OnlyOfAConversationTheListingGivesForTheFolder()
    {
        using var directory = TestTempDirectory.Create();
        var (root, project, folder) = Layout(directory);
        foreach (var (id, entrypoint, cwd) in new[] { (VsCode, "claude-vscode", project), (Programmatic, "sdk-cli", project), (OtherFolder, "claude-vscode", project + "-other") })
        {
            var transcript = new Transcript(Path.Combine(folder, id + ".jsonl"), id, cwd);
            transcript.Write(transcript.User("u1", null, "Hello.", entrypoint), transcript.Assistant("a1", "u1", "m1", Text("Hi."), entrypoint));
        }

        var cli = new ClaudeCodeFakeCli { ConfigRoot = root };
        cli.HoldConversation(4242, VsCode, status: "busy");
        var conversations = new ClaudeCodeExternalConversations(root, new ClaudeCodeLiveSessions(root, new ClaudeCodeOwnProcesses(cli.Probe), cli.Probe, TimeProvider.System));

        var described = conversations.Describe(project, VsCode)!;
        Assert.AreEqual((VsCode, $"claude --resume {VsCode}", project, $"vscode://anthropic.claude-code/open?session={VsCode}"),
            (described.ConversationId, described.ResumeCommand, described.WorkingDirectory, described.EditorUri));
        Assert.AreEqual((AgentConversationHolderOrigin.VsCode, "busy"), (described.Holder!.Origin, described.Holder.Status));

        // Not a conversation of the listing of that folder: started by a program, started in another folder, missing, or not an identifier.
        foreach (var id in new[] { Programmatic, OtherFolder, Untitled, "../" + VsCode, VsCode + "x" })
            Assert.IsNull(conversations.Describe(project, id), id);
        Assert.IsNull(conversations.Describe(project + "-other", VsCode), "A transcript of the folder only.");
    }

    [TestMethod]
    public void Read_GivesEveryTurn_AndTheStateThatResumesTheConversation()
    {
        using var directory = TestTempDirectory.Create();
        var (root, project, folder) = Layout(directory);
        var transcript = new Transcript(Path.Combine(folder, VsCode + ".jsonl"), VsCode, project);
        transcript.Write(
            transcript.User("u1", null, "Read the file."),
            transcript.Assistant("a1", "u1", "m1", ClaudeCodeExternalTurnsTests.ToolUse("toolu_1", "Read", new JsonObject { ["file_path"] = Path.Combine(project, "a.txt") })),
            transcript.ToolResult("r1", "a1", "toolu_1", "contents"),
            transcript.Assistant("a2", "r1", "m2", Text("It says contents.")),
            transcript.Boundary("b1", "a2"),
            transcript.Summary("s1", "b1", "Summary of the conversation."),
            transcript.User("u2", "s1", "Now fix it.", "cli"),
            transcript.Assistant("a3", "u2", "m3", Text("Fixed."), "cli"),
            Title(VsCode, "Read and fix"),
            Transcript.Line(new JsonObject { ["type"] = "cost-state", ["sessionId"] = VsCode, ["totalCostUSD"] = 0.42 }));

        var conversations = new ClaudeCodeExternalConversations(root, null);
        var read = conversations.Read(project, VsCode, CancellationToken.None)!;
        Assert.AreEqual("Read and fix", read.Conversation.Title);
        Assert.AreEqual(AgentExternalTurnsStatus.Read, read.Turns.Status);
        CollectionAssert.AreEqual(new[] { false, true, false }, read.Turns.Turns.Select(static turn => turn.IsCompaction).ToArray());
        CollectionAssert.AreEqual(new[] { AgentExternalTurnOrigin.VsCode, AgentExternalTurnOrigin.VsCode, AgentExternalTurnOrigin.Terminal },
            read.Turns.Turns.Select(static turn => turn.Origin).ToArray());

        var state = ClaudeCodeProviderState.Read(read.Turns.ProviderStateAfter!(3, 2, 3))!;
        Assert.AreEqual(VsCode, state.SessionId);
        Assert.AreEqual((2, 3), (state.Users, state.Assistants), "The counts are those given: what was written.");
        Assert.AreEqual("a3", state.Leaf);
        Assert.AreEqual("a3", state.Anchor);
        Assert.AreEqual(0.42, state.Cost!.Value, 1e-9);
        Assert.IsNotNull(state.Position, "A catch-up reads only what is written after the import.");
        Assert.IsNull(state.Instructions);
        Assert.IsTrue(state.Imported, "The conversation was never given the instructions of CodeAlta.");

        // Between two turns: the point a fork or a rewind resumes at, without what is known only of the whole read.
        var between = ClaudeCodeProviderState.Read(read.Turns.ProviderStateAfter(1, 1, 2))!;
        Assert.AreEqual((1, 2, "a2"), (between.Users, between.Assistants, between.Leaf));
        Assert.IsNull(between.Position);
        Assert.IsNull(between.Cost);
        Assert.IsTrue(between.Imported);

        // Only what the listing gives for the folder is read.
        Assert.IsNull(conversations.Read(project, Programmatic, CancellationToken.None), "A missing transcript.");
        Assert.IsNull(conversations.Read(project, "not-a-session", CancellationToken.None));
        Assert.IsNull(conversations.Read(Path.Combine(directory.Path, "elsewhere"), VsCode, CancellationToken.None));
        var programmatic = new Transcript(Path.Combine(folder, Programmatic + ".jsonl"), Programmatic, project);
        programmatic.Write(programmatic.User("u1", null, "From the SDK.", "sdk-cli"), programmatic.Assistant("a1", "u1", "m1", Text("Done."), "sdk-cli"));
        Assert.IsNull(conversations.Read(project, Programmatic, CancellationToken.None), "A session a program started is not imported.");
    }

    [TestMethod]
    public void ProviderState_KeepsTheImportedMark_OnlyUntilTheInstructionsAreGiven()
    {
        var imported = new ClaudeCodeProviderState("c1", 1, 1, Leaf: "a1", Imported: true);
        Assert.IsTrue(ClaudeCodeProviderState.Read(imported.ToJson())!.Imported);
        var instructed = ClaudeCodeProviderState.Read((imported with { Instructions = "hash" }).ToJson())!;
        Assert.IsFalse(instructed.Imported, "Once given, the instructions are updated as for any conversation.");
        Assert.IsFalse(ClaudeCodeProviderState.Read(new ClaudeCodeProviderState("c1", 1, 1).ToJson())!.Imported);
    }

    [TestMethod]
    public void InstructionsOfAnImportedConversation_AreGivenAsNew()
    {
        var note = ClaudeCodePrompts.CreateInstructionsForImportedConversation("# CodeAlta\n\nWork carefully.\n");
        StringAssert.StartsWith(note, "<codealta_instructions_update>");
        StringAssert.Contains(note, "This conversation was started outside CodeAlta");
        StringAssert.Contains(note, "You were not given the instructions of CodeAlta before");
        StringAssert.Contains(note, "# CodeAlta\n\nWork carefully.");
        Assert.IsFalse(note.Contains("replaces", StringComparison.Ordinal), "Nothing of CodeAlta is in its system prompt to replace.");
        StringAssert.EndsWith(note, "</codealta_instructions_update>");
    }

    [TestMethod]
    public void Preview_ReadsOnlyWhatTheListingGives_UpToItsSize_AndKeepsTheLastReadsOfUnchangedTranscripts()
    {
        using var directory = TestTempDirectory.Create();
        var (root, project, folder) = Layout(directory);
        string Write(string id, string title)
        {
            var transcript = new Transcript(Path.Combine(folder, id + ".jsonl"), id, project);
            transcript.Write(transcript.User("u1", null, "Read the file."), transcript.Assistant("a1", "u1", "m1", Text("Read.")), Title(id, title));
            return Path.Combine(folder, id + ".jsonl");
        }

        var first = Write(VsCode, "First");
        Write(Terminal, "Second");
        Write(Untitled, "Third");
        var conversations = new ClaudeCodeExternalConversations(root, null);
        var shown = conversations.Preview(project, VsCode, 1024 * 1024, CancellationToken.None);
        Assert.AreEqual((AgentExternalConversationPreviewStatus.Read, "First", new FileInfo(first).Length), (shown.Status, shown.Conversation!.Conversation.Title, shown.Length));
        Assert.AreEqual(new FileInfo(first).LastWriteTimeUtc, shown.LastWriteTimeUtc);
        Assert.AreSame(shown, conversations.Preview(project, VsCode, 1024 * 1024, CancellationToken.None), "An unchanged transcript is not read again.");

        // The last two are kept: the one shown and the one before.
        var second = conversations.Preview(project, Terminal, 1024 * 1024, CancellationToken.None);
        Assert.AreSame(shown, conversations.Preview(project, VsCode, 1024 * 1024, CancellationToken.None));
        conversations.Preview(project, Untitled, 1024 * 1024, CancellationToken.None);
        Assert.AreNotSame(second, conversations.Preview(project, Terminal, 1024 * 1024, CancellationToken.None), "The oldest read was let go.");

        // A transcript that changed is read again; a larger one than asked is not read.
        File.AppendAllText(first, Transcript.Line(new JsonObject { ["type"] = "custom-title", ["customTitle"] = "Renamed", ["sessionId"] = VsCode }) + "\n");
        File.SetLastWriteTimeUtc(first, DateTime.UtcNow.AddSeconds(5));
        var changed = conversations.Preview(project, VsCode, 1024 * 1024, CancellationToken.None);
        Assert.AreNotSame(shown, changed);
        Assert.AreEqual("Renamed", changed.Conversation!.Conversation.Title);
        var large = conversations.Preview(project, VsCode, 16, CancellationToken.None);
        Assert.AreEqual((AgentExternalConversationPreviewStatus.TooLarge, null, new FileInfo(first).Length), (large.Status, large.Conversation, large.Length));

        Assert.AreEqual(AgentExternalConversationPreviewStatus.NotFound, conversations.Preview(project, Programmatic, 1024 * 1024, CancellationToken.None).Status);
        Assert.AreEqual(AgentExternalConversationPreviewStatus.NotFound, conversations.Preview(project, "not-a-session", 1024 * 1024, CancellationToken.None).Status);
        Assert.AreEqual(AgentExternalConversationPreviewStatus.NotFound,
            conversations.Preview(Path.Combine(directory.Path, "elsewhere"), VsCode, 1024 * 1024, CancellationToken.None).Status);
    }

    private static (string Root, string Project, string Folder) Layout(TestTempDirectory directory)
    {
        var root = Path.Combine(directory.Path, "claude");
        var project = Directory.CreateDirectory(Path.Combine(directory.Path, "project")).FullName;
        var folder = Directory.CreateDirectory(ClaudeCodeProjectFolders.GetProjectFolder(root, project)).FullName;
        return (root, project, folder);
    }

    private static JsonObject Text(string text) => ClaudeCodeExternalTurnsTests.Text(text);

    private static string Title(string sessionId, string title)
        => Transcript.Line(new JsonObject { ["type"] = "ai-title", ["aiTitle"] = title, ["sessionId"] = sessionId });

    private static string With(string line, string name, JsonNode value)
    {
        var node = JsonNode.Parse(line)!.AsObject();
        node[name] = value;
        return node.ToJsonString();
    }
}
