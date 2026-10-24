using System.Text.Json.Nodes;
using CodeAlta.Agent;
using CodeAlta.Agent.Claude;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using Transcript = CodeAlta.Tests.ClaudeCodeExternalTurnsTests.Transcript;

namespace CodeAlta.Tests;

/// <summary>
/// The Claude Code sessions CodeAlta can import: those VS Code or a terminal started, in every folder of the profile,
/// which VS Code lists too and no session of CodeAlta continues; and what an import reads of one.
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
    public void List_GivesWhatVsCodeLists_UnknownToCodeAlta_NewestFirst_WithTheFolderItStartedIn()
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
        // Filed in the folder of the project, started in another folder: listed with the folder it started in, which
        // decides where CodeAlta shows it.
        Write(OtherFolder, t => [t.User("u1", null, "Elsewhere."), t.Assistant("a1", "u1", "m1", Text("Done."))], 8, project + "-other");
        Write(Untitled, _ => [Transcript.Line(new JsonObject { ["type"] = "mode", ["mode"] = "default", ["sessionId"] = Untitled })], 9);
        File.WriteAllText(Path.Combine(folder, Empty + ".jsonl"), string.Empty);
        File.WriteAllText(Path.Combine(folder, VsCode + "_HOST_2026-10-14_Conflict.jsonl"), File.ReadAllText(Path.Combine(folder, VsCode + ".jsonl")));
        File.WriteAllText(Path.Combine(folder, "notes.jsonl"), File.ReadAllText(Path.Combine(folder, VsCode + ".jsonl")));

        var conversations = new ClaudeCodeExternalConversations(root, null);
        var all = conversations.List(id => id == Known, CancellationToken.None);
        // Continuation is listed: what continues another transcript is a session of its own; the one it continues is not.
        CollectionAssert.AreEqual(new[] { OtherFolder, Terminal, VsCode, Continuation }, all.Select(static conversation => conversation.ConversationId).ToArray());
        var vsCode = all[2];
        Assert.AreEqual("Fix the parser of quoted keys", vsCode.Title);
        Assert.AreEqual(AgentExternalTurnOrigin.VsCode, vsCode.Origin);
        Assert.AreEqual(new DateTimeOffset(written.AddMinutes(1)), vsCode.LastChangedAt);
        Assert.AreEqual((project, project), (vsCode.StartDirectory, vsCode.EndDirectory), "It never left the folder it started in.");
        Assert.AreEqual(AgentExternalTurnOrigin.Terminal, all[1].Origin);
        Assert.AreEqual("Review the release.", all[1].Title, "Without a title, the first prompt.");
        Assert.AreEqual(project + "-other", all[0].StartDirectory);
        Assert.IsFalse(vsCode.IsHeld, "Holders are looked up for a page only.");

        // A transcript that changed is read again; one that did not is not.
        File.AppendAllText(Path.Combine(folder, Terminal + ".jsonl"), Title(Terminal, "Review the release notes") + "\n");
        File.SetLastWriteTimeUtc(Path.Combine(folder, Terminal + ".jsonl"), written.AddMinutes(20));
        Assert.AreEqual("Review the release notes", conversations.List(static _ => false, CancellationToken.None)[0].Title);

        Assert.AreEqual(0, new ClaudeCodeExternalConversations(Path.Combine(directory.Path, "no-claude"), null)
            .List(static _ => false, CancellationToken.None).Count, "No configuration folder: nothing.");
    }

    [TestMethod]
    public void List_LooksInEveryFolder_KeepingTheNewestTranscriptOfASession_AndWhereItEnded()
    {
        using var directory = TestTempDirectory.Create();
        var (root, project, folder) = Layout(directory);
        var worktree = Directory.CreateDirectory(Path.Combine(project, ".claude", "worktrees", "fix")).FullName;
        var worktreeFolder = Directory.CreateDirectory(ClaudeCodeProjectFolders.GetProjectFolder(root, worktree)).FullName;
        var written = new DateTime(2026, 10, 14, 8, 0, 0, DateTimeKind.Utc);

        // EnterWorktree moved the transcript to the folder of the worktree: it started in the project and ended there.
        var movedPath = Path.Combine(worktreeFolder, VsCode + ".jsonl");
        var moved = new Transcript(movedPath, VsCode, project);
        moved.Write(moved.User("u1", null, "Make a worktree."), WithCwd(moved.Assistant("a1", "u1", "m1", Text("Made.")), worktree));
        File.SetLastWriteTimeUtc(movedPath, written.AddMinutes(2));
        // An older copy of the same session elsewhere (the same file seen through a link, or a copy left behind) is not listed twice.
        File.Copy(movedPath, Path.Combine(folder, VsCode + ".jsonl"));
        File.SetLastWriteTimeUtc(Path.Combine(folder, VsCode + ".jsonl"), written.AddMinutes(1));
        var otherPath = Path.Combine(folder, Terminal + ".jsonl");
        var other = new Transcript(otherPath, Terminal, project);
        other.Write(other.User("u1", null, "Review.", "cli"), other.Assistant("a1", "u1", "m1", Text("Reviewed."), "cli"));
        File.SetLastWriteTimeUtc(otherPath, written);

        var conversations = new ClaudeCodeExternalConversations(root, null);
        var all = conversations.List(static _ => false, CancellationToken.None);
        CollectionAssert.AreEqual(new[] { VsCode, Terminal }, all.Select(static conversation => conversation.ConversationId).ToArray());
        Assert.AreEqual((project, worktree), (all[0].StartDirectory, all[0].EndDirectory));
        Assert.AreEqual(new DateTimeOffset(written.AddMinutes(2)), all[0].LastChangedAt, "The newest transcript of the session.");

        // Read, shown and described by its identifier alone, wherever its transcript is.
        Assert.AreEqual(worktree, conversations.Read(VsCode, CancellationToken.None)!.Conversation.EndDirectory);
        Assert.AreEqual(AgentExternalConversationPreviewStatus.Read, conversations.Preview(VsCode, 1024 * 1024, CancellationToken.None).Status);
        Assert.AreEqual(worktree, conversations.Describe(VsCode.ToUpperInvariant(), worktree)!.WorkingDirectory);
        Assert.AreEqual(VsCode, conversations.Describe(VsCode.ToUpperInvariant(), worktree)!.ConversationId, "The identifier as the transcript is named.");
    }

    [TestMethod]
    public void ToLocalDirectory_GivesAFolderOfGitBashAsAWindowsPath_AndKeepsOthers()
    {
        Assert.IsNull(ClaudeCodeExternalConversations.ToLocalDirectory(null));
        Assert.IsNull(ClaudeCodeExternalConversations.ToLocalDirectory(" "));
        if (OperatingSystem.IsWindows())
        {
            Assert.AreEqual(@"C:\dev\project", ClaudeCodeExternalConversations.ToLocalDirectory("/c/dev/project"));
            Assert.AreEqual(@"C:\", ClaudeCodeExternalConversations.ToLocalDirectory("/c"));
            Assert.AreEqual(@"C:\dev\project", ClaudeCodeExternalConversations.ToLocalDirectory(@"C:\dev\project\"));
            Assert.AreEqual("/home/me/project", ClaudeCodeExternalConversations.ToLocalDirectory("/home/me/project"), "A folder of another system, as written.");
        }
        else
        {
            Assert.AreEqual("/c/dev/project", ClaudeCodeExternalConversations.ToLocalDirectory("/c/dev/project/"));
        }
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
        var held = conversations.WithHolders(conversations.List(static _ => false, CancellationToken.None))
            .ToDictionary(static c => c.ConversationId, static c => (c.IsHeld, c.HolderStatus, c.HolderOrigin));
        Assert.AreEqual((true, "idle", (AgentConversationHolderOrigin?)AgentConversationHolderOrigin.VsCode), held[VsCode]);
        Assert.AreEqual((true, "busy", (AgentConversationHolderOrigin?)AgentConversationHolderOrigin.Terminal), held[Terminal]);
        Assert.AreEqual((false, (string?)null, (AgentConversationHolderOrigin?)null), held[Continuation]);
    }

    [TestMethod]
    public void Describe_GivesTheCommandAndTheVsCodeAddress_OnlyOfAConversationTheListingGives()
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

        var described = conversations.Describe(VsCode, project)!;
        Assert.AreEqual((VsCode, $"claude --resume {VsCode}", project, $"vscode://anthropic.claude-code/open?session={VsCode}"),
            (described.ConversationId, described.ResumeCommand, described.WorkingDirectory, described.EditorUri));
        Assert.AreEqual((AgentConversationHolderOrigin.VsCode, "busy"), (described.Holder!.Origin, described.Holder.Status));
        Assert.AreEqual(project + "-other", conversations.Describe(OtherFolder, project + "-other")!.WorkingDirectory, "Run in the folder given.");

        // Not a conversation of the listing: started by a program, missing, or not an identifier.
        foreach (var id in new[] { Programmatic, Untitled, "../" + VsCode, VsCode + "x" })
            Assert.IsNull(conversations.Describe(id, project), id);
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
        var read = conversations.Read(VsCode, CancellationToken.None)!;
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

        // Only what the listing gives is read.
        Assert.IsNull(conversations.Read(Programmatic, CancellationToken.None), "A missing transcript.");
        Assert.IsNull(conversations.Read("not-a-session", CancellationToken.None));
        var programmatic = new Transcript(Path.Combine(folder, Programmatic + ".jsonl"), Programmatic, project);
        programmatic.Write(programmatic.User("u1", null, "From the SDK.", "sdk-cli"), programmatic.Assistant("a1", "u1", "m1", Text("Done."), "sdk-cli"));
        Assert.IsNull(conversations.Read(Programmatic, CancellationToken.None), "A session a program started is not imported.");
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
        var shown = conversations.Preview(VsCode, 1024 * 1024, CancellationToken.None);
        Assert.AreEqual((AgentExternalConversationPreviewStatus.Read, "First", new FileInfo(first).Length), (shown.Status, shown.Conversation!.Conversation.Title, shown.Length));
        Assert.AreEqual(new FileInfo(first).LastWriteTimeUtc, shown.LastWriteTimeUtc);
        Assert.AreSame(shown, conversations.Preview(VsCode, 1024 * 1024, CancellationToken.None), "An unchanged transcript is not read again.");

        // The last two are kept: the one shown and the one before.
        var second = conversations.Preview(Terminal, 1024 * 1024, CancellationToken.None);
        Assert.AreSame(shown, conversations.Preview(VsCode, 1024 * 1024, CancellationToken.None));
        conversations.Preview(Untitled, 1024 * 1024, CancellationToken.None);
        Assert.AreNotSame(second, conversations.Preview(Terminal, 1024 * 1024, CancellationToken.None), "The oldest read was let go.");

        // A transcript that changed is read again; a larger one than asked is not read.
        File.AppendAllText(first, Transcript.Line(new JsonObject { ["type"] = "custom-title", ["customTitle"] = "Renamed", ["sessionId"] = VsCode }) + "\n");
        File.SetLastWriteTimeUtc(first, DateTime.UtcNow.AddSeconds(5));
        var changed = conversations.Preview(VsCode, 1024 * 1024, CancellationToken.None);
        Assert.AreNotSame(shown, changed);
        Assert.AreEqual("Renamed", changed.Conversation!.Conversation.Title);
        var large = conversations.Preview(VsCode, 16, CancellationToken.None);
        Assert.AreEqual((AgentExternalConversationPreviewStatus.TooLarge, null, new FileInfo(first).Length), (large.Status, large.Conversation, large.Length));

        Assert.AreEqual(AgentExternalConversationPreviewStatus.NotFound, conversations.Preview(Programmatic, 1024 * 1024, CancellationToken.None).Status);
        Assert.AreEqual(AgentExternalConversationPreviewStatus.NotFound, conversations.Preview("not-a-session", 1024 * 1024, CancellationToken.None).Status);
    }

    [TestMethod]
    public async Task ListAsync_AfterARestart_TakesTheHeadersOfUnchangedTranscriptsFromTheStore_WithoutReadingThem()
    {
        using var directory = TestTempDirectory.Create();
        var (root, project, folder) = Layout(directory);
        var written = new DateTime(2026, 10, 14, 8, 0, 0, DateTimeKind.Utc);
        var paths = new List<string>();
        foreach (var (id, entrypoint, minutes) in new[] { (VsCode, "claude-vscode", 1), (Terminal, "cli", 2), (Programmatic, "sdk-cli", 3) })
        {
            var path = Path.Combine(folder, id + ".jsonl");
            var transcript = new Transcript(path, id, project);
            transcript.Write(transcript.User("u1", null, "Hello from " + id, entrypoint), WithCwd(transcript.Assistant("a1", "u1", "m1", Text("Hi."), entrypoint), project + "-end"));
            File.SetLastWriteTimeUtc(path, written.AddMinutes(minutes));
            paths.Add(path);
        }

        await using var database = ApplicationDatabase.Create(new CatalogOptions { GlobalRoot = Path.Combine(directory.Path, "alta") });
        var store = new ExternalConversationHeaderStore(database);
        var first = new ClaudeCodeExternalConversations(root, null, store);
        var listed = await first.ListAsync(static _ => false, CancellationToken.None);
        await first.PendingStoreWrite;
        Assert.HasCount(2, listed);
        var rows = await store.ReadAsync(ClaudeCodeExternalConversations.HeaderStoreProvider);
        Assert.HasCount(3, rows!, "A transcript the listing does not give is kept too: it is not read again either.");
        Assert.IsFalse(rows!.Single(row => row.Path.EndsWith(Programmatic + ".jsonl", StringComparison.Ordinal)).Importable);

        // As at a start of the application: nothing is remembered in memory, and no transcript can be opened.
        var locks = paths.Select(static path => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)).ToList();
        try
        {
            var restarted = new ClaudeCodeExternalConversations(root, null, store);
            var again = await restarted.ListAsync(static _ => false, CancellationToken.None);
            CollectionAssert.AreEqual(listed.Select(Describe).ToArray(), again.Select(Describe).ToArray());
        }
        finally
        {
            foreach (var held in locks) held.Dispose();
        }

        static string Describe(AgentExternalConversation conversation)
            => $"{conversation.ConversationId}|{conversation.Title}|{conversation.LastChangedAt:O}|{conversation.Origin}|{conversation.Entrypoint}|{conversation.StartDirectory}|{conversation.EndDirectory}";
    }

    [TestMethod]
    public async Task ListAsync_ReadsAgainTheTranscriptsThatChanged_AndRemovesTheRowsOfThoseThatAreGone()
    {
        using var directory = TestTempDirectory.Create();
        var (root, project, folder) = Layout(directory);
        var written = new DateTime(2026, 10, 14, 8, 0, 0, DateTimeKind.Utc);
        string Write(string id, int minutes)
        {
            var path = Path.Combine(folder, id + ".jsonl");
            var transcript = new Transcript(path, id, project);
            transcript.Write(transcript.User("u1", null, "Hello."), transcript.Assistant("a1", "u1", "m1", Text("Hi.")), Title(id, "Title of " + id));
            File.SetLastWriteTimeUtc(path, written.AddMinutes(minutes));
            return path;
        }

        var vsCode = Write(VsCode, 1);
        var terminal = Write(Terminal, 2);
        var untouched = Write(Continuation, 3);
        await using var database = ApplicationDatabase.Create(new CatalogOptions { GlobalRoot = Path.Combine(directory.Path, "alta") });
        var store = new ExternalConversationHeaderStore(database);
        var first = new ClaudeCodeExternalConversations(root, null, store);
        await first.ListAsync(static _ => false, CancellationToken.None);
        await first.PendingStoreWrite;

        // A transcript that grew, one that is gone, and a title given in VS Code beside one that did not change.
        File.AppendAllText(terminal, Title(Terminal, "Renamed in the terminal") + "\n");
        File.SetLastWriteTimeUtc(terminal, written.AddMinutes(10));
        File.Delete(vsCode);
        Directory.CreateDirectory(Path.Combine(folder, Continuation));
        File.WriteAllText(Path.Combine(folder, Continuation, "custom-title.json"), "{\"customTitle\":\"Named in VS Code\"}");

        var restarted = new ClaudeCodeExternalConversations(root, null, store);
        var listed = await restarted.ListAsync(static _ => false, CancellationToken.None);
        await restarted.PendingStoreWrite;
        CollectionAssert.AreEqual(new[] { (Terminal, "Renamed in the terminal"), (Continuation, "Named in VS Code") },
            listed.Select(static conversation => (conversation.ConversationId, conversation.Title)).ToArray(),
            "The title VS Code keeps beside a transcript that did not change is read too.");
        var rows = (await store.ReadAsync(ClaudeCodeExternalConversations.HeaderStoreProvider))!.ToDictionary(static row => row.Path);
        CollectionAssert.AreEquivalent(new[] { terminal, untouched }, rows.Keys.ToArray(), "The row of the transcript that is gone is removed.");
        Assert.AreEqual(new FileInfo(terminal).Length, rows[terminal].Length);
        Assert.AreEqual(written.AddMinutes(10).Ticks, rows[terminal].LastWriteUtcTicks);
    }

    [TestMethod]
    public async Task ListAsync_ReadsTheTranscriptsAgain_WhenTheFormatOfTheKeptHeadersChanges()
    {
        using var directory = TestTempDirectory.Create();
        var (root, project, folder) = Layout(directory);
        var path = Path.Combine(folder, VsCode + ".jsonl");
        var transcript = new Transcript(path, VsCode, project);
        transcript.Write(transcript.User("u1", null, "Hello."), transcript.Assistant("a1", "u1", "m1", Text("Hi.")), Title(VsCode, "Fix the parser"));
        var stamp = new DateTime(2026, 10, 14, 8, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, stamp);
        await using var database = ApplicationDatabase.Create(new CatalogOptions { GlobalRoot = Path.Combine(directory.Path, "alta") });
        var store = new ExternalConversationHeaderStore(database);
        var first = new ClaudeCodeExternalConversations(root, null, store);
        Assert.AreEqual("Fix the parser", (await first.ListAsync(static _ => false, CancellationToken.None)).Single().Title);
        await first.PendingStoreWrite;

        // The same length and the same last write time: only a new format of the headers reads it again.
        File.WriteAllText(path, File.ReadAllText(path).Replace("Fix the parser", "Fix the router", StringComparison.Ordinal));
        File.SetLastWriteTimeUtc(path, stamp);
        Assert.AreEqual("Fix the parser", (await new ClaudeCodeExternalConversations(root, null, store)
            .ListAsync(static _ => false, CancellationToken.None)).Single().Title, "The header kept is taken.");
        var newer = new ClaudeCodeExternalConversations(root, null, store, ClaudeCodeExternalConversations.HeaderFormatVersion + 1);
        Assert.AreEqual("Fix the router", (await newer.ListAsync(static _ => false, CancellationToken.None)).Single().Title);
        await newer.PendingStoreWrite;
        Assert.AreEqual(ClaudeCodeExternalConversations.HeaderFormatVersion + 1,
            (await store.ReadAsync(ClaudeCodeExternalConversations.HeaderStoreProvider))!.Single().FormatVersion);
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

    private static string WithCwd(string line, string cwd) => With(line, "cwd", cwd);

    private static string With(string line, string name, JsonNode value)
    {
        var node = JsonNode.Parse(line)!.AsObject();
        node[name] = value;
        return node.ToJsonString();
    }
}
