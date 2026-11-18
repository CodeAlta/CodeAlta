using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace CodeAlta.Plugin.Git;

/// <summary>One page of issues and pull requests, and what asks the provider for the next one; null when there is none.</summary>
internal readonly record struct GitReferenceFetch(IReadOnlyList<GitIssueReferenceItem> Items, string? NextCursor);

/// <summary>
/// The issues and the pull requests of a GitHub repository for a prompt picker, through the GraphQL API: one request
/// answers both kinds in one order, with only the fields a row shows, and a cursor for the page that follows. The REST
/// listings answer every field of every item (hundreds of kilobytes a page) and need a request for each kind.
/// </summary>
/// <remarks>GitHub answers GraphQL only to a signed-in caller: a visitor is served by the REST listings.</remarks>
internal static class GitHubReferenceSearch
{
    private const string Endpoint = "https://api.github.com/graphql";
    private const string Fields = "number title url state updatedAt";
    private const string Node = "__typename ... on Issue{" + Fields + "} ... on PullRequest{" + Fields + " isDraft}";

    private const string SearchDocument =
        "query($q:String!,$first:Int!,$after:String){search(type:ISSUE,query:$q,first:$first,after:$after){pageInfo{hasNextPage endCursor} nodes{" + Node + "}}}";

    private const string ExactDocument =
        "query($owner:String!,$name:String!,$number:Int!){repository(owner:$owner,name:$name){issueOrPullRequest(number:$number){" + Node + "}}}";

    /// <summary>
    /// Reads one page of the issues and the pull requests of the repository, the most recently updated first.
    /// </summary>
    /// <param name="query">The repository and its credentials; a refusal is reported on it.</param>
    /// <param name="words">The words and qualifiers of a GitHub search the items must match; null for every item.</param>
    /// <param name="includeClosed">Whether closed and merged items are listed beside the open ones.</param>
    /// <param name="first">The size of the page, 1 to 100.</param>
    /// <param name="after">The cursor of the page before, as an earlier call returned it; null for the first page.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The page, or null when GitHub refused the request.</returns>
    public static async Task<GitReferenceFetch?> SearchAsync(GitIssueQuery query, string? words, bool includeClosed, int first, string? after, CancellationToken cancellationToken)
    {
        var search = new StringBuilder("repo:").Append(query.Repository.FullName);
        // What the user wrote about the state is not contradicted.
        if (!includeClosed && !NamesState(words)) search.Append(" is:open");
        if (!string.IsNullOrWhiteSpace(words)) search.Append(' ').Append(words.Trim());
        search.Append(" sort:updated-desc");
        using var document = await PostAsync(query, SearchDocument, writer =>
        {
            writer.WriteString("q", search.ToString());
            writer.WriteNumber("first", Math.Clamp(first, 1, GitIssueSource.MaximumPageSize));
            if (after is not null) writer.WriteString("after", after);
        }, cancellationToken).ConfigureAwait(false);
        if (document is null) return null;
        if (!TryGet(document.RootElement, out var found, "data", "search") || found.ValueKind != JsonValueKind.Object)
        {
            query.ReportFailure(RefusalOf(document.RootElement));
            return null;
        }

        var items = new List<GitIssueReferenceItem>();
        if (found.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array)
        {
            foreach (var node in nodes.EnumerateArray())
            {
                if (Read(node, query.Repository.FullName) is { } item) items.Add(item);
            }
        }

        var next = TryGet(found, out var more, "pageInfo", "hasNextPage") && more.ValueKind == JsonValueKind.True
                   && TryGet(found, out var cursor, "pageInfo", "endCursor") && cursor.ValueKind == JsonValueKind.String
            ? cursor.GetString()
            : null;
        return new(items, next);
    }

    /// <summary>Reads the issue or the pull request with a number; null when the number names none, or GitHub refused.</summary>
    public static async Task<GitIssueReferenceItem?> GetAsync(GitIssueQuery query, int number, CancellationToken cancellationToken)
    {
        using var document = await PostAsync(query, ExactDocument, writer =>
        {
            writer.WriteString("owner", query.Repository.Owner);
            writer.WriteString("name", query.Repository.Name);
            writer.WriteNumber("number", number);
        }, cancellationToken).ConfigureAwait(false);
        return document is not null && TryGet(document.RootElement, out var node, "data", "repository", "issueOrPullRequest")
            ? Read(node, query.Repository.FullName)
            : null;
    }

    private static bool NamesState(string? words)
        => words is not null && (words.Contains("is:", StringComparison.OrdinalIgnoreCase) || words.Contains("state:", StringComparison.OrdinalIgnoreCase));

    private static async Task<JsonDocument?> PostAsync(GitIssueQuery query, string document, Action<Utf8JsonWriter> variables, CancellationToken cancellationToken)
    {
        using var body = new MemoryStream();
        using (var writer = new Utf8JsonWriter(body))
        {
            writer.WriteStartObject();
            writer.WriteString("query", document);
            writer.WriteStartObject("variables");
            variables(writer);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        using var response = await query.PostJsonAsync(Endpoint, Encoding.UTF8.GetString(body.GetBuffer(), 0, (int)body.Length), cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            query.ReportFailure(response.StatusCode);
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    // GraphQL answers 200 to a request it refuses, with the reason in `errors`: a limit that passes, or a search it cannot read.
    private static HttpStatusCode RefusalOf(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
        {
            foreach (var error in errors.EnumerateArray())
            {
                if (TryGet(error, out var type, "type") && type.ValueKind == JsonValueKind.String && type.ValueEquals("RATE_LIMITED")) return HttpStatusCode.TooManyRequests;
            }
        }

        return HttpStatusCode.UnprocessableEntity;
    }

    // A node is null for an item the caller may not read, and empty for a kind the search was not asked about.
    private static GitIssueReferenceItem? Read(JsonElement node, string repository)
    {
        if (node.ValueKind != JsonValueKind.Object
            || !node.TryGetProperty("number", out var number) || number.ValueKind != JsonValueKind.Number || !number.TryGetInt32(out var value) || value <= 0
            || Text(node, "title") is not { } title || Text(node, "url") is not { } url
            || Text(node, "updatedAt") is not { } updated || !DateTimeOffset.TryParse(updated, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var updatedAt))
        {
            return null;
        }

        var state = Text(node, "state") ?? string.Empty;
        var open = string.Equals(state, "OPEN", StringComparison.OrdinalIgnoreCase);
        var pull = Text(node, "__typename") == "PullRequest";
        var draft = pull && open && node.TryGetProperty("isDraft", out var isDraft) && isDraft.ValueKind == JsonValueKind.True;
        return new(value, title, url, updatedAt, draft ? "draft" : state.ToLowerInvariant(), open, repository) { IsPullRequest = pull };
    }

    private static string? Text(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { } text && !string.IsNullOrWhiteSpace(text) ? text : null;

    private static bool TryGet(JsonElement element, out JsonElement value, params ReadOnlySpan<string> path)
    {
        value = element;
        foreach (var name in path)
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out value)) return false;
        }

        return true;
    }
}
