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

    [TestMethod]
    public async Task Inventory_ListsEveryCheckoutGitKnows_WhetherASessionRecordsItOrNot()
    {
        using var repository = await Repository.CreateAsync();
        var project = await repository.Projects.UpsertFromPathAsync(repository.Root);
        var used = await repository.Worktrees.CreateAsync(project);
        // The session that made this one was deleted: nothing of the catalog records it, and it is on the disk all the same.
        var orphan = await repository.Worktrees.CreateAsync(project);
        var stale = await repository.Worktrees.CreateAsync(project);
        Repository.Delete(stale.Root!);
        var inside = Directory.CreateDirectory(Path.Combine(used.Root!, "src")).FullName;
        var day = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
        var sessions = new List<AgentSessionMetadata>
        {
            Recorded("in-project", day, repository.Root),
            Recorded("older", day.AddDays(1), repository.Root, used.Root),
            Recorded("newer", day.AddDays(2), repository.Root, inside),
            Recorded("of-stale", day.AddDays(3), repository.Root, stale.Root),
            // Neither a folder outside the repository nor a worktree that is gone from inside it is taken for a checkout that is there.
            Recorded("elsewhere", day.AddDays(4), repository.Home),
            Recorded("removed-inside", day.AddDays(5), repository.Root, Path.Combine(repository.Root, ".alta", "worktrees", "gone")),
            Recorded("odd", day.AddDays(6), repository.Root, "relative\u0001folder"),
        };
        var busy = new List<SessionWorkFolder>();
        var service = repository.Manager(() => [.. busy], () => sessions);

        var inventory = await service.InventoryAsync(new(Epoch, project.Id), default);

        Assert.AreEqual(("ok", project.Id, true, false), (inventory.Status, inventory.ProjectId, inventory.SessionsKnown, inventory.Truncated));
        WorktreeInventoryItem Item(WorktreeInventoryResponse response, string path) => response.Worktrees.Single(worktree => worktree.Path == path);
        string[] Ids(WorktreeInventoryItem item) => [.. item.Sessions.Select(static session => session.Id)];
        Assert.AreEqual(repository.Root, inventory.Worktrees[0].Path);
        CollectionAssert.AreEquivalent(new[] { repository.Root, used.Root, orphan.Root, stale.Root }, inventory.Worktrees.Select(static worktree => worktree.Path).ToArray());
        // The folder of the project is the checkout that is never removed.
        var main = inventory.Worktrees[0];
        Assert.AreEqual((true, true, "main", false, false, "main", repository.Root), (main.Main, main.Project, main.Protection, main.Missing, main.Busy, main.Branch, main.Folder));
        CollectionAssert.AreEqual(new[] { "in-project" }, Ids(main));
        Assert.AreEqual((1, (DateTimeOffset?)day), (main.SessionCount, main.LastUsedAt));
        // A worktree is last used when the most recent session that records it was: the one in a folder inside it counts.
        var first = Item(inventory, used.Root!);
        Assert.AreEqual((false, false, false, false, used.Branch, used.Root, 7), (first.Main, first.Project, first.Locked, first.Missing, first.Branch, first.Folder, first.Head!.Length));
        CollectionAssert.AreEqual(new[] { "newer", "older" }, Ids(first));
        Assert.AreEqual((2, (DateTimeOffset?)day.AddDays(2), "newer", false), (first.SessionCount, first.LastUsedAt, first.Sessions[0].Title, first.Sessions[0].Running));
        // What sessions recorded in the past does not say that a checkout is in use: nothing protects it.
        Assert.AreEqual((false, null), (first.Busy, first.Protection));
        // A worktree no session records is listed like the others, and when it was last used is not known.
        var second = Item(inventory, orphan.Root!);
        Assert.AreEqual((false, false, null, 0, null, 0), (second.Missing, second.Busy, second.Protection, second.SessionCount, second.LastUsedAt, second.Sessions.Length));
        // One whose folder is gone is still what git lists: it is shown as such, with the session that records it.
        var third = Item(inventory, stale.Root!);
        Assert.AreEqual((true, false, null, 1, (DateTimeOffset?)day.AddDays(3)), (third.Missing, third.Busy, third.Protection, third.SessionCount, third.LastUsedAt));
        CollectionAssert.AreEqual(new[] { "of-stale" }, Ids(third));
        var wire = JsonSerializer.Deserialize(JsonSerializer.Serialize(inventory, DesktopJsonContext.Default.WorktreeInventoryResponse), DesktopJsonContext.Default.WorktreeInventoryResponse)!;
        Assert.AreEqual((inventory.Status, inventory.SessionsKnown, inventory.Worktrees.Length), (wire.Status, wire.SessionsKnown, wire.Worktrees.Length));
        Assert.AreEqual(first.Sessions[0], Item(wire, used.Root!).Sessions[0]);
        Assert.AreEqual((first.Protection, first.LastUsedAt, first.SessionCount), (Item(wire, used.Root!).Protection, Item(wire, used.Root!).LastUsedAt, Item(wire, used.Root!).SessionCount));

        // A session at work protects its checkout, also one the catalog does not list yet; the session that runs is named first.
        busy.Add(new("older", used.Root!, true));
        busy.Add(new("not-listed", Path.Combine(orphan.Root!, "anywhere"), true));
        var working = await service.InventoryAsync(new(Epoch, project.Id), default);
        Assert.AreEqual((true, "in_use"), (Item(working, used.Root!).Busy, Item(working, used.Root!).Protection));
        CollectionAssert.AreEqual(new[] { ("older", true), ("newer", false) }, Item(working, used.Root!).Sessions.Select(static session => (session.Id, session.Running)).ToArray());
        Assert.AreEqual((true, "in_use", 0), (Item(working, orphan.Root!).Busy, Item(working, orphan.Root!).Protection, Item(working, orphan.Root!).SessionCount));
        Assert.AreEqual((false, "main"), (working.Worktrees[0].Busy, working.Worktrees[0].Protection));
        busy.Clear();

        // What git keeps locked is not removed here.
        repository.Git("worktree", "lock", orphan.Root!);
        var locked = Item(await service.InventoryAsync(new(Epoch, project.Id), default), orphan.Root!);
        Assert.AreEqual((true, "locked"), (locked.Locked, locked.Protection));
        repository.Git("worktree", "unlock", orphan.Root!);

        // A few sessions are named for a checkout; how many record it is said.
        sessions.AddRange(Enumerable.Range(0, 7).Select(index => Recorded("more-" + index, day.AddDays(10 + index), repository.Root, used.Root)));
        var many = Item(await service.InventoryAsync(new(Epoch, project.Id), default), used.Root!);
        Assert.AreEqual((WorktreesService.MaximumSessionsPerCheckout, 9, (DateTimeOffset?)day.AddDays(16), "more-6"), (many.Sessions.Length, many.SessionCount, many.LastUsedAt, many.Sessions[0].Id));

        // A catalog that cannot be read hides no checkout: only who used them is not known.
        var unread = await repository.Manager(static () => [], static () => throw new IOException("The catalog is locked.")).InventoryAsync(new(Epoch, project.Id), default);
        Assert.AreEqual(("ok", false, 4), (unread.Status, unread.SessionsKnown, unread.Worktrees.Length));
        Assert.IsTrue(unread.Worktrees.All(static worktree => worktree.SessionCount == 0 && worktree.LastUsedAt is null));
        var unnamed = await repository.Service(static () => []).InventoryAsync(new(Epoch, project.Id), default);
        Assert.AreEqual(("ok", false, 4), (unnamed.Status, unnamed.SessionsKnown, unnamed.Worktrees.Length));

        Assert.AreEqual("stale_epoch", (await service.InventoryAsync(new("other", project.Id), default)).Status);
        Assert.AreEqual("unknown_project", (await service.InventoryAsync(new(Epoch, "missing"), default)).Status);
        Assert.AreEqual("invalid_request", (await service.InventoryAsync(new(Epoch, null), default)).Status);
        Assert.AreEqual("unavailable", (await new WorktreesService().InventoryAsync(new(Epoch, project.Id), default)).Status);
        var plain = await repository.Projects.UpsertFromPathAsync(Directory.CreateDirectory(Path.Combine(repository.Home, "plain")).FullName);
        Assert.AreEqual("not_repository", (await service.InventoryAsync(new(Epoch, plain.Id), default)).Status);
    }

    [TestMethod]
    public async Task Inventory_HoldsABoundedNumberOfCheckouts_AndSaysWhenGitListsMore()
    {
        using var repository = await Repository.CreateAsync();
        var project = await repository.Projects.UpsertFromPathAsync(repository.Root);
        // A repository git lists more checkouts for than a window shows: what git would print, without making them.
        var porcelain = new System.Text.StringBuilder($"worktree {repository.Root}\nHEAD 1234567890abcdef\nbranch refs/heads/main\n\n");
        for (var index = 0; index < WorktreesService.MaximumCheckouts + 40; index++)
            porcelain.Append($"worktree {Path.Combine(repository.Home, "none", "tree-" + index)}\nHEAD 1234567890abcdef\nbranch refs/heads/alta/tree-{index}\n\n");
        var listing = new GitWorktreeService(repository.Options, repository.Config, (_, _, _, _) => Task.FromResult(new GitRun(0, porcelain.ToString(), string.Empty)));
        var sessions = new[]
        {
            new AgentSessionMetadata("titled", default, DateTimeOffset.UnixEpoch, new string('t', 5000), WorkspacePath: repository.Root),
            new AgentSessionMetadata(new string('i', 300), default, DateTimeOffset.UnixEpoch, "id", WorkspacePath: repository.Root),
        };
        var service = new WorktreesService(listing, repository.Projects, repository.Config, static () => [], Epoch, _ => Task.FromResult<IReadOnlyList<AgentSessionMetadata>>(sessions), new DiskFolders(), new DesktopEditorView());

        var inventory = await service.InventoryAsync(new(Epoch, project.Id), default);

        Assert.AreEqual(("ok", WorktreesService.MaximumCheckouts, true), (inventory.Status, inventory.Worktrees.Length, inventory.Truncated));
        Assert.AreEqual((repository.Root, "main"), (inventory.Worktrees[0].Path, inventory.Worktrees[0].Protection));
        // What a row shows of a session is short, and a session with an identity no list accepts is left out.
        var named = inventory.Worktrees[0].Sessions.Single();
        Assert.AreEqual(("titled", true), (named.Id, named.Title.Length is > 0 and <= 200));
        Assert.AreEqual(1, inventory.Worktrees[0].SessionCount);
        var wire = JsonSerializer.Deserialize(JsonSerializer.Serialize(inventory, DesktopJsonContext.Default.WorktreeInventoryResponse), DesktopJsonContext.Default.WorktreeInventoryResponse)!;
        Assert.IsTrue(wire.Truncated);
    }

    [TestMethod]
    public async Task Inventory_OfAProjectThatLivesInAWorktree_ProtectsItsCheckoutAndTheMainOne()
    {
        using var repository = await Repository.CreateAsync();
        var whole = await repository.Projects.UpsertFromPathAsync(repository.Root);
        var home = await repository.Worktrees.CreateAsync(whole);
        var other = await repository.Worktrees.CreateAsync(whole);
        var project = await repository.Projects.UpsertFromPathAsync(home.Root!);
        var service = repository.Manager(static () => [], static () => []);

        var inventory = await service.InventoryAsync(new(Epoch, project.Id), default);

        WorktreeInventoryItem Item(string path) => inventory.Worktrees.Single(worktree => worktree.Path == path);
        Assert.AreEqual((true, false, "main"), (Item(repository.Root).Main, Item(repository.Root).Project, Item(repository.Root).Protection));
        Assert.AreEqual((true, true, "main"), (Item(home.Root!).Main, Item(home.Root!).Project, Item(home.Root!).Protection));
        Assert.AreEqual((false, false, null), (Item(other.Root!).Main, Item(other.Root!).Project, Item(other.Root!).Protection));
        // The changes of the main checkout can be read: the folder the inventory names for it is one of the
        // project's repository. The list of the Changes tab does not tell it from the checkout of the project
        // (both are `Main` there), which is why that tab cannot be asked to show it.
        var changes = await new ProjectGitService(repository.Projects, Epoch).ChangesAsync(new(Epoch, project.Id, "head", null, Worktree: Item(repository.Root).Folder), default);
        Assert.AreEqual(("ok", repository.Root), (changes.Status, changes.Root));
        var listed = (await service.ListAsync(new(Epoch, project.Id), default)).Worktrees;
        CollectionAssert.AreEquivalent(new[] { repository.Root, home.Root }, listed.Where(static worktree => worktree.Main).Select(static worktree => worktree.Path).ToArray());
        var removed = await service.RemoveManyAsync(new(Epoch, project.Id, [repository.Root, home.Root!, other.Root!]), default);
        CollectionAssert.AreEqual(new[] { "main", "main", "ok" }, removed.Results.Select(static result => result.Status).ToArray());
        Assert.IsTrue(Directory.Exists(repository.Root) && Directory.Exists(home.Root) && !Directory.Exists(other.Root));
    }

    [TestMethod]
    public async Task RemoveMany_RemovesWhatCanGo_AndSaysWhyEachOtherStays()
    {
        using var repository = await Repository.CreateAsync();
        var project = await repository.Projects.UpsertFromPathAsync(repository.Root);
        var clean = await repository.Worktrees.CreateAsync(project);
        var dirty = await repository.Worktrees.CreateAsync(project);
        var working = await repository.Worktrees.CreateAsync(project);
        var locked = await repository.Worktrees.CreateAsync(project);
        var stale = await repository.Worktrees.CreateAsync(project);
        var forgotten = await repository.Worktrees.CreateAsync(project);
        var ahead = await repository.Worktrees.CreateAsync(project);
        File.WriteAllText(Path.Combine(dirty.Root!, "draft.txt"), "not committed");
        repository.Git("worktree", "lock", locked.Root!);
        Repository.Delete(stale.Root!);
        Repository.Delete(forgotten.Root!);
        File.WriteAllText(Path.Combine(ahead.Root!, "work.txt"), "work");
        repository.GitIn(ahead.Root!, "add", "-A");
        repository.GitIn(ahead.Root!, "commit", "-q", "-m", "work");
        var busy = new List<SessionWorkFolder> { new("session-1", Path.Combine(working.Root!, "src"), true) };
        var service = repository.Manager(() => [.. busy], static () => []);
        var asked = new[] { clean.Root!, dirty.Root!, working.Root!, locked.Root!, stale.Root!, repository.Root, repository.Home, Path.Combine(ahead.Root!, "src") };

        var removed = await service.RemoveManyAsync(new(Epoch, project.Id, asked), default);

        // One answer for each folder, in the order they were asked: what could go went, and each other says why it stays.
        Assert.AreEqual("ok", removed.Status);
        CollectionAssert.AreEqual(asked, removed.Results.Select(static result => result.Path).ToArray());
        CollectionAssert.AreEqual(new[] { "ok", "dirty", "in_use", "locked", "ok", "main", "not_worktree", "not_worktree" }, removed.Results.Select(static result => result.Status).ToArray());
        Assert.IsFalse(Directory.Exists(clean.Root));
        Assert.IsTrue(File.Exists(Path.Combine(dirty.Root!, "draft.txt")) && Directory.Exists(working.Root) && Directory.Exists(locked.Root) && Directory.Exists(ahead.Root) && Directory.Exists(repository.Root));
        // The trash button of the Changes tab is refused the same way: a locked worktree stays, also when it is asked with its changes.
        Assert.AreEqual(new WorktreeChangeResponse("locked"), await service.RemoveAsync(new(Epoch, project.Id, locked.Root, Force: true), default));
        Assert.IsTrue(Directory.Exists(locked.Root));
        // No branch goes unless it is asked for.
        Assert.IsTrue(removed.Results.All(static result => result.BranchDeleted is null && result.BranchKept is null));
        StringAssert.Contains(repository.Git("branch", "--list", clean.Branch!), clean.Name!);
        // Only the worktree that was asked is forgotten: the other one whose folder is gone is still listed.
        var left = (await service.InventoryAsync(new(Epoch, project.Id), default)).Worktrees;
        CollectionAssert.AreEquivalent(new[] { repository.Root, dirty.Root, working.Root, locked.Root, forgotten.Root, ahead.Root }, left.Select(static worktree => worktree.Path).ToArray());
        Assert.IsTrue(left.Single(worktree => worktree.Path == forgotten.Root).Missing);
        var wire = JsonSerializer.Deserialize(JsonSerializer.Serialize(removed, DesktopJsonContext.Default.WorktreeRemoveManyResponse), DesktopJsonContext.Default.WorktreeRemoveManyResponse)!;
        CollectionAssert.AreEqual(removed.Results, wire.Results);
        var request = new WorktreeRemoveManyRequest(Epoch, project.Id, [dirty.Root!], [dirty.Root!], DeleteMergedBranches: true);
        var sent = JsonSerializer.Deserialize(JsonSerializer.Serialize(request, DesktopJsonContext.Default.WorktreeRemoveManyRequest), DesktopJsonContext.Default.WorktreeRemoveManyRequest)!;
        Assert.AreEqual((request.ProjectId, request.DeleteMergedBranches, dirty.Root, dirty.Root), (sent.ProjectId, sent.DeleteMergedBranches, sent.Paths!.Single(), sent.Discard!.Single()));

        // A request that is not whole removes nothing: no folder, a folder twice, a folder that is no full path, more
        // folders than a request holds, and changes to throw away for a folder that was not asked to go.
        foreach (var invalid in new WorktreeRemoveManyRequest[]
                 {
                     new(Epoch, project.Id, null),
                     new(Epoch, project.Id, []),
                     new(Epoch, project.Id, ["relative/folder"]),
                     new(Epoch, project.Id, [dirty.Root!, dirty.Root! + Path.DirectorySeparatorChar]),
                     new(Epoch, project.Id, [ahead.Root!], [dirty.Root!]),
                     new(Epoch, project.Id, [ahead.Root!], ["relative/folder"]),
                     new(Epoch, project.Id, [.. Enumerable.Range(0, WorktreesService.MaximumRemovals + 1).Select(index => Path.Combine(repository.Home, "none-" + index))]),
                 })
        {
            var refused = await service.RemoveManyAsync(invalid, default);
            Assert.AreEqual(("invalid_request", 0), (refused.Status, refused.Results.Length));
        }

        Assert.AreEqual("stale_epoch", (await service.RemoveManyAsync(new("other", project.Id, [dirty.Root!], [dirty.Root!]), default)).Status);
        Assert.AreEqual("unknown_project", (await service.RemoveManyAsync(new(Epoch, "missing", [dirty.Root!]), default)).Status);
        Assert.AreEqual("unavailable", (await new WorktreesService().RemoveManyAsync(new(Epoch, project.Id, [dirty.Root!]), default)).Status);
        Assert.IsTrue(File.Exists(Path.Combine(dirty.Root!, "draft.txt")) && Directory.Exists(ahead.Root));

        // Changes that are not committed are thrown away only for the folder that is named for it, whatever else is asked.
        var kept = await service.RemoveManyAsync(new(Epoch, project.Id, [dirty.Root!], [], DeleteMergedBranches: true), default);
        Assert.AreEqual(new WorktreeRemoval(dirty.Root!, "dirty"), kept.Results.Single());
        Assert.IsTrue(File.Exists(Path.Combine(dirty.Root!, "draft.txt")));
        // A branch that was asked to go goes when all its commits are elsewhere, and stays when it holds commits of its own.
        var discarded = await service.RemoveManyAsync(new(Epoch, project.Id, [dirty.Root!, ahead.Root!], [dirty.Root!], DeleteMergedBranches: true), default);
        CollectionAssert.AreEqual(new[] { new WorktreeRemoval(dirty.Root!, "ok", null, null, dirty.Branch), new WorktreeRemoval(ahead.Root!, "ok", null, ahead.Branch, null) }, discarded.Results);
        Assert.IsFalse(Directory.Exists(dirty.Root) || Directory.Exists(ahead.Root));
        Assert.AreEqual(string.Empty, repository.Git("branch", "--list", dirty.Branch!).Trim());
        StringAssert.Contains(repository.Git("branch", "--list", ahead.Branch!), ahead.Name!);

        // The session finished and git let go: what stayed can go now, and the folder of the project is what is left.
        busy.Clear();
        repository.Git("worktree", "unlock", locked.Root!);
        var rest = await service.RemoveManyAsync(new(Epoch, project.Id, [working.Root!, locked.Root!, forgotten.Root!]), default);
        CollectionAssert.AreEqual(new[] { "ok", "ok", "ok" }, rest.Results.Select(static result => result.Status).ToArray());
        CollectionAssert.AreEqual(new[] { repository.Root }, (await service.InventoryAsync(new(Epoch, project.Id), default)).Worktrees.Select(static worktree => worktree.Path).ToArray());
        // What the window still showed is gone by now: it is said, and nothing else is touched.
        Assert.AreEqual("not_worktree", (await service.RemoveManyAsync(new(Epoch, project.Id, [working.Root!]), default)).Results.Single().Status);
        Assert.IsTrue(Directory.Exists(repository.Root));
    }

    [TestMethod]
    public async Task RemoveMany_OfAWorktreeGitCannotForget_LeavesEveryOtherRegistration()
    {
        using var repository = await Repository.CreateAsync();
        var project = await repository.Projects.UpsertFromPathAsync(repository.Root);
        var refusedByGit = await repository.Worktrees.CreateAsync(project);
        var stale = await repository.Worktrees.CreateAsync(project);
        var clean = await repository.Worktrees.CreateAsync(project);
        // Git lists two worktrees as ones to prune: one whose folder lost its `.git`, which git does not remove
        // by its name, and one whose folder is gone, which was not asked to go.
        File.Delete(Path.Combine(refusedByGit.Root!, ".git"));
        Repository.Delete(stale.Root!);
        var service = repository.Manager(static () => [], static () => []);
        var before = (await service.InventoryAsync(new(Epoch, project.Id), default)).Worktrees;
        Assert.IsTrue(before.Single(worktree => worktree.Path == refusedByGit.Root).Missing && before.Single(worktree => worktree.Path == stale.Root).Missing);

        // Asked alone, asked with its changes, and asked through the trash button of the Changes tab.
        var alone = await service.RemoveManyAsync(new(Epoch, project.Id, [refusedByGit.Root!, clean.Root!]), default);
        var forced = await service.RemoveManyAsync(new(Epoch, project.Id, [refusedByGit.Root!], [refusedByGit.Root!], DeleteMergedBranches: true), default);
        var single = await service.RemoveAsync(new(Epoch, project.Id, refusedByGit.Root, Force: true), default);

        // The failure is the answer for that worktree, with what git said, and the batch goes on.
        CollectionAssert.AreEqual(new[] { "failed", "ok" }, alone.Results.Select(static result => result.Status).ToArray());
        StringAssert.Contains(alone.Results[0].Message, ".git");
        Assert.AreEqual(("failed", null, null), (forced.Results.Single().Status, forced.Results.Single().BranchKept, forced.Results.Single().BranchDeleted));
        Assert.AreEqual("failed", single.Status);
        // No registration that was not asked is forgotten, and nothing of the folder git refused is touched.
        var left = (await service.InventoryAsync(new(Epoch, project.Id), default)).Worktrees;
        CollectionAssert.AreEquivalent(new[] { repository.Root, refusedByGit.Root, stale.Root }, left.Select(static worktree => worktree.Path).ToArray());
        Assert.IsTrue(left.Single(worktree => worktree.Path == stale.Root).Missing);
        Assert.IsTrue(File.Exists(Path.Combine(refusedByGit.Root!, "readme.md")));
        StringAssert.Contains(repository.Git("branch", "--list", refusedByGit.Branch!), refusedByGit.Name!);
        StringAssert.Contains(repository.Git("branch", "--list", stale.Branch!), stale.Name!);
    }

    [TestMethod]
    public async Task RemoveMany_StopsBetweenTwoWorktrees_WhenTheWindowIsGone()
    {
        using var repository = await Repository.CreateAsync();
        var project = await repository.Projects.UpsertFromPathAsync(repository.Root);
        var first = await repository.Worktrees.CreateAsync(project);
        var second = await repository.Worktrees.CreateAsync(project);
        using var gone = new CancellationTokenSource();
        var asked = 0;
        // The window goes away while the first worktree is being removed.
        var service = repository.Manager(() => { if (++asked == 1) gone.Cancel(); return []; }, static () => []);

        var removed = await service.RemoveManyAsync(new(Epoch, project.Id, [first.Root!, second.Root!]), gone.Token);

        // What started is finished, and what did not start is left as it is.
        CollectionAssert.AreEqual(new[] { new WorktreeRemoval(first.Root!, "ok"), new WorktreeRemoval(second.Root!, "canceled") }, removed.Results);
        Assert.IsTrue(!Directory.Exists(first.Root) && Directory.Exists(second.Root));
    }

    [TestMethod]
    public async Task OpenEditor_OpensTheCheckoutThatWasAsked_AndNeverAnotherFolder()
    {
        using var repository = await Repository.CreateAsync();
        repository.Write("src/app/main.txt", "app");
        repository.Commit("app");
        var whole = await repository.Projects.UpsertFromPathAsync(repository.Root);
        var below = await repository.Projects.UpsertFromPathAsync(Path.Combine(repository.Root, "src", "app"));
        var created = await repository.Worktrees.CreateAsync(whole);
        var gone = await repository.Worktrees.CreateAsync(whole);
        Repository.Delete(gone.Root!);
        var folders = new DiskFolders();
        var editor = new DesktopEditorView();
        var service = repository.Manager(static () => [], static () => [], folders, editor);
        // Without a window nothing is shown, and it is said.
        Assert.AreEqual("no_window", (await service.OpenEditorAsync(new(Epoch, whole.Id, created.Root), default)).Status);
        var opened = new List<ProjectFileShowEvent>();
        using var watching = editor.Watch(opened.Add);

        // A worktree opens as a folder of its own, named by the worktree: it is not the editor of the project, which shows the folder of the project.
        Assert.AreEqual("ok", (await service.OpenEditorAsync(new(Epoch, whole.Id, created.Root), default)).Status);
        var tab = opened.Single();
        Assert.IsTrue(DiskFolders.IsId(tab.ProjectId) && !DiskFolders.IsFixed(tab.ProjectId));
        Assert.AreNotEqual(whole.Id, tab.ProjectId);
        Assert.AreEqual((created.Root, created.Name, null, null), (tab.Root, tab.Name, tab.Path, tab.Line));
        Assert.AreEqual(("ok", created.Root), folders.Resolve(tab.ProjectId));
        Assert.AreNotEqual(repository.Root, tab.Root);

        // The checkout the project lives in is the editor of the project itself.
        opened.Clear();
        Assert.AreEqual("ok", (await service.OpenEditorAsync(new(Epoch, whole.Id, repository.Root), default)).Status);
        Assert.AreEqual(new ProjectFileShowEvent(whole.Id, null, null, null), opened.Single());

        // A project below the root of its repository is shown in its own folder of the worktree.
        opened.Clear();
        Assert.AreEqual("ok", (await service.OpenEditorAsync(new(Epoch, below.Id, created.Root), default)).Status);
        Assert.AreEqual((Path.Combine(created.Root!, "src", "app"), created.Name), (opened.Single().Root, opened.Single().Name));
        Assert.AreNotEqual(below.ProjectPath, opened.Single().Root);

        // What is not a checkout that git lists with its folder opens nothing.
        opened.Clear();
        Assert.AreEqual("worktree_missing", (await service.OpenEditorAsync(new(Epoch, whole.Id, gone.Root), default)).Status);
        Assert.AreEqual("not_worktree", (await service.OpenEditorAsync(new(Epoch, whole.Id, repository.Home), default)).Status);
        Assert.AreEqual("not_worktree", (await service.OpenEditorAsync(new(Epoch, whole.Id, Path.Combine(created.Root!, "src")), default)).Status);
        Assert.AreEqual("invalid_request", (await service.OpenEditorAsync(new(Epoch, whole.Id, "relative/folder"), default)).Status);
        Assert.AreEqual("invalid_request", (await service.OpenEditorAsync(new(Epoch, whole.Id, null), default)).Status);
        Assert.AreEqual("stale_epoch", (await service.OpenEditorAsync(new("other", whole.Id, created.Root), default)).Status);
        Assert.AreEqual("unknown_project", (await service.OpenEditorAsync(new(Epoch, "missing", created.Root), default)).Status);
        Assert.AreEqual("unavailable", (await repository.Service(static () => []).OpenEditorAsync(new(Epoch, whole.Id, created.Root), default)).Status);
        Assert.AreEqual("unavailable", (await new WorktreesService().OpenEditorAsync(new(Epoch, whole.Id, created.Root), default)).Status);
        Assert.IsEmpty(opened);
        var request = new WorktreeOpenRequest(Epoch, whole.Id, created.Root);
        Assert.AreEqual(request, JsonSerializer.Deserialize(JsonSerializer.Serialize(request, DesktopJsonContext.Default.WorktreeOpenRequest), DesktopJsonContext.Default.WorktreeOpenRequest));
    }

    [TestMethod]
    public async Task Inventory_NamesTheFolderWhoseChangesAreThoseOfTheCheckout()
    {
        using var repository = await Repository.CreateAsync();
        repository.Write("src/app/main.txt", "app");
        repository.Commit("app");
        var project = await repository.Projects.UpsertFromPathAsync(Path.Combine(repository.Root, "src", "app"));
        var created = await repository.Worktrees.CreateAsync(project);
        File.WriteAllText(Path.Combine(created.Folder!, "draft.txt"), "only in the worktree");
        var changes = new ProjectGitService(repository.Projects, Epoch);

        var inventory = await repository.Manager(static () => [], static () => []).InventoryAsync(new(Epoch, project.Id), default);

        // What the window hands to the changes tab for a worktree is the folder of the project in that checkout.
        var item = inventory.Worktrees.Single(worktree => worktree.Path == created.Root);
        Assert.AreEqual(created.Folder, item.Folder);
        var there = await changes.ChangesAsync(new(Epoch, project.Id, "head", null, Worktree: item.Folder), default);
        Assert.AreEqual(("ok", created.Root), (there.Status, there.Root));
        CollectionAssert.AreEqual(new[] { "src/app/draft.txt" }, there.Files!.Select(static file => file.Path).ToArray());
        // The folder of the project has none of them: the two are never mistaken for each other.
        Assert.AreEqual(project.ProjectPath, inventory.Worktrees[0].Folder);
        var here = await changes.ChangesAsync(new(Epoch, project.Id, "head", null), default);
        Assert.AreEqual(("ok", repository.Root, 0), (here.Status, here.Root, here.Files!.Length));
    }

    private static AgentSessionMetadata Recorded(string id, DateTimeOffset updated, string workspace, string? worktree = null)
        => new(id, updated.AddHours(-1), updated, id, WorkspacePath: workspace, ProviderKey: "codex", WorktreePath: worktree);

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

        /// <summary>The service as the window of the worktrees uses it: with the sessions of the catalog and the code editor.</summary>
        public WorktreesService Manager(Func<IReadOnlyList<SessionWorkFolder>> busy, Func<IReadOnlyList<AgentSessionMetadata>> sessions, DiskFolders? folders = null, DesktopEditorView? editor = null)
            => new(Worktrees, Projects, Config, busy, Epoch, _ => Task.FromResult(sessions()), folders ?? new DiskFolders(), editor ?? new DesktopEditorView());

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
