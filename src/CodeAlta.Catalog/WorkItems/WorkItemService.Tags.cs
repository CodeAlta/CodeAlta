using System.Text;

namespace CodeAlta.Catalog.WorkItems;

/// <summary>The tags of the tasks of a project: its tags file, and the <c>tags</c> line of each task.</summary>
public sealed partial class WorkItemService
{
    /// <summary>Lists the tags of a project: those of its tags file, then those only found on its tasks.</summary>
    /// <param name="project">The project.</param>
    /// <returns>The tags, with how many open tasks have each, and the names the user declined.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="project"/> is null.</exception>
    public WorkTags ListTags(ProjectDescriptor project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return ListTags(project, ListTasks(project));
    }

    /// <summary>Lists the tags of a project from its tasks already read: those of its tags file, then those only found on the tasks.</summary>
    /// <param name="project">The project.</param>
    /// <param name="tasks">Its tasks, as <see cref="ListTasks"/> gave them.</param>
    /// <returns>The tags, with how many open tasks have each, and the names the user declined.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public WorkTags ListTags(ProjectDescriptor project, IReadOnlyList<WorkTask> tasks)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(tasks);
        var file = ReadTags(project);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var task in tasks)
        {
            foreach (var tag in task.Tags)
            {
                var open = task.Status is WorkTaskStatus.Pending or WorkTaskStatus.Later ? 1 : 0;
                counts[tag] = counts.GetValueOrDefault(tag) + open;
            }
        }

        var tags = new List<WorkTag>();
        foreach (var (name, description) in file.Tags)
        {
            tags.Add(new WorkTag(name, description, counts.GetValueOrDefault(name), Listed: true));
        }

        foreach (var name in counts.Keys.Where(name => !file.Tags.Any(tag => tag.Key == name)).Order(StringComparer.Ordinal))
        {
            if (tags.Count < WorkItemFiles.MaximumTags)
            {
                tags.Add(new WorkTag(name, null, counts[name], Listed: false));
            }
        }

        return new WorkTags(tags, file.Declined);
    }

    /// <summary>
    /// Adds a tag to the tags file of a project, or changes its description. A tag found only on tasks is listed so.
    /// A declined name that the user creates is no longer declined.
    /// </summary>
    /// <param name="project">The project.</param>
    /// <param name="name">The name of the tag.</param>
    /// <param name="description">What the tag is for, in a line; null or blank for none.</param>
    /// <returns>How the change ended.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="project"/> is null.</exception>
    /// <exception cref="ArgumentException">The name is not a tag name, or the description is too long.</exception>
    public WorkTagChange SetTag(ProjectDescriptor project, string name, string? description)
    {
        ArgumentNullException.ThrowIfNull(project);
        var tag = RequireTagName(name, nameof(name));
        var line = OneLine(description);
        if (line is { Length: > WorkItemFiles.MaximumTagDescription })
        {
            throw new ArgumentException($"The description of a tag has at most {WorkItemFiles.MaximumTagDescription} characters.", nameof(description));
        }

        return ChangeTags(project, (tags, declined) =>
        {
            var at = tags.FindIndex(existing => existing.Key == tag);
            if (at >= 0)
            {
                tags[at] = new(tag, line);
            }
            else if (tags.Count < WorkItemFiles.MaximumTags)
            {
                tags.Add(new(tag, line));
            }

            declined.Remove(tag);
            return WorkTagChange.Done;
        });
    }

    /// <summary>Renames a tag in the tags file of a project and on every task that has it.</summary>
    /// <param name="project">The project.</param>
    /// <param name="name">The name of the tag.</param>
    /// <param name="newName">Its new name.</param>
    /// <returns>How the change ended: <see cref="WorkTagChange.Exists"/> when another tag has the new name.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="project"/> is null.</exception>
    /// <exception cref="ArgumentException">A name is not a tag name.</exception>
    public WorkTagChange RenameTag(ProjectDescriptor project, string name, string newName)
    {
        ArgumentNullException.ThrowIfNull(project);
        var from = RequireTagName(name, nameof(name));
        var to = RequireTagName(newName, nameof(newName));
        if (from == to)
        {
            return WorkTagChange.Done;
        }

        var known = ListTags(project).Tags;
        if (known.All(tag => tag.Name != from))
        {
            return WorkTagChange.NotFound;
        }

        if (known.Any(tag => tag.Name == to))
        {
            return WorkTagChange.Exists;
        }

        var listed = ChangeTags(project, (tags, _) =>
        {
            var at = tags.FindIndex(existing => existing.Key == from);
            if (at >= 0)
            {
                tags[at] = new(to, tags[at].Value);
            }

            return WorkTagChange.Done;
        });
        return listed == WorkTagChange.Done ? RetagTasks(project, from, to) : listed;
    }

    /// <summary>Deletes a tag from the tags file of a project and from every task that has it.</summary>
    /// <param name="project">The project.</param>
    /// <param name="name">The name of the tag.</param>
    /// <returns>How the change ended.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="project"/> is null.</exception>
    /// <exception cref="ArgumentException">The name is not a tag name.</exception>
    public WorkTagChange DeleteTag(ProjectDescriptor project, string name)
    {
        ArgumentNullException.ThrowIfNull(project);
        var tag = RequireTagName(name, nameof(name));
        if (ListTags(project).Tags.All(existing => existing.Name != tag))
        {
            return WorkTagChange.NotFound;
        }

        var listed = ReadTags(project).Tags.Any(existing => existing.Key == tag)
            ? ChangeTags(project, (tags, _) => { tags.RemoveAll(existing => existing.Key == tag); return WorkTagChange.Done; })
            : WorkTagChange.Done;
        return listed == WorkTagChange.Done ? RetagTasks(project, tag, null) : listed;
    }

    /// <summary>Records that the user declined a tag an agent proposed, or forgets it.</summary>
    /// <param name="project">The project.</param>
    /// <param name="name">The name of the tag.</param>
    /// <param name="declined">True to decline the name, false to forget that it was.</param>
    /// <returns>How the change ended.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="project"/> is null.</exception>
    /// <exception cref="ArgumentException">The name is not a tag name.</exception>
    public WorkTagChange SetTagDeclined(ProjectDescriptor project, string name, bool declined)
    {
        ArgumentNullException.ThrowIfNull(project);
        var tag = RequireTagName(name, nameof(name));
        return ChangeTags(project, (_, names) =>
        {
            names.Remove(tag);
            if (declined && names.Count < WorkItemFiles.MaximumTags)
            {
                names.Add(tag);
            }

            return WorkTagChange.Done;
        });
    }

    /// <summary>Gives a task other tags, in its file.</summary>
    /// <param name="project">The project.</param>
    /// <param name="id">The id of the task.</param>
    /// <param name="tags">The tag names of the task.</param>
    /// <returns>False when the project has no such task or its file could not be written.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">A tag is not a tag name, or there are too many.</exception>
    public bool SetTaskTags(ProjectDescriptor project, string id, IReadOnlyList<string> tags)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(tags);
        // Validates the tags before the file is read.
        _ = WorkItemFiles.WithTags(string.Empty, tags);
        if (TaskPath(project, id) is not { } path || !Rewrite(path, text => WorkItemFiles.WithTags(text, tags)))
        {
            return false;
        }

        Changed?.Invoke();
        return true;
    }

    private static string RequireTagName(string name, string parameter)
        => WorkItemFiles.TagNameOf(name)
            ?? throw new ArgumentException($"'{name}' is not a tag name: lower case letters, digits and single hyphens, at most {WorkItemFiles.MaximumTagName} characters.", parameter);

    private sealed record TagsFileContent(bool Readable, IReadOnlyList<KeyValuePair<string, string?>> Tags, IReadOnlyList<string> Declined);

    // A file that cannot be read is no tags, and is not written over.
    private static TagsFileContent ReadTags(ProjectDescriptor project)
    {
        var path = WorkItemFiles.TagsPath(project.ProjectPath);
        try
        {
            if (!File.Exists(path))
            {
                return new(true, [], []);
            }

            if (WorkItemFiles.IsLinked(project.ProjectPath, path) || new FileInfo(path).Length > 256 * 1024)
            {
                return new(false, [], []);
            }

            if (SharedTextFile.TryReadAllText(path) is not { } text) return new(true, [], []);
            return WorkItemFiles.TryParseTagsFile(text, out var tags, out var declined) ? new(true, tags, declined) : new(false, [], []);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new(false, [], []);
        }
    }

    private WorkTagChange ChangeTags(ProjectDescriptor project, Func<List<KeyValuePair<string, string?>>, List<string>, WorkTagChange> change)
    {
        var file = ReadTags(project);
        if (!file.Readable)
        {
            return WorkTagChange.Failed;
        }

        var tags = file.Tags.ToList();
        var declined = file.Declined.ToList();
        var result = change(tags, declined);
        if (result != WorkTagChange.Done)
        {
            return result;
        }

        var path = WorkItemFiles.TagsPath(project.ProjectPath);
        try
        {
            if (WorkItemFiles.IsLinked(project.ProjectPath, Path.GetDirectoryName(path)!))
            {
                return WorkTagChange.Failed;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            SharedTextFile.WriteAllText(path, WorkItemFiles.SerializeTagsFile(tags, declined));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return WorkTagChange.Failed;
        }

        Changed?.Invoke();
        return WorkTagChange.Done;
    }

    // Replaces a tag with another on every task that has it, or removes it when the other is null.
    private WorkTagChange RetagTasks(ProjectDescriptor project, string from, string? to)
    {
        var failed = false;
        foreach (var task in ListTasks(project).Where(task => task.Tags.Contains(from)))
        {
            var tags = task.Tags.Select(tag => tag == from ? to : tag).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
            failed |= !Rewrite(task.Path, text => WorkItemFiles.WithTags(text, tags));
        }

        Changed?.Invoke();
        return failed ? WorkTagChange.Failed : WorkTagChange.Done;
    }
}
