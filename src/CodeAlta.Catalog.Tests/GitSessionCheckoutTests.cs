using CodeAlta.Catalog.Worktrees;

namespace CodeAlta.Catalog.Tests;

/// <summary>
/// Where a session of a project works once it is sent to another checkout of the repository of its project. The
/// checkouts are laid out as git lays them out, without git.
/// </summary>
[TestClass]
public sealed class GitSessionCheckoutTests
{
    [TestMethod]
    public void AWorktreeOfTheRepository_IsWhereTheSessionWorks_WhereverItIs()
    {
        using var layout = new Layout();
        var tree = layout.Worktree("elsewhere-tree");

        Assert.AreEqual((GitWorktreeService.Ok, tree), GitWorktreeService.ResolveSessionCheckout(layout.Main, tree));
        Assert.AreEqual((GitWorktreeService.Ok, tree), GitWorktreeService.ResolveSessionCheckout(layout.Main, Directory.CreateDirectory(Path.Combine(tree, "src", "deep")).FullName),
            "Any folder of the checkout names it.");
        Assert.AreEqual((GitWorktreeService.Ok, (string?)null), GitWorktreeService.ResolveSessionCheckout(layout.Main, layout.Main),
            "The checkout the project lives in is the folder of the project.");
        Assert.AreEqual((GitWorktreeService.Ok, layout.Main), GitWorktreeService.ResolveSessionCheckout(tree, layout.Main),
            "A project that lives in a worktree can send a session to the main checkout.");
    }

    [TestMethod]
    public void AProjectInASubfolder_WorksInTheSameSubfolderOfTheOtherCheckout()
    {
        using var layout = new Layout();
        var project = Directory.CreateDirectory(Path.Combine(layout.Main, "app")).FullName;
        var tree = layout.Worktree("tree");
        var bare = layout.Worktree("bare");
        Directory.CreateDirectory(Path.Combine(tree, "app"));

        Assert.AreEqual((GitWorktreeService.Ok, Path.Combine(tree, "app")), GitWorktreeService.ResolveSessionCheckout(project, tree));
        Assert.AreEqual(("project_missing", (string?)null), GitWorktreeService.ResolveSessionCheckout(project, bare));
    }

    [TestMethod]
    public void AFolderOutsideTheRepository_IsRefused()
    {
        using var layout = new Layout();
        var clone = Directory.CreateDirectory(Path.Combine(layout.Root, "clone")).FullName;
        Directory.CreateDirectory(Path.Combine(clone, ".git"));
        var plain = Directory.CreateDirectory(Path.Combine(layout.Root, "plain")).FullName;

        Assert.AreEqual("other_repository", GitWorktreeService.ResolveSessionCheckout(layout.Main, clone).Status, "A separate clone is another repository.");
        Assert.AreEqual("not_repository", GitWorktreeService.ResolveSessionCheckout(layout.Main, plain).Status);
        Assert.AreEqual("not_found", GitWorktreeService.ResolveSessionCheckout(layout.Main, Path.Combine(layout.Root, "gone")).Status);
        Assert.AreEqual("invalid", GitWorktreeService.ResolveSessionCheckout(layout.Main, "relative").Status);
        Assert.AreEqual("invalid", GitWorktreeService.ResolveSessionCheckout(layout.Main, null).Status);
        Assert.AreEqual("invalid", GitWorktreeService.ResolveSessionCheckout(layout.Main, layout.Main + "\n").Status);
        Assert.AreEqual("not_repository", GitWorktreeService.ResolveSessionCheckout(plain, layout.Main).Status, "A project in no repository has no other checkout.");
    }

    // A main checkout and its linked worktrees, as git writes them: `.git` is a folder in the main checkout, and a file
    // that names its folder inside the repository in a linked worktree, whose `commondir` leads back to the repository.
    private sealed class Layout : IDisposable
    {
        public Layout()
        {
            Root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "codealta-checkout-" + Guid.NewGuid().ToString("N"))).FullName;
            Main = Directory.CreateDirectory(Path.Combine(Root, "main")).FullName;
            Directory.CreateDirectory(Path.Combine(Main, ".git"));
        }

        public string Root { get; }

        public string Main { get; }

        public string Worktree(string name)
        {
            var linked = Directory.CreateDirectory(Path.Combine(Main, ".git", "worktrees", name)).FullName;
            File.WriteAllText(Path.Combine(linked, "commondir"), "../..");
            var tree = Directory.CreateDirectory(Path.Combine(Root, name)).FullName;
            File.WriteAllText(Path.Combine(tree, ".git"), $"gitdir: {linked}");
            return tree;
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
        }
    }
}
