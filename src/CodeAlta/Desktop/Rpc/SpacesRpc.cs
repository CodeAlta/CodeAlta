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

    /// <summary>How many sessions above a waiting one its reading names.</summary>
    internal const int MaximumAncestors = 16;

    private readonly SpaceCatalog? _catalog;
    private readonly DesktopSpaceView? _view;
    private readonly Func<IReadOnlyList<SessionRuntimeOverview>>? _runs;
    private readonly Func<ValueTask<IReadOnlyList<string>>>? _waiting;
    // Gives the chats of a space that is deleted the default space.
    private readonly Func<string, Task<OwnedSessionSpaceMoveResult>>? _moveChats;
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
    /// <param name="moveChats">Gives the chats of a deleted space the default space; null where nothing moves them.</param>
    internal SpacesService(SpaceCatalog catalog, DesktopSpaceView view, Func<IReadOnlyList<SessionRuntimeOverview>> runs,
        Func<ValueTask<IReadOnlyList<string>>> waiting, string epoch, Func<string, Task<OwnedSessionSpaceMoveResult>>? moveChats = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(waiting);
        ArgumentException.ThrowIfNullOrWhiteSpace(epoch);
        (_catalog, _view, _runs, _waiting, _epoch, _moveChats) = (catalog, view, runs, waiting, epoch, moveChats);
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

    /// <summary>
    /// Deletes a space. Its projects stay, in the default space and in their other ones, and its chats go to the
    /// default space: <c>movedSessions</c> says how many did. A chat at work is left as it is, and reads as in the
    /// default space all the same.
    /// </summary>
    [NeoRpcMethod("delete")]
    public async Task<SpaceChangeResponse> DeleteAsync(SpaceDeleteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var id = request.Id ?? string.Empty;
        var deleted = await ChangeAsync(request.ExpectedEpoch, () => _catalog!.DeleteAsync(id, cancellationToken)).ConfigureAwait(false);
        if (deleted.Status != "ok" || _moveChats is null) return deleted;
        try
        {
            var moved = await _moveChats(id).ConfigureAwait(false);
            return deleted with { MovedSessions = moved.Moved.Count };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The space is gone: its chats read as in the default space until they are moved there.
            return deleted;
        }
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
    /// The spaces each resource is assigned to in the global configuration: providers, MCP servers, plugins, skills
    /// and agent prompts. A resource that is not listed is in every space.
    /// </summary>
    [NeoRpcMethod("assignments")]
    public SpaceAssignmentsResponse Assignments(SpacesRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return SpaceAssignmentsResponse.Failed(refused);
        if (ConfigStore is not { } store) return SpaceAssignmentsResponse.Failed("unavailable");
        try
        {
            var assignments = store.LoadGlobalSpaceAssignments();
            static IReadOnlyList<SpaceResourceAssignment> Of(IReadOnlyDictionary<string, IReadOnlyList<string>> map)
                => [.. map.Take(MaximumAssignments).Select(static pair => new SpaceResourceAssignment(pair.Key, [.. pair.Value]))];
            return new("ok", Of(assignments.Providers), Of(assignments.McpServers), Of(assignments.Plugins), Of(assignments.Skills), Of(assignments.Prompts));
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return SpaceAssignmentsResponse.Failed("unreadable");
        }
    }

    /// <summary>
    /// Assigns a resource to spaces, beside its definition in the global configuration: an empty list puts it in
    /// every space. Only the user does it, from the window: no command of an agent writes the spaces of a resource.
    /// </summary>
    [NeoRpcMethod("assignResource")]
    public async Task<SpaceChangeResponse> AssignResourceAsync(SpaceResourceAssignRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, null, null);
        if (ConfigStore is not { } store) return new("unavailable", null, null);
        if (ParseKind(request.Kind) is not { } kind || string.IsNullOrWhiteSpace(request.Key) || request.Key.Length > MaximumKeyUnits
            || request.Key.Any(char.IsControl) || (request.Spaces ?? []).Count > MaximumAssignments)
            return new("invalid", "The resource is not one that is assigned to spaces.", null);
        if ((request.Spaces ?? []).FirstOrDefault(static id => !SpaceDescriptor.IsValidId(id)) is { } wrong)
            return new("invalid", $"'{wrong}' is not the identifier of a space.", null);
        await _assignGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            store.SaveGlobalSpaceAssignment(kind, request.Key.Trim(), request.Spaces);
        }
        catch (InvalidOperationException exception)
        {
            return new("invalid", exception.Message, null);
        }
        catch (InvalidDataException exception)
        {
            return new("unreadable", exception.GetBaseException().Message, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new("write_failed", null, null);
        }
        finally
        {
            _assignGate.Release();
        }

        // The pages of the resources and of the spaces read them again.
        _view!.NotifyChanged();
        return new("ok", null, null);
    }

    /// <summary>
    /// Sets the agent prompt the new sessions of a space start with when none is chosen; null for the default one.
    /// </summary>
    [NeoRpcMethod("setAgentPrompt")]
    public Task<SpaceChangeResponse> SetAgentPromptAsync(SpaceAgentPromptRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var agentPrompt = string.IsNullOrWhiteSpace(request.AgentPrompt) ? null : request.AgentPrompt.Trim();
        return ChangeAsync(request.ExpectedEpoch, () => _catalog!.UpdateSettingsAsync(request.Id ?? string.Empty, settings => settings with
        {
            NewSessions = (settings.NewSessions ?? new SpaceNewSessions()) with { AgentPrompt = agentPrompt } is { IsEmpty: false } kept ? kept : null,
        }, cancellationToken));
    }

    /// <summary>Sets the built-in tools the sessions of a space do without; an empty list for none.</summary>
    [NeoRpcMethod("setDisabledTools")]
    public Task<SpaceChangeResponse> SetDisabledToolsAsync(SpaceDisabledToolsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var names = (request.Tools ?? []).Select(static name => name?.Trim() ?? string.Empty).Distinct(StringComparer.Ordinal).ToArray();
        return ChangeAsync(request.ExpectedEpoch, () => _catalog!.UpdateSettingsAsync(request.Id ?? string.Empty,
            settings => settings with { Tools = names.Length == 0 ? null : new SpaceTools(names) }, cancellationToken));
    }

    /// <summary>What a session can use: the space it belongs to and the resources of that space that are turned on.</summary>
    [NeoRpcMethod("sessionResources")]
    public async Task<SpaceSessionResourcesResponse> SessionResourcesAsync(SpaceSessionResourcesRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        SpaceSessionResourcesResponse Failed(string status) => new(status, null, [], [], [], []);
        if (Refuse(request.ExpectedEpoch) is { } refused) return Failed(refused);
        if (SessionResources is not { } read) return Failed("unavailable");
        if (string.IsNullOrWhiteSpace(request.SessionId) || request.SessionId.Length > 128 || request.SessionId.Any(char.IsControl)) return Failed("invalid");
        try
        {
            return await read(request.SessionId, cancellationToken).ConfigureAwait(false) is { } resources
                ? new("ok", resources.SpaceId, resources.McpServers, resources.Skills, resources.Prompts, resources.Plugins)
                : Failed("not_found");
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Failed("unreadable");
        }
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
        var runs = _runs!();
        // A fallback is news for a short while: the page says it once.
        var since = DateTimeOffset.UtcNow - RecentFallback;
        var fallbacks = (Fallbacks?.Invoke() ?? []).Where(fallback => fallback.At >= since)
            .ToDictionary(static fallback => fallback.SessionId, StringComparer.OrdinalIgnoreCase);
        var sessions = runs
            .Where(session => session.Running || session.BackgroundTasks > 0 || session.Failed || waiting.Contains(session.SessionId)
                || fallbacks.ContainsKey(session.SessionId))
            // The ones that wait for the user first: a reading cut short keeps what matters most.
            .OrderByDescending(session => waiting.Contains(session.SessionId)).ThenByDescending(static session => session.Failed)
            .ThenBy(static session => session.SessionId, StringComparer.Ordinal)
            .ToArray();
        // A session that waits names the sessions above it, so that the one that created it shows it waits too.
        var parents = runs.Where(static session => session.ParentSessionId is not null)
            .GroupBy(static session => session.SessionId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First().ParentSessionId!, StringComparer.OrdinalIgnoreCase);
        return new("ok", [.. sessions.Take(MaximumActivitySessions).Select(session => new SpaceSessionActivity(session.SessionId, session.ProjectId,
            Cut(session.Title, 256), session.Running, Math.Min(session.BackgroundTasks, 99), session.Failed, waiting.Contains(session.SessionId),
            waiting.Contains(session.SessionId) ? Ancestors(session.SessionId, parents) : [],
            SpaceDescriptor.IsValidId(session.SpaceId) ? session.SpaceId : null)
            {
                Fallback = fallbacks.TryGetValue(session.SessionId, out var fallback)
                    ? new SpaceSessionFallback(fallback.At.ToString("O", System.Globalization.CultureInfo.InvariantCulture), Cut(fallback.Message, 512))
                    : null,
            })],
            sessions.Length > MaximumActivitySessions);
    }

    /// <summary>How long a fallback of a session is listed with its activity.</summary>
    internal static readonly TimeSpan RecentFallback = TimeSpan.FromMinutes(10);

    /// <summary>Gets the last fallback of each session since the start of the host; none when null.</summary>
    internal Func<IReadOnlyList<SessionFallbackRecord>>? Fallbacks { get; init; }

    // The sessions above one, nearest first, as far as the runtime holds them: a chain that loops or goes deeper
    // than any real one stops.
    internal static IReadOnlyList<string> Ancestors(string sessionId, IReadOnlyDictionary<string, string> parents)
    {
        var ancestors = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { sessionId };
        for (var current = sessionId; ancestors.Count < MaximumAncestors && parents.TryGetValue(current, out var parent) && seen.Add(parent); current = parent)
            ancestors.Add(parent);
        return ancestors;
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

    /// <summary>Gets the store of the global configuration, where the spaces of the resources are written; null where the host keeps none.</summary>
    internal CodeAltaConfigStore? ConfigStore { get; init; }

    /// <summary>Gets what reads the resources a session can use; null where the host keeps none.</summary>
    internal Func<string, CancellationToken, Task<SessionResources?>>? SessionResources { get; init; }

    /// <summary>The most resources of one kind the assignments name, and the most spaces one resource lists.</summary>
    internal const int MaximumAssignments = 512;

    private const int MaximumKeyUnits = 256;

    // One write of the configuration at a time from this window.
    private readonly SemaphoreSlim _assignGate = new(1, 1);

    private static SpaceResourceKind? ParseKind(string? kind)
        => kind switch
        {
            "provider" => SpaceResourceKind.Provider,
            "mcp" => SpaceResourceKind.McpServer,
            "plugin" => SpaceResourceKind.Plugin,
            "skill" => SpaceResourceKind.Skill,
            "prompt" => SpaceResourceKind.Prompt,
            _ => null,
        };

    private string? Refuse(string? expectedEpoch)
        => _catalog is null ? "unavailable" : string.Equals(expectedEpoch, _epoch, StringComparison.Ordinal) ? null : "stale_epoch";

    // A project file that cannot be read: the catalog of projects says so in its own place.
    private static bool Unreadable(Exception exception)
        => exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or SharpYaml.YamlException;

    private static SpaceItem Item(SpaceMembers members)
        => new(members.Space.Id, members.Space.Name, members.Space.Description, members.Space.Icon, members.Space.Color, members.Space.IsDefault,
            [.. members.ProjectIds], members.Space.SourcePath)
        {
            AgentPrompt = members.Space.Settings.NewSessions?.AgentPrompt,
            DisabledTools = members.Space.Settings.Tools?.Disabled ?? [],
        };

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
    IReadOnlyList<string> ProjectIds, string? File)
{
    /// <summary>Gets the agent prompt the new sessions of the space start with when none is chosen; null for the default one.</summary>
    public string? AgentPrompt { get; init; }

    /// <summary>Gets the built-in tools the sessions of the space do without.</summary>
    public IReadOnlyList<string> DisabledTools { get; init; } = [];
}

/// <summary>The spaces of the resources that list some; a resource that is not listed is in every space.</summary>
/// <param name="Status"><c>ok</c>, <c>unavailable</c>, <c>stale_epoch</c> or <c>unreadable</c> (the configuration).</param>
/// <param name="Providers">The providers, by key.</param>
/// <param name="McpServers">The MCP servers, by key.</param>
/// <param name="Plugins">The plugins, by id.</param>
/// <param name="Skills">The skills, by name.</param>
/// <param name="Prompts">The agent prompts, by id.</param>
internal sealed record SpaceAssignmentsResponse(string Status, IReadOnlyList<SpaceResourceAssignment> Providers, IReadOnlyList<SpaceResourceAssignment> McpServers,
    IReadOnlyList<SpaceResourceAssignment> Plugins, IReadOnlyList<SpaceResourceAssignment> Skills, IReadOnlyList<SpaceResourceAssignment> Prompts)
{
    internal static SpaceAssignmentsResponse Failed(string status) => new(status, [], [], [], [], []);
}

/// <summary>The spaces of one resource.</summary>
/// <param name="Key">Its key, name or id.</param>
/// <param name="Spaces">The identifiers of its spaces, in the order written.</param>
internal sealed record SpaceResourceAssignment(string Key, IReadOnlyList<string> Spaces);

/// <summary>Assigns a resource to spaces.</summary>
/// <param name="ExpectedEpoch">The host epoch.</param>
/// <param name="Kind"><c>provider</c>, <c>mcp</c>, <c>plugin</c>, <c>skill</c> or <c>prompt</c>.</param>
/// <param name="Key">Its key, name or id.</param>
/// <param name="Spaces">The identifiers of its spaces; empty for every space.</param>
internal sealed record SpaceResourceAssignRequest(string ExpectedEpoch, string? Kind, string? Key, IReadOnlyList<string>? Spaces);

/// <summary>Sets the built-in tools the sessions of a space do without.</summary>
/// <param name="ExpectedEpoch">The host epoch.</param>
/// <param name="Id">The space.</param>
/// <param name="Tools">The names of the tools; empty for none.</param>
internal sealed record SpaceDisabledToolsRequest(string ExpectedEpoch, string? Id, IReadOnlyList<string>? Tools);

/// <summary>Names a session whose resources are read.</summary>
internal sealed record SpaceSessionResourcesRequest(string ExpectedEpoch, string? SessionId);

/// <summary>What a session can use. Names only.</summary>
/// <param name="Status"><c>ok</c>, <c>not_found</c>, <c>invalid</c>, <c>unreadable</c>, <c>unavailable</c> or <c>stale_epoch</c>.</param>
/// <param name="SpaceId">The space of the session.</param>
/// <param name="McpServers">The MCP servers it can activate.</param>
/// <param name="Skills">The skills it is offered.</param>
/// <param name="Prompts">The agent prompts it can run with.</param>
/// <param name="Plugins">The plugins that add to it.</param>
internal sealed record SpaceSessionResourcesResponse(string Status, string? SpaceId, IReadOnlyList<string> McpServers, IReadOnlyList<string> Skills,
    IReadOnlyList<string> Prompts, IReadOnlyList<string> Plugins);

/// <summary>Sets the agent prompt the new sessions of a space start with.</summary>
/// <param name="ExpectedEpoch">The host epoch.</param>
/// <param name="Id">The space.</param>
/// <param name="AgentPrompt">The id of the agent prompt; null or empty for the default one.</param>
internal sealed record SpaceAgentPromptRequest(string ExpectedEpoch, string? Id, string? AgentPrompt);

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
/// <param name="MovedSessions">For a deletion, how many chats of the space went to the default space.</param>
internal sealed record SpaceChangeResponse(string Status, string? Message, SpaceItem? Space, int MovedSessions = 0);

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
/// <param name="Ancestors">
/// For a session that waits, the sessions above it, nearest first: the one that created it, the one that created that
/// one… Empty otherwise, and for a session no other session created.
/// </param>
/// <param name="SpaceId">The space the session records; null for one created before sessions had a space.</param>
internal sealed record SpaceSessionActivity(string SessionId, string? ProjectId, string Title, bool Running, int BackgroundTasks, bool Failed, bool Waiting,
    IReadOnlyList<string> Ancestors, string? SpaceId = null)
{
    /// <summary>Gets the last fallback of the session, when it went on with another model a short while ago.</summary>
    public SpaceSessionFallback? Fallback { get; init; }
}

/// <summary>A fallback of a session: it went on with the next model of its tier after a failure of its provider.</summary>
/// <param name="At">When, as an ISO 8601 instant.</param>
/// <param name="Message">What the timeline of the session says of it.</param>
internal sealed record SpaceSessionFallback(string At, string Message);
