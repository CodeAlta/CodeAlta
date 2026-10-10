using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

[TestClass]
public sealed class SessionRuntimeMoveTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 10, 10, 9, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task MoveOwnedSessionAsync_MovesTheSessionAndWhatItStarted_AndLeavesTheOthers()
    {
        await using var f = await Fixture.CreateAsync();
        await f.CreateSessionAsync("root", f.Source, title: null);
        await f.CreateSessionAsync("child", f.Source, title: "Named child", parent: "root");
        await f.CreateSessionAsync("other", f.Source, title: "Other");

        var result = await f.Runtime.MoveOwnedSessionAsync("root", f.Source.Id, f.Source.ProjectPath, f.Target.Id, f.Target.ProjectPath, static _ => false);

        Assert.AreEqual("ok", result.Status);
        CollectionAssert.AreEqual(new[] { "child", "root" }, result.Moved.Select(static moved => moved.SessionId).ToArray(),
            "What the session started moves first, the session last.");
        Assert.IsTrue(result.Moved.All(moved => moved.ProjectId == f.Target.Id && moved.WorkspacePath == f.Target.ProjectPath));
        foreach (var id in new[] { "root", "child" })
        {
            var header = await f.Journal.ReadHeaderAsync(id, CreatedAt);
            Assert.AreEqual(f.Target.Id, header!.ProjectRef);
            Assert.AreEqual(f.Target.ProjectPath, header.WorkingDirectory);
        }

        Assert.AreEqual(f.Source.Id, (await f.Journal.ReadHeaderAsync("other", CreatedAt))!.ProjectRef);
        var store = f.Journal.CreateSessionStore();
        Assert.AreEqual(f.Target.DisplayName, (await store.GetSessionSummaryAsync("root"))!.Title,
            "A session that was never named is named after its project, now the new one.");
        Assert.AreEqual("Named child", (await store.GetSessionSummaryAsync("child"))!.Title);
        var notice = f.Runtime.TakeMovedNotice("root");
        StringAssert.Contains(notice, f.Source.ProjectPath);
        StringAssert.Contains(notice, f.Target.ProjectPath);
        Assert.IsNull(f.Runtime.TakeMovedNotice("root"), "The notice is given once.");
    }

    [TestMethod]
    public async Task MoveOwnedSessionAsync_RefusesWhatCannotMove_AndChangesNothing()
    {
        await using var f = await Fixture.CreateAsync();
        await f.CreateSessionAsync("session", f.Source, title: "Kept");
        await f.CreateSessionAsync("child", f.Source, title: "Child", parent: "session");

        Assert.AreEqual("same_project",
            (await f.Runtime.MoveOwnedSessionAsync("session", f.Source.Id, f.Source.ProjectPath, f.Source.Id, f.Source.ProjectPath, static _ => false)).Status);
        Assert.AreEqual("target_missing",
            (await f.Runtime.MoveOwnedSessionAsync("session", f.Source.Id, f.Source.ProjectPath, "missing", f.Target.ProjectPath, static _ => false)).Status);
        Assert.AreEqual("target_missing",
            (await f.Runtime.MoveOwnedSessionAsync("session", f.Source.Id, f.Source.ProjectPath, f.Target.Id, f.Source.ProjectPath, static _ => false)).Status);
        Assert.AreEqual("session_missing",
            (await f.Runtime.MoveOwnedSessionAsync("session", f.Source.Id, f.Target.ProjectPath, f.Target.Id, f.Target.ProjectPath, static _ => false)).Status);
        Assert.AreEqual("session_missing",
            (await f.Runtime.MoveOwnedSessionAsync("missing", f.Source.Id, f.Source.ProjectPath, f.Target.Id, f.Target.ProjectPath, static _ => false)).Status);
        var busy = await f.Runtime.MoveOwnedSessionAsync("session", f.Source.Id, f.Source.ProjectPath, f.Target.Id, f.Target.ProjectPath,
            static id => id == "child");
        Assert.AreEqual("busy", busy.Status, "A session that cannot move keeps the session that started it where it is.");
        Assert.AreEqual(0, busy.Moved.Count);
        foreach (var id in new[] { "session", "child" })
            Assert.AreEqual(f.Source.Id, (await f.Journal.ReadHeaderAsync(id, CreatedAt))!.ProjectRef);
    }

    [TestMethod]
    public async Task MoveOwnedSessionAsync_AsksTheProviderToMoveWhatItKeeps()
    {
        var relocations = new List<(string From, string To)>();
        await using var f = await Fixture.CreateAsync(registry => registry.RegisterOrReplace(new(new("relocating"), "Relocating", "relocating"),
            () => new RelocatingRuntime(relocations)));
        await f.CreateSessionAsync("session", f.Source, title: "Kept", provider: "relocating");

        var result = await f.Runtime.MoveOwnedSessionAsync("session", f.Source.Id, f.Source.ProjectPath, f.Target.Id, f.Target.ProjectPath, static _ => false);

        Assert.AreEqual("ok", result.Status);
        CollectionAssert.AreEqual(new[] { (f.Source.ProjectPath, f.Target.ProjectPath) }, relocations);
        CollectionAssert.AreEqual(new[] { "moved what it keeps" }, result.Warnings.ToArray());
    }

    [TestMethod]
    public void KeptWorktree_KeepsAWorktreeOfTheRepositoryOfTheTarget_Only()
    {
        using var temp = new TempDirectory();
        var main = Directory.CreateDirectory(Path.Combine(temp.Path, "main")).FullName;
        var git = Directory.CreateDirectory(Path.Combine(main, ".git")).FullName;
        var linked = Directory.CreateDirectory(Path.Combine(git, "worktrees", "tree")).FullName;
        File.WriteAllText(Path.Combine(linked, "commondir"), "../..");
        var tree = Directory.CreateDirectory(Path.Combine(temp.Path, "tree")).FullName;
        File.WriteAllText(Path.Combine(tree, ".git"), $"gitdir: {linked}");
        var other = Directory.CreateDirectory(Path.Combine(temp.Path, "other")).FullName;
        Directory.CreateDirectory(Path.Combine(other, ".git"));

        Assert.AreEqual(tree, SessionRuntimeService.KeptWorktree(tree, main), "Another checkout of the same repository keeps the worktree.");
        Assert.IsNull(SessionRuntimeService.KeptWorktree(tree, tree), "A project that lives in the worktree is where the session works now.");
        Assert.IsNull(SessionRuntimeService.KeptWorktree(tree, other), "Another repository has nothing to do with the worktree.");
        Assert.IsNull(SessionRuntimeService.KeptWorktree(Path.Combine(temp.Path, "gone"), main), "A worktree that is gone is left.");
        Assert.IsNull(SessionRuntimeService.KeptWorktree(null, main));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TempDirectory _temp;
        private readonly AgentHub _hub;

        private Fixture(TempDirectory temp, AgentHub hub, SessionRuntimeService runtime, SessionViewJournalStore journal,
            ProjectDescriptor source, ProjectDescriptor target)
        {
            _temp = temp;
            _hub = hub;
            Runtime = runtime;
            Journal = journal;
            Source = source;
            Target = target;
        }

        public SessionRuntimeService Runtime { get; }

        public SessionViewJournalStore Journal { get; }

        public ProjectDescriptor Source { get; }

        public ProjectDescriptor Target { get; }

        public static async Task<Fixture> CreateAsync(Action<ModelProviderRegistry>? configure = null)
        {
            var temp = new TempDirectory();
            var registry = new ModelProviderRegistry();
            configure?.Invoke(registry);
            var hub = new AgentHub(registry, temp.Path);
            var options = new CatalogOptions { GlobalRoot = Path.Combine(temp.Path, "alta") };
            var sessionViewCatalog = new SessionViewCatalog(options);
            var projects = new ProjectCatalog(options);
            var source = await projects.UpsertFromPathAsync(Directory.CreateDirectory(Path.Combine(temp.Path, "source")).FullName);
            var target = await projects.UpsertFromPathAsync(Directory.CreateDirectory(Path.Combine(temp.Path, "target")).FullName);
            var runtime = new SessionRuntimeService(hub, new AgentSessionCatalog(sessionViewCatalog.JournalStore.CreateSessionStore()), projects,
                sessionViewCatalog, new AgentInstructionTemplateProvider(catalogOptions: options), options);
            return new Fixture(temp, hub, runtime, sessionViewCatalog.JournalStore, source, target);
        }

        public async Task CreateSessionAsync(string id, ProjectDescriptor project, string? title, string? parent = null, string provider = "codex")
        {
            var session = new SessionViewDescriptor
            {
                SessionId = id, Kind = SessionViewKind.ProjectSession, ProviderId = provider, ProviderKey = provider, ProjectRef = project.Id,
                ParentSessionId = parent, WorkingDirectory = project.ProjectPath, Title = title ?? project.DisplayName,
                Status = SessionViewStatus.Active, CreatedAt = CreatedAt, UpdatedAt = CreatedAt, LastActiveAt = CreatedAt,
            };
            await Journal.EnsureHeaderAsync(session);
            var store = Journal.CreateSessionStore();
            await store.UpsertSessionAsync(new() { SessionId = id, ProviderId = new(provider), ProviderKey = provider, ProtocolFamily = provider,
                WorkingDirectory = project.ProjectPath, Title = title ?? project.DisplayName, ParentSessionId = parent,
                CreatedAt = CreatedAt, UpdatedAt = CreatedAt });
            await store.UpsertStateAsync(new() { SessionId = id, ProviderKey = provider, ProtocolFamily = provider, ProviderSessionId = "opaque", UpdatedAt = CreatedAt });
            await Journal.AppendStateAsync(session, new SessionViewLocalState { ProviderKey = provider, ParentSessionId = parent });
        }

        public async ValueTask DisposeAsync()
        {
            await Runtime.DisposeAsync();
            await _hub.DisposeAsync();
            _temp.Dispose();
        }
    }

    private sealed class RelocatingRuntime(List<(string From, string To)> relocations) : IModelProviderRuntime, IAgentSessionRelocationProvider
    {
        public ModelProviderDescriptor Descriptor { get; } = new(new("relocating"), "Relocating", "relocating");

        public Task<string?> RelocateSessionAsync(AgentSessionState state, string fromDirectory, string toDirectory, CancellationToken cancellationToken = default)
        {
            relocations.Add((fromDirectory, toDirectory));
            return Task.FromResult<string?>("moved what it keeps");
        }

        public Task StartAsync(CancellationToken cancellationToken = default) => throw new AssertFailedException("A move starts no provider.");

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<ModelProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IModelProviderTurnExecutor CreateTurnExecutor() => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codealta-move-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup for temporary test files.
            }
        }
    }
}
