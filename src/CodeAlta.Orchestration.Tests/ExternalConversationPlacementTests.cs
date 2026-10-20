using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

/// <summary>
/// The one project that lists a conversation started outside CodeAlta, chosen from the folder it started in and the git
/// checkout of that folder.
/// </summary>
[TestClass]
public sealed class ExternalConversationPlacementTests
{
    [TestMethod]
    public void Place_ListsAConversationUnderTheDeepestProjectOfItsCheckout_WithWhereItStarted()
    {
        using var temp = new TempDirectory();
        var main = Repository(temp, "stride");
        var tools = Directory.CreateDirectory(Path.Combine(main, "tools")).FullName;
        var claudeTree = Worktree(main, Path.Combine(main, ".claude", "worktrees", "fix"), "fix");
        var placement = new ExternalConversationPlacement([("main", main), ("tools", tools)], []);

        Assert.AreEqual(new ExternalConversationPlace("main", null), placement.Place(main));
        Assert.AreEqual(new ExternalConversationPlace("main", "src"), placement.Place(Path.Combine(main, "src")));
        Assert.AreEqual(new ExternalConversationPlace("main", "src"), placement.Place(Path.Combine(main, "src") + Path.DirectorySeparatorChar));
        Assert.AreEqual(new ExternalConversationPlace("tools", "x"), placement.Place(Path.Combine(tools, "x")), "The deepest project.");
        // A worktree Claude Code made in the project is named by its own name, also once it is removed.
        Assert.AreEqual(new ExternalConversationPlace("main", "fix"), placement.Place(claudeTree));
        Assert.AreEqual(new ExternalConversationPlace("main", "gone"), placement.Place(Path.Combine(main, ".claude", "worktrees", "gone")));
        if (OperatingSystem.IsWindows())
            Assert.AreEqual(new ExternalConversationPlace("main", null), placement.Place(main.ToUpperInvariant()), "Folders are compared as Windows does.");
    }

    [TestMethod]
    public void Place_ListsAnotherCheckoutUnderTheProjectThatUsesIt_ElseTheMainCheckout_ElseTheFirstOfTheRepository()
    {
        using var temp = new TempDirectory();
        var main = Repository(temp, "stride");
        var second = Worktree(main, Path.Combine(temp.Path, "stride2"), "stride2");
        var third = Worktree(main, Path.Combine(temp.Path, "stride3"), "stride3");
        var gal = Worktree(main, Path.Combine(temp.Path, "stride-gal"), "stride-gal");
        var used = Worktree(main, Path.Combine(temp.Path, "alta-tree"), "alta-tree");

        var placement = new ExternalConversationPlacement([("main", main), ("third", third), ("second", second)], [(used, second)]);
        Assert.AreEqual(new ExternalConversationPlace("second", null), placement.Place(second), "A checkout that is a project lists its own conversations.");
        Assert.AreEqual(new ExternalConversationPlace("main", Path.Combine("stride-gal", "sources")), placement.Place(Path.Combine(gal, "sources")));
        Assert.AreEqual(new ExternalConversationPlace("second", "alta-tree"), placement.Place(used), "The project whose sessions work in it.");

        var withoutMain = new ExternalConversationPlacement([("third", third), ("second", second)], []);
        Assert.AreEqual(new ExternalConversationPlace("second", "stride-gal"), withoutMain.Place(gal), "The first project of the repository by path.");
    }

    [TestMethod]
    public void Place_ListsAClaudeWorktreeUnderTheLinkedCheckoutProjectThatHoldsIt_NotTheMainCheckout()
    {
        using var temp = new TempDirectory();
        var main = Repository(temp, "stride");
        var second = Worktree(main, Path.Combine(temp.Path, "stride2"), "stride2");
        var claudeTree = Worktree(main, Path.Combine(second, ".claude", "worktrees", "fix"), "fix");
        var placement = new ExternalConversationPlacement([("main", main), ("second", second)], []);

        Assert.AreEqual(new ExternalConversationPlace("second", "fix"), placement.Place(claudeTree));
        Assert.AreEqual(new ExternalConversationPlace("second", Path.Combine("fix", "src")), placement.Place(Path.Combine(claudeTree, "src")));
    }

    [TestMethod]
    public void Place_ListsUnderAParentFolderProjectLast_AndUnderNoProjectWhatNoProjectHolds()
    {
        using var temp = new TempDirectory();
        var parent = Directory.CreateDirectory(Path.Combine(temp.Path, "parent")).FullName;
        var nested = Repository(temp, Path.Combine("parent", "repo"));
        var other = Repository(temp, "other");
        var placement = new ExternalConversationPlacement([("parent", parent)], []);

        Assert.AreEqual(new ExternalConversationPlace("parent", "notes"), placement.Place(Path.Combine(parent, "notes")), "A project of no checkout.");
        Assert.AreEqual(new ExternalConversationPlace("parent", Path.Combine("repo", "src")), placement.Place(Path.Combine(nested, "src")),
            "A repository no project is of, inside a project folder.");
        Assert.AreEqual(new ExternalConversationPlace(null, other), placement.Place(other));
        var removed = Path.Combine(temp.Path, "stride-pr", "3469");
        Assert.AreEqual(new ExternalConversationPlace(null, removed), placement.Place(removed));
        Assert.AreEqual(new ExternalConversationPlace(null, null), placement.Place(null));
        if (OperatingSystem.IsWindows())
            Assert.AreEqual(new ExternalConversationPlace(null, "/home/me/stride"), placement.Place("/home/me/stride"), "A folder of another system, as written.");
    }

    [TestMethod]
    public void Constructor_IgnoresProjectsAndCheckoutsWithoutAFolderOfThisSystem()
    {
        using var temp = new TempDirectory();
        var main = Repository(temp, "stride");
        var tree = Worktree(main, Path.Combine(temp.Path, "tree"), "tree");
        var placement = new ExternalConversationPlacement([("", main), ("relative", "stride"), ("main", main)], [(tree, "relative"), ("tree", main)]);
        Assert.AreEqual(new ExternalConversationPlace("main", "tree"), placement.Place(tree));
        Assert.ThrowsExactly<ArgumentNullException>(() => new ExternalConversationPlacement(null!, []));
        Assert.ThrowsExactly<ArgumentNullException>(() => new ExternalConversationPlacement([], null!));
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"CodeAlta.Placement.{Guid.NewGuid():N}")).FullName;

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }

    // A main checkout: its `.git` is a folder.
    private static string Repository(TempDirectory temp, string name)
    {
        var folder = Directory.CreateDirectory(Path.Combine(temp.Path, name)).FullName;
        Directory.CreateDirectory(Path.Combine(folder, ".git"));
        return folder;
    }

    // A linked worktree of a main checkout, as git writes it: a `.git` file naming its folder in the repository.
    private static string Worktree(string main, string folder, string name)
    {
        var linked = Directory.CreateDirectory(Path.Combine(main, ".git", "worktrees", name)).FullName;
        File.WriteAllText(Path.Combine(linked, "commondir"), "../..");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, ".git"), $"gitdir: {linked}");
        return Path.GetFullPath(folder);
    }
}
