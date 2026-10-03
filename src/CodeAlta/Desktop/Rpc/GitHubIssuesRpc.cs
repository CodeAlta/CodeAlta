using System.Globalization;
using System.Text;
using CodeAlta.Catalog;
using CodeAlta.Plugin.GitHub;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// Looks up the GitHub issues of a project for the prompt editor's <c>#</c> issue picker, with the
/// query behavior of the terminal picker (the shared <see cref="GitHubIssueLookup"/>).
/// </summary>
/// <remarks>
/// The page only names a project id: the folder comes from the host's project catalog, never from the
/// request. A project that is not a github.com repository is answered without any network request.
/// Titles are text written by anyone who can file an issue, so they are bounded and stripped of
/// control characters before they leave the host; credentials and failure details never do.
/// The type is spelled <c>Github</c> because the generated TypeScript client is named after it:
/// <c>githubIssues</c>, like the wire name.
/// </remarks>
[NeoRpcService("githubIssues", Version = 1)]
internal sealed class GithubIssuesService : IDisposable
{
    /// <summary>Number of issues returned when the request names no limit.</summary>
    internal const int DefaultLimit = 50;

    /// <summary>Largest number of issues one response returns.</summary>
    internal const int MaximumLimit = 100;

    /// <summary>Longest accepted query, in UTF-16 units.</summary>
    internal const int MaximumQueryLength = 256;

    /// <summary>Longest returned issue title, in UTF-16 units.</summary>
    internal const int MaximumTitleLength = 512;

    /// <summary>Longest returned failure message, in UTF-16 units.</summary>
    internal const int MaximumMessageLength = 512;

    private const int MaximumIdentityLength = 256;
    private const int MaximumRepositoryPartLength = 100;
    private const int MaximumUrlLength = 2048;
    private const int MaximumStateLength = 32;
    private readonly ProjectCatalog _projects;
    private readonly string _epoch;
    private readonly HttpClient _client;
    private readonly GitHubIssueLookup _lookup;

    /// <summary>Creates the service for an owned host, using git, the user's GitHub token and api.github.com.</summary>
    /// <param name="projects">The host's project catalog, used to resolve a project id to its root.</param>
    /// <param name="epoch">The host epoch that requests must name.</param>
    /// <exception cref="ArgumentNullException"><paramref name="projects"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="epoch"/> is blank.</exception>
    internal GithubIssuesService(ProjectCatalog projects, string epoch)
        : this(projects, epoch, GitHubIssueLookup.CreateHttpClient(), new GitHubIssueLookupOptions { TokenProvider = GitHubIssueLookup.ResolveTokenAsync })
    {
    }

    /// <summary>Creates the service over a literal transport; tests supply the handler, remote reader, token and clock.</summary>
    /// <param name="projects">The host's project catalog, used to resolve a project id to its root.</param>
    /// <param name="epoch">The host epoch that requests must name.</param>
    /// <param name="handler">The handler that answers the GitHub REST requests; disposed with the service.</param>
    /// <param name="options">The cache, clock, git and token behavior of the lookup.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="epoch"/> is blank.</exception>
    internal GithubIssuesService(ProjectCatalog projects, string epoch, HttpMessageHandler handler, GitHubIssueLookupOptions options)
        : this(projects, epoch, GitHubIssueLookup.CreateHttpClient(handler), options)
    {
    }

    private GithubIssuesService(ProjectCatalog projects, string epoch, HttpClient client, GitHubIssueLookupOptions options)
    {
        _client = client;
        try
        {
            ArgumentNullException.ThrowIfNull(projects);
            ArgumentException.ThrowIfNullOrWhiteSpace(epoch);
            _projects = projects;
            _epoch = epoch;
            _lookup = new GitHubIssueLookup(client, options);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>Searches the issues of the named project's GitHub repository for the text typed after <c>#</c>.</summary>
    [NeoRpcMethod("search")]
    public async Task<GitHubIssuesSearchResponse> SearchAsync(GitHubIssuesSearchRequest request, CancellationToken cancellationToken)
    {
        GitHubIssuesSearchResponse Refused(string status, string? repository = null, string? message = null)
            => new(status, _epoch, repository, [], message);

        if (request is null || !Identity(request.ExpectedEpoch) || !SingleLine(request.Query, MaximumQueryLength)) return Refused("invalid_request");
        if (!string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) return Refused("stale_epoch");
        // Issues belong to a project's repository: without a project there is nothing to look up.
        if (request.ProjectId is null) return Refused("not_github");
        var project = await SettingsProjectScope.ResolveAsync(_projects, request.ProjectId, cancellationToken).ConfigureAwait(false);
        if (project.Status == "read_failed") return Refused("failed", null, "The project could not be read.");
        if (project.Status != "ok") return Refused(project.Status);
        if (project.Root is not { } root) return Refused("project_unavailable");

        var limit = Math.Clamp(request.Limit ?? DefaultLimit, 1, MaximumLimit);
        string? repositoryName = null;
        try
        {
            var repository = await _lookup.ResolveRepositoryAsync(root, cancellationToken).ConfigureAwait(false);
            // A remote that does not spell an ordinary owner/name is not sent to GitHub or to the page.
            if (repository is null || !RepositoryPart(repository.Owner) || !RepositoryPart(repository.Name)) return Refused("not_github");
            repositoryName = repository.FullName;
            var result = await _lookup.QueryAsync(repository, request.Query, limit, cancellationToken).ConfigureAwait(false);
            var issues = Entries(result.Issues, limit);
            // Matches found beside a refused request (a rate-limited search) are still worth showing.
            return issues.Count == 0 && result.FailureStatusCode is { } refusal
                ? Refused("failed", repositoryName, string.Create(CultureInfo.InvariantCulture, $"GitHub answered the request with HTTP {(int)refusal}."))
                : new("ok", _epoch, repositoryName, issues, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Never serialize exception text: it can name local paths, hosts or request headers.
            return Refused("failed", repositoryName, "GitHub could not be reached or returned an unexpected answer.");
        }
    }

    /// <inheritdoc />
    public void Dispose() => _client.Dispose();

    private static List<GitHubIssueEntry> Entries(IReadOnlyList<GitHubIssueReferenceItem> issues, int limit)
    {
        var entries = new List<GitHubIssueEntry>(Math.Min(issues.Count, limit));
        foreach (var issue in issues)
        {
            if (entries.Count >= limit) break;
            var title = Clean(issue.Title, MaximumTitleLength);
            if (issue.Number <= 0 || title.Length == 0 || IssueUrl(issue.Url) is not { } url) continue;
            entries.Add(new(issue.Number, title, url, Clean(issue.State, MaximumStateLength), issue.UpdatedAt));
        }

        return entries;
    }

    // Only a link to github.com itself is handed to the page for insertion into a prompt.
    private static string? IssueUrl(string? value)
        => value is { Length: > 0 and <= MaximumUrlLength } && !value.Any(static character => char.IsControl(character) || char.IsWhiteSpace(character))
           && Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0
           && string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) && uri.AbsoluteUri.Length <= MaximumUrlLength
            ? uri.AbsoluteUri
            : null;

    // Removes control, line-breaking and bidirectional-override characters and unpaired surrogates, then bounds the text.
    private static string Clean(string? value, int maximum)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var text = new StringBuilder(Math.Min(value.Length, maximum));
        for (var index = 0; index < value.Length && text.Length < maximum; index++)
        {
            var character = value[index];
            if (char.IsHighSurrogate(character) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
            {
                if (text.Length + 2 > maximum) break;
                text.Append(character).Append(value[++index]);
            }
            else if (!char.IsSurrogate(character) && !char.IsControl(character)
                     && character is not ('\u2028' or '\u2029' or (>= '\u202A' and <= '\u202E') or (>= '\u2066' and <= '\u2069')))
            {
                text.Append(character);
            }
        }

        return text.ToString().Trim();
    }

    private static bool Identity(string? value)
        => SingleLine(value, MaximumIdentityLength) && !string.IsNullOrWhiteSpace(value) && value == value.Trim();

    private static bool SingleLine(string? value, int maximum)
    {
        if (value is null || value.Length > maximum) return false;
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsControl(value[index])) return false;
            if (char.IsSurrogate(value[index]) && (!char.IsHighSurrogate(value[index]) || ++index == value.Length || !char.IsLowSurrogate(value[index]))) return false;
        }

        return true;
    }

    private static bool RepositoryPart(string value)
        => value is { Length: > 0 and <= MaximumRepositoryPartLength } && value is not ("." or "..")
           && value.All(static character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
}

/// <summary>A lookup of the named project's issues; an empty query lists the most recently updated ones.</summary>
internal sealed record GitHubIssuesSearchRequest(string ExpectedEpoch, string? ProjectId, string Query, int? Limit);

/// <summary>
/// The found issues, most recently updated first. The status is <c>ok</c>, <c>invalid_request</c>,
/// <c>stale_epoch</c>, <c>unknown_project</c>, <c>archived_project</c>, <c>project_unavailable</c>,
/// <c>not_github</c> (no project, or its folder has no github.com remote) or <c>failed</c>; only a
/// failure carries a message.
/// </summary>
internal sealed record GitHubIssuesSearchResponse(string Status, string Epoch, string? Repository,
    IReadOnlyList<GitHubIssueEntry> Issues, string? Message);

/// <summary>One issue (never a pull request); the title is untrusted text to render as text.</summary>
internal sealed record GitHubIssueEntry(int Number, string Title, string Url, string State, DateTimeOffset? UpdatedAt);
