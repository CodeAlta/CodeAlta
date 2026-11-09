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
    public async Task MoveOwnedSessionAsync_ThatFailsPartWay_SaysWhatMoved()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Only Windows refuses to replace a file that is open elsewhere.");
        await using var f = await Fixture.CreateAsync();
        await f.CreateSessionAsync("root", f.Source, title: "Root");
        await f.CreateSessionAsync("child", f.Source, title: "Child", parent: "root");
        var journal = Directory.EnumerateFiles(f.Root, "root.jsonl", SearchOption.AllDirectories).Single();

        OwnedSessionMoveResult result;
        using (new FileStream(journal, FileMode.Open, FileAccess.Read, FileShare.Read))
            result = await f.Runtime.MoveOwnedSessionAsync("root", f.Source.Id, f.Source.ProjectPath, f.Target.Id, f.Target.ProjectPath, static _ => false);

        // What moved before the failure stays moved, and is named, so that its tabs and its remote control follow it.
        Assert.AreEqual("move_partial", result.Status);
        CollectionAssert.AreEqual(new[] { "child" }, result.Moved.Select(static moved => moved.SessionId).ToArray());
        Assert.IsTrue(result.Warnings.Any(static warning => warning.Contains("root", StringComparison.Ordinal)));
        Assert.AreEqual(f.Target.Id, (await f.Journal.ReadHeaderAsync("child", CreatedAt))!.ProjectRef);
        Assert.AreEqual(f.Source.Id, (await f.Journal.ReadHeaderAsync("root", CreatedAt))!.ProjectRef, "The journal that could not be replaced is as it was.");
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
    [DataRow(false, DisplayName = "after a turn")]
    [DataRow(true, DisplayName = "after a turn that failed")]
    public async Task MoveSessionAsync_MovesASessionThatRan_OnAProviderWhoseKeyIsNotItsType(bool fails)
    {
        // A second Claude Code account: the key names the entry, the protocol family is the one of its type.
        using var temp = new TempDirectory();
        var global = Directory.CreateDirectory(Path.Combine(temp.Path, "global")).FullName;
        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "source")).FullName;
        var provider = new AccountProvider { Fails = fails };
        await using var host = await CodeAlta.Orchestration.Hosting.CodeAltaHost.CreateAsync(new()
        {
            GlobalRoot = global, CurrentProjectPath = source, IsHeadless = true, AutoApproveOwnedPermissions = true, OwnedCommandReceiptCapacity = 8,
            ConfigureModelProviders = registry => registry.RegisterOrReplace(provider.Descriptor, () => provider),
        }, CancellationToken.None);
        var target = await host.ProjectCatalog.UpsertFromPathAsync(Directory.CreateDirectory(Path.Combine(temp.Path, "target")).FullName);
        var sessionId = (await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, "Fix the login error")).SessionId;
        var receipt = host.Commands.AdmitSend(new(Guid.NewGuid().ToString("N"), sessionId, "one")).Receipt;
        Assert.IsNotNull(receipt);
        var sent = await receipt.Completion.WaitAsync(TestDeadlines.Hang);
        Assert.AreEqual(fails ? OwnedSessionCommandOutcome.Failed : OwnedSessionCommandOutcome.Completed, sent.Outcome, sent.Code);

        var store = host.SessionViewCatalog.JournalStore.CreateSessionStore();
        // No record says otherwise in between: a move or a resume read there would find the session needing recovery.
        var journal = Directory.EnumerateFiles(global, sessionId + ".jsonl", SearchOption.AllDirectories).Single();
        Assert.IsFalse(File.ReadLines(journal).Any(static line => line.Contains("\"protocolFamily\":\"claude-business\"", StringComparison.Ordinal)),
            "The attachment records the family of the provider, not its key.");
        Assert.AreEqual("claude-code", (await store.GetSessionSummaryAsync(sessionId))!.ProtocolFamily, "The summary records the family of the provider, not its key.");
        var result = await host.Commands.MoveSessionAsync(sessionId, host.CurrentProject.Id, source, target.Id, target.ProjectPath);

        Assert.AreEqual("ok", result.Status, string.Join(" ", result.Warnings));
        Assert.AreEqual(target.ProjectPath, (await store.GetSessionSummaryAsync(sessionId))!.WorkingDirectory);

        // And to another space.
        var space = (await host.SpaceCatalog.CreateAsync("Business")).Space!;
        var toSpace = await host.Commands.MoveSessionToSpaceAsync(sessionId, space.Id);
        Assert.AreEqual("ok", toSpace.Status, string.Join(" ", toSpace.Warnings));
        Assert.AreEqual(space.Id, (await host.SessionViewCatalog.JournalStore.ReadHeaderAsync(sessionId, (await store.GetSessionSummaryAsync(sessionId))!.CreatedAt))!.SpaceId);
    }

    [TestMethod]
    public async Task MoveOwnedSessionAsync_MovesASessionOf18WhoseSummaryNamesTheProviderKeyAsFamily()
    {
        // A custom OpenAI-compatible provider of 1.8: its attachment wrote the key as the family of the summary.
        var provider = new AccountProvider("neuralwatt", "openai-chat");
        await using var f = await Fixture.CreateAsync(registry => registry.RegisterOrReplace(provider.Descriptor, () => provider));
        await f.CreateSessionAsync("session", f.Source, title: "Kept", provider: "neuralwatt", summaryFamily: "neuralwatt", stateFamily: "openai-chat");

        var result = await f.Runtime.MoveOwnedSessionAsync("session", f.Source.Id, f.Source.ProjectPath, f.Target.Id, f.Target.ProjectPath, static _ => false);

        Assert.AreEqual("ok", result.Status, string.Join(" ", result.Warnings));
        var store = f.Journal.CreateSessionStore();
        Assert.AreEqual("openai-chat", (await store.GetSessionSummaryAsync("session"))!.ProtocolFamily);
        Assert.AreEqual("opaque", (await store.GetStateAsync("session"))!.ProviderSessionId, "The conversation goes on.");

        // Such a session also changes space.
        await f.CreateSessionAsync("spaced", f.Source, title: "Spaced", provider: "neuralwatt", summaryFamily: "neuralwatt", stateFamily: "openai-chat");
        var toSpace = await f.Runtime.MoveOwnedSessionToSpaceAsync("spaced", "business", static _ => false);
        Assert.AreEqual("ok", toSpace.Status, string.Join(" ", toSpace.Warnings));
        Assert.AreEqual("business", (await f.Journal.ReadHeaderAsync("spaced", CreatedAt))!.SpaceId);
        Assert.AreEqual("openai-chat", (await store.GetSessionSummaryAsync("spaced"))!.ProtocolFamily);
        Assert.AreEqual("opaque", (await store.GetStateAsync("spaced"))!.ProviderSessionId);
    }

    [TestMethod]
    public async Task FollowMovedProjectFoldersAsync_BindsTheSessionsOfAProjectWhoseFolderMoved_ToItsNewFolder()
    {
        var relocations = new List<(string From, string To)>();
        await using var f = await Fixture.CreateAsync(registry => registry.RegisterOrReplace(new(new("relocating"), "Relocating", "relocating"),
            () => new RelocatingRuntime(relocations)));
        await f.CreateSessionAsync("root", f.Source, title: "Root", provider: "relocating");
        await f.CreateSessionAsync("child", f.Source, title: "Child", parent: "root");
        await f.CreateSessionAsync("other", f.Target, title: "Other");
        var oldPath = f.Source.ProjectPath;
        var newPath = Path.Combine(f.Root, "moved-source");
        Directory.Move(oldPath, newPath);
        var moved = f.Source.Copy();
        moved.ProjectPath = newPath;
        await f.Projects.SaveAsync(moved);

        var result = await f.Runtime.FollowMovedProjectFoldersAsync(static _ => false);

        Assert.AreEqual("ok", result.Status, string.Join(" ", result.Warnings));
        CollectionAssert.AreEquivalent(new[] { "root", "child" }, result.Moved.Select(static value => value.SessionId).ToArray());
        var store = f.Journal.CreateSessionStore();
        foreach (var id in new[] { "root", "child" })
        {
            var header = await f.Journal.ReadHeaderAsync(id, CreatedAt);
            Assert.AreEqual(f.Source.Id, header!.ProjectRef, "The session stays in its project.");
            Assert.AreEqual(newPath, header.WorkingDirectory);
            Assert.AreEqual(newPath, (await store.GetSessionSummaryAsync(id))!.WorkingDirectory);
        }
        Assert.AreEqual("Root", (await store.GetSessionSummaryAsync("root"))!.Title, "Its title stays.");
        Assert.AreEqual(f.Target.ProjectPath, (await f.Journal.ReadHeaderAsync("other", CreatedAt))!.WorkingDirectory);
        CollectionAssert.AreEqual(new[] { (oldPath, newPath) }, relocations, "What the provider keeps follows the folder.");
        var notice = f.Runtime.TakeMovedNotice("root");
        StringAssert.Contains(notice, oldPath);
        StringAssert.Contains(notice, newPath);
        Assert.IsNotNull(ProjectCatalog.ResolveSessionProject(f.Source.Id, newPath, await f.Projects.LoadAsync()), "The lists find the session in its project again.");

        var again = await f.Runtime.FollowMovedProjectFoldersAsync(static _ => false);
        Assert.AreEqual(("ok", 0), (again.Status, again.Moved.Count), "Nothing is left to bind.");
    }

    [TestMethod]
    public async Task Host_FollowsAProjectWhoseFileNamesANewFolder_WhenTheSessionListFindsItsSessions()
    {
        using var temp = new TempDirectory();
        var global = Directory.CreateDirectory(Path.Combine(temp.Path, "global")).FullName;
        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "docs-old")).FullName;
        var provider = new AccountProvider();
        await using var host = await CodeAlta.Orchestration.Hosting.CodeAltaHost.CreateAsync(new()
        {
            GlobalRoot = global, CurrentProjectPath = source, IsHeadless = true, AutoApproveOwnedPermissions = true, OwnedCommandReceiptCapacity = 8,
            ConfigureModelProviders = registry => registry.RegisterOrReplace(provider.Descriptor, () => provider),
        }, CancellationToken.None);
        var sessionId = (await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, "Review the docs")).SessionId;
        host.FollowMovedProjectFolders();
        await host.MovedProjectFoldersPass.WaitAsync(TestDeadlines.Hang);

        // The folder is moved, and the project file is written by hand: no change of the catalog is told.
        var target = Path.Combine(temp.Path, "docs");
        Directory.Move(source, target);
        var project = (await host.ProjectCatalog.GetByIdAsync(host.CurrentProject.Id))!.Copy();
        project.ProjectPath = target;
        await new ProjectCatalog(host.CatalogOptions).SaveAsync(project);

        // The session list finds the session of the project in the folder that is gone, and the host binds it to the new one.
        _ = await host.WorkspaceReads.ReadSnapshotAsync(CancellationToken.None);
        await host.MovedProjectFoldersPass.WaitAsync(TestDeadlines.Hang);
        var snapshot = await host.WorkspaceReads.ReadSnapshotAsync(CancellationToken.None);
        var listed = snapshot.Sessions.Single(session => session.SessionId == sessionId);
        Assert.AreEqual(target, listed.WorkspacePath);
        Assert.AreEqual(project.Id, ProjectCatalog.ResolveSessionProject(snapshot.SessionHeaders[sessionId].ProjectRef, listed.WorkspacePath, snapshot.Projects)?.Id);
    }

    [TestMethod]
    public async Task FollowMovedProjectFoldersAsync_LeavesASessionAtWork_AndOneWhoseFolderStillExists()
    {
        await using var f = await Fixture.CreateAsync();
        await f.CreateSessionAsync("busy", f.Source, title: "Busy");
        var oldPath = f.Source.ProjectPath;
        var copy = Directory.CreateDirectory(Path.Combine(f.Root, "copy")).FullName;
        var moved = f.Source.Copy();
        moved.ProjectPath = copy;
        await f.Projects.SaveAsync(moved);

        var kept = await f.Runtime.FollowMovedProjectFoldersAsync(static _ => false);
        Assert.AreEqual(0, kept.Moved.Count, "A folder that still exists may be the one the session works in: it is left.");

        Directory.Delete(oldPath);
        var atWork = await f.Runtime.FollowMovedProjectFoldersAsync(static id => id == "busy");
        Assert.AreEqual(("move_partial", 0), (atWork.Status, atWork.Moved.Count), "A session at work is left for a later pass.");
        Assert.AreEqual(oldPath, (await f.Journal.ReadHeaderAsync("busy", CreatedAt))!.WorkingDirectory);

        var later = await f.Runtime.FollowMovedProjectFoldersAsync(static _ => false);
        Assert.AreEqual(("ok", 1), (later.Status, later.Moved.Count));
        Assert.AreEqual(copy, (await f.Journal.ReadHeaderAsync("busy", CreatedAt))!.WorkingDirectory);
    }

    [TestMethod]
    public void FindMovedSessionProject_IsTheRecordedProject_WhoseFolderChanged_WhenTheRecordedFolderIsGone()
    {
        var project = new ProjectDescriptor { Id = "docs", Slug = "docs", Name = "docs", ProjectPath = "/work/docs" };
        var archived = new ProjectDescriptor { Id = "old", Slug = "old", Name = "old", ProjectPath = "/work/old-new", Archived = true };
        ProjectDescriptor[] projects = [project, archived];
        var existing = new HashSet<string>(StringComparer.Ordinal) { "/work/docs", "/work/old-new", "/work/still-here" };
        bool Exists(string path) => existing.Contains(path);

        Assert.AreSame(project, ProjectCatalog.FindMovedSessionProject("docs", "/work/docs-old", projects, Exists));
        Assert.IsNull(ProjectCatalog.FindMovedSessionProject("docs", "/work/docs", projects, Exists), "A session in the folder of its project did not move.");
        Assert.IsNull(ProjectCatalog.FindMovedSessionProject("docs", "/work/still-here", projects, Exists), "A folder that exists keeps its sessions.");
        Assert.IsNull(ProjectCatalog.FindMovedSessionProject("DOCS", "/work/docs-old", projects, Exists), "Another identifier names another project.");
        Assert.IsNull(ProjectCatalog.FindMovedSessionProject("old", "/work/old", projects, Exists), "An archived project takes no session.");
        Assert.IsNull(ProjectCatalog.FindMovedSessionProject("missing", "/work/docs-old", projects, Exists));
        existing.Remove("/work/docs");
        Assert.IsNull(ProjectCatalog.FindMovedSessionProject("docs", "/work/docs-old", projects, Exists), "A project whose folder is gone too takes nothing.");
    }

    [TestMethod]
    public async Task MovedProjectFolders_RunsOnePassAtATime_AndOneMoreForWhatCameMeanwhile()
    {
        var runs = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var follower = new MovedProjectFolders(async () =>
        {
            Interlocked.Increment(ref runs);
            entered.TrySetResult();
            await gate.Task;
            return new OwnedSessionMoveResult("ok", [], []);
        });

        follower.Follow();
        // Requested once the pass reads the sessions: a request made before it starts is that very pass.
        await entered.Task.WaitAsync(TestDeadlines.Hang);
        follower.Follow();
        follower.Found(["a|/old"]);
        gate.SetResult();
        await follower.Current.WaitAsync(TestDeadlines.Hang);
        Assert.AreEqual(2, runs, "The requests made while a pass ran give one more pass.");

        follower.Found(["a|/old"]);
        await follower.Current.WaitAsync(TestDeadlines.Hang);
        Assert.AreEqual(2, runs, "A session already looked at starts no pass by itself.");
        follower.Found(["a|/old", "b|/old"]);
        await follower.Current.WaitAsync(TestDeadlines.Hang);
        Assert.AreEqual(3, runs);
        follower.ProjectChanged();
        await follower.Current.WaitAsync(TestDeadlines.Hang);
        follower.Found(["a|/old"]);
        await follower.Current.WaitAsync(TestDeadlines.Hang);
        Assert.AreEqual(5, runs, "A change of a project looks at every session again.");
    }

    [TestMethod]
    public async Task MovedProjectFolders_TriesAgainWhenAnotherChangeOfTheCatalogHeldIt()
    {
        var statuses = new Queue<string>(["busy", "busy", "ok"]);
        var follower = new MovedProjectFolders(() => Task.FromResult(new OwnedSessionMoveResult(statuses.Dequeue(), [], [])), static _ => Task.CompletedTask);
        follower.Follow();
        await follower.Current.WaitAsync(TestDeadlines.Hang);
        Assert.AreEqual(0, statuses.Count);
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
            ProjectCatalog projects, ProjectDescriptor source, ProjectDescriptor target)
        {
            Projects = projects;
            _temp = temp;
            _hub = hub;
            Runtime = runtime;
            Journal = journal;
            Source = source;
            Target = target;
        }

        public SessionRuntimeService Runtime { get; }

        public ProjectCatalog Projects { get; }

        public string Root => _temp.Path;

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
            return new Fixture(temp, hub, runtime, sessionViewCatalog.JournalStore, projects, source, target);
        }

        public async Task CreateSessionAsync(string id, ProjectDescriptor project, string? title, string? parent = null, string provider = "codex",
            string? summaryFamily = null, string? stateFamily = null)
        {
            var session = new SessionViewDescriptor
            {
                SessionId = id, Kind = SessionViewKind.ProjectSession, ProviderId = provider, ProviderKey = provider, ProjectRef = project.Id,
                ParentSessionId = parent, WorkingDirectory = project.ProjectPath, Title = title ?? project.DisplayName,
                Status = SessionViewStatus.Active, CreatedAt = CreatedAt, UpdatedAt = CreatedAt, LastActiveAt = CreatedAt,
            };
            await Journal.EnsureHeaderAsync(session);
            var store = Journal.CreateSessionStore();
            await store.UpsertSessionAsync(new() { SessionId = id, ProviderId = new(provider), ProviderKey = provider, ProtocolFamily = summaryFamily ?? provider,
                WorkingDirectory = project.ProjectPath, Title = title ?? project.DisplayName, ParentSessionId = parent,
                CreatedAt = CreatedAt, UpdatedAt = CreatedAt });
            await store.UpsertStateAsync(new() { SessionId = id, ProviderKey = provider, ProtocolFamily = stateFamily ?? provider, ProviderSessionId = "opaque", UpdatedAt = CreatedAt });
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

    // A provider whose key is not its type: a second Claude Code account by default.
    private sealed class AccountProvider(string key = "claude-business", string family = "claude-code") : IAgentModelProviderRuntime, IModelProviderTurnExecutor
    {
        public ModelProviderDescriptor Descriptor { get; } = new(new ModelProviderId(key), key, family) { DefaultModelId = "fake-model" };

        public ModelProviderRuntimeDescriptor RuntimeDescriptor { get; } = new()
        {
            ProtocolFamily = family, ProviderKey = key, DisplayName = key, TransportKind = AgentTransportKind.OpenAIResponses,
        };

        public IModelProviderModelCatalog? ModelCatalog => null;

        public AgentRuntimeProviderRegistration CreateProviderRegistration() => new() { Provider = RuntimeDescriptor, TurnExecutor = this };

        public IModelProviderTurnExecutor CreateTurnExecutor() => this;

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<ModelProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ModelProviderProbeResult
            {
                ProviderId = Descriptor.ProviderId, Availability = ModelProviderAvailability.Ready,
                Models = [new AgentModelInfo("fake-model", DisplayName: "Fake Model")], SelectedModelId = "fake-model",
            });

        // A turn that fails, as the first one of an account that is not logged in yet.
        public bool Fails { get; init; }

        public Task<AgentTurnResponse> ExecuteTurnAsync(AgentTurnRequest request, Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate, CancellationToken cancellationToken = default)
            => Fails ? throw new InvalidOperationException("Not logged in.") : Task.FromResult(new AgentTurnResponse
            {
                AssistantMessage = new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.Text("Done.")]),
            });

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
