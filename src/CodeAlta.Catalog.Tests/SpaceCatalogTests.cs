using System.Text;

namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed class SpaceCatalogTests
{
    [TestMethod]
    public async Task ACatalogWithoutSpaces_HasTheDefaultOne_AndGetsItsFirstOnesOnce()
    {
        using var fixture = new Fixture();
        var spaces = await fixture.Spaces.LoadAsync();
        Assert.AreEqual(1, spaces.Count);
        Assert.IsTrue(spaces[0].IsDefault);
        Assert.AreEqual("Default", spaces[0].Name);
        Assert.IsNull(spaces[0].SourcePath);
        Assert.IsFalse(Directory.Exists(fixture.Options.SpacesRoot), "Reading the spaces writes nothing.");

        Assert.IsTrue(await fixture.Spaces.SeedAsync());
        spaces = await fixture.Spaces.LoadAsync();
        CollectionAssert.AreEqual(new[] { "default", "work", "personal" }, spaces.Select(space => space.Id).ToArray());
        CollectionAssert.AreEqual(new[] { "Default", "Work", "Personal" }, spaces.Select(space => space.Name).ToArray());
        Assert.AreEqual("briefcase", spaces[1].Icon);
        Assert.AreEqual("#2d72d2", spaces[1].Color);
        Assert.IsTrue(File.Exists(Path.Combine(fixture.Options.SpacesRoot, "work.md")));

        // What the user deleted does not come back, and neither does what they renamed.
        Assert.IsTrue((await fixture.Spaces.DeleteAsync("work")).Succeeded);
        Assert.IsTrue((await fixture.Spaces.DeleteAsync("personal")).Succeeded);
        Assert.IsFalse(await fixture.Spaces.SeedAsync());
        Assert.AreEqual(1, (await fixture.Spaces.LoadAsync()).Count);
    }

    [TestMethod]
    public async Task ASpace_GetsItsIdentifierFromItsFirstName_AndKeepsItWhenRenamed()
    {
        using var fixture = new Fixture();
        var created = await fixture.Spaces.CreateAsync("  Open Source! ", "Libraries I maintain.\n\nMostly .NET.", "Rocket", "#D33D17");
        Assert.AreEqual(SpaceChangeStatus.Ok, created.Status);
        Assert.AreEqual("open-source", created.Space!.Id);
        Assert.AreEqual("Open Source!", created.Space.Name);
        Assert.AreEqual("rocket", created.Space.Icon);
        var text = await File.ReadAllTextAsync(Path.Combine(fixture.Options.SpacesRoot, "open-source.md"));
        Assert.AreEqual("---\nid: open-source\nkind: space\nname: \"Open Source!\"\nicon: rocket\ncolor: \"#D33D17\"\norder: 1\n---\n\nLibraries I maintain.\n\nMostly .NET.\n", text);

        var renamed = await fixture.Spaces.UpdateAsync("open-source", new SpaceEdit(Name: "OSS", Description: "", Color: ""));
        Assert.AreEqual(SpaceChangeStatus.Ok, renamed.Status);
        var read = (await fixture.Spaces.GetAsync("open-source"))!;
        Assert.AreEqual("OSS", read.Name);
        Assert.IsNull(read.Description);
        Assert.IsNull(read.Color);
        Assert.AreEqual("rocket", read.Icon, "What an edit does not name stays.");
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Options.SpacesRoot, "oss.md")));

        // A name already given to an identifier gets the next free one; a name without letters gets a plain one.
        Assert.AreEqual("open-source-2", (await fixture.Spaces.CreateAsync("Open source")).Space!.Id);
        Assert.AreEqual("space", (await fixture.Spaces.CreateAsync("日本")).Space!.Id);
        Assert.AreEqual("default-2", (await fixture.Spaces.CreateAsync("default!")).Space!.Id);
        CollectionAssert.AreEqual(new[] { "default", "open-source", "open-source-2", "space", "default-2" },
            (await fixture.Spaces.LoadAsync()).Select(space => space.Id).ToArray(), "Spaces are listed in the order they were made.");
    }

    [TestMethod]
    public async Task WhatIsNotASpace_IsRefusedWithoutWriting()
    {
        using var fixture = new Fixture();
        Assert.IsTrue((await fixture.Spaces.CreateAsync("Work")).Succeeded);
        foreach (var attempt in new[]
                 {
                     await fixture.Spaces.CreateAsync(" "), await fixture.Spaces.CreateAsync(new string('x', 65)), await fixture.Spaces.CreateAsync("Two\nlines"),
                     await fixture.Spaces.CreateAsync("work"), await fixture.Spaces.CreateAsync("Default"),
                     await fixture.Spaces.CreateAsync("Icon", icon: "not an icon"), await fixture.Spaces.CreateAsync("Color", color: "blue"),
                     await fixture.Spaces.CreateAsync("Long", new string('d', 2001)), await fixture.Spaces.UpdateAsync("work", new SpaceEdit(Name: "default")),
                 })
        {
            Assert.AreEqual(SpaceChangeStatus.Invalid, attempt.Status, attempt.Message);
            Assert.IsFalse(string.IsNullOrWhiteSpace(attempt.Message));
        }

        Assert.AreEqual(SpaceChangeStatus.NotFound, (await fixture.Spaces.UpdateAsync("missing", new SpaceEdit(Name: "X"))).Status);
        Assert.AreEqual(SpaceChangeStatus.NotFound, (await fixture.Spaces.DeleteAsync("missing")).Status);
        Assert.AreEqual(SpaceChangeStatus.Refused, (await fixture.Spaces.DeleteAsync("default")).Status);
        Assert.AreEqual(1, Directory.GetFiles(fixture.Options.SpacesRoot).Length);
    }

    [TestMethod]
    public async Task TheDefaultSpace_CanBeDescribed_ButNeverDeleted()
    {
        using var fixture = new Fixture();
        var changed = await fixture.Spaces.UpdateAsync("default", new SpaceEdit(Name: "Everything", Icon: "layers", Color: "#abc", Description: "All my projects."));
        Assert.AreEqual(SpaceChangeStatus.Ok, changed.Status);
        var spaces = await fixture.Spaces.LoadAsync();
        Assert.AreEqual("Everything", spaces[0].Name);
        Assert.AreEqual("layers", spaces[0].Icon);
        Assert.AreEqual("#abc", spaces[0].Color);
        Assert.AreEqual("All my projects.", spaces[0].Description);
        Assert.IsTrue(spaces[0].IsDefault);
        Assert.AreEqual(SpaceChangeStatus.Refused, (await fixture.Spaces.DeleteAsync("default")).Status);
        Assert.IsTrue(File.Exists(Path.Combine(fixture.Options.SpacesRoot, "default.md")));
    }

    [TestMethod]
    public async Task AFileThatIsNotASpace_IsLeftOut_AndTheOthersAreListed()
    {
        using var fixture = new Fixture();
        Assert.IsTrue((await fixture.Spaces.CreateAsync("Work")).Succeeded);
        await File.WriteAllTextAsync(Path.Combine(fixture.Options.SpacesRoot, "broken.md"), "no front matter");
        await File.WriteAllTextAsync(Path.Combine(fixture.Options.SpacesRoot, "bad-yaml.md"), "---\nname: [unclosed\n---\n");
        await File.WriteAllTextAsync(Path.Combine(fixture.Options.SpacesRoot, "Not A Slug.md"), "---\nname: X\n---\n");
        await File.WriteAllTextAsync(Path.Combine(fixture.Options.SpacesRoot, "notes.txt"), "not a space");
        // A file written by hand: its name is its identifier, whatever its front matter says, and what it says wrongly is ignored.
        await File.WriteAllTextAsync(Path.Combine(fixture.Options.SpacesRoot, "by-hand.md"),
            "---\nid: other\nname: By hand\nicon: Rocket\ncolor: purple\norder: -3\nunknown: kept out\n---\n\r\n  Written in an editor.  \r\n");
        var spaces = await fixture.Spaces.LoadAsync();
        CollectionAssert.AreEqual(new[] { "default", "by-hand", "work" }, spaces.Select(space => space.Id).ToArray());
        Assert.AreEqual("rocket", spaces[1].Icon);
        Assert.IsNull(spaces[1].Color);
        Assert.AreEqual("Written in an editor.", spaces[1].Description);
    }

    [TestMethod]
    public async Task AProject_JoinsAndLeavesSpaces_InItsOwnFile_WhichStaysAsItWasOtherwise()
    {
        using var fixture = new Fixture();
        var project = await fixture.Projects.UpsertFromPathAsync(fixture.Folder("alpha"));
        var source = project.SourcePath!;
        Assert.IsFalse((await File.ReadAllTextAsync(source)).Contains("\nspaces:", StringComparison.Ordinal), "A project in no space says nothing of spaces.");
        Assert.IsTrue((await fixture.Spaces.CreateAsync("Work")).Succeeded);
        Assert.IsTrue((await fixture.Spaces.CreateAsync("No")).Succeeded);
        Assert.IsTrue((await fixture.Spaces.CreateAsync("3.5 Labs")).Succeeded);

        // A file as a user keeps it: a byte order mark, Windows line ends, a comment, a key CodeAlta does not know.
        var text = (await File.ReadAllTextAsync(source)).Replace("archived: false", "archived: false # kept", StringComparison.Ordinal)
            .Replace("\n---\n", "\ncustom: [one, two] # retained\n---\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal) + "Retained   markdown ✨\r\n";
        var encoding = new UTF8Encoding(true, true);
        await File.WriteAllTextAsync(source, text, encoding);

        Assert.AreEqual(SpaceChangeStatus.Ok, (await fixture.Spaces.AssignAsync(project.Id, ["work", "no", "3-5-labs", "work"], null)).Status);
        var joined = text.Replace("custom: [one, two] # retained\r\n---\r\n", "custom: [one, two] # retained\r\nspaces: [work, \"no\", \"3-5-labs\"]\r\n---\r\n", StringComparison.Ordinal);
        CollectionAssert.AreEqual(encoding.GetPreamble().Concat(encoding.GetBytes(joined)).ToArray(), await File.ReadAllBytesAsync(source));
        CollectionAssert.AreEqual(new[] { "work", "no", "3-5-labs" }, (await fixture.Projects.GetByIdAsync(project.Id))!.Spaces);

        // The same request again writes nothing.
        var written = File.GetLastWriteTimeUtc(source);
        Assert.AreEqual(SpaceChangeStatus.Ok, (await fixture.Spaces.AssignAsync(project.Id, ["no"], null)).Status);
        Assert.AreEqual(written, File.GetLastWriteTimeUtc(source));

        // One edit moves a project from a space to another.
        Assert.AreEqual(SpaceChangeStatus.Ok, (await fixture.Spaces.AssignAsync(project.Id, ["work"], ["no", "3-5-labs"])).Status);
        Assert.AreEqual(text.Replace("---\r\n\r\n", "spaces: [work]\r\n---\r\n\r\n", StringComparison.Ordinal), await File.ReadAllTextAsync(source));

        // Leaving the last space removes the entry: the file is the one before spaces, byte for byte.
        Assert.AreEqual(SpaceChangeStatus.Ok, (await fixture.Spaces.AssignAsync(project.Id, null, ["work"])).Status);
        CollectionAssert.AreEqual(encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray(), await File.ReadAllBytesAsync(source));
    }

    [TestMethod]
    public async Task AListWrittenByHandOrByASave_IsReplacedWhole_AndWhatCannotBeEditedIsLeftAlone()
    {
        using var fixture = new Fixture();
        var project = await fixture.Projects.UpsertFromPathAsync(fixture.Folder("alpha"));
        var source = project.SourcePath!;
        foreach (var name in new[] { "One", "Two", "Three" }) Assert.IsTrue((await fixture.Spaces.CreateAsync(name)).Succeeded);
        var original = await File.ReadAllTextAsync(source);

        // A list on several lines, at the margin or indented, with a comment after it and a space that no longer exists.
        foreach (var list in new[] { "spaces:\n- one\n- gone\n", "spaces:\n  - one\n  - gone\n", "spaces :   [ one, gone ]\n" })
        {
            await File.WriteAllTextAsync(source, original.Replace("archived: false\n", "archived: false\n" + list + "# a note\n", StringComparison.Ordinal));
            CollectionAssert.AreEqual(new[] { "one", "gone" }, (await fixture.Projects.GetByIdAsync(project.Id))!.Spaces);
            Assert.AreEqual(SpaceChangeStatus.Ok, (await fixture.Spaces.AssignAsync(project.Id, ["two"], ["one"])).Status);
            Assert.AreEqual(original.Replace("archived: false\n", "archived: false\nspaces: [gone, two]\n# a note\n", StringComparison.Ordinal), await File.ReadAllTextAsync(source));
        }

        // A whole save of the project (the terminal UI, an import) keeps the spaces, and a project in none has no entry.
        var loaded = (await fixture.Projects.GetByIdAsync(project.Id))!;
        loaded.Description = "Saved whole";
        await fixture.Projects.SaveAsync(loaded);
        CollectionAssert.AreEqual(new[] { "gone", "two" }, (await fixture.Projects.GetByIdAsync(project.Id))!.Spaces);
        Assert.AreEqual(SpaceChangeStatus.Ok, (await fixture.Spaces.AssignAsync(project.Id, ["three"], null)).Status);
        CollectionAssert.AreEqual(new[] { "gone", "two", "three" }, (await fixture.Projects.GetByIdAsync(project.Id))!.Spaces);

        Assert.AreEqual(SpaceChangeStatus.NotFound, (await fixture.Spaces.AssignAsync(Guid.NewGuid().ToString(), ["one"], null)).Status);
        Assert.AreEqual(SpaceChangeStatus.NotFound, (await fixture.Spaces.AssignAsync(project.Id, ["missing"], null)).Status);
        Assert.AreEqual(SpaceChangeStatus.Refused, (await fixture.Spaces.AssignAsync(project.Id, ["default"], null)).Status);
        Assert.AreEqual(SpaceChangeStatus.Refused, (await fixture.Spaces.AssignAsync(project.Id, null, ["default"])).Status);
    }

    [TestMethod]
    public async Task TheProjectsOfASpace_AreTheOnesThatNameIt_AndDeletingItLeavesTheProjects()
    {
        using var fixture = new Fixture();
        var alpha = await fixture.Projects.UpsertFromPathAsync(fixture.Folder("alpha"));
        var beta = await fixture.Projects.UpsertFromPathAsync(fixture.Folder("beta"));
        var gamma = await fixture.Projects.UpsertFromPathAsync(fixture.Folder("gamma"));
        Assert.IsTrue((await fixture.Spaces.CreateAsync("Work")).Succeeded);
        Assert.IsTrue((await fixture.Spaces.CreateAsync("Personal")).Succeeded);
        // A project is in several spaces at once.
        await fixture.Spaces.AssignAsync(alpha.Id, ["work", "personal"], null);
        await fixture.Spaces.AssignAsync(beta.Id, ["work"], null);

        var members = await fixture.Spaces.LoadMembersAsync();
        CollectionAssert.AreEqual(new[] { "default", "work", "personal" }, members.Select(entry => entry.Space.Id).ToArray());
        CollectionAssert.AreEqual(new[] { alpha.Id, beta.Id, gamma.Id }, members[0].ProjectIds.ToArray(), "The default space holds every project.");
        CollectionAssert.AreEqual(new[] { alpha.Id, beta.Id }, members[1].ProjectIds.ToArray());
        CollectionAssert.AreEqual(new[] { alpha.Id }, members[2].ProjectIds.ToArray());

        // Concurrent changes of one project are made one after the other: none is lost.
        await Task.WhenAll(fixture.Spaces.AssignAsync(gamma.Id, ["work"], null), fixture.Spaces.AssignAsync(gamma.Id, ["personal"], null),
            fixture.Projects.UpdateSpacesAsync(beta.Id, current => current.Append("personal")));
        CollectionAssert.AreEquivalent(new[] { "work", "personal" }, (await fixture.Projects.GetByIdAsync(gamma.Id))!.Spaces);

        var removed = await fixture.Spaces.DeleteAsync("work");
        Assert.AreEqual(SpaceChangeStatus.Ok, removed.Status);
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Options.SpacesRoot, "work.md")));
        members = await fixture.Spaces.LoadMembersAsync();
        CollectionAssert.AreEqual(new[] { "default", "personal" }, members.Select(entry => entry.Space.Id).ToArray());
        Assert.AreEqual(3, members[0].ProjectIds.Count, "No project goes with a space.");
        CollectionAssert.AreEquivalent(new[] { alpha.Id, beta.Id, gamma.Id }, members[1].ProjectIds.ToArray());
        Assert.IsFalse((await fixture.Projects.LoadAsync()).Any(project => project.Spaces.Contains("work")), "No project file names a space that was deleted.");

        // An archived project stays in its spaces, and is left out where archived projects are.
        var evidence = (await fixture.Projects.ReadArchiveAsync(alpha.Id, alpha.ProjectPath))!;
        await fixture.Projects.SetArchivedAsync(alpha.Id, alpha.ProjectPath, evidence.SourcePath, evidence.Revision, false, true);
        Assert.AreEqual(2, (await fixture.Spaces.LoadMembersAsync(includeArchived: false))[1].ProjectIds.Count);
        Assert.AreEqual(3, (await fixture.Spaces.LoadMembersAsync())[1].ProjectIds.Count);
    }

    [TestMethod]
    public async Task Spaces_AreListedInTheOrderTheUserGivesThem()
    {
        using var fixture = new Fixture();
        foreach (var name in new[] { "One", "Two", "Three" }) Assert.IsTrue((await fixture.Spaces.CreateAsync(name)).Succeeded);
        Assert.AreEqual(SpaceChangeStatus.Ok, (await fixture.Spaces.ReorderAsync(["three", "default", "missing", "one"])).Status);
        CollectionAssert.AreEqual(new[] { "default", "three", "one", "two" }, (await fixture.Spaces.LoadAsync()).Select(space => space.Id).ToArray());
        // A new space comes last.
        Assert.IsTrue((await fixture.Spaces.CreateAsync("Four")).Succeeded);
        CollectionAssert.AreEqual(new[] { "default", "three", "one", "two", "four" }, (await fixture.Spaces.LoadAsync()).Select(space => space.Id).ToArray());
    }

    [TestMethod]
    public void TheIdentifierOfASpace_IsWorkedOutFromItsName()
    {
        Assert.AreEqual("open-source", SpaceDescriptor.IdFromName("  Open -- Source  "));
        Assert.AreEqual("r-d-2026", SpaceDescriptor.IdFromName("R&D 2026"));
        Assert.AreEqual("space", SpaceDescriptor.IdFromName("A"));
        Assert.AreEqual("space", SpaceDescriptor.IdFromName(null));
        Assert.AreEqual(64, SpaceDescriptor.IdFromName(new string('a', 80)).Length);
        CollectionAssert.AreEqual(new[] { "work", "a.b" }, SpaceDescriptor.NormalizeIds(["work", " work ", "default", "Not Valid", null, "a.b", "x"]));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "codealta-spaces-" + Guid.NewGuid().ToString("N"));
        public CatalogOptions Options { get; }
        public ProjectCatalog Projects { get; }
        public SpaceCatalog Spaces { get; }

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
