namespace CodeAlta.Catalog.WorkItems;

/// <summary>What a task is, as the user sees it.</summary>
public enum WorkTaskStatus
{
    /// <summary>Proposed and waiting for a decision.</summary>
    Pending,

    /// <summary>Set aside for later: still to do, out of the way.</summary>
    Later,

    /// <summary>Done, in a file the user chose to keep.</summary>
    Done,

    /// <summary>Dismissed, in a file the user chose to keep.</summary>
    Dismissed,
}

/// <summary>Where a plan is in its life.</summary>
public enum WorkPlanStatus
{
    /// <summary>Being written or reviewed.</summary>
    Draft,

    /// <summary>Approved and ready to be carried out.</summary>
    Approved,

    /// <summary>Being carried out.</summary>
    InProgress,

    /// <summary>Carried out.</summary>
    Done,

    /// <summary>Stopped on something that needs a decision.</summary>
    Blocked,
}

/// <summary>
/// A follow-up task: a Markdown file with a front matter under <c>.alta/tasks/</c> of a project.
/// </summary>
/// <param name="Id">The name of the file without its extension, which identifies the task in its project.</param>
/// <param name="Title">What the task is, in a line.</param>
/// <param name="Summary">One or two sentences for a notification; null when the file gives none.</param>
/// <param name="Kind">Why the task exists: <c>gap</c>, <c>problem</c> or <c>improvement</c>.</param>
/// <param name="Status">Where the task is.</param>
/// <param name="Created">The day the task was written, as <c>yyyy-MM-dd</c>; null when the file does not say.</param>
/// <param name="Path">The full path of the file.</param>
/// <param name="Body">The Markdown under the front matter.</param>
public sealed record WorkTask(string Id, string Title, string? Summary, string Kind, WorkTaskStatus Status, string? Created, string Path, string Body);

/// <summary>What a new task is made of.</summary>
/// <param name="Title">What the task is, in a line.</param>
/// <param name="Kind">Why the task exists; <c>improvement</c> when null.</param>
/// <param name="Summary">One or two sentences for a notification.</param>
/// <param name="Body">The Markdown that says why, what and where.</param>
public sealed record WorkTaskDraft(string Title, string? Kind, string? Summary, string Body);

/// <summary>
/// A plan: a Markdown file under <c>.alta/plans/</c> of a project. A plan written before plans had a front
/// matter is read from its title and its <c>- Status:</c> line.
/// </summary>
/// <param name="Id">The name of the file without its extension, which identifies the plan in its project.</param>
/// <param name="Title">What the plan is for, in a line.</param>
/// <param name="Summary">One or two sentences; null when the file gives none.</param>
/// <param name="Status">Where the plan is.</param>
/// <param name="StatusText">What the file says of its status when that is more than the name of a status; null otherwise.</param>
/// <param name="Created">The day the plan was written, as <c>yyyy-MM-dd</c>; null when neither the file nor its name says.</param>
/// <param name="Path">The full path of the file.</param>
/// <param name="HasFrontMatter">Whether the file starts with a front matter.</param>
public sealed record WorkPlan(string Id, string Title, string? Summary, WorkPlanStatus Status, string? StatusText, string? Created, string Path, bool HasFrontMatter);

/// <summary>The two kinds of work items.</summary>
public static class WorkItemKinds
{
    /// <summary>A task.</summary>
    public const string Task = "task";

    /// <summary>A plan.</summary>
    public const string Plan = "plan";
}

/// <summary>
/// What this computer knows of a task or a plan beside its file: the session that proposed it and the session
/// that carries it out. Session ids mean nothing to another computer, so they are not written in the file.
/// </summary>
/// <param name="ProjectId">The project of the item.</param>
/// <param name="Kind">One of <see cref="WorkItemKinds"/>.</param>
/// <param name="Id">The id of the item in its project.</param>
public sealed record WorkItemLink(string ProjectId, string Kind, string Id)
{
    /// <summary>Gets the session that proposed the item, which shows it as a card.</summary>
    public string? ProposedBy { get; init; }

    /// <summary>Gets the session that carries the item out.</summary>
    public string? Runner { get; init; }

    /// <summary>Gets when the runner was given the item.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>Gets a value indicating whether the user put the card of the item away without deciding.</summary>
    public bool Acknowledged { get; init; }
}
