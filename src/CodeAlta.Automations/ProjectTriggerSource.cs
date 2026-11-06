using System.Text.Json;
using System.Text.Json.Nodes;
using CodeAlta.Catalog;
using CodeAlta.Plugin.Git;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Automations;

/// <summary>
/// The Project trigger: a project is added to the catalog, archived, or removed from it; with a space, joins that space,
/// is archived in it or leaves it. It compares the catalog with what its cursor saw, so it sees the projects of any
/// process and those added while the application was away; a change this process makes asks for a look at once.
/// </summary>
internal sealed class ProjectTriggerSource : IAutomationTriggerSource
{
    /// <summary>A project joins the catalog, or the space; or comes back after it was archived.</summary>
    internal const string Added = "added";

    /// <summary>A project is archived.</summary>
    internal const string Archived = "archived";

    /// <summary>A project leaves the catalog, or the space.</summary>
    internal const string Removed = "removed";

    /// <summary>The most origins of added projects kept until a reading takes them.</summary>
    internal const int MaximumOrigins = 256;

    private readonly Func<CancellationToken, Task<IReadOnlyList<ProjectDescriptor>>> _projects;
    private readonly Func<string, CancellationToken, ValueTask<GitRepositoryReference?>> _repository;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, string> _origins = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _originOrder = new();

    /// <summary>Creates the source.</summary>
    /// <param name="projects">Lists the projects of the catalog as they are now.</param>
    /// <param name="repository">Finds the hosted repository of a folder; null for the remotes of its git checkout.</param>
    internal ProjectTriggerSource(Func<CancellationToken, Task<IReadOnlyList<ProjectDescriptor>>> projects,
        Func<string, CancellationToken, ValueTask<GitRepositoryReference?>>? repository = null)
    {
        ArgumentNullException.ThrowIfNull(projects);
        _projects = projects;
        _repository = repository ?? ((folder, token) => GitIssueLookup.DetectRepositoryAsync(folder, token));
        Contribution = new PluginAutomationTriggerContribution
        {
            Type = AutomationSourceTypes.Project,
            DisplayName = "Project",
            Description = "When a project is added to the catalog, archived or removed, in any space or in one.",
            Events =
            [
                new(Added, "Added") { Description = "A project is added to the catalog, or to the space, or comes back after it was archived." },
                new(Archived, "Archived") { Description = "A project is archived." },
                new(Removed, "Removed") { Description = "A project is removed from the catalog, or leaves the space." },
            ],
            Fields = [new("space", "Space", PluginTriggerFieldKind.Space) { Description = "Only the projects of this space; every project when empty." }],
            Source = this,
            Signal = Signal,
        };
    }

    /// <summary>Gets what the trigger is, for the registry of the sources.</summary>
    internal PluginAutomationTriggerContribution Contribution { get; }

    /// <summary>Gets the signal raised when this process changed a project.</summary>
    internal PluginTriggerSignal Signal { get; } = new();

    /// <inheritdoc />
    public PluginTriggerDelivery Delivery => PluginTriggerDelivery.Polled;

    /// <summary>Takes a change of the catalog of this process: what added a project, and a look at once.</summary>
    /// <param name="change">The change.</param>
    internal void OnCatalogChanged(ProjectCatalogChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (change is { Kind: ProjectCatalogChangeKind.Added or ProjectCatalogChangeKind.Saved, Origin: { } origin })
        {
            lock (_gate)
            {
                if (!_origins.ContainsKey(change.Project.Id)) _originOrder.Enqueue(change.Project.Id);
                _origins[change.Project.Id] = origin;
                while (_originOrder.Count > MaximumOrigins) _origins.Remove(_originOrder.Dequeue());
            }
        }

        Signal.Raise();
    }

    /// <inheritdoc />
    public async ValueTask<PluginTriggerPage> ReadAsync(PluginTriggerContext context, string? cursor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var space = context.Settings.Text("space") is { Length: > 0 } named && !string.Equals(named, SpaceDescriptor.DefaultId, StringComparison.OrdinalIgnoreCase) ? named : null;
        var projects = await _projects(cancellationToken).ConfigureAwait(false);
        var now = new Dictionary<string, Known>(StringComparer.OrdinalIgnoreCase);
        // A project of this instance that has no file of the catalog is not one of the catalog.
        foreach (var project in projects.Where(project => project.SourcePath is not null && InSpace(project, space)))
            now.TryAdd(project.Id, new Known(project.Slug, Name(project), project.ProjectPath, project.Archived, [.. project.Spaces]) { Project = project });
        var (generation, before) = Read(cursor);
        if (before is null) return new PluginTriggerPage([], Write(generation, now));

        var events = new List<PluginTriggerEvent>();
        var changed = false;
        foreach (var (id, known) in now.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            if (!before.TryGetValue(id, out var previous))
            {
                changed = true;
                if (!known.Archived) events.Add(await EventAsync(Added, id, known, generation + 1, cancellationToken).ConfigureAwait(false));
            }
            else if (previous.Archived != known.Archived)
            {
                changed = true;
                events.Add(await EventAsync(known.Archived ? Archived : Added, id, known, generation + 1, cancellationToken).ConfigureAwait(false));
            }
            else if (!previous.Equals(known))
            {
                changed = true;
            }
        }

        foreach (var (id, previous) in before.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            if (now.ContainsKey(id)) continue;
            changed = true;
            events.Add(await EventAsync(Removed, id, previous, generation + 1, cancellationToken).ConfigureAwait(false));
        }

        return new PluginTriggerPage(events, changed ? Write(generation + 1, now) : cursor);
    }

    private static bool InSpace(ProjectDescriptor project, string? space)
        => space is null || project.Spaces.Contains(space, StringComparer.OrdinalIgnoreCase);

    private static string Name(ProjectDescriptor project) => string.IsNullOrWhiteSpace(project.DisplayName) ? project.Name : project.DisplayName;

    // The event of a project: its id holds the generation of the cursor, so that a project archived, restored and archived
    // again is archived twice.
    private async Task<PluginTriggerEvent> EventAsync(string on, string id, Known known, long generation, CancellationToken cancellationToken)
    {
        string? origin = null;
        if (on == Added)
        {
            lock (_gate) origin = _origins.Remove(id, out var found) ? found : null;
            origin ??= ProjectOrigins.Unknown;
        }

        GitRepositoryReference? repository = null;
        // The folder of a project that left is not read: it may be gone with it.
        if (on != Removed && Directory.Exists(known.Path))
        {
            try
            {
                repository = await _repository(known.Path, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            {
                // A project without a repository that can be read is still a project.
            }
        }

        var repoUrl = repository is null ? null : WebUrl(repository);
        var data = new JsonObject
        {
            ["projectId"] = id, ["slug"] = known.Slug, ["name"] = known.Name, ["path"] = known.Path, ["archived"] = known.Archived,
            ["spaces"] = new JsonArray([.. known.Spaces.Select(static space => (JsonNode?)space)]),
        };
        if (known.Project?.Description is { Length: > 0 } description) data["description"] = description;
        if (repository is not null) data["repository"] = repository.FullName;
        if (repoUrl is not null) data["repoUrl"] = repoUrl;
        if (origin is not null) data["addedBy"] = origin;

        var fields = new List<KeyValuePair<string, string>> { new("Project", $"{known.Name} ({known.Slug})"), new("Folder", known.Path) };
        if (known.Spaces.Count > 0) fields.Add(new("Spaces", string.Join(", ", known.Spaces)));
        if (repoUrl is not null) fields.Add(new("Repository", repoUrl));
        if (origin is not null) fields.Add(new("Added by", origin));
        var verb = on switch { Added => "added", Archived => "archived", _ => "removed" };
        return new PluginTriggerEvent($"{on}:{id}:{generation}", on, $"Project {known.Name} {verb}") { Fields = fields, Url = repoUrl, DataJson = data.ToJsonString() };
    }

    private static string WebUrl(GitRepositoryReference repository) => repository.Provider == GitRemoteProvider.AzureDevOps
        ? $"https://{repository.Host}/{repository.Owner}/{repository.Project}/_git/{repository.Name}"
        : $"https://{repository.Host}/{repository.FullName}";

    // A project as the cursor keeps it: what tells its events apart, and what describes it once it is gone.
    private sealed record Known(string Slug, string Name, string Path, bool Archived, IReadOnlyList<string> Spaces)
    {
        // The project it was read from, for what its event says beyond the cursor; never written.
        public ProjectDescriptor? Project { get; init; }

        public bool Equals(Known? other) => other is not null && Slug == other.Slug && Name == other.Name && Path == other.Path && Archived == other.Archived
            && Spaces.SequenceEqual(other.Spaces, StringComparer.OrdinalIgnoreCase);

        public override int GetHashCode() => HashCode.Combine(Slug, Name, Path, Archived);
    }

    // The cursor: a generation, which grows with each change it saw, and the projects it saw.
    private static string Write(long generation, Dictionary<string, Known> projects)
    {
        var list = new JsonObject();
        foreach (var (id, known) in projects.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            list[id] = new JsonObject
            {
                ["s"] = known.Slug, ["n"] = known.Name, ["p"] = known.Path, ["a"] = known.Archived,
                ["sp"] = new JsonArray([.. known.Spaces.Select(static space => (JsonNode?)space)]),
            };
        }

        return new JsonObject { ["g"] = generation, ["projects"] = list }.ToJsonString();
    }

    private static (long Generation, Dictionary<string, Known>? Projects) Read(string? cursor)
    {
        if (cursor is null) return (0, null);
        try
        {
            if (JsonNode.Parse(cursor) is not JsonObject root || root["projects"] is not JsonObject list) return (0, null);
            var generation = root["g"]?.GetValue<long>() ?? 0;
            var projects = new Dictionary<string, Known>(StringComparer.OrdinalIgnoreCase);
            foreach (var (id, value) in list)
            {
                if (value is not JsonObject item) continue;
                projects[id] = new Known(item["s"]?.GetValue<string>() ?? string.Empty, item["n"]?.GetValue<string>() ?? string.Empty, item["p"]?.GetValue<string>() ?? string.Empty,
                    item["a"]?.GetValue<bool>() ?? false, [.. (item["sp"] as JsonArray ?? []).Select(static space => space?.GetValue<string>()).OfType<string>()]);
            }

            return (generation, projects);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            // A cursor that cannot be read starts again from what is there now.
            return (0, null);
        }
    }
}
