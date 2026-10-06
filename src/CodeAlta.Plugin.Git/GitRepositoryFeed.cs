using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CodeAlta.Plugin.Git;

/// <summary>What a <see cref="GitRepositoryFeed"/> lists.</summary>
public enum GitFeedKind
{
    /// <summary>The open issues, newest first (work items for Azure DevOps).</summary>
    Issues,

    /// <summary>The open pull requests, most recently updated first (merge requests for GitLab).</summary>
    PullRequests,
}

/// <summary>How a reading of a <see cref="GitRepositoryFeed"/> ended.</summary>
public enum GitFeedStatus
{
    /// <summary>The provider answered with the items.</summary>
    Ok,

    /// <summary>Nothing changed since the reading the entity tag comes from: there are no items to look at.</summary>
    NotModified,

    /// <summary>The folder has no repository on a supported provider.</summary>
    NoRepository,

    /// <summary>The provider refused: no access, no sign-in, or too many requests.</summary>
    Refused,

    /// <summary>The provider could not be reached, or did not answer as expected.</summary>
    Failed,
}

/// <summary>An issue or a pull request of a repository.</summary>
/// <param name="Number">The number people use for it.</param>
/// <param name="Title">Its title.</param>
/// <param name="Url">Its page.</param>
/// <param name="Author">The login of who opened it, when the provider tells.</param>
/// <param name="CreatedAt">When it was opened.</param>
public sealed record GitFeedItem(int Number, string Title, string Url, string? Author, DateTimeOffset CreatedAt)
{
    /// <summary>Gets the last commit of a pull request; null for an issue.</summary>
    public string? Head { get; init; }

    /// <summary>
    /// Gets whether who opened it can write to the repository, when the provider says so with the item: null when
    /// it has to be asked with <see cref="GitRepositoryFeed.IsTrustedAsync"/>.
    /// </summary>
    public bool? Trusted { get; init; }

    /// <summary>Gets the identifier the provider uses for the author, for <see cref="GitRepositoryFeed.IsTrustedAsync"/>.</summary>
    public string? AuthorId { get; init; }
}

/// <summary>One reading of a <see cref="GitRepositoryFeed"/>.</summary>
/// <param name="Status">How the reading ended.</param>
/// <param name="Repository">The repository of the folder, when it has one.</param>
/// <param name="Items">The items, when <paramref name="Status"/> is <see cref="GitFeedStatus.Ok"/>.</param>
/// <param name="EntityTag">What to pass to the next reading so that an unchanged list costs nothing; null when the provider gives none.</param>
/// <param name="Message">Why the provider refused or the reading failed.</param>
public sealed record GitFeedPage(GitFeedStatus Status, GitRepositoryReference? Repository, IReadOnlyList<GitFeedItem> Items, string? EntityTag = null, string? Message = null);

/// <summary>
/// Reads the open issues and pull requests of the repository of a folder from GitHub, GitLab or Azure DevOps, with
/// the credentials <see cref="GitIssueLookup.ResolveCredentialAsync"/> finds. A caller that looks again from time
/// to time passes the entity tag of its last reading: GitHub then answers an unchanged list without counting the
/// request.
/// </summary>
public sealed class GitRepositoryFeed : IDisposable
{
    /// <summary>The most issues one reading returns: the newest ones.</summary>
    public const int IssuePageSize = 30;

    /// <summary>
    /// The most pull requests one reading returns: the ones that changed last. A caller that compares their last
    /// commit between two readings sees no further than this page.
    /// </summary>
    public const int PullRequestPageSize = 100;

    private const string GitHubAccept = "application/vnd.github+json";
    private const string AzureApiVersion = "7.1";
    private const int GitLabDeveloperAccess = 30;
    private const int MaximumTrustEntries = 512;

    private readonly HttpClient _client;
    private readonly Func<string, CancellationToken, ValueTask<GitRepositoryReference?>> _repository;
    private readonly Func<GitRepositoryReference, CancellationToken, ValueTask<GitRemoteCredential?>> _credential;
    private readonly ConcurrentDictionary<string, bool> _trust = new(StringComparer.Ordinal);

    /// <summary>Creates a feed that reads the providers over the network.</summary>
    public GitRepositoryFeed()
        : this(GitIssueLookup.CreateHttpClient(), static (directory, token) => GitIssueLookup.DetectRepositoryAsync(directory, token), GitIssueLookup.ResolveCredentialAsync)
    {
    }

    internal GitRepositoryFeed(HttpClient client, Func<string, CancellationToken, ValueTask<GitRepositoryReference?>> repository,
        Func<GitRepositoryReference, CancellationToken, ValueTask<GitRemoteCredential?>> credential)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(credential);
        (_client, _repository, _credential) = (client, repository, credential);
    }

    /// <summary>Reads the open issues or pull requests of the repository of a folder.</summary>
    /// <param name="directory">A folder of the repository.</param>
    /// <param name="kind">What to list.</param>
    /// <param name="entityTag">The entity tag of the previous reading of the same list, or null.</param>
    /// <param name="cancellationToken">Cancels the reading.</param>
    /// <returns>The items, or why there are none. Failures of the provider and of the network are results, not exceptions.</returns>
    /// <exception cref="ArgumentException"><paramref name="directory"/> is blank.</exception>
    /// <exception cref="OperationCanceledException">The reading was canceled.</exception>
    public async ValueTask<GitFeedPage> ReadAsync(string directory, GitFeedKind kind, string? entityTag = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        GitRepositoryReference? repository = null;
        try
        {
            repository = await _repository(directory, cancellationToken).ConfigureAwait(false);
            if (repository is null) return new(GitFeedStatus.NoRepository, null, []);
            var authorization = (await _credential(repository, cancellationToken).ConfigureAwait(false))?.ToHeader();
            return repository.Provider switch
            {
                GitRemoteProvider.GitLab => await ReadGitLabAsync(repository, kind, authorization, cancellationToken).ConfigureAwait(false),
                GitRemoteProvider.AzureDevOps => await ReadAzureAsync(repository, kind, authorization, cancellationToken).ConfigureAwait(false),
                _ => await ReadGitHubAsync(repository, kind, entityTag, authorization, cancellationToken).ConfigureAwait(false),
            };
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or IOException
            || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // A request that timed out is canceled by the client, not by the caller.
            return new(GitFeedStatus.Failed, repository, [], Message: exception is OperationCanceledException ? "The provider did not answer in time." : exception.Message);
        }
    }

    /// <summary>
    /// Tells whether who opened an item is one of the people of its repository: on GitHub its owner, a member of
    /// its organization or a collaborator, on GitLab a developer or more, on Azure DevOps anyone of the
    /// organization. On GitHub this is the association the provider reports, which does not say that the person
    /// can write to the repository.
    /// </summary>
    /// <param name="repository">The repository the item is from.</param>
    /// <param name="item">The item.</param>
    /// <param name="cancellationToken">Cancels the question.</param>
    /// <returns>Whether the provider says so; null when it could not be asked, and asking again later may tell.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="OperationCanceledException">The question was canceled.</exception>
    public async ValueTask<bool?> IsTrustedAsync(GitRepositoryReference repository, GitFeedItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(item);
        if (item.Trusted is { } known) return known;
        if (repository.Provider != GitRemoteProvider.GitLab || string.IsNullOrWhiteSpace(item.AuthorId)) return false;
        var key = repository.Host + "/" + repository.FullName + "#" + item.AuthorId;
        if (_trust.TryGetValue(key, out var cached)) return cached;
        var trusted = false;
        try
        {
            var authorization = (await _credential(repository, cancellationToken).ConfigureAwait(false))?.ToHeader();
            using var response = await SendAsync(HttpMethod.Get, GitLabProjectUrl(repository) + "/members/all/" + Uri.EscapeDataString(item.AuthorId), "application/json",
                authorization, null, null, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
                trusted = document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("access_level", out var level)
                    && level.ValueKind == JsonValueKind.Number && level.TryGetInt32(out var access) && access >= GitLabDeveloperAccess;
            }
            else if (response.StatusCode != HttpStatusCode.NotFound)
            {
                // Not an answer about this person.
                return null;
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or IOException
            || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        if (_trust.Count >= MaximumTrustEntries) _trust.Clear();
        _trust[key] = trusted;
        return trusted;
    }

    /// <inheritdoc />
    public void Dispose() => _client.Dispose();

    private async Task<GitFeedPage> ReadGitHubAsync(GitRepositoryReference repository, GitFeedKind kind, string? entityTag, AuthenticationHeaderValue? authorization, CancellationToken cancellationToken)
    {
        var path = kind == GitFeedKind.Issues ? "issues?state=open&sort=created&direction=desc" : "pulls?state=open&sort=updated&direction=desc";
        var url = FormattableString.Invariant($"https://api.github.com/repos/{Uri.EscapeDataString(repository.Owner)}/{Uri.EscapeDataString(repository.Name)}/{path}&per_page={Size(kind)}");
        using var response = await SendAsync(HttpMethod.Get, url, GitHubAccept, authorization, entityTag, null, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotModified) return new(GitFeedStatus.NotModified, repository, [], entityTag);
        if (!response.IsSuccessStatusCode) return Refused(repository, response.StatusCode);
        using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var items = new List<GitFeedItem>();
        foreach (var item in Array(document.RootElement))
        {
            // The listing of the issues holds the pull requests too.
            if (kind == GitFeedKind.Issues && item.TryGetProperty("pull_request", out _)) continue;
            if (!TryNumber(item, "number", out var number) || Text(item, "title") is not { } title || Text(item, "html_url") is not { } page || !TryDate(item, "created_at", out var created)) continue;
            var association = Text(item, "author_association");
            items.Add(new GitFeedItem(number, title, page, item.TryGetProperty("user", out var user) ? Text(user, "login") : null, created)
            {
                Head = kind == GitFeedKind.PullRequests && item.TryGetProperty("head", out var head) ? Text(head, "sha") : null,
                Trusted = association is "OWNER" or "MEMBER" or "COLLABORATOR",
            });
        }

        return new(GitFeedStatus.Ok, repository, items, response.Headers.ETag?.ToString());
    }

    private async Task<GitFeedPage> ReadGitLabAsync(GitRepositoryReference repository, GitFeedKind kind, AuthenticationHeaderValue? authorization, CancellationToken cancellationToken)
    {
        var path = kind == GitFeedKind.Issues ? "issues?state=opened&order_by=created_at&sort=desc" : "merge_requests?state=opened&order_by=updated_at&sort=desc";
        var url = FormattableString.Invariant($"{GitLabProjectUrl(repository)}/{path}&per_page={Size(kind)}");
        using var response = await SendAsync(HttpMethod.Get, url, "application/json", authorization, null, null, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return Refused(repository, response.StatusCode);
        using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var items = new List<GitFeedItem>();
        foreach (var item in Array(document.RootElement))
        {
            if (!TryNumber(item, "iid", out var number) || Text(item, "title") is not { } title || Text(item, "web_url") is not { } page || !TryDate(item, "created_at", out var created)) continue;
            var author = item.TryGetProperty("author", out var value) && value.ValueKind == JsonValueKind.Object ? value : default;
            items.Add(new GitFeedItem(number, title, page, Text(author, "username"), created)
            {
                Head = kind == GitFeedKind.PullRequests ? Text(item, "sha") : null,
                AuthorId = author.ValueKind == JsonValueKind.Object && author.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number ? id.GetRawText() : null,
            });
        }

        return new(GitFeedStatus.Ok, repository, items);
    }

    private async Task<GitFeedPage> ReadAzureAsync(GitRepositoryReference repository, GitFeedKind kind, AuthenticationHeaderValue? authorization, CancellationToken cancellationToken)
    {
        var project = $"https://{repository.Host}/{Uri.EscapeDataString(repository.Owner)}/{Uri.EscapeDataString(repository.Project ?? repository.Name)}";
        var items = new List<GitFeedItem>();
        if (kind == GitFeedKind.PullRequests)
        {
            var url = FormattableString.Invariant($"{project}/_apis/git/repositories/{Uri.EscapeDataString(repository.Name)}/pullrequests?searchCriteria.status=active&$top={PullRequestPageSize}&api-version={AzureApiVersion}");
            using var response = await SendAsync(HttpMethod.Get, url, "application/json", authorization, null, null, cancellationToken).ConfigureAwait(false);
            if (!IsAzureAnswer(response)) return Refused(repository, response.IsSuccessStatusCode ? HttpStatusCode.Unauthorized : response.StatusCode);
            using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            foreach (var item in Array(document.RootElement.TryGetProperty("value", out var value) ? value : default))
            {
                if (!TryNumber(item, "pullRequestId", out var number) || Text(item, "title") is not { } title || !TryDate(item, "creationDate", out var created)) continue;
                items.Add(new GitFeedItem(number, title, FormattableString.Invariant($"{project}/_git/{Uri.EscapeDataString(repository.Name)}/pullrequest/{number}"),
                    item.TryGetProperty("createdBy", out var author) ? Text(author, "uniqueName") ?? Text(author, "displayName") : null, created)
                {
                    Head = item.TryGetProperty("lastMergeSourceCommit", out var commit) ? Text(commit, "commitId") : null,
                    Trusted = true,
                });
            }

            return new(GitFeedStatus.Ok, repository, items);
        }

        // Work items are found in two steps: a query returns their numbers, then one request reads their fields.
        var numbers = new List<int>();
        var query = "{\"query\":\"" + JsonEncodedText.Encode("SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = @project ORDER BY [System.CreatedDate] DESC") + "\"}";
        using (var response = await SendAsync(HttpMethod.Post, FormattableString.Invariant($"{project}/_apis/wit/wiql?$top={IssuePageSize}&api-version={AzureApiVersion}"), "application/json",
            authorization, null, query, cancellationToken).ConfigureAwait(false))
        {
            if (!IsAzureAnswer(response)) return Refused(repository, response.IsSuccessStatusCode ? HttpStatusCode.Unauthorized : response.StatusCode);
            using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            foreach (var item in Array(document.RootElement.TryGetProperty("workItems", out var value) ? value : default))
            {
                if (TryNumber(item, "id", out var number)) numbers.Add(number);
            }
        }

        if (numbers.Count == 0) return new(GitFeedStatus.Ok, repository, items);
        var read = FormattableString.Invariant($"{project}/_apis/wit/workitems?ids={string.Join(',', numbers.Select(static number => number.ToString(CultureInfo.InvariantCulture)))}&fields=System.Id,System.Title,System.CreatedDate,System.CreatedBy&api-version={AzureApiVersion}");
        using (var response = await SendAsync(HttpMethod.Get, read, "application/json", authorization, null, null, cancellationToken).ConfigureAwait(false))
        {
            if (!IsAzureAnswer(response)) return Refused(repository, response.IsSuccessStatusCode ? HttpStatusCode.Unauthorized : response.StatusCode);
            using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            foreach (var item in Array(document.RootElement.TryGetProperty("value", out var value) ? value : default))
            {
                if (!TryNumber(item, "id", out var number) || !item.TryGetProperty("fields", out var fields) || Text(fields, "System.Title") is not { } title
                    || !TryDate(fields, "System.CreatedDate", out var created)) continue;
                var author = fields.TryGetProperty("System.CreatedBy", out var by) ? by.ValueKind == JsonValueKind.Object ? Text(by, "uniqueName") ?? Text(by, "displayName") : Text(fields, "System.CreatedBy") : null;
                items.Add(new GitFeedItem(number, title, FormattableString.Invariant($"{project}/_workitems/edit/{number}"), author, created) { Trusted = true });
            }
        }

        items.Sort(static (left, right) => right.CreatedAt.CompareTo(left.CreatedAt));
        return new(GitFeedStatus.Ok, repository, items);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string accept, AuthenticationHeaderValue? authorization, string? entityTag, string? json, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(method, url);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        if (authorization is not null) message.Headers.Authorization = authorization;
        if (entityTag is not null && EntityTagHeaderValue.TryParse(entityTag, out var tag)) message.Headers.IfNoneMatch.Add(tag);
        if (json is not null) message.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await _client.SendAsync(message, cancellationToken).ConfigureAwait(false);
    }

    private static GitFeedPage Refused(GitRepositoryReference repository, HttpStatusCode status)
        => new((int)status >= 500 ? GitFeedStatus.Failed : GitFeedStatus.Refused, repository, [], Message: status switch
        {
            HttpStatusCode.Unauthorized => $"{repository.Provider.GetDisplayName()} asks to sign in.",
            HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests => $"{repository.Provider.GetDisplayName()} refused the request: no access, or too many requests.",
            HttpStatusCode.NotFound => $"{repository.Provider.GetDisplayName()} does not show {repository.FullName} to this account.",
            _ => $"{repository.Provider.GetDisplayName()} answered {(int)status}.",
        });

    private static string GitLabProjectUrl(GitRepositoryReference repository)
        => $"https://{repository.Host}/api/v4/projects/{Uri.EscapeDataString(repository.FullName)}";

    // Azure DevOps answers a sign-in page, with a success status, to a request it does not accept.
    private static bool IsAzureAnswer(HttpResponseMessage response)
        => response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NonAuthoritativeInformation
            && string.Equals(response.Content.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase);

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static int Size(GitFeedKind kind) => kind == GitFeedKind.Issues ? IssuePageSize : PullRequestPageSize;

    private static IEnumerable<JsonElement> Array(JsonElement element)
        => element.ValueKind == JsonValueKind.Array ? element.EnumerateArray().Take(PullRequestPageSize * 2) : [];

    private static string? Text(JsonElement item, string property)
        => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.String
            && element.GetString() is { Length: > 0 } text ? text : null;

    private static bool TryDate(JsonElement item, string property, out DateTimeOffset value)
    {
        value = default;
        return Text(item, property) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out value);
    }

    private static bool TryNumber(JsonElement item, string property, out int value)
    {
        value = 0;
        return item.ValueKind == JsonValueKind.Object && item.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out value) && value > 0;
    }
}
