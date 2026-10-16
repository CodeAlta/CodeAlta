using System.Collections.Frozen;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// The workspace calls of the Claude Code sessions started in VS Code or a terminal: the listing of an exact project and
/// the import of one, with the provider that shares its conversations.
/// </summary>
[TestClass]
public sealed class WorkspaceClaudeCodeSessionsTests
{
    private const string Epoch = "11111111-1111-4111-8111-111111111111";
    private const string Cli = "0f5c2c1e-7a0e-4a52-9d55-2a8d2b1c0e11";
    private const string Other2 = "0f5c2c1e-7a0e-4a52-9d55-2a8d2b1c0e12";
    private const string Other3 = "0f5c2c1e-7a0e-4a52-9d55-2a8d2b1c0e13";
    private static readonly ModelProviderDescriptor Other = new(new("other-fixture"), "Other") { IsDefault = true };
    private static readonly ModelProviderDescriptor Claude = new(new("claude-fixture"), "Claude Code", "claude-code") { SharesConversations = true };
    private static readonly ModelProviderDescriptor Home = new(new("claude-home"), "Claude Code (home)", "claude-code") { SharesConversations = true };

    [TestMethod]
    public async Task Listing_IsOfTheExactProject_WithTheProviderThatSharesItsConversations()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-claude-sessions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var host = await CreateHostAsync(root);
            var projectPath = Path.Combine(root, "project");
            var project = await host.ProjectCatalog.UpsertFromPathAsync(projectPath);
            var asked = new List<(string ProjectId, string Provider, int Offset, int Count)>();
            var changed = new DateTimeOffset(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);
            var reply = new ExternalConversationListing(
                [new ExternalConversationSummary(Cli, "Fix the parser", changed, "vscode", "claude-vscode", true, "busy"),
                 new ExternalConversationSummary(Other2, "Review", changed, "terminal", "cli", false, "busy"),
                 new ExternalConversationSummary(Other3, "Plan", changed, "vscode", "claude-vscode", true, "Not A Status!")], 67);
            Func<ProjectDescriptor, ModelProviderDescriptor, int, int, CancellationToken, Task<ExternalConversationListing>> list = (target, provider, offset, count, _) =>
            {
                asked.Add((target.Id, provider.ProviderId.Value, offset, count));
                return Task.FromResult(reply);
            };
            var service = Service(host, list, static (_, _, _, _) => throw new InvalidOperationException("No import."));
            WorkspaceClaudeCodeSessionsRequest Request() => new(Epoch, project.Id, projectPath, 20, 50);

            var listed = await service.ClaudeCodeSessionsAsync(Request(), CancellationToken.None);
            Assert.AreEqual(("ok", "claude-fixture", 20, 67), (listed.Status, listed.ProviderId, listed.Offset, listed.Total));
            // What the holder does is said only of a held session, and only as a short word of the registry.
            CollectionAssert.AreEqual(new[]
            {
                new WorkspaceClaudeCodeSession(Cli, "Fix the parser", changed, "vscode", true, "busy"),
                new WorkspaceClaudeCodeSession(Other2, "Review", changed, "terminal", false, null),
                new WorkspaceClaudeCodeSession(Other3, "Plan", changed, "vscode", true, null),
            }, listed.Sessions.ToArray());
            Assert.AreEqual((project.Id, "claude-fixture", 20, 50), asked.Single(),
                "The default provider of the project does not share its conversations: the one that does is asked, for the page asked.");
            Assert.AreEqual(listed.Total, JsonSerializer.Deserialize(JsonSerializer.Serialize(listed, DesktopJsonContext.Default.WorkspaceClaudeCodeSessionsResponse),
                DesktopJsonContext.Default.WorkspaceClaudeCodeSessionsResponse)!.Total);

            Assert.AreEqual("stale_epoch", (await service.ClaudeCodeSessionsAsync(Request() with { ExpectedHostEpoch = Guid.NewGuid().ToString("D") }, CancellationToken.None)).Status);
            Assert.AreEqual("invalid_scope", (await service.ClaudeCodeSessionsAsync(Request() with { ProjectPath = "" }, CancellationToken.None)).Status);
            Assert.AreEqual("project_missing", (await service.ClaudeCodeSessionsAsync(Request() with { ProjectPath = root }, CancellationToken.None)).Status);
            // A page is from 1 to 50 sessions, from a place that is not negative.
            foreach (var (offset, count) in new[] { (-1, 20), (0, 0), (0, 51), (100_001, 50) })
                Assert.AreEqual("invalid_scope", (await service.ClaudeCodeSessionsAsync(Request() with { Offset = offset, Count = count }, CancellationToken.None)).Status, $"{offset}+{count}");
            Assert.AreEqual(1, asked.Count, "Nothing is read for a request that is refused.");
            Assert.AreEqual("unconfigured", (await new WorkspaceService(host.WorkspaceReads, host.ProjectCatalog, Epoch).ClaudeCodeSessionsAsync(Request(), CancellationToken.None)).Status);

            var failing = Service(host, static (_, _, _, _, _) => throw new IOException("unreadable"), static (_, _, _, _) => throw new InvalidOperationException());
            Assert.AreEqual("read_failed", (await failing.ClaudeCodeSessionsAsync(Request(), CancellationToken.None)).Status);

            // Without a provider that shares its conversations, there is nothing to list, and nothing is read.
            var without = new ModelProviderRegistry();
            without.RegisterOrReplace(Other, static () => throw new InvalidOperationException("Not started."));
            var none = new WorkspaceService(host.WorkspaceReads, host.ProjectCatalog, Epoch, without, list, static (_, _, _, _) => throw new InvalidOperationException(),
                static (_, _, _, _) => throw new InvalidOperationException(), static _ => throw new InvalidOperationException("Nothing is opened."));
            Assert.AreEqual(("no_provider", 0), ((await none.ClaudeCodeSessionsAsync(Request(), CancellationToken.None)).Status, asked.Count - 1));
            await service.CloseSessionsAsync();
            await service.CloseImportsAsync();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task Import_GivesTheSessionThatContinuesTheConversation_OneAtATime()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-claude-sessions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var host = await CreateHostAsync(root);
            var projectPath = Path.Combine(root, "project");
            var project = await host.ProjectCatalog.UpsertFromPathAsync(projectPath);
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var answers = new Queue<Func<ExternalConversationImportResult>>();
            var imported = new List<(string ProjectId, string Provider, string Conversation)>();
            var service = Service(host, static (_, _, _, _, _) => Task.FromResult(new ExternalConversationListing([], 0)), async (target, provider, conversation, _) =>
            {
                imported.Add((target.Id, provider.ProviderId.Value, conversation));
                await gate.Task;
                return answers.Dequeue()();
            });
            WorkspaceImportClaudeCodeSessionRequest Request() => new(Epoch, project.Id, projectPath, Cli);
            SessionViewDescriptor Session(string id) => new() { SessionId = id, Kind = SessionViewKind.ProjectSession, ProjectRef = project.Id, WorkingDirectory = projectPath };

            // One import at a time: a second one while the first runs is refused, not queued.
            answers.Enqueue(() => new("imported", Session("s-new")));
            var first = service.ImportClaudeCodeSessionAsync(Request(), CancellationToken.None);
            Assert.AreEqual("busy", (await service.ImportClaudeCodeSessionAsync(Request(), CancellationToken.None)).Status);
            gate.TrySetResult();
            var opened = await first;
            Assert.AreEqual(("ok", "s-new", Cli), (opened.Status, opened.SessionId, opened.CliSessionId));
            Assert.AreEqual((project.Id, "claude-fixture", Cli), imported.Single());
            Assert.AreEqual(opened.SessionId, JsonSerializer.Deserialize(JsonSerializer.Serialize(opened, DesktopJsonContext.Default.WorkspaceImportClaudeCodeSessionResponse),
                DesktopJsonContext.Default.WorkspaceImportClaudeCodeSessionResponse)!.SessionId);

            answers.Enqueue(() => new("existing", Session("s-old")));
            Assert.AreEqual(("existing", "s-old"), await StatusAsync(service, Request()));
            answers.Enqueue(() => new("not_found", null));
            Assert.AreEqual(("not_found", null), await StatusAsync(service, Request()));
            // Written or not, a failure after the import began is not a refusal.
            answers.Enqueue(() => throw new IOException("disk full"));
            Assert.AreEqual(("import_unconfirmed", null), await StatusAsync(service, Request()));

            Assert.AreEqual("invalid_scope", (await service.ImportClaudeCodeSessionAsync(Request() with { CliSessionId = "../escape" }, CancellationToken.None)).Status);
            Assert.AreEqual("stale_epoch", (await service.ImportClaudeCodeSessionAsync(Request() with { ExpectedHostEpoch = Guid.NewGuid().ToString("D") }, CancellationToken.None)).Status);
            Assert.AreEqual("project_missing", (await service.ImportClaudeCodeSessionAsync(Request() with { ProjectId = "missing" }, CancellationToken.None)).Status);
            Assert.AreEqual(4, imported.Count, "Nothing is imported for a request that is refused.");

            await service.CloseSessionsAsync();
            Assert.AreEqual("closed", (await service.ImportClaudeCodeSessionAsync(Request(), CancellationToken.None)).Status);
            await service.CloseImportsAsync();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task Import_RightAfterAReply_IsNeverRefusedBusy()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-claude-sessions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var host = await CreateHostAsync(root);
            var projectPath = Path.Combine(root, "project");
            var project = await host.ProjectCatalog.UpsertFromPathAsync(projectPath);
            // Inline answers resume this test before the worker that answered has ended: the slot must already be free.
            var service = new WorkspaceService(host.WorkspaceReads, host.ProjectCatalog, Epoch, host.ModelProviderRegistry,
                static (_, _, _, _, _) => Task.FromResult(new ExternalConversationListing([], 0)),
                async (_, _, _, _) =>
                {
                    await Task.Yield();
                    return new("imported", new SessionViewDescriptor { SessionId = "s-new", Kind = SessionViewKind.ProjectSession, ProjectRef = project.Id, WorkingDirectory = projectPath });
                },
                static (_, _, _, _) => throw new InvalidOperationException("Nothing is described."), static _ => throw new InvalidOperationException("Nothing is opened."))
            {
                CatalogReplyOptions = TaskCreationOptions.None,
            };
            for (var i = 0; i < 200; i++)
            {
                Assert.AreEqual(("ok", "s-new"), await StatusAsync(service, new(Epoch, project.Id, projectPath, Cli)), $"import {i}");
            }

            await service.CloseSessionsAsync();
            await service.CloseImportsAsync();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task Import_KeepsItsProject_TheProjectIsNeitherDeletedNorArchivedWhileItRuns()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-claude-sessions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var host = await CreateHostAsync(root);
            var projectPath = Path.Combine(root, "project");
            var project = await host.ProjectCatalog.UpsertFromPathAsync(projectPath);
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var deleted = new List<string>();
            var service = new WorkspaceService(host.WorkspaceReads, host.ProjectCatalog, Epoch, host.ModelProviderRegistry,
                static (_, _, _, _, _) => Task.FromResult(new ExternalConversationListing([], 0)),
                async (_, _, _, _) =>
                {
                    await gate.Task;
                    return new ExternalConversationImportResult("imported",
                        new() { SessionId = "s-new", Kind = SessionViewKind.ProjectSession, ProjectRef = project.Id, WorkingDirectory = projectPath });
                },
                static (_, _, _, _) => throw new InvalidOperationException("Nothing is described."),
                static _ => throw new InvalidOperationException("Nothing is opened."),
                deleteProject: (id, _, _, _) => { deleted.Add(id); return Task.FromResult("ok"); });

            // A large conversation is imported: the project it writes a session into stays until the session is written.
            var importing = service.ImportClaudeCodeSessionAsync(new(Epoch, project.Id, projectPath, Cli), CancellationToken.None);
            Assert.AreEqual("busy", (await service.DeleteProjectAsync(new(Epoch, project.Id, projectPath, project.DisplayName, false, true), CancellationToken.None)).Status);
            Assert.AreEqual("busy", (await service.ArchiveProjectAsync(new(Epoch, project.Id, projectPath, false, true, false, null, null), CancellationToken.None)).Status);
            Assert.IsEmpty(deleted);
            gate.TrySetResult();
            Assert.AreEqual("ok", (await importing).Status);

            // Once the session is written, the project can go.
            Assert.AreEqual("ok", (await service.DeleteProjectAsync(new(Epoch, project.Id, projectPath, project.DisplayName, false, true), CancellationToken.None)).Status);
            CollectionAssert.AreEqual(new[] { project.Id }, deleted);
            await service.CloseSessionsAsync();
            await service.CloseImportsAsync();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task TheProvider_IsOneOfTheSpaceThePageShows_AndTheImportedSessionBelongsToThatSpace()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-claude-sessions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var host = await CreateHostAsync(root);
            host.ModelProviderRegistry.RegisterOrReplace(Home, static () => throw new InvalidOperationException("Not started."));
            var projectPath = Path.Combine(root, "project");
            var project = await host.ProjectCatalog.UpsertFromPathAsync(projectPath);
            var business = (await host.SpaceCatalog.CreateAsync("Business")).Space!.Id;
            var personal = (await host.SpaceCatalog.CreateAsync("Personal")).Space!.Id;
            // One Claude Code provider of the business space, another of the default space, none of the personal one.
            File.WriteAllText(host.CatalogOptions.ConfigPath, $"""
                [providers.claude-fixture]
                type = "claude-code"
                spaces = ["{business}"]

                [providers.claude-home]
                type = "claude-code"
                spaces = ["{SpaceDescriptor.DefaultId}"]
                """);
            var asked = new List<string>();
            var imported = new List<(string Provider, string? Space)>();
            var importAnswer = "imported";
            SessionViewDescriptor Session(string id) => new() { SessionId = id, Kind = SessionViewKind.ProjectSession, ProjectRef = project.Id, WorkingDirectory = projectPath };
            var service = new WorkspaceService(host.WorkspaceReads, host.ProjectCatalog, Epoch, host.ModelProviderRegistry,
                (_, provider, _, _, _) => { asked.Add(provider.ProviderId.Value); return Task.FromResult(new ExternalConversationListing([], 0)); },
                (_, provider, _, space) =>
                {
                    imported.Add((provider.ProviderId.Value, space));
                    return Task.FromResult(new ExternalConversationImportResult(importAnswer, importAnswer == "imported" ? Session("s-new") : null));
                },
                (_, provider, _, _) => { asked.Add(provider.ProviderId.Value); return Task.FromResult(new OwnedSharedConversationResult("not_found", null)); },
                static _ => throw new InvalidOperationException("Nothing is opened."),
                (_, provider, _, _) => { asked.Add(provider.ProviderId.Value); return Task.FromResult(new ExternalConversationPreviewResult("not_found", null, null)); },
                new DesktopDefaultProvider(host.CatalogOptions));

            async Task<string?> ListedBy(string? space)
                => (await service.ClaudeCodeSessionsAsync(new(Epoch, project.Id, projectPath, 0, 20, space), CancellationToken.None)).ProviderId;
            Assert.AreEqual("claude-fixture", await ListedBy(business));
            Assert.AreEqual("claude-home", await ListedBy(SpaceDescriptor.DefaultId));
            Assert.AreEqual("claude-home", await ListedBy(null), "No space is the default one.");
            CollectionAssert.AreEqual(new[] { "claude-fixture", "claude-home", "claude-home" }, asked);

            // A space without a Claude Code provider has nothing to list, describe, show or import, and nothing is asked.
            asked.Clear();
            Assert.AreEqual("no_provider", (await service.ClaudeCodeSessionsAsync(new(Epoch, project.Id, projectPath, 0, 20, personal), CancellationToken.None)).Status);
            Assert.AreEqual("no_provider", (await service.ClaudeCodeSessionConversationAsync(new(Epoch, project.Id, projectPath, Cli, personal), CancellationToken.None)).Status);
            Assert.AreEqual("no_provider", (await service.ClaudeCodeSessionPreviewAsync(new(Epoch, project.Id, projectPath, Cli, null, personal), CancellationToken.None)).Status);
            Assert.AreEqual("no_provider", (await service.ImportClaudeCodeSessionAsync(new(Epoch, project.Id, projectPath, Cli, personal), CancellationToken.None)).Status);
            Assert.IsEmpty(asked);
            Assert.IsEmpty(imported);
            Assert.AreEqual("not_found", (await service.ClaudeCodeSessionConversationAsync(new(Epoch, project.Id, projectPath, Cli, business), CancellationToken.None)).Status);
            Assert.AreEqual("not_found", (await service.ClaudeCodeSessionPreviewAsync(new(Epoch, project.Id, projectPath, Cli, null, business), CancellationToken.None)).Status);
            CollectionAssert.AreEqual(new[] { "claude-fixture", "claude-fixture" }, asked);

            // The import gives the session the provider and the space the page shows.
            Assert.AreEqual(("ok", "s-new"), await StatusAsync(service, new(Epoch, project.Id, projectPath, Cli, business)));
            Assert.AreEqual(("claude-fixture", business), imported.Single());
            importAnswer = SessionRuntimeService.ProviderOutsideSpace;
            Assert.AreEqual((SessionRuntimeService.ProviderOutsideSpace, null), await StatusAsync(service, new(Epoch, project.Id, projectPath, Cli, business)),
                "The provider left the space meanwhile: nothing is imported.");
            Assert.AreEqual("space_missing", (await service.ImportClaudeCodeSessionAsync(new(Epoch, project.Id, projectPath, Cli, "gone"), CancellationToken.None)).Status);
            Assert.AreEqual("invalid_scope", (await service.ImportClaudeCodeSessionAsync(new(Epoch, project.Id, projectPath, Cli, "Not a space"), CancellationToken.None)).Status);
            Assert.AreEqual("invalid_scope", (await service.ClaudeCodeSessionsAsync(new(Epoch, project.Id, projectPath, 0, 20, "Not a space"), CancellationToken.None)).Status);
            Assert.AreEqual(2, imported.Count);
            await service.CloseSessionsAsync();
            await service.CloseImportsAsync();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task DiscoveredConversation_IsDescribedAndOpenedInVsCode_OnlyAsTheHostFoundItInTheFolderOfTheProject()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-claude-sessions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var host = await CreateHostAsync(root);
            var projectPath = Path.Combine(root, "project");
            var project = await host.ProjectCatalog.UpsertFromPathAsync(projectPath);
            var asked = new List<(string ProjectId, string Provider, string Conversation)>();
            var answers = new Queue<OwnedSharedConversationResult>();
            var opened = new List<string>();
            var openWorks = true;
            var service = new WorkspaceService(host.WorkspaceReads, host.ProjectCatalog, Epoch, host.ModelProviderRegistry,
                static (_, _, _, _, _) => Task.FromResult(new ExternalConversationListing([], 0)),
                static (_, _, _, _) => throw new InvalidOperationException("No import."),
                (target, provider, conversation, _) =>
                {
                    asked.Add((target.Id, provider.ProviderId.Value, conversation));
                    return Task.FromResult(answers.Dequeue());
                },
                address => { opened.Add(address); return openWorks; });
            WorkspaceClaudeCodeSessionRequest Request() => new(Epoch, project.Id, projectPath, Cli);
            OwnedSharedConversation Found(string? uri = null, string? command = null, string? folder = null, string id = Cli, string? account = null)
                => new(id, command ?? $"claude --resume {id}", folder ?? projectPath, uri ?? $"vscode://anthropic.claude-code/open?session={id}",
                    new AgentConversationHolder(AgentConversationHolderOrigin.VsCode, "VS Code", 4242, "claude-vscode", "interactive", "busy", projectPath, null))
                { ConfigDirectory = account };

            answers.Enqueue(new("ok", Found()));
            var described = await service.ClaudeCodeSessionConversationAsync(Request(), CancellationToken.None);
            Assert.AreEqual(("ok", Cli, $"claude --resume {Cli}", projectPath, "vsCode", "busy"),
                (described.Status, described.CliSessionId, described.ResumeCommand, described.WorkingDirectory, described.Held!.Origin, described.Held.Status));
            Assert.AreEqual((project.Id, "claude-fixture", Cli), asked.Single(), "The provider that shares its conversations describes it, for the project.");
            var wire = JsonSerializer.Serialize(described, DesktopJsonContext.Default.WorkspaceClaudeCodeSessionConversationResponse);
            Assert.DoesNotContain("vscode://", wire, "The page never receives the address.");

            Assert.IsNull(described.ConfigDir);

            // A provider with an account folder of its own: the page sets CLAUDE_CONFIG_DIR to it in the command, and
            // VS Code, whose link names a session only, would look for it in the folder of its own account.
            var account = Path.Combine(root, "claude-work");
            answers.Enqueue(new("ok", Found(account: account)));
            var inAccount = await service.ClaudeCodeSessionConversationAsync(Request(), CancellationToken.None);
            Assert.AreEqual(("ok", account, OperatingSystem.IsWindows()), (inAccount.Status, inAccount.ConfigDir, inAccount.Windows));
            answers.Enqueue(new("ok", Found(account: account)));
            Assert.AreEqual("account_folder", (await service.OpenClaudeCodeSessionInVsCodeAsync(Request(), CancellationToken.None)).Status);
            Assert.IsEmpty(opened);
            answers.Enqueue(new("ok", Found(account: "relative")));
            Assert.AreEqual("unsupported", (await service.ClaudeCodeSessionConversationAsync(Request(), CancellationToken.None)).Status);

            answers.Enqueue(new("ok", Found()));
            Assert.AreEqual("opened", (await service.OpenClaudeCodeSessionInVsCodeAsync(Request(), CancellationToken.None)).Status);
            CollectionAssert.AreEqual(new[] { $"vscode://anthropic.claude-code/open?session={Cli}" }, opened, "The address the host built, nothing to release first.");
            openWorks = false;
            answers.Enqueue(new("ok", Found()));
            Assert.AreEqual("open_failed", (await service.OpenClaudeCodeSessionInVsCodeAsync(Request(), CancellationToken.None)).Status);

            // What the host does not find, or finds continued by a session, is not opened; nor is what is not a conversation of the CLI.
            opened.Clear();
            foreach (var (answer, status) in new (OwnedSharedConversationResult, string)[]
            {
                (new("not_found", null), "not_found"),
                (new("existing", null), "existing"),
                (new("ok", Found(uri: "https://example.com/" + Cli)), "unsupported"),
                (new("ok", Found(command: "claude --resume " + Cli + " && calc")), "unsupported"),
                (new("ok", Found(folder: root)), "unsupported"),
                (new("ok", Found(id: Other2)), "unsupported"),
            })
            {
                answers.Enqueue(answer);
                Assert.AreEqual(status, (await service.OpenClaudeCodeSessionInVsCodeAsync(Request(), CancellationToken.None)).Status, status);
            }

            Assert.IsEmpty(opened);
            var before = asked.Count;
            Assert.AreEqual("invalid_scope", (await service.OpenClaudeCodeSessionInVsCodeAsync(Request() with { CliSessionId = "../" + Cli }, CancellationToken.None)).Status);
            Assert.AreEqual("invalid_scope", (await service.ClaudeCodeSessionConversationAsync(Request() with { CliSessionId = "not-a-conversation" }, CancellationToken.None)).Status);
            Assert.AreEqual("stale_epoch", (await service.ClaudeCodeSessionConversationAsync(Request() with { ExpectedHostEpoch = Guid.NewGuid().ToString("D") }, CancellationToken.None)).Status);
            Assert.AreEqual("project_missing", (await service.ClaudeCodeSessionConversationAsync(Request() with { ProjectPath = root }, CancellationToken.None)).Status);
            Assert.AreEqual(before, asked.Count, "Nothing is asked for a request that is refused.");
            Assert.AreEqual("unconfigured", (await new WorkspaceService(host.WorkspaceReads, host.ProjectCatalog, Epoch).ClaudeCodeSessionConversationAsync(Request(), CancellationToken.None)).Status);
            Assert.IsEmpty(opened);
            await service.CloseSessionsAsync();
            await service.CloseImportsAsync();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task Preview_GivesTheNewestRecordsFirst_ThenOlderPages_OfTheConversationTheHostFoundForTheProject()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-claude-sessions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var host = await CreateHostAsync(root);
            var projectPath = Path.Combine(root, "project");
            var project = await host.ProjectCatalog.UpsertFromPathAsync(projectPath);
            var previewId = "preview:" + Cli;
            var at = new DateTimeOffset(2026, 10, 15, 8, 0, 0, TimeSpan.Zero);
            AgentEvent Record(int index) => new AgentContentCompletedEvent(Claude.ProviderId, previewId, at, new AgentRunId("external:r" + index / 10),
                index % 10 == 0 ? AgentContentKind.User : AgentContentKind.Assistant, $"c{index}", null, $"Text {index}");
            var preview = new ExternalConversationPreview("Fix the parser", 1_000_000, 638_000_000_000_000_000, [.. Enumerable.Range(0, 250).Select(Record)]);
            var answers = new Queue<ExternalConversationPreviewResult>();
            var asked = new List<(string ProjectId, string Provider, string Conversation)>();
            var service = new WorkspaceService(host.WorkspaceReads, host.ProjectCatalog, Epoch, host.ModelProviderRegistry,
                static (_, _, _, _, _) => Task.FromResult(new ExternalConversationListing([], 0)),
                static (_, _, _, _) => throw new InvalidOperationException("No import."),
                static (_, _, _, _) => throw new InvalidOperationException("Nothing is described."),
                static _ => throw new InvalidOperationException("Nothing is opened."),
                (target, provider, conversation, _) =>
                {
                    asked.Add((target.Id, provider.ProviderId.Value, conversation));
                    return Task.FromResult(answers.Count > 0 ? answers.Dequeue() : new("ok", null, preview));
                });
            WorkspaceClaudeCodeSessionPreviewRequest Request(HistoryCursor? cursor = null) => new(Epoch, project.Id, projectPath, Cli, cursor);

            // The newest page: the last hundred records, numbered from the first one, with the place of the page before.
            var newest = await service.ClaudeCodeSessionPreviewAsync(Request(), CancellationToken.None);
            Assert.AreEqual(("ok", "ok", "Fix the parser", null), (newest.Status, newest.Page.Status, newest.Title, newest.SessionId));
            Assert.AreEqual((100, "151", "250", "Text 249"), (newest.Page.Entries.Length, newest.Page.Entries[0].Offset, newest.Page.Entries[^1].Offset, newest.Page.Entries[^1].Text));
            Assert.AreEqual(new HistoryCursor(2, previewId, "1000000", "638000000000000000", "151"), newest.Page.Next);
            Assert.IsTrue(newest.Page.Entries.All(entry => entry.SessionId == previewId));
            Assert.AreEqual((project.Id, "claude-fixture", Cli), asked.Single(), "The provider that shares its conversations reads it, for the project.");
            Assert.AreEqual(newest.Page.Entries.Length, JsonSerializer.Deserialize(JsonSerializer.Serialize(newest, DesktopJsonContext.Default.WorkspaceClaudeCodeSessionPreviewResponse),
                DesktopJsonContext.Default.WorkspaceClaudeCodeSessionPreviewResponse)!.Page.Entries.Length);

            var older = await service.ClaudeCodeSessionPreviewAsync(Request(newest.Page.Next), CancellationToken.None);
            Assert.AreEqual(("51", "150", "51"), (older.Page.Entries[0].Offset, older.Page.Entries[^1].Offset, older.Page.Next!.Offset));
            var oldest = await service.ClaudeCodeSessionPreviewAsync(Request(older.Page.Next), CancellationToken.None);
            Assert.AreEqual(("1", "50", null), (oldest.Page.Entries[0].Offset, oldest.Page.Entries[^1].Offset, oldest.Page.Next));

            // The transcript changed since the page after: the page is not given from another revision.
            Assert.AreEqual("history_changed", (await service.ClaudeCodeSessionPreviewAsync(Request(newest.Page.Next! with { Length = "1000001" }), CancellationToken.None)).Status);
            foreach (var cursor in new[] { newest.Page.Next! with { Offset = "251" }, newest.Page.Next! with { SessionId = "preview:" + Other2 }, newest.Page.Next! with { Version = 1 },
                newest.Page.Next! with { Offset = "-1" } })
                Assert.AreEqual("invalid_cursor", (await service.ClaudeCodeSessionPreviewAsync(Request(cursor), CancellationToken.None)).Status, cursor.ToString());

            // A conversation a session continues opens that session; what the host does not show says why.
            answers.Enqueue(new("existing", "s-old", null));
            var existing = await service.ClaudeCodeSessionPreviewAsync(Request(), CancellationToken.None);
            Assert.AreEqual(("existing", "s-old", 0), (existing.Status, existing.SessionId, existing.Page.Entries.Length));
            foreach (var status in new[] { "not_found", "too_large", "closed" })
            {
                answers.Enqueue(new(status, null, null));
                Assert.AreEqual(status, (await service.ClaudeCodeSessionPreviewAsync(Request(), CancellationToken.None)).Status);
            }

            var before = asked.Count;
            Assert.AreEqual("invalid_scope", (await service.ClaudeCodeSessionPreviewAsync(Request() with { CliSessionId = "../" + Cli }, CancellationToken.None)).Status);
            Assert.AreEqual("stale_epoch", (await service.ClaudeCodeSessionPreviewAsync(Request() with { ExpectedHostEpoch = Guid.NewGuid().ToString("D") }, CancellationToken.None)).Status);
            Assert.AreEqual("project_missing", (await service.ClaudeCodeSessionPreviewAsync(Request() with { ProjectPath = root }, CancellationToken.None)).Status);
            Assert.AreEqual(before, asked.Count, "Nothing is read for a request that is refused.");
            Assert.AreEqual("unconfigured", (await Service(host, static (_, _, _, _, _) => Task.FromResult(new ExternalConversationListing([], 0)),
                static (_, _, _, _) => throw new InvalidOperationException()).ClaudeCodeSessionPreviewAsync(Request(), CancellationToken.None)).Status);
            await service.CloseSessionsAsync();
            await service.CloseImportsAsync();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task<(string Status, string? SessionId)> StatusAsync(WorkspaceService service, WorkspaceImportClaudeCodeSessionRequest request)
    {
        var reply = await service.ImportClaudeCodeSessionAsync(request, CancellationToken.None);
        return (reply.Status, reply.SessionId);
    }

    private static WorkspaceService Service(CodeAltaHost host,
        Func<ProjectDescriptor, ModelProviderDescriptor, int, int, CancellationToken, Task<ExternalConversationListing>> list,
        Func<ProjectDescriptor, ModelProviderDescriptor, string, string?, Task<ExternalConversationImportResult>> import)
        => new(host.WorkspaceReads, host.ProjectCatalog, Epoch, host.ModelProviderRegistry, list, import,
            static (_, _, _, _) => throw new InvalidOperationException("Nothing is described."), static _ => throw new InvalidOperationException("Nothing is opened."));

    private static async Task<CodeAltaHost> CreateHostAsync(string root)
    {
        var global = Path.Combine(root, "global"); var projectPath = Path.Combine(root, "project");
        var home = Path.Combine(root, "home"); var builtin = Path.Combine(root, "builtin");
        foreach (var path in new[] { global, projectPath, home, builtin }) Directory.CreateDirectory(path);
        return await CodeAltaHost.CreateAsync(new CodeAltaHostOptions
        {
            GlobalRoot = global, CurrentProjectPath = projectPath, DiscoveryScope = new(home, root), BuiltInSkillRoot = builtin,
            PluginEnvironment = FrozenDictionary<string, string?>.Empty, StartPlugins = false, IsHeadless = true, OwnsLogging = false,
            ConfigureModelProviders = registry =>
            {
                // Nothing of a provider is started: the calls of the host are those of the test.
                registry.RegisterOrReplace(Other, static () => throw new InvalidOperationException("Not started."));
                registry.RegisterOrReplace(Claude, static () => throw new InvalidOperationException("Not started."));
            },
        });
    }
}
