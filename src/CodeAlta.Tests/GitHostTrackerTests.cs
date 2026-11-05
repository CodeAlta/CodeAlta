using System.Net;
using System.Text;
using CodeAlta.Catalog.Issues;
using CodeAlta.Plugin.Git;
using CodeAlta.Plugins.Abstractions;
using CodeAlta.Plugins.Issues;

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
    public async Task AzureDevOps_GivesEachWorkItemTheColorOfItsState_ReadOncePerType()
    {
        var handler = new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/org/proj/_apis/wit/wiql" => """{"workItems":[{"id":1},{"id":2},{"id":3}]}""",
            "/org/proj/_apis/wit/workitems" => """
                {"value":[{"id":1,"fields":{"System.Title":"A","System.State":"Active","System.WorkItemType":"Bug","System.TeamProject":"proj"}},
                          {"id":2,"fields":{"System.Title":"B","System.State":"On Hold","System.WorkItemType":"Bug","System.TeamProject":"proj"}},
                          {"id":3,"fields":{"System.Title":"C","System.State":"Ready","System.WorkItemType":"User Story","System.TeamProject":"proj"}}]}
                """,
            "/org/proj/_apis/wit/workitemtypes/Bug/states" => """{"value":[{"name":"Active","color":"007acc","category":"InProgress"},{"name":"On Hold","color":"cc293d"}]}""",
            "/org/proj/_apis/wit/workitems/1" => """{"id":1,"fields":{"System.Title":"A","System.State":"Active","System.WorkItemType":"Bug","System.TeamProject":"proj"}}""",
            _ => null,
        });
        var tracker = Tracker(handler, GitRepositoryReference.AzureDevOps("org", "proj", "app"));

        var items = await tracker.ListAsync(new(TrackedItemKind.Issue, TrackedItemFilter.Open, null, 10), default);
        CollectionAssert.AreEqual(new[] { "007acc", "cc293d", null }, items.Items.Select(static item => item.StateColor).ToArray(),
            "Each state has the color of its type; a type whose states cannot be read has none.");
        Assert.AreEqual(1, handler.Requests.Count(static request => request.StartsWith("/org/proj/_apis/wit/workitemtypes/Bug/states", StringComparison.Ordinal)), "The states of a type are read once.");
        StringAssert.Contains(string.Join(' ', handler.Requests), "/workitemtypes/User%20Story/states", "The name of a type is escaped.");

        var detail = await tracker.ReadAsync(TrackedItemKind.Issue, "1", default);
        Assert.AreEqual("007acc", detail!.Item.StateColor, "A work item read alone has the color of its state too.");
    }

    [TestMethod]
    public async Task AzureDevOps_ReadsTheTextOfABug_FromItsReproStepsAndAcceptanceCriteria()
    {
        var handler = new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/org/proj/_apis/wit/workitems/64" => """
                {"id":64,"fields":{"System.Title":"Badge missing","System.State":"Active","System.WorkItemType":"Bug","System.TeamProject":"proj",
                  "Microsoft.VSTS.TCM.ReproSteps":"<div>Open a mutation.</div>","Microsoft.VSTS.TCM.SystemInfo":" ","Microsoft.VSTS.Common.AcceptanceCriteria":"<div>The badge stays.</div>"}}
                """,
            "/org/proj/_apis/wit/workitems/65" => """
                {"id":65,"fields":{"System.Title":"Export","System.State":"New","System.WorkItemType":"User Story","System.TeamProject":"proj","System.Description":"<div>As a user.</div>",
                  "Microsoft.VSTS.Common.AcceptanceCriteria":"<div>It exports.</div>"}}
                """,
            _ => null,
        });
        var tracker = Tracker(handler, GitRepositoryReference.AzureDevOps("org", "proj", "app"));

        var bug = await tracker.ReadAsync(TrackedItemKind.Issue, "64", default);
        Assert.AreEqual("<h2>Repro Steps</h2>\n<div>Open a mutation.</div>\n<h2>Acceptance Criteria</h2>\n<div>The badge stays.</div>", bug!.Body,
            "A bug has no description: its text is in Repro Steps and Acceptance Criteria, each under its name; an empty one is left out.");
        Assert.IsFalse(handler.Requests[0].Contains("fields=", StringComparison.Ordinal), "A field the organization does not have would refuse the whole read: all the fields are read.");

        var story = await tracker.ReadAsync(TrackedItemKind.Issue, "65", default);
        Assert.AreEqual("<div>As a user.</div>\n<h2>Acceptance Criteria</h2>\n<div>It exports.</div>", story!.Body, "The description is shown as it is, without a heading, before the other texts.");
    }

    [TestMethod]
    public async Task AzureDevOps_PullRequests_WaitingForMyReview_AreFoundThroughNestedGroups()
    {
        var hosts = new List<string>();
        var handler = new Handler(request =>
        {
            lock (hosts) hosts.Add(request.RequestUri!.Host + request.RequestUri.AbsolutePath);
            return PullRequestAnswer(request);
        });
        var tracker = Tracker(handler, GitRepositoryReference.AzureDevOps("org", "proj", "app"));
        static Dictionary<string, string> Chosen(params (string Id, string Value)[] values) => values.ToDictionary(static pair => pair.Id, static pair => pair.Value);
        async Task<TrackedItemPage> List(Dictionary<string, string> filters, TrackedItemFilter state = TrackedItemFilter.Open)
            => await tracker.ListAsync(new(TrackedItemKind.PullRequest, state, null, 50) { Filters = filters }, default);

        var waiting = await List(Chosen(("scope", "@project"), ("reviewer", "@me_groups"), ("required", "@required"), ("vote", "@none")));
        Assert.AreEqual("1 5", string.Join(' ', waiting.Items.Select(static item => item.Id)), "The required reviews of a group of the user, a nested one too, that no one gave.");
        Assert.AreEqual("Required · through Core · No vote yet", waiting.Items[0].Match);
        Assert.IsNull(waiting.Items[0].Place, "A pull request of the repository of the project is not elsewhere.");
        var core = waiting.Items[0].Reviewers.Single(static reviewer => reviewer.Name == "Core");
        Assert.AreEqual((true, true, true, TrackedVote.None), (core.IsGroup, core.Required, core.Mine, core.Vote));
        Assert.IsFalse(waiting.Items[0].Reviewers.Single(static reviewer => reviewer.Name == "Security").Mine);
        StringAssert.Contains(handler.Requests.Single(static request => request.Contains("/_apis/git/pullrequests?", StringComparison.Ordinal)), "/org/proj/_apis/git/pullrequests?searchCriteria.status=active&$top=200&$skip=0");
        CollectionAssert.Contains(hosts, "vssps.dev.azure.com/org/_apis/identities", "The groups of the user are on the vssps host of the organization.");

        var mine = await List(Chosen(("scope", "@project"), ("reviewer", "@me_groups"), ("vote", "@none"), ("author", "@not_me"), ("draft", "@hide")));
        Assert.AreEqual("3 1", string.Join(' ', mine.Items.Select(static item => item.Id)), "Optional reviews too, the newest first; not the draft the user opened.");
        Assert.AreEqual("Optional · No vote yet", mine.Items[0].Match, "A review asked of the user directly is not through a group.");

        var approved = await List(Chosen(("scope", "@project"), ("reviewer", "@me_groups"), ("vote", "@approved\u001F@suggestions")));
        var other = approved.Items.Single();
        Assert.AreEqual(("2", "proj/other", "https://dev.azure.com/org/proj/_git/other/pullrequest/2"), (other.Id, other.Place, other.Url),
            "A colleague approved for the group: the pull request no longer waits, and it is of another repository.");

        Assert.AreEqual("3", string.Join(' ', (await List(Chosen(("reviewer", "@me")))).Items.Select(static item => item.Id)), "Me alone, in the repository of the project.");
        StringAssert.Contains(handler.Requests[^1], "/org/proj/_apis/git/repositories/app/pullrequests?");
        Assert.AreEqual("5", string.Join(' ', (await List(Chosen(("scope", "@organization"), ("author", "@me")))).Items.Select(static item => item.Id)));
        StringAssert.Contains(handler.Requests.Last(static request => request.Contains("pullrequests?", StringComparison.Ordinal)), "/org/_apis/git/pullrequests?");
        Assert.AreEqual("5 1", string.Join(' ', (await List(Chosen(("scope", "@project"), ("target_branch", "main\u001Frelease"), ("sort", "@oldest")))).Items.Select(static item => item.Id)));
        Assert.IsEmpty((await List(Chosen(("reviewer", "@someone")))).Items, "A value the filter does not offer lists nothing.");

        var filters = await tracker.GetFiltersAsync(new(Chosen(("scope", "@project")), []) { Kind = TrackedItemKind.PullRequest }, default);
        CollectionAssert.AreEqual(new[] { "scope", "reviewer", "required", "vote", "author", "target_branch", "draft", "sort" }, filters.Select(static filter => filter.Id).ToArray());
        Assert.IsNull(filters[1].Problem);
        CollectionAssert.AreEqual(new[] { "develop", "main", "release" }, filters[5].Values.Select(static value => value.Id).ToArray(), "The target branches of the open pull requests of the scope.");
        Assert.IsTrue(filters[3].Multiple && filters[5].Multiple);

        var detail = await tracker.ReadAsync(TrackedItemKind.PullRequest, "2", default);
        Assert.AreEqual(("proj/other", "Looks good."), (detail!.Item.Place, detail.Comments.Single().Body), "A pull request is read by its id, in whichever repository it is.");
        CollectionAssert.Contains(handler.Requests, "/org/proj-id/_apis/git/repositories/other-id/pullrequests/2/threads?api-version=7.1");
    }

    [TestMethod]
    public async Task AzureDevOps_PullRequestsOfMyGroups_AreNotListed_WhenTheGroupsCannotBeRead()
    {
        var handler = new Handler(request => request.RequestUri!.Host.StartsWith("vssps.", StringComparison.Ordinal) ? null : PullRequestAnswer(request), HttpStatusCode.Unauthorized);
        var tracker = Tracker(handler, GitRepositoryReference.AzureDevOps("org", "proj", "app"));

        var filters = await tracker.GetFiltersAsync(new(TrackedFilters.None, []) { Kind = TrackedItemKind.PullRequest }, default);
        StringAssert.Contains(filters.Single(static filter => filter.Id == "reviewer").Problem, "Identity (Read)");
        var page = await tracker.ListAsync(new(TrackedItemKind.PullRequest, TrackedItemFilter.Open, null, 50) { Filters = new Dictionary<string, string> { ["reviewer"] = "@me_groups" } }, default);
        Assert.IsEmpty(page.Items, "A list that would miss the reviews of the groups is not given as whole.");
        Assert.HasCount(3, (await tracker.ListAsync(new(TrackedItemKind.PullRequest, TrackedItemFilter.Open, null, 50), default)).Items, "Without those filters the pull requests of the repository are listed.");
    }

    [TestMethod]
    public async Task AzureDevOps_PullRequests_PastTheMostThatAreRead_AreSaid_AndTheirTargetBranchesAreAllRead()
    {
        // Every page is full: 1,000 pull requests to main that wait for no one, and the one to release comes after the first page.
        static string Page(int skip, int count, Func<int, string> target)
            => """{"value":[""" + string.Join(',', Enumerable.Range(skip, count).Select(index => "{\"pullRequestId\":" + (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ",\"title\":\"Pull\",\"status\":\"active\",\"creationDate\":\"2026-10-01T10:00:00Z\",\"targetRefName\":\"refs/heads/" + target(index)
                + "\",\"repository\":{\"id\":\"app-id\",\"name\":\"app\",\"project\":{\"id\":\"proj-id\",\"name\":\"proj\"}},\"reviewers\":[]}")) + "]}";
        var handler = new Handler(request =>
        {
            var query = Uri.UnescapeDataString(request.RequestUri!.Query);
            if (request.RequestUri.AbsolutePath != "/org/proj/_apis/git/pullrequests") return PullRequestAnswer(request);
            var skip = int.Parse(query.Split("$skip=")[1].Split('&')[0], System.Globalization.CultureInfo.InvariantCulture);
            return Page(skip, 200, index => index == 200 ? "release" : "main");
        });
        var tracker = Tracker(handler, GitRepositoryReference.AzureDevOps("org", "proj", "app"));

        var page = await tracker.ListAsync(new(TrackedItemKind.PullRequest, TrackedItemFilter.Open, null, 50)
        {
            Filters = new Dictionary<string, string> { ["scope"] = "@project", ["reviewer"] = "@me_groups" },
        }, default);
        Assert.IsEmpty(page.Items);
        StringAssert.Contains(page.Problem, "1000 most recent pull requests", "A listing that did not read them all says so.");

        var filters = await tracker.GetFiltersAsync(new(new Dictionary<string, string> { ["scope"] = "@project" }, []) { Kind = TrackedItemKind.PullRequest }, default);
        var branches = filters.Single(static filter => filter.Id == "target_branch");
        CollectionAssert.AreEqual(new[] { "main", "release" }, branches.Values.Select(static value => value.Id).ToArray(), "The target branches past the first page are offered.");
        StringAssert.Contains(branches.Problem, "1000 most recent open pull requests");
    }

    [TestMethod]
    public async Task AzureDevOps_APullRequest_IsRead_WhenTheGroupsOfTheUserCannotBeReached()
    {
        var handler = new Handler(request => request.RequestUri!.Host.StartsWith("vssps.", StringComparison.Ordinal)
            ? throw new HttpRequestException("no route to host") : PullRequestAnswer(request));
        var tracker = Tracker(handler, GitRepositoryReference.AzureDevOps("org", "proj", "app"));

        var detail = await tracker.ReadAsync(TrackedItemKind.PullRequest, "2", default);
        Assert.AreEqual("New fields.", detail?.Body, "The groups only mark the reviews of the user.");
        var filters = await tracker.GetFiltersAsync(new(TrackedFilters.None, []) { Kind = TrackedItemKind.PullRequest }, default);
        StringAssert.Contains(filters.Single(static filter => filter.Id == "reviewer").Problem, "Identity (Read)", "The filters are still given, and say why the groups are missing.");
        var page = await tracker.ListAsync(new(TrackedItemKind.PullRequest, TrackedItemFilter.Open, null, 50) { Filters = new Dictionary<string, string> { ["reviewer"] = "@me_groups" } }, default);
        Assert.IsEmpty(page.Items);
        StringAssert.Contains(page.Problem, "Identity (Read)", "A listing by the groups of the user says why it is empty.");
    }

    [TestMethod]
    public async Task AzureDevOps_ARepositoryNamedByItsId_IsThatOfTheProject()
    {
        var tracker = Tracker(new Handler(PullRequestAnswer), GitRepositoryReference.AzureDevOps("org", "proj-id", "app-id"));

        var page = await tracker.ListAsync(new(TrackedItemKind.PullRequest, TrackedItemFilter.Open, null, 50) { Filters = new Dictionary<string, string> { ["scope"] = "@organization" } }, default);

        CollectionAssert.AreEqual(new[] { "1", "2", "3", "5" }, page.Items.Select(static item => item.Id).Order(StringComparer.Ordinal).ToArray());
        CollectionAssert.AreEqual(new[] { "1", "3", "5" }, page.Items.Where(static item => item.Place is null).Select(static item => item.Id).Order(StringComparer.Ordinal).ToArray(),
            "A remote that names the project and the repository by their ids is the repository of the pull requests that have them.");
        Assert.AreEqual("proj/other", page.Items.Single(static item => item.Id == "2").Place);
    }

    [TestMethod]
    public async Task AzureDevOps_ANumber_FindsItsWorkItemOrPullRequest_HoweverOld()
    {
        var handler = new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/org/proj/_apis/wit/wiql" => """{"workItems":[]}""",
            _ => PullRequestAnswer(request),
        });
        var tracker = Tracker(handler, GitRepositoryReference.AzureDevOps("org", "proj", "app"));

        await tracker.ListAsync(new(TrackedItemKind.Issue, TrackedItemFilter.All, "#31", 10), default);
        StringAssert.Contains(System.Text.Json.JsonDocument.Parse(handler.Bodies[^1]).RootElement.GetProperty("query").GetString(), "AND ([System.Title] CONTAINS '#31' OR [System.Id] = 31)");
        await tracker.ListAsync(new(TrackedItemKind.Issue, TrackedItemFilter.All, "cache", 10), default);
        Assert.IsFalse(System.Text.Json.JsonDocument.Parse(handler.Bodies[^1]).RootElement.GetProperty("query").GetString()!.Contains("OR [System.Id]", StringComparison.Ordinal));

        var found = await tracker.ListAsync(new(TrackedItemKind.PullRequest, TrackedItemFilter.Open, "2", 10), default);
        Assert.AreEqual(("2", "proj/other"), (found.Items.Single().Id, found.Items.Single().Place), "A pull request is found by its number, past the most recent ones and in another repository.");
        Assert.IsEmpty((await tracker.ListAsync(new(TrackedItemKind.PullRequest, TrackedItemFilter.Merged, "2", 10), default)).Items, "Only in the list it belongs to.");
        Assert.AreEqual("3", (await tracker.ListAsync(new(TrackedItemKind.PullRequest, TrackedItemFilter.Open, "3", 10), default)).Items.Single().Id, "One the list holds is not read twice.");
        Assert.IsFalse(handler.Requests.Any(static request => request.StartsWith("/org/_apis/git/pullrequests/3", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task AzureDevOps_ReadsTheImagesOfItsAttachments_WithItsSignIn_AndNoOtherAddress()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
        var asked = new List<(string Address, string? Authorization)>();
        var answer = png;
        var handler = new BytesHandler(request =>
        {
            asked.Add((request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString()));
            return answer;
        });
        var tracker = Tracker(handler, GitRepositoryReference.AzureDevOps("org", "proj", "app"), GitRemoteCredential.Bearer("token"));
        async Task<TrackedImage?> Read(string address) => await tracker.ReadImageAsync(new Uri(address), default);

        var image = await Read("https://dev.azure.com/org/proj-id/_apis/wit/attachments/0f1e?fileName=image.png");
        Assert.AreEqual("image/png", image?.MediaType);
        CollectionAssert.AreEqual(png, image!.Bytes);
        Assert.AreEqual("Bearer token", asked.Single().Authorization, "An attachment is read with the sign-in of the organization.");
        Assert.IsNotNull(await Read("https://dev.azure.com/ORG/proj-id/_apis/git/repositories/repo-id/pullRequests/7/attachments/image.png"));

        foreach (var elsewhere in new[]
        {
            "https://dev.azure.com/other/proj-id/_apis/wit/attachments/0f1e?fileName=image.png",
            "https://example.com/org/proj-id/_apis/wit/attachments/0f1e?fileName=image.png",
            "http://dev.azure.com/org/proj-id/_apis/wit/attachments/0f1e?fileName=image.png",
            "https://dev.azure.com:8443/org/proj-id/_apis/wit/attachments/0f1e",
            "https://dev.azure.com/org/proj-id/_apis/wit/workitems/12",
            "https://dev.azure.com/org/proj-id/_apis/git/repositories/repo-id/pullRequests/seven/attachments/image.png",
        })
        {
            Assert.IsNull(await Read(elsewhere), elsewhere);
        }

        Assert.HasCount(2, asked, "No request leaves for an address that is not an attachment of the organization.");
        answer = "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>"u8.ToArray();
        Assert.IsNull(await Read("https://dev.azure.com/org/proj-id/_apis/wit/attachments/0f1f"), "An SVG is never an image here.");
        answer = new byte[TrackedImage.MaximumBytes + 1];
        png.CopyTo(answer, 0);
        Assert.IsNull(await Read("https://dev.azure.com/org/proj-id/_apis/wit/attachments/0f20"), "An image too large is not read.");

        var github = Tracker(handler, GitRepositoryReference.GitHub("o", "r"));
        Assert.IsNull(await github.ReadImageAsync(new Uri("https://github.com/user-attachments/assets/1"), default));
        Assert.HasCount(4, asked, "GitHub reads no image yet.");
    }

    [TestMethod]
    public async Task AzureDevOps_StopsReadingAnImage_PastTheLargestOne()
    {
        // An attachment the service sends without its length: the reading stops past the largest image, it does not
        // take the whole body in memory first.
        var body = new CountingStream([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], 4L * TrackedImage.MaximumBytes);
        var handler = new StreamHandler(body);
        var tracker = Tracker(handler, GitRepositoryReference.AzureDevOps("org", "proj", "app"), GitRemoteCredential.Bearer("token"));

        Assert.IsNull(await tracker.ReadImageAsync(new Uri("https://dev.azure.com/org/proj-id/_apis/wit/attachments/0f1e?fileName=big.png"), default));
        Assert.IsLessThanOrEqualTo(TrackedImage.MaximumBytes + 1024L * 1024, body.Consumed, "At most a little more than the largest image is read.");
    }

    [TestMethod]
    public async Task AzureDevOps_ReadsAnAttachmentOfTheOrganization_WrittenWithoutItsProject()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1];
        var asked = new List<string>();
        var tracker = Tracker(new BytesHandler(request => { asked.Add(request.RequestUri!.AbsoluteUri); return png; }),
            GitRepositoryReference.AzureDevOps("org", "proj", "app"), GitRemoteCredential.Bearer("token"));

        // The service answers an attachment it was given with the address of its organization, without a project.
        Assert.AreEqual("image/png", (await tracker.ReadImageAsync(new Uri("https://dev.azure.com/org/_apis/wit/attachments/0f1e?fileName=image.png"), default))?.MediaType);
        Assert.IsNull(await tracker.ReadImageAsync(new Uri("https://dev.azure.com/other/_apis/wit/attachments/0f1e?fileName=image.png"), default));
        Assert.IsNull(await tracker.ReadImageAsync(new Uri("https://dev.azure.com/org/_apis/wit/workitems/0f1e"), default));
        Assert.HasCount(1, asked, "Only the attachment of the organization is asked.");
    }

    [TestMethod]
    public void AnImage_IsKnownByItsFirstBytes_NotByWhatTheServiceSays()
    {
        Assert.AreEqual("image/png", TrackedImage.Detect([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]));
        Assert.AreEqual("image/jpeg", TrackedImage.Detect([0xFF, 0xD8, 0xFF, 0xE0]));
        Assert.AreEqual("image/gif", TrackedImage.Detect("GIF89a.."u8));
        Assert.AreEqual("image/webp", TrackedImage.Detect("RIFF\0\0\0\0WEBPVP8 "u8));
        Assert.IsNull(TrackedImage.Detect("<svg/>"u8));
        Assert.IsNull(TrackedImage.Detect("RIFF\0\0\0\0WAVE"u8));
        Assert.IsNull(TrackedImage.Detect([]));
    }

    private sealed class BytesHandler(Func<HttpRequestMessage, byte[]> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(answer(request)), RequestMessage = request });
    }

    // Answers a body of unknown length.
    private sealed class StreamHandler(Stream body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body), RequestMessage = request });
    }

    // A body that starts with some bytes and then has zeros, that cannot seek and counts what was read of it.
    private sealed class CountingStream(byte[] start, long length) : Stream
    {
        public long Consumed { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => Consumed; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var count = (int)Math.Min(buffer.Length, length - Consumed);
            buffer[..count].Clear();
            for (var index = 0; index < count && Consumed + index < start.Length; index++) buffer[index] = start[Consumed + index];
            Consumed += count;
            return count;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromResult(Read(buffer.Span));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Task.FromResult(Read(buffer.AsSpan(offset, count)));

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // An organization where the user is in Core directly and in Fields through Core, and the open pull requests of its project.
    private static string? PullRequestAnswer(HttpRequestMessage request)
    {
        var path = request.RequestUri!.AbsolutePath;
        var query = Uri.UnescapeDataString(request.RequestUri.Query);
        const string Pulls = """
            {"value":[
              {"pullRequestId":1,"title":"Cache","status":"active","creationDate":"2026-10-03T10:00:00Z","createdBy":{"id":"ana-id","displayName":"Ana"},"targetRefName":"refs/heads/main",
               "repository":{"id":"app-id","name":"app","project":{"id":"proj-id","name":"proj"}},
               "reviewers":[{"id":"g-core","displayName":"[proj]\\Core","isContainer":true,"isRequired":true,"vote":0},{"id":"g-sec","displayName":"[proj]\\Security","isContainer":true,"isRequired":true,"vote":0}]},
              {"pullRequestId":2,"title":"Fields","status":"active","creationDate":"2026-10-04T10:00:00Z","createdBy":{"id":"ana-id","displayName":"Ana"},"targetRefName":"refs/heads/develop",
               "repository":{"id":"other-id","name":"other","project":{"id":"proj-id","name":"proj"}},
               "reviewers":[{"id":"bo-id","displayName":"Bo","vote":10,"votedFor":[{"id":"g-fields"}]},{"id":"g-fields","displayName":"[proj]\\(CodeOwners) Fields","isContainer":true,"isRequired":true,"vote":10}]},
              {"pullRequestId":3,"title":"Logs","status":"active","creationDate":"2026-10-05T10:00:00Z","createdBy":{"id":"bo-id","displayName":"Bo"},"targetRefName":"refs/heads/develop",
               "repository":{"id":"app-id","name":"app","project":{"id":"proj-id","name":"proj"}},
               "reviewers":[{"id":"me-id","displayName":"Me","vote":0},{"id":"g-sec","displayName":"[proj]\\Security","isContainer":true,"isRequired":true,"vote":0}]},
              {"pullRequestId":5,"title":"Mine","status":"active","isDraft":true,"creationDate":"2026-10-01T10:00:00Z","createdBy":{"id":"me-id","displayName":"Me"},"targetRefName":"refs/heads/release",
               "repository":{"id":"app-id","name":"app","project":{"id":"proj-id","name":"proj"}},
               "reviewers":[{"id":"g-fields","displayName":"[proj]\\(CodeOwners) Fields","isContainer":true,"isRequired":true,"vote":0}]}]}
            """;
        return (request.RequestUri.Host, path) switch
        {
            ("dev.azure.com", "/org/_apis/connectionData") => """{"authenticatedUser":{"id":"me-id","subjectDescriptor":"aad.me"}}""",
            ("vssps.dev.azure.com", "/org/_apis/identities") when query.Contains("identityIds=me-id&queryMembership=Expanded", StringComparison.Ordinal)
                => """{"value":[{"id":"me-id","memberOf":["Microsoft.TeamFoundation.Identity;S-core","Microsoft.TeamFoundation.Identity;S-fields"]}]}""",
            ("vssps.dev.azure.com", "/org/_apis/identities") when query.Contains("descriptors=Microsoft.TeamFoundation.Identity;S-core,Microsoft.TeamFoundation.Identity;S-fields", StringComparison.Ordinal)
                => """{"value":[{"id":"g-core","providerDisplayName":"[proj]\\Core"},{"id":"g-fields","providerDisplayName":"[proj]\\(CodeOwners) Fields"}]}""",
            ("dev.azure.com", "/org/proj/_apis/git/pullrequests" or "/org/_apis/git/pullrequests")
                => query.Contains("$skip=0", StringComparison.Ordinal) || !query.Contains("$skip", StringComparison.Ordinal) ? Pulls : """{"value":[]}""",
            ("dev.azure.com", "/org/proj/_apis/git/repositories/app/pullrequests") => query.Contains("$skip=0", StringComparison.Ordinal) || !query.Contains("$skip", StringComparison.Ordinal)
                ? """{"value":[""" + string.Join(',', System.Text.Json.JsonDocument.Parse(Pulls).RootElement.GetProperty("value").EnumerateArray()
                    .Where(static pull => pull.GetProperty("repository").GetProperty("name").GetString() == "app").Select(static pull => pull.GetRawText())) + "]}"
                : """{"value":[]}""",
            ("dev.azure.com", "/org/_apis/git/pullrequests/2") => """
                {"pullRequestId":2,"title":"Fields","status":"active","description":"New fields.","repository":{"id":"other-id","name":"other","project":{"id":"proj-id","name":"proj"}},"reviewers":[]}
                """,
            ("dev.azure.com", "/org/proj-id/_apis/git/repositories/other-id/pullrequests/2/threads") => """
                {"value":[{"comments":[{"commentType":"text","content":"Looks good.","author":{"displayName":"Bo"}},{"commentType":"system","content":"Bo voted 10"}]}]}
                """,
            _ => null,
        };
    }

    [TestMethod]
    public async Task AzureDevOps_OffersTheTeamsTheirSprintsAndTheTypes_YourTeamsFirst()
    {
        var tracker = Tracker(AzureDevOpsHandler(), GitRepositoryReference.AzureDevOps("org", "proj", "app"));

        var filters = await tracker.GetFiltersAsync(new(new Dictionary<string, string> { ["team"] = "t-platform" }, []), default);

        CollectionAssert.AreEqual(new[] { "team", "sprint", "assigned_to", "type", "sort" }, filters.Select(static filter => filter.Id).ToArray());
        var teams = filters[0].Values;
        CollectionAssert.AreEqual(new[] { "Platform", "Alpha", "Zeta" }, teams.Select(static team => team.Label).ToArray(), "The teams of the user first, then the others by name.");
        Assert.AreEqual("Your team", teams[0].Detail);
        var sprints = filters[1];
        CollectionAssert.AreEqual(new[] { "team" }, sprints.DependsOn.ToArray());
        CollectionAssert.AreEqual(new[] { "@current", "@next", "@previous" }, sprints.Values.Take(3).Select(static value => value.Id).ToArray());
        Assert.AreEqual("S12 · 2026-10-19 – 2026-10-30", sprints.Values[0].Detail);
        Assert.AreEqual("S13 · 2026-11-02 – 2026-11-13", sprints.Values[1].Detail);
        Assert.AreEqual("S11 · 2026-10-05 – 2026-10-16", sprints.Values[2].Detail);
        CollectionAssert.AreEqual(new[] { "S6", "S7", "S8", "S9", "S10", "S11", "S12", "S13" }, sprints.Values.Skip(3).Select(static value => value.Label).ToArray(),
            "The last six sprints that ended, then the current one and those to come.");
        CollectionAssert.AreEqual(new[] { "Bug", "User Story" }, filters[3].Values.Select(static value => value.Id).ToArray(), "Neither the disabled types nor the hidden ones.");
    }

    [TestMethod]
    public async Task AzureDevOps_WritesTheChosenFiltersInTheQuery_AsExplicitPaths()
    {
        var handler = AzureDevOpsHandler();
        var tracker = Tracker(handler, GitRepositoryReference.AzureDevOps("org", "proj", "app"));

        await tracker.ListAsync(new(TrackedItemKind.Issue, TrackedItemFilter.Open, null, 10)
        {
            Filters = new Dictionary<string, string> { ["team"] = "t-platform", ["sprint"] = "@current", ["assigned_to"] = "@me", ["type"] = "User Story" },
        }, default);

        var query = System.Text.Json.JsonDocument.Parse(handler.Bodies[^1]).RootElement.GetProperty("query").GetString()!;
        StringAssert.Contains(query, @"AND ([System.AreaPath] UNDER 'proj\Platform' OR [System.AreaPath] = 'proj\O''Brien')");
        StringAssert.Contains(query, @"AND [System.IterationPath] = 'proj\Platform\S12'");
        StringAssert.Contains(query, "AND [System.AssignedTo] = @Me");
        StringAssert.Contains(query, "AND [System.WorkItemType] IN ('User Story')");
        Assert.IsTrue(handler.Requests.Any(static request => request.StartsWith("/org/proj/t-platform/_apis/work/teamsettings/iterations", StringComparison.Ordinal)), "The sprints are those of the team.");

        var before = handler.Bodies.Count;
        var none = await tracker.ListAsync(new(TrackedItemKind.Issue, TrackedItemFilter.Open, null, 10)
        {
            Filters = new Dictionary<string, string> { ["team"] = "t-empty", ["sprint"] = "@current" },
        }, default);
        Assert.AreEqual(0, none.Items.Count);
        Assert.AreEqual(before, handler.Bodies.Count, "A team without a current sprint lists nothing, rather than every sprint.");
    }

    [TestMethod]
    public async Task AzureDevOps_OffersItsFieldsAsFilters_WithTheValuesTheProcessAllows()
    {
        var handler = AzureDevOpsHandler();
        var tracker = Tracker(handler, GitRepositoryReference.AzureDevOps("org", "proj", "app"));

        var fields = await tracker.GetFieldsAsync(default);

        CollectionAssert.AreEqual(new[] { "Area Path:path", "Created By:person", "Market:text", "Priority:number", "Size:number", "Stack Rank:number", "State:text", "Tags:tags" },
            fields.Select(static field => field.Label + ":" + field.Type).ToArray(), "By name, without the tracker's own filters, the dates and the fields of links.");
        Assert.AreEqual(("field:Microsoft.VSTS.Common.Priority", "Microsoft.VSTS.Common.Priority"), (fields[3].Id, fields[3].Name));

        string[] asked = ["field:Microsoft.VSTS.Common.Priority", "field:Custom.Market", "field:System.Tags", "field:System.CreatedBy", "field:System.AreaPath", "field:Custom.Gone"];
        var filters = (await tracker.GetFiltersAsync(new(new Dictionary<string, string>(), asked), default)).Where(static filter => filter.Id.StartsWith("field:", StringComparison.Ordinal)).ToArray();

        CollectionAssert.AreEqual(asked[..5], filters.Select(static filter => filter.Id).ToArray(), "In the order asked; a field the project does not have is left out.");
        Assert.AreEqual((TrackedFilterKind.Choice, "number", "1,2,3"), (filters[0].Kind, filters[0].FieldType, string.Join(',', filters[0].Values.Select(static value => value.Id))),
            "The values the process allows in each type, together, as numbers.");
        Assert.AreEqual((TrackedFilterKind.Text, "Market", "text"), (filters[1].Kind, filters[1].Label, filters[1].FieldType), "A field that allows any text takes a text.");
        CollectionAssert.AreEqual(new[] { "perf", "ui" }, filters[2].Values.Select(static value => value.Id).ToArray());
        CollectionAssert.AreEqual(new[] { "@me", "@none" }, filters[3].Values.Select(static value => value.Id).ToArray());
        CollectionAssert.AreEqual(new[] { "proj", @"proj\Platform", @"proj\Platform\Web" }, filters[4].Values.Select(static value => value.Id).ToArray());

        await tracker.ListAsync(new(TrackedItemKind.Issue, TrackedItemFilter.All, null, 10)
        {
            Filters = new Dictionary<string, string>
            {
                ["field:Microsoft.VSTS.Common.Priority"] = "2", ["field:Custom.Market"] = "Contoso's", ["field:System.Tags"] = "ui", ["field:System.CreatedBy"] = "@me", ["field:System.AreaPath"] = @"proj\Platform",
            },
        }, default);
        var query = System.Text.Json.JsonDocument.Parse(handler.Bodies[^1]).RootElement.GetProperty("query").GetString()!;
        StringAssert.Contains(query, "AND [Microsoft.VSTS.Common.Priority] IN (2)");
        StringAssert.Contains(query, "AND [Custom.Market] CONTAINS 'Contoso''s'");
        StringAssert.Contains(query, "AND ([System.Tags] CONTAINS 'ui')");
        StringAssert.Contains(query, "AND [System.CreatedBy] = @Me");
        StringAssert.Contains(query, @"AND ([System.AreaPath] UNDER 'proj\Platform')");

        var before = handler.Bodies.Count;
        Assert.AreEqual(0, (await tracker.ListAsync(new(TrackedItemKind.Issue, TrackedItemFilter.All, null, 10)
        {
            Filters = new Dictionary<string, string> { ["field:Microsoft.VSTS.Common.Priority"] = "high" },
        }, default)).Items.Count);
        Assert.AreEqual(before, handler.Bodies.Count, "A number that is none lists nothing, rather than refusing the query.");
    }

    [TestMethod]
    public async Task AzureDevOps_SaysWhyTheTagsOrThePathsOfAProjectAreNotOffered()
    {
        var handler = new Handler(request => request.RequestUri!.AbsolutePath is "/org/proj/_apis/wit/tags" or "/org/proj/_apis/wit/classificationnodes/areas" ? null : AzureDevOpsAnswer(request));
        var tracker = Tracker(handler, GitRepositoryReference.AzureDevOps("org", "proj", "app"));

        var filters = await tracker.GetFiltersAsync(new(new Dictionary<string, string>(), ["field:System.Tags", "field:System.AreaPath"]), default);

        Assert.AreEqual(("The tags of the project could not be read.", 0), (filters.Single(static filter => filter.Id == "field:System.Tags").Problem, filters.Single(static filter => filter.Id == "field:System.Tags").Values.Count));
        Assert.AreEqual("The areas of the project could not be read.", filters.Single(static filter => filter.Id == "field:System.AreaPath").Problem);
    }

    [TestMethod]
    public async Task AzureDevOps_TakesSeveralValuesOfAFilter_AndListsInTheOrderOfTheBacklog()
    {
        var handler = AzureDevOpsHandler();
        var tracker = Tracker(handler, GitRepositoryReference.AzureDevOps("org", "proj", "app"));

        var filters = await tracker.GetFiltersAsync(new(new Dictionary<string, string>(), ["field:Microsoft.VSTS.Common.Priority", "field:System.CreatedBy"]), default);
        Assert.IsTrue(filters.Single(static filter => filter.Id == "type").Multiple);
        Assert.IsTrue(filters.Single(static filter => filter.Id == "field:Microsoft.VSTS.Common.Priority").Multiple);
        Assert.IsFalse(filters.Single(static filter => filter.Id == "field:System.CreatedBy").Multiple, "A person is the user or nobody.");
        var sort = filters.Single(static filter => filter.Id == TrackedFilters.Sort);
        Assert.AreEqual(("Last changed first", "backlog,priority,created"), (sort.AllLabel, string.Join(',', sort.Values.Select(static value => value.Id))));

        await tracker.ListAsync(new(TrackedItemKind.Issue, TrackedItemFilter.Open, null, 10)
        {
            Filters = new Dictionary<string, string>
            {
                ["type"] = TrackedFilters.Join(["Bug", "User Story"]), ["field:Microsoft.VSTS.Common.Priority"] = TrackedFilters.Join(["1", "2"]),
                ["field:System.Tags"] = TrackedFilters.Join(["ui", "perf"]), [TrackedFilters.Sort] = "backlog",
            },
        }, default);

        var query = System.Text.Json.JsonDocument.Parse(handler.Bodies[^1]).RootElement.GetProperty("query").GetString()!;
        StringAssert.Contains(query, "AND [System.WorkItemType] IN ('Bug', 'User Story')");
        StringAssert.Contains(query, "AND [Microsoft.VSTS.Common.Priority] IN (1, 2)");
        StringAssert.Contains(query, "AND ([System.Tags] CONTAINS 'ui' OR [System.Tags] CONTAINS 'perf')");
        StringAssert.EndsWith(query, "ORDER BY [Microsoft.VSTS.Common.StackRank] ASC, [System.ChangedDate] DESC", "The order the boards keep when items are dragged.");
    }

    [TestMethod]
    public async Task AzureDevOps_ReadsTheFieldsOfEachTypeOnce_ForAllTheFieldsAndListings()
    {
        var handler = AzureDevOpsHandler();
        var tracker = Tracker(handler, GitRepositoryReference.AzureDevOps("org", "proj", "app"));
        string[] asked = ["field:Microsoft.VSTS.Common.Priority", "field:System.State", "field:Custom.Size", "field:Custom.Market"];

        var filters = await tracker.GetFiltersAsync(new(new Dictionary<string, string>(), asked), default);
        await tracker.GetFiltersAsync(new(new Dictionary<string, string>(), asked), default);
        await tracker.ListAsync(new(TrackedItemKind.Issue, TrackedItemFilter.All, null, 10)
        {
            Filters = new Dictionary<string, string> { ["field:System.State"] = TrackedFilters.Join(["New", "Ready"]), ["field:Custom.Size"] = "8" },
        }, default);

        Assert.AreEqual(2, handler.Requests.Count(static request => request.Contains("/_apis/wit/workitemtypes/", StringComparison.Ordinal) && request.Contains("/fields?", StringComparison.Ordinal)),
            "One request for each type, whatever the fields and the listings.");
        var state = filters.Single(static filter => filter.Id == "field:System.State");
        Assert.AreEqual((TrackedFilterKind.Choice, "list", "New,Active,Closed,Ready"), (state.Kind, state.FieldType, string.Join(',', state.Values.Select(static value => value.Id))));
        var size = filters.Single(static filter => filter.Id == "field:Custom.Size");
        Assert.AreEqual((TrackedFilterKind.Choice, "number", "3,8"), (size.Kind, size.FieldType, string.Join(',', size.Values.Select(static value => value.Id))),
            "A picklist of numbers stays a number.");
        var query = System.Text.Json.JsonDocument.Parse(handler.Bodies[^1]).RootElement.GetProperty("query").GetString()!;
        StringAssert.Contains(query, "AND [System.State] IN ('New', 'Ready')");
        StringAssert.Contains(query, "AND [Custom.Size] IN (8)", "A number is not quoted.");
    }

    [TestMethod]
    public async Task AzureDevOps_ARefusalSaysNothingOfTheFieldsOfAType_AndIsAskedAgain()
    {
        var handler = AzureDevOpsHandler();
        handler.Refuse = static request => request.RequestUri!.AbsolutePath.StartsWith("/org/proj/_apis/wit/workitemtypes/", StringComparison.Ordinal) ? HttpStatusCode.TooManyRequests : null;
        var tracker = Tracker(handler, GitRepositoryReference.AzureDevOps("org", "proj", "app"));

        var state = (await tracker.GetFiltersAsync(new(new Dictionary<string, string>(), ["field:System.State"]), default)).Single(static filter => filter.Id == "field:System.State");

        Assert.AreEqual((TrackedFilterKind.Choice, 0, "The values of State could not be read."), (state.Kind, state.Values.Count, state.Problem), "A refusal is not a field that takes any text.");
        var before = handler.Bodies.Count;
        await tracker.ListAsync(new(TrackedItemKind.Issue, TrackedItemFilter.All, null, 10) { Filters = new Dictionary<string, string> { ["field:System.State"] = "New" } }, default);
        Assert.AreEqual(before, handler.Bodies.Count, "A value that cannot be applied lists nothing, rather than searching for a word.");

        // A type that is not found has no field; the refused ones were not kept.
        handler.Refuse = static request => request.RequestUri!.AbsolutePath.EndsWith("/User%20Story/fields", StringComparison.Ordinal) ? HttpStatusCode.NotFound : null;
        state = (await tracker.GetFiltersAsync(new(new Dictionary<string, string>(), ["field:System.State"]), default)).Single(static filter => filter.Id == "field:System.State");
        Assert.AreEqual((null, "New,Active,Closed"), (state.Problem, string.Join(',', state.Values.Select(static value => value.Id))));
    }

    [TestMethod]
    public async Task AzureDevOps_AnEmptyValueListsEveryItem_AndALargeNumberIsWrittenWithoutExponent()
    {
        var handler = AzureDevOpsHandler();
        var tracker = Tracker(handler, GitRepositoryReference.AzureDevOps("org", "proj", "app"));

        await tracker.ListAsync(new(TrackedItemKind.Issue, TrackedItemFilter.All, null, 10)
        {
            Filters = new Dictionary<string, string> { ["assigned_to"] = "", ["field:Microsoft.VSTS.Common.StackRank"] = "1e16" },
        }, default);

        var query = System.Text.Json.JsonDocument.Parse(handler.Bodies.Single()).RootElement.GetProperty("query").GetString()!;
        Assert.IsFalse(query.Contains("[System.AssignedTo]", StringComparison.Ordinal), "No value is every item.");
        StringAssert.Contains(query, "AND [Microsoft.VSTS.Common.StackRank] IN (10000000000000000)");
    }

    [TestMethod]
    public async Task AzureDevOps_ANumberKeepsItsValue_HoweverLargeOrSmall()
    {
        var handler = AzureDevOpsHandler();
        var tracker = Tracker(handler, GitRepositoryReference.AzureDevOps("org", "proj", "app"));

        await tracker.ListAsync(new(TrackedItemKind.Issue, TrackedItemFilter.All, null, 10)
        {
            Filters = new Dictionary<string, string> { ["field:Microsoft.VSTS.Common.StackRank"] = TrackedFilters.Join(["1e30", "1e-29", "0.1", "-2.5"]) },
        }, default);

        var query = System.Text.Json.JsonDocument.Parse(handler.Bodies.Single()).RootElement.GetProperty("query").GetString()!;
        StringAssert.Contains(query, "AND [Microsoft.VSTS.Common.StackRank] IN (1" + new string('0', 30) + ", 0." + new string('0', 28) + "1, 0.1, -2.5)");
        Assert.AreEqual("17976931348623157" + new string('0', 292), AzureDevOpsApi.NumberLiteral("1.7976931348623157e308"), "The largest double, every digit written.");
    }

    [TestMethod]
    [DataRow("1e16", "10000000000000000")]
    [DataRow("1e30", "1000000000000000000000000000000")]
    [DataRow("1e-29", "0.00000000000000000000000000001")]
    [DataRow("1.5e-7", "0.00000015")]
    [DataRow("0.1", "0.1")]
    [DataRow("-1.25E+20", "-125000000000000000000")]
    [DataRow("-7", "-7")]
    [DataRow("1e999", null)]
    [DataRow("NaN", null)]
    [DataRow("high", null)]
    public void AzureDevOps_WritesANumberWithoutExponent_AsTheDoubleItReads(string text, string? literal)
    {
        Assert.AreEqual(literal, AzureDevOpsApi.NumberLiteral(text));
        if (literal is not null) Assert.AreEqual(double.Parse(text, System.Globalization.CultureInfo.InvariantCulture), double.Parse(literal, System.Globalization.CultureInfo.InvariantCulture));
    }

    [TestMethod]
    public async Task AzureDevOps_ValuesOfAFieldThatNotEveryTypeAnswered_SayItAndKeepTheValuesOfTheView()
    {
        var handler = AzureDevOpsHandler();
        handler.Refuse = static request => request.RequestUri!.AbsolutePath.EndsWith("/User%20Story/fields", StringComparison.Ordinal) ? HttpStatusCode.TooManyRequests : null;
        var tracker = Tracker(handler, GitRepositoryReference.AzureDevOps("org", "proj", "app"));
        var settings = new IssueFilterSettings([new("field:System.State", Value: TrackedFilters.Join(["New", "Ready"]))]);

        var selection = await IssueFilterChoice.ChooseAsync(tracker, TrackedFilters.None, settings, [], default);

        var state = selection.Filters.Single(static filter => filter.Id == "field:System.State");
        Assert.AreEqual(("Not all the values of State could be read.", "New,Active,Closed"), (state.Problem, string.Join(',', state.Values.Select(static value => value.Id))));
        Assert.AreEqual(TrackedFilters.Join(["New", "Ready"]), selection.Selected["field:System.State"], "Ready, of the type that did not answer, is kept.");
        await tracker.ListAsync(new(TrackedItemKind.Issue, TrackedItemFilter.All, null, 10) { Filters = selection.Applied }, default);
        var query = System.Text.Json.JsonDocument.Parse(handler.Bodies[^1]).RootElement.GetProperty("query").GetString()!;
        StringAssert.Contains(query, "AND [System.State] IN ('New', 'Ready')", "A value the list does not offer narrows the listing.");
    }

    [TestMethod]
    public async Task AzureDevOps_ValuesPastTheMostOffered_SayIt_AndAreKeptForTheView()
    {
        var states = Enumerable.Range(1, 301).Select(static number => "S" + number.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        var handler = new Handler(request => request.RequestUri!.AbsolutePath == "/org/proj/_apis/wit/workitemtypes/Bug/fields"
            ? "{\"value\":[{\"referenceName\":\"System.State\",\"allowedValues\":[" + string.Join(',', states.Select(static state => "\"" + state + "\"")) + "]}]}"
            : AzureDevOpsAnswer(request));
        var tracker = Tracker(handler, GitRepositoryReference.AzureDevOps("org", "proj", "app"));
        var settings = new IssueFilterSettings([new("field:System.State", Value: TrackedFilters.Join(["S1", "S301"]))]);

        var selection = await IssueFilterChoice.ChooseAsync(tracker, TrackedFilters.None, settings, [], default);

        var state = selection.Filters.Single(static filter => filter.Id == "field:System.State");
        Assert.AreEqual(("Only the first 300 values of State are offered.", 300), (state.Problem, state.Values.Count));
        Assert.AreEqual(TrackedFilters.Join(["S1", "S301"]), selection.Selected["field:System.State"], "A value past those offered is kept.");
    }

    [TestMethod]
    public async Task AzureDevOps_KeepsTheValuesOfItsFilters_ForTheCredentialsTheClientCarries()
    {
        var handler = AzureDevOpsHandler();
        using var client = GitIssueLookup.CreateHttpClient(handler);
        var tracker = new GitIssueLookup(client, new GitIssueLookupOptions { CacheDuration = TimeSpan.Zero }).GetTracker(GitRepositoryReference.AzureDevOps("org", "proj", "app"));
        int TeamReads() => handler.Requests.Count(static request => request.StartsWith("/org/_apis/projects/proj/teams?", StringComparison.Ordinal) && !request.Contains("mine", StringComparison.Ordinal));

        client.DefaultRequestHeaders.Authorization = new("Bearer", "first");
        await tracker.GetFiltersAsync(new(new Dictionary<string, string>(), []), default);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "second");
        await tracker.GetFiltersAsync(new(new Dictionary<string, string>(), []), default);
        Assert.AreEqual(2, TeamReads(), "Another user of the client is not given the teams the first one may read.");
        client.DefaultRequestHeaders.Authorization = null;
        await tracker.GetFiltersAsync(new(new Dictionary<string, string>(), []), default);
        Assert.AreEqual(3, TeamReads(), "Nor is a visitor.");
        client.DefaultRequestHeaders.Authorization = new("Bearer", "first");
        await tracker.GetFiltersAsync(new(new Dictionary<string, string>(), []), default);
        Assert.AreEqual(3, TeamReads(), "The answers of each user are kept.");
    }

    [TestMethod]
    public async Task AzureDevOps_KeepsTheValuesOfItsFilters_ForTheCredentialsTheyWereReadWith()
    {
        var handler = AzureDevOpsHandler();
        var credential = GitRemoteCredential.Bearer("first");
        var lookup = new GitIssueLookup(GitIssueLookup.CreateHttpClient(handler), new GitIssueLookupOptions
        {
            CacheDuration = TimeSpan.Zero, CredentialProvider = (_, _) => ValueTask.FromResult<GitRemoteCredential?>(credential),
        });
        var tracker = lookup.GetTracker(GitRepositoryReference.AzureDevOps("org", "proj", "app"));
        int TeamReads() => handler.Requests.Count(static request => request.StartsWith("/org/_apis/projects/proj/teams?", StringComparison.Ordinal) && !request.Contains("mine", StringComparison.Ordinal));

        await tracker.GetFiltersAsync(new(new Dictionary<string, string>(), []), default);
        credential = GitRemoteCredential.Bearer("second");
        await tracker.GetFiltersAsync(new(new Dictionary<string, string>(), []), default);
        Assert.AreEqual(2, TeamReads(), "Another user is not given the teams the first one may read.");
        credential = GitRemoteCredential.Bearer("first");
        await tracker.GetFiltersAsync(new(new Dictionary<string, string>(), []), default);
        Assert.AreEqual(2, TeamReads(), "The answers of each user are kept.");
    }

    [TestMethod]
    public async Task AzureDevOps_AValueOfAViewThatCannotBeRead_StillNarrowsTheListing()
    {
        // The teams cannot be read; the team of the view is named in the addresses of the service as it is written.
        var handler = new Handler(static request => AzureDevOpsAnswer(request.RequestUri!.AbsolutePath.Replace("/proj/Platform/", "/proj/t-platform/", StringComparison.Ordinal), request.RequestUri.Query))
        {
            Refuse = static request => request.RequestUri!.AbsolutePath == "/org/_apis/projects/proj/teams" ? HttpStatusCode.ServiceUnavailable : null,
        };
        var tracker = Tracker(handler, GitRepositoryReference.AzureDevOps("org", "proj", "app"));
        var settings = new IssueFilterSettings([new("team", Value: "Platform"), new("sprint", Value: "@current")]);

        var selection = await IssueFilterChoice.ChooseAsync(tracker, TrackedFilters.None, settings, [], default);

        Assert.AreEqual(("Platform", "@current"), (selection.Selected["team"], selection.Selected["sprint"]), "The team of the view is kept as it is written.");
        Assert.AreEqual("The teams of the project could not be read.", selection.Filters.Single(static filter => filter.Id == "team").Problem);
        await tracker.ListAsync(new(TrackedItemKind.Issue, TrackedItemFilter.Open, null, 10) { Filters = selection.Applied }, default);
        var query = System.Text.Json.JsonDocument.Parse(handler.Bodies[^1]).RootElement.GetProperty("query").GetString()!;
        StringAssert.Contains(query, @"[System.AreaPath] UNDER 'proj\Platform'");
        StringAssert.Contains(query, @"AND [System.IterationPath] = 'proj\Platform\S12'", "The current sprint of that team, not of the default team.");

        // A team the service does not know lists nothing, rather than the whole project.
        selection = await IssueFilterChoice.ChooseAsync(tracker, TrackedFilters.None, new([new("team", Value: "Web")]), [], default);
        Assert.AreEqual("Web", selection.Selected["team"]);
        var before = handler.Bodies.Count;
        Assert.AreEqual(0, (await tracker.ListAsync(new(TrackedItemKind.Issue, TrackedItemFilter.Open, null, 10) { Filters = selection.Applied }, default)).Items.Count);
        Assert.AreEqual(before, handler.Bodies.Count);
    }

    [TestMethod]
    public void AzureDevOps_QuotesTheTextsOfAQuery()
        => Assert.AreEqual("'It''s ''done'''", AzureDevOpsApi.Quote("It's 'done'"));

    // A project with three teams (Platform is the user's), the areas and the sprints of Platform, and its work item types.
    private static Handler AzureDevOpsHandler() => new(AzureDevOpsAnswer);

    private static string? AzureDevOpsAnswer(HttpRequestMessage request) => AzureDevOpsAnswer(request.RequestUri!.AbsolutePath, request.RequestUri.Query);

    private static string? AzureDevOpsAnswer(string path, string query)
        => (path, query.Contains("$mine=true", StringComparison.Ordinal)) switch
        {
            ("/org/_apis/projects/proj/teams", true) => """{"value":[{"id":"t-platform","name":"Platform"}]}""",
            ("/org/_apis/projects/proj/teams", false) => """{"value":[{"id":"t-zeta","name":"Zeta"},{"id":"t-platform","name":"Platform"},{"id":"t-alpha","name":"Alpha"}]}""",
            ("/org/proj/t-platform/_apis/work/teamsettings/teamfieldvalues", _) => """
                {"field":{"referenceName":"System.AreaPath"},"values":[{"value":"proj\\Platform","includeChildren":true},{"value":"proj\\O'Brien","includeChildren":false}]}
                """,
            ("/org/proj/t-empty/_apis/work/teamsettings/teamfieldvalues", _) => """{"field":{"referenceName":"System.AreaPath"},"values":[{"value":"proj\\Empty","includeChildren":true}]}""",
            ("/org/proj/t-empty/_apis/work/teamsettings/iterations", _) => """{"value":[]}""",
            ("/org/proj/t-platform/_apis/work/teamsettings/iterations", _) => "{\"value\":[" + string.Join(',', Enumerable.Range(1, 13).Select(static number =>
            {
                var start = new DateTime(2026, 5, 18).AddDays(14 * (number - 1));
                var frame = number < 12 ? "past" : number == 12 ? "current" : "future";
                var dates = FormattableString.Invariant($"\"startDate\":\"{start:yyyy-MM-dd}T00:00:00Z\",\"finishDate\":\"{start.AddDays(11):yyyy-MM-dd}T00:00:00Z\"");
                return "{\"id\":\"s" + number + "\",\"name\":\"S" + number + "\",\"path\":\"proj\\\\Platform\\\\S" + number + "\",\"attributes\":{" + dates + ",\"timeFrame\":\"" + frame + "\"}}";
            })) + "]}",
            ("/org/proj/_apis/wit/workitemtypes", _) => """{"value":[{"name":"User Story"},{"name":"Bug"},{"name":"Old","isDisabled":true},{"name":"Code Review Request"}]}""",
            ("/org/proj/_apis/wit/workitemtypecategories", _) => """{"value":[{"referenceName":"Microsoft.HiddenCategory","workItemTypes":[{"name":"Code Review Request"}]}]}""",
            ("/org/proj/_apis/wit/wiql", _) => """{"workItems":[]}""",
            ("/org/proj/_apis/wit/fields", _) => """
                {"value":[
                  {"name":"Priority","referenceName":"Microsoft.VSTS.Common.Priority","type":"integer","usage":"workItem"},
                  {"name":"Market","referenceName":"Custom.Market","type":"string","usage":"workItem"},
                  {"name":"Tags","referenceName":"System.Tags","type":"plainText","usage":"workItem"},
                  {"name":"Created By","referenceName":"System.CreatedBy","type":"string","isIdentity":true,"usage":"workItem"},
                  {"name":"Area Path","referenceName":"System.AreaPath","type":"treePath","usage":"workItem"},
                  {"name":"Stack Rank","referenceName":"Microsoft.VSTS.Common.StackRank","type":"double","usage":"workItem"},
                  {"name":"State","referenceName":"System.State","type":"string","usage":"workItem"},
                  {"name":"Size","referenceName":"Custom.Size","type":"integer","isPicklist":true,"usage":"workItem"},
                  {"name":"Title","referenceName":"System.Title","type":"string","usage":"workItem"},
                  {"name":"Changed Date","referenceName":"System.ChangedDate","type":"dateTime","usage":"workItem"},
                  {"name":"Link Comment","referenceName":"System.Links.Comment","type":"string","usage":"workItemLink"}]}
                """,
            ("/org/proj/_apis/wit/workitemtypes/Bug/fields", _) => """
                {"value":[{"referenceName":"Microsoft.VSTS.Common.Priority","allowedValues":[2,1]},{"referenceName":"Custom.Market","allowedValues":[]},
                  {"referenceName":"System.State","allowedValues":["New","Active","Closed"]},{"referenceName":"Custom.Size","allowedValues":[8,3]}]}
                """,
            ("/org/proj/_apis/wit/workitemtypes/User%20Story/fields", _) => """
                {"value":[{"referenceName":"Microsoft.VSTS.Common.Priority","allowedValues":[2,3]},{"referenceName":"System.State","allowedValues":["New","Ready"]}]}
                """,
            ("/org/proj/_apis/wit/tags", _) => """{"value":[{"name":"ui"},{"name":"perf"}]}""",
            ("/org/proj/_apis/wit/classificationnodes/areas", _) => """{"name":"proj","children":[{"name":"Platform","children":[{"name":"Web"}]}]}""",
            _ => null,
        };

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

    private static IIssueTracker Tracker(HttpMessageHandler handler, GitRepositoryReference repository, GitRemoteCredential? credential = null)
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

        /// <summary>Gets or sets the refusal a request is answered with before its JSON is looked for; null for none.</summary>
        public Func<HttpRequestMessage, HttpStatusCode?> Refuse { get; set; } = static _ => null;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests) Requests.Add(request.RequestUri!.PathAndQuery);
            if (request.Content is not null) Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            if (Refuse(request) is { } refused) return new HttpResponseMessage(refused);
            return answer(request) is { } json
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(missing);
        }
    }
}
