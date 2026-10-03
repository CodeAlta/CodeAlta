using System.Net;
using System.Text.Json;
using CodeAlta.Catalog;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Plugin.GitHub;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// The issue lookup behind the WebApp <c>#</c> picker. GitHub is a literal handler and the git remotes
/// are a literal list: no test reaches the network or runs git.
/// </summary>
[TestClass]
public sealed class GitHubIssuesRpcTests
{
    private const string Epoch = "epoch-1";
    private const string RecentPath = "/repos/org/repo/issues?state=all&sort=updated&direction=desc&per_page=100&page=";

    [TestMethod]
    public async Task Search_RefusesMalformedRequestsAndAnotherEpoch_BeforeReadingAnything()
    {
        using var fixture = await Fixture.CreateAsync();
        var id = fixture.Project.Id;
        foreach (var request in new GitHubIssuesSearchRequest?[]
                 {
                     null,
                     new(null!, id, "", null),
                     new("", id, "", null),
                     new(" " + Epoch, id, "", null),
                     new(Epoch, id, null!, null),
                     new(Epoch, id, new string('a', GithubIssuesService.MaximumQueryLength + 1), null),
                     new(Epoch, id, "two\nlines", null),
                     new(Epoch, id, "tab\tseparated", null),
                     new(Epoch, id, "lone \ud800 surrogate", null),
                 })
        {
            var refused = await fixture.Service.SearchAsync(request!, default);
            Assert.AreEqual("invalid_request", refused.Status);
            Assert.AreEqual(Epoch, refused.Epoch);
            Assert.IsNull(refused.Repository);
            Assert.IsEmpty(refused.Issues);
            Assert.IsNull(refused.Message);
        }

        Assert.AreEqual("stale_epoch", (await fixture.Service.SearchAsync(new("another", id, "", null), default)).Status);
        Assert.AreEqual("ok", (await fixture.Service.SearchAsync(new(Epoch, id, new string('a', GithubIssuesService.MaximumQueryLength), null), default)).Status);
        Assert.AreEqual(1, fixture.RemoteReads, "Only the accepted request may inspect the project.");
    }

    [TestMethod]
    public async Task Search_ResolvesTheFolderOnlyFromTheCatalog_AndRefusesUnknownArchivedOrGoneProjects()
    {
        using var fixture = await Fixture.CreateAsync();
        Assert.AreEqual("unknown_project", (await fixture.SearchAsync("", Guid.NewGuid().ToString("D"))).Status);
        Assert.AreEqual("unknown_project", (await fixture.SearchAsync("", fixture.Project.ProjectPath)).Status, "A path is not a project id.");
        Assert.AreEqual("unknown_project", (await fixture.SearchAsync("", "")).Status);

        var gone = await fixture.AddProjectAsync("gone", git: true);
        Directory.Delete(gone.ProjectPath, recursive: true);
        Assert.AreEqual("project_unavailable", (await fixture.SearchAsync("", gone.Id)).Status);

        fixture.Project.Archived = true;
        await fixture.Projects.SaveAsync(fixture.Project);
        var archived = await fixture.SearchAsync("", fixture.Project.Id);
        Assert.AreEqual("archived_project", archived.Status);
        Assert.IsNull(archived.Message);

        Assert.AreEqual(0, fixture.RemoteReads);
        Assert.IsEmpty(fixture.GitHub.Requests);
    }

    [TestMethod]
    public async Task Search_AnswersNotGitHubWithoutANetworkRequest()
    {
        using var fixture = await Fixture.CreateAsync();
        Assert.AreEqual("not_github", (await fixture.SearchAsync("", projectId: null)).Status);
        Assert.AreEqual(0, fixture.RemoteReads, "Without a project there is no folder to inspect.");

        fixture.Remotes = [];
        var plain = await fixture.AddProjectAsync("plain", git: false);
        Assert.AreEqual("not_github", (await fixture.SearchAsync("", plain.Id)).Status);

        foreach (var remote in new[]
                 {
                     "https://gitlab.com/org/repo.git", "git@bitbucket.org:org/repo.git", "https://github.com.evil.example/org/repo",
                     "https://github.com/org", "https://github.com/or%20g/repo", "not a url",
                 })
        {
            fixture.Remotes = [remote];
            var project = await fixture.AddProjectAsync("other-" + Guid.NewGuid().ToString("N"), git: true);
            var refused = await fixture.SearchAsync("crash", project.Id);
            Assert.AreEqual("not_github", refused.Status, remote);
            Assert.IsNull(refused.Repository);
            Assert.IsNull(refused.Message);
        }

        Assert.IsEmpty(fixture.GitHub.Requests);
        Assert.AreEqual(0, fixture.TokenReads, "No credential is resolved for a folder that is not a GitHub repository.");
    }

    [TestMethod]
    public async Task Search_RecognizesEveryGitHubRemoteForm_AndTakesTheFirstGitHubRemote()
    {
        using var fixture = await Fixture.CreateAsync();
        foreach (var remotes in new string[][]
                 {
                     ["https://github.com/org/repo.git"], ["https://github.com/org/repo"], ["git@github.com:org/repo.git"],
                     ["ssh://git@github.com/org/repo.git"], ["https://gitlab.com/org/mirror.git", "git@github.com:org/repo.git", "https://github.com/fork/repo.git"],
                 })
        {
            fixture.Remotes = remotes;
            var project = await fixture.AddProjectAsync("form-" + Guid.NewGuid().ToString("N"), git: true);
            var found = await fixture.SearchAsync("", project.Id);
            Assert.AreEqual("ok", found.Status, remotes[0]);
            Assert.AreEqual("org/repo", found.Repository, remotes[0]);
        }
    }

    [TestMethod]
    public async Task EmptyQuery_ListsRecentIssuesNewestFirst_WithoutPullRequests()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.GitHub.Respond = _ => Json(Array(
            Issue(45, "Older issue", day: 3, state: "closed"),
            Issue(77, "A pull request", day: 9, pullRequest: true),
            Issue(123, "Recent issue", day: 5)));

        var found = await fixture.SearchAsync("");

        Assert.AreEqual("ok", found.Status);
        Assert.AreEqual(Epoch, found.Epoch);
        Assert.AreEqual("org/repo", found.Repository);
        Assert.IsNull(found.Message);
        CollectionAssert.AreEqual(new[] { 123, 45 }, found.Issues.Select(issue => issue.Number).ToArray());
        var recent = found.Issues[0];
        Assert.AreEqual("Recent issue", recent.Title);
        Assert.AreEqual("https://github.com/org/repo/issues/123", recent.Url);
        Assert.AreEqual("open", recent.State);
        Assert.AreEqual(new DateTimeOffset(2026, 5, 5, 10, 0, 0, TimeSpan.Zero), recent.UpdatedAt);
        Assert.AreEqual("closed", found.Issues[1].State);

        CollectionAssert.AreEqual(new[] { RecentPath + "1" }, fixture.GitHub.Requests);
        var sent = fixture.GitHub.Headers.Single();
        Assert.AreEqual("application/vnd.github+json", sent.Accept);
        Assert.AreEqual("CodeAlta-GitHub-Plugin", sent.UserAgent);
        Assert.AreEqual("Bearer " + Fixture.Token, sent.Authorization);
        var wire = JsonSerializer.Serialize(found, DesktopJsonContext.Default.GitHubIssuesSearchResponse);
        Assert.IsFalse(wire.Contains(Fixture.Token, StringComparison.Ordinal));
        Assert.IsFalse(wire.Contains(fixture.Root, StringComparison.OrdinalIgnoreCase));
        StringAssert.Contains(wire, "\"issues\":[{\"number\":123,");
    }

    [TestMethod]
    public async Task EmptyQuery_PagesPastPullRequests_ForAtMostFivePages()
    {
        using var fixture = await Fixture.CreateAsync();
        var pullRequests = Array(Enumerable.Range(1, 100).Select(number => Issue(number, "PR " + number, day: 9, pullRequest: true)).ToArray());
        fixture.GitHub.Respond = request => Json(request.EndsWith("&page=3", StringComparison.Ordinal) ? Array(Issue(456, "Paged issue", day: 2)) : pullRequests);

        var found = await fixture.SearchAsync("");
        CollectionAssert.AreEqual(new[] { 456 }, found.Issues.Select(issue => issue.Number).ToArray());
        CollectionAssert.AreEqual(new[] { RecentPath + "1", RecentPath + "2", RecentPath + "3" }, fixture.GitHub.Requests);

        fixture.GitHub.Requests.Clear();
        fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        fixture.GitHub.Respond = _ => Json(pullRequests);
        var none = await fixture.SearchAsync("");
        Assert.AreEqual("ok", none.Status);
        Assert.IsEmpty(none.Issues);
        Assert.HasCount(5, fixture.GitHub.Requests);
    }

    [TestMethod]
    public async Task ShortOrNumericQuery_FetchesTheExactIssueAndFiltersTheRecentList()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.GitHub.Respond = request => request == "/repos/org/repo/issues/12"
            ? Json(Issue(12, "Exact issue", day: 1))
            : Json(Array(
                Issue(120, "Number prefix", day: 8),
                Issue(12, "Exact issue", day: 1),
                Issue(7, "Mentions 12 in the title", day: 6),
                Issue(5, "Unrelated", day: 7),
                Issue(121, "A pull request", day: 9, pullRequest: true)));

        var numeric = await fixture.SearchAsync("12");
        Assert.AreEqual("ok", numeric.Status);
        CollectionAssert.AreEqual(new[] { 120, 7, 12 }, numeric.Issues.Select(issue => issue.Number).ToArray(), "Merged without duplicates, newest first.");
        CollectionAssert.AreEqual(new[] { "/repos/org/repo/issues/12", RecentPath + "1" }, fixture.GitHub.Requests);

        // The exact issue is kept even when newer recent matches would fill the limit.
        var first = await fixture.SearchAsync("12", limit: 1);
        CollectionAssert.AreEqual(new[] { 12 }, first.Issues.Select(issue => issue.Number).ToArray());

        fixture.GitHub.Requests.Clear();
        var word = await fixture.SearchAsync("PR");
        CollectionAssert.AreEqual(new[] { 120 }, word.Issues.Select(issue => issue.Number).ToArray(), "Two characters filter the recent titles, ignoring case.");
        Assert.IsEmpty(fixture.GitHub.Requests, "A short word neither asks for an exact issue nor searches.");

        // A number that names a pull request or nothing only leaves the recent matches.
        fixture.GitHub.Respond = request => request.StartsWith("/repos/org/repo/issues/", StringComparison.Ordinal)
            ? request.EndsWith("/121", StringComparison.Ordinal) ? Json(Issue(121, "A pull request", day: 9, pullRequest: true)) : new HttpResponseMessage(HttpStatusCode.NotFound)
            : Json("[]");
        Assert.IsEmpty((await fixture.SearchAsync("121")).Issues);
        var missing = await fixture.SearchAsync("5");
        Assert.AreEqual("ok", missing.Status);
        CollectionAssert.AreEqual(new[] { 5 }, missing.Issues.Select(issue => issue.Number).ToArray());
    }

    [TestMethod]
    public async Task QueryWithSearchSyntax_UsesGitHubSearchOnly()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.GitHub.Respond = _ => Json("{\"total_count\":3,\"items\":" + Array(
            Issue(8, "Crash on start", day: 2),
            Issue(9, "A pull request", day: 5, pullRequest: true),
            Issue(10, "Crash on exit", day: 4)) + "}");

        foreach (var query in new[] { "label:bug crash", "two words", "\"quoted\"", "it's", "  is:open  " })
        {
            fixture.GitHub.Requests.Clear();
            var found = await fixture.SearchAsync(query);
            Assert.AreEqual("ok", found.Status, query);
            CollectionAssert.AreEqual(new[] { 10, 8 }, found.Issues.Select(issue => issue.Number).ToArray(), query);
            CollectionAssert.AreEqual(
                new[] { $"/search/issues?q=repo:org/repo is:issue {query.Trim()}&sort=updated&order=desc&per_page=100&page=1" },
                fixture.GitHub.Requests, query);
        }

        // The typed text travels as one escaped query value: it cannot add request parameters.
        fixture.GitHub.Requests.Clear();
        await fixture.SearchAsync("a b&per_page=1#x");
        StringAssert.EndsWith(fixture.GitHub.RawRequests[^1], "a%20b%26per_page%3D1%23x&sort=updated&order=desc&per_page=100&page=1");
    }

    [TestMethod]
    public async Task PlainWordQuery_FiltersRecentIssuesAndAddsSearchResultsOnlyWhenFewerThanTheLimit()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.GitHub.Respond = request => request.StartsWith("/search/", StringComparison.Ordinal)
            ? Json("{\"items\":" + Array(Issue(30, "Old crash report", day: 1), Issue(20, "Crash in the PICKER", day: 4), Issue(40, "Newest crash", day: 9)) + "}")
            : Json(Array(Issue(21, "Unrelated", day: 6), Issue(20, "Crash in the PICKER", day: 4)));

        var merged = await fixture.SearchAsync("crash");
        Assert.AreEqual("ok", merged.Status);
        CollectionAssert.AreEqual(new[] { 40, 20, 30 }, merged.Issues.Select(issue => issue.Number).ToArray(), "Local and searched matches, without duplicates, newest first.");
        CollectionAssert.AreEqual(
            new[] { RecentPath + "1", "/search/issues?q=repo:org/repo is:issue crash&sort=updated&order=desc&per_page=100&page=1" },
            fixture.GitHub.Requests);

        fixture.GitHub.Requests.Clear();
        var local = await fixture.SearchAsync("picker", limit: 1);
        CollectionAssert.AreEqual(new[] { 20 }, local.Issues.Select(issue => issue.Number).ToArray());
        Assert.IsEmpty(fixture.GitHub.Requests, "Enough local matches need no search, and the recent list is still cached.");

        // The local match is kept ahead of searched ones when the limit cuts the merge.
        var cut = await fixture.SearchAsync("crash", limit: 2);
        CollectionAssert.AreEqual(new[] { 20, 30 }, cut.Issues.Select(issue => issue.Number).ToArray());
    }

    [TestMethod]
    public async Task Limit_DefaultsToFiftyAndIsClampedBetweenOneAndOneHundred()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.GitHub.Respond = request => Json(Array(Enumerable.Range(0, 100)
            .Select(index => Issue((request.EndsWith("&page=1", StringComparison.Ordinal) ? 1000 : 2000) + index, "Issue", day: 1)).ToArray()));

        Assert.HasCount(GithubIssuesService.DefaultLimit, (await fixture.SearchAsync("")).Issues);
        Assert.HasCount(GithubIssuesService.MaximumLimit, (await fixture.SearchAsync("", limit: 1000)).Issues);
        Assert.HasCount(100, (await fixture.SearchAsync("", limit: 100)).Issues);
        Assert.HasCount(7, (await fixture.SearchAsync("", limit: 7)).Issues);
        Assert.HasCount(1, (await fixture.SearchAsync("", limit: 0)).Issues);
        Assert.HasCount(1, (await fixture.SearchAsync("", limit: int.MinValue)).Issues);
        CollectionAssert.AreEqual(new[] { RecentPath + "1" }, fixture.GitHub.Requests, "One full page already holds one hundred issues.");
    }

    [TestMethod]
    public async Task RecentIssuesRepositoryAndToken_AreReusedOnlyWithinTheCacheWindow()
    {
        using var fixture = await Fixture.CreateAsync();
        var updated = 1;
        fixture.GitHub.Respond = _ => Json(Array(Issue(1, "Cached issue", day: updated)));

        var first = await fixture.SearchAsync("");
        fixture.Clock.Advance(TimeSpan.FromSeconds(59));
        updated = 2;
        var second = await fixture.SearchAsync("");
        var filtered = await fixture.SearchAsync("ca");
        Assert.AreEqual(first.Issues.Single().UpdatedAt, second.Issues.Single().UpdatedAt);
        Assert.HasCount(1, filtered.Issues);
        Assert.HasCount(1, fixture.GitHub.Requests, "Typing within the window must not refetch the recent list.");
        Assert.AreEqual(1, fixture.RemoteReads, "Typing within the window must not inspect the remotes again.");
        Assert.AreEqual(1, fixture.TokenReads);

        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        var third = await fixture.SearchAsync("");
        Assert.AreEqual(first.Issues.Single().UpdatedAt?.AddDays(1), third.Issues.Single().UpdatedAt);
        Assert.HasCount(2, fixture.GitHub.Requests);
        Assert.AreEqual(2, fixture.RemoteReads);
        Assert.AreEqual(2, fixture.TokenReads);

        // Another project has its own folder and repository entries.
        fixture.Remotes = ["https://github.com/other/place.git"];
        var other = await fixture.AddProjectAsync("other", git: true);
        Assert.AreEqual("other/place", (await fixture.SearchAsync("", other.Id)).Repository);
        Assert.AreEqual("/repos/other/place/issues?state=all&sort=updated&direction=desc&per_page=100&page=1", fixture.GitHub.Requests[^1]);
        Assert.AreEqual("org/repo", (await fixture.SearchAsync("")).Repository);
        Assert.HasCount(3, fixture.GitHub.Requests);
    }

    [TestMethod]
    public async Task Failure_IsReportedWithoutExceptionTextCredentialsOrPaths_AndIsNeverCached()
    {
        using var fixture = await Fixture.CreateAsync();
        var secret = "connect failed for " + fixture.Root + " with ghp_SECRET_IN_EXCEPTION";
        fixture.GitHub.Respond = _ => throw new HttpRequestException(secret);
        var unreachable = await fixture.SearchAsync("");
        Assert.AreEqual("failed", unreachable.Status);
        Assert.AreEqual("org/repo", unreachable.Repository);
        Assert.IsEmpty(unreachable.Issues);
        AssertSafeFailure(unreachable, fixture);

        // The client's own timeout surfaces as a cancellation the caller did not request.
        fixture.GitHub.Respond = _ => throw new TaskCanceledException(secret, new TimeoutException(secret));
        AssertSafeFailure(await fixture.SearchAsync(""), fixture);

        fixture.GitHub.Respond = _ => Json("<html>" + secret + "</html>");
        AssertSafeFailure(await fixture.SearchAsync(""), fixture);

        fixture.GitHub.Respond = _ => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{\"message\":\"" + Fixture.Token + " rate limited\"}") };
        var refused = await fixture.SearchAsync("");
        AssertSafeFailure(refused, fixture);
        StringAssert.Contains(refused.Message, "403");

        fixture.GitHub.Requests.Clear();
        fixture.GitHub.Respond = _ => Json(Array(Issue(3, "Back again", day: 1)));
        var recovered = await fixture.SearchAsync("");
        Assert.AreEqual("ok", recovered.Status);
        Assert.IsNull(recovered.Message);
        Assert.HasCount(1, recovered.Issues);
        Assert.HasCount(1, fixture.GitHub.Requests, "A failed listing is not kept for the cache window.");

        // A rate-limited search does not hide the matches already found in the recent list.
        fixture.GitHub.Respond = request => request.StartsWith("/search/", StringComparison.Ordinal) ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) : Json("[]");
        var partial = await fixture.SearchAsync("back");
        Assert.AreEqual("ok", partial.Status);
        CollectionAssert.AreEqual(new[] { 3 }, partial.Issues.Select(issue => issue.Number).ToArray());
        var nothing = await fixture.SearchAsync("absent");
        Assert.AreEqual("failed", nothing.Status);
        StringAssert.Contains(nothing.Message, "429");
    }

    [TestMethod]
    public async Task Search_HonorsCancellation()
    {
        using var fixture = await Fixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.GitHub.RespondAsync = async (_, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Json("[]");
        };

        var search = fixture.Service.SearchAsync(new(Epoch, fixture.Project.Id, "", null), cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => search.WaitAsync(TimeSpan.FromSeconds(10)));

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Service.SearchAsync(new(Epoch, fixture.Project.Id, "", null), cancelled.Token));

        // The abandoned fetch left nothing behind: the next query asks GitHub again and succeeds.
        fixture.GitHub.RespondAsync = null;
        fixture.GitHub.Respond = _ => Json(Array(Issue(1, "After cancellation", day: 1)));
        Assert.HasCount(1, (await fixture.SearchAsync("")).Issues);
    }

    [TestMethod]
    public async Task Titles_AreBoundedAndStrippedOfControlCharacters_AndForeignLinksAreDropped()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.GitHub.Respond = _ => Json(Array(
            Issue(1, "  Bell\u0007 line\nbreak\u2028 override\u202E \ud83d\ude00 esc\u001b[31m  ", day: 9, state: "open\u0000\n"),
            Issue(2, new string('x', GithubIssuesService.MaximumTitleLength - 1) + "\ud83d\ude00tail", day: 8, state: new string('s', 100)),
            Issue(3, "Foreign link", day: 7, url: "https://evil.example/org/repo/issues/3"),
            Issue(4, "Plain http", day: 6, url: "http://github.com/org/repo/issues/4"),
            Issue(5, "Script link", day: 5, url: "javascript:alert(1)"),
            Issue(6, "Credentials in link", day: 4, url: "https://user:pass@github.com/org/repo/issues/6"),
            Issue(7, "\u0007\n\u202E", day: 3),
            Issue(8, "Transferred issue", day: 2, url: "https://github.com/org/elsewhere/issues/80")));

        var found = await fixture.SearchAsync("");

        Assert.AreEqual("ok", found.Status);
        CollectionAssert.AreEqual(new[] { 1, 2, 8 }, found.Issues.Select(issue => issue.Number).ToArray());
        Assert.AreEqual("Bell linebreak override \ud83d\ude00 esc[31m", found.Issues[0].Title);
        Assert.AreEqual("open", found.Issues[0].State);
        Assert.AreEqual(new string('x', GithubIssuesService.MaximumTitleLength - 1), found.Issues[1].Title, "A surrogate pair is never cut in half.");
        Assert.AreEqual(32, found.Issues[1].State.Length);
        Assert.AreEqual("https://github.com/org/elsewhere/issues/80", found.Issues[2].Url);
        foreach (var issue in found.Issues)
        {
            Assert.IsLessThanOrEqualTo(GithubIssuesService.MaximumTitleLength, issue.Title.Length);
            Assert.IsFalse(issue.Title.Concat(issue.State).Any(char.IsControl));
        }
    }

    private static void AssertSafeFailure(GitHubIssuesSearchResponse response, Fixture fixture)
    {
        Assert.AreEqual("failed", response.Status);
        Assert.IsNotNull(response.Message);
        Assert.IsLessThanOrEqualTo(GithubIssuesService.MaximumMessageLength, response.Message.Length);
        var wire = JsonSerializer.Serialize(response, DesktopJsonContext.Default.GitHubIssuesSearchResponse);
        foreach (var leaked in new[] { "ghp_SECRET_IN_EXCEPTION", Fixture.Token, "connect failed", "Exception", "<html>", fixture.Root, JsonEncodedText.Encode(fixture.Root).ToString() })
            Assert.IsFalse(wire.Contains(leaked, StringComparison.OrdinalIgnoreCase), leaked);
    }

    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK) { Content = new StringContent(content) };

    private static string Array(params string[] items) => "[" + string.Join(",", items) + "]";

    private static string Issue(int number, string title, int day, string state = "open", bool pullRequest = false, string? url = null)
        => "{\"number\":" + number + ",\"title\":" + JsonSerializer.Serialize(title) + ",\"html_url\":"
           + JsonSerializer.Serialize(url ?? $"https://github.com/org/repo/{(pullRequest ? "pull" : "issues")}/{number}")
           + ",\"updated_at\":\"2026-05-" + day.ToString("00") + "T10:00:00Z\",\"state\":" + JsonSerializer.Serialize(state)
           + (pullRequest ? ",\"pull_request\":{}" : "") + "}";

    /// <summary>Answers api.github.com from a literal function and records what was asked.</summary>
    private sealed class GitHubStub : HttpMessageHandler
    {
        /// <summary>Path and query of each request, unescaped for readable comparisons.</summary>
        public List<string> Requests { get; } = [];

        /// <summary>Path and query of each request exactly as sent.</summary>
        public List<string> RawRequests { get; } = [];

        public List<(string Accept, string UserAgent, string? Authorization)> Headers { get; } = [];

        public Func<string, HttpResponseMessage> Respond { get; set; } = _ => Json("[]");

        public Func<string, CancellationToken, Task<HttpResponseMessage>>? RespondAsync { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("https://api.github.com", request.RequestUri!.GetLeftPart(UriPartial.Authority));
            var path = Uri.UnescapeDataString(request.RequestUri.PathAndQuery);
            Requests.Add(path);
            RawRequests.Add(request.RequestUri.PathAndQuery);
            Headers.Add((request.Headers.Accept.ToString(), request.Headers.UserAgent.ToString(), request.Headers.Authorization?.ToString()));
            return RespondAsync is { } respond ? respond(path, cancellationToken) : Task.FromResult(Respond(path));
        }
    }

    /// <summary>A clock that only moves when a test advances it.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _ticks);

        public void Advance(TimeSpan amount) => Interlocked.Add(ref _ticks, amount.Ticks);
    }

    private sealed class Fixture : IDisposable
    {
        public const string Token = "ghp_TEST_TOKEN";
        private readonly string _root;

        private Fixture(string root, ProjectCatalog projects)
        {
            _root = root;
            Projects = projects;
            Service = new GithubIssuesService(projects, Epoch, GitHub, new GitHubIssueLookupOptions
            {
                TimeProvider = Clock,
                RemoteUrlReader = ReadRemotesAsync,
                TokenProvider = _ =>
                {
                    TokenReads++;
                    return ValueTask.FromResult<string?>(Token);
                },
            });
        }

        public static async Task<Fixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "CodeAlta-github-issues-" + Guid.NewGuid().ToString("N"));
            var global = Directory.CreateDirectory(Path.Combine(root, "home", ".alta")).FullName;
            var fixture = new Fixture(root, new ProjectCatalog(new CatalogOptions { GlobalRoot = global }));
            fixture.Project = await fixture.AddProjectAsync("project", git: true);
            return fixture;
        }

        public ProjectCatalog Projects { get; }
        public ProjectDescriptor Project { get; private set; } = null!; // Assigned by CreateAsync before the fixture is handed out.
        public GithubIssuesService Service { get; }
        public GitHubStub GitHub { get; } = new();
        public ManualClock Clock { get; } = new();
        public string Root => _root;

        /// <summary>The remote URLs every working tree of this fixture reports, most preferred first.</summary>
        public string[] Remotes { get; set; } = ["https://github.com/org/repo.git"];
        public int RemoteReads { get; private set; }
        public int TokenReads { get; private set; }

        /// <summary>Registers a project folder; <paramref name="git"/> makes it look like a git working tree.</summary>
        public async Task<ProjectDescriptor> AddProjectAsync(string name, bool git)
        {
            var path = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
            if (git) Directory.CreateDirectory(Path.Combine(path, ".git"));
            return await Projects.UpsertFromPathAsync(path);
        }

        public Task<GitHubIssuesSearchResponse> SearchAsync(string query) => SearchAsync(query, Project.Id);

        public Task<GitHubIssuesSearchResponse> SearchAsync(string query, int? limit) => SearchAsync(query, Project.Id, limit);

        public Task<GitHubIssuesSearchResponse> SearchAsync(string query, string? projectId, int? limit = null)
            => Service.SearchAsync(new(Epoch, projectId, query, limit), default);

        private async IAsyncEnumerable<string> ReadRemotesAsync(string directory, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Assert.IsTrue(directory.StartsWith(_root, StringComparison.OrdinalIgnoreCase), "Only catalog project folders are inspected.");
            RemoteReads++;
            foreach (var remote in Remotes)
            {
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                yield return remote;
            }
        }

        public void Dispose()
        {
            Service.Dispose();
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { /* Best-effort cleanup of a temporary directory. */ }
        }
    }
}
