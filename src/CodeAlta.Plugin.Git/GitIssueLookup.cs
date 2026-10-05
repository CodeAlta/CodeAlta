using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace CodeAlta.Plugin.Git;

/// <summary>
/// Detects the hosted repository of a folder (GitHub, GitLab or Azure DevOps) and queries its issues
/// for a prompt issue picker.
/// </summary>
/// <remarks>
/// One instance serves concurrent callers. Repository detection (per folder), the list of recently
/// updated issues (per repository) and the credentials (per provider host) are kept in the instance for
/// <see cref="GitIssueLookupOptions.CacheDuration"/>, so a picker that queries on every keystroke does
/// not run git, a provider CLI or refetch the list each time. The <see cref="HttpClient"/> is borrowed:
/// the caller owns and disposes it.
/// </remarks>
public sealed class GitIssueLookup
{
    private const int MaximumCacheEntries = 64;

    // The resource every Azure DevOps organization accepts Microsoft Entra tokens for.
    private const string AzureDevOpsResource = "499b84ac-1321-427f-aa17-267ca6975798";

    private readonly HttpClient _client;
    private readonly TimeSpan _cacheDuration;
    private readonly TimeProvider _timeProvider;
    private readonly Func<string, CancellationToken, IAsyncEnumerable<string>> _remoteUrlReader;
    private readonly Func<GitRepositoryReference, CancellationToken, ValueTask<GitRemoteCredential?>>? _credentialProvider;
    private readonly IReadOnlyCollection<string> _gitLabHosts;
    private readonly Lock _cacheLock = new();
    private readonly Dictionary<string, CacheEntry<GitRepositoryReference?>> _repositories = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CacheEntry<IReadOnlyList<GitIssueReferenceItem>>> _recentIssues = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CacheEntry<GitRemoteCredential?>> _credentials = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _repositoryGate = new(1, 1);
    private readonly SemaphoreSlim _recentIssuesGate = new(1, 1);
    private readonly SemaphoreSlim _credentialGate = new(1, 1);

    /// <summary>Initializes a lookup with the default options.</summary>
    /// <param name="client">The borrowed client used for the provider REST requests; see <see cref="CreateHttpClient()"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> is null.</exception>
    public GitIssueLookup(HttpClient client)
        : this(client, new GitIssueLookupOptions())
    {
    }

    /// <summary>Initializes a lookup.</summary>
    /// <param name="client">The borrowed client used for the provider REST requests; see <see cref="CreateHttpClient()"/>.</param>
    /// <param name="options">The cache, clock, git, host and credential behavior.</param>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> or <paramref name="options"/> is null.</exception>
    public GitIssueLookup(HttpClient client, GitIssueLookupOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        _client = client;
        _cacheDuration = options.CacheDuration;
        _timeProvider = options.TimeProvider ?? TimeProvider.System;
        _remoteUrlReader = options.RemoteUrlReader ?? ReadGitRemoteUrlsAsync;
        _credentialProvider = options.CredentialProvider;
        _gitLabHosts = NormalizeHosts(options.GitLabHosts) ?? GitRemoteUrl.ReadGitLabHostsFromEnvironment();
    }

    /// <summary>Creates a client configured for the provider REST APIs (15 second timeout, user agent).</summary>
    /// <returns>A client the caller owns and disposes.</returns>
    public static HttpClient CreateHttpClient()
        => Configure(new HttpClient());

    /// <summary>Creates a client configured for the provider REST APIs over the specified handler.</summary>
    /// <param name="handler">The handler that sends the requests; the returned client disposes it.</param>
    /// <returns>A client the caller owns and disposes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="handler"/> is null.</exception>
    public static HttpClient CreateHttpClient(HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Configure(new HttpClient(handler));
    }

    /// <summary>
    /// Resolves the credentials of the current user for the provider that hosts a repository, from the
    /// environment first and then from the provider's CLI when it is installed and signed in.
    /// </summary>
    /// <param name="repository">The repository the credentials are for.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The credentials, or <see langword="null"/> when requests must stay unauthenticated.</returns>
    /// <remarks>
    /// <list type="bullet">
    /// <item>GitHub: <c>GITHUB_TOKEN</c>, then <c>GH_TOKEN</c>, then <c>gh auth token</c>.</item>
    /// <item>GitLab: <c>GITLAB_TOKEN</c> or <c>GITLAB_ACCESS_TOKEN</c> for glab's default host only
    /// (<c>GITLAB_HOST</c>, else gitlab.com), then <c>glab config get token --host</c> for the
    /// repository's host. A token is never sent to another host than the one it was issued for.</item>
    /// <item>Azure DevOps: <c>AZURE_DEVOPS_EXT_PAT</c>, then <c>az account get-access-token</c>.</item>
    /// </list>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="repository"/> is null.</exception>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    public static async ValueTask<GitRemoteCredential?> ResolveCredentialAsync(GitRepositoryReference repository, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        switch (repository.Provider)
        {
            case GitRemoteProvider.GitLab:
            {
                var defaultHost = GitRemoteUrl.ReadGitLabHostsFromEnvironment().FirstOrDefault() ?? GitRemoteUrl.GitLabHost;
                var token = string.Equals(repository.Host, defaultHost, StringComparison.OrdinalIgnoreCase)
                    ? ReadEnvironment("GITLAB_TOKEN", "GITLAB_ACCESS_TOKEN")
                    : null;
                token ??= await GitCommandLine.TryReadTokenAsync("glab", ["config", "get", "token", "--host", repository.Host], TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                return GitRemoteCredential.TryBearer(token);
            }

            case GitRemoteProvider.AzureDevOps:
            {
                if (ReadEnvironment("AZURE_DEVOPS_EXT_PAT") is { } personalAccessToken)
                {
                    return GitRemoteCredential.Basic(string.Empty, personalAccessToken);
                }

                // The Azure CLI starts a Python interpreter: it needs more time than the other CLIs.
                var token = await GitCommandLine.TryReadTokenAsync(
                    "az", ["account", "get-access-token", "--resource", AzureDevOpsResource, "--query", "accessToken", "--output", "tsv"],
                    TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                return GitRemoteCredential.TryBearer(token);
            }

            default:
            {
                var token = ReadEnvironment("GITHUB_TOKEN", "GH_TOKEN")
                            ?? await GitCommandLine.TryReadTokenAsync("gh", ["auth", "token"], TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                return GitRemoteCredential.TryBearer(token);
            }
        }
    }

    /// <summary>
    /// Detects the hosted repository of a folder by running git, without any cache: the folder (or an
    /// ancestor) must contain <c>.git</c>, and <c>origin</c> is preferred over the other remotes.
    /// </summary>
    /// <param name="directory">The folder to inspect.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The repository, or <see langword="null"/> when no remote of the folder names a supported provider.</returns>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    public static ValueTask<GitRepositoryReference?> DetectRepositoryAsync(string? directory, CancellationToken cancellationToken = default)
        => DetectRepositoryAsync(directory, ReadGitRemoteUrlsAsync, GitRemoteUrl.ReadGitLabHostsFromEnvironment(), cancellationToken);

    /// <summary>
    /// Resolves the hosted repository of a folder like <see cref="DetectRepositoryAsync(string, CancellationToken)"/>,
    /// through this instance's remote reader, GitLab hosts and cache.
    /// </summary>
    /// <param name="directory">The folder to inspect.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The repository, or <see langword="null"/> when no remote of the folder names a supported provider.</returns>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    public async ValueTask<GitRepositoryReference?> ResolveRepositoryAsync(string? directory, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        if (!IsCaching)
        {
            return await DetectRepositoryAsync(directory, _remoteUrlReader, _gitLabHosts, cancellationToken).ConfigureAwait(false);
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

            var repository = await DetectRepositoryAsync(key, _remoteUrlReader, _gitLabHosts, cancellationToken).ConfigureAwait(false);
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
    /// <returns>The matching issues (never pull or merge requests), most recently updated first.</returns>
    /// <remarks>
    /// An empty query lists recent issues. A query under three characters or made of digits filters the
    /// recent list (number prefix or title text) and adds the exact issue for a number. A query with
    /// whitespace, <c>:</c> or a quote uses the provider's search. Any other word filters the recent list
    /// and adds search results when there are fewer matches than requested. For Azure DevOps the issues
    /// are the work items of the repository's project, and the search matches their titles.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="repository"/> or <paramref name="queryText"/> is null.</exception>
    /// <exception cref="HttpRequestException">The provider could not be reached.</exception>
    /// <exception cref="JsonException">The provider returned a response that is not valid JSON.</exception>
    /// <exception cref="OperationCanceledException">The operation was canceled or the request timed out.</exception>
    public async ValueTask<GitIssueLookupResult> QueryAsync(GitRepositoryReference repository, string queryText, int maximumResults, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(queryText);
        var resultLimit = Math.Clamp(maximumResults, 1, GitIssueSource.MaximumPageSize);
        var query = new GitIssueQuery(_client, repository, await GetAuthorizationAsync(repository, cancellationToken).ConfigureAwait(false));
        var issues = await QueryIssuesAsync(query, queryText.Trim(), resultLimit, cancellationToken).ConfigureAwait(false);
        return new GitIssueLookupResult(issues.OrderByDescending(static issue => issue.UpdatedAt).ToArray(), query.FailureStatusCode);
    }

    private bool IsCaching => _cacheDuration > TimeSpan.Zero;

    private async Task<IReadOnlyList<GitIssueReferenceItem>> QueryIssuesAsync(GitIssueQuery query, string text, int resultLimit, CancellationToken cancellationToken)
    {
        var source = GitIssueSource.For(query.Repository.Provider);
        if (string.IsNullOrWhiteSpace(text))
        {
            return FilterRecentIssues(await GetRecentIssuesAsync(source, query, cancellationToken).ConfigureAwait(false), text, resultLimit);
        }

        if (ShouldFilterRecentIssues(text))
        {
            IReadOnlyList<GitIssueReferenceItem> exactMatches = [];
            if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0)
            {
                exactMatches = await source.GetAsync(query, number, cancellationToken).ConfigureAwait(false);
            }

            var recentMatches = FilterRecentIssues(await GetRecentIssuesAsync(source, query, cancellationToken).ConfigureAwait(false), text, resultLimit);
            return MergeIssueResults(exactMatches, recentMatches, resultLimit);
        }

        if (!IsPlainIssueWordQuery(text))
        {
            return (await source.SearchAsync(query, text, resultLimit, cancellationToken).ConfigureAwait(false)).Issues;
        }

        var localMatches = FilterRecentIssues(await GetRecentIssuesAsync(source, query, cancellationToken).ConfigureAwait(false), text, resultLimit);
        if (localMatches.Count >= resultLimit)
        {
            return localMatches;
        }

        var searchMatches = (await source.SearchAsync(query, text, resultLimit, cancellationToken).ConfigureAwait(false)).Issues;
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

    private static IReadOnlyList<GitIssueReferenceItem> FilterRecentIssues(IReadOnlyList<GitIssueReferenceItem> issues, string query, int maximumResults)
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

    private static bool IssueMatchesRecentFilter(GitIssueReferenceItem issue, string query)
        => issue.Number.ToString(CultureInfo.InvariantCulture).StartsWith(query, StringComparison.OrdinalIgnoreCase) ||
            issue.Title.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<GitIssueReferenceItem> MergeIssueResults(IReadOnlyList<GitIssueReferenceItem> primary, IReadOnlyList<GitIssueReferenceItem> secondary, int maximumResults)
    {
        var results = new List<GitIssueReferenceItem>(maximumResults);
        AddIssues(primary);
        AddIssues(secondary);
        return results.ToArray();

        void AddIssues(IReadOnlyList<GitIssueReferenceItem> source)
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

    // The list every query shape except a provider search starts from: up to 100 recently updated issues.
    private async Task<IReadOnlyList<GitIssueReferenceItem>> GetRecentIssuesAsync(GitIssueSource source, GitIssueQuery query, CancellationToken cancellationToken)
    {
        if (!IsCaching)
        {
            return (await source.ListRecentAsync(query, GitIssueSource.MaximumPageSize, cancellationToken).ConfigureAwait(false)).Issues;
        }

        var key = HostKey(query.Repository) + "/" + query.Repository.FullName;
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

            var fetched = await source.ListRecentAsync(query, GitIssueSource.MaximumPageSize, cancellationToken).ConfigureAwait(false);
            if (fetched.Complete)
            {
                // A refused or partial listing is never retained: the next query asks the provider again.
                Store(_recentIssues, key, fetched.Issues);
            }

            return fetched.Issues;
        }
        finally
        {
            _recentIssuesGate.Release();
        }
    }

    // Resolved once per provider host and kept for the cache window, so typing does not run a CLI per
    // keystroke. A missing credential is kept too: a signed-out CLI is not asked again on every key.
    private async ValueTask<AuthenticationHeaderValue?> GetAuthorizationAsync(GitRepositoryReference repository, CancellationToken cancellationToken)
    {
        if (_credentialProvider is null)
        {
            return null; // The client carries its own credentials, if any.
        }

        if (!IsCaching)
        {
            return (await _credentialProvider(repository, cancellationToken).ConfigureAwait(false))?.ToHeader();
        }

        var key = HostKey(repository);
        if (TryGetCached(_credentials, key, out var cached))
        {
            return cached?.ToHeader();
        }

        await _credentialGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryGetCached(_credentials, key, out cached))
            {
                return cached?.ToHeader();
            }

            var credential = await _credentialProvider(repository, cancellationToken).ConfigureAwait(false);
            // A value that cannot be a header is not a credential: stay unauthenticated.
            credential = credential?.ToHeader() is null ? null : credential;
            Store(_credentials, key, credential);
            return credential?.ToHeader();
        }
        finally
        {
            _credentialGate.Release();
        }
    }

    private static string HostKey(GitRepositoryReference repository)
        => repository.Provider + ":" + repository.Host;

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
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CodeAlta-Git-Plugin");
        return client;
    }

    private static string? ReadEnvironment(params ReadOnlySpan<string> variables)
    {
        foreach (var variable in variables)
        {
            if (Environment.GetEnvironmentVariable(variable) is { } value && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static IReadOnlyCollection<string>? NormalizeHosts(IReadOnlyCollection<string>? hosts)
    {
        if (hosts is null)
        {
            return null;
        }

        var normalized = new List<string>(hosts.Count);
        foreach (var value in hosts)
        {
            if (GitRemoteUrl.TryNormalizeHost(value, out var host))
            {
                normalized.Add(host);
            }
        }

        return normalized;
    }

    private static async ValueTask<GitRepositoryReference?> DetectRepositoryAsync(
        string? directory,
        Func<string, CancellationToken, IAsyncEnumerable<string>> remoteUrlReader,
        IReadOnlyCollection<string> gitLabHosts,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(directory) || !HasGitPath(directory))
        {
            return null;
        }

        await foreach (var remoteUrl in remoteUrlReader(Path.GetFullPath(directory), cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (GitRemoteUrl.TryParse(remoteUrl, gitLabHosts, out var repository))
            {
                return repository;
            }
        }

        return null;
    }

    // `origin` first, then the other remotes in git's order; each is only read when the previous did not match.
    private static async IAsyncEnumerable<string> ReadGitRemoteUrlsAsync(string workingDirectory, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var originUrl = await GitCommandLine.TryGetGitRemoteUrlAsync(workingDirectory, "origin", cancellationToken).ConfigureAwait(false);
        if (originUrl is not null)
        {
            yield return originUrl;
        }

        var remoteNames = await GitCommandLine.GetGitRemoteNamesAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
        foreach (var remoteName in remoteNames)
        {
            if (string.Equals(remoteName, "origin", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var remoteUrl = await GitCommandLine.TryGetGitRemoteUrlAsync(workingDirectory, remoteName, cancellationToken).ConfigureAwait(false);
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

    private readonly record struct CacheEntry<T>(T Value, long StoredAt);
}

/// <summary>Configures a <see cref="GitIssueLookup"/>.</summary>
public sealed class GitIssueLookupOptions
{
    /// <summary>
    /// Gets how long a detected repository, the recent issue list and resolved credentials are reused.
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
    /// Gets the provider of the credentials sent with the requests for a repository (see
    /// <see cref="GitIssueLookup.ResolveCredentialAsync"/>), or <see langword="null"/> to send only what
    /// the client itself carries. A provider that returns <see langword="null"/> leaves requests
    /// unauthenticated.
    /// </summary>
    public Func<GitRepositoryReference, CancellationToken, ValueTask<GitRemoteCredential?>>? CredentialProvider { get; init; }

    /// <summary>
    /// Gets the self-managed GitLab hosts, beside gitlab.com and hosts named <c>gitlab.*</c>, or
    /// <see langword="null"/> to read them from <c>GITLAB_HOST</c>, <c>GITLAB_URI</c> and <c>GL_HOST</c>.
    /// </summary>
    public IReadOnlyCollection<string>? GitLabHosts { get; init; }
}

/// <summary>The credentials sent to a provider in the <c>Authorization</c> header.</summary>
/// <remarks>The value is a secret: the type has no text form that shows it.</remarks>
public sealed class GitRemoteCredential
{
    private GitRemoteCredential(string scheme, string parameter)
    {
        Scheme = scheme;
        Parameter = parameter;
    }

    /// <summary>Gets the authorization scheme: <c>Bearer</c> or <c>Basic</c>.</summary>
    public string Scheme { get; }

    /// <summary>Gets the secret value that follows the scheme.</summary>
    public string Parameter { get; }

    /// <summary>Creates bearer credentials from an access token.</summary>
    /// <param name="token">The access token.</param>
    /// <returns>The credentials.</returns>
    /// <exception cref="ArgumentException"><paramref name="token"/> is null or blank.</exception>
    public static GitRemoteCredential Bearer(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return new GitRemoteCredential("Bearer", token.Trim());
    }

    /// <summary>Creates basic credentials, as an Azure DevOps personal access token needs (with an empty user name).</summary>
    /// <param name="userName">The user name; may be empty.</param>
    /// <param name="password">The password or personal access token.</param>
    /// <returns>The credentials.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="userName"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="password"/> is null or blank.</exception>
    public static GitRemoteCredential Basic(string userName, string password)
    {
        ArgumentNullException.ThrowIfNull(userName);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        return new GitRemoteCredential("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(userName + ":" + password.Trim())));
    }

    /// <inheritdoc />
    public override string ToString() => Scheme;

    internal static GitRemoteCredential? TryBearer(string? token)
        => string.IsNullOrWhiteSpace(token) ? null : Bearer(token);

    internal AuthenticationHeaderValue? ToHeader()
    {
        try
        {
            return new AuthenticationHeaderValue(Scheme, Parameter);
        }
        catch (FormatException)
        {
            return null; // A value with a line break or another separator cannot travel in a header.
        }
    }
}

/// <summary>The issues found by one <see cref="GitIssueLookup.QueryAsync"/> call.</summary>
/// <param name="Issues">The matching issues, most recently updated first.</param>
/// <param name="FailureStatusCode">
/// The status of the first request the provider refused (rate limit, missing access, server error), or
/// <see langword="null"/> when every request succeeded. The issues gathered before or beside the
/// refusal are still returned.
/// </param>
public sealed record GitIssueLookupResult(IReadOnlyList<GitIssueReferenceItem> Issues, HttpStatusCode? FailureStatusCode);
