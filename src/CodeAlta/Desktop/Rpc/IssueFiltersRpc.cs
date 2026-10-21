using CodeAlta.Catalog.Issues;
using CodeAlta.Plugins.Abstractions;
using CodeAlta.Plugins.Issues;
using NeoAstra.Rpc;
using Tomlyn;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// The filters of the Issues tab: those a tracker offers with their values as the current view gives them, the fields
/// filters can be made of, and the views of a project (the user's and the team's) that the tab switches between and its
/// window of the views edits.
/// </summary>
internal sealed partial class IssuesService
{
    private const int MaximumFilters = IssueViewStore.MaximumFilters;
    private const int MaximumFilterValues = 500;
    private const int MaximumFields = 1000;
    private readonly Lock _viewsGate = new();

    /// <summary>Gets the views of the Issues tab in the folders of the projects; null when the host has none.</summary>
    internal IssueViewStore? Views { get; init; }

    /// <summary>
    /// Gets the filters of a tracker of a project with their values, the ones the current view lists by, and the value
    /// each one has: the view's for an active filter, else the one chosen, else none.
    /// </summary>
    [NeoRpcMethod("filters", TimeoutMilliseconds = 60_000)]
    public async Task<IssueFiltersResponse> FiltersAsync(IssueFiltersRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return IssueFiltersResponse.Refused(refused);
        if (!Name(request.Service) || !ValidSelections(request.Selections) || request.Include is { } include && (include.Count > MaximumFilters || !include.All(FilterId)))
            return IssueFiltersResponse.Refused("invalid_request");
        if (await ResolveAsync(request.ProjectId, cancellationToken).ConfigureAwait(false) is not { } project) return IssueFiltersResponse.Refused("not_found");
        if ((await TrackersAsync(project, cancellationToken).ConfigureAwait(false)).FirstOrDefault(candidate => candidate.Service == request.Service) is not { } tracker)
            return IssueFiltersResponse.Refused("no_tracker");

        var current = ReadCurrent(request.Service, project);
        // The window of the views asks for the values of the filters of the view it edits, whatever the current view fixes.
        var settings = request.WithoutView == true ? IssueFilterSettings.Empty : current?.Filters ?? IssueFilterSettings.Empty;
        var views = ReadViews(request.Service, project);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ListTimeout);
        try
        {
            var (filters, selected) = await ChooseAsync(tracker, request.Selections ?? [], settings, request.Include ?? [], timeout.Token).ConfigureAwait(false);
            // The filters the view lists by, those the tab may change first, then in the order of the view: the tab names them under its toolbar.
            var offered = settings.Filters.Where(setting => setting.Active && filters.Any(filter => filter.Id == setting.Id)).ToArray();
            var pick = offered.Where(static setting => setting.Pick).Select(static setting => setting.Id).ToArray();
            var active = pick.Concat(offered.Where(setting => !setting.Pick && selected.GetValueOrDefault(setting.Id) is { Length: > 0 }).Select(static setting => setting.Id)).ToArray();
            return new("ok", filters, selected) { Active = active, Pick = pick, Views = views, Current = current is null ? null : new(current.Name, SourceName(current.Source)) };
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new("ok", [], []) { Problem = $"{GitIssuesService.Clean(tracker.DisplayName, MaximumName)} did not give its filters.", Views = views };
        }
    }

    /// <summary>Lists the fields of the issues of a tracker that a filter can be made of.</summary>
    [NeoRpcMethod("fields", TimeoutMilliseconds = 60_000)]
    public async Task<IssueFieldsResponse> FieldsAsync(IssueFieldsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, []);
        if (!Name(request.Service)) return new("invalid_request", []);
        if (await ResolveAsync(request.ProjectId, cancellationToken).ConfigureAwait(false) is not { } project) return new("not_found", []);
        if ((await TrackersAsync(project, cancellationToken).ConfigureAwait(false)).FirstOrDefault(candidate => candidate.Service == request.Service) is not { } tracker)
            return new("no_tracker", []);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ListTimeout);
        try
        {
            var fields = await tracker.GetFieldsAsync(timeout.Token).ConfigureAwait(false);
            return new("ok", [.. fields.Where(static field => FilterId(field.Id)).Take(MaximumFields)
                .Select(static field => new IssueFieldInfo(field.Id, GitIssuesService.Clean(field.Label, MaximumName), GitIssuesService.Clean(field.Type, MaximumName), Nullable(field.Name)))]);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new("failed", []);
        }
    }

    /// <summary>Reads the views of a tracker of a project with their filters, the user's first, and the current one.</summary>
    [NeoRpcMethod("views")]
    public async Task<IssueViewsResponse> ViewsAsync(IssueViewsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, [], null);
        if (Views is null) return new("unavailable", [], null);
        if (!Name(request.Service)) return new("invalid_request", [], null);
        if (await ResolveAsync(request.ProjectId, cancellationToken).ConfigureAwait(false) is not { } project) return new("not_found", [], null);
        var current = ReadCurrent(request.Service, project);
        return new("ok", [.. ReadAll(request.Service, project).Select(view => new IssueViewDetail(view.Name, SourceName(view.Source),
            IssueViewStore.Locate(view.Source, project.ProjectPath), [.. view.Filters.Filters.Select(static filter => new IssueFilterEntry(filter.Id, filter.Active ? "on" : "off", filter.Value, filter.Label, filter.Pick ? true : null))]))],
            current is null ? null : new(current.Name, SourceName(current.Source)))
        {
            Paths = [new(SourceName(IssueViewSource.Personal), IssueViewStore.Locate(IssueViewSource.Personal, project.ProjectPath)),
                new(SourceName(IssueViewSource.Team), IssueViewStore.Locate(IssueViewSource.Team, project.ProjectPath))],
        };
    }

    /// <summary>Writes the filters of a view, made when it is missing; with <see cref="IssueViewSaveRequest.Select"/> it becomes the current one.</summary>
    [NeoRpcMethod("saveView")]
    public async Task<IssueViewChangeResponse> SaveViewAsync(IssueViewSaveRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (await CheckViewRequestAsync(request.ExpectedEpoch, request.ProjectId, request.Service, request.Source, request.Name, cancellationToken).ConfigureAwait(false) is not { } checkedRequest)
            return new(await RefusalAsync(request.ExpectedEpoch, request.ProjectId, cancellationToken).ConfigureAwait(false));
        if (request.Filters is not { } entries || entries.Count > MaximumFilters
            || !entries.All(static entry => FilterId(entry.Id) && (entry.Mode is null or "on" or "off") && IssueViewStore.IsFilterText(entry.Value) && IssueViewStore.IsFilterText(entry.Label))
            || entries.Select(static entry => entry.Id).Distinct(StringComparer.Ordinal).Count() != entries.Count) return new("invalid_request");
        // A name is one view: a view of the user does not take the name of one of the team, nor the reverse; the store checks it.
        return WriteViews(() =>
        {
            checkedRequest.Store.SaveView(checkedRequest.Source, request.Service, checkedRequest.Project.ProjectPath, request.Name,
                new([.. entries.Select(static entry => new IssueFilterSetting(entry.Id, entry.Mode != "off", entry.Value?.Trim(), string.IsNullOrWhiteSpace(entry.Label) ? null : entry.Label.Trim(), entry.Pick == true))]));
            if (request.Select) checkedRequest.Store.SetCurrent(request.Service, checkedRequest.Project.ProjectPath, request.Name);
        });
    }

    /// <summary>Renames a view; the choice of the user follows it.</summary>
    [NeoRpcMethod("renameView")]
    public async Task<IssueViewChangeResponse> RenameViewAsync(IssueViewRenameRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (await CheckViewRequestAsync(request.ExpectedEpoch, request.ProjectId, request.Service, request.Source, request.Name, cancellationToken).ConfigureAwait(false) is not { } checkedRequest)
            return new(await RefusalAsync(request.ExpectedEpoch, request.ProjectId, cancellationToken).ConfigureAwait(false));
        if (!IssueViewStore.IsViewName(request.NewName)) return new("invalid_request");
        var found = false;
        var result = WriteViews(() => found = checkedRequest.Store.RenameView(checkedRequest.Source, request.Service, checkedRequest.Project.ProjectPath, request.Name, request.NewName));
        return result.Status == "ok" && !found ? new("not_found") : result;
    }

    /// <summary>Removes a view.</summary>
    [NeoRpcMethod("removeView")]
    public async Task<IssueViewChangeResponse> RemoveViewAsync(IssueViewRemoveRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (await CheckViewRequestAsync(request.ExpectedEpoch, request.ProjectId, request.Service, request.Source, request.Name, cancellationToken).ConfigureAwait(false) is not { } checkedRequest)
            return new(await RefusalAsync(request.ExpectedEpoch, request.ProjectId, cancellationToken).ConfigureAwait(false));
        var found = false;
        var result = WriteViews(() => found = checkedRequest.Store.RemoveView(checkedRequest.Source, request.Service, checkedRequest.Project.ProjectPath, request.Name));
        return result.Status == "ok" && !found ? new("not_found") : result;
    }

    /// <summary>Makes a view the one the tab and <c>alta issue</c> use; no name forgets the choice.</summary>
    [NeoRpcMethod("selectView")]
    public async Task<IssueViewChangeResponse> SelectViewAsync(IssueViewSelectRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused);
        if (Views is not { } store) return new("unavailable");
        if (!Name(request.Service) || request.Name is not null && !IssueViewStore.IsViewName(request.Name)) return new("invalid_request");
        if (await ResolveAsync(request.ProjectId, cancellationToken).ConfigureAwait(false) is not { } project) return new("not_found");
        if (request.Name is not null && ReadAll(request.Service, project).All(view => view.Name != request.Name)) return new("not_found");
        return WriteViews(() => store.SetCurrent(request.Service, project.ProjectPath, request.Name));
    }

    private sealed record CheckedViewRequest(IssueViewStore Store, Catalog.ProjectDescriptor Project, IssueViewSource Source);

    // The store, the project and the source a request about one view names; null when one of them is wrong.
    private async Task<CheckedViewRequest?> CheckViewRequestAsync(string? epoch, string? projectId, string? service, string? source, string? name, CancellationToken cancellationToken)
    {
        if (Refuse(epoch) is not null || Views is not { } store || !Name(service) || Source(source) is not { } viewSource || !IssueViewStore.IsViewName(name)) return null;
        return await ResolveAsync(projectId, cancellationToken).ConfigureAwait(false) is { } project ? new(store, project, viewSource) : null;
    }

    // Why a request about a view was refused, when CheckViewRequestAsync gave nothing.
    private async Task<string> RefusalAsync(string? epoch, string? projectId, CancellationToken cancellationToken)
        => Refuse(epoch) is { } refused ? refused : Views is null ? "unavailable"
            : await ResolveAsync(projectId, cancellationToken).ConfigureAwait(false) is null ? "not_found" : "invalid_request";

    private IssueViewChangeResponse WriteViews(Action write)
    {
        try
        {
            lock (_viewsGate) write();
            return new("ok");
        }
        catch (IssueViewNameTakenException)
        {
            return new("name_taken"); // Checked by the store under the lock of the file.
        }
        catch (Exception exception) when (exception is TomlException or InvalidDataException or InvalidOperationException or FormatException or ArgumentException)
        {
            return new("config_invalid"); // The file does not parse and is left as it is.
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new("write_failed");
        }
    }

    private IReadOnlyList<IssueView> ReadAll(string service, Catalog.ProjectDescriptor project)
    {
        try
        {
            return Views?.ReadViews(service, project.ProjectPath) ?? [];
        }
        catch (ArgumentException)
        {
            return [];
        }
    }

    private IReadOnlyList<IssueViewName> ReadViews(string service, Catalog.ProjectDescriptor project)
        => [.. ReadAll(service, project).Select(static view => new IssueViewName(view.Name, SourceName(view.Source)))];

    private IssueView? ReadCurrent(string service, Catalog.ProjectDescriptor project)
    {
        try
        {
            return Views?.ReadCurrent(service, project.ProjectPath);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static IssueViewSource? Source(string? value) => value switch { "personal" => IssueViewSource.Personal, "team" => IssueViewSource.Team, _ => null };

    private static string SourceName(IssueViewSource source) => source == IssueViewSource.Team ? "team" : "personal";

    /// <summary>Asks the tracker for its filters and gives each one its value (<see cref="IssueFilterChoice"/>), described for the page.</summary>
    internal static async Task<(IssueFilterField[] Filters, Dictionary<string, string> Selected)> ChooseAsync(IIssueTracker tracker, IReadOnlyDictionary<string, string> chosen,
        IssueFilterSettings settings, IReadOnlyList<string> include, CancellationToken cancellationToken)
    {
        var selection = await IssueFilterChoice.ChooseAsync(tracker, chosen, settings, include, cancellationToken).ConfigureAwait(false);
        return ([.. selection.Filters.Select(descriptor => Describe(descriptor, settings.Find(descriptor.Id)?.Label))], new(selection.Selected, StringComparer.Ordinal));
    }

    // A value is offered as the tracker wrote its id, since it is given back to it: one that is not a plain name is not offered.
    private static IssueFilterField Describe(TrackedFilterDescriptor descriptor, string? label)
    {
        var tracker = GitIssuesService.Clean(descriptor.Label, MaximumName);
        return new(descriptor.Id, label is { Length: > 0 } ? GitIssuesService.Clean(label, MaximumName) : tracker,
            [.. descriptor.Values.Where(static value => Name(value.Id)).Take(MaximumFilterValues)
                .Select(static value => new IssueFilterValue(value.Id, GitIssuesService.Clean(value.Label, MaximumName), Nullable(value.Detail)))],
            [.. descriptor.DependsOn.Where(FilterId)], descriptor.Problem is null ? null : GitIssuesService.Clean(descriptor.Problem, MaximumTitle))
        {
            TrackerLabel = tracker,
            Kind = descriptor.Kind == TrackedFilterKind.Text ? "text" : "choice",
            FieldType = Nullable(descriptor.FieldType),
            Multiple = descriptor.Multiple && descriptor.Kind == TrackedFilterKind.Choice,
            AllLabel = Nullable(descriptor.AllLabel),
        };
    }

    /// <summary>
    /// The values a listing passes on to the tracker: those of the active filters of the current view that the tab may not
    /// change, whatever was asked, and the ones asked when the tracker offers them; null when a value asked is not offered.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>?> CheckedFiltersAsync(IIssueTracker tracker, IReadOnlyDictionary<string, string>? chosen, string service,
        Catalog.ProjectDescriptor project, CancellationToken cancellationToken)
    {
        var settings = ReadCurrent(service, project)?.Filters ?? IssueFilterSettings.Empty;
        var asked = (chosen ?? new Dictionary<string, string>()).Where(static pair => pair.Value.Length > 0).ToDictionary(StringComparer.Ordinal);
        if (asked.Count == 0 && !settings.Filters.Any(static setting => setting.Active)) return TrackedFilters.None;
        // A filter the tab may change lists every item when the listing asks for that, as any filter the view does not fix.
        foreach (var setting in settings.Filters.Where(setting => setting is { Active: true, Pick: true } && chosen?.ContainsKey(setting.Id) == true)) asked.TryAdd(setting.Id, string.Empty);
        var (_, selected) = await ChooseAsync(tracker, asked, settings, [], cancellationToken).ConfigureAwait(false);
        var fixedIds = settings.Filters.Where(static setting => setting.Active && !setting.Pick).Select(static setting => setting.Id).ToHashSet(StringComparer.Ordinal);
        if (!asked.Where(pair => !fixedIds.Contains(pair.Key)).All(pair => selected.GetValueOrDefault(pair.Key) == pair.Value)) return null;
        var passed = selected.Where(static pair => pair.Value.Length > 0).ToDictionary(StringComparer.Ordinal);
        return passed.Count == 0 ? TrackedFilters.None : passed;
    }

    private static bool ValidSelections(IReadOnlyDictionary<string, string>? values)
        => values is null || values.Count <= MaximumFilters && values.All(static pair => FilterId(pair.Key) && FilterValue(pair.Value));

    // A value is empty, one plain name, or several joined with the separator of the values.
    private static bool FilterValue(string value)
        => value.Length == 0 || value.Length <= 4096 && TrackedFilters.Split(value) is { Count: > 0 and <= MaximumFilterValues } values && values.All(Name);

    // The id of a filter is a plain name: it is a key of the configuration and of the query of a tracker.
    private static bool FilterId(string? value) => IssueViewStore.IsFilterId(value);
}

/// <summary>Asks for the filters of a tracker of a project.</summary>
/// <param name="ExpectedEpoch">The host epoch.</param>
/// <param name="ProjectId">The project.</param>
/// <param name="Service">The tracker, by its <see cref="IssueSource.Service"/>.</param>
/// <param name="Selections">
/// The values chosen, by filter id; an empty value is the choice of every item. An active filter of the current view keeps
/// the view's value unless the view lets the tab change it; a filter the view does not name has the value chosen, else none.
/// </param>
/// <param name="Include">The filters made of fields to describe besides those the view names: the ones the window of the views adds.</param>
/// <param name="WithoutView">Whether the current view takes no part: the window of the views asks for the values of the view it edits.</param>
internal sealed record IssueFiltersRequest(string ExpectedEpoch, string ProjectId, string Service, Dictionary<string, string>? Selections = null, IReadOnlyList<string>? Include = null,
    bool? WithoutView = null);

/// <summary>
/// The filters of a tracker. Status is <c>ok</c>, <c>unavailable</c>, <c>stale_epoch</c>, <c>invalid_request</c>,
/// <c>not_found</c> or <c>no_tracker</c>.
/// </summary>
/// <param name="Status">How the request ended.</param>
/// <param name="Filters">Every filter the tracker offers, with its values, and those of the fields the views or the request name.</param>
/// <param name="Selected">The value of each filter: the view's for an active filter, else the one chosen, else empty for every item.</param>
internal sealed record IssueFiltersResponse(string Status, IReadOnlyList<IssueFilterField> Filters, Dictionary<string, string> Selected)
{
    /// <summary>Gets the ids of the filters the current view lists by, those of <see cref="Pick"/> first, then in its order: the tab names them under its toolbar.</summary>
    public IReadOnlyList<string> Active { get; init; } = [];

    /// <summary>Gets the ids of the active filters whose value the tab offers to change for a while: it shows them first, each with its values.</summary>
    public IReadOnlyList<string> Pick { get; init; } = [];

    /// <summary>Gets why the tracker gave no filters, in words for the user.</summary>
    public string? Problem { get; init; }

    /// <summary>Gets the views of the tracker, the user's first.</summary>
    public IReadOnlyList<IssueViewName> Views { get; init; } = [];

    /// <summary>Gets the view whose filters these are; null when the project has none.</summary>
    public IssueViewName? Current { get; init; }

    internal static IssueFiltersResponse Refused(string status) => new(status, [], []);
}

/// <summary>A filter of a tracker.</summary>
/// <param name="Id">Its id: <c>team</c>, <c>sprint</c>, <c>assigned_to</c>, <c>type</c>, <c>field:&lt;name&gt;</c>...</param>
/// <param name="Label">Its name for the user: the one the configuration gives, else the tracker's.</param>
/// <param name="Values">Its values; none for a text filter.</param>
/// <param name="DependsOn">The filters whose value changes its values.</param>
/// <param name="Problem">Why its values could not all be read.</param>
internal sealed record IssueFilterField(string Id, string Label, IReadOnlyList<IssueFilterValue> Values, IReadOnlyList<string> DependsOn, string? Problem)
{
    /// <summary>Gets the name the tracker gives the filter.</summary>
    public string TrackerLabel { get; init; } = string.Empty;

    /// <summary>Gets how its value is given: <c>choice</c> or <c>text</c>.</summary>
    public string Kind { get; init; } = "choice";

    /// <summary>Gets the type of the field it is made of (<c>list</c>, <c>number</c>, <c>person</c>, <c>tags</c>, <c>path</c>, <c>boolean</c>, <c>text</c>); null for a filter of the tracker's own.</summary>
    public string? FieldType { get; init; }

    /// <summary>Gets whether it takes several values at once, joined with U+001F.</summary>
    public bool Multiple { get; init; }

    /// <summary>Gets what it does without a value, when that is not "every item": <c>Last changed</c> for the order.</summary>
    public string? AllLabel { get; init; }
}

/// <summary>A value of a filter.</summary>
internal sealed record IssueFilterValue(string Id, string Label, string? Detail);

/// <summary>Asks for the fields of a tracker of a project that filters can be made of.</summary>
internal sealed record IssueFieldsRequest(string ExpectedEpoch, string ProjectId, string Service);

/// <summary>The fields. Status is <c>ok</c>, <c>unavailable</c>, <c>stale_epoch</c>, <c>invalid_request</c>, <c>not_found</c>, <c>no_tracker</c> or <c>failed</c>.</summary>
internal sealed record IssueFieldsResponse(string Status, IReadOnlyList<IssueFieldInfo> Fields);

/// <summary>A field a filter can be made of.</summary>
/// <param name="Id">The id of the filter it makes: <c>field:&lt;name&gt;</c>.</param>
/// <param name="Label">Its name in the tracker.</param>
/// <param name="Type">What it holds: <c>list</c>, <c>number</c>, <c>person</c>, <c>tags</c>, <c>path</c>, <c>boolean</c> or <c>text</c>.</param>
/// <param name="Name">Its name in the queries of the tracker, when it says more than its label.</param>
internal sealed record IssueFieldInfo(string Id, string Label, string Type, string? Name);

/// <summary>What a view says of one filter.</summary>
/// <param name="Id">The id of the filter.</param>
/// <param name="Mode"><c>on</c> (the view lists by it; null too) or <c>off</c> (it keeps its value and its name, unused).</param>
/// <param name="Value">Its value; empty for every item.</param>
/// <param name="Label">The name the tab shows for it.</param>
/// <param name="Pick">Whether the tab offers to change its value for a while; null is no.</param>
internal sealed record IssueFilterEntry(string Id, string? Mode, string? Value, string? Label, bool? Pick = null);

/// <summary>Asks for the views of a tracker of a project.</summary>
internal sealed record IssueViewsRequest(string ExpectedEpoch, string ProjectId, string Service);

/// <summary>
/// The views of a tracker, the user's first, and the one the tab uses. Status is <c>ok</c>, <c>unavailable</c>,
/// <c>stale_epoch</c>, <c>invalid_request</c> or <c>not_found</c>.
/// </summary>
internal sealed record IssueViewsResponse(string Status, IReadOnlyList<IssueViewDetail> Views, IssueViewName? Current)
{
    /// <summary>Gets the files of the views: the user's (<c>personal</c>) and the team's (<c>team</c>).</summary>
    public IReadOnlyList<IssueViewPath> Paths { get; init; } = [];
}

/// <summary>A view and its filters.</summary>
/// <param name="Name">Its name.</param>
/// <param name="Source"><c>personal</c> (<c>.alta/local/issues.toml</c>) or <c>team</c> (<c>.alta/issues.toml</c>).</param>
/// <param name="Path">The file it is written in.</param>
/// <param name="Filters">What it says of each filter, in its order.</param>
internal sealed record IssueViewDetail(string Name, string Source, string Path, IReadOnlyList<IssueFilterEntry> Filters);

/// <summary>A view by its name and whose it is.</summary>
internal sealed record IssueViewName(string Name, string Source);

/// <summary>The file of the views of a source.</summary>
internal sealed record IssueViewPath(string Source, string Path);

/// <summary>Writes the filters of a view.</summary>
/// <param name="ExpectedEpoch">The host epoch.</param>
/// <param name="ProjectId">The project.</param>
/// <param name="Service">The tracker.</param>
/// <param name="Source"><c>personal</c> or <c>team</c>.</param>
/// <param name="Name">The name of the view; a missing view is made.</param>
/// <param name="Filters">What the view says of each filter, in its order.</param>
/// <param name="Select">Whether the view becomes the current one.</param>
internal sealed record IssueViewSaveRequest(string ExpectedEpoch, string ProjectId, string Service, string Source, string Name, IReadOnlyList<IssueFilterEntry>? Filters, bool Select = false);

/// <summary>Renames a view.</summary>
internal sealed record IssueViewRenameRequest(string ExpectedEpoch, string ProjectId, string Service, string Source, string Name, string NewName);

/// <summary>Removes a view.</summary>
internal sealed record IssueViewRemoveRequest(string ExpectedEpoch, string ProjectId, string Service, string Source, string Name);

/// <summary>Makes a view the current one; a null name forgets the choice.</summary>
internal sealed record IssueViewSelectRequest(string ExpectedEpoch, string ProjectId, string Service, string? Name);

/// <summary>
/// Status is <c>ok</c>, <c>unavailable</c>, <c>stale_epoch</c>, <c>invalid_request</c>, <c>not_found</c>,
/// <c>name_taken</c>, <c>config_invalid</c> or <c>write_failed</c>.
/// </summary>
internal sealed record IssueViewChangeResponse(string Status);
