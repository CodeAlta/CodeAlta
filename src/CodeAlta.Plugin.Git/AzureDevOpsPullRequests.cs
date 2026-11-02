using System.Globalization;
using System.Text.Json;
using CodeAlta.Plugins.Abstractions;
using XenoAtom.Logging;

namespace CodeAlta.Plugin.Git;

/// <summary>
/// The pull requests of Azure DevOps with filters: where they are read from (the repository, its project, the whole
/// organization), whose review they wait for, their votes, their author, their target branch and whether they are drafts.
/// </summary>
/// <remarks>
/// The service filters reviewers by one identity only (<c>searchCriteria.reviewerId</c>), without the groups the user is
/// in, so the pull requests are read and filtered here. The groups of the user, nested ones included, come from the
/// identities of the organization (<c>queryMembership=Expanded</c>), which only the <c>vssps</c> host answers.
/// </remarks>
internal sealed partial class AzureDevOpsApi
{
    /// <summary>The id of the filter of where the pull requests are read from.</summary>
    public const string ScopeFilter = "scope";

    /// <summary>The value of the pull requests of every repository of the project.</summary>
    public const string ProjectScope = "@project";

    /// <summary>The value of the pull requests of every project of the organization.</summary>
    public const string OrganizationScope = "@organization";

    /// <summary>The id of the filter of whose review a pull request asks for.</summary>
    public const string ReviewerFilter = "reviewer";

    /// <summary>The value of the reviews asked of the user or of a group the user is in, directly or through other groups.</summary>
    public const string MeOrMyGroups = "@me_groups";

    /// <summary>The id of the filter of whether the review is required.</summary>
    public const string RequiredFilter = "required";

    /// <summary>The value of the reviews that are required.</summary>
    public const string RequiredReview = "@required";

    /// <summary>The value of the reviews that are optional.</summary>
    public const string OptionalReview = "@optional";

    /// <summary>The id of the filter of the vote of the review.</summary>
    public const string VoteFilter = "vote";

    /// <summary>The id of the filter of who opened a pull request.</summary>
    public const string AuthorFilter = "author";

    /// <summary>The value of the pull requests the user did not open.</summary>
    public const string NotMe = "@not_me";

    /// <summary>The id of the filter of the branch a pull request goes to.</summary>
    public const string TargetBranchFilter = "target_branch";

    /// <summary>The id of the filter of the drafts.</summary>
    public const string DraftFilter = "draft";

    /// <summary>The value that leaves the drafts out.</summary>
    public const string HideDrafts = "@hide";

    /// <summary>The value that lists the drafts alone.</summary>
    public const string OnlyDrafts = "@only";

    /// <summary>The order of the oldest pull requests first.</summary>
    public const string OldestFirst = "@oldest";

    // The most pull requests a filtered listing reads, and how many one request asks for.
    private const int MaximumScanned = 1000;
    private const int ScanPage = 200;

    // How many descriptors one request of identities resolves: they are long, and they are written in its address.
    private const int DescriptorsPerRequest = 20;

    private static readonly (string Id, string Label, TrackedVote Vote)[] Votes =
    [
        (Nobody, "No vote yet", TrackedVote.None),
        ("@approved", "Approved", TrackedVote.Approved),
        ("@suggestions", "Approved with suggestions", TrackedVote.ApprovedWithSuggestions),
        ("@waiting", "Waiting for author", TrackedVote.WaitingForAuthor),
        ("@rejected", "Rejected", TrackedVote.Rejected),
    ];

    private const string NoGroups = "Your groups could not be read from Azure DevOps: sign in again with az login, or give the token Identity (Read).";

    /// <summary>The signed-in user and the groups the user is in, directly or through other groups, by identity id.</summary>
    private sealed record Membership(string Id, IReadOnlyDictionary<string, string> Groups);

    /// <summary>The target branches of the open pull requests, and whether there were more pull requests than were read.</summary>
    private sealed record TargetBranches(IReadOnlyList<string> Names, bool Capped);

    private static async Task<IReadOnlyList<TrackedFilterDescriptor>> PullRequestFiltersAsync(GitIssueQuery query, CancellationToken cancellationToken)
    {
        var membership = await TryMembershipAsync(query, cancellationToken).ConfigureAwait(false);
        var noGroups = membership is null ? NoGroups : null;
        var branches = await TargetBranchesAsync(query, cancellationToken).ConfigureAwait(false);
        return
        [
            new(ScopeFilter, "Scope", [new(ProjectScope, "This project") { Detail = "Every repository of the project" }, new(OrganizationScope, "Organization") { Detail = "Every project" }])
            {
                AllLabel = "This repository",
            },
            new(ReviewerFilter, "Reviewer", [new(Me, "Me"), new(MeOrMyGroups, "Me or my groups") { Detail = "Also through groups in groups" }]) { Problem = noGroups },
            new(RequiredFilter, "Required", [new(RequiredReview, "Required"), new(OptionalReview, "Optional")]) { Problem = noGroups },
            new(VoteFilter, "Vote", [.. Votes.Select(static vote => new TrackedFilterValue(vote.Id, vote.Label))]) { Multiple = true, Problem = noGroups },
            new(AuthorFilter, "Author", [new(Me, "Me"), new(NotMe, "Not me")]) { Problem = noGroups },
            new(TargetBranchFilter, "Target branch", branches is null ? [] : [.. branches.Names.Select(static branch => new TrackedFilterValue(branch, branch))])
            {
                DependsOn = [ScopeFilter],
                Multiple = true,
                Problem = branches is null ? "The pull requests could not be read for their target branches."
                    : branches.Capped ? FormattableString.Invariant($"Only the target branches of the {MaximumScanned} most recent open pull requests are offered.") : null,
            },
            new(DraftFilter, "Drafts", [new(HideDrafts, "Hide drafts"), new(OnlyDrafts, "Only drafts")]),
            new(TrackedFilters.Sort, "Order", [new(OldestFirst, "Oldest first")]) { AllLabel = "Newest first" },
        ];
    }

    // The pull requests of the scope chosen, filtered as the filters say, in the order chosen.
    private static async Task<IReadOnlyList<TrackedItem>> ListPullsAsync(GitIssueQuery query, TrackedItemFilter filter, string? text, int limit, CancellationToken cancellationToken)
    {
        var repository = query.Repository;
        var filters = query.Filters;
        var status = filter switch { TrackedItemFilter.Open => "active", TrackedItemFilter.Closed => "abandoned", TrackedItemFilter.Merged => "completed", _ => "all" };
        var scope = filters.GetValueOrDefault(ScopeFilter);
        if (scope is { Length: > 0 } and not (ProjectScope or OrganizationScope)) return [];
        var reviews = filters.GetValueOrDefault(ReviewerFilter) is { Length: > 0 } || filters.GetValueOrDefault(RequiredFilter) is { Length: > 0 }
            || filters.GetValueOrDefault(VoteFilter) is { Length: > 0 };
        var filtered = reviews || scope is { Length: > 0 } || filters.Keys.Any(static id => id is AuthorFilter or TargetBranchFilter or DraftFilter or TrackedFilters.Sort);
        Membership? membership = null;
        if (reviews || filters.GetValueOrDefault(AuthorFilter) is { Length: > 0 })
        {
            // A filter of the user that cannot be applied lists nothing rather than every pull request, and says why.
            if ((membership = await TryMembershipAsync(query, cancellationToken).ConfigureAwait(false)) is null)
            {
                query.Incomplete = NoGroups;
                return [];
            }
        }

        // Without a filter the service gives the most recent ones itself; words alone are looked for in the titles of the
        // last hundred, as before there were filters.
        var wanted = filtered ? MaximumScanned : text is not null ? Math.Max(limit, 100) : limit;
        var pulls = new List<TrackedItem>();
        var whole = true;
        for (var skip = 0; skip < wanted; skip += ScanPage)
        {
            var top = Math.Min(ScanPage, wanted - skip);
            // The service may have more than the most that are read: what is listed is then of the most recent ones only.
            whole = skip + top < wanted;
            var url = FormattableString.Invariant($"{PullsRoot(repository, scope)}?searchCriteria.status={status}&$top={top}&$skip={skip}&api-version={ApiVersion}");
            using var document = await GetAnswerAsync(query, url, cancellationToken).ConfigureAwait(false);
            if (document is null) break;
            var page = Items(document.RootElement, "value").ToArray();
            foreach (var element in page)
            {
                if (ReadPull(element, repository, membership) is not { } pull || !Keeps(pull, element, filters, membership, out var match)) continue;
                if (text is not null && !pull.Title.Contains(text, StringComparison.OrdinalIgnoreCase) && pull.Id != text.TrimStart('#', '!')) continue;
                pulls.Add(match is null ? pull : pull with { Match = match });
            }

            if (page.Length < top || !filtered && text is null)
            {
                whole = true;
                break;
            }
        }

        if (filtered && !whole)
        {
            query.Incomplete = FormattableString.Invariant($"Only the {MaximumScanned} most recent pull requests were searched: older ones are not listed, nor ordered.");
        }

        var ordered = filters.GetValueOrDefault(TrackedFilters.Sort) == OldestFirst
            ? pulls.OrderBy(static item => item.CreatedAt ?? DateTimeOffset.MaxValue)
            : pulls.OrderByDescending(static item => item.UpdatedAt);
        return [.. ordered.Take(limit)];
    }

    // Where the pull requests of a scope are listed: the repository, its project, or the organization.
    private static string PullsRoot(GitRepositoryReference repository, string? scope)
        => scope switch
        {
            ProjectScope => $"{Project(repository)}/_apis/git/pullrequests",
            OrganizationScope => $"{Organization(repository)}/_apis/git/pullrequests",
            _ => $"{Project(repository)}/_apis/git/repositories/{Escape(repository.Name)}/pullrequests",
        };

    // Whether a pull request passes the filters; a review that matched says why in words.
    private static bool Keeps(TrackedItem pull, JsonElement element, IReadOnlyDictionary<string, string> filters, Membership? membership, out string? match)
    {
        match = null;
        if (filters.GetValueOrDefault(DraftFilter) is { Length: > 0 } draft && (draft == OnlyDrafts) != (pull.State == TrackedItemState.Draft)) return false;
        if (filters.GetValueOrDefault(TargetBranchFilter) is { Length: > 0 } targets && !TrackedFilters.Split(targets).Contains(pull.TargetBranch ?? string.Empty, StringComparer.Ordinal)) return false;
        if (filters.GetValueOrDefault(AuthorFilter) is { Length: > 0 } author)
        {
            if (membership is null) return false;
            var mine = string.Equals(Text(element, "createdBy", "id"), membership.Id, StringComparison.OrdinalIgnoreCase);
            if (author == Me ? !mine : author == NotMe ? mine : true) return false;
        }

        var reviewer = filters.GetValueOrDefault(ReviewerFilter) ?? string.Empty;
        var required = filters.GetValueOrDefault(RequiredFilter) ?? string.Empty;
        var votes = TrackedFilters.Split(filters.GetValueOrDefault(VoteFilter) ?? string.Empty);
        if (reviewer.Length == 0 && required.Length == 0 && votes.Count == 0) return true;
        // A value the filter does not offer lists nothing, as for the other filters.
        if (membership is null || reviewer is not ("" or Me or MeOrMyGroups) || required is not ("" or RequiredReview or OptionalReview)) return false;
        // The review that is asked: one entry of the reviewers is the user, or a group of the user, with the vote and the
        // requirement asked. A group a colleague already voted for has the colleague's vote.
        foreach (var entry in Items(element, "reviewers"))
        {
            if (Text(entry, "id") is not { } id) continue;
            var direct = string.Equals(id, membership.Id, StringComparison.OrdinalIgnoreCase);
            var group = membership.Groups.GetValueOrDefault(id);
            if (!direct && (reviewer == Me || group is null)) continue;
            var isRequired = Flag(entry, "isRequired");
            if (required == RequiredReview && !isRequired || required == OptionalReview && isRequired) continue;
            var vote = VoteOf(Number(entry, "vote"));
            if (votes.Count > 0 && !votes.Any(chosen => Votes.Any(known => known.Id == chosen && known.Vote == vote))) continue;
            match = string.Join(" · ", new[]
            {
                isRequired ? "Required" : "Optional",
                direct ? null : "through " + GroupName(group!),
                Votes.First(known => known.Vote == vote).Label,
            }.OfType<string>());
            return true;
        }

        return false;
    }

    // The name of a group as people say it: [Project]\Platform is Platform.
    private static string GroupName(string name) => name.LastIndexOf('\\') is var at and >= 0 && at < name.Length - 1 ? name[(at + 1)..] : name;

    private static TrackedVote VoteOf(int? vote)
        => vote switch
        {
            >= 10 => TrackedVote.Approved,
            > 0 => TrackedVote.ApprovedWithSuggestions,
            <= -10 => TrackedVote.Rejected,
            < 0 => TrackedVote.WaitingForAuthor,
            _ => TrackedVote.None,
        };

    private static IReadOnlyList<TrackedReviewer> Reviewers(JsonElement element, Membership? membership)
        => [.. Items(element, "reviewers").Select(entry => Text(entry, "displayName") is { } name
            ? new TrackedReviewer(Flag(entry, "isContainer") ? GroupName(name) : name, VoteOf(Number(entry, "vote")))
            {
                IsGroup = Flag(entry, "isContainer"),
                Required = Flag(entry, "isRequired"),
                Mine = membership is not null && Text(entry, "id") is { } id && (string.Equals(id, membership.Id, StringComparison.OrdinalIgnoreCase) || membership.Groups.ContainsKey(id)),
            }
            : null).OfType<TrackedReviewer>()];

    // The target branches of the open pull requests of the scope chosen: a repository has many branches, few are targets.
    private static Task<TargetBranches?> TargetBranchesAsync(GitIssueQuery query, CancellationToken cancellationToken)
    {
        var root = FormattableString.Invariant($"{PullsRoot(query.Repository, query.Filters.GetValueOrDefault(ScopeFilter))}?searchCriteria.status=active");
        return CachedAsync(query, root + " targets", cancellationToken, async () =>
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            for (var skip = 0; skip < MaximumScanned; skip += ScanPage)
            {
                using var document = await GetAnswerAsync(query, FormattableString.Invariant($"{root}&$top={ScanPage}&$skip={skip}&api-version={ApiVersion}"), cancellationToken, report: false)
                    .ConfigureAwait(false);
                if (document is null) return skip == 0 ? null : new TargetBranches([.. names.Order(StringComparer.OrdinalIgnoreCase)], true);
                var page = Items(document.RootElement, "value").ToArray();
                names.UnionWith(page.Select(static pull => Branch(Text(pull, "targetRefName"))).OfType<string>());
                if (page.Length < ScanPage) return new TargetBranches([.. names.Order(StringComparer.OrdinalIgnoreCase)], false);
            }

            return new TargetBranches([.. names.Order(StringComparer.OrdinalIgnoreCase)], true);
        });
    }

    // The membership of the user, or null when the service could not be reached for it: who the user is only marks the
    // reviews of the user, and a failure there does not fail what does not need it.
    private static async Task<Membership?> TryMembershipAsync(GitIssueQuery query, CancellationToken cancellationToken)
    {
        try
        {
            return await MembershipAsync(query, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException
            || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            GitIssueLog.Log?.Warn(exception, "Azure DevOps: the groups of the user could not be read.");
            return null;
        }
    }

    // The signed-in user and every group the user is in; null when they cannot be read.
    private static Task<Membership?> MembershipAsync(GitIssueQuery query, CancellationToken cancellationToken)
    {
        var organization = Organization(query.Repository);
        return CachedAsync(query, organization + " membership", cancellationToken, async () =>
        {
            using var connection = await GetAnswerAsync(query, $"{organization}/_apis/connectionData", cancellationToken, report: false).ConfigureAwait(false);
            if (connection is null || Text(connection.RootElement, "authenticatedUser", "id") is not { } me) return null;
            // The identities of an organization are on its vssps host: dev.azure.com/{org} answers its sign-in page.
            var identities = FormattableString.Invariant($"https://vssps.{query.Repository.Host}/{Escape(query.Repository.Owner)}/_apis/identities");
            using var expanded = await GetAnswerAsync(query, $"{identities}?identityIds={Escape(me)}&queryMembership=Expanded&api-version={ApiVersion}", cancellationToken, report: false).ConfigureAwait(false);
            if (expanded is null) return null;
            var descriptors = Items(expanded.RootElement, "value").SelectMany(static identity => Items(identity, "memberOf"))
                .Select(static descriptor => descriptor.ValueKind == JsonValueKind.String ? descriptor.GetString() : null).OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
            var groups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var batch in descriptors.Chunk(DescriptorsPerRequest))
            {
                using var resolved = await GetAnswerAsync(query, $"{identities}?descriptors={Escape(string.Join(',', batch))}&api-version={ApiVersion}", cancellationToken, report: false).ConfigureAwait(false);
                if (resolved is null) return null;
                foreach (var group in Items(resolved.RootElement, "value"))
                {
                    if (Text(group, "id") is { } id) groups[id] = Text(group, "providerDisplayName") ?? id;
                }
            }

            return new Membership(me, groups);
        });
    }

    // Reads a pull request by its id, which is unique in the organization: one of another repository is read too.
    private static async Task<TrackedItemDetail?> ReadPullAsync(GitIssueQuery query, int number, CancellationToken cancellationToken)
    {
        var repository = query.Repository;
        using var document = await GetAnswerAsync(query, FormattableString.Invariant($"{Organization(repository)}/_apis/git/pullrequests/{number}?api-version={ApiVersion}"), cancellationToken).ConfigureAwait(false);
        if (document is null) return null;
        // Who the user is only marks the reviews of the user: a pull request is read without it.
        var membership = await TryMembershipAsync(query, cancellationToken).ConfigureAwait(false);
        if (ReadPull(document.RootElement, repository, membership) is not { } pull) return null;
        var comments = new List<TrackedComment>();
        var project = Text(document.RootElement, "repository", "project", "id") ?? repository.Project ?? repository.Name;
        var repositoryId = Text(document.RootElement, "repository", "id") ?? repository.Name;
        var threadsUrl = FormattableString.Invariant($"{Organization(repository)}/{Escape(project)}/_apis/git/repositories/{Escape(repositoryId)}/pullrequests/{number}/threads?api-version={ApiVersion}");
        using var threads = await GetAnswerAsync(query, threadsUrl, cancellationToken, report: false).ConfigureAwait(false);
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

    private static bool Names(string written, string name, string? id)
        => string.Equals(written, name, StringComparison.OrdinalIgnoreCase) || id is not null && string.Equals(written, id, StringComparison.OrdinalIgnoreCase);

    private static TrackedItem? ReadPull(JsonElement element, GitRepositoryReference repository, Membership? membership)
    {
        if (Number(element, "pullRequestId") is not { } id || Text(element, "title") is not { } title) return null;
        var status = Text(element, "status");
        // A pull request of a listing of the project or the organization can be of another repository: its own address and place.
        var project = Text(element, "repository", "project", "name") ?? repository.Project ?? repository.Name;
        var name = Text(element, "repository", "name") ?? repository.Name;
        // A remote names its project and repository by their names or by their ids.
        var elsewhere = !Names(repository.Name, name, Text(element, "repository", "id"))
            || !Names(repository.Project ?? repository.Name, project, Text(element, "repository", "project", "id"));
        var web = elsewhere ? $"{Organization(repository)}/{Escape(project)}/_git/{Escape(name)}" : Instance.WebUrl(repository);
        return new(TrackedItemKind.PullRequest, id.ToString(CultureInfo.InvariantCulture), title, FormattableString.Invariant($"{web}/pullrequest/{id}"),
            status == "completed" ? TrackedItemState.Merged : status == "abandoned" ? TrackedItemState.Closed : Flag(element, "isDraft") ? TrackedItemState.Draft : TrackedItemState.Open)
        {
            Author = Text(element, "createdBy", "displayName"),
            Assignees = [.. Items(element, "reviewers").Where(static reviewer => !Flag(reviewer, "isContainer")).Select(static reviewer => Text(reviewer, "displayName")).OfType<string>()],
            Reviewers = Reviewers(element, membership),
            Labels = [.. Items(element, "labels").Select(static label => Text(label, "name") is { } labelName ? new TrackedLabel(labelName) : null).OfType<TrackedLabel>()],
            CreatedAt = Date(element, "creationDate"),
            UpdatedAt = Date(element, "closedDate") ?? Date(element, "creationDate"),
            SourceBranch = Branch(Text(element, "sourceRefName")),
            TargetBranch = Branch(Text(element, "targetRefName")),
            Place = elsewhere ? $"{project}/{name}" : null,
        };
    }
}
