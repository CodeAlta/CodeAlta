using CodeAlta.Catalog;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Plugin.Mobile;
using CodeAlta.Plugins;

namespace CodeAlta.Node.Plugins;

/// <summary>
/// The page of the phones of a node, for <c>control.mobile.*</c>: the node turns its Mobile plugin on and off itself (the
/// setting of the profile, applied at once), since a plugin that is off hears no request, and gives the plugin that runs
/// the calls of its tab and of its settings page (<see cref="IMobileHostControl"/>).
/// </summary>
internal sealed class NodeMobile(CodeAltaHost host, string? pageRoot = null) : IFleetNodeMobile
{
    private readonly CodeAltaHost _host = host ?? throw new ArgumentNullException(nameof(host));
    private readonly string _pageRoot = pageRoot ?? NodeBuiltInPlugins.MobilePageRoot;

    private IMobileHostControl? Running
        => _host.PluginRuntime.ActivePlugins.Select(static plugin => plugin.Instance).OfType<IMobileHostControl>().FirstOrDefault();

    // Whether this node can serve the page: it starts plugins, has the Mobile one, and the files of the page.
    private bool Available => _host.PluginRuntime.IsStarted && File.Exists(Path.Combine(_pageRoot, FolderPluginAppFiles.ListFileName));

    /// <inheritdoc />
    public Task<FleetMobileState> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(Read());

    private FleetMobileState Read()
    {
        var running = Running is not null;
        return new FleetMobileState(Available, running, running, Available ? null : Unavailable);
    }

    private string Unavailable => !_host.PluginRuntime.IsStarted
        ? "This node runs without its plugins (--no-plugins): it does not serve the page of the phones."
        : "This build of altanode has no page for phones.";

    /// <inheritdoc />
    public async Task<FleetMobileState> EnableAsync(bool enabled, CancellationToken cancellationToken)
    {
        if (enabled && !Available) return Read();
        new CodeAltaConfigStore(_host.CatalogOptions).SaveGlobalPluginEnabled(NodeBuiltInPlugins.MobileId, enabled);
        if (!_host.PluginRuntime.IsStarted) return Read();
        var change = await _host.PluginRuntime.ApplyBuiltInAsync(NodeBuiltInPlugins.MobileId, cancellationToken).ConfigureAwait(false);
        var state = Read();
        return change == PluginPackageChange.StartFailed ? state with { Refusal = "The page of the phones did not start: see the log of the node (altanode logs)." } : state;
    }

    /// <inheritdoc />
    public async Task<string?> InvokeAsync(string method, string? payload, CancellationToken cancellationToken)
        => Running is { } control ? await control.InvokeAsync(method, payload, cancellationToken).ConfigureAwait(false) : null;
}
