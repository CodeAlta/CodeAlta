using CodeAlta.Catalog.PullRequests;

namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed class PullRequestPromptCatalogTests
{
    [TestMethod]
    public void TheInstructionsThatShip_SayWhatAReviewerNeeds_AndWhatIsNeverDone()
    {
        var prompt = PullRequestPromptCatalog.Default;

        Assert.AreEqual((PullRequestPromptCatalog.DefaultId, "Default", PullRequestPromptSource.BuiltIn, (string?)null), (prompt.Id, prompt.Name, prompt.Source, prompt.Path));
        Assert.IsFalse(string.IsNullOrWhiteSpace(prompt.Description));
        StringAssert.StartsWith(prompt.Content, "Create a pull request for the work of this session.");
        foreach (var part in new[] { "gh pr create", "glab mr create", "az repos pr create", "Do not merge", "do not force-push", "say what it is and stop" })
        {
            StringAssert.Contains(prompt.Content, part);
        }

        Assert.IsFalse(prompt.Content.Contains("---", StringComparison.Ordinal), "The front matter is not part of what is sent.");
    }

    [TestMethod]
    public void AKindNearerToTheProject_TakesThePlaceOfOneWithItsName()
    {
        using var root = new Root();
        var catalog = new PullRequestPromptCatalog(root.Options);
        Assert.AreEqual(1, catalog.List(root.Project).Count, "Without files there is only what ships.");

        catalog.Save(null, "Release", "Release notes", "With the notes of the release.", "Open the pull request of a release.\r\n\r\nAdd the notes.");
        catalog.Save(null, "default", null, null, "My own default.");
        catalog.Save(root.Project, "default", "Team default", "As the team does it.", "The default of the project.");
        File.WriteAllText(Path.Combine(catalog.GlobalFolder, "notes.md"), "Not a kind.");
        File.WriteAllText(Path.Combine(catalog.GlobalFolder, "bad name.pr.md"), "Not a kind either.");
        File.WriteAllText(Path.Combine(catalog.GlobalFolder, "empty.pr.md"), "---\nname: Empty\n---\n");

        var all = catalog.List(root.Project);
        CollectionAssert.AreEqual(new[] { ("default", PullRequestPromptSource.BuiltIn, true), ("default", PullRequestPromptSource.Global, true), ("release", PullRequestPromptSource.Global, false), ("default", PullRequestPromptSource.Project, false) },
            all.Select(static prompt => (prompt.Id, prompt.Source, prompt.Overridden)).ToArray());
        Assert.AreEqual(("Release notes", "With the notes of the release.", "Open the pull request of a release.\n\nAdd the notes."), (all[2].Name, all[2].Description, all[2].Content));
        Assert.AreEqual("default", all[1].Name, "A kind without a name is named by its file.");
        File.WriteAllText(Path.Combine(catalog.GlobalFolder, "plain.pr.md"), "Only the instructions.\n");
        Assert.AreEqual(("plain", (string?)null, "Only the instructions."), catalog.List(null).Where(static prompt => prompt.Id == "plain").Select(static prompt => (prompt.Name, prompt.Description, prompt.Content)).Single(),
            "A file needs no header.");
        File.Delete(Path.Combine(catalog.GlobalFolder, "plain.pr.md"));

        var effective = catalog.Effective(root.Project);
        CollectionAssert.AreEqual(new[] { ("default", "Team default"), ("release", "Release notes") }, effective.Select(static prompt => (prompt.Id, prompt.Name)).ToArray(), "The default one first.");
        CollectionAssert.AreEqual(new[] { "My own default.", "Open the pull request of a release.\n\nAdd the notes." }, catalog.Effective(null).Select(static prompt => prompt.Content).ToArray(),
            "Another project does not get the kinds of this one.");

        Assert.IsTrue(catalog.Delete(root.Project, "DEFAULT"));
        Assert.IsFalse(catalog.Delete(root.Project, "default"));
        Assert.IsFalse(catalog.Delete(null, "../config"));
        Assert.AreEqual("My own default.", catalog.Effective(root.Project)[0].Content);
    }

    [TestMethod]
    public void AKind_IsWrittenAsAFileThatIsReadBackAsItWasGiven()
    {
        using var root = new Root();
        var catalog = new PullRequestPromptCatalog(root.Options);

        var saved = catalog.Save(root.Project, "hot-fix", "Hot \"fix\"", "One line\nonly.", "  Cherry-pick, then open it against `release`.  ");

        Assert.AreEqual(Path.Combine(PullRequestPromptCatalog.ProjectFolder(root.Project), "hot-fix.pr.md"), saved.Path);
        Assert.AreEqual("---\nname: \"Hot \\\"fix\\\"\"\ndescription: \"One line only.\"\n---\nCherry-pick, then open it against `release`.\n", File.ReadAllText(saved.Path!));
        var read = catalog.List(root.Project).Single(static prompt => prompt.Id == "hot-fix");
        Assert.AreEqual(("Hot \"fix\"", "One line only.", "Cherry-pick, then open it against `release`.", PullRequestPromptSource.Project), (read.Name, read.Description, read.Content, read.Source));
        Assert.IsEmpty(Directory.GetFiles(PullRequestPromptCatalog.ProjectFolder(root.Project), "*.tmp"));

        // Instructions that begin with a rule are still instructions, not a header.
        Assert.AreEqual("---\nA rule first.", catalog.Save(null, "ruled", null, null, "---\nA rule first.").Content);
        Assert.AreEqual("---\nA rule first.", catalog.List(null).Single(static prompt => prompt.Id == "ruled").Content);

        Assert.ThrowsExactly<ArgumentException>(() => catalog.Save(null, "bad name", null, null, "Text."));
        Assert.ThrowsExactly<ArgumentException>(() => catalog.Save(null, "empty", null, null, "   "));
        Assert.ThrowsExactly<ArgumentException>(() => catalog.Save(null, "long", null, null, new string('x', PullRequestPromptCatalog.MaximumContent + 1)));
        Assert.AreEqual("release_2", PullRequestPromptCatalog.NormalizeId(" Release_2 "));
        Assert.IsNull(PullRequestPromptCatalog.NormalizeId("a.b"));
    }

    private sealed class Root : IDisposable
    {
        private readonly string _path = Directory.CreateTempSubdirectory("codealta-pr-prompts-").FullName;

        public Root()
        {
            Options = new CatalogOptions { GlobalRoot = Directory.CreateDirectory(Path.Combine(_path, "global")).FullName };
            Project = Directory.CreateDirectory(Path.Combine(_path, "project")).FullName;
        }

        public CatalogOptions Options { get; }

        public string Project { get; }

        public void Dispose()
        {
            try { Directory.Delete(_path, recursive: true); } catch (IOException) { }
        }
    }
}
