using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugins.Tests;

/// <summary>The plugins of the repository of a sub-project, when CodeAlta starts in the folder of the sub-project.</summary>
[TestClass]
public sealed class PluginInheritedRootsTests
{
    [TestMethod]
    public void AHostStartedInASubProject_FindsThePluginsOfItsRepository_ScopedToTheirFolder()
    {
        var repository = Path.Combine(Path.GetTempPath(), "contoso");
        var billing = Path.Combine(repository, "services", "billing");
        var roots = PluginRuntimeManager.BuildRoots(new PluginRuntimeManagerOptions
        {
            GlobalRoot = Path.Combine(Path.GetTempPath(), "alta-home"),
            ProjectContext = new PluginProjectContext { ProjectId = "billing", ProjectPath = billing, InheritedRoots = [repository] },
        });

        Assert.AreEqual(3, roots.Count);
        var inherited = roots[2];
        Assert.AreEqual((Path.Combine(repository, ".alta", "plugins"), PluginScope.Project, repository, true),
            (inherited.RootPath, inherited.Scope, inherited.ProjectPath, inherited.Inherited));
        Assert.IsFalse(roots[1].Inherited, "The project's own folder comes first.");
    }

    [TestMethod]
    public void APackageOfTheProject_WinsOverTheOneOfTheSameIdOfItsRepository()
    {
        var own = new PluginRoot { RootPath = "own", Scope = PluginScope.Project };
        var inherited = new PluginRoot { RootPath = "repository", Scope = PluginScope.Project, Inherited = true };
        SourcePluginPackage Package(string id, PluginRoot root) => new() { PackageId = id, Root = root, PackageDirectory = Path.Combine(root.RootPath, id),
            EntryFilePath = Path.Combine(root.RootPath, id, "plugin.cs") };

        var kept = PluginRuntimeManager.WithoutInheritedShadows([Package("tasks", own), Package("tasks", inherited), Package("board", inherited)]);
        CollectionAssert.AreEqual(new[] { "own", "repository" }, kept.Select(static package => package.Root.RootPath).ToArray());
        CollectionAssert.AreEqual(new[] { "tasks", "board" }, kept.Select(static package => package.PackageId).ToArray());
    }
}
