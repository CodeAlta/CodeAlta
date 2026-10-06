using System.Net;
using System.Net.Http.Headers;
using System.Text;
using CodeAlta.Plugin.Git;

namespace CodeAlta.Tests;

/// <summary>The feed of issues and pull requests over a literal handler and a literal repository: no network, no git.</summary>
[TestClass]
public sealed class GitRepositoryFeedTests
{
    private static readonly GitRepositoryReference GitHub = GitRepositoryReference.GitHub("org", "repo");
    private static readonly GitRepositoryReference GitLab = GitRepositoryReference.GitLab("gitlab.com", "group/sub", "project");
    private static readonly GitRepositoryReference Azure = GitRepositoryReference.AzureDevOps("org", "My Project", "repo");

    [TestMethod]
    public async Task GitHubIssues_AreReadWithoutThePullRequests_AndSayWhoCanWrite()
    {
        var handler = new StubHandler(_ => Ok("""
            [
              { "number": 9, "title": "A pull request", "html_url": "https://github.com/org/repo/pull/9", "created_at": "2026-05-25T10:00:00Z", "pull_request": {}, "user": { "login": "octo" }, "author_association": "MEMBER" },
              { "number": 8, "title": "From the owner", "html_url": "https://github.com/org/repo/issues/8", "created_at": "2026-05-25T09:00:00Z", "user": { "login": "octo" }, "author_association": "OWNER" },
              { "number": 7, "title": "From a visitor", "html_url": "https://github.com/org/repo/issues/7", "created_at": "2026-05-24T09:00:00Z", "user": { "login": "guest" }, "author_association": "NONE" },
              { "title": "No number" }
            ]
            """, "W/\"abc\""));
        using var feed = Feed(handler, GitHub, GitRemoteCredential.Bearer("secret"));

        var page = await feed.ReadAsync("C:/repo", GitFeedKind.Issues);

        Assert.AreEqual(GitFeedStatus.Ok, page.Status);
        Assert.AreSame(GitHub, page.Repository);
        Assert.AreEqual("W/\"abc\"", page.EntityTag);
        CollectionAssert.AreEqual(new[] { 8, 7 }, page.Items.Select(static item => item.Number).ToArray());
        Assert.AreEqual("From the owner", page.Items[0].Title);
        Assert.AreEqual("https://github.com/org/repo/issues/8", page.Items[0].Url);
        Assert.AreEqual("octo", page.Items[0].Author);
        Assert.AreEqual(new DateTimeOffset(2026, 5, 25, 9, 0, 0, TimeSpan.Zero), page.Items[0].CreatedAt);
        Assert.IsNull(page.Items[0].Head);
        Assert.IsTrue(await feed.IsTrustedAsync(GitHub, page.Items[0]));
        Assert.IsFalse(await feed.IsTrustedAsync(GitHub, page.Items[1]));
        Assert.AreEqual("/repos/org/repo/issues?state=open&sort=created&direction=desc&per_page=30", handler.Requests.Single(), "Who can write comes with the list: nothing more is asked.");
        Assert.AreEqual("api.github.com", handler.Hosts.Single());
        Assert.AreEqual("Bearer secret", handler.Authorizations.Single());
    }

    [TestMethod]
    public async Task GitHubPullRequests_GiveTheirLastCommit_AndAnUnchangedListCostsNothing()
    {
        var unchanged = false;
        var handler = new StubHandler(_ => unchanged ? new HttpResponseMessage(HttpStatusCode.NotModified) : Ok("""
            [ { "number": 12, "title": "Add a thing", "html_url": "https://github.com/org/repo/pull/12", "created_at": "2026-05-25T10:00:00Z", "user": { "login": "octo" },
                "author_association": "COLLABORATOR", "head": { "sha": "abc123" } } ]
            """, "\"tag-1\""));
        using var feed = Feed(handler, GitHub, null);

        var first = await feed.ReadAsync("C:/repo", GitFeedKind.PullRequests);
        unchanged = true;
        var second = await feed.ReadAsync("C:/repo", GitFeedKind.PullRequests, first.EntityTag);

        Assert.AreEqual("abc123", first.Items.Single().Head);
        Assert.IsTrue(first.Items.Single().Trusted);
        Assert.AreEqual("/repos/org/repo/pulls?state=open&sort=updated&direction=desc&per_page=100", handler.Requests[0]);
        Assert.IsNull(handler.EntityTags[0]);
        Assert.AreEqual("\"tag-1\"", handler.EntityTags[1]);
        Assert.AreEqual(GitFeedStatus.NotModified, second.Status);
        Assert.AreEqual(0, second.Items.Count);
        Assert.AreEqual("\"tag-1\"", second.EntityTag, "The tag still stands for the list.");
        Assert.IsNull(handler.Authorizations[0], "A public repository is read without signing in.");
    }

    [TestMethod]
    public async Task GitLab_ListsIssuesAndMergeRequests_AndAsksWhoCanWriteOnce()
    {
        var handler = new StubHandler(path => path switch
        {
            _ when path.Contains("/issues?", StringComparison.Ordinal) => Ok("""
                [ { "iid": 4, "title": "Crash", "web_url": "https://gitlab.com/group/sub/project/-/issues/4", "created_at": "2026-05-25T10:00:00Z", "author": { "id": 77, "username": "dev" } },
                  { "iid": 3, "title": "Typo", "web_url": "https://gitlab.com/group/sub/project/-/issues/3", "created_at": "2026-05-24T10:00:00Z", "author": { "id": 78, "username": "guest" } } ]
                """),
            _ when path.Contains("/merge_requests?", StringComparison.Ordinal) => Ok("""
                [ { "iid": 5, "title": "Fix", "web_url": "https://gitlab.com/group/sub/project/-/merge_requests/5", "created_at": "2026-05-25T11:00:00Z", "sha": "def456", "author": { "id": 77, "username": "dev" } } ]
                """),
            _ when path.EndsWith("/members/all/77", StringComparison.Ordinal) => Ok("""{ "id": 77, "access_level": 30 }"""),
            _ when path.EndsWith("/members/all/78", StringComparison.Ordinal) => new HttpResponseMessage(HttpStatusCode.NotFound),
            _ => new HttpResponseMessage(HttpStatusCode.InternalServerError),
        });
        using var feed = Feed(handler, GitLab, GitRemoteCredential.Bearer("secret"));

        var issues = await feed.ReadAsync("C:/repo", GitFeedKind.Issues);
        var requests = await feed.ReadAsync("C:/repo", GitFeedKind.PullRequests);

        Assert.AreEqual(GitFeedStatus.Ok, issues.Status);
        CollectionAssert.AreEqual(new[] { 4, 3 }, issues.Items.Select(static item => item.Number).ToArray());
        Assert.AreEqual("dev", issues.Items[0].Author);
        Assert.IsNull(issues.Items[0].Trusted, "GitLab does not say who can write with the list.");
        Assert.IsNull(issues.EntityTag);
        Assert.AreEqual("def456", requests.Items.Single().Head);
        Assert.AreEqual("/api/v4/projects/group/sub/project/issues?state=opened&order_by=created_at&sort=desc&per_page=30", handler.Requests[0]);
        Assert.AreEqual("/api/v4/projects/group%2Fsub%2Fproject/issues?state=opened&order_by=created_at&sort=desc&per_page=30", handler.RawRequests[0]);
        Assert.AreEqual("/api/v4/projects/group/sub/project/merge_requests?state=opened&order_by=updated_at&sort=desc&per_page=100", handler.Requests[1]);

        Assert.AreEqual(true, await feed.IsTrustedAsync(GitLab, issues.Items[0]), "A developer can write.");
        Assert.AreEqual(true, await feed.IsTrustedAsync(GitLab, requests.Items[0]));
        Assert.AreEqual(false, await feed.IsTrustedAsync(GitLab, issues.Items[1]), "Someone who is not a member cannot.");
        Assert.AreEqual(false, await feed.IsTrustedAsync(GitLab, issues.Items[1]));
        Assert.AreEqual(1, handler.Requests.Count(static path => path.EndsWith("/members/all/77", StringComparison.Ordinal)), "The same person is asked about once.");
        Assert.AreEqual(1, handler.Requests.Count(static path => path.EndsWith("/members/all/78", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task GitLab_WhenWhoCanWriteCannotBeAsked_TellsNothing_AndAsksAgain()
    {
        var down = true;
        var handler = new StubHandler(path => path.EndsWith("/members/all/77", StringComparison.Ordinal)
            ? down ? new HttpResponseMessage(HttpStatusCode.BadGateway) : Ok("""{ "id": 77, "access_level": 20 }""")
            : Ok("""[ { "iid": 4, "title": "Crash", "web_url": "https://gitlab.com/x/-/issues/4", "created_at": "2026-05-25T10:00:00Z", "author": { "id": 77, "username": "dev" } } ]"""));
        using var feed = Feed(handler, GitLab, null);
        var item = (await feed.ReadAsync("C:/repo", GitFeedKind.Issues)).Items.Single();

        Assert.IsNull(await feed.IsTrustedAsync(GitLab, item));
        down = false;

        Assert.AreEqual(false, await feed.IsTrustedAsync(GitLab, item), "A reporter cannot write.");
    }

    [TestMethod]
    public async Task AzureDevOps_ListsWorkItemsInTwoSteps_AndPullRequestsInOne()
    {
        var handler = new StubHandler(path => path switch
        {
            _ when path.Contains("/_apis/wit/wiql", StringComparison.Ordinal) => Ok("""{ "workItems": [ { "id": 41 }, { "id": 40 } ] }"""),
            _ when path.Contains("/_apis/wit/workitems?ids=41,40", StringComparison.Ordinal) => Ok("""
                { "value": [
                  { "id": 40, "fields": { "System.Title": "Older", "System.CreatedDate": "2026-05-24T10:00:00Z", "System.CreatedBy": { "uniqueName": "a@example.com" } } },
                  { "id": 41, "fields": { "System.Title": "Newer", "System.CreatedDate": "2026-05-25T10:00:00Z", "System.CreatedBy": { "displayName": "B" } } } ] }
                """),
            _ when path.Contains("/pullrequests?", StringComparison.Ordinal) => Ok("""
                { "value": [ { "pullRequestId": 6, "title": "Change", "creationDate": "2026-05-25T12:00:00Z", "createdBy": { "uniqueName": "a@example.com" }, "lastMergeSourceCommit": { "commitId": "0a1b" } } ] }
                """),
            _ => new HttpResponseMessage(HttpStatusCode.InternalServerError),
        });
        using var feed = Feed(handler, Azure, GitRemoteCredential.Basic(string.Empty, "pat"));

        var items = await feed.ReadAsync("C:/repo", GitFeedKind.Issues);
        var requests = await feed.ReadAsync("C:/repo", GitFeedKind.PullRequests);

        Assert.AreEqual(GitFeedStatus.Ok, items.Status);
        CollectionAssert.AreEqual(new[] { 41, 40 }, items.Items.Select(static item => item.Number).ToArray(), "Newest first, whatever the order of the answer.");
        Assert.AreEqual("https://dev.azure.com/org/My%20Project/_workitems/edit/41", items.Items[0].Url);
        Assert.AreEqual("B", items.Items[0].Author);
        Assert.AreEqual("a@example.com", items.Items[1].Author);
        Assert.AreEqual(true, await feed.IsTrustedAsync(Azure, items.Items[0]), "Who can open a work item belongs to the organization.");
        Assert.AreEqual("POST", handler.Methods[0]);
        StringAssert.Contains(handler.Bodies[0], "ORDER BY [System.CreatedDate] DESC");
        Assert.AreEqual("/org/My Project/_apis/wit/wiql?$top=30&api-version=7.1", handler.Requests[0]);

        var request = requests.Items.Single();
        Assert.AreEqual(6, request.Number);
        Assert.AreEqual("0a1b", request.Head);
        Assert.AreEqual("https://dev.azure.com/org/My%20Project/_git/repo/pullrequest/6", request.Url);
        Assert.AreEqual("/org/My Project/_apis/git/repositories/repo/pullrequests?searchCriteria.status=active&$top=100&api-version=7.1", handler.Requests[2]);
        Assert.AreEqual("dev.azure.com", handler.Hosts[2]);
    }

    [TestMethod]
    public async Task AzureDevOps_SignInPageInsteadOfAnAnswer_IsARefusal()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NonAuthoritativeInformation) { Content = new StringContent("<html>Sign in</html>", Encoding.UTF8, "text/html") });
        using var feed = Feed(handler, Azure, null);

        var page = await feed.ReadAsync("C:/repo", GitFeedKind.PullRequests);

        Assert.AreEqual(GitFeedStatus.Refused, page.Status);
        Assert.AreEqual("Azure DevOps asks to sign in.", page.Message);
        Assert.AreSame(Azure, page.Repository);
    }

    [TestMethod]
    public async Task Refusals_Failures_AndAFolderWithoutRepository_AreResults()
    {
        foreach (var (status, expected, message) in new[]
                 {
                     (HttpStatusCode.Unauthorized, GitFeedStatus.Refused, "GitHub asks to sign in."),
                     (HttpStatusCode.Forbidden, GitFeedStatus.Refused, "GitHub refused the request: no access, or too many requests."),
                     (HttpStatusCode.NotFound, GitFeedStatus.Refused, "GitHub does not show org/repo to this account."),
                     (HttpStatusCode.BadGateway, GitFeedStatus.Failed, "GitHub answered 502."),
                 })
        {
            using var feed = Feed(new StubHandler(_ => new HttpResponseMessage(status)), GitHub, null);
            var page = await feed.ReadAsync("C:/repo", GitFeedKind.Issues);
            Assert.AreEqual(expected, page.Status, status.ToString());
            Assert.AreEqual(message, page.Message);
            Assert.AreEqual(0, page.Items.Count);
        }

        using (var feed = Feed(new StubHandler(_ => throw new HttpRequestException("No route.")), GitHub, null))
        {
            var page = await feed.ReadAsync("C:/repo", GitFeedKind.Issues);
            Assert.AreEqual(GitFeedStatus.Failed, page.Status);
            Assert.AreEqual("No route.", page.Message);
        }

        using (var feed = Feed(new StubHandler(_ => Ok("not json")), GitHub, null))
        {
            Assert.AreEqual(GitFeedStatus.Failed, (await feed.ReadAsync("C:/repo", GitFeedKind.Issues)).Status);
        }

        var handler = new StubHandler(_ => Ok("[]"));
        using (var feed = Feed(handler, null, null))
        {
            var page = await feed.ReadAsync("C:/repo", GitFeedKind.Issues);
            Assert.AreEqual(GitFeedStatus.NoRepository, page.Status);
            Assert.IsNull(page.Repository);
            Assert.AreEqual(0, handler.Requests.Count);
        }
    }

    [TestMethod]
    public async Task Read_RejectsABlankFolder_AndStopsWhenCanceled()
    {
        using var feed = Feed(new StubHandler(_ => Ok("[]")), GitHub, null);
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await feed.ReadAsync(" ", GitFeedKind.Issues));
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(async () => await feed.IsTrustedAsync(null!, new GitFeedItem(1, "t", "u", null, default)));
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(async () => await feed.IsTrustedAsync(GitHub, null!));
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        using var waiting = Feed(new StubHandler(_ => Ok("[]")), GitHub, null);
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await waiting.ReadAsync("C:/repo", GitFeedKind.Issues, null, canceled.Token));
    }

    private static GitRepositoryFeed Feed(StubHandler handler, GitRepositoryReference? repository, GitRemoteCredential? credential)
        => new(new HttpClient(handler), (_, _) => ValueTask.FromResult(repository), (_, _) => ValueTask.FromResult(credential));

    private static HttpResponseMessage Ok(string content, string? entityTag = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content, Encoding.UTF8, "application/json") };
        if (entityTag is not null) response.Headers.ETag = EntityTagHeaderValue.Parse(entityTag);
        return response;
    }

    /// <summary>Answers the provider hosts from a literal function of the unescaped path and query, and records what was asked.</summary>
    private sealed class StubHandler(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        public List<string> RawRequests { get; } = [];

        public List<string> Hosts { get; } = [];

        public List<string> Methods { get; } = [];

        public List<string?> Bodies { get; } = [];

        public List<string?> Authorizations { get; } = [];

        public List<string?> EntityTags { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.AreEqual(Uri.UriSchemeHttps, request.RequestUri!.Scheme);
            var path = Uri.UnescapeDataString(request.RequestUri.PathAndQuery);
            Requests.Add(path);
            RawRequests.Add(request.RequestUri.PathAndQuery);
            Hosts.Add(request.RequestUri.Host);
            Methods.Add(request.Method.Method);
            Bodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
            Authorizations.Add(request.Headers.Authorization?.ToString());
            EntityTags.Add(request.Headers.IfNoneMatch.Count == 0 ? null : request.Headers.IfNoneMatch.ToString());
            return respond(path);
        }
    }
}
