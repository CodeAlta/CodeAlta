using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugin.Jira;

/// <summary>Who the Atlassian CLI is signed in as.</summary>
/// <param name="SignedIn">Whether the CLI has an account.</param>
/// <param name="Site">The site of that account.</param>
/// <param name="Email">The email of that account.</param>
/// <param name="Kind">How it signed in: <c>oauth</c> or <c>api_token</c>.</param>
internal sealed record JiraAccount(bool SignedIn, string? Site = null, string? Email = null, string? Kind = null);

/// <summary>What an operation on Jira gave: a value, or why there is none.</summary>
internal readonly record struct JiraResult<T>(T? Value, string? Problem, bool NeedsSignIn = false)
{
    public static implicit operator JiraResult<T>(T value) => new(value, null);

    public static JiraResult<T> Failed(string problem, bool needsSignIn = false) => new(default, problem, needsSignIn);
}

/// <summary>
/// The Jira of one site, through the Atlassian CLI: who is signed in, the issues of a project, one issue with its
/// description and comments, and the changes an agent or the user asks for.
/// </summary>
internal sealed partial class JiraClient
{
    /// <summary>The fields a listing asks for. The CLI does not give the dates of the issues it searches.</summary>
    internal const string ListFields = "key,summary,status,issuetype,priority,assignee,reporter,labels";

    /// <summary>The fields the reading of one issue asks for.</summary>
    internal const string ViewFields = "summary,status,issuetype,priority,assignee,reporter,labels,created,updated,description,comment";

    private static readonly TimeSpan Short = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(60);
    private readonly IJiraCli _cli;

    internal JiraClient(IJiraCli cli) => _cli = cli;

    /// <summary>Says who the CLI is signed in as.</summary>
    public async Task<JiraAccount> GetAccountAsync(CancellationToken cancellationToken)
    {
        var result = await _cli.RunAsync(["jira", "auth", "status"], null, Short, cancellationToken).ConfigureAwait(false);
        var text = result.Output + "\n" + result.Error;
        if (!result.Succeeded || Field(text, "Site") is not { } site) return new(false);
        return new(true, JiraSettings.NormalizeSite(site) ?? site, Field(text, "Email"), Field(text, "Authentication Type"));
    }

    /// <summary>Makes the account of a site the one the CLI uses, when it has one.</summary>
    public async Task<bool> SwitchAsync(string site, CancellationToken cancellationToken)
        => (await _cli.RunAsync(["jira", "auth", "switch", "--site", site], null, Short, cancellationToken).ConfigureAwait(false)).Succeeded;

    /// <summary>Signs in with an API token, which the CLI reads from its standard input.</summary>
    public async Task<string?> SignInWithTokenAsync(string site, string email, string token, CancellationToken cancellationToken)
    {
        var result = await _cli.RunAsync(["jira", "auth", "login", "--site", site, "--email", email, "--token"], token + "\n", Long, cancellationToken).ConfigureAwait(false);
        return result.Succeeded ? null : result.Message is { Length: > 0 } message ? message : "Jira refused the API token.";
    }

    /// <summary>Signs in through the browser: the CLI opens the page of Atlassian and waits for the user there.</summary>
    public async Task<string?> SignInWithBrowserAsync(TimeSpan patience, CancellationToken cancellationToken)
    {
        var result = await _cli.RunAsync(["jira", "auth", "login", "--web"], null, patience, cancellationToken).ConfigureAwait(false);
        return result.Succeeded ? null : result.ExitCode == -1 ? "The sign-in was not finished in the browser." : result.Message is { Length: > 0 } message ? message : "The sign-in did not complete.";
    }

    /// <summary>Lists the issues a query finds, in the order of the query.</summary>
    public async Task<JiraResult<IReadOnlyList<TrackedItem>>> SearchAsync(JiraSettings settings, string jql, int limit, CancellationToken cancellationToken)
    {
        var result = await _cli.RunAsync(["jira", "workitem", "search", "--jql", jql, "--fields", ListFields, "--limit", limit.ToString(CultureInfo.InvariantCulture), "--json"],
            null, Long, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded) return Failure<IReadOnlyList<TrackedItem>>(result);
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(result.Output) ? "[]" : result.Output);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return JiraResult<IReadOnlyList<TrackedItem>>.Failed("Jira gave an answer that could not be read.");
            return new([.. document.RootElement.EnumerateArray().Select(issue => Item(issue, settings)).OfType<TrackedItem>()], null);
        }
        catch (JsonException)
        {
            return JiraResult<IReadOnlyList<TrackedItem>>.Failed("Jira gave an answer that could not be read.");
        }
    }

    /// <summary>Reads one issue with its description and its comments.</summary>
    public async Task<JiraResult<TrackedItemDetail>> ViewAsync(JiraSettings settings, string key, CancellationToken cancellationToken)
    {
        var result = await _cli.RunAsync(["jira", "workitem", "view", key, "--fields", ViewFields, "--json"], null, Long, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded) return Failure<TrackedItemDetail>(result);
        try
        {
            using var document = JsonDocument.Parse(result.Output);
            var issue = document.RootElement;
            if (Item(issue, settings) is not { } item) return JiraResult<TrackedItemDetail>.Failed("Jira gave an answer that could not be read.");
            var fields = issue.GetProperty("fields");
            var comments = new List<TrackedComment>();
            var total = 0;
            if (fields.TryGetProperty("comment", out var comment) && comment.ValueKind == JsonValueKind.Object)
            {
                total = comment.TryGetProperty("total", out var count) && count.TryGetInt32(out var number) ? number : 0;
                if (comment.TryGetProperty("comments", out var list) && list.ValueKind == JsonValueKind.Array)
                {
                    comments.AddRange(list.EnumerateArray().Select(static entry => (Body: entry.TryGetProperty("body", out var body) ? JiraDocument.ToMarkdown(body) : string.Empty, Entry: entry))
                        .Where(static entry => entry.Body.Length > 0)
                        .Select(static entry => new TrackedComment(Text(entry.Entry, "author", "displayName"), Date(Text(entry.Entry, "created")), entry.Body)));
                }
            }

            var description = fields.TryGetProperty("description", out var value) ? JiraDocument.ToMarkdown(value) : string.Empty;
            return new TrackedItemDetail(item with { CommentCount = total }, description, comments) { MoreComments = total > comments.Count };
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return JiraResult<TrackedItemDetail>.Failed("Jira gave an answer that could not be read.");
        }
    }

    /// <summary>Creates an issue and answers its key.</summary>
    public async Task<JiraResult<string>> CreateAsync(string project, string type, string summary, string? description, IReadOnlyList<string> labels, CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "jira", "workitem", "create", "--project", project, "--type", type, "--summary", summary, "--json" };
        if (!string.IsNullOrWhiteSpace(description)) arguments.AddRange(["--description", description]);
        if (labels.Count > 0) arguments.AddRange(["--label", string.Join(',', labels)]);
        var result = await _cli.RunAsync(arguments, null, Long, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded) return Failure<string>(result);
        try
        {
            using var document = JsonDocument.Parse(result.Output);
            return Text(document.RootElement, "key") is { } key ? key : JiraResult<string>.Failed("Jira created the issue but did not say its key.");
        }
        catch (JsonException)
        {
            return JiraResult<string>.Failed("Jira created the issue but did not say its key.");
        }
    }

    /// <summary>Adds a comment to an issue.</summary>
    public async Task<string?> CommentAsync(string key, string body, CancellationToken cancellationToken)
        => Problem(await _cli.RunAsync(["jira", "workitem", "comment", "create", "--key", key, "--body", body], null, Long, cancellationToken).ConfigureAwait(false));

    /// <summary>Moves an issue to a status of its workflow.</summary>
    public async Task<string?> TransitionAsync(string key, string status, CancellationToken cancellationToken)
        => Problem(await _cli.RunAsync(["jira", "workitem", "transition", "--key", key, "--status", status, "--yes"], null, Long, cancellationToken).ConfigureAwait(false));

    /// <summary>Assigns an issue: an email, an account id, or <c>@me</c>.</summary>
    public async Task<string?> AssignAsync(string key, string assignee, CancellationToken cancellationToken)
        => Problem(await _cli.RunAsync(["jira", "workitem", "assign", "--key", key, "--assignee", assignee], null, Long, cancellationToken).ConfigureAwait(false));

    /// <summary>Writes a text between the quotes of a JQL query.</summary>
    internal static string Quote(string text) => "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    /// <summary>Gets whether a text is the key of an issue: <c>ALTA-12</c>.</summary>
    internal static bool IsKey(string? text) => text is { Length: > 2 and <= 64 } && KeyPattern().IsMatch(text);

    private static string? Problem(JiraCliResult result)
        => result.Succeeded ? null : result.Message is { Length: > 0 } message ? message : "Jira refused the change.";

    private static JiraResult<T> Failure<T>(JiraCliResult result)
    {
        var message = result.Message;
        var signIn = message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) || message.Contains("not logged in", StringComparison.OrdinalIgnoreCase)
            || message.Contains("authenticate", StringComparison.OrdinalIgnoreCase) || message.Contains("401", StringComparison.Ordinal);
        return JiraResult<T>.Failed(result.ExitCode == 127 ? "The Atlassian CLI is not installed." : message.Length > 0 ? "Jira: " + message : "Jira did not answer.", signIn);
    }

    private static TrackedItem? Item(JsonElement issue, JiraSettings settings)
    {
        if (Text(issue, "key") is not { } key || !issue.TryGetProperty("fields", out var fields) || Text(fields, "summary") is not { } summary) return null;
        var done = Text(fields, "status", "statusCategory", "key") == "done";
        return new(TrackedItemKind.Issue, key, summary, settings.BrowseUrl(key), done ? TrackedItemState.Closed : TrackedItemState.Open)
        {
            StateText = Text(fields, "status", "name"),
            Type = Text(fields, "issuetype", "name"),
            Priority = Text(fields, "priority", "name"),
            Author = Text(fields, "reporter", "displayName"),
            Assignees = Text(fields, "assignee", "displayName") is { } assignee ? [assignee] : [],
            Labels = fields.TryGetProperty("labels", out var labels) && labels.ValueKind == JsonValueKind.Array
                ? [.. labels.EnumerateArray().Select(static label => label.GetString()).OfType<string>().Select(static label => new TrackedLabel(label))]
                : [],
            CreatedAt = Date(Text(fields, "created")),
            UpdatedAt = Date(Text(fields, "updated")),
        };
    }

    private static string? Text(JsonElement element, params ReadOnlySpan<string> path)
    {
        foreach (var name in path)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out element)) return null;
        }

        return element.ValueKind == JsonValueKind.String && element.GetString() is { Length: > 0 } text ? text : null;
    }

    // Jira writes the offset of a date without its colon: 2026-10-07T23:17:18.501+0200.
    internal static DateTimeOffset? Date(string? text)
    {
        if (text is null) return null;
        if (text.Length > 5 && text[^5] is '+' or '-' && text[^3] != ':') text = text[..^2] + ":" + text[^2..];
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : null;
    }

    private static string? Field(string text, string name)
    {
        foreach (var line in text.ReplaceLineEndings("\n").Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase)) return trimmed[(name.Length + 1)..].Trim() is { Length: > 0 } value ? value : null;
        }

        return null;
    }

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]*-\d+$")]
    private static partial Regex KeyPattern();
}

/// <summary>The issues of the Jira project of a CodeAlta project.</summary>
internal sealed class JiraTracker : IIssueTracker
{
    private static readonly TrackedItemKind[] Issues = [TrackedItemKind.Issue];
    private readonly JiraSettings _settings;
    private readonly JiraClient _client;
    private readonly Func<CancellationToken, ValueTask<(string? Problem, bool NeedsSignIn)>> _ready;

    /// <summary>Creates the tracker.</summary>
    /// <param name="settings">The site and the project.</param>
    /// <param name="client">The Jira of the CLI.</param>
    /// <param name="ready">Makes the CLI ready and signed in for the site, or says why it is not.</param>
    internal JiraTracker(JiraSettings settings, JiraClient client, Func<CancellationToken, ValueTask<(string? Problem, bool NeedsSignIn)>> ready)
        => (_settings, _client, _ready) = (settings, client, ready);

    /// <inheritdoc />
    public string Service => "jira";

    /// <inheritdoc />
    public string DisplayName => "Jira";

    /// <inheritdoc />
    public string Location => _settings.Project;

    /// <inheritdoc />
    public string? WebUrl => _settings.BrowseUrl(_settings.Project);

    /// <inheritdoc />
    public IReadOnlyList<TrackedItemKind> Kinds => Issues;

    /// <summary>Writes the query of a listing.</summary>
    internal static string Query(string project, TrackedItemFilter filter, string? text)
    {
        var jql = "project = " + JiraClient.Quote(project);
        jql += filter switch { TrackedItemFilter.Open => " AND statusCategory != Done", TrackedItemFilter.Closed => " AND statusCategory = Done", _ => string.Empty };
        var words = text?.Trim();
        if (!string.IsNullOrEmpty(words))
        {
            // A key, or the number of one, names an issue; anything else is looked for in the texts.
            jql += JiraClient.IsKey(words) ? " AND key = " + JiraClient.Quote(words.ToUpperInvariant())
                : words.All(char.IsAsciiDigit) ? " AND key = " + JiraClient.Quote(project + "-" + words)
                : " AND text ~ " + JiraClient.Quote(words);
        }

        return jql + " ORDER BY updated DESC";
    }

    /// <inheritdoc />
    public async ValueTask<TrackedItemPage> ListAsync(TrackedItemQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Kind != TrackedItemKind.Issue || query.Filter == TrackedItemFilter.Merged) return new([]);
        var (problem, needsSignIn) = await _ready(cancellationToken).ConfigureAwait(false);
        if (problem is not null) return new([]) { Problem = problem, NeedsSignIn = needsSignIn };
        var limit = Math.Clamp(query.Limit, 1, 100);
        var found = await _client.SearchAsync(_settings, Query(_settings.Project, query.Filter, query.Text), limit + 1, cancellationToken).ConfigureAwait(false);
        if (found.Value is not { } items) return new([]) { Problem = found.Problem, NeedsSignIn = found.NeedsSignIn };
        return new(items.Count > limit ? [.. items.Take(limit)] : items) { More = items.Count > limit };
    }

    /// <inheritdoc />
    public async ValueTask<TrackedItemDetail?> ReadAsync(TrackedItemKind kind, string id, CancellationToken cancellationToken)
    {
        if (kind != TrackedItemKind.Issue || !JiraClient.IsKey(id)) return null;
        if ((await _ready(cancellationToken).ConfigureAwait(false)).Problem is not null) return null;
        return (await _client.ViewAsync(_settings, id.ToUpperInvariant(), cancellationToken).ConfigureAwait(false)).Value;
    }
}
