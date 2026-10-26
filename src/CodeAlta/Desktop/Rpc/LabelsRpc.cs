using CodeAlta.Catalog;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// The labels of the catalog for the page: the names given to projects to find them together, in a tree, each with the
/// projects that have it. Every change asks every window to read the spaces and the labels again.
/// </summary>
[NeoRpcService("labels", Version = 1)]
internal sealed class LabelsService
{
    /// <summary>The most projects one request names.</summary>
    internal const int MaximumProjects = 256;

    private readonly LabelCatalog? _catalog;
    private readonly DesktopSpaceView? _view;
    private readonly string? _epoch;

    /// <summary>Creates the service of a window whose host keeps no labels: it answers <c>unavailable</c>.</summary>
    public LabelsService()
    {
    }

    /// <summary>Creates the service of an owned host.</summary>
    /// <param name="catalog">The labels.</param>
    /// <param name="view">What asks the windows to read the labels again.</param>
    /// <param name="epoch">The host epoch.</param>
    internal LabelsService(LabelCatalog catalog, DesktopSpaceView view, string epoch)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(view);
        ArgumentException.ThrowIfNullOrWhiteSpace(epoch);
        (_catalog, _view, _epoch) = (catalog, view, epoch);
    }

    /// <summary>Lists the labels in the order of their tree, each with the projects whose file names it.</summary>
    [NeoRpcMethod("list")]
    public async Task<LabelsResponse> ListAsync(LabelsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, []);
        try
        {
            var members = await _catalog!.LoadMembersAsync(includeArchived: true, cancellationToken).ConfigureAwait(false);
            var labels = members.Select(static member => member.Label).ToArray();
            return new("ok", [.. members.Select(member => Item(member.Label, labels, member.ProjectIds))]);
        }
        catch (Exception exception) when (Unreadable(exception))
        {
            return new("unreadable", []);
        }
    }

    /// <summary>Creates a label, last under its parent, and gives it to the projects named.</summary>
    [NeoRpcMethod("create")]
    public Task<LabelChangeResponse> CreateAsync(LabelCreateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ChangeAsync(request.ExpectedEpoch, async () =>
        {
            var created = await _catalog!.CreateAsync(request.Name ?? string.Empty, request.Parent, request.Icon, request.Color, request.Space,
                request.Favorite, request.Description, cancellationToken).ConfigureAwait(false);
            if (!created.Succeeded) return created;
            foreach (var projectId in (request.ProjectIds ?? []).Take(MaximumProjects))
            {
                var assigned = await _catalog.AssignAsync(projectId, [created.Label!.Id], null, CancellationToken.None).ConfigureAwait(false);
                if (!assigned.Succeeded) return assigned with { Label = created.Label };
            }

            return created;
        });
    }

    /// <summary>Changes the name, the description, the icon, the color, the favorite or the space of a label; what is null stays.</summary>
    [NeoRpcMethod("update")]
    public Task<LabelChangeResponse> UpdateAsync(LabelUpdateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ChangeAsync(request.ExpectedEpoch, () => _catalog!.UpdateAsync(request.Id ?? string.Empty,
            new LabelEdit(request.Name, request.Description, request.Icon, request.Color, request.Favorite, request.Space), cancellationToken));
    }

    /// <summary>Moves a label, with its sub-labels, under another label or to the top, at a place among its new siblings.</summary>
    [NeoRpcMethod("move")]
    public Task<LabelChangeResponse> MoveAsync(LabelMoveRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ChangeAsync(request.ExpectedEpoch, () => _catalog!.MoveAsync(request.Id ?? string.Empty,
            string.IsNullOrWhiteSpace(request.Parent) ? null : request.Parent, request.Index, cancellationToken));
    }

    /// <summary>Deletes a label: its projects lose it, and its sub-labels take its place.</summary>
    [NeoRpcMethod("delete")]
    public Task<LabelChangeResponse> DeleteAsync(LabelDeleteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ChangeAsync(request.ExpectedEpoch, () => _catalog!.DeleteAsync(request.Id ?? string.Empty, cancellationToken));
    }

    /// <summary>Gives labels to a project and takes others from it, in one edit of its file.</summary>
    [NeoRpcMethod("assign")]
    public Task<LabelChangeResponse> AssignAsync(LabelAssignRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ChangeAsync(request.ExpectedEpoch, () => _catalog!.AssignAsync(request.ProjectId ?? string.Empty, request.Add, request.Remove, cancellationToken));
    }

    private async Task<LabelChangeResponse> ChangeAsync(string? expectedEpoch, Func<Task<LabelChange>> change)
    {
        if (Refuse(expectedEpoch) is { } refused) return new(refused, null, null);
        try
        {
            var result = await change().ConfigureAwait(false);
            var status = result.Status switch
            {
                LabelChangeStatus.Ok => "ok",
                LabelChangeStatus.NotFound => "not_found",
                LabelChangeStatus.Conflict => "conflict",
                _ => "invalid",
            };
            // The other windows read the labels again; this page does when it gets the answer.
            if (result.Succeeded) _view!.NotifyChanged();
            if (result.Label is not { } label) return new(status, result.Succeeded ? null : result.Message, null);
            var labels = label.SourcePath is null ? [label] : await _catalog!.LoadAsync(CancellationToken.None).ConfigureAwait(false);
            return new(status, result.Succeeded ? null : result.Message, Item(label, labels, []));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new("write_failed", null, null);
        }
        catch (Exception exception) when (Unreadable(exception))
        {
            return new("unreadable", null, null);
        }
    }

    private string? Refuse(string? expectedEpoch)
        => _catalog is null ? "unavailable" : string.Equals(expectedEpoch, _epoch, StringComparison.Ordinal) ? null : "stale_epoch";

    private static bool Unreadable(Exception exception)
        => exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or SharpYaml.YamlException;

    private static LabelItem Item(LabelDescriptor label, IReadOnlyList<LabelDescriptor> labels, IReadOnlyList<string> projectIds)
        => new(label.Id, label.Name, label.Description, label.Icon, label.Color, label.Order, label.Parent, label.Favorite, label.Space,
            LabelCatalog.PathOf(label, labels), [.. projectIds]);
}

/// <summary>Asks for the labels.</summary>
/// <param name="ExpectedEpoch">The host epoch.</param>
internal sealed record LabelsRequest(string ExpectedEpoch);

/// <summary>The labels. Status is <c>ok</c>, <c>unavailable</c>, <c>stale_epoch</c> or <c>unreadable</c> (a catalog file).</summary>
/// <param name="Status">How the reading ended.</param>
/// <param name="Labels">The labels, in the order of their tree: a label, then the labels under it.</param>
internal sealed record LabelsResponse(string Status, IReadOnlyList<LabelItem> Labels);

/// <summary>One label.</summary>
/// <param name="Id">Its identifier, which a rename or a move keeps.</param>
/// <param name="Name">The name shown to the user.</param>
/// <param name="Description">What it stands for; null when nothing is said.</param>
/// <param name="Icon">The name of its icon; null for the usual one.</param>
/// <param name="Color">The color of its badge, <c>#rgb</c> or <c>#rrggbb</c>; null for none.</param>
/// <param name="Order">Its place among the labels of its parent, from 1.</param>
/// <param name="Parent">The label it is under; null for a top-level label.</param>
/// <param name="Favorite">Whether the user keeps it on top of the others.</param>
/// <param name="Space">Its space, the one of its top-level label; null for every space.</param>
/// <param name="Path">The names of the labels above it and its own: <c>Infra › Helm charts</c>.</param>
/// <param name="ProjectIds">The projects whose file names it, archived ones included.</param>
internal sealed record LabelItem(string Id, string Name, string? Description, string? Icon, string? Color, int Order, string? Parent, bool Favorite,
    string? Space, string Path, IReadOnlyList<string> ProjectIds);

/// <summary>Creates a label.</summary>
/// <param name="ExpectedEpoch">The host epoch.</param>
/// <param name="Name">Its name.</param>
/// <param name="Parent">The label it goes under; null for the top level.</param>
/// <param name="Icon">The name of its icon.</param>
/// <param name="Color">The color of its badge.</param>
/// <param name="Space">The space of a top-level label; null for every space.</param>
/// <param name="Favorite">Whether it is a favorite.</param>
/// <param name="Description">What it stands for.</param>
/// <param name="ProjectIds">The projects that get it.</param>
internal sealed record LabelCreateRequest(string ExpectedEpoch, string? Name, string? Parent, string? Icon, string? Color, string? Space, bool Favorite,
    string? Description, IReadOnlyList<string>? ProjectIds);

/// <summary>Changes a label; a value left null stays as it is, and an empty one is removed.</summary>
internal sealed record LabelUpdateRequest(string ExpectedEpoch, string? Id, string? Name, string? Description, string? Icon, string? Color, bool? Favorite, string? Space);

/// <summary>Moves a label under a parent (null for the top level), at a place among its new siblings (-1 for the last).</summary>
internal sealed record LabelMoveRequest(string ExpectedEpoch, string? Id, string? Parent, int Index);

/// <summary>Names a label to delete.</summary>
internal sealed record LabelDeleteRequest(string ExpectedEpoch, string? Id);

/// <summary>Names the labels a project gets and loses.</summary>
internal sealed record LabelAssignRequest(string ExpectedEpoch, string? ProjectId, IReadOnlyList<string>? Add, IReadOnlyList<string>? Remove);

/// <summary>
/// Status is <c>ok</c>, <c>invalid</c> (with a message), <c>not_found</c>, <c>conflict</c>, <c>write_failed</c>,
/// <c>unreadable</c>, <c>unavailable</c> or <c>stale_epoch</c>.
/// </summary>
/// <param name="Status">How the change ended.</param>
/// <param name="Message">What was wrong, for a change that was not made.</param>
/// <param name="Label">The label after the change, without its projects; null when the change names none.</param>
internal sealed record LabelChangeResponse(string Status, string? Message, LabelItem? Label);
