using CodeAlta.Catalog.StartPrompts;

namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed class StartPromptCatalogTests
{
    [TestMethod]
    public void TheReviewOfAPullRequest_StartsWithWhatShips_UntilAFileReplacesIt()
    {
        using var root = new Root();
        var catalog = new StartPromptCatalog(root.Options);
        var shipped = StartPromptCatalog.BuiltIn(StartPromptCatalog.PullRequestReviewId);
        StringAssert.StartsWith(shipped, "Review the pull request {reference} of {location} on {service}:");
        StringAssert.Contains(shipped, "Do not merge it, push to it or comment on it unless I ask.");
        Assert.AreEqual(shipped, catalog.Text(StartPromptCatalog.PullRequestReviewId, root.Project));
        Assert.AreEqual(1, catalog.List(root.Project).Count, "Without files there is only what ships.");

        catalog.Save(null, "Pull-Request-Review", "Mine.\r\n\r\nFor {reference}.");
        Assert.AreEqual("Mine.\n\nFor {reference}.", catalog.Text(StartPromptCatalog.PullRequestReviewId, root.Project));
        catalog.Save(root.Project, StartPromptCatalog.PullRequestReviewId, "The team's.");
        Assert.AreEqual("The team's.", catalog.Text(StartPromptCatalog.PullRequestReviewId, root.Project));
        Assert.AreEqual("Mine.\n\nFor {reference}.", catalog.Text(StartPromptCatalog.PullRequestReviewId, null), "A project's text is for that project alone.");
        CollectionAssert.AreEqual(new[] { (StartPromptSource.BuiltIn, true), (StartPromptSource.Global, true), (StartPromptSource.Project, false) },
            catalog.List(root.Project).Select(static prompt => (prompt.Source, prompt.Overridden)).ToArray());
        Assert.AreEqual(Path.Combine(root.Project, ".alta", "prompts", "start", "pull-request-review.md"), catalog.List(root.Project)[2].Path);

        // An empty file is no text: the next one is used.
        File.WriteAllText(Path.Combine(StartPromptCatalog.ProjectFolder(root.Project), "pull-request-review.md"), " \n");
        Assert.AreEqual("Mine.\n\nFor {reference}.", catalog.Text(StartPromptCatalog.PullRequestReviewId, root.Project));

        Assert.IsTrue(catalog.Delete(null, StartPromptCatalog.PullRequestReviewId));
        Assert.IsFalse(catalog.Delete(null, StartPromptCatalog.PullRequestReviewId));
        Assert.AreEqual(shipped, catalog.Text(StartPromptCatalog.PullRequestReviewId, root.Project));
    }

    [TestMethod]
    public void OnlyTheStartPromptsOfCodeAlta_AreWritten_AndNeverEmpty()
    {
        using var root = new Root();
        var catalog = new StartPromptCatalog(root.Options);

        Assert.ThrowsExactly<ArgumentException>(() => catalog.Save(null, "../config", "Text."));
        Assert.ThrowsExactly<ArgumentException>(() => catalog.Save(null, "issue", "Text."));
        Assert.ThrowsExactly<ArgumentException>(() => catalog.Save(null, StartPromptCatalog.PullRequestReviewId, "  "));
        Assert.ThrowsExactly<ArgumentException>(() => catalog.Save(null, StartPromptCatalog.PullRequestReviewId, new string('x', StartPromptCatalog.MaximumContent + 1)));
        Assert.ThrowsExactly<ArgumentException>(() => catalog.Text("issue", null));
        Assert.IsFalse(catalog.Delete(null, "../config"));
        Assert.IsFalse(Directory.Exists(catalog.GlobalFolder));
    }

    [TestMethod]
    public void AFolderOrAFileThatIsALink_IsNeitherReadNorWrittenNorDeleted()
    {
        using var root = new Root();
        var catalog = new StartPromptCatalog(root.Options);
        // The folder of the user's prompts, which a link of a project must not reach.
        var mine = catalog.Save(null, StartPromptCatalog.PullRequestReviewId, "Mine.");
        var folder = StartPromptCatalog.ProjectFolder(root.Project);
        Directory.CreateDirectory(Path.GetDirectoryName(folder)!);
        try { Directory.CreateSymbolicLink(folder, catalog.GlobalFolder); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Inconclusive("Creating links requires platform permission.");
        }

        Assert.AreEqual(1, catalog.List(root.Project).Count(static prompt => prompt.Source != StartPromptSource.BuiltIn), "The user's file is not read again as the project's.");
        Assert.ThrowsExactly<UnauthorizedAccessException>(() => catalog.Save(root.Project, StartPromptCatalog.PullRequestReviewId, "The team's."));
        Assert.ThrowsExactly<UnauthorizedAccessException>(() => catalog.Delete(root.Project, StartPromptCatalog.PullRequestReviewId));
        Assert.AreEqual("Mine.\n", File.ReadAllText(mine.Path!));
        Assert.AreEqual(1, Directory.GetFiles(catalog.GlobalFolder).Length, "A refused write leaves no staged file.");

        // A file that is a link is not read either.
        Directory.Delete(folder);
        Directory.CreateDirectory(folder);
        File.CreateSymbolicLink(Path.Combine(folder, "pull-request-review.md"), mine.Path!);
        Assert.AreEqual("Mine.", catalog.Text(StartPromptCatalog.PullRequestReviewId, root.Project));
        Assert.ThrowsExactly<UnauthorizedAccessException>(() => catalog.Delete(root.Project, StartPromptCatalog.PullRequestReviewId));
        Assert.IsTrue(File.Exists(mine.Path!));
    }

    [TestMethod]
    public void ThePlaceholders_AreFilledOnce_AndOtherBracesAreKept()
    {
        var values = new Dictionary<string, string> { ["reference"] = "#30", ["location"] = "{service}", ["service"] = "GitHub" };

        Assert.AreEqual("Review #30 of {service} on GitHub: keep {this} and { braces }.",
            StartPromptCatalog.Fill("Review {reference} of {location} on {service}: keep {this} and { braces }.", values));
        Assert.AreEqual("{ {#30} }", StartPromptCatalog.Fill("{ {{reference}} }", values));
        Assert.AreEqual("Unclosed {reference", StartPromptCatalog.Fill("Unclosed {reference", values));
    }

    private sealed class Root : IDisposable
    {
        private readonly string _path = Directory.CreateTempSubdirectory("codealta-start-prompts-").FullName;

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
