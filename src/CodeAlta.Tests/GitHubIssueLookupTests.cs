using System.Net;
using System.Net.Http.Headers;
using CodeAlta.Plugin.GitHub;

namespace CodeAlta.Tests;

/// <summary>The reusable issue lookup over a literal handler and literal remotes: no network, no git.</summary>
[TestClass]
public sealed class GitHubIssueLookupTests
{
    private const string OneIssue = """
        [ { "number": 7, "title": "Found issue", "html_url": "https://github.com/org/repo/issues/7", "updated_at": "2026-05-25T10:00:00Z", "state": "open" } ]
        """;

    [TestMethod]
    public async Task Constructor_AndQuery_RejectNullArguments()
    {
        using var client = new HttpClient(new StubHandler(_ => Ok("[]")));
        Assert.ThrowsExactly<ArgumentNullException>(() => new GitHubIssueLookup(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => new GitHubIssueLookup(client, null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => GitHubIssueLookup.CreateHttpClient(null!));
        var lookup = new GitHubIssueLookup(client);
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(async () => await lookup.QueryAsync(null!, "", 10));
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(async () => await lookup.QueryAsync(new("org", "repo"), null!, 10));
    }

    [TestMethod]
    public async Task ResolveRepository_ReadsRemotesOnlyInsideAGitWorkingTree()
    {
        using var folder = new TempFolder();
        var reads = 0;
        using var client = new HttpClient(new StubHandler(_ => throw new AssertFailedException("Detection must not reach GitHub.")));
        var lookup = new GitHubIssueLookup(client, new GitHubIssueLookupOptions
        {
            RemoteUrlReader = (_, _) =>
            {
                reads++;
                return Remotes("https://example.invalid/org/mirror.git", "git@github.com:org/repo.git");
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
        Assert.AreEqual("org", repository.Owner);
        Assert.AreEqual("repo", repository.Name);
        Assert.AreEqual("org/repo", repository.FullName);
        Assert.AreEqual(1, reads);
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
        var lookup = new GitHubIssueLookup(client, new GitHubIssueLookupOptions
        {
            CacheDuration = TimeSpan.Zero,
            RemoteUrlReader = (_, _) =>
            {
                reads++;
                return Remotes("https://github.com/org/repo.git");
            },
            TokenProvider = _ =>
            {
                tokens++;
                return ValueTask.FromResult<string?>("token-" + tokens);
            },
        });

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var repository = await lookup.ResolveRepositoryAsync(folder.Path);
            Assert.IsNotNull(repository);
            var result = await lookup.QueryAsync(repository, "", 10);
            Assert.AreEqual(7, result.Issues.Single().Number);
            Assert.AreEqual("org/repo", result.Issues.Single().Repository);
            Assert.IsNull(result.FailureStatusCode);
            Assert.AreEqual(attempt, reads);
            Assert.HasCount(attempt, handler.Requests);
            Assert.AreEqual("Bearer token-" + attempt, handler.Authorizations[^1]);
        }
    }

    [TestMethod]
    public async Task Credentials_ComeFromTheProviderOrElseFromTheClient()
    {
        var handler = new StubHandler(_ => Ok("[]"));
        using var client = new HttpClient(handler);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "client-token");
        var repository = new GitHubRepositoryReference("org", "repo");

        await new GitHubIssueLookup(client).QueryAsync(repository, "", 10);
        Assert.AreEqual("Bearer client-token", handler.Authorizations[^1], "Without a provider the client's own credentials are sent.");

        string? provided = "  provider-token\n";
        var lookup = new GitHubIssueLookup(client, new GitHubIssueLookupOptions { CacheDuration = TimeSpan.Zero, TokenProvider = _ => ValueTask.FromResult<string?>(provided) });
        await lookup.QueryAsync(repository, "", 10);
        Assert.AreEqual("Bearer provider-token", handler.Authorizations[^1]);

        client.DefaultRequestHeaders.Authorization = null;
        foreach (var unusable in new[] { null, "", "   ", "two\nlines" })
        {
            provided = unusable;
            await lookup.QueryAsync(repository, "", 10);
            Assert.IsNull(handler.Authorizations[^1], "A missing or malformed token leaves the request unauthenticated.");
        }
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
        var lookup = new GitHubIssueLookup(client);
        var repository = new GitHubRepositoryReference("org", "repo");

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
    }

    private static HttpResponseMessage Ok(string content) => new(HttpStatusCode.OK) { Content = new StringContent(content) };

    private static async IAsyncEnumerable<string> Remotes(params string[] urls)
    {
        foreach (var url in urls)
        {
            await Task.Yield();
            yield return url;
        }
    }

    private sealed class StubHandler(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        public List<string?> Authorizations { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.AreEqual("api.github.com", request.RequestUri!.Host);
            var path = Uri.UnescapeDataString(request.RequestUri.PathAndQuery);
            Requests.Add(path);
            Authorizations.Add(request.Headers.Authorization?.ToString());
            return Task.FromResult(respond(path));
        }
    }

    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } = Directory.CreateDirectory(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CodeAlta.GitHubIssueLookupTests." + Guid.NewGuid().ToString("N"))).FullName;

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { /* Best-effort cleanup of a temporary directory. */ }
        }
    }
}
