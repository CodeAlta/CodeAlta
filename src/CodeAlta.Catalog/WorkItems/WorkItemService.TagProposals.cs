namespace CodeAlta.Catalog.WorkItems;

/// <summary>Why a tag proposal was not recorded.</summary>
public enum WorkTagProposalRefusal
{
    /// <summary>The project has no tag of that name, for the tasks an existing tag fits.</summary>
    UnknownTag,

    /// <summary>The project already has a tag of that name, for a new tag.</summary>
    Exists,

    /// <summary>The user declined that name when an agent proposed it.</summary>
    Declined,

    /// <summary>A task named is not an open task of the project.</summary>
    UnknownTask,

    /// <summary>A new tag needs more open tasks than were named.</summary>
    TooFewTasks,

    /// <summary>The session already has a new tag waiting for the user.</summary>
    AlreadyProposed,
}

/// <summary>What recording a tag proposal gave.</summary>
/// <param name="Proposal">The proposal; null when it was refused.</param>
/// <param name="Refusal">Why it was refused; null when it was recorded.</param>
/// <param name="Detail">What was wrong, such as the reference of a task that is not known; null otherwise.</param>
public sealed record WorkTagProposalResult(WorkTagProposal? Proposal, WorkTagProposalRefusal? Refusal, string? Detail = null);

/// <summary>The tag proposals: agents propose, the user applies or dismisses.</summary>
public sealed partial class WorkItemService
{
    /// <summary>The fewest open tasks a new tag is proposed for, the task that leads to it included.</summary>
    public const int MinimumNewTagTasks = 3;

    /// <summary>Lists the tag proposals of a project that wait for the user.</summary>
    /// <param name="projectId">The project.</param>
    /// <returns>The proposals, the oldest first.</returns>
    public IReadOnlyList<WorkTagProposal> ListTagProposals(string projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        return _tagProposals.Of(projectId);
    }

    /// <summary>Records the open tasks an existing tag fits, as a session found them, for the user to apply.</summary>
    /// <param name="project">The project.</param>
    /// <param name="name">The name of the tag.</param>
    /// <param name="matches">The tasks, by id or short id, each with why; the ones that already have the tag are left out.</param>
    /// <param name="sessionId">The session that found them; null when it is not known.</param>
    /// <returns>The proposal, or why it was refused.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The name is not a tag name.</exception>
    public WorkTagProposalResult ProposeTagMatches(ProjectDescriptor project, string name, IReadOnlyList<WorkTagMatch> matches, string? sessionId)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(matches);
        var tag = RequireTagName(name, nameof(name));
        var tasks = ListTasks(project);
        if (ListTags(project, tasks).Tags.All(existing => existing.Name != tag))
        {
            return new(null, WorkTagProposalRefusal.UnknownTag);
        }

        if (Resolve(tasks, matches, out var unknown) is not { } found)
        {
            return new(null, WorkTagProposalRefusal.UnknownTask, unknown);
        }

        var proposal = new WorkTagProposal(project.Id, tag, null, NewTag: false, [.. found.Where(match => !tasks.First(task => task.Id == match.TaskId).Tags.Contains(tag))],
            sessionId, _time.GetUtcNow());
        _tagProposals.Put(proposal);
        Changed?.Invoke();
        return new(proposal, null);
    }

    /// <summary>
    /// Records a new tag an agent proposes, with the open tasks it would have, for the user to create or decline. It is
    /// not a tag until the user creates it.
    /// </summary>
    /// <param name="project">The project.</param>
    /// <param name="name">The name of the tag.</param>
    /// <param name="description">What the tag would be for, in a line.</param>
    /// <param name="matches">The tasks, by id or short id, each with why: at least <see cref="MinimumNewTagTasks"/>.</param>
    /// <param name="sessionId">The session that proposes it, whose card shows it; null when it is not known.</param>
    /// <returns>The proposal, or why it was refused.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The name is not a tag name, or the description is blank or too long.</exception>
    public WorkTagProposalResult ProposeTag(ProjectDescriptor project, string name, string description, IReadOnlyList<WorkTagMatch> matches, string? sessionId)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(matches);
        var tag = RequireTagName(name, nameof(name));
        if (OneLine(description) is not { Length: <= WorkItemFiles.MaximumTagDescription } line)
        {
            throw new ArgumentException($"A new tag needs a description of at most {WorkItemFiles.MaximumTagDescription} characters.", nameof(description));
        }

        var tasks = ListTasks(project);
        var tags = ListTags(project, tasks);
        if (tags.Tags.Any(existing => existing.Name == tag))
        {
            return new(null, WorkTagProposalRefusal.Exists);
        }

        if (tags.Declined.Contains(tag))
        {
            return new(null, WorkTagProposalRefusal.Declined);
        }

        if (Resolve(tasks, matches, out var unknown) is not { } found)
        {
            return new(null, WorkTagProposalRefusal.UnknownTask, unknown);
        }

        if (found.Count < MinimumNewTagTasks)
        {
            return new(null, WorkTagProposalRefusal.TooFewTasks);
        }

        if (sessionId is not null && _tagProposals.Of(project.Id).Any(existing => existing.NewTag && existing.SessionId == sessionId && existing.Name != tag))
        {
            return new(null, WorkTagProposalRefusal.AlreadyProposed);
        }

        var proposal = new WorkTagProposal(project.Id, tag, line, NewTag: true, found, sessionId, _time.GetUtcNow());
        _tagProposals.Put(proposal);
        Changed?.Invoke();
        return new(proposal, null);
    }

    /// <summary>
    /// Applies a tag proposal as the user chose: a new tag is created with the description given, and the tasks chosen
    /// get the tag. The proposal is then gone.
    /// </summary>
    /// <param name="project">The project.</param>
    /// <param name="name">The name of the tag.</param>
    /// <param name="taskIds">The tasks the user kept, by id.</param>
    /// <param name="description">The description of a new tag, as the user left it; null keeps the one proposed.</param>
    /// <returns>How it ended: <see cref="WorkTagChange.NotFound"/> without such a proposal.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The name is not a tag name.</exception>
    public WorkTagChange ApplyTagProposal(ProjectDescriptor project, string name, IReadOnlyList<string> taskIds, string? description)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(taskIds);
        var tag = RequireTagName(name, nameof(name));
        if (_tagProposals.Of(project.Id).FirstOrDefault(existing => existing.Name == tag) is not { } proposal)
        {
            return WorkTagChange.NotFound;
        }

        if (proposal.NewTag && SetTag(project, tag, description ?? proposal.Description) is var created and not WorkTagChange.Done)
        {
            return created;
        }

        var failed = false;
        foreach (var task in ListTasks(project).Where(task => taskIds.Contains(task.Id) && !task.Tags.Contains(tag)))
        {
            failed |= task.Tags.Count >= WorkItemFiles.MaximumTaskTags || !SetTaskTags(project, task.Id, [.. task.Tags, tag]);
        }

        _tagProposals.Take(project.Id, tag);
        Changed?.Invoke();
        return failed ? WorkTagChange.Failed : WorkTagChange.Done;
    }

    /// <summary>Dismisses a tag proposal; the name of a new tag is declined, so that no agent proposes it again.</summary>
    /// <param name="project">The project.</param>
    /// <param name="name">The name of the tag.</param>
    /// <returns>How it ended: <see cref="WorkTagChange.NotFound"/> without such a proposal.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="project"/> is null.</exception>
    /// <exception cref="ArgumentException">The name is not a tag name.</exception>
    public WorkTagChange DismissTagProposal(ProjectDescriptor project, string name)
    {
        ArgumentNullException.ThrowIfNull(project);
        var tag = RequireTagName(name, nameof(name));
        if (_tagProposals.Take(project.Id, tag) is not { } proposal)
        {
            return WorkTagChange.NotFound;
        }

        var result = proposal.NewTag ? SetTagDeclined(project, tag, declined: true) : WorkTagChange.Done;
        Changed?.Invoke();
        return result;
    }

    // The open tasks the references name, each once with its full id; null with the first reference that names none.
    private static List<WorkTagMatch>? Resolve(IReadOnlyList<WorkTask> tasks, IReadOnlyList<WorkTagMatch> matches, out string? unknown)
    {
        var found = new List<WorkTagMatch>();
        foreach (var match in matches)
        {
            var reference = match.TaskId.Trim();
            var named = tasks.Where(task => task.Status is WorkTaskStatus.Pending or WorkTaskStatus.Later
                && (task.Id == reference || task.ShortId == reference.ToLowerInvariant())).ToList();
            if (named.Count != 1)
            {
                unknown = reference;
                return null;
            }

            if (found.TrueForAll(existing => existing.TaskId != named[0].Id))
            {
                found.Add(new WorkTagMatch(named[0].Id, OneLine(match.Reason) is { } reason ? reason[..Math.Min(reason.Length, WorkItemFiles.MaximumSummary)] : null));
            }
        }

        unknown = null;
        return found;
    }
}
