using CodeAlta.Catalog;
using CodeAlta.Plugin.Mcp;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// Status items that plugins show at the end of the composer's status line, such as the MCP plugin's count
/// of configured servers and of the tools a session activated, and the session status items of the other
/// active plugins.
/// </summary>
/// <remarks>
/// An item is what a plugin's <see cref="PluginStatusItem"/> carries: a label, a text and a tone. Only
/// configuration and what the host's plugins already know are read: nothing here connects to an MCP server.
/// Without a running MCP plugin (explicit roots, plugins turned off) the MCP item describes the
/// configuration alone.
/// </remarks>
[NeoRpcService("composerStatus", Version = 1)]
internal sealed class ComposerStatusService
{
    /// <summary>Plugin id of the built-in MCP plugin, as used in the configuration.</summary>
    internal const string McpPluginId = "mcp";

    /// <summary>Largest number of items in one response.</summary>
    internal const int MaximumItems = 8;

    /// <summary>Longest label or text of an item, in UTF-16 units; longer ones are cut.</summary>
    internal const int MaximumTextUnits = 160;

    /// <summary>Largest number of MCP servers in one <c>mcp</c> response.</summary>
    internal const int MaximumMcpServers = 64;

    /// <summary>Largest number of tools of one MCP server in one <c>mcp</c> response.</summary>
    internal const int MaximumMcpTools = 256;

    /// <summary>Largest number of arguments of one MCP tool in one <c>mcp</c> response.</summary>
    internal const int MaximumMcpArguments = 32;

    /// <summary>Longest description of an MCP tool, in UTF-16 units; a longer one is cut.</summary>
    internal const int MaximumDescriptionUnits = 4000;

    /// <summary>Longest session id a request may name, in UTF-16 units.</summary>
    internal const int MaximumSessionIdUnits = 128;

    private readonly ProjectCatalog? _projects;
    private readonly string? _epoch;
    private readonly string? _home;
    private readonly PluginRuntimeManager? _plugins;
    private readonly McpManagementService _mcp = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Gets the space of a session, which the status items of its plugins are filtered by.</summary>
    internal Func<string, CancellationToken, Task<string?>>? SessionSpace { get; init; }

    /// <summary>Gets what the space of a session has, which the MCP servers it counts are filtered by.</summary>
    internal Func<string, CancellationToken, Task<SpaceFilter>>? SessionSpaceFilter { get; init; }

    /// <summary>
    /// Gets what the space a new session of a project (null for a chat) will be created in has: the space the window
    /// shows, as the creation of the session decides it. The composer of a session that does not exist yet counts by it.
    /// </summary>
    internal Func<string?, CancellationToken, Task<SpaceFilter>>? NewSessionSpaceFilter { get; init; }

    /// <summary>Creates an unavailable service for launches without an owned host.</summary>
    internal ComposerStatusService()
    {
    }

    /// <summary>Creates the service for an owned host.</summary>
    /// <param name="projects">The host's project catalog, used to resolve a project id to its root.</param>
    /// <param name="epoch">The host epoch that requests must name.</param>
    /// <param name="home">The user home that holds the global <c>.alta</c> folder, or null for the profile.</param>
    /// <param name="plugins">The host's plugin runtime, or null when the host runs no plugin.</param>
    /// <exception cref="ArgumentNullException"><paramref name="projects"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="epoch"/> is blank.</exception>
    internal ComposerStatusService(ProjectCatalog projects, string epoch, string? home, PluginRuntimeManager? plugins = null)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentException.ThrowIfNullOrWhiteSpace(epoch);
        _projects = projects;
        _epoch = epoch;
        _home = home;
        _plugins = plugins;
    }

    /// <summary>Returns the status items for a composer of the named project, or of no project, and of its session.</summary>
    [NeoRpcMethod("read")]
    public async Task<ComposerStatusResponse> ReadAsync(ComposerStatusRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ComposerStatusResponse Failed(string status) => new(status, request.ProjectId, []);
        if (_projects is null) return Failed("unavailable");
        if (!string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) return Failed("stale_epoch");
        if (request.SessionId is { } session && (session.Length is 0 or > MaximumSessionIdUnits || session.Any(char.IsControl))) return Failed("invalid_request");
        // An archived project still shows its sessions, and their composers show the same status.
        var project = await SettingsProjectScope.ResolveAsync(_projects, request.ProjectId, allowArchived: true, cancellationToken).ConfigureAwait(false);
        if (project.Status != "ok") return Failed(project.Status);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var items = new List<ComposerStatusItem>();
            var (snapshot, inSpace) = await SnapshotAsync(project.Root, request, cancellationToken).ConfigureAwait(false);
            var active = _plugins?.ActivePlugins ?? [];
            var plugin = active.Select(static plugin => plugin.Instance).OfType<McpPlugin>().FirstOrDefault();
            var status = plugin is null ? McpPlugin.CreateStatus(snapshot) : plugin.CreateStatus(snapshot, request.SessionId);
            if (snapshot.Policy.Enabled && status is { } mcp) items.Add(Item(McpPluginId, "mcp-status", mcp, "mcp"));
            if (_plugins is not null && active.Count > 0)
            {
                // The window is the interactive surface of these contributions.
                var options = new PluginAdapterOperationOptions
                {
                    ProjectId = request.ProjectId, ProjectPath = project.Root, SessionId = request.SessionId, HasInteractiveUi = true,
                    // The plugins of the session's space only.
                    SpaceId = request.SessionId is { } sessionId && SessionSpace is { } space ? await space(sessionId, cancellationToken).ConfigureAwait(false) : inSpace?.SpaceId,
                };
                var statuses = _plugins.Adapter.GetStatusEntries(active, PluginUiRegion.SessionStatus, options);
                var commands = _plugins.Adapter.GetContributions<PluginCommandContribution>(PluginPoint.Command, options);
                for (var index = 0; index < statuses.Count; index++)
                {
                    // An item can run a command of its own plugin.
                    var (registration, item) = (statuses[index].Registration, statuses[index].Item);
                    var command = item.Command is null ? null : commands.FirstOrDefault(candidate =>
                        string.Equals(candidate.Handle.PluginRuntimeKey, registration.Handle.PluginRuntimeKey, StringComparison.Ordinal)
                        && string.Equals(((PluginCommandContribution)candidate.Contribution).Name, item.Command, StringComparison.OrdinalIgnoreCase));
                    items.Add(Item("plugins", "status-" + index, item, null) with { CommandId = command?.Handle.RuntimeContributionKey });
                }
            }

            return new("ok", request.ProjectId, [.. items.Take(MaximumItems)]);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Failed("read_failed"); // Never serialize exceptions, file paths or parser diagnostics.
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Returns the MCP servers a composer of the named project, or of no project, and of its session can use: whether the
    /// session activated each, and their tools as CodeAlta last listed them, with what the session has of them. Nothing
    /// connects to a server.
    /// </summary>
    [NeoRpcMethod("mcp")]
    public async Task<ComposerMcpResponse> ReadMcpAsync(ComposerStatusRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ComposerMcpResponse Failed(string status) => new(status, request.ProjectId, false, 0, []);
        if (_projects is null) return Failed("unavailable");
        if (!string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) return Failed("stale_epoch");
        if (request.SessionId is { } session && (session.Length is 0 or > MaximumSessionIdUnits || session.Any(char.IsControl))) return Failed("invalid_request");
        var project = await SettingsProjectScope.ResolveAsync(_projects, request.ProjectId, allowArchived: true, cancellationToken).ConfigureAwait(false);
        if (project.Status != "ok") return Failed(project.Status);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (snapshot, _) = await SnapshotAsync(project.Root, request, cancellationToken).ConfigureAwait(false);
            if (!snapshot.Policy.Enabled) return new("ok", request.ProjectId, false, 0, []);
            var plugin = (_plugins?.ActivePlugins ?? []).Select(static plugin => plugin.Instance).OfType<McpPlugin>().FirstOrDefault();
            var described = plugin is null ? McpPlugin.DescribeServers(snapshot, _home) : plugin.DescribeSession(snapshot, request.SessionId);
            var servers = described.Take(MaximumMcpServers).Select(McpServer).ToArray();
            return new("ok", request.ProjectId, true, described.Count - servers.Length, servers);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Failed("read_failed"); // Never serialize exceptions, file paths or parser diagnostics.
        }
        finally
        {
            _gate.Release();
        }
    }

    // The configuration snapshot of a project, with the servers of the space of the session, or of the one the session of a
    // composer without one will be created in.
    private async Task<(McpManagementSnapshot Snapshot, SpaceFilter? Space)> SnapshotAsync(string? root, ComposerStatusRequest request, CancellationToken cancellationToken)
    {
        var snapshot = _mcp.RefreshSnapshot(new McpManagementRequest { ProjectDirectory = root, UserHomeDirectory = _home, ProbeWritability = false });
        var inSpace = request.SessionId is { } owner
            ? SessionSpaceFilter is { } filter ? await filter(owner, cancellationToken).ConfigureAwait(false) : null
            : NewSessionSpaceFilter is { } created ? await created(request.ProjectId, cancellationToken).ConfigureAwait(false) : null;
        return (inSpace is null ? snapshot : InSpace(snapshot, inSpace), inSpace);
    }

    private static ComposerMcpServer McpServer(McpSessionServerView view)
    {
        var server = view.Server;
        var tools = view.Tools.Take(MaximumMcpTools).Select(static tool => new ComposerMcpTool(Cut(tool.Name), Cut(tool.Alias), tool.Title is null ? null : Cut(tool.Title),
            tool.Description is null ? null : Clip(tool.Description, MaximumDescriptionUnits),
            [.. tool.Arguments.Take(MaximumMcpArguments).Select(static argument => new ComposerMcpArgument(Cut(argument.Name),
                argument.Description is null ? null : Clip(argument.Description, MaximumTextUnits)))],
            tool.Enabled, tool.Active)).ToArray();
        return new ComposerMcpServer(Cut(server.Key), server.Transport == McpManagementTransport.Http ? "http" : "stdio", Cut(server.SourceName ?? string.Empty),
            server.SourceScope == McpManagementScope.Project ? "project" : "global", server.PolicyEnabled != false, server.PolicyRequired == true,
            view.Activated, view.InUse, view.ActiveToolCount, view.ListedAt, view.Tools.Count - tools.Length, tools);
    }

    // A long text kept whole up to a bound: line breaks stay, other control characters do not.
    private static string Clip(string text, int units)
    {
        var value = string.Concat(text.Select(static character => character is '\n' or '\t' || !char.IsControl(character) ? character : ' ')).Trim();
        if (value.Length <= units) return value;
        var length = units - 1;
        if (char.IsHighSurrogate(value[length - 1])) length--;
        return value[..length] + "…";
    }

    // The servers a session's space has: one of another space is neither offered to it nor counted for it.
    private static McpManagementSnapshot InSpace(McpManagementSnapshot snapshot, SpaceFilter space)
    {
        var outside = snapshot.Servers.Where(server => server.State is McpManagementServerState.Configured or McpManagementServerState.Disabled
            && !space.Allows(SpaceResourceKind.McpServer, server.Key)).ToArray();
        if (outside.Length == 0) return snapshot;
        var summary = snapshot.Summary;
        return snapshot with
        {
            Servers = [.. snapshot.Servers.Except(outside)],
            Summary = summary with
            {
                ConfiguredServerCount = summary.ConfiguredServerCount - outside.Length,
                ActiveServerCount = summary.ActiveServerCount - outside.Count(static server => server.State == McpManagementServerState.Configured && server.PolicyEnabled != false),
                UnavailableServerCount = summary.UnavailableServerCount - outside.Count(static server => server.State == McpManagementServerState.Disabled),
                ExposedToolCount = summary.ExposedToolCount - outside.Sum(static server => server.ExposedToolCount),
                TotalToolCount = summary.TotalToolCount - outside.Sum(static server => server.TotalToolCount),
            },
        };
    }

    private static ComposerStatusItem Item(string pluginId, string name, PluginStatusItem status, string? settingsPage)
        => new(pluginId, name, Cut(status.Label), Cut(status.Text), status.Tone switch
        {
            PluginStatusTone.Success => "success",
            PluginStatusTone.Warning => "warning",
            PluginStatusTone.Error => "error",
            PluginStatusTone.Muted => "muted",
            _ => "info",
        }, settingsPage);

    private static string Cut(string text)
    {
        var line = string.Concat(text.Select(static value => char.IsControl(value) ? ' ' : value)).Trim();
        if (line.Length <= MaximumTextUnits) return line;
        var length = MaximumTextUnits - 1;
        if (char.IsHighSurrogate(line[length - 1])) length--;
        return line[..length] + "…";
    }
}

/// <summary>Asks for the composer status items of a project.</summary>
/// <param name="ExpectedEpoch">The host epoch the page believes it is talking to.</param>
/// <param name="ProjectId">The composer's project, or null for a composer without one.</param>
/// <param name="SessionId">The composer's session, or null for a composer that has none yet.</param>
internal sealed record ComposerStatusRequest(string ExpectedEpoch, string? ProjectId, string? SessionId = null);

/// <summary><c>ok</c> with the items in display order, or a refusal code with none.</summary>
internal sealed record ComposerStatusResponse(string Status, string? ProjectId, ComposerStatusItem[] Items);

/// <summary>One plugin status item of the composer.</summary>
/// <param name="PluginId">The plugin that contributes the item.</param>
/// <param name="Name">The contribution's name, unique within its plugin.</param>
/// <param name="Label">The short label shown first, such as <c>MCP</c>.</param>
/// <param name="Text">The status text after the label.</param>
/// <param name="Tone"><c>info</c>, <c>success</c>, <c>warning</c>, <c>error</c> or <c>muted</c>.</param>
/// <param name="SettingsPage">The Settings page the item opens (<c>mcp</c>), or null when it opens nothing.</param>
/// <param name="CommandId">The plugin command the item runs, as listed by <c>pluginUi.contributions</c>, or null.</param>
internal sealed record ComposerStatusItem(string PluginId, string Name, string Label, string Text, string Tone, string? SettingsPage, string? CommandId = null);

/// <summary><c>ok</c> with the MCP servers a session can use, or a refusal code with none.</summary>
/// <param name="Status"><c>ok</c> or a refusal code.</param>
/// <param name="ProjectId">The asked project.</param>
/// <param name="Enabled">Whether MCP is turned on: when it is not, no server is listed.</param>
/// <param name="Omitted">The number of servers left out of a long list.</param>
/// <param name="Servers">The servers, by key.</param>
internal sealed record ComposerMcpResponse(string Status, string? ProjectId, bool Enabled, int Omitted, ComposerMcpServer[] Servers);

/// <summary>An MCP server as a session sees it.</summary>
/// <param name="Key">Its key.</param>
/// <param name="Transport"><c>stdio</c> or <c>http</c>.</param>
/// <param name="Source">The short name of the file that defines it.</param>
/// <param name="Scope"><c>global</c> or <c>project</c>.</param>
/// <param name="Enabled">Whether the policy lets sessions use it.</param>
/// <param name="Required">Whether the policy has every session of its space activate it.</param>
/// <param name="Activated">Whether the session activated it as a whole.</param>
/// <param name="InUse">Whether the session uses it: activated as a whole, or with a tool activated.</param>
/// <param name="ActiveToolCount">The number of its tools the last run of the session was given, or null.</param>
/// <param name="ListedAt">When CodeAlta last listed its tools, or null when it never did.</param>
/// <param name="OmittedTools">The number of tools left out of a long list.</param>
/// <param name="Tools">Its tools, by name.</param>
internal sealed record ComposerMcpServer(string Key, string Transport, string Source, string Scope, bool Enabled, bool Required, bool Activated, bool InUse,
    int? ActiveToolCount, DateTimeOffset? ListedAt, int OmittedTools, ComposerMcpTool[] Tools);

/// <summary>A tool of an MCP server as a session sees it.</summary>
/// <param name="Name">Its name on the server.</param>
/// <param name="Alias">The name a session calls it by.</param>
/// <param name="Title">Its title, or null.</param>
/// <param name="Description">What it does, or null.</param>
/// <param name="Arguments">Its arguments.</param>
/// <param name="Enabled">Whether the policy lets a session call it.</param>
/// <param name="Active">Whether the session has it.</param>
internal sealed record ComposerMcpTool(string Name, string Alias, string? Title, string? Description, ComposerMcpArgument[] Arguments, bool Enabled, bool Active);

/// <summary>An argument of an MCP tool.</summary>
/// <param name="Name">Its name.</param>
/// <param name="Description">What it is for, or null.</param>
internal sealed record ComposerMcpArgument(string Name, string? Description);
