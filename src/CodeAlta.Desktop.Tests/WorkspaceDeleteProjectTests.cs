using System.Collections.Frozen;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class WorkspaceDeleteProjectTests
{
    private const string Epoch = "11111111-1111-4111-8111-111111111111";

    [TestMethod]
    public async Task PendingAskSurvivesItsProducerAndRefusesProjectDeletion()
    {
        var root = CreateRoot();
        try
        {
            var projectPath = Directory.CreateDirectory(Path.Combine(root, "project")).FullName;
            var global = Directory.CreateDirectory(Path.Combine(root, "global")).FullName;
            await using var host = await CreateHost(root, global, projectPath);
            var project = await host.ProjectCatalog.UpsertFromPathAsync(projectPath);
            var store = host.SessionViewCatalog.JournalStore.CreateSessionStore();
            var now = DateTimeOffset.UtcNow;
            await store.UpsertSessionAsync(new AgentSessionSummary
            {
                SessionId = "asking", ProviderId = new("test"), ProtocolFamily = "test", ProviderKey = "test", Title = "Asking",
                WorkingDirectory = projectPath, CreatedAt = now, UpdatedAt = now,
            });
            var execution = host.Commands.Asks.CreateExecution(Guid.NewGuid(), "asking", default);
            try
            {
                execution.Bind(Guid.NewGuid(), 1, new("test"));
                await execution.StartedAsync(new("producer-run"), default);
                using var json = JsonDocument.Parse("{\"args\":[\"ask\",\"--stdin\"],\"stdin\":\"{\\\"questions\\\":[{\\\"title\\\":\\\"Question\\\",\\\"question\\\":\\\"Continue?\\\",\\\"freeform\\\":{}}]}\"}");
                Assert.IsTrue((await execution.Tool.Handler(new(new("test"), "asking", "call", "alta", json.RootElement.Clone()), default)).Success);
            }
            finally { execution.Close(); }
            Assert.IsNotNull(host.Commands.Asks.List("asking").Head);
            var rpc = new WorkspaceService(host, Epoch);
            Assert.AreEqual("session_in_use", (await rpc.DeleteProjectAsync(new(Epoch, project.Id, projectPath, project.DisplayName, false, true), CancellationToken.None)).Status);
            Assert.IsNotNull(await store.GetSessionAsync("asking"));
            Assert.IsNotNull(await host.ProjectCatalog.GetByIdAsync(project.Id));
            await rpc.CloseSessionsAsync();
            await rpc.CloseImportsAsync();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ConfirmedDeletionIncludesAllProjectSessionsAndKeepsSourceFiles(bool archived)
    {
        var root = CreateRoot();
        try
        {
            var projectPath = Directory.CreateDirectory(Path.Combine(root, "project")).FullName;
            var otherPath = Directory.CreateDirectory(Path.Combine(root, "other")).FullName;
            var global = Directory.CreateDirectory(Path.Combine(root, "global")).FullName;
            var worktree = Directory.CreateDirectory(Path.Combine(root, "worktree")).FullName;
            var marker = Path.Combine(projectPath, "keep.txt");
            await File.WriteAllTextAsync(marker, "source files stay");
            await using var host = await CreateHost(root, global, projectPath);
            var project = await host.ProjectCatalog.UpsertFromPathAsync(projectPath);
            var other = await host.ProjectCatalog.UpsertFromPathAsync(otherPath);
            var store = host.SessionViewCatalog.JournalStore.CreateSessionStore();
            var now = DateTimeOffset.UtcNow;
            Task Add(string id, string path, string? parent = null, string? tree = null) => store.UpsertSessionAsync(new AgentSessionSummary
            {
                SessionId = id, ProviderId = new("delete-project-fixture"), ProtocolFamily = "test", ProviderKey = "delete-project-fixture",
                WorkingDirectory = path, Title = id, ParentSessionId = parent, WorktreeDirectory = tree, CreatedAt = now, UpdatedAt = now,
            });
            await Add("parent", projectPath);
            await Add("child", projectPath, "parent");
            await Add("grandchild", projectPath, "child", worktree);
            // More than the header-enrichment window; do not delete only the visible snapshot.
            for (var index = 0; index < 501; index++) await Add($"old-{index}", projectPath);
            await Add("foreign", otherPath);
            await Add("global", global);
            if (archived)
            {
                var evidence = await host.ProjectCatalog.ReadArchiveAsync(project.Id, projectPath);
                Assert.IsNotNull(evidence);
                Assert.AreEqual(ProjectDisplayNameRenameStatus.Updated, await host.ProjectCatalog.SetArchivedAsync(project.Id, projectPath,
                    evidence.SourcePath, evidence.Revision, false, true));
            }
            var rpc = new WorkspaceService(host, Epoch);
            var request = new WorkspaceDeleteProjectRequest(Epoch, project.Id, projectPath, project.DisplayName, archived, true);
            Assert.AreEqual("confirmation_required", (await rpc.DeleteProjectAsync(request with { Confirmed = false }, CancellationToken.None)).Status);
            Assert.AreEqual("stale_epoch", (await rpc.DeleteProjectAsync(request with { ExpectedHostEpoch = Guid.NewGuid().ToString("D") }, CancellationToken.None)).Status);
            Assert.AreEqual("invalid_scope", (await rpc.DeleteProjectAsync(request with { ConfirmedName = "" }, CancellationToken.None)).Status);
            Assert.AreEqual("scope_missing", (await rpc.DeleteProjectAsync(request with { ConfirmedName = "Wrong project" }, CancellationToken.None)).Status);
            Assert.AreEqual("scope_missing", (await rpc.DeleteProjectAsync(request with { ProjectPath = otherPath }, CancellationToken.None)).Status);
            Assert.AreEqual("scope_missing", (await rpc.DeleteProjectAsync(request with { ExpectedArchived = !archived }, CancellationToken.None)).Status);
            Assert.IsNotNull(await store.GetSessionAsync("parent"));
            var result = await rpc.DeleteProjectAsync(request, CancellationToken.None);
            Assert.AreEqual("ok", result.Status);
            Assert.AreEqual(request, JsonSerializer.Deserialize(JsonSerializer.Serialize(request, DesktopJsonContext.Default.WorkspaceDeleteProjectRequest),
                DesktopJsonContext.Default.WorkspaceDeleteProjectRequest));
            Assert.AreEqual(result, JsonSerializer.Deserialize(JsonSerializer.Serialize(result, DesktopJsonContext.Default.WorkspaceDeleteProjectResponse),
                DesktopJsonContext.Default.WorkspaceDeleteProjectResponse));
            var remaining = new List<string>();
            await foreach (var metadata in store.ListSessionsAsync()) remaining.Add(metadata.SessionId);
            CollectionAssert.AreEquivalent(new[] { "foreign", "global" }, remaining);
            Assert.IsNull(await host.ProjectCatalog.GetByIdAsync(project.Id));
            Assert.IsNotNull(await host.ProjectCatalog.GetByIdAsync(other.Id));
            Assert.AreEqual("source files stay", await File.ReadAllTextAsync(marker));
            Assert.IsTrue(Directory.Exists(worktree));
            await rpc.CloseSessionsAsync();
            await rpc.CloseImportsAsync();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ForeignDescendantsAndCyclicLineageRefuseBeforeAnyDeletion(bool cycle)
    {
        var root = CreateRoot();
        try
        {
            var projectPath = Directory.CreateDirectory(Path.Combine(root, "project")).FullName;
            var global = Directory.CreateDirectory(Path.Combine(root, "global")).FullName;
            await using var host = await CreateHost(root, global, projectPath);
            var project = await host.ProjectCatalog.UpsertFromPathAsync(projectPath);
            var store = host.SessionViewCatalog.JournalStore.CreateSessionStore();
            var now = DateTimeOffset.UtcNow;
            foreach (var (id, path, parent) in new[] { ("parent", projectPath, cycle ? "child" : (string?)null), ("child", cycle ? projectPath : global, "parent") })
                await store.UpsertSessionAsync(new AgentSessionSummary
                {
                    SessionId = id, ProviderId = new("test"), ProtocolFamily = "test", ProviderKey = "test", Title = id,
                    WorkingDirectory = path, ParentSessionId = parent, CreatedAt = now, UpdatedAt = now,
                });
            var rpc = new WorkspaceService(host, Epoch);
            Assert.AreEqual("has_children", (await rpc.DeleteProjectAsync(new(Epoch, project.Id, projectPath, project.DisplayName, false, true), CancellationToken.None)).Status);
            Assert.IsNotNull(await store.GetSessionAsync("parent"));
            Assert.IsNotNull(await store.GetSessionAsync("child"));
            Assert.IsNotNull(await host.ProjectCatalog.GetByIdAsync(project.Id));
            await rpc.CloseSessionsAsync();
            await rpc.CloseImportsAsync();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task WarmCacheDoesNotHideRestoredJournalsAndUnreadableJournalsRefuseDeletion(bool malformed)
    {
        var root = CreateRoot();
        try
        {
            var projectPath = Directory.CreateDirectory(Path.Combine(root, "project")).FullName;
            var global = Directory.CreateDirectory(Path.Combine(root, "global")).FullName;
            await using var host = await CreateHost(root, global, projectPath);
            var project = await host.ProjectCatalog.UpsertFromPathAsync(projectPath);
            var cached = host.SessionViewCatalog.JournalStore.CreateSessionStore();
            await foreach (var _ in cached.ListSessionsAsync()) { }
            var disk = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(host.CatalogOptions.StateRoot));
            var now = DateTimeOffset.UtcNow;
            await disk.UpsertSessionAsync(new AgentSessionSummary
            {
                SessionId = "restored", ProviderId = new("test"), ProtocolFamily = "test", ProviderKey = "test", Title = "Restored",
                WorkingDirectory = projectPath, CreatedAt = now, UpdatedAt = now,
            });
            var journals = new List<SessionJournalFile>();
            await foreach (var journal in host.SessionJournals.ListAsync()) journals.Add(journal);
            if (malformed) await File.WriteAllTextAsync(journals.Single().Path, "not a journal\n");
            var rpc = new WorkspaceService(host, Epoch);
            var result = await rpc.DeleteProjectAsync(new(Epoch, project.Id, projectPath, project.DisplayName, false, true), CancellationToken.None);
            Assert.AreEqual(malformed ? "delete_unconfirmed" : "ok", result.Status);
            Assert.AreEqual(malformed, File.Exists(journals.Single().Path));
            Assert.AreEqual(malformed, await host.ProjectCatalog.GetByIdAsync(project.Id) is not null);
            if (!malformed)
            {
                var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => host.RuntimeService.CreateProjectSessionAsync(project,
                    new SessionExecutionOptions { ProviderId = new("test"), WorkingDirectory = projectPath,
                        OnPermissionRequest = host.RuntimeService.Permissions.OwnedDefaultPermissionHandler }, "Stale project", CancellationToken.None));
                StringAssert.Contains(failure.Message, "project is being deleted or has been deleted");
                Assert.IsNull(await host.ProjectCatalog.GetByIdAsync(project.Id));
            }
            await rpc.CloseSessionsAsync();
            await rpc.CloseImportsAsync();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task CancelledWaitRetainsAdmissionAndCloseDrainsTheOriginal()
    {
        var root = CreateRoot();
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? closing = null;
        try
        {
            var global = Directory.CreateDirectory(Path.Combine(root, "global")).FullName;
            var catalog = new ProjectCatalog(new CatalogOptions { GlobalRoot = global });
            await using var reads = new OwnedSessionWorkspace(catalog, new SessionViewJournalStore(catalog.Options));
            var count = 0;
            var rpc = new WorkspaceService(reads, catalog, Epoch, (string _, string _, string _, bool _) => { count++; return release.Task; });
            var request = new WorkspaceDeleteProjectRequest(Epoch, "exact-project", root, "Exact project", true, true);
            using var cancel = new CancellationTokenSource();
            var original = rpc.DeleteProjectAsync(request, cancel.Token);
            Assert.AreEqual(1, count);
            Assert.AreEqual("busy", (await rpc.DeleteProjectAsync(request, CancellationToken.None)).Status);
            Assert.AreEqual("busy", (await rpc.ArchiveProjectAsync(new(Epoch, request.ProjectId, root, true, false, false, null, null), CancellationToken.None)).Status);
            cancel.Cancel();
            await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () => await original);
            closing = rpc.CloseImportsAsync();
            Assert.IsFalse(closing.IsCompleted);
            release.TrySetResult("delete_unconfirmed");
            await closing.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(1, count);
            Assert.AreEqual("closed", (await rpc.DeleteProjectAsync(request, CancellationToken.None)).Status);
            await rpc.CloseSessionsAsync();
        }
        finally
        {
            release.TrySetResult("delete_unconfirmed");
            if (closing is not null) await closing.WaitAsync(TimeSpan.FromSeconds(10));
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-project-delete-" + Guid.NewGuid().ToString("N"));
        // macOS's /var is a symlink; the ownership check requires a non-linked ancestor chain.
        if (OperatingSystem.IsMacOS() && root.StartsWith("/var/", StringComparison.Ordinal)) root = "/private" + root;
        return Directory.CreateDirectory(root).FullName;
    }

    private static Task<CodeAltaHost> CreateHost(string root, string global, string projectPath)
        => CodeAltaHost.CreateAsync(new CodeAltaHostOptions
        {
            GlobalRoot = global, CurrentProjectPath = projectPath, DiscoveryScope = new(Directory.CreateDirectory(Path.Combine(root, "home")).FullName, root),
            BuiltInSkillRoot = Directory.CreateDirectory(Path.Combine(root, "builtin")).FullName,
            PluginEnvironment = FrozenDictionary<string, string?>.Empty, StartPlugins = false, IsHeadless = true, OwnsLogging = false, EnableOwnedAsks = true,
        });
}
