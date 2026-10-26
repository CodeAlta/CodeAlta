namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed class LabelCatalogTests
{
    [TestMethod]
    public async Task ALabel_GetsItsIdentifierFromItsFirstName_AndKeepsItWhenRenamed()
    {
        using var fixture = new Fixture();
        Assert.AreEqual(0, (await fixture.Labels.LoadAsync()).Count);
        Assert.IsFalse(Directory.Exists(fixture.Options.LabelsRoot), "Reading the labels writes nothing.");

        var created = await fixture.Labels.CreateAsync(" Helm charts! ", icon: "Cube", color: "#238551", description: "Charts.\n\nDeployed by the platform team.");
        Assert.AreEqual(LabelChangeStatus.Ok, created.Status, created.Message);
        Assert.AreEqual("helm-charts", created.Label!.Id);
        var text = await File.ReadAllTextAsync(Path.Combine(fixture.Options.LabelsRoot, "helm-charts.md"));
        Assert.AreEqual("---\nid: helm-charts\nkind: label\nname: \"Helm charts!\"\nicon: cube\ncolor: \"#238551\"\norder: 1\n---\n\nCharts.\n\nDeployed by the platform team.\n", text);

        Assert.IsTrue((await fixture.Labels.UpdateAsync("helm-charts", new LabelEdit(Name: "Charts", Color: "", Favorite: true))).Succeeded);
        var read = (await fixture.Labels.GetAsync("helm-charts"))!;
        Assert.AreEqual("Charts", read.Name);
        Assert.IsNull(read.Color);
        Assert.IsTrue(read.Favorite);
        Assert.AreEqual("cube", read.Icon, "What an edit does not name stays.");

        Assert.AreEqual("helm-charts-2", (await fixture.Labels.CreateAsync("Helm charts")).Label!.Id);
        Assert.AreEqual("label", (await fixture.Labels.CreateAsync("日本")).Label!.Id);
        Assert.AreEqual(LabelChangeStatus.Invalid, (await fixture.Labels.CreateAsync("charts")).Status, "Two labels side by side do not share a name.");
        Assert.AreEqual(LabelChangeStatus.Invalid, (await fixture.Labels.CreateAsync("Bad", color: "red")).Status);
    }

    [TestMethod]
    public async Task SubLabels_FollowTheirParent_AndTakeItsSpace()
    {
        using var fixture = new Fixture();
        Assert.IsTrue((await fixture.Spaces.CreateAsync("Work")).Succeeded);
        var infra = (await fixture.Labels.CreateAsync("Infra", space: "work")).Label!;
        var terraform = (await fixture.Labels.CreateAsync("Terraform", space: "work")).Label!;
        var helm = (await fixture.Labels.CreateAsync("Helm", parent: infra.Id, space: "personal")).Label!;
        Assert.AreEqual("work", helm.Space, "A sub-label is in the space of its parent, whatever is asked.");
        Assert.IsFalse((await File.ReadAllTextAsync(helm.SourcePath!)).Contains("space:"), "The file of a sub-label names no space.");
        Assert.AreEqual(LabelChangeStatus.NotFound, (await fixture.Labels.CreateAsync("Other", space: "missing")).Status);
        Assert.AreEqual(LabelChangeStatus.NotFound, (await fixture.Labels.CreateAsync("Other", parent: "missing")).Status);

        var labels = await fixture.Labels.LoadAsync();
        CollectionAssert.AreEqual(new[] { "infra", "helm", "terraform" }, labels.Select(label => label.Id).ToArray(), "Labels are in the order of their tree.");
        Assert.AreEqual("Infra › Helm", LabelCatalog.PathOf(labels[1], labels));
        CollectionAssert.AreEqual(new[] { "infra", "helm" }, LabelCatalog.Subtree("infra", labels).ToArray());
        Assert.IsTrue(labels[1].IsIn("work"));
        Assert.IsFalse(labels[1].IsIn("personal"));
        Assert.IsFalse(labels[1].IsIn(null));

        Assert.AreEqual(LabelChangeStatus.Invalid, (await fixture.Labels.MoveAsync("infra", "helm", 0)).Status, "A label never goes under its own sub-label.");
        Assert.AreEqual(LabelChangeStatus.Invalid, (await fixture.Labels.UpdateAsync("helm", new LabelEdit(Space: ""))).Status);

        // Moved to the top, a sub-label keeps the space it was in; moved first, the others follow it.
        Assert.IsTrue((await fixture.Labels.MoveAsync("helm", null, 0)).Succeeded);
        labels = await fixture.Labels.LoadAsync();
        CollectionAssert.AreEqual(new[] { "helm", "infra", "terraform" }, labels.Select(label => label.Id).ToArray());
        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, labels.Select(label => label.Order).ToArray());
        Assert.AreEqual("work", labels[0].Space);
        Assert.IsNull(labels[0].Parent);

        Assert.IsTrue((await fixture.Labels.MoveAsync("helm", "terraform", -1)).Succeeded);
        Assert.AreEqual("terraform", (await fixture.Labels.GetAsync("helm"))!.Parent);
    }

    [TestMethod]
    public async Task TheTree_IsCappedInDepth()
    {
        using var fixture = new Fixture();
        string? parent = null;
        for (var level = 1; level <= LabelDescriptor.MaximumDepth; level++)
            parent = (await fixture.Labels.CreateAsync($"Level {level}", parent: parent)).Label!.Id;
        Assert.AreEqual(LabelChangeStatus.Invalid, (await fixture.Labels.CreateAsync("Too deep", parent: parent)).Status);

        var side = (await fixture.Labels.CreateAsync("Side")).Label!;
        Assert.IsTrue((await fixture.Labels.CreateAsync("Under side", parent: side.Id)).Succeeded);
        Assert.AreEqual(LabelChangeStatus.Invalid, (await fixture.Labels.MoveAsync(side.Id, "level-3", 0)).Status,
            "A label with a sub-label does not fit under the third level.");
    }

    [TestMethod]
    public async Task FilesWrittenByHand_AreReadWithoutBreakingTheTree()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Options.LabelsRoot);
        await File.WriteAllTextAsync(Path.Combine(fixture.Options.LabelsRoot, "aa.md"), "---\nname: A\nparent: bb\n---\n");
        await File.WriteAllTextAsync(Path.Combine(fixture.Options.LabelsRoot, "bb.md"), "---\nname: B\nparent: aa\n---\n");
        await File.WriteAllTextAsync(Path.Combine(fixture.Options.LabelsRoot, "orphan.md"), "---\nname: Orphan\nparent: gone\ncolor: red\nspace: work\n---\n");
        await File.WriteAllTextAsync(Path.Combine(fixture.Options.LabelsRoot, "broken.md"), "---\nname: [unclosed\n---\n");
        await File.WriteAllTextAsync(Path.Combine(fixture.Options.LabelsRoot, "Not An Id.md"), "---\nname: X\n---\n");

        var labels = await fixture.Labels.LoadAsync();
        CollectionAssert.AreEquivalent(new[] { "aa", "bb", "orphan" }, labels.Select(label => label.Id).ToArray());
        Assert.AreEqual(1, labels.Count(label => label.Parent is null && label.Id is "aa" or "bb"), "A loop is broken once.");
        var orphan = labels.Single(label => label.Id == "orphan");
        Assert.IsNull(orphan.Parent);
        Assert.IsNull(orphan.Color);
        Assert.AreEqual("work", orphan.Space);
    }

    [TestMethod]
    public async Task Projects_GetLabels_InTheirOwnFile_AndLoseThemWithTheLabel()
    {
        using var fixture = new Fixture();
        var alpha = await fixture.Projects.UpsertFromPathAsync(fixture.Folder("alpha"));
        var beta = await fixture.Projects.UpsertFromPathAsync(fixture.Folder("beta"));
        var infra = (await fixture.Labels.CreateAsync("Infra")).Label!;
        var helm = (await fixture.Labels.CreateAsync("Helm", parent: infra.Id)).Label!;
        var other = (await fixture.Labels.CreateAsync("Other")).Label!;

        Assert.IsTrue((await fixture.Labels.AssignAsync(alpha.Id, [infra.Id, helm.Id], null)).Succeeded);
        Assert.IsTrue((await fixture.Labels.AssignAsync(beta.Id, [helm.Id], null)).Succeeded);
        Assert.AreEqual(LabelChangeStatus.NotFound, (await fixture.Labels.AssignAsync(beta.Id, ["missing"], null)).Status);
        var file = await File.ReadAllTextAsync((await fixture.Projects.GetByIdAsync(alpha.Id))!.SourcePath!);
        StringAssert.Contains(file, "labels: [infra, helm]");

        // Other edits of the project file keep its labels.
        Assert.AreEqual(ProjectSpacesStatus.Updated, await fixture.Projects.UpdateSpacesAsync(alpha.Id, _ => ["work"]));
        var loaded = (await fixture.Projects.GetByIdAsync(alpha.Id))!;
        loaded.Description = "Changed.";
        await fixture.Projects.SaveAsync(loaded);
        CollectionAssert.AreEqual(new[] { "infra", "helm" }, (await fixture.Projects.GetByIdAsync(alpha.Id))!.Labels);

        var members = await fixture.Labels.LoadMembersAsync();
        CollectionAssert.AreEquivalent(new[] { alpha.Id, beta.Id }, members.Single(member => member.Label.Id == helm.Id).ProjectIds.ToArray());

        // Deleted, a label leaves its projects, and its sub-labels take its place.
        Assert.IsTrue((await fixture.Labels.MoveAsync(other.Id, null, 0)).Succeeded);
        Assert.IsTrue((await fixture.Labels.DeleteAsync(infra.Id)).Succeeded);
        var labels = await fixture.Labels.LoadAsync();
        CollectionAssert.AreEqual(new[] { "other", "helm" }, labels.Select(label => label.Id).ToArray());
        Assert.IsNull(labels[1].Parent);
        CollectionAssert.AreEqual(new[] { "helm" }, (await fixture.Projects.GetByIdAsync(alpha.Id))!.Labels);

        Assert.IsTrue((await fixture.Labels.AssignAsync(alpha.Id, null, [helm.Id])).Succeeded);
        Assert.IsFalse((await File.ReadAllTextAsync((await fixture.Projects.GetByIdAsync(alpha.Id))!.SourcePath!)).Contains("labels:"),
            "A project without labels has no labels line.");
    }

    [TestMethod]
    public async Task DeletingASpace_DeletesItsLabels()
    {
        using var fixture = new Fixture();
        Assert.IsTrue((await fixture.Spaces.CreateAsync("Work")).Succeeded);
        var alpha = await fixture.Projects.UpsertFromPathAsync(fixture.Folder("alpha"));
        var infra = (await fixture.Labels.CreateAsync("Infra", space: "work")).Label!;
        var helm = (await fixture.Labels.CreateAsync("Helm", parent: infra.Id)).Label!;
        var shared = (await fixture.Labels.CreateAsync("Shared")).Label!;
        Assert.IsTrue((await fixture.Labels.AssignAsync(alpha.Id, [helm.Id, shared.Id], null)).Succeeded);

        Assert.IsTrue((await fixture.Spaces.DeleteAsync("work")).Succeeded);
        CollectionAssert.AreEqual(new[] { "shared" }, (await fixture.Labels.LoadAsync()).Select(label => label.Id).ToArray());
        CollectionAssert.AreEqual(new[] { "shared" }, (await fixture.Projects.GetByIdAsync(alpha.Id))!.Labels);
    }

    [TestMethod]
    public async Task AReadOnlyCatalog_ChangesNoLabel()
    {
        using var fixture = new Fixture();
        var options = new CatalogOptions { GlobalRoot = fixture.Options.GlobalRoot, ReadOnlyGlobalState = true };
        var readOnly = new SpaceCatalog(new ProjectCatalog(options)).Labels;
        Assert.IsTrue(readOnly.IsReadOnly);
        Assert.AreEqual(LabelChangeStatus.Refused, (await readOnly.CreateAsync("Infra")).Status);
        Assert.IsFalse(Directory.Exists(fixture.Options.LabelsRoot));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "codealta-labels-" + Guid.NewGuid().ToString("N"));
        public CatalogOptions Options { get; }
        public ProjectCatalog Projects { get; }
        public SpaceCatalog Spaces { get; }
        public LabelCatalog Labels => Spaces.Labels;

        public Fixture()
        {
            Directory.CreateDirectory(Root);
            Options = new CatalogOptions { GlobalRoot = Path.Combine(Root, "global") };
            Projects = new ProjectCatalog(Options);
            Spaces = new SpaceCatalog(Projects);
        }

        public string Folder(string name) => Directory.CreateDirectory(Path.Combine(Root, "folders", name)).FullName;

        public void Dispose() => Directory.Delete(Root, true);
    }
}
