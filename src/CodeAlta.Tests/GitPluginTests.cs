using System.Diagnostics;
using System.Net;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Plugin.Git;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Tests;

[TestClass]
public sealed class GitPluginTests
{
    [TestMethod]
    public async Task IssueReferenceAvailabilityDeclinesWhenProjectHasNoSupportedRemote()
    {
        using var tempDirectory = TempDirectory.Create();
        var plugin = new GitPlugin();

        Assert.IsFalse(await plugin.CanResolveIssueReferencesAsync(tempDirectory.Path, CancellationToken.None));

        using var other = TempDirectory.CreateRepository("https://example.com/org/repo.git");
        Assert.IsFalse(await plugin.CanResolveIssueReferencesAsync(other.Path, CancellationToken.None));
        Assert.IsNull(await plugin.ResolveIssueRepositoryAsync(other.Path, CancellationToken.None));
    }

    [TestMethod]
    public async Task IssueReferenceAvailabilityDetectsTheProviderOfTheRemote()
    {
        foreach (var (remoteUrl, provider, fullName) in new[]
                 {
                     ("https://github.com/org/repo.git", GitRemoteProvider.GitHub, "org/repo"),
                     ("git@gitlab.com:group/sub/project.git", GitRemoteProvider.GitLab, "group/sub/project"),
                     ("https://dev.azure.com/org/Project/_git/repo", GitRemoteProvider.AzureDevOps, "org/Project/repo"),
                 })
        {
            using var tempDirectory = TempDirectory.CreateRepository(remoteUrl);
            var plugin = new GitPlugin();

            Assert.IsTrue(await plugin.CanResolveIssueReferencesAsync(tempDirectory.Path, CancellationToken.None), remoteUrl);
            var repository = await plugin.ResolveIssueRepositoryAsync(tempDirectory.Path, CancellationToken.None);
            Assert.IsNotNull(repository, remoteUrl);
            Assert.AreEqual(provider, repository.Provider);
            Assert.AreEqual(fullName, repository.FullName);
        }
    }

    [TestMethod]
    public async Task IssueReferenceQueryUsesOriginRemoteWhenSubmoduleUrlAppearsFirst()
    {
        using var tempDirectory = TempDirectory.CreateRepositoryWithSubmoduleBeforeOrigin();
        await using var plugin = await CreateInitializedPluginAsync(new FakeIssueHandler(
            """
            [
              { "number": 123, "title": "Origin issue", "html_url": "https://github.com/org/repo/issues/123", "updated_at": "2026-05-25T10:00:00Z", "state": "open" }
            ]
            """));

        var result = await plugin.QueryIssueReferencesAsync(tempDirectory.Path, string.Empty, 10, CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(123, result[0].Number);
        Assert.AreEqual("org/repo", result[0].Repository);
    }

    [TestMethod]
    public async Task IssueReferenceAvailabilityFallsBackToCurrentDirectoryWhenNoProjectPathIsSelected()
    {
        using var tempDirectory = TempDirectory.CreateRepository("git@github.com:org/repo.git");
        var previousCurrentDirectory = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = tempDirectory.Path;
            var plugin = new GitPlugin();

            var available = await plugin.CanResolveIssueReferencesAsync(null, CancellationToken.None);

            Assert.IsTrue(available);
        }
        finally
        {
            Environment.CurrentDirectory = previousCurrentDirectory;
        }
    }

    [TestMethod]
    public async Task IssueReferenceQueryDeclinesBeforeInitializationAndWithoutASupportedRemote()
    {
        using var gitHub = TempDirectory.CreateRepository("https://github.com/org/repo.git");
        Assert.IsNull(await new GitPlugin().QueryIssueReferencesAsync(gitHub.Path, "18", 10, CancellationToken.None), "A backend that was not initialized has no client.");

        using var tempDirectory = TempDirectory.Create();
        await using var plugin = await CreateInitializedPluginAsync(new FakeIssueHandler("[]"));
        Assert.IsNull(await plugin.QueryIssueReferencesAsync(tempDirectory.Path, "18", 10, CancellationToken.None));
    }

    [TestMethod]
    public async Task IssueReferenceQueryShowsRecentIssuesForEmptyQuery()
    {
        using var tempDirectory = TempDirectory.CreateRepository();
        await using var plugin = await CreateInitializedPluginAsync(new FakeIssueHandler(
            """
            [
              { "number": 123, "title": "Recent issue", "html_url": "https://github.com/org/repo/issues/123", "updated_at": "2026-05-25T10:00:00Z", "state": "open" },
              { "number": 45, "title": "Older issue", "html_url": "https://github.com/org/repo/issues/45", "updated_at": "2026-05-24T10:00:00Z", "state": "closed" }
            ]
            """));

        var result = await plugin.QueryIssueReferencesAsync(tempDirectory.Path, string.Empty, 10, CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.AreEqual(2, result.Count);
        Assert.AreEqual(123, result[0].Number);
        Assert.IsTrue(result[0].IsOpen);
        Assert.AreEqual(45, result[1].Number);
        Assert.IsFalse(result[1].IsOpen);
    }

    [TestMethod]
    public async Task IssueReferenceQueryFiltersRecentIssuesForShortNumericQuery()
    {
        using var tempDirectory = TempDirectory.CreateRepository();
        await using var plugin = await CreateInitializedPluginAsync(new FakeIssueHandler(
            """
            [
              { "number": 123, "title": "Matching issue", "html_url": "https://github.com/org/repo/issues/123", "updated_at": "2026-05-25T10:00:00Z", "state": "open" },
              { "number": 45, "title": "Other issue", "html_url": "https://github.com/org/repo/issues/45", "updated_at": "2026-05-24T10:00:00Z", "state": "open" }
            ]
            """));

        var result = await plugin.QueryIssueReferencesAsync(tempDirectory.Path, "1", 10, CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(123, result[0].Number);
    }

    [TestMethod]
    public async Task IssueReferenceQueryFetchesExactIssueForNumericQuery()
    {
        using var tempDirectory = TempDirectory.CreateRepository();
        await using var plugin = await CreateInitializedPluginAsync(new ExactIssueHandler());

        var result = await plugin.QueryIssueReferencesAsync(tempDirectory.Path, "123", 10, CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(123, result[0].Number);
        Assert.AreEqual("Exact issue", result[0].Title);
    }

    [TestMethod]
    public async Task IssueReferenceQueryPagesRecentIssuesPastPullRequests()
    {
        using var tempDirectory = TempDirectory.CreateRepository();
        await using var plugin = await CreateInitializedPluginAsync(new PagedIssueHandler());

        var result = await plugin.QueryIssueReferencesAsync(tempDirectory.Path, string.Empty, 10, CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(456, result[0].Number);
    }

    [TestMethod]
    public async Task IssueReferenceQueryFiltersRecentIssuesCaseInsensitivelyForWordQuery()
    {
        using var tempDirectory = TempDirectory.CreateRepository();
        await using var plugin = await CreateInitializedPluginAsync(new FakeIssueHandler(
            """
            [
              { "number": 123, "title": "Improve GitHub PICKER search", "html_url": "https://github.com/org/repo/issues/123", "updated_at": "2026-05-25T10:00:00Z", "state": "open" },
              { "number": 45, "title": "Other issue", "html_url": "https://github.com/org/repo/issues/45", "updated_at": "2026-05-24T10:00:00Z", "state": "open" }
            ]
            """));

        var result = await plugin.QueryIssueReferencesAsync(tempDirectory.Path, "picker", 1, CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(123, result[0].Number);
    }

    [TestMethod]
    public async Task IssueReferenceQueryReadsGitLabIssuesFromTheHostOfTheRemote()
    {
        using var tempDirectory = TempDirectory.CreateRepository("git@gitlab.com:group/sub/project.git");
        var handler = new RecordingHandler(
            """
            [
              { "id": 9001, "iid": 12, "title": "GitLab issue", "web_url": "https://gitlab.com/group/sub/project/-/issues/12", "updated_at": "2026-05-25T10:00:00.000Z", "state": "opened" }
            ]
            """);
        await using var plugin = await CreateInitializedPluginAsync(handler);

        var result = await plugin.QueryIssueReferencesAsync(tempDirectory.Path, string.Empty, 10, CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.AreEqual(12, result.Single().Number);
        Assert.AreEqual("[#12](https://gitlab.com/group/sub/project/-/issues/12)", result.Single().Markdown);
        Assert.IsTrue(result.Single().IsOpen);
        Assert.AreEqual("https://gitlab.com/api/v4/projects/group%2Fsub%2Fproject/issues?scope=all&order_by=updated_at&sort=desc&per_page=100&page=1", handler.Requests.Single());
    }

    [TestMethod]
    public void PluginContributesPromptEditorAttachment()
    {
        var contribution = new GitPlugin(GitTerminalContributions.CreatePromptEditorContributions).GetPromptEditorContributions().Single();

        Assert.AreEqual("Git issue prompt picker", contribution.Name);
        Assert.AreEqual("[#] to reference an issue", contribution.PlaceholderText);
        Assert.IsNotNull(contribution.Attach);
    }

    [TestMethod]
    public async Task CliToolsAreOfferedOnlyForInstalledClis_AndRequireAStructuredArgumentArray()
    {
        Assert.AreEqual(0, new GitPlugin().GetAgentTools().Count(), "A backend that was not initialized offers no tool.");
        await using var none = await CreateInitializedPluginAsync(new FakeIssueHandler("[]"));
        Assert.AreEqual(0, none.GetAgentTools().Count());

        await using var gitLabOnly = await CreateInitializedPluginAsync(new FakeIssueHandler("[]"), static name => name == "glab" ? new GitCliCommand("glab", []) : null);
        Assert.AreEqual("glab", gitLabOnly.GetAgentTools().Single().Definition.Spec.Name);

        await using var plugin = await CreateInitializedPluginAsync(new FakeIssueHandler("[]"), static name => new GitCliCommand(name, []));
        var tools = plugin.GetAgentTools().ToArray();
        CollectionAssert.AreEqual(new[] { "gh", "glab", "az" }, tools.Select(static tool => tool.Definition.Spec.Name).ToArray());
        foreach (var contribution in tools)
        {
            var name = contribution.Definition.Spec.Name;
            var properties = contribution.Definition.Spec.InputSchema.GetProperty("properties");
            var arguments = properties.GetProperty("arguments");
            Assert.AreEqual("array", arguments.GetProperty("type").GetString());
            Assert.AreEqual("string", arguments.GetProperty("items").GetProperty("type").GetString());
            StringAssert.Contains(arguments.GetProperty("description").GetString(), $"excluding the {name} executable name");
            Assert.IsFalse(properties.TryGetProperty("command", out _));
            StringAssert.Contains(contribution.PromptGuidance, "Pass arguments as an array");
            StringAssert.Contains(contribution.PromptSnippet, $"`{name}`");
            Assert.AreEqual(PluginToolActivationPolicy.CodeAltaManagedOnly, contribution.ActivationPolicy);
        }
    }

    [TestMethod]
    public async Task CliToolRunsTheLocatedCommandWithTheArgumentsAsGiven()
    {
        using var tempDirectory = TempDirectory.CreateRepository();
        var previousCurrentDirectory = Environment.CurrentDirectory;
        try
        {
            // Without a selected project the tools run in, and stay inside, the current directory.
            Environment.CurrentDirectory = tempDirectory.Path;
            // `git` stands in for the provider CLI: it is the one program these tests already need.
            await using var plugin = await CreateInitializedPluginAsync(new FakeIssueHandler("[]"), static name => name == "gh" ? new GitCliCommand("git", ["remote"]) : null);
            var tool = plugin.GetAgentTools().Single().Definition;

            var listed = await InvokeAsync(tool, """{ "arguments": ["get-url", "origin"] }""");
            Assert.IsTrue(listed.Success, listed.Error);
            var output = ((AgentToolResultItem.Text)listed.Items.Single()).Value;
            StringAssert.Contains(output, "exit_code: 0");
            StringAssert.Contains(output, "https://github.com/org/repo.git");

            var failed = await InvokeAsync(tool, """{ "arguments": ["get-url", "no & such | remote"] }""");
            Assert.IsFalse(failed.Success);
            Assert.AreEqual("gh exited with a non-zero status.", failed.Error);
            StringAssert.Contains(((AgentToolResultItem.Text)failed.Items.Single()).Value, "no & such | remote", "The argument reached the program as one value.");

            foreach (var (json, error) in new[]
                     {
                         ("[]", "Arguments must be a JSON object."),
                         ("{}", "Property 'arguments' is required and must be an array of strings."),
                         ("""{ "arguments": "remote" }""", "Property 'arguments' is required and must be an array of strings."),
                         ("""{ "arguments": [1] }""", "Every gh argument must be a string."),
                         ("""{ "arguments": [] }""", "At least one gh argument is required."),
                         ("""{ "arguments": ["get-url", "origin"], "workingDirectory": ".." }""", "The gh workingDirectory must be inside the selected project."),
                     })
            {
                var refused = await InvokeAsync(tool, json);
                Assert.IsFalse(refused.Success, json);
                Assert.AreEqual(error, refused.Error, json);
            }
        }
        finally
        {
            Environment.CurrentDirectory = previousCurrentDirectory;
        }
    }

    [TestMethod]
    public async Task AzureCliToolOnlyRunsAzureDevOpsCommandGroups()
    {
        using var tempDirectory = TempDirectory.CreateRepository("https://dev.azure.com/org/Project/_git/repo");
        var previousCurrentDirectory = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = tempDirectory.Path;
            // `git remote` stands in for the Azure CLI: an accepted command reaches it and fails there.
            await using var plugin = await CreateInitializedPluginAsync(new FakeIssueHandler("[]"), static name => name == "az" ? new GitCliCommand("git", ["remote"]) : null);
            var tool = plugin.GetAgentTools().Single().Definition;
            Assert.AreEqual("az", tool.Spec.Name);

            foreach (var arguments in new[] { """["account", "show"]""", """["vm", "delete"]""", """["--version"]""", """["Boards", "query"]""", """[" boards"]""", """["rest", "--url", "https://management.azure.com"]""" })
            {
                var refused = await InvokeAsync(tool, $$"""{ "arguments": {{arguments}} }""");
                Assert.IsFalse(refused.Success, arguments);
                Assert.AreEqual("The az tool only runs Azure DevOps commands: the first argument must be one of devops, boards, repos, pipelines, artifacts.", refused.Error, arguments);
            }

            foreach (var group in new[] { "devops", "boards", "repos", "pipelines", "artifacts" })
            {
                var ran = await InvokeAsync(tool, $$"""{ "arguments": ["{{group}}"] }""");
                Assert.AreEqual("az exited with a non-zero status.", ran.Error, group);
            }
        }
        finally
        {
            Environment.CurrentDirectory = previousCurrentDirectory;
        }
    }

    private static Task<AgentToolResult> InvokeAsync(AgentToolDefinition tool, string json)
    {
        using var document = JsonDocument.Parse(json);
        return tool.Handler(new AgentToolInvocation(new ModelProviderId("provider"), "session", "call", tool.Spec.Name, document.RootElement.Clone()), CancellationToken.None);
    }

    /// <summary>Creates a plugin over a literal handler and CLI locator, attached to no-op host services and initialized.</summary>
    private static async Task<GitPlugin> CreateInitializedPluginAsync(HttpMessageHandler handler, Func<string, GitCliCommand?>? locateCli = null)
    {
        var plugin = new GitPlugin(null, new GitPluginBackend(locateCli ?? (static _ => null), () => new HttpClient(handler), new GitIssueLookupOptions { GitLabHosts = [] }));
        var services = NoopPluginServices.Create();
        plugin.AttachRuntimeContext(new PluginRuntimeContext
        {
            Plugin = PluginDescriptorFactory.FromType(typeof(GitPlugin)),
            Host = new PluginHostInfo
            {
                ApplicationName = "CodeAlta",
                Version = "test",
                HostApiVersion = "1",
                UserDataDirectory = Path.GetTempPath(),
                IsHeadless = true,
            },
            Logger = services.Logger,
            Services = services,
            PackageDirectory = Path.GetTempPath(),
        });
        await plugin.InitializeAsync(CancellationToken.None);
        return plugin;
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
            => Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CodeAlta.GitPluginTests." + Guid.NewGuid().ToString("N"));

        public string Path { get; }

        public static TempDirectory Create()
        {
            var directory = new TempDirectory();
            Directory.CreateDirectory(directory.Path);
            return directory;
        }

        public static TempDirectory CreateRepository(string remoteUrl = "https://github.com/org/repo.git")
        {
            var directory = Create();
            InitializeGitRepository(directory.Path);
            RunGitOrFail(directory.Path, ["remote", "add", "origin", remoteUrl]);
            return directory;
        }

        public static TempDirectory CreateRepositoryWithSubmoduleBeforeOrigin()
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
                Assert.Inconclusive("git is required for repository detection tests: " + error);
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

    private sealed class RecordingHandler(string responseJson) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson),
            });
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
