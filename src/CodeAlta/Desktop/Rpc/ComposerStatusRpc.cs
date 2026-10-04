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

    /// <summary>Longest session id a request may name, in UTF-16 units.</summary>
    internal const int MaximumSessionIdUnits = 128;

    private readonly ProjectCatalog? _projects;
    private readonly string? _epoch;
    private readonly string? _home;
    private readonly PluginRuntimeManager? _plugins;
    private readonly McpManagementService _mcp = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

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
            var snapshot = _mcp.RefreshSnapshot(new McpManagementRequest { ProjectDirectory = project.Root, UserHomeDirectory = _home, ProbeWritability = false });
            var active = _plugins?.ActivePlugins ?? [];
            var plugin = active.Select(static plugin => plugin.Instance).OfType<McpPlugin>().FirstOrDefault();
            var status = plugin is null ? McpPlugin.CreateStatus(snapshot) : plugin.CreateStatus(snapshot, request.SessionId);
            if (snapshot.Policy.Enabled && status is { } mcp) items.Add(Item(McpPluginId, "mcp-status", mcp, "mcp"));
            if (_plugins is not null && active.Count > 0)
            {
                // The window is the interactive surface of these contributions.
                var statuses = _plugins.Adapter.GetStatusItems(active, PluginUiRegion.SessionStatus, new PluginAdapterOperationOptions
                {
                    ProjectId = request.ProjectId, ProjectPath = project.Root, SessionId = request.SessionId, HasInteractiveUi = true,
                });
                for (var index = 0; index < statuses.Count; index++) items.Add(Item("plugins", "status-" + index, statuses[index], null));
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
internal sealed record ComposerStatusItem(string PluginId, string Name, string Label, string Text, string Tone, string? SettingsPage);
