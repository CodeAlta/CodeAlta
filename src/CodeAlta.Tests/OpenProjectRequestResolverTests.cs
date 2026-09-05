using CodeAlta.Tui.App;
using CodeAlta.Catalog;

namespace CodeAlta.Tests;

[TestClass]
public sealed class OpenProjectRequestResolverTests
{
    [TestMethod]
    public void LooksLikePath_TreatsHomeAndRootedInputsAsPaths()
    {
        Assert.IsTrue(OpenProjectRequestResolver.LooksLikePath("~"));
        Assert.IsTrue(OpenProjectRequestResolver.LooksLikePath("~/repo"));
        Assert.IsTrue(OpenProjectRequestResolver.LooksLikePath(Path.GetPathRoot(Environment.CurrentDirectory)!));
        Assert.IsFalse(OpenProjectRequestResolver.LooksLikePath("CodeAlta"));
    }

    [TestMethod]
    public void ResolveProjectReference_UsesDisplayNameOnly()
    {
        var project = CreateProject("project-1", "codealta", "CodeAlta");

        var result = OpenProjectRequestResolver.ResolveProjectReference(
            [project],
            "CodeAlta");

        Assert.AreSame(project, result);
    }

    [TestMethod]
    public void ResolveProjectReference_DoesNotMatchSlug()
    {
        var project = CreateProject("project-1", "codealta", "Main Repo");

        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => OpenProjectRequestResolver.ResolveProjectReference([project], "codealta"));

        StringAssert.Contains(ex.Message, "project name from the sidebar");
    }

    [TestMethod]
    public void ResolveProjectReference_ThrowsForAmbiguousDisplayName()
    {
        var projects = new[]
        {
            CreateProject("project-1", "codealta-a", "CodeAlta"),
            CreateProject("project-2", "codealta-b", "CodeAlta"),
        };

        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => OpenProjectRequestResolver.ResolveProjectReference(projects, "CodeAlta"));

        StringAssert.Contains(ex.Message, "matched multiple entries");
        StringAssert.Contains(ex.Message, Path.Combine(Path.GetTempPath(), "codealta-a"));
        StringAssert.Contains(ex.Message, Path.Combine(Path.GetTempPath(), "codealta-b"));
    }

    private static ProjectDescriptor CreateProject(string id, string slug, string displayName)
    {
        return new ProjectDescriptor
        {
            Id = id,
            Slug = slug,
            Name = displayName,
            DisplayName = displayName,
            ProjectPath = Path.Combine(Path.GetTempPath(), slug),
            DefaultBranch = "main",
        };
    }
}
