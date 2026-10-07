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
public sealed record TrackedItemQuery(TrackedItemKind Kind, TrackedItemFilter Filter, string? Text, int Limit);

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
