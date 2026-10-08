using System.Runtime.CompilerServices;
using System.Threading.Channels;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// The spaces of the catalog for the page: the groups of projects the user works on together, the projects
/// of each, the one the window shows, and what the sessions of every space are doing. The page shows one
/// space at a time; the code calls a space a space.
/// </summary>
[NeoRpcService("spaces", Version = 1)]
internal sealed class SpacesService
{
    /// <summary>The most sessions one activity reading lists.</summary>
    internal const int MaximumActivitySessions = 128;

    private readonly SpaceCatalog? _catalog;
    private readonly DesktopSpaceView? _view;
    private readonly Func<IReadOnlyList<SessionRuntimeOverview>>? _runs;
    private readonly Func<ValueTask<IReadOnlyList<string>>>? _waiting;
    private readonly string? _epoch;

    /// <summary>Creates the service of a window whose host keeps no spaces: it answers <c>unavailable</c>.</summary>
    public SpacesService()
    {
    }

    /// <summary>Creates the service of an owned host.</summary>
    /// <param name="catalog">The spaces.</param>
    /// <param name="view">The space the window shows and the requests that reach it.</param>
    /// <param name="runs">What the sessions of the runtime are doing.</param>
    /// <param name="waiting">The sessions that wait for the user: a question, a command to review, a form.</param>
    /// <param name="epoch">The host epoch.</param>
    internal SpacesService(SpaceCatalog catalog, DesktopSpaceView view, Func<IReadOnlyList<SessionRuntimeOverview>> runs,
        Func<ValueTask<IReadOnlyList<string>>> waiting, string epoch)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(waiting);
        ArgumentException.ThrowIfNullOrWhiteSpace(epoch);
        (_catalog, _view, _runs, _waiting, _epoch) = (catalog, view, runs, waiting, epoch);
    }

    /// <summary>Lists the spaces with the projects of each, the default one first.</summary>
    [NeoRpcMethod("list")]
    public async Task<SpacesResponse> ListAsync(SpacesRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, []);
        try
        {
            return new("ok", [.. (await _catalog!.LoadMembersAsync(includeArchived: true, cancellationToken).ConfigureAwait(false)).Select(Item)]);
        }
        catch (Exception exception) when (Unreadable(exception))
        {
            return new("unreadable", []);
        }
    }

    /// <summary>Creates a space, with the projects given in it.</summary>
    [NeoRpcMethod("create")]
    public Task<SpaceChangeResponse> CreateAsync(SpaceCreateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ChangeAsync(request.ExpectedEpoch, async () =>
        {
            var created = await _catalog!.CreateAsync(request.Name ?? string.Empty, request.Description, request.Icon, request.Color, cancellationToken).ConfigureAwait(false);
            if (!created.Succeeded) return created;
            foreach (var projectId in (request.ProjectIds ?? []).Take(256))
            {
                var joined = await _catalog.AssignAsync(projectId, [created.Space!.Id], null, CancellationToken.None).ConfigureAwait(false);
                if (!joined.Succeeded) return joined with { Space = created.Space };
            }

            return created;
        });
    }

    /// <summary>Changes the name, the description, the icon or the color of a space; what is null stays.</summary>
    [NeoRpcMethod("update")]
    public Task<SpaceChangeResponse> UpdateAsync(SpaceUpdateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ChangeAsync(request.ExpectedEpoch, () => _catalog!.UpdateAsync(request.Id ?? string.Empty,
            new SpaceEdit(request.Name, request.Description, request.Icon, request.Color), cancellationToken));
    }

    /// <summary>Deletes a space. Its projects stay, in the default space and in their other ones.</summary>
    [NeoRpcMethod("delete")]
    public Task<SpaceChangeResponse> DeleteAsync(SpaceDeleteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ChangeAsync(request.ExpectedEpoch, () => _catalog!.DeleteAsync(request.Id ?? string.Empty, cancellationToken));
    }

    /// <summary>Makes a project join spaces and leave others, in one edit of its file.</summary>
    [NeoRpcMethod("assign")]
    public Task<SpaceChangeResponse> AssignAsync(SpaceAssignRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ChangeAsync(request.ExpectedEpoch, () => _catalog!.AssignAsync(request.ProjectId ?? string.Empty, request.Join, request.Leave, cancellationToken));
    }

    /// <summary>Puts the spaces in the order given.</summary>
    [NeoRpcMethod("reorder")]
    public Task<SpaceChangeResponse> ReorderAsync(SpaceReorderRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ChangeAsync(request.ExpectedEpoch, () => _catalog!.ReorderAsync(request.Ids ?? [], cancellationToken));
    }

    /// <summary>
    /// The page says which space it shows: the commands of the sessions read it as their current space.
    /// </summary>
    [NeoRpcMethod("shown")]
    public Task<SpaceChangeResponse> ShownAsync(SpaceShownRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return Task.FromResult(new SpaceChangeResponse(refused, null, null));
        if (!SpaceDescriptor.IsValidId(request.Id)) return Task.FromResult(new SpaceChangeResponse("invalid", null, null));
        _view!.SetShown(request.Id!);
        return Task.FromResult(new SpaceChangeResponse("ok", null, null));
    }

    /// <summary>
    /// What the sessions are doing right now, in every space: the ones that run, that work in the background,
    /// whose last run failed, or that wait for the user. The page works out from it what each space is doing.
    /// </summary>
    [NeoRpcMethod("activity")]
    public async Task<SpacesActivityResponse> ActivityAsync(SpacesRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, [], false);
        var waiting = (await _waiting!().ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
        var sessions = _runs!()
            .Where(session => session.Running || session.BackgroundTasks > 0 || session.Failed || waiting.Contains(session.SessionId))
            // The ones that wait for the user first: a reading cut short keeps what matters most.
            .OrderByDescending(session => waiting.Contains(session.SessionId)).ThenByDescending(static session => session.Failed)
            .ThenBy(static session => session.SessionId, StringComparer.Ordinal)
            .ToArray();
        return new("ok", [.. sessions.Take(MaximumActivitySessions).Select(session => new SpaceSessionActivity(session.SessionId, session.ProjectId,
            Cut(session.Title, 256), session.Running, Math.Min(session.BackgroundTasks, 99), session.Failed, waiting.Contains(session.SessionId)))],
            sessions.Length > MaximumActivitySessions);
    }

    /// <summary>The requests that reach this page from elsewhere, until the page goes away.</summary>
    [NeoRpcMethod("watch")]
    public NeoRpcChannel<SpacesEvent> Watch(SpacesRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new(WatchAsync(request, cancellationToken), DesktopJsonContext.Default.SpacesEvent);
    }

    internal async IAsyncEnumerable<SpacesEvent> WatchAsync(SpacesRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_view is null || !string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) yield break;
        // A page that does not read keeps the newest requests.
        var requests = Channel.CreateBounded<SpacesEvent>(new BoundedChannelOptions(8) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        using var registration = _view.Watch(value => requests.Writer.TryWrite(value));
        await foreach (var value in requests.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false)) yield return value;
    }

    private async Task<SpaceChangeResponse> ChangeAsync(string? expectedEpoch, Func<Task<SpaceChange>> change)
    {
        if (Refuse(expectedEpoch) is { } refused) return new(refused, null, null);
        try
        {
            var result = await change().ConfigureAwait(false);
            var status = result.Status switch
            {
                SpaceChangeStatus.Ok => "ok",
                SpaceChangeStatus.NotFound => "not_found",
                SpaceChangeStatus.Conflict => "conflict",
                _ => "invalid",
            };
            return new(status, result.Succeeded ? null : result.Message, result.Space is null ? null : Item(new SpaceMembers(result.Space, [])));
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

    // A project file that cannot be read: the catalog of projects says so in its own place.
    private static bool Unreadable(Exception exception)
        => exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or SharpYaml.YamlException;

    private static SpaceItem Item(SpaceMembers members)
        => new(members.Space.Id, members.Space.Name, members.Space.Description, members.Space.Icon, members.Space.Color, members.Space.IsDefault,
            [.. members.ProjectIds], members.Space.SourcePath);

    private static string Cut(string value, int maximum)
        => value.Length <= maximum ? value : value[..(char.IsHighSurrogate(value[maximum - 1]) ? maximum - 1 : maximum)];
}

/// <summary>Asks for the spaces, their activity or their requests.</summary>
/// <param name="ExpectedEpoch">The host epoch.</param>
internal sealed record SpacesRequest(string ExpectedEpoch);

/// <summary>The spaces. Status is <c>ok</c>, <c>unavailable</c>, <c>stale_epoch</c> or <c>unreadable</c> (a catalog file).</summary>
internal sealed record SpacesResponse(string Status, IReadOnlyList<SpaceItem> Spaces);

/// <summary>One space.</summary>
/// <param name="Id">Its identifier, which a rename keeps.</param>
/// <param name="Name">The name shown to the user.</param>
/// <param name="Description">What it is for; null when nothing is said.</param>
/// <param name="Icon">The name of its icon; null for the usual one.</param>
/// <param name="Color">Its color, <c>#rgb</c> or <c>#rrggbb</c>; null for none.</param>
/// <param name="IsDefault">Whether it is the default space, which holds every project and cannot be deleted.</param>
/// <param name="ProjectIds">The identifiers of its projects, archived ones included.</param>
/// <param name="File">The path of its file; null for a default space that was never described.</param>
internal sealed record SpaceItem(string Id, string Name, string? Description, string? Icon, string? Color, bool IsDefault,
    IReadOnlyList<string> ProjectIds, string? File);

/// <summary>Creates a space.</summary>
internal sealed record SpaceCreateRequest(string ExpectedEpoch, string? Name, string? Description, string? Icon, string? Color, IReadOnlyList<string>? ProjectIds);

/// <summary>Changes a space; a value left null stays as it is, and an empty one is removed.</summary>
internal sealed record SpaceUpdateRequest(string ExpectedEpoch, string? Id, string? Name, string? Description, string? Icon, string? Color);

/// <summary>Names a space to delete.</summary>
internal sealed record SpaceDeleteRequest(string ExpectedEpoch, string? Id);

/// <summary>Names the spaces a project joins and leaves.</summary>
internal sealed record SpaceAssignRequest(string ExpectedEpoch, string? ProjectId, IReadOnlyList<string>? Join, IReadOnlyList<string>? Leave);

/// <summary>The identifiers of the spaces, first to last.</summary>
internal sealed record SpaceReorderRequest(string ExpectedEpoch, IReadOnlyList<string>? Ids);

/// <summary>The space a page shows.</summary>
internal sealed record SpaceShownRequest(string ExpectedEpoch, string? Id);

/// <summary>
/// Status is <c>ok</c>, <c>invalid</c> (with a message), <c>not_found</c>, <c>conflict</c>, <c>write_failed</c>,
/// <c>unreadable</c>, <c>unavailable</c> or <c>stale_epoch</c>.
/// </summary>
/// <param name="Status">How the change ended.</param>
/// <param name="Message">What was wrong, for a change that was not made.</param>
/// <param name="Space">The space after the change, without its projects; null when the change names none.</param>
internal sealed record SpaceChangeResponse(string Status, string? Message, SpaceItem? Space);

/// <summary>A request that reaches the page from elsewhere.</summary>
/// <param name="Kind"><c>show</c>: show the space named; <c>changed</c>: read the spaces again.</param>
/// <param name="SpaceId">The space to show.</param>
internal sealed record SpacesEvent(string Kind, string? SpaceId);

/// <summary>What the sessions of every space are doing.</summary>
/// <param name="Status"><c>ok</c>, <c>unavailable</c> or <c>stale_epoch</c>.</param>
/// <param name="Sessions">The sessions with something to say, the ones that wait for the user first.</param>
/// <param name="Truncated">Whether more sessions than the reading holds have something to say.</param>
internal sealed record SpacesActivityResponse(string Status, IReadOnlyList<SpaceSessionActivity> Sessions, bool Truncated);

/// <summary>What one session is doing.</summary>
/// <param name="SessionId">The session.</param>
/// <param name="ProjectId">Its project; null for a chat.</param>
/// <param name="Title">Its title.</param>
/// <param name="Running">Whether it runs.</param>
/// <param name="BackgroundTasks">How many tasks its provider goes on doing in the background.</param>
/// <param name="Failed">Whether its last run ended with an error.</param>
/// <param name="Waiting">Whether it waits for the user: a question, a command to review, a form to fill.</param>
internal sealed record SpaceSessionActivity(string SessionId, string? ProjectId, string Title, bool Running, int BackgroundTasks, bool Failed, bool Waiting);
