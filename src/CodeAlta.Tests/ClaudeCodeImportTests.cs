using System.Text.Json.Nodes;
using CodeAlta.Agent;
using CodeAlta.Agent.Claude;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;
using Transcript = CodeAlta.Tests.ClaudeCodeExternalTurnsTests.Transcript;

namespace CodeAlta.Tests;

/// <summary>
/// The import of a Claude Code session started in VS Code: a new session of the project holds its turns as turns made
/// there, and its first prompt resumes the conversation of the CLI with the instructions of CodeAlta, given as new.
/// </summary>
[TestClass]
public sealed class ClaudeCodeImportTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private const string Conversation = "5b0c8b0e-3f7c-4c1e-9a55-6d1f0b2c3e01";

    [TestMethod]
    public async Task Import_WritesASessionOfTheProject_WhoseFirstPromptResumesTheConversation_WithTheInstructionsGivenAsNew()
    {
        using var directory = TestTempDirectory.Create();
        var (cli, projectRoot) = Prepare(directory);
        WriteVsCodeConversation(cli, projectRoot, Conversation);
        await using var host = await CreateHostAsync(directory, cli, projectRoot);
        var project = await host.ProjectCatalog.EnsurePersistedAsync(host.CurrentProject);
        var provider = Provider(host);

        var listed = await host.Commands.ListExternalConversationsAsync(project, provider, 0, 50);
        Assert.AreEqual(1, listed.Total);
        var entry = listed.Conversations.Single();
        Assert.AreEqual((Conversation, "Fix the parser", "vscode", false), (entry.ConversationId, entry.Title, entry.Origin, entry.IsHeld));
        // Before it is imported, the conversation opens in VS Code or a terminal from the folder of the project.
        var described = await host.Commands.DescribeExternalConversationAsync(project, provider, Conversation).WaitAsync(Timeout);
        Assert.AreEqual(("ok", $"claude --resume {Conversation}", project.ProjectPath, $"vscode://anthropic.claude-code/open?session={Conversation}"),
            (described.Status, described.Conversation!.ResumeCommand, described.Conversation.WorkingDirectory, described.Conversation.EditorUri));
        Assert.AreEqual("not_found", (await host.Commands.DescribeExternalConversationAsync(project, provider, Guid.NewGuid().ToString("D")).WaitAsync(Timeout)).Status);

        var imported = await host.Commands.ImportExternalConversationAsync(project, provider, Conversation).WaitAsync(Timeout);
        Assert.AreEqual("imported", imported.Status);
        Assert.AreEqual(("existing", null), await DescribedAsync(host, project, provider, Conversation), "A session continues it: it is opened from that session.");
        var sessionId = imported.Session!.SessionId;

        // A session of the project, in its folder, titled as VS Code titles it, with the provider and its default model.
        var session = await ListedAsync(host, sessionId);
        Assert.AreEqual(SessionViewKind.ProjectSession, session.Kind);
        Assert.AreEqual(project.Id, session.ProjectRef);
        Assert.AreEqual(project.ProjectPath, session.WorkingDirectory);
        Assert.AreEqual("Fix the parser", session.Title);
        Assert.AreEqual(provider.ProviderId.Value, session.ResolvedProviderKey);

        // Its turns are those of VS Code, shown as made there, and are not usage of CodeAlta.
        var store = host.SessionViewCatalog.JournalStore.CreateSessionStore();
        var turns = (await store.ReadTurnsAsync(sessionId)).Turns;
        CollectionAssert.AreEqual(new[] { "Read the file.", "Now add a test." }, turns.Select(static turn => turn.PromptPreview).ToArray());
        Assert.IsTrue(turns.All(static turn => turn.MadeElsewhere && turn.PromptContentId!.StartsWith("external:vscode:", StringComparison.Ordinal)));
        var history = (await host.RuntimeService.TryReadStoredHistoryAsync(session))!;
        Assert.IsTrue(history.Where(static e => e.RunId is not null).All(static e => e.RunId!.Value.Value.StartsWith(AgentSessionTurn.ExternalRunPrefix, StringComparison.Ordinal)),
            "Every record of a turn is under a run made elsewhere, which statistics do not count.");
        Assert.IsFalse(history.OfType<AgentSessionUpdateEvent>().Any(static update => update.Usage is not null), "No usage is recorded.");
        Assert.IsNull((await store.GetSessionSummaryAsync(sessionId))!.Usage);
        Assert.AreEqual("Added a test.", (await store.GetSessionSummaryAsync(sessionId))!.Summary);

        var state = (await store.GetStateAsync(sessionId))!;
        Assert.AreEqual(Conversation, state.ProviderSessionId);
        var recorded = ClaudeCodeProviderState.Read(state)!;
        Assert.AreEqual((Conversation, 2, 3, "a3"), (recorded.SessionId, recorded.Users, recorded.Assistants, recorded.Leaf));
        Assert.AreEqual(0.42, recorded.Cost!.Value, 1e-9);
        Assert.IsTrue(recorded.Imported);
        Assert.IsNull(recorded.Instructions);

        // Imported twice: the session there is, and the listing no longer gives it.
        var again = await host.Commands.ImportExternalConversationAsync(project, provider, Conversation).WaitAsync(Timeout);
        Assert.AreEqual(("existing", sessionId), (again.Status, again.Session!.SessionId));
        Assert.AreEqual(0, (await host.Commands.ListExternalConversationsAsync(project, provider, 0, 50)).Total);

        // The first prompt resumes the conversation of the CLI: only its prompt is sent, after the instructions of
        // CodeAlta given as new, without the conversation told again as text.
        var receipt = host.Commands.AdmitSend(new(Guid.NewGuid().ToString("N"), sessionId, "Run the tests.")).Receipt;
        Assert.IsNotNull(receipt);
        var result = await receipt.Completion.WaitAsync(Timeout);
        Assert.AreEqual(OwnedSessionCommandOutcome.Completed, result.Outcome, result.Code);
        var process = cli.Processes.Last(static candidate => candidate.Launch.Arguments.Contains("--mcp-config"));
        Assert.AreEqual(Conversation, process.ResumedSessionId);
        Assert.IsFalse(process.ForksSession);
        var sent = ClaudeCodeFakeProcess.UserText(process.UserMessages.Single());
        StringAssert.StartsWith(sent, "<codealta_instructions_update>");
        StringAssert.Contains(sent, "You were not given the instructions of CodeAlta before");
        StringAssert.Contains(sent, "# CodeAlta");
        Assert.IsFalse(sent.Contains("<codealta_previous_conversation>", StringComparison.Ordinal), sent);
        StringAssert.EndsWith(sent, "Run the tests.");

        var after = ClaudeCodeProviderState.Read((await store.GetStateAsync(sessionId))!)!;
        Assert.IsFalse(after.Imported, "Given once, the instructions are updated as for any conversation.");
        Assert.IsNotNull(after.Instructions);
        Assert.AreEqual((3, 4), (after.Users, after.Assistants));
    }

    [TestMethod]
    public async Task Import_OfAConversationTheListingDoesNotGive_WritesNothing()
    {
        using var directory = TestTempDirectory.Create();
        var (cli, projectRoot) = Prepare(directory);
        var path = Path.Combine(ClaudeCodeProjectFolders.GetProjectFolder(cli.ConfigRoot, projectRoot), Conversation + ".jsonl");
        var programmatic = new Transcript(path, Conversation, projectRoot);
        programmatic.Write(programmatic.User("u1", null, "From the SDK.", "sdk-cli"), programmatic.Assistant("a1", "u1", "m1", ClaudeCodeExternalTurnsTests.Text("Done."), "sdk-cli"));
        await using var host = await CreateHostAsync(directory, cli, projectRoot);
        var project = await host.ProjectCatalog.EnsurePersistedAsync(host.CurrentProject);

        Assert.AreEqual(0, (await host.Commands.ListExternalConversationsAsync(project, Provider(host), 0, 50)).Total);
        var imported = await host.Commands.ImportExternalConversationAsync(project, Provider(host), Conversation).WaitAsync(Timeout);
        Assert.AreEqual(("not_found", null), (imported.Status, imported.Session));
        await foreach (var session in host.RuntimeService.ListRecoverableSessionsAsync())
            Assert.Fail($"No session is written; found {session.SessionId}.");
    }

    private static (ClaudeCodeFakeCli Cli, string ProjectRoot) Prepare(TestTempDirectory directory)
    {
        var projectRoot = Directory.CreateDirectory(Path.Combine(directory.Path, "project")).FullName;
        Directory.CreateDirectory(Path.Combine(directory.Path, "global"));
        return (new ClaudeCodeFakeCli { ConfigRoot = Path.Combine(directory.Path, "claude") }, projectRoot);
    }

    // A conversation of VS Code: a turn with a tool call, then another one; a title, and what it cost.
    private static void WriteVsCodeConversation(ClaudeCodeFakeCli cli, string projectRoot, string conversation)
    {
        var transcript = new Transcript(Path.Combine(ClaudeCodeProjectFolders.GetProjectFolder(cli.ConfigRoot, projectRoot), conversation + ".jsonl"),
            conversation, projectRoot);
        transcript.Write(
            transcript.User("u1", null, "Read the file."),
            transcript.Assistant("a1", "u1", "m1", ClaudeCodeExternalTurnsTests.ToolUse("toolu_1", "Read", new JsonObject { ["file_path"] = Path.Combine(projectRoot, "a.txt") })),
            transcript.ToolResult("r1", "a1", "toolu_1", "contents"),
            transcript.Assistant("a2", "r1", "m2", ClaudeCodeExternalTurnsTests.Text("It says contents.")),
            transcript.User("u2", "a2", "Now add a test."),
            transcript.Assistant("a3", "u2", "m3", ClaudeCodeExternalTurnsTests.Text("Added a test.")),
            Transcript.Line(new JsonObject { ["type"] = "ai-title", ["aiTitle"] = "Fix the parser", ["sessionId"] = conversation }),
            Transcript.Line(new JsonObject { ["type"] = "cost-state", ["sessionId"] = conversation, ["totalCostUSD"] = 0.42 }));
        // The CLI finds it where the session resumes it.
        cli.WriteTranscript(conversation);
    }

    private static Task<CodeAltaHost> CreateHostAsync(TestTempDirectory directory, ClaudeCodeFakeCli cli, string projectRoot)
    {
        var options = cli.CreateOptions();
        return CodeAltaHost.CreateAsync(
            new CodeAltaHostOptions
            {
                GlobalRoot = Path.Combine(directory.Path, "global"),
                CurrentProjectPath = projectRoot,
                IsHeadless = true,
                HasInteractiveUi = false,
                StartPlugins = false,
                AutoApproveOwnedPermissions = true,
                ConfigureModelProviders = registry => registry.RegisterOrReplace(
                    ClaudeCodeModelProviderRuntime.CreateDescriptor(options),
                    () => new ClaudeCodeModelProviderRuntime(options)),
            });
    }

    private static ModelProviderDescriptor Provider(CodeAltaHost host)
        => host.ModelProviderRegistry.ListProviders().Single(static provider => provider.SharesConversations);

    private static async Task<(string Status, OwnedSharedConversation? Conversation)> DescribedAsync(CodeAltaHost host, ProjectDescriptor project,
        ModelProviderDescriptor provider, string conversation)
    {
        var described = await host.Commands.DescribeExternalConversationAsync(project, provider, conversation).WaitAsync(Timeout);
        return (described.Status, described.Conversation);
    }

    private static async Task<SessionViewDescriptor> ListedAsync(CodeAltaHost host, string sessionId)
    {
        await foreach (var session in host.RuntimeService.ListRecoverableSessionsAsync())
        {
            if (session.SessionId == sessionId) return session;
        }

        Assert.Fail($"Session '{sessionId}' is not listed.");
        return null!;
    }
}
