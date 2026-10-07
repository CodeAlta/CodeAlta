using System.Globalization;
using System.Text;
using CodeAlta.Catalog;
using CodeAlta.Plugin.Git;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// Looks up the issues of a project's hosted repository (GitHub, GitLab or Azure DevOps) for the prompt
/// editor's <c>#</c> issue picker, with the query behavior of the terminal picker (the shared
/// <see cref="GitIssueLookup"/>).
/// </summary>
/// <remarks>
/// The page only names a project id: the folder comes from the host's project catalog, never from the
/// request. A project without a remote of a supported provider is answered without any network request.
/// Titles are text written by anyone who can file an issue, so they are bounded and stripped of
/// control characters before they leave the host; credentials and failure details never do.
/// </remarks>
[NeoRpcService("gitIssues", Version = 1)]
internal sealed class GitIssuesService : IDisposable
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
    private const int MaximumRepositoryLength = 512;
    private const int MaximumUrlLength = 2048;
    private const int MaximumStateLength = 32;
    private readonly ProjectCatalog _projects;
    private readonly string _epoch;
    private readonly HttpClient _client;
    private readonly GitIssueLookup _lookup;

    /// <summary>Creates the service for an owned host, using git, the user's provider credentials and the provider REST APIs.</summary>
    /// <param name="projects">The host's project catalog, used to resolve a project id to its root.</param>
    /// <param name="epoch">The host epoch that requests must name.</param>
    /// <exception cref="ArgumentNullException"><paramref name="projects"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="epoch"/> is blank.</exception>
    internal GitIssuesService(ProjectCatalog projects, string epoch)
        : this(projects, epoch, GitIssueLookup.CreateHttpClient(), new GitIssueLookupOptions { CredentialProvider = GitIssueLookup.ResolveCredentialAsync })
    {
    }

    /// <summary>Creates the service over a literal transport; tests supply the handler, remote reader, credentials and clock.</summary>
    /// <param name="projects">The host's project catalog, used to resolve a project id to its root.</param>
    /// <param name="epoch">The host epoch that requests must name.</param>
    /// <param name="handler">The handler that answers the provider REST requests; disposed with the service.</param>
    /// <param name="options">The cache, clock, git, host and credential behavior of the lookup.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="epoch"/> is blank.</exception>
    internal GitIssuesService(ProjectCatalog projects, string epoch, HttpMessageHandler handler, GitIssueLookupOptions options)
        : this(projects, epoch, GitIssueLookup.CreateHttpClient(handler), options)
    {
    }

    private GitIssuesService(ProjectCatalog projects, string epoch, HttpClient client, GitIssueLookupOptions options)
    {
        _client = client;
        try
        {
            ArgumentNullException.ThrowIfNull(projects);
            ArgumentException.ThrowIfNullOrWhiteSpace(epoch);
            _projects = projects;
            _epoch = epoch;
            _lookup = new GitIssueLookup(client, options);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>Searches the issues of the named project's hosted repository for the text typed after <c>#</c>.</summary>
    [NeoRpcMethod("search")]
    public async Task<GitIssuesSearchResponse> SearchAsync(GitIssuesSearchRequest request, CancellationToken cancellationToken)
    {
        string? provider = null;
        GitIssuesSearchResponse Refused(string status, string? repository = null, string? message = null)
            => new(status, _epoch, provider, repository, [], message);

        if (request is null || !Identity(request.ExpectedEpoch) || !SingleLine(request.Query, MaximumQueryLength)) return Refused("invalid_request");
        if (!string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) return Refused("stale_epoch");
        // Issues belong to a project's repository: without a project there is nothing to look up.
        if (request.ProjectId is null) return Refused("no_repository");
        var project = await SettingsProjectScope.ResolveAsync(_projects, request.ProjectId, cancellationToken).ConfigureAwait(false);
        if (project.Status == "read_failed") return Refused("failed", null, "The project could not be read.");
        if (project.Status != "ok") return Refused(project.Status);
        if (project.Root is not { } root) return Refused("project_unavailable");

        var limit = Math.Clamp(request.Limit ?? DefaultLimit, 1, MaximumLimit);
        string? repositoryName = null;
        try
        {
            // The lookup only recognizes a remote that spells an ordinary repository path: nothing else is
            // sent to a provider or to the page.
            var repository = await _lookup.ResolveRepositoryAsync(root, cancellationToken).ConfigureAwait(false);
            if (repository is null || repository.FullName.Length > MaximumRepositoryLength) return Refused("no_repository");
            provider = ProviderName(repository.Provider);
            repositoryName = repository.FullName;
            var result = await _lookup.QueryAsync(repository, request.Query, limit, cancellationToken).ConfigureAwait(false);
            // The pull requests are found beside the issues: a provider that refuses them still shows its issues.
            IReadOnlyList<GitIssueReferenceItem> pulls = [];
            try { pulls = await _lookup.QueryPullRequestsAsync(repository, request.Query, limit, cancellationToken).ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested) { }
            var issues = Entries(Merge(result.Issues, pulls), repository.Host, limit);
            // Matches found beside a refused request (a rate-limited search) are still worth showing.
            return issues.Count == 0 && result.FailureStatusCode is { } refusal
                ? Refused("failed", repositoryName, string.Create(CultureInfo.InvariantCulture, $"{repository.Provider.GetDisplayName()} answered the request with HTTP {(int)refusal}."))
                : new("ok", _epoch, provider, repositoryName, issues, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Never serialize exception text: it can name local paths, hosts or request headers.
            return Refused("failed", repositoryName, "The repository host could not be reached or returned an unexpected answer.");
        }
    }

    /// <inheritdoc />
    public void Dispose() => _client.Dispose();

    private static string ProviderName(GitRemoteProvider provider)
        => provider switch
        {
            GitRemoteProvider.GitLab => "gitlab",
            GitRemoteProvider.AzureDevOps => "azure_devops",
            GitRemoteProvider.Bitbucket => "bitbucket",
            _ => "github",
        };

    // Both lists come in the order they are shown in: the one whose next item changed last gives the next row.
    private static List<GitIssueReferenceItem> Merge(IReadOnlyList<GitIssueReferenceItem> issues, IReadOnlyList<GitIssueReferenceItem> pulls)
    {
        var merged = new List<GitIssueReferenceItem>(issues.Count + pulls.Count);
        int issue = 0, pull = 0;
        while (issue < issues.Count || pull < pulls.Count)
        {
            merged.Add(pull >= pulls.Count || issue < issues.Count && issues[issue].UpdatedAt >= pulls[pull].UpdatedAt ? issues[issue++] : pulls[pull++]);
        }

        return merged;
    }

    private static List<GitIssueEntry> Entries(IReadOnlyList<GitIssueReferenceItem> issues, string host, int limit)
    {
        var entries = new List<GitIssueEntry>(Math.Min(issues.Count, limit));
        foreach (var issue in issues)
        {
            if (entries.Count >= limit) break;
            var title = Clean(issue.Title, MaximumTitleLength);
            if (issue.Number <= 0 || title.Length == 0 || IssueUrl(issue.Url, host) is not { } url) continue;
            entries.Add(new(issue.Number, title, url, Clean(issue.State, MaximumStateLength), issue.IsOpen, issue.UpdatedAt, issue.IsPullRequest ? "pull_request" : "issue"));
        }

        return entries;
    }

    // Only a link to the host of the repository itself is handed to the page for insertion into a prompt.
    internal static string? IssueUrl(string? value, string host)
        => value is { Length: > 0 and <= MaximumUrlLength } && !value.Any(static character => char.IsControl(character) || char.IsWhiteSpace(character))
           && Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0
           && string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase) && uri.AbsoluteUri.Length <= MaximumUrlLength
            ? uri.AbsoluteUri
            : null;

    // Removes control, line-breaking and bidirectional-override characters and unpaired surrogates, then bounds the text.
    internal static string Clean(string? value, int maximum)
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

    internal static bool Identity(string? value)
        => SingleLine(value, MaximumIdentityLength) && !string.IsNullOrWhiteSpace(value) && value == value.Trim();

    internal static bool SingleLine(string? value, int maximum)
    {
        if (value is null || value.Length > maximum) return false;
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsControl(value[index])) return false;
            if (char.IsSurrogate(value[index]) && (!char.IsHighSurrogate(value[index]) || ++index == value.Length || !char.IsLowSurrogate(value[index]))) return false;
        }

        return true;
    }
}

/// <summary>A lookup of the named project's issues; an empty query lists the most recently updated ones.</summary>
internal sealed record GitIssuesSearchRequest(string ExpectedEpoch, string? ProjectId, string Query, int? Limit);

/// <summary>
/// The found issues, most recently updated first. The status is <c>ok</c>, <c>invalid_request</c>,
/// <c>stale_epoch</c>, <c>unknown_project</c>, <c>archived_project</c>, <c>project_unavailable</c>,
/// <c>no_repository</c> (no project, or its folder has no GitHub, GitLab, Azure DevOps or Bitbucket remote) or
/// <c>failed</c>; only a failure carries a message. The provider is <c>github</c>, <c>gitlab</c>,
/// <c>azure_devops</c> or <c>bitbucket</c> once the repository is known.
/// </summary>
internal sealed record GitIssuesSearchResponse(string Status, string Epoch, string? Provider, string? Repository,
    IReadOnlyList<GitIssueEntry> Issues, string? Message);

/// <summary>
/// One issue, work item or pull request; the title is untrusted text to render as text. The state is the
/// provider's own word for it, and <c>Open</c> says whether it still counts as open. The kind is
/// <c>issue</c> or <c>pull_request</c>.
/// </summary>
internal sealed record GitIssueEntry(int Number, string Title, string Url, string State, bool Open, DateTimeOffset? UpdatedAt, string Kind = "issue");
