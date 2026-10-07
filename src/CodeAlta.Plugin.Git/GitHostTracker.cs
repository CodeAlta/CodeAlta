using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugin.Git;

/// <summary>
/// The issues and the pull requests of a repository on GitHub, GitLab, Azure DevOps or Bitbucket, read through
/// the REST API of the service with the credentials of the user.
/// </summary>
internal sealed class GitHostTracker : IIssueTracker
{
    /// <summary>The most items one listing returns.</summary>
    public const int MaximumItems = 100;

    /// <summary>The most comments read for one item.</summary>
    public const int MaximumComments = 100;

    private static readonly TrackedItemKind[] BothKinds = [TrackedItemKind.Issue, TrackedItemKind.PullRequest];
    private readonly HttpClient _client;
    private readonly GitRepositoryReference _repository;
    private readonly Func<CancellationToken, ValueTask<AuthenticationHeaderValue?>> _authorization;

    internal GitHostTracker(HttpClient client, GitRepositoryReference repository, Func<CancellationToken, ValueTask<AuthenticationHeaderValue?>> authorization)
        => (_client, _repository, _authorization) = (client, repository, authorization);

    /// <inheritdoc />
    public string Service => ServiceName(_repository.Provider);

    /// <inheritdoc />
    public string DisplayName => _repository.Provider.GetDisplayName();

    /// <inheritdoc />
    public string Location => _repository.FullName;

    /// <inheritdoc />
    public string? WebUrl => Api.WebUrl(_repository);

    /// <inheritdoc />
    public IReadOnlyList<TrackedItemKind> Kinds => BothKinds;

    private GitHostApi Api => GitHostApi.For(_repository.Provider);

    /// <summary>Gets the short name of a provider, as the application writes it in its data.</summary>
    internal static string ServiceName(GitRemoteProvider provider)
        => provider switch
        {
            GitRemoteProvider.GitLab => "gitlab",
            GitRemoteProvider.AzureDevOps => "azure_devops",
            GitRemoteProvider.Bitbucket => "bitbucket",
            _ => "github",
        };

    /// <inheritdoc />
    public async ValueTask<TrackedItemPage> ListAsync(TrackedItemQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var limit = Math.Clamp(query.Limit, 1, MaximumItems);
        var text = string.IsNullOrWhiteSpace(query.Text) ? null : query.Text.Trim();
        // A merge is what happens to a pull request: no issue was ever merged.
        if (query.Kind == TrackedItemKind.Issue && query.Filter == TrackedItemFilter.Merged) return new([]);
        GitIssueQuery? request = null;
        try
        {
            request = new GitIssueQuery(_client, _repository, await _authorization(cancellationToken).ConfigureAwait(false));
            var items = await Api.ListAsync(request, query.Kind, query.Filter, text, limit + 1, cancellationToken).ConfigureAwait(false);
            var page = new TrackedItemPage(items.Count > limit ? [.. items.Take(limit)] : items) { More = items.Count > limit };
            return request.FailureStatusCode is { } refused ? Refused(page, refused, request.Anonymous) : page;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException
            || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new([]) { Problem = exception is OperationCanceledException ? $"{DisplayName} did not answer in time." : $"{DisplayName} could not be reached." };
        }
    }

    /// <inheritdoc />
    public async ValueTask<TrackedItemDetail?> ReadAsync(TrackedItemKind kind, string id, CancellationToken cancellationToken)
    {
        if (!int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0) return null;
        try
        {
            var request = new GitIssueQuery(_client, _repository, await _authorization(cancellationToken).ConfigureAwait(false));
            return await Api.ReadAsync(request, kind, number, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException
            || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private TrackedItemPage Refused(TrackedItemPage page, HttpStatusCode status, bool anonymous)
    {
        // A private repository answers "not found" to someone it does not know.
        var signIn = status is HttpStatusCode.Unauthorized || anonymous && status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound;
        var problem = status switch
        {
            HttpStatusCode.Unauthorized => $"{DisplayName} asks to sign in.",
            HttpStatusCode.Forbidden => anonymous ? $"{DisplayName} refused the request: sign in to read this repository." : $"{DisplayName} refused the request.",
            HttpStatusCode.NotFound or HttpStatusCode.Gone => anonymous ? $"{DisplayName} does not show this repository to a visitor: sign in." : $"{DisplayName} has nothing of this kind for this repository.",
            HttpStatusCode.TooManyRequests => $"{DisplayName} is asked too often. Try again in a moment.",
            _ => string.Create(CultureInfo.InvariantCulture, $"{DisplayName} answered {(int)status}."),
        };
        return page with { Problem = problem, NeedsSignIn = signIn };
    }
}

/// <summary>The REST API of one hosting service, as far as issues and pull requests go.</summary>
internal abstract class GitHostApi
{
    public static GitHostApi For(GitRemoteProvider provider)
        => provider switch
        {
            GitRemoteProvider.GitLab => GitLabApi.Instance,
            GitRemoteProvider.AzureDevOps => AzureDevOpsApi.Instance,
            GitRemoteProvider.Bitbucket => BitbucketApi.Instance,
            _ => GitHubApi.Instance,
        };

    /// <summary>Gets the page of the repository on the web.</summary>
    public abstract string WebUrl(GitRepositoryReference repository);

    /// <summary>Lists up to <paramref name="limit"/> items, the most recently updated first. A refusal is reported on the query.</summary>
    public abstract Task<IReadOnlyList<TrackedItem>> ListAsync(GitIssueQuery query, TrackedItemKind kind, TrackedItemFilter filter, string? text, int limit, CancellationToken cancellationToken);

    /// <summary>Reads one item with its description and its comments; null when there is none.</summary>
    public abstract Task<TrackedItemDetail?> ReadAsync(GitIssueQuery query, TrackedItemKind kind, int number, CancellationToken cancellationToken);

    protected static async Task<JsonDocument?> GetJsonAsync(GitIssueQuery query, string url, string accept, CancellationToken cancellationToken, bool report = true)
    {
        using var response = await query.GetAsync(url, accept, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            if (report) query.ReportFailure(response.StatusCode);
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    protected static IEnumerable<JsonElement> Items(JsonElement element, string? property = null)
    {
        if (property is not null) element = element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var inner) ? inner : default;
        return element.ValueKind == JsonValueKind.Array ? element.EnumerateArray() : [];
    }

    protected static JsonElement At(JsonElement element, params ReadOnlySpan<string> path)
    {
        foreach (var name in path)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out element)) return default;
        }

        return element;
    }

    protected static string? Text(JsonElement element, params ReadOnlySpan<string> path)
    {
        var value = At(element, path);
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    protected static int? Number(JsonElement element, params ReadOnlySpan<string> path)
    {
        var value = At(element, path);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;
    }

    protected static bool Flag(JsonElement element, params ReadOnlySpan<string> path) => At(element, path).ValueKind == JsonValueKind.True;

    protected static DateTimeOffset? Date(JsonElement element, params ReadOnlySpan<string> path)
        => Text(element, path) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : null;

    protected static bool IsHttps(string? url) => url is not null && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    protected static string Escape(string value) => Uri.EscapeDataString(value);

    protected static string? Branch(string? reference)
        => reference is null ? null : reference.StartsWith("refs/heads/", StringComparison.Ordinal) ? reference["refs/heads/".Length..] : reference;
}

/// <summary>GitHub through api.github.com.</summary>
internal sealed class GitHubApi : GitHostApi
{
    private const string Accept = "application/vnd.github+json";

    public static GitHubApi Instance { get; } = new();

    public override string WebUrl(GitRepositoryReference repository) => $"https://{repository.Host}/{repository.Owner}/{repository.Name}";

    private static string Root(GitRepositoryReference repository) => $"https://api.github.com/repos/{Escape(repository.Owner)}/{Escape(repository.Name)}";

    public override async Task<IReadOnlyList<TrackedItem>> ListAsync(GitIssueQuery query, TrackedItemKind kind, TrackedItemFilter filter, string? text, int limit, CancellationToken cancellationToken)
    {
        var pulls = kind == TrackedItemKind.PullRequest;
        var results = new List<TrackedItem>(limit);
        // The search knows words and what "merged" means; the listings are cheaper and are what an anonymous visitor may ask most.
        if (text is not null || pulls && filter is TrackedItemFilter.Closed or TrackedItemFilter.Merged)
        {
            var state = filter switch
            {
                TrackedItemFilter.Open => " is:open",
                TrackedItemFilter.Closed => pulls ? " is:closed is:unmerged" : " is:closed",
                TrackedItemFilter.Merged => " is:merged",
                _ => string.Empty,
            };
            var search = $"repo:{query.Repository.FullName} {(pulls ? "is:pr" : "is:issue")}{state}{(text is null ? string.Empty : " " + text)}";
            using var found = await GetJsonAsync(query, FormattableString.Invariant($"https://api.github.com/search/issues?q={Escape(search)}&sort=updated&order=desc&per_page={Math.Min(limit, 100)}"), Accept, cancellationToken).ConfigureAwait(false);
            if (found is not null) results.AddRange(Items(found.RootElement, "items").Select(Read).OfType<TrackedItem>().Where(item => item.Kind == kind));
            return results;
        }

        var listed = filter switch { TrackedItemFilter.Open => "open", TrackedItemFilter.Closed => "closed", _ => "all" };
        // The listing of issues holds the pull requests too: more pages are read until enough issues are found.
        for (var page = 1; page <= (pulls ? 1 : 3) && results.Count < limit; page++)
        {
            var url = FormattableString.Invariant($"{Root(query.Repository)}/{(pulls ? "pulls" : "issues")}?state={listed}&sort=updated&direction=desc&per_page=100&page={page}");
            using var document = await GetJsonAsync(query, url, Accept, cancellationToken).ConfigureAwait(false);
            if (document is null) break;
            var count = 0;
            foreach (var element in Items(document.RootElement))
            {
                count++;
                if (Read(element) is { } item && item.Kind == kind && results.Count < limit) results.Add(item);
            }

            if (count < 100) break;
        }

        return results;
    }

    public override async Task<TrackedItemDetail?> ReadAsync(GitIssueQuery query, TrackedItemKind kind, int number, CancellationToken cancellationToken)
    {
        var root = Root(query.Repository);
        using var document = await GetJsonAsync(query, FormattableString.Invariant($"{root}/{(kind == TrackedItemKind.PullRequest ? "pulls" : "issues")}/{number}"), Accept, cancellationToken).ConfigureAwait(false);
        if (document is null || Read(document.RootElement) is not { } item || item.Kind != kind) return null;
        var comments = new List<TrackedComment>();
        using var listed = await GetJsonAsync(query, FormattableString.Invariant($"{root}/issues/{number}/comments?per_page={GitHostTracker.MaximumComments}"), Accept, cancellationToken, report: false).ConfigureAwait(false);
        if (listed is not null)
        {
            comments.AddRange(Items(listed.RootElement).Select(static comment => (Body: Text(comment, "body"), Comment: comment)).Where(static entry => entry.Body is not null)
                .Select(static entry => new TrackedComment(Text(entry.Comment, "user", "login"), Date(entry.Comment, "created_at"), entry.Body!)));
        }

        return new(item, Text(document.RootElement, "body") ?? string.Empty, comments) { MoreComments = (item.CommentCount ?? 0) > comments.Count };
    }

    private static TrackedItem? Read(JsonElement element)
    {
        if (Number(element, "number") is not { } number || Text(element, "title") is not { } title || Text(element, "html_url") is not { } url || !IsHttps(url)) return null;
        var pull = At(element, "pull_request").ValueKind == JsonValueKind.Object || At(element, "head").ValueKind == JsonValueKind.Object;
        var merged = Text(element, "merged_at") is not null || Text(element, "pull_request", "merged_at") is not null;
        var open = string.Equals(Text(element, "state"), "open", StringComparison.OrdinalIgnoreCase);
        return new(pull ? TrackedItemKind.PullRequest : TrackedItemKind.Issue, number.ToString(CultureInfo.InvariantCulture), title, url,
            merged ? TrackedItemState.Merged : !open ? TrackedItemState.Closed : pull && Flag(element, "draft") ? TrackedItemState.Draft : TrackedItemState.Open)
        {
            Type = Text(element, "type", "name"),
            Author = Text(element, "user", "login"),
            Assignees = [.. Items(element, "assignees").Select(static user => Text(user, "login")).OfType<string>()],
            Labels = [.. Items(element, "labels").Select(static label => Text(label, "name") is { } name ? new TrackedLabel(name, Text(label, "color")) : null).OfType<TrackedLabel>()],
            CreatedAt = Date(element, "created_at"),
            UpdatedAt = Date(element, "updated_at"),
            CommentCount = Number(element, "comments"),
            SourceBranch = Text(element, "head", "ref"),
            TargetBranch = Text(element, "base", "ref"),
        };
    }
}

/// <summary>GitLab through the API of its instance.</summary>
internal sealed class GitLabApi : GitHostApi
{
    private const string Accept = "application/json";

    public static GitLabApi Instance { get; } = new();

    public override string WebUrl(GitRepositoryReference repository) => $"https://{repository.Host}/{repository.FullName}";

    private static string Root(GitRepositoryReference repository) => $"https://{repository.Host}/api/v4/projects/{Escape(repository.FullName)}";

    public override async Task<IReadOnlyList<TrackedItem>> ListAsync(GitIssueQuery query, TrackedItemKind kind, TrackedItemFilter filter, string? text, int limit, CancellationToken cancellationToken)
    {
        var state = filter switch { TrackedItemFilter.Open => "&state=opened", TrackedItemFilter.Closed => "&state=closed", TrackedItemFilter.Merged => "&state=merged", _ => string.Empty };
        var url = FormattableString.Invariant($"{Root(query.Repository)}/{Path(kind)}?scope=all{state}&order_by=updated_at&sort=desc&per_page={Math.Min(limit, 100)}&page=1")
            + (text is null ? string.Empty : "&search=" + Escape(text));
        using var document = await GetJsonAsync(query, url, Accept, cancellationToken).ConfigureAwait(false);
        return document is null ? [] : [.. Items(document.RootElement).Select(element => Read(element, kind)).OfType<TrackedItem>()];
    }

    public override async Task<TrackedItemDetail?> ReadAsync(GitIssueQuery query, TrackedItemKind kind, int number, CancellationToken cancellationToken)
    {
        var root = FormattableString.Invariant($"{Root(query.Repository)}/{Path(kind)}/{number}");
        using var document = await GetJsonAsync(query, root, Accept, cancellationToken).ConfigureAwait(false);
        if (document is null || Read(document.RootElement, kind) is not { } item) return null;
        var comments = new List<TrackedComment>();
        using var notes = await GetJsonAsync(query, FormattableString.Invariant($"{root}/notes?sort=asc&order_by=created_at&per_page={GitHostTracker.MaximumComments}"), Accept, cancellationToken, report: false).ConfigureAwait(false);
        if (notes is not null)
        {
            // What GitLab writes itself ("changed the description") is not a comment.
            comments.AddRange(Items(notes.RootElement).Where(static note => !Flag(note, "system") && Text(note, "body") is not null)
                .Select(static note => new TrackedComment(Text(note, "author", "username"), Date(note, "created_at"), Text(note, "body")!)));
        }

        return new(item, Text(document.RootElement, "description") ?? string.Empty, comments) { MoreComments = (item.CommentCount ?? 0) > comments.Count };
    }

    private static string Path(TrackedItemKind kind) => kind == TrackedItemKind.PullRequest ? "merge_requests" : "issues";

    private static TrackedItem? Read(JsonElement element, TrackedItemKind kind)
    {
        if (Number(element, "iid") is not { } number || Text(element, "title") is not { } title || Text(element, "web_url") is not { } url || !IsHttps(url)) return null;
        var state = Text(element, "state");
        return new(kind, number.ToString(CultureInfo.InvariantCulture), title, url,
            state == "merged" ? TrackedItemState.Merged : state is not ("opened" or "locked") ? TrackedItemState.Closed
            : kind == TrackedItemKind.PullRequest && (Flag(element, "draft") || Flag(element, "work_in_progress")) ? TrackedItemState.Draft : TrackedItemState.Open)
        {
            Type = kind == TrackedItemKind.Issue && Text(element, "issue_type") is { } type && type != "issue" ? type : null,
            Author = Text(element, "author", "username"),
            Assignees = [.. Items(element, "assignees").Select(static user => Text(user, "username")).OfType<string>()],
            Labels = [.. Items(element, "labels").Select(static label => label.ValueKind == JsonValueKind.String && label.GetString() is { Length: > 0 } name ? new TrackedLabel(name) : null).OfType<TrackedLabel>()],
            CreatedAt = Date(element, "created_at"),
            UpdatedAt = Date(element, "updated_at"),
            CommentCount = Number(element, "user_notes_count"),
            SourceBranch = Text(element, "source_branch"),
            TargetBranch = Text(element, "target_branch"),
        };
    }
}

/// <summary>Azure DevOps Services: the work items of the project and the pull requests of the repository.</summary>
internal sealed class AzureDevOpsApi : GitHostApi
{
    private const string Accept = "application/json";
    private const string ApiVersion = "7.1";
    private const string Closed = "'Closed', 'Done', 'Removed', 'Completed'";
    private const string Fields = "System.Id,System.Title,System.State,System.WorkItemType,System.AssignedTo,System.CreatedBy,System.CreatedDate,System.ChangedDate,System.Tags,System.CommentCount,System.TeamProject,Microsoft.VSTS.Common.Priority";

    public static AzureDevOpsApi Instance { get; } = new();

    public override string WebUrl(GitRepositoryReference repository) => $"{Project(repository)}/_git/{Escape(repository.Name)}";

    private static string Project(GitRepositoryReference repository)
        => $"https://{repository.Host}/{Escape(repository.Owner)}/{Escape(repository.Project ?? repository.Name)}";

    public override async Task<IReadOnlyList<TrackedItem>> ListAsync(GitIssueQuery query, TrackedItemKind kind, TrackedItemFilter filter, string? text, int limit, CancellationToken cancellationToken)
    {
        var repository = query.Repository;
        if (kind == TrackedItemKind.PullRequest)
        {
            var status = filter switch { TrackedItemFilter.Open => "active", TrackedItemFilter.Closed => "abandoned", TrackedItemFilter.Merged => "completed", _ => "all" };
            // The service has no search of pull requests: more are read, and the words are looked for in their titles.
            var url = FormattableString.Invariant($"{Project(repository)}/_apis/git/repositories/{Escape(repository.Name)}/pullrequests?searchCriteria.status={status}&$top={(text is null ? limit : 100)}&api-version={ApiVersion}");
            using var document = await GetAnswerAsync(query, url, cancellationToken).ConfigureAwait(false);
            if (document is null) return [];
            var pulls = Items(document.RootElement, "value").Select(element => ReadPull(element, repository)).OfType<TrackedItem>();
            if (text is not null) pulls = pulls.Where(item => item.Title.Contains(text, StringComparison.OrdinalIgnoreCase) || item.Id == text.TrimStart('#', '!'));
            return [.. pulls.OrderByDescending(static item => item.UpdatedAt).Take(limit)];
        }

        var wiql = "SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = @project"
            + filter switch { TrackedItemFilter.Open => $" AND [System.State] NOT IN ({Closed})", TrackedItemFilter.Closed => $" AND [System.State] IN ({Closed})", _ => string.Empty }
            + (text is null ? string.Empty : " AND [System.Title] CONTAINS '" + text.Replace("'", "''", StringComparison.Ordinal) + "'")
            + " ORDER BY [System.ChangedDate] DESC";
        var ids = new List<int>(limit);
        using (var response = await query.PostJsonAsync(FormattableString.Invariant($"{Project(repository)}/_apis/wit/wiql?$top={Math.Min(limit, 200)}&api-version={ApiVersion}"),
            "{\"query\":\"" + JsonEncodedText.Encode(wiql) + "\"}", cancellationToken).ConfigureAwait(false))
        {
            if (!IsAnswer(response))
            {
                query.ReportFailure(RefusalOf(response));
                return [];
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var found = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            ids.AddRange(Items(found.RootElement, "workItems").Select(static item => Number(item, "id")).OfType<int>());
        }

        if (ids.Count == 0) return [];
        using var items = await GetAnswerAsync(query, FormattableString.Invariant($"{Project(repository)}/_apis/wit/workitems?ids={string.Join(',', ids)}&fields={Fields}&errorPolicy=omit&api-version={ApiVersion}"), cancellationToken).ConfigureAwait(false);
        if (items is null) return [];
        var read = Items(items.RootElement, "value").Select(element => ReadWorkItem(element, repository)).OfType<TrackedItem>().ToDictionary(static item => item.Id);
        // The fields come in the order of the service; the query gave the order that was asked.
        return [.. ids.Select(id => read.GetValueOrDefault(id.ToString(CultureInfo.InvariantCulture))).OfType<TrackedItem>()];
    }

    public override async Task<TrackedItemDetail?> ReadAsync(GitIssueQuery query, TrackedItemKind kind, int number, CancellationToken cancellationToken)
    {
        var repository = query.Repository;
        var comments = new List<TrackedComment>();
        if (kind == TrackedItemKind.PullRequest)
        {
            var root = FormattableString.Invariant($"{Project(repository)}/_apis/git/repositories/{Escape(repository.Name)}/pullrequests/{number}");
            using var document = await GetAnswerAsync(query, $"{root}?api-version={ApiVersion}", cancellationToken).ConfigureAwait(false);
            if (document is null || ReadPull(document.RootElement, repository) is not { } pull) return null;
            using var threads = await GetAnswerAsync(query, $"{root}/threads?api-version={ApiVersion}", cancellationToken, report: false).ConfigureAwait(false);
            if (threads is not null)
            {
                // A thread also holds what the service notes itself (a vote, a push): only what people wrote is kept.
                comments.AddRange(Items(threads.RootElement, "value").SelectMany(static thread => Items(thread, "comments"))
                    .Where(static comment => Text(comment, "commentType") == "text" && !Flag(comment, "isDeleted") && Text(comment, "content") is not null)
                    .Select(static comment => new TrackedComment(Text(comment, "author", "displayName"), Date(comment, "publishedDate"), Text(comment, "content")!))
                    .OrderBy(static comment => comment.CreatedAt));
            }

            var kept = comments.Count > GitHostTracker.MaximumComments;
            return new(pull, Text(document.RootElement, "description") ?? string.Empty, kept ? [.. comments.Take(GitHostTracker.MaximumComments)] : comments) { MoreComments = kept };
        }

        var itemUrl = FormattableString.Invariant($"{Project(repository)}/_apis/wit/workitems/{number}?fields={Fields},System.Description&api-version={ApiVersion}");
        using var item = await GetAnswerAsync(query, itemUrl, cancellationToken).ConfigureAwait(false);
        if (item is null || ReadWorkItem(item.RootElement, repository) is not { } work) return null;
        using var listed = await GetAnswerAsync(query, FormattableString.Invariant($"{Project(repository)}/_apis/wit/workItems/{number}/comments?$top={GitHostTracker.MaximumComments}&order=asc&api-version=7.1-preview.4"), cancellationToken, report: false).ConfigureAwait(false);
        if (listed is not null)
        {
            comments.AddRange(Items(listed.RootElement, "comments").Where(static comment => Text(comment, "text") is not null)
                .Select(static comment => new TrackedComment(Text(comment, "createdBy", "displayName"), Date(comment, "createdDate"), Text(comment, "text")!))
                .OrderBy(static comment => comment.CreatedAt));
        }

        return new(work, Text(item.RootElement, "fields", "System.Description") ?? string.Empty, comments) { MoreComments = (work.CommentCount ?? 0) > comments.Count };
    }

    private static async Task<JsonDocument?> GetAnswerAsync(GitIssueQuery query, string url, CancellationToken cancellationToken, bool report = true)
    {
        using var response = await query.GetAsync(url, Accept, cancellationToken).ConfigureAwait(false);
        if (!IsAnswer(response))
        {
            if (report) query.ReportFailure(RefusalOf(response));
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    // Azure DevOps does not answer 401 to a request it wants signed in: it answers 203 with its sign-in page, or
    // redirects to that page. Only a JSON body is an answer of the API.
    private static bool IsAnswer(HttpResponseMessage response)
        => response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NonAuthoritativeInformation &&
           string.Equals(response.Content.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase);

    private static HttpStatusCode RefusalOf(HttpResponseMessage response) => response.IsSuccessStatusCode ? HttpStatusCode.Unauthorized : response.StatusCode;

    private static TrackedItem? ReadWorkItem(JsonElement element, GitRepositoryReference repository)
    {
        var fields = At(element, "fields");
        if (Number(element, "id") is not { } id || Text(fields, "System.Title") is not { } title) return null;
        // Work item ids are unique in the organization: one read by its id can belong to another project.
        var project = repository.Project ?? repository.Name;
        if (Text(fields, "System.TeamProject") is { } owner && !string.Equals(owner, project, StringComparison.OrdinalIgnoreCase)) return null;
        var state = Text(fields, "System.State");
        var closed = state is not null && Closed.Contains("'" + state + "'", StringComparison.OrdinalIgnoreCase);
        return new(TrackedItemKind.Issue, id.ToString(CultureInfo.InvariantCulture), title,
            FormattableString.Invariant($"https://{repository.Host}/{Escape(repository.Owner)}/{Escape(project)}/_workitems/edit/{id}"), closed ? TrackedItemState.Closed : TrackedItemState.Open)
        {
            StateText = state,
            Type = Text(fields, "System.WorkItemType"),
            Priority = Number(fields, "Microsoft.VSTS.Common.Priority")?.ToString(CultureInfo.InvariantCulture),
            Author = Text(fields, "System.CreatedBy", "displayName") ?? Text(fields, "System.CreatedBy"),
            Assignees = (Text(fields, "System.AssignedTo", "displayName") ?? Text(fields, "System.AssignedTo")) is { } assigned ? [assigned] : [],
            Labels = [.. (Text(fields, "System.Tags") ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(static tag => new TrackedLabel(tag))],
            CreatedAt = Date(fields, "System.CreatedDate"),
            UpdatedAt = Date(fields, "System.ChangedDate"),
            CommentCount = Number(fields, "System.CommentCount"),
        };
    }

    private static TrackedItem? ReadPull(JsonElement element, GitRepositoryReference repository)
    {
        if (Number(element, "pullRequestId") is not { } id || Text(element, "title") is not { } title) return null;
        var status = Text(element, "status");
        return new(TrackedItemKind.PullRequest, id.ToString(CultureInfo.InvariantCulture), title, FormattableString.Invariant($"{Instance.WebUrl(repository)}/pullrequest/{id}"),
            status == "completed" ? TrackedItemState.Merged : status == "abandoned" ? TrackedItemState.Closed : Flag(element, "isDraft") ? TrackedItemState.Draft : TrackedItemState.Open)
        {
            Author = Text(element, "createdBy", "displayName"),
            Assignees = [.. Items(element, "reviewers").Where(static reviewer => !Flag(reviewer, "isContainer")).Select(static reviewer => Text(reviewer, "displayName")).OfType<string>()],
            Labels = [.. Items(element, "labels").Select(static label => Text(label, "name") is { } name ? new TrackedLabel(name) : null).OfType<TrackedLabel>()],
            CreatedAt = Date(element, "creationDate"),
            UpdatedAt = Date(element, "closedDate") ?? Date(element, "creationDate"),
            SourceBranch = Branch(Text(element, "sourceRefName")),
            TargetBranch = Branch(Text(element, "targetRefName")),
        };
    }
}

/// <summary>Bitbucket Cloud through api.bitbucket.org.</summary>
internal sealed class BitbucketApi : GitHostApi
{
    private const string Accept = "application/json";

    public static BitbucketApi Instance { get; } = new();

    public override string WebUrl(GitRepositoryReference repository) => $"https://{repository.Host}/{repository.Owner}/{repository.Name}";

    private static string Root(GitRepositoryReference repository) => $"https://api.bitbucket.org/2.0/repositories/{Escape(repository.Owner)}/{Escape(repository.Name)}";

    public override async Task<IReadOnlyList<TrackedItem>> ListAsync(GitIssueQuery query, TrackedItemKind kind, TrackedItemFilter filter, string? text, int limit, CancellationToken cancellationToken)
    {
        var pulls = kind == TrackedItemKind.PullRequest;
        var state = pulls
            ? filter switch
            {
                TrackedItemFilter.Open => "state = \"OPEN\"",
                TrackedItemFilter.Closed => "(state = \"DECLINED\" OR state = \"SUPERSEDED\")",
                TrackedItemFilter.Merged => "state = \"MERGED\"",
                _ => "(state = \"OPEN\" OR state = \"MERGED\" OR state = \"DECLINED\" OR state = \"SUPERSEDED\")",
            }
            : filter switch
            {
                TrackedItemFilter.Open => "(state = \"new\" OR state = \"open\" OR state = \"on hold\")",
                TrackedItemFilter.Closed => "(state != \"new\" AND state != \"open\" AND state != \"on hold\")",
                _ => null,
            };
        var words = text is null ? null : "title ~ \"" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
        var where = state is not null && words is not null ? state + " AND " + words : state ?? words;
        var url = FormattableString.Invariant($"{Root(query.Repository)}/{(pulls ? "pullrequests" : "issues")}?sort=-updated_on&pagelen={Math.Min(limit, 50)}") + (where is null ? string.Empty : "&q=" + Escape(where));
        using var document = await GetJsonAsync(query, url, Accept, cancellationToken).ConfigureAwait(false);
        return document is null ? [] : [.. Items(document.RootElement, "values").Select(element => Read(element, kind)).OfType<TrackedItem>()];
    }

    public override async Task<TrackedItemDetail?> ReadAsync(GitIssueQuery query, TrackedItemKind kind, int number, CancellationToken cancellationToken)
    {
        var root = FormattableString.Invariant($"{Root(query.Repository)}/{(kind == TrackedItemKind.PullRequest ? "pullrequests" : "issues")}/{number}");
        using var document = await GetJsonAsync(query, root, Accept, cancellationToken).ConfigureAwait(false);
        if (document is null || Read(document.RootElement, kind) is not { } item) return null;
        var comments = new List<TrackedComment>();
        using var listed = await GetJsonAsync(query, FormattableString.Invariant($"{root}/comments?pagelen={GitHostTracker.MaximumComments}"), Accept, cancellationToken, report: false).ConfigureAwait(false);
        if (listed is not null)
        {
            comments.AddRange(Items(listed.RootElement, "values").Where(static comment => !Flag(comment, "deleted") && Text(comment, "content", "raw") is not null)
                .Select(static comment => new TrackedComment(Text(comment, "user", "display_name"), Date(comment, "created_on"), Text(comment, "content", "raw")!))
                .OrderBy(static comment => comment.CreatedAt));
        }

        var body = Text(document.RootElement, "content", "raw") ?? Text(document.RootElement, "summary", "raw") ?? Text(document.RootElement, "description") ?? string.Empty;
        return new(item, body, comments) { MoreComments = Text(listed?.RootElement ?? default, "next") is not null };
    }

    private static TrackedItem? Read(JsonElement element, TrackedItemKind kind)
    {
        if (Number(element, "id") is not { } id || Text(element, "title") is not { } title || Text(element, "links", "html", "href") is not { } url || !IsHttps(url)) return null;
        var state = Text(element, "state") ?? string.Empty;
        var pull = kind == TrackedItemKind.PullRequest;
        var open = pull ? state == "OPEN" : state is "new" or "open" or "on hold";
        return new(kind, id.ToString(CultureInfo.InvariantCulture), title, url,
            pull && state == "MERGED" ? TrackedItemState.Merged : !open ? TrackedItemState.Closed : pull && Flag(element, "draft") ? TrackedItemState.Draft : TrackedItemState.Open)
        {
            StateText = pull || state is "new" or "open" or "closed" ? null : state,
            Type = pull ? null : Text(element, "kind"),
            Priority = pull ? null : Text(element, "priority"),
            Author = Text(element, pull ? "author" : "reporter", "display_name"),
            Assignees = Text(element, "assignee", "display_name") is { } assignee ? [assignee] : [],
            CreatedAt = Date(element, "created_on"),
            UpdatedAt = Date(element, "updated_on"),
            CommentCount = Number(element, "comment_count"),
            SourceBranch = Text(element, "source", "branch", "name"),
            TargetBranch = Text(element, "destination", "branch", "name"),
        };
    }
}
