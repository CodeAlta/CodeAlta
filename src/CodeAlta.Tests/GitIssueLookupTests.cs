using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CodeAlta.Plugin.Git;

namespace CodeAlta.Tests;

/// <summary>The reusable issue lookup over a literal handler and literal remotes: no network, no git.</summary>
[TestClass]
public sealed class GitIssueLookupTests
{
    private const string OneIssue = """
        [ { "number": 7, "title": "Found issue", "html_url": "https://github.com/org/repo/issues/7", "updated_at": "2026-05-25T10:00:00Z", "state": "open" } ]
        """;

    private const string WorkItemFields = "System.Id,System.Title,System.State,System.ChangedDate,System.TeamProject";

    [TestMethod]
    public async Task Constructor_AndQuery_RejectNullArguments()
    {
        using var client = new HttpClient(new StubHandler(_ => Ok("[]")));
        Assert.ThrowsExactly<ArgumentNullException>(() => new GitIssueLookup(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => new GitIssueLookup(client, null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => GitIssueLookup.CreateHttpClient(null!));
        var lookup = new GitIssueLookup(client);
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(async () => await lookup.QueryAsync(null!, "", 10));
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(async () => await lookup.QueryAsync(GitRepositoryReference.GitHub("org", "repo"), null!, 10));
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(async () => await GitIssueLookup.ResolveCredentialAsync(null!));
    }

    [TestMethod]
    public void RemoteUrls_OfEveryProviderForm_NameTheirRepository()
    {
        foreach (var (remote, provider, host, fullName) in new (string, GitRemoteProvider, string, string)[]
                 {
                     ("https://github.com/org/repo.git", GitRemoteProvider.GitHub, "github.com", "org/repo"),
                     ("https://github.com/org/repo", GitRemoteProvider.GitHub, "github.com", "org/repo"),
                     ("git@github.com:org/repo.git", GitRemoteProvider.GitHub, "github.com", "org/repo"),
                     ("ssh://git@github.com/org/repo.git", GitRemoteProvider.GitHub, "github.com", "org/repo"),
                     ("ssh://git@ssh.github.com:443/org/repo.git", GitRemoteProvider.GitHub, "github.com", "org/repo"),
                     ("  https://token@GitHub.com/Org/Repo.git\n", GitRemoteProvider.GitHub, "github.com", "Org/Repo"),

                     ("https://gitlab.com/group/project.git", GitRemoteProvider.GitLab, "gitlab.com", "group/project"),
                     ("https://gitlab.com/group/sub/deeper/project", GitRemoteProvider.GitLab, "gitlab.com", "group/sub/deeper/project"),
                     ("git@gitlab.com:group/sub/project.git", GitRemoteProvider.GitLab, "gitlab.com", "group/sub/project"),
                     ("ssh://git@altssh.gitlab.com:443/group/project.git", GitRemoteProvider.GitLab, "gitlab.com", "group/project"),
                     ("https://oauth2:secret@gitlab.com/group/project.git", GitRemoteProvider.GitLab, "gitlab.com", "group/project"),
                     ("ssh://git@gitlab.example.com:2222/team/project.git", GitRemoteProvider.GitLab, "gitlab.example.com", "team/project"),
                     ("git@code.example.com:team/project.git", GitRemoteProvider.GitLab, "code.example.com", "team/project"),

                     ("https://dev.azure.com/org/Project/_git/repo", GitRemoteProvider.AzureDevOps, "dev.azure.com", "org/Project/repo"),
                     ("https://org@dev.azure.com/org/My%20Project/_git/My%20Repo", GitRemoteProvider.AzureDevOps, "dev.azure.com", "org/My Project/My Repo"),
                     ("https://dev.azure.com/org/_git/same", GitRemoteProvider.AzureDevOps, "dev.azure.com", "org/same/same"),
                     ("https://dev.azure.com/org/Project/_git/_optimized/repo", GitRemoteProvider.AzureDevOps, "dev.azure.com", "org/Project/repo"),
                     ("git@ssh.dev.azure.com:v3/org/Project/repo", GitRemoteProvider.AzureDevOps, "dev.azure.com", "org/Project/repo"),
                     ("https://org.visualstudio.com/Project/_git/repo", GitRemoteProvider.AzureDevOps, "dev.azure.com", "org/Project/repo"),
                     ("https://org.visualstudio.com/DefaultCollection/Project/_git/repo", GitRemoteProvider.AzureDevOps, "dev.azure.com", "org/Project/repo"),
                     ("org@vs-ssh.visualstudio.com:v3/org/Project/repo", GitRemoteProvider.AzureDevOps, "dev.azure.com", "org/Project/repo"),
                 })
        {
            Assert.IsTrue(GitRemoteUrl.TryParse(remote, ["code.example.com"], out var repository), remote);
            Assert.AreEqual(provider, repository.Provider, remote);
            Assert.AreEqual(host, repository.Host, remote);
            Assert.AreEqual(fullName, repository.FullName, remote);
        }

        Assert.IsTrue(GitRemoteUrl.TryParse("https://dev.azure.com/org/My%20Project/_git/repo", [], out var azure));
        Assert.AreEqual("org", azure.Owner);
        Assert.AreEqual("My Project", azure.Project);
        Assert.AreEqual("repo", azure.Name);
        Assert.IsTrue(GitRemoteUrl.TryParse("git@gitlab.com:group/sub/project.git", [], out var gitLab));
        Assert.AreEqual("group/sub", gitLab.Owner);
        Assert.AreEqual("project", gitLab.Name);
        Assert.IsNull(gitLab.Project);
    }

    [TestMethod]
    public void RemoteUrls_OfOtherHostsLookalikesAndMalformedPaths_AreNotRepositories()
    {
        foreach (var remote in new[]
                 {
                     null, "", "   ", "not a url", "/srv/git/repo.git", @"C:\repos\project", "file:///srv/git/repo.git",
                     "git@bitbucket.org:org/repo.git", "https://example.com/org/repo.git", "git@code.example.com:team/project.git",
                     // A host that only resembles a provider is another host.
                     "https://github.com.evil.example/org/repo", "https://evilgithub.com/org/repo", "https://notgitlab.com/group/project",
                     "https://dev.azure.com.evil.example/org/Project/_git/repo", "https://visualstudio.com/Project/_git/repo",
                     "https://org.vsrm.visualstudio.com/Project/_git/repo",
                     "ftp://github.com/org/repo.git", "https://-github.com/org/repo",
                     // A path that does not spell an ordinary repository is never sent to a provider.
                     "https://github.com/org", "https://github.com/or%20g/repo", "https://github.com/org/re%2Fpo", "https://github.com/../repo",
                     "https://gitlab.com/project", "https://gitlab.com/group/pro%3Fject", "git@gitlab.com:group/..%2F..%2Fapi/project.git",
                     "https://dev.azure.com/org/Project/repo", "https://dev.azure.com/org/Project/_git", "https://dev.azure.com/or%20g/Project/_git/repo",
                     "https://dev.azure.com/org/Pro%2Fject/_git/repo", "https://dev.azure.com/org/Project/_git/repo/extra",
                     "git@ssh.dev.azure.com:v2/org/Project/repo", "git@ssh.dev.azure.com:v3/org/Project",
                 })
        {
            Assert.IsFalse(GitRemoteUrl.TryParse(remote, [], out _), remote);
        }
    }

    [TestMethod]
    public async Task ResolveRepository_ReadsRemotesOnlyInsideAGitWorkingTree_AndTakesTheFirstSupportedRemote()
    {
        using var folder = new TempFolder();
        var reads = 0;
        using var client = new HttpClient(new StubHandler(_ => throw new AssertFailedException("Detection must not reach a provider.")));
        var lookup = new GitIssueLookup(client, new GitIssueLookupOptions
        {
            GitLabHosts = [],
            RemoteUrlReader = (_, _) =>
            {
                reads++;
                return Remotes("https://example.invalid/org/mirror.git", "git@github.com:org/repo.git", "https://gitlab.com/org/mirror.git");
            },
        });

        Assert.IsNull(await lookup.ResolveRepositoryAsync(null));
        Assert.IsNull(await lookup.ResolveRepositoryAsync("  "));
        Assert.IsNull(await lookup.ResolveRepositoryAsync(folder.Path), "A folder outside any working tree has no remotes to read.");
        Assert.AreEqual(0, reads);

        Directory.CreateDirectory(Path.Combine(folder.Path, ".git"));
        var nested = Directory.CreateDirectory(Path.Combine(folder.Path, "src", "nested")).FullName;
        Assert.IsNull(await lookup.ResolveRepositoryAsync(folder.Path), "The earlier answer is kept for the cache window.");
        var repository = await lookup.ResolveRepositoryAsync(nested);
        Assert.IsNotNull(repository);
        Assert.AreEqual(GitRemoteProvider.GitHub, repository.Provider);
        Assert.AreEqual("org", repository.Owner);
        Assert.AreEqual("repo", repository.Name);
        Assert.AreEqual("org/repo", repository.FullName);
        Assert.AreEqual(1, reads);
    }

    [TestMethod]
    public async Task ResolveRepository_RecognizesSelfManagedGitLabOnlyByNameOrConfiguration()
    {
        using var folder = new TempFolder();
        Directory.CreateDirectory(Path.Combine(folder.Path, ".git"));
        using var client = new HttpClient(new StubHandler(_ => Ok("[]")));
        var remote = "git@code.example.com:team/project.git";

        var unconfigured = new GitIssueLookup(client, new GitIssueLookupOptions { CacheDuration = TimeSpan.Zero, GitLabHosts = [], RemoteUrlReader = (_, _) => Remotes(remote) });
        Assert.IsNull(await unconfigured.ResolveRepositoryAsync(folder.Path));

        // The configured value may be a host name or the URL of the instance.
        foreach (var configured in new[] { "code.example.com", "https://Code.Example.com/", "code.example.com:8443" })
        {
            var lookup = new GitIssueLookup(client, new GitIssueLookupOptions { CacheDuration = TimeSpan.Zero, GitLabHosts = [configured], RemoteUrlReader = (_, _) => Remotes(remote) });
            var repository = await lookup.ResolveRepositoryAsync(folder.Path);
            Assert.IsNotNull(repository, configured);
            Assert.AreEqual(GitRemoteProvider.GitLab, repository.Provider);
            Assert.AreEqual("code.example.com", repository.Host);
            Assert.AreEqual("team/project", repository.FullName);
        }
    }

    [TestMethod]
    public async Task WithoutACache_EveryCallDetectsAndFetchesAgain()
    {
        using var folder = new TempFolder();
        Directory.CreateDirectory(Path.Combine(folder.Path, ".git"));
        var reads = 0;
        var tokens = 0;
        var handler = new StubHandler(_ => Ok(OneIssue));
        using var client = new HttpClient(handler);
        var lookup = new GitIssueLookup(client, new GitIssueLookupOptions
        {
            CacheDuration = TimeSpan.Zero,
            RemoteUrlReader = (_, _) =>
            {
                reads++;
                return Remotes("https://github.com/org/repo.git");
            },
            CredentialProvider = (_, _) =>
            {
                tokens++;
                return ValueTask.FromResult<GitRemoteCredential?>(GitRemoteCredential.Bearer("token-" + tokens));
            },
        });

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var repository = await lookup.ResolveRepositoryAsync(folder.Path);
            Assert.IsNotNull(repository);
            var result = await lookup.QueryAsync(repository, "", 10);
            Assert.AreEqual(7, result.Issues.Single().Number);
            Assert.AreEqual("org/repo", result.Issues.Single().Repository);
            Assert.IsTrue(result.Issues.Single().IsOpen);
            Assert.IsNull(result.FailureStatusCode);
            Assert.AreEqual(attempt, reads);
            Assert.HasCount(attempt, handler.Requests);
            Assert.AreEqual("Bearer token-" + attempt, handler.Authorizations[^1]);
            Assert.AreEqual("application/vnd.github+json", handler.Accepts[^1]);
        }
    }

    [TestMethod]
    public async Task Credentials_ComeFromTheProviderOrElseFromTheClient()
    {
        var handler = new StubHandler(_ => Ok("[]"));
        using var client = new HttpClient(handler);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "client-token");
        var repository = GitRepositoryReference.GitHub("org", "repo");

        await new GitIssueLookup(client).QueryAsync(repository, "", 10);
        Assert.AreEqual("Bearer client-token", handler.Authorizations[^1], "Without a provider the client's own credentials are sent.");

        GitRemoteCredential? provided = GitRemoteCredential.Bearer("  provider-token\n");
        var lookup = new GitIssueLookup(client, new GitIssueLookupOptions { CacheDuration = TimeSpan.Zero, CredentialProvider = (_, _) => ValueTask.FromResult<GitRemoteCredential?>(provided) });
        await lookup.QueryAsync(repository, "", 10);
        Assert.AreEqual("Bearer provider-token", handler.Authorizations[^1]);

        client.DefaultRequestHeaders.Authorization = null;
        foreach (var unusable in new GitRemoteCredential?[] { null, GitRemoteCredential.Bearer("two\nlines") })
        {
            provided = unusable;
            await lookup.QueryAsync(repository, "", 10);
            Assert.IsNull(handler.Authorizations[^1], "A missing or malformed token leaves the request unauthenticated.");
        }

        Assert.ThrowsExactly<ArgumentException>(() => GitRemoteCredential.Bearer("  "));
        Assert.ThrowsExactly<ArgumentException>(() => GitRemoteCredential.Basic("user", ""));
        Assert.ThrowsExactly<ArgumentNullException>(() => GitRemoteCredential.Basic(null!, "secret"));
        Assert.AreEqual("Bearer", GitRemoteCredential.Bearer("secret-value").ToString(), "The text form of a credential never shows the secret.");
    }

    [TestMethod]
    public async Task Credentials_AreResolvedPerProviderHost_AndASignedOutAnswerIsKeptForTheCacheWindow()
    {
        var handler = new StubHandler(_ => Ok("[]"));
        using var client = new HttpClient(handler);
        var asked = new List<string>();
        var lookup = new GitIssueLookup(client, new GitIssueLookupOptions
        {
            CredentialProvider = (repository, _) =>
            {
                asked.Add(repository.Provider + ":" + repository.Host);
                return ValueTask.FromResult(repository.Host == "gitlab.example.com" ? null : GitRemoteCredential.Bearer("token-for-" + repository.Host));
            },
        });

        var gitHub = GitRepositoryReference.GitHub("org", "repo");
        var gitLab = GitRepositoryReference.GitLab("gitlab.com", "group", "project");
        var selfManaged = GitRepositoryReference.GitLab("gitlab.example.com", "group", "project");
        // A number always asks for that exact issue, so every query sends a request whatever is cached.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await lookup.QueryAsync(gitHub, "7", 10);
            Assert.AreEqual("Bearer token-for-github.com", handler.Authorizations[^1]);
            await lookup.QueryAsync(GitRepositoryReference.GitHub("org", "another"), "7", 10);
            Assert.AreEqual("Bearer token-for-github.com", handler.Authorizations[^1]);
            await lookup.QueryAsync(gitLab, "7", 10);
            Assert.AreEqual("Bearer token-for-gitlab.com", handler.Authorizations[^1]);
            Assert.AreEqual("gitlab.com", handler.Hosts[^1]);
            await lookup.QueryAsync(selfManaged, "7", 10);
            Assert.IsNull(handler.Authorizations[^1], "The token of one host is never sent to another.");
            Assert.AreEqual("gitlab.example.com", handler.Hosts[^1]);
        }

        CollectionAssert.AreEqual(new[] { "GitHub:github.com", "GitLab:gitlab.com", "GitLab:gitlab.example.com" }, asked);
    }

    [TestMethod]
    public async Task Query_ReportsTheFirstRefusal_KeepsWhatWasFound_AndDoesNotCacheARefusedListing()
    {
        var refuseListing = true;
        var handler = new StubHandler(request => request.StartsWith("/search/", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
            : request.StartsWith("/repos/org/repo/issues/", StringComparison.Ordinal)
                ? new HttpResponseMessage(request.EndsWith("/500", StringComparison.Ordinal) ? HttpStatusCode.InternalServerError : HttpStatusCode.NotFound)
                : refuseListing ? new HttpResponseMessage(HttpStatusCode.Forbidden) : Ok(OneIssue));
        using var client = new HttpClient(handler);
        var lookup = new GitIssueLookup(client);
        var repository = GitRepositoryReference.GitHub("org", "repo");

        var refused = await lookup.QueryAsync(repository, "", 10);
        Assert.IsEmpty(refused.Issues);
        Assert.AreEqual(HttpStatusCode.Forbidden, refused.FailureStatusCode);

        refuseListing = false;
        var listed = await lookup.QueryAsync(repository, "", 10);
        Assert.HasCount(1, listed.Issues);
        Assert.IsNull(listed.FailureStatusCode);
        Assert.HasCount(2, handler.Requests, "The refused listing was not kept.");

        var missing = await lookup.QueryAsync(repository, "404", 10);
        Assert.IsNull(missing.FailureStatusCode, "A number that names no issue is not a failure.");
        var broken = await lookup.QueryAsync(repository, "500", 10);
        Assert.AreEqual(HttpStatusCode.InternalServerError, broken.FailureStatusCode);

        var partial = await lookup.QueryAsync(repository, "found", 10);
        Assert.AreEqual(7, partial.Issues.Single().Number);
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, partial.FailureStatusCode);
        Assert.HasCount(5, handler.Requests, "The successful listing is reused within the cache window.");
        Assert.IsTrue(handler.Hosts.All(static host => host == "api.github.com"));
    }

    [TestMethod]
    public async Task GitLab_ListsSearchesAndReadsIssuesOfTheProjectOnItsOwnHost()
    {
        const string Listing = "/api/v4/projects/group%2Fsub%2Fproject/issues?scope=all&order_by=updated_at&sort=desc&per_page=100&page=1";
        var handler = new StubHandler(request => request.Contains("/issues/12", StringComparison.Ordinal)
            ? Ok(GitLabIssue(12, "Exact issue", day: 1, state: "closed"))
            : request.Contains("/issues/404", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : request.Contains("&search=", StringComparison.Ordinal)
                    ? Ok("[" + GitLabIssue(30, "Searched crash", day: 2) + "]")
                    : Ok("[" + GitLabIssue(120, "Newest", day: 9) + "," + GitLabIssue(5, "Closed one", day: 3, state: "closed") + ",{\"iid\":0,\"title\":\"No number\"}]"));
        using var client = new HttpClient(handler);
        var lookup = new GitIssueLookup(client, new GitIssueLookupOptions { CredentialProvider = (_, _) => ValueTask.FromResult<GitRemoteCredential?>(GitRemoteCredential.Bearer("glpat-token")) });
        var repository = GitRepositoryReference.GitLab("gitlab.example.com", "group/sub", "project");

        var recent = await lookup.QueryAsync(repository, "", 10);
        Assert.IsNull(recent.FailureStatusCode);
        CollectionAssert.AreEqual(new[] { 120, 5 }, recent.Issues.Select(static issue => issue.Number).ToArray());
        var newest = recent.Issues[0];
        Assert.AreEqual("Newest", newest.Title);
        Assert.AreEqual("https://gitlab.example.com/group/sub/project/-/issues/120", newest.Url);
        Assert.AreEqual("[#120](https://gitlab.example.com/group/sub/project/-/issues/120)", newest.Markdown);
        Assert.AreEqual("group/sub/project", newest.Repository);
        Assert.AreEqual("open", newest.State, "GitLab's `opened` reads like the other providers.");
        Assert.IsTrue(newest.IsOpen);
        Assert.AreEqual("closed", recent.Issues[1].State);
        Assert.IsFalse(recent.Issues[1].IsOpen);
        CollectionAssert.AreEqual(new[] { Listing }, handler.RawRequests);
        Assert.AreEqual("gitlab.example.com", handler.Hosts.Single());
        Assert.AreEqual("Bearer glpat-token", handler.Authorizations.Single());
        Assert.AreEqual("application/json", handler.Accepts.Single());

        // A number reads the exact issue and filters the cached recent list.
        handler.RawRequests.Clear();
        var numeric = await lookup.QueryAsync(repository, "12", 10);
        CollectionAssert.AreEqual(new[] { 120, 12 }, numeric.Issues.Select(static issue => issue.Number).ToArray());
        CollectionAssert.AreEqual(new[] { "/api/v4/projects/group%2Fsub%2Fproject/issues/12" }, handler.RawRequests);
        Assert.IsNull((await lookup.QueryAsync(repository, "404", 10)).FailureStatusCode, "A number that names no issue is not a failure.");

        // Text with search syntax goes to the instance's search as one escaped value.
        handler.RawRequests.Clear();
        var searched = await lookup.QueryAsync(repository, "crash & burn", 10);
        Assert.AreEqual(30, searched.Issues.Single().Number);
        CollectionAssert.AreEqual(
            new[] { "/api/v4/projects/group%2Fsub%2Fproject/issues?scope=all&order_by=updated_at&sort=desc&per_page=10&page=1&search=crash%20%26%20burn" },
            handler.RawRequests);

        // A plain word filters the recent list first and adds searched issues.
        var word = await lookup.QueryAsync(repository, "newest", 10);
        CollectionAssert.AreEqual(new[] { 120, 30 }, word.Issues.Select(static issue => issue.Number).ToArray());
    }

    [TestMethod]
    public async Task GitLab_RefusalIsReportedAndNotCached()
    {
        var refuse = true;
        var handler = new StubHandler(_ => refuse ? new HttpResponseMessage(HttpStatusCode.NotFound) : Ok("[" + GitLabIssue(1, "Visible now", day: 1) + "]"));
        using var client = new HttpClient(handler);
        var lookup = new GitIssueLookup(client);
        var repository = GitRepositoryReference.GitLab("gitlab.com", "group", "private");

        // A private project answers 404 to a request without access: the listing failed, it is not empty.
        var refused = await lookup.QueryAsync(repository, "", 10);
        Assert.IsEmpty(refused.Issues);
        Assert.AreEqual(HttpStatusCode.NotFound, refused.FailureStatusCode);
        refuse = false;
        Assert.HasCount(1, (await lookup.QueryAsync(repository, "", 10)).Issues);
    }

    [TestMethod]
    public async Task AzureDevOps_QueriesTheWorkItemsOfTheProject_InTwoRequests()
    {
        const string Project = "/org/My%20Project/_apis/wit/";
        var handler = new StubHandler(request => request.StartsWith("/org/My Project/_apis/wit/wiql", StringComparison.Ordinal)
            ? Ok("{\"queryType\":\"flat\",\"workItems\":[{\"id\":42,\"url\":\"https://dev.azure.com/org/_apis/wit/workItems/42\"},{\"id\":7},{\"id\":\"x\"}]}")
            : request.StartsWith("/org/My Project/_apis/wit/workitems?", StringComparison.Ordinal)
                ? Ok("{\"count\":2,\"value\":[" + WorkItem(42, "Fix login", day: 2, state: "Active") + ",null," + WorkItem(7, "Old cleanup", day: 9, state: "Done") + "]}")
                : request.StartsWith("/org/My Project/_apis/wit/workitems/9", StringComparison.Ordinal)
                    ? Ok(WorkItem(9, "Exact work item", day: 1, state: "New"))
                    : request.StartsWith("/org/My Project/_apis/wit/workitems/8", StringComparison.Ordinal)
                        ? Ok(WorkItem(8, "Other project", day: 1, state: "New", project: "Elsewhere"))
                        : new HttpResponseMessage(HttpStatusCode.NotFound));
        using var client = new HttpClient(handler);
        var lookup = new GitIssueLookup(client, new GitIssueLookupOptions { CredentialProvider = (_, _) => ValueTask.FromResult<GitRemoteCredential?>(GitRemoteCredential.Basic("", "pat-secret")) });
        var repository = GitRepositoryReference.AzureDevOps("org", "My Project", "repo");

        var recent = await lookup.QueryAsync(repository, "", 10);
        Assert.IsNull(recent.FailureStatusCode);
        CollectionAssert.AreEqual(new[] { 7, 42 }, recent.Issues.Select(static issue => issue.Number).ToArray(), "Most recently changed first.");
        var done = recent.Issues[0];
        Assert.AreEqual("Old cleanup", done.Title);
        Assert.AreEqual("Done", done.State);
        Assert.IsFalse(done.IsOpen);
        Assert.AreEqual("https://dev.azure.com/org/My%20Project/_workitems/edit/7", done.Url);
        Assert.AreEqual("[#7](https://dev.azure.com/org/My%20Project/_workitems/edit/7)", done.Markdown);
        Assert.AreEqual("org/My Project/repo", done.Repository);
        Assert.AreEqual("Active", recent.Issues[1].State);
        Assert.IsTrue(recent.Issues[1].IsOpen);

        CollectionAssert.AreEqual(
            new[]
            {
                "POST " + Project + "wiql?$top=100&api-version=7.1",
                "GET " + Project + "workitems?ids=42,7&fields=" + WorkItemFields + "&errorPolicy=omit&api-version=7.1",
            },
            handler.Methods.Zip(handler.RawRequests, static (method, request) => method + " " + request).ToArray());
        Assert.AreEqual("SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = @project ORDER BY [System.ChangedDate] DESC", WiqlOf(handler.Bodies[0]));
        Assert.IsTrue(handler.Hosts.All(static host => host == "dev.azure.com"));
        Assert.IsTrue(handler.Authorizations.All(static value => value == "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(":pat-secret"))));

        // A number reads that work item, unless it belongs to another project of the organization.
        handler.RawRequests.Clear();
        var exact = await lookup.QueryAsync(repository, "9", 10);
        CollectionAssert.AreEqual(new[] { 9 }, exact.Issues.Select(static issue => issue.Number).ToArray());
        CollectionAssert.AreEqual(new[] { Project + "workitems/9?fields=" + WorkItemFields + "&api-version=7.1" }, handler.RawRequests, "The recent list is cached.");
        Assert.IsEmpty((await lookup.QueryAsync(repository, "8", 10)).Issues);
        var missing = await lookup.QueryAsync(repository, "404", 10);
        Assert.IsEmpty(missing.Issues);
        Assert.IsNull(missing.FailureStatusCode);

        // Text is matched against the titles; a quote cannot end the WIQL string.
        handler.Bodies.Clear();
        handler.RawRequests.Clear();
        await lookup.QueryAsync(repository, "it's broken", 5);
        Assert.AreEqual(Project + "wiql?$top=5&api-version=7.1", handler.RawRequests[0]);
        Assert.AreEqual(
            "SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = @project AND [System.Title] CONTAINS 'it''s broken' ORDER BY [System.ChangedDate] DESC",
            WiqlOf(handler.Bodies[0]));
    }

    [TestMethod]
    public async Task AzureDevOps_SignInPageAndRefusals_AreFailures_AndAnEmptyQueryNeedsOneRequest()
    {
        HttpStatusCode? signInPage = HttpStatusCode.NonAuthoritativeInformation;
        var handler = new StubHandler(_ => signInPage is { } status
            ? new HttpResponseMessage(status) { Content = new StringContent("<html>Sign in</html>", Encoding.UTF8, "text/html") }
            : Ok("{\"workItems\":[]}"));
        using var client = new HttpClient(handler);
        var lookup = new GitIssueLookup(client);
        var repository = GitRepositoryReference.AzureDevOps("org", "Project", "repo");

        // Azure DevOps answers an unauthenticated request with 203 and its sign-in page.
        var signIn = await lookup.QueryAsync(repository, "", 10);
        Assert.IsEmpty(signIn.Issues);
        Assert.AreEqual(HttpStatusCode.Unauthorized, signIn.FailureStatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await lookup.QueryAsync(repository, "12", 10)).FailureStatusCode);

        // Or it redirects to that page, which a client that follows redirects receives as a 200 HTML answer.
        signInPage = HttpStatusCode.OK;
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await lookup.QueryAsync(repository, "", 10)).FailureStatusCode);

        signInPage = HttpStatusCode.Forbidden;
        Assert.AreEqual(HttpStatusCode.Forbidden, (await lookup.QueryAsync(repository, "", 10)).FailureStatusCode);

        handler.Requests.Clear();
        signInPage = null;
        var empty = await lookup.QueryAsync(repository, "", 10);
        Assert.IsEmpty(empty.Issues);
        Assert.IsNull(empty.FailureStatusCode);
        Assert.HasCount(1, handler.Requests, "Without ids there are no fields to read.");
    }

    [TestMethod]
    public void CliLocator_FindsProgramsOnThePath_AndNeverRunsABatchFile()
    {
        using var folder = new TempFolder();
        var empty = Directory.CreateDirectory(Path.Combine(folder.Path, "empty")).FullName;
        var tools = Directory.CreateDirectory(Path.Combine(folder.Path, "tools")).FullName;
        var searchPath = string.Join(Path.PathSeparator, empty, Path.Combine(folder.Path, "missing"), tools);
        Assert.IsNull(GitCliLocator.Find("gh", searchPath));
        Assert.IsNull(GitCliLocator.Find("gh", null));
        Assert.IsNull(GitCliLocator.Find("gh", "  "));
        Assert.ThrowsExactly<ArgumentException>(() => GitCliLocator.Find(" ", searchPath));

        if (!OperatingSystem.IsWindows())
        {
            var program = Path.Combine(tools, "gh");
            File.WriteAllText(program, "");
            var found = GitCliLocator.Find("gh", searchPath);
            Assert.IsNotNull(found);
            Assert.AreEqual(program, found.FileName);
            Assert.IsEmpty(found.LeadingArguments);
            return;
        }

        // A bare name is not a program on Windows: the Azure CLI installs a shell script named `az`.
        File.WriteAllText(Path.Combine(tools, "gh"), "");
        Assert.IsNull(GitCliLocator.Find("gh", searchPath));
        var executable = Path.Combine(tools, "gh.exe");
        File.WriteAllText(executable, "");
        var gh = GitCliLocator.Find("gh", searchPath);
        Assert.IsNotNull(gh);
        Assert.AreEqual(executable, gh.FileName);
        Assert.IsEmpty(gh.LeadingArguments);

        // cmd.exe would parse the arguments of a batch file again, so one is never started.
        File.WriteAllText(Path.Combine(tools, "glab.cmd"), "");
        Assert.IsNull(GitCliLocator.Find("glab", searchPath));

        // The Azure CLI's az.cmd only starts the Python installed beside it: that Python is started directly.
        var wbin = Directory.CreateDirectory(Path.Combine(folder.Path, "CLI2", "wbin")).FullName;
        File.WriteAllText(Path.Combine(wbin, "az"), "");
        File.WriteAllText(Path.Combine(wbin, "az.cmd"), "");
        Assert.IsNull(GitCliLocator.Find("az", wbin), "Without its Python the batch file is not used.");
        var python = Path.Combine(folder.Path, "CLI2", "python.exe");
        File.WriteAllText(python, "");
        var az = GitCliLocator.Find("az", wbin);
        Assert.IsNotNull(az);
        Assert.AreEqual(python, az.FileName);
        CollectionAssert.AreEqual(new[] { "-IBm", "azure.cli" }, az.LeadingArguments.ToArray());
    }

    private static HttpResponseMessage Ok(string content) => new(HttpStatusCode.OK) { Content = new StringContent(content, Encoding.UTF8, "application/json") };

    private static string GitLabIssue(int number, string title, int day, string state = "opened")
        => "{\"id\":" + (number + 9000) + ",\"iid\":" + number + ",\"title\":" + JsonSerializer.Serialize(title)
           + ",\"web_url\":\"https://gitlab.example.com/group/sub/project/-/issues/" + number + "\",\"updated_at\":\"2026-05-" + day.ToString("00")
           + "T10:00:00.000Z\",\"state\":\"" + state + "\"}";

    private static string WorkItem(int id, string title, int day, string state, string project = "My Project")
        => "{\"id\":" + id + ",\"fields\":{\"System.Id\":" + id + ",\"System.Title\":" + JsonSerializer.Serialize(title) + ",\"System.State\":\"" + state
           + "\",\"System.ChangedDate\":\"2026-05-" + day.ToString("00") + "T10:00:00Z\",\"System.TeamProject\":\"" + project + "\"}}";

    private static string? WiqlOf(string? body)
    {
        Assert.IsNotNull(body);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("query").GetString();
    }

    private static async IAsyncEnumerable<string> Remotes(params string[] urls)
    {
        foreach (var url in urls)
        {
            await Task.Yield();
            yield return url;
        }
    }

    /// <summary>Answers the provider hosts from a literal function of the unescaped path and query, and records what was asked.</summary>
    private sealed class StubHandler(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        /// <summary>Path and query of each request, unescaped for readable comparisons.</summary>
        public List<string> Requests { get; } = [];

        /// <summary>Path and query of each request exactly as sent.</summary>
        public List<string> RawRequests { get; } = [];

        public List<string> Hosts { get; } = [];

        public List<string> Methods { get; } = [];

        public List<string?> Bodies { get; } = [];

        public List<string?> Authorizations { get; } = [];

        public List<string> Accepts { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.AreEqual(Uri.UriSchemeHttps, request.RequestUri!.Scheme);
            var path = Uri.UnescapeDataString(request.RequestUri.PathAndQuery);
            Requests.Add(path);
            RawRequests.Add(request.RequestUri.PathAndQuery);
            Hosts.Add(request.RequestUri.Host);
            Methods.Add(request.Method.Method);
            Bodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
            Authorizations.Add(request.Headers.Authorization?.ToString());
            Accepts.Add(request.Headers.Accept.ToString());
            return respond(path);
        }
    }

    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } = Directory.CreateDirectory(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CodeAlta.GitIssueLookupTests." + Guid.NewGuid().ToString("N"))).FullName;

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { /* Best-effort cleanup of a temporary directory. */ }
        }
    }
}
