namespace CodeAlta.Catalog.WorkItems;

/// <summary>
/// The prompts that give a task or a plan to the session that carries it out. They are the same whoever starts
/// the work: a card of a session, the Plans and tasks tab, or a command.
/// </summary>
public static class WorkItemPrompts
{
    /// <summary>The most characters of a title in the name of a session.</summary>
    public const int MaximumSessionTitle = 120;

    /// <summary>Writes the prompt that asks a session to do a task.</summary>
    /// <param name="task">The task.</param>
    /// <returns>The prompt: the task itself, since the session may work in a checkout that does not have its file.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="task"/> is null.</exception>
    public static string ForTask(WorkTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return $"""
            Do this follow-up task. It is the task `{task.Id}` of the project, kept in `{WorkItemFiles.TasksFolder}/{task.Id}.md` of the project folder.

            # {task.Title}

            {task.Body}

            ---

            Start with `alta task start {task.Id}`. When the task is implemented and verified, run `alta task complete {task.Id}`. If it turns out to be unnecessary or cannot be done, say why and leave it open.
            """.ReplaceLineEndings("\n");
    }

    /// <summary>Writes the prompt that asks a session to carry out a plan.</summary>
    /// <param name="plan">The plan, as the project has it.</param>
    /// <param name="projectPath">The folder of the project.</param>
    /// <param name="workingDirectory">The folder the session works in: the folder of the project, or a git worktree of it.</param>
    /// <returns>The prompt.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static string ForPlan(WorkPlan plan, string projectPath, string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(projectPath);
        ArgumentNullException.ThrowIfNull(workingDirectory);
        var file = $"{WorkItemFiles.PlansFolder}/{plan.Id}.md";
        if (File.Exists(Path.Combine(WorkItemFiles.PlansDirectory(workingDirectory), plan.Id + ".md")))
        {
            return $"Execute the approved plan at `{file}`. Run `alta plan status {plan.Id} in-progress` first, keep the checkboxes of the plan up to date as you go, and run `alta plan status {plan.Id} done` when everything is implemented and verified.";
        }

        // A worktree has what is committed: a plan that is not is only in the folder of the project.
        return $"""
            Execute the approved plan `{plan.Id}`: {plan.Title}.

            Its file is `{file}` in the project folder (`{projectPath}`), which is not this worktree. Read it with `alta plan show {plan.Id}`. Do not copy it into this worktree and do not commit it here: it would collide with the original when this branch is merged.

            Run `alta plan status {plan.Id} in-progress` first, show your progress with `alta notes`, and run `alta plan status {plan.Id} done` when everything is implemented and verified.
            """.ReplaceLineEndings("\n");
    }

    /// <summary>Names the session that carries an item out.</summary>
    /// <param name="title">The title of the task or the plan.</param>
    /// <returns>The title, cut to what the name of a session takes.</returns>
    public static string SessionTitle(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        var line = title.ReplaceLineEndings(" ").Trim();
        line = new string([.. line.Where(static character => !char.IsControl(character))]);
        return line.Length <= MaximumSessionTitle ? line : line[..(MaximumSessionTitle - 1)].TrimEnd() + "…";
    }
}
