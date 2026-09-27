using CodeAlta.Catalog;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Orchestration.Runtime.SystemPrompts;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class PromptCreationRpcTests
{
    private const string Epoch = "11111111-1111-4111-8111-111111111111";

    [TestMethod]
    public async Task CanceledWaitRetainsOriginalAndShutdownJoinsPublication()
    {
        var root = Path.Combine(Path.GetTempPath(), "alta-create-cancel-" + Guid.NewGuid().ToString("N"));
        try
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<PromptResourceStore?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var service = new PromptCreationService(Epoch, async (_, _) => { entered.SetResult(); return ("ok", await release.Task); });
            using var cancel = new CancellationTokenSource();
            var wait = service.CreateAsync(Request(), cancel.Token);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                cancel.Cancel();
                await Assert.ThrowsAsync<OperationCanceledException>(() => wait);
                Assert.AreEqual("busy", (await service.CreateAsync(Request() with { RequestId = "other" }, default)).Status);
                var drain = service.DrainAsync();
                Assert.IsFalse(drain.IsCompleted);
                Assert.AreEqual("closed", (await service.CreateAsync(Request(), default)).Status);
                var store = new PromptResourceStore(Path.Combine(root, "built"), Path.Combine(root, "global"), null, new TextFileCodec());
                release.SetResult(store);
                await drain.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.AreEqual("body", store.Load(new(PromptResourceScope.Global, PromptResourceKind.Agent, "example")).Content.Body);
            }
            finally { release.TrySetResult(null); await service.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task MalformedAndUnconfirmedRequestsNeverResolveRoots()
    {
        var calls = 0;
        var service = new PromptCreationService(Epoch, (_, _) => { calls++; return Task.FromResult(("refused", (PromptResourceStore?)null)); });
        foreach (var request in new[] { Request() with { PromptId = "../bad" }, Request() with { PromptId = "Upper" },
            Request() with { Body = new string('a', 16385) }, Request() with { Body = "bad\ud800" },
            Request() with { RootKind = "builtin" }, Request() with { UnderstoodShadowing = false },
            Request() with { Mode = "invalid" }, Request() with { Name = " " }, Request() with { Scope = "system" },
            Request() with { Name = new string('a', 129) }, Request() with { Description = new string('a', 513) },
            Request() with { Name = "bad\ud800" }, Request() with { PromptId = new string('a', 65) },
            Request() with { RootKind = "project_alta" }, Request() with { PromptId = "con" } })
            Assert.AreEqual("refused", (await service.CreateAsync(request, default)).Status);
        Assert.AreEqual("stale_epoch", (await service.CreateAsync(Request() with { ExpectedHostEpoch = "22222222-2222-4222-8222-222222222222" }, default)).Status);
        Assert.AreEqual(0, calls);
    }

    internal static PromptCreateRequest Request() => new(Epoch, "original", "session", "2026-09-27T00:00:00+00:00", "global", null, null,
        "user_alta", "example", "Example", "Description", "body", "replace", true);

    [TestMethod]
    public async Task ExactSavedScopeCreatesRuntimeReadableSourcesWithActualPrecedenceAndRefusesArchiveMismatch()
    {
        var root = Path.Combine(Path.GetTempPath(), "alta-create-rpc-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = new CatalogOptions { GlobalRoot = Path.Combine(root, "global") };
            var projects = new ProjectCatalog(options);
            var projectRoot = Directory.CreateDirectory(Path.Combine(root, "project")).FullName;
            var project = await projects.UpsertFromPathAsync(projectRoot);
            var journals = new SessionViewJournalStore(options);
            var session = new SessionViewDescriptor { SessionId = "saved", Kind = SessionViewKind.ProjectSession,
                ProjectRef = project.Id, WorkingDirectory = projectRoot, ProviderId = "fake", ProviderKey = "fake", CreatedAt = DateTimeOffset.UtcNow, Title = "test" };
            await journals.EnsureHeaderAsync(session);
            var service = new PromptCreationService(projects, journals, Epoch);
            var request = Request() with { SessionId = session.SessionId, CreatedAt = session.CreatedAt.ToString("O"), Scope = "project",
                ProjectId = project.Id, ProjectPath = projectRoot, Body = "global body" };
            Assert.AreEqual("created", (await service.CreateAsync(request, default)).Status);
            request = request with { RootKind = "project_alta", Mode = "append", Body = "project body" };
            Assert.AreEqual("created", (await service.CreateAsync(request, default)).Status);
            var effective = new AgentPromptCatalog().ListEffectivePrompts(new AgentPromptCatalogQuery {
                AppBaseDirectory = Path.Combine(root, "app"), UserProfileRoot = Path.Combine(root, "profile"),
                UserCodeAltaRoot = options.GlobalRoot, ProjectRoot = projectRoot }).Single(row => row.PromptName == "example");
            Assert.AreEqual($"global body{Environment.NewLine}{Environment.NewLine}project body", effective.Body);
            Assert.AreEqual("Example", effective.DisplayName);
            Assert.AreEqual("Description", effective.Description);
            Assert.AreEqual(AgentPromptSourceKind.Project, effective.SourceKind);
            Assert.AreEqual("conflict", (await service.CreateAsync(request with { Body = "replacement" }, default)).Status);
            foreach (var invalid in new[] { request with { ProjectPath = root }, request with { ProjectId = Guid.NewGuid().ToString("D") },
                request with { SessionId = "not-saved" }, request with { CreatedAt = DateTimeOffset.UnixEpoch.ToString("O") } })
                Assert.AreEqual("refused", (await service.CreateAsync(invalid, default)).Status);
            project.Archived = true; await projects.SaveAsync(project);
            Assert.AreEqual("refused", (await service.CreateAsync(request with { PromptId = "archived" }, default)).Status);
            Assert.AreEqual("refused", (await service.CreateAsync(request with { RootKind = "user_alta", PromptId = "archived" }, default)).Status);
            project.Archived = false; await projects.SaveAsync(project);
            File.Delete(Directory.EnumerateFiles(options.ProjectsRoot, "*.md").Single());
            Assert.AreEqual("refused", (await service.CreateAsync(request with { PromptId = "uncataloged" }, default)).Status);
            var global = new SessionViewDescriptor { SessionId = "global-saved", Kind = SessionViewKind.GlobalSession,
                WorkingDirectory = options.GlobalRoot, ProviderId = "fake", ProviderKey = "fake", CreatedAt = session.CreatedAt, Title = "global test" };
            await journals.EnsureHeaderAsync(global);
            var globalRequest = Request() with { SessionId = global.SessionId, CreatedAt = global.CreatedAt.ToString("O"), PromptId = "global-example" };
            Assert.AreEqual("created", (await service.CreateAsync(globalRequest, default)).Status);
            Assert.AreEqual("refused", (await service.CreateAsync(globalRequest with { RootKind = "project_alta" }, default)).Status);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task CatalogWriterGateExcludesArchiveAndDrainsHeldCreate()
    {
        var root = Path.Combine(Path.GetTempPath(), "alta-create-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var catalog = new ProjectCatalog(new CatalogOptions { GlobalRoot = Path.Combine(root, "global") });
            var project = await catalog.UpsertFromPathAsync(root);
            await using var reads = new OwnedSessionWorkspace(catalog, new SessionViewJournalStore(catalog.Options));
            var workspace = new WorkspaceService(reads, catalog, Epoch);
            var hold = new TaskCompletionSource<PromptCreateResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            var original = workspace.RunPromptCreationAsync(() => hold.Task, status => new(status, Epoch, Request()));
            Assert.AreEqual("busy", (await workspace.ArchiveProjectAsync(new(Epoch, project.Id, root, false, true, false, null, null), default)).Status);
            Assert.AreEqual("busy", (await workspace.RunPromptCreationAsync(() => hold.Task, status => new(status, Epoch, Request()))).Status);
            var drain = workspace.CloseImportsAsync();
            Assert.IsFalse(drain.IsCompleted);
            Assert.AreEqual("closed", (await workspace.RunPromptCreationAsync(() => hold.Task, status => new(status, Epoch, Request()))).Status);
            hold.SetResult(new("created", Epoch, Request()));
            await drain.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual("created", (await original).Status);
        }
        finally { Directory.Delete(root, true); }
    }
}
