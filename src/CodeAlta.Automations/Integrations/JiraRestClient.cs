using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CodeAlta.Automations.Integrations;

/// <summary>A version of a Jira project.</summary>
/// <param name="Id">Its id.</param>
/// <param name="Name">Its name, such as <c>4.0.0</c>.</param>
/// <param name="Released">Whether it is released.</param>
/// <param name="ReleaseDate">Its release date as Jira writes it (<c>2026-11-04</c>); null without one.</param>
/// <param name="Description">Its description; null without one.</param>
internal sealed record JiraVersion(string Id, string Name, bool Released, string? ReleaseDate, string? Description);

/// <summary>An issue of Jira, as a search reads it.</summary>
/// <param name="Id">Its id.</param>
/// <param name="Key">Its key, such as <c>PLAT-12</c>.</param>
/// <param name="Summary">Its summary.</param>
/// <param name="Status">The name of its status.</param>
/// <param name="Type">The name of its type.</param>
/// <param name="Reporter">The display name of who reported it.</param>
/// <param name="Updated">When it last changed, as Jira writes it.</param>
internal sealed record JiraIssue(string Id, string Key, string? Summary, string? Status, string? Type, string? Reporter, string? Updated);

/// <summary>
/// The REST API v3 of a Jira Cloud site, with the API token of an account (basic authentication with its email): what the
/// triggers of Jira read (versions of a project, a JQL search) and write (a comment on an issue). The token is the one a
/// secret of the store gives: it goes only to the site the trigger names.
/// </summary>
internal sealed class JiraRestClient
{
    private const string Service = "Jira";
    private readonly HttpClient _http;
    private readonly Uri _site;
    private readonly AuthenticationHeaderValue _authorization;

    /// <summary>Creates the client of a site.</summary>
    /// <param name="http">The client of the host.</param>
    /// <param name="site">The https address of the site.</param>
    /// <param name="email">The email of the account of the token.</param>
    /// <param name="token">The API token.</param>
    internal JiraRestClient(HttpClient http, Uri site, string email, string token)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(site);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        (_http, _site) = (http, new Uri(site.GetLeftPart(UriPartial.Authority) + "/"));
        _authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(email + ":" + token)));
    }

    /// <summary>The address of an issue on the web.</summary>
    internal string Browse(string key) => new Uri(_site, "browse/" + Uri.EscapeDataString(key)).ToString();

    /// <summary>Says who the token is: the connection test of the editor.</summary>
    /// <returns>The display name of the account.</returns>
    internal Task<IntegrationResult<string>> MyselfAsync(CancellationToken cancellationToken)
        => IntegrationHttp.SendAsync(_http, Request(HttpMethod.Get, "rest/api/3/myself"), Service, static (root, _) =>
            root.Text("displayName") is { } name ? IntegrationResult<string>.Of(name) : IntegrationResult<string>.Fail("Jira did not say who the token is."), cancellationToken);

    /// <summary>Lists the versions of a project.</summary>
    internal Task<IntegrationResult<IReadOnlyList<JiraVersion>>> VersionsAsync(string projectKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectKey);
        return IntegrationHttp.SendAsync(_http, Request(HttpMethod.Get, $"rest/api/3/project/{Uri.EscapeDataString(projectKey)}/versions"), Service, static (root, _) =>
        {
            if (root.ValueKind != JsonValueKind.Array) return IntegrationResult<IReadOnlyList<JiraVersion>>.Fail("Jira did not answer a list of versions.");
            var versions = new List<JiraVersion>();
            foreach (var item in root.EnumerateArray())
            {
                if (item.Text("id") is not { Length: > 0 } id) continue;
                versions.Add(new JiraVersion(id, item.Text("name") ?? id, item.Flag("released"), item.Text("releaseDate"), item.Text("description")));
            }

            return IntegrationResult<IReadOnlyList<JiraVersion>>.Of(versions);
        }, cancellationToken);
    }

    /// <summary>Searches issues with JQL, the most recently updated first as the query orders them.</summary>
    internal Task<IntegrationResult<IReadOnlyList<JiraIssue>>> SearchAsync(string jql, int maximum, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jql);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("jql", jql);
            writer.WriteNumber("maxResults", Math.Clamp(maximum, 1, 100));
            writer.WriteStartArray("fields");
            foreach (var field in new[] { "summary", "status", "issuetype", "reporter", "updated" }) writer.WriteStringValue(field);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        var request = IntegrationHttp.Json(HttpMethod.Post, new Uri(_site, "rest/api/3/search/jql"), Encoding.UTF8.GetString(buffer.ToArray()));
        request.Headers.Authorization = _authorization;
        return IntegrationHttp.SendAsync(_http, request, Service, static (root, _) =>
        {
            if (root.Member("issues") is not { ValueKind: JsonValueKind.Array } issues) return IntegrationResult<IReadOnlyList<JiraIssue>>.Fail("Jira did not answer a list of issues.");
            var found = new List<JiraIssue>();
            foreach (var issue in issues.EnumerateArray())
            {
                if (issue.Text("key") is not { Length: > 0 } key) continue;
                var fields = issue.Member("fields");
                found.Add(new JiraIssue(issue.Text("id") ?? key, key, fields.Text("summary"), fields.Member("status").Text("name"), fields.Member("issuetype").Text("name"),
                    fields.Member("reporter").Text("displayName"), fields.Text("updated")));
            }

            return IntegrationResult<IReadOnlyList<JiraIssue>>.Of(found);
        }, cancellationToken);
    }

    /// <summary>Writes a comment on an issue, as plain paragraphs.</summary>
    /// <returns>The id of the comment.</returns>
    internal Task<IntegrationResult<string>> CommentAsync(string issueKey, string text, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(issueKey);
        ArgumentNullException.ThrowIfNull(text);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            // The document format of Jira: one paragraph per paragraph of the text.
            writer.WriteStartObject();
            writer.WriteStartObject("body");
            writer.WriteString("type", "doc");
            writer.WriteNumber("version", 1);
            writer.WriteStartArray("content");
            foreach (var paragraph in text.ReplaceLineEndings("\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                writer.WriteStartObject();
                writer.WriteString("type", "paragraph");
                writer.WriteStartArray("content");
                writer.WriteStartObject();
                writer.WriteString("type", "text");
                writer.WriteString("text", paragraph);
                writer.WriteEndObject();
                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        var request = IntegrationHttp.Json(HttpMethod.Post, new Uri(_site, $"rest/api/3/issue/{Uri.EscapeDataString(issueKey)}/comment"), Encoding.UTF8.GetString(buffer.ToArray()));
        request.Headers.Authorization = _authorization;
        return IntegrationHttp.SendAsync(_http, request, Service, static (root, _) => IntegrationResult<string>.Of(root.Text("id") ?? string.Empty), cancellationToken);
    }

    /// <summary>The time Jira wrote, as a stamp that changes with each change of the issue.</summary>
    internal static string Stamp(JiraIssue issue)
        => DateTimeOffset.TryParse(issue.Updated, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)
            ? time.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) : issue.Updated ?? string.Empty;

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, new Uri(_site, path));
        request.Headers.Authorization = _authorization;
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }
}
