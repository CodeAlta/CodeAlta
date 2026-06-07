using System.Diagnostics;
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
        using var tempDirectory = TempDirectory.CreateGitHubRepository();
        var plugin = new GitHubPlugin();

        var available = await plugin.CanResolveIssueReferencesAsync(tempDirectory.Path, CancellationToken.None);

        Assert.IsTrue(available);
    }

    [TestMethod]
    public async Task IssueReferenceQueryUsesOriginRemoteWhenSubmoduleUrlAppearsFirst()
    {
        using var tempDirectory = TempDirectory.CreateGitHubRepositoryWithSubmoduleBeforeOrigin();
        await using var plugin = CreatePluginWithIssueJsonResponse(
            """
            [
              { "number": 123, "title": "Origin issue", "html_url": "https://github.com/org/repo/issues/123", "updated_at": "2026-05-25T10:00:00Z", "state": "open" }
            ]
            """);

        var result = await plugin.QueryIssueReferencesAsync(tempDirectory.Path, string.Empty, 10, CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(123, result[0].Number);
        Assert.AreEqual("org/repo", result[0].Repository);
    }

    [TestMethod]
    public async Task IssueReferenceAvailabilityFallsBackToCurrentDirectoryWhenNoProjectPathIsSelected()
    {
        using var tempDirectory = TempDirectory.CreateGitHubRepository("git@github.com:org/repo.git");
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
    public async Task IssueReferenceQueryFetchesExactIssueForNumericQuery()
    {
        using var tempDirectory = TempDirectory.CreateGitHubRepository();
        await using var plugin = CreatePluginWithHttpHandler(new ExactIssueHandler());

        var result = await plugin.QueryIssueReferencesAsync(tempDirectory.Path, "123", 10, CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(123, result[0].Number);
        Assert.AreEqual("Exact issue", result[0].Title);
    }

    [TestMethod]
    public async Task IssueReferenceQueryPagesRecentIssuesPastPullRequests()
    {
        using var tempDirectory = TempDirectory.CreateGitHubRepository();
        await using var plugin = CreatePluginWithHttpHandler(new PagedIssueHandler());

        var result = await plugin.QueryIssueReferencesAsync(tempDirectory.Path, string.Empty, 10, CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(456, result[0].Number);
    }

    [TestMethod]
    public async Task IssueReferenceQueryFiltersRecentIssuesCaseInsensitivelyForWordQuery()
    {
        using var tempDirectory = TempDirectory.CreateGitHubRepository();
        await using var plugin = CreatePluginWithIssueJsonResponse(
            """
            [
              { "number": 123, "title": "Improve GitHub PICKER search", "html_url": "https://github.com/org/repo/issues/123", "updated_at": "2026-05-25T10:00:00Z", "state": "open" },
              { "number": 45, "title": "Other issue", "html_url": "https://github.com/org/repo/issues/45", "updated_at": "2026-05-24T10:00:00Z", "state": "open" }
            ]
            """);

        var result = await plugin.QueryIssueReferencesAsync(tempDirectory.Path, "picker", 1, CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(123, result[0].Number);
    }

    [TestMethod]
    public void PluginContributesPromptEditorAttachment()
    {
        var contribution = new GitHubPlugin().GetPromptEditorContributions().Single();

        Assert.AreEqual("GitHub issue prompt picker", contribution.Name);
        Assert.AreEqual("[#] to reference a GitHub issue", contribution.PlaceholderText);
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
        => CreatePluginWithHttpHandler(new FakeIssueHandler(responseJson));

    private static GitHubPlugin CreatePluginWithHttpHandler(HttpMessageHandler handler)
    {
        var plugin = new GitHubPlugin();
        var client = new HttpClient(handler);
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

        public static TempDirectory CreateGitHubRepository(string remoteUrl = "https://github.com/org/repo.git")
        {
            var directory = Create();
            InitializeGitRepository(directory.Path);
            RunGitOrFail(directory.Path, ["remote", "add", "origin", remoteUrl]);
            return directory;
        }

        public static TempDirectory CreateGitHubRepositoryWithSubmoduleBeforeOrigin()
        {
            var directory = Create();
            InitializeGitRepository(directory.Path);
            RunGitOrFail(directory.Path, ["config", "submodule.ext/dependency.url", "https://github.com/org/dependency.git"]);
            RunGitOrFail(directory.Path, ["config", "submodule.ext/dependency.active", "true"]);
            RunGitOrFail(directory.Path, ["remote", "add", "origin", "https://github.com/org/repo.git"]);
            return directory;
        }

        private static void InitializeGitRepository(string path)
            => RunGitOrInconclusive(path, ["init", "-q"]);

        private static void RunGitOrInconclusive(string workingDirectory, IReadOnlyList<string> arguments)
        {
            if (!TryRunGit(workingDirectory, arguments, out var error))
            {
                Assert.Inconclusive("git is required for GitHub repository detection tests: " + error);
            }
        }

        private static void RunGitOrFail(string workingDirectory, IReadOnlyList<string> arguments)
        {
            if (!TryRunGit(workingDirectory, arguments, out var error))
            {
                Assert.Fail("git command failed: " + error);
            }
        }

        private static bool TryRunGit(string workingDirectory, IReadOnlyList<string> arguments, out string error)
        {
            error = string.Empty;
            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "git",
                        WorkingDirectory = workingDirectory,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                    },
                };
                foreach (var argument in arguments)
                {
                    process.StartInfo.ArgumentList.Add(argument);
                }

                process.Start();
                if (!process.WaitForExit(10_000))
                {
                    TryKill(process);
                    error = "git timed out.";
                    return false;
                }

                var stdout = process.StandardOutput.ReadToEnd();
                var stderr = process.StandardError.ReadToEnd();
                error = string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim();
                return process.ExitCode == 0;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }
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
            if (request.RequestUri?.AbsolutePath.StartsWith("/repos/org/repo/issues/", StringComparison.OrdinalIgnoreCase) == true)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            Assert.AreEqual("/repos/org/repo/issues", request.RequestUri?.AbsolutePath);
            Assert.IsTrue(request.RequestUri?.Query.Contains("state=all", StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson),
            });
        }
    }

    private sealed class ExactIssueHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath == "/repos/org/repo/issues/123")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        { "number": 123, "title": "Exact issue", "html_url": "https://github.com/org/repo/issues/123", "updated_at": "2026-05-25T10:00:00Z", "state": "open" }
                        """),
                });
            }

            Assert.AreEqual("/repos/org/repo/issues", request.RequestUri?.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]"),
            });
        }
    }

    private sealed class PagedIssueHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.AreEqual("/repos/org/repo/issues", request.RequestUri?.AbsolutePath);
            Assert.IsTrue(request.RequestUri?.Query.Contains("state=all", StringComparison.OrdinalIgnoreCase));
            var content = request.RequestUri?.Query.Contains("&page=1", StringComparison.OrdinalIgnoreCase) == true
                ? BuildPullRequestPage()
                : """
                  [
                    { "number": 456, "title": "Paged issue", "html_url": "https://github.com/org/repo/issues/456", "updated_at": "2026-05-24T10:00:00Z", "state": "open" }
                  ]
                  """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content),
            });
        }

        private static string BuildPullRequestPage()
        {
            var items = Enumerable.Range(1, 100).Select(static number => $$"""
                { "number": {{number}}, "title": "PR {{number}}", "html_url": "https://github.com/org/repo/pull/{{number}}", "updated_at": "2026-05-25T10:00:00Z", "state": "open", "pull_request": {} }
                """);
            return "[" + string.Join(",", items) + "]";
        }
    }
}
