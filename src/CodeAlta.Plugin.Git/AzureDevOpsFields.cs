using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Net;
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

    // The work item types whose fields are read at once.
    private const int ParallelTypeReads = 4;

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

    /// <summary>The values the process allows for the fields of one work item type, by reference name; a field that allows any value has none.</summary>
    private sealed record TypeFields(IReadOnlyDictionary<string, IReadOnlyList<string>> Allowed);

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
                FieldType = values is { Count: > 0 } && field.Type == "text" ? "list" : field.Type,
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
                    ? Offered(tags, field) : ([], "The tags of the project could not be read.");
            case "path":
                var areas = field.Name == "System.AreaPath";
                return await PathsAsync(query, areas ? "areas" : "iterations", cancellationToken).ConfigureAwait(false) is { } paths
                    ? Offered(paths, field) : ([], areas ? "The areas of the project could not be read." : "The iterations of the project could not be read.");
            default:
                // Values that could not be read are not "any value": the filter stays a list, empty, and says why.
                return await AllowedValuesAsync(query, field, cancellationToken).ConfigureAwait(false) is not { } allowed
                    ? ([], $"The values of {field.Label} could not be read.")
                    : allowed.Values.Count == 0 ? (null, null)
                    : allowed.Partial ? ([.. allowed.Values.Take(MaximumFieldValues).Select(static value => new TrackedFilterValue(value, value))], $"Not all the values of {field.Label} could be read.")
                    : Offered(allowed.Values, field);
        }
    }

    // The values read, at most MaximumFieldValues: those past it are not offered, and the filter says so. With a problem,
    // a value a view fixes that the filter does not offer is kept as the view writes it, rather than dropped.
    private static (IReadOnlyList<TrackedFilterValue> Values, string? Problem) Offered(IReadOnlyList<string> values, Field field)
        => ([.. values.Take(MaximumFieldValues).Select(static value => new TrackedFilterValue(value, value))],
            values.Count > MaximumFieldValues ? string.Create(CultureInfo.InvariantCulture, $"Only the first {MaximumFieldValues} values of {field.Label} are offered.") : null);

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
                        if (NumberLiteral(one) is not { } number) return false;
                        numbers.Add(number);
                    }

                    clauses.Append(CultureInfo.InvariantCulture, $" AND {column} IN ({string.Join(", ", numbers)})");
                    break;
                default:
                    // Without its values, a list cannot be told from a text: the value cannot be applied.
                    if (await AllowedValuesAsync(query, field, cancellationToken).ConfigureAwait(false) is not { } allowed) return false;
                    // A value the list does not offer (not all of it could be read) is asked as it is: it narrows the listing.
                    clauses.Append(allowed.Values.Count > 0
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
        return CachedAsync<IReadOnlyList<Field>>(query, url, cancellationToken, async () =>
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
                // A picklist of numbers stays a number: its values are written unquoted.
                var kind = name == "System.Tags" ? "tags"
                    : Flag(element, "isIdentity") || type == "identity" ? "person"
                    : type is "integer" or "double" or "picklistInteger" or "picklistDouble" ? "number"
                    : Flag(element, "isPicklist") || type.StartsWith("picklist", StringComparison.OrdinalIgnoreCase) ? "list"
                    : type switch { "boolean" => "boolean", "treePath" => "path", "string" or "plainText" or "html" => "text", _ => null };
                if (kind is not null) fields.Add(new(name, label, kind, type is "plainText" or "html"));
            }

            return fields;
        });
    }

    /// <summary>
    /// Writes a number as a WIQL literal, which takes no exponent: the shortest text that reads back as the same double,
    /// its exponent written out (<c>1e16</c> is <c>10000000000000000</c>, <c>1e-29</c> is <c>0.00…01</c>).
    /// </summary>
    /// <param name="text">The number, as a person writes it.</param>
    /// <returns>The literal; null when the text is not a finite number.</returns>
    internal static string? NumberLiteral(string text)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number)) return null;
        var written = number.ToString("R", CultureInfo.InvariantCulture);
        var e = written.IndexOf('E', StringComparison.Ordinal);
        if (e < 0) return written;
        var negative = written[0] == '-';
        var mantissa = written[(negative ? 1 : 0)..e];
        var point = mantissa.IndexOf('.', StringComparison.Ordinal);
        var digits = point < 0 ? mantissa : mantissa.Remove(point, 1);
        // Where the point goes among the digits once the exponent moves it.
        var position = (point < 0 ? mantissa.Length : point) + int.Parse(written.AsSpan(e + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        var plain = position <= 0 ? "0." + new string('0', -position) + digits
            : position >= digits.Length ? digits + new string('0', position - digits.Length)
            : digits[..position] + "." + digits[position..];
        return negative ? "-" + plain : plain;
    }

    /// <summary>The values the process allows for a field, at most one past <see cref="MaximumFieldValues"/>.</summary>
    /// <param name="Values">The values, in the order they are offered.</param>
    /// <param name="Partial">Whether a type that did not answer may allow others.</param>
    private sealed record AllowedValues(IReadOnlyList<string> Values, bool Partial);

    // The values the process allows for a field, in all the types of the project: Priority 1 to 4, the states, a picklist.
    // Null when they are not known: the types, or every type that could have the field, could not be read.
    private static async Task<AllowedValues?> AllowedValuesAsync(GitIssueQuery query, Field field, CancellationToken cancellationToken)
    {
        if (await TypesAsync(query, cancellationToken).ConfigureAwait(false) is not { } types) return null;
        var read = new TypeFields?[types.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, types.Count), new ParallelOptions { MaxDegreeOfParallelism = ParallelTypeReads, CancellationToken = cancellationToken },
            async (index, token) => read[index] = await TypeFieldsAsync(query, types[index], token).ConfigureAwait(false)).ConfigureAwait(false);
        var values = new List<string>();
        foreach (var value in read.OfType<TypeFields>().SelectMany(type => type.Allowed.GetValueOrDefault(field.Name) ?? []))
        {
            // One past the most offered tells that some are not.
            if (!values.Contains(value, StringComparer.Ordinal) && values.Count <= MaximumFieldValues) values.Add(value);
        }

        // A type that did not answer may be the one that has the field: none found is not "any value" then, and some
        // found may not be all of them.
        var partial = read.Any(static type => type is null);
        if (values.Count == 0 && partial) return null;
        // A number is offered in the order of numbers, a text in the order the process gives.
        return new(field.Type == "number" ? [.. values.OrderBy(static value => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : double.MaxValue)] : values, partial);
    }

    // The values the process allows for every field of a type, in one request; null when the type could not be read.
    private static Task<TypeFields?> TypeFieldsAsync(GitIssueQuery query, string type, CancellationToken cancellationToken)
    {
        var url = FormattableString.Invariant($"{Project(query.Repository)}/_apis/wit/workitemtypes/{Escape(type)}/fields?$expand=allowedValues&api-version={ApiVersion}");
        return CachedAsync<TypeFields>(query, url, cancellationToken, async () =>
        {
            using var response = await query.GetAsync(url, Accept, cancellationToken).ConfigureAwait(false);
            // A type that is gone has no field; a refusal (401, 403, 429, 5xx) says nothing of its fields, and is asked again.
            if (response.StatusCode == HttpStatusCode.NotFound) return new TypeFields(FrozenDictionary<string, IReadOnlyList<string>>.Empty);
            if (!IsAnswer(response)) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await System.Text.Json.JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var allowed = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var element in Items(document.RootElement, "value"))
            {
                if (Text(element, "referenceName") is not { } name) continue;
                var values = Items(element, "allowedValues")
                    .Select(static value => value.ValueKind == System.Text.Json.JsonValueKind.Number ? value.GetRawText() : value.ValueKind == System.Text.Json.JsonValueKind.String ? value.GetString() : null)
                    .Where(static text => !string.IsNullOrWhiteSpace(text)).OfType<string>().Distinct(StringComparer.Ordinal).Take(MaximumFieldValues + 1).ToArray();
                if (values.Length > 0) allowed[name] = values;
            }

            return new TypeFields(allowed.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase));
        });
    }

    private static Task<IReadOnlyList<string>?> TagsAsync(GitIssueQuery query, CancellationToken cancellationToken)
    {
        var url = FormattableString.Invariant($"{Project(query.Repository)}/_apis/wit/tags?api-version=7.1-preview.1");
        return CachedAsync<IReadOnlyList<string>>(query, url, cancellationToken, async () =>
        {
            using var document = await GetAnswerAsync(query, url, cancellationToken, report: false).ConfigureAwait(false);
            return document is null ? null : [.. Items(document.RootElement, "value").Select(static tag => Text(tag, "name")).OfType<string>()
                .Order(StringComparer.CurrentCultureIgnoreCase).Take(MaximumFieldValues + 1)];
        });
    }

    // The areas or the iterations of the project as the paths a query writes: Project\Team\Part.
    private static Task<IReadOnlyList<string>?> PathsAsync(GitIssueQuery query, string group, CancellationToken cancellationToken)
    {
        var url = FormattableString.Invariant($"{Project(query.Repository)}/_apis/wit/classificationnodes/{group}?$depth={MaximumAreaDepth}&api-version={ApiVersion}");
        return CachedAsync<IReadOnlyList<string>>(query, url, cancellationToken, async () =>
        {
            using var document = await GetAnswerAsync(query, url, cancellationToken, report: false).ConfigureAwait(false);
            if (document is null) return null;
            var paths = new List<string>();
            void Walk(System.Text.Json.JsonElement node, string? parent)
            {
                if (Text(node, "name") is not { } name || paths.Count > MaximumFieldValues) return;
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
