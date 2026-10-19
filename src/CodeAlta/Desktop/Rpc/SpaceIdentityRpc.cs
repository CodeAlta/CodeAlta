using CodeAlta.Catalog;
using CodeAlta.Desktop.Spaces;
using CodeAlta.Orchestration.Runtime;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// The identity of a space for its page: the Git, GitHub and Jira identity its sessions work under and the profiles of
/// their other command line tools, each card saved on its own, the usual settings a card left empty keeps, and the
/// check of each card. Also the identity a session works under, for its information window. Only references are read
/// and written, never a secret.
/// </summary>
[NeoRpcService("spaceIdentity", Version = 1)]
internal sealed class SpaceIdentityService : IDisposable
{
    private readonly SpaceCatalog? _catalog;
    private readonly Func<string, CancellationToken, Task<SessionIdentity>>? _session;
    private readonly SpaceIdentityChecks? _checks;
    private readonly string? _epoch;

    /// <summary>Creates the service of a window whose host keeps no spaces: it answers <c>unavailable</c>.</summary>
    public SpaceIdentityService()
    {
    }

    /// <summary>Creates the service of an owned host.</summary>
    /// <param name="catalog">The spaces.</param>
    /// <param name="session">Gives the identity a session works under.</param>
    /// <param name="epoch">The host epoch.</param>
    /// <param name="checks">The checks of the cards; null for the tools of this machine. Disposed with the service.</param>
    internal SpaceIdentityService(SpaceCatalog catalog, Func<string, CancellationToken, Task<SessionIdentity>> session, string epoch, SpaceIdentityChecks? checks = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(epoch);
        (_catalog, _session, _epoch, _checks) = (catalog, session, epoch, checks ?? new SpaceIdentityChecks());
    }

    /// <inheritdoc />
    public void Dispose() => _checks?.Dispose();

    /// <summary>Reads the identity of a space, with the usual settings that what it leaves empty keeps.</summary>
    [NeoRpcMethod("get")]
    public async Task<SpaceIdentityResponse> GetAsync(SpaceIdentityRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, null, null, null);
        try
        {
            var space = await _catalog!.GetAsync(request.SpaceId ?? string.Empty, cancellationToken).ConfigureAwait(false);
            if (space is null) return new("not_found", null, null, null);
            // What the file wrote that is not valid was left out: the page says so, as the message of the answer.
            return new("ok", space.IdentityProblem, Item(space.Settings.Identity), await _checks!.UsualAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new("unreadable", null, null, null);
        }
    }

    /// <summary>
    /// Saves one card of the identity of a space (<c>git</c>, <c>github</c>, <c>jira</c>, or <c>cli</c> for all the CLI
    /// profiles at once): what it gives replaces that part, an empty card goes back to the usual settings, and the
    /// other parts stay as the file has them.
    /// </summary>
    [NeoRpcMethod("update")]
    public async Task<SpaceIdentityResponse> UpdateAsync(SpaceIdentityUpdateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, null, null, null);
        if (request.Part is not ("git" or "github" or "jira" or "cli")) return new("invalid", PartProblem, null, null);
        try
        {
            var change = await _catalog!.UpdateSettingsAsync(request.SpaceId ?? string.Empty, settings =>
            {
                var identity = settings.Identity ?? new SpaceIdentity();
                identity = request.Part switch
                {
                    "git" => identity with { Git = Git(request.Git) },
                    "github" => identity with { GitHub = GitHub(request.GitHub) },
                    "cli" => identity with { Cli = Cli(request.Cli) },
                    _ => identity with { Jira = Jira(request.Jira) },
                };
                return settings with { Identity = identity.IsEmpty ? null : identity };
            }, cancellationToken).ConfigureAwait(false);
            var status = change.Status switch
            {
                SpaceChangeStatus.Ok => "ok",
                SpaceChangeStatus.NotFound => "not_found",
                SpaceChangeStatus.Conflict => "conflict",
                _ => "invalid",
            };
            return new(status, change.Succeeded ? null : change.Message, change.Space is { } space ? Item(space.Settings.Identity) : null, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new("write_failed", null, null, null);
        }
    }

    /// <summary>
    /// Checks one card as it is written on the page, with the tool it is for; for <c>cli</c>, one profile with its own
    /// check command. Nothing is saved or changed.
    /// </summary>
    [NeoRpcMethod("check")]
    public async Task<SpaceIdentityCheckResponse> CheckAsync(SpaceIdentityCheckRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, []);
        IReadOnlyList<IdentityCheckLine> lines = request.Part switch
        {
            "git" => await _checks!.CheckGitAsync(Git(request.Git), cancellationToken).ConfigureAwait(false),
            "github" => await _checks!.CheckGitHubAsync(GitHub(request.GitHub), cancellationToken).ConfigureAwait(false),
            "jira" => await _checks!.CheckJiraAsync(Jira(request.Jira), cancellationToken).ConfigureAwait(false),
            "cli" => request.Profile is { } profile
                ? await _checks!.CheckCliAsync(Profile(profile), cancellationToken).ConfigureAwait(false)
                : [new("error", "A CLI profile is needed.")],
            _ => [new("error", PartProblem)],
        };
        return new(lines.Any(static line => line.Kind == "error") ? "problem" : "ok", lines);
    }

    /// <summary>
    /// Says which identity a session works under: the one of its space with the CLI profiles of its project, as the
    /// session attached with it. The values of the CLI profiles are the ones written, never what a reference gives.
    /// </summary>
    [NeoRpcMethod("session")]
    public async Task<SessionIdentityResponse> SessionAsync(SessionIdentityRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, null, null, null, false, null, null, null, []);
        if (string.IsNullOrWhiteSpace(request.SessionId)) return new("invalid", null, null, null, false, null, null, null, []);
        var identity = await _session!(request.SessionId.Trim(), cancellationToken).ConfigureAwait(false);
        var git = identity.Identity?.Git;
        var author = git is null || git.Name is null && git.Email is null ? null
            : git.Email is null ? git.Name : git.Name is null ? $"<{git.Email}>" : $"{git.Name} <{git.Email}>";
        return new("ok", identity.SpaceId, author, SpaceGitIdentity.FormatSign(git?.Sign), git?.SignOff == true,
            identity.Environment.Get(SpaceIdentityEnvironment.GitHubConfigDirectoryVariable), identity.Identity?.Jira?.Site, identity.Identity?.Jira?.Email,
            [.. (identity.Identity?.Cli ?? []).Select(profile => new SessionCliProfileItem(profile.Name,
                identity.ProjectProfiles.Contains(profile.Name, StringComparer.OrdinalIgnoreCase) ? "project" : "space",
                [.. profile.Environment.Select(static variable => new SpaceCliVariableItem(variable.Key, variable.Value))],
                // A variable that refers to one that is not set is left out of the processes of the session.
                [.. profile.Environment.Where(variable => identity.Environment.Get(variable.Key) is null).Select(static variable => variable.Key)]))]);
    }

    private const string PartProblem = "The part of an identity is git, github, jira or cli.";

    private string? Refuse(string? expectedEpoch)
        => _catalog is null ? "unavailable" : string.Equals(expectedEpoch, _epoch, StringComparison.Ordinal) ? null : "stale_epoch";

    private static SpaceIdentityItem? Item(SpaceIdentity? identity)
        => identity is null ? null : new(
            identity.Git is { } git ? new(git.Name, git.Email, git.SshKey, SpaceGitIdentity.FormatSign(git.Sign), git.SigningKey, git.SignOff) : null,
            identity.GitHub is { } gitHub ? new(gitHub.ConfigDir) : null,
            identity.Jira is { } jira ? new(jira.Site, jira.Email, jira.TokenEnv) : null,
            identity.Cli is { Count: > 0 } cli ? [.. cli.Select(static profile => new SpaceCliProfileItem(profile.Name,
                [.. profile.Environment.Select(static variable => new SpaceCliVariableItem(variable.Key, variable.Value))], profile.Check, profile.Login))] : null);

    // What the page wrote: an empty field is the usual setting; a sign-off that is off is no line of the instructions.
    private static SpaceGitIdentity? Git(SpaceGitIdentityItem? item)
    {
        if (item is null) return null;
        var git = new SpaceGitIdentity(Blank(item.Name), Blank(item.Email), Blank(item.SshKey), SpaceGitIdentity.ParseSign(item.Sign), Blank(item.SigningKey),
            item.SignOff == true ? true : null);
        return git.IsEmpty ? null : git;
    }

    private static SpaceGitHubIdentity? GitHub(SpaceGitHubIdentityItem? item)
        => Blank(item?.ConfigDir) is { } folder ? new SpaceGitHubIdentity(folder) : null;

    private static SpaceJiraIdentity? Jira(SpaceJiraIdentityItem? item)
    {
        if (item is null) return null;
        var jira = new SpaceJiraIdentity(Blank(item.Site), Blank(item.Email), Blank(item.TokenEnv));
        return jira.IsEmpty ? null : jira;
    }

    // The profiles as the page wrote them, in its order; a variable without a name nor a value is a line left empty.
    private static IReadOnlyList<SpaceCliProfile>? Cli(IReadOnlyList<SpaceCliProfileItem>? items)
        => items is null || items.Count == 0 ? null : [.. items.Select(Profile)];

    private static SpaceCliProfile Profile(SpaceCliProfileItem item)
        => new(item.Name?.Trim() ?? string.Empty,
            [.. (item.Env ?? []).Where(static variable => Blank(variable.Name) is not null || Blank(variable.Value) is not null)
                .Select(static variable => new KeyValuePair<string, string>(variable.Name?.Trim() ?? string.Empty, variable.Value?.Trim() ?? string.Empty))],
            Blank(item.Check), Blank(item.Login));

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}

/// <summary>Asks for the identity of a space.</summary>
/// <param name="ExpectedEpoch">The host epoch.</param>
/// <param name="SpaceId">The identifier of the space.</param>
internal sealed record SpaceIdentityRequest(string ExpectedEpoch, string? SpaceId);

/// <summary>
/// The identity of a space. Status is <c>ok</c>, <c>unavailable</c>, <c>stale_epoch</c>, <c>not_found</c>,
/// <c>invalid</c> (with the message of what is wrong), <c>conflict</c>, <c>unreadable</c> or <c>write_failed</c>.
/// </summary>
/// <param name="Status">How the call ended.</param>
/// <param name="Message">What was wrong, for a change that was not made.</param>
/// <param name="Identity">The identity; null for one that sets nothing.</param>
/// <param name="Usual">The usual settings, which an empty field keeps; only in the answer of <c>get</c>.</param>
internal sealed record SpaceIdentityResponse(string Status, string? Message, SpaceIdentityItem? Identity, SpaceIdentityUsual? Usual);

/// <summary>The identity of a space, by card; a card that sets nothing is null.</summary>
/// <param name="Git">The Git card.</param>
/// <param name="GitHub">The GitHub card.</param>
/// <param name="Jira">The Jira card.</param>
/// <param name="Cli">The CLI profiles, in their order; null for none.</param>
internal sealed record SpaceIdentityItem(SpaceGitIdentityItem? Git, SpaceGitHubIdentityItem? GitHub, SpaceJiraIdentityItem? Jira, IReadOnlyList<SpaceCliProfileItem>? Cli);

/// <summary>The card of one CLI profile.</summary>
/// <param name="Name">The name of the profile, such as <c>azure</c>.</param>
/// <param name="Env">The variables it sets, in their order.</param>
/// <param name="Check">The command that tells who or what the tool is signed in to.</param>
/// <param name="Login">The command that signs the tool in with the profile.</param>
internal sealed record SpaceCliProfileItem(string? Name, IReadOnlyList<SpaceCliVariableItem>? Env, string? Check, string? Login);

/// <summary>One variable of a CLI profile: a path, a name, or <c>${env:NAME}</c>; never a secret.</summary>
/// <param name="Name">The name of the variable.</param>
/// <param name="Value">Its value, as written.</param>
internal sealed record SpaceCliVariableItem(string? Name, string? Value);

/// <summary>The Git card: an empty value is the usual setting.</summary>
/// <param name="Name">The name of the author and committer.</param>
/// <param name="Email">Their email.</param>
/// <param name="SshKey">The private key Git fetches and pushes with over SSH.</param>
/// <param name="Sign">How commits are signed: <c>off</c>, <c>ssh</c>, <c>gpg</c>; null for the usual way.</param>
/// <param name="SigningKey">The public SSH key file, or the GPG key id, commits are signed with.</param>
/// <param name="SignOff">Whether commits carry a <c>Signed-off-by</c> line.</param>
internal sealed record SpaceGitIdentityItem(string? Name, string? Email, string? SshKey, string? Sign, string? SigningKey, bool? SignOff);

/// <summary>The GitHub card.</summary>
/// <param name="ConfigDir">The folder of the profile of the GitHub CLI, given as <c>GH_CONFIG_DIR</c>.</param>
internal sealed record SpaceGitHubIdentityItem(string? ConfigDir);

/// <summary>The Jira card.</summary>
/// <param name="Site">The Jira site, such as <c>contoso.atlassian.net</c>.</param>
/// <param name="Email">The email of the account.</param>
/// <param name="TokenEnv">The name of the environment variable that holds the API token; never the token.</param>
internal sealed record SpaceJiraIdentityItem(string? Site, string? Email, string? TokenEnv);

/// <summary>Saves one card of the identity of a space.</summary>
/// <param name="ExpectedEpoch">The host epoch.</param>
/// <param name="SpaceId">The identifier of the space.</param>
/// <param name="Part">The card: <c>git</c>, <c>github</c>, <c>jira</c> or <c>cli</c>.</param>
/// <param name="Git">The Git card, for <c>git</c>; null or empty for the usual settings.</param>
/// <param name="GitHub">The GitHub card, for <c>github</c>.</param>
/// <param name="Jira">The Jira card, for <c>jira</c>.</param>
/// <param name="Cli">All the CLI profiles, for <c>cli</c>; null or empty for none.</param>
internal sealed record SpaceIdentityUpdateRequest(string ExpectedEpoch, string? SpaceId, string? Part, SpaceGitIdentityItem? Git, SpaceGitHubIdentityItem? GitHub,
    SpaceJiraIdentityItem? Jira, IReadOnlyList<SpaceCliProfileItem>? Cli = null);

/// <summary>Checks one card as the page shows it.</summary>
/// <param name="ExpectedEpoch">The host epoch.</param>
/// <param name="Part">The card: <c>git</c>, <c>github</c>, <c>jira</c> or <c>cli</c>.</param>
/// <param name="Git">The Git card, for <c>git</c>.</param>
/// <param name="GitHub">The GitHub card, for <c>github</c>.</param>
/// <param name="Jira">The Jira card, for <c>jira</c>.</param>
/// <param name="Profile">The CLI profile to check, for <c>cli</c>.</param>
internal sealed record SpaceIdentityCheckRequest(string ExpectedEpoch, string? Part, SpaceGitIdentityItem? Git, SpaceGitHubIdentityItem? GitHub, SpaceJiraIdentityItem? Jira,
    SpaceCliProfileItem? Profile = null);

/// <summary>What the check of a card found. Status is <c>ok</c>, <c>problem</c> (a line says what is wrong), <c>unavailable</c> or <c>stale_epoch</c>.</summary>
/// <param name="Status">How the check ended.</param>
/// <param name="Lines">What it found, in order.</param>
internal sealed record SpaceIdentityCheckResponse(string Status, IReadOnlyList<IdentityCheckLine> Lines);

/// <summary>Asks which identity a session works under.</summary>
/// <param name="ExpectedEpoch">The host epoch.</param>
/// <param name="SessionId">The session.</param>
internal sealed record SessionIdentityRequest(string ExpectedEpoch, string? SessionId);

/// <summary>The identity a session works under; a value that is null is the usual setting.</summary>
/// <param name="Status">Is <c>ok</c>, <c>invalid</c>, <c>unavailable</c> or <c>stale_epoch</c>.</param>
/// <param name="SpaceId">The space the identity is of.</param>
/// <param name="GitAuthor">The author and committer, <c>Name &lt;email&gt;</c>.</param>
/// <param name="Signing">How its commits are signed: <c>off</c>, <c>ssh</c> or <c>gpg</c>.</param>
/// <param name="SignOff">Whether its commits are signed off.</param>
/// <param name="GitHubProfile">The folder of the profile of the GitHub CLI.</param>
/// <param name="JiraSite">The Jira site of its space.</param>
/// <param name="JiraEmail">The email of the Jira account of its space.</param>
/// <param name="Cli">The CLI profiles it works under, in their order.</param>
internal sealed record SessionIdentityResponse(string Status, string? SpaceId, string? GitAuthor, string? Signing, bool SignOff, string? GitHubProfile, string? JiraSite,
    string? JiraEmail, IReadOnlyList<SessionCliProfileItem> Cli);

/// <summary>A CLI profile a session works under.</summary>
/// <param name="Name">The name of the profile.</param>
/// <param name="Source"><c>space</c>, or <c>project</c> when the project of the session gives or changes it.</param>
/// <param name="Env">Its variables, as written: a reference <c>${env:NAME}</c> is shown, never what it gives.</param>
/// <param name="Unset">The variables left out because a variable they refer to is not set.</param>
internal sealed record SessionCliProfileItem(string Name, string Source, IReadOnlyList<SpaceCliVariableItem> Env, IReadOnlyList<string> Unset);
