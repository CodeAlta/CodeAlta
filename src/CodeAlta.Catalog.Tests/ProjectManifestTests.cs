namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed class ProjectManifestTests
{
    private const string Example = """
        # The projects of contoso.

        [labels.backend]
        name = "Backend"
        icon = "server"
        color = "#2d72d2"

        [labels.payments]
        name = "Payments"
        parent = "backend"

        [labels.frontend]
        name = "Frontend"

        [projects.billing]
        path = "services/billing"
        name = "Billing"
        description = "Invoices and payment collection."
        labels = ["payments"]

        [projects.web]
        path = "apps\\web/"
        labels = ["frontend"]
        write = "folder"

        [projects.legacy-reports]
        path = "tools/reports"
        optional = true
        """;

    [TestMethod]
    public void AMap_ReadsItsProjectsAndLabels_InTheOrderOfTheFile()
    {
        var map = ProjectManifest.Parse(Example);

        Assert.AreEqual(0, map.Problems.Count, string.Join("\n", map.Problems.Select(static problem => problem.Message)));
        CollectionAssert.AreEqual(new[] { "backend", "payments", "frontend" }, map.Labels.Select(static label => label.Key).ToArray());
        Assert.AreEqual("backend", map.Labels[1].Parent);
        Assert.AreEqual(("server", "#2d72d2"), (map.Labels[0].Icon, map.Labels[0].Color));
        CollectionAssert.AreEqual(new[] { "billing", "web", "legacy-reports" }, map.Projects.Select(static entry => entry.Key).ToArray());
        var billing = map.Find("billing")!;
        Assert.AreEqual(("services/billing", "Billing", "Invoices and payment collection."), (billing.Path, billing.Name, billing.Description));
        CollectionAssert.AreEqual(new[] { "payments" }, billing.Labels.ToArray());
        Assert.AreEqual(ProjectWriteScope.Repository, billing.Write);
        Assert.AreEqual("apps/web", map.Find("web")!.Path, "Either separator, and none at the end.");
        Assert.AreEqual(ProjectWriteScope.Folder, map.Find("web")!.Write);
        Assert.IsTrue(map.Find("legacy-reports")!.Optional);
    }

    [TestMethod]
    public void AMistake_CostsTheEntryOrTheValue_NeverTheMap()
    {
        var map = ProjectManifest.Parse("""
            title = "contoso"

            [labels.ops]
            name = "Ops"
            color = "red"
            parent = "missing"

            [labels.Bad]
            name = "Bad"

            [projects.up]
            path = "../elsewhere"

            [projects.absolute]
            path = "C:/code/other"

            [projects.git]
            path = ".git/modules/x"

            [projects.root]
            path = "."

            [projects.one]
            path = "services/one"
            labels = ["ops", "nope"]
            color = "blue"

            [projects.again]
            path = "services/one/"

            [projects.write]
            path = "services/two"
            write = "everything"
            optional = "yes"
            """);

        CollectionAssert.AreEqual(new[] { "one", "write" }, map.Projects.Select(static entry => entry.Key).ToArray());
        CollectionAssert.AreEqual(new[] { "ops" }, map.Find("one")!.Labels.ToArray(), "A label the map does not declare is left out.");
        Assert.AreEqual(ProjectWriteScope.Repository, map.Find("write")!.Write);
        Assert.IsFalse(map.Find("write")!.Optional);
        Assert.AreEqual(1, map.Labels.Count);
        Assert.IsNull(map.Labels[0].Parent, "A parent that is no label of the map is read as none.");
        Assert.IsNull(map.Labels[0].Color);
        var codes = map.Problems.Select(static problem => $"{problem.Key}:{problem.Code}").ToArray();
        foreach (var expected in new[] { ":key.unknown", "ops:label.color", "ops:label.parent", "Bad:label.key", "up:project.path", "absolute:project.path",
                     "git:project.path", "root:project.path", "one:project.label", "one:key.unknown", "again:project.duplicate", "write:project.write",
                     "write:project.optional" })
            CollectionAssert.Contains(codes, expected);
    }

    [TestMethod]
    public void AFileThatIsNotTomlOrTooLarge_IsOneProblem()
    {
        var invalid = ProjectManifest.Parse("[projects.a]\npath = \"a\"\n[projects.a]\npath = \"b\"\n");
        Assert.AreEqual(0, invalid.Projects.Count);
        Assert.AreEqual("file.invalid", invalid.Problems.Single().Code);

        var large = ProjectManifest.Parse("# " + new string('x', ProjectManifest.MaximumBytes));
        Assert.AreEqual("file.tooLarge", large.Problems.Single().Code);
        Assert.AreEqual(0, ProjectManifest.Parse(string.Empty).Problems.Count);
    }

    [TestMethod]
    public void TheLabelsOfAMap_GoFourLevelsDeep_WithoutALoop()
    {
        var map = ProjectManifest.Parse("""
            [labels.a]
            name = "A"
            parent = "b"
            [labels.b]
            name = "B"
            parent = "a"
            [labels.c]
            name = "C"
            [labels.d]
            name = "D"
            parent = "c"
            [labels.e]
            name = "E"
            parent = "d"
            [labels.f]
            name = "F"
            parent = "e"
            [labels.g]
            name = "G"
            parent = "f"
            """);

        Assert.IsNull(map.Labels.Single(static label => label.Key == "g").Parent, "A fifth level is read as a top-level label.");
        Assert.IsTrue(map.Labels.Where(static label => label.Key is "a" or "b").Any(static label => label.Parent is null), "A loop is broken.");
        var order = map.Labels.Select(static label => label.Key).ToList();
        foreach (var label in map.Labels.Where(static label => label.Parent is not null))
            Assert.IsTrue(order.IndexOf(label.Parent!) < order.IndexOf(label.Key), "A parent comes before its labels.");
    }

    [TestMethod]
    public void KeysAndPaths_AreNormalized()
    {
        Assert.AreEqual("billing", ProjectManifest.KeyFrom("services/billing"));
        Assert.AreEqual("my-app", ProjectManifest.KeyFrom(@"apps\My App"));
        Assert.AreEqual("project", ProjectManifest.KeyFrom("日本"));
        Assert.IsTrue(ProjectManifest.IsValidKey("legacy-reports"));
        Assert.IsFalse(ProjectManifest.IsValidKey("-x"));
        Assert.IsFalse(ProjectManifest.IsValidKey("a.b"), "A dot separates the slug of the repository and the key in the identifier of a label.");
        Assert.AreEqual("libs/core", ProjectManifest.NormalizePath("./libs/core/", out _));
        Assert.IsNull(ProjectManifest.NormalizePath("libs//core", out var problem));
        Assert.IsNotNull(problem);
        foreach (var settings in new[] { ".alta", "tools/.alta/skills", "apps/.claude", ".GIT/x", ".codex", "a/.vscode" })
            Assert.IsNull(ProjectManifest.NormalizePath(settings, out _), $"{settings}: a folder of settings is no project.");
    }

    [TestMethod]
    public void AFolder_IsAnyFolderInsideTheRepository_ASubmoduleIncluded()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "codealta-map-" + Guid.NewGuid().ToString("N"))).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "codealta-map-out-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var submodule = Directory.CreateDirectory(Path.Combine(root, "vendor", "lib")).FullName;
            // A git submodule is a folder whose `.git` is a file that names its git folder in the repository.
            File.WriteAllText(Path.Combine(submodule, ".git"), "gitdir: ../../.git/modules/vendor/lib\n");
            Assert.AreEqual((submodule, ProjectManifestFolderState.Ok), ProjectManifest.ResolveFolder(root, "vendor/lib"));
            Assert.AreEqual(ProjectManifestFolderState.Missing, ProjectManifest.ResolveFolder(root, "vendor/none").State);
            File.WriteAllText(Path.Combine(root, "file"), "");
            Assert.AreEqual(ProjectManifestFolderState.Missing, ProjectManifest.ResolveFolder(root, "file").State, "A file is no folder.");

            try
            {
                Directory.CreateSymbolicLink(Path.Combine(root, "away"), outside);
                Directory.CreateSymbolicLink(Path.Combine(root, "near"), submodule);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Assert.Inconclusive("This system does not let the test create a link.");
            }

            Assert.AreEqual(ProjectManifestFolderState.Outside, ProjectManifest.ResolveFolder(root, "away").State);
            Assert.AreEqual(ProjectManifestFolderState.Ok, ProjectManifest.ResolveFolder(root, "near").State, "A link that stays inside is a folder like another.");
            Directory.CreateSymbolicLink(Path.Combine(root, "alias"), Directory.CreateDirectory(Path.Combine(root, ".git", "modules")).FullName);
            Assert.AreEqual(ProjectManifestFolderState.Outside, ProjectManifest.ResolveFolder(root, "alias").State, "A link into a folder of settings is no folder for a project.");
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(outside, true);
        }
    }
}
