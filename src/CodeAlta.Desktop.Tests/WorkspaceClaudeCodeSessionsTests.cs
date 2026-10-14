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
    private static readonly ModelProviderDescriptor Other = new(new("other-fixture"), "Other") { IsDefault = true };
    private static readonly ModelProviderDescriptor Claude = new(new("claude-fixture"), "Claude Code", "claude-code") { SharesConversations = true };

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
            var asked = new List<(string ProjectId, string Provider, int Limit)>();
            var reply = new ExternalConversationListing(
                [new ExternalConversationSummary(Cli, "Fix the parser", new DateTimeOffset(2026, 10, 14, 8, 0, 0, TimeSpan.Zero), "vscode", "claude-vscode", true)], 7);
            Func<ProjectDescriptor, ModelProviderDescriptor, int, CancellationToken, Task<ExternalConversationListing>> list = (target, provider, limit, _) =>
            {
                asked.Add((target.Id, provider.ProviderId.Value, limit));
                return Task.FromResult(reply);
            };
            var service = Service(host, list, static (_, _, _) => throw new InvalidOperationException("No import."));
            WorkspaceClaudeCodeSessionsRequest Request() => new(Epoch, project.Id, projectPath);

            var listed = await service.ClaudeCodeSessionsAsync(Request(), CancellationToken.None);
            Assert.AreEqual(("ok", "claude-fixture", 7), (listed.Status, listed.ProviderId, listed.Total));
            Assert.AreEqual(new WorkspaceClaudeCodeSession(Cli, "Fix the parser", reply.Conversations[0].LastChangedAt, "vscode", true), listed.Sessions.Single());
            Assert.AreEqual((project.Id, "claude-fixture", WorkspaceService.ClaudeCodeSessionsLimit), asked.Single(),
                "The default provider of the project does not share its conversations: the one that does is asked.");
            Assert.AreEqual(listed.Total, JsonSerializer.Deserialize(JsonSerializer.Serialize(listed, DesktopJsonContext.Default.WorkspaceClaudeCodeSessionsResponse),
                DesktopJsonContext.Default.WorkspaceClaudeCodeSessionsResponse)!.Total);

            Assert.AreEqual("stale_epoch", (await service.ClaudeCodeSessionsAsync(Request() with { ExpectedHostEpoch = Guid.NewGuid().ToString("D") }, CancellationToken.None)).Status);
            Assert.AreEqual("invalid_scope", (await service.ClaudeCodeSessionsAsync(Request() with { ProjectPath = "" }, CancellationToken.None)).Status);
            Assert.AreEqual("project_missing", (await service.ClaudeCodeSessionsAsync(Request() with { ProjectPath = root }, CancellationToken.None)).Status);
            Assert.AreEqual(1, asked.Count, "Nothing is read for a request that is refused.");
            Assert.AreEqual("unconfigured", (await new WorkspaceService(host.WorkspaceReads, host.ProjectCatalog, Epoch).ClaudeCodeSessionsAsync(Request(), CancellationToken.None)).Status);

            var failing = Service(host, static (_, _, _, _) => throw new IOException("unreadable"), static (_, _, _) => throw new InvalidOperationException());
            Assert.AreEqual("read_failed", (await failing.ClaudeCodeSessionsAsync(Request(), CancellationToken.None)).Status);

            // Without a provider that shares its conversations, there is nothing to list, and nothing is read.
            var without = new ModelProviderRegistry();
            without.RegisterOrReplace(Other, static () => throw new InvalidOperationException("Not started."));
            var none = new WorkspaceService(host.WorkspaceReads, host.ProjectCatalog, Epoch, without, list, static (_, _, _) => throw new InvalidOperationException());
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
            var service = Service(host, static (_, _, _, _) => Task.FromResult(new ExternalConversationListing([], 0)), async (target, provider, conversation) =>
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

    private static async Task<(string Status, string? SessionId)> StatusAsync(WorkspaceService service, WorkspaceImportClaudeCodeSessionRequest request)
    {
        var reply = await service.ImportClaudeCodeSessionAsync(request, CancellationToken.None);
        return (reply.Status, reply.SessionId);
    }

    private static WorkspaceService Service(CodeAltaHost host,
        Func<ProjectDescriptor, ModelProviderDescriptor, int, CancellationToken, Task<ExternalConversationListing>> list,
        Func<ProjectDescriptor, ModelProviderDescriptor, string, Task<ExternalConversationImportResult>> import)
        => new(host.WorkspaceReads, host.ProjectCatalog, Epoch, host.ModelProviderRegistry, list, import);

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
