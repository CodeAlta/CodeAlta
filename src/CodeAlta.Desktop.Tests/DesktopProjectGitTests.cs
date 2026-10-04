using System.ComponentModel;
using CodeAlta.Catalog;
using CodeAlta.Desktop.Rpc;

namespace CodeAlta.Desktop.Tests;

/// <summary>Real folders with hand-written <c>.git</c> content; the git run and the clock are literals.</summary>
[TestClass]
public sealed class DesktopProjectGitTests
{
    private const string Epoch = "epoch-1";
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    private const string ShortStat = " 3 files changed, 10 insertions(+), 2 deletions(-)\n";

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
        Assert.AreEqual(Path.GetFullPath(nested.ProjectPath), fixture.Runs.Single(), "Git runs in the project folder.");
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
    [DataRow(ShortStat, 3, 10, 2)]
    [DataRow(" 1 file changed, 1 insertion(+), 1 deletion(-)\n", 1, 1, 1)]
    [DataRow(" 2 files changed, 7 insertions(+)\n", 2, 7, 0)]
    [DataRow(" 1 file changed, 4 deletions(-)\r\n", 1, 0, 4)]
    [DataRow(" 1 file changed, 0 insertions(+), 0 deletions(-)\n", 1, 0, 0)]
    [DataRow("", 0, 0, 0)]
    [DataRow("\n", 0, 0, 0)]
    public void TryParseShortStat_ReadsThePartsGitPrints(string output, int changedFiles, int insertions, int deletions)
    {
        Assert.IsTrue(ProjectGitService.TryParseShortStat(output, out var actualFiles, out var actualInsertions, out var actualDeletions));
        Assert.AreEqual((changedFiles, insertions, deletions), (actualFiles, actualInsertions, actualDeletions));
    }

    [TestMethod]
    [DataRow("fatal: not a git repository")]
    [DataRow("3 insertions(+), 2 deletions(-)")]
    [DataRow(" 3 files changed, many insertions(+)")]
    [DataRow(" 3 files changed, 99999999999 insertions(+)")]
    [DataRow(" 3 files changed, -1 insertions(+)")]
    [DataRow(" 1 file changed, 1 insertion(+)\n 1 file changed, 1 deletion(-)")]
    public void TryParseShortStat_RefusesOtherOutput(string output)
        => Assert.IsFalse(ProjectGitService.TryParseShortStat(output, out _, out _, out _));

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
        Assert.AreEqual(branchOnly, await fixture.ServiceOver((_, _) => Task.FromResult<string?>("fatal: bad revision 'HEAD'")).StatusAsync(new(Epoch, fixture.ProjectId), default));

        // Too slow: a run that stops when asked, and one that never returns.
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observing = fixture.ServiceOver(async (_, token) =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { stopped.SetResult(); }
            return ShortStat;
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
            if (Interlocked.Increment(ref runs) > 1) return ShortStat;
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return ShortStat;
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
    public async Task Status_ReusesAResponseForFiveSeconds()
    {
        using var fixture = await Fixture.CreateAsync();
        var head = fixture.WriteHead(Path.Combine(fixture.ProjectPath, ".git"), "ref: refs/heads/main\n");
        var first = await fixture.Service.StatusAsync(new(Epoch, fixture.ProjectId), default);

        // Within the window neither HEAD nor git is read again.
        File.WriteAllText(head, "ref: refs/heads/other\n");
        fixture.Output = string.Empty;
        fixture.Now += ProjectGitService.CacheLifetime - TimeSpan.FromMilliseconds(1);
        Assert.AreEqual(first, await fixture.Service.StatusAsync(new(Epoch, fixture.ProjectId), default));
        Assert.HasCount(1, fixture.Runs);

        // Another folder has its own entry.
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
        release.SetResult(ShortStat);
        var responses = await Task.WhenAll(requests);

        Assert.IsTrue(responses.All(static response => response is { Status: "ok", Insertions: 10 }));
        CollectionAssert.AreEqual(new[] { "main", "main", "second", "main" }, responses.Select(static response => response.Branch).ToArray());
        Assert.AreEqual(2, runs, "One run for each folder.");
        Assert.AreEqual(1, most, "Never two git processes at a time.");
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

        Assert.IsNull(await ProjectGitService.RunGitAsync(Path.GetFullPath(fixture.ProjectPath), default));
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
        public string? Output { get; set; } = ShortStat;
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        public string ProjectId => Project.Id;
        public string ProjectPath => Project.ProjectPath;

        public ProjectGitService ServiceOver(Func<string, CancellationToken, Task<string?>> run, TimeSpan? timeout = null)
            => new(Projects, Epoch, run, () => Now, timeout ?? ProjectGitService.GitTimeout);

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
