using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace CodeAlta.Plugin.GitHub;

/// <summary>
/// Detects the github.com repository of a folder and queries its issues for a prompt issue picker.
/// </summary>
/// <remarks>
/// One instance serves concurrent callers. Repository detection (per folder) and the list of recently
/// updated issues (per repository) are kept in the instance for <see cref="GitHubIssueLookupOptions.CacheDuration"/>,
/// so a picker that queries on every keystroke does not run git or refetch the list each time. The
/// <see cref="HttpClient"/> is borrowed: the caller owns and disposes it.
/// </remarks>
public sealed class GitHubIssueLookup
{
    private const int GitHubMaximumPageSize = 100;
    private const int MaximumPages = 5;
    private const int MaximumCacheEntries = 64;

    private readonly HttpClient _client;
    private readonly TimeSpan _cacheDuration;
    private readonly TimeProvider _timeProvider;
    private readonly Func<string, CancellationToken, IAsyncEnumerable<string>> _remoteUrlReader;
    private readonly Func<CancellationToken, ValueTask<string?>>? _tokenProvider;
    private readonly Lock _cacheLock = new();
    private readonly Dictionary<string, CacheEntry<GitHubRepositoryReference?>> _repositories = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CacheEntry<IReadOnlyList<GitHubIssueReferenceItem>>> _recentIssues = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _repositoryGate = new(1, 1);
    private readonly SemaphoreSlim _recentIssuesGate = new(1, 1);
    private CacheEntry<string?>? _token;

    /// <summary>Initializes a lookup with the default options.</summary>
    /// <param name="client">The borrowed client used for GitHub REST requests; see <see cref="CreateHttpClient()"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> is null.</exception>
    public GitHubIssueLookup(HttpClient client)
        : this(client, new GitHubIssueLookupOptions())
    {
    }

    /// <summary>Initializes a lookup.</summary>
    /// <param name="client">The borrowed client used for GitHub REST requests; see <see cref="CreateHttpClient()"/>.</param>
    /// <param name="options">The cache, clock, git and token behavior.</param>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> or <paramref name="options"/> is null.</exception>
    public GitHubIssueLookup(HttpClient client, GitHubIssueLookupOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        _client = client;
        _cacheDuration = options.CacheDuration;
        _timeProvider = options.TimeProvider ?? TimeProvider.System;
        _remoteUrlReader = options.RemoteUrlReader ?? ReadGitRemoteUrlsAsync;
        _tokenProvider = options.TokenProvider;
    }

    /// <summary>Creates a client configured for the GitHub REST API (15 second timeout, GitHub media type, user agent).</summary>
    /// <returns>A client the caller owns and disposes.</returns>
    public static HttpClient CreateHttpClient()
        => Configure(new HttpClient());

    /// <summary>Creates a client configured for the GitHub REST API over the specified handler.</summary>
    /// <param name="handler">The handler that sends the requests; the returned client disposes it.</param>
    /// <returns>A client the caller owns and disposes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="handler"/> is null.</exception>
    public static HttpClient CreateHttpClient(HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Configure(new HttpClient(handler));
    }

    /// <summary>
    /// Resolves the GitHub token of the current user: <c>GITHUB_TOKEN</c>, then <c>GH_TOKEN</c>, then
    /// <c>gh auth token</c> when the GitHub CLI is installed and signed in.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The token, or <see langword="null"/> when requests must stay unauthenticated.</returns>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    public static async ValueTask<string?> ResolveTokenAsync(CancellationToken cancellationToken = default)
    {
        var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN") ?? Environment.GetEnvironmentVariable("GH_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
        {
            token = await GitHubCommandLine.TryGetGhAuthTokenAsync(cancellationToken).ConfigureAwait(false);
        }

        return string.IsNullOrWhiteSpace(token) ? null : token.Trim();
    }

    /// <summary>
    /// Detects the github.com repository of a folder by running git, without any cache: the folder (or
    /// an ancestor) must contain <c>.git</c>, and <c>origin</c> is preferred over the other remotes.
    /// </summary>
    /// <param name="directory">The folder to inspect.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The repository, or <see langword="null"/> when the folder has no github.com remote.</returns>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    public static ValueTask<GitHubRepositoryReference?> DetectRepositoryAsync(string? directory, CancellationToken cancellationToken = default)
        => DetectRepositoryAsync(directory, ReadGitRemoteUrlsAsync, cancellationToken);

    /// <summary>
    /// Resolves the github.com repository of a folder like <see cref="DetectRepositoryAsync(string, CancellationToken)"/>,
    /// through this instance's remote reader and cache.
    /// </summary>
    /// <param name="directory">The folder to inspect.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The repository, or <see langword="null"/> when the folder has no github.com remote.</returns>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    public async ValueTask<GitHubRepositoryReference?> ResolveRepositoryAsync(string? directory, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        if (!IsCaching)
        {
            return await DetectRepositoryAsync(directory, _remoteUrlReader, cancellationToken).ConfigureAwait(false);
        }

        var key = Path.GetFullPath(directory);
        if (TryGetCached(_repositories, key, out var cached))
        {
            return cached;
        }

        // Concurrent queries for one folder wait for the first detection instead of each running git.
        await _repositoryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryGetCached(_repositories, key, out cached))
            {
                return cached;
            }

            var repository = await DetectRepositoryAsync(key, _remoteUrlReader, cancellationToken).ConfigureAwait(false);
            Store(_repositories, key, repository);
            return repository;
        }
        finally
        {
            _repositoryGate.Release();
        }
    }

    /// <summary>Queries the issues of a repository for the text typed after <c>#</c>.</summary>
    /// <param name="repository">The repository to query.</param>
    /// <param name="queryText">The typed text; empty lists the most recently updated issues.</param>
    /// <param name="maximumResults">The preferred maximum result count, clamped to 1..100.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The matching issues (never pull requests), most recently updated first.</returns>
    /// <remarks>
    /// An empty query lists recent issues. A query under three characters or made of digits filters the
    /// recent list (number prefix or title text) and adds the exact issue for a number. A query with
    /// whitespace, <c>:</c> or a quote uses GitHub search. Any other word filters the recent list and
    /// adds GitHub search results when there are fewer matches than requested.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="repository"/> or <paramref name="queryText"/> is null.</exception>
    /// <exception cref="HttpRequestException">GitHub could not be reached.</exception>
    /// <exception cref="JsonException">GitHub returned a response that is not valid JSON.</exception>
    /// <exception cref="OperationCanceledException">The operation was canceled or the request timed out.</exception>
    public async ValueTask<GitHubIssueLookupResult> QueryAsync(GitHubRepositoryReference repository, string queryText, int maximumResults, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(queryText);
        var resultLimit = Math.Clamp(maximumResults, 1, GitHubMaximumPageSize);
        var request = new QueryRequest(repository, await GetAuthorizationAsync(cancellationToken).ConfigureAwait(false));
        var issues = await QueryIssuesAsync(request, queryText.Trim(), resultLimit, cancellationToken).ConfigureAwait(false);
        return new GitHubIssueLookupResult(issues.OrderByDescending(static issue => issue.UpdatedAt).ToArray(), request.FailureStatusCode);
    }

    private bool IsCaching => _cacheDuration > TimeSpan.Zero;

    private async Task<IReadOnlyList<GitHubIssueReferenceItem>> QueryIssuesAsync(QueryRequest request, string query, int resultLimit, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return FilterRecentIssues(await GetRecentIssuesAsync(request, cancellationToken).ConfigureAwait(false), query, resultLimit);
        }

        if (ShouldFilterRecentIssues(query))
        {
            IReadOnlyList<GitHubIssueReferenceItem> exactMatches = [];
            if (int.TryParse(query, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0)
            {
                exactMatches = await GetIssueAsync(request, number, cancellationToken).ConfigureAwait(false);
            }

            var recentMatches = FilterRecentIssues(await GetRecentIssuesAsync(request, cancellationToken).ConfigureAwait(false), query, resultLimit);
            return MergeIssueResults(exactMatches, recentMatches, resultLimit);
        }

        if (!IsPlainIssueWordQuery(query))
        {
            return (await FetchIssuesAsync(request, query, resultLimit, cancellationToken).ConfigureAwait(false)).Issues;
        }

        var localMatches = FilterRecentIssues(await GetRecentIssuesAsync(request, cancellationToken).ConfigureAwait(false), query, resultLimit);
        if (localMatches.Count >= resultLimit)
        {
            return localMatches;
        }

        var searchMatches = (await FetchIssuesAsync(request, query, resultLimit, cancellationToken).ConfigureAwait(false)).Issues;
        return MergeIssueResults(localMatches, searchMatches, resultLimit);
    }

    private static bool ShouldFilterRecentIssues(string query)
        => query.Length < 3 || query.All(char.IsAsciiDigit);

    private static bool IsPlainIssueWordQuery(string query)
    {
        foreach (var ch in query)
        {
            if (char.IsWhiteSpace(ch) || ch is ':' or '"' or '\'')
            {
                return false;
            }
        }

        return true;
    }

    private static IReadOnlyList<GitHubIssueReferenceItem> FilterRecentIssues(IReadOnlyList<GitHubIssueReferenceItem> issues, string query, int maximumResults)
    {
        var filtered = issues;
        if (!string.IsNullOrWhiteSpace(query))
        {
            filtered = issues
                .Where(issue => IssueMatchesRecentFilter(issue, query))
                .ToArray();
        }

        return filtered.Count <= maximumResults ? filtered : filtered.Take(maximumResults).ToArray();
    }

    private static bool IssueMatchesRecentFilter(GitHubIssueReferenceItem issue, string query)
        => issue.Number.ToString(CultureInfo.InvariantCulture).StartsWith(query, StringComparison.OrdinalIgnoreCase) ||
            issue.Title.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<GitHubIssueReferenceItem> MergeIssueResults(IReadOnlyList<GitHubIssueReferenceItem> primary, IReadOnlyList<GitHubIssueReferenceItem> secondary, int maximumResults)
    {
        var results = new List<GitHubIssueReferenceItem>(maximumResults);
        AddIssues(primary);
        AddIssues(secondary);
        return results.ToArray();

        void AddIssues(IReadOnlyList<GitHubIssueReferenceItem> source)
        {
            foreach (var issue in source)
            {
                if (results.Count >= maximumResults)
                {
                    return;
                }

                if (results.Any(existing => existing.Number == issue.Number && string.Equals(existing.Repository, issue.Repository, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                results.Add(issue);
            }
        }
    }

    // The list every query shape except a GitHub search starts from: up to 100 recently updated issues.
    private async Task<IReadOnlyList<GitHubIssueReferenceItem>> GetRecentIssuesAsync(QueryRequest request, CancellationToken cancellationToken)
    {
        if (!IsCaching)
        {
            return (await FetchIssuesAsync(request, string.Empty, GitHubMaximumPageSize, cancellationToken).ConfigureAwait(false)).Issues;
        }

        var key = request.Repository.FullName;
        if (TryGetCached(_recentIssues, key, out var cached))
        {
            return cached;
        }

        // Concurrent queries (fast typing) wait for the first fetch instead of each requesting the list.
        await _recentIssuesGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryGetCached(_recentIssues, key, out cached))
            {
                return cached;
            }

            var fetched = await FetchIssuesAsync(request, string.Empty, GitHubMaximumPageSize, cancellationToken).ConfigureAwait(false);
            if (fetched.Complete)
            {
                // A refused or partial listing is never retained: the next query asks GitHub again.
                Store(_recentIssues, key, fetched.Issues);
            }

            return fetched.Issues;
        }
        finally
        {
            _recentIssuesGate.Release();
        }
    }

    private async Task<(IReadOnlyList<GitHubIssueReferenceItem> Issues, bool Complete)> FetchIssuesAsync(QueryRequest request, string query, int maximumResults, CancellationToken cancellationToken)
    {
        var repository = request.Repository;
        var resultLimit = Math.Clamp(maximumResults, 1, GitHubMaximumPageSize);
        var pageSize = GitHubMaximumPageSize;
        var results = new List<GitHubIssueReferenceItem>(resultLimit);
        for (var page = 1; page <= MaximumPages && results.Count < resultLimit; page++)
        {
            var url = string.IsNullOrWhiteSpace(query)
                ? FormattableString.Invariant($"https://api.github.com/repos/{Uri.EscapeDataString(repository.Owner)}/{Uri.EscapeDataString(repository.Name)}/issues?state=all&sort=updated&direction=desc&per_page={pageSize}&page={page}")
                : FormattableString.Invariant($"https://api.github.com/search/issues?q={Uri.EscapeDataString($"repo:{repository.FullName} is:issue {query}")}&sort=updated&order=desc&per_page={pageSize}&page={page}");
            using var response = await SendAsync(request, url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                request.ReportFailure(response.StatusCode);
                return (results.ToArray(), false);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            var array = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array ? items : root;
            var arrayItemCount = array.ValueKind == JsonValueKind.Array ? array.GetArrayLength() : 0;
            if (arrayItemCount == 0)
            {
                break;
            }

            foreach (var issue in ReadIssues(array, repository.FullName))
            {
                if (results.Count >= resultLimit)
                {
                    break;
                }

                results.Add(issue);
            }

            if (arrayItemCount < pageSize)
            {
                break;
            }
        }

        return (results.ToArray(), true);
    }

    private async Task<IReadOnlyList<GitHubIssueReferenceItem>> GetIssueAsync(QueryRequest request, int number, CancellationToken cancellationToken)
    {
        var repository = request.Repository;
        var url = FormattableString.Invariant($"https://api.github.com/repos/{Uri.EscapeDataString(repository.Owner)}/{Uri.EscapeDataString(repository.Name)}/issues/{number}");
        using var response = await SendAsync(request, url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // A number that names no issue is an ordinary miss, not a failed lookup.
            if (response.StatusCode is not (HttpStatusCode.NotFound or HttpStatusCode.Gone))
            {
                request.ReportFailure(response.StatusCode);
            }

            return [];
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return TryReadIssue(document.RootElement, repository.FullName, out var issue) ? [issue] : [];
    }

    private async Task<HttpResponseMessage> SendAsync(QueryRequest request, string url, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, url);
        if (request.Authorization is not null)
        {
            message.Headers.Authorization = request.Authorization;
        }

        return await _client.SendAsync(message, cancellationToken).ConfigureAwait(false);
    }

    // Resolved once per query, and kept for the cache window so typing does not run `gh` per keystroke.
    private async ValueTask<AuthenticationHeaderValue?> GetAuthorizationAsync(CancellationToken cancellationToken)
    {
        if (_tokenProvider is null)
        {
            return null; // The client carries its own credentials, if any.
        }

        string? token;
        lock (_cacheLock)
        {
            token = _token is { } entry && IsFresh(entry.StoredAt) ? entry.Value : null;
        }

        if (token is null)
        {
            token = await _tokenProvider(cancellationToken).ConfigureAwait(false);
            token = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
            if (IsCaching && token is not null)
            {
                lock (_cacheLock)
                {
                    _token = new CacheEntry<string?>(token, _timeProvider.GetTimestamp());
                }
            }
        }

        try
        {
            return token is null ? null : new AuthenticationHeaderValue("Bearer", token);
        }
        catch (FormatException)
        {
            return null; // A value that cannot be a header is not a token: stay unauthenticated.
        }
    }

    private bool IsFresh(long storedAt)
        => IsCaching && _timeProvider.GetElapsedTime(storedAt) < _cacheDuration;

    private bool TryGetCached<T>(Dictionary<string, CacheEntry<T>> cache, string key, out T value)
    {
        lock (_cacheLock)
        {
            if (cache.TryGetValue(key, out var entry) && IsFresh(entry.StoredAt))
            {
                value = entry.Value;
                return true;
            }
        }

        value = default!;
        return false;
    }

    private void Store<T>(Dictionary<string, CacheEntry<T>> cache, string key, T value)
    {
        lock (_cacheLock)
        {
            if (cache.Count >= MaximumCacheEntries && !cache.ContainsKey(key))
            {
                foreach (var stale in cache.Where(pair => !IsFresh(pair.Value.StoredAt)).Select(static pair => pair.Key).ToArray())
                {
                    cache.Remove(stale);
                }

                if (cache.Count >= MaximumCacheEntries)
                {
                    cache.Clear();
                }
            }

            cache[key] = new CacheEntry<T>(value, _timeProvider.GetTimestamp());
        }
    }

    private static HttpClient Configure(HttpClient client)
    {
        client.Timeout = TimeSpan.FromSeconds(15);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CodeAlta-GitHub-Plugin");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private static async ValueTask<GitHubRepositoryReference?> DetectRepositoryAsync(string? directory, Func<string, CancellationToken, IAsyncEnumerable<string>> remoteUrlReader, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(directory) || !HasGitPath(directory))
        {
            return null;
        }

        await foreach (var remoteUrl in remoteUrlReader(Path.GetFullPath(directory), cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (remoteUrl is not null && TryParseGitHubRemote(remoteUrl, out var repository))
            {
                return repository;
            }
        }

        return null;
    }

    // `origin` first, then the other remotes in git's order; each is only read when the previous did not match.
    private static async IAsyncEnumerable<string> ReadGitRemoteUrlsAsync(string workingDirectory, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var originUrl = await GitHubCommandLine.TryGetGitRemoteUrlAsync(workingDirectory, "origin", cancellationToken).ConfigureAwait(false);
        if (originUrl is not null)
        {
            yield return originUrl;
        }

        var remoteNames = await GitHubCommandLine.GetGitRemoteNamesAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
        foreach (var remoteName in remoteNames)
        {
            if (string.Equals(remoteName, "origin", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var remoteUrl = await GitHubCommandLine.TryGetGitRemoteUrlAsync(workingDirectory, remoteName, cancellationToken).ConfigureAwait(false);
            if (remoteUrl is not null)
            {
                yield return remoteUrl;
            }
        }
    }

    private static bool HasGitPath(string directoryPath)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(directoryPath));
        while (directory is not null)
        {
            var dotGit = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(dotGit) || File.Exists(dotGit))
            {
                return true;
            }

            directory = directory.Parent;
        }

        return false;
    }

    private static bool TryParseGitHubRemote(string remoteUrl, out GitHubRepositoryReference repository)
    {
        repository = default!;
        var normalized = remoteUrl.Trim();
        if (normalized.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "https://github.com/" + normalized[15..];
        }
        else if (normalized.StartsWith("ssh://git@github.com/", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "https://github.com/" + normalized[21..];
        }

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length < 2)
        {
            return false;
        }

        var repo = segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? segments[1][..^4] : segments[1];
        if (string.IsNullOrWhiteSpace(segments[0]) || string.IsNullOrWhiteSpace(repo))
        {
            return false;
        }

        repository = new GitHubRepositoryReference(segments[0], repo);
        return true;
    }

    private static IEnumerable<GitHubIssueReferenceItem> ReadIssues(JsonElement array, string repository)
    {
        if (array.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var item in array.EnumerateArray())
        {
            if (TryReadIssue(item, repository, out var issue))
            {
                yield return issue;
            }
        }
    }

    private static bool TryReadIssue(JsonElement item, string repository, out GitHubIssueReferenceItem issue)
    {
        issue = default!;
        if (item.TryGetProperty("pull_request", out _))
        {
            return false;
        }

        if (!item.TryGetProperty("number", out var numberElement) ||
            !item.TryGetProperty("title", out var titleElement) ||
            !item.TryGetProperty("html_url", out var urlElement) ||
            !item.TryGetProperty("updated_at", out var updatedElement))
        {
            return false;
        }

        var number = numberElement.GetInt32();
        var title = titleElement.GetString() ?? string.Empty;
        var url = urlElement.GetString() ?? string.Empty;
        var state = item.TryGetProperty("state", out var stateElement) ? stateElement.GetString() ?? string.Empty : string.Empty;
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(url) || !DateTimeOffset.TryParse(updatedElement.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var updatedAt))
        {
            return false;
        }

        issue = new GitHubIssueReferenceItem(number, title, url, updatedAt, state, repository);
        return true;
    }

    private readonly record struct CacheEntry<T>(T Value, long StoredAt);

    // One query: the repository, the credentials resolved for it and the first refusal GitHub answered.
    private sealed class QueryRequest(GitHubRepositoryReference repository, AuthenticationHeaderValue? authorization)
    {
        public GitHubRepositoryReference Repository { get; } = repository;

        public AuthenticationHeaderValue? Authorization { get; } = authorization;

        public HttpStatusCode? FailureStatusCode { get; private set; }

        public void ReportFailure(HttpStatusCode statusCode)
            => FailureStatusCode ??= statusCode;
    }
}

/// <summary>Configures a <see cref="GitHubIssueLookup"/>.</summary>
public sealed class GitHubIssueLookupOptions
{
    /// <summary>
    /// Gets how long a detected repository, the recent issue list and a resolved token are reused.
    /// Zero disables the cache. The default is 60 seconds.
    /// </summary>
    public TimeSpan CacheDuration { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Gets the clock that ages cache entries, or <see langword="null"/> for the system clock.</summary>
    public TimeProvider? TimeProvider { get; init; }

    /// <summary>
    /// Gets the reader of a folder's git remote URLs, most preferred first, or <see langword="null"/>
    /// to run git. It is only called for a folder that is inside a git working tree.
    /// </summary>
    public Func<string, CancellationToken, IAsyncEnumerable<string>>? RemoteUrlReader { get; init; }

    /// <summary>
    /// Gets the provider of the bearer token sent with each request (see
    /// <see cref="GitHubIssueLookup.ResolveTokenAsync"/>), or <see langword="null"/> to send only what
    /// the client itself carries. A provider that returns <see langword="null"/> leaves requests
    /// unauthenticated.
    /// </summary>
    public Func<CancellationToken, ValueTask<string?>>? TokenProvider { get; init; }
}

/// <summary>Identifies a github.com repository.</summary>
/// <param name="Owner">The owning user or organization.</param>
/// <param name="Name">The repository name.</param>
public sealed record GitHubRepositoryReference(string Owner, string Name)
{
    /// <summary>Gets the <c>owner/name</c> form.</summary>
    public string FullName => Owner + "/" + Name;
}

/// <summary>The issues found by one <see cref="GitHubIssueLookup.QueryAsync"/> call.</summary>
/// <param name="Issues">The matching issues, most recently updated first.</param>
/// <param name="FailureStatusCode">
/// The status of the first request GitHub refused (rate limit, missing access, server error), or
/// <see langword="null"/> when every request succeeded. The issues gathered before or beside the
/// refusal are still returned.
/// </param>
public sealed record GitHubIssueLookupResult(IReadOnlyList<GitHubIssueReferenceItem> Issues, HttpStatusCode? FailureStatusCode);
