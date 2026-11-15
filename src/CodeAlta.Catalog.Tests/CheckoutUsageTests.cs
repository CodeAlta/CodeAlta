using System.ComponentModel;
using System.Diagnostics;
using CodeAlta.Catalog.Storage;
using CodeAlta.Catalog.Worktrees;

namespace CodeAlta.Catalog.Tests;

/// <summary>
/// The measure and the git state of checkouts, on real repositories made with the git on the path, each with a local
/// bare repository as its remote. Inconclusive on a machine without git.
/// </summary>
[TestClass]
public sealed class CheckoutUsageTests
{
    [TestMethod]
    public async Task DefaultReaders_DoNotRefreshTheGitIndex()
    {
        using var checkout = Checkout.Create();
        var index = checkout.At(".git/index");
        foreach (var measure in new[] { true, false })
        {
            // Change a tracked file's stat data without changing its contents: ordinary status writes the index.
            File.SetLastWriteTimeUtc(checkout.At("readme.md"), DateTime.UtcNow.AddDays(measure ? -2 : -3));
            var before = File.ReadAllBytes(index);
            if (measure)
                Assert.IsTrue((await new CheckoutUsageScanner().ScanAsync(checkout.Root, CancellationToken.None)).GitKnown);
            else
                Assert.IsNotNull(await new GitCheckoutStateReader().ReadAsync(checkout.Root, GitCheckoutScope.Repository, CancellationToken.None));
            CollectionAssert.AreEqual(before, File.ReadAllBytes(index), "A disk usage read must leave the index unchanged.");
        }
    }

    [TestMethod]
    public void FolderUsage_CountsTheFiles_WithoutFollowingALinkToAnotherFolder()
    {
        var root = Directory.CreateTempSubdirectory("codealta-folder-usage-");
        try
        {
            var tree = Directory.CreateDirectory(Path.Combine(root.FullName, "tree"));
            File.WriteAllBytes(Path.Combine(tree.FullName, "a.txt"), new byte[10]);
            File.WriteAllBytes(Path.Combine(Directory.CreateDirectory(Path.Combine(tree.FullName, "src", "deep")).FullName, "b.bin"), new byte[2048]);
            var elsewhere = Directory.CreateDirectory(Path.Combine(root.FullName, "elsewhere"));
            File.WriteAllBytes(Path.Combine(elsewhere.FullName, "big.bin"), new byte[100_000]);
            if (OperatingSystem.IsWindows())
            {
                // A junction, as a package manager makes one, to a folder the removal does not delete.
                using var link = Process.Start(new ProcessStartInfo("cmd.exe", ["/d", "/c", "mklink", "/J", Path.Combine(tree.FullName, "node_modules"), elsewhere.FullName])
                    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
                link.WaitForExit();
                Assert.AreEqual(0, link.ExitCode, "the junction is made");
            }
            else
            {
                Directory.CreateSymbolicLink(Path.Combine(tree.FullName, "node_modules"), elsewhere.FullName);
            }

            Assert.AreEqual((2058L, 2L, 0L), Figures(FolderUsage.Measure(tree.FullName, CancellationToken.None)));
            Assert.AreEqual((10L, 1L, 0L), Figures(FolderUsage.Measure(Path.Combine(tree.FullName, "a.txt"), CancellationToken.None)), "a file is measured too");
            Assert.AreEqual(FolderUsage.Empty, FolderUsage.Measure(Path.Combine(tree.FullName, "node_modules"), CancellationToken.None), "a link holds nothing");
            Assert.AreEqual(FolderUsage.Empty, FolderUsage.Measure(Path.Combine(root.FullName, "gone"), CancellationToken.None), "a folder that is not there holds nothing");
        }
        finally
        {
            // The link goes first, without what it points to.
            var link = Path.Combine(root.FullName, "tree", "node_modules");
            if (Directory.Exists(link)) Directory.Delete(link);
            root.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void BuildOutput_IsAWhollyIgnoredFolderOfTheList_OrAFileOfTheSystem()
    {
        Assert.IsTrue(BuildOutput.Includes("bin", folder: true));
        Assert.IsTrue(BuildOutput.Includes("src/App/OBJ", folder: true), "the case of a name does not matter");
        Assert.IsFalse(BuildOutput.Includes("web/node_modules/left-pad", folder: true), "only the name of the folder itself counts");
        Assert.IsFalse(BuildOutput.Includes("bin/Debug", folder: true));
        Assert.IsFalse(BuildOutput.Includes("build/secrets", folder: true));
        Assert.IsFalse(BuildOutput.Includes(".vs", folder: true), "it holds the state of the user and launch files written by hand");
        Assert.IsFalse(BuildOutput.Includes("src/bin/out.dll", folder: false), "a file git names one by one is never build output");
        Assert.IsFalse(BuildOutput.Includes("bin/.env", folder: false));
        Assert.IsFalse(BuildOutput.Includes("build", folder: false), "a file named as a build folder is not one");
        Assert.IsFalse(BuildOutput.Includes(".env", folder: false));
        Assert.IsFalse(BuildOutput.Includes("data/cache", folder: true));
        Assert.IsFalse(BuildOutput.Includes("binaries", folder: true));
        Assert.IsTrue(BuildOutput.Includes(".DS_Store", folder: false));
        Assert.IsTrue(BuildOutput.Includes("docs/images/Thumbs.db", folder: false));
        Assert.IsTrue(BuildOutput.Includes("Desktop.ini", folder: false));
    }

    [TestMethod]
    public async Task ACleanPushedCheckout_HasNothingLocal()
    {
        using var checkout = Checkout.Create();

        var state = await checkout.ReadAsync();
        var usage = await checkout.ScanAsync();

        Assert.IsNotNull(state);
        Assert.IsTrue(state.HasRemote);
        Assert.AreEqual("main", state.Branch);
        Assert.IsFalse(state.HasLocalWork, state.ToString());
        Assert.IsNotNull(state.LastCommit);
        Assert.IsTrue(DateTimeOffset.UtcNow - state.LastCommit.Value < TimeSpan.FromHours(1));
        Assert.IsTrue(usage.GitKnown);
        Assert.IsTrue(usage.GitBytes > 0, "the .git folder of the project folder is measured");
        Assert.AreEqual(FolderUsage.Measure(checkout.Root, CancellationToken.None).Bytes, usage.TotalBytes);
        Assert.AreEqual(CheckoutVerdict.NothingLocal, CheckoutVerdicts.Of(state, usage));
    }

    [TestMethod]
    public async Task AChangedFile_IsLocalWork()
    {
        using var checkout = Checkout.Create();
        checkout.Write("readme.md", "changed");

        var state = await checkout.ReadAsync();

        Assert.AreEqual(1, state!.ChangedFiles);
        Assert.AreEqual(0, state.UntrackedFiles);
        Assert.IsTrue(state.HasLocalWork);
        Assert.AreEqual(CheckoutVerdict.HasLocalWork, CheckoutVerdicts.Of(state, await checkout.ScanAsync()));
    }

    [TestMethod]
    public async Task ARename_CountsOnce()
    {
        using var checkout = Checkout.Create();
        checkout.Git("mv", "readme.md", "renamed.md");

        var state = await checkout.ReadAsync();

        // The record of a rename carries the path it came from: it is not read as another change.
        Assert.AreEqual(1, state!.ChangedFiles);
        Assert.AreEqual(0, state.UntrackedFiles);
    }

    [TestMethod]
    public async Task AnUntrackedFile_IsLocalWork()
    {
        using var checkout = Checkout.Create();
        checkout.Write("notes/new.txt", "new");

        var state = await checkout.ReadAsync();

        Assert.AreEqual(0, state!.ChangedFiles);
        Assert.AreEqual(1, state.UntrackedFiles);
        Assert.IsTrue(state.HasLocalWork);
    }

    [TestMethod]
    public async Task ABranchWithACommitNotPushed_IsLocalWork()
    {
        using var checkout = Checkout.Create();
        checkout.Git("switch", "-q", "-c", "feature");
        checkout.Write("feature.txt", "feature");
        checkout.Commit("feature");
        checkout.Write("feature.txt", "more");
        checkout.Commit("more");
        checkout.Git("switch", "-q", "main");

        var state = await checkout.ReadAsync();

        CollectionAssert.AreEqual(new[] { new UnpushedBranch("feature", 2) }, state!.UnpushedBranches.ToArray());
        Assert.IsFalse(state.UnpushedBranchesTruncated);
        Assert.IsTrue(state.HasLocalWork);
    }

    [TestMethod]
    public async Task ABranchPushedUnderAnotherName_IsNotLocalWork()
    {
        using var checkout = Checkout.Create();
        checkout.Git("switch", "-q", "-c", "feature");
        checkout.Write("feature.txt", "feature");
        checkout.Commit("feature");
        checkout.Git("push", "-q", "origin", "feature:other");

        var state = await checkout.ReadAsync();

        Assert.AreEqual(0, state!.UnpushedBranches.Count, "its commits are on origin/other");
        Assert.AreEqual("feature", state.Branch);
        Assert.IsFalse(state.HasLocalWork);
    }

    [TestMethod]
    public async Task ARepositoryWithoutARemote_IsLocalWork()
    {
        using var checkout = Checkout.Create(remote: false);

        var state = await checkout.ReadAsync();

        Assert.IsFalse(state!.HasRemote);
        CollectionAssert.AreEqual(new[] { new UnpushedBranch("main", 1) }, state.UnpushedBranches.ToArray());
        Assert.IsTrue(state.HasLocalWork);
    }

    [TestMethod]
    public async Task AStash_IsLocalWorkOfTheRepository()
    {
        using var checkout = Checkout.Create();
        checkout.Write("readme.md", "stashed");
        checkout.Git("stash", "push", "-q", "-m", "one");
        checkout.Write("readme.md", "stashed again");
        checkout.Git("stash", "push", "-q", "-m", "two");

        var state = await checkout.ReadAsync();
        var worktree = await checkout.ReadAsync(scope: GitCheckoutScope.Worktree);

        Assert.AreEqual(2, state!.Stashes);
        Assert.AreEqual(0, state.ChangedFiles);
        Assert.IsTrue(state.HasLocalWork);
        Assert.AreEqual(0, worktree!.Stashes, "the stashes belong to the repository, not to one checkout");
        Assert.IsFalse(worktree.HasLocalWork);
    }

    [TestMethod]
    public async Task ADetachedHeadOnACommitOfNoBranch_IsLocalWork()
    {
        using var checkout = Checkout.Create();
        checkout.Git("switch", "-q", "--detach");
        var clean = await checkout.ReadAsync();
        checkout.Write("detached.txt", "detached");
        checkout.Commit("detached");

        var state = await checkout.ReadAsync();

        Assert.IsTrue(clean!.Detached);
        Assert.AreEqual(0, clean.DetachedCommits, "a detached HEAD on a pushed commit loses nothing");
        Assert.IsFalse(clean.HasLocalWork);
        Assert.IsNull(state!.Branch);
        Assert.AreEqual(1, state.DetachedCommits);
        Assert.IsTrue(state.HasLocalWork);
    }

    [TestMethod]
    public async Task AWorktree_CountsOnlyItsOwnFilesAndBranch()
    {
        using var checkout = Checkout.Create();
        var tree = Path.Combine(checkout.Home, "tree");
        checkout.Git("worktree", "add", "-q", "-b", "work", tree);
        checkout.GitIn(tree, "push", "-q", "-u", "origin", "work");
        // Elsewhere in the repository: a stash and a branch with a commit of its own.
        checkout.Write("readme.md", "stashed");
        checkout.Git("stash", "push", "-q");
        checkout.Git("switch", "-q", "-c", "local");
        checkout.Write("local.txt", "local");
        checkout.Commit("local");

        var inTree = await checkout.ReadAsync(tree, GitCheckoutScope.Worktree);
        var repository = await checkout.ReadAsync();
        var usage = await checkout.ScanAsync(tree);

        Assert.IsNotNull(inTree);
        Assert.AreEqual("work", inTree.Branch);
        Assert.IsFalse(inTree.HasLocalWork, inTree.ToString());
        Assert.IsTrue(repository!.HasLocalWork);
        Assert.AreEqual(1, repository.Stashes);
        CollectionAssert.AreEqual(new[] { new UnpushedBranch("local", 1) }, repository.UnpushedBranches.ToArray());
        Assert.IsTrue(usage.GitKnown);
        Assert.AreEqual(0L, usage.GitBytes, "the .git of a worktree is a file");
        Assert.AreEqual(CheckoutVerdict.NothingLocal, CheckoutVerdicts.Of(inTree, usage));

        // An unpushed commit on its own branch counts against it.
        checkout.GitIn(tree, "commit", "-q", "--allow-empty", "-m", "unpushed");
        var later = await checkout.ReadAsync(tree, GitCheckoutScope.Worktree);
        CollectionAssert.AreEqual(new[] { new UnpushedBranch("work", 1) }, later!.UnpushedBranches.ToArray());
    }

    [TestMethod]
    public async Task ASubmoduleWithAChange_IsNamed_AndItsIgnoredFilesArePrefixed()
    {
        using var checkout = Checkout.Create();
        checkout.AddSubmodule("lib");
        checkout.Git("push", "-q");
        var clean = await checkout.ReadAsync();
        File.WriteAllText(Path.Combine(checkout.Root, "lib", ".gitignore"), "*.log\n");
        File.WriteAllText(Path.Combine(checkout.Root, "lib", "trace.log"), "12345");

        var state = await checkout.ReadAsync();
        var usage = await checkout.ScanAsync();

        Assert.IsFalse(clean!.HasLocalWork, clean.ToString());
        CollectionAssert.AreEqual(new[] { "lib" }, state!.SubmodulesWithLocalWork.ToArray());
        Assert.IsTrue(state.HasLocalWork);
        Assert.IsTrue(usage.GitKnown);
        Assert.AreEqual(new IgnoredEntry("lib/trace.log", false, 5, false) { Repository = "lib" }, Bare(usage.IgnoredEntries.Single()), "git named it in the submodule");
    }

    [TestMethod]
    public async Task AnIgnoredBinFolder_IsBuildOutput_AndAnIgnoredEnvIsNot()
    {
        using var checkout = Checkout.Create();
        checkout.Write(".gitignore", "bin/\n.env\n");
        checkout.Commit("ignore");
        checkout.Git("push", "-q");
        checkout.Write("bin/Debug/a.dll", new string('a', 1000));
        checkout.Write("bin/b.dll", new string('b', 500));
        var buildOnly = await checkout.ScanAsync();
        checkout.Write(".env", "SECRET=1");

        var state = await checkout.ReadAsync();
        var usage = await checkout.ScanAsync();

        Assert.AreEqual(CheckoutVerdict.NothingLocal, CheckoutVerdicts.Of(state, buildOnly));
        Assert.AreEqual(1500L, usage.BuildOutputBytes);
        Assert.AreEqual(8L, usage.OtherIgnoredBytes);
        Assert.AreEqual(1L, usage.OtherIgnoredCount);
        CollectionAssert.AreEqual(new[] { new IgnoredEntry("bin", true, 1500, true), new IgnoredEntry(".env", false, 8, false) }, Bare(usage.IgnoredEntries));
        Assert.IsFalse(usage.IgnoredEntriesTruncated);
        var tracked = new FileInfo(checkout.At("readme.md")).Length + new FileInfo(checkout.At(".gitignore")).Length;
        Assert.AreEqual(tracked + usage.GitBytes + 1500 + 8, usage.TotalBytes);
        Assert.AreEqual(Figures(FolderUsage.Measure(checkout.Root, CancellationToken.None)), (usage.TotalBytes, usage.TotalFiles, 0L), "each part is counted once");
        Assert.IsFalse(state!.HasLocalWork, "ignored files are not changes");
        Assert.AreEqual(CheckoutVerdict.IgnoredFilesOnly, CheckoutVerdicts.Of(state, usage));
    }

    [TestMethod]
    public async Task AFolderLeftWithOnlyItsBuildFolders_IsBuildOutput()
    {
        using var checkout = Checkout.Create();
        checkout.Write(".gitignore", "bin/\nobj/\n*.log\n");
        checkout.Commit("ignore");
        checkout.Git("push", "-q");
        // What a project removed on another branch leaves behind: nothing tracked, only what its builds wrote.
        checkout.Write("src/Old/bin/a.dll", new string('a', 40));
        checkout.Write("src/Old/obj/b", new string('b', 10));

        var usage = await checkout.ScanAsync();

        CollectionAssert.AreEqual(new[] { new IgnoredEntry("src/Old/bin", true, 40, true), new IgnoredEntry("src/Old/obj", true, 10, true) },
            Bare(usage.IgnoredEntries));
        Assert.AreEqual(CheckoutVerdict.NothingLocal, CheckoutVerdicts.Of(await checkout.ReadAsync(), usage));

        // A folder that holds only ignored files of another kind is named file by file, and is not build output.
        checkout.Write("logs/x.log", "x");
        usage = await checkout.ScanAsync();
        Assert.AreEqual(1L, usage.OtherIgnoredCount);
        Assert.AreEqual(CheckoutVerdict.IgnoredFilesOnly, CheckoutVerdicts.Of(await checkout.ReadAsync(), usage));
    }

    [TestMethod]
    public async Task AnIgnoredFileBesideATrackedOneInABinFolder_IsOtherIgnored()
    {
        using var checkout = Checkout.Create();
        checkout.Write(".gitignore", "bin/\nobj/\n");
        checkout.Write("bin/tool.sh", "echo tool");
        checkout.Git("add", "-f", "bin/tool.sh");
        checkout.Commit("tool");
        checkout.Git("push", "-q");
        checkout.Write("bin/.env", new string('e', 30));
        checkout.Write("src/obj/cache/x", new string('x', 20));

        var usage = await checkout.ScanAsync();

        // Git names the ignored files of a folder that holds a tracked file one by one: the folder is not output only.
        CollectionAssert.AreEqual(new[] { new IgnoredEntry("bin/.env", false, 30, false), new IgnoredEntry("src/obj", true, 20, true) },
            Bare(usage.IgnoredEntries));
        Assert.AreEqual(20L, usage.BuildOutputBytes);
        Assert.AreEqual(30L, usage.OtherIgnoredBytes);
        Assert.AreEqual(CheckoutVerdict.IgnoredFilesOnly, CheckoutVerdicts.Of(await checkout.ReadAsync(), usage));
    }

    [TestMethod]
    public async Task IgnoredFilesOfTheSystem_AreBuildOutputWhereverTheyAre()
    {
        using var checkout = Checkout.Create();
        checkout.Write(".gitignore", ".DS_Store\nThumbs.db\n");
        checkout.Write("docs/keep.md", "kept");
        checkout.Commit("ignore");
        checkout.Git("push", "-q");
        checkout.Write(".DS_Store", "1234");
        checkout.Write("docs/Thumbs.db", "12");

        var usage = await checkout.ScanAsync();

        Assert.AreEqual(6L, usage.BuildOutputBytes);
        Assert.AreEqual(0L, usage.OtherIgnoredCount);
        Assert.AreEqual(CheckoutVerdict.NothingLocal, CheckoutVerdicts.Of(await checkout.ReadAsync(), usage));
    }

    [TestMethod]
    public async Task ALinkToALargeFolderOutside_IsNotFollowed()
    {
        using var checkout = Checkout.Create();
        checkout.Write(".gitignore", "cache\n");
        checkout.Commit("ignore");
        checkout.Git("push", "-q");
        var outside = Directory.CreateDirectory(Path.Combine(checkout.Home, "outside")).FullName;
        File.WriteAllBytes(Path.Combine(outside, "large.bin"), new byte[200_000]);
        try
        {
            Directory.CreateSymbolicLink(checkout.At("cache"), outside);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Assert.Inconclusive("Symbolic links cannot be created here: " + exception.Message);
        }

        var usage = await checkout.ScanAsync();

        Assert.IsTrue(usage.TotalBytes < 200_000, usage.TotalBytes.ToString());
        Assert.AreEqual(new IgnoredEntry("cache", false, 0, false), Bare(usage.IgnoredEntries.Single()));
        Assert.AreEqual(0L, usage.OtherIgnoredBytes);
        Assert.AreEqual(0L, usage.SkippedEntries);
    }

    [TestMethod]
    public async Task AFolderThatIsNotARepository_HasATotal_AndGitIsNotKnown()
    {
        using var checkout = Checkout.Create(init: false);
        Directory.CreateDirectory(checkout.Root);
        checkout.Write("data/a.bin", new string('a', 1234));
        checkout.Write("b.txt", "bb");

        var usage = await checkout.ScanAsync();
        var state = await checkout.ReadAsync();

        Assert.IsFalse(usage.GitKnown);
        Assert.AreEqual(1236L, usage.TotalBytes);
        Assert.AreEqual(2L, usage.TotalFiles);
        Assert.AreEqual(0, usage.IgnoredEntries.Count);
        Assert.IsNull(state);
        Assert.AreEqual(CheckoutVerdict.Unknown, CheckoutVerdicts.Of(state, usage));
    }

    [TestMethod]
    public async Task AFolderInsideACheckout_IsNotACheckout()
    {
        using var checkout = Checkout.Create();
        checkout.Write("src/a.txt", "a");
        checkout.Commit("src");

        Assert.IsNull(await checkout.ReadAsync(checkout.At("src")));
        Assert.IsFalse((await checkout.ScanAsync(checkout.At("src"))).GitKnown);
    }

    [TestMethod]
    public async Task AnAnswerOfGitThatMayBeCut_IsNotKnown()
    {
        using var checkout = Checkout.Create();
        // As the runner of the application answers when the output passes its limit: exit code 0, the output cut.
        GitRunner cut = async (folder, arguments, timeout, token) => arguments[0] == "status"
            ? new GitRun(0, new string('x', GitWorktreeService.MaximumOutputCharacters), string.Empty)
            : await checkout.RunAsync(folder, arguments, timeout, token);

        Assert.IsNull(await new GitCheckoutStateReader(cut).ReadAsync(checkout.Root, GitCheckoutScope.Repository, CancellationToken.None));
        var usage = await new CheckoutUsageScanner(cut).ScanAsync(checkout.Root, CancellationToken.None);
        Assert.IsFalse(usage.GitKnown);
        Assert.IsTrue(usage.GitBytes > 0);
        Assert.AreEqual(FolderUsage.Measure(checkout.Root, CancellationToken.None).Bytes, usage.TotalBytes);
    }

    [TestMethod]
    public async Task Cancellation_StopsTheMeasureAndTheRead()
    {
        using var checkout = Checkout.Create();
        for (var index = 0; index < 50; index++) checkout.Write($"files/{index}.txt", "x");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => new CheckoutUsageScanner(checkout.RunAsync).ScanAsync(checkout.Root, canceled.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => new GitCheckoutStateReader(checkout.RunAsync).ReadAsync(checkout.Root, GitCheckoutScope.Repository, canceled.Token));

        // Canceled while it walks the files.
        using var during = new CancellationTokenSource();
        var reports = new List<CheckoutUsageProgress>();
        var progress = new Reports(report =>
        {
            reports.Add(report);
            if (report.ProcessedFiles > 0) during.Cancel();
        });
        await Assert.ThrowsAsync<OperationCanceledException>(() => new CheckoutUsageScanner(checkout.RunAsync).ScanAsync(checkout.Root, progress, during.Token));
        Assert.AreEqual(new CheckoutUsageProgress(0), reports[0]);
        Assert.IsTrue(reports[^1].ProcessedFiles < 50, "the walk stopped");
    }

    [TestMethod]
    public void TheVerdict_FollowsTheStateAndTheIgnoredFiles()
    {
        var clean = new GitCheckoutState(true, 0, 0, "main", [], false, 0, 0, [], DateTimeOffset.UnixEpoch);
        CheckoutUsage Usage(bool known, long otherCount) => new("/c", 10, 1, 0, 5, otherCount, otherCount, [], false, 0, known);

        Assert.AreEqual(CheckoutVerdict.Unknown, CheckoutVerdicts.Of(null, Usage(true, 0)));
        Assert.AreEqual(CheckoutVerdict.Unknown, CheckoutVerdicts.Of(clean, null), "the ignored files are not known");
        Assert.AreEqual(CheckoutVerdict.Unknown, CheckoutVerdicts.Of(clean, Usage(false, 0)));
        Assert.AreEqual(CheckoutVerdict.NothingLocal, CheckoutVerdicts.Of(clean, Usage(true, 0)));
        Assert.AreEqual(CheckoutVerdict.IgnoredFilesOnly, CheckoutVerdicts.Of(clean, Usage(true, 1)));
        Assert.AreEqual(CheckoutVerdict.IgnoredFilesOnly, CheckoutVerdicts.Of(clean, Usage(true, 0) with { OtherIgnoredCount = 1 }), "an empty .env is still there");
        // A folder git could not open may hold anything: git exits 0 and only warns.
        Assert.AreEqual(CheckoutVerdict.Unknown, CheckoutVerdicts.Of(clean, Usage(true, 0) with { SkippedEntries = 1 }));
        foreach (var work in new[]
                 {
                     clean with { HasRemote = false }, clean with { ChangedFiles = 1 }, clean with { UntrackedFiles = 1 },
                     clean with { UnpushedBranches = [new("feature", 1)] }, clean with { UnpushedBranchesTruncated = true }, clean with { Stashes = 1 },
                     clean with { Branch = null, DetachedCommits = 1 }, clean with { SubmodulesWithLocalWork = ["lib"] }, clean with { HiddenFiles = 1 },
                     clean with { TagOnlyCommits = 1 }, clean with { UnexaminedSubmoduleRepositories = 1 }, clean with { OperationInProgress = "rebase" },
                     clean with { OtherWorktreeDetachedCommits = 1 }, clean with { OtherRefs = 1 },
                 })
        {
            Assert.AreEqual(CheckoutVerdict.HasLocalWork, CheckoutVerdicts.Of(work, Usage(true, 1)), work.ToString());
            Assert.AreEqual(CheckoutVerdict.HasLocalWork, CheckoutVerdicts.Of(work, null), "local work is known without the measure");
        }
    }

    [TestMethod]
    public async Task FilesMarkedAssumeUnchangedOrSkipWorktree_AreLocalWork()
    {
        using var checkout = Checkout.Create();
        checkout.Write("config.json", "{}");
        checkout.Write("local.json", "{}");
        checkout.Commit("config");
        checkout.Git("push", "-q");
        checkout.Git("update-index", "--assume-unchanged", "config.json");
        checkout.Git("update-index", "--skip-worktree", "local.json");
        checkout.Write("config.json", "{ \"changed\": true }");
        checkout.Write("local.json", "{ \"changed\": true }");

        var state = await checkout.ReadAsync();

        Assert.AreEqual(0, state!.ChangedFiles, "git status hides them");
        Assert.AreEqual(2, state.HiddenFiles);
        Assert.IsTrue(state.HasLocalWork);
        Assert.AreEqual(2, (await checkout.ReadAsync(scope: GitCheckoutScope.Worktree))!.HiddenFiles, "they are files of the checkout itself");
    }

    [TestMethod]
    public async Task TheFilesASparseCheckoutLeavesOut_AreNotLocalWork()
    {
        using var checkout = Checkout.Create();
        checkout.Write("keep/a.txt", "a");
        checkout.Write("away/b.txt", "b");
        checkout.Commit("folders");
        checkout.Git("push", "-q");
        checkout.Git("sparse-checkout", "set", "keep");

        var state = await checkout.ReadAsync();

        Assert.IsFalse(File.Exists(checkout.At("away/b.txt")), "the sparse checkout left it out");
        Assert.AreEqual(0, state!.HiddenFiles);
        Assert.IsFalse(state.HasLocalWork, state.ToString());
    }

    [TestMethod]
    public async Task MarkedFiles_AreCountedFolderByFolder_WhenTheListIsCut()
    {
        using var checkout = Checkout.Create();
        foreach (var path in new[] { "top.txt", "a/a1.txt", "a/a2.txt", "a/b/b1.txt", "a/b/b2.txt", "c [1]/c1.txt", "c [1]/d/d1.txt", "c [1]/d/d2.txt" }) checkout.Write(path, path);
        checkout.Commit("files");
        checkout.Git("push", "-q");
        checkout.Git("update-index", "--assume-unchanged", "a/b/b1.txt", "c [1]/c1.txt", "top.txt");
        var listings = new List<string>();
        // As the runner of the application answers a listing of more files than it keeps: a large repository, made small.
        GitRunner cutAbove(int records) => async (folder, arguments, timeout, token) =>
        {
            var run = await checkout.RunAsync(folder, arguments, timeout, token);
            if (arguments[0] != "ls-files" || arguments[1] != "-v") return run;
            listings.Add(string.Join(' ', arguments.Skip(4)));
            return run.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries).Length > records
                ? run with { Output = new string('x', GitWorktreeService.MaximumOutputCharacters) }
                : run;
        };

        var state = await new GitCheckoutStateReader(cutAbove(2)).ReadAsync(checkout.Root, GitCheckoutScope.Repository, CancellationToken.None);

        Assert.AreEqual(3, state!.HiddenFiles);
        CollectionAssert.Contains(listings, ":(top,literal)a/b/");
        CollectionAssert.Contains(listings, @":(top,glob)c \[1]/*", "a name is a pattern there, with its special characters escaped");
        // A folder with more files right in it than one answer holds cannot be split: the state is not known.
        Assert.IsNull(await new GitCheckoutStateReader(cutAbove(1)).ReadAsync(checkout.Root, GitCheckoutScope.Repository, CancellationToken.None));
    }

    [TestMethod]
    public async Task CommitsOnlyATagHolds_AreLocalWorkOfTheRepository()
    {
        using var checkout = Checkout.Create();
        checkout.Write("release.txt", "release");
        checkout.Commit("release");
        checkout.Git("tag", "v1");
        checkout.Git("reset", "-q", "--hard", "HEAD~1");

        var state = await checkout.ReadAsync();

        Assert.AreEqual(1, state!.TagOnlyCommits);
        Assert.IsTrue(state.HasLocalWork);
        Assert.AreEqual(0, (await checkout.ReadAsync(scope: GitCheckoutScope.Worktree))!.TagOnlyCommits);
        // Git keeps no remote-tracking ref of a pushed tag: read offline, its commits are still on no remote.
        checkout.Git("push", "-q", "origin", "v1");
        Assert.AreEqual(1, (await checkout.ReadAsync())!.TagOnlyCommits);
    }

    [TestMethod]
    public async Task TheDetachedHeadOfAnotherWorktree_IsLocalWorkOfTheRepository()
    {
        using var checkout = Checkout.Create();
        var tree = Path.Combine(checkout.Home, "tree");
        checkout.Git("worktree", "add", "-q", "--detach", tree);
        checkout.GitIn(tree, "commit", "-q", "--allow-empty", "-m", "detached");

        var repository = await checkout.ReadAsync();
        var inTree = await checkout.ReadAsync(tree, GitCheckoutScope.Worktree);

        Assert.AreEqual("main", repository!.Branch);
        Assert.AreEqual((0, 1), (repository.DetachedCommits, repository.OtherWorktreeDetachedCommits));
        Assert.IsTrue(repository.HasLocalWork);
        Assert.AreEqual(1, inTree!.DetachedCommits);
    }

    [TestMethod]
    public async Task ASubmoduleRepositoryNotCheckedOut_IsLocalWork()
    {
        using var checkout = Checkout.Create();
        checkout.AddSubmodule("lib");
        checkout.Git("push", "-q");
        var checkedOut = await checkout.ReadAsync();
        checkout.Git("submodule", "deinit", "-q", "-f", "lib");

        var state = await checkout.ReadAsync();

        Assert.AreEqual(0, checkedOut!.UnexaminedSubmoduleRepositories);
        Assert.IsFalse(checkedOut.HasLocalWork, checkedOut.ToString());
        Assert.IsTrue(Directory.Exists(Path.Combine(checkout.Root, ".git", "modules", "lib")), "deinit keeps the repository of the submodule");
        Assert.AreEqual(1, state!.UnexaminedSubmoduleRepositories);
        Assert.IsTrue(state.HasLocalWork);
    }

    [TestMethod]
    public async Task ARemoteInsideTheCheckout_IsNotARemote()
    {
        using var checkout = Checkout.Create(remote: false);
        checkout.Write(".gitignore", "inner.git/\n");
        checkout.Commit("ignore");
        checkout.Git("init", "-q", "--bare", "-b", "main", checkout.At("inner.git"));
        checkout.Git("remote", "add", "origin", checkout.At("inner.git"));
        checkout.Git("remote", "add", "relative", "./inner.git");
        checkout.Git("remote", "add", "url", new Uri(checkout.At("inner.git")).AbsoluteUri);
        checkout.Git("push", "-q", "-u", "origin", "main");

        var state = await checkout.ReadAsync();

        Assert.AreEqual(0, state!.UnpushedBranches.Count, "its remote-tracking refs hold the branch");
        Assert.IsFalse(state.HasRemote);
        Assert.IsTrue(state.HasLocalWork);
    }

    [TestMethod]
    public async Task AnOperationInProgress_IsLocalWork()
    {
        using var checkout = Checkout.Create();
        checkout.Git("bisect", "start");
        var bisect = await checkout.ReadAsync();
        checkout.Git("bisect", "reset");
        checkout.Git("switch", "-q", "-c", "other");
        checkout.Write("readme.md", "other");
        checkout.Commit("other");
        checkout.Git("push", "-q", "-u", "origin", "other");
        checkout.Git("switch", "-q", "main");
        checkout.Git("merge", "--no-ff", "--no-commit", "other");
        checkout.Git("reset", "-q", "--", "readme.md");
        checkout.Git("checkout", "--", "readme.md");

        var merge = await checkout.ReadAsync(scope: GitCheckoutScope.Worktree);

        Assert.AreEqual("bisect", bisect!.OperationInProgress);
        Assert.IsTrue(bisect.HasLocalWork);
        Assert.AreEqual(0, merge!.ChangedFiles, "nothing of the merge is left in the files");
        Assert.AreEqual("merge", merge.OperationInProgress);
        Assert.IsTrue(merge.HasLocalWork);
    }

    [TestMethod]
    public async Task ASubmodulePath_NeedsAGitlinkAndNoLink()
    {
        using var checkout = Checkout.Create();
        checkout.AddSubmodule("lib");
        File.WriteAllText(Path.Combine(checkout.Root, "lib", ".gitignore"), "*.log\n");
        File.WriteAllText(Path.Combine(checkout.Root, "lib", "trace.log"), "12345");
        // A repository .gitmodules names, without a gitlink in the index.
        var nested = Directory.CreateDirectory(checkout.At("nested")).FullName;
        checkout.GitIn(nested, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(nested, ".gitignore"), "*.log\n");
        File.WriteAllText(Path.Combine(nested, "nested.log"), "123");
        try
        {
            // A gitlink below a link to the checkout itself: the submodule again, through another path.
            Directory.CreateSymbolicLink(checkout.At("via"), checkout.Root);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Assert.Inconclusive("Symbolic links cannot be created here: " + exception.Message);
        }

        var commit = checkout.GitIn(checkout.At("lib"), "rev-parse", "HEAD").Trim();
        checkout.Git("update-index", "--add", "--cacheinfo", "160000," + commit + ",via/lib");
        File.AppendAllText(checkout.At(".gitmodules"), "[submodule \"nested\"]\n\tpath = nested\n\turl = ./nested\n[submodule \"via\"]\n\tpath = via/lib\n\turl = ./lib\n");

        var usage = await checkout.ScanAsync();
        var state = await checkout.ReadAsync();

        Assert.IsTrue(usage.GitKnown);
        CollectionAssert.AreEqual(new[] { "lib/trace.log" }, usage.IgnoredEntries.Select(entry => entry.Path).ToArray());
        CollectionAssert.AreEqual(new[] { "lib" }, state!.SubmodulesWithLocalWork.ToArray());
    }

    [TestMethod]
    public void TheWalk_DoesNotEnterAFolderThatBecameALink()
    {
        var root = Directory.CreateTempSubdirectory("codealta-folder-link-");
        try
        {
            var tree = Directory.CreateDirectory(Path.Combine(root.FullName, "tree")).FullName;
            File.WriteAllBytes(Path.Combine(Directory.CreateDirectory(Path.Combine(tree, "victim")).FullName, "small.bin"), new byte[5]);
            var outside = Directory.CreateDirectory(Path.Combine(root.FullName, "outside")).FullName;
            File.WriteAllBytes(Path.Combine(outside, "large.bin"), new byte[100_000]);
            var victim = Path.Combine(tree, "victim");

            // Listed as a folder by its parent, then replaced by a link before the walk takes it.
            var usage = FolderUsage.Measure(tree, null, null, null, folder =>
            {
                if (folder != victim) return;
                Directory.Delete(victim, recursive: true);
                try
                {
                    Directory.CreateSymbolicLink(victim, outside);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    Assert.Inconclusive("Symbolic links cannot be created here: " + exception.Message);
                }
            }, CancellationToken.None);

            Assert.AreEqual(FolderUsage.Empty, usage);
        }
        finally
        {
            var link = Path.Combine(root.FullName, "tree", "victim");
            if (new DirectoryInfo(link).LinkTarget is not null) Directory.Delete(link);
            root.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task TheGitFolderInAnotherCase_IsCountedOnce()
    {
        using var checkout = Checkout.Create();
        if (!File.Exists(checkout.At("README.MD"))) Assert.Inconclusive("The file system tells case apart: one name is one folder only.");
        Directory.Move(Path.Combine(checkout.Root, ".git"), Path.Combine(checkout.Root, ".tmp-git"));
        Directory.Move(Path.Combine(checkout.Root, ".tmp-git"), Path.Combine(checkout.Root, ".GIT"));

        var usage = await checkout.ScanAsync();

        Assert.IsTrue(usage.GitBytes > 0);
        Assert.AreEqual(FolderUsage.Measure(checkout.Root, CancellationToken.None).Bytes, usage.TotalBytes);
    }

    [TestMethod]
    public async Task APathNamedTwice_IsMeasuredOnce()
    {
        using var checkout = Checkout.Create();
        checkout.AddSubmodule("lib");
        File.WriteAllText(Path.Combine(checkout.Root, "lib", ".gitignore"), "*.log\n");
        File.WriteAllText(Path.Combine(checkout.Root, "lib", "trace.log"), "12345");
        File.AppendAllText(checkout.At(".gitmodules"), "[submodule \"again\"]\n\tpath = lib/\n\turl = ./lib\n");
        checkout.Write(".gitignore", "*.tmp\n");
        checkout.Write("a.tmp", "abc");
        // Git does not name an entry twice; a runner that does must not count it twice.
        GitRunner twice = async (folder, arguments, timeout, token) =>
        {
            var run = await checkout.RunAsync(folder, arguments, timeout, token);
            return arguments[0] == "status" ? run with { Output = run.Output + run.Output } : run;
        };

        var usage = await new CheckoutUsageScanner(twice).ScanAsync(checkout.Root, CancellationToken.None);
        var state = await checkout.ReadAsync();

        CollectionAssert.AreEqual(new[] { "a.tmp", "lib/trace.log" }, usage.IgnoredEntries.Select(entry => entry.Path).Order(StringComparer.Ordinal).ToArray());
        Assert.AreEqual(8L, usage.OtherIgnoredBytes);
        Assert.AreEqual(FolderUsage.Measure(checkout.Root, CancellationToken.None).Bytes, usage.TotalBytes);
        CollectionAssert.AreEqual(new[] { "lib" }, state!.SubmodulesWithLocalWork.ToArray());
    }

    [TestMethod]
    public async Task ACommitTimeADateCannotHold_HasNoTime()
    {
        using var checkout = Checkout.Create();
        GitRunner far = async (folder, arguments, timeout, token) => arguments[0] == "log"
            ? new GitRun(0, "99999999999999\n", string.Empty)
            : await checkout.RunAsync(folder, arguments, timeout, token);

        var state = await new GitCheckoutStateReader(far).ReadAsync(checkout.Root, GitCheckoutScope.Repository, CancellationToken.None);

        Assert.IsNotNull(state);
        Assert.IsNull(state.LastCommit);
        Assert.IsFalse(state.HasLocalWork);
    }

    [TestMethod]
    public async Task SumsPastTheRangeOfALong_AreSkippedNotWrapped()
    {
        using var checkout = Checkout.Create();
        checkout.Write(".gitignore", "*.tmp\n");
        checkout.Commit("ignore");
        checkout.Git("push", "-q");
        checkout.Write("a.tmp", "a");
        var scanner = new CheckoutUsageScanner(checkout.RunAsync) { MeasurePart = (_, _, _, _, _) => new FolderUsage(long.MaxValue - 1, 1, 0) };

        var usage = await scanner.ScanAsync(checkout.Root, CancellationToken.None);

        Assert.AreEqual(long.MaxValue - 1, usage.TotalBytes, "the first part is counted, the others would wrap");
        Assert.IsTrue(usage.SkippedEntries >= 2, usage.ToString());
        Assert.AreEqual(CheckoutVerdict.Unknown, CheckoutVerdicts.Of(await checkout.ReadAsync(), usage));
    }

    [TestMethod]
    public async Task Cancellation_StopsAGitReadThatHasNotAnswered()
    {
        using var checkout = Checkout.Create();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        GitRunner hangs = async (folder, arguments, timeout, token) =>
        {
            if (arguments[0] != "status") return await checkout.RunAsync(folder, arguments, timeout, token);
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("not reached");
        };

        foreach (var read in new Func<CancellationToken, Task>[]
                 {
                     token => new CheckoutUsageScanner(hangs).ScanAsync(checkout.Root, token),
                     token => new GitCheckoutStateReader(hangs).ReadAsync(checkout.Root, GitCheckoutScope.Repository, token),
                 })
        {
            started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellation = new CancellationTokenSource();
            var running = read(cancellation.Token);
            await started.Task.WithinAsync(TestTime.Seconds(30));
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => running);
        }
    }

    [TestMethod]
    public async Task MoreBranchesWithUnpushedCommitsThanAreExamined_AreLocalWork()
    {
        using var checkout = Checkout.Create();
        checkout.Git("commit", "-q", "--allow-empty", "-m", "local");
        for (var index = 0; index <= GitCheckoutStateReader.MaximumBranches; index++) checkout.Git("branch", $"b{index:D3}");
        checkout.Git("reset", "-q", "--hard", "origin/main");

        var state = await checkout.ReadAsync();

        Assert.AreEqual(GitCheckoutStateReader.MaximumBranches, state!.UnpushedBranches.Count);
        Assert.IsTrue(state.UnpushedBranchesTruncated);
        Assert.IsTrue(state.HasLocalWork);
    }

    [TestMethod]
    public async Task MoreBranchesThanAreExamined_NoneUnpushedAmongTheExamined_SayHowManyWereNotExamined()
    {
        using var checkout = Checkout.Create();
        // The examined ones all on the remote; a branch with a commit of its own sorts after them.
        for (var index = 0; index < GitCheckoutStateReader.MaximumBranches; index++) checkout.Git("branch", $"a{index:D3}");
        checkout.Git("switch", "-q", "-c", "z-local");
        checkout.Git("commit", "-q", "--allow-empty", "-m", "local");
        checkout.Git("switch", "-q", "main");

        var state = await checkout.ReadAsync();

        Assert.AreEqual((0, true), (state!.UnpushedBranches.Count, state.UnpushedBranchesTruncated));
        // main and z-local were not examined.
        Assert.AreEqual(2, state.UnexaminedBranches);
        Assert.IsTrue(state.HasLocalWork);
    }

    [TestMethod]
    public async Task TheDetachedHeadOfAnotherWorktree_IsCountedApartFromTheOwnOne()
    {
        using var checkout = Checkout.Create();
        var tree = Path.Combine(checkout.Home, "tree");
        checkout.Git("worktree", "add", "-q", "--detach", tree);
        checkout.GitIn(tree, "commit", "-q", "--allow-empty", "-m", "on no branch");

        var state = await checkout.ReadAsync();

        Assert.AreEqual(("main", 0, 1), (state!.Branch, state.DetachedCommits, state.OtherWorktreeDetachedCommits));
        Assert.IsTrue(state.HasLocalWork);
        Assert.AreEqual(0, (await checkout.ReadAsync(tree, GitCheckoutScope.Worktree))!.OtherWorktreeDetachedCommits);
    }

    [TestMethod]
    public async Task RefsOutsideBranchesTagsAndRemotes_AreLocalWorkOfTheRepository()
    {
        using var checkout = Checkout.Create();
        var clean = await checkout.ReadAsync();
        checkout.Git("notes", "add", "-m", "a note", "HEAD");
        // A copy of the remote that git maintenance keeps is no local work.
        checkout.Git("update-ref", "refs/prefetch/remotes/origin/main", "HEAD");

        var state = await checkout.ReadAsync();
        var inTree = await checkout.ReadAsync(scope: GitCheckoutScope.Worktree);

        Assert.AreEqual((0, false), (clean!.OtherRefs, clean.HasLocalWork));
        Assert.AreEqual(1, state!.OtherRefs);
        CollectionAssert.AreEqual(new[] { "refs/notes/commits" }, state.OtherRefNames.ToArray());
        Assert.IsTrue(state.HasLocalWork);
        Assert.AreEqual(0, inTree!.OtherRefs);
    }

    [TestMethod]
    public async Task MoreIgnoredEntriesThanAreListed_AreAllInTheSums()
    {
        using var checkout = Checkout.Create();
        checkout.Write(".gitignore", "*.log\n");
        // A tracked file keeps git from naming the folder once.
        checkout.Write("logs/keep.txt", "kept");
        checkout.Commit("ignore");
        checkout.Git("push", "-q");
        var count = CheckoutUsageScanner.MaximumIgnoredEntries + 1;
        for (var index = 0; index < count; index++) checkout.Write($"logs/{index:D3}.log", new string('l', index == 7 ? 50 : 2));

        var usage = await checkout.ScanAsync();

        Assert.AreEqual(CheckoutUsageScanner.MaximumIgnoredEntries, usage.IgnoredEntries.Count);
        Assert.IsTrue(usage.IgnoredEntriesTruncated);
        Assert.AreEqual(new IgnoredEntry("logs/007.log", false, 50, false), Bare(usage.IgnoredEntries[0]), "the largest first");
        Assert.AreEqual(count, usage.OtherIgnoredCount);
        Assert.AreEqual(50L + 2 * (count - 1), usage.OtherIgnoredBytes);
    }

    [TestMethod]
    public async Task FoldersMeasuredOnTheirOwn_AreLeftOutOfTheTotals_AndOfTheIgnoredEntries()
    {
        using var checkout = Checkout.Create();
        checkout.Write(".gitignore", "bin/\ntools/\n");
        checkout.Commit("ignore");
        checkout.Git("push", "-q");
        checkout.Write("bin/a.dll", new string('a', 100));
        // A worktree inside the project, where CodeAlta places them, with the ignore file it writes beside them.
        checkout.Write(".alta/worktrees/.gitignore", "*\n");
        checkout.Git("worktree", "add", "-q", "-b", "inside", checkout.At(".alta/worktrees/w"));
        // A folder that is a project of its own, inside an ignored folder that holds nothing else.
        checkout.Write("tools/other/notes.txt", new string('n', 30));
        var excluded = new[] { checkout.At(".alta/worktrees/w"), checkout.At("tools/other") };
        var whole = FolderUsage.Measure(checkout.Root, CancellationToken.None).Bytes;
        var worktree = FolderUsage.Measure(checkout.At(".alta/worktrees/w"), CancellationToken.None).Bytes;

        var usage = await new CheckoutUsageScanner(checkout.RunAsync).ScanAsync(checkout.Root, excluded, null, CancellationToken.None);

        Assert.AreEqual(whole - worktree - 30, usage.TotalBytes);
        CollectionAssert.AreEqual(new[] { "bin" }, usage.IgnoredEntries.Select(static entry => entry.Path).ToArray(),
            "neither the worktree nor the ignore file written beside it is an ignored file of the project");
        Assert.AreEqual((100L, 0L, 0L), (usage.BuildOutputBytes, usage.OtherIgnoredBytes, usage.OtherIgnoredCount));
        CollectionAssert.AreEqual(excluded, usage.ExcludedFolders.ToArray());
        Assert.AreEqual(CheckoutVerdict.NothingLocal, CheckoutVerdicts.Of(await checkout.ReadAsync(), usage));
    }

    // An entry without the figures of its walk that a test cannot know in advance: its files and the time of its last write.
    internal static IgnoredEntry Bare(IgnoredEntry entry) => entry with { Files = 0, LastWrite = null };

    internal static IgnoredEntry[] Bare(IEnumerable<IgnoredEntry> entries) => [.. entries.Select(Bare)];

    private static (long Bytes, long Files, long Skipped) Figures(FolderUsage usage) => (usage.Bytes, usage.Files, usage.Skipped);

    private sealed class Reports(Action<CheckoutUsageProgress> report) : IProgress<CheckoutUsageProgress>
    {
        public void Report(CheckoutUsageProgress value) => report(value);
    }
}
