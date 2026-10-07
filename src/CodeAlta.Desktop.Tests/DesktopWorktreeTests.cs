using System.Collections.Frozen;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Worktrees;
using CodeAlta.Desktop.Rpc;
using CodeAlta.LiveTool;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// The worktrees of a project as the window uses them, on real repositories made with the git on the path: a
/// session created in a new worktree, the checkouts of a repository listed and removed, a checkout moved to
/// another branch, and where new worktrees go. Inconclusive on a machine without git.
/// </summary>
[TestClass]
public sealed class DesktopWorktreeTests
{
    private const string Epoch = "11111111-1111-4111-8111-111111111111";

    [TestMethod]
    public async Task CreateSession_InANewWorktree_MakesTheCheckoutAndListsWhereTheSessionWorks()
    {
        using var repository = await Repository.CreateAsync();
        repository.Git("branch", "feature");
        var provider = new FixtureProvider();
        await using var host = await repository.HostAsync(provider);
        var project = await host.ProjectCatalog.UpsertFromPathAsync(repository.Root);
        var plain = await host.ProjectCatalog.UpsertFromPathAsync(Directory.CreateDirectory(Path.Combine(repository.Home, "plain")).FullName);
        var worktrees = new GitWorktreeService(host.CatalogOptions, new CodeAltaConfigStore(host.CatalogOptions));
        var service = new WorkspaceService(host, Epoch, worktrees);
        try
        {
            var request = new WorkspaceCreateSessionRequest(Epoch, "project", project.Id, project.ProjectPath, "In a worktree", null, Worktree: true);
            Assert.AreEqual(request with { BaseBranch = "feature" }, JsonSerializer.Deserialize(
                JsonSerializer.Serialize(request with { BaseBranch = "feature" }, DesktopJsonContext.Default.WorkspaceCreateSessionRequest), DesktopJsonContext.Default.WorkspaceCreateSessionRequest));

            var created = await service.CreateSessionAsync(request, CancellationToken.None);

            Assert.AreEqual("ok", created.Status, created.Reason + " " + created.Message);
            Assert.AreEqual(created, JsonSerializer.Deserialize(JsonSerializer.Serialize(created, DesktopJsonContext.Default.WorkspaceCreateSessionResponse), DesktopJsonContext.Default.WorkspaceCreateSessionResponse));
            // The session is one of its project, and works in a checkout of its own under the folder of the catalog.
            Assert.AreEqual(project.ProjectPath, created.WorkspacePath);
            var worktree = created.WorktreePath!;
            Assert.AreEqual(Path.Combine(host.CatalogOptions.WorktreesRoot, project.Slug), Path.GetDirectoryName(worktree));
            Assert.IsTrue(File.Exists(Path.Combine(worktree, "readme.md")));
            StringAssert.Contains(repository.Git("branch", "--list", GitWorktreeService.BranchPrefix + Path.GetFileName(worktree)), Path.GetFileName(worktree));

            var listed = (await service.SnapshotAsync(new(), CancellationToken.None)).Sessions.Single(session => session.Id == created.SessionId);
            Assert.AreEqual(("project", project.Id, project.ProjectPath), (listed.ScopeKind, listed.ProjectId, listed.WorkspacePath));
            Assert.AreEqual((worktree, worktree, Path.GetFileName(worktree), false), (listed.WorktreePath, listed.WorktreeRoot, listed.WorktreeName, listed.WorktreeMissing));
            Assert.AreEqual(listed, JsonSerializer.Deserialize(JsonSerializer.Serialize(listed, DesktopJsonContext.Default.WorkspaceSession), DesktopJsonContext.Default.WorkspaceSession));

            // A worktree starts from the commit the folder of the project is on, or from the branch that is named.
            var based = await service.CreateSessionAsync(request with { BaseBranch = "feature" }, CancellationToken.None);
            Assert.AreEqual("ok", based.Status, based.Reason + " " + based.Message);
            Assert.AreNotEqual(worktree, based.WorktreePath);
            Assert.HasCount(3, (await worktrees.ListAsync(repository.Root))!);

            // A session without one works in the folder of its project, as before.
            var ordinary = await service.CreateSessionAsync(request with { Worktree = false }, CancellationToken.None);
            Assert.AreEqual(("ok", null), (ordinary.Status, ordinary.WorktreePath));
            Assert.IsNull((await service.SnapshotAsync(new(), CancellationToken.None)).Sessions.Single(session => session.Id == ordinary.SessionId).WorktreePath);

            // No checkout, no session: git says why, and nothing is created.
            var sessions = (await service.SnapshotAsync(new(), CancellationToken.None)).Sessions.Length;
            var outside = await service.CreateSessionAsync(request with { ProjectId = plain.Id, ProjectPath = plain.ProjectPath }, CancellationToken.None);
            Assert.AreEqual(("worktree_failed", "not_repository", null), (outside.Status, outside.Reason, outside.SessionId));
            var unknown = await service.CreateSessionAsync(request with { BaseBranch = "no-such-branch" }, CancellationToken.None);
            Assert.AreEqual(("worktree_failed", "invalid", null), (unknown.Status, unknown.Reason, unknown.SessionId));
            Assert.HasCount(sessions, (await service.SnapshotAsync(new(), CancellationToken.None)).Sessions);
            Assert.HasCount(3, (await worktrees.ListAsync(repository.Root))!);
            // A worktree is a checkout of a project; a base is a name, and only that of a worktree.
            foreach (var invalid in new[]
                     {
                         new WorkspaceCreateSessionRequest(Epoch, "global", null, null, null, null, Worktree: true),
                         request with { BaseBranch = "--force" },
                         request with { Worktree = false, BaseBranch = "feature" },
                     })
                Assert.AreEqual("invalid_scope", (await service.CreateSessionAsync(invalid, CancellationToken.None)).Status);
            // A window that was given no worktrees creates none.
            Assert.AreEqual("unconfigured", (await new WorkspaceService(host.WorkspaceReads, host.ProjectCatalog, Epoch, host.ModelProviderRegistry,
                (target, descriptor, title) => host.Commands.CreateDraftSessionAsync(target, descriptor, title)).CreateSessionAsync(request, CancellationToken.None)).Status);

            // The folder is removed behind the window: the session still names its worktree, and says that it is gone.
            Repository.Delete(worktree);
            var gone = (await service.SnapshotAsync(new(), CancellationToken.None)).Sessions.Single(session => session.Id == created.SessionId);
            Assert.AreEqual((worktree, worktree, Path.GetFileName(worktree), true), (gone.WorktreePath, gone.WorktreeRoot, gone.WorktreeName, gone.WorktreeMissing));
        }
        finally
        {
            await service.CloseSessionsAsync();
            await service.CloseImportsAsync();
        }
    }

    [TestMethod]
    public async Task Worktrees_AreListedAndRemoved_UnlessASessionIsAtWorkInThem()
    {
        using var repository = await Repository.CreateAsync();
        var project = await repository.Projects.UpsertFromPathAsync(repository.Root);
        var busy = new List<SessionWorkFolder>();
        var service = repository.Service(() => [.. busy]);
        var first = await repository.Worktrees.CreateAsync(project);
        var second = await repository.Worktrees.CreateAsync(project);
        Assert.AreEqual((GitWorktreeService.Ok, GitWorktreeService.Ok), (first.Status, second.Status));

        var list = await service.ListAsync(new(Epoch, project.Id), default);

        Assert.AreEqual(("ok", project.Id, Path.Combine(repository.Options.WorktreesRoot, project.Slug)), (list.Status, list.ProjectId, list.NewFolder));
        Assert.AreEqual(list.Worktrees.Length, JsonSerializer.Deserialize(JsonSerializer.Serialize(list, DesktopJsonContext.Default.WorktreesListResponse), DesktopJsonContext.Default.WorktreesListResponse)!.Worktrees.Length);
        // The folder of the project first: it is the one that is not removed.
        Assert.AreEqual(new WorktreeItem(repository.Root, Path.GetFileName(repository.Root), "main", list.Worktrees[0].Head, true, false, false, repository.Root, false), list.Worktrees[0]);
        Assert.AreEqual(7, list.Worktrees[0].Head!.Length);
        // The worktrees follow, in the order of git: by their folders.
        Assert.AreEqual(new WorktreeItem(first.Root!, first.Name!, first.Branch, list.Worktrees[0].Head, false, false, false, first.Root!, false),
            list.Worktrees.Single(worktree => worktree.Path == first.Root));
        CollectionAssert.AreEqual(new[] { first.Root, second.Root }.Order(StringComparer.Ordinal).ToArray(), list.Worktrees.Skip(1).Select(static worktree => worktree.Path).ToArray());

        // A session at work in a worktree keeps it: neither removed nor moved to another branch.
        busy.Add(new("session-1", Path.Combine(first.Root!, "src"), true));
        var working = await service.ListAsync(new(Epoch, project.Id), default);
        CollectionAssert.AreEqual(new[] { first.Root }, working.Worktrees.Where(static worktree => worktree.Busy).Select(static worktree => worktree.Path).ToArray());
        Assert.AreEqual(new WorktreeChangeResponse("in_use"), await service.RemoveAsync(new(Epoch, project.Id, first.Root, Force: true), default));
        Assert.AreEqual(new WorktreeChangeResponse("in_use"), await service.SwitchAsync(new(Epoch, project.Id, first.Root, "main-2", Create: true), default));
        Assert.IsTrue((await service.BranchesAsync(new(Epoch, project.Id, first.Root), default)).Busy);
        Assert.IsFalse((await service.BranchesAsync(new(Epoch, project.Id), default)).Busy);
        Assert.IsTrue(Directory.Exists(first.Root));
        busy.Clear();

        // What is not committed is not thrown away without being asked for.
        File.WriteAllText(Path.Combine(first.Root!, "draft.txt"), "not committed");
        Assert.AreEqual(new WorktreeChangeResponse("dirty"), await service.RemoveAsync(new(Epoch, project.Id, first.Root), default));
        Assert.IsTrue(Directory.Exists(first.Root));
        var removed = await service.RemoveAsync(new(Epoch, project.Id, first.Root, Force: true), default);
        Assert.AreEqual(new WorktreeChangeResponse("ok"), removed);
        Assert.AreEqual(removed, JsonSerializer.Deserialize(JsonSerializer.Serialize(removed, DesktopJsonContext.Default.WorktreeChangeResponse), DesktopJsonContext.Default.WorktreeChangeResponse));
        Assert.IsFalse(Directory.Exists(first.Root));

        // A branch that holds commits of its own stays when its worktree goes.
        File.WriteAllText(Path.Combine(second.Root!, "work.txt"), "work");
        repository.GitIn(second.Root!, "add", "-A");
        repository.GitIn(second.Root!, "commit", "-q", "-m", "work");
        Assert.AreEqual(new WorktreeChangeResponse("ok", null, second.Branch), await service.RemoveAsync(new(Epoch, project.Id, second.Root), default));
        Assert.HasCount(1, (await service.ListAsync(new(Epoch, project.Id), default)).Worktrees);

        // Only a worktree of the project's repository is removed, and never the folder of the project.
        Assert.AreEqual("main", (await service.RemoveAsync(new(Epoch, project.Id, repository.Root, Force: true), default)).Status);
        Assert.AreEqual("not_worktree", (await service.RemoveAsync(new(Epoch, project.Id, repository.Home, Force: true), default)).Status);
        Assert.AreEqual("invalid_request", (await service.RemoveAsync(new(Epoch, project.Id, "relative/folder"), default)).Status);
        Assert.AreEqual("stale_epoch", (await service.RemoveAsync(new("other", project.Id, repository.Root), default)).Status);
        Assert.AreEqual("unknown_project", (await service.ListAsync(new(Epoch, "missing"), default)).Status);
        Assert.AreEqual("invalid_request", (await service.ListAsync(new(Epoch, null), default)).Status);
        Assert.AreEqual("unavailable", (await new WorktreesService().ListAsync(new(Epoch, project.Id), default)).Status);
        var plain = await repository.Projects.UpsertFromPathAsync(Directory.CreateDirectory(Path.Combine(repository.Home, "plain")).FullName);
        Assert.AreEqual("not_repository", (await service.ListAsync(new(Epoch, plain.Id), default)).Status);
        Assert.IsTrue(Directory.Exists(repository.Root));
    }

    [TestMethod]
    public async Task WorktreeInsideTheRepository_IsACheckoutOfItsOwn()
    {
        using var repository = await Repository.CreateAsync();
        repository.Config.SaveGlobalWorktreeSettings(WorktreeSettings.ProjectName, null);
        var project = await repository.Projects.UpsertFromPathAsync(repository.Root);
        var busy = new List<SessionWorkFolder>();
        var service = repository.Service(() => [.. busy]);
        var nested = await repository.Worktrees.CreateAsync(project);
        Assert.AreEqual(Path.Combine(repository.Root, ".alta", "worktrees", nested.Name!), nested.Root);

        // A session at work in the worktree is not at work in the folder of the project that holds it, nor the other way round.
        busy.Add(new("session-1", nested.Folder!, true));
        CollectionAssert.AreEqual(new[] { false, true }, (await service.ListAsync(new(Epoch, project.Id), default)).Worktrees.Select(static worktree => worktree.Busy).ToArray());
        Assert.AreEqual("ok", (await service.SwitchAsync(new(Epoch, project.Id, null, "topic", Create: true), default)).Status);
        busy[0] = new("session-2", repository.Root, false);
        CollectionAssert.AreEqual(new[] { true, false }, (await service.ListAsync(new(Epoch, project.Id), default)).Worktrees.Select(static worktree => worktree.Busy).ToArray());
        Assert.AreEqual("in_use", (await service.SwitchAsync(new(Epoch, project.Id, null, "main"), default)).Status);
        Assert.AreEqual("ok", (await service.RemoveAsync(new(Epoch, project.Id, nested.Root), default)).Status);
    }

    [TestMethod]
    public async Task ProjectBelowTheRepositoryRoot_NamesItsOwnFolderInEachCheckout()
    {
        using var repository = await Repository.CreateAsync();
        repository.Write("src/app/main.txt", "app");
        repository.Commit("app");
        var project = await repository.Projects.UpsertFromPathAsync(Path.Combine(repository.Root, "src", "app"));
        var service = repository.Service(static () => []);
        var created = await repository.Worktrees.CreateAsync(project);

        var list = await service.ListAsync(new(Epoch, project.Id), default);

        // What a request names to read a checkout is the folder of the project in it.
        Assert.AreEqual((repository.Root, true, project.ProjectPath), (list.Worktrees[0].Path, list.Worktrees[0].Main, list.Worktrees[0].Folder));
        Assert.AreEqual((created.Root, false, Path.Combine(created.Root!, "src", "app")), (list.Worktrees[1].Path, list.Worktrees[1].Main, list.Worktrees[1].Folder));
        Assert.AreEqual(created.Folder, list.Worktrees[1].Folder);
        Assert.AreEqual("ok", (await service.BranchesAsync(new(Epoch, project.Id, created.Folder), default)).Status);
        Assert.AreEqual("ok", (await service.RemoveAsync(new(Epoch, project.Id, created.Folder), default)).Status);
    }

    [TestMethod]
    public async Task Branches_AreListedAndACheckoutMovesToOne()
    {
        using var repository = await Repository.CreateAsync();
        repository.Git("branch", "feature");
        var project = await repository.Projects.UpsertFromPathAsync(repository.Root);
        var service = repository.Service(static () => []);
        var created = await repository.Worktrees.CreateAsync(project);

        var branches = await service.BranchesAsync(new(Epoch, project.Id), default);

        Assert.AreEqual("ok", branches.Status);
        Assert.AreEqual(branches.Branches.Length, JsonSerializer.Deserialize(JsonSerializer.Serialize(branches, DesktopJsonContext.Default.WorktreeBranchesResponse), DesktopJsonContext.Default.WorktreeBranchesResponse)!.Branches.Length);
        Assert.AreEqual(new WorktreeBranch("main", true, false, repository.Root, Path.GetFileName(repository.Root)), branches.Branches.Single(static branch => branch.Name == "main"));
        Assert.AreEqual(new WorktreeBranch("feature", false, false, null, null), branches.Branches.Single(static branch => branch.Name == "feature"));
        // A branch is used by one checkout at a time: the one that has it is named.
        Assert.AreEqual(new WorktreeBranch(created.Branch!, false, false, created.Root, created.Name), branches.Branches.Single(branch => branch.Name == created.Branch));
        Assert.AreEqual(created.Branch, (await service.BranchesAsync(new(Epoch, project.Id, created.Root), default)).Branches.Single(static branch => branch.Current).Name);

        Assert.AreEqual(new WorktreeChangeResponse("ok"), await service.SwitchAsync(new(Epoch, project.Id, created.Root, "feature"), default));
        Assert.AreEqual("feature", (await service.ListAsync(new(Epoch, project.Id), default)).Worktrees[1].Branch);
        Assert.AreEqual("ok", (await service.SwitchAsync(new(Epoch, project.Id, null, "topic/new", Create: true), default)).Status);
        Assert.AreEqual("topic/new", (await service.ListAsync(new(Epoch, project.Id), default)).Worktrees[0].Branch);

        // What git refuses comes back with what it said; what is no name is never handed to it.
        var taken = await service.SwitchAsync(new(Epoch, project.Id, null, "feature"), default);
        Assert.AreEqual("failed", taken.Status);
        Assert.IsFalse(string.IsNullOrWhiteSpace(taken.Message));
        Assert.AreEqual("not_found", (await service.SwitchAsync(new(Epoch, project.Id, null, "nowhere"), default)).Status);
        Assert.AreEqual("invalid_request", (await service.SwitchAsync(new(Epoch, project.Id, null, "--force"), default)).Status);
        // Only a checkout of the project's repository is moved.
        using var other = await Repository.CreateAsync();
        Assert.AreEqual("not_worktree", (await service.SwitchAsync(new(Epoch, project.Id, other.Root, "main"), default)).Status);
        Assert.AreEqual("not_worktree", (await service.BranchesAsync(new(Epoch, project.Id, Path.Combine(repository.Home, "nowhere")), default)).Status);
    }

    [TestMethod]
    public async Task AltaCommands_OfASessionInAWorktree_ShowItsChanges_AndLeaveTheEditorToTheProject()
    {
        using var repository = await Repository.CreateAsync();
        var project = await repository.Projects.UpsertFromPathAsync(repository.Root);
        var created = await repository.Worktrees.CreateAsync(project);
        var changes = new DesktopChangesView();
        var editor = new DesktopEditorView();
        var services = new AltaServiceCollection().Add(repository.Projects).Add<IAltaChangesView>(changes).Add<IAltaEditorView>(editor);
        var registry = new AltaCommandRegistry();
        var dispatcher = new AltaCommandDispatcher(registry, services);
        services.Add(registry).Add(dispatcher);
        var session = new AltaCallerIdentity { Kind = "agent", SourceSessionId = "s", SourceProjectId = project.Id };
        var shown = new List<ProjectGitShowEvent>();
        using var watching = changes.Watch(shown.Add);
        var opened = 0;
        using var editing = editor.Watch(_ => opened++);

        // The commands of a session run where the session works: in its worktree, its changes are those of the worktree.
        Assert.AreEqual(AltaExitCodes.Success, (await dispatcher.InvokeAsync(["diff", "show", "--file", "readme.md"], caller: session, cwd: created.Folder)).ExitCode);
        Assert.AreEqual(AltaExitCodes.Success, (await dispatcher.InvokeAsync(["diff", "show"], caller: session, cwd: repository.Root)).ExitCode);
        // A project that is named is shown as it is, and a folder that is no checkout of the repository is not a worktree.
        Assert.AreEqual(AltaExitCodes.Success, (await dispatcher.InvokeAsync(["diff", "show", "--project", project.Id], caller: session, cwd: created.Folder)).ExitCode);
        Assert.AreEqual(AltaExitCodes.Success, (await dispatcher.InvokeAsync(["diff", "show"], caller: session, cwd: repository.Home)).ExitCode);
        CollectionAssert.AreEqual(new ProjectGitShowEvent[] { new(project.Id, "readme.md", created.Folder), new(project.Id, null), new(project.Id, null), new(project.Id, null) }, shown);

        // The code editor shows the folder of the project: the files of a worktree are not shown under its names.
        var refused = await dispatcher.InvokeAsync(["editor", "open", "--file", "readme.md"], caller: session, cwd: created.Folder);
        Assert.AreEqual(AltaExitCodes.Unsupported, refused.ExitCode);
        StringAssert.Contains(refused.Stdout + refused.Stderr, "editor.worktree");
        Assert.AreEqual(0, opened);
        Assert.AreEqual(AltaExitCodes.Success, (await dispatcher.InvokeAsync(["editor", "open", "--file", "readme.md"], caller: session, cwd: repository.Root)).ExitCode);
        Assert.AreEqual(AltaExitCodes.Success, (await dispatcher.InvokeAsync(["editor", "open", "--file", "readme.md", "--project", project.Id], caller: session, cwd: created.Folder)).ExitCode);
        Assert.AreEqual(2, opened);
    }

    [TestMethod]
    public async Task Settings_ChooseWhereNewWorktreesGo()
    {
        using var repository = await Repository.CreateAsync();
        var project = await repository.Projects.UpsertFromPathAsync(repository.Root);
        var service = repository.Service(static () => []);
        var custom = Path.Combine(repository.Home, "trees");
        File.WriteAllText(repository.Options.ConfigPath, "[chat]\ndefault_provider = \"codex\"\n");

        Assert.AreEqual(new WorktreeSettingsResponse("ok", "global", null, repository.Options.WorktreesRoot), await service.SettingsAsync(new(Epoch), default));

        var saved = await service.SaveSettingsAsync(new(Epoch, "custom", custom), default);
        Assert.AreEqual(new WorktreeSettingsResponse("ok", "custom", custom, repository.Options.WorktreesRoot), saved);
        Assert.AreEqual(saved, JsonSerializer.Deserialize(JsonSerializer.Serialize(saved, DesktopJsonContext.Default.WorktreeSettingsResponse), DesktopJsonContext.Default.WorktreeSettingsResponse));
        Assert.AreEqual(Path.Combine(custom, project.Slug), (await service.ListAsync(new(Epoch, project.Id), default)).NewFolder);
        StringAssert.Contains(File.ReadAllText(repository.Options.ConfigPath), "default_provider = \"codex\"");

        // Inside each project, where git ignores them; the folder that was chosen is kept for the next time.
        Assert.AreEqual(new WorktreeSettingsResponse("ok", "project", custom, repository.Options.WorktreesRoot), await service.SaveSettingsAsync(new(Epoch, "project", custom), default));
        Assert.AreEqual(Path.Combine(repository.Root, ".alta", "worktrees"), (await service.ListAsync(new(Epoch, project.Id), default)).NewFolder);
        Assert.AreEqual("global", (await service.SaveSettingsAsync(new(Epoch, "global", null), default)).Location);

        // A place of the user's own needs a folder, an absolute one.
        foreach (var (location, folder) in new (string?, string?)[] { ("custom", null), ("custom", "relative/trees"), ("elsewhere", custom), (null, custom) })
            Assert.AreEqual(("invalid_request", "global"), await Save(location, folder), $"{location} {folder}");
        Assert.AreEqual("stale_epoch", (await service.SaveSettingsAsync(new("other", "project", null), default)).Status);
        Assert.AreEqual("unavailable", (await new WorktreesService().SettingsAsync(new(Epoch), default)).Status);

        async Task<(string, string?)> Save(string? location, string? folder)
        {
            var reply = await service.SaveSettingsAsync(new(Epoch, location, folder), default);
            return (reply.Status, reply.Location);
        }
    }

    [TestMethod]
    public void Snapshot_NamesTheWorktreeASessionRecords()
    {
        var created = new DateTimeOffset(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);
        AgentSessionMetadata Session(string id, string? worktree) => new(id, created, created, id, WorkspacePath: "/work/app", ProviderKey: "codex", WorktreePath: worktree);
        var projects = new[] { new ProjectDescriptor { Id = "p", Slug = "app", Name = "App", DisplayName = "App", ProjectPath = "/work/app" } };
        var seen = new List<string>();

        var snapshot = WorkspaceService.ProjectSnapshot(projects,
            [Session("plain", null), Session("blank", " "), Session("tree", "/trees/app/quiet-heron/src"), Session("gone", "/trees/app/amber-denali"), Session("odd", "/trees/\u0001")],
            worktree: folder => { seen.Add(folder); return folder.EndsWith("src", StringComparison.Ordinal) ? ("/trees/app/quiet-heron", "quiet-heron", false) : (folder, "amber-denali", true); });

        WorkspaceSession Row(string id) => snapshot.Sessions.Single(session => session.Id == id);
        Assert.AreEqual((null, null, null, false), (Row("plain").WorktreePath, Row("plain").WorktreeRoot, Row("plain").WorktreeName, Row("plain").WorktreeMissing));
        Assert.IsNull(Row("blank").WorktreePath);
        Assert.AreEqual(("/trees/app/quiet-heron/src", "/trees/app/quiet-heron", "quiet-heron", false), (Row("tree").WorktreePath, Row("tree").WorktreeRoot, Row("tree").WorktreeName, Row("tree").WorktreeMissing));
        Assert.AreEqual(("/trees/app/amber-denali", "/trees/app/amber-denali", "amber-denali", true), (Row("gone").WorktreePath, Row("gone").WorktreeRoot, Row("gone").WorktreeName, Row("gone").WorktreeMissing));
        // The session belongs to the folder it records, whatever checkout it works in.
        Assert.IsTrue(snapshot.Sessions.All(static session => session.WorkspacePath == "/work/app"));
        // A folder with a character that is none of a path is not looked at.
        Assert.IsNull(Row("odd").WorktreePath);
        CollectionAssert.AreEquivalent(new[] { "/trees/app/quiet-heron/src", "/trees/app/amber-denali" }, seen);
    }

    [TestMethod]
    public async Task DescribeWorktree_FindsTheCheckoutOfAFolder_AndSaysWhenItIsGone()
    {
        using var repository = await Repository.CreateAsync();
        repository.Write("src/app/main.txt", "app");
        repository.Commit("app");
        var project = await repository.Projects.UpsertFromPathAsync(Path.Combine(repository.Root, "src", "app"));
        var created = await repository.Worktrees.CreateAsync(project);

        // The session of a project below the root works below the root of its worktree: the worktree is named, not that folder.
        Assert.AreEqual((created.Root!, created.Name!, false), WorkspaceService.DescribeWorktree(created.Folder!));
        Repository.Delete(created.Root!);
        // A folder that is gone is in no checkout: it is not taken for one of another.
        Assert.AreEqual((created.Folder!, "app", true), WorkspaceService.DescribeWorktree(created.Folder!));
    }

    private sealed class Repository : IDisposable
    {
        private Repository(string home)
        {
            Home = home;
            Root = Directory.CreateDirectory(Path.Combine(home, "repository")).FullName;
            Options = new CatalogOptions { GlobalRoot = Directory.CreateDirectory(Path.Combine(home, "global")).FullName };
            Projects = new ProjectCatalog(Options);
            Config = new CodeAltaConfigStore(Options);
            Worktrees = new GitWorktreeService(Options, Config, RunAsync);
        }

        public static Task<Repository> CreateAsync()
        {
            // The real path: git answers with it, and a temporary folder may be reached through a link.
            var home = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "CodeAlta-desktop-worktrees-" + Guid.NewGuid().ToString("N"))).FullName;
            if (Directory.ResolveLinkTarget(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), returnFinalTarget: true) is { } target)
                home = Path.Combine(target.FullName, Path.GetFileName(home));
            var repository = new Repository(home);
            try
            {
                repository.Git("init", "-q", "-b", "main");
            }
            catch (Win32Exception)
            {
                repository.Dispose();
                Assert.Inconclusive("git is not installed.");
            }

            foreach (var (name, value) in new[] { ("user.name", "Test"), ("user.email", "test@example.invalid"), ("core.autocrlf", "false"), ("commit.gpgsign", "false") })
                repository.Git("config", name, value);
            repository.Write("readme.md", "one");
            repository.Commit("one");
            return Task.FromResult(repository);
        }

        public string Home { get; }
        public string Root { get; }
        public CatalogOptions Options { get; }
        public ProjectCatalog Projects { get; }
        public CodeAltaConfigStore Config { get; }
        public GitWorktreeService Worktrees { get; }

        public WorktreesService Service(Func<IReadOnlyList<SessionWorkFolder>> busy) => new(Worktrees, Projects, Config, busy, Epoch);

        /// <summary>A host over the catalog of this repository, with a provider that answers at once.</summary>
        public Task<CodeAltaHost> HostAsync(FixtureProvider provider)
        {
            var home = Directory.CreateDirectory(Path.Combine(Home, "home")).FullName;
            var builtin = Directory.CreateDirectory(Path.Combine(Home, "builtin")).FullName;
            return CodeAltaHost.CreateAsync(new CodeAltaHostOptions
            {
                GlobalRoot = Options.GlobalRoot, CurrentProjectPath = Root, DiscoveryScope = new(home, Home), BuiltInSkillRoot = builtin,
                PluginEnvironment = FrozenDictionary<string, string?>.Empty, StartPlugins = false, IsHeadless = true, OwnsLogging = false,
                ConfigureModelProviders = registry => registry.RegisterOrReplace(provider.Descriptor, () => provider),
            });
        }

        public void Write(string relative, string content)
        {
            var path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public void Commit(string message)
        {
            Git("add", "-A");
            Git("commit", "-q", "-m", message);
        }

        public string Git(params string[] arguments) => GitIn(Root, arguments);

        public string GitIn(string folder, params string[] arguments)
        {
            using var process = Process.Start(Start(folder, arguments))!;
            var error = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            Assert.AreEqual(0, process.ExitCode, $"git {string.Join(' ', arguments)}: {error.Result}");
            return output;
        }

        // What the service runs, with nothing of the machine's or the user's configuration deciding the outcome.
        private async Task<GitRun> RunAsync(string folder, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            using var process = Process.Start(Start(folder, arguments))!;
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return new(process.ExitCode, await output, await error);
        }

        private ProcessStartInfo Start(string folder, IReadOnlyList<string> arguments)
        {
            var start = new ProcessStartInfo("git") { WorkingDirectory = folder, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
            start.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(Home, "no-global-config");
            start.Environment["LC_ALL"] = "C";
            foreach (var inherited in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE" }) start.Environment.Remove(inherited);
            return start;
        }

        public static void Delete(string folder)
        {
            // Git marks its objects read-only.
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(folder, recursive: true);
        }

        public void Dispose()
        {
            try
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                Delete(Home);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup of a temporary directory.
            }
        }
    }

    private sealed class FixtureProvider : IAgentModelProviderRuntime, IModelProviderTurnExecutor
    {
        public ModelProviderDescriptor Descriptor { get; } = new(new ModelProviderId("worktree-fixture"), "Worktree fixture") { IsDefault = true, DefaultModelId = "fixture-model" };

        public ModelProviderRuntimeDescriptor RuntimeDescriptor { get; } = new()
        {
            ProtocolFamily = "test", ProviderKey = "worktree-fixture", DisplayName = "Worktree fixture", TransportKind = AgentTransportKind.OpenAIResponses,
        };

        public IModelProviderModelCatalog? ModelCatalog => null;

        public AgentRuntimeProviderRegistration CreateProviderRegistration() => new() { Provider = RuntimeDescriptor, TurnExecutor = this };

        public IModelProviderTurnExecutor CreateTurnExecutor() => this;

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<ModelProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ModelProviderProbeResult
            {
                ProviderId = Descriptor.ProviderId,
                Availability = ModelProviderAvailability.Ready,
                Models = [new AgentModelInfo("fixture-model", DisplayName: "Fixture model")],
                SelectedModelId = "fixture-model",
            });

        public Task<AgentTurnResponse> ExecuteTurnAsync(AgentTurnRequest request, Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate, CancellationToken cancellationToken = default)
            => Task.FromResult(new AgentTurnResponse
            {
                AssistantMessage = new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.Text("done")]),
            });

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
