using CodeAlta.Agent;
using CodeAlta.Presentation.Prompting;
using CodeAlta.Catalog;
using XenoAtom.Terminal.UI.Text;
using XenoAtom.Terminal.UI.Styling;

namespace CodeAlta.Tests;

[TestClass]
public sealed class ProjectFilePromptReferenceTests
{
    [TestMethod]
    public void Parse_HandlesEscapesQuotedPathsRangesAndEmailFalsePositives()
    {
        var tokens = ProjectFilePromptReferenceParser.Parse(
            "mail name@example.com @@ literal @\"src/My File.cs\":10-12 and @docs/readme.md");

        Assert.AreEqual(3, tokens.Count);
        Assert.AreEqual(ProjectFilePromptTokenKind.EscapedAt, tokens[0].Kind);
        Assert.AreEqual(ProjectFilePromptTokenKind.Reference, tokens[1].Kind);
        Assert.AreEqual("src/My File.cs", tokens[1].LookupText);
        Assert.AreEqual(10, tokens[1].LineRange!.StartLine);
        Assert.AreEqual(12, tokens[1].LineRange!.EndLine);
        Assert.AreEqual(ProjectFilePromptTokenKind.Reference, tokens[2].Kind);
        Assert.AreEqual("docs/readme.md", tokens[2].LookupText);
    }

    [TestMethod]
    public void Parse_RecognizesProjectRelativeMarkdownLinks()
    {
        var tokens = ProjectFilePromptReferenceParser.Parse(
            "See [Program.cs](src/Program.cs:10-12) and [site](https://example.com)");

        Assert.AreEqual(1, tokens.Count);
        Assert.AreEqual(ProjectFilePromptTokenKind.Reference, tokens[0].Kind);
        Assert.AreEqual("Program.cs", tokens[0].DisplayText);
        Assert.AreEqual("src/Program.cs", tokens[0].LookupText);
        Assert.AreEqual(10, tokens[0].LineRange!.StartLine);
        Assert.AreEqual(12, tokens[0].LineRange!.EndLine);
    }

    [TestMethod]
    public async Task BuildAsync_NormalizesResolvedReferencesAndCreatesAttachments()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"CodeAlta.ProjectFiles.{Guid.NewGuid():N}");
        Directory.CreateDirectory(projectRoot);
        var service = new FakeProjectFileSearchService(
            [
                CreateItem(projectRoot, "src/My File.cs"),
                CreateItem(projectRoot, "docs", ProjectFileSearchItemKind.Directory),
            ]);

        var result = await ProjectFilePromptInputBuilder.BuildAsync(
            "Review @@literal @\"src/My File.cs\":10-12 and @docs",
            projectRoot,
            service).ConfigureAwait(false);

        Assert.AreEqual("Review @literal [My File.cs](src/My File.cs:10-12) and [docs](docs)", result.NormalizedPromptText);
        Assert.AreEqual(2, result.ResolvedReferences.Count);
        Assert.AreEqual(3, result.Input.Items.Count);
        Assert.AreEqual(result.NormalizedPromptText, ((AgentInputItem.Text)result.Input.Items[0]).Value);
        var file = (AgentInputItem.File)result.Input.Items[1];
        Assert.AreEqual(Path.Combine(projectRoot, "src", "My File.cs"), file.Path);
        Assert.AreEqual("src/My File.cs", file.DisplayName);
        Assert.AreEqual(new AgentLineRange(10, 12), file.LineRange);
        var directory = (AgentInputItem.Directory)result.Input.Items[2];
        Assert.AreEqual(Path.Combine(projectRoot, "docs"), directory.Path);
        Assert.AreEqual("docs", directory.DisplayName);
    }

    [TestMethod]
    public async Task BuildAsync_ResolvesMarkdownLinksAndCreatesAttachments()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"CodeAlta.ProjectFiles.{Guid.NewGuid():N}");
        Directory.CreateDirectory(projectRoot);
        var service = new FakeProjectFileSearchService(
            [
                CreateItem(projectRoot, "src/My File.cs"),
                CreateItem(projectRoot, "docs", ProjectFileSearchItemKind.Directory),
            ]);

        var result = await ProjectFilePromptInputBuilder.BuildAsync(
            "Review [Source file](src/My File.cs:10-12) and [Project docs](docs)",
            projectRoot,
            service).ConfigureAwait(false);

        Assert.AreEqual("Review [Source file](src/My File.cs:10-12) and [Project docs](docs)", result.NormalizedPromptText);
        Assert.AreEqual(2, result.ResolvedReferences.Count);
        Assert.AreEqual(3, result.Input.Items.Count);
        Assert.AreEqual(new AgentLineRange(10, 12), ((AgentInputItem.File)result.Input.Items[1]).LineRange);
        Assert.AreEqual("docs", ((AgentInputItem.Directory)result.Input.Items[2]).DisplayName);
    }

    [TestMethod]
    public async Task BuildAsync_LeavesUnresolvedReferencesUntouched()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"CodeAlta.ProjectFiles.{Guid.NewGuid():N}");
        Directory.CreateDirectory(projectRoot);
        var service = new FakeProjectFileSearchService([]);

        var result = await ProjectFilePromptInputBuilder.BuildAsync(
            "Review @missing and @@literal",
            projectRoot,
            service).ConfigureAwait(false);

        Assert.AreEqual("Review @missing and @literal", result.NormalizedPromptText);
        Assert.AreEqual(0, result.ResolvedReferences.Count);
        Assert.AreEqual(1, result.Input.Items.Count);
        Assert.AreEqual("Review @missing and @literal", ((AgentInputItem.Text)result.Input.Items[0]).Value);
    }

    [TestMethod]
    public void Highlighter_AddRuns_StylesResolvedAndUnresolvedReferences()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"CodeAlta.ProjectFiles.{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
        File.WriteAllText(Path.Combine(projectRoot, "src", "app.cs"), string.Empty);

        var runs = new List<StyledRun>();
        ProjectFilePromptHighlighter.AddRuns("See [app.cs](src/app.cs) and @missing.cs", projectRoot, Theme.Default, runs);

        Assert.AreEqual(2, runs.Count);
        Assert.AreEqual("[app.cs](src/app.cs)".Length, runs[0].Length);
        Assert.AreNotEqual(runs[0].Style, runs[1].Style);
    }

    private static ProjectFileSearchItem CreateItem(
        string projectRoot,
        string relativePath,
        ProjectFileSearchItemKind kind = ProjectFileSearchItemKind.File)
    {
        var basename = Path.GetFileName(relativePath);
        var extension = kind == ProjectFileSearchItemKind.Directory ? string.Empty : Path.GetExtension(basename);
        return new ProjectFileSearchItem
        {
            Kind = kind,
            ProjectRoot = projectRoot,
            RelativePath = relativePath.Replace('\\', '/'),
            FullPath = Path.Combine(projectRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)),
            Basename = basename,
            ParentPath = Path.GetDirectoryName(relativePath)?.Replace('\\', '/') ?? string.Empty,
            Extension = extension,
            LastWriteTimeUtc = DateTimeOffset.UtcNow,
            SearchFields = new ProjectFileSearchFields(
                basename.ToLowerInvariant(),
                relativePath.ToLowerInvariant(),
                relativePath.Split('/').Select(static segment => segment.ToLowerInvariant()).ToArray(),
                extension.ToLowerInvariant()),
            Usage = null,
        };
    }

    private sealed class FakeProjectFileSearchService(IReadOnlyList<ProjectFileSearchItem> items) : IProjectFileSearchService
    {
        public ValueTask<IProjectFileSearchSession> CreateSessionAsync(
            ProjectFileSearchSessionOptions options,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<ProjectFileResolution> ResolveAsync(
            ProjectFileResolveQuery query,
            CancellationToken cancellationToken = default)
        {
            var normalized = query.ReferenceText.Replace('\\', '/').Trim().Trim('"');
            var item = items.FirstOrDefault(candidate => string.Equals(candidate.RelativePath, normalized, StringComparison.OrdinalIgnoreCase));
            return ValueTask.FromResult(
                item is null
                    ? new ProjectFileResolution(false, normalized, Item: null, query.LineRange)
                    : new ProjectFileResolution(true, normalized, item, query.LineRange));
        }

        public ValueTask RecordUsageAsync(ProjectFileUsageEvent usageEvent, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask InvalidateAsync(string projectRoot, ProjectFileInvalidationReason reason, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }
}
