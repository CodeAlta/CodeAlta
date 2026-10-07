using System.Net;
using System.Text;
using CodeAlta.Plugin.Git;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Tests;

[TestClass]
public sealed class GitHostTrackerTests
{
    [TestMethod]
    public void BitbucketRemotes_AreRecognized()
    {
        foreach (var remote in new[] { "https://bitbucket.org/team/app.git", "git@bitbucket.org:team/app.git", "ssh://git@altssh.bitbucket.org:443/team/app.git" })
        {
            Assert.IsTrue(GitRemoteUrl.TryParse(remote, [], out var repository), remote);
            Assert.AreEqual((GitRemoteProvider.Bitbucket, "bitbucket.org", "team/app"), (repository.Provider, repository.Host, repository.FullName));
        }

        Assert.AreEqual("Bitbucket", GitRemoteProvider.Bitbucket.GetDisplayName());
        Assert.IsFalse(GitRemoteUrl.TryParse("https://bitbucket.org/team", [], out _));
    }

    [TestMethod]
    public async Task GitHub_ListsIssuesWithoutThePullRequestsOfTheSameListing_AndReadsOneWithItsComments()
    {
        var handler = new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/repos/o/r/issues" => """
                [{"number":7,"title":"A bug","html_url":"https://github.com/o/r/issues/7","state":"open","user":{"login":"ana"},"comments":2,
                  "labels":[{"name":"bug","color":"D73A4A"}],"assignees":[{"login":"bo"}],"created_at":"2026-10-01T10:00:00Z","updated_at":"2026-10-02T10:00:00Z","type":{"name":"Bug"}},
                 {"number":8,"title":"A pull","html_url":"https://github.com/o/r/pull/8","state":"open","pull_request":{"merged_at":null}},
                 {"number":6,"title":"Done","html_url":"https://github.com/o/r/issues/6","state":"closed"},
                 {"number":5,"title":"Not a link","html_url":"http://github.com/o/r/issues/5","state":"open"}]
                """,
            "/repos/o/r/issues/7" => """{"number":7,"title":"A bug","html_url":"https://github.com/o/r/issues/7","state":"open","comments":2,"body":"It **fails**."}""",
            "/repos/o/r/issues/7/comments" => """[{"user":{"login":"bo"},"created_at":"2026-10-02T09:00:00Z","body":"Seen."},{"user":{"login":"ana"},"body":""}]""",
            _ => null,
        });
        var tracker = Tracker(handler, GitRepositoryReference.GitHub("o", "r"));

        Assert.AreEqual(("github", "GitHub", "o/r", "https://github.com/o/r"), (tracker.Service, tracker.DisplayName, tracker.Location, tracker.WebUrl));
        var page = await tracker.ListAsync(new(TrackedItemKind.Issue, TrackedItemFilter.All, null, 10), default);

        Assert.IsNull(page.Problem);
        CollectionAssert.AreEqual(new[] { "7", "6" }, page.Items.Select(static item => item.Id).ToArray(), "A pull request and an address that is not https are not issues to show.");
        var first = page.Items[0];
        Assert.AreEqual((TrackedItemState.Open, "ana", "Bug", 2, "bo"), (first.State, first.Author, first.Type, first.CommentCount, first.Assignees.Single()));
        Assert.AreEqual(new TrackedLabel("bug", "D73A4A"), first.Labels.Single());
        Assert.AreEqual(TrackedItemState.Closed, page.Items[1].State);
        StringAssert.Contains(handler.Requests[0], "state=all&sort=updated");

        var detail = await tracker.ReadAsync(TrackedItemKind.Issue, "7", default);
        Assert.AreEqual(("It **fails**.", 1, "Seen.", true), (detail!.Body, detail.Comments.Count, detail.Comments[0].Body, detail.MoreComments));
        Assert.IsNull(await tracker.ReadAsync(TrackedItemKind.Issue, "seven", default));
        Assert.IsNull(await tracker.ReadAsync(TrackedItemKind.PullRequest, "7", default), "An issue is not read as a pull request.");
    }

    [TestMethod]
    public async Task GitHub_PullRequests_AreListedByStateAndSearchedForWordsAndMerges()
    {
        var handler = new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/repos/o/r/pulls" => """
                [{"number":12,"title":"Draft work","html_url":"https://github.com/o/r/pull/12","state":"open","draft":true,"head":{"ref":"feature"},"base":{"ref":"main"},"user":{"login":"ana"}},
                 {"number":11,"title":"Ready","html_url":"https://github.com/o/r/pull/11","state":"open","draft":false,"head":{"ref":"fix"},"base":{"ref":"main"}}]
                """,
            "/search/issues" => """{"items":[{"number":9,"title":"Merged one","html_url":"https://github.com/o/r/pull/9","state":"closed","pull_request":{"merged_at":"2026-09-01T00:00:00Z"}}]}""",
            _ => null,
        });
        var tracker = Tracker(handler, GitRepositoryReference.GitHub("o", "r"));

        var open = await tracker.ListAsync(new(TrackedItemKind.PullRequest, TrackedItemFilter.Open, null, 10), default);
        Assert.AreEqual((TrackedItemState.Draft, "feature", "main"), (open.Items[0].State, open.Items[0].SourceBranch, open.Items[0].TargetBranch));
        Assert.AreEqual(TrackedItemState.Open, open.Items[1].State);
        StringAssert.Contains(handler.Requests[^1], "/pulls?state=open");

        var merged = await tracker.ListAsync(new(TrackedItemKind.PullRequest, TrackedItemFilter.Merged, " cache ", 10), default);
        Assert.AreEqual(TrackedItemState.Merged, merged.Items.Single().State);
        StringAssert.Contains(Uri.UnescapeDataString(handler.Requests[^1]), "q=repo:o/r is:pr is:merged cache");

        Assert.IsEmpty((await tracker.ListAsync(new(TrackedItemKind.Issue, TrackedItemFilter.Merged, null, 10), default)).Items, "No issue was ever merged: nothing is asked.");
        Assert.HasCount(2, handler.Requests);
    }

    [TestMethod]
    public async Task ARefusal_IsAProblemInWords_AndSaysWhenSigningInHelps()
    {
        var tracker = Tracker(new Handler(_ => null, HttpStatusCode.NotFound), GitRepositoryReference.GitHub("o", "private"));
        var page = await tracker.ListAsync(new(TrackedItemKind.Issue, TrackedItemFilter.Open, null, 10), default);
        Assert.IsEmpty(page.Items);
        Assert.IsTrue(page.NeedsSignIn, "A private repository answers 'not found' to a visitor.");
        StringAssert.Contains(page.Problem, "sign in");

        var signedIn = Tracker(new Handler(_ => null, HttpStatusCode.NotFound), GitRepositoryReference.GitHub("o", "gone"), GitRemoteCredential.Bearer("token"));
        Assert.IsFalse((await signedIn.ListAsync(new(TrackedItemKind.Issue, TrackedItemFilter.Open, null, 10), default)).NeedsSignIn);

        var unreachable = Tracker(new Handler(_ => throw new HttpRequestException("no route to host 10.0.0.1")), GitRepositoryReference.GitHub("o", "r"));
        Assert.AreEqual("GitHub could not be reached.", (await unreachable.ListAsync(new(TrackedItemKind.Issue, TrackedItemFilter.Open, null, 10), default)).Problem);
    }

    [TestMethod]
    public async Task GitLab_ListsIssuesAndMergeRequests_AndLeavesOutWhatItWroteItself()
    {
        var handler = new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/v4/projects/group%2Fsub%2Fapp/merge_requests" => """
                [{"iid":4,"title":"Draft: new parser","web_url":"https://gitlab.example.com/group/sub/app/-/merge_requests/4","state":"opened","draft":true,
                  "author":{"username":"ana"},"labels":["parser"],"source_branch":"parser","target_branch":"main","user_notes_count":3,"updated_at":"2026-10-02T10:00:00Z"},
                 {"iid":3,"title":"Old","web_url":"https://gitlab.example.com/group/sub/app/-/merge_requests/3","state":"merged"}]
                """,
            "/api/v4/projects/group%2Fsub%2Fapp/issues/2" => """{"iid":2,"title":"Slow","web_url":"https://gitlab.example.com/group/sub/app/-/issues/2","state":"closed","description":"Too slow.","issue_type":"incident"}""",
            "/api/v4/projects/group%2Fsub%2Fapp/issues/2/notes" => """[{"system":true,"body":"closed"},{"system":false,"body":"Fixed in 1.2.","author":{"username":"bo"},"created_at":"2026-10-02T09:00:00Z"}]""",
            _ => null,
        });
        var tracker = Tracker(handler, GitRepositoryReference.GitLab("gitlab.example.com", "group/sub", "app"));

        var page = await tracker.ListAsync(new(TrackedItemKind.PullRequest, TrackedItemFilter.All, "parser", 10), default);
        Assert.AreEqual((TrackedItemState.Draft, "parser", 3, "parser"), (page.Items[0].State, page.Items[0].SourceBranch, page.Items[0].CommentCount, page.Items[0].Labels.Single().Name));
        Assert.AreEqual(TrackedItemState.Merged, page.Items[1].State);
        StringAssert.Contains(handler.Requests[0], "search=parser");
        Assert.IsFalse(handler.Requests[0].Contains("state=", StringComparison.Ordinal));

        var detail = await tracker.ReadAsync(TrackedItemKind.Issue, "2", default);
        Assert.AreEqual((TrackedItemState.Closed, "incident", "Too slow.", "Fixed in 1.2."), (detail!.Item.State, detail.Item.Type, detail.Body, detail.Comments.Single().Body));
    }

    [TestMethod]
    public async Task AzureDevOps_ReadsWorkItemsInTheOrderAsked_AndPullRequestsWithTheirBranches()
    {
        var handler = new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/org/proj/_apis/wit/wiql" => """{"workItems":[{"id":31},{"id":30}]}""",
            "/org/proj/_apis/wit/workitems" => """
                {"value":[{"id":30,"fields":{"System.Title":"Older","System.State":"Closed","System.WorkItemType":"Bug","System.TeamProject":"proj"}},
                          {"id":31,"fields":{"System.Title":"Newer","System.State":"Active","System.WorkItemType":"User Story","System.Tags":"ui; perf","System.AssignedTo":{"displayName":"Ana"},
                            "Microsoft.VSTS.Common.Priority":2,"System.TeamProject":"proj"}}]}
                """,
            "/org/proj/_apis/git/repositories/app/pullrequests" => """
                {"value":[{"pullRequestId":5,"title":"Add cache","status":"active","isDraft":false,"createdBy":{"displayName":"Ana"},"creationDate":"2026-10-01T10:00:00Z",
                  "sourceRefName":"refs/heads/cache","targetRefName":"refs/heads/main","reviewers":[{"displayName":"Bo"},{"displayName":"Team","isContainer":true}]},
                 {"pullRequestId":4,"title":"Remove logs","status":"completed"}]}
                """,
            _ => null,
        });
        var tracker = Tracker(handler, GitRepositoryReference.AzureDevOps("org", "proj", "app"));

        var items = await tracker.ListAsync(new(TrackedItemKind.Issue, TrackedItemFilter.Open, null, 10), default);
        CollectionAssert.AreEqual(new[] { "31", "30" }, items.Items.Select(static item => item.Id).ToArray());
        var newer = items.Items[0];
        Assert.AreEqual((TrackedItemState.Open, "Active", "User Story", "2", "Ana"), (newer.State, newer.StateText, newer.Type, newer.Priority, newer.Assignees.Single()));
        CollectionAssert.AreEqual(new[] { "ui", "perf" }, newer.Labels.Select(static label => label.Name).ToArray());
        Assert.AreEqual("https://dev.azure.com/org/proj/_workitems/edit/31", newer.Url);
        StringAssert.Contains(System.Text.Json.JsonDocument.Parse(handler.Bodies[0]).RootElement.GetProperty("query").GetString(), "NOT IN ('Closed', 'Done', 'Removed', 'Completed')");

        var pulls = await tracker.ListAsync(new(TrackedItemKind.PullRequest, TrackedItemFilter.All, "cache", 10), default);
        var pull = pulls.Items.Single();
        Assert.AreEqual(("5", "cache", "main", "Bo", "https://dev.azure.com/org/proj/_git/app/pullrequest/5"), (pull.Id, pull.SourceBranch, pull.TargetBranch, pull.Assignees.Single(), pull.Url));
        StringAssert.Contains(handler.Requests[^1], "searchCriteria.status=all");
    }

    [TestMethod]
    public async Task Bitbucket_ListsPullRequestsAndIssuesWithItsOwnQueries()
    {
        var handler = new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/2.0/repositories/team/app/pullrequests" => """
                {"values":[{"id":10,"title":"Cleanup","state":"MERGED","links":{"html":{"href":"https://bitbucket.org/team/app/pull-requests/10"}},"author":{"display_name":"Ana"},
                  "source":{"branch":{"name":"cleanup"}},"destination":{"branch":{"name":"main"}},"comment_count":1,"updated_on":"2026-08-11T06:41:42.031771+00:00","draft":false}]}
                """,
            "/2.0/repositories/team/app/issues" => """
                {"values":[{"id":3,"title":"Crash","state":"on hold","kind":"bug","priority":"major","links":{"html":{"href":"https://bitbucket.org/team/app/issues/3"}},"reporter":{"display_name":"Bo"}}]}
                """,
            "/2.0/repositories/team/app/issues/3" => """{"id":3,"title":"Crash","state":"resolved","links":{"html":{"href":"https://bitbucket.org/team/app/issues/3"}},"content":{"raw":"It crashes."}}""",
            "/2.0/repositories/team/app/issues/3/comments" => """{"values":[{"content":{"raw":"Fixed."},"user":{"display_name":"Ana"},"created_on":"2026-08-12T00:00:00+00:00"},{"content":{"raw":null}}]}""",
            _ => null,
        });
        var tracker = Tracker(handler, GitRepositoryReference.Bitbucket("team", "app"));

        var pull = (await tracker.ListAsync(new(TrackedItemKind.PullRequest, TrackedItemFilter.Merged, "clean \"up\"", 10), default)).Items.Single();
        Assert.AreEqual((TrackedItemState.Merged, "cleanup", "main", "Ana", 1), (pull.State, pull.SourceBranch, pull.TargetBranch, pull.Author, pull.CommentCount));
        StringAssert.Contains(Uri.UnescapeDataString(handler.Requests[0]), "q=state = \"MERGED\" AND title ~ \"clean \\\"up\\\"\"");

        var issue = (await tracker.ListAsync(new(TrackedItemKind.Issue, TrackedItemFilter.Open, null, 10), default)).Items.Single();
        Assert.AreEqual((TrackedItemState.Open, "on hold", "bug", "major", "Bo"), (issue.State, issue.StateText, issue.Type, issue.Priority, issue.Author));

        var detail = await tracker.ReadAsync(TrackedItemKind.Issue, "3", default);
        Assert.AreEqual((TrackedItemState.Closed, "resolved", "It crashes.", "Fixed."), (detail!.Item.State, detail.Item.StateText, detail.Body, detail.Comments.Single().Body));
    }

    [TestMethod]
    public async Task ThePicker_FindsPullRequestsByNumberAndByWords_FromOneListing()
    {
        var handler = new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/repos/o/r/pulls" => """
                [{"number":120,"title":"Add cache","html_url":"https://github.com/o/r/pull/120","state":"open","head":{"ref":"a"},"updated_at":"2026-10-02T10:00:00Z"},
                 {"number":34,"title":"Remove logs","html_url":"https://github.com/o/r/pull/34","state":"closed","merged_at":"2026-09-01T00:00:00Z","head":{"ref":"b"},"updated_at":"2026-09-01T10:00:00Z"}]
                """,
            "/repos/o/r/pulls/12" => """{"number":12,"title":"Exact","html_url":"https://github.com/o/r/pull/12","state":"open","head":{"ref":"c"}}""",
            _ => null,
        });
        using var client = GitIssueLookup.CreateHttpClient(handler);
        var lookup = new GitIssueLookup(client, new GitIssueLookupOptions { CacheDuration = TimeSpan.FromMinutes(1) });
        var repository = GitRepositoryReference.GitHub("o", "r");

        var recent = await lookup.QueryPullRequestsAsync(repository, string.Empty, 10);
        Assert.IsTrue(recent.All(static item => item.IsPullRequest));
        Assert.AreEqual((120, true, "[#120](https://github.com/o/r/pull/120)"), (recent[0].Number, recent[0].IsOpen, recent[0].Markdown));
        Assert.IsFalse(recent[1].IsOpen);

        CollectionAssert.AreEqual(new[] { 12, 120 }, (await lookup.QueryPullRequestsAsync(repository, "12", 10)).Select(static item => item.Number).ToArray(), "The exact number first, then the ones that start with it.");
        CollectionAssert.AreEqual(new[] { 34 }, (await lookup.QueryPullRequestsAsync(repository, "lo", 10)).Select(static item => item.Number).ToArray());
        Assert.AreEqual(1, handler.Requests.Count(static request => request.Contains("/pulls?", StringComparison.Ordinal)), "The recent pull requests are listed once while typing.");
    }

    private static IIssueTracker Tracker(Handler handler, GitRepositoryReference repository, GitRemoteCredential? credential = null)
    {
        var lookup = new GitIssueLookup(GitIssueLookup.CreateHttpClient(handler), new GitIssueLookupOptions
        {
            CacheDuration = TimeSpan.Zero,
            CredentialProvider = credential is null ? null : (_, _) => ValueTask.FromResult<GitRemoteCredential?>(credential),
        });
        return lookup.GetTracker(repository);
    }

    // Answers each request with the JSON its path has, or with a refusal.
    private sealed class Handler(Func<HttpRequestMessage, string?> answer, HttpStatusCode missing = HttpStatusCode.NotFound) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.PathAndQuery);
            if (request.Content is not null) Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            return answer(request) is { } json
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(missing);
        }
    }
}
