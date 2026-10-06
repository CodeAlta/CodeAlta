using System.ComponentModel;
using System.Diagnostics;
using CodeAlta.Catalog;
using CodeAlta.Desktop.Rpc;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// The changes of real repositories, made with the git on the path: what the list, the totals and the two
/// contents of a file are for each kind of change. Inconclusive on a machine without git.
/// </summary>
[TestClass]
public sealed class DesktopProjectGitChangesTests
{
    private const string Epoch = "epoch-1";

    [TestMethod]
    public async Task Changes_ListEveryKindOfChangeWithItsLines_AndTheStatusHasTheirSum()
    {
        using var repository = await Repository.CreateAsync();
        repository.Write("a.txt", "one\ntwo\nthree\n");
        repository.Write("gone.txt", "1\n2\n3\n4\n");
        repository.Write("old.txt", "kept\nas\nit\nis\n");
        repository.Write("image.bin", [1, 0, 2, 0, 3]);
        repository.Write(".gitignore", "*.log\n");
        repository.Commit("first");

        repository.Write("a.txt", "one\nTWO\nthree\nfour\n");
        File.Delete(repository.At("gone.txt"));
        repository.Git("mv", "old.txt", "new.txt");
        repository.Write("staged.txt", "s1\ns2\n");
        repository.Git("add", "staged.txt");
        repository.Write("sub/dir/untracked.txt", "u1\nu2\nu3");
        repository.Write("sub/data.bin", [0, 1, 2]);
        repository.Write("image.bin", [9, 0, 9]);
        repository.Write("ignored.log", "never listed\n");

        var changes = await repository.Service.ChangesAsync(new ProjectGitChangesRequest(Epoch, repository.ProjectId, null, null), default);

        Assert.AreEqual("ok", changes.Status);
        CollectionAssert.AreEqual(new[]
        {
            "a.txt|modified|2|1|False|",
            "gone.txt|deleted|0|4|False|",
            "image.bin|modified|||True|",
            "new.txt|renamed|0|0|False|old.txt",
            "staged.txt|added|2|0|False|",
            "sub/data.bin|untracked|||True|",
            "sub/dir/untracked.txt|untracked|3|0|False|",
        }, changes.Files!.Select(static file => $"{file.Path}|{file.Status}|{file.Insertions}|{file.Deletions}|{file.Binary}|{file.OriginalPath}").ToArray());
        Assert.AreEqual((7, 5, false), (changes.Insertions, changes.Deletions, changes.Truncated));
        Assert.AreEqual((repository.Root, "", "main", false, "head"), (changes.Root, changes.Prefix, changes.Branch, changes.Detached, changes.Comparison));
        Assert.IsNull(changes.BaseReference, "No other branch to be based on.");
        Assert.AreEqual(new ProjectGitStatusResponse("ok", repository.ProjectId, "main", false, 7, 5, 7), await repository.Service.StatusAsync(new(Epoch, repository.ProjectId), default));

        // The same list again is answered with nothing but its revision; any change makes another one.
        var again = await repository.Service.ChangesAsync(new ProjectGitChangesRequest(Epoch, repository.ProjectId, null, changes.Revision), default);
        Assert.AreEqual(ProjectGitChangesResponse.Refused("unchanged") with { ProjectId = repository.ProjectId, Revision = changes.Revision }, again);
        repository.Write("a.txt", "one\nTWO\nthree\nFOUR\n");
        File.SetLastWriteTimeUtc(repository.At("a.txt"), DateTime.UtcNow.AddMinutes(5));
        repository.Now += TimeSpan.FromMinutes(1);
        var next = await repository.Service.ChangesAsync(new ProjectGitChangesRequest(Epoch, repository.ProjectId, null, changes.Revision), default);
        Assert.AreEqual("ok", next.Status);
        Assert.AreNotEqual(changes.Revision, next.Revision);
        // The lines are the same number: only the revision of that file says that its content is another.
        var before = changes.Files!.ToDictionary(static file => file.Path, static file => file.Revision);
        CollectionAssert.AreEqual(new[] { "a.txt" }, next.Files!.Where(file => before[file.Path] != file.Revision).Select(static file => file.Path).ToArray());
    }

    [TestMethod]
    public async Task File_ReturnsTheContentOfTheCommitAndOfTheWorkTree()
    {
        using var repository = await Repository.CreateAsync();
        repository.Write("a.txt", "one\ntwo\n");
        repository.Write("gone.txt", "bye\n");
        repository.Write("old.txt", "moved\ncontent\n");
        repository.Write("image.bin", [1, 0, 2]);
        repository.Write("big.txt", "small\n");
        repository.Commit("first");
        repository.Write("a.txt", "\uFEFFone\r\nTWO\r\n");
        File.Delete(repository.At("gone.txt"));
        repository.Git("mv", "old.txt", "new.txt");
        repository.Write("fresh.txt", "new file\n");
        repository.Write("image.bin", [3, 0, 4]);
        repository.Write("big.txt", new string('x', ProjectGitService.MaximumFileBytes + 1));

        Task<ProjectGitFileResponse> Read(string path) => repository.Service.FileAsync(new(Epoch, repository.ProjectId, null, path), default);
        var list = (await repository.Service.ChangesAsync(new ProjectGitChangesRequest(Epoch, repository.ProjectId, null, null), default)).Files!;

        var modified = await Read("a.txt");
        Assert.AreEqual(("ok", "one\ntwo\n", "text", "one\r\nTWO\r\n", "text"), (modified.Status, modified.Original, modified.OriginalState, modified.Modified, modified.ModifiedState));
        Assert.AreEqual(list.Single(static file => file.Path == "a.txt").Revision, modified.Revision, "The revision of the contents is the one of the list.");
        var deleted = await Read("gone.txt");
        Assert.AreEqual(("bye\n", "text", null, "absent"), (deleted.Original, deleted.OriginalState, deleted.Modified, deleted.ModifiedState));
        var renamed = await Read("new.txt");
        Assert.AreEqual(("moved\ncontent\n", "text", "moved\ncontent\n", "text"), (renamed.Original, renamed.OriginalState, renamed.Modified, renamed.ModifiedState));
        var untracked = await Read("fresh.txt");
        Assert.AreEqual((null, "absent", "new file\n", "text"), (untracked.Original, untracked.OriginalState, untracked.Modified, untracked.ModifiedState));
        var binary = await Read("image.bin");
        Assert.AreEqual((null, "binary", null, "binary"), (binary.Original, binary.OriginalState, binary.Modified, binary.ModifiedState));
        var large = await Read("big.txt");
        Assert.AreEqual(("small\n", "text", null, "too_large"), (large.Original, large.OriginalState, large.Modified, large.ModifiedState));

        // A file that is not changed is not read, whatever it is.
        repository.Write("b.txt", "committed later\n");
        Assert.AreEqual(ProjectGitFileResponse.Refused("not_changed"), await Read(".git/config"));
        Assert.AreEqual(ProjectGitFileResponse.Refused("not_changed"), await Read("old.txt"));
    }

    [TestMethod]
    public async Task Changes_SinceTheBaseOfTheBranch_IncludeItsCommits()
    {
        using var repository = await Repository.CreateAsync();
        repository.Write("a.txt", "one\n");
        repository.Write("b.txt", "one\n");
        repository.Commit("first");
        repository.Git("checkout", "-q", "-b", "feature");
        repository.Write("a.txt", "one\ntwo\n");
        repository.Commit("second");
        repository.Write("b.txt", "one\ntwo\nthree\n");

        var head = await repository.Service.ChangesAsync(new ProjectGitChangesRequest(Epoch, repository.ProjectId, "head", null), default);
        Assert.AreEqual(("feature", "head", "main", 1), (head.Branch, head.Comparison, head.BaseReference, head.BaseAhead));
        CollectionAssert.AreEqual(new[] { "b.txt" }, head.Files!.Select(static file => file.Path).ToArray());

        var branch = await repository.Service.ChangesAsync(new ProjectGitChangesRequest(Epoch, repository.ProjectId, "branch", null), default);
        Assert.AreEqual("branch", branch.Comparison);
        CollectionAssert.AreEqual(new[] { "a.txt|1|0", "b.txt|2|0" }, branch.Files!.Select(static file => $"{file.Path}|{file.Insertions}|{file.Deletions}").ToArray());
        Assert.AreNotEqual(head.Revision, branch.Revision);
        var committed = await repository.Service.FileAsync(new(Epoch, repository.ProjectId, "branch", "a.txt"), default);
        Assert.AreEqual(("one\n", "one\ntwo\n"), (committed.Original, committed.Modified));
        Assert.AreEqual("not_changed", (await repository.Service.FileAsync(new(Epoch, repository.ProjectId, "head", "a.txt"), default)).Status);

        // On the base branch itself there is nothing but what is not committed.
        repository.Git("checkout", "-q", "main");
        repository.Now += TimeSpan.FromMinutes(1);
        var main = await repository.Service.ChangesAsync(new ProjectGitChangesRequest(Epoch, repository.ProjectId, "branch", null), default);
        Assert.AreEqual(("main", "head", null), (main.Branch, main.Comparison, main.BaseReference));
    }

    [TestMethod]
    public async Task Commits_ListTheHistoryNewestFirst_AndACommitShowsWhatItChanged()
    {
        using var repository = await Repository.CreateAsync();
        Assert.AreEqual(new ProjectGitCommitsResponse("ok", repository.ProjectId, ProjectGitChanges.Hash(""), [], false),
            await repository.Service.CommitsAsync(new(Epoch, repository.ProjectId, null, null), default) with { Commits = [] }, "No commit yet: no history.");
        repository.Write("a.txt", "one\n");
        repository.Write("gone.txt", "bye\n");
        repository.Commit("First commit");
        repository.Write("a.txt", "one\ntwo\n");
        File.Delete(repository.At("gone.txt"));
        repository.Write("new.txt", "n\n");
        repository.Commit("Second: change, delete and add\n\nWith a body that is not shown.");
        repository.Write("a.txt", "one\ntwo\nthree\n");
        repository.Commit("Third");
        repository.Write("a.txt", "uncommitted\n");

        var history = await repository.Service.CommitsAsync(new(Epoch, repository.ProjectId, 2, null), default);
        Assert.AreEqual(("ok", true), (history.Status, history.More));
        CollectionAssert.AreEqual(new[] { "Third|Test", "Second: change, delete and add|Test" }, history.Commits!.Select(static commit => $"{commit.Subject}|{commit.Author}").ToArray());
        Assert.IsTrue(history.Commits!.All(static commit => ProjectGitChanges.IsObjectId(commit.Id) && commit.Id.StartsWith(commit.ShortId, StringComparison.Ordinal)
            && DateTimeOffset.Parse(commit.Time, System.Globalization.CultureInfo.InvariantCulture) > DateTimeOffset.UtcNow.AddHours(-1)));
        Assert.AreEqual("unchanged", (await repository.Service.CommitsAsync(new(Epoch, repository.ProjectId, 2, history.Revision), default)).Status);
        var all = await repository.Service.CommitsAsync(new(Epoch, repository.ProjectId, 500, history.Revision), default);
        Assert.AreEqual(("ok", 3, false), (all.Status, all.Commits!.Length, all.More));

        // What the second commit changed, whatever the work tree holds now.
        var second = history.Commits![1].Id;
        var changes = await repository.Service.ChangesAsync(new ProjectGitChangesRequest(Epoch, repository.ProjectId, "commit", null, second), default);
        Assert.AreEqual(("ok", "commit", 2, 1), (changes.Status, changes.Comparison, changes.Insertions, changes.Deletions));
        CollectionAssert.AreEqual(new[] { "a.txt|modified|1|0", "gone.txt|deleted|0|1", "new.txt|added|1|0" },
            changes.Files!.Select(static file => $"{file.Path}|{file.Status}|{file.Insertions}|{file.Deletions}").ToArray());
        Task<ProjectGitFileResponse> Read(string path, string commit) => repository.Service.FileAsync(new(Epoch, repository.ProjectId, "commit", path, commit), default);
        var modified = await Read("a.txt", second);
        Assert.AreEqual(("one\n", "text", "one\ntwo\n", "text"), (modified.Original, modified.OriginalState, modified.Modified, modified.ModifiedState));
        Assert.AreEqual(changes.Files!.Single(static file => file.Path == "a.txt").Revision, modified.Revision);
        var deleted = await Read("gone.txt", second);
        Assert.AreEqual(("bye\n", "text", null, "absent"), (deleted.Original, deleted.OriginalState, deleted.Modified, deleted.ModifiedState));
        var added = await Read("new.txt", second);
        Assert.AreEqual((null, "absent", "n\n", "text"), (added.Original, added.OriginalState, added.Modified, added.ModifiedState));

        // The first commit is compared with nothing: everything in it was added.
        var first = all.Commits![2].Id;
        var root = await repository.Service.ChangesAsync(new ProjectGitChangesRequest(Epoch, repository.ProjectId, "commit", null, first), default);
        CollectionAssert.AreEqual(new[] { "a.txt|added", "gone.txt|added" }, root.Files!.Select(static file => $"{file.Path}|{file.Status}").ToArray());
        Assert.AreNotEqual(changes.Revision, root.Revision);

        // A commit is named by its whole id, and only with the comparison that takes one.
        foreach (var (comparison, commit) in new[] { ("commit", null), ("commit", "HEAD"), ("commit", second[..12]), ("head", second), (null, second), ("commit", "--output=x") })
        {
            Assert.AreEqual("invalid", (await repository.Service.ChangesAsync(new ProjectGitChangesRequest(Epoch, repository.ProjectId, comparison, null, commit), default)).Status);
            Assert.AreEqual("invalid", (await repository.Service.FileAsync(new(Epoch, repository.ProjectId, comparison, "a.txt", commit), default)).Status);
        }

        Assert.AreEqual("git_failed", (await repository.Service.ChangesAsync(new ProjectGitChangesRequest(Epoch, repository.ProjectId, "commit", null, new string('0', 39) + "1"), default)).Status);
    }

    [TestMethod]
    public async Task Changes_OfARepositoryWithoutACommit_AreEverythingInIt_AndANestedProjectHasItsPrefix()
    {
        using var repository = await Repository.CreateAsync();
        repository.Write("packages/app/staged.txt", "a\nb\n");
        repository.Git("add", "packages/app/staged.txt");
        repository.Write("notes.txt", "n\n");
        var nested = await repository.Projects.UpsertFromPathAsync(repository.At("packages/app"));

        var changes = await repository.Service.ChangesAsync(new ProjectGitChangesRequest(Epoch, nested.Id, null, null), default);

        Assert.AreEqual(("ok", repository.Root, "packages/app/", "main"), (changes.Status, changes.Root, changes.Prefix, changes.Branch));
        CollectionAssert.AreEqual(new[] { "notes.txt|untracked|1", "packages/app/staged.txt|added|2" },
            changes.Files!.Select(static file => $"{file.Path}|{file.Status}|{file.Insertions}").ToArray());
        var staged = await repository.Service.FileAsync(new(Epoch, nested.Id, null, "packages/app/staged.txt"), default);
        Assert.AreEqual((null, "absent", "a\nb\n", "text"), (staged.Original, staged.OriginalState, staged.Modified, staged.ModifiedState));
    }

    [TestMethod]
    public async Task Watch_DeliversTheRequestsOfTheChangesView()
    {
        using var repository = await Repository.CreateAsync();
        var view = new DesktopChangesView();
        Assert.IsFalse(view.Show("project", null), "No window watches yet.");
        var service = new ProjectGitService(repository.Projects, Epoch, view);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var requests = service.WatchAsync(new(Epoch), cancellation.Token).GetAsyncEnumerator(cancellation.Token);

        var pending = requests.MoveNextAsync();
        while (!view.Show(repository.ProjectId, "src/app.ts")) await Task.Delay(10, cancellation.Token); // Until the page listens.
        Assert.IsTrue(await pending);
        Assert.AreEqual(new ProjectGitShowEvent(repository.ProjectId, "src/app.ts"), requests.Current);

        pending = requests.MoveNextAsync();
        Assert.IsTrue(view.Show(repository.ProjectId, " "));
        Assert.IsTrue(await pending);
        Assert.AreEqual(new ProjectGitShowEvent(repository.ProjectId, null), requests.Current);
        Assert.ThrowsExactly<ArgumentException>(() => view.Show(" ", null));

        // A page of another host instance is told nothing.
        await foreach (var _ in service.WatchAsync(new("another"), default)) Assert.Fail("A stale page gets no request.");
    }

    private sealed class Repository : IDisposable
    {
        private readonly string _home;

        private Repository(string home, string root, ProjectCatalog projects, ProjectDescriptor project)
        {
            _home = home;
            Root = root;
            Projects = projects;
            ProjectId = project.Id;
            Service = new ProjectGitService(projects, Epoch, ProjectGitService.RunGitAsync, () => Now, TimeSpan.FromSeconds(60));
        }

        public static async Task<Repository> CreateAsync()
        {
            var home = Path.Combine(Path.GetTempPath(), "CodeAlta-git-changes-" + Guid.NewGuid().ToString("N"));
            var root = Directory.CreateDirectory(Path.Combine(home, "repository")).FullName;
            var projects = new ProjectCatalog(new CatalogOptions { GlobalRoot = Directory.CreateDirectory(Path.Combine(home, "global")).FullName });
            var repository = new Repository(home, root, projects, await projects.UpsertFromPathAsync(root));
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
            return repository;
        }

        public string Root { get; }
        public ProjectCatalog Projects { get; }
        public string ProjectId { get; }
        public ProjectGitService Service { get; }
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

        public string At(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

        public void Write(string relative, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(At(relative))!);
            File.WriteAllText(At(relative), content);
        }

        public void Write(string relative, byte[] content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(At(relative))!);
            File.WriteAllBytes(At(relative), content);
        }

        public void Commit(string message)
        {
            Git("add", "-A");
            Git("commit", "-q", "-m", message);
        }

        public void Git(params string[] arguments)
        {
            var start = new ProcessStartInfo("git") { WorkingDirectory = Root, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            // Nothing of the machine's or the user's configuration decides what these repositories do.
            start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
            start.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(_home, "no-global-config");
            foreach (var inherited in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE" }) start.Environment.Remove(inherited);
            using var process = Process.Start(start)!;
            var error = process.StandardError.ReadToEndAsync();
            process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            Assert.AreEqual(0, process.ExitCode, $"git {string.Join(' ', arguments)}: {error.Result}");
        }

        public void Dispose()
        {
            try
            {
                // Git marks its objects read-only.
                foreach (var file in Directory.EnumerateFiles(_home, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(_home, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup of a temporary directory.
            }
        }
    }
}
