namespace CodeAlta.Catalog.WorkItems;

/// <summary>What becomes of the file of a task or a plan that is closed.</summary>
public enum WorkItemClosing
{
    /// <summary>The file is deleted.</summary>
    Delete,

    /// <summary>The file is kept, with the status it ended with.</summary>
    Keep,
}

/// <summary>Where the work of a task or a plan is started.</summary>
public enum WorkItemStart
{
    /// <summary>A new session in a new git worktree.</summary>
    Worktree,

    /// <summary>A new session in the folder of the project.</summary>
    Session,

    /// <summary>The session that shows the item, after its current work.</summary>
    Here,
}

/// <summary>
/// The user's choices for tasks and plans: the <c>[work_items]</c> table of the user's configuration file.
/// </summary>
/// <param name="Propose">Whether agents may propose follow-up tasks.</param>
/// <param name="Notify">Whether a session shows what it proposed as cards.</param>
/// <param name="CompletedTasks">What becomes of a completed task.</param>
/// <param name="DismissedTasks">What becomes of a dismissed task.</param>
/// <param name="CompletedPlans">What becomes of a plan once it is done.</param>
/// <param name="Start">The way of starting that the cards put first.</param>
public sealed record WorkItemSettings(bool Propose, bool Notify, WorkItemClosing CompletedTasks, WorkItemClosing DismissedTasks, WorkItemClosing CompletedPlans, WorkItemStart Start)
{
    /// <summary>The name of <see cref="WorkItemClosing.Delete"/> in the configuration file.</summary>
    public const string DeleteName = "delete";

    /// <summary>The name of <see cref="WorkItemClosing.Keep"/> in the configuration file.</summary>
    public const string KeepName = "keep";

    /// <summary>The name of <see cref="WorkItemStart.Worktree"/> in the configuration file.</summary>
    public const string WorktreeName = "worktree";

    /// <summary>The name of <see cref="WorkItemStart.Session"/> in the configuration file.</summary>
    public const string SessionName = "session";

    /// <summary>The name of <see cref="WorkItemStart.Here"/> in the configuration file.</summary>
    public const string HereName = "here";

    /// <summary>
    /// The choices of a configuration file that says nothing: a closed task is deleted, and a plan that is done is
    /// kept, as plans always were.
    /// </summary>
    public static WorkItemSettings Default { get; } = new(true, true, WorkItemClosing.Delete, WorkItemClosing.Delete, WorkItemClosing.Keep, WorkItemStart.Worktree);

    /// <summary>Reads the choices from a configuration document; what is absent or not known reads as the default.</summary>
    /// <param name="document">The <c>[work_items]</c> table, or null when the file has none.</param>
    /// <returns>The choices.</returns>
    public static WorkItemSettings Read(CodeAltaWorkItemSettingsDocument? document)
        => document is null ? Default : new(
            document.Propose ?? Default.Propose,
            document.Notify ?? Default.Notify,
            ParseClosing(document.CompletedTasks) ?? Default.CompletedTasks,
            ParseClosing(document.DismissedTasks) ?? Default.DismissedTasks,
            ParseClosing(document.CompletedPlans) ?? Default.CompletedPlans,
            ParseStart(document.Start) ?? Default.Start);

    /// <summary>Gets the name of a closing in the configuration file.</summary>
    /// <param name="closing">The closing.</param>
    /// <returns><c>delete</c> or <c>keep</c>.</returns>
    public static string NameOf(WorkItemClosing closing) => closing == WorkItemClosing.Keep ? KeepName : DeleteName;

    /// <summary>Gets the name of a way of starting in the configuration file.</summary>
    /// <param name="start">The way of starting.</param>
    /// <returns><c>worktree</c>, <c>session</c> or <c>here</c>.</returns>
    public static string NameOf(WorkItemStart start)
        => start switch
        {
            WorkItemStart.Session => SessionName,
            WorkItemStart.Here => HereName,
            _ => WorktreeName,
        };

    /// <summary>Reads a closing by its name.</summary>
    /// <param name="name">The name as written.</param>
    /// <returns>The closing, or null when the name is not one.</returns>
    public static WorkItemClosing? ParseClosing(string? name)
        => name?.Trim().ToLowerInvariant() switch
        {
            DeleteName => WorkItemClosing.Delete,
            KeepName => WorkItemClosing.Keep,
            _ => null,
        };

    /// <summary>Reads a way of starting by its name.</summary>
    /// <param name="name">The name as written.</param>
    /// <returns>The way of starting, or null when the name is not one.</returns>
    public static WorkItemStart? ParseStart(string? name)
        => name?.Trim().ToLowerInvariant() switch
        {
            WorktreeName => WorkItemStart.Worktree,
            SessionName => WorkItemStart.Session,
            HereName => WorkItemStart.Here,
            _ => null,
        };
}
