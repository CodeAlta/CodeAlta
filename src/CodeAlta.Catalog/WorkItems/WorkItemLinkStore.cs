using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeAlta.Catalog.WorkItems;

/// <summary>
/// Keeps the links between tasks or plans and the sessions of this computer, in <c>work_items.json</c> of the
/// state folder of the application. A file that cannot be read is an empty one: a link is a convenience, and
/// the tasks and the plans are in their own files.
/// </summary>
internal sealed class WorkItemLinkStore
{
    private const int MaximumLinks = 2000;
    private readonly string _path;
    private readonly Lock _gate = new();
    private Dictionary<string, WorkItemLink>? _links;

    internal WorkItemLinkStore(string stateRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);
        _path = Path.Combine(stateRoot, "work_items.json");
    }

    internal WorkItemLink? Get(string projectId, string kind, string id)
    {
        lock (_gate)
        {
            return Load().GetValueOrDefault(Key(projectId, kind, id));
        }
    }

    internal IReadOnlyList<WorkItemLink> All()
    {
        lock (_gate)
        {
            return [.. Load().Values];
        }
    }

    /// <summary>Changes the link of an item; a link that says nothing any more is removed.</summary>
    internal void Update(string projectId, string kind, string id, Func<WorkItemLink, WorkItemLink> change)
    {
        lock (_gate)
        {
            var links = Load();
            var key = Key(projectId, kind, id);
            var next = change(links.GetValueOrDefault(key) ?? new WorkItemLink(projectId, kind, id));
            if (next is { ProposedBy: null, Runner: null, Acknowledged: false })
            {
                if (!links.Remove(key))
                {
                    return;
                }
            }
            else
            {
                if (!links.ContainsKey(key) && links.Count >= MaximumLinks)
                {
                    // The oldest links are of items long gone.
                    links.Remove(links.Keys.First());
                }

                links[key] = next;
            }

            Save(links);
        }
    }

    internal void Remove(string projectId, string kind, string id) => Update(projectId, kind, id, static link => link with { ProposedBy = null, Runner = null, StartedAt = null, Acknowledged = false });

    private static string Key(string projectId, string kind, string id) => string.Concat(projectId, "\n", kind, "\n", id);

    private Dictionary<string, WorkItemLink> Load()
    {
        if (_links is not null)
        {
            return _links;
        }

        var links = new Dictionary<string, WorkItemLink>(StringComparer.Ordinal);
        try
        {
            if (File.Exists(_path))
            {
                using var stream = File.OpenRead(_path);
                foreach (var link in JsonSerializer.Deserialize(stream, WorkItemLinkJsonContext.Default.WorkItemLinkFile)?.Items ?? [])
                {
                    if (!string.IsNullOrWhiteSpace(link.ProjectId) && link.Kind is WorkItemKinds.Task or WorkItemKinds.Plan && WorkItemFiles.IsValidId(link.Id))
                    {
                        links[Key(link.ProjectId, link.Kind, link.Id)] = link;
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            links.Clear();
        }

        return _links = links;
    }

    private void Save(Dictionary<string, WorkItemLink> links)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var staged = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var stream = File.Create(staged))
            {
                JsonSerializer.Serialize(stream, new WorkItemLinkFile { Items = [.. links.Values] }, WorkItemLinkJsonContext.Default.WorkItemLinkFile);
            }

            File.Move(staged, _path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // What is in memory keeps serving this run of the application.
        }
    }
}

internal sealed class WorkItemLinkFile
{
    public List<WorkItemLink> Items { get; set; } = [];
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, WriteIndented = true)]
[JsonSerializable(typeof(WorkItemLinkFile))]
internal sealed partial class WorkItemLinkJsonContext : JsonSerializerContext;
