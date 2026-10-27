using System.Net;
using System.Text;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Logging;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Plugin.Git;
using CodeAlta.Plugins.Abstractions;
using XenoAtom.Logging;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// What the issue trackers write to the log when they fail: the failure the Issues tab shows, the request and what
/// the service said of it, never a credential. Initializes the process logger, so it never runs beside other logging tests.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class IssueTrackerLoggingTests
{
    private const string Epoch = "6c8f2a14-0d6b-4c2e-9a7f-1b3e5d7f9a2c";

    [TestMethod]
    public async Task AzureDevOps_AskingToSignIn_IsLoggedWithTheRequestAndThePageItAnswered()
    {
        var rows = await LoggedAsync(async () =>
        {
            // Azure DevOps answers a request without credentials with its sign-in page, and 203.
            var handler = new Handler(_ => Answer(HttpStatusCode.NonAuthoritativeInformation, "text/html",
                "<html><head><title>Azure DevOps Services | Sign In</title></head><body>…</body></html>"));
            var tracker = Tracker(handler, GitRepositoryReference.AzureDevOps("org", "proj", "app"), null);

            var page = await tracker.ListAsync(new(TrackedItemKind.Issue, TrackedItemFilter.Open, null, 10), default);

            Assert.AreEqual("Azure DevOps asks to sign in.", page.Problem);
        });

        var warning = rows.Single(static row => row.Level == "Warn");
        Assert.AreEqual((GitIssueLog.LoggerName, LogFeatures.IssueTrackers), (warning.Logger, warning.Feature));
        StringAssert.Contains(warning.Text, "Azure DevOps");
        StringAssert.Contains(warning.Text, "listing the issues failed (Azure DevOps asks to sign in.)");
        StringAssert.Contains(warning.Text, "https://dev.azure.com/org/");
        StringAssert.Contains(warning.Text, "answered 203 NonAuthoritativeInformation (text/html) without credentials");
        StringAssert.Contains(warning.Text, "an HTML page titled \"Azure DevOps Services | Sign In\"");
        StringAssert.Contains(warning.Text, "The requests carried no credentials");
        Assert.IsTrue(rows.Any(static row => row.Level == "Debug" && row.Text.Contains("answered 203", StringComparison.Ordinal)), "Each request is in the log at Debug.");
    }

    [TestMethod]
    public async Task GitHub_RefusingTheCredentials_IsLoggedWithWhatItSaid_NeverTheToken()
    {
        var rows = await LoggedAsync(async () =>
        {
            var handler = new Handler(request => request.RequestUri!.AbsolutePath == "/repos/o/r/issues/7"
                ? Answer(HttpStatusCode.OK, "application/json", "{\"message\":\"boom\"")
                : Answer(HttpStatusCode.Unauthorized, "application/json", """{"message":"Bad credentials","documentation_url":"https://docs.github.com/rest"}"""));
            var tracker = Tracker(handler, GitRepositoryReference.GitHub("o", "r"), GitRemoteCredential.TryBearer("secret-token-123"));

            var page = await tracker.ListAsync(new(TrackedItemKind.PullRequest, TrackedItemFilter.All, null, 10), default);
            Assert.AreEqual("GitHub asks to sign in.", page.Problem);

            // A body that is not JSON: the failure is the exception, logged with what was read.
            Assert.IsNull(await tracker.ReadAsync(TrackedItemKind.Issue, "7", default));
        });

        var listing = rows.Single(static row => row.Level == "Warn" && row.Text.Contains("listing", StringComparison.Ordinal));
        StringAssert.Contains(listing.Text, "GitHub o/r: listing the pull requests failed (GitHub asks to sign in.)");
        StringAssert.Contains(listing.Text, "GET https://api.github.com/repos/o/r/pulls");
        StringAssert.Contains(listing.Text, "answered 401 Unauthorized (application/json) with credentials: Bad credentials");
        var reading = rows.Single(static row => row.Level == "Warn" && row.Text.Contains("reading", StringComparison.Ordinal));
        StringAssert.Contains(reading.Text, "GitHub o/r: reading the issue 7 failed.");
        StringAssert.Contains(reading.Text, "System.Text.Json");
        Assert.IsFalse(rows.Any(static row => row.Text.Contains("secret-token-123", StringComparison.Ordinal)), "Never the token.");
    }

    [TestMethod]
    public async Task TheIssuesTab_LogsWhatItShowsInPlaceOfAListing_WithTheExceptionItDoesNotShow()
    {
        var root = Directory.CreateTempSubdirectory("codealta-issues-log-").FullName;
        try
        {
            var projects = new ProjectCatalog(new CatalogOptions { GlobalRoot = Directory.CreateDirectory(Path.Combine(root, "global")).FullName });
            var project = await projects.UpsertFromPathAsync(Directory.CreateDirectory(Path.Combine(root, "app")).FullName);
            var tracker = new FailingTracker();
            IIssueTrackerSource[] sources = [new Source([tracker]), new Source(null)];
            var rpc = new IssuesService(() => sources, projects, null, Epoch);

            IssueListResponse? refused = null, failed = null;
            var rows = await LoggedAsync(async () =>
            {
                tracker.Page = new([]) { Problem = "Jira: You are not logged in. Run acli jira auth login.", NeedsSignIn = true };
                refused = await rpc.ListAsync(new(Epoch, project.Id, "jira", "issue", "open", null, null), default);
                tracker.Page = null;
                failed = await rpc.ListAsync(new(Epoch, project.Id, "jira", "issue", "open", null, null), default);
            });

            Assert.AreEqual("Jira: You are not logged in. Run acli jira auth login.", refused!.Problem);
            Assert.AreEqual("Jira could not be read.", failed!.Problem, "The page is not given the exception.");
            var warnings = rows.Where(static row => row.Level == "Warn" && row.Logger == IssuesService.LoggerName).Select(static row => row.Text).ToArray();
            Assert.IsTrue(warnings.Any(static text => text.Contains("could not give the trackers", StringComparison.Ordinal)
                && text.Contains("plugin failed", StringComparison.Ordinal)), string.Join('\n', warnings));
            Assert.IsTrue(warnings.Any(static text => text == "Jira ALTA: listing the issues shows: Jira: You are not logged in. Run acli jira auth login. (sign-in asked)"
                ), string.Join('\n', warnings));
            Assert.IsTrue(warnings.Any(static text => text.StartsWith("Jira ALTA: listing the issues shows: Jira could not be read.", StringComparison.Ordinal)
                && text.Contains("HttpRequestException", StringComparison.Ordinal) && text.Contains("connection refused", StringComparison.Ordinal)), string.Join('\n', warnings));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Jira_ACommandThatFails_IsLoggedWithWhatTheCliSaid_NeverTheTokenItReads()
    {
        var cli = new FakeJiraCli();
        var client = new CodeAlta.Plugin.Jira.JiraClient(cli);
        var rows = await LoggedAsync(async () =>
        {
            Assert.IsNotNull(await client.SignInWithTokenAsync("example.atlassian.net", "ana@example.com", "jira-secret-token", default));
        });

        var row = rows.Single();
        Assert.AreEqual((CodeAlta.Plugin.Jira.JiraClient.LoggerName, "Info", LogFeatures.IssueTrackers), (row.Logger, row.Level, row.Feature));
        StringAssert.Contains(row.Text, "'acli jira auth login --site example.atlassian.net --email ana@example.com --token' exited with code 1");
        StringAssert.Contains(row.Text, "Error: unauthorized: invalid API token");
        Assert.AreEqual("jira-secret-token\n", cli.Input, "The token is given to the CLI, not to the log.");
        Assert.IsFalse(row.Text.Contains("jira-secret-token", StringComparison.Ordinal));
    }

    private static async Task<DesktopLogLine[]> LoggedAsync(Func<Task> action)
    {
        var capture = new DesktopLogCapture();
        var config = new LogManagerConfig();
        config.RootLogger.MinimumLevel = LogLevel.Debug;
        config.RootLogger.Writers.Add(new DesktopCaptureLogWriter(capture));
        LogManager.Initialize(config);
        try { await action(); }
        finally
        {
            // The loggers keep the levels of the last configuration once the log ends: the tests that run after this
            // one, without a log, must find them off.
            config.RootLogger.MinimumLevel = LogLevel.None;
            config.ApplyChanges();
            LogManager.Shutdown();
        }

        return capture.Snapshot().Rows;
    }

    private static IIssueTracker Tracker(Handler handler, GitRepositoryReference repository, GitRemoteCredential? credential)
        => new GitIssueLookup(GitIssueLookup.CreateHttpClient(handler), new GitIssueLookupOptions
        {
            CacheDuration = TimeSpan.Zero,
            CredentialProvider = (_, _) => ValueTask.FromResult(credential),
        }).GetTracker(repository);

    private static HttpResponseMessage Answer(HttpStatusCode status, string media, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, media) };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(answer(request));
    }

    private sealed class Source(IReadOnlyList<IIssueTracker>? trackers) : IIssueTrackerSource
    {
        public ValueTask<IReadOnlyList<IIssueTracker>> GetTrackersAsync(string projectPath, CancellationToken cancellationToken)
            => trackers is null ? throw new InvalidOperationException("plugin failed") : ValueTask.FromResult(trackers);
    }

    private sealed class FakeJiraCli : CodeAlta.Plugin.Jira.IJiraCli
    {
        public string? Input { get; private set; }
        public bool IsInstalled => true;
        public string? ExecutablePath => "/fake/acli";
        public ValueTask<string?> EnsureInstalledAsync(Action? progress, CancellationToken cancellationToken) => ValueTask.FromResult<string?>(null);

        public Task<CodeAlta.Plugin.Jira.JiraCliResult> RunAsync(IReadOnlyList<string> arguments, string? input, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Input = input;
            return Task.FromResult(new CodeAlta.Plugin.Jira.JiraCliResult(1, string.Empty, "  Error: unauthorized:\n invalid API token  "));
        }
    }

    private sealed class FailingTracker : IIssueTracker
    {
        public string Service => "jira";
        public string DisplayName => "Jira";
        public string Location => "ALTA";
        public string? WebUrl => null;
        public IReadOnlyList<TrackedItemKind> Kinds => [TrackedItemKind.Issue];
        public IReadOnlyList<string> Parts => [];
        public TrackedItemPage? Page { get; set; }

        public ValueTask<IReadOnlyList<TrackedFilterDescriptor>> GetFiltersAsync(TrackedFilterRequest request, CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<TrackedFilterDescriptor>>([]);
        public ValueTask<IReadOnlyList<TrackedField>> GetFieldsAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<TrackedField>>([]);
        public ValueTask<TrackedItemPage> ListAsync(TrackedItemQuery query, CancellationToken cancellationToken)
            => Page is { } page ? ValueTask.FromResult(page) : throw new HttpRequestException("connection refused (dev.azure.com:443)");
        public ValueTask<TrackedItemDetail?> ReadAsync(TrackedItemKind kind, string id, CancellationToken cancellationToken) => ValueTask.FromResult<TrackedItemDetail?>(null);
    }
}
