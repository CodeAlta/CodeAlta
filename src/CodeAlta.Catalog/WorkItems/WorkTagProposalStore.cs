using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAlta.Core.IO;

namespace CodeAlta.Catalog.WorkItems;

/// <summary>A task a tag proposal gives the tag to, and why.</summary>
/// <param name="TaskId">The id of the task.</param>
/// <param name="Reason">Why the tag fits the task, in a line; null when the agent did not say.</param>
public sealed record WorkTagMatch(string TaskId, string? Reason);

/// <summary>
/// What an agent proposes for the tags of a project, until the user decides: the open tasks an existing tag fits,
/// or a new tag with the tasks it would have.
/// </summary>
/// <param name="ProjectId">The project.</param>
/// <param name="Name">The name of the tag.</param>
/// <param name="Description">What a new tag is for; null for an existing tag.</param>
/// <param name="NewTag">Whether the tag does not exist yet: the user creates it by applying the proposal.</param>
/// <param name="Tasks">The tasks the tag would be given to.</param>
/// <param name="SessionId">The session that proposed it; null when it is not known.</param>
/// <param name="At">When it was proposed.</param>
public sealed record WorkTagProposal(string ProjectId, string Name, string? Description, bool NewTag, IReadOnlyList<WorkTagMatch> Tasks, string? SessionId, DateTimeOffset At);

/// <summary>
/// Keeps the tag proposals that wait for the user, in <c>work_tag_proposals.json</c> of the state folder: they are not
/// tags until the user applies them, so they are not written in the project. A file that cannot be read is no proposals.
/// </summary>
internal sealed class WorkTagProposalStore
{
    private const int MaximumProposals = 200;
    private readonly string _path;
    private readonly Lock _gate = new();
    private List<WorkTagProposal>? _proposals;

    internal WorkTagProposalStore(string stateRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);
        _path = Path.Combine(stateRoot, "work_tag_proposals.json");
    }

    internal IReadOnlyList<WorkTagProposal> Of(string projectId)
    {
        lock (_gate)
        {
            return [.. Load().Where(proposal => proposal.ProjectId == projectId)];
        }
    }

    // A proposal replaces the one of the same tag of the project.
    internal void Put(WorkTagProposal proposal)
    {
        lock (_gate)
        {
            var proposals = Load();
            proposals.RemoveAll(existing => existing.ProjectId == proposal.ProjectId && existing.Name == proposal.Name);
            proposals.Add(proposal);
            if (proposals.Count > MaximumProposals)
            {
                proposals.RemoveRange(0, proposals.Count - MaximumProposals);
            }

            Save(proposals);
        }
    }

    internal WorkTagProposal? Take(string projectId, string name)
    {
        lock (_gate)
        {
            var proposals = Load();
            var found = proposals.Find(existing => existing.ProjectId == projectId && existing.Name == name);
            if (found is not null)
            {
                proposals.Remove(found);
                Save(proposals);
            }

            return found;
        }
    }

    private List<WorkTagProposal> Load()
    {
        if (_proposals is not null)
        {
            return _proposals;
        }

        try
        {
            _proposals = File.Exists(_path)
                ? JsonSerializer.Deserialize(File.ReadAllText(_path), WorkTagProposalJsonContext.Default.ListWorkTagProposal) ?? []
                : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _proposals = [];
        }

        return _proposals;
    }

    private void Save(List<WorkTagProposal> proposals)
    {
        _proposals = proposals;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            SafeFile.WriteAllBytes(_path, JsonSerializer.SerializeToUtf8Bytes(proposals, WorkTagProposalJsonContext.Default.ListWorkTagProposal));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The proposals stay in memory until the next write: a proposal is a convenience.
        }
    }
}

[JsonSerializable(typeof(List<WorkTagProposal>))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class WorkTagProposalJsonContext : JsonSerializerContext;
