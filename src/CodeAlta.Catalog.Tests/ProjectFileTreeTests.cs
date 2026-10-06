namespace CodeAlta.Catalog.Tests;

/// <summary>Real temporary folders; a folder named <c>.git</c> is what makes one a work tree.</summary>
[TestClass]
public sealed class ProjectFileTreeTests
{
    [TestMethod]
    public void ListFolder_ReadsOneFolder_FoldersFirstInNaturalOrder()
    {
        using var project = TempProject.Create(git: true);
        project.Write("readme.md", "docs/guide.md", "docs/deep/inner.md", "src/b.cs", "file10.txt", "file2.txt", "File1.txt", "_notes.txt", ".editorconfig");

        var root = new ProjectFileTree().ListFolder(project.Path, string.Empty, includeIgnored: false, 100, default);

        Assert.IsFalse(root.Truncated);
        // Nothing below a folder is read: "docs" is one entry, and so is "src". The .git folder is not listed.
        CollectionAssert.AreEqual(new[] { "docs/", "src/", ".editorconfig", "_notes.txt", "File1.txt", "file2.txt", "file10.txt", "readme.md" }, Names(root));
        CollectionAssert.AreEqual(new[] { "deep/", "guide.md" }, Names(new ProjectFileTree().ListFolder(project.Path, "docs", false, 100, default)));
        CollectionAssert.AreEqual(new[] { "inner.md" }, Names(new ProjectFileTree().ListFolder(project.Path, "docs\\deep/", false, 100, default)));
    }

    [TestMethod]
    public void ListFolder_LeavesOutWhatGitIgnores_OrMarksIt()
    {
        using var project = TempProject.Create(git: true);
        project.Write("app.cs", "bin/app.dll", "obj/cache.bin", "notes.tmp", "keep.tmp", "src/gen/code.g.cs", "src/gen/keep.cs", "src/main.cs", "local.user");
        project.WriteText(".gitignore", "bin/\nobj/\n*.tmp\n!keep.tmp\n");
        project.WriteText("src/.gitignore", "gen/*\n!gen/keep.cs\n");
        project.WriteText(".git/info/exclude", "*.user\n");
        var tree = new ProjectFileTree();

        CollectionAssert.AreEqual(new[] { "src/", ".gitignore", "app.cs", "keep.tmp" }, Names(tree.ListFolder(project.Path, "", false, 100, default)));
        // A rule of a nested .gitignore is relative to its own folder.
        CollectionAssert.AreEqual(new[] { "gen/", ".gitignore", "main.cs" }, Names(tree.ListFolder(project.Path, "src", false, 100, default)));
        CollectionAssert.AreEqual(new[] { "keep.cs" }, Names(tree.ListFolder(project.Path, "src/gen", false, 100, default)));

        var everything = tree.ListFolder(project.Path, "", includeIgnored: true, 100, default);
        CollectionAssert.AreEqual(new[] { "bin/", "obj/", "src/", ".gitignore", "app.cs", "keep.tmp", "local.user", "notes.tmp" }, Names(everything));
        CollectionAssert.AreEqual(new[] { "bin", "obj", "local.user", "notes.tmp" }, everything.Entries.Where(entry => entry.IsIgnored).Select(entry => entry.Name).ToArray());
        // Everything inside an ignored folder is ignored.
        Assert.IsTrue(tree.ListFolder(project.Path, "bin", true, 100, default).Entries.Single().IsIgnored);
        Assert.IsEmpty(tree.ListFolder(project.Path, "bin", false, 100, default).Entries);
    }

    [TestMethod]
    public void ListFolder_ReadsAnIgnoreFileAgainWhenItChanges()
    {
        using var project = TempProject.Create(git: true);
        project.Write("a.txt", "b.txt");
        var tree = new ProjectFileTree();
        CollectionAssert.AreEqual(new[] { "a.txt", "b.txt" }, Names(tree.ListFolder(project.Path, "", false, 100, default)));

        project.WriteText(".gitignore", "a.txt\n");
        CollectionAssert.AreEqual(new[] { ".gitignore", "b.txt" }, Names(tree.ListFolder(project.Path, "", false, 100, default)));

        project.WriteText(".gitignore", "b.txt\n# longer\n");
        CollectionAssert.AreEqual(new[] { ".gitignore", "a.txt" }, Names(tree.ListFolder(project.Path, "", false, 100, default)));

        File.Delete(Path.Combine(project.Path, ".gitignore"));
        CollectionAssert.AreEqual(new[] { "a.txt", "b.txt" }, Names(tree.ListFolder(project.Path, "", false, 100, default)));
    }

    [TestMethod]
    public void ListFolder_UsesTheRulesOfTheWorkTreeTheFolderIsIn()
    {
        // The project is a folder of a larger work tree: the rules of the work tree's root apply to it.
        using var repository = TempProject.Create(git: true);
        repository.Write("apps/web/index.ts", "apps/web/dist/out.js", "apps/web/vendor/lib.js", "apps/web/vendor/.git/HEAD", "apps/web/vendor/build/x.o");
        repository.WriteText(".gitignore", "dist/\n/apps/web/secret.txt\n");
        repository.WriteText("apps/web/secret.txt", "s");
        // A work tree nested in the project has its own rules; the outer ones do not reach into it.
        repository.WriteText("apps/web/vendor/.gitignore", "build/\n");
        repository.WriteText("apps/web/vendor/dist/kept.js", "k");
        var project = Path.Combine(repository.Path, "apps", "web");
        var tree = new ProjectFileTree();

        CollectionAssert.AreEqual(new[] { "vendor/", "index.ts" }, Names(tree.ListFolder(project, "", false, 100, default)));
        CollectionAssert.AreEqual(new[] { "dist/", ".gitignore", "lib.js" }, Names(tree.ListFolder(project, "vendor", false, 100, default)));
    }

    [TestMethod]
    public void ListFolder_OutsideAWorkTree_UsesTheIgnoreFilesOfTheProject()
    {
        using var project = TempProject.Create(git: false);
        project.Write("a.txt", "out/b.txt", "sub/c.txt", "sub/skip/d.txt", "sub/.hg/store", ".svn/entries");
        project.WriteText(".gitignore", "out/\n");
        project.WriteText("sub/.gitignore", "skip/\n");
        var tree = new ProjectFileTree();

        CollectionAssert.AreEqual(new[] { "sub/", ".gitignore", "a.txt" }, Names(tree.ListFolder(project.Path, "", false, 100, default)));
        CollectionAssert.AreEqual(new[] { ".gitignore", "c.txt" }, Names(tree.ListFolder(project.Path, "sub", false, 100, default)));
        // The folders of version control systems are not shown even with the ignored entries.
        CollectionAssert.AreEqual(new[] { "skip/", ".gitignore", "c.txt" }, Names(tree.ListFolder(project.Path, "sub", true, 100, default)));
    }

    [TestMethod]
    public void ListFolder_StopsAtTheLimit_AndRefusesAFolderOutsideTheProject()
    {
        using var project = TempProject.Create(git: true);
        project.Write("c.txt", "a.txt", "b.txt", "z/1.txt");
        var tree = new ProjectFileTree();

        var limited = tree.ListFolder(project.Path, "", false, 2, default);
        Assert.IsTrue(limited.Truncated);
        CollectionAssert.AreEqual(new[] { "z/", "a.txt" }, Names(limited));

        Assert.ThrowsExactly<DirectoryNotFoundException>(() => tree.ListFolder(project.Path, "missing", false, 10, default));
        foreach (var folder in new[] { "..", "z/../..", "./z", "z//x", Path.GetTempPath() })
            Assert.ThrowsExactly<ArgumentException>(() => tree.ListFolder(project.Path, folder, false, 10, default), folder);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => tree.ListFolder(project.Path, "", false, 0, default));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => tree.ListFolder(project.Path, "", false, 10, canceled.Token));
    }

    [TestMethod]
    public void ListFolder_DoesNotFollowLinks()
    {
        using var project = TempProject.Create(git: true);
        using var outside = TempProject.Create(git: false);
        project.Write("real.txt");
        outside.Write("secret.txt");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(project.Path, "linked"), outside.Path);
            File.CreateSymbolicLink(Path.Combine(project.Path, "linked.txt"), Path.Combine(outside.Path, "secret.txt"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Assert.Inconclusive("Symbolic links cannot be created on this system.");
        }

        CollectionAssert.AreEqual(new[] { "real.txt" }, Names(new ProjectFileTree().ListFolder(project.Path, "", true, 100, default)));
    }

    [TestMethod]
    public void EnumerateFiles_WalksTheProjectWithItsOwnRelativePaths()
    {
        using var repository = TempProject.Create(git: true);
        repository.Write("root.txt", "apps/web/index.ts", "apps/web/src/a.ts", "apps/web/dist/out.js", "apps/api/main.cs");
        repository.WriteText(".gitignore", "dist/\n");
        var tree = new ProjectFileTree();

        var whole = tree.EnumerateFiles(repository.Path, default).Select(file => file.RelativePath).Order(StringComparer.Ordinal).ToArray();
        CollectionAssert.AreEqual(new[] { ".gitignore", "apps/api/main.cs", "apps/web/index.ts", "apps/web/src/a.ts", "root.txt" }, whole);

        // A project that is a folder of the work tree gets paths relative to itself.
        var web = tree.EnumerateFiles(Path.Combine(repository.Path, "apps", "web"), default).OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToArray();
        CollectionAssert.AreEqual(new[] { "index.ts", "src/a.ts" }, web.Select(file => file.RelativePath).ToArray());
        Assert.AreEqual(Path.Combine(repository.Path, "apps", "web", "src", "a.ts"), web[1].FullPath);
        Assert.AreEqual(1, web[1].Length);

        using var plain = TempProject.Create(git: false);
        plain.Write("a.txt", "skip/b.txt", "sub/c.txt");
        plain.WriteText(".gitignore", "skip/\n");
        CollectionAssert.AreEqual(new[] { ".gitignore", "a.txt", "sub/c.txt" },
            tree.EnumerateFiles(plain.Path, default).Select(file => file.RelativePath).Order(StringComparer.Ordinal).ToArray());
    }

    [TestMethod]
    public void NameComparer_IgnoresCaseAndComparesNumbersByValue()
    {
        var names = new[] { "b", "A", "a10", "a9", "a09", "a9b", "_x", ".y", "Zed", "a" };
        Array.Sort(names, ProjectFileNameComparer.Instance);

        CollectionAssert.AreEqual(new[] { ".y", "_x", "A", "a", "a09", "a9", "a9b", "a10", "b", "Zed" }, names);
        Assert.AreEqual(0, ProjectFileNameComparer.Instance.Compare("same", "same"));
        Assert.IsLessThan(0, ProjectFileNameComparer.Instance.Compare(null, "a"));
        Assert.IsGreaterThan(0, ProjectFileNameComparer.Instance.Compare("a", null));
        Assert.IsLessThan(0, ProjectFileNameComparer.Instance.Compare("a", "ab"));
    }

    private static string[] Names(ProjectFolderListing listing)
        => listing.Entries.Select(static entry => entry.IsDirectory ? entry.Name + "/" : entry.Name).ToArray();

    private sealed class TempProject : IDisposable
    {
        private TempProject(string path) => Path = path;

        public string Path { get; }

        public static TempProject Create(bool git)
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"CodeAlta.ProjectFileTree.Tests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            if (git) Directory.CreateDirectory(System.IO.Path.Combine(path, ".git"));
            return new TempProject(path);
        }

        public void Write(params string[] files)
        {
            foreach (var file in files) WriteText(file, "x");
        }

        public void WriteText(string relative, string text)
        {
            var path = System.IO.Path.Combine(Path, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup of a temporary directory.
            }
        }
    }
}
