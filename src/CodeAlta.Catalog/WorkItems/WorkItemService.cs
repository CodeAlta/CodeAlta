using System.Globalization;
using System.Text;

namespace CodeAlta.Catalog.WorkItems;

/// <summary>Why a task was not created.</summary>
public enum WorkTaskRefusal
{
    /// <summary>The user turned task proposals off in the settings.</summary>
    Disabled,

    /// <summary>The session already has as many open proposals as one session can have.</summary>
    TooManyProposals,

    /// <summary>The project already has as many tasks as a project can have.</summary>
    TooManyTasks,
}

/// <summary>What creating a task gave.</summary>
/// <param name="Task">The task that was written; null when it was refused.</param>
/// <param name="Refusal">Why it was refused; null when it was written.</param>
public sealed record WorkTaskCreation(WorkTask? Task, WorkTaskRefusal? Refusal);

/// <summary>
/// The tasks and the plans of projects: the files under <c>.alta/tasks/</c> and <c>.alta/plans/</c> of each
/// project, and what this computer knows of the sessions that proposed them and that carry them out.
/// </summary>
/// <remarks>
/// The files are read at each call: agents and people write them directly. <see cref="Changed"/> tells of the
/// changes made through this service; a file written directly is seen at the next read.
/// </remarks>
public sealed class WorkItemService
{
    /// <summary>The most tasks or plans of a project that are listed.</summary>
    public const int MaximumItems = 300;

    /// <summary>The most open tasks one session can have proposed.</summary>
    public const int MaximumProposals = 5;

    private readonly CodeAltaConfigStore _config;
    private readonly WorkItemLinkStore _links;
    private readonly TimeProvider _time;

    /// <summary>Creates the service.</summary>
    /// <param name="config">The configuration, where the user's choices for tasks are.</param>
    /// <param name="stateRoot">The state folder of this instance of the application, where the links to its sessions are kept.</param>
    /// <param name="time">The clock; the system's when null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="config"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="stateRoot"/> is blank.</exception>
    public WorkItemService(CodeAltaConfigStore config, string stateRoot, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
        _links = new WorkItemLinkStore(stateRoot);
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Raised after a change made through this service.</summary>
    public event Action? Changed;

    /// <summary>Gets the user's choices, as the configuration file has them now.</summary>
    public WorkItemSettings Settings
    {
        get
        {
            try
            {
                return WorkItemSettings.Read(_config.LoadGlobal().WorkItems);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or FormatException)
            {
                return WorkItemSettings.Default;
            }
        }
    }

    /// <summary>Saves the user's choices.</summary>
    /// <param name="settings">The choices.</param>
    /// <exception cref="ArgumentNullException"><paramref name="settings"/> is null.</exception>
    public void SaveSettings(WorkItemSettings settings)
    {
        _config.SaveGlobalWorkItemSettings(settings);
        Changed?.Invoke();
    }

    /// <summary>Lists the tasks of a project, the newest first.</summary>
    /// <param name="project">The project.</param>
    /// <returns>The tasks whose files can be read, up to <see cref="MaximumItems"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="project"/> is null.</exception>
    public IReadOnlyList<WorkTask> ListTasks(ProjectDescriptor project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var tasks = new List<WorkTask>();
        foreach (var path in Files(WorkItemFiles.TasksDirectory(project.ProjectPath)))
        {
            if (WorkItemFiles.ReadHead(path, plan: false) is { } text)
            {
                tasks.Add(WorkItemFiles.ParseTask(path, text));
            }
        }

        return tasks;
    }

    /// <summary>Reads one task.</summary>
    /// <param name="project">The project.</param>
    /// <param name="id">The id of the task.</param>
    /// <returns>The task, or null when the project has none with this id.</returns>
    public WorkTask? GetTask(ProjectDescriptor project, string id)
    {
        ArgumentNullException.ThrowIfNull(project);
        return TaskPath(project, id) is { } path && File.Exists(path) && WorkItemFiles.ReadHead(path, plan: false) is { } text
            ? WorkItemFiles.ParseTask(path, text)
            : null;
    }

    /// <summary>Writes a new task in the project.</summary>
    /// <param name="project">The project.</param>
    /// <param name="draft">What the task is.</param>
    /// <param name="proposedBy">The session that proposes it, which then shows it; null when a person or a tool outside a session does.</param>
    /// <returns>The task, or why it was refused.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The title or the body is blank or too long, the summary is too long, or the kind is not one.</exception>
    /// <exception cref="IOException">The file could not be written.</exception>
    public WorkTaskCreation CreateTask(ProjectDescriptor project, WorkTaskDraft draft, string? proposedBy)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(draft);
        var title = OneLine(draft.Title);
        var summary = OneLine(draft.Summary);
        var body = draft.Body?.ReplaceLineEndings("\n").Trim() ?? string.Empty;
        var kind = string.IsNullOrWhiteSpace(draft.Kind) ? WorkItemFiles.TaskKinds[^1] : draft.Kind.Trim().ToLowerInvariant();
        if (title is null || title.Length > WorkItemFiles.MaximumTitle)
        {
            throw new ArgumentException($"A task needs a title of at most {WorkItemFiles.MaximumTitle} characters.", nameof(draft));
        }

        if (body.Length == 0 || body.Length > WorkItemFiles.MaximumTaskBody)
        {
            throw new ArgumentException($"A task needs a description of at most {WorkItemFiles.MaximumTaskBody} characters.", nameof(draft));
        }

        if (summary is { Length: > WorkItemFiles.MaximumSummary })
        {
            throw new ArgumentException($"The summary of a task has at most {WorkItemFiles.MaximumSummary} characters.", nameof(draft));
        }

        if (!WorkItemFiles.TaskKinds.Contains(kind))
        {
            throw new ArgumentException($"The kind of a task is one of: {string.Join(", ", WorkItemFiles.TaskKinds)}.", nameof(draft));
        }

        if (proposedBy is not null && !Settings.Propose)
        {
            return new(null, WorkTaskRefusal.Disabled);
        }

        var existing = ListTasks(project);
        if (existing.Count >= MaximumItems)
        {
            return new(null, WorkTaskRefusal.TooManyTasks);
        }

        if (proposedBy is not null)
        {
            var open = existing.Where(static task => task.Status == WorkTaskStatus.Pending).Select(static task => task.Id).ToHashSet(StringComparer.Ordinal);
            var proposed = _links.All().Count(link => link.Kind == WorkItemKinds.Task && link.ProjectId == project.Id
                && string.Equals(link.ProposedBy, proposedBy, StringComparison.Ordinal) && link.Runner is null && open.Contains(link.Id));
            if (proposed >= MaximumProposals)
            {
                return new(null, WorkTaskRefusal.TooManyProposals);
            }
        }

        var today = DateOnly.FromDateTime(_time.GetLocalNow().DateTime);
        var directory = WorkItemFiles.TasksDirectory(project.ProjectPath);
        Directory.CreateDirectory(directory);
        var stem = WorkItemFiles.NewId(today, title);
        var created = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var text = WorkItemFiles.SerializeTask(title, kind, WorkTaskStatus.Pending, created, summary, body);
        for (var attempt = 1; ; attempt++)
        {
            var id = attempt == 1 ? stem : stem + "-" + attempt.ToString(CultureInfo.InvariantCulture);
            var path = Path.Combine(directory, id + ".md");
            try
            {
                using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(Encoding.UTF8.GetBytes(text));
                }

                if (proposedBy is not null)
                {
                    _links.Update(project.Id, WorkItemKinds.Task, id, link => link with { ProposedBy = proposedBy });
                }

                Changed?.Invoke();
                return new(WorkItemFiles.ParseTask(path, text), null);
            }
            catch (IOException) when (File.Exists(path) && attempt < 50)
            {
                // Another task of the day has this name.
            }
        }
    }

    /// <summary>Changes the status of a task in its file.</summary>
    /// <param name="project">The project.</param>
    /// <param name="id">The id of the task.</param>
    /// <param name="status">The new status.</param>
    /// <returns>False when the project has no such task or its file could not be written.</returns>
    public bool SetTaskStatus(ProjectDescriptor project, string id, WorkTaskStatus status)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (TaskPath(project, id) is not { } path || !Rewrite(path, text => WorkItemFiles.WithStatus(text, WorkItemFiles.NameOf(status))))
        {
            return false;
        }

        if (status != WorkTaskStatus.Pending)
        {
            // A task that is set aside or closed is no longer one a session carries out.
            _links.Update(project.Id, WorkItemKinds.Task, id, static link => link with { Runner = null, StartedAt = null });
        }

        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// Completes a task: its file is deleted, or kept with the status <c>done</c> when the user chose to keep
    /// completed tasks.
    /// </summary>
    /// <param name="project">The project.</param>
    /// <param name="id">The id of the task.</param>
    /// <returns>False when the project has no such task.</returns>
    public bool CompleteTask(ProjectDescriptor project, string id) => Close(project, id, Settings.CompletedTasks, WorkTaskStatus.Done);

    /// <summary>
    /// Dismisses a task: its file is deleted, or kept with the status <c>dismissed</c> when the user chose to
    /// keep dismissed tasks.
    /// </summary>
    /// <param name="project">The project.</param>
    /// <param name="id">The id of the task.</param>
    /// <returns>False when the project has no such task.</returns>
    public bool DismissTask(ProjectDescriptor project, string id) => Close(project, id, Settings.DismissedTasks, WorkTaskStatus.Dismissed);

    /// <summary>Deletes the file of a task, whatever the settings say.</summary>
    /// <param name="project">The project.</param>
    /// <param name="id">The id of the task.</param>
    /// <returns>False when the project has no such task or its file could not be deleted.</returns>
    public bool RemoveTask(ProjectDescriptor project, string id) => Close(project, id, WorkItemClosing.Delete, WorkTaskStatus.Dismissed);

    /// <summary>Lists the plans of a project, the newest first.</summary>
    /// <param name="project">The project.</param>
    /// <returns>The plans whose files can be read, up to <see cref="MaximumItems"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="project"/> is null.</exception>
    public IReadOnlyList<WorkPlan> ListPlans(ProjectDescriptor project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return ListPlansIn(project.ProjectPath);
    }

    /// <summary>Reads what describes one plan.</summary>
    /// <param name="project">The project.</param>
    /// <param name="id">The id of the plan.</param>
    /// <param name="workingDirectory">
    /// The folder a session works in. When it is another checkout of the project that has the plan, the plan is the
    /// one of that checkout: it is the copy the session keeps up to date.
    /// </param>
    /// <returns>The plan, or null when there is none with this id.</returns>
    public WorkPlan? GetPlan(ProjectDescriptor project, string id, string? workingDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        return PlanPath(project, id, workingDirectory) is { } path && WorkItemFiles.ReadHead(path, plan: true) is { } text
            ? WorkItemFiles.ParsePlan(path, text)
            : null;
    }

    /// <summary>
    /// Changes the status of a plan in its file. A plan that is done is deleted instead when the user chose not to
    /// keep completed plans.
    /// </summary>
    /// <param name="project">The project.</param>
    /// <param name="id">The id of the plan.</param>
    /// <param name="status">The new status.</param>
    /// <param name="workingDirectory">The folder of the session that changes it, as for <see cref="GetPlan"/>.</param>
    /// <returns>False when there is no such plan or its file could not be written.</returns>
    public bool SetPlanStatus(ProjectDescriptor project, string id, WorkPlanStatus status, string? workingDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (PlanPath(project, id, workingDirectory) is not { } path)
        {
            return false;
        }

        var deleted = status == WorkPlanStatus.Done && Settings.CompletedPlans == WorkItemClosing.Delete;
        if (deleted ? !Delete(path) : !Rewrite(path, text => WorkItemFiles.WithStatus(text, WorkItemFiles.NameOf(status))))
        {
            return false;
        }

        if (deleted && PlanPath(project, id, null) is null)
        {
            _links.Remove(project.Id, WorkItemKinds.Plan, id);
        }
        else if (status == WorkPlanStatus.Done)
        {
            // The copy of the project may still say what it said, until the branch of a worktree is merged: the
            // session that proposed the plan does not show it again.
            _links.Update(project.Id, WorkItemKinds.Plan, id, static link => link with { Runner = null, StartedAt = null, Acknowledged = true });
        }
        else if (status == WorkPlanStatus.Draft)
        {
            _links.Update(project.Id, WorkItemKinds.Plan, id, static link => link with { Runner = null, StartedAt = null });
        }

        Changed?.Invoke();
        return true;
    }

    /// <summary>Deletes the file of a plan.</summary>
    /// <param name="project">The project.</param>
    /// <param name="id">The id of the plan.</param>
    /// <returns>False when the project has no such plan or its file could not be deleted.</returns>
    public bool RemovePlan(ProjectDescriptor project, string id)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (PlanPath(project, id, null) is not { } path || !Delete(path))
        {
            return false;
        }

        _links.Remove(project.Id, WorkItemKinds.Plan, id);
        Changed?.Invoke();
        return true;
    }

    /// <summary>Reads the Markdown of a task or a plan, without its front matter.</summary>
    /// <param name="project">The project.</param>
    /// <param name="kind">One of <see cref="WorkItemKinds"/>.</param>
    /// <param name="id">The id of the item.</param>
    /// <param name="maximumLength">The most characters to return.</param>
    /// <param name="workingDirectory">The folder of the session that reads a plan, as for <see cref="GetPlan"/>.</param>
    /// <returns>The text and whether it was cut, or null when there is no such item.</returns>
    public (string Markdown, bool Truncated)? ReadMarkdown(ProjectDescriptor project, string kind, string id, int maximumLength, string? workingDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumLength);
        var path = kind == WorkItemKinds.Task ? TaskPath(project, id) : PlanPath(project, id, workingDirectory);
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            // The front matter is small: one more block is enough to return a full window of the body.
            var buffer = new char[maximumLength + 4096];
            var read = reader.ReadBlock(buffer, 0, buffer.Length);
            var body = WorkItemFiles.Body(new string(buffer, 0, read));
            var truncated = read == buffer.Length && reader.Peek() >= 0 || body.Length > maximumLength;
            return (body.Length > maximumLength ? body[..maximumLength] : body, truncated);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Gets what this computer knows of an item beside its file.</summary>
    /// <param name="projectId">The project.</param>
    /// <param name="kind">One of <see cref="WorkItemKinds"/>.</param>
    /// <param name="id">The id of the item.</param>
    /// <returns>The link, or null when nothing is known.</returns>
    public WorkItemLink? GetLink(string projectId, string kind, string id) => _links.Get(projectId, kind, id);

    /// <summary>Gets every link this computer keeps.</summary>
    public IReadOnlyList<WorkItemLink> Links => _links.All();

    /// <summary>Records the session that proposed an item, which shows it as a card until the user decides.</summary>
    /// <param name="projectId">The project.</param>
    /// <param name="kind">One of <see cref="WorkItemKinds"/>.</param>
    /// <param name="id">The id of the item.</param>
    /// <param name="sessionId">The session.</param>
    public void Propose(string projectId, string kind, string id, string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        Link(projectId, kind, id, link => link with { ProposedBy = sessionId, Acknowledged = false });
    }

    /// <summary>Records the session that carries an item out.</summary>
    /// <param name="projectId">The project.</param>
    /// <param name="kind">One of <see cref="WorkItemKinds"/>.</param>
    /// <param name="id">The id of the item.</param>
    /// <param name="sessionId">The session; null when no session carries it out any more.</param>
    public void SetRunner(string projectId, string kind, string id, string? sessionId)
        => Link(projectId, kind, id, link => link with { Runner = sessionId, StartedAt = sessionId is null ? null : _time.GetUtcNow(), Acknowledged = sessionId is not null || link.Acknowledged });

    /// <summary>Records that the user put the card of an item away without deciding.</summary>
    /// <param name="projectId">The project.</param>
    /// <param name="kind">One of <see cref="WorkItemKinds"/>.</param>
    /// <param name="id">The id of the item.</param>
    public void Acknowledge(string projectId, string kind, string id) => Link(projectId, kind, id, static link => link with { Acknowledged = true });

    internal static IReadOnlyList<WorkPlan> ListPlansIn(string projectPath)
    {
        var plans = new List<WorkPlan>();
        foreach (var path in Files(WorkItemFiles.PlansDirectory(projectPath)))
        {
            if (WorkItemFiles.ReadHead(path, plan: true) is { } text)
            {
                plans.Add(WorkItemFiles.ParsePlan(path, text));
            }
        }

        return plans;
    }

    private void Link(string projectId, string kind, string id, Func<WorkItemLink, WorkItemLink> change)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        if (kind is not (WorkItemKinds.Task or WorkItemKinds.Plan) || !WorkItemFiles.IsValidId(id))
        {
            throw new ArgumentException("The item is not a task or a plan of a project.", nameof(id));
        }

        _links.Update(projectId, kind, id, change);
        Changed?.Invoke();
    }

    private bool Close(ProjectDescriptor project, string id, WorkItemClosing closing, WorkTaskStatus kept)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (TaskPath(project, id) is not { } path || !File.Exists(path))
        {
            return false;
        }

        if (closing == WorkItemClosing.Keep)
        {
            return SetTaskStatus(project, id, kept);
        }

        if (!Delete(path))
        {
            return false;
        }

        _links.Remove(project.Id, WorkItemKinds.Task, id);
        Changed?.Invoke();
        return true;
    }

    private static string? TaskPath(ProjectDescriptor project, string id)
        => WorkItemFiles.IsValidId(id) ? Path.Combine(WorkItemFiles.TasksDirectory(project.ProjectPath), id + ".md") : null;

    private static string? PlanPath(ProjectDescriptor project, string id, string? workingDirectory)
    {
        if (!WorkItemFiles.IsValidId(id))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(workingDirectory) && !string.Equals(Path.TrimEndingDirectorySeparator(workingDirectory), Path.TrimEndingDirectorySeparator(project.ProjectPath), StringComparison.OrdinalIgnoreCase))
        {
            var local = Path.Combine(WorkItemFiles.PlansDirectory(workingDirectory), id + ".md");
            if (File.Exists(local))
            {
                return local;
            }
        }

        var path = Path.Combine(WorkItemFiles.PlansDirectory(project.ProjectPath), id + ".md");
        return File.Exists(path) ? path : null;
    }

    // The newest first: the names start with the day.
    private static IEnumerable<string> Files(string directory)
    {
        string[] files;
        try
        {
            if (!Directory.Exists(directory))
            {
                return [];
            }

            files = Directory.GetFiles(directory, "*.md", SearchOption.TopDirectoryOnly);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        return files.Where(static path => WorkItemFiles.IsValidId(Path.GetFileNameWithoutExtension(path)))
            .OrderByDescending(static path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .Take(MaximumItems);
    }

    private static bool Rewrite(string path, Func<string, string> change)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            var bytes = File.ReadAllBytes(path);
            var bom = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);
            var text = Encoding.UTF8.GetString(bom ? bytes.AsSpan(3) : bytes);
            var next = change(text);
            if (!string.Equals(next, text, StringComparison.Ordinal))
            {
                File.WriteAllText(path, next, new UTF8Encoding(bom));
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool Delete(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string? OneLine(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var line = value.ReplaceLineEndings(" ").Trim();
        return line.Length == 0 ? null : line;
    }
}
