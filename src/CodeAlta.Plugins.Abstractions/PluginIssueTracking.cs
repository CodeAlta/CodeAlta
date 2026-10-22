namespace CodeAlta.Plugins.Abstractions;

/// <summary>What an item of a tracker is.</summary>
public enum TrackedItemKind
{
    /// <summary>An issue, a work item or a ticket.</summary>
    Issue,

    /// <summary>A pull request or a merge request.</summary>
    PullRequest,
}

/// <summary>Where an item of a tracker stands.</summary>
public enum TrackedItemState
{
    /// <summary>Open: something is left to do.</summary>
    Open,

    /// <summary>A pull request that is open and not ready for review.</summary>
    Draft,

    /// <summary>Closed without being merged: done, declined or abandoned.</summary>
    Closed,

    /// <summary>A pull request whose changes were merged.</summary>
    Merged,
}

/// <summary>Which items a listing asks for.</summary>
public enum TrackedItemFilter
{
    /// <summary>The items that are open, drafts included.</summary>
    Open,

    /// <summary>The items that are closed; for pull requests, the ones closed without a merge.</summary>
    Closed,

    /// <summary>The pull requests that were merged.</summary>
    Merged,

    /// <summary>Every item.</summary>
    All,
}

/// <summary>A label, a tag or a component of an item.</summary>
/// <param name="Name">The text of the label.</param>
/// <param name="Color">Its color as six hexadecimal digits without <c>#</c>, when the tracker gives one.</param>
public sealed record TrackedLabel(string Name, string? Color = null);

/// <summary>One issue or pull request as a list shows it.</summary>
/// <param name="Kind">What the item is.</param>
/// <param name="Id">What names the item in its tracker: a number as text (<c>128</c>) or a key (<c>ALTA-12</c>).</param>
/// <param name="Title">The title.</param>
/// <param name="Url">The https address of the item on the web.</param>
/// <param name="State">Where the item stands.</param>
public sealed record TrackedItem(TrackedItemKind Kind, string Id, string Title, string Url, TrackedItemState State)
{
    /// <summary>Gets the state as the tracker names it (<c>In Progress</c>, <c>Resolved</c>), when it says more than <see cref="State"/>.</summary>
    public string? StateText { get; init; }

    /// <summary>Gets the type of the item as the tracker names it (<c>Bug</c>, <c>Story</c>), when it has types.</summary>
    public string? Type { get; init; }

    /// <summary>Gets the priority as the tracker names it, when it has one.</summary>
    public string? Priority { get; init; }

    /// <summary>Gets who opened the item.</summary>
    public string? Author { get; init; }

    /// <summary>Gets who the item is assigned to.</summary>
    public IReadOnlyList<string> Assignees { get; init; } = [];

    /// <summary>Gets the labels of the item.</summary>
    public IReadOnlyList<TrackedLabel> Labels { get; init; } = [];

    /// <summary>Gets when the item was opened.</summary>
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>Gets when the item last changed.</summary>
    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>Gets the number of comments, when the listing gives it.</summary>
    public int? CommentCount { get; init; }

    /// <summary>Gets the branch a pull request brings.</summary>
    public string? SourceBranch { get; init; }

    /// <summary>Gets the branch a pull request goes to.</summary>
    public string? TargetBranch { get; init; }
}

/// <summary>A comment of an item.</summary>
/// <param name="Author">Who wrote it.</param>
/// <param name="CreatedAt">When it was written.</param>
/// <param name="Body">Its text, as Markdown; a tracker that keeps HTML gives its HTML.</param>
public sealed record TrackedComment(string? Author, DateTimeOffset? CreatedAt, string Body);

/// <summary>One issue or pull request with what a reader wants: its description and its comments.</summary>
/// <param name="Item">The item.</param>
/// <param name="Body">The description, as Markdown; a tracker that keeps HTML gives its HTML. Empty when there is none.</param>
/// <param name="Comments">The comments, the oldest first.</param>
public sealed record TrackedItemDetail(TrackedItem Item, string Body, IReadOnlyList<TrackedComment> Comments)
{
    /// <summary>Gets whether the item has more comments than <see cref="Comments"/> holds.</summary>
    public bool MoreComments { get; init; }
}

/// <summary>What a listing asks a tracker.</summary>
/// <param name="Kind">Issues or pull requests.</param>
/// <param name="Filter">Which of them.</param>
/// <param name="Text">Words the items must match; null or empty for the most recently updated ones.</param>
/// <param name="Limit">The most items to return.</param>
public sealed record TrackedItemQuery(TrackedItemKind Kind, TrackedItemFilter Filter, string? Text, int Limit)
{
    /// <summary>Gets the part of the tracker to list, one of its <see cref="IIssueTracker.Parts"/>; null for all of them.</summary>
    public string? Part { get; init; }

    /// <summary>
    /// Gets the values chosen for the filters of the tracker (<see cref="IIssueTracker.GetFiltersAsync"/>): the id of a
    /// filter and the id of one of its values. A filter that is not named lists every item.
    /// </summary>
    public IReadOnlyDictionary<string, string> Filters { get; init; } = TrackedFilters.None;
}

/// <summary>One value a filter of a tracker can take: a team, a sprint, a person, a type.</summary>
/// <param name="Id">What names the value when it is given back to the tracker; a symbolic value starts with <c>@</c> (<c>@current</c>, <c>@me</c>).</param>
/// <param name="Label">The value as the user reads it.</param>
public sealed record TrackedFilterValue(string Id, string Label)
{
    /// <summary>Gets what more the user reads about the value: the dates of a sprint, the sprint that is the current one.</summary>
    public string? Detail { get; init; }
}

/// <summary>How the value of a filter is given.</summary>
public enum TrackedFilterKind
{
    /// <summary>One of the values the filter offers.</summary>
    Choice,

    /// <summary>A text the items must contain, or a number they must have: the filter offers no values.</summary>
    Text,
}

/// <summary>
/// A filter a tracker can apply to a listing, with the values it can take. Which filters a listing uses and their values
/// are said by the views of the user and of the team, not chosen by the tracker.
/// </summary>
/// <param name="Id">A stable name of the filter: <c>team</c>, <c>sprint</c>, <c>assigned_to</c>, <c>type</c>, or <c>field:&lt;name&gt;</c> for a field of the items.</param>
/// <param name="Label">The name of the filter as the user reads it.</param>
/// <param name="Values">The values, in the order they are offered; none for a <see cref="TrackedFilterKind.Text"/> filter.</param>
public sealed record TrackedFilterDescriptor(string Id, string Label, IReadOnlyList<TrackedFilterValue> Values)
{
    /// <summary>Gets the filters whose value changes the values of this one: the sprints of the team that is chosen.</summary>
    public IReadOnlyList<string> DependsOn { get; init; } = [];

    /// <summary>Gets why the values could not all be read, in words for the user; null when they were.</summary>
    public string? Problem { get; init; }

    /// <summary>Gets how the value of the filter is given.</summary>
    public TrackedFilterKind Kind { get; init; } = TrackedFilterKind.Choice;

    /// <summary>Gets the type of the field the filter is made of (<see cref="TrackedField.Type"/>); null for a filter of the tracker's own.</summary>
    public string? FieldType { get; init; }

    /// <summary>
    /// Gets whether the filter takes several of its values at once (the items in any of them): they are joined with
    /// <see cref="TrackedFilters.Separator"/> (<see cref="TrackedFilters.Join"/>).
    /// </summary>
    public bool Multiple { get; init; }

    /// <summary>Gets what the filter does without a value, when that is not "every item": <c>Last changed</c> for the order of a listing.</summary>
    public string? AllLabel { get; init; }
}

/// <summary>A field of the items of a tracker that a filter can be made of.</summary>
/// <param name="Id">The id of the filter it makes: <c>field:&lt;name&gt;</c>.</param>
/// <param name="Label">The name of the field as the user reads it in the tracker.</param>
/// <param name="Type">What the field holds: <c>list</c>, <c>number</c>, <c>person</c>, <c>tags</c>, <c>path</c>, <c>boolean</c> or <c>text</c>.</param>
public sealed record TrackedField(string Id, string Label, string Type)
{
    /// <summary>Gets the name of the field in the tracker, when it says more than its label: <c>Microsoft.VSTS.Common.Priority</c>.</summary>
    public string? Name { get; init; }
}

/// <summary>What a tracker is asked of its filters.</summary>
/// <param name="Selections">The values chosen so far, by filter id: a filter can offer values that depend on them (<see cref="TrackedFilterDescriptor.DependsOn"/>).</param>
/// <param name="Fields">The ids of the filters made of fields (<see cref="TrackedField.Id"/>) the views name, besides the tracker's own.</param>
public sealed record TrackedFilterRequest(IReadOnlyDictionary<string, string> Selections, IReadOnlyList<string> Fields);

/// <summary>Values shared by the filters of trackers.</summary>
public static class TrackedFilters
{
    /// <summary>What joins the values of a filter that takes several (<see cref="TrackedFilterDescriptor.Multiple"/>): the unit separator.</summary>
    public const char Separator = '\u001F';

    /// <summary>The id of the filter that gives the order of a listing, when a tracker offers it: its values are orders, none is the most recently changed first.</summary>
    public const string Sort = "sort";

    /// <summary>Gets the choice of no filter at all.</summary>
    public static IReadOnlyDictionary<string, string> None { get; } = new Dictionary<string, string>(0, StringComparer.Ordinal).AsReadOnly();

    /// <summary>Gets the values a value of a filter holds: one, several joined with <see cref="Separator"/>, or none when it is empty.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The values, each once.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    public static IReadOnlyList<string> Split(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Length == 0 ? [] : value.Split(Separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>Gets the value of a filter that holds several values.</summary>
    /// <param name="values">The values.</param>
    /// <returns>The values joined with <see cref="Separator"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="values"/> is null.</exception>
    public static string Join(IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return string.Join(Separator, values);
    }
}

/// <summary>The answer of a tracker to a listing.</summary>
/// <param name="Items">The items, the most recently updated first.</param>
public sealed record TrackedItemPage(IReadOnlyList<TrackedItem> Items)
{
    /// <summary>Gets whether the tracker has more matching items than it returned.</summary>
    public bool More { get; init; }

    /// <summary>Gets why the listing is empty or incomplete, in words for the user; null when it is whole.</summary>
    public string? Problem { get; init; }

    /// <summary>Gets whether <see cref="Problem"/> is about signing in, so that the user can be told how.</summary>
    public bool NeedsSignIn { get; init; }
}

/// <summary>
/// The issues and the pull requests of one place: a repository on a hosting service, or a project of a tracker.
/// An instance belongs to the folder it was resolved for and is not kept by its caller between requests.
/// </summary>
public interface IIssueTracker
{
    /// <summary>Gets a short, stable name of the service: <c>github</c>, <c>gitlab</c>, <c>azure_devops</c>, <c>bitbucket</c>, <c>jira</c>.</summary>
    string Service { get; }

    /// <summary>Gets the name of the service as its users write it: <c>GitHub</c>, <c>Jira</c>.</summary>
    string DisplayName { get; }

    /// <summary>Gets what the tracker is of: <c>owner/repository</c>, or the key of a project.</summary>
    string Location { get; }

    /// <summary>Gets the https address of the tracker on the web, when it has one.</summary>
    string? WebUrl { get; }

    /// <summary>
    /// Gets the parts of a tracker that is of several places, each of which a listing can be limited to
    /// (<see cref="TrackedItemQuery.Part"/>): the keys of the Jira projects of a project that names several.
    /// Empty for a tracker of one place.
    /// </summary>
    IReadOnlyList<string> Parts => [];

    /// <summary>
    /// Gets the filters the tracker can apply to a listing of issues (<see cref="TrackedItemQuery.Filters"/>), with
    /// their values. A tracker without filters answers none.
    /// </summary>
    /// <param name="request">The values chosen so far, and the fields the configuration makes filters of.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The filters: the tracker's own, then those of the fields it knows; a filter whose values could not be read carries a <see cref="TrackedFilterDescriptor.Problem"/>.</returns>
    ValueTask<IReadOnlyList<TrackedFilterDescriptor>> GetFiltersAsync(TrackedFilterRequest request, CancellationToken cancellationToken)
        => ValueTask.FromResult<IReadOnlyList<TrackedFilterDescriptor>>([]);

    /// <summary>Gets the fields of the items a filter can be made of; none for a tracker whose filters are only its own.</summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The fields, by their label.</returns>
    ValueTask<IReadOnlyList<TrackedField>> GetFieldsAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult<IReadOnlyList<TrackedField>>([]);

    /// <summary>Gets the kinds of items the tracker has.</summary>
    IReadOnlyList<TrackedItemKind> Kinds { get; }

    /// <summary>Lists items, the most recently updated first.</summary>
    /// <param name="query">What to list.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The items; a tracker that cannot be reached answers a page with a <see cref="TrackedItemPage.Problem"/>.</returns>
    ValueTask<TrackedItemPage> ListAsync(TrackedItemQuery query, CancellationToken cancellationToken);

    /// <summary>Reads one item with its description and its comments.</summary>
    /// <param name="kind">What the item is.</param>
    /// <param name="id">The <see cref="TrackedItem.Id"/> of the item.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The item, or null when the tracker has none with this id or cannot be reached.</returns>
    ValueTask<TrackedItemDetail?> ReadAsync(TrackedItemKind kind, string id, CancellationToken cancellationToken);
}

/// <summary>
/// Implemented by a plugin that knows trackers: the application asks every active plugin that implements it for
/// the trackers of a project, and shows their issues and pull requests together.
/// </summary>
public interface IIssueTrackerSource
{
    /// <summary>Finds the trackers of the project in a folder.</summary>
    /// <param name="projectPath">The folder of the project.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The trackers, or none when the plugin has nothing for this folder.</returns>
    ValueTask<IReadOnlyList<IIssueTracker>> GetTrackersAsync(string projectPath, CancellationToken cancellationToken);

    /// <summary>
    /// Finds the trackers of the project in a folder for the work of a session, which reads them under the identity of
    /// its space (<see cref="IPluginServices.Identity"/>). A plugin without identities finds the same as without a session.
    /// </summary>
    /// <param name="projectPath">The folder of the project.</param>
    /// <param name="sessionId">The session that reads them; null for the window.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The trackers, or none when the plugin has nothing for this folder.</returns>
    ValueTask<IReadOnlyList<IIssueTracker>> GetTrackersAsync(string projectPath, string? sessionId, CancellationToken cancellationToken)
        => GetTrackersAsync(projectPath, cancellationToken);
}

/// <summary>What happened to an item of a tracker.</summary>
public enum TrackedEventKind
{
    /// <summary>The item was created.</summary>
    Created,

    /// <summary>The item changed after it was created.</summary>
    Updated,
}

/// <summary>An item of a tracker that was created or changed.</summary>
/// <param name="Item">The item, as a list shows it.</param>
/// <param name="Stamp">
/// A text that changes each time the item does (the time of its last change): the same item with the same stamp is
/// the same event, seen again.
/// </param>
public sealed record TrackedEvent(TrackedItem Item, string Stamp);

/// <summary>What a tracker answers when it is asked what happened lately.</summary>
/// <param name="DisplayName">The name of the service as its users write it.</param>
/// <param name="Location">What the tracker is of.</param>
/// <param name="Events">The events, the oldest first.</param>
public sealed record TrackedEventPage(string DisplayName, string Location, IReadOnlyList<TrackedEvent> Events)
{
    /// <summary>Gets why the tracker could not say what happened, in words for the user; null when it could.</summary>
    public string? Problem { get; init; }
}

/// <summary>
/// Implemented by a plugin whose tracker can say what happened lately, so that automations start on it: the
/// application asks the plugins that implement it for the events of a service in a project.
/// </summary>
public interface IIssueEventSource
{
    /// <summary>Reads what happened in the tracker of a project during the last while.</summary>
    /// <param name="projectPath">The folder of the project.</param>
    /// <param name="service">The service the events are asked of, by its <see cref="IIssueTracker.Service"/> name.</param>
    /// <param name="kind">What kind of event.</param>
    /// <param name="window">How far back to look.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The events; null when the plugin has no tracker of this service for this project.</returns>
    ValueTask<TrackedEventPage?> ReadEventsAsync(string projectPath, string service, TrackedEventKind kind, TimeSpan window, CancellationToken cancellationToken);
}
