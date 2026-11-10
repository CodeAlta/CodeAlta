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
public sealed record WorkTask(string Id, string Title, string? Summary, string Kind, WorkTaskStatus Status, string? Created, string Path, string Body)
{
    /// <summary>Gets the short id of the task: the characters that end its id; null for a task written before ids had them.</summary>
    public string? ShortId => WorkItemFiles.ShortIdOf(Id);

    /// <summary>Gets the tags of the task, as its file names them (<see cref="WorkItemFiles.IsTagName"/>), each once.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];
}

/// <summary>What a new task is made of.</summary>
/// <param name="Title">What the task is, in a line.</param>
/// <param name="Kind">Why the task exists; <c>improvement</c> when null.</param>
/// <param name="Summary">One or two sentences for a notification.</param>
/// <param name="Body">The Markdown that says why, what and where.</param>
public sealed record WorkTaskDraft(string Title, string? Kind, string? Summary, string Body)
{
    /// <summary>Gets the tags of the new task; each is a tag name (<see cref="WorkItemFiles.IsTagName"/>).</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];
}

/// <summary>A tag of the tasks of a project.</summary>
/// <param name="Name">The name: lower case letters, digits and hyphens (<see cref="WorkItemFiles.IsTagName"/>).</param>
/// <param name="Description">What the tag is for, in a line, which agents match tasks against; null when it has none.</param>
/// <param name="OpenTasks">How many tasks that are not closed have the tag.</param>
/// <param name="Listed">Whether the tags file of the project lists the tag; a tag only found on tasks is not.</param>
public sealed record WorkTag(string Name, string? Description, int OpenTasks, bool Listed);

/// <summary>The tags of the tasks of a project, and the names the user declined when an agent proposed them.</summary>
/// <param name="Tags">The tags: those of the tags file in its order, then those only found on tasks, by name.</param>
/// <param name="Declined">The names the user turned down, which agents do not propose again.</param>
public sealed record WorkTags(IReadOnlyList<WorkTag> Tags, IReadOnlyList<string> Declined);

/// <summary>How a change of the tags of a project ended.</summary>
public enum WorkTagChange
{
    /// <summary>The change was made, or there was nothing to change.</summary>
    Done,

    /// <summary>The project has no tag of that name.</summary>
    NotFound,

    /// <summary>The project already has a tag of the new name.</summary>
    Exists,

    /// <summary>A file could not be read or written; what was written before stays.</summary>
    Failed,
}

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
/// The provider, the model and the reasoning effort a task or a plan is carried out with: those of the session
/// that proposed it.
/// </summary>
/// <param name="ProviderKey">The key of the model provider.</param>
/// <param name="ModelId">The model; null leaves it to the provider.</param>
/// <param name="ReasoningEffort">The name of the reasoning effort, in lower case; null leaves it to the model.</param>
public sealed record WorkItemSelection(string ProviderKey, string? ModelId = null, string? ReasoningEffort = null);

/// <summary>
/// What this computer knows of a task or a plan beside its file: the session that proposed it, what that session
/// ran with, and the session that carries it out. Session ids and provider keys mean nothing to another computer,
/// so they are not written in the file.
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

    /// <summary>Gets what the session that proposed the item ran with, which a session started for the item takes; null when it is not known.</summary>
    public WorkItemSelection? RunsWith { get; init; }
}
