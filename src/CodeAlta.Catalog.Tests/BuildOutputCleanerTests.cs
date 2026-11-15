using CodeAlta.Catalog.Storage;
using CodeAlta.Catalog.Worktrees;

namespace CodeAlta.Catalog.Tests;

/// <summary>
/// The clean of the build output of checkouts, on real repositories made with the git on the path. Inconclusive on a
/// machine without git.
/// </summary>
[TestClass]
public sealed class BuildOutputCleanerTests
{
    [TestMethod]
    public async Task OnlyTheIgnoredBuildFoldersGo_WithTheBytesTheyHeld()
    {
        using var checkout = Checkout.Create();
        checkout.Write(".gitignore", "bin/\nobj/\n.env\n*.log\n");
        checkout.Write("src/app.cs", "class App;");
        checkout.Commit("ignore");
        checkout.Write("bin/Debug/a.dll", new string('a', 100));
        checkout.Write("src/obj/cache/b", new string('b', 50));
        checkout.Write(".env", "SECRET=1");
        checkout.Write("app.log", "12345");
        var planned = BuildOutputCleaner.Planned(await checkout.ScanAsync());

        var cleanup = await CleanPlannedAsync(checkout);
        var again = await CleanPlannedAsync(checkout);

        CollectionAssert.AreEqual(new[] { "bin", "src/obj" }, planned.Select(entry => entry.Path).ToArray());
        Assert.AreEqual(BuildOutputCleaner.Ok, cleanup.Status, cleanup.Message);
        CollectionAssert.AreEqual(new[] { new CleanedEntry("bin", CleanOutcome.Removed, 100, 100), new CleanedEntry("src/obj", CleanOutcome.Removed, 50, 50) },
            cleanup.Entries.ToArray());
        Assert.AreEqual(150L, cleanup.BytesFreed);
        Assert.IsFalse(cleanup.MoreRemain);
        Assert.IsFalse(Directory.Exists(checkout.At("bin")));
        Assert.IsFalse(Directory.Exists(checkout.At("src/obj")));
        foreach (var kept in new[] { ".env", "app.log", "src/app.cs", "readme.md", ".gitignore" }) Assert.IsTrue(File.Exists(checkout.At(kept)), kept);
        Assert.AreEqual(BuildOutputCleaner.Ok, again.Status);
        Assert.AreEqual(0, again.Entries.Count, "a second clean finds nothing");
        Assert.AreEqual(0L, again.BytesFreed);
    }

    [TestMethod]
    public async Task OnlyTheConfirmedPathsThatAreStillPlanned_Go()
    {
        using var checkout = Checkout.Create();
        checkout.Write(".gitignore", "bin/\nobj/\n.env\n");
        checkout.Commit("ignore");
        checkout.Write("bin/a.dll", "aaa");
        checkout.Write("obj/b", "bb");
        checkout.Write(".env", "SECRET=1");

        var cleanup = await Cleaner(checkout).CleanAsync(await checkout.ScanAsync(), ["bin", "readme.md", ".env", "gone", "bin/"], Now(await checkout.ScanAsync()), CancellationToken.None);

        Assert.AreEqual(BuildOutputCleaner.Ok, cleanup.Status, cleanup.Message);
        CollectionAssert.AreEqual(new[]
        {
            new CleanedEntry("bin", CleanOutcome.Removed, 3, 3),
            new CleanedEntry("readme.md", CleanOutcome.KeptNotBuildOutput, 0, 0),
            new CleanedEntry(".env", CleanOutcome.KeptNotBuildOutput, 8, 0),
            new CleanedEntry("gone", CleanOutcome.KeptNotBuildOutput, 0, 0),
        }, cleanup.Entries.ToArray(), "a path named twice is answered once; one planned and not confirmed is not answered");
        Assert.IsTrue(File.Exists(checkout.At("obj/b")), "planned, but not confirmed");
        Assert.IsTrue(File.Exists(checkout.At(".env")));
    }

    [TestMethod]
    public async Task AFolderBelowABuildFolder_IsNotBuildOutput()
    {
        using var checkout = Checkout.Create();
        checkout.Write(".gitignore", "secrets/\nprivate/\nDebug/\n");
        checkout.Write("build/ci.sh", "make");
        checkout.Write("bin/tool.sh", "run");
        checkout.Commit("tracked build folders");
        checkout.Write("build/secrets/key.pem", "key");
        checkout.Write("bin/private/notes.txt", "notes");
        checkout.Write("bin/Debug/a.dll", "a");

        var usage = await checkout.ScanAsync();
        var cleanup = await Cleaner(checkout).CleanAsync(await checkout.ScanAsync(), ["build/secrets", "bin/private", "bin/Debug"], Now(await checkout.ScanAsync()), CancellationToken.None);

        Assert.AreEqual(0, BuildOutputCleaner.Planned(usage).Count);
        Assert.IsTrue(usage.IgnoredEntries.All(entry => !entry.BuildOutput), "only the name of the folder itself counts");
        Assert.IsTrue(cleanup.Entries.All(entry => entry.Outcome == CleanOutcome.KeptNotBuildOutput));
        foreach (var kept in new[] { "build/secrets/key.pem", "bin/private/notes.txt", "bin/Debug/a.dll" }) Assert.IsTrue(File.Exists(checkout.At(kept)), kept);
    }

    [TestMethod]
    public async Task ASubmoduleBelowABuildFolder_KeepsWhatItIgnores()
    {
        using var checkout = Checkout.Create();
        checkout.AddSubmodule("build/toolchain");
        File.WriteAllText(checkout.At("build/toolchain/.gitignore"), "private/\n");
        checkout.Write("build/toolchain/private/token", "token");

        var usage = await checkout.ScanAsync();
        var cleanup = await Cleaner(checkout).CleanAsync(await checkout.ScanAsync(), ["build/toolchain/private"], Now(await checkout.ScanAsync()), CancellationToken.None);

        var entry = usage.IgnoredEntries.Single();
        Assert.AreEqual(("build/toolchain/private", "build/toolchain", false), (entry.Path, entry.Repository, entry.BuildOutput));
        Assert.AreEqual(CleanOutcome.KeptNotBuildOutput, cleanup.Entries.Single().Outcome);
        Assert.IsTrue(File.Exists(checkout.At("build/toolchain/private/token")));
    }

    [TestMethod]
    public async Task AFolderIgnoredOnlyByTheUser_IsNotBuildOutput()
    {
        using var checkout = Checkout.Create();
        checkout.Write(".gitignore", "bin/\n");
        checkout.Commit("ignore");
        File.AppendAllText(Path.Combine(checkout.Root, ".git", "info", "exclude"), "dist/\n");
        Directory.CreateDirectory(Path.GetDirectoryName(checkout.UserIgnoreFile)!);
        File.WriteAllText(checkout.UserIgnoreFile, "target/\n.DS_Store\n");
        checkout.Write("bin/a.dll", "a");
        checkout.Write("dist/mine.txt", "mine");
        checkout.Write("target/mine.txt", "mine");
        checkout.Write(".DS_Store", "finder");

        var usage = await checkout.ScanAsync();
        var cleanup = await Cleaner(checkout).CleanAsync(await checkout.ScanAsync(), ["bin", "dist", "target"], Now(await checkout.ScanAsync()), CancellationToken.None);

        var build = usage.IgnoredEntries.ToDictionary(entry => entry.Path, entry => entry.BuildOutput);
        Assert.IsTrue(build["bin"]);
        Assert.IsFalse(build["dist"], ".git/info/exclude is the user's own");
        Assert.IsFalse(build["target"], "core.excludesFile is the user's own");
        Assert.IsTrue(build[".DS_Store"], "a file of the system counts whatever ignores it");
        CollectionAssert.AreEqual(new[] { CleanOutcome.Removed, CleanOutcome.KeptNotBuildOutput, CleanOutcome.KeptNotBuildOutput },
            cleanup.Entries.Select(entry => entry.Outcome).ToArray());
        Assert.IsTrue(File.Exists(checkout.At("dist/mine.txt")));
        Assert.IsTrue(File.Exists(checkout.At("target/mine.txt")));
    }

    [TestMethod]
    public async Task TheVisualStudioFolder_IsNotBuildOutput()
    {
        using var checkout = Checkout.Create();
        checkout.Write(".gitignore", ".vs/\n");
        checkout.Commit("ignore");
        checkout.Write(".vs/launch.vs.json", "{}");

        var cleanup = await Cleaner(checkout).CleanAsync(await checkout.ScanAsync(), [".vs"], Now(await checkout.ScanAsync()), CancellationToken.None);

        Assert.AreEqual(CleanOutcome.KeptNotBuildOutput, cleanup.Entries.Single().Outcome);
        Assert.IsTrue(File.Exists(checkout.At(".vs/launch.vs.json")));
    }

    [TestMethod]
    public async Task TheGitDirectoryOfTheCheckoutInABuildFolder_IsKept()
    {
        using var checkout = Checkout.Create(init: false);
        Directory.CreateDirectory(checkout.At("build"));
        try
        {
            checkout.GitIn(checkout.Root, "init", "-q", "-b", "main", "--separate-git-dir", checkout.At("build/admin"));
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Assert.Inconclusive("git is not installed.");
        }

        checkout.Configure(checkout.Root);
        checkout.Write(".gitignore", "build/\n");
        checkout.Commit("ignore");

        var usage = await checkout.ScanAsync();
        var cleanup = await Cleaner(checkout).CleanAsync(await checkout.ScanAsync(), ["build"], Now(await checkout.ScanAsync()), CancellationToken.None);

        var entry = usage.IgnoredEntries.Single();
        Assert.AreEqual(("build", true, false), (entry.Path, entry.HoldsRepository, entry.BuildOutput));
        Assert.AreEqual(CleanOutcome.KeptHoldsRepository, cleanup.Entries.Single().Outcome);
        Assert.AreEqual("main", checkout.Git("branch", "--show-current").Trim(), "the database of the repository is still there");
    }

    [TestMethod]
    public async Task FoldersThatHoldARepository_AreKept()
    {
        using var checkout = Checkout.Create();
        checkout.Write(".gitignore", "dist/\nnode_modules/\nbin/\nobj/\n");
        checkout.Commit("ignore");
        // A bare mirror, an intact nested repository, a worktree whose git directory is gone, and a worktree of the repository.
        checkout.Git("clone", "-q", "--bare", checkout.Root, checkout.At("dist/mirror.git"));
        checkout.Write("node_modules/linked/index.js", "linked");
        checkout.GitIn(checkout.At("node_modules/linked"), "init", "-q");
        checkout.Write("bin/old/.git", "gitdir: " + Path.Combine(checkout.Home, "gone"));
        checkout.Write("bin/old/work.txt", "work");
        checkout.Git("worktree", "add", "-q", "--detach", checkout.At("obj"));

        var usage = await checkout.ScanAsync();
        var cleanup = await Cleaner(checkout).CleanAsync(await checkout.ScanAsync(), ["dist", "node_modules", "bin", "obj"], Now(await checkout.ScanAsync()), CancellationToken.None);

        Assert.AreEqual(0, BuildOutputCleaner.Planned(usage).Count, "never planned");
        Assert.IsTrue(usage.IgnoredEntries.All(entry => entry is { HoldsRepository: true, BuildOutput: false }), string.Join("; ", usage.IgnoredEntries));
        Assert.IsTrue(cleanup.Entries.All(entry => entry.Outcome == CleanOutcome.KeptHoldsRepository));
        foreach (var kept in new[] { "dist/mirror.git/HEAD", "node_modules/linked/index.js", "bin/old/work.txt", "obj/readme.md" })
            Assert.IsTrue(File.Exists(checkout.At(kept)), kept);
    }

    [TestMethod]
    public async Task AnEntry_SaysHowManyFilesItHoldsAndWhenOneWasLastWritten()
    {
        using var checkout = Checkout.Create();
        checkout.Write(".gitignore", "bin/\n");
        checkout.Commit("ignore");
        checkout.Write("bin/a.dll", "a");
        checkout.Write("bin/Debug/b.dll", "b");
        var newest = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(checkout.At("bin/a.dll"), newest.AddDays(-3));
        File.SetLastWriteTimeUtc(checkout.At("bin/Debug/b.dll"), newest);

        var entry = (await checkout.ScanAsync()).IgnoredEntries.Single();

        Assert.AreEqual(2L, entry.Files);
        Assert.AreEqual(new DateTimeOffset(newest), entry.LastWrite);
    }

    [TestMethod]
    public async Task IgnoredFilesBesideATrackedFileInABinFolder_AndAtTheTop_Stay()
    {
        using var checkout = Checkout.Create();
        checkout.Write(".gitignore", "bin/\n.env\n.DS_Store\n");
        checkout.Write("bin/tool.sh", "echo tool");
        checkout.Git("add", "-f", "bin/tool.sh");
        checkout.Commit("tool");
        checkout.Write("bin/.env", "SECRET=1");
        checkout.Write("bin/out.dll", "out");
        checkout.Write(".env", "SECRET=2");
        checkout.Write(".DS_Store", "finder");

        var usage = await checkout.ScanAsync();
        var cleanup = await Cleaner(checkout).CleanAsync(await checkout.ScanAsync(), [.. usage.IgnoredEntries.Select(entry => entry.Path)], Now(await checkout.ScanAsync()), CancellationToken.None);

        Assert.IsTrue(usage.IgnoredEntries.Any(entry => entry is { Path: ".DS_Store", BuildOutput: true }), "a file of the system is build output");
        Assert.AreEqual(0, BuildOutputCleaner.Planned(usage).Count, "only folders are cleaned");
        Assert.AreEqual(BuildOutputCleaner.Ok, cleanup.Status, cleanup.Message);
        Assert.IsTrue(cleanup.Entries.All(entry => entry.Outcome == CleanOutcome.KeptNotBuildOutput));
        foreach (var kept in new[] { "bin/tool.sh", "bin/.env", "bin/out.dll", ".env", ".DS_Store" }) Assert.IsTrue(File.Exists(checkout.At(kept)), kept);
    }

    [TestMethod]
    public async Task AnUntrackedBuildFolderThatIsNotIgnored_Stays()
    {
        using var checkout = Checkout.Create();
        checkout.Write("build/notes.txt", "mine");

        var cleanup = await Cleaner(checkout).CleanAsync(await checkout.ScanAsync(), ["build"], Now(await checkout.ScanAsync()), CancellationToken.None);

        Assert.AreEqual(BuildOutputCleaner.Ok, cleanup.Status);
        Assert.AreEqual(CleanOutcome.KeptNotBuildOutput, cleanup.Entries.Single().Outcome);
        Assert.IsTrue(File.Exists(checkout.At("build/notes.txt")));
    }

    [TestMethod]
    public async Task AFileTheIgnoreFileTriesToKeepInABuildFolder_Goes()
    {
        using var checkout = Checkout.Create();
        // Git cannot include again a file whose folder is excluded: `!build/keep.txt` does nothing, and the file goes with the folder.
        checkout.Write(".gitignore", "build/\n!build/keep.txt\n");
        checkout.Commit("ignore");
        checkout.Write("build/keep.txt", "kept?");
        checkout.Write("build/out.o", "o");

        var cleanup = await CleanPlannedAsync(checkout);

        Assert.AreEqual(new CleanedEntry("build", CleanOutcome.Removed, 6, 6), cleanup.Entries.Single());
        Assert.IsFalse(File.Exists(checkout.At("build/keep.txt")));
    }

    [TestMethod]
    public async Task ALinkInABuildFolder_Goes_WithoutWhatItPointsTo()
    {
        using var checkout = Checkout.Create();
        checkout.Write(".gitignore", "obj/\n");
        checkout.Commit("ignore");
        checkout.Write("obj/o.txt", "o");
        var outside = Directory.CreateDirectory(Path.Combine(checkout.Home, "outside")).FullName;
        File.WriteAllBytes(Path.Combine(outside, "large.bin"), new byte[10_000]);
        try
        {
            Directory.CreateSymbolicLink(checkout.At("obj/link"), outside);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Assert.Inconclusive("Symbolic links cannot be created here: " + exception.Message);
        }

        var cleanup = await CleanPlannedAsync(checkout);

        Assert.AreEqual(BuildOutputCleaner.Ok, cleanup.Status, cleanup.Message);
        Assert.AreEqual(new CleanedEntry("obj", CleanOutcome.Removed, 1, 1), cleanup.Entries.Single());
        Assert.IsFalse(Path.Exists(checkout.At("obj")));
        Assert.IsTrue(File.Exists(Path.Combine(outside, "large.bin")));
    }

    [TestMethod]
    public async Task ABuildFolderOfASubmodule_IsCleanedInTheSubmodule()
    {
        using var checkout = Checkout.Create();
        checkout.Write(".gitignore", "dist/\n");
        checkout.Commit("ignore");
        checkout.AddSubmodule("lib");
        File.WriteAllText(checkout.At("lib/.gitignore"), "bin/\n");
        checkout.Write("lib/bin/lib.dll", new string('l', 7));
        // The same name in the checkout, which it does not ignore.
        checkout.Write("bin/mine.txt", "mine");
        checkout.Write("dist/app.js", new string('o', 3));
        var commands = new List<(string Folder, string Arguments)>();
        var cleaner = new BuildOutputCleaner(async (folder, arguments, timeout, token) =>
        {
            if (arguments.Contains("clean")) commands.Add((folder, string.Join(' ', arguments)));
            return await checkout.RunAsync(folder, arguments, timeout, token);
        });

        var cleanup = await cleaner.CleanAsync(await checkout.ScanAsync(), ["lib/bin", "dist", "bin"], Now(await checkout.ScanAsync()), CancellationToken.None);

        Assert.AreEqual(BuildOutputCleaner.Ok, cleanup.Status, cleanup.Message);
        CollectionAssert.AreEqual(new[]
        {
            new CleanedEntry("lib/bin", CleanOutcome.Removed, 7, 7), new CleanedEntry("dist", CleanOutcome.Removed, 3, 3),
            new CleanedEntry("bin", CleanOutcome.KeptNotBuildOutput, 0, 0),
        }, cleanup.Entries.ToArray());
        var gitDirectory = Path.Combine(checkout.Root, ".git");
        CollectionAssert.AreEquivalent(new[]
        {
            (checkout.At("lib"), $"--git-dir={Path.Combine(gitDirectory, "modules", "lib")} --work-tree={checkout.At("lib")} clean -f -d -X -- :(top,literal)bin"),
            (checkout.Root, $"--git-dir={gitDirectory} --work-tree={checkout.Root} clean -f -d -X -- :(top,literal)dist"),
        }, commands);
        Assert.IsTrue(File.Exists(checkout.At("bin/mine.txt")));
        Assert.IsTrue(File.Exists(checkout.At("lib/lib.txt")));
    }

    [TestMethod]
    public async Task ABuildFolderOfANestedSubmodule_IsCleanedInIt()
    {
        using var checkout = Checkout.Create();
        var inner = checkout.CreateSource("a-b");
        var outer = checkout.CreateSource("a");
        checkout.GitIn(outer, "submodule", "add", "-q", inner.Replace('\\', '/'), "b");
        checkout.GitIn(outer, "commit", "-q", "-m", "b");
        checkout.Git("submodule", "add", "-q", outer.Replace('\\', '/'), "a");
        checkout.Git("submodule", "update", "-q", "--init", "--recursive");
        checkout.Commit("a");
        File.WriteAllText(checkout.At("a/b/.gitignore"), "bin/\n");
        checkout.Write("a/b/bin/b.dll", "bb");

        var cleanup = await CleanPlannedAsync(checkout);

        Assert.AreEqual(new CleanedEntry("a/b/bin", CleanOutcome.Removed, 2, 2), cleanup.Entries.Single());
        Assert.IsTrue(File.Exists(checkout.At("a/b/a-b.txt")));
    }

    [TestMethod]
    public async Task NamesThatLookLikeMagicOrOptions_AreCleaned_AndNothingElse()
    {
        using var checkout = Checkout.Create();
        checkout.Write(".gitignore", "bin\n");
        // Names a pattern, the magic of a pathspec or an option would read otherwise; `*` and `:` are not names on Windows.
        string[] names = OperatingSystem.IsWindows() ? [" sp", "[ab]", "-d", "-f"] : [" sp", "[ab]", "*s", ":c", "-d", ":(glob)y"];
        foreach (var name in names) checkout.Write(name + "/keep.txt", "kept");
        checkout.Git(["add", "--", .. names.Select(name => ":(literal)" + name + "/keep.txt")]);
        checkout.Commit("folders");
        foreach (var name in names) checkout.Write(name + "/bin/z.dll", "zz");
        // Ignored files that a glob of those names would match: `[ab]/bin` matches a/bin, `*s/bin` abs/bin, `:(glob)y/bin` y/bin.
        string[] decoys = ["a/bin", "abs/bin", "y/bin"];
        foreach (var decoy in decoys) checkout.Write(decoy, "decoy");

        var cleanup = await CleanPlannedAsync(checkout);

        Assert.AreEqual(BuildOutputCleaner.Ok, cleanup.Status, cleanup.Message);
        CollectionAssert.AreEquivalent(names.Select(name => name + "/bin").ToArray(), cleanup.Entries.Select(entry => entry.Path).ToArray());
        Assert.IsTrue(cleanup.Entries.All(entry => entry.Outcome == CleanOutcome.Removed));
        Assert.AreEqual(2L * names.Length, cleanup.BytesFreed);
        foreach (var name in names)
        {
            Assert.IsFalse(Directory.Exists(checkout.At(name + "/bin")), name);
            Assert.IsTrue(File.Exists(checkout.At(name + "/keep.txt")), name);
        }

        foreach (var decoy in decoys) Assert.IsTrue(File.Exists(checkout.At(decoy)), decoy);
    }

    [TestMethod]
    public async Task MoreFoldersThanOneRunOfGit_AreAllCleaned()
    {
        using var checkout = ManyBuildFolders(150);
        var runs = 0;
        var cleaner = new BuildOutputCleaner(async (folder, arguments, timeout, token) =>
        {
            if (arguments.Contains("clean")) runs++;
            return await checkout.RunAsync(folder, arguments, timeout, token);
        });

        var cleanup = await CleanPlannedAsync(checkout, cleaner);

        Assert.AreEqual(BuildOutputCleaner.Ok, cleanup.Status, cleanup.Message);
        Assert.AreEqual(150, cleanup.Entries.Count);
        Assert.AreEqual(150L, cleanup.BytesFreed);
        Assert.IsTrue(runs >= 3, $"{runs} runs");
        Assert.AreEqual(0, Directory.EnumerateDirectories(checkout.Root, "bin", SearchOption.AllDirectories).Count());
    }

    [TestMethod]
    public async Task AFileGitCannotRemove_FailsItsRun_AndTheLaterRunsAreNotAttempted()
    {
        if (OperatingSystem.IsWindows()) Assert.Inconclusive("A folder git cannot write to is made with the modes of Unix.");
        using var checkout = ManyBuildFolders(70);
        // The largest comes first, in the first run.
        checkout.Write("p000/bin/locked/z.dll", new string('z', 100));
        var locked = checkout.At("p000/bin/locked");
        SetMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var cleanup = await CleanPlannedAsync(checkout);

            Assert.AreEqual(BuildOutputCleaner.Failed, cleanup.Status);
            StringAssert.Contains(cleanup.Message, "failed to remove");
            var outcomes = cleanup.Entries.ToDictionary(entry => entry.Path, entry => entry);
            Assert.AreEqual(CleanOutcome.PartlyRemoved, outcomes["p000/bin"].Outcome);
            Assert.AreEqual(1L, outcomes["p000/bin"].BytesFreed, "a.dll went, the locked file stayed");
            Assert.AreEqual(64, cleanup.Entries.Count(entry => entry.Outcome is CleanOutcome.Removed or CleanOutcome.PartlyRemoved));
            var notAttempted = cleanup.Entries.Where(entry => entry.Outcome == CleanOutcome.NotAttempted).ToArray();
            Assert.AreEqual(6, notAttempted.Length);
            foreach (var entry in notAttempted) Assert.IsTrue(Directory.Exists(checkout.At(entry.Path)), entry.Path);
        }
        finally
        {
            SetMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [TestMethod]
    public async Task ACancellationBetweenTwoRuns_IsAStatusWithEveryEntry()
    {
        using var checkout = ManyBuildFolders(70);
        using var cancellation = new CancellationTokenSource();
        var cleaner = new BuildOutputCleaner(async (folder, arguments, timeout, token) =>
        {
            var run = await checkout.RunAsync(folder, arguments, timeout, token);
            if (arguments.Contains("clean")) cancellation.Cancel();
            return run;
        });
        var measure = await checkout.ScanAsync();
        var planned = BuildOutputCleaner.Planned(measure).Select(entry => entry.Path).ToArray();

        var cleanup = await cleaner.CleanAsync(measure, planned, Now(measure), cancellation.Token);

        Assert.AreEqual(BuildOutputCleaner.Canceled, cleanup.Status);
        Assert.AreEqual(70, cleanup.Entries.Count);
        Assert.AreEqual(64, cleanup.Entries.Count(entry => entry.Outcome == CleanOutcome.Removed));
        Assert.AreEqual(6, cleanup.Entries.Count(entry => entry.Outcome == CleanOutcome.NotAttempted));
        Assert.AreEqual(64L, cleanup.BytesFreed);
    }

    [TestMethod]
    public async Task AnEntryWhoseFolderCannotBeReadAfterTheClean_IsUnknown()
    {
        if (OperatingSystem.IsWindows()) Assert.Inconclusive("A folder that cannot be read is made with the modes of Unix.");
        using var checkout = Checkout.Create();
        checkout.Write(".gitignore", "obj/\n");
        checkout.Write("src/app.cs", "class App;");
        checkout.Commit("ignore");
        checkout.Write("src/obj/o.bin", "ooo");
        var source = checkout.At("src");
        // Git cannot open the folder: it warns, removes nothing and ends well.
        var cleaner = new BuildOutputCleaner(async (folder, arguments, timeout, token) =>
        {
            if (arguments.Contains("clean")) SetMode(source, UnixFileMode.None);
            return await checkout.RunAsync(folder, arguments, timeout, token);
        });
        try
        {
            var cleanup = await cleaner.CleanAsync(await checkout.ScanAsync(), ["src/obj"], Now(await checkout.ScanAsync()), CancellationToken.None);

            Assert.AreEqual(BuildOutputCleaner.Ok, cleanup.Status, cleanup.Message);
            Assert.AreEqual(new CleanedEntry("src/obj", CleanOutcome.Unknown, 3, 0), cleanup.Entries.Single());
            Assert.AreEqual(0L, cleanup.BytesFreed);
        }
        finally
        {
            SetMode(source, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Assert.IsTrue(File.Exists(checkout.At("src/obj/o.bin")), "nothing was deleted");
    }

    [TestMethod]
    public async Task AWorkTreeMovedByTheConfigurationAfterTheMeasure_IsNotWhereGitCleans()
    {
        using var checkout = Checkout.Create();
        checkout.Write(".gitignore", "bin/\n");
        checkout.Commit("ignore");
        checkout.Write("bin/a.dll", "a");
        var elsewhere = Directory.CreateDirectory(Path.Combine(checkout.Home, "elsewhere")).FullName;
        File.WriteAllText(Path.Combine(elsewhere, ".gitignore"), "bin/\n");
        Directory.CreateDirectory(Path.Combine(elsewhere, "bin"));
        File.WriteAllText(Path.Combine(elsewhere, "bin", "theirs.dll"), "theirs");
        var cleaner = new BuildOutputCleaner(async (folder, arguments, timeout, token) =>
        {
            if (arguments.Contains("clean")) checkout.Git("config", "core.worktree", elsewhere);
            return await checkout.RunAsync(folder, arguments, timeout, token);
        });

        var cleanup = await cleaner.CleanAsync(await checkout.ScanAsync(), ["bin"], Now(await checkout.ScanAsync()), CancellationToken.None);

        Assert.AreEqual(BuildOutputCleaner.Ok, cleanup.Status, cleanup.Message);
        Assert.AreEqual(CleanOutcome.Removed, cleanup.Entries.Single().Outcome);
        Assert.IsTrue(File.Exists(Path.Combine(elsewhere, "bin", "theirs.dll")));
    }

    [TestMethod]
    public async Task ACheckoutReachedThroughALinkThatChanges_IsCleanedWhereItWasMeasured()
    {
        using var checkout = Checkout.Create();
        checkout.Write(".gitignore", "bin/\n");
        checkout.Commit("ignore");
        checkout.Write("bin/a.dll", "a");
        // Another repository with a build folder of its own, where the link points once the clean starts.
        var other = Path.Combine(checkout.Home, "other");
        checkout.Git("clone", "-q", checkout.Root, other);
        Directory.CreateDirectory(Path.Combine(other, "bin"));
        File.WriteAllText(Path.Combine(other, "bin", "theirs.dll"), "theirs");
        var alias = Path.Combine(checkout.Home, "alias");
        try
        {
            Directory.CreateSymbolicLink(alias, checkout.Root);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Assert.Inconclusive("Symbolic links cannot be created here: " + exception.Message);
        }

        var cleaner = new BuildOutputCleaner(async (folder, arguments, timeout, token) =>
        {
            if (arguments.Contains("clean") && new DirectoryInfo(alias).LinkTarget != other)
            {
                Directory.Delete(alias);
                Directory.CreateSymbolicLink(alias, other);
            }

            return await checkout.RunAsync(folder, arguments, timeout, token);
        });

        var cleanup = await cleaner.CleanAsync(await checkout.ScanAsync(alias), ["bin"], Now(await checkout.ScanAsync(alias)), CancellationToken.None);

        Assert.AreEqual(BuildOutputCleaner.Ok, cleanup.Status, cleanup.Message);
        Assert.AreEqual(CleanOutcome.Removed, cleanup.Entries.Single().Outcome);
        Assert.IsFalse(Directory.Exists(checkout.At("bin")));
        Assert.IsTrue(File.Exists(Path.Combine(other, "bin", "theirs.dll")));
    }

    [TestMethod]
    public async Task AGitDirectoryThatChangedSinceTheMeasure_RefusesTheClean()
    {
        using var checkout = Checkout.Create();
        checkout.Write(".gitignore", "bin/\n");
        checkout.Commit("ignore");
        checkout.Write("bin/a.dll", "a");
        var checks = 0;
        var cleaner = new BuildOutputCleaner(async (folder, arguments, timeout, token) =>
        {
            // After the measure, the checkout is pointed at another git directory.
            if (arguments is ["rev-parse", "--absolute-git-dir"] && ++checks == 2)
            {
                var moved = Path.Combine(checkout.Home, "moved.git");
                Directory.Move(Path.Combine(checkout.Root, ".git"), moved);
                File.WriteAllText(Path.Combine(checkout.Root, ".git"), "gitdir: " + moved + "\n");
            }

            if (arguments.Contains("clean")) Assert.Fail("git clean ran after the git directory changed.");
            return await checkout.RunAsync(folder, arguments, timeout, token);
        });

        var cleanup = await cleaner.CleanAsync(await checkout.ScanAsync(), ["bin"], Now(await checkout.ScanAsync()), CancellationToken.None);

        Assert.AreEqual(BuildOutputCleaner.CheckoutChanged, cleanup.Status);
        StringAssert.Contains(cleanup.Message, "changed since it was measured");
        Assert.AreEqual(CleanOutcome.NotAttempted, cleanup.Entries.Single().Outcome);
        Assert.IsTrue(File.Exists(checkout.At("bin/a.dll")));
    }

    [TestMethod]
    public async Task AGitForWindowsBefore2_23_RefusesTheClean()
    {
        using var checkout = Checkout.Create();
        checkout.Write(".gitignore", "bin/\n");
        checkout.Commit("ignore");
        checkout.Write("bin/a.dll", "a");
        var cleaner = new BuildOutputCleaner(async (folder, arguments, timeout, token) => arguments is ["version"]
            ? new GitRun(0, "git version 2.22.0.windows.1\n", string.Empty)
            : await checkout.RunAsync(folder, arguments, timeout, token)) { RequiresJunctionSafeGit = true };

        var cleanup = await cleaner.CleanAsync(await checkout.ScanAsync(), ["bin"], Now(await checkout.ScanAsync()), CancellationToken.None);

        Assert.AreEqual(BuildOutputCleaner.GitTooOld, cleanup.Status);
        StringAssert.Contains(cleanup.Message, "2.22.0");
        Assert.AreEqual(CleanOutcome.NotAttempted, cleanup.Entries.Single().Outcome);
        Assert.IsTrue(File.Exists(checkout.At("bin/a.dll")));
    }

    [TestMethod]
    public void TheVersionOfGit_IsSafeFrom2_23()
    {
        Assert.IsTrue(BuildOutputCleaner.IsJunctionSafe("git version 2.23.0.windows.1\n"));
        Assert.IsTrue(BuildOutputCleaner.IsJunctionSafe("git version 2.54.0 (Apple Git-157)"));
        Assert.IsTrue(BuildOutputCleaner.IsJunctionSafe("git version 3.0.0"));
        Assert.IsFalse(BuildOutputCleaner.IsJunctionSafe("git version 2.22.4.windows.1"));
        Assert.IsFalse(BuildOutputCleaner.IsJunctionSafe("git version 1.99.0"));
        Assert.IsFalse(BuildOutputCleaner.IsJunctionSafe("something else"), "what cannot be read is not safe");
    }

    [TestMethod]
    [DoNotParallelize] // It sets a variable of the process, which every git started meanwhile would inherit.
    public async Task InheritedPathspecVariables_DoNotChangeWhatTheRunnerOfTheApplicationCleans()
    {
        using var checkout = Checkout.Create();
        checkout.Write(".gitignore", "bin/\n");
        checkout.Commit("ignore");
        checkout.Write("bin/a.dll", "a");
        var isolation = checkout.Isolation;
        var cleaner = new BuildOutputCleaner((folder, arguments, timeout, token) => GitWorktreeService.RunGitAsync(folder, arguments, timeout, isolation, token));
        var names = new[] { "GIT_LITERAL_PATHSPECS", "GIT_GLOB_PATHSPECS", "GIT_NOGLOB_PATHSPECS", "GIT_ICASE_PATHSPECS" };
        try
        {
            // With GIT_LITERAL_PATHSPECS, `:(top,literal)bin` names a file of that name: git ends well and removes nothing.
            Environment.SetEnvironmentVariable("GIT_LITERAL_PATHSPECS", "1");
            var cleanup = await cleaner.CleanAsync(await checkout.ScanAsync(), ["bin"], Now(await checkout.ScanAsync()), CancellationToken.None);

            Assert.AreEqual(new CleanedEntry("bin", CleanOutcome.Removed, 1, 1), cleanup.Entries.Single());
        }
        finally
        {
            foreach (var name in names) Environment.SetEnvironmentVariable(name, null);
        }
    }

    [TestMethod]
    public async Task MoreIgnoredEntriesThanTheMeasureLists_AreSaidToRemain()
    {
        using var checkout = Checkout.Create();
        checkout.Write(".gitignore", "*.log\nbin/\n");
        checkout.Write("logs/keep.txt", "kept");
        checkout.Commit("ignore");
        for (var index = 0; index <= CheckoutUsageScanner.MaximumIgnoredEntries; index++) checkout.Write($"logs/{index:D3}.log", "l");
        checkout.Write("bin/a.dll", new string('a', 100));

        var cleanup = await CleanPlannedAsync(checkout);

        Assert.AreEqual(BuildOutputCleaner.Ok, cleanup.Status, cleanup.Message);
        Assert.AreEqual("bin", cleanup.Entries.Single().Path, "the largest are listed");
        Assert.IsTrue(cleanup.MoreRemain);
        Assert.IsTrue(File.Exists(checkout.At("logs/000.log")));
    }

    [TestMethod]
    public async Task AFolderThatIsNotARepository_IsNotCleaned()
    {
        using var checkout = Checkout.Create(init: false);
        checkout.Write("bin/a.dll", "a");
        checkout.Write("node_modules/x/index.js", "x");

        var cleanup = await Cleaner(checkout).CleanAsync(await checkout.ScanAsync(), ["bin", "node_modules"], Now(await checkout.ScanAsync()), CancellationToken.None);

        Assert.AreEqual(BuildOutputCleaner.NotRepository, cleanup.Status);
        Assert.IsTrue(cleanup.Entries.All(entry => entry.Outcome == CleanOutcome.NotAttempted));
        Assert.AreEqual(2, cleanup.Entries.Count);
        Assert.AreEqual(0L, cleanup.BytesFreed);
        Assert.IsTrue(File.Exists(checkout.At("bin/a.dll")));
        Assert.IsTrue(File.Exists(checkout.At("node_modules/x/index.js")));
    }

    [TestMethod]
    public async Task ACancellationBeforeTheStart_DeletesNothing()
    {
        using var checkout = Checkout.Create();
        checkout.Write(".gitignore", "bin/\n");
        checkout.Commit("ignore");
        checkout.Write("bin/a.dll", "a");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        var measure = await checkout.ScanAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => Cleaner(checkout).CleanAsync(measure, ["bin"], Now(measure), canceled.Token));

        Assert.IsTrue(File.Exists(checkout.At("bin/a.dll")));
    }

    [TestMethod]
    public async Task ACancellationAfterTheMeasure_StopsBeforeGitCleans()
    {
        using var checkout = Checkout.Create();
        checkout.Write(".gitignore", "bin/\n");
        checkout.Commit("ignore");
        checkout.Write("bin/a.dll", "a");
        using var cancellation = new CancellationTokenSource();
        var checks = 0;
        var cleaner = new BuildOutputCleaner(async (folder, arguments, timeout, token) =>
        {
            // The first check of the repository is the measure's, the second the cleaner's, after it: the user stops it there.
            if (arguments is ["rev-parse", "--show-cdup"] && ++checks == 2) cancellation.Cancel();
            if (arguments.Contains("clean")) Assert.Fail("git clean ran after the cancellation.");
            return await checkout.RunAsync(folder, arguments, timeout, CancellationToken.None);
        });

        var measure = await checkout.ScanAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => cleaner.CleanAsync(measure, ["bin"], Now(measure), cancellation.Token));

        Assert.AreEqual(2, checks);
        Assert.IsTrue(File.Exists(checkout.At("bin/a.dll")));
    }

    [TestMethod]
    public async Task AFailureOfGit_IsAStatusWithItsMessage()
    {
        using var checkout = Checkout.Create();
        checkout.Write(".gitignore", "bin/\n");
        checkout.Commit("ignore");
        checkout.Write("bin/a.dll", "a");
        var cleaner = new BuildOutputCleaner(async (folder, arguments, timeout, token) => arguments.Contains("clean")
            ? new GitRun(128, string.Empty, "fatal: something\n\u001b[31mbroke\u001b[0m\n" + new string('x', 2000))
            : await checkout.RunAsync(folder, arguments, timeout, token));

        var cleanup = await cleaner.CleanAsync(await checkout.ScanAsync(), ["bin"], Now(await checkout.ScanAsync()), CancellationToken.None);

        Assert.AreEqual(BuildOutputCleaner.Failed, cleanup.Status);
        StringAssert.StartsWith(cleanup.Message, "fatal: something");
        Assert.IsTrue(cleanup.Message!.Length <= 601, "the message is bounded");
        Assert.IsFalse(cleanup.Message.Contains('\u001b'), "nothing a terminal would interpret");
        Assert.AreEqual(new CleanedEntry("bin", CleanOutcome.PartlyRemoved, 1, 0), cleanup.Entries.Single(), "what git was asked to remove is measured again");
    }

    [TestMethod]
    public void Planned_IsTheBuildFoldersOfTheMeasure_ThatHoldNoRepository()
    {
        IgnoredEntry[] entries =
        [
            new("bin", true, 100, true), new(".env", false, 8, false), new(".DS_Store", false, 6, true), new("data", true, 50, false),
            new("lib/obj", true, 20, true) { Repository = "lib" }, new("dist", true, 30, true) { HoldsRepository = true },
        ];
        var usage = new CheckoutUsage("/c", 300, 9, 0, 156, 58, 2, entries, false, 0, true);

        CollectionAssert.AreEqual(new[] { entries[0], entries[4] }, BuildOutputCleaner.Planned(usage).ToArray());
        Assert.AreEqual(0, BuildOutputCleaner.Planned(usage with { GitKnown = false }).Count);
    }

    [TestMethod]
    public async Task AConfirmationOfACheckoutThatWasRepointedSince_CleansNothing()
    {
        using var first = BuildFolderIn(Checkout.Create());
        using var second = BuildFolderIn(Checkout.Create());
        var alias = Path.Combine(first.Home, "project");
        if (!TryLink(alias, first.Root)) return;
        var shown = await first.ScanAsync(alias);
        // The project path now names another checkout, with a build folder of the same name.
        Directory.Delete(alias);
        Directory.CreateSymbolicLink(alias, second.Root);

        var cleanup = await Cleaner(first).CleanAsync(shown, ["bin"], Now(shown), CancellationToken.None);

        Assert.AreEqual("checkout_changed", cleanup.Status);
        Assert.AreEqual(CleanOutcome.NotAttempted, cleanup.Entries.Single().Outcome);
        Assert.IsTrue(File.Exists(second.At("bin/a.dll")));
        Assert.IsTrue(File.Exists(first.At("bin/a.dll")));
    }

    [TestMethod]
    public async Task AConfirmationOfACheckoutWhoseGitDirectoryChangedSince_CleansNothing()
    {
        using var checkout = BuildFolderIn(Checkout.Create());
        var shown = await checkout.ScanAsync();
        var moved = Path.Combine(checkout.Home, "moved.git");
        Directory.Move(Path.Combine(checkout.Root, ".git"), moved);
        File.WriteAllText(Path.Combine(checkout.Root, ".git"), "gitdir: " + moved + "\n");

        var cleanup = await Cleaner(checkout).CleanAsync(shown, ["bin"], Now(shown), CancellationToken.None);

        Assert.AreEqual("checkout_changed", cleanup.Status);
        Assert.IsTrue(File.Exists(checkout.At("bin/a.dll")));
    }

    [TestMethod]
    public async Task APathTheConfirmedMeasureDidNotPlan_IsKept_EvenWhenTheNewMeasurePlansIt()
    {
        using var checkout = Checkout.Create();
        checkout.Write(".gitignore", "bin/\ndist/\n");
        checkout.Commit("ignore");
        checkout.Write("bin/a.dll", "aaa");
        var shown = await checkout.ScanAsync();
        // A build folder that appeared after the measure the user saw.
        checkout.Write("dist/late.js", "late");

        var cleanup = await Cleaner(checkout).CleanAsync(shown, ["bin", "dist"], Now(shown), CancellationToken.None);

        Assert.AreEqual(BuildOutputCleaner.Ok, cleanup.Status, cleanup.Message);
        CollectionAssert.AreEqual(new[] { CleanOutcome.Removed, CleanOutcome.KeptNotBuildOutput }, cleanup.Entries.Select(static entry => entry.Outcome).ToArray());
        Assert.IsTrue(File.Exists(checkout.At("dist/late.js")));
    }

    [TestMethod]
    public async Task AnotherRepositoryCreatedAtTheSamePath_IsNotCleaned()
    {
        using var checkout = BuildFolderIn(Checkout.Create());
        var shown = await checkout.ScanAsync();
        // The same paths, another repository: the old one is kept aside, so the new folders cannot take its identities.
        Directory.Move(checkout.Root, checkout.Root + "-old");
        Directory.CreateDirectory(checkout.Root);
        checkout.GitIn(checkout.Root, "init", "-q", "-b", "main");
        checkout.Configure(checkout.Root);
        BuildFolderIn(checkout);

        var cleanup = await Cleaner(checkout).CleanAsync(shown, ["bin"], Now(shown), CancellationToken.None);

        Assert.AreEqual(BuildOutputCleaner.CheckoutChanged, cleanup.Status, cleanup.Message);
        Assert.AreEqual(CleanOutcome.NotAttempted, cleanup.Entries.Single().Outcome);
        Assert.IsTrue(File.Exists(checkout.At("bin/a.dll")));
    }

    [TestMethod]
    public async Task ASubmoduleReplacedByAnotherClone_IsNotCleaned()
    {
        using var checkout = Checkout.Create();
        checkout.AddSubmodule("lib");
        File.WriteAllText(checkout.At("lib/.gitignore"), "bin/\n");
        checkout.Write("lib/bin/lib.dll", "lib");
        var shown = await checkout.ScanAsync();
        // Another clone in the folder of the submodule, which ignores a build folder of the same name.
        Directory.Move(checkout.At("lib"), checkout.At("lib-old"));
        checkout.GitIn(checkout.Root, "clone", "-q", checkout.CreateSource("other/lib"), "lib");
        checkout.Configure(checkout.At("lib"));
        File.WriteAllText(checkout.At("lib/.gitignore"), "bin/\n");
        checkout.Write("lib/bin/other.dll", "other");

        var cleanup = await Cleaner(checkout).CleanAsync(shown, ["lib/bin"], Now(shown), CancellationToken.None);

        Assert.AreNotEqual(BuildOutputCleaner.Ok, cleanup.Status, cleanup.Message);
        Assert.AreEqual(CleanOutcome.NotAttempted, cleanup.Entries.Single().Outcome);
        Assert.IsTrue(File.Exists(checkout.At("lib/bin/other.dll")));
    }

    [TestMethod]
    public async Task ACheckoutReplacedByALinkRightBeforeGitRuns_IsNotCleaned()
    {
        TestLinks.RequireSymbolicLinks();
        using var checkout = BuildFolderIn(Checkout.Create());
        using var other = BuildFolderIn(Checkout.Create());
        var shown = await checkout.ScanAsync();
        var options = Now(shown) with
        {
            // Asked right before git runs: the folder is replaced by a link to another repository there.
            Guard = () =>
            {
                Directory.Move(checkout.Root, checkout.Root + "-old");
                Directory.CreateSymbolicLink(checkout.Root, other.Root);
                return null;
            },
        };

        var cleanup = await Cleaner(checkout).CleanAsync(shown, ["bin"], options, CancellationToken.None);

        Assert.AreEqual(BuildOutputCleaner.CheckoutChanged, cleanup.Status, cleanup.Message);
        Assert.AreEqual(CleanOutcome.NotAttempted, cleanup.Entries.Single().Outcome);
        Assert.IsTrue(File.Exists(other.At("bin/a.dll")));
    }

    [TestMethod]
    public async Task TheGuardOfTheCaller_AskedBeforeEachRun_StopsTheRunsThatRemain()
    {
        using var checkout = Checkout.Create();
        checkout.Write(".gitignore", "dist/\n");
        checkout.Commit("ignore");
        checkout.AddSubmodule("lib");
        File.WriteAllText(checkout.At("lib/.gitignore"), "bin/\n");
        checkout.Write("lib/bin/lib.dll", "lib");
        checkout.Write("dist/app.js", "app");
        var shown = await checkout.ScanAsync();
        var asked = 0;
        var progress = new List<CleanProgress>();
        var options = Now(shown) with
        {
            Guard = () => ++asked == 1 ? null : new CleanStop("busy", "A session started to work in the checkout."),
            Progress = new Recorded<CleanProgress>(progress),
        };

        var cleanup = await Cleaner(checkout).CleanAsync(shown, ["dist", "lib/bin"], options, CancellationToken.None);

        Assert.AreEqual(("busy", "A session started to work in the checkout.", 2), (cleanup.Status, cleanup.Message, asked));
        CollectionAssert.AreEqual(new[] { CleanOutcome.Removed, CleanOutcome.NotAttempted }, cleanup.Entries.Select(static entry => entry.Outcome).ToArray());
        Assert.IsTrue(File.Exists(checkout.At("lib/bin/lib.dll")));
        CollectionAssert.AreEqual(new[] { new CleanProgress(0, 2), new CleanProgress(1, 2) }, progress);
    }

    [TestMethod]
    public async Task TheFoldersMeasuredOnTheirOwnNow_AreLeftOutOfTheClean()
    {
        using var checkout = BuildFolderIn(Checkout.Create());
        var shown = await checkout.ScanAsync();
        // A project registered inside the build folder after the measure.
        checkout.Write("bin/project/notes.txt", "mine");

        var cleanup = await Cleaner(checkout).CleanAsync(shown, ["bin"], new BuildOutputCleanOptions([checkout.At("bin/project")]), CancellationToken.None);

        Assert.AreEqual(CleanOutcome.KeptNotBuildOutput, cleanup.Entries.Single().Outcome);
        Assert.IsTrue(File.Exists(checkout.At("bin/project/notes.txt")));
    }

    [TestMethod]
    public async Task ARepositoryCreatedInABuildFolderWhileEarlierRunsClean_IsKept()
    {
        using var checkout = ManyBuildFolders(70);
        var runs = 0;
        var cleaner = new BuildOutputCleaner(async (folder, arguments, timeout, token) =>
        {
            var run = await checkout.RunAsync(folder, arguments, timeout, token);
            if (arguments.Contains("clean") && ++runs == 1)
            {
                // In two folders of the second run: a bare repository, and a checkout whose .git names nothing.
                checkout.GitIn(checkout.Home, "init", "-q", "--bare", checkout.At("p069/bin/mirror.git"));
                checkout.Write("p068/bin/broken/.git", "gitdir: " + Path.Combine(checkout.Home, "nowhere"));
            }

            return run;
        });

        var cleanup = await CleanPlannedAsync(checkout, cleaner);

        Assert.AreEqual(BuildOutputCleaner.Ok, cleanup.Status, cleanup.Message);
        var outcomes = cleanup.Entries.ToDictionary(entry => entry.Path, entry => entry.Outcome);
        Assert.AreEqual(CleanOutcome.KeptHoldsRepository, outcomes["p069/bin"]);
        Assert.AreEqual(CleanOutcome.KeptHoldsRepository, outcomes["p068/bin"]);
        Assert.AreEqual(68, outcomes.Values.Count(outcome => outcome == CleanOutcome.Removed));
        Assert.IsTrue(File.Exists(checkout.At("p069/bin/mirror.git/HEAD")));
        Assert.IsTrue(File.Exists(checkout.At("p068/bin/broken/.git")));
    }

    [TestMethod]
    public async Task ABuildFolderNotReadWhole_IsNotBuildOutput()
    {
        if (OperatingSystem.IsWindows()) Assert.Inconclusive("A folder that cannot be read is made with the modes of Unix.");
        using var checkout = BuildFolderIn(Checkout.Create());
        var closed = Directory.CreateDirectory(checkout.At("bin/closed")).FullName;
        SetMode(closed, UnixFileMode.None);
        try
        {
            var entry = (await checkout.ScanAsync()).IgnoredEntries.Single();

            Assert.AreEqual("bin", entry.Path);
            Assert.IsFalse(entry.BuildOutput, "what was not read may hold a repository");
        }
        finally
        {
            SetMode(closed, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [TestMethod]
    public async Task AGitDirectoryNamedLikeAnIgnoreFile_GivesNoRuleOfTheRepository()
    {
        if (OperatingSystem.IsWindows()) Assert.Inconclusive("`:` is not a character of a name on Windows.");
        using var checkout = Checkout.Create(init: false);
        var gitDirectory = checkout.At("policy/.gitignore:1:pretend/admin");
        Directory.CreateDirectory(Path.GetDirectoryName(gitDirectory)!);
        Directory.CreateDirectory(checkout.Root);
        checkout.GitIn(checkout.Root, "init", "-q", "-b", "main", "--separate-git-dir", gitDirectory);
        checkout.Configure(checkout.Root);
        checkout.Write("readme.md", "one");
        checkout.Git("add", "readme.md");
        checkout.Git("commit", "-q", "-m", "one");
        // The rule is in .git/info/exclude, whose path git prints as `policy/.gitignore:1:pretend/admin/info/exclude:<line>:bin/`.
        File.AppendAllText(Path.Combine(gitDirectory, "info", "exclude"), "bin/\n");
        checkout.Write("bin/secret.key", "key");

        await AssertNotBuildOutputAsync(checkout);
    }

    [TestMethod]
    public async Task AnExcludesFileInAFolderLinkedFromTheCheckout_GivesNoRuleOfTheRepository()
    {
        using var checkout = Checkout.Create();
        var outside = Directory.CreateDirectory(Path.Combine(checkout.Home, "outside")).FullName;
        File.WriteAllText(Path.Combine(outside, ".gitignore"), "bin/\n");
        if (!TryLink(checkout.At("policy"), outside)) return;
        checkout.Git("config", "core.excludesFile", checkout.At("policy/.gitignore"));
        checkout.Write("bin/secret.key", "key");

        await AssertNotBuildOutputAsync(checkout);
    }

    [TestMethod]
    public async Task AnExcludesFileInsideTheCheckout_GivesNoRuleOfTheRepository()
    {
        using var checkout = Checkout.Create();
        checkout.Write("policy/.gitignore", "bin/\n");
        checkout.Git("config", "core.excludesFile", checkout.At("policy/.gitignore"));
        checkout.Write("bin/secret.key", "key");

        await AssertNotBuildOutputAsync(checkout);
    }

    [TestMethod]
    public async Task TheIgnoreFileAtTheTopThatIsAlsoTheExcludesFile_GivesNoRuleOfTheRepository()
    {
        using var checkout = Checkout.Create();
        checkout.Write(".gitignore", "bin/\n");
        checkout.Git("config", "core.excludesFile", checkout.At(".gitignore"));
        checkout.Write("bin/secret.key", "key");

        await AssertNotBuildOutputAsync(checkout);
    }

    [TestMethod]
    public async Task AConfirmedPath_MatchesThePlannedOneInItsExactCase()
    {
        using var checkout = BuildFolderIn(Checkout.Create());

        var cleanup = await Cleaner(checkout).CleanAsync(await checkout.ScanAsync(), ["BIN"], Now(await checkout.ScanAsync()), CancellationToken.None);

        Assert.AreEqual(new CleanedEntry("BIN", CleanOutcome.KeptNotBuildOutput, 0, 0), cleanup.Entries.Single());
        Assert.IsTrue(File.Exists(checkout.At("bin/a.dll")));
    }

    [TestMethod]
    public async Task OnAVolumeThatTellsCaseApart_FoldersThatDifferInCaseAreTwo()
    {
        using var volume = CaseSensitiveVolume.Create();
        using var checkout = Checkout.Create(parent: volume.Path);
        checkout.Write(".gitignore", "bin/\n");
        checkout.Write("Bin/tool.txt", new string('t', 100));
        checkout.Commit("tracked Bin");
        checkout.Write("bin/a.dll", new string('a', 50));

        var usage = await checkout.ScanAsync();
        var measured = FolderUsage.Measure(checkout.Root, CancellationToken.None).Bytes;
        var cleanup = await Cleaner(checkout).CleanAsync(usage, ["bin"], Now(usage), CancellationToken.None);

        Assert.AreEqual(measured, usage.TotalBytes, "the tracked Bin is not left out with the ignored bin");
        Assert.AreEqual(new CleanedEntry("bin", CleanOutcome.Removed, 50, 50), cleanup.Entries.Single());
        Assert.IsTrue(File.Exists(checkout.At("Bin/tool.txt")));
    }

    [TestMethod]
    public async Task ACheckoutWhoseNameEndsWithASpace_IsMeasuredAndCleaned()
    {
        if (OperatingSystem.IsWindows()) Assert.Inconclusive("Windows takes the space at the end of a name off.");
        using var checkout = BuildFolderIn(Checkout.Create(name: "repository "));

        var usage = await checkout.ScanAsync();
        var measured = FolderUsage.Measure(checkout.Root, CancellationToken.None).Bytes;
        var cleanup = await Cleaner(checkout).CleanAsync(usage, ["bin"], Now(usage), CancellationToken.None);

        Assert.IsTrue(usage.GitKnown);
        Assert.AreEqual(measured, usage.TotalBytes);
        Assert.AreEqual(checkout.Root, usage.Repositories[0].WorkTree);
        Assert.AreEqual(new CleanedEntry("bin", CleanOutcome.Removed, 1, 1), cleanup.Entries.Single());
        Assert.IsNotNull(await checkout.ReadAsync());
    }

    // A rule that is not the repository's own: the folder it ignores is not build output, and a clean keeps it.
    private static async Task AssertNotBuildOutputAsync(Checkout checkout)
    {
        var usage = await checkout.ScanAsync();
        var cleanup = await Cleaner(checkout).CleanAsync(usage, ["bin"], Now(usage), CancellationToken.None);

        var entry = usage.IgnoredEntries.Single(candidate => candidate.Path == "bin");
        Assert.IsFalse(entry.BuildOutput, "the rule is not one of the repository");
        Assert.AreEqual(CleanOutcome.KeptNotBuildOutput, cleanup.Entries.Single().Outcome);
        Assert.IsTrue(File.Exists(checkout.At("bin/secret.key")));
    }

    // A committed `.gitignore` of `bin/` and an ignored bin/a.dll of one byte.
    private static Checkout BuildFolderIn(Checkout checkout)
    {
        checkout.Write(".gitignore", "bin/\n");
        checkout.Commit("ignore");
        checkout.Write("bin/a.dll", "a");
        return checkout;
    }

    private static bool TryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Assert.Inconclusive("Symbolic links cannot be created here: " + exception.Message);
            return false;
        }
    }

    // A folder on a volume that tells `bin` and `BIN` apart: a disk image on macOS, the temporary folder on Linux.
    private sealed class CaseSensitiveVolume : IDisposable
    {
        private readonly string _home;
        private readonly bool _mounted;

        private CaseSensitiveVolume(string home, string path, bool mounted) => (_home, Path, _mounted) = (home, path, mounted);

        public string Path { get; }

        public static CaseSensitiveVolume Create()
        {
            var home = Directory.CreateTempSubdirectory("codealta-case-").FullName;
            if (OperatingSystem.IsLinux()) return new(home, home, mounted: false);
            if (!OperatingSystem.IsMacOS()) Assert.Inconclusive("A volume that tells case apart is made on macOS and Linux only.");
            var image = System.IO.Path.Combine(home, "case.dmg");
            var mount = Directory.CreateDirectory(System.IO.Path.Combine(home, "mount")).FullName;
            if (!Run("hdiutil", "create", "-quiet", "-size", "64m", "-fs", "Case-sensitive APFS", "-volname", "codealta-case", image)
                || !Run("hdiutil", "attach", "-quiet", "-nobrowse", "-mountpoint", mount, image))
            {
                Directory.Delete(home, recursive: true);
                Assert.Inconclusive("hdiutil could not make a case-sensitive volume.");
            }

            return new(home, mount, mounted: true);
        }

        public void Dispose()
        {
            if (_mounted) Run("hdiutil", "detach", "-quiet", "-force", Path);
            try
            {
                Directory.Delete(_home, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup of a temporary folder.
            }
        }

        private static bool Run(string file, params string[] arguments)
        {
            var start = new System.Diagnostics.ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = System.Diagnostics.Process.Start(start)!;
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            return process.WaitForExit(TestTime.MillisecondsInt(120_000)) && process.ExitCode == 0;
        }
    }

    [TestMethod]
    public async Task ABuildFolderThatHoldsAFolderMeasuredOnItsOwn_IsNeverPlanned_NorCleaned()
    {
        using var checkout = Checkout.Create();
        checkout.Write(".gitignore", "bin/\n");
        checkout.Commit("ignore");
        checkout.Write("bin/a.dll", "aaa");
        // A project of its own, not a repository, inside the build folder.
        checkout.Write("bin/tool/keep.txt", "keep");
        var measure = await new CheckoutUsageScanner(checkout.RunAsync).ScanAsync(checkout.Root, [checkout.At("bin/tool")], null, CancellationToken.None);

        var cleanup = await Cleaner(checkout).CleanAsync(measure, ["bin"], Now(measure), CancellationToken.None);

        Assert.IsEmpty(BuildOutputCleaner.Planned(measure));
        Assert.AreEqual(BuildOutputCleaner.Ok, cleanup.Status, cleanup.Message);
        Assert.AreEqual(CleanOutcome.KeptNotBuildOutput, cleanup.Entries.Single().Outcome);
        Assert.IsTrue(File.Exists(checkout.At("bin/tool/keep.txt")));
        Assert.IsTrue(File.Exists(checkout.At("bin/a.dll")));
    }

    private static BuildOutputCleaner Cleaner(Checkout checkout) => new(checkout.RunAsync);

    // Records each report at once, on the thread that reports it.
    private sealed class Recorded<T>(List<T> reports) : IProgress<T>
    {
        public void Report(T value) => reports.Add(value);
    }

    // What a caller knows now: the folders measured on their own, as the measure had them.
    private static BuildOutputCleanOptions Now(CheckoutUsage measure) => new(measure.ExcludedFolders);

    // The tests that call it are inconclusive on Windows, before they get here.
    private static void SetMode(string path, UnixFileMode mode)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, mode);
    }

    // Cleans everything a new measure plans, as a user who confirms the whole list.
    private static async Task<BuildOutputCleanup> CleanPlannedAsync(Checkout checkout, BuildOutputCleaner? cleaner = null)
    {
        var measure = await checkout.ScanAsync();
        var planned = BuildOutputCleaner.Planned(measure).Select(entry => entry.Path).ToArray();
        return await (cleaner ?? Cleaner(checkout)).CleanAsync(measure, planned, Now(measure), CancellationToken.None);
    }

    // Folders p000 to p(count-1), each with a tracked file and an ignored bin folder of one byte.
    private static Checkout ManyBuildFolders(int count)
    {
        var checkout = Checkout.Create();
        checkout.Write(".gitignore", "bin/\n");
        for (var index = 0; index < count; index++) checkout.Write($"p{index:D3}/keep.txt", "kept");
        checkout.Commit("projects");
        for (var index = 0; index < count; index++) checkout.Write($"p{index:D3}/bin/a.dll", "a");
        return checkout;
    }
}
