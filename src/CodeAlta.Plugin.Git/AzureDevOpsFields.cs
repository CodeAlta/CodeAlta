using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugin.Git;

/// <summary>
/// Filters made of the fields of the work items of an Azure DevOps project, its own fields included: a field the
/// configuration names (<c>field:Microsoft.VSTS.Common.Priority</c>) offers the values the process allows for it, or
/// takes a text when it allows any.
/// </summary>
internal sealed partial class AzureDevOpsApi
{
    /// <summary>What the id of a filter made of a field starts with.</summary>
    public const string FieldPrefix = "field:";

    private const int MaximumFieldValues = 300;
    private const int MaximumAreaDepth = 4;

    // The fields the filters of the tracker already cover, and those whose value names the item rather than sorts it.
    private static readonly FrozenSet<string> OwnFields = new[] {"System.TeamProject", "System.IterationPath", "System.WorkItemType", "System.AssignedTo", "System.Id", "System.Title",
        "System.Rev", "System.Watermark", "System.Description", "System.History", "System.AreaId", "System.IterationId", "System.NodeName", "System.AuthorizedAs", "System.PersonId",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>A field of the project as the service describes it.</summary>
    /// <param name="Name">Its reference name, as queries write it.</param>
    /// <param name="Label">Its name as people read it.</param>
    /// <param name="Type">What it holds, as <see cref="TrackedField.Type"/> says it.</param>
    /// <param name="LongText">Whether it is a long text, which a query searches by words.</param>
    private sealed record Field(string Name, string Label, string Type, bool LongText);

    public override async Task<IReadOnlyList<TrackedField>> FieldsAsync(GitIssueQuery query, CancellationToken cancellationToken)
        => (await ProjectFieldsAsync(query, cancellationToken).ConfigureAwait(false) ?? [])
            .Select(static field => new TrackedField(FieldPrefix + field.Name, field.Label, field.Type) { Name = field.Name })
            .OrderBy(static field => field.Label, StringComparer.CurrentCultureIgnoreCase).ToArray();

    // The filters of the fields the configuration names, in its order; a field the project does not have is left out.
    private static async Task<IReadOnlyList<TrackedFilterDescriptor>> FieldFiltersAsync(GitIssueQuery query, CancellationToken cancellationToken)
    {
        if (query.Fields.Count == 0) return [];
        var fields = await ProjectFieldsAsync(query, cancellationToken).ConfigureAwait(false);
        var filters = new List<TrackedFilterDescriptor>();
        foreach (var id in query.Fields.Distinct(StringComparer.Ordinal))
        {
            if (FieldOf(fields, id) is not { } field) continue;
            var (values, problem) = await FieldValuesAsync(query, field, cancellationToken).ConfigureAwait(false);
            filters.Add(new(id, field.Label, values ?? [])
            {
                Problem = problem,
                Kind = values is null ? TrackedFilterKind.Text : TrackedFilterKind.Choice,
                // A text the process gives values for (State, Severity) is a list.
                FieldType = values is not null && field.Type == "text" ? "list" : field.Type,
                // A person is the user or nobody, a boolean one of two: the others take several values (New, Ready, On Hold).
                Multiple = values is not null && field.Type is not ("person" or "boolean"),
            });
        }

        return filters;
    }

    // The values a field offers, or null when it takes any text or number; and why the values of a list could not be read.
    private static async Task<(IReadOnlyList<TrackedFilterValue>? Values, string? Problem)> FieldValuesAsync(GitIssueQuery query, Field field, CancellationToken cancellationToken)
    {
        switch (field.Type)
        {
            case "person":
                return ([new(Me, "Me"), new(Nobody, "Nobody")], null);
            case "boolean":
                return ([new("true", "Yes"), new("false", "No")], null);
            case "tags":
                return await TagsAsync(query, cancellationToken).ConfigureAwait(false) is { } tags
                    ? ([.. tags.Select(static tag => new TrackedFilterValue(tag, tag))], null) : ([], "The tags of the project could not be read.");
            case "path":
                var areas = field.Name == "System.AreaPath";
                return await PathsAsync(query, areas ? "areas" : "iterations", cancellationToken).ConfigureAwait(false) is { } paths
                    ? ([.. paths.Select(static path => new TrackedFilterValue(path, path))], null) : ([], areas ? "The areas of the project could not be read." : "The iterations of the project could not be read.");
            default:
                var allowed = await AllowedValuesAsync(query, field, cancellationToken).ConfigureAwait(false);
                return (allowed is { Count: > 0 } ? [.. allowed.Select(static value => new TrackedFilterValue(value, value))] : null, null);
        }
    }

    // The clauses of the values chosen for fields; false when one cannot be applied (a number that is none).
    private static async Task<bool> AppendFieldClausesAsync(GitIssueQuery query, StringBuilder clauses, CancellationToken cancellationToken)
    {
        var chosen = query.Filters.Where(static pair => pair.Key.StartsWith(FieldPrefix, StringComparison.Ordinal) && pair.Value.Length > 0).ToArray();
        if (chosen.Length == 0) return true;
        var fields = await ProjectFieldsAsync(query, cancellationToken).ConfigureAwait(false);
        foreach (var (id, value) in chosen)
        {
            // A field the project does not have cannot be asked: the listing would be refused whole.
            if (FieldOf(fields, id) is not { } field) return false;
            var column = $"[{field.Name}]";
            switch (field.Type)
            {
                case "person":
                    clauses.Append(value switch { Me => $" AND {column} = @Me", Nobody => $" AND {column} = ''", _ => $" AND {column} = {Quote(value)}" });
                    break;
                case "boolean":
                    if (value is not ("true" or "false")) return false;
                    clauses.Append(CultureInfo.InvariantCulture, $" AND {column} = {value}");
                    break;
                case "tags":
                    clauses.Append(" AND (").AppendJoin(" OR ", TrackedFilters.Split(value).Select(tag => $"{column} CONTAINS {Quote(tag)}")).Append(')');
                    break;
                case "path":
                    clauses.Append(" AND (").AppendJoin(" OR ", TrackedFilters.Split(value).Select(path => $"{column} UNDER {Quote(path)}")).Append(')');
                    break;
                case "number":
                    var numbers = new List<string>();
                    foreach (var one in TrackedFilters.Split(value))
                    {
                        if (!double.TryParse(one, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number)) return false;
                        numbers.Add(number.ToString("R", CultureInfo.InvariantCulture));
                    }

                    clauses.Append(CultureInfo.InvariantCulture, $" AND {column} IN ({string.Join(", ", numbers)})");
                    break;
                default:
                    var allowed = await AllowedValuesAsync(query, field, cancellationToken).ConfigureAwait(false);
                    clauses.Append(allowed is { Count: > 0 }
                        ? $" AND {column} IN ({string.Join(", ", TrackedFilters.Split(value).Select(Quote))})"
                        : $" AND {column} {(field.LongText ? "CONTAINS WORDS" : "CONTAINS")} {Quote(value)}");
                    break;
            }
        }

        return true;
    }

    private static Field? FieldOf(IReadOnlyList<Field>? fields, string id)
        => id.StartsWith(FieldPrefix, StringComparison.Ordinal) ? fields?.FirstOrDefault(field => string.Equals(field.Name, id[FieldPrefix.Length..], StringComparison.OrdinalIgnoreCase)) : null;

    // The fields of work items a query can ask, of the types a filter knows; the reference name is written in brackets in a query, so it must be a plain name.
    private static Task<IReadOnlyList<Field>?> ProjectFieldsAsync(GitIssueQuery query, CancellationToken cancellationToken)
    {
        var url = FormattableString.Invariant($"{Project(query.Repository)}/_apis/wit/fields?api-version={ApiVersion}");
        return CachedAsync<IReadOnlyList<Field>>(query, url, async () =>
        {
            using var document = await GetAnswerAsync(query, url, cancellationToken, report: false).ConfigureAwait(false);
            if (document is null) return null;
            var fields = new List<Field>();
            foreach (var element in Items(document.RootElement, "value"))
            {
                if (Text(element, "referenceName") is not { } name || Text(element, "name") is not { } label || OwnFields.Contains(name)
                    || !name.All(static ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_')
                    || At(element, "isQueryable").ValueKind == System.Text.Json.JsonValueKind.False
                    || Text(element, "usage") is { } usage && !string.Equals(usage, "workItem", StringComparison.OrdinalIgnoreCase)) continue;
                var type = Text(element, "type") ?? string.Empty;
                var kind = name == "System.Tags" ? "tags"
                    : Flag(element, "isIdentity") || type == "identity" ? "person"
                    : Flag(element, "isPicklist") || type.StartsWith("picklist", StringComparison.OrdinalIgnoreCase) ? "list"
                    : type switch { "integer" or "double" => "number", "boolean" => "boolean", "treePath" => "path", "string" or "plainText" or "html" => "text", _ => null };
                if (kind is not null) fields.Add(new(name, label, kind, type is "plainText" or "html"));
            }

            return fields;
        });
    }

    // The values the process allows for a field, in all the types of the project: Priority 1 to 4, the states, a picklist.
    private static Task<IReadOnlyList<string>?> AllowedValuesAsync(GitIssueQuery query, Field field, CancellationToken cancellationToken)
    {
        var key = FormattableString.Invariant($"{Project(query.Repository)}/allowed/{field.Name}");
        return CachedAsync<IReadOnlyList<string>>(query, key, async () =>
        {
            var values = new List<string>();
            // Without the types the values are not known: nothing is kept, and the next listing asks again.
            if (await TypesAsync(query, cancellationToken).ConfigureAwait(false) is not { } types) return null;
            foreach (var type in types)
            {
                var url = FormattableString.Invariant($"{Project(query.Repository)}/_apis/wit/workitemtypes/{Escape(type)}/fields/{Escape(field.Name)}?$expand=allowedValues&api-version={ApiVersion}");
                using var document = await GetAnswerAsync(query, url, cancellationToken, report: false).ConfigureAwait(false);
                // A field of some types only is not found in the others.
                if (document is null) continue;
                foreach (var value in Items(document.RootElement, "allowedValues"))
                {
                    var text = value.ValueKind == System.Text.Json.JsonValueKind.Number ? value.GetRawText() : value.ValueKind == System.Text.Json.JsonValueKind.String ? value.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(text) && !values.Contains(text, StringComparer.Ordinal) && values.Count < MaximumFieldValues) values.Add(text);
                }
            }

            // A number is offered in the order of numbers, a text in the order the process gives.
            return field.Type == "number" ? [.. values.OrderBy(static value => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : double.MaxValue)] : values;
        });
    }

    private static Task<IReadOnlyList<string>?> TagsAsync(GitIssueQuery query, CancellationToken cancellationToken)
    {
        var url = FormattableString.Invariant($"{Project(query.Repository)}/_apis/wit/tags?api-version=7.1-preview.1");
        return CachedAsync<IReadOnlyList<string>>(query, url, async () =>
        {
            using var document = await GetAnswerAsync(query, url, cancellationToken, report: false).ConfigureAwait(false);
            return document is null ? null : [.. Items(document.RootElement, "value").Select(static tag => Text(tag, "name")).OfType<string>()
                .Order(StringComparer.CurrentCultureIgnoreCase).Take(MaximumFieldValues)];
        });
    }

    // The areas or the iterations of the project as the paths a query writes: Project\Team\Part.
    private static Task<IReadOnlyList<string>?> PathsAsync(GitIssueQuery query, string group, CancellationToken cancellationToken)
    {
        var url = FormattableString.Invariant($"{Project(query.Repository)}/_apis/wit/classificationnodes/{group}?$depth={MaximumAreaDepth}&api-version={ApiVersion}");
        return CachedAsync<IReadOnlyList<string>>(query, url, async () =>
        {
            using var document = await GetAnswerAsync(query, url, cancellationToken, report: false).ConfigureAwait(false);
            if (document is null) return null;
            var paths = new List<string>();
            void Walk(System.Text.Json.JsonElement node, string? parent)
            {
                if (Text(node, "name") is not { } name || paths.Count >= MaximumFieldValues) return;
                var path = parent is null ? name : parent + "\\" + name;
                paths.Add(path);
                foreach (var child in Items(node, "children")) Walk(child, path);
            }

            Walk(document.RootElement, null);
            return paths;
        });
    }

    /// <summary>The order of a listing by the order of the backlog, which the boards keep in this field when items are moved.</summary>
    public const string BacklogOrder = "backlog";

    /// <summary>The order of a listing by priority, the highest first.</summary>
    public const string PriorityOrder = "priority";

    /// <summary>The order of a listing by creation, the newest first.</summary>
    public const string CreatedOrder = "created";

    // The field the boards keep the order of the backlog in, when items are dragged: Stack Rank (Agile, CMMI, Basic) or Backlog Priority (Scrum).
    private static readonly ImmutableArray<string> BacklogFields = ["Microsoft.VSTS.Common.StackRank", "Microsoft.VSTS.Common.BacklogPriority"];

    // The orders the project allows: without a value, the most recently changed first.
    private static async Task<TrackedFilterDescriptor> SortFilterAsync(GitIssueQuery query, CancellationToken cancellationToken)
    {
        var fields = await ProjectFieldsAsync(query, cancellationToken).ConfigureAwait(false) ?? [];
        var orders = new List<TrackedFilterValue>();
        if (BacklogFields.Any(name => fields.Any(field => field.Name == name))) orders.Add(new(BacklogOrder, "Backlog order"));
        if (fields.Any(static field => field.Name == "Microsoft.VSTS.Common.Priority")) orders.Add(new(PriorityOrder, "Highest priority first"));
        orders.Add(new(CreatedOrder, "Newest first"));
        return new(TrackedFilters.Sort, "Order", orders) { AllLabel = "Last changed first" };
    }

    // The ORDER BY of a listing, its ties in the order of their last change.
    private static async Task<string> OrderByAsync(GitIssueQuery query, CancellationToken cancellationToken)
    {
        const string Changed = "[System.ChangedDate] DESC";
        var order = query.Filters.GetValueOrDefault(TrackedFilters.Sort);
        if (order is not (BacklogOrder or PriorityOrder or CreatedOrder)) return " ORDER BY " + Changed;
        var fields = await ProjectFieldsAsync(query, cancellationToken).ConfigureAwait(false) ?? [];
        return order switch
        {
            BacklogOrder when BacklogFields.FirstOrDefault(name => fields.Any(field => field.Name == name)) is { } backlog => $" ORDER BY [{backlog}] ASC, {Changed}",
            PriorityOrder => $" ORDER BY [Microsoft.VSTS.Common.Priority] ASC, {Changed}",
            CreatedOrder => " ORDER BY [System.CreatedDate] DESC",
            _ => " ORDER BY " + Changed,
        };
    }
}
