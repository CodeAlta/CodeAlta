using System.ComponentModel;
using System.Text;
using CodeAlta.Catalog;
using CodeAlta.Desktop.Rpc;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// Real folders with hand-written <c>.git</c> content; the git run and the clock are literals. What git itself
/// prints is covered by <see cref="DesktopProjectGitChangesTests"/>.
/// </summary>
[TestClass]
public sealed class DesktopProjectGitTests
{
    private const string Epoch = "epoch-1";
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    // Three changed files, 10 lines added and 2 removed, as `git diff --raw --numstat -z` prints them.
    private const string Diff = ":100644 100644 " + Commit + " " + Zero + " M\0a.cs\0:100644 100644 " + Commit + " " + Zero + " M\0b.cs\0"
        + ":000000 100644 " + Zero + " " + Zero + " A\0c.cs\0" + "7\t2\ta.cs\0" + "3\t0\tb.cs\0" + "0\t0\tc.cs\0";
    private const string Zero = "0000000000000000000000000000000000000000";

    [TestMethod]
    public async Task Status_ReadsTheBranchFromHeadAndTheCountsFromGit()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.WriteHead(Path.Combine(fixture.ProjectPath, ".git"), "ref: refs/heads/feature/branch-indicator\n");

        var response = await fixture.Service.StatusAsync(new(Epoch, fixture.ProjectId), default);

        Assert.AreEqual(new ProjectGitStatusResponse("ok", fixture.ProjectId, "feature/branch-indicator", false, 10, 2, 3), response);
        Assert.AreEqual(Path.GetFullPath(fixture.ProjectPath), fixture.Runs.Single());
    }

    [TestMethod]
    public async Task Status_ReportsADetachedCommitByItsFirstSevenDigits()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.WriteHead(Path.Combine(fixture.ProjectPath, ".git"), Commit + "\n");

        var response = await fixture.Service.StatusAsync(new(Epoch, fixture.ProjectId), default);

        Assert.AreEqual(new ProjectGitStatusResponse("ok", fixture.ProjectId, "0123456", true, 10, 2, 3), response);
    }

    [TestMethod]
    public async Task Status_FollowsAGitFileToTheDirectoryItNames()
    {
        // A linked worktree: its .git is a file naming a folder of the main repository.
        using var fixture = await Fixture.CreateAsync();
        var worktree = Path.Combine(fixture.Root, "main", ".git", "worktrees", "linked");
        fixture.WriteHead(worktree, "ref: refs/heads/linked\n");
        fixture.WriteHead(Path.Combine(fixture.Root, "main", ".git"), "ref: refs/heads/main\n");
        File.WriteAllText(Path.Combine(fixture.ProjectPath, ".git"), "gitdir: " + worktree.Replace('\\', '/') + "\n");
        Assert.AreEqual("linked", (await fixture.Service.StatusAsync(new(Epoch, fixture.ProjectId), default)).Branch);

        // A submodule names its directory relative to its own folder.
        var submodule = await fixture.AddProjectAsync("relative");
        fixture.WriteHead(Path.Combine(fixture.Root, "modules", "relative"), Commit + "\n");
        File.WriteAllText(Path.Combine(submodule.ProjectPath, ".git"), "gitdir: ../modules/relative\r\n");
        var detached = await fixture.Service.StatusAsync(new(Epoch, submodule.Id), default);
        Assert.AreEqual("0123456", detached.Branch);
        Assert.IsTrue(detached.Detached);
    }

    [TestMethod]
    public async Task Status_FindsTheRepositoryAboveANestedProjectFolder()
    {
        using var fixture = await Fixture.CreateAsync();
        var repository = Directory.CreateDirectory(Path.Combine(fixture.Root, "repository")).FullName;
        fixture.WriteHead(Path.Combine(repository, ".git"), "ref: refs/heads/main\n");
        var nested = await fixture.AddProjectAsync(Path.Combine("repository", "packages", "app"));

        var response = await fixture.Service.StatusAsync(new(Epoch, nested.Id), default);

        Assert.AreEqual(new ProjectGitStatusResponse("ok", nested.Id, "main", false, 10, 2, 3), response);
        Assert.AreEqual(repository, fixture.Runs.Single(), "Git runs in the work tree: the changes are those of the whole repository.");
    }

    [TestMethod]
    public async Task Status_ReportsNoRepositoryWithoutRunningGit()
    {
        using var fixture = await Fixture.CreateAsync();
        var none = new ProjectGitStatusResponse("not_repository", null, null, false, null, null, null);

        // Not a git directory: no HEAD in it.
        Directory.CreateDirectory(Path.Combine(fixture.ProjectPath, ".git"));
        Assert.AreEqual(none, await fixture.Service.StatusAsync(new(Epoch, fixture.ProjectId), default));

        // Neither a pointer to a git directory, nor one to a directory that exists.
        var garbage = await fixture.AddProjectAsync("garbage");
        File.WriteAllText(Path.Combine(garbage.ProjectPath, ".git"), "not a pointer\n");
        Assert.AreEqual(none, await fixture.Service.StatusAsync(new(Epoch, garbage.Id), default));
        var dangling = await fixture.AddProjectAsync("dangling");
        File.WriteAllText(Path.Combine(dangling.ProjectPath, ".git"), "gitdir: ../gone\n");
        Assert.AreEqual(none, await fixture.Service.StatusAsync(new(Epoch, dangling.Id), default));

        // The temporary folder itself may be inside a repository (a versioned home folder): only then is one found.
        var plain = await fixture.AddProjectAsync("plain");
        if (ProjectGitService.FindGitDirectory(fixture.Root) is null)
            Assert.AreEqual(none, await fixture.Service.StatusAsync(new(Epoch, plain.Id), default));

        Assert.IsEmpty(fixture.Runs);
    }

    [TestMethod]
    public async Task Status_RefusesAHeadItCannotRead()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.WriteHead(Path.Combine(fixture.ProjectPath, ".git"), "something else\n");

        var response = await fixture.Service.StatusAsync(new(Epoch, fixture.ProjectId), default);

        Assert.AreEqual(new ProjectGitStatusResponse("read_failed", null, null, false, null, null, null), response);
        Assert.IsEmpty(fixture.Runs);
    }

    [TestMethod]
    [DataRow("ref: refs/heads/main\n", "main", false)]
    [DataRow("ref: refs/heads/main", "main", false)]
    [DataRow("ref:refs/heads/topic/a.b-c\r\n", "topic/a.b-c", false)]
    [DataRow("ref: refs/remotes/origin/main\n", "refs/remotes/origin/main", false)]
    [DataRow(Commit + "\n", "0123456", true)]
    [DataRow("ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef0123456789\n", "ABCDEF0", true)]
    public void TryParseHead_ReadsABranchOrACommit(string head, string branch, bool detached)
    {
        Assert.IsTrue(ProjectGitService.TryParseHead(head, out var actual, out var actualDetached));
        Assert.AreEqual(branch, actual);
        Assert.AreEqual(detached, actualDetached);
    }

    [TestMethod]
    public void TryParseHead_RefusesAnythingElse()
    {
        var refused = new[]
        {
            "", "\n", "ref:\n", "ref: refs/heads/\n", "0123456\n", Commit + "0\n", Commit[..39] + "g\n", "ref: refs/heads/tab\there\n",
            "ref: refs/heads/" + new string('a', ProjectGitService.MaximumBranchLength + 1) + "\n",
        };
        foreach (var head in refused)
        {
            Assert.IsFalse(ProjectGitService.TryParseHead(head, out var branch, out var detached), head);
            Assert.IsNull(branch, head);
            Assert.IsFalse(detached, head);
        }

        Assert.IsTrue(ProjectGitService.TryParseHead("ref: refs/heads/" + new string('a', ProjectGitService.MaximumBranchLength), out _, out _));
    }

    [TestMethod]
    public async Task Status_WithoutAnyDifference_ReportsZeroCounts()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.WriteHead(Path.Combine(fixture.ProjectPath, ".git"), "ref: refs/heads/main\n");
        fixture.Output = string.Empty;

        var response = await fixture.Service.StatusAsync(new(Epoch, fixture.ProjectId), default);

        Assert.AreEqual(new ProjectGitStatusResponse("ok", fixture.ProjectId, "main", false, 0, 0, 0), response);
    }

    [TestMethod]
    public async Task Status_KeepsTheBranchWhenGitCannotAnswer()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.WriteHead(Path.Combine(fixture.ProjectPath, ".git"), "ref: refs/heads/main\n");
        var branchOnly = new ProjectGitStatusResponse("ok", fixture.ProjectId, "main", false, null, null, null);

        // Not installed, or an error exit such as a repository without a commit.
        Assert.AreEqual(branchOnly, await fixture.ServiceOver((_, _) => Task.FromResult<string?>(null)).StatusAsync(new(Epoch, fixture.ProjectId), default));
        Assert.AreEqual(branchOnly, await fixture.ServiceOver((_, _) => throw new Win32Exception(2)).StatusAsync(new(Epoch, fixture.ProjectId), default));

        // Too slow: a run that stops when asked, and one that never returns.
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observing = fixture.ServiceOver(async (_, token) =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { stopped.SetResult(); }
            return Diff;
        }, TimeSpan.FromMilliseconds(50));
        Assert.AreEqual(branchOnly, await observing.StatusAsync(new(Epoch, fixture.ProjectId), default));
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(30)); // The run is canceled at the timeout.
        var never = new TaskCompletionSource<string?>();
        Assert.AreEqual(branchOnly, await fixture.ServiceOver((_, _) => never.Task, TimeSpan.FromMilliseconds(50)).StatusAsync(new(Epoch, fixture.ProjectId), default));
    }

    [TestMethod]
    public async Task Status_CancelsWithTheRequest()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.WriteHead(Path.Combine(fixture.ProjectPath, ".git"), "ref: refs/heads/main\n");
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = 0;
        var service = fixture.ServiceOver(async (_, token) =>
        {
            if (Interlocked.Increment(ref runs) > 1) return Diff;
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Diff;
        }, TimeSpan.FromMinutes(1));

        var pending = service.StatusAsync(new(Epoch, fixture.ProjectId), cancellation.Token);
        await started.Task;
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        // Nothing was cached and the service is free again.
        Assert.AreEqual(10, (await service.StatusAsync(new(Epoch, fixture.ProjectId), default)).Insertions);
        Assert.AreEqual(2, runs);
    }

    [TestMethod]
    public async Task Status_ReusesAListForASecond_AndTheBranchIsAlwaysRead()
    {
        using var fixture = await Fixture.CreateAsync();
        var head = fixture.WriteHead(Path.Combine(fixture.ProjectPath, ".git"), "ref: refs/heads/main\n");
        var first = await fixture.Service.StatusAsync(new(Epoch, fixture.ProjectId), default);

        // Within the window git is not run again; HEAD is a file and is read each time.
        File.WriteAllText(head, "ref: refs/heads/other\n");
        fixture.Output = string.Empty;
        fixture.Now += ProjectGitService.MinimumCacheLifetime - TimeSpan.FromMilliseconds(1);
        Assert.AreEqual(first with { Branch = "other" }, await fixture.Service.StatusAsync(new(Epoch, fixture.ProjectId), default));
        Assert.HasCount(1, fixture.Runs);

        // Another repository has its own entry.
        var other = await fixture.AddProjectAsync("other");
        fixture.WriteHead(Path.Combine(other.ProjectPath, ".git"), "ref: refs/heads/second\n");
        Assert.AreEqual("second", (await fixture.Service.StatusAsync(new(Epoch, other.Id), default)).Branch);
        Assert.HasCount(2, fixture.Runs);

        fixture.Now += TimeSpan.FromMilliseconds(1);
        Assert.AreEqual(new ProjectGitStatusResponse("ok", fixture.ProjectId, "other", false, 0, 0, 0), await fixture.Service.StatusAsync(new(Epoch, fixture.ProjectId), default));
        Assert.HasCount(3, fixture.Runs);

        // A clock set back does not keep an entry alive.
        fixture.Now -= TimeSpan.FromHours(1);
        await fixture.Service.StatusAsync(new(Epoch, fixture.ProjectId), default);
        Assert.HasCount(4, fixture.Runs);
    }

    [TestMethod]
    public async Task Status_ReusesTheListOfASlowRepositoryForLonger()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.WriteHead(Path.Combine(fixture.ProjectPath, ".git"), "ref: refs/heads/main\n");
        var runs = 0;
        // Git takes three seconds: its list is kept four times as long.
        var service = fixture.ServiceOver((_, _) => { runs++; fixture.Now += TimeSpan.FromSeconds(3); return Task.FromResult<string?>(Diff); });

        await service.StatusAsync(new(Epoch, fixture.ProjectId), default);
        fixture.Now += TimeSpan.FromSeconds(11.9);
        await service.StatusAsync(new(Epoch, fixture.ProjectId), default);
        Assert.AreEqual(1, runs);
        fixture.Now += TimeSpan.FromSeconds(0.2);
        await service.StatusAsync(new(Epoch, fixture.ProjectId), default);
        Assert.AreEqual(2, runs);

        // However slow, never longer than the maximum; a failed run is tried again after five seconds.
        var slow = fixture.ServiceOver((_, _) => { runs++; fixture.Now += TimeSpan.FromMinutes(1); return Task.FromResult<string?>(Diff); }, TimeSpan.FromHours(1));
        await slow.StatusAsync(new(Epoch, fixture.ProjectId), default);
        fixture.Now += ProjectGitService.MaximumCacheLifetime;
        await slow.StatusAsync(new(Epoch, fixture.ProjectId), default);
        Assert.AreEqual(4, runs);
        runs = 0;
        var failing = fixture.ServiceOver((_, _) => { runs++; return Task.FromResult<string?>(null); });
        await failing.StatusAsync(new(Epoch, fixture.ProjectId), default);
        fixture.Now += ProjectGitService.FailureCacheLifetime - TimeSpan.FromMilliseconds(1);
        await failing.StatusAsync(new(Epoch, fixture.ProjectId), default);
        Assert.AreEqual(1, runs, "Git is not asked again while the failure is kept.");
    }

    [TestMethod]
    public async Task Changes_RefuseWithoutReadingAnything_AndFilesOutsideTheListAreNotRead()
    {
        Assert.AreEqual("unavailable", (await new ProjectGitService().ChangesAsync(new ProjectGitChangesRequest(Epoch, "project", null, null), default)).Status);
        Assert.AreEqual("unavailable", (await new ProjectGitService().FileAsync(new(Epoch, "project", null, "a.cs"), default)).Status);
        await foreach (var _ in new ProjectGitService().WatchAsync(new(Epoch), default)) Assert.Fail("A service without a host has nothing to show.");

        using var fixture = await Fixture.CreateAsync();
        fixture.WriteHead(Path.Combine(fixture.ProjectPath, ".git"), "ref: refs/heads/main\n");
        Assert.AreEqual("stale_epoch", (await fixture.Service.ChangesAsync(new ProjectGitChangesRequest("another", fixture.ProjectId, null, null), default)).Status);
        Assert.AreEqual("invalid", (await fixture.Service.ChangesAsync(new ProjectGitChangesRequest(Epoch, fixture.ProjectId, "index", null), default)).Status);
        Assert.AreEqual("stale_epoch", (await fixture.Service.FileAsync(new("another", fixture.ProjectId, null, "a.cs"), default)).Status);
        Assert.AreEqual("invalid", (await fixture.Service.FileAsync(new(Epoch, fixture.ProjectId, null, null), default)).Status);
        Assert.AreEqual("invalid", (await fixture.Service.FileAsync(new(Epoch, fixture.ProjectId, null, new string('a', 1025)), default)).Status);
        Assert.IsEmpty(fixture.Runs);

        // Only a path of the list is read: nothing else in the work tree, and nothing outside it.
        File.WriteAllText(Path.Combine(fixture.ProjectPath, "secret.txt"), "secret");
        foreach (var path in new[] { "secret.txt", "../project/secret.txt", Path.Combine(fixture.ProjectPath, "secret.txt"), "A.CS", ".git/HEAD" })
            Assert.AreEqual(ProjectGitFileResponse.Refused("not_changed"), await fixture.Service.FileAsync(new(Epoch, fixture.ProjectId, null, path), default), path);

        // Git could not list anything: the branch is still known.
        fixture.Output = null;
        fixture.Now += TimeSpan.FromMinutes(1);
        var failed = await fixture.Service.ChangesAsync(new ProjectGitChangesRequest(Epoch, fixture.ProjectId, null, null), default);
        Assert.AreEqual("git_failed", failed.Status);
        Assert.AreEqual("main", failed.Branch);
        Assert.IsNull(failed.Files);
        Assert.AreEqual("git_failed", (await fixture.Service.FileAsync(new(Epoch, fixture.ProjectId, null, "a.cs"), default)).Status);
    }

    [TestMethod]
    public void ParseDiff_ReadsStatusesRenamesAndCounts()
    {
        const string blob = "89abcdef0123456789abcdef0123456789abcdef";
        var output = Encoding.UTF8.GetBytes(
            ":100644 100644 " + Commit + " " + Zero + " M\0src/a b.cs\0"
            + ":100644 000000 " + blob + " " + Zero + " D\0gone.txt\0"
            + ":100644 100644 " + blob + " " + blob + " R086\0old name.txt\0new/näme.txt\0"
            + ":000000 100644 " + Zero + " " + blob + " A\0:odd\0"
            + ":100644 100644 " + blob + " " + Zero + " M\0image.png\0"
            + ":160000 160000 " + blob + " " + Commit + " M\0module\0"
            + ":000000 000000 " + Zero + " " + Zero + " U\0conflict.cs\0"
            + ":100644 100755 " + blob + " " + blob + " T\0link\0"
            + "12\t3\tsrc/a b.cs\0" + "0\t9\tgone.txt\0" + "1\t2\t\0old name.txt\0new/näme.txt\0" + "4\t0\t:odd\0" + "-\t-\timage.png\0"
            + "1\t1\tmodule\0" + "0\t0\tlink\0");

        var files = ProjectGitChanges.ParseDiff(output);

        CollectionAssert.AreEqual(new[]
        {
            $"src/a b.cs||modified|12|3|False|{Commit}",
            $"gone.txt||deleted|0|9|False|{blob}",
            $"new/näme.txt|old name.txt|renamed|1|2|False|{blob}",
            ":odd||added|4|0|False|",
            $"image.png||modified|||True|{blob}",
            "module||modified|1|1|True|",
            "conflict.cs||conflicted|||False|",
            $"link||modified|0|0|False|{blob}",
        }, files.Select(static file => $"{file.Path}|{file.OriginalPath}|{file.Status}|{file.Insertions}|{file.Deletions}|{file.Binary}|{file.OriginalBlob}").ToArray());

        // A cut output loses its last record, never a half one; paths that leave the work tree are not listed.
        Assert.HasCount(1, ProjectGitChanges.ParseDiff(output.AsSpan(0, 150)));
        Assert.IsEmpty(ProjectGitChanges.ParseDiff("garbage"u8));
        Assert.IsEmpty(ProjectGitChanges.ParseDiff(Encoding.UTF8.GetBytes(":100644 100644 " + blob + " " + Zero + " M\0../outside.txt\0")));
        CollectionAssert.AreEqual(new[] { "a.txt", "dir/b c.txt" }, ProjectGitChanges.ParseUntracked("a.txt\0dir/b c.txt\0/rooted\0x/../y\0cut"u8));
    }

    [TestMethod]
    public void CountLinesAndDecodeText_TreatContentLikeGit()
    {
        Assert.AreEqual(0, ProjectGitChanges.CountLines(""u8));
        Assert.AreEqual(2, ProjectGitChanges.CountLines("a\nb\n"u8));
        Assert.AreEqual(2, ProjectGitChanges.CountLines("a\r\nb"u8), "A last line without its end counts.");
        Assert.IsNull(ProjectGitChanges.CountLines("a\0b\n"u8), "A NUL makes a content binary.");

        Assert.AreEqual("é\n", ProjectGitChanges.DecodeText([0xEF, 0xBB, 0xBF, 0xC3, 0xA9, 0x0A]));
        Assert.AreEqual("hi", ProjectGitChanges.DecodeText([0xFF, 0xFE, (byte)'h', 0, (byte)'i', 0]));
        Assert.AreEqual("hi", ProjectGitChanges.DecodeText([0xFE, 0xFF, 0, (byte)'h', 0, (byte)'i']));
        Assert.IsNull(ProjectGitChanges.DecodeText([1, 0, 2]));
        Assert.AreEqual("", ProjectGitChanges.DecodeText([]));
        Assert.IsTrue(ProjectGitChanges.IsObjectId(Commit));
        Assert.IsFalse(ProjectGitChanges.IsObjectId(Commit[..39] + "G"));
        Assert.AreEqual(16, ProjectGitChanges.Hash("a").Length);
        Assert.AreNotEqual(ProjectGitChanges.Hash("a"), ProjectGitChanges.Hash("b"));
    }

    [TestMethod]
    public async Task Status_RunsGitOnceForConcurrentRequests()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.WriteHead(Path.Combine(fixture.ProjectPath, ".git"), "ref: refs/heads/main\n");
        var other = await fixture.AddProjectAsync("other");
        fixture.WriteHead(Path.Combine(other.ProjectPath, ".git"), "ref: refs/heads/second\n");
        var release = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = 0;
        var most = 0;
        var runs = 0;
        var service = fixture.ServiceOver(async (_, _) =>
        {
            Interlocked.Increment(ref runs);
            most = Math.Max(most, Interlocked.Increment(ref running));
            var output = await release.Task;
            Interlocked.Decrement(ref running);
            return output;
        }, TimeSpan.FromMinutes(1));

        var requests = new[]
        {
            service.StatusAsync(new(Epoch, fixture.ProjectId), default), service.StatusAsync(new(Epoch, fixture.ProjectId), default),
            service.StatusAsync(new(Epoch, other.Id), default), service.StatusAsync(new(Epoch, fixture.ProjectId), default),
        };
        release.SetResult(Diff);
        var responses = await Task.WhenAll(requests);

        Assert.IsTrue(responses.All(static response => response is { Status: "ok", Insertions: 10 }));
        CollectionAssert.AreEqual(new[] { "main", "main", "second", "main" }, responses.Select(static response => response.Branch).ToArray());
        Assert.AreEqual(2, runs, "One run for each folder.");
        Assert.AreEqual(1, most, "Never two lists read at a time.");
    }

    [TestMethod]
    public async Task Status_RefusesWithoutReadingAnything()
    {
        var none = new ProjectGitStatusResponse("unavailable", null, null, false, null, null, null);
        Assert.AreEqual(none, await new ProjectGitService().StatusAsync(new(Epoch, "project"), default));

        using var fixture = await Fixture.CreateAsync();
        fixture.WriteHead(Path.Combine(fixture.ProjectPath, ".git"), "ref: refs/heads/main\n");
        Assert.AreEqual(none with { Status = "stale_epoch" }, await fixture.Service.StatusAsync(new("another", fixture.ProjectId), default));
        Assert.AreEqual(none with { Status = "stale_epoch" }, await fixture.Service.StatusAsync(new(null, fixture.ProjectId), default));
        Assert.AreEqual(none with { Status = "invalid" }, await fixture.Service.StatusAsync(new(Epoch, null), default));
        Assert.AreEqual(none with { Status = "unknown_project" }, await fixture.Service.StatusAsync(new(Epoch, Guid.NewGuid().ToString("D")), default));
        Assert.AreEqual(none with { Status = "unknown_project" }, await fixture.Service.StatusAsync(new(Epoch, fixture.ProjectPath), default), "A path is not a project id.");
        var gone = await fixture.AddProjectAsync("gone");
        Directory.Delete(gone.ProjectPath, recursive: true);
        Assert.AreEqual(none with { Status = "project_unavailable" }, await fixture.Service.StatusAsync(new(Epoch, gone.Id), default));
        Assert.IsEmpty(fixture.Runs);

        // Read-only information: an archived project is answered.
        fixture.Project.Archived = true;
        await fixture.Projects.SaveAsync(fixture.Project);
        Assert.AreEqual(new ProjectGitStatusResponse("ok", fixture.ProjectId, "main", false, 10, 2, 3), await fixture.Service.StatusAsync(new(Epoch, fixture.ProjectId), default));
    }

    [TestMethod]
    public async Task RunGit_ReturnsNothingForAFolderGitRefuses()
    {
        // Passes with or without git installed: a malformed .git file makes git exit with an error.
        using var fixture = await Fixture.CreateAsync();
        File.WriteAllText(Path.Combine(fixture.ProjectPath, ".git"), "not a pointer\n");

        Assert.IsNull(await ProjectGitService.RunGitAsync(Path.GetFullPath(fixture.ProjectPath), ProjectGitChanges.DiffArguments("HEAD"), 1024, default));
    }

    private sealed class Fixture : IDisposable
    {
        private Fixture(string root, ProjectCatalog projects, ProjectDescriptor project)
        {
            Root = root;
            Projects = projects;
            Project = project;
            Service = ServiceOver((folder, _) =>
            {
                Runs.Add(folder);
                return Task.FromResult<string?>(Output);
            });
        }

        public static async Task<Fixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "CodeAlta-project-git-" + Guid.NewGuid().ToString("N"));
            var projects = new ProjectCatalog(new CatalogOptions { GlobalRoot = Directory.CreateDirectory(Path.Combine(root, "global")).FullName });
            var project = await projects.UpsertFromPathAsync(Directory.CreateDirectory(Path.Combine(root, "project")).FullName);
            return new Fixture(root, projects, project);
        }

        public string Root { get; }
        public ProjectCatalog Projects { get; }
        public ProjectDescriptor Project { get; }
        public ProjectGitService Service { get; }
        public List<string> Runs { get; } = [];
        /// <summary>What the comparison prints; empty for no difference, null when git fails.</summary>
        public string? Output { get; set; } = Diff;
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        public string ProjectId => Project.Id;
        public string ProjectPath => Project.ProjectPath;

        /// <summary>A service whose comparison with HEAD is <paramref name="run"/>; nothing is untracked and no other git command answers.</summary>
        public ProjectGitService ServiceOver(Func<string, CancellationToken, Task<string?>> run, TimeSpan? timeout = null)
            => new(Projects, Epoch, async (folder, arguments, _, token) => arguments[0] switch
            {
                "diff" when arguments.Contains("HEAD") => await run(folder, token) is { } output ? new GitOutput(Encoding.UTF8.GetBytes(output), false) : null,
                "ls-files" => new GitOutput([], false),
                _ => null,
            }, () => Now, timeout ?? ProjectGitService.GitTimeout);

        public Task<ProjectDescriptor> AddProjectAsync(string relative)
            => Projects.UpsertFromPathAsync(Directory.CreateDirectory(Path.Combine(Root, relative)).FullName);

        public string WriteHead(string gitDirectory, string head)
        {
            var path = Path.Combine(Directory.CreateDirectory(gitDirectory).FullName, "HEAD");
            File.WriteAllText(path, head);
            return path;
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { /* Best-effort cleanup of a temporary directory. */ }
        }
    }
}
