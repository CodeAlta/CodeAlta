using CodeAlta.Catalog;
using XenoAtom.Terminal.UI;

namespace CodeAlta.App;

internal sealed record PluginShellTabRequest
{
    public required string PluginId { get; init; }

    public required string SurfaceKey { get; init; }

    public ProjectId? ProjectId { get; init; }

    public string? ThreadId { get; init; }

    public required Visual Header { get; init; }

    public required Visual Content { get; init; }

    public required object ViewModel { get; init; }

    public Func<ShellTabCloseReason, ValueTask>? OnClosedAsync { get; init; }
}

internal interface IPluginShellTabService
{
    ShellTabSnapshot? OpenOrGetPluginTab(PluginShellTabRequest request);

    ValueTask<bool> ClosePluginTabAsync(string pluginId, string surfaceKey, ShellTabCloseReason reason, CancellationToken cancellationToken = default);
}

internal sealed class PluginShellTabService : IPluginShellTabService
{
    private readonly IShellTabService _tabs;
    private readonly bool _hasInteractiveUi;

    public PluginShellTabService(IShellTabService tabs, bool hasInteractiveUi)
    {
        ArgumentNullException.ThrowIfNull(tabs);
        _tabs = tabs;
        _hasInteractiveUi = hasInteractiveUi;
    }

    public ShellTabSnapshot? OpenOrGetPluginTab(PluginShellTabRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SurfaceKey);
        ArgumentNullException.ThrowIfNull(request.Header);
        ArgumentNullException.ThrowIfNull(request.Content);
        ArgumentNullException.ThrowIfNull(request.ViewModel);

        if (!_hasInteractiveUi)
        {
            return null;
        }

        var tabId = CreateTabId(request.PluginId, request.SurfaceKey);
        return _tabs.OpenOrGetTab(new ShellTabDescriptor
        {
            TabId = tabId,
            Kind = ShellTabKind.Plugin,
            Association = new ShellTabAssociation.Plugin(request.PluginId, request.SurfaceKey, request.ProjectId, request.ThreadId),
            Header = request.Header,
            Content = request.Content,
            ViewModel = request.ViewModel,
            OnClosedAsync = request.OnClosedAsync,
        });
    }

    public async ValueTask<bool> ClosePluginTabAsync(
        string pluginId,
        string surfaceKey,
        ShellTabCloseReason reason,
        CancellationToken cancellationToken = default)
    {
        var tabId = CreateTabId(pluginId, surfaceKey);
        if (!_hasInteractiveUi)
        {
            return false;
        }

        return await _tabs.CloseTabAsync(tabId, reason, cancellationToken);
    }

    private static ShellTabId CreateTabId(string pluginId, string surfaceKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(surfaceKey);
        return new ShellTabId($"plugin:{pluginId}:{surfaceKey}");
    }
}
