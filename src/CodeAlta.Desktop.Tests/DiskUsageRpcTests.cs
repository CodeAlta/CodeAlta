using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Storage;
using CodeAlta.Catalog.Worktrees;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// The disk usage as the page reads it, on real repositories made with the git on the path: every project of the
/// catalog with its checkouts and sessions, one scan that requests join, and the last measurement kept in
/// memory and in the file of the instance. Inconclusive on a machine without git.
/// </summary>
[TestClass]
public sealed class DiskUsageRpcTests
{
    private const string Epoch = "22222222-2222-4222-8222-222222222222";
    private static readonly DateTimeOffset Day = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private static TimeSpan Patience => TestTime.Seconds(60);

    [TestMethod]
    public async Task Scan_MeasuresAProject_ItsCheckouts_AndItsSessions_NotTheProfile()
    {
        using var fixture = Fixture.Create();
        var repository = fixture.Repository("repository");
        repository.Write(".gitignore", "bin/\nobj/\n.env\n");
        repository.Commit("ignore");
        repository.Write("bin/app.dll", new string('b', 1000));
        repository.Write(".env", "SECRET=1");
        var project = await fixture.Projects.UpsertFromPathAsync(repository.Root);
        // In the profile, under its worktrees folder.
        var worktree = (await fixture.Worktrees.CreateAsync(project)).Root!;
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(worktree, "obj")).FullName, "x"), new string('o', 50));

        fixture.Session("in-project", project, 300);
        fixture.Session("in-worktree", project, 200, worktree, attachments: 50);
        fixture.Chat("chat", 40);
        fixture.SessionFile("2026/11/10/lost.jsonl", 13);
        fixture.SessionFile("notes.txt", 11);
        fixture.Busy.Add(new("in-worktree", worktree, true));
        var service = fixture.Service(applicationPaths: [Path.Combine(worktree, "bin", "alta")]);

        var events = await ReadAllAsync(service.ScanAsync(new(Epoch), default));

        Assert.IsTrue(events.All(static update => update.Status == "ok"));
        Assert.AreEqual("done", events[^1].Kind);
        // The profile has a view of its own (Settings > Storage): no record of it, no phase.
        Assert.IsFalse(events.Any(static update => update.Kind is not ("progress" or "project" or "done")), string.Join(",", events.Select(static update => update.Kind)));
        Assert.IsFalse(events.Any(static update => update.Progress?.Phase is not (null or "sessions" or "projects")));
        Assert.IsTrue(events.Any(static update => update.Progress is { Phase: "projects", CheckoutsTotal: 2 }), "The checkouts to measure are counted.");
        var projectEvent = events.Single(static update => update.Kind == "project");
        var record = projectEvent.Project!;
        Assert.AreEqual((project.Id, project.ProjectPath, "ok", true, false, null as string, 0), (record.Id, record.Folder, record.Status, record.Repository, record.Archived, record.MeasuredWith, record.SharedWith.Length));
        Assert.AreEqual(GitWorktreeService.FindCommonDirectory(repository.Root), record.RepositoryKey);
        CollectionAssert.AreEqual(new[] { repository.Root, worktree }, record.Checkouts.Select(static checkout => checkout.Path).ToArray());

        // The folder of the project: its .git, its build output, an ignored file a clone does not bring back, and no remote.
        var main = record.Checkouts[0];
        Assert.AreEqual((true, true, false, false, "main", false, 1), (main.Main, main.Project, main.Missing, main.InUse, main.Protection, main.RunningApplication, main.SessionCount));
        CollectionAssert.AreEqual(new[] { project.Id }, main.ProjectIds);
        var measure = main.Measure!;
        Assert.AreEqual((1000L, 8L, 1L, 0L, true), (measure.BuildOutputBytes, measure.OtherIgnoredBytes, measure.OtherIgnoredCount, measure.SkippedEntries, measure.GitKnown));
        Assert.IsTrue(measure.GitBytes > 0);
        Assert.AreEqual(FolderUsage.Measure(repository.Root, default).Bytes, measure.TotalBytes);
        Assert.AreEqual((false, "main", "has_local_work"), (main.Git!.HasRemote, main.Git.Branch, main.Verdict));
        Assert.AreEqual(1, main.Planned.Length);
        Assert.AreEqual(("bin", 1000L, 1L, false), (main.Planned[0].Path, main.Planned[0].Bytes, main.Planned[0].Files, main.PlannedTruncated));
        Assert.IsNotNull(main.LastCommit);
        Assert.AreEqual(Later(main.LastCommit, Day), main.LastUsedAt);

        // The worktree: a session at work in it, the application started from it, its own build output.
        var linked = record.Checkouts[1];
        Assert.AreEqual((false, false, true, "in_use", true, 1, 0), (linked.Main, linked.Project, linked.InUse, linked.Protection, linked.RunningApplication, linked.SessionCount, linked.ProjectIds.Length));
        Assert.AreEqual((0L, 50L, true), (linked.Measure!.GitBytes, linked.Measure.BuildOutputBytes, linked.Measure.GitKnown));
        Assert.AreEqual(Path.GetFileName(worktree), linked.Name);
        Assert.IsNotNull(linked.Branch);
        CollectionAssert.AreEqual(new[] { "obj" }, linked.Planned.Select(static entry => entry.Path).ToArray());
        Assert.AreEqual(Later(linked.LastCommit, Day.AddDays(1)), linked.LastUsedAt);

        // The sessions of the project, the largest first, each with the checkout it works in.
        Assert.AreEqual((2, 550L, true, false), (record.Sessions.Count, record.Sessions.Bytes, record.Sessions.BytesKnown, record.Sessions.Truncated));
        Assert.AreEqual(new DiskUsageSession("in-project", "in-project", 300, Day, false, repository.Root), record.Sessions.Largest[0]);
        Assert.AreEqual(new DiskUsageSession("in-worktree", "in-worktree", 250, Day.AddDays(1), true, worktree), record.Sessions.Largest[1]);

        // One total of the projects, every byte once: their checkouts and their sessions; the chat and the files of no
        // session are not of a project.
        var done = events[^1].Done!;
        Assert.AreEqual((1, true, true, 0), (done.Projects, done.SessionsKnown, done.Complete, done.Unreadable.Length));
        Assert.AreEqual((measure.TotalBytes + linked.Measure.TotalBytes + 550, false), (done.TotalBytes, done.TotalPartial));
        AssertTotalsAddUp(events);

        // What the page reads goes over the wire whole.
        var wire = JsonSerializer.Deserialize(JsonSerializer.Serialize(projectEvent, DesktopJsonContext.Default.DiskUsageScanEvent), DesktopJsonContext.Default.DiskUsageScanEvent)!;
        Assert.AreEqual(record.Checkouts[0].Measure, wire.Project!.Checkouts[0].Measure);
        Assert.AreEqual(record.Sessions.Largest[1], wire.Project.Sessions.Largest[1]);
        StringAssert.Contains(JsonSerializer.Serialize(projectEvent, DesktopJsonContext.Default.DiskUsageScanEvent), "\"buildOutputBytes\":\"1000\"");
    }

    [TestMethod]
    public async Task Scan_MeasuresARepositoryOnce_ForEveryProjectOfIt_AndRecordsFoldersGitDoesNotReadOrThatAreGone()
    {
        using var fixture = Fixture.Create();
        var repository = fixture.Repository("monorepo");
        repository.Write("sub/readme.md", "sub");
        repository.Commit("sub");
        var root = await fixture.Projects.UpsertFromPathAsync(repository.Root);
        var sub = await fixture.Projects.UpsertFromPathAsync(Path.Combine(repository.Root, "sub"));
        var plainFolder = Directory.CreateDirectory(Path.Combine(fixture.Home, "plain")).FullName;
        File.WriteAllText(Path.Combine(plainFolder, "notes.txt"), "twelve bytes");
        var plain = await fixture.Projects.UpsertFromPathAsync(plainFolder);
        var goneFolder = Directory.CreateDirectory(Path.Combine(fixture.Home, "gone")).FullName;
        var gone = await fixture.Projects.UpsertFromPathAsync(goneFolder);
        Directory.Delete(goneFolder);
        fixture.Session("of-gone", gone, 21);
        fixture.Session("of-sub", sub, 5);

        var records = (await ReadAllAsync(fixture.Service().ScanAsync(new(Epoch), default)))
            .Where(static update => update.Kind == "project").Select(static update => update.Project!).ToDictionary(static project => project.Id);

        Assert.HasCount(4, records);
        // One project carries the checkouts of the repository, the other names it.
        Assert.AreEqual((null as string, 1), (records[root.Id].MeasuredWith, records[root.Id].Checkouts.Length));
        CollectionAssert.AreEqual(new[] { sub.Id }, records[root.Id].SharedWith);
        Assert.AreEqual((root.Id, 0, true, records[root.Id].RepositoryKey), (records[sub.Id].MeasuredWith, records[sub.Id].Checkouts.Length, records[sub.Id].Repository, records[sub.Id].RepositoryKey));
        // Its sessions are its own, and they work in the checkout of the repository.
        Assert.AreEqual((1, 5L, repository.Root), (records[sub.Id].Sessions.Count, records[sub.Id].Sessions.Bytes, records[sub.Id].Sessions.Largest[0].Checkout));
        Assert.AreEqual(0, records[root.Id].Sessions.Count);

        // A folder git does not read is measured as a folder.
        var folder = records[plain.Id];
        Assert.AreEqual(("ok", false, 1), (folder.Status, folder.Repository, folder.Checkouts.Length));
        Assert.AreEqual((plainFolder, true, "main", 12L, false, "unknown", (DiskUsageGitState?)null),
            (folder.Checkouts[0].Path, folder.Checkouts[0].Project, folder.Checkouts[0].Protection, folder.Checkouts[0].Measure!.TotalBytes, folder.Checkouts[0].Measure!.GitKnown,
                folder.Checkouts[0].Verdict, folder.Checkouts[0].Git));

        // A folder that is gone: no figure, its sessions all the same.
        var missing = records[gone.Id];
        Assert.AreEqual(("missing", 0, 1, 21L), (missing.Status, missing.Checkouts.Length, missing.Sessions.Count, missing.Sessions.Bytes));
    }

    [TestMethod]
    public async Task Scan_GoesOnWhenOneProjectFails_WhileItIsMeasured_OrWhileItIsDiscovered()
    {
        using var fixture = Fixture.Create();
        var good = fixture.Repository("good");
        var bad = fixture.Repository("bad");
        var worse = fixture.Repository("worse");
        var goodProject = await fixture.Projects.UpsertFromPathAsync(good.Root);
        var badProject = await fixture.Projects.UpsertFromPathAsync(bad.Root);
        var worseProject = await fixture.Projects.UpsertFromPathAsync(worse.Root);
        // The checkouts of one cannot be measured; the checkouts of another cannot even be listed.
        var listing = new GitWorktreeService(fixture.Options, new CodeAltaConfigStore(fixture.Options), (folder, arguments, timeout, token) =>
            arguments is ["worktree", ..] && GitWorktreeService.IsWithin(folder, worse.Root) ? throw new IOException("The index is locked.") : fixture.RunAsync(folder, arguments, timeout, token));
        var service = fixture.Service((folder, arguments, timeout, token) => GitWorktreeService.IsWithin(folder, bad.Root)
            ? throw new IOException("The disk is gone.")
            : fixture.RunAsync(folder, arguments, timeout, token), worktrees: listing);

        var events = await ReadAllAsync(service.ScanAsync(new(Epoch), default));

        var records = events.Where(static update => update.Kind == "project").Select(static update => update.Project!).ToDictionary(static project => project.Id);
        var failed = records[badProject.Id].Checkouts.Single();
        Assert.AreEqual(("The disk is gone.", (DiskUsageMeasure?)null, "unknown"), (failed.Problem, failed.Measure, failed.Verdict));
        Assert.AreEqual(("failed", "The index is locked.", 0), (records[worseProject.Id].Status, records[worseProject.Id].Problem, records[worseProject.Id].Checkouts.Length));
        var measured = records[goodProject.Id].Checkouts.Single();
        Assert.AreEqual((null as string, true), (measured.Problem, measured.Measure!.TotalBytes > 0));
        Assert.AreEqual(("done", 3), (events[^1].Kind, events[^1].Done!.Projects));
        Assert.IsTrue(events[^1].Done!.TotalPartial, "A checkout that could not be measured leaves the total a lower bound.");
    }

    [TestMethod]
    public async Task AProjectReachedThroughALink_IsMeasuredAsItsPhysicalFolder_AndOnce()
    {
        TestLinks.RequireSymbolicLinks();
        using var fixture = Fixture.Create();
        var repository = fixture.Repository("real");
        fixture.AddRemote(repository);
        // A stash and a branch with a commit on no remote: the repository holds local work.
        repository.Write("readme.md", "changed");
        repository.Git("stash", "-q");
        repository.Git("checkout", "-q", "-b", "local");
        repository.Write("local.txt", "local");
        repository.Commit("local");
        repository.Git("checkout", "-q", "main");
        var alias = Path.Combine(fixture.Home, "alias");
        Directory.CreateSymbolicLink(alias, repository.Root);
        var throughLink = await fixture.Projects.UpsertFromPathAsync(alias);
        var direct = await fixture.Projects.UpsertFromPathAsync(repository.Root);
        Assert.AreNotEqual(throughLink.Id, direct.Id);
        fixture.Session("through-link", throughLink, 10);

        var records = (await ReadAllAsync(fixture.Service().ScanAsync(new(Epoch), default)))
            .Where(static update => update.Kind == "project").Select(static update => update.Project!).ToArray();

        var carrier = records.Single(static project => project.MeasuredWith is null);
        var other = records.Single(static project => project.MeasuredWith is not null);
        Assert.AreEqual((carrier.Id, 0, carrier.RepositoryKey), (other.MeasuredWith, other.Checkouts.Length, other.RepositoryKey));
        var checkout = carrier.Checkouts.Single();
        Assert.AreEqual((true, true, "main", 1), (checkout.Project, checkout.Main, checkout.Protection, checkout.SessionCount));
        CollectionAssert.AreEquivalent(new[] { throughLink.Id, direct.Id }, checkout.ProjectIds);
        // The project folder is read as the repository: its stash and its branch count.
        Assert.AreEqual((1, "has_local_work"), (checkout.Git!.Stashes, checkout.Verdict));
        CollectionAssert.Contains(checkout.Git.UnpushedBranches.Select(static branch => branch.Name).ToArray(), "local");
    }

    [TestMethod]
    public async Task TheRunningApplication_IsFound_FromItsExecutableOrItsFolder_ThroughLinks()
    {
        TestLinks.RequireSymbolicLinks();
        using var fixture = Fixture.Create();
        var repository = fixture.Repository("repository");
        repository.Write("bin/alta.dll", "app");
        await fixture.Projects.UpsertFromPathAsync(repository.Root);
        // The folder the application runs from, reached through a link (as the bundle of macOS reaches the installed version).
        var link = Path.Combine(fixture.Home, "integration-app");
        Directory.CreateSymbolicLink(link, Path.Combine(repository.Root, "bin"));

        async Task<bool> RunningFromAsync(params string?[] paths)
            => (await ReadAllAsync(fixture.Service(applicationPaths: paths).ScanAsync(new(Epoch), default)))
                .Single(static update => update.Kind == "project").Project!.Checkouts.Single().RunningApplication;

        Assert.IsTrue(await RunningFromAsync(Path.Combine(fixture.Home, "elsewhere", "CodeAlta"), link + Path.DirectorySeparatorChar), "the folder of the application");
        Assert.IsTrue(await RunningFromAsync(Path.Combine(link, "alta"), null), "the executable");
        Assert.IsFalse(await RunningFromAsync(Path.Combine(fixture.Home, "elsewhere", "CodeAlta"), Path.Combine(fixture.Home, "elsewhere")));
        if (ReferenceEquals(CheckoutPaths.ComparerOf(repository.Root), StringComparer.OrdinalIgnoreCase))
            Assert.IsTrue(await RunningFromAsync(Path.Combine(repository.Root.ToUpperInvariant(), "BIN", "alta")), "a volume that does not tell case apart");
    }

    [TestMethod]
    public async Task ACheckoutThatIsTheFolderOfAnotherProject_NamesIt_AndIsProtectedAsTheWorktreesWindowDoes()
    {
        using var fixture = Fixture.Create();
        var repository = fixture.Repository("repository");
        var first = await fixture.Projects.UpsertFromPathAsync(repository.Root);
        var linked = (await fixture.Worktrees.CreateAsync(first)).Root!;
        var second = await fixture.Projects.UpsertFromPathAsync(linked);

        var records = (await ReadAllAsync(fixture.Service().ScanAsync(new(Epoch), default)))
            .Where(static update => update.Kind == "project").Select(static update => update.Project!).ToDictionary(static project => project.Id);

        Assert.AreEqual((first.Id, 0), (records[second.Id].MeasuredWith, records[second.Id].Checkouts.Length));
        var row = records[first.Id].Checkouts.Single(checkout => checkout.Path == linked);
        Assert.AreEqual((true, false, "main"), (row.Main, row.Project, row.Protection));
        CollectionAssert.AreEqual(new[] { second.Id }, row.ProjectIds);
        var inventory = await new WorktreesService(fixture.Worktrees, fixture.Projects, new CodeAltaConfigStore(fixture.Options), static () => [], Epoch,
            static _ => Task.FromResult<IReadOnlyList<AgentSessionMetadata>>([]), new DiskFolders(), new DesktopEditorView()).InventoryAsync(new(Epoch, second.Id), default);
        var listed = inventory.Worktrees.Single(worktree => worktree.Path == linked);
        Assert.AreEqual((listed.Main, listed.Protection), (row.Main, row.Protection));
    }

    [TestMethod]
    public async Task SizesAreExclusive_AWorktreeInsideItsProject_IsCountedOnce_AndTheProjectHoldsIt()
    {
        using var fixture = Fixture.Create();
        var repository = fixture.Repository("repository");
        fixture.AddRemote(repository);
        await fixture.Projects.UpsertFromPathAsync(repository.Root);
        // Where "Inside each project" places worktrees, with the ignore file CodeAlta writes beside them.
        repository.Write(".alta/worktrees/.gitignore", "*\n");
        var inside = Path.Combine(repository.Root, ".alta", "worktrees", "w");
        repository.Git("worktree", "add", "-q", "-b", "inside", inside);
        var whole = FolderUsage.Measure(repository.Root, default).Bytes;
        var nested = FolderUsage.Measure(inside, default).Bytes;

        var events = await ReadAllAsync(fixture.Service().ScanAsync(new(Epoch), default));

        var record = events.Single(static update => update.Kind == "project").Project!;
        var main = record.Checkouts.Single(static checkout => checkout.Project);
        var worktree = record.Checkouts.Single(checkout => checkout.Path == inside);
        // The ignore file CodeAlta writes there is no other ignored file, and the worktree it holds is clean.
        Assert.AreEqual((whole - nested, 0L, "nothing_local"), (main.Measure!.TotalBytes, main.Measure.OtherIgnoredCount, main.Verdict));
        CollectionAssert.AreEqual(new[] { inside }, main.HeldCheckouts);
        Assert.AreEqual(0, main.WorseHeldCheckouts.Length);
        Assert.AreEqual(nested, worktree.Measure!.TotalBytes);
        Assert.AreEqual(whole, events[^1].Done!.TotalBytes);
        AssertTotalsAddUp(events);
    }

    [TestMethod]
    public async Task TwoRegistrationsOfOneFolder_AreMeasuredOnce_AndTheSecondNamesTheFirst()
    {
        TestLinks.RequireSymbolicLinks();
        using var fixture = Fixture.Create();
        var repository = fixture.Repository("repository");
        await fixture.Projects.UpsertFromPathAsync(repository.Root);
        var first = Path.Combine(fixture.Home, "first");
        var second = Path.Combine(fixture.Home, "second");
        repository.Git("worktree", "add", "-q", "-b", "first", first);
        repository.Git("worktree", "add", "-q", "-b", "second", second);
        File.WriteAllText(Path.Combine(second, "data.bin"), new string('d', 500));
        // Git keeps both registrations; one of them now reaches the folder of the other.
        Directory.Delete(first, recursive: true);
        Directory.CreateSymbolicLink(first, second);
        repository.Git("worktree", "lock", first);
        var once = FolderUsage.Measure(second, default).Bytes;

        var events = await ReadAllAsync(fixture.Service().ScanAsync(new(Epoch), default));

        var checkouts = events.Single(static update => update.Kind == "project").Project!.Checkouts
            .Where(checkout => checkout.Path == first || checkout.Path == second).ToArray();
        Assert.HasCount(2, checkouts);
        var measured = checkouts.Single(static checkout => checkout.Measure is not null);
        var other = checkouts.Single(static checkout => checkout.Measure is null);
        Assert.AreEqual((once, null as string), (measured.Measure!.TotalBytes, measured.SameFolderAs));
        Assert.AreEqual((measured.Path, null as string), (other.SameFolderAs, other.Problem));
        // Each registration keeps what protects it.
        Assert.AreEqual("locked", checkouts.Single(checkout => checkout.Path == first).Protection);
        Assert.IsNull(checkouts.Single(checkout => checkout.Path == second).Protection);
        Assert.IsFalse(events[^1].Done!.TotalPartial);
        AssertTotalsAddUp(events);
    }

    [TestMethod]
    public async Task EveryLocalWorkOfTheRepository_IsCarriedByTheRecord_ForThePageToSay()
    {
        using var fixture = Fixture.Create();
        var repository = fixture.Repository("repository");
        fixture.AddRemote(repository);
        await fixture.Projects.UpsertFromPathAsync(repository.Root);
        repository.Git("notes", "add", "-m", "a note", "HEAD");
        var other = Path.Combine(fixture.Home, "other");
        repository.Git("worktree", "add", "-q", "--detach", other);
        new GitRepository(fixture, other).Git("-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "-q", "--allow-empty", "-m", "on no branch");

        var events = await ReadAllAsync(fixture.Service().ScanAsync(new(Epoch), default));

        var git = events.Single(static update => update.Kind == "project").Project!.Checkouts.Single(checkout => checkout.Path == repository.Root).Git!;
        Assert.AreEqual((0, 1, 1, 0), (git.DetachedCommits, git.OtherWorktreeDetachedCommits, git.OtherRefs, git.UnexaminedBranches));
        CollectionAssert.AreEqual(new[] { "refs/notes/commits" }, git.OtherRefNames);
        Assert.IsTrue(git.HasLocalWork);
    }

    [TestMethod]
    public async Task ACheckoutThatHoldsAnotherCheckout_NamesIt_AndItsVerdictIsTheWorseOfBoth()
    {
        using var fixture = Fixture.Create();
        var repository = fixture.Repository("repository");
        fixture.AddRemote(repository);
        await fixture.Projects.UpsertFromPathAsync(repository.Root);
        var outer = Path.Combine(fixture.Home, "outer");
        repository.Git("worktree", "add", "-q", "--detach", outer, "origin/main");
        // A worktree in its folder, which git ignores there: removing the outer one would take it.
        File.AppendAllText(Path.Combine(repository.Root, ".git", "info", "exclude"), "nested/\n");
        var inner = Path.Combine(outer, "nested", "inner");
        repository.Git("worktree", "add", "-q", "--detach", inner, "origin/main");

        // Both clean and pushed: nothing local, and the mark of what it holds.
        var clean = await OuterAndInnerAsync();
        CollectionAssert.AreEqual(new[] { inner }, clean.Outer.HeldCheckouts);
        Assert.AreEqual(("nothing_local", 0, "holds_worktree"), (clean.Outer.Verdict, clean.Outer.WorseHeldCheckouts.Length, clean.Outer.Protection));
        Assert.AreEqual(("nothing_local", 0), (clean.Inner.Verdict, clean.Inner.HeldCheckouts.Length));

        // Work in the inner one: the outer one says so, and names it.
        File.WriteAllText(Path.Combine(inner, "work.txt"), "not committed");
        var dirty = await OuterAndInnerAsync();
        Assert.AreEqual(("has_local_work", "has_local_work"), (dirty.Outer.Verdict, dirty.Inner.Verdict));
        CollectionAssert.AreEqual(new[] { inner }, dirty.Outer.WorseHeldCheckouts);
        CollectionAssert.AreEqual(new[] { inner }, dirty.Outer.HeldCheckouts);

        async Task<(DiskUsageCheckout Outer, DiskUsageCheckout Inner)> OuterAndInnerAsync()
        {
            var checkouts = (await ReadAllAsync(fixture.Service().ScanAsync(new(Epoch), default))).Single(static update => update.Kind == "project").Project!.Checkouts;
            return (checkouts.Single(checkout => checkout.Path == outer), checkouts.Single(checkout => checkout.Path == inner));
        }
    }

    [TestMethod]
    public async Task TheMainCheckout_IsJudgedForTheWholeRepository_WhenOnlyALinkedWorktreeIsAProject()
    {
        using var fixture = Fixture.Create();
        var repository = fixture.Repository("repository");
        fixture.AddRemote(repository);
        // A stash: local work of the repository, which the main checkout carries.
        repository.Write("readme.md", "changed");
        repository.Git("stash", "-q");
        var linked = Path.Combine(fixture.Home, "linked");
        repository.Git("worktree", "add", "-q", "--detach", linked, "origin/main");
        await fixture.Projects.UpsertFromPathAsync(linked);

        var events = await ReadAllAsync(fixture.Service().ScanAsync(new(Epoch), default));

        var main = events.Single(static update => update.Kind == "project").Project!.Checkouts.Single(checkout => checkout.Path == repository.Root);
        Assert.AreEqual((false, 1, "has_local_work"), (main.Project, main.Git!.Stashes, main.Verdict));
    }

    [TestMethod]
    public async Task WhatACleanWouldRemove_SaysWhenTheMeasureListedOnlyPartOfTheIgnoredEntries()
    {
        using var fixture = Fixture.Create();
        var repository = fixture.Repository("repository");
        repository.Write(".gitignore", "bin/\n*.log\n");
        repository.Write("logs/keep.txt", "kept");
        repository.Commit("ignore");
        repository.Write("bin/a.dll", "a");
        for (var index = 0; index <= CheckoutUsageScanner.MaximumIgnoredEntries; index++) repository.Write($"logs/{index:D3}.log", "ll");
        await fixture.Projects.UpsertFromPathAsync(repository.Root);

        var checkout = (await ReadAllAsync(fixture.Service().ScanAsync(new(Epoch), default))).Single(static update => update.Kind == "project").Project!.Checkouts.Single();

        Assert.AreEqual((1L, 0, true), (checkout.Measure!.BuildOutputBytes, checkout.Planned.Length, checkout.PlannedTruncated));
    }

    [TestMethod]
    public async Task ProjectsTheCatalogCouldNotRead_AreNamed_AndTheMeasurementIsIncomplete()
    {
        using var fixture = Fixture.Create();
        var repository = fixture.Repository("repository");
        await fixture.Projects.UpsertFromPathAsync(repository.Root);
        var broken = Path.Combine(fixture.Options.ProjectsRoot, "broken.md");
        fixture.Unreadable.Add(new(broken, "The front matter is not YAML."));
        var service = fixture.Service();

        var done = (await ReadAllAsync(service.ScanAsync(new(Epoch), default)))[^1].Done!;

        Assert.AreEqual((false, 1), (done.Complete, done.Projects));
        CollectionAssert.AreEqual(new[] { broken }, done.Unreadable);
        var last = (await service.LastAsync(new(Epoch), default)).Measurement!;
        Assert.IsFalse(last.Complete);
        CollectionAssert.AreEqual(new[] { broken }, last.Unreadable);
    }

    [TestMethod]
    public async Task ASecondRequest_JoiningAfterAProjectWasMeasured_ReceivesIt_AndEveryLaterRecord()
    {
        using var fixture = Fixture.Create();
        var quick = fixture.Repository("quick");
        var slow = fixture.Repository("slow");
        var quickProject = await fixture.Projects.UpsertFromPathAsync(quick.Root);
        await fixture.Projects.UpsertFromPathAsync(slow.Root);
        var gate = new Gate(fixture, slow.Root);
        var service = fixture.Service(gate.RunAsync);

        await using var first = service.ScanAsync(new(Epoch), default).GetAsyncEnumerator();
        var one = new List<DiskUsageScanEvent>();
        while (await first.MoveNextAsync().AsTask().WithinAsync(Patience))
        {
            one.Add(first.Current);
            if (first.Current.Project?.Id == quickProject.Id) break;
        }

        await gate.Entered.Task.WithinAsync(Patience);
        await using var second = service.ScanAsync(new(Epoch), default).GetAsyncEnumerator();
        var two = new List<DiskUsageScanEvent>();
        // What was produced is given at once: no wait for the project that is still measured.
        while (two.All(update => update.Project?.Id != quickProject.Id) && await second.MoveNextAsync().AsTask().WithinAsync(TestTime.Seconds(10))) two.Add(second.Current);
        Assert.IsTrue((await service.LastAsync(new(Epoch), default)).Running);
        gate.Open();
        while (await first.MoveNextAsync().AsTask().WithinAsync(Patience)) one.Add(first.Current);
        while (await second.MoveNextAsync().AsTask().WithinAsync(Patience)) two.Add(second.Current);

        // One scan: the catalog was read once and each of the two checkouts measured once.
        Assert.AreEqual((1, 2), (fixture.WorkspaceReads, gate.Measures));
        static DiskUsageScanEvent[] Records(List<DiskUsageScanEvent> events) => [.. events.Where(static update => update.Kind != "progress")];
        CollectionAssert.AreEqual(Records(one), Records(two));
        CollectionAssert.AreEqual(new[] { "project", "project", "done" }, Records(one).Select(static update => update.Kind).ToArray());
    }

    [TestMethod]
    public async Task CancellingOneListener_KeepsTheScan_CancellingTheLast_StopsItsWorker_AndKeepsTheLastMeasurement()
    {
        using var fixture = Fixture.Create();
        var repository = fixture.Repository("repository");
        await fixture.Projects.UpsertFromPathAsync(repository.Root);
        var service = fixture.Service();
        var completed = (await ReadAllAsync(service.ScanAsync(new(Epoch), default)))[^1].Done!;

        // Two follow; one goes away: the other gets the end.
        var gate = new Gate(fixture);
        service = fixture.Service(gate.RunAsync);
        using (var leaving = new CancellationTokenSource())
        {
            var first = Task.Run(() => ReadAllAsync(service.ScanAsync(new(Epoch), leaving.Token)));
            await gate.Entered.Task.WithinAsync(Patience);
            await using var staying = service.ScanAsync(new(Epoch), default).GetAsyncEnumerator();
            Assert.IsTrue(await staying.MoveNextAsync());
            await leaving.CancelAsync();
            await Assert.ThrowsAsync<OperationCanceledException>(() => first.WithinAsync(Patience));
            Assert.IsFalse(gate.Canceled.Task.IsCompleted);
            gate.Open();
            var last = staying.Current;
            while (await staying.MoveNextAsync().AsTask().WithinAsync(Patience)) last = staying.Current;
            Assert.AreEqual("done", last.Kind);
        }

        var kept = (await service.LastAsync(new(Epoch), default)).Measurement!;

        // The only one goes away: the scan stops, its worker ends, and the measurement before it stays.
        gate = new Gate(fixture);
        var stopping = fixture.Service(gate.RunAsync);
        Assert.AreEqual(kept.CompletedAt, (await stopping.LastAsync(new(Epoch), default)).Measurement!.CompletedAt, "The file keeps the last measurement.");
        using (var only = new CancellationTokenSource())
        {
            var reading = Task.Run(() => ReadAllAsync(stopping.ScanAsync(new(Epoch), only.Token)));
            await gate.Entered.Task.WithinAsync(Patience);
            await only.CancelAsync();
            await Assert.ThrowsAsync<OperationCanceledException>(() => reading.WithinAsync(Patience));
            await gate.Canceled.Task.WithinAsync(Patience);
            await stopping.Worker!.WithinAsync(Patience);
        }

        var after = await stopping.LastAsync(new(Epoch), default);
        Assert.AreEqual((false, kept.CompletedAt), (after.Running, after.Measurement!.CompletedAt));
        Assert.AreEqual(kept.CompletedAt, (await DiskUsageService.ReadFileAsync(stopping.StatePath!, DiskUsageService.MaximumFileBytesDefault, default))!.CompletedAt);
        Assert.AreNotEqual(completed.CompletedAt, kept.CompletedAt);
    }

    [TestMethod]
    public async Task TheLastListenerLeavingAtTheCommit_CancelsTheScan_AndNothingIsKept()
    {
        using var fixture = Fixture.Create();
        var repository = fixture.Repository("repository");
        await fixture.Projects.UpsertFromPathAsync(repository.Root);
        var atCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = fixture.Service(stage: async (stage, _) =>
        {
            if (stage != "commit") return;
            atCommit.TrySetResult();
            await release.Task;
        });

        using (var leaving = new CancellationTokenSource())
        {
            var reading = Task.Run(() => ReadAllAsync(service.ScanAsync(new(Epoch), leaving.Token)));
            await atCommit.Task.WithinAsync(Patience);
            await leaving.CancelAsync();
            await Assert.ThrowsAsync<OperationCanceledException>(() => reading.WithinAsync(Patience));
            release.SetResult();
            await service.Worker!.WithinAsync(Patience);
        }

        Assert.IsNull((await service.LastAsync(new(Epoch), default)).Measurement);
        Assert.IsFalse(File.Exists(service.StatePath));
    }

    [TestMethod]
    public async Task AFailureOfTheWorker_EndsEveryListenerWithAFailedRecord()
    {
        using var fixture = Fixture.Create();
        var repository = fixture.Repository("repository");
        await fixture.Projects.UpsertFromPathAsync(repository.Root);
        var service = fixture.Service((folder, arguments, timeout, token) => arguments is ["status", ..]
            ? throw new TimeoutException("git did not answer.")
            : fixture.RunAsync(folder, arguments, timeout, token));

        var events = await ReadAllAsync(service.ScanAsync(new(Epoch), default)).WithinAsync(Patience);

        Assert.AreEqual(("ok", "failed", "git did not answer."), (events[^1].Status, events[^1].Kind, events[^1].Problem));
        Assert.IsNull((await service.LastAsync(new(Epoch), default)).Measurement);
    }

    [TestMethod]
    public async Task AListenerThatJoinsAfterTheEnd_ReceivesTheEndLast()
    {
        using var fixture = Fixture.Create();
        var repository = fixture.Repository("repository");
        await fixture.Projects.UpsertFromPathAsync(repository.Root);
        var atDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = fixture.Service(stage: async (stage, _) =>
        {
            if (stage != "done") return;
            atDone.TrySetResult();
            await release.Task;
        });

        var first = Task.Run(() => ReadAllAsync(service.ScanAsync(new(Epoch), default)));
        await atDone.Task.WithinAsync(Patience);
        var second = Task.Run(() => ReadAllAsync(service.ScanAsync(new(Epoch), default)));
        await Task.Yield();
        release.SetResult();

        foreach (var events in new[] { await first.WithinAsync(Patience), await second.WithinAsync(Patience) })
            Assert.AreEqual("done", events[^1].Kind, string.Join(", ", events.Select(static update => update.Kind)));
    }

    [TestMethod]
    public async Task AListenerThatDoesNotRead_HoldsTheRecordsAndOnlyTheLatestProgress()
    {
        var broadcast = new DiskUsageBroadcast();
        var listener = broadcast.TryJoin()!;
        for (var index = 0; index < 100_000; index++) broadcast.Publish(new("ok", "progress") { Progress = new("projects", null, 0, 3, index) });
        for (var index = 0; index < 3; index++) broadcast.Publish(new("ok", "project"));
        broadcast.Publish(new("ok", "progress") { Progress = new("projects", null, 3, 3, 1) });
        broadcast.Publish(new("ok", "done"));
        broadcast.Publish(new("ok", "progress") { Progress = new("projects", null, 3, 3, 2) });
        broadcast.End();

        Assert.IsLessThanOrEqualTo(5, listener.Buffered);
        var read = new List<DiskUsageScanEvent>();
        await foreach (var update in listener.ReadAllAsync(default)) read.Add(update);
        Assert.AreEqual("done", read[^1].Kind);
        Assert.AreEqual(3, read.Count(static update => update.Kind == "project"));
    }

    [TestMethod]
    public async Task Disposal_StopsAndAwaitsTheWorker_WritesNothing_AndRefusesLaterScans()
    {
        using var fixture = Fixture.Create();
        var repository = fixture.Repository("repository");
        await fixture.Projects.UpsertFromPathAsync(repository.Root);
        var gate = new Gate(fixture);
        var service = fixture.Service(gate.RunAsync);
        try
        {
            var reading = Task.Run(() => ReadAllAsync(service.ScanAsync(new(Epoch), default)));
            await gate.Entered.Task.WithinAsync(Patience);

            await service.DisposeAsync().AsTask().WithinAsync(Patience);

            Assert.IsTrue(service.Worker!.IsCompleted);
            await gate.Canceled.Task.WithinAsync(Patience);
            await reading.WithinAsync(Patience);
            Assert.IsFalse(File.Exists(service.StatePath));
            Assert.AreEqual("unavailable", (await ReadAllAsync(service.ScanAsync(new(Epoch), default))).Single().Status);
        }
        finally
        {
            gate.Open();
        }
    }

    [TestMethod]
    public async Task Last_IsTheLastCompleteMeasurement_FromMemory_ThenFromAWholeFileOfTheInstance()
    {
        using var fixture = Fixture.Create();
        var repository = fixture.Repository("repository");
        await fixture.Projects.UpsertFromPathAsync(repository.Root);
        var service = fixture.Service();
        var none = await service.LastAsync(new(Epoch), default);
        Assert.AreEqual(("ok", (DiskUsageMeasurement?)null, false, false), (none.Status, none.Measurement, none.Running, none.Current));

        var done = (await ReadAllAsync(service.ScanAsync(new(Epoch), default)))[^1].Done!;
        var answer = await service.LastAsync(new(Epoch), default);
        Assert.IsTrue(answer.Current, "A measurement of this run can be cleaned against.");
        var last = answer.Measurement!;
        Assert.AreEqual((done.StartedAt, done.CompletedAt, 1, true), (last.StartedAt, last.CompletedAt, last.Projects.Length, last.Complete));
        Assert.IsTrue(service.LastMeasures.ContainsKey(repository.Root));

        // The file is in the state of the instance: a developer instance has its own.
        Assert.AreEqual(Path.Combine(fixture.Options.StateRoot, "cache", DiskUsageService.FileName), service.StatePath);
        Assert.AreNotEqual(fixture.Options.GlobalRoot, fixture.Options.StateRoot);
        var restarted = await fixture.Service().LastAsync(new(Epoch), default);
        Assert.AreEqual((last.CompletedAt, last.Projects[0].Checkouts[0].Measure), (restarted.Measurement!.CompletedAt, restarted.Measurement.Projects[0].Checkouts[0].Measure));
        Assert.IsFalse(restarted.Current, "The file of an earlier run has no measure to clean against.");
        // A file larger than the bound is not read.
        Assert.IsNull((await fixture.Service(maximumFileBytes: 100).LastAsync(new(Epoch), default)).Measurement);

        // A file of another version, one that is not whole, or one that cannot be read, gives none.
        var text = File.ReadAllText(service.StatePath!);
        foreach (var other in new[]
        {
            text.Replace($"\"version\":{DiskUsageService.FileVersion}", "\"version\":99", StringComparison.Ordinal),
            // The shape of before, with the profile.
            text.Replace($"\"version\":{DiskUsageService.FileVersion}", "\"version\":1", StringComparison.Ordinal),
            $"{{\"version\":{DiskUsageService.FileVersion},\"measurement\":{{\"projects\":[]}}}}",
            text.Replace("\"unpushedBranches\":[", "\"unpushedBranches\":[null,", StringComparison.Ordinal),
            text.Replace("\"checkouts\":[", "\"checkouts\":[null,", StringComparison.Ordinal),
            "{ not json",
        })
        {
            File.WriteAllText(service.StatePath!, other);
            Assert.IsNull((await fixture.Service().LastAsync(new(Epoch), default)).Measurement, other);
        }
    }

    [TestMethod]
    public async Task Requests_OfAnotherEpoch_OrWithoutAHost_AreRefused()
    {
        using var fixture = Fixture.Create();
        var service = fixture.Service();
        Assert.AreEqual(("stale_epoch", null as string), (await ReadAllAsync(service.ScanAsync(new("other"), default))) is [var stale] ? (stale.Status, stale.Kind) : default);
        Assert.AreEqual("stale_epoch", (await service.LastAsync(new("other"), default)).Status);
        var unavailable = new DiskUsageService();
        Assert.AreEqual(("unavailable", null as string), (await ReadAllAsync(unavailable.ScanAsync(new(Epoch), default))) is [var refused] ? (refused.Status, refused.Kind) : default);
        var last = await unavailable.LastAsync(new(Epoch), default);
        Assert.AreEqual(("unavailable", (DiskUsageMeasurement?)null), (last.Status, last.Measurement));
        Assert.AreEqual(0, fixture.WorkspaceReads);
    }

    [TestMethod]
    public async Task TheDiskUsage_NeverProtectsLessThanTheWorktreesWindow()
    {
        TestLinks.RequireSymbolicLinks();
        using var fixture = Fixture.Create();
        var repository = fixture.Repository("repository");
        var project = await fixture.Projects.UpsertFromPathAsync(repository.Root);
        var busyOne = (await fixture.Worktrees.CreateAsync(project)).Root!;
        var first = (await fixture.Worktrees.CreateAsync(project)).Root!;
        var second = (await fixture.Worktrees.CreateAsync(project)).Root!;
        // A session at work in a folder that became a link to outside the repository.
        var sub = Directory.CreateDirectory(Path.Combine(busyOne, "sub")).FullName;
        fixture.Busy.Add(new("working", sub, true));
        Directory.Move(sub, Path.Combine(busyOne, "sub-moved"));
        Directory.CreateSymbolicLink(sub, Directory.CreateDirectory(Path.Combine(fixture.Home, "outside")).FullName);
        // A worktree replaced by a link to another, whose session stays its own.
        foreach (var file in Directory.EnumerateFiles(first, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(first, recursive: true);
        Directory.CreateSymbolicLink(first, second);
        fixture.Session("in-second", project, 10, second);
        var window = await new WorktreesService(fixture.Worktrees, fixture.Projects, new CodeAltaConfigStore(fixture.Options), () => [.. fixture.Busy], Epoch,
            _ => Task.FromResult<IReadOnlyList<AgentSessionMetadata>>(fixture.Sessions), new DiskFolders(), new DesktopEditorView()).InventoryAsync(new(Epoch, project.Id), default);

        var checkouts = (await ReadAllAsync(fixture.Service().ScanAsync(new(Epoch), default))).Single(static update => update.Kind == "project").Project!.Checkouts;

        foreach (var listed in window.Worktrees)
        {
            var row = checkouts.Single(checkout => checkout.Path == listed.Path);
            Assert.IsTrue(!listed.Busy || row.InUse, listed.Path);
            Assert.IsTrue(listed.Protection is null || row.Protection is not null, listed.Path);
            Assert.AreEqual(listed.SessionCount, row.SessionCount, listed.Path);
        }

        Assert.AreEqual((true, "in_use"), (checkouts.Single(checkout => checkout.Path == busyOne).InUse, checkouts.Single(checkout => checkout.Path == busyOne).Protection));
        Assert.AreEqual((0, 1), (checkouts.Single(checkout => checkout.Path == first).SessionCount, checkouts.Single(checkout => checkout.Path == second).SessionCount));
    }

    [TestMethod]
    public async Task TheTotalCountsEveryByteOnce_ForAProjectInTheStateFolderWithItsOwnSessions()
    {
        using var fixture = Fixture.Create();
        var repository = fixture.Repository("repository");
        await fixture.Projects.UpsertFromPathAsync(repository.Root);
        Directory.CreateDirectory(fixture.Options.StateRoot);
        var state = await fixture.Projects.UpsertFromPathAsync(fixture.Options.StateRoot);
        state.Archived = true;
        await fixture.Projects.SaveAsync(state);
        fixture.Session("of-the-state-folder", state, 100);

        var events = await ReadAllAsync(fixture.Service().ScanAsync(new(Epoch), default));

        AssertTotalsAddUp(events);
    }

    [TestMethod]
    public async Task TheTotalCountsEveryByteOnce_ForAProjectAtTheProfileRoot()
    {
        using var fixture = Fixture.Create();
        fixture.SessionFile("2026/11/10/lost.jsonl", 13);
        await fixture.Projects.UpsertFromPathAsync(fixture.Options.GlobalRoot);

        var events = await ReadAllAsync(fixture.Service().ScanAsync(new(Epoch), default));

        AssertTotalsAddUp(events);
    }

    [TestMethod]
    public async Task ACheckoutListCutAtItsBound_LeavesTheMeasurementIncomplete()
    {
        using var fixture = Fixture.Create();
        var repository = fixture.Repository("repository");
        var project = await fixture.Projects.UpsertFromPathAsync(repository.Root);
        await fixture.Worktrees.CreateAsync(project);

        var events = await ReadAllAsync(fixture.Service(checkoutBound: 1).ScanAsync(new(Epoch), default));

        Assert.IsTrue(events.Single(static update => update.Kind == "project").Project!.CheckoutsTruncated);
        var done = events[^1].Done!;
        Assert.AreEqual((true, false), (done.TotalPartial, done.Complete));
    }

    [TestMethod]
    public async Task EveryDisposal_AwaitsTheSameEndOfTheWorker()
    {
        using var fixture = Fixture.Create();
        var repository = fixture.Repository("repository");
        await fixture.Projects.UpsertFromPathAsync(repository.Root);
        var atCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = fixture.Service(stage: async (stage, _) =>
        {
            if (stage != "commit") return;
            atCommit.TrySetResult();
            await release.Task;
        });
        var reading = Task.Run(() => ReadAllAsync(service.ScanAsync(new(Epoch), default)));
        await atCommit.Task.WithinAsync(Patience);

        var one = service.DisposeAsync().AsTask();
        var two = service.DisposeAsync().AsTask();
        await Task.Yield();

        Assert.IsFalse(one.IsCompleted || two.IsCompleted, "no disposal ends before the worker");
        release.SetResult();
        await Task.WhenAll(one, two).WithinAsync(Patience);
        Assert.IsTrue(service.Worker!.IsCompleted);
        await reading.WithinAsync(Patience);
    }

    [TestMethod]
    public async Task Clean_RemovesTheConfirmedBuildFolders_AndTheLastMeasurementShowsTheNewState()
    {
        using var fixture = Fixture.Create();
        var (repository, project) = await fixture.BuildingRepositoryAsync();
        var service = fixture.Service();
        var before = (await ReadAllAsync(service.ScanAsync(new(Epoch), default)))[^1].Done!;
        var shown = await ShownAsync(service, project, repository.Root);
        CollectionAssert.AreEquivalent(new[] { "bin", "obj" }, shown.Planned.Select(static entry => entry.Path).ToArray());

        var answer = await CleanAsync(service, project, repository.Root, "bin");

        Assert.AreEqual(("ok", null as string, 1000L, false), (answer.Status, answer.Message, answer.BytesFreed, answer.MoreRemain));
        Assert.AreEqual(("bin", "removed", 1000L, 1000L), answer.Entries is [var entry] ? (entry.Path, entry.Outcome, entry.BytesBefore, entry.BytesFreed) : default);
        Assert.IsFalse(Directory.Exists(repository.At("bin")));
        Assert.IsTrue(File.Exists(repository.At("obj/x.o")));
        // The checkout measured again, as a fresh record with a measure of its own.
        var cleaned = answer.Checkout!;
        Assert.AreEqual((repository.Root, shown.Measure!.TotalBytes - 1000, shown.Measure.BuildOutputBytes - 1000), (cleaned.Path, cleaned.Measure!.TotalBytes, cleaned.Measure.BuildOutputBytes));
        CollectionAssert.AreEqual(new[] { "obj" }, cleaned.Planned.Select(static planned => planned.Path).ToArray());
        Assert.IsNotNull(cleaned.MeasureId);
        Assert.AreNotEqual(shown.MeasureId, cleaned.MeasureId);
        // The last measurement has it, in memory and in the file, with its total.
        var last = (await service.LastAsync(new(Epoch), default)).Measurement!;
        Assert.AreEqual((cleaned, before.TotalBytes - 1000), (last.Projects.Single().Checkouts.Single(), last.TotalBytes));
        Assert.AreEqual((cleaned.Measure.TotalBytes, cleaned.MeasureId), (service.LastMeasures[repository.Root].Usage.TotalBytes, service.LastMeasures[repository.Root].Id));
        var kept = (await fixture.Service().LastAsync(new(Epoch), default)).Measurement!;
        Assert.AreEqual((cleaned.Measure, before.TotalBytes - 1000), (kept.Projects.Single().Checkouts.Single().Measure, kept.TotalBytes));
        AssertMeasurementAddsUp(last);
        Assert.IsNull(service.Cleaning);
    }

    [TestMethod]
    public async Task Clean_IsRefused_ForAnInvalidRequest_OrWhatTheLastMeasurementDoesNotHave()
    {
        using var fixture = Fixture.Create();
        var (repository, project) = await fixture.BuildingRepositoryAsync();
        var project2 = fixture.Repository("other");
        var otherProject = await fixture.Projects.UpsertFromPathAsync(project2.Root);
        var gone = (await fixture.Worktrees.CreateAsync(project)).Root!;
        var service = fixture.Service();
        // Nothing measured yet: nothing to clean.
        Assert.AreEqual("not_found", (await service.CleanAsync(new(Epoch, project.Id, repository.Root, "id", ["bin"]))).Status);
        Directory.Delete(gone, recursive: true);
        await ReadAllAsync(service.ScanAsync(new(Epoch), default));
        var id = (await ShownAsync(service, project, repository.Root)).MeasureId;

        Assert.AreEqual("stale_epoch", (await service.CleanAsync(new("other", project.Id, repository.Root, id, ["bin"]))).Status);
        Assert.AreEqual("unavailable", (await new DiskUsageService().CleanAsync(new(Epoch, project.Id, repository.Root, id, ["bin"]))).Status);
        foreach (var invalid in new DiskUsageCleanRequest[]
        {
            new(Epoch, null, repository.Root, id, ["bin"]),
            new(Epoch, project.Id, null, id, ["bin"]),
            new(Epoch, project.Id, "relative", id, ["bin"]),
            new(Epoch, project.Id, repository.Root, null, ["bin"]),
            new(Epoch, project.Id, repository.Root, id, null),
            new(Epoch, project.Id, repository.Root, id, []),
            new(Epoch, project.Id, repository.Root, id, ["bin", "bin"]),
            new(Epoch, project.Id, repository.Root, id, ["../bin"]),
            new(Epoch, project.Id, repository.Root, id, [repository.At("bin")]),
            new(Epoch, project.Id, repository.Root, id, ["bin\0x"]),
            new(Epoch, project.Id, repository.Root, id, [new string('a', 5000)]),
            new(Epoch, project.Id, repository.Root, id, [.. Enumerable.Range(0, DiskUsageService.MaximumPlannedEntries + 1).Select(static index => $"bin{index}")]),
            // Only what the record listed, exactly: no alias, no other folder.
            new(Epoch, project.Id, repository.Root, id, ["BIN"]),
            new(Epoch, project.Id, repository.Root, id, ["bin/"]),
            new(Epoch, project.Id, repository.Root, id, ["./bin"]),
            new(Epoch, project.Id, repository.Root, id, ["bin", "dist"]),
            new(Epoch, project.Id, repository.Root, id, ["readme.md"]),
        })
            Assert.AreEqual("invalid_request", (await service.CleanAsync(invalid)).Status, string.Join(",", invalid.Paths ?? []));

        Assert.AreEqual("not_found", (await service.CleanAsync(new(Epoch, "no-such-project", repository.Root, id, ["bin"]))).Status);
        Assert.AreEqual("not_found", (await service.CleanAsync(new(Epoch, otherProject.Id, repository.Root, id, ["bin"]))).Status);
        Assert.AreEqual("not_found", (await service.CleanAsync(new(Epoch, project.Id, Path.Combine(fixture.Home, "elsewhere"), id, ["bin"]))).Status);
        Assert.AreEqual("not_found", (await service.CleanAsync(new(Epoch, project.Id, gone, id, ["bin"]))).Status);
        Assert.IsTrue(Directory.Exists(repository.At("bin")));
        Assert.IsNull(service.Cleaning);
    }

    [TestMethod]
    public async Task Clean_IsRefused_NotMeasured_WhenTheMeasureIsOnlyInTheFile()
    {
        using var fixture = Fixture.Create();
        var (repository, project) = await fixture.BuildingRepositoryAsync();
        await ReadAllAsync(fixture.Service().ScanAsync(new(Epoch), default));
        // Another run of the application: the measurement comes from the file, the measure the user saw is not in memory.
        var restarted = fixture.Service();

        var answer = await CleanAsync(restarted, project, repository.Root, "bin");

        Assert.AreEqual(("not_measured", 0), (answer.Status, answer.Entries.Length));
        Assert.IsTrue(Directory.Exists(repository.At("bin")));
    }

    [TestMethod]
    public async Task Clean_IsRefused_StaleMeasure_WhenTheCheckoutWasMeasuredAgainSinceThePageShowedIt()
    {
        using var fixture = Fixture.Create();
        var (repository, project) = await fixture.BuildingRepositoryAsync();
        var service = fixture.Service();
        await ReadAllAsync(service.ScanAsync(new(Epoch), default));
        var shownInTheFirstWindow = (await ShownAsync(service, project, repository.Root)).MeasureId;
        // A second window measures again.
        await ReadAllAsync(service.ScanAsync(new(Epoch), default));

        var answer = await service.CleanAsync(new(Epoch, project.Id, repository.Root, shownInTheFirstWindow, ["bin"]));

        Assert.AreEqual("stale_measure", answer.Status);
        Assert.IsTrue(Directory.Exists(repository.At("bin")));
        Assert.IsNull(service.Cleaning);
    }

    [TestMethod]
    public async Task Clean_IsRefused_StaleMeasure_WhenTheFoldersMeasuredOnTheirOwnInTheCheckoutChanged()
    {
        using var fixture = Fixture.Create();
        var (repository, project) = await fixture.BuildingRepositoryAsync();
        var service = fixture.Service();
        await ReadAllAsync(service.ScanAsync(new(Epoch), default));
        // A project of its own registered inside the checkout after the measure: the checkout is now measured without it.
        var nested = fixture.Repository(Path.Combine("building", "bin", "tool"));
        await fixture.Projects.UpsertFromPathAsync(nested.Root);

        var answer = await CleanAsync(service, project, repository.Root, "bin");

        Assert.AreEqual("stale_measure", answer.Status);
        Assert.IsTrue(File.Exists(repository.At("bin/app.dll")));
        Assert.IsTrue(Directory.Exists(nested.Root));
        Assert.IsNull(service.Cleaning);
    }

    [TestMethod]
    public async Task Clean_IsRefused_Busy_WhenASessionStartsWorkingInTheCheckoutAfterTheMeasurement()
    {
        using var fixture = Fixture.Create();
        var (repository, project) = await fixture.BuildingRepositoryAsync();
        var service = fixture.Service();
        await ReadAllAsync(service.ScanAsync(new(Epoch), default));
        fixture.Busy.Add(new("late", Directory.CreateDirectory(repository.At("src")).FullName, true));

        var answer = await CleanAsync(service, project, repository.Root, "bin");

        Assert.AreEqual("busy", answer.Status);
        Assert.IsTrue(Directory.Exists(repository.At("bin")));
        Assert.IsNull(service.Cleaning);
    }

    [TestMethod]
    public async Task Clean_IsRefused_Busy_WhenASessionHasOnlyABackgroundCommandInTheCheckout()
    {
        using var fixture = Fixture.Create();
        var (repository, project) = await fixture.BuildingRepositoryAsync();
        var service = fixture.Service();
        await ReadAllAsync(service.ScanAsync(new(Epoch), default));
        // No run: the Worktrees window does not see it; a clean does.
        fixture.Working.Add(new("with-a-command", repository.Root, false));

        var answer = await CleanAsync(service, project, repository.Root, "bin");

        Assert.AreEqual("busy", answer.Status);
        Assert.IsTrue(Directory.Exists(repository.At("bin")));
    }

    [TestMethod]
    public async Task Clean_IsRefused_Busy_WhenASessionWorksInASubmoduleOfTheCheckout()
    {
        using var fixture = Fixture.Create();
        var (repository, project) = await fixture.BuildingRepositoryAsync();
        fixture.AddSubmodule(repository, "lib");
        var service = fixture.Service();
        await ReadAllAsync(service.ScanAsync(new(Epoch), default));
        fixture.Busy.Add(new("in-the-submodule", repository.At("lib"), false));

        var answer = await CleanAsync(service, project, repository.Root, "bin");

        Assert.AreEqual("busy", answer.Status);
        Assert.IsTrue(Directory.Exists(repository.At("bin")));
    }

    [TestMethod]
    public async Task ASessionThatStartsBetweenTwoRunsOfGit_StopsTheRunsThatRemain()
    {
        using var fixture = Fixture.Create();
        var (repository, project) = await fixture.BuildingRepositoryAsync();
        fixture.AddSubmodule(repository, "lib");
        File.WriteAllText(repository.At("lib/.gitignore"), "bin/\n");
        repository.Write("lib/bin/lib.dll", new string('l', 200));
        var holding = new CleanGate(fixture.RunAsync);
        var service = fixture.Service(holding.RunAsync);
        await ReadAllAsync(service.ScanAsync(new(Epoch), default));
        CollectionAssert.IsSubsetOf(new[] { "bin", "lib/bin" }, (await ShownAsync(service, project, repository.Root)).Planned.Select(static entry => entry.Path).ToArray());

        var cleaning = CleanAsync(service, project, repository.Root, "bin", "lib/bin");
        await holding.Entered.Task.WithinAsync(Patience);
        fixture.Working.Add(new("started", repository.At("src"), false));
        holding.Open();
        var answer = await cleaning.WithinAsync(Patience);

        Assert.AreEqual("busy", answer.Status, answer.Message);
        CollectionAssert.AreEqual(new[] { "removed", "not_attempted" }, answer.Entries.Select(static entry => entry.Outcome).ToArray());
        Assert.IsTrue(File.Exists(repository.At("lib/bin/lib.dll")));
        Assert.IsFalse(Directory.Exists(repository.At("bin")));
        Assert.IsNull(service.Cleaning);
    }

    [TestMethod]
    public async Task Clean_IsRefused_WhenTheApplicationRunsFromInsideTheCheckout()
    {
        using var fixture = Fixture.Create();
        var (repository, project) = await fixture.BuildingRepositoryAsync();
        var service = fixture.Service(applicationPaths: [repository.At("bin/alta")]);
        await ReadAllAsync(service.ScanAsync(new(Epoch), default));

        var answer = await CleanAsync(service, project, repository.Root, "bin");

        Assert.AreEqual("running_application", answer.Status);
        Assert.IsTrue(Directory.Exists(repository.At("bin")));
    }

    [TestMethod]
    public async Task Clean_IsRefused_WhenAnotherProgramRunsFromInsideTheCheckout()
    {
        using var fixture = Fixture.Create();
        var (repository, project) = await fixture.BuildingRepositoryAsync();
        var service = fixture.Service();
        await ReadAllAsync(service.ScanAsync(new(Epoch), default));
        // A program started from the build folder: a server, a test run, another instance.
        var (source, arguments) = OperatingSystem.IsWindows()
            ? (Path.Combine(Environment.SystemDirectory, "ping.exe"), new[] { "-n", "600", "127.0.0.1" })
            : ("/bin/sleep", new[] { "600" });
        var program = repository.At("bin/" + Path.GetFileName(source));
        File.Copy(source, program);
        // macOS kills a copy of a program of the system that keeps its signature: it is signed again for this place.
        if (OperatingSystem.IsMacOS()) Assert.AreEqual(0, RunTool("codesign", "-s", "-", "-f", program));
        using var running = Process.Start(new ProcessStartInfo(program, arguments) { UseShellExecute = false, RedirectStandardOutput = true })!;
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(300)); // not a limit
            Assert.IsFalse(running.HasExited, "the program runs from the build folder");
            var answer = await CleanAsync(service, project, repository.Root, "bin");

            Assert.AreEqual("running_application", answer.Status);
            Assert.IsTrue(File.Exists(program));
        }
        finally
        {
            running.Kill(entireProcessTree: true);
            await running.WaitForExitAsync().WithinAsync(Patience);
        }
    }

    [TestMethod]
    public async Task AConfirmedPathThatWasNotShown_MakesTheRequestInvalid_AndNothingGoes()
    {
        using var fixture = Fixture.Create();
        var (repository, project) = await fixture.BuildingRepositoryAsync();
        var service = fixture.Service();
        await ReadAllAsync(service.ScanAsync(new(Epoch), default));
        // A build folder that appeared after the measure: the user never saw it.
        repository.Write("dist/late.js", new string('l', 300));

        var answer = await CleanAsync(service, project, repository.Root, "bin", "dist");

        Assert.AreEqual(("invalid_request", 0), (answer.Status, answer.Entries.Length));
        foreach (var path in new[] { "dist/late.js", "bin/app.dll", "obj/x.o" }) Assert.IsTrue(File.Exists(repository.At(path)), path);
    }

    [TestMethod]
    public async Task MorePlannedFoldersThanTheRecordShows_OnlyTheShownOnesMayBeConfirmed_AndMoreRemain()
    {
        using var fixture = Fixture.Create();
        var repository = fixture.Repository("many");
        repository.Write(".gitignore", "bin/\n");
        for (var index = 0; index <= DiskUsageService.MaximumPlannedEntries; index++) repository.Write($"p{index:D3}/keep.txt", "kept");
        repository.Commit("projects");
        for (var index = 0; index <= DiskUsageService.MaximumPlannedEntries; index++) repository.Write($"p{index:D3}/bin/a.dll", new string('a', 10 + index));
        var project = await fixture.Projects.UpsertFromPathAsync(repository.Root);
        var service = fixture.Service();
        await ReadAllAsync(service.ScanAsync(new(Epoch), default));
        var shown = await ShownAsync(service, project, repository.Root);
        Assert.AreEqual((DiskUsageService.MaximumPlannedEntries, true), (shown.Planned.Length, shown.PlannedTruncated));
        // The smallest is the one the record does not show.
        Assert.IsFalse(shown.Planned.Any(static entry => entry.Path == "p000/bin"));

        Assert.AreEqual("invalid_request", (await CleanAsync(service, project, repository.Root, "p000/bin")).Status);
        var answer = await CleanAsync(service, project, repository.Root, "p100/bin");

        Assert.AreEqual(("ok", true), (answer.Status, answer.MoreRemain));
        Assert.IsTrue(File.Exists(repository.At("p000/bin/a.dll")));
        Assert.IsFalse(Directory.Exists(repository.At("p100/bin")));
    }

    [TestMethod]
    public async Task ACleanWhileAScanRuns_OrWhileAStoppedScanStillEnds_IsRefused_AndAScanWhileACleanRuns_WaitsForIt()
    {
        using var fixture = Fixture.Create();
        var (repository, project) = await fixture.BuildingRepositoryAsync();
        var holding = new CleanGate(fixture.RunAsync);
        var commit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var atCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold = 0;
        var service = fixture.Service(holding.RunAsync, stage: async (stage, token) =>
        {
            if (stage == "commit" && Volatile.Read(ref hold) == 1)
            {
                atCommit.TrySetResult();
                await commit.Task;
            }
        });
        await ReadAllAsync(service.ScanAsync(new(Epoch), default));

        // A scan whose last listener left, whose worker has not ended yet: no clean.
        Volatile.Write(ref hold, 1);
        using (var leaving = new CancellationTokenSource())
        {
            var reading = ReadAllAsync(service.ScanAsync(new(Epoch), leaving.Token));
            await atCommit.Task.WithinAsync(Patience);
            await leaving.CancelAsync();
            await Assert.ThrowsAsync<OperationCanceledException>(() => reading.WithinAsync(Patience));
        }

        Assert.IsFalse((await service.LastAsync(new(Epoch), default)).Running, "the stopped scan says it ended");
        Assert.AreEqual("scanning", (await CleanAsync(service, project, repository.Root, "bin").WithinAsync(Patience)).Status);
        Volatile.Write(ref hold, 0);
        commit.SetResult();
        await service.Worker!.WithinAsync(Patience);

        // A clean that runs: the scan waits for it, and measures what it left.
        var cleaning = CleanAsync(service, project, repository.Root, "bin");
        await holding.Entered.Task.WithinAsync(Patience);
        var scan = ReadAllAsync(service.ScanAsync(new(Epoch), default));
        Assert.IsTrue((await service.LastAsync(new(Epoch), default)).Running);
        Assert.IsFalse(scan.IsCompleted);
        holding.Open();
        Assert.AreEqual("ok", (await cleaning.WithinAsync(Patience)).Status);
        var events = await scan.WithinAsync(Patience);
        var measured = events.Single(static update => update.Kind == "project").Project!.Checkouts.Single();
        CollectionAssert.AreEqual(new[] { "obj" }, measured.Planned.Select(static planned => planned.Path).ToArray());
    }

    [TestMethod]
    public async Task TwoCleansAtOnce_TheSecondIsRefused()
    {
        using var fixture = Fixture.Create();
        var (repository, project) = await fixture.BuildingRepositoryAsync();
        var holding = new CleanGate(fixture.RunAsync);
        var service = fixture.Service(holding.RunAsync);
        await ReadAllAsync(service.ScanAsync(new(Epoch), default));

        var first = CleanAsync(service, project, repository.Root, "bin");
        await holding.Entered.Task.WithinAsync(Patience);
        var second = await CleanAsync(service, project, repository.Root, "obj");
        holding.Open();

        Assert.AreEqual(("cleaning", "ok"), (second.Status, (await first.WithinAsync(Patience)).Status));
        Assert.IsTrue(File.Exists(repository.At("obj/x.o")));
    }

    [TestMethod]
    public async Task TheChannelOfAClean_StreamsWhereItIs_ThenItsReport_AndAListenerThatLeavesDoesNotStopIt()
    {
        using var fixture = Fixture.Create();
        var (repository, project) = await fixture.BuildingRepositoryAsync();
        var holding = new CleanGate(fixture.RunAsync);
        holding.Open();
        var service = fixture.Service(holding.RunAsync);
        await ReadAllAsync(service.ScanAsync(new(Epoch), default));

        var events = new List<DiskUsageCleanEvent>();
        await foreach (var update in service.CleanStreamAsync(new(Epoch, project.Id, repository.Root, (await ShownAsync(service, project, repository.Root)).MeasureId, ["bin"]), default))
            events.Add(update);

        Assert.AreEqual("running", events[0].Kind);
        Assert.AreEqual((0, 1), (events[0].Progress!.Done, events[0].Progress!.Total));
        Assert.AreEqual(("done", "ok", 1000L), (events[^1].Kind, events[^1].Result!.Status, events[^1].Result!.BytesFreed));
        Assert.IsTrue(events.Take(events.Count - 1).All(static update => update.Kind == "running"));

        // A listener that leaves while it runs.
        var held = new CleanGate(fixture.RunAsync);
        var leaving = fixture.Service(held.RunAsync);
        await ReadAllAsync(leaving.ScanAsync(new(Epoch), default));
        await using (var reading = leaving.CleanStreamAsync(new(Epoch, project.Id, repository.Root, (await ShownAsync(leaving, project, repository.Root)).MeasureId, ["obj"]), default)
            .GetAsyncEnumerator())
        {
            Assert.IsTrue(await reading.MoveNextAsync().AsTask().WithinAsync(Patience));
            await held.Entered.Task.WithinAsync(Patience);
        }

        var cleaning = leaving.Cleaning!;
        held.Open();
        await cleaning.WithinAsync(Patience);
        Assert.IsFalse(Directory.Exists(repository.At("obj")));
        CollectionAssert.AreEqual(Array.Empty<string>(), (await ShownAsync(leaving, project, repository.Root)).Planned.Select(static planned => planned.Path).ToArray());
    }

    [TestMethod]
    public async Task DisposalDuringAClean_WaitsForIt()
    {
        using var fixture = Fixture.Create();
        var (repository, project) = await fixture.BuildingRepositoryAsync();
        var holding = new CleanGate(fixture.RunAsync);
        var service = fixture.Service(holding.RunAsync);
        await ReadAllAsync(service.ScanAsync(new(Epoch), default));
        var id = (await ShownAsync(service, project, repository.Root)).MeasureId;

        var cleaning = service.CleanAsync(new(Epoch, project.Id, repository.Root, id, ["bin"]));
        await holding.Entered.Task.WithinAsync(Patience);
        var disposal = service.DisposeAsync().AsTask();
        await Task.Yield();
        Assert.IsFalse(disposal.IsCompleted, "the disposal waits for the clean");
        Assert.AreEqual("unavailable", (await service.CleanAsync(new(Epoch, project.Id, repository.Root, id, ["obj"]))).Status);
        holding.Open();

        await disposal.WithinAsync(Patience);
        Assert.IsTrue(cleaning.IsCompleted);
        Assert.AreEqual("ok", (await cleaning).Status);
        Assert.IsFalse(Directory.Exists(repository.At("bin")));
    }

    [TestMethod]
    public async Task ASecondCleanOfTheSameCheckout_IsConfirmedAgainstTheMeasureOfTheFirst()
    {
        using var fixture = Fixture.Create();
        var (repository, project) = await fixture.BuildingRepositoryAsync();
        var service = fixture.Service();
        await ReadAllAsync(service.ScanAsync(new(Epoch), default));
        var first = (await ShownAsync(service, project, repository.Root)).MeasureId;
        // A build folder written after the scan: not in the first measure, in the one the first clean makes.
        repository.Write("dist/late.js", new string('l', 300));
        var cleaned = await CleanAsync(service, project, repository.Root, "bin");
        Assert.AreEqual("ok", cleaned.Status);
        CollectionAssert.AreEquivalent(new[] { "obj", "dist" }, cleaned.Checkout!.Planned.Select(static planned => planned.Path).ToArray());

        // The first measure confirms nothing any more; the second one confirms what it shows.
        Assert.AreEqual("stale_measure", (await service.CleanAsync(new(Epoch, project.Id, repository.Root, first, ["obj"]))).Status);
        Assert.AreEqual("invalid_request", (await CleanAsync(service, project, repository.Root, "bin")).Status);
        var second = await service.CleanAsync(new(Epoch, project.Id, repository.Root, cleaned.Checkout.MeasureId, ["dist"]));

        Assert.AreEqual(("ok", 300L), (second.Status, second.BytesFreed));
        Assert.IsFalse(Directory.Exists(repository.At("dist")));
        Assert.IsTrue(File.Exists(repository.At("obj/x.o")));
    }

    [TestMethod]
    public async Task ALinkedWorktree_IsCleaned_AsTheMainCheckoutIs()
    {
        using var fixture = Fixture.Create();
        var (repository, project) = await fixture.BuildingRepositoryAsync();
        var worktree = (await fixture.Worktrees.CreateAsync(project)).Root!;
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(worktree, "bin")).FullName, "w.dll"), new string('w', 700));
        var service = fixture.Service();
        await ReadAllAsync(service.ScanAsync(new(Epoch), default));

        var answer = await CleanAsync(service, project, worktree, "bin");

        Assert.AreEqual(("ok", 700L), (answer.Status, answer.BytesFreed));
        Assert.IsFalse(Directory.Exists(Path.Combine(worktree, "bin")));
        Assert.IsTrue(File.Exists(repository.At("bin/app.dll")));
        AssertMeasurementAddsUp((await service.LastAsync(new(Epoch), default)).Measurement!);
    }

    [TestMethod]
    public async Task ACheckoutInsideTheProfile_IsCleaned_AndTheTotalFollows()
    {
        using var fixture = Fixture.Create();
        var repository = fixture.Repository(Path.Combine("global", "work", "inside"));
        repository.Write(".gitignore", "bin/\n");
        repository.Commit("ignore");
        repository.Write("bin/app.dll", new string('b', 1000));
        var project = await fixture.Projects.UpsertFromPathAsync(repository.Root);
        var service = fixture.Service();
        await ReadAllAsync(service.ScanAsync(new(Epoch), default));
        var before = (await service.LastAsync(new(Epoch), default)).Measurement!;

        var answer = await CleanAsync(service, project, repository.Root, "bin");

        Assert.AreEqual(("ok", 1000L), (answer.Status, answer.BytesFreed));
        var last = (await service.LastAsync(new(Epoch), default)).Measurement!;
        Assert.AreEqual(before.TotalBytes - 1000, last.TotalBytes);
        AssertMeasurementAddsUp(last);
    }

    [TestMethod]
    public async Task ACheckoutChangedSinceTheMeasure_OrAGitThatFails_IsReported_AndTheNextCleanMayRun()
    {
        using var fixture = Fixture.Create();
        var (repository, project) = await fixture.BuildingRepositoryAsync();
        var failing = new CleanGate(fixture.RunAsync, failClean: true);
        failing.Open();
        var service = fixture.Service(failing.RunAsync);
        await ReadAllAsync(service.ScanAsync(new(Epoch), default));

        var failed = await CleanAsync(service, project, repository.Root, "bin");
        Assert.AreEqual(BuildOutputCleaner.Failed, failed.Status);
        Assert.IsTrue(File.Exists(repository.At("bin/app.dll")));
        Assert.IsNull(service.Cleaning);

        // The same paths, another repository.
        var other = fixture.Service();
        await ReadAllAsync(other.ScanAsync(new(Epoch), default));
        Directory.Move(repository.Root, repository.Root + "-old");
        var recreated = fixture.Repository("building");
        recreated.Write(".gitignore", "bin/\nobj/\ndist/\n");
        recreated.Commit("ignore");
        recreated.Write("bin/app.dll", new string('n', 1000));
        var changed = await CleanAsync(other, project, repository.Root, "bin");

        Assert.AreEqual(BuildOutputCleaner.CheckoutChanged, changed.Status, changed.Message);
        Assert.IsTrue(File.Exists(recreated.At("bin/app.dll")));
        Assert.IsNull(other.Cleaning);
    }

    [TestMethod]
    public async Task ACleanWhoseNewMeasureFails_ReportsTheClean_AndTheCheckoutIsNotMeasured()
    {
        using var fixture = Fixture.Create();
        var (repository, project) = await fixture.BuildingRepositoryAsync();
        var failing = new CleanGate(fixture.RunAsync, failAfterClean: true);
        failing.Open();
        var service = fixture.Service(failing.RunAsync);
        var before = (await ReadAllAsync(service.ScanAsync(new(Epoch), default)))[^1].Done!;
        var shown = service.LastMeasures[repository.Root].Usage.TotalBytes;

        var answer = await CleanAsync(service, project, repository.Root, "bin");

        Assert.AreEqual(("ok", 1000L, "removed"), (answer.Status, answer.BytesFreed, answer.Entries.Single().Outcome));
        Assert.IsNull(answer.Checkout!.Measure);
        Assert.IsNotNull(answer.Checkout.Problem);
        var last = (await service.LastAsync(new(Epoch), default)).Measurement!;
        Assert.AreEqual((null as DiskUsageMeasure, before.TotalBytes - shown, true), (last.Projects.Single().Checkouts.Single().Measure, last.TotalBytes, last.TotalPartial));
        Assert.IsFalse(service.LastMeasures.ContainsKey(repository.Root));
        // Without a measure, a later clean is refused.
        Assert.AreEqual("not_measured", (await CleanAsync(service, project, repository.Root, "obj")).Status);
        Assert.IsNull(service.Cleaning);
    }

    private static int RunTool(string file, params string[] arguments)
    {
        var start = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode;
    }

    private static async Task<DiskUsageCheckout> ShownAsync(DiskUsageService service, ProjectDescriptor project, string checkout)
        => (await service.LastAsync(new(Epoch), default)).Measurement!.Projects.Single(record => record.Id == project.Id).Checkouts.Single(record => record.Path == checkout);

    // Cleans what the page shows now: the measure of the checkout in the last measurement.
    private static async Task<DiskUsageCleanResponse> CleanAsync(DiskUsageService service, ProjectDescriptor project, string checkout, params string[] paths)
    {
        var shown = (await service.LastAsync(new(Epoch), default)).Measurement?.Projects.FirstOrDefault(record => record.Id == project.Id)?.Checkouts
            .FirstOrDefault(record => record.Path == checkout);
        return await service.CleanAsync(new(Epoch, project.Id, checkout, shown?.MeasureId ?? "none", paths));
    }

    // What a measurement leaves to show sums to its total, as AssertTotalsAddUp for the records of a scan.
    private static void AssertMeasurementAddsUp(DiskUsageMeasurement measurement)
    {
        var checkouts = measurement.Projects.SelectMany(static project => project.Checkouts).Where(static checkout => checkout.Measure is not null)
            .Sum(static checkout => checkout.Measure!.TotalBytes);
        var sessions = measurement.Projects.Sum(static project => project.Sessions.Bytes - project.Sessions.BytesInCheckouts);
        Assert.AreEqual(checkouts + sessions, measurement.TotalBytes);
    }

    // What the records show (each checkout, and the sessions of each project that no checkout holds) sums to the total of the end.
    private static void AssertTotalsAddUp(List<DiskUsageScanEvent> events)
    {
        var checkouts = events.Where(static update => update.Kind == "project").SelectMany(static update => update.Project!.Checkouts)
            .Where(static checkout => checkout.Measure is not null).Sum(static checkout => checkout.Measure!.TotalBytes);
        var sessions = events.Where(static update => update.Kind == "project")
            .Sum(static update => update.Project!.Sessions.Bytes - update.Project.Sessions.BytesInCheckouts);
        Assert.AreEqual(checkouts + sessions, events[^1].Done!.TotalBytes);
    }

    private static DateTimeOffset? Later(DateTimeOffset? one, DateTimeOffset other) => one is null || other > one ? other : one;

    private static async Task<List<DiskUsageScanEvent>> ReadAllAsync(IAsyncEnumerable<DiskUsageScanEvent> events)
    {
        var all = new List<DiskUsageScanEvent>();
        await foreach (var update in events) all.Add(update);
        return all;
    }

    // Holds the runs of git that clean until it is opened; can make them fail, or every later read of git.
    private sealed class CleanGate(GitRunner run, bool failAfterClean = false, bool failClean = false)
    {
        private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _cleaned;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Open() => _open.TrySetResult();

        public async Task<GitRun> RunAsync(string folder, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (arguments.Contains("clean"))
            {
                Entered.TrySetResult();
                await _open.Task.WaitAsync(cancellationToken);
                if (failClean) return new(1, string.Empty, "error: could not remove a file");
                var cleaned = await run(folder, arguments, timeout, cancellationToken);
                Volatile.Write(ref _cleaned, 1);
                return cleaned;
            }

            if (failAfterClean && Volatile.Read(ref _cleaned) == 1) return new(128, string.Empty, "fatal: not readable now");
            return await run(folder, arguments, timeout, cancellationToken);
        }
    }

    // Holds the measure of the checkouts (of all, or of those in one folder) until it is opened, and says when one waits
    // and when one was stopped.
    private sealed class Gate(Fixture fixture, string? only = null)
    {
        private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _measures;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Measures => Volatile.Read(ref _measures);

        public void Open() => _open.TrySetResult();

        public async Task<GitRun> RunAsync(string folder, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (arguments is ["status", _, "--ignored=matching", ..])
            {
                Interlocked.Increment(ref _measures);
                if (only is null || GitWorktreeService.IsWithin(folder, only))
                {
                    Entered.TrySetResult();
                    try
                    {
                        await _open.Task.WaitAsync(cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        Canceled.TrySetResult();
                        throw;
                    }
                }
            }

            return await fixture.RunAsync(folder, arguments, timeout, cancellationToken);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly List<AgentSessionMetadata> _sessions = [];
        private readonly Dictionary<string, SessionViewJournalHeader> _headers = new(StringComparer.Ordinal);
        private int _workspaceReads;

        private Fixture(string home)
        {
            Home = home;
            // A state root of its own, as the developer instance has.
            Options = new CatalogOptions
            {
                GlobalRoot = Directory.CreateDirectory(Path.Combine(home, "global")).FullName,
                StateRoot = Path.Combine(home, "global", "dev"),
            };
            Projects = new ProjectCatalog(Options);
            Worktrees = new GitWorktreeService(Options, new CodeAltaConfigStore(Options), RunAsync);
        }

        public static Fixture Create()
        {
            // The real path: git answers with it, and a temporary folder may be reached through a link.
            var home = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "CodeAlta-disk-usage-" + Guid.NewGuid().ToString("N"))).FullName;
            if (Directory.ResolveLinkTarget(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), returnFinalTarget: true) is { } target)
                home = Path.Combine(target.FullName, Path.GetFileName(home));
            return new Fixture(home);
        }

        public string Home { get; }
        public CatalogOptions Options { get; }
        public ProjectCatalog Projects { get; }
        public GitWorktreeService Worktrees { get; }
        public List<SessionWorkFolder> Busy { get; } = [];
        // Sessions at work that the Worktrees window does not count: a background command, a task of the provider.
        public List<SessionWorkFolder> Working { get; } = [];
        public List<UnreadableProjectFile> Unreadable { get; } = [];
        public int WorkspaceReads => Volatile.Read(ref _workspaceReads);

        public IReadOnlyList<AgentSessionMetadata> Sessions => [.. _sessions];

        public DiskUsageService Service(GitRunner? run = null, GitWorktreeService? worktrees = null, IReadOnlyList<string?>? applicationPaths = null,
            Func<string, CancellationToken, Task>? stage = null, long? maximumFileBytes = null, int? checkoutBound = null)
            => new(Options, worktrees ?? Worktrees, () => [.. Busy], () => [.. Busy, .. Working], async token =>
            {
                Interlocked.Increment(ref _workspaceReads);
                return new OwnedWorkspaceSnapshot(await Projects.LoadAsync(token), [.. _sessions])
                {
                    SessionHeaders = new Dictionary<string, SessionViewJournalHeader>(_headers), UnreadableProjects = [.. Unreadable],
                };
            }, Epoch, run ?? RunAsync)
            {
                // Not the test host: no checkout of a test holds it.
                ApplicationPaths = applicationPaths ?? [Path.Combine(Home, "application", "alta")],
                Stage = stage,
                MaximumFileBytes = maximumFileBytes ?? DiskUsageService.MaximumFileBytesDefault,
                CheckoutBound = checkoutBound ?? DiskUsageService.MaximumCheckouts,
            };

        public GitRepository Repository(string name)
        {
            var repository = new GitRepository(this, Directory.CreateDirectory(Path.Combine(Home, name)).FullName);
            try
            {
                repository.Git("init", "-q", "-b", "main");
            }
            catch (Win32Exception)
            {
                Assert.Inconclusive("git is not installed.");
            }

            foreach (var (key, value) in new[] { ("user.name", "Test"), ("user.email", "test@example.invalid"), ("core.autocrlf", "false"), ("commit.gpgsign", "false") })
                repository.Git("config", key, value);
            repository.Write("readme.md", "one");
            repository.Commit("one");
            return repository;
        }

        // A project whose repository ignores the build folders bin (1000 bytes), obj (500 bytes) and dist (none yet).
        public async Task<(GitRepository Repository, ProjectDescriptor Project)> BuildingRepositoryAsync()
        {
            var repository = Repository("building");
            repository.Write(".gitignore", "bin/\nobj/\ndist/\n");
            repository.Commit("ignore");
            repository.Write("bin/app.dll", new string('b', 1000));
            repository.Write("obj/x.o", new string('o', 500));
            return (repository, await Projects.UpsertFromPathAsync(repository.Root));
        }

        // A submodule at a path of the repository, cloned from a repository beside it, and committed.
        public void AddSubmodule(GitRepository repository, string path)
        {
            var source = Repository(Path.GetFileName(repository.Root) + "-" + path.Replace('/', '-') + "-source");
            repository.Git("-c", "protocol.file.allow=always", "submodule", "add", "-q", source.Root.Replace('\\', '/'), path);
            repository.Commit("submodule " + path);
            foreach (var (key, value) in new[] { ("user.name", "Test"), ("user.email", "test@example.invalid"), ("core.autocrlf", "false"), ("commit.gpgsign", "false") })
                new GitRepository(this, repository.At(path)).Git("config", key, value);
        }

        // A remote the repository pushed its branch to: everything committed is on it.
        public void AddRemote(GitRepository repository)
        {
            var bare = Path.Combine(Home, Path.GetFileName(repository.Root) + ".git");
            repository.Git("init", "-q", "--bare", "-b", "main", bare);
            repository.Git("remote", "add", "origin", bare.Replace('\\', '/'));
            repository.Git("push", "-q", "-u", "origin", "main");
        }

        // A session of a project, with its journal (and attachments) in the sessions folder.
        public void Session(string id, ProjectDescriptor project, int bytes, string? worktree = null, int attachments = 0)
        {
            var updated = worktree is null ? Day : Day.AddDays(1);
            _sessions.Add(new(id, updated.AddHours(-1), updated, id, WorkspacePath: project.ProjectPath, ProviderKey: "codex", WorktreePath: worktree));
            _headers[id] = new SessionViewJournalHeader
            {
                SessionId = id, Kind = SessionViewKind.ProjectSession, ProjectRef = project.Id, WorkingDirectory = project.ProjectPath, CreatedAt = updated.AddHours(-1),
            };
            SessionFile($"2026/11/10/{id}.jsonl", bytes);
            if (attachments > 0) SessionFile($"2026/11/10/{id}.attachments/image.png", attachments);
        }

        // A chat: a session of no project.
        public void Chat(string id, int bytes)
        {
            _sessions.Add(new(id, Day.AddHours(-1), Day, id, WorkspacePath: Home, ProviderKey: "codex"));
            _headers[id] = new SessionViewJournalHeader { SessionId = id, Kind = SessionViewKind.GlobalSession, WorkingDirectory = Home, CreatedAt = Day.AddHours(-1) };
            SessionFile($"2026/11/10/{id}.jsonl", bytes);
        }

        public void SessionFile(string relative, int bytes)
        {
            var path = Path.Combine(Options.SessionsRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, new string('s', bytes));
        }

        // What the service runs, with nothing of the machine's or the user's configuration deciding the outcome.
        public async Task<GitRun> RunAsync(string folder, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            using var process = Process.Start(Start(folder, arguments))!;
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return new(process.ExitCode, await output, await error);
        }

        public ProcessStartInfo Start(string folder, IReadOnlyList<string> arguments)
        {
            var start = new ProcessStartInfo("git") { WorkingDirectory = folder, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
            start.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(Home, "no-global-config");
            start.Environment["LC_ALL"] = "C";
            foreach (var inherited in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE" }) start.Environment.Remove(inherited);
            return start;
        }

        public void Dispose()
        {
            try
            {
                // Git marks its objects read-only.
                foreach (var file in Directory.EnumerateFiles(Home, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(Home, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup of a temporary directory.
            }
        }
    }

    private sealed class GitRepository(Fixture fixture, string root)
    {
        public string Root { get; } = root;

        public void Write(string relative, string content)
        {
            var path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public string At(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

        public void Commit(string message)
        {
            Git("add", "-A");
            Git("commit", "-q", "-m", message);
        }

        public string Git(params string[] arguments)
        {
            using var process = Process.Start(fixture.Start(Root, arguments))!;
            var error = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            Assert.AreEqual(0, process.ExitCode, $"git {string.Join(' ', arguments)}: {error.Result}");
            return output;
        }
    }
}
