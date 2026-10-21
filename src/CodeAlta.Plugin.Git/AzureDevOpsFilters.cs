using System.Globalization;
using System.Text;
using System.Text.Json;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugin.Git;

/// <summary>
/// The filters of the work items of an Azure DevOps project: the team, the sprint of the team, who an item is assigned
/// to, and its type.
/// </summary>
/// <remarks>
/// The macros of the web portal (<c>@CurrentIteration</c>, <c>@TeamAreas</c>) are not documented for queries sent
/// through the REST API, so a team and a sprint are read from the settings of the team and written as explicit paths.
/// </remarks>
internal sealed partial class AzureDevOpsApi
{
    /// <summary>The id of the filter of the team.</summary>
    public const string TeamFilter = "team";

    /// <summary>The id of the filter of the sprint.</summary>
    public const string SprintFilter = "sprint";

    /// <summary>The id of the filter of who an item is assigned to.</summary>
    public const string AssignedFilter = "assigned_to";

    /// <summary>The id of the filter of the type of an item.</summary>
    public const string TypeFilter = "type";

    /// <summary>The value of the sprint that runs today.</summary>
    public const string CurrentSprint = "@current";

    /// <summary>The value of the sprint after the one that runs today.</summary>
    public const string NextSprint = "@next";

    /// <summary>The value of the sprint before the one that runs today.</summary>
    public const string PreviousSprint = "@previous";

    /// <summary>The value of the items assigned to the signed-in user.</summary>
    public const string Me = "@me";

    /// <summary>The value of the items assigned to nobody.</summary>
    public const string Nobody = "@none";

    private const int MaximumTeams = 200;
    private const int OfferedPastSprints = 6;

    private sealed record Team(string Id, string Name, bool Mine);

    private sealed record TeamField(string Field, IReadOnlyList<(string Value, bool Children)> Values);

    private sealed record Iteration(string Id, string Name, string Path, DateTimeOffset? Start, DateTimeOffset? Finish, string? TimeFrame);

    public override async Task<IReadOnlyList<TrackedFilterDescriptor>> FiltersAsync(GitIssueQuery query, CancellationToken cancellationToken)
    {
        var filters = new List<TrackedFilterDescriptor>(4);
        var teams = await TeamsAsync(query, cancellationToken).ConfigureAwait(false);
        filters.Add(new(TeamFilter, "Team", teams is null ? [] : [.. teams.Select(static team => new TrackedFilterValue(team.Id, team.Name) { Detail = team.Mine ? "Your team" : null })])
        {
            Problem = teams is null ? "The teams of the project could not be read." : null,
        });

        var team = ChosenTeam(query, teams);
        var iterations = await IterationsAsync(query, team, cancellationToken).ConfigureAwait(false);
        var sprints = new List<TrackedFilterValue>();
        if (iterations is not null)
        {
            sprints.Add(new(CurrentSprint, "Current sprint") { Detail = Pick(iterations, CurrentSprint) is { } current ? Describe(current) : "None today" });
            sprints.Add(new(NextSprint, "Next sprint") { Detail = Pick(iterations, NextSprint) is { } next ? Describe(next) : null });
            sprints.Add(new(PreviousSprint, "Previous sprint") { Detail = Pick(iterations, PreviousSprint) is { } previous ? Describe(previous) : null });
            // A team keeps every sprint it ever had: the last few that ended are offered, and those to come.
            var past = iterations.Where(static iteration => iteration.TimeFrame == "past").OrderByDescending(static iteration => iteration.Finish ?? DateTimeOffset.MinValue)
                .Take(OfferedPastSprints).ToHashSet();
            sprints.AddRange(iterations.Where(iteration => iteration.TimeFrame != "past" || past.Contains(iteration))
                .Select(static iteration => new TrackedFilterValue(iteration.Id, iteration.Name) { Detail = Dates(iteration) }));
        }

        filters.Add(new(SprintFilter, "Sprint", sprints)
        {
            DependsOn = [TeamFilter],
            Problem = iterations is null ? "The sprints of the team could not be read." : null,
        });
        filters.Add(new(AssignedFilter, "Assigned to", [new(Me, "Me"), new(Nobody, "Nobody")]));

        var types = await TypesAsync(query, cancellationToken).ConfigureAwait(false);
        filters.Add(new(TypeFilter, "Type", types is null ? [] : [.. types.Select(static type => new TrackedFilterValue(type, type))])
        {
            Problem = types is null ? "The work item types of the project could not be read." : null,
            Multiple = true,
        });
        filters.Add(await SortFilterAsync(query, cancellationToken).ConfigureAwait(false));
        filters.AddRange(await FieldFiltersAsync(query, cancellationToken).ConfigureAwait(false));
        return filters;
    }

    /// <summary>
    /// Gets the clauses of the values chosen for the filters, each one starting with <c> AND</c>; empty for none, null
    /// when a value cannot be applied (the team has no current sprint, its areas could not be read).
    /// </summary>
    private static async Task<string?> FilterClausesAsync(GitIssueQuery query, CancellationToken cancellationToken)
    {
        var filters = query.Filters;
        if (filters.Count == 0) return string.Empty;
        var clauses = new StringBuilder();
        var team = filters.TryGetValue(TeamFilter, out var teamId) && teamId.Length > 0 ? teamId : null;
        if (team is not null)
        {
            if (await TeamFieldAsync(query, team, cancellationToken).ConfigureAwait(false) is not { Values.Count: > 0 } field) return null;
            clauses.Append(" AND (")
                .AppendJoin(" OR ", field.Values.Select(value => $"[{field.Field}] {(value.Children ? "UNDER" : "=")} {Quote(value.Value)}"))
                .Append(')');
        }

        if (filters.TryGetValue(SprintFilter, out var sprint) && sprint.Length > 0)
        {
            if (await IterationsAsync(query, team, cancellationToken).ConfigureAwait(false) is not { } iterations || Pick(iterations, sprint) is not { } iteration) return null;
            clauses.Append(" AND [System.IterationPath] = ").Append(Quote(iteration.Path));
        }

        if (filters.TryGetValue(AssignedFilter, out var assigned))
        {
            // A value the filter does not offer lists nothing, as for the other filters.
            if (assigned is not (Me or Nobody)) return null;
            clauses.Append(assigned == Me ? " AND [System.AssignedTo] = @Me" : " AND [System.AssignedTo] = ''");
        }

        if (filters.TryGetValue(TypeFilter, out var type) && TrackedFilters.Split(type) is { Count: > 0 } chosenTypes)
        {
            clauses.Append(" AND [System.WorkItemType] IN (").AppendJoin(", ", chosenTypes.Select(Quote)).Append(')');
        }

        return await AppendFieldClausesAsync(query, clauses, cancellationToken).ConfigureAwait(false) ? clauses.ToString() : null;
    }

    /// <summary>Writes a text as a WIQL literal: in single quotes, each single quote doubled.</summary>
    internal static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    // The team the sprints are of: the one chosen when the project has it, otherwise the default team of the project.
    private static string? ChosenTeam(GitIssueQuery query, IReadOnlyList<Team>? teams)
        => query.Filters.TryGetValue(TeamFilter, out var id) && id.Length > 0 && (teams is null || teams.Any(team => team.Id == id)) ? id : null;

    private static Iteration? Pick(IReadOnlyList<Iteration> iterations, string value)
    {
        switch (value)
        {
            case CurrentSprint:
                return iterations.FirstOrDefault(static iteration => iteration.TimeFrame == "current");
            case NextSprint:
                return iterations.Where(static iteration => iteration.TimeFrame == "future").OrderBy(static iteration => iteration.Start ?? DateTimeOffset.MaxValue).FirstOrDefault();
            case PreviousSprint:
                return iterations.Where(static iteration => iteration.TimeFrame == "past").OrderByDescending(static iteration => iteration.Finish ?? DateTimeOffset.MinValue).FirstOrDefault();
            default:
                return iterations.FirstOrDefault(iteration => string.Equals(iteration.Id, value, StringComparison.OrdinalIgnoreCase));
        }
    }

    private static string Describe(Iteration iteration) => Dates(iteration) is { } dates ? $"{iteration.Name} · {dates}" : iteration.Name;

    private static string? Dates(Iteration iteration)
        => iteration.Start is { } start && iteration.Finish is { } finish
            ? string.Create(CultureInfo.InvariantCulture, $"{start:yyyy-MM-dd} – {finish:yyyy-MM-dd}")
            : null;

    private static string Organization(GitRepositoryReference repository) => $"https://{repository.Host}/{Escape(repository.Owner)}";

    // The teams of the user first, then the others the user can read.
    private static Task<IReadOnlyList<Team>?> TeamsAsync(GitIssueQuery query, CancellationToken cancellationToken)
    {
        var repository = query.Repository;
        var url = FormattableString.Invariant($"{Organization(repository)}/_apis/projects/{Escape(repository.Project ?? repository.Name)}/teams?$top={MaximumTeams}&api-version={ApiVersion}");
        return CachedAsync<IReadOnlyList<Team>>(query, url, async () =>
        {
            using var mine = await GetAnswerAsync(query, url + "&$mine=true", cancellationToken, report: false).ConfigureAwait(false);
            using var all = await GetAnswerAsync(query, url, cancellationToken, report: false).ConfigureAwait(false);
            if (all is null) return null;
            var own = mine is null ? [] : Items(mine.RootElement, "value").Select(static team => Text(team, "id")).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
            return [.. Items(all.RootElement, "value")
                .Select(team => Text(team, "id") is { } id && Text(team, "name") is { } name ? new Team(id, name, own.Contains(id)) : null).OfType<Team>()
                .OrderByDescending(static team => team.Mine).ThenBy(static team => team.Name, StringComparer.CurrentCultureIgnoreCase)];
        });
    }

    private static Task<TeamField?> TeamFieldAsync(GitIssueQuery query, string team, CancellationToken cancellationToken)
    {
        var url = FormattableString.Invariant($"{Project(query.Repository)}/{Escape(team)}/_apis/work/teamsettings/teamfieldvalues?api-version={ApiVersion}");
        return CachedAsync(query, url, async () =>
        {
            using var document = await GetAnswerAsync(query, url, cancellationToken, report: false).ConfigureAwait(false);
            if (document is null) return null;
            // The field is a reference name the query writes in brackets: anything else is not trusted there.
            var field = Text(document.RootElement, "field", "referenceName") ?? "System.AreaPath";
            if (!field.All(static ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_')) return null;
            return new TeamField(field, [.. Items(document.RootElement, "values")
                .Select(static value => Text(value, "value") is { } path ? (path, Flag(value, "includeChildren")) : ((string, bool)?)null).OfType<(string, bool)>()]);
        });
    }

    // The sprints the team chose, or those of the default team of the project.
    private static Task<IReadOnlyList<Iteration>?> IterationsAsync(GitIssueQuery query, string? team, CancellationToken cancellationToken)
    {
        var scope = team is null ? Project(query.Repository) : $"{Project(query.Repository)}/{Escape(team)}";
        var url = FormattableString.Invariant($"{scope}/_apis/work/teamsettings/iterations?api-version={ApiVersion}");
        return CachedAsync<IReadOnlyList<Iteration>>(query, url, async () =>
        {
            using var document = await GetAnswerAsync(query, url, cancellationToken, report: false).ConfigureAwait(false);
            if (document is null) return null;
            return [.. Items(document.RootElement, "value")
                .Select(static element => Text(element, "id") is { } id && Text(element, "name") is { } name && Text(element, "path") is { } path
                    ? new Iteration(id, name, path, Date(element, "attributes", "startDate"), Date(element, "attributes", "finishDate"), TimeFrame(At(element, "attributes", "timeFrame")))
                    : null)
                .OfType<Iteration>()
                .OrderBy(static iteration => iteration.Start ?? DateTimeOffset.MaxValue)];
        });
    }

    // The types the project uses: not the disabled ones, nor those of the hidden category (code reviews, feedback, shared steps).
    private static Task<IReadOnlyList<string>?> TypesAsync(GitIssueQuery query, CancellationToken cancellationToken)
    {
        var url = FormattableString.Invariant($"{Project(query.Repository)}/_apis/wit/workitemtypes?api-version={ApiVersion}");
        return CachedAsync<IReadOnlyList<string>>(query, url, async () =>
        {
            using var types = await GetAnswerAsync(query, url, cancellationToken, report: false).ConfigureAwait(false);
            if (types is null) return null;
            using var categories = await GetAnswerAsync(query, FormattableString.Invariant($"{Project(query.Repository)}/_apis/wit/workitemtypecategories?api-version={ApiVersion}"), cancellationToken, report: false).ConfigureAwait(false);
            var hidden = categories is null ? [] : Items(categories.RootElement, "value")
                .Where(static category => Text(category, "referenceName") == "Microsoft.HiddenCategory")
                .SelectMany(static category => Items(category, "workItemTypes")).Select(static type => Text(type, "name")).OfType<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return [.. Items(types.RootElement, "value")
                .Where(static type => !Flag(type, "isDisabled")).Select(static type => Text(type, "name")).OfType<string>()
                .Where(name => !hidden.Contains(name)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase)];
        });
    }

    // The service writes the time frame as a name or, in older versions, as its number.
    private static string? TimeFrame(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()?.ToLowerInvariant(),
            JsonValueKind.Number when value.TryGetInt32(out var number) => number switch { 0 => "past", 1 => "current", 2 => "future", _ => null },
            _ => null,
        };

    private static Task<T?> CachedAsync<T>(GitIssueQuery query, string url, Func<Task<T?>> read) where T : class
        => query.Cache is { } cache ? cache.GetAsync(url, read) : read();
}
