using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CodeAlta.Plugin.Git;

/// <summary>The issues gathered by one request sequence, and whether the provider answered all of it.</summary>
internal readonly record struct GitIssueFetch(IReadOnlyList<GitIssueReferenceItem> Issues, bool Complete);

/// <summary>One query: the repository, the credentials resolved for it and the first refusal the provider answered.</summary>
internal sealed class GitIssueQuery(HttpClient client, GitRepositoryReference repository, AuthenticationHeaderValue? authorization)
{
    public GitRepositoryReference Repository { get; } = repository;

    public HttpStatusCode? FailureStatusCode { get; private set; }

    public void ReportFailure(HttpStatusCode statusCode)
        => FailureStatusCode ??= statusCode;

    public Task<HttpResponseMessage> GetAsync(string url, string accept, CancellationToken cancellationToken)
        => SendAsync(new HttpRequestMessage(HttpMethod.Get, url), accept, cancellationToken);

    public Task<HttpResponseMessage> PostJsonAsync(string url, string json, CancellationToken cancellationToken)
        => SendAsync(new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(json, Encoding.UTF8, "application/json") }, "application/json", cancellationToken);

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, string accept, CancellationToken cancellationToken)
    {
        using (message)
        {
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
            if (authorization is not null)
            {
                message.Headers.Authorization = authorization;
            }

            return await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>The issue tracker of one provider, reached through its REST API.</summary>
internal abstract class GitIssueSource
{
    /// <summary>Largest number of issues one listing or search returns.</summary>
    public const int MaximumPageSize = 100;

    /// <summary>Gets the source that serves a provider.</summary>
    public static GitIssueSource For(GitRemoteProvider provider)
        => provider switch
        {
            GitRemoteProvider.GitLab => GitLabIssueSource.Instance,
            GitRemoteProvider.AzureDevOps => AzureDevOpsIssueSource.Instance,
            _ => GitHubIssueSource.Instance,
        };

    /// <summary>Lists the most recently updated issues, open and closed.</summary>
    public abstract Task<GitIssueFetch> ListRecentAsync(GitIssueQuery query, int maximumResults, CancellationToken cancellationToken);

    /// <summary>Searches the issues for typed text, most recently updated first.</summary>
    public abstract Task<GitIssueFetch> SearchAsync(GitIssueQuery query, string text, int maximumResults, CancellationToken cancellationToken);

    /// <summary>Reads the issue with a number; empty when the number names no issue.</summary>
    public abstract Task<IReadOnlyList<GitIssueReferenceItem>> GetAsync(GitIssueQuery query, int number, CancellationToken cancellationToken);

    protected static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    // A number that names no issue is an ordinary miss, not a failed lookup.
    protected static void ReportUnlessMissing(GitIssueQuery query, HttpStatusCode statusCode)
    {
        if (statusCode is not (HttpStatusCode.NotFound or HttpStatusCode.Gone))
        {
            query.ReportFailure(statusCode);
        }
    }

    protected static bool TryReadText(JsonElement item, string property, out string value)
    {
        value = item.ValueKind == JsonValueKind.Object && item.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? string.Empty
            : string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    protected static bool TryReadDate(JsonElement item, string property, out DateTimeOffset value)
    {
        value = default;
        return TryReadText(item, property, out var text) && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out value);
    }

    protected static bool TryReadNumber(JsonElement item, string property, out int value)
    {
        value = 0;
        return item.ValueKind == JsonValueKind.Object && item.TryGetProperty(property, out var element) &&
               element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value) && value > 0;
    }
}

/// <summary>GitHub issues through api.github.com.</summary>
internal sealed class GitHubIssueSource : GitIssueSource
{
    private const string Accept = "application/vnd.github+json";
    private const int MaximumPages = 5;

    public static GitHubIssueSource Instance { get; } = new();

    public override Task<GitIssueFetch> ListRecentAsync(GitIssueQuery query, int maximumResults, CancellationToken cancellationToken)
        => FetchAsync(query, string.Empty, maximumResults, cancellationToken);

    public override Task<GitIssueFetch> SearchAsync(GitIssueQuery query, string text, int maximumResults, CancellationToken cancellationToken)
        => FetchAsync(query, text, maximumResults, cancellationToken);

    public override async Task<IReadOnlyList<GitIssueReferenceItem>> GetAsync(GitIssueQuery query, int number, CancellationToken cancellationToken)
    {
        var repository = query.Repository;
        var url = FormattableString.Invariant($"https://api.github.com/repos/{Uri.EscapeDataString(repository.Owner)}/{Uri.EscapeDataString(repository.Name)}/issues/{number}");
        using var response = await query.GetAsync(url, Accept, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            ReportUnlessMissing(query, response.StatusCode);
            return [];
        }

        using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        return TryReadIssue(document.RootElement, repository.FullName, out var issue) ? [issue] : [];
    }

    // The issue listing also returns pull requests, so it is paged until enough issues are found.
    private static async Task<GitIssueFetch> FetchAsync(GitIssueQuery query, string text, int maximumResults, CancellationToken cancellationToken)
    {
        var repository = query.Repository;
        var resultLimit = Math.Clamp(maximumResults, 1, MaximumPageSize);
        var pageSize = MaximumPageSize;
        var results = new List<GitIssueReferenceItem>(resultLimit);
        for (var page = 1; page <= MaximumPages && results.Count < resultLimit; page++)
        {
            var url = string.IsNullOrWhiteSpace(text)
                ? FormattableString.Invariant($"https://api.github.com/repos/{Uri.EscapeDataString(repository.Owner)}/{Uri.EscapeDataString(repository.Name)}/issues?state=all&sort=updated&direction=desc&per_page={pageSize}&page={page}")
                : FormattableString.Invariant($"https://api.github.com/search/issues?q={Uri.EscapeDataString($"repo:{repository.FullName} is:issue {text}")}&sort=updated&order=desc&per_page={pageSize}&page={page}");
            using var response = await query.GetAsync(url, Accept, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                query.ReportFailure(response.StatusCode);
                return new(results.ToArray(), false);
            }

            using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            var array = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array ? items : root;
            var arrayItemCount = array.ValueKind == JsonValueKind.Array ? array.GetArrayLength() : 0;
            if (arrayItemCount == 0)
            {
                break;
            }

            foreach (var item in array.EnumerateArray())
            {
                if (results.Count >= resultLimit)
                {
                    break;
                }

                if (TryReadIssue(item, repository.FullName, out var issue))
                {
                    results.Add(issue);
                }
            }

            if (arrayItemCount < pageSize)
            {
                break;
            }
        }

        return new(results.ToArray(), true);
    }

    private static bool TryReadIssue(JsonElement item, string repository, out GitIssueReferenceItem issue)
    {
        issue = default!;
        if (item.ValueKind != JsonValueKind.Object || item.TryGetProperty("pull_request", out _))
        {
            return false;
        }

        if (!TryReadNumber(item, "number", out var number) || !TryReadText(item, "title", out var title) ||
            !TryReadText(item, "html_url", out var url) || !TryReadDate(item, "updated_at", out var updatedAt))
        {
            return false;
        }

        TryReadText(item, "state", out var state);
        issue = new GitIssueReferenceItem(number, title, url, updatedAt, state, string.Equals(state, "open", StringComparison.OrdinalIgnoreCase), repository);
        return true;
    }
}

/// <summary>GitLab issues through the REST API of the instance that hosts the project.</summary>
internal sealed class GitLabIssueSource : GitIssueSource
{
    private const string Accept = "application/json";

    public static GitLabIssueSource Instance { get; } = new();

    public override Task<GitIssueFetch> ListRecentAsync(GitIssueQuery query, int maximumResults, CancellationToken cancellationToken)
        => FetchAsync(query, string.Empty, maximumResults, cancellationToken);

    public override Task<GitIssueFetch> SearchAsync(GitIssueQuery query, string text, int maximumResults, CancellationToken cancellationToken)
        => FetchAsync(query, text, maximumResults, cancellationToken);

    public override async Task<IReadOnlyList<GitIssueReferenceItem>> GetAsync(GitIssueQuery query, int number, CancellationToken cancellationToken)
    {
        var url = FormattableString.Invariant($"{ProjectUrl(query.Repository)}/issues/{number}");
        using var response = await query.GetAsync(url, Accept, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            ReportUnlessMissing(query, response.StatusCode);
            return [];
        }

        using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        return TryReadIssue(document.RootElement, query.Repository.FullName, out var issue) ? [issue] : [];
    }

    // The project is addressed by its URL-encoded path: group%2Fsubgroup%2Fproject.
    private static string ProjectUrl(GitRepositoryReference repository)
        => $"https://{repository.Host}/api/v4/projects/{Uri.EscapeDataString(repository.FullName)}";

    private static async Task<GitIssueFetch> FetchAsync(GitIssueQuery query, string text, int maximumResults, CancellationToken cancellationToken)
    {
        var resultLimit = Math.Clamp(maximumResults, 1, MaximumPageSize);
        var url = FormattableString.Invariant($"{ProjectUrl(query.Repository)}/issues?scope=all&order_by=updated_at&sort=desc&per_page={resultLimit}&page=1");
        if (!string.IsNullOrWhiteSpace(text))
        {
            url += "&search=" + Uri.EscapeDataString(text);
        }

        using var response = await query.GetAsync(url, Accept, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            query.ReportFailure(response.StatusCode);
            return new([], false);
        }

        using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var results = new List<GitIssueReferenceItem>(resultLimit);
        if (document.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (results.Count < resultLimit && TryReadIssue(item, query.Repository.FullName, out var issue))
                {
                    results.Add(issue);
                }
            }
        }

        return new(results.ToArray(), true);
    }

    private static bool TryReadIssue(JsonElement item, string repository, out GitIssueReferenceItem issue)
    {
        issue = default!;
        // `iid` is the number shown in the project (#12); `id` is unique across the whole instance.
        if (!TryReadNumber(item, "iid", out var number) || !TryReadText(item, "title", out var title) ||
            !TryReadText(item, "web_url", out var url) || !TryReadDate(item, "updated_at", out var updatedAt))
        {
            return false;
        }

        TryReadText(item, "state", out var state);
        var isOpen = string.Equals(state, "opened", StringComparison.OrdinalIgnoreCase);
        issue = new GitIssueReferenceItem(number, title, url, updatedAt, isOpen ? "open" : state, isOpen, repository);
        return true;
    }
}

/// <summary>Azure DevOps work items of the project that owns the repository, through dev.azure.com.</summary>
internal sealed class AzureDevOpsIssueSource : GitIssueSource
{
    private const string Accept = "application/json";
    private const string ApiVersion = "7.1";
    private const string Fields = "System.Id,System.Title,System.State,System.ChangedDate,System.TeamProject";

    public static AzureDevOpsIssueSource Instance { get; } = new();

    public override Task<GitIssueFetch> ListRecentAsync(GitIssueQuery query, int maximumResults, CancellationToken cancellationToken)
        => FetchAsync(query, string.Empty, maximumResults, cancellationToken);

    public override Task<GitIssueFetch> SearchAsync(GitIssueQuery query, string text, int maximumResults, CancellationToken cancellationToken)
        => FetchAsync(query, text, maximumResults, cancellationToken);

    public override async Task<IReadOnlyList<GitIssueReferenceItem>> GetAsync(GitIssueQuery query, int number, CancellationToken cancellationToken)
    {
        var url = FormattableString.Invariant($"{ProjectUrl(query.Repository)}/_apis/wit/workitems/{number}?fields={Fields}&api-version={ApiVersion}");
        using var response = await query.GetAsync(url, Accept, cancellationToken).ConfigureAwait(false);
        if (!IsAnswer(response))
        {
            ReportUnlessMissing(query, RefusalOf(response));
            return [];
        }

        using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        return TryReadWorkItem(document.RootElement, query.Repository, out var issue) ? [issue] : [];
    }

    private static string ProjectUrl(GitRepositoryReference repository)
        => $"https://{repository.Host}/{Uri.EscapeDataString(repository.Owner)}/{Uri.EscapeDataString(repository.Project ?? repository.Name)}";

    // Work items are found in two steps: a WIQL query returns their ids, then one request reads their fields.
    private static async Task<GitIssueFetch> FetchAsync(GitIssueQuery query, string text, int maximumResults, CancellationToken cancellationToken)
    {
        var repository = query.Repository;
        var resultLimit = Math.Clamp(maximumResults, 1, MaximumPageSize);
        var wiql = "SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = @project"
                   + (string.IsNullOrWhiteSpace(text) ? string.Empty : " AND [System.Title] CONTAINS '" + text.Replace("'", "''", StringComparison.Ordinal) + "'")
                   + " ORDER BY [System.ChangedDate] DESC";
        var queryUrl = FormattableString.Invariant($"{ProjectUrl(repository)}/_apis/wit/wiql?$top={resultLimit}&api-version={ApiVersion}");
        var ids = new List<int>(resultLimit);
        using (var response = await query.PostJsonAsync(queryUrl, "{\"query\":\"" + JsonEncodedText.Encode(wiql) + "\"}", cancellationToken).ConfigureAwait(false))
        {
            if (!IsAnswer(response))
            {
                query.ReportFailure(RefusalOf(response));
                return new([], false);
            }

            using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("workItems", out var workItems) && workItems.ValueKind == JsonValueKind.Array)
            {
                foreach (var workItem in workItems.EnumerateArray())
                {
                    if (ids.Count < resultLimit && TryReadNumber(workItem, "id", out var id))
                    {
                        ids.Add(id);
                    }
                }
            }
        }

        if (ids.Count == 0)
        {
            return new([], true);
        }

        // A work item deleted between the two requests is left out instead of failing the whole read.
        var itemsUrl = FormattableString.Invariant($"{ProjectUrl(repository)}/_apis/wit/workitems?ids={string.Join(',', ids)}&fields={Fields}&errorPolicy=omit&api-version={ApiVersion}");
        using var itemsResponse = await query.GetAsync(itemsUrl, Accept, cancellationToken).ConfigureAwait(false);
        if (!IsAnswer(itemsResponse))
        {
            query.ReportFailure(RefusalOf(itemsResponse));
            return new([], false);
        }

        using var itemsDocument = await ReadJsonAsync(itemsResponse, cancellationToken).ConfigureAwait(false);
        var results = new List<GitIssueReferenceItem>(ids.Count);
        if (itemsDocument.RootElement.ValueKind == JsonValueKind.Object && itemsDocument.RootElement.TryGetProperty("value", out var values) && values.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in values.EnumerateArray())
            {
                if (TryReadWorkItem(value, repository, out var issue))
                {
                    results.Add(issue);
                }
            }
        }

        return new(results.ToArray(), true);
    }

    // Azure DevOps does not answer 401 to a request it wants signed in: it answers 203 with its sign-in
    // page, or redirects to that page. Only a JSON body is an answer of the API.
    private static bool IsAnswer(HttpResponseMessage response)
        => response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NonAuthoritativeInformation &&
           string.Equals(response.Content.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase);

    private static HttpStatusCode RefusalOf(HttpResponseMessage response)
        => response.IsSuccessStatusCode ? HttpStatusCode.Unauthorized : response.StatusCode;

    private static bool TryReadWorkItem(JsonElement item, GitRepositoryReference repository, out GitIssueReferenceItem issue)
    {
        issue = default!;
        if (!TryReadNumber(item, "id", out var id) || !item.TryGetProperty("fields", out var fields) ||
            !TryReadText(fields, "System.Title", out var title) || !TryReadDate(fields, "System.ChangedDate", out var updatedAt))
        {
            return false;
        }

        // Work item ids are unique in the organization: one read by its id can belong to another project.
        var project = repository.Project ?? repository.Name;
        if (TryReadText(fields, "System.TeamProject", out var owner) && !string.Equals(owner, project, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        TryReadText(fields, "System.State", out var state);
        var url = FormattableString.Invariant($"https://{repository.Host}/{Uri.EscapeDataString(repository.Owner)}/{Uri.EscapeDataString(project)}/_workitems/edit/{id}");
        issue = new GitIssueReferenceItem(id, title, url, updatedAt, state, !IsClosedState(state), repository.FullName);
        return true;
    }

    // States are defined per process template; these are the ones the stock templates end a work item in.
    private static bool IsClosedState(string state)
        => state.Equals("Closed", StringComparison.OrdinalIgnoreCase) || state.Equals("Done", StringComparison.OrdinalIgnoreCase) ||
           state.Equals("Removed", StringComparison.OrdinalIgnoreCase) || state.Equals("Completed", StringComparison.OrdinalIgnoreCase);
}
