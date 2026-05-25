using System.Net;
using System.Reflection;
using CodeAlta.Plugin.GitHub;

namespace CodeAlta.Tests;

[TestClass]
public sealed class GitHubPluginTests
{
    [TestMethod]
    public async Task IssueReferenceAvailabilityDeclinesWhenProjectIsNotGitHubRepository()
    {
        using var tempDirectory = TempDirectory.Create();
        var plugin = new GitHubPlugin();

        var available = await plugin.CanResolveIssueReferencesAsync(tempDirectory.Path, CancellationToken.None);

        Assert.IsFalse(available);
    }

    [TestMethod]
    public async Task IssueReferenceAvailabilityDetectsGitHubRemote()
    {
        using var tempDirectory = TempDirectory.Create();
        Directory.CreateDirectory(Path.Combine(tempDirectory.Path, ".git"));
        File.WriteAllText(
            Path.Combine(tempDirectory.Path, ".git", "config"),
            """
            [remote "origin"]
                url = https://github.com/org/repo.git
            """);
        var plugin = new GitHubPlugin();

        var available = await plugin.CanResolveIssueReferencesAsync(tempDirectory.Path, CancellationToken.None);

        Assert.IsTrue(available);
    }

    [TestMethod]
    public async Task IssueReferenceAvailabilityFallsBackToCurrentDirectoryWhenNoProjectPathIsSelected()
    {
        using var tempDirectory = TempDirectory.Create();
        Directory.CreateDirectory(Path.Combine(tempDirectory.Path, ".git"));
        File.WriteAllText(
            Path.Combine(tempDirectory.Path, ".git", "config"),
            """
            [remote "origin"]
                url = git@github.com:org/repo.git
            """);
        var previousCurrentDirectory = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = tempDirectory.Path;
            var plugin = new GitHubPlugin();

            var available = await plugin.CanResolveIssueReferencesAsync(null, CancellationToken.None);

            Assert.IsTrue(available);
        }
        finally
        {
            Environment.CurrentDirectory = previousCurrentDirectory;
        }
    }

    [TestMethod]
    public async Task IssueReferenceQueryQuicklyDeclinesWhenProjectIsNotGitHubRepository()
    {
        using var tempDirectory = TempDirectory.Create();
        var plugin = new GitHubPlugin();

        var result = await plugin.QueryIssueReferencesAsync(tempDirectory.Path, "18", 10, CancellationToken.None);

        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task IssueReferenceQueryShowsRecentIssuesForEmptyQuery()
    {
        using var tempDirectory = TempDirectory.CreateGitHubRepository();
        await using var plugin = CreatePluginWithIssueJsonResponse(
            """
            [
              { "number": 123, "title": "Recent issue", "html_url": "https://github.com/org/repo/issues/123", "updated_at": "2026-05-25T10:00:00Z", "state": "open" },
              { "number": 45, "title": "Older issue", "html_url": "https://github.com/org/repo/issues/45", "updated_at": "2026-05-24T10:00:00Z", "state": "closed" }
            ]
            """);

        var result = await plugin.QueryIssueReferencesAsync(tempDirectory.Path, string.Empty, 10, CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.AreEqual(2, result.Count);
        Assert.AreEqual(123, result[0].Number);
        Assert.AreEqual(45, result[1].Number);
    }

    [TestMethod]
    public async Task IssueReferenceQueryFiltersRecentIssuesForShortNumericQuery()
    {
        using var tempDirectory = TempDirectory.CreateGitHubRepository();
        await using var plugin = CreatePluginWithIssueJsonResponse(
            """
            [
              { "number": 123, "title": "Matching issue", "html_url": "https://github.com/org/repo/issues/123", "updated_at": "2026-05-25T10:00:00Z", "state": "open" },
              { "number": 45, "title": "Other issue", "html_url": "https://github.com/org/repo/issues/45", "updated_at": "2026-05-24T10:00:00Z", "state": "open" }
            ]
            """);

        var result = await plugin.QueryIssueReferencesAsync(tempDirectory.Path, "1", 10, CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(123, result[0].Number);
    }

    [TestMethod]
    public void PluginContributesPromptEditorAttachment()
    {
        var contribution = new GitHubPlugin().GetPromptEditorContributions().Single();

        Assert.AreEqual("GitHub issue prompt picker", contribution.Name);
        Assert.IsNotNull(contribution.Attach);
    }

    [TestMethod]
    public void GitHubCliToolRequiresStructuredArgumentArray()
    {
        var plugin = new GitHubPlugin();
        SetPrivateField(plugin, "_ghAvailable", true);

        var contribution = plugin.GetAgentTools().Single();
        var schema = contribution.Definition.Spec.InputSchema;
        var properties = schema.GetProperty("properties");
        var arguments = properties.GetProperty("arguments");

        Assert.AreEqual("gh", contribution.Definition.Spec.Name);
        Assert.AreEqual("array", arguments.GetProperty("type").GetString());
        Assert.AreEqual("string", arguments.GetProperty("items").GetProperty("type").GetString());
        Assert.IsFalse(properties.TryGetProperty("command", out _));
        StringAssert.Contains(contribution.PromptGuidance, "Pass arguments as an array");
    }

    private static GitHubPlugin CreatePluginWithIssueJsonResponse(string responseJson)
    {
        var plugin = new GitHubPlugin();
        var client = new HttpClient(new FakeIssueHandler(responseJson));
        SetPrivateField(plugin, "_httpClient", client);
        return plugin;
    }

    private static void SetPrivateField(GitHubPlugin plugin, string name, object? value)
    {
        var field = typeof(GitHubPlugin).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field);
        field.SetValue(plugin, value);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
            => Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CodeAlta.GitHubPluginTests." + Guid.NewGuid().ToString("N"));

        public string Path { get; }

        public static TempDirectory Create()
        {
            var directory = new TempDirectory();
            Directory.CreateDirectory(directory.Path);
            return directory;
        }

        public static TempDirectory CreateGitHubRepository()
        {
            var directory = Create();
            Directory.CreateDirectory(System.IO.Path.Combine(directory.Path, ".git"));
            File.WriteAllText(
                System.IO.Path.Combine(directory.Path, ".git", "config"),
                """
                [remote "origin"]
                    url = https://github.com/org/repo.git
                """);
            return directory;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private sealed class FakeIssueHandler(string responseJson) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.AreEqual("/repos/org/repo/issues", request.RequestUri?.AbsolutePath);
            Assert.IsTrue(request.RequestUri?.Query.Contains("state=all", StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson),
            });
        }
    }
}
