using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using CodeAlta.Catalog.Worktrees;

namespace CodeAlta.Catalog.Tests;

/// <summary>
/// The worktrees of real repositories, made with the git on the path. Inconclusive on a machine without git.
/// </summary>
[TestClass]
public sealed partial class GitWorktreeServiceTests
{
    [GeneratedRegex("^[a-z]{3,11}$")]
    private static partial Regex Word();

    [TestMethod]
    public void Names_AreLowerCaseWordsThatAppearOnce()
    {
        var adjectives = WorktreeNames.Adjectives.ToArray();
        var nouns = WorktreeNames.Nouns.ToArray();

        foreach (var word in adjectives.Concat(nouns)) Assert.IsTrue(Word().IsMatch(word), word);
        Assert.AreEqual(adjectives.Length, adjectives.Distinct(StringComparer.Ordinal).Count(), "An adjective appears twice.");
        Assert.AreEqual(nouns.Length, nouns.Distinct(StringComparer.Ordinal).Count(), "A noun appears twice.");
        Assert.AreEqual(0, adjectives.Intersect(nouns).Count(), "A word is in both lists.");
        Assert.AreEqual(adjectives.Length * nouns.Length, WorktreeNames.Count);
        Assert.IsTrue(WorktreeNames.Count > 100_000, "There are too few names.");
    }

    [TestMethod]
    public void Pick_IsAnAdjectiveAndANoun()
    {
        var random = new Random(7);
        for (var index = 0; index < 200; index++)
        {
            var parts = WorktreeNames.Pick(random).Split('-');
            Assert.AreEqual(2, parts.Length);
            Assert.IsTrue(WorktreeNames.Adjectives.Contains(parts[0]));
            Assert.IsTrue(WorktreeNames.Nouns.Contains(parts[1]));
        }
    }

    [TestMethod]
    public void PickFree_TakesAnotherNameAndThenANumber()
    {
        var first = WorktreeNames.Pick(new Random(11));
        var another = WorktreeNames.PickFree(new Random(11), name => name == first);
        Assert.AreNotEqual(first, another);
        Assert.AreEqual(2, another.Split('-').Length);

        // When every name is taken the last one gets a number.
        var numbered = WorktreeNames.PickFree(new Random(11), static name => name.Split('-').Length == 2);
        StringAssert.EndsWith(numbered, "-2");
        Assert.ThrowsExactly<InvalidOperationException>(() => WorktreeNames.PickFree(new Random(11), static _ => true));
    }

    [TestMethod]
    public void Settings_ReadTheKnownLocations()
    {
        var home = OperatingSystem.IsWindows() ? @"C:\Users\someone" : "/home/someone";
        var absolute = OperatingSystem.IsWindows() ? @"D:\trees" : "/srv/trees";

        Assert.AreEqual(new WorktreeSettings(WorktreeLocation.Global, null), WorktreeSettings.Read(null));
        Assert.AreEqual(new WorktreeSettings(WorktreeLocation.Global, null), WorktreeSettings.Read(new() { Location = "elsewhere" }));
        Assert.AreEqual(new WorktreeSettings(WorktreeLocation.Project, null), WorktreeSettings.Read(new() { Location = " Project ", Folder = absolute }));
        Assert.AreEqual(new WorktreeSettings(WorktreeLocation.Custom, absolute), WorktreeSettings.Read(new() { Location = "custom", Folder = absolute }));
        Assert.AreEqual(new WorktreeSettings(WorktreeLocation.Custom, Path.Combine(home, "trees")), WorktreeSettings.Read(new() { Location = "custom", Folder = "~/trees" }, home));
        // A custom location needs an absolute folder.
        Assert.AreEqual(new WorktreeSettings(WorktreeLocation.Global, null), WorktreeSettings.Read(new() { Location = "custom" }));
        Assert.AreEqual(new WorktreeSettings(WorktreeLocation.Global, null), WorktreeSettings.Read(new() { Location = "custom", Folder = "trees" }));
        Assert.AreEqual("custom", WorktreeSettings.NameOf(WorktreeLocation.Custom));
        Assert.AreEqual("global", WorktreeSettings.NameOf(WorktreeLocation.Global));
    }

    [TestMethod]
    public void Configuration_KeepsTheLocationAndTheRestOfTheFile()
    {
        using var repository = Repository.Create(commit: false);
        var path = repository.Options.ConfigPath;
        File.WriteAllText(path, "[chat]\ndefault_provider = \"codex\"\n\n[automations.one]\nname = \"Kept\"\n");
        var folder = OperatingSystem.IsWindows() ? @"D:\trees" : "/srv/trees";

        repository.Config.SaveGlobalWorktreeSettings("custom", folder);

        Assert.AreEqual(new WorktreeSettings(WorktreeLocation.Custom, folder), repository.Service.ReadSettings());
        var text = File.ReadAllText(path);
        StringAssert.Contains(text, "[worktrees]");
        StringAssert.Contains(text, "default_provider = \"codex\"");
        StringAssert.Contains(text, "name = \"Kept\"");

        // The default location is not written; the folder stays for the next time.
        repository.Config.SaveGlobalWorktreeSettings("global", folder);
        Assert.AreEqual(new WorktreeSettings(WorktreeLocation.Global, null), repository.Service.ReadSettings());
        Assert.IsFalse(File.ReadAllText(path).Contains("location", StringComparison.Ordinal));
        Assert.AreEqual(folder, repository.Config.LoadGlobal().Worktrees?.Folder);

        repository.Config.SaveGlobalWorktreeSettings(null, null);
        Assert.IsFalse(File.ReadAllText(path).Contains("[worktrees]", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Create_AddsACheckoutOnABranchOfItsOwn()
    {
        using var repository = Repository.Create();
        repository.Write("notes.txt", "not committed");

        var created = await repository.Service.CreateAsync(repository.Project);

        Assert.AreEqual(GitWorktreeService.Ok, created.Status, created.Message);
        Assert.AreEqual(GitWorktreeService.BranchPrefix + created.Name, created.Branch);
        Assert.AreEqual(Path.Combine(repository.Options.WorktreesRoot, "repository", created.Name!), created.Root);
        Assert.AreEqual(created.Root, created.Folder);
        Assert.AreEqual("one", File.ReadAllText(Path.Combine(created.Root!, "readme.md")));
        // A worktree starts from a commit: what is not committed stays in the folder of the project.
        Assert.IsFalse(File.Exists(Path.Combine(created.Root!, "notes.txt")));

        var worktrees = await repository.Service.ListAsync(repository.Root);
        Assert.IsNotNull(worktrees);
        Assert.AreEqual(2, worktrees.Count);
        Assert.IsTrue(worktrees[0].Main);
        Assert.AreEqual("main", worktrees[0].Branch);
        Assert.AreEqual(new GitWorktree(created.Root!, created.Name!, created.Branch, worktrees[1].Head, false, false, false), worktrees[1]);
        Assert.IsTrue(GitWorktreeService.SameRepository(repository.Root, created.Root));
        Assert.AreEqual(created.Root, GitWorktreeService.FindCheckoutRoot(Path.Combine(created.Root!, "missing", "below")));
        Assert.AreEqual(worktrees[1], GitWorktreeService.Find(worktrees, Path.Combine(created.Root!, "src")));
    }

    [TestMethod]
    public async Task Create_ForAProjectBelowTheRoot_ReturnsTheSameFolderOfTheNewCheckout()
    {
        using var repository = Repository.Create();
        repository.Write("src/app/main.txt", "app");
        repository.Commit("app");
        var project = new ProjectDescriptor { Id = "app", Slug = "app", Name = "App", DisplayName = "App", ProjectPath = Path.Combine(repository.Root, "src", "app") };

        var created = await repository.Service.CreateAsync(project);

        Assert.AreEqual(GitWorktreeService.Ok, created.Status, created.Message);
        Assert.AreEqual(Path.Combine(repository.Options.WorktreesRoot, "app", created.Name!), created.Root);
        Assert.AreEqual(Path.Combine(created.Root!, "src", "app"), created.Folder);
        Assert.IsTrue(File.Exists(Path.Combine(created.Folder!, "main.txt")));
    }

    [TestMethod]
    public async Task Create_InsideTheRepository_IsIgnoredByGit()
    {
        using var repository = Repository.Create();
        repository.Config.SaveGlobalWorktreeSettings("project", null);

        var created = await repository.Service.CreateAsync(repository.Project);

        Assert.AreEqual(GitWorktreeService.Ok, created.Status, created.Message);
        Assert.AreEqual(Path.Combine(repository.Root, ".alta", "worktrees", created.Name!), created.Root);
        Assert.AreEqual("", repository.Git("status", "--porcelain").Trim(), "The worktree shows up as a change of the repository.");

        // The folder that held the worktrees goes with the last one.
        Assert.AreEqual(GitWorktreeService.Ok, (await repository.Service.RemoveAsync(repository.Root, created.Root!, force: false)).Status);
        Assert.IsFalse(Directory.Exists(Path.Combine(repository.Root, ".alta", "worktrees")));
    }

    [TestMethod]
    public async Task Create_UnderACustomFolder_AndFromAnotherBranch()
    {
        using var repository = Repository.Create();
        repository.Git("switch", "-q", "-c", "feature");
        repository.Write("feature.txt", "feature");
        repository.Commit("feature");
        repository.Git("switch", "-q", "main");
        var custom = Path.Combine(repository.Home, "trees");
        repository.Config.SaveGlobalWorktreeSettings("custom", custom);

        var created = await repository.Service.CreateAsync(repository.Project, "feature");

        Assert.AreEqual(GitWorktreeService.Ok, created.Status, created.Message);
        Assert.AreEqual(Path.Combine(custom, "repository", created.Name!), created.Root);
        Assert.IsTrue(File.Exists(Path.Combine(created.Root!, "feature.txt")));
        // A folder the user chose stays when its last worktree goes; the folder of the project in it does not.
        Assert.AreEqual(GitWorktreeService.Ok, (await repository.Service.RemoveAsync(repository.Root, created.Root!, force: false)).Status);
        Assert.IsTrue(Directory.Exists(custom));
        Assert.IsFalse(Directory.Exists(Path.Combine(custom, "repository")));
        Assert.AreEqual("invalid", (await repository.Service.CreateAsync(repository.Project, "no-such-branch")).Status);
        Assert.AreEqual("invalid", (await repository.Service.CreateAsync(repository.Project, "--force")).Status);
    }

    [TestMethod]
    public async Task Create_NeedsARepositoryWithACommit()
    {
        using var empty = Repository.Create(commit: false);
        Assert.AreEqual("no_commit", (await empty.Service.CreateAsync(empty.Project)).Status);

        var outside = new ProjectDescriptor { Id = "x", Slug = "x", Name = "x", DisplayName = "x", ProjectPath = Directory.CreateDirectory(Path.Combine(empty.Home, "plain")).FullName };
        Assert.AreEqual("not_repository", (await empty.Service.CreateAsync(outside)).Status);
        Assert.IsNull(await empty.Service.ListAsync(outside.ProjectPath));
        Assert.IsNull(GitWorktreeService.FindCommonDirectory(outside.ProjectPath));
    }

    [TestMethod]
    public async Task Create_NeverGivesTwoWorktreesTheSameName()
    {
        using var repository = Repository.Create(random: new Random(3));
        var first = await repository.Service.CreateAsync(repository.Project);
        // The same choices again: the folder and the branch of the first one are taken.
        using var again = repository.WithRandom(new Random(3));
        var second = await again.Service.CreateAsync(repository.Project);

        Assert.AreEqual(GitWorktreeService.Ok, second.Status, second.Message);
        Assert.AreNotEqual(first.Name, second.Name);
    }

    [TestMethod]
    public async Task Remove_LeavesAWorktreeWithChanges_UnlessItIsForced()
    {
        using var repository = Repository.Create();
        var created = await repository.Service.CreateAsync(repository.Project);
        File.WriteAllText(Path.Combine(created.Root!, "draft.txt"), "not committed");

        var refused = await repository.Service.RemoveAsync(repository.Root, created.Root!, force: false);
        Assert.AreEqual("dirty", refused.Status);
        Assert.IsTrue(Directory.Exists(created.Root));

        var removed = await repository.Service.RemoveAsync(repository.Root, created.Root!, force: true);
        Assert.AreEqual(GitWorktreeService.Ok, removed.Status, removed.Message);
        Assert.IsNull(removed.BranchKept);
        Assert.IsFalse(Directory.Exists(created.Root));
        Assert.AreEqual(1, (await repository.Service.ListAsync(repository.Root))!.Count);
        Assert.AreEqual("", repository.Git("branch", "--list", created.Branch!).Trim(), "The branch of the worktree is still there.");
        // The folders that only held it are gone with it.
        Assert.IsFalse(Directory.Exists(repository.Options.WorktreesRoot));
    }

    [TestMethod]
    public async Task Remove_KeepsABranchThatHoldsCommitsOfItsOwn()
    {
        using var repository = Repository.Create();
        var created = await repository.Service.CreateAsync(repository.Project);
        File.WriteAllText(Path.Combine(created.Root!, "work.txt"), "work");
        repository.GitIn(created.Root!, "add", "-A");
        repository.GitIn(created.Root!, "commit", "-q", "-m", "work");

        var removed = await repository.Service.RemoveAsync(repository.Root, Path.Combine(created.Root!, "below"), force: false);

        Assert.AreEqual(GitWorktreeService.Ok, removed.Status, removed.Message);
        Assert.AreEqual(created.Branch, removed.BranchKept);
        StringAssert.Contains(repository.Git("branch", "--list", created.Branch!), created.Name!);
    }

    [TestMethod]
    public async Task Remove_RefusesTheMainCheckoutAndWhatIsNoWorktree()
    {
        using var repository = Repository.Create();
        Assert.AreEqual("main", (await repository.Service.RemoveAsync(repository.Root, repository.Root, force: true)).Status);
        Assert.AreEqual("not_worktree", (await repository.Service.RemoveAsync(repository.Root, repository.Home, force: true)).Status);
        Assert.IsTrue(Directory.Exists(repository.Root));
    }

    [TestMethod]
    public async Task Remove_ForgetsAWorktreeWhoseFolderIsGone()
    {
        using var repository = Repository.Create();
        var created = await repository.Service.CreateAsync(repository.Project);
        Repository.Delete(created.Root!);

        var listed = (await repository.Service.ListAsync(repository.Root))!;
        Assert.AreEqual(2, listed.Count);
        Assert.IsTrue(listed[1].Missing);

        Assert.AreEqual(GitWorktreeService.Ok, (await repository.Service.RemoveAsync(repository.Root, created.Root!, force: false)).Status);
        Assert.AreEqual(1, (await repository.Service.ListAsync(repository.Root))!.Count);
    }

    [TestMethod]
    public async Task Branches_AreListedAndACheckoutMovesToOne()
    {
        using var repository = Repository.Create();
        repository.Git("branch", "feature");
        var created = await repository.Service.CreateAsync(repository.Project);

        var branches = (await repository.Service.ListBranchesAsync(repository.Root))!;
        CollectionAssert.AreEquivalent(new[] { "main", "feature", created.Branch }, branches.Select(static branch => branch.Name).ToArray());
        Assert.IsTrue(branches.Single(static branch => branch.Name == "main").Current);
        Assert.AreEqual(repository.Root, branches.Single(static branch => branch.Name == "main").Worktree);
        Assert.AreEqual(created.Root, branches.Single(branch => branch.Name == created.Branch).Worktree);
        Assert.IsNull(branches.Single(static branch => branch.Name == "feature").Worktree);
        // Seen from the worktree, its own branch is the current one.
        Assert.AreEqual(created.Branch, (await repository.Service.ListBranchesAsync(created.Root!))!.Single(static branch => branch.Current).Name);

        Assert.AreEqual(GitWorktreeService.Ok, (await repository.Service.SwitchAsync(repository.Root, "feature", create: false)).Status);
        Assert.AreEqual("feature", (await repository.Service.ListAsync(repository.Root))![0].Branch);

        Assert.AreEqual(GitWorktreeService.Ok, (await repository.Service.SwitchAsync(repository.Root, "topic/new", create: true)).Status);
        Assert.AreEqual("topic/new", (await repository.Service.ListAsync(repository.Root))![0].Branch);

        // A branch is used by one checkout at a time: git says which one has it.
        var taken = await repository.Service.SwitchAsync(repository.Root, created.Branch!, create: false);
        Assert.AreEqual("failed", taken.Status);
        Assert.IsFalse(string.IsNullOrWhiteSpace(taken.Message));
        Assert.AreEqual("not_found", (await repository.Service.SwitchAsync(repository.Root, "nowhere", create: false)).Status);
        Assert.AreEqual("invalid", (await repository.Service.SwitchAsync(repository.Root, "-f", create: false)).Status);
    }

    [TestMethod]
    public async Task Switch_IsRefusedWhenChangesWouldBeOverwritten()
    {
        using var repository = Repository.Create();
        repository.Git("switch", "-q", "-c", "feature");
        repository.Write("readme.md", "two");
        repository.Commit("two");
        repository.Git("switch", "-q", "main");
        repository.Write("readme.md", "mine");

        var outcome = await repository.Service.SwitchAsync(repository.Root, "feature", create: false);

        Assert.AreEqual("failed", outcome.Status);
        StringAssert.Contains(outcome.Message, "readme.md");
        Assert.AreEqual("mine", File.ReadAllText(repository.At("readme.md")));
    }

    [TestMethod]
    public void Branches_OfARemoteAreOfferedWhileNoLocalBranchHasTheirName()
    {
        var branches = GitWorktreeService.ParseBranches(string.Join('\n',
            "refs/heads/main\t*\t/work/repo",
            "refs/heads/alta/quiet-heron\t\t/trees/quiet-heron",
            "refs/remotes/origin/HEAD\t\t",
            "refs/remotes/origin/main\t\t",
            "refs/remotes/origin/release/2\t\t",
            ""));

        CollectionAssert.AreEqual(new[] { "main", "alta/quiet-heron", "origin/release/2" }, branches.Select(static branch => branch.Name).ToArray());
        Assert.IsTrue(branches[0].Current);
        Assert.IsFalse(branches[1].Remote);
        Assert.IsTrue(branches[2].Remote);
    }

    [TestMethod]
    public void Worktrees_AreReadFromWhatGitPrints()
    {
        var root = OperatingSystem.IsWindows() ? "C:/work" : "/work";
        var worktrees = GitWorktreeService.ParseWorktrees(string.Join('\n',
            $"worktree {root}/repo", "HEAD 1111111111111111111111111111111111111111", "branch refs/heads/main", "",
            $"worktree {root}/trees/quiet-heron", "HEAD 2222222222222222222222222222222222222222", "branch refs/heads/alta/quiet-heron", "locked in use", "",
            $"worktree {root}/trees/gone", "HEAD 3333333333333333333333333333333333333333", "detached", "prunable gitdir file points to non-existent location", ""));

        Assert.AreEqual(3, worktrees.Count);
        Assert.AreEqual(new GitWorktree(Path.GetFullPath(root + "/repo"), "repo", "main", "1111111111111111111111111111111111111111", true, false, false), worktrees[0]);
        Assert.AreEqual(new GitWorktree(Path.GetFullPath(root + "/trees/quiet-heron"), "quiet-heron", "alta/quiet-heron", "2222222222222222222222222222222222222222", false, true, false), worktrees[1]);
        Assert.AreEqual(new GitWorktree(Path.GetFullPath(root + "/trees/gone"), "gone", null, "3333333333333333333333333333333333333333", false, false, true), worktrees[2]);
    }

    [TestMethod]
    public void ReferenceNames_AreNeverOptions()
    {
        foreach (var name in new[] { "main", "alta/quiet-heron", "origin/release/2.0", "v1.2.3", "feature_x" })
            Assert.IsTrue(GitWorktreeService.IsReferenceName(name), name);
        foreach (var name in new string?[] { null, "", "-f", "--force", "a b", "a..b", "a~1", "a^", "a:b", "a?", "a*", "[a", "a\\b", "a@{1}", "/a", "a/", "a//b", "a/.b", "a.lock", "a.", ".a", "@", "a\tb", new string('a', 201) })
            Assert.IsFalse(GitWorktreeService.IsReferenceName(name), name);
    }

    [TestMethod]
    public void IsWithin_ComparesWholeFolders()
    {
        var root = OperatingSystem.IsWindows() ? @"C:\trees\quiet-heron" : "/trees/quiet-heron";
        Assert.IsTrue(GitWorktreeService.IsWithin(root, root));
        Assert.IsTrue(GitWorktreeService.IsWithin(Path.Combine(root, "src", "app"), root));
        Assert.IsTrue(GitWorktreeService.IsWithin(root + Path.DirectorySeparatorChar, root));
        Assert.IsFalse(GitWorktreeService.IsWithin(root + "-2", root));
        Assert.IsFalse(GitWorktreeService.IsWithin(Path.GetDirectoryName(root), root));
        Assert.IsFalse(GitWorktreeService.IsWithin(null, root));
        Assert.IsFalse(GitWorktreeService.IsWithin(root, " "));
    }

    private sealed class Repository : IDisposable
    {
        private readonly bool _owner;

        private Repository(string home, CatalogOptions options, Random? random, bool owner)
        {
            Home = home;
            Root = Path.Combine(home, "repository");
            Options = options;
            Config = new CodeAltaConfigStore(options);
            Service = new GitWorktreeService(options, Config, RunAsync, random);
            Project = new ProjectDescriptor { Id = "repository", Slug = "repository", Name = "Repository", DisplayName = "Repository", ProjectPath = Root };
            _owner = owner;
        }

        public static Repository Create(bool commit = true, Random? random = null)
        {
            // The real path: git answers with it, and a temporary folder may be reached through a link.
            var home = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "CodeAlta-worktrees-" + Guid.NewGuid().ToString("N"))).FullName;
            if (Directory.ResolveLinkTarget(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), returnFinalTarget: true) is { } target)
                home = Path.Combine(target.FullName, Path.GetFileName(home));
            var options = new CatalogOptions { GlobalRoot = Directory.CreateDirectory(Path.Combine(home, "global")).FullName };
            var repository = new Repository(home, options, random, owner: true);
            Directory.CreateDirectory(repository.Root);
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
            if (commit)
            {
                repository.Write("readme.md", "one");
                repository.Commit("one");
            }

            return repository;
        }

        public string Home { get; }
        public string Root { get; }
        public CatalogOptions Options { get; }
        public CodeAltaConfigStore Config { get; }
        public GitWorktreeService Service { get; }
        public ProjectDescriptor Project { get; }

        // The same repository and configuration, with another source of names.
        public Repository WithRandom(Random random) => new(Home, Options, random, owner: false);

        public string At(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

        public void Write(string relative, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(At(relative))!);
            File.WriteAllText(At(relative), content);
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
            if (!_owner) return;
            try
            {
                Delete(Home);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup of a temporary directory.
            }
        }
    }
}
