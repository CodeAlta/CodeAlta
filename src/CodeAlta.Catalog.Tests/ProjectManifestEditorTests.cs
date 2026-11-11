namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed class ProjectManifestEditorTests
{
    [TestMethod]
    public void AnEdit_KeepsTheCommentsAndTheOrder_AndPutsANewLabelBeforeTheProjects()
    {
        using var fixture = new Fixture("services/billing", "apps/web", "apps/admin");
        fixture.Write("""
            # The projects of contoso.

            [labels.backend]
            name = "Backend" # the services

            # Billing is owned by the payments team.
            [projects.billing]
            path = "services/billing"
            labels = ["backend"]
            """);

        var added = ProjectManifestEditor.AddProject(fixture.Root, null, new ProjectManifestEdit("apps\\admin", Name: "Admin", Labels: ["frontend"]));
        Assert.AreEqual(ProjectManifestChangeStatus.Updated, added.Status, added.Message);
        Assert.AreEqual("admin", added.Key);
        CollectionAssert.AreEqual(new[] { "frontend" }, added.CreatedLabels!.ToArray());
        var text = fixture.Read();
        StringAssert.Contains(text, "# The projects of contoso.");
        StringAssert.Contains(text, "name = \"Backend\" # the services");
        StringAssert.Contains(text, "# Billing is owned by the payments team.\n[projects.billing]");
        Assert.IsTrue(text.IndexOf("[labels.frontend]", StringComparison.Ordinal) < text.IndexOf("[projects.billing]", StringComparison.Ordinal),
            "A label goes with the labels.");
        var map = ProjectManifest.Parse(text);
        Assert.AreEqual(0, map.Problems.Count);
        Assert.AreEqual(("apps/admin", "Admin"), (map.Find("admin")!.Path, map.Find("admin")!.Name));
        Assert.AreEqual("Frontend", map.Labels.Single(static label => label.Key == "frontend").Name);

        Assert.AreEqual(ProjectManifestChangeStatus.Updated, ProjectManifestEditor.SetProject(fixture.Root, "admin",
            new ProjectManifestEdit(Name: "", Write: ProjectWriteScope.Folder, Optional: true)).Status);
        var admin = ProjectManifest.Parse(fixture.Read()).Find("admin")!;
        Assert.AreEqual((null, ProjectWriteScope.Folder, true), (admin.Name, admin.Write, admin.Optional));
        Assert.AreEqual(ProjectManifestChangeStatus.Unchanged, ProjectManifestEditor.SetProject(fixture.Root, "admin", new ProjectManifestEdit(Optional: true)).Status);

        Assert.AreEqual(ProjectManifestChangeStatus.Updated, ProjectManifestEditor.SetLabel(fixture.Root, "payments", new ProjectManifestLabelEdit("Payments", Parent: "backend", Color: "#238551")).Status);
        Assert.AreEqual(ProjectManifestChangeStatus.Invalid, ProjectManifestEditor.SetLabel(fixture.Root, "backend", new ProjectManifestLabelEdit(Parent: "payments")).Status,
            "A label never goes under one of its own.");
        Assert.AreEqual(ProjectManifestChangeStatus.Invalid, ProjectManifestEditor.SetLabel(fixture.Root, "payments", new ProjectManifestLabelEdit(Color: "green")).Status,
            "A change that adds a problem to the map is refused.");

        Assert.AreEqual(ProjectManifestChangeStatus.Updated, ProjectManifestEditor.RemoveLabel(fixture.Root, "backend").Status);
        map = ProjectManifest.Parse(fixture.Read());
        Assert.AreEqual(0, map.Problems.Count, string.Join("\n", map.Problems.Select(static problem => problem.Message)));
        Assert.AreEqual(0, map.Find("billing")!.Labels.Count, "Its projects lose it.");
        Assert.IsNull(map.Labels.Single(static label => label.Key == "payments").Parent, "The labels under it take its place.");

        Assert.AreEqual(ProjectManifestChangeStatus.Updated, ProjectManifestEditor.RemoveProject(fixture.Root, "billing").Status);
        Assert.IsNull(ProjectManifest.Parse(fixture.Read()).Find("billing"));
        StringAssert.Contains(fixture.Read(), "# The projects of contoso.");
    }

    [TestMethod]
    public void AnAdd_NamesAFolderOfTheRepository_OnceAndUnderAFreeKey()
    {
        using var fixture = new Fixture("services/billing", "apps/billing");
        var first = ProjectManifestEditor.AddProject(fixture.Root, null, new ProjectManifestEdit("services/billing"));
        Assert.AreEqual(ProjectManifestChangeStatus.Updated, first.Status);
        StringAssert.StartsWith(fixture.Read(), "# The projects of this repository for CodeAlta", "A new map says what it is.");
        Assert.AreEqual("billing-2", ProjectManifestEditor.AddProject(fixture.Root, null, new ProjectManifestEdit("apps/billing")).Key);
        Assert.AreEqual(ProjectManifestChangeStatus.Invalid, ProjectManifestEditor.AddProject(fixture.Root, null, new ProjectManifestEdit("services/billing/")).Status);
        Assert.AreEqual(ProjectManifestChangeStatus.Invalid, ProjectManifestEditor.AddProject(fixture.Root, null, new ProjectManifestEdit("services/none")).Status);
        Assert.AreEqual(ProjectManifestChangeStatus.Invalid, ProjectManifestEditor.AddProject(fixture.Root, null, new ProjectManifestEdit("../outside")).Status);
        Assert.AreEqual(ProjectManifestChangeStatus.Invalid, ProjectManifestEditor.AddProject(fixture.Root, "Bad Key", new ProjectManifestEdit("apps")).Status);
        Assert.AreEqual(ProjectManifestChangeStatus.NotFound, ProjectManifestEditor.SetProject(fixture.Root, "missing", new ProjectManifestEdit(Name: "x")).Status);
    }

    [TestMethod]
    public void AMapWrittenInAnotherForm_IsNotEdited()
    {
        using var fixture = new Fixture("apps/web");
        fixture.Write("projects = { web = { path = \"apps/web\" } }\n");
        var change = ProjectManifestEditor.SetProject(fixture.Root, "web", new ProjectManifestEdit(Name: "Web"));
        Assert.AreEqual(ProjectManifestChangeStatus.Unsupported, change.Status);
        Assert.AreEqual("projects = { web = { path = \"apps/web\" } }\n", fixture.Read());

        fixture.Write("[projects.web\n");
        Assert.AreEqual(ProjectManifestChangeStatus.Unsupported, ProjectManifestEditor.AddProject(fixture.Root, null, new ProjectManifestEdit("apps/web")).Status);
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture(params string[] folders)
        {
            Root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "codealta-map-edit-" + Guid.NewGuid().ToString("N"))).FullName;
            foreach (var folder in folders) Directory.CreateDirectory(Path.Combine(Root, folder));
        }

        public string Root { get; }

        public void Write(string text)
        {
            Directory.CreateDirectory(Path.Combine(Root, ".alta"));
            File.WriteAllText(ProjectManifest.PathOf(Root), text.ReplaceLineEndings("\n"));
        }

        public string Read() => File.ReadAllText(ProjectManifest.PathOf(Root));

        public void Dispose() => Directory.Delete(Root, true);
    }
}
